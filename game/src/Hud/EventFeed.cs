using System;
using System.Collections.Generic;
using Godot;

namespace Hearthspan.Game.Hud;

/// <summary>
/// Plan v5 M3 (D10): the news, down the right side — what just happened that matters to you, newest
/// on top, one or two lines each, fading as it ages and gone after a few turns. Click an item to fly
/// to where it happened. Replaces the turn report panel with a narrow feed that shows only events
/// worth knowing about and keeps most of the map clear.
/// </summary>
public partial class EventFeed : VBoxContainer, IUiDumpable
{
    public const int MaxEntries = 7;
    public const int KeepTurns = 5;

    public sealed class Entry
    {
        public string Text = "";
        public Color Color = HudStyle.Gold;
        public Icon Icon = Icon.Star;
        public int Turn;
        public int? FocusCol, FocusRow;
        public string Tooltip = "";
    }

    public Action<int, int>? FocusRequested;

    private readonly List<Entry> _entries = new();
    private int _currentTurn;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        AddThemeConstantOverride("separation", 5);
    }

    public IReadOnlyList<Entry> Entries => _entries;

    public void Add(Entry entry)
    {
        _entries.Insert(0, entry);
        if (_entries.Count > MaxEntries) _entries.RemoveRange(MaxEntries, _entries.Count - MaxEntries);
        Rebuild();
    }

    public void SetTurn(int turn)
    {
        _currentTurn = turn;
        _entries.RemoveAll(e => e.Turn < turn - KeepTurns);
        Rebuild();
    }

    public void ClearAll()
    {
        _entries.Clear();
        Rebuild();
    }

    private void Rebuild()
    {
        HudStyle.Clear(this);
        foreach (var e in _entries) AddChild(BuildEntry(e));
    }

    private Control BuildEntry(Entry e)
    {
        var panel = new ClickPanel { TooltipText = e.Tooltip };
        var normal = HudStyle.Box(HudStyle.CardBg with { A = 0.86f }, null, 0, 5, 8, 5);
        normal.BorderColor = e.Color;
        normal.BorderWidthLeft = 3;
        var hover = HudStyle.Box(HudStyle.CardHeaderBg, HudStyle.LineSoft, 1, 5, 8, 5);
        hover.BorderColor = e.Color;
        hover.BorderWidthLeft = 3;
        panel.SetStyles(normal, hover);
        if (e.FocusCol is int col && e.FocusRow is int row) panel.Clicked = () => FocusRequested?.Invoke(col, row);
        else panel.MouseDefaultCursorShape = CursorShape.Arrow;

        var line = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        line.AddThemeConstantOverride("separation", 7);
        line.AddChild(new IconRect(e.Icon, 16, e.Color));
        var text = HudStyle.Label(e.Text, 13, HudStyle.Text, wrap: true);
        text.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        line.AddChild(text);
        panel.AddChild(line);

        int age = Math.Max(0, _currentTurn - e.Turn);
        panel.Modulate = new Color(1, 1, 1, age == 0 ? 1f : age == 1 ? 0.82f : 0.62f);
        return panel;
    }

    public string DumpUiDigest()
    {
        var lines = new List<string> { $"EventFeed entries={_entries.Count} turn={_currentTurn}" };
        foreach (var e in _entries) lines.Add($"  [t{e.Turn}] {e.Text}");
        return string.Join('\n', lines);
    }
}
