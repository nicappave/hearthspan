using System;
using System.Collections.Generic;
using System.Text.Json;
using Godot;

namespace Hearthspan.Game;

/// <summary>
/// Drives Killer Spike A's measurement (docs/development-plan.md Phase 3 item 3): continuous
/// synthetic pan/zoom against a generated 2000+-node graph, forcing a full _Draw() every frame — the
/// worst case, since a static view would trivially hit target frame time. Records per-frame draw time
/// and per-frame hit-test latency, writes a JSON metrics file, then quits.
/// </summary>
public partial class TechTreeSpikeRunner : Node, IUiDumpable
{
    private TechTreeView _view = null!;
    private int _frame;
    private int _warmupFrames;
    private int _benchFrames;
    private string? _benchOut;
    private readonly List<double> _drawMicros = new();
    private readonly List<double> _hitTestMicros = new();
    private readonly List<double> _frameMicros = new();
    private ulong _lastTick;

    public override void _Ready()
    {
        _view = GetNode<TechTreeView>("TechTreeView");
        _warmupFrames = GreyBoxArgs.GetInt("warmup-frames", 15);
        _benchFrames = GreyBoxArgs.GetInt("bench-frames", 180);
        _benchOut = GreyBoxArgs.Get("bench-out");
        _lastTick = Time.GetTicksUsec();
    }

    public override void _Process(double delta)
    {
        _frame++;

        ulong now = Time.GetTicksUsec();
        double frameMicros = now - _lastTick;
        _lastTick = now;

        // Continuous pan + oscillating zoom — guarantees a different visible set every frame so the
        // Control can never coast on an unchanged, already-drawn frame.
        float t = _frame * 0.05f;
        var pan = new Vector2(-t * 40f % 4000f, Mathf.Sin(t) * 200f);
        float zoom = 0.5f + 0.5f * (Mathf.Sin(t * 0.7f) + 1f) * 0.5f;
        _view.SetView(pan, zoom);

        var hitPos = _view.Size * 0.5f;
        _view.HitTest(hitPos);

        if (_frame > _warmupFrames && _frame <= _warmupFrames + _benchFrames)
        {
            _drawMicros.Add(_view.LastDrawMicros);
            _hitTestMicros.Add(_view.LastHitTestMicros);
            _frameMicros.Add(frameMicros);
        }

        if (_frame >= _warmupFrames + _benchFrames)
        {
            if (_benchOut != null) WriteResults();
            GetTree().Quit(0);
        }
    }

    private void WriteResults()
    {
        _drawMicros.Sort();
        _hitTestMicros.Sort();
        _frameMicros.Sort();

        var payload = new
        {
            nodeCount = GreyBoxArgs.GetInt("node-count", 2000),
            sampledFrames = _drawMicros.Count,
            drawMicros = Summarize(_drawMicros),
            hitTestMicros = Summarize(_hitTestMicros),
            frameMicros = Summarize(_frameMicros),
            impliedFps = _frameMicros.Count > 0 ? 1_000_000.0 / Percentile(_frameMicros, 0.5) : 0,
        };

        System.IO.File.WriteAllText(_benchOut!, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        GD.Print($"TechTreeSpikeRunner: wrote metrics to {_benchOut}");
    }

    private static object Summarize(List<double> sorted) => new
    {
        min = sorted.Count > 0 ? sorted[0] : 0,
        p50 = Percentile(sorted, 0.5),
        p95 = Percentile(sorted, 0.95),
        max = sorted.Count > 0 ? sorted[^1] : 0,
    };

    private static double Percentile(List<double> sorted, double p)
    {
        if (sorted.Count == 0) return 0;
        int idx = Math.Clamp((int)(p * (sorted.Count - 1)), 0, sorted.Count - 1);
        return sorted[idx];
    }

    public string DumpUiDigest() => _view.DumpUiDigest();
}
