using System.Collections.Generic;
using Godot;

namespace Hearthspan.Game;

/// <summary>
/// Autoload that turns any scene into something `hearthspan shot` / `dump-ui` can drive headlessly
/// from a shell (CLAUDE.md governing rule). --shot-out=path.png (windowed, real renderer only — see
/// the trap list: --headless returns a null Image) captures after --frames warm-up frames.
/// --dump-ui-out=path.txt asks the current scene for a text digest if it implements IUiDumpable, and
/// works fine headless since it never touches pixels. --perf-frames=N (Stage 1 task 9, §5.7) measures
/// real per-frame render cost over N frames after the same warm-up — windowed-offscreen only, per the
/// trap list: headless `_Draw()`/frame timing is ~60x off real cost (docs/phase-3-spikes.md).
/// </summary>
public partial class AppRunner : Node
{
    private int _frame;
    private int _targetFrame;
    private string? _shotOut;
    private string? _dumpUiOut;
    private string? _cameraBookmark;
    private int _perfFrameCount;
    private readonly List<double> _perfFrameTimesMs = new();
    private bool _perfDone;

    public override void _Ready()
    {
        _shotOut = GreyBoxArgs.Get("shot-out");
        _dumpUiOut = GreyBoxArgs.Get("dump-ui-out");
        _cameraBookmark = GreyBoxArgs.Get("camera");
        _targetFrame = GreyBoxArgs.GetInt("frames", 30);
        _perfFrameCount = GreyBoxArgs.GetInt("perf-frames", 0);
    }

    public override void _Process(double delta)
    {
        _frame++;

        // Applied on the first frame the current scene is actually a GameSession, once: every node
        // already in the tree at that point (including GameSession, which builds the world and does
        // its own default CameraRig.FrameMap call) has finished its own _Ready() by the time any
        // node's _Process() runs, so the bookmark is guaranteed to land after — and override — that
        // default framing, not race it. Plan v3 task 5.1 (found via the exported-build launch route):
        // frame 1 alone is not enough when launch goes through MainMenu.tscn (the real exported exe's
        // run/main_scene) rather than Main.tscn directly — MainMenu's skip-menu forwarding defers its
        // ChangeSceneToFile a frame (Stage 6 task fix), so on frame 1 CurrentScene is still MainMenu,
        // and the bookmark silently no-opped on every exported-build launch that named a camera.
        // Retrying until the scene actually is a GameSession (capped so a genuinely wrong scene still
        // errors instead of hanging silently) fixes both routes without racing GameSession's own
        // default framing on the direct-launch path, where it still lands on frame 1 as before.
        if (_cameraBookmark != null)
        {
            if (GetTree().CurrentScene is GameSession)
            {
                ApplyCameraBookmark(_cameraBookmark);
                _cameraBookmark = null;
            }
            else if (_frame >= 5)
            {
                GD.PrintErr($"AppRunner: --camera {_cameraBookmark} requested but the current scene is not a GameSession after {_frame} frames.");
                _cameraBookmark = null;
            }
        }

        if (_dumpUiOut != null && _frame >= 2)
        {
            DoDumpUi();
            _dumpUiOut = null;
        }

        if (_shotOut != null && _frame >= _targetFrame)
        {
            DoScreenshot();
            _shotOut = null;
        }

        // Same warm-up window as the screenshot path (_targetFrame — shaders compiled, MultiMeshes
        // built, camera settled), then _perfFrameCount consecutive samples of real frame delta.
        if (_perfFrameCount > 0 && !_perfDone && _frame > _targetFrame)
        {
            _perfFrameTimesMs.Add(delta * 1000.0);
            if (_perfFrameTimesMs.Count >= _perfFrameCount)
            {
                ReportPerf();
                _perfDone = true;
            }
        }

        bool perfPending = _perfFrameCount > 0 && !_perfDone;
        if (_shotOut == null && _dumpUiOut == null && !perfPending
            && (GreyBoxArgs.Get("shot-out") != null || GreyBoxArgs.Get("dump-ui-out") != null || GreyBoxArgs.Get("perf-frames") != null))
        {
            GetTree().Quit(0);
        }
    }

    /// <summary>Prints p50/p95/max frame time in milliseconds as one JSON line — the render-budget
    /// number 5.7 wants, in a form a shell script can parse the same way `hearthspan hash`/`balance`
    /// JSON output already is, without needing a dedicated harness subcommand for what the plan itself
    /// calls "a scripted measurement, not a CI gate."</summary>
    private void ReportPerf()
    {
        var sorted = new List<double>(_perfFrameTimesMs);
        sorted.Sort();
        double Percentile(double p)
        {
            int idx = Mathf.Clamp((int)(p * (sorted.Count - 1)), 0, sorted.Count - 1);
            return sorted[idx];
        }
        double sum = 0;
        foreach (var t in sorted) sum += t;

        var json = new Godot.Collections.Dictionary
        {
            ["frames"] = sorted.Count,
            ["meanMs"] = sum / sorted.Count,
            ["p50Ms"] = Percentile(0.50),
            ["p95Ms"] = Percentile(0.95),
            ["maxMs"] = sorted[^1],
        };
        GD.Print("AppRunner: perf " + Json.Stringify(json));
    }

    private void DoDumpUi()
    {
        var scene = GetTree().CurrentScene;
        string digest = scene is IUiDumpable dumpable
            ? dumpable.DumpUiDigest()
            : $"(scene '{scene?.Name}' does not implement IUiDumpable)";
        System.IO.File.WriteAllText(_dumpUiOut!, digest);
        GD.Print($"AppRunner: wrote dump-ui digest to {_dumpUiOut}");
    }

    private void DoScreenshot()
    {
        var img = GetViewport().GetTexture().GetImage();
        img.SavePng(_shotOut);
        GD.Print($"AppRunner: saved screenshot to {_shotOut} ({img.GetWidth()}x{img.GetHeight()})");
    }

    // Stage 7 task B2 (docs/stage-7-plan.md): bookmark resolution moved to
    // GameSession.ApplyCameraBookmark, which has the live World a real bookmark needs (the player's
    // capital, the actual grid dimensions) — AppRunner only knows the current scene, not the game.
    private void ApplyCameraBookmark(string name)
    {
        if (GetTree().CurrentScene is not GameSession session)
        {
            GD.PrintErr($"AppRunner: --camera {name} requested but the current scene is not a GameSession.");
            return;
        }
        if (!session.ApplyCameraBookmark(name))
        {
            GD.PrintErr($"AppRunner: unknown camera bookmark '{name}'. Known: {string.Join(", ", CameraRig.BookmarkNames)}");
        }
    }
}
