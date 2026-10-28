using Vein.Compiler.Diagnostics;

namespace Vein.Compiler.Parsing;

/// <summary>D27: templates. A `shared("…")` shard or `ShardView … on` inside a publicator is a TEMPLATE — inert where it
/// is declared — and `shard X from *A.B.P.!Name` / `ShardView X from *A.B.P.!Name { Slot { … } }` copies it into the
/// bundle that writes it, before anything else lowers it.</summary>
/// <remarks>
/// <para><b>NOTHING ARRIVES WITH AN IMPORT.</b> The stdlib runs no shards, because behaviour that came with a `need`
/// would be invisible. A template keeps that true: it does nothing until a bundle writes `from`, and then it is that
/// bundle's own shard or view, named by it, in its own file's terms.</para>
/// <para><b>A COPY READS AS ITS NEW BUNDLE.</b> The copied body's bare names resolve in the bundle that wrote `from` —
/// so a template reaches its library's shared shapes, builders and events through that bundle's `need`, and calls
/// HOOKS the using bundle defines (`documentText()`): that is how a library asks the app for its part.</para>
/// <para><b>SLOTS.</b> A template view's widget marked `slot Content` takes the fill `Content { … }` as children, after
/// its own. A view the template places with `socket !Item` comes along from the same publicator when the using
/// bundle has none of that name.</para>
/// </remarks>
public static class TemplateExpand
{
    public static BundleDecl Apply(BundleDecl bundle, DiagnosticBag diag, Func<string, BundleDecl?> loadOwner)
    {
        // Templates declared here are definitions only — they never run in their own bundle.
        static bool IsTemplate(Decl d) => d.Shared && d is ShardDecl or ViewOnDecl;
        var members = bundle.Members.Select(m => m is PublicatorDecl p && p.Members.Any(IsTemplate)
            ? p with { Members = p.Members.Where(x => !IsTemplate(x)).ToList() } : m).ToList();

        if (!members.Any(m => m is TemplateUseDecl)) return members.SequenceEqual(bundle.Members) ? bundle : bundle with { Members = members };

        var owners = new Dictionary<string, BundleDecl?>(StringComparer.Ordinal);
        BundleDecl? Owner(string key) => owners.TryGetValue(key, out var b) ? b : owners[key] = key == Key(bundle) ? bundle : loadOwner(key);

        var result = new List<Decl>();
        var imported = new List<(string Publicator, BundleDecl Owner)>();
        foreach (var m in members)
        {
            if (m is not TemplateUseDecl use) { result.Add(m); continue; }
            if (use.Path.Count < 3)
            {
                diag.Error("VS0386", $"`from *{string.Join(".", use.Path)}.!{use.Template}`: a template is `*Author.Bundle.Publicator.!Name`.", use.Span);
                continue;
            }
            string ownerKey = use.Path[0] + "." + use.Path[1];
            string publicator = use.Path[2];
            var source = Owner(ownerKey);
            var template = source?.Members.OfType<PublicatorDecl>().Where(p => p.Name == publicator)
                .SelectMany(p => p.Members).FirstOrDefault(d => d.Shared && Named(d) == use.Template);

            if (use.IsView)
            {
                if (template is not ViewOnDecl view)
                {
                    diag.Error("VS0386", $"`ShardView {use.Name} from *{string.Join(".", use.Path)}.!{use.Template}`: no shared view of that " +
                        $"name there.{Offer(source, publicator, viewsOnly: true)}", use.Span);
                    continue;
                }
                var slots = Slots(view.Body).ToHashSet(StringComparer.Ordinal);
                foreach (var fill in use.Fills.Where(f => !slots.Contains(f.Slot)))
                    diag.Error("VS0386", $"`{fill.Slot} {{ … }}`: {use.Template} has no slot of that name" +
                        (slots.Count == 0 ? "." : $" — its slots are {string.Join(", ", slots)}."), fill.Span);
                result.Add(view with { Name = use.Name, Body = Fill(view.Body, use.Fills), Span = use.Span, Shared = false, Exported = false, Doc = null });
                imported.Add((publicator, source!));
            }
            else
            {
                if (template is not ShardDecl shard)
                {
                    diag.Error("VS0386", $"`shard {use.Name} from *{string.Join(".", use.Path)}.!{use.Template}`: no shared shard of that " +
                        $"name there.{Offer(source, publicator, viewsOnly: false)}", use.Span);
                    continue;
                }
                result.Add(shard with { Name = use.Name, Members = shard.Members.Concat(use.Extra).ToList(), Span = use.Span, Shared = false, Exported = false, Doc = null });
            }
        }

        // The views a copied view places by socket, from its publicator, when this bundle has none of that name.
        for (bool more = true; more;)
        {
            more = false;
            var have = result.OfType<ViewOnDecl>().Select(v => v.Name).Concat(result.OfType<ViewDecl>().Select(v => v.Name)).ToHashSet(StringComparer.Ordinal);
            foreach (var view in result.OfType<ViewOnDecl>().ToList())
                foreach (var wanted in Sockets(view.Body).Where(n => !have.Contains(n)))
                    foreach (var (publicator, source) in imported)
                        if (source.Members.OfType<PublicatorDecl>().Where(p => p.Name == publicator).SelectMany(p => p.Members)
                                .FirstOrDefault(d => d.Shared && d is ViewOnDecl v && v.Name == wanted) is ViewOnDecl brought)
                        {
                            result.Add(brought with { Shared = false, Exported = false, Doc = null });
                            have.Add(wanted);
                            more = true;
                            break;
                        }
        }
        return bundle with { Members = result };
    }

    private static string Key(BundleDecl b) => (b.Author ?? "local") + "." + b.Name;

    private static string? Named(Decl d) => d switch { ShardDecl s => s.Name, ViewOnDecl v => v.Name, _ => null };

    private static string Offer(BundleDecl? source, string publicator, bool viewsOnly)
    {
        if (source is null) return " (that bundle is not found — is it `need`ed?)";
        var names = source.Members.OfType<PublicatorDecl>().Where(p => p.Name == publicator).SelectMany(p => p.Members)
            .Where(d => d.Shared && (viewsOnly ? d is ViewOnDecl : d is ShardDecl)).Select(Named).ToList();
        return names.Count == 0 ? "" : $" It shares {string.Join(", ", names)}.";
    }

    /// The body with every `slot S` widget given the fill for S after its own children.
    private static List<ViewItem> Fill(IReadOnlyList<ViewItem> body, IReadOnlyList<SlotFill> fills) =>
        body.Select(item => item is WidgetItem w
            ? w with
            {
                Children = Fill(w.Children, fills)
                    .Concat(w.Slot is { } s ? fills.Where(f => f.Slot == s).SelectMany(f => f.Items) : Enumerable.Empty<ViewItem>()).ToList(),
            }
            : item).ToList();

    private static IEnumerable<string> Slots(IEnumerable<ViewItem> items) =>
        items.OfType<WidgetItem>().SelectMany(w => (w.Slot is { } s ? new[] { s } : Array.Empty<string>()).Concat(Slots(w.Children)));

    private static IEnumerable<string> Sockets(IEnumerable<ViewItem> items) =>
        items.OfType<WidgetItem>().SelectMany(w => (w.Socket is { } s ? new[] { s.View } : Array.Empty<string>()).Concat(Sockets(w.Children)));
}
