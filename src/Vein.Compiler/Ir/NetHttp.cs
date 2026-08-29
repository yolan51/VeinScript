using System.Text;

namespace Vein.Compiler.Ir;

/// The HTTP client half of Vein.Net — how a program reaches a service that is not a VeinScript peer.
///
/// [NetBus] is symmetric: two programs that both hold a key address each other by identity. HTTP is the
/// opposite shape, and pretending otherwise would be a lie — a REST endpoint has no mark, does not know
/// VeinScript, and answers exactly once per question. So `@Fetch` is a REQUEST with a matching
/// `@Fetched`/`@Failed` reply, not an addressed send, and the URL stays a string rather than becoming a
/// fake identity.
public static class NetHttp
{
    /// One client for the process, as System.Net.Http intends: a fresh HttpClient per request exhausts
    /// the socket pool under any real load (each one leaves a connection in TIME_WAIT).
    private static readonly Lazy<HttpClient> Shared = new(() =>
        new HttpClient { Timeout = TimeSpan.FromSeconds(30) });

    /// Test/host seam: when set, answers instead of touching the network. Mirrors ConsoleBus.Hook and
    /// NetBus.Hook, so a test can assert what a program requested with no server anywhere.
    public static Func<string, string, string, Result>? Hook;

    /// A completed request. `Error` is null on success; when it is set, status/body are meaningless and
    /// the caller raises @Failed instead of @Fetched.
    public readonly record struct Result(long Status, string Body, string? Error);

    /// Perform one request and wait for it. Callers decide whether that wait happens on the event loop
    /// (one-shot render, where determinism matters more than latency) or on a worker thread that posts
    /// the result back to the inbox (a live session, where blocking the loop would freeze the console).
    public static Result Fetch(string url, string method, string body)
    {
        if (Hook is not null) return Hook(url, method, body);

        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return new Result(0, "", "not a valid absolute URL: " + url);
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return new Result(0, "", "unsupported scheme: " + uri.Scheme);

        try
        {
            var verb = string.IsNullOrWhiteSpace(method) ? "GET" : method.Trim().ToUpperInvariant();
            using var req = new HttpRequestMessage(new HttpMethod(verb), uri);
            // A body is attached whenever one was given, whatever the verb: the server decides what is
            // legal, and second-guessing it here would block a legitimate DELETE-with-body.
            if (!string.IsNullOrEmpty(body))
                req.Content = new StringContent(body, new UTF8Encoding(false), "application/json");

            using var res = Shared.Value.Send(req);
            string text = res.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            // A 404 is an ANSWER, not a failure: the peer replied. Only a request that never got a reply
            // becomes @Failed, which is the same line @Undelivered draws for the peer bus.
            return new Result((long)res.StatusCode, text, null);
        }
        catch (Exception ex)
        {
            return new Result(0, "", Reason(ex));
        }
    }

    /// The innermost message, since an HttpRequestException usually wraps the socket error that actually
    /// explains the failure ("No such host is known") behind a generic outer sentence.
    private static string Reason(Exception ex)
    {
        var e = ex;
        while (e.InnerException is not null) e = e.InnerException;
        return e.Message;
    }
}
