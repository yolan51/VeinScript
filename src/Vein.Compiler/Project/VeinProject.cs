using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vein.Compiler.Project;

/// A run configuration saved with the project, rather than read out of a file header each time.
public sealed record SavedRun(string Label, string Command, List<string> Args, Dictionary<string, string> Env);

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
