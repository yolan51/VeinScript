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
    /// NetBus.Hook, so a test can assert what a program requested with no server anywhere. Headers are
    /// the fourth argument precisely so a test can assert THEM: an API key that never left the program
    /// is the failure a status-only assertion cannot see.
    public static Func<string, string, string, string, Result>? Hook;

    /// A completed request. `Error` is null on success; when it is set, status/body are meaningless and
    /// the caller raises @Failed instead of @Fetched.
    public readonly record struct Result(long Status, string Body, string? Error);

    /// Perform one request and wait for it. Callers decide whether that wait happens on the event loop
    /// (one-shot render, where determinism matters more than latency) or on a worker thread that posts
    /// the result back to the inbox (a live session, where blocking the loop would freeze the console).
    public static Result Fetch(string url, string method, string body, string headers)
    {
        if (Hook is not null) return Hook(url, method, body, headers);

        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return new Result(0, "", "not a valid absolute URL: " + url);
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return new Result(0, "", "unsupported scheme: " + uri.Scheme);

        var parsed = ParseHeaders(headers);
        // Content-Type has to be settled BEFORE the content exists — StringContent takes it in its
        // constructor — so it is pulled out of the list rather than added alongside the others.
        string contentType = "application/json";
        foreach (var (name, value) in parsed)
            if (string.Equals(name, "Content-Type", StringComparison.OrdinalIgnoreCase))
                contentType = value;

        try
        {
            var verb = string.IsNullOrWhiteSpace(method) ? "GET" : method.Trim().ToUpperInvariant();
            using var req = new HttpRequestMessage(new HttpMethod(verb), uri);
            // A body is attached whenever one was given, whatever the verb: the server decides what is
            // legal, and second-guessing it here would block a legitimate DELETE-with-body.
            if (!string.IsNullOrEmpty(body))
                req.Content = new StringContent(body, new UTF8Encoding(false), contentType);

            Apply(req, parsed);

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

    /// Headers arrive from VeinScript as ONE string, `Name: Value` per line — the HTTP wire format
    /// itself. A map would have been the obvious signature and is not available: an event payload field
    /// has a scalar type, there is no map literal, and a list is a value with no type to declare
    /// (samples/rows_in_order.vein). The wire format is the shape the language can already build with
    /// `+` and `\n`, and it is self-describing when printed in a log.
    ///
    /// Blank lines are skipped so `header(a) + "\n" + header(b)` composes without the caller tracking
    /// whether it is first. A line with no colon is skipped rather than guessed at.
    ///
    /// Public because the DROPPING rules below are the security-relevant half of this file and a test has
    /// to be able to assert them directly — reaching them through Fetch would need a live server.
    public static List<(string Name, string Value)> ParseHeaders(string headers)
    {
        var list = new List<(string, string)>();
        if (string.IsNullOrWhiteSpace(headers)) return list;

        foreach (var raw in headers.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            int colon = line.IndexOf(':');
            if (colon <= 0) continue;

            string name = line[..colon].Trim();
            string value = line[(colon + 1)..].Trim();
            // Refuse control characters outright. Everything here is added WITHOUT validation, so a
            // stray CR or LF in a value taken from data would otherwise let a caller append headers of
            // its own — response splitting, wearing the clothes of an ordinary string concatenation.
            if (name.Length == 0 || HasControl(name) || HasControl(value)) continue;

            list.Add((name, value));
        }
        return list;
    }

    private static bool HasControl(string s)
    {
        foreach (char c in s) if (c < ' ' || c == (char)127) return true;
        return false;
    }

    /// Request headers and CONTENT headers are two different collections in System.Net.Http, and adding
    /// one to the wrong collection throws. Rather than keep a list of which is which, try the request
    /// first and fall back to the content — that is what the split actually means at the call site.
    private static void Apply(HttpRequestMessage req, List<(string Name, string Value)> headers)
    {
        foreach (var (name, value) in headers)
        {
            // Already spent on StringContent's constructor; adding it again would duplicate the header.
            if (string.Equals(name, "Content-Type", StringComparison.OrdinalIgnoreCase)) continue;

            try
            {
                if (req.Headers.TryAddWithoutValidation(name, value)) continue;
                req.Content?.Headers.TryAddWithoutValidation(name, value);
            }
            catch (InvalidOperationException) { /* a header this request cannot carry — skip it */ }
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
