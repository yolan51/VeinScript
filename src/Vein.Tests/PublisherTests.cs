using System.Text.Json;
using Vein.Cloud;
using Vein.Compiler.Project;
using Xunit;

namespace Vein.Tests;

// A real scaffolded project, all the way through: package, compile, redact, name, push, version.
// Nothing here touches the network — the Hook answers — but everything up to the request body is the
// code that will run against the live service.
//
// [Collection] because `VeinCloudClient.Hook` is one static field shared by three test classes.
[Collection("Cloud")]
public class PublisherTests : IDisposable
{
    private readonly List<CloudRequest> _sent = new();

    public PublisherTests() => VeinCloudClient.Hook = null;
    public void Dispose() => VeinCloudClient.Hook = null;

    private static string Temp() =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "vein-pub-" + Guid.NewGuid().ToString("N"))).FullName;

    private static CloudSession Signed => new("tok", "u1", "a@b.test", "Alice", "user");

    /// Answer a push with ids derived from the names actually sent, so the bundle call really does get
    /// the ids of the files this publish wrote.
    private void AnswerRealistically()
    {
        VeinCloudClient.Hook = req =>
        {
            _sent.Add(req);

            if (req.Url.EndsWith("workbenchPush", StringComparison.Ordinal))
            {
                using var doc = JsonDocument.Parse(req.Body!);
                var files = doc.RootElement.GetProperty("files").EnumerateArray()
                    .Select((f, i) => $$"""{"id":"id-{{f.GetProperty("name").GetString()}}","name":"{{f.GetProperty("name").GetString()}}","updated_date":"now"}""");

                return new CloudResponse(200,
                    $$"""{"saved":{{doc.RootElement.GetProperty("files").GetArrayLength()}},"created":{{doc.RootElement.GetProperty("files").GetArrayLength()}},"updated":0,"files":[{{string.Join(",", files)}}]}""",
                    null);
            }

            return new CloudResponse(200,
                """{"status":"created","version":1,"bundle":{"id":"b1","name":"alice.Demo","version":1,"shard_ids":[]}}""", null);
        };
    }

    [Fact]
    public void A_plan_is_worked_out_without_sending_anything()
    {
        // The dialog renders this. Nothing has left the machine, which is the whole point: a publish is
        // never a side effect of a successful build.
        string root = Temp();
        try
        {
            ProjectScaffold.New(ProjectKind.Bundle, root, "Demo", "alice");

            var plan = Publisher.Prepare(Path.Combine(root, "Demo"), "alice");

            Assert.True(plan.CanPublish, plan.Check.Summary());
            Assert.Equal("alice.Demo", plan.BundleName);
            Assert.Empty(plan.ForeignAuthors);

            // Every name is an address, and the folder survived in it.
            Assert.Contains(plan.Files, f => f.Name == "alice.Demo.vein");
            Assert.Contains(plan.Files, f => f.Name == "alice.Demo.publicators.Shapes.vein");
            Assert.Contains(plan.Files, f => f.Name == "alice.Demo.shards.Boot.vein");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void A_project_that_does_not_compile_cannot_be_published()
    {
        string root = Temp();
        try
        {
            ProjectScaffold.New(ProjectKind.Bundle, root, "Demo", "alice");
            string dir = Path.Combine(root, "Demo");
            File.WriteAllText(Path.Combine(dir, "Demo.vein"), "bundle Demo by alice { this will not parse");

            var plan = Publisher.Prepare(dir, "alice");

            Assert.False(plan.CanPublish);
            Assert.NotEmpty(plan.Check.Summary());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Publishing_a_broken_project_throws_before_it_sends_anything()
    {
        string root = Temp();
        try
        {
            ProjectScaffold.New(ProjectKind.Bundle, root, "Demo", "alice");
            string dir = Path.Combine(root, "Demo");
            File.WriteAllText(Path.Combine(dir, "Demo.vein"), "bundle Demo by alice { broken");

            AnswerRealistically();
            var plan = Publisher.Prepare(dir, "alice");

            await Assert.ThrowsAsync<CloudException>(() => Publisher.PublishAsync(Signed, plan));
            Assert.Empty(_sent);          // nothing left the machine
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Files_go_up_before_the_bundle_that_names_them()
    {
        // The ordering IS the safety. workbenchPush is not atomic across batches, so a dropped
        // connection can leave some files up — but nothing references them until the bundle row is
        // written, in one call, last.
        string root = Temp();
        try
        {
            ProjectScaffold.New(ProjectKind.Bundle, root, "Demo", "alice");
            AnswerRealistically();

            var plan = Publisher.Prepare(Path.Combine(root, "Demo"), "alice");
            var outcome = await Publisher.PublishAsync(Signed, plan);

            Assert.EndsWith("workbenchPush", _sent[0].Url);
            Assert.EndsWith("bundlePush", _sent[^1].Url);

            using var bundleBody = JsonDocument.Parse(_sent[^1].Body!);
            var ids = bundleBody.RootElement.GetProperty("shard_ids").EnumerateArray()
                                .Select(e => e.GetString()!).ToList();

            Assert.Equal(plan.Files.Count, ids.Count);
            Assert.All(ids, id => Assert.StartsWith("id-alice.Demo", id));
            Assert.Equal("alice.Demo", bundleBody.RootElement.GetProperty("name").GetString());

            Assert.True(outcome.Bundle.Created);
            Assert.Equal(1, outcome.Bundle.Version);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task The_ids_go_up_in_a_stable_order()
    {
        // So that re-publishing unchanged content really does come back `unchanged`, rather than
        // looking different because the membership list was merely reordered.
        string root = Temp();
        try
        {
            ProjectScaffold.New(ProjectKind.Bundle, root, "Demo", "alice");
            AnswerRealistically();

            var plan = Publisher.Prepare(Path.Combine(root, "Demo"), "alice");
            await Publisher.PublishAsync(Signed, plan);

            using var doc = JsonDocument.Parse(_sent[^1].Body!);
            var ids = doc.RootElement.GetProperty("shard_ids").EnumerateArray().Select(e => e.GetString()!).ToList();

            Assert.Equal(ids.OrderBy(x => x, StringComparer.Ordinal), ids);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void An_imported_bundle_is_reported_rather_than_republished()
    {
        // `bundles/` holds other people's code. Pushing it would republish their work attributed to
        // you — under a naming scheme whose entire point is that the name says who wrote it.
        string root = Temp();
        try
        {
            ProjectScaffold.New(ProjectKind.Solution, root, "Demo", "alice");
            string dir = Path.Combine(root, "Demo");
            string vendor = Path.Combine(dir, "bundles", "Theirs");
            Directory.CreateDirectory(vendor);
            File.WriteAllText(Path.Combine(vendor, "Theirs.vein"), "bundle Theirs by bob { }\n");

            var plan = Publisher.Prepare(dir, "alice");

            Assert.DoesNotContain(plan.Files, f => f.Path.StartsWith("bundles/", StringComparison.Ordinal));
            Assert.Contains(plan.Skipped, s => s.Path.StartsWith("bundles/", StringComparison.Ordinal)
                                            && s.Reason.Contains("imported"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void A_by_line_that_is_not_yours_is_surfaced_not_rewritten()
    {
        string root = Temp();
        try
        {
            string dir = Path.Combine(root, "Combat");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "Combat.vein"), "bundle Combat by bob { }\n");

            var plan = Publisher.Prepare(dir, "alice");

            Assert.Equal(new[] { "bob" }, plan.ForeignAuthors);

            // And the source is untouched — the dialog offers the fix, it does not perform it.
            Assert.Contains("by bob", plan.Files.Single().Source);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void A_secret_is_redacted_in_what_is_sent_and_left_alone_on_disk()
    {
        string root = Temp();
        try
        {
            string dir = Path.Combine(root, "Keys");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "Keys.vein");
            File.WriteAllText(file,
                "bundle Keys by alice {\n    fn apiKey() -> string { return \"9f3Ka81mZq47LpXv02Tb\" }\n}\n");

            var plan = Publisher.Prepare(dir, "alice");

            Assert.Single(plan.Redactions);
            Assert.Contains("PASTE-YOUR-API-KEY", plan.Files.Single().Source);
            Assert.DoesNotContain("9f3Ka81mZq47LpXv02Tb", plan.Files.Single().Source);

            // The author keeps working with the real key. Rewriting their disk to protect them would
            // be a worse failure than the one being prevented.
            Assert.Contains("9f3Ka81mZq47LpXv02Tb", File.ReadAllText(file));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void A_plan_carries_everything_the_dialog_has_to_show()
    {
        // The review screen is the only thing standing between someone and an irreversible public
        // publish, so every section it renders has to come from the plan rather than be recomputed —
        // a second source for "which files" would eventually disagree with the one that sends them.
        string root = Temp();
        try
        {
            ProjectScaffold.New(ProjectKind.Bundle, root, "Demo", "alice");
            string dir = Path.Combine(root, "Demo");
            File.WriteAllText(Path.Combine(dir, "README.md"), "# Demo\n");
            File.WriteAllText(Path.Combine(dir, "shards", "Keys.vein"),
                "shard Keys { run once { let apiKey = \"9f3Ka81mZq47LpXv02Tb\" } }\n");

            var plan = Vein.Cloud.Publisher.Prepare(dir, "alice", "A demo bundle.");

            Assert.Equal("alice.Demo", plan.BundleName);        // the heading
            Assert.Equal("alice", plan.Author);
            Assert.Equal("A demo bundle.", plan.Description);   // prefilled in the description box
            Assert.True(plan.Check.Ok);                         // the compile badge
            Assert.NotEmpty(plan.Files);                        // the file list, with published names
            Assert.All(plan.Files, f => Assert.StartsWith("alice.Demo", f.Name));
            Assert.Single(plan.Redactions);                     // the redaction list
            Assert.Contains(plan.Skipped, s => s.Path == "README.md");   // the skipped list
            Assert.Empty(plan.ForeignAuthors);                  // the author warning, absent here
            Assert.True(plan.CanPublish);                       // whether the button is enabled
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void A_project_that_does_not_compile_is_shown_but_not_sendable()
    {
        // The dialog opens anyway and lists the errors. Refusing to open it would be a dead end: the
        // person would be told no with no way to see why.
        string root = Temp();
        try
        {
            ProjectScaffold.New(ProjectKind.Bundle, root, "Demo", "alice");
            string dir = Path.Combine(root, "Demo");
            File.WriteAllText(Path.Combine(dir, "Demo.vein"), "bundle Demo by alice { broken");

            var plan = Vein.Cloud.Publisher.Prepare(dir, "alice");

            Assert.False(plan.CanPublish);
            Assert.NotEmpty(plan.Check.Summary());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void A_file_that_is_not_vein_is_reported_rather_than_dropped()
    {
        // A README cannot travel, and `.veinproj` is what names the entry when a folder has several
        // candidates — so its absence is worth stating rather than discovering on restore.
        string root = Temp();
        try
        {
            string dir = Path.Combine(root, "Demo");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "Demo.vein"), "bundle Demo by alice { }\n");
            File.WriteAllText(Path.Combine(dir, "README.md"), "# Demo\n");
            new VeinProject { Principal = "Demo.vein" }.Save(dir);

            var plan = Publisher.Prepare(dir, "alice");

            Assert.Contains(plan.Skipped, s => s.Path == "README.md");
            Assert.Contains(plan.Skipped, s => s.Path == VeinProject.FileName);
            Assert.All(plan.Files, f => Assert.EndsWith(".vein", f.Path));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
