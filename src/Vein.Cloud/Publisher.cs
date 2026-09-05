using Vein.Compiler.Project;

namespace Vein.Cloud;

/// A file that will not be published, and why. Shown rather than dropped: "17 files" and "17 files,
/// one of which was quietly left behind" are different publishes, and only one of them compiles when
/// somebody restores it.
public sealed record Unpublishable(string Path, string Reason);

/// Everything a publish would do, worked out without touching the network.
///
/// This is what the dialog renders. Nothing here has left the machine yet, which is the point: the
/// person sees the exact names, the compile result and every redaction BEFORE anything is sent, and a
/// publish is never a side effect of a successful build.
public sealed record PublishPlan(
    ProjectPackage Package,
    PublishCheck Check,
    string BundleName,
    string Author,
    string Description,
    IReadOnlyList<(string Name, string Source, string Path)> Files,
    IReadOnlyList<SecretFinding> Redactions,
    IReadOnlyList<Unpublishable> Skipped,
    IReadOnlyList<string> ForeignAuthors)
{
    /// Errors block. Warnings are counted and shown, never a refusal — warning-free is the right bar
    /// for a standard library and a hostile one for someone's first upload.
    public bool CanPublish => Check.Ok && Files.Count > 0;
}

/// What actually happened.
public sealed record PublishOutcome(PushResult Files, BundleResult Bundle)
{
    public string Summary => Bundle.Unchanged
        ? $"{Bundle.Name} is already up to date at v{Bundle.Version}"
        : $"{Bundle.Name} v{Bundle.Version} — {Files.Created} new, {Files.Updated} changed";
}

/// Package → compile → redact → name → push files → version the bundle.
public static class Publisher
{
    /// Work out the publish without sending anything.
    ///
    /// `author` is the handle to publish under. It is a parameter rather than read from the source
    /// because the source is what is being *checked*: a bundle written `by you` is not yours to
    /// publish as `you`, and the mismatch has to be visible.
    public static PublishPlan Prepare(string projectDir, string author, string? description = null)
    {
        var package = ProjectPackage.Create(projectDir);
        var check = PublishGate.Check(package);

        var skipped = new List<Unpublishable>();
        var publishable = new List<PackagedFile>();

        foreach (var file in package.Files)
        {
            // IMPORTED BUNDLES ARE NOT YOURS TO PUBLISH. `bundles/` holds other people's code; it
            // already belongs in the catalogue under their name, and pushing it would republish their
            // work attributed to you — under a naming scheme whose entire point is that the name says
            // who wrote it.
            if (file.Path.StartsWith("bundles/", StringComparison.OrdinalIgnoreCase))
            {
                skipped.Add(new Unpublishable(file.Path, "an imported bundle — its author publishes it"));
                continue;
            }

            // The service takes `.vein` files. A README or a `.veinproj` cannot travel, and saying so
            // matters: `.veinproj` is what names the entry when a folder has several candidates.
            if (!file.Path.EndsWith(".vein", StringComparison.Ordinal))
            {
                skipped.Add(new Unpublishable(file.Path, "only .vein files are published"));
                continue;
            }

            publishable.Add(file);
        }

        // Redact before naming, so the names describe what is actually sent.
        var (clean, redactions) = SecretScan.Redact(publishable);

        string bundle = package.Name is { Length: > 0 } name ? name : Path.GetFileName(package.Root);
        var named = new List<(string, string, string)>();

        foreach (var file in clean)
        {
            if (VeinNames.ToName(author, bundle, file.Path) is { } address)
                named.Add((address, file.Content, file.Path));
            else
                skipped.Add(new Unpublishable(file.Path,
                    VeinNames.Reason(author, bundle, file.Path) ?? "cannot be addressed"));
        }

        // Every `by` in the package that is not the publisher's. The dialog offers to fix the line
        // rather than rewriting anyone's source behind their back.
        var foreign = package.Authors
            .Where(a => !a.Equals(author, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return new PublishPlan(
            package, check,
            BundleName: $"{author}.{bundle}",
            Author: author,
            Description: description ?? package.Name ?? bundle,
            Files: named,
            Redactions: redactions,
            Skipped: skipped,
            ForeignAuthors: foreign);
    }

    /// Send it. Files first, then the bundle that names them.
    ///
    /// THE ORDER IS THE SAFETY. `workbenchPush` upserts one file at a time and is not atomic across
    /// batches, so a dropped connection mid-push leaves some files up and some not — but nothing
    /// references them yet, so nobody can resolve a half-written bundle. The bundle row is written
    /// last, in one call, and only ever names files that are already there.
    public static async Task<PublishOutcome> PublishAsync(
        CloudSession session, PublishPlan plan, CancellationToken ct = default)
    {
        if (!plan.Check.Ok)
            throw new CloudException(0, "this project does not compile:\n" + plan.Check.Summary());
        if (plan.Files.Count == 0)
            throw new CloudException(0, "nothing to publish");

        var files = await FilesApi.PushAsync(session,
            plan.Files.Select(f => (f.Name, f.Source)).ToList(), ct).ConfigureAwait(false);

        // Match the returned ids back to what we sent. Ordinal by name, so the bundle's membership is
        // stable across publishes and a re-push of unchanged content really does come back `unchanged`
        // rather than merely reordered.
        var ids = files.Files
            .Where(f => f.Id.Length > 0)
            .OrderBy(f => f.Name, StringComparer.Ordinal)
            .Select(f => f.Id)
            .ToList();

        var bundle = await BundleApi.PushAsync(session, plan.BundleName, plan.Description,
            ids, session.Display, ct).ConfigureAwait(false);

        return new PublishOutcome(files, bundle);
    }
}
