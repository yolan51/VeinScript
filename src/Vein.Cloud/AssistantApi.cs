using System.Text;
using System.Text.Json;
using Vein.Compiler.Diagnostics;
using Vein.Compiler.Project;
using Vein.Compiler.Service;

namespace Vein.Cloud;

/// One turn of the transcript. `Role` is "user" or "assistant".
public sealed record ChatTurn(string Role, string Content);

/// A fenced block from an assistant reply, already put through the compiler.
///
/// WHY IT IS CHECKED BEFORE IT IS OFFERED. The assistant is stateless and works from a grammar digest
/// that is maintained separately from this compiler, so the two can drift — and the failure mode is
/// source that looks plausible, goes into the editor, and does not compile. The Workbench holds the
/// only authority on that question, so it asks it before the code is offered rather than after it is
/// pasted.
///
/// A block that does not compile is still shown. Being told "this suggestion does not compile, here is
/// why" is useful; having it silently dropped is not.
public sealed record SuggestedCode(
    string Source,
    bool Compiles,
    IReadOnlyList<Diagnostic> Diagnostics,
    bool Wrapped)
{
    public string Summary(int take = 3) =>
        string.Join("\n", Diagnostics.Where(d => d.Severity == Severity.Error).Take(take).Select(d => d.ToString()));
}

/// What the assistant said, and what of it is usable.
public sealed record AssistantAnswer(string Reply, IReadOnlyList<SuggestedCode> Blocks)
{
    public bool HasUsableCode => Blocks.Any(b => b.Compiles);
}

/// The VeinScript assistant.
///
/// STATELESS SERVER-SIDE, so the transcript lives here and the tail is echoed on every call. Only the
/// last ten turns are used, so the client trims rather than paying to send what will be ignored.
public static class AssistantApi
{
    public const int MaxMessage = 4000;
    public const int HistoryTurns = 10;

    /// Ask, then compile-check anything it suggested.
    ///
    /// `projectDir`, when given, scopes bundle resolution to the user's project — so a suggestion that
    /// uses one of THEIR bundles is judged against the code they actually have, not against an empty
    /// world where every qualified reference is unresolvable.
    public static async Task<AssistantAnswer> AskAsync(
        CloudSession session, string message, IReadOnlyList<ChatTurn>? history = null,
        string? projectDir = null, CancellationToken ct = default)
    {
        message = message.Trim();
        if (message.Length == 0) throw new CloudException(0, "there is nothing to ask");
        if (message.Length > MaxMessage)
            throw new CloudException(0, $"a question is at most {MaxMessage} characters; this one is {message.Length}");

        var tail = (history ?? Array.Empty<ChatTurn>())
            .TakeLast(HistoryTurns)
            .Select(t => new { role = t.Role, content = t.Content });

        var root = await VeinCloudClient.PostAsync("workbenchChat",
            new { message, history = tail }, session.Token, ct).ConfigureAwait(false);

        string reply = AuthApi.Str(root, "reply");

        return new AssistantAnswer(reply, Check(ExtractVeinBlocks(reply), projectDir));
    }

    /// Trim a transcript to what is worth keeping between turns. Twice the window the service reads,
    /// so a scrollback still shows more than it sends.
    public static IReadOnlyList<ChatTurn> Trim(IReadOnlyList<ChatTurn> transcript, int keep = HistoryTurns * 2) =>
        transcript.Count <= keep ? transcript : transcript.Skip(transcript.Count - keep).ToList();

    // ---- fenced blocks ----------------------------------------------------------------------------

    /// Pull ```vein blocks out of a markdown reply.
    ///
    /// Only tagged blocks. An untagged fence in an answer about VeinScript is as likely to be a shell
    /// command, a JSON payload or an error message, and running those through the compiler would
    /// produce confident nonsense about code that was never meant to compile.
    public static IReadOnlyList<string> ExtractVeinBlocks(string markdown)
    {
        var blocks = new List<string>();
        if (string.IsNullOrEmpty(markdown)) return blocks;

        string[] lines = markdown.Replace("\r\n", "\n").Split('\n');
        StringBuilder? current = null;

        foreach (string line in lines)
        {
            string trimmed = line.TrimStart();

            if (!trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                current?.AppendLine(line);
                continue;
            }

            if (current is not null)
            {
                blocks.Add(current.ToString().TrimEnd('\n', '\r'));
                current = null;
                continue;
            }

            string tag = trimmed[3..].Trim();
            if (tag.Equals("vein", StringComparison.OrdinalIgnoreCase) ||
                tag.Equals("veinscript", StringComparison.OrdinalIgnoreCase))
                current = new StringBuilder();
        }

        // An unterminated fence is still a suggestion; the reply was probably truncated.
        if (current is { Length: > 0 }) blocks.Add(current.ToString().TrimEnd('\n', '\r'));

        return blocks;
    }

    private static IReadOnlyList<SuggestedCode> Check(IReadOnlyList<string> blocks, string? projectDir)
    {
        var checkedBlocks = new List<SuggestedCode>();
        if (blocks.Count == 0) return checkedBlocks;

        using var _ = BundleSearch.Scope(projectDir);

        foreach (string block in blocks)
        {
            // A snippet is usually a fragment — `shard Flee { … }` with no bundle around it, which is
            // a guaranteed VS0101 on its own. Wrapping it is what makes the check answer the question
            // the person actually has: "would this work in my bundle?"
            bool wrapped = !HasHeader(block);
            string source = wrapped ? $"bundle Suggestion by you {{\n{block}\n}}\n" : block;

            var result = new VeinCompilerService().Compile(
                new CompileRequest("suggestion.vein", source, ProjectDir: projectDir));

            checkedBlocks.Add(new SuggestedCode(block, result.Success, result.Diagnostics, wrapped));
        }

        return checkedBlocks;
    }

    /// Does this snippet already stand on its own? Only `bundle` and `app` open a compilation unit.
    private static bool HasHeader(string source)
    {
        foreach (string line in source.Split('\n'))
        {
            string t = line.TrimStart();
            if (t.StartsWith("bundle ", StringComparison.Ordinal) ||
                t.StartsWith("app ", StringComparison.Ordinal)) return true;
        }
        return false;
    }
}
