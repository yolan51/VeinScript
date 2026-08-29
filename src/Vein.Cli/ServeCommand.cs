using System.Net;
using System.Text;
using Vein.Compiler.Diagnostics;
using Vein.Compiler.Ir;
using Vein.Compiler.Project;



/// `veinc serve <file> --port N` — bind a port and answer real HTTP with the program's @Response.
///
/// stdlib/Web.vein has always declared @Request/@Response, and `veinc render` has always fired a FAKE
/// @Request to prove a page assembles. This is the same pipeline with a socket in front: nothing in the
/// language or the interpreter changes, because a request was already the boot event. That is why this
/// command is ~100 lines rather than a web framework.
///
/// One request = one Render = one fresh interpreter. A page that depended on the last visitor's state
/// would be a bug in a request/response model, and a per-request world makes that structurally
/// impossible — the same reason `veinc render` is one-shot.
internal static class ServeCommand
{
    public static int Serve(string path, string source, int port, string host)
    {
        var diagnostics = new DiagnosticBag();
        var unit = BundleLoader.Load(path, diagnostics, editing: (path, source));
        foreach (var d in diagnostics.Items) Console.Error.WriteLine(d);
        if (diagnostics.HasErrors) return 1;

        var bundle = unit.Bundles.FirstOrDefault();
        if (bundle is null)
        {
            // An `app` manifest holds `load`s, not bundles, so it lands here with nothing to serve.
            // Naming that explicitly beats "no bundle to serve", which reads like the file was empty.
            Console.Error.WriteLine(unit.Apps.Count > 0
                ? $"`veinc serve` cannot serve an app manifest yet — `app {unit.Apps[0].Name}` loads " +
                  $"{unit.Apps[0].Loads.Count} bundle(s); linking them into one program is the app " +
                  $"link+run follow-on (docs/RUNTIME.md §5). Serve a single bundle file instead."
                : "no bundle to serve");
            return 1;
        }

        string projectDir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";

        // Lower ONCE, outside the request loop. Lowering is pure and the module is read-only at run time,
        // so re-lowering per request would only buy latency; the per-request freshness that matters is
        // the interpreter's world, which Render already gives us.
        var lower = new Lower(diagnostics, projectDir);
        var module = lower.LowerBundle(bundle);
        if (diagnostics.HasErrors)
        {
            foreach (var d in diagnostics.Items) Console.Error.WriteLine(d);
            return 1;
        }

        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://{host}:{port}/");
        try { listener.Start(); }
        catch (HttpListenerException ex)
        {
            // The overwhelmingly common cause on Windows: a non-loopback prefix needs a URL ACL. Say so,
            // with the command that fixes it, rather than printing "Access is denied".
            Console.Error.WriteLine($"cannot bind http://{host}:{port}/ — {ex.Message}");
            if (!IsLoopback(host))
                Console.Error.WriteLine($"  (a non-localhost prefix needs a reservation: " +
                                        $"netsh http add urlacl url=http://{host}:{port}/ user=%USERNAME%)");
            return 1;
        }

        Console.Error.WriteLine($"serving {bundle.Name} on http://{host}:{port}/  (Ctrl+C to stop)");

        // Ctrl+C should close the port, not abandon it: an HttpListener left running holds the prefix
        // until the process dies, and a half-dead listener is exactly the state that makes the next run
        // fail to bind.
        using var stopping = new ManualResetEventSlim(false);
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stopping.Set(); try { listener.Stop(); } catch { } };

        while (!stopping.IsSet)
        {
            HttpListenerContext ctx;
            try { ctx = listener.GetContext(); }
            catch when (stopping.IsSet) { break; }      // Stop() unblocked us — an ordinary shutdown
            catch (Exception ex) { Console.Error.WriteLine($"accept failed: {ex.Message}"); continue; }

            Handle(ctx, module);
        }

        Console.Error.WriteLine("stopped.");
        return 0;
    }

    private static void Handle(HttpListenerContext ctx, IrModule module)
    {
        string requestPath = ctx.Request.Url?.AbsolutePath ?? "/";
        try
        {
            // Query string arrives as boot payload fields, so `?name=x` reads as `r.name` — the same
            // shape `veinc render --set name=x` already produces. One surface, two drivers.
            var inputs = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (string? k in ctx.Request.QueryString.AllKeys)
                if (k is not null) inputs[k] = ctx.Request.QueryString[k];

            var result = new Interp().Render(module, requestPath, inputs);

            // No @Response is a 404, not a crash: the program simply had nothing to say about this path,
            // which is what an unrouted URL IS.
            if (result.Body is null) { Write(ctx, 404, $"no @Response for {requestPath}"); return; }
            Write(ctx, (int)result.Status, result.Body);
        }
        catch (Exception ex)
        {
            // One failing request must never take the server down — the console loop learned the same
            // lesson (Interp.Run guards each action separately).
            Console.Error.WriteLine($"  ! {requestPath}: {ex.Message}");
            try { Write(ctx, 500, "internal error"); } catch { }
        }
    }

    private static void Write(HttpListenerContext ctx, int status, string body)
    {
        var bytes = new UTF8Encoding(false).GetBytes(body);
        ctx.Response.StatusCode = status;
        // Charset stated explicitly: a browser that guesses gets the em-dashes and arrows in a Vein page
        // wrong in exactly the way the console did before it was switched to UTF-8.
        ctx.Response.ContentType = LooksLikeHtml(body) ? "text/html; charset=utf-8" : "text/plain; charset=utf-8";
        ctx.Response.ContentLength64 = bytes.Length;
        using var output = ctx.Response.OutputStream;
        output.Write(bytes, 0, bytes.Length);
    }

    private static bool LooksLikeHtml(string body)
    {
        var t = body.TrimStart();
        return t.StartsWith("<!", StringComparison.OrdinalIgnoreCase) || t.StartsWith("<html", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("<", StringComparison.Ordinal) && t.Contains('>');
    }

    private static bool IsLoopback(string host) =>
        host is "localhost" or "127.0.0.1" or "::1";
}
