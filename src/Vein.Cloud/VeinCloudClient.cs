using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Vein.Cloud;

/// One request as it would go out. The Hook receives this, so a test asserts the URL, the verb, the
/// body AND whether a bearer token was attached — the last of which is the failure a status-only
/// assertion cannot see: a call that quietly went out unauthenticated, or one that leaked a token to a
/// public endpoint that never needed it.
public readonly record struct CloudRequest(string Method, string Url, string? Body, string? Bearer);

/// A completed request. `Error` is null on success; when it is set, `Status` and `Body` are whatever
/// came back and the caller shows the message rather than a number.
public readonly record struct CloudResponse(int Status, string Body, string? Error);

/// Raised when the service answers with something other than success. Carries the status so a caller
/// can tell "your password is wrong" (401) from "the service is down" (503).
public sealed class CloudException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

/// The HTTP half of the Base44 integration: one client, one place that knows about bearer tokens, and
/// one place that turns a non-2xx `{ "error": … }` into something a person can read.
///
/// WHY A HOOK RATHER THAN AN INTERFACE. It is the pattern this repository already uses in three
/// places — `NetHttp.Hook`, `NetBus.Hook`, `ConsoleBus.Hook` — so a test drives the whole client with
/// no server anywhere and asserts exactly what was requested. An interface would mean a mock library
/// this solution does not have and a seam that only the tests use.
public static class VeinCloudClient
{
    /// One client for the process, as System.Net.Http intends: a fresh HttpClient per request exhausts
    /// the socket pool under any real load. Mirrors NetHttp.cs.
    private static readonly Lazy<HttpClient> Shared = new(() =>
        new HttpClient { Timeout = TimeSpan.FromSeconds(30) });

    /// Test/host seam. When set, answers instead of touching the network.
    public static Func<CloudRequest, CloudResponse>? Hook;

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public static Task<JsonElement> GetAsync(string function, string? bearer, CancellationToken ct = default) =>
        SendAsync("GET", CloudConfig.Current.Url(function), null, bearer, ct);

    public static Task<JsonElement> PostAsync(string function, object body, string? bearer, CancellationToken ct = default) =>
        SendAsync("POST", CloudConfig.Current.Url(function), JsonSerializer.Serialize(body, Json), bearer, ct);

    /// Fetch a `file_url` from the catalogue. A plain GET of an absolute URL — no token, because these
    /// are public and sending one to a host we did not choose would be handing a credential away.
    public static async Task<string> GetTextAsync(string absoluteUrl, CancellationToken ct = default)
    {
        var request = new CloudRequest("GET", absoluteUrl, null, null);

        if (Hook is { } hook)
        {
            var hooked = hook(request);
            if (hooked.Error is { } err) throw new CloudException(hooked.Status, err);
            return hooked.Body;
        }

        using var message = new HttpRequestMessage(HttpMethod.Get, absoluteUrl);
        using var response = await Shared.Value.SendAsync(message, ct).ConfigureAwait(false);
        string text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw new CloudException((int)response.StatusCode, Explain((int)response.StatusCode, text));

        return text;
    }

    public static async Task<JsonElement> SendAsync(
        string method, string url, string? body, string? bearer, CancellationToken ct)
    {
        var request = new CloudRequest(method, url, body, bearer);
        CloudResponse answer;

        if (Hook is { } hook)
        {
            answer = hook(request);
        }
        else
        {
            try
            {
                using var message = new HttpRequestMessage(new HttpMethod(method), url);
                if (body is not null)
                    message.Content = new StringContent(body, new UTF8Encoding(false), "application/json");
                if (bearer is not null)
                    message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);

                using var response = await Shared.Value.SendAsync(message, ct).ConfigureAwait(false);
                string text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                answer = new CloudResponse((int)response.StatusCode, text,
                    response.IsSuccessStatusCode ? null : Explain((int)response.StatusCode, text));
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                // A request that never got a reply. Distinguished from a reply that said no, because
                // "the service refused you" and "you are offline" want different words in the UI.
                answer = new CloudResponse(0, "", Innermost(ex));
            }
        }

        if (answer.Error is { } error) throw new CloudException(answer.Status, error);

        return answer.Body.Length == 0
            ? default
            : JsonDocument.Parse(answer.Body).RootElement.Clone();
    }

    /// Errors come back as `{ "error": "…" }`. Reading the message out of it is the difference between
    /// "Push failed: 400" and a sentence naming the file that was wrong.
    private static string Explain(int status, string body)
    {
        try
        {
            if (JsonDocument.Parse(body).RootElement.TryGetProperty("error", out var e) &&
                e.GetString() is { Length: > 0 } message)
                return message;
        }
        catch (JsonException) { /* not JSON — fall through to the status */ }

        return status switch
        {
            401 or 403 => "signed out, or that account cannot do this — sign in again",
            404 => "no such endpoint",
            429 => "too many requests just now — try again in a moment",
            >= 500 => "the service is having trouble; nothing was saved",
            _ => $"the service answered {status}"
        };
    }

    private static string Innermost(Exception ex)
    {
        var cause = ex;
        while (cause.InnerException is { } inner) cause = inner;
        return cause.Message;
    }
}
