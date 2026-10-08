using Godot;

namespace Hearthspan.Game;

/// <summary>
/// Stage 6 settings persistence: a Godot <see cref="ConfigFile"/> at <c>user://settings.cfg</c> — the
/// standard built-in pattern, not a hand-rolled JSON file — for volume/window-resolution/camera-speed.
///
/// <para><see cref="Load"/> is idempotent (safe to call from more than one entry point) and
/// deliberately defaults to values that match the engine's own pre-Stage-6 behavior exactly: master
/// volume at unity/0dB, the window size Godot itself uses when <c>project.godot</c> has no
/// <c>[display]</c> override (1152x648 — see the trap-list entry on the null-Image screenshot repro,
/// which measured that exact size), and a 1.0x camera-speed multiplier that <see cref="CameraRig"/>
/// folds into its existing pan/zoom acceleration constants without changing them when left at
/// default. A machine with no <c>settings.cfg</c> yet — every headless verification run in this
/// project's history, none of which ever wrote one — therefore renders and paces identically to
/// before Stage 6 existed.</para>
/// </summary>
public static class GameSettings
{
    private const string ConfigPath = "user://settings.cfg";

    public const int DefaultWindowWidth = 1152;
    public const int DefaultWindowHeight = 648;

    public static float Volume = 1.0f; // linear 0..1; 1.0 == unity gain == engine default.
    public static int WindowWidth = DefaultWindowWidth;
    public static int WindowHeight = DefaultWindowHeight;
    public static bool Fullscreen = false;
    public static float CameraSpeedMultiplier = 1.0f;

    private static bool _loaded;

    /// <summary>Reads persisted values off disk into the static fields above. Safe to call more than
    /// once (e.g. from both <see cref="MainMenu"/> and <see cref="CameraRig"/>, since a direct
    /// <c>Main.tscn</c> launch that skips the menu entirely still needs the camera-speed setting) —
    /// only the first call actually touches the filesystem.</summary>
    public static void Load()
    {
        if (_loaded) return;
        _loaded = true;

        var cfg = new ConfigFile();
        if (cfg.Load(ConfigPath) != Error.Ok) return; // first-ever run: keep the defaults above.

        Volume = (float)(double)cfg.GetValue("audio", "volume", Volume);
        WindowWidth = (int)(long)cfg.GetValue("display", "width", (long)WindowWidth);
        WindowHeight = (int)(long)cfg.GetValue("display", "height", (long)WindowHeight);
        Fullscreen = (bool)cfg.GetValue("display", "fullscreen", Fullscreen);
        CameraSpeedMultiplier = (float)(double)cfg.GetValue("camera", "speed", CameraSpeedMultiplier);
    }

    /// <summary>Writes the current values to disk. Called on discrete settings changes (resolution
    /// picked, fullscreen toggled) immediately, and on slider changes only at
    /// <c>Slider.DragEnded</c> — <see cref="Apply"/> already gives live feedback on every drag tick,
    /// so there's no need to hit the filesystem on every one of those too.</summary>
    public static void Save()
    {
        var cfg = new ConfigFile();
        cfg.SetValue("audio", "volume", Volume);
        cfg.SetValue("display", "width", WindowWidth);
        cfg.SetValue("display", "height", WindowHeight);
        cfg.SetValue("display", "fullscreen", Fullscreen);
        cfg.SetValue("camera", "speed", CameraSpeedMultiplier);
        cfg.Save(ConfigPath);
    }

    /// <summary>Applies every persisted value to live engine state — called once on MainMenu boot
    /// (right after <see cref="Load"/>) and again immediately whenever a settings control changes, so
    /// the menu reflects what's about to be saved without needing a restart.
    ///
    /// <para>Deliberately a no-op under any harness-capture flag (<c>--shot-out</c>/
    /// <c>--dump-ui-out</c>/<c>--perf-frames</c>): those recipes assume a fixed, known viewport size
    /// (CLAUDE.md's windowed-offscreen trap-list entries measure and compare against it), and
    /// <c>user://</c> is a real per-machine profile directory — a settings.cfg left over from an
    /// interactive session on this dev box must never silently resize a screenshot/perf run launched
    /// later against the same Windows user profile.</para></summary>
    public static void Apply()
    {
        bool captureRun = GreyBoxArgs.Get("shot-out") != null
            || GreyBoxArgs.Get("dump-ui-out") != null
            || GreyBoxArgs.Get("perf-frames") != null;
        if (captureRun) return;

        int master = AudioServer.GetBusIndex("Master");
        if (master >= 0)
        {
            AudioServer.SetBusMute(master, Volume <= 0.0001f);
            if (Volume > 0.0001f)
            {
                AudioServer.SetBusVolumeDb(master, Mathf.LinearToDb(Volume));
            }
        }

        // --headless runs the dummy display driver (CLAUDE.md trap list: null Image, no real
        // viewport) — window mode/size calls against it are pointless at best, so every headless
        // verification invocation (not just the ones that also pass a capture flag above) skips
        // this half of Apply() too.
        if (OS.HasFeature("headless")) return;

        DisplayServer.WindowSetMode(Fullscreen ? DisplayServer.WindowMode.Fullscreen : DisplayServer.WindowMode.Windowed);
        if (!Fullscreen)
        {
            DisplayServer.WindowSetSize(new Vector2I(WindowWidth, WindowHeight));
        }
    }
}
