using System.Text.Json;

namespace Vein.Cloud;

/// Where the service lives.
///
/// The default is the real one, so a fresh install works with nothing to configure. It is overridable
/// because a URL baked into a binary is a URL you cannot move — a staging environment, a self-hosted
/// instance, or simply the day the app is renamed would each otherwise need a rebuild.
///
/// NO CREDENTIAL LIVES HERE. This file may be read by anything and copied anywhere; the token is in
/// `CredentialStore`, on its own, deliberately.
public sealed class CloudConfig
{
    public const string DefaultBaseUrl = "https://veinscript.base44.app/functions/";

    /// Where a person creates an account. The Workbench signs in; it does not sign anyone up — the
    /// website owns the handle, the email confirmation and the terms.
    public const string DefaultSignupUrl = "https://veinscript.base44.app/";

    public string BaseUrl { get; set; } = DefaultBaseUrl;
    public string SignupUrl { get; set; } = DefaultSignupUrl;

    /// The full URL of one function. Tolerant of a base with or without its trailing slash, because a
    /// hand-edited config file will eventually have one of each.
    public string Url(string function) => BaseUrl.TrimEnd('/') + "/" + function.TrimStart('/');

    private static string Path0 => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VeinScript", "cloud.json");

    private static CloudConfig? _current;

    /// Read once per process. Order: the config file, then environment variables, then the defaults —
    /// so a machine-wide override is possible without editing anything a person owns.
    public static CloudConfig Current => _current ??= Load();

    /// For tests, and for the Workbench when someone edits the settings while it is running.
    public static void Reset(CloudConfig? replacement = null) => _current = replacement;

    public static CloudConfig Load()
    {
        var config = new CloudConfig();

        try
        {
            if (File.Exists(Path0) &&
                JsonSerializer.Deserialize<CloudConfig>(File.ReadAllText(Path0)) is { } stored)
                config = stored;
        }
        catch { /* unreadable or from a newer version — the defaults are a fine answer */ }

        if (Environment.GetEnvironmentVariable("VEIN_CLOUD_URL") is { Length: > 0 } url) config.BaseUrl = url;
        if (Environment.GetEnvironmentVariable("VEIN_CLOUD_SIGNUP") is { Length: > 0 } s) config.SignupUrl = s;

        return config;
    }
}
