using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vein.Compiler.Project;

/// A run configuration saved with the project, rather than read out of a file header each time.
public sealed record SavedRun(string Label, string Command, List<string> Args, Dictionary<string, string> Env);

/// Which published project this folder is, once it has been published once.
///
/// WHY IT HAS TO BE WRITTEN DOWN. Nothing else on disk says that this folder is already `alice/combat`.
/// Clone the repo onto a second machine, publish, and without this you get a SECOND project — or a
/// uniqueness failure with nothing to explain it. The slug can be recomputed from the source, but only
/// for a bundle; a solution and a scratch folder take a free slug, and there is no second place to
/// look it up.
///
/// `Id` is the backend's own identifier and `Slug` the readable address. Both, because the id survives
/// a rename the schema does not allow today and might tomorrow, and the slug is what a person reads in
/// a diff when they wonder where their publish went.
public sealed record CloudLink(string Id, string Slug, string? OwnerHandle = null);

// A `.veinproj` — the few things about a project that are NOT derivable from the code.
//
// Deliberately thin, and the reason matters. Every other project system in this repo's tooling reads
// the source: BundleLoader finds fragments, RunConfig reads a sample's header, RouteMap recovers routes
// from conditions. A project file that restated any of that would be a second copy to drift, and the
// derived version is the one that stays right.
//
// So this holds only what the code cannot say:
//   * which file is the entry point, when a folder has several that could be;
//   * run configurations you EDITED — `--ticks 40` is a choice about this session, not about the file,
//     which is exactly why it does not belong in the header;
//   * where the stdlib is, when it is not the one beside the compiler.
//
// Absent is the normal case. A folder with no .veinproj works exactly as it did.
public sealed class VeinProject
{
    public const string FileName = ".veinproj";

    /// The file ▶ runs when nothing else is chosen. Relative to the project folder.
    public string? Principal { get; set; }

    /// Configurations the author saved. These are OFFERED ALONGSIDE the ones read from a file's header,
    /// never instead of them — a saved config going stale must not hide what the file itself says.
    public List<SavedRun> Runs { get; set; } = new();

    /// An override for where `stdlib/` lives. Null means "the one found by walking up", which is right
    /// for everyone working inside this repo.
    public string? StdlibPath { get; set; }

    /// Where this folder was published, written on the first successful publish. Null until then, and
    /// null forever for a project nobody publishes — which stays the normal case.
    ///
    /// It belongs here for exactly the reason in this file's header: it is one of the few things about
    /// a project that is NOT derivable from the code.
    public CloudLink? Cloud { get; set; }

    [JsonIgnore]
    public string? Folder { get; private set; }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// Load the project in `folder`, or null when there is none.
    ///
    /// Null rather than a default instance: "this folder has no project file" and "this folder has an
    /// empty one" are different, and only the first should be silently written over.
    public static VeinProject? Load(string folder)
    {
        string path = Path.Combine(folder, FileName);
        if (!File.Exists(path)) return null;

        try
        {
            var project = JsonSerializer.Deserialize<VeinProject>(File.ReadAllText(path), Options);
            if (project is not null) project.Folder = folder;
            return project;
        }
        catch
        {
            // Malformed or from a newer version. Refusing to open the folder over it would be worse
            // than ignoring it — the code is still perfectly readable without the project file.
            return null;
        }
    }

    public void Save(string folder)
    {
        Folder = folder;
        File.WriteAllText(Path.Combine(folder, FileName), JsonSerializer.Serialize(this, Options));
    }

    /// The principal as an absolute path, when it names a file that exists.
    public string? PrincipalPath =>
        Folder is not null && Principal is not null && File.Exists(Path.Combine(Folder, Principal))
            ? Path.Combine(Folder, Principal)
            : null;
}
