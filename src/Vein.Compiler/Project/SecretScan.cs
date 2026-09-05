using System.Security.Cryptography;
using System.Text;

namespace Vein.Compiler.Project;

/// One credential found in a package, and what replaced it.
///
/// `Preview` is the first few characters and nothing more: a dialog has to let you recognise WHICH key
/// it caught without putting the key back on screen — and, for a shared screen, without putting it
/// anywhere a photograph would catch it.
public sealed record SecretFinding(string Path, int Line, string Kind, string Placeholder, string Preview);

/// Find credentials in source about to be published, and replace them with a named placeholder.
///
/// WHY REPLACE RATHER THAN REFUSE. A refusal leaves the author to find and blank the key themselves,
/// and the version they then publish usually has the line deleted — so whoever reads it later cannot
/// tell that a key was ever needed. A named placeholder says both things at once: this was a secret,
/// and you have to supply your own. It is already this repository's convention;
/// `samples/db_supabase.vein:117-122` ships `"PASTE-YOUR-ANON-KEY"` precisely because someone will
/// paste a real one there.
///
/// WHY IT MATTERS HERE MORE THAN USUAL. The catalogue is public and there is no delete endpoint, so a
/// published key is published permanently and to everyone.
///
/// THE LOCAL FILE IS NEVER TOUCHED. `Redact` returns new `PackagedFile`s; the caller uploads those and
/// the author keeps working with the real key. Rewriting somebody's source on their disk to protect
/// them would be a worse failure than the one being prevented.
///
/// THE BAR IS DELIBERATELY TWO-SIDED. Redacting an innocent string would corrupt published source —
/// quietly, in a way nobody would think to check — so a literal is only replaced when it is BOTH
/// named like a secret AND shaped like one, or when it carries a prefix that no ordinary string has.
public static class SecretScan
{
    /// Words that make a nearby literal suspicious. Matched case-insensitively as substrings, so
    /// `apiKey`, `API_KEY` and `supabaseKey` all count.
    ///
    /// `Strong` means the NAME alone settles it, and the value need only be long and unbroken. Nobody
    /// assigns an innocent value to something called `password`. The weak ones need the value to look
    /// opaque as well, and `token` in particular has to: this repository is full of programs about
    /// parsing, where `let token = "identifier"` is ordinary code and redacting it would be absurd.
    ///
    /// `key` is last, and weak, because it is a substring of half the others — a better word wins.
    private static readonly (string Word, string Kind, bool Strong)[] Suggestive =
    {
        ("password", "password", true), ("passwd", "password", true), ("pwd", "password", true),
        ("apikey", "api key", true), ("api_key", "api key", true), ("api-key", "api key", true),
        ("privatekey", "private key", true), ("private_key", "private key", true),
        ("credential", "credential", true), ("bearer", "token", true),
        ("secret", "secret", true),

        ("authorization", "token", false), ("auth_", "token", false),
        ("token", "token", false),
        ("key", "api key", false),
    };

    /// Prefixes that identify a credential on their own, whatever it is called. These are issued
    /// formats — no ordinary string in a program begins this way and then runs on for twenty
    /// characters.
    private static readonly (string Prefix, string Kind)[] Known =
    {
        ("eyJ",   "JWT"),                 // a JWT header is base64 of `{"`
        ("sk-",   "API key"),
        ("ghp_",  "GitHub token"), ("gho_", "GitHub token"),
        ("ghu_",  "GitHub token"), ("ghs_", "GitHub token"), ("github_pat_", "GitHub token"),
        ("AIza",  "Google API key"),
        ("xox",   "Slack token"),
        ("AKIA",  "AWS access key"),
        ("SG.",   "SendGrid key"),
    };

    /// Text that says "this is already a placeholder". Left alone — replacing a placeholder with
    /// another placeholder would report a finding that is not one, and teach people to ignore the list.
    private static readonly string[] AlreadyPlaceholder =
    {
        "paste", "your-", "your_", "yourkey", "todo", "changeme", "change-me", "replace-me",
        "xxxx", "example", "placeholder", "<", "…", "...",
    };

    /// Scan and rewrite. Files with nothing to hide come back unchanged, byte for byte.
    public static (IReadOnlyList<PackagedFile> Files, IReadOnlyList<SecretFinding> Findings)
        Redact(IReadOnlyList<PackagedFile> files)
    {
        var output = new List<PackagedFile>(files.Count);
        var findings = new List<SecretFinding>();

        foreach (var file in files)
        {
            var (text, found) = RedactOne(file.Path, file.Content, findings.Count == 0);
            findings.AddRange(found);

            if (found.Count == 0) { output.Add(file); continue; }

            byte[] bytes = Encoding.UTF8.GetBytes(text);
            output.Add(file with
            {
                Content = text,
                Bytes = bytes.Length,
                Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()
            });
        }

        return (output, findings);
    }

    private static (string Text, List<SecretFinding> Found) RedactOne(string path, string content, bool _)
    {
        var found = new List<SecretFinding>();

        // Per line, because a VeinScript string literal cannot span one: the lexer reports a literal
        // newline inside quotes as VS0003 (Lexer.cs:225,246). That makes line-at-a-time scanning exact
        // rather than an approximation.
        string[] lines = content.Split('\n');

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (!line.Contains('"')) continue;

            var rebuilt = new StringBuilder(line.Length);

            // CODE ONLY, never earlier literals. `Auth.keyed("apikey", key)` in stdlib/Rest.vein is the
            // case that taught this: with the whole line prefix as context, the header NAME "apikey"
            // sitting in an earlier string made every later literal on that line look like a secret. A
            // suggestive word inside a string is data; only an identifier is a name.
            var context = new StringBuilder();
            int at = 0;

            while (true)
            {
                int open = IndexOfQuote(line, at);
                if (open < 0) { rebuilt.Append(line, at, line.Length - at); break; }

                int close = IndexOfQuote(line, open + 1);
                if (close < 0) { rebuilt.Append(line, at, line.Length - at); break; }

                string literal = line[(open + 1)..close];
                context.Append(line, at, open - at);

                if (Classify(context.ToString(), literal) is { } kind)
                {
                    string placeholder = Placeholder(kind);
                    rebuilt.Append(line, at, open + 1 - at).Append(placeholder);
                    found.Add(new SecretFinding(path, i + 1, kind, placeholder, Preview(literal)));
                }
                else
                {
                    rebuilt.Append(line, at, close - at);
                }

                rebuilt.Append('"');
                at = close + 1;
            }

            lines[i] = rebuilt.ToString();
        }

        return (found.Count == 0 ? content : string.Join('\n', lines), found);
    }

    /// The next unescaped `"` at or after `from`.
    private static int IndexOfQuote(string line, int from)
    {
        for (int i = from; i < line.Length; i++)
        {
            if (line[i] != '"') continue;

            int slashes = 0;
            for (int j = i - 1; j >= 0 && line[j] == '\\'; j--) slashes++;
            if (slashes % 2 == 0) return i;
        }
        return -1;
    }

    /// What kind of secret this literal is, or null to leave it alone.
    private static string? Classify(string before, string literal)
    {
        if (literal.Length < 16) return null;
        if (literal.Contains(' ')) return null;                  // a sentence, not a credential
        if (LooksLikePlaceholder(literal)) return null;

        foreach (var (prefix, kind) in Known)
            if (literal.StartsWith(prefix, StringComparison.Ordinal) && literal.Length >= 20)
                return kind;

        // Otherwise the name must vouch for it, and for the weaker names the shape must too.
        string context = before.ToLowerInvariant();
        bool url = literal.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                   literal.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

        foreach (var (word, kind, strong) in Suggestive)
        {
            if (!context.Contains(word, StringComparison.Ordinal)) continue;

            // A strong name needs only that the value is not a URL; a weak one needs it to look opaque
            // as well. Either way the FIRST word that matches decides — the list is ordered strongest
            // first, so `apiKey` is judged as an api key and never falls through to the looser `key`.
            return (strong ? !url : Opaque(literal)) ? kind : null;
        }

        return null;
    }

    /// Shaped like a credential: long, mixed, and with enough distinct characters that it is not a
    /// word, a path or a URL. Cheap, and it only ever runs on a literal that was already named like a
    /// secret — the two together are what keep an innocent string safe.
    private static bool Opaque(string s)
    {
        if (s.Length < 16) return false;
        if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return false;

        bool letter = s.Any(char.IsAsciiLetter);
        bool digit = s.Any(char.IsAsciiDigit);
        int distinct = s.Distinct().Count();

        return letter && digit && distinct >= 10;
    }

    private static bool LooksLikePlaceholder(string s)
    {
        string lower = s.ToLowerInvariant();
        return AlreadyPlaceholder.Any(p => lower.Contains(p, StringComparison.Ordinal));
    }

    private static string Placeholder(string kind) => kind switch
    {
        "password"    => "PASTE-YOUR-PASSWORD",
        "token"       => "PASTE-YOUR-TOKEN",
        "JWT"         => "PASTE-YOUR-JWT",
        "private key" => "PASTE-YOUR-PRIVATE-KEY",
        "credential"  => "PASTE-YOUR-CREDENTIAL",
        "secret"      => "PASTE-YOUR-SECRET",
        _             => "PASTE-YOUR-API-KEY",
    };

    /// Enough to recognise it, not enough to use it.
    private static string Preview(string literal) =>
        literal.Length <= 6 ? "…" : literal[..6] + "…";
}
