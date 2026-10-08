using System.Linq;
using Godot;

namespace Hearthspan.Game;

/// <summary>
/// Plan v3 task 3.8: three honest columns — what you can build (with cost, turns and effect), what is
/// queued, what is done. The 6x6 grid (Killer Spike B's district-placement proof, docs/development-plan.md
/// Phase 3 item 4) never meant anything to a player — nothing in the sim reads a cell index, so a
/// building "placed" at cell 7 behaved identically to one at cell 30. Real Button/Label rows in a
/// VBoxContainer per ScrollContainer column (rule 5's usual allowance: at most a few dozen real nodes,
/// same pattern <see cref="SelectionPanel"/>'s verb list already uses), not the grid's immediate-mode
/// cells. <see cref="PendingConstructionDto"/>/<c>CellIndex</c> stay in the DTO (nothing else reads them
/// either, but deleting a wire format is a bigger, unrelated change) — this view simply stops consuming
/// them.
/// </summary>
public partial class BuildView : Control, IUiDumpable
{
    private const float Left = 20f;
    private const float Top = 56f;
    private const float ColumnGap = 24f;
    private const float AvailableWidth = 520f;
    private const float QueueWidth = 380f;
    private const float CompletedWidth = 380f;
    private const float SwitcherHeight = 150f;
    private const float RowGap = 14f;
    private const float ButtonHeight = 26f;

    public readonly record struct AvailableRow(
        string Id, string Name, bool Hero, int Cost, int Turns, string Effect, bool Locked, string LockReason, string Tooltip);

    public readonly record struct QueueRow(string Id, string Name, int Turns);
    public readonly record struct CompletedRow(string Name, string Effect);

    private WorldViewDto? _world;
    private SettlementViewDto? _settlement;

    private System.Collections.Generic.IReadOnlyList<AvailableRow> _available = System.Array.Empty<AvailableRow>();
    private System.Collections.Generic.IReadOnlyList<QueueRow> _queue = System.Array.Empty<QueueRow>();
    private System.Collections.Generic.IReadOnlyList<CompletedRow> _completed = System.Array.Empty<CompletedRow>();
    private System.Collections.Generic.IReadOnlyList<(int id, string label)> _ownedSettlements = System.Array.Empty<(int, string)>();

    private bool _showLocked;
    private Button? _showLockedToggle;
    private int _lockedCount;

    private ScrollContainer _availableScroll = null!;
    private VBoxContainer _availableList = null!;
    private ScrollContainer _queueScroll = null!;
    private VBoxContainer _queueList = null!;
    private ScrollContainer _switcherScroll = null!;
    private VBoxContainer _switcherList = null!;
    private ScrollContainer _completedScroll = null!;
    private VBoxContainer _completedList = null!;

    private readonly System.Collections.Generic.List<Button> _availableButtons = new();
    private readonly System.Collections.Generic.List<Button> _queueButtons = new();
    private readonly System.Collections.Generic.List<Button> _switcherButtons = new();

    [Signal]
    public delegate void BuildingQueuedEventHandler(string buildingId);

    [Signal]
    public delegate void QueueItemCancelledEventHandler(int queueIndex);

    [Signal]
    public delegate void QueueItemMovedEventHandler(int queueIndex, int delta);

    [Signal]
    public delegate void SettlementSelectedEventHandler(int settlementId);

    private (float availableX, float queueX, float completedX, float columnHeight) LayoutMetrics()
    {
        var size = Size;
        if (size.Y <= 1f) size = new Vector2(1600f, 860f);

        float availableX = Left;
        float queueX = availableX + AvailableWidth + ColumnGap;
        float completedX = queueX + QueueWidth + ColumnGap;
        float columnHeight = Mathf.Max(60f, size.Y - Top - 20f);
        return (availableX, queueX, completedX, columnHeight);
    }

    private void ApplyLayout()
    {
        var (availableX, queueX, completedX, columnHeight) = LayoutMetrics();

        _availableScroll.Position = new Vector2(availableX, Top);
        _availableScroll.Size = new Vector2(AvailableWidth, columnHeight);

        _queueScroll.Position = new Vector2(queueX, Top);
        _queueScroll.Size = new Vector2(QueueWidth, columnHeight);

        _switcherScroll.Position = new Vector2(completedX, Top);
        _switcherScroll.Size = new Vector2(CompletedWidth, SwitcherHeight);

        _completedScroll.Position = new Vector2(completedX, Top + SwitcherHeight + 20f);
        _completedScroll.Size = new Vector2(CompletedWidth, columnHeight - SwitcherHeight - 20f);
    }

    /// <summary>Buildable-but-not-yet-queued options — unlocked first (each alphabetical, via
    /// GameSession's own ordering), then locked ones collapsed under "Show locked (N)". Each unlocked
    /// row is name/cost/turns plus its effect line and a real Queue button; a locked row is just its
    /// name and what it needs, disabled, with no Queue button at all.</summary>
    public void SetAvailableBuildings(System.Collections.Generic.IReadOnlyList<AvailableRow> available)
    {
        _available = available;
        RebuildAvailableList();
    }

    private void RebuildAvailableList()
    {
        ApplyLayout();
        foreach (var b in _availableButtons) b.QueueFree();
        _availableButtons.Clear();
        foreach (var child in _availableList.GetChildren()) child.QueueFree();
        _showLockedToggle = null;

        var unlocked = _available.Where(a => !a.Locked).ToList();
        var locked = _available.Where(a => a.Locked).ToList();
        _lockedCount = locked.Count;

        if (unlocked.Count == 0)
        {
            var cta = new Label
            {
                Text = "Nothing is unlocked here yet. Research something in the Tech Tree to open more buildings.",
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
            };
            cta.AddThemeColorOverride("font_color", MapPalette.UiAccent);
            _availableList.AddChild(cta);
        }

        foreach (var row in unlocked) AddAvailableRow(row, locked: false);

        if (_lockedCount > 0)
        {
            _showLockedToggle = new Button
            {
                Text = _showLocked ? $"Hide locked ({_lockedCount})" : $"Show locked ({_lockedCount})",
                CustomMinimumSize = new Vector2(0, ButtonHeight),
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
            };
            _showLockedToggle.Pressed += () => { _showLocked = !_showLocked; RebuildAvailableList(); };
            _availableList.AddChild(_showLockedToggle);

            if (_showLocked)
            {
                foreach (var row in locked) AddAvailableRow(row, locked: true);
            }
        }
    }

    private void AddAvailableRow(AvailableRow row, bool locked)
    {
        // Plan v5 M3: each building is a card; landmarks wear a gold edge.
        var card = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        var box = Hud.HudStyle.Box(Hud.HudStyle.CardBg, row.Hero && !locked ? Hud.HudStyle.Gold : Hud.HudStyle.LineSoft, 1, 6, 12, 8);
        if (row.Hero && !locked) box.BorderWidthLeft = 3;
        card.AddThemeStyleboxOverride("panel", box);
        var container = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        container.AddThemeConstantOverride("separation", 2);
        card.AddChild(container);

        var topRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        string star = row.Hero ? "★ " : "";
        string turnsText = row.Turns >= 0 ? $"{row.Turns} turn{(row.Turns == 1 ? "" : "s")}" : "no production here yet";
        string nameLine = locked ? $"{star}{row.Name}   {row.LockReason}" : $"{star}{row.Name}   cost {row.Cost} · {turnsText}";
        var nameLabel = new Label { Text = nameLine, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        nameLabel.AddThemeFontSizeOverride("font_size", 15);
        if (locked) nameLabel.AddThemeColorOverride("font_color", MapPalette.UiTextMuted);
        else if (row.Hero) nameLabel.AddThemeColorOverride("font_color", Hud.HudStyle.Gold);
        topRow.AddChild(nameLabel);

        if (!locked)
        {
            string buildingId = row.Id;
            var queueButton = new Button
            {
                Text = "Queue",
                CustomMinimumSize = new Vector2(70, ButtonHeight),
                TooltipText = row.Tooltip,
            };
            queueButton.Pressed += () => EmitSignal(SignalName.BuildingQueued, buildingId);
            Hud.HudStyle.Style(queueButton, Hud.ButtonKind.Primary);
            topRow.AddChild(queueButton);
            _availableButtons.Add(queueButton);
        }
        container.AddChild(topRow);

        if (!locked)
        {
            string effectText = row.Hero ? $"{row.Effect}, landmark" : row.Effect;
            var effectLabel = new Label { Text = effectText, AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = SizeFlags.ExpandFill };
            effectLabel.AddThemeFontSizeOverride("font_size", 12);
            effectLabel.AddThemeColorOverride("font_color", MapPalette.UiTextSecondary);
            container.AddChild(effectLabel);
        }

        _availableList.AddChild(card);
    }

    /// <summary>The settlement's live build queue, front first, each row's turns figure already
    /// cumulative (GameSession computes it — see <see cref="QueueRow.Turns"/>'s doc site).</summary>
    public void SetQueue(System.Collections.Generic.IReadOnlyList<string> queueIds,
                         System.Collections.Generic.IReadOnlyDictionary<string, string> displayNames,
                         System.Collections.Generic.IReadOnlyList<int> turnsPerItem)
    {
        var rows = new System.Collections.Generic.List<QueueRow>(queueIds.Count);
        for (int i = 0; i < queueIds.Count; i++)
        {
            string name = displayNames.TryGetValue(queueIds[i], out var display) ? display : queueIds[i];
            int turns = i < turnsPerItem.Count ? turnsPerItem[i] : -1;
            rows.Add(new QueueRow(queueIds[i], name, turns));
        }
        _queue = rows;
        RebuildQueueList();
    }

    private void RebuildQueueList()
    {
        ApplyLayout();
        foreach (var b in _queueButtons) b.QueueFree();
        _queueButtons.Clear();
        foreach (var child in _queueList.GetChildren()) child.QueueFree();

        if (_queue.Count == 0)
        {
            var empty = new Label { Text = "Nothing queued.", AutowrapMode = TextServer.AutowrapMode.WordSmart };
            empty.AddThemeColorOverride("font_color", MapPalette.UiAccent);
            _queueList.AddChild(empty);
            return;
        }

        for (int i = 0; i < _queue.Count; i++)
        {
            int index = i;
            var row = _queue[i];
            string turnsText = row.Turns >= 0 ? $"{row.Turns} turn{(row.Turns == 1 ? "" : "s")}" : "unknown time";
            string suffix = index == 0 ? "  (building)" : "";

            var container = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            container.AddThemeConstantOverride("separation", 2);

            var topRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            var label = new Label { Text = $"{index + 1}. {row.Name}{suffix}", SizeFlagsHorizontal = SizeFlags.ExpandFill };
            topRow.AddChild(label);

            var up = new Button { Text = "▲", CustomMinimumSize = new Vector2(28, ButtonHeight), Disabled = index == 0, TooltipText = "Move this item earlier in the build queue." };
            up.Pressed += () => EmitSignal(SignalName.QueueItemMoved, index, -1);
            var down = new Button { Text = "▼", CustomMinimumSize = new Vector2(28, ButtonHeight), Disabled = index == _queue.Count - 1, TooltipText = "Move this item later in the build queue." };
            down.Pressed += () => EmitSignal(SignalName.QueueItemMoved, index, 1);
            var cancel = new Button
            {
                Text = "✕",
                CustomMinimumSize = new Vector2(28, ButtonHeight),
                TooltipText = index == 0
                    ? "Cancel this item — it is currently under construction, so the production already invested in it is forfeited."
                    : "Remove this item from the queue. It hasn't started yet, so nothing is lost.",
            };
            cancel.Pressed += () => EmitSignal(SignalName.QueueItemCancelled, index);
            topRow.AddChild(up);
            topRow.AddChild(down);
            topRow.AddChild(cancel);
            container.AddChild(topRow);

            var turnsLabel = new Label { Text = turnsText };
            turnsLabel.AddThemeFontSizeOverride("font_size", 11);
            turnsLabel.AddThemeColorOverride("font_color", MapPalette.UiTextMuted);
            container.AddChild(turnsLabel);

            _queueList.AddChild(container);
            _queueButtons.Add(up);
            _queueButtons.Add(down);
            _queueButtons.Add(cancel);
        }
    }

    /// <summary>Completed buildings for the current settlement, with their effect text — the record of
    /// what standing here actually does, not just what's still to come.</summary>
    public void SetCompletedBuildings(System.Collections.Generic.IReadOnlyList<(string Name, string Effect)> completed)
    {
        _completed = completed.Select(c => new CompletedRow(c.Name, c.Effect)).ToList();
        RebuildCompletedList();
    }

    private void RebuildCompletedList()
    {
        ApplyLayout();
        foreach (var child in _completedList.GetChildren()) child.QueueFree();

        if (_completed.Count == 0)
        {
            var empty = new Label { Text = "Nothing completed here yet.", AutowrapMode = TextServer.AutowrapMode.WordSmart };
            empty.AddThemeColorOverride("font_color", MapPalette.UiTextMuted);
            _completedList.AddChild(empty);
            return;
        }

        foreach (var row in _completed)
        {
            var container = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            container.AddThemeConstantOverride("separation", 2);
            container.AddChild(new Label { Text = row.Name });
            var effectLabel = new Label { Text = row.Effect, AutowrapMode = TextServer.AutowrapMode.WordSmart };
            effectLabel.AddThemeFontSizeOverride("font_size", 11);
            effectLabel.AddThemeColorOverride("font_color", MapPalette.UiTextMuted);
            container.AddChild(effectLabel);
            _completedList.AddChild(container);
        }
    }

    /// <summary>Every settlement the player owns, so a build view exists for all of them rather than
    /// for the capital alone. Sits above the Completed list within the same right-hand column.</summary>
    public void SetOwnedSettlements(System.Collections.Generic.IReadOnlyList<(int id, string label)> owned, int selectedId)
    {
        ApplyLayout();
        _ownedSettlements = owned;
        foreach (var b in _switcherButtons) { _switcherList.RemoveChild(b); b.QueueFree(); }
        _switcherButtons.Clear();

        foreach (var (id, label) in owned)
        {
            var button = new Button
            {
                Text = label,
                CustomMinimumSize = new Vector2(0, 28),
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                Disabled = id == selectedId,
                TooltipText = id == selectedId ? "This is the settlement currently shown." : $"Switch the build view to {label}.",
            };
            button.Pressed += () => EmitSignal(SignalName.SettlementSelected, id);
            _switcherList.AddChild(button);
            _switcherButtons.Add(button);
        }
    }

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Stop;

        _availableList = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _availableList.AddThemeConstantOverride("separation", (int)RowGap);
        _availableScroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _availableScroll.AddChild(_availableList);
        AddChild(_availableScroll);

        _queueList = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _queueList.AddThemeConstantOverride("separation", (int)RowGap);
        _queueScroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _queueScroll.AddChild(_queueList);
        AddChild(_queueScroll);

        _switcherList = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _switcherScroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _switcherScroll.AddChild(_switcherList);
        AddChild(_switcherScroll);

        _completedList = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _completedList.AddThemeConstantOverride("separation", (int)RowGap);
        _completedScroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _completedScroll.AddChild(_completedList);
        AddChild(_completedScroll);

        ApplyLayout();
    }

    /// <summary>Re-projects from a fresh WorldView snapshot (rule 4) for the given settlement. Called
    /// by GameSession after every turn/order.</summary>
    public void LoadWorld(string path, int settlementId)
    {
        _world = WorldViewDto.Load(path);
        if (settlementId < 0)
        {
            settlementId = _world.Civilizations.FirstOrDefault()?.CapitalSettlementId ?? _world.Settlements.FirstOrDefault()?.Id ?? -1;
        }
        _settlement = _world.Settlements.FirstOrDefault(s => s.Id == settlementId);

        if (_settlement == null)
        {
            GD.PrintErr($"BuildView: settlement {settlementId} not found in worldview.");
            return;
        }

        QueueRedraw();
    }

    public override void _Draw()
    {
        if (_world == null || _settlement == null) return;
        ApplyLayout();

        var font = MapPalette.UiFont;
        // UiScreenBg, not UiPanelBg - see its doc comment (docs/stage-7-plan.md defect H): UiPanelBg's
        // 0.88 alpha is for small floating overlays, and lets the map bleed through at full screen.
        DrawRect(new Rect2(Vector2.Zero, Size), MapPalette.UiScreenBg, filled: true);

        DrawString(font, new Vector2(Left, 26),
            $"{MapPalette.SettlementName(_settlement.Id)} — {(SettlementTierName)_settlement.Tier}, {_settlement.Population} people",
            HorizontalAlignment.Left, -1, 19, MapPalette.UiTextPrimary);
        DrawString(font, new Vector2(Size.X - 330, 26), "B or Esc: back to the map",
            HorizontalAlignment.Left, -1, 13, MapPalette.UiTextMuted);

        var (availableX, queueX, completedX, _) = LayoutMetrics();
        var kicker = Hud.HudStyle.GoldDim;
        DrawString(font, new Vector2(availableX, Top - 8), "CAN BUILD", HorizontalAlignment.Left, -1, 12, kicker);
        DrawString(font, new Vector2(queueX, Top - 8), $"BUILD QUEUE ({_queue.Count})", HorizontalAlignment.Left, -1, 12, kicker);
        DrawString(font, new Vector2(completedX, Top - 8), $"YOUR SETTLEMENTS ({_ownedSettlements.Count})", HorizontalAlignment.Left, -1, 12, kicker);
        DrawString(font, new Vector2(completedX, Top + SwitcherHeight + 12), $"STANDING HERE ({_completed.Count})", HorizontalAlignment.Left, -1, 12, kicker);
    }

    public string DumpUiDigest()
    {
        if (_world == null || _settlement == null) return "(no worldview/settlement loaded)";
        var lines = new System.Collections.Generic.List<string>
        {
            $"BuildView settlement={_settlement.Id} pop={_settlement.Population} tier={(SettlementTierName)_settlement.Tier}",
            $"available count={_available.Count} unlocked={_available.Count(a => !a.Locked)} locked={_lockedCount} showLocked={_showLocked}",
        };
        lines.AddRange(_available.Select(a => a.Locked
            ? $"  {a.Name} needs={a.LockReason}"
            : $"  {a.Name} cost={a.Cost} turns={a.Turns} effect=\"{a.Effect}\" hero={a.Hero}"));
        lines.Add($"buildQueue count={_queue.Count}");
        lines.AddRange(_queue.Select((q, i) => $"  {i}: {q.Name} turns={q.Turns}{(i == 0 ? " (building)" : "")}"));
        lines.Add($"completed count={_completed.Count}");
        lines.AddRange(_completed.Select(c => $"  {c.Name} effect=\"{c.Effect}\""));
        lines.Add($"ownedSettlements count={_ownedSettlements.Count} selected={_settlement.Id}");
        lines.AddRange(_ownedSettlements.Select(s => $"  id={s.id} {s.label}"));
        lines.Add($"switcherControls count={_switcherButtons.Count}");
        lines.AddRange(_switcherButtons.Select(b => $"  \"{b.Text}\" tooltip=\"{b.TooltipText}\""));
        return string.Join('\n', lines);
    }
}
