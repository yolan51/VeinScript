namespace Vein.Compiler.Ir;

/// Where `@PlaySound` goes.
///
/// A sound is the one stdlib effect this interpreter cannot perform itself: it has no speaker, and
/// pulling an audio library into the compiler so that `veinc run` could beep would be the wrong
/// dependency in the wrong place. So this is a SEAM, in the shape `NetHttp.Hook`, `NetBus.Hook` and
/// `ConsoleBus.Hook` already use — the host that has a speaker (an editor, a player) sets it, and a
/// test can assert what a program asked to play with no audio device anywhere.
///
/// With nothing set, the sound is DROPPED and `Dropped` says so. That is honest for a console, and it
/// is a different thing from silently compiling to nothing: `@PlaySound` is in `Interp.HostEvents`,
/// so the C# backend refuses it with a note rather than producing a game that runs and is mute. A
/// program's audio works wherever there is a host to play it and is reported everywhere there is not.
public static class AudioOut
{
    /// `(source, volume)` — the file a program named, and 0..1. Set by the host; null in `veinc run`.
    public static Action<string, double>? Hook;

    /// Sounds requested with no hook to play them, kept so a host or a test can see what was missed
    /// rather than wondering whether the program ever asked.
    public static readonly List<(string Source, double Volume)> Dropped = new();

    public static void Play(string source, double volume)
    {
        double v = double.IsFinite(volume) ? Math.Clamp(volume, 0.0, 1.0) : 1.0;

        if (Hook is { } hook) { hook(source, v); return; }
        Dropped.Add((source, v));
    }
}
