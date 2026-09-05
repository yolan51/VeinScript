using System.Xml;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;

namespace Vein.Workbench;

/// The editor's syntax theme, loaded once and shared.
///
/// It was inline in MainWindow until the assistant needed to render code too. Two copies of a
/// highlighting definition is two places for the theme to drift, and the drift would show up as the
/// same source looking like two languages depending on which pane you read it in.
///
/// Cached because loading walks an XML document to build the rule set; the assistant makes a fresh
/// editor per suggestion, and re-parsing the grammar for each one would be work spent to get the same
/// answer.
internal static class VeinHighlighting
{
    private static IHighlightingDefinition? _cached;
    private static bool _tried;

    /// Null when the resource cannot be read — callers fall back to unhighlighted text rather than
    /// refusing to show source at all.
    public static IHighlightingDefinition? Definition
    {
        get
        {
            if (_tried) return _cached;
            _tried = true;

            try
            {
                var assembly = typeof(VeinHighlighting).Assembly;

                // The resource name is RootNamespace + path, which is `Vein.Workbench.…` and does NOT
                // follow AssemblyName. Found by suffix rather than spelled out, so renaming either one
                // cannot silently turn every .vein file into plain text — the failure mode of a
                // hardcoded name here is invisible, because the load is caught and carried on from.
                string? resource = assembly.GetManifestResourceNames()
                    .FirstOrDefault(n => n.EndsWith("VeinScript.xshd", StringComparison.Ordinal));
                if (resource is null) return null;

                using var stream = assembly.GetManifestResourceStream(resource);
                if (stream is null) return null;

                using var reader = XmlReader.Create(stream);
                _cached = HighlightingLoader.Load(reader, HighlightingManager.Instance);
            }
            catch { /* a missing or malformed theme must not stop the IDE opening */ }

            return _cached;
        }
    }
}
