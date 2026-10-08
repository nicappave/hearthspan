using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Hearthspan.Game.Hud;

/// <summary>
/// Plan v5 M3 (D10): what is selected, bottom-left — a settlement, a unit or a bare tile — as one
/// compact card: a coloured header, a few icon rows (with bars where a number has a goal), and the
/// actions that make sense right now as a two-column grid of buttons. Actions that are not possible
/// yet are hidden rather than listed greyed (the tech tree is where you browse the future); actions
/// that are possible in principle but blocked right now stay, disabled, with the reason on hover.
/// GameSession fills the model; this class only draws it.
/// </summary>
public partial class SelectionCard : PanelContainer, IUiDumpable
{
    public const float Width = 392f;

    public sealed class Row
    {
        public Icon? Icon;
        public Color? IconTint;
        public string Text = "";
        public Color? Color;
        public double? Progress;
        public Color? BarColor;
        public string Tooltip = "";
        public int FontSize = 14;
        /// <summary>When set, the row is a strip of small icon + value pairs instead of a sentence.</summary>
        public List<(Icon Icon, string Text, string Tooltip)>? Chips;
    }

    public sealed class Action
    {
        public string Label = "";
        public string Tooltip = "";
        public bool Enabled = true;
        public string Reason = "";
        public ButtonKind Kind = ButtonKind.Normal;
        public System.Action? Commit;
        /// <summary>Takes a whole row of the grid (long labels, the one main action).</summary>
        public bool Wide;
        /// <summary>When set, the action sits in a collapsible section of that name ("Edicts"),
        /// folded until the player opens it; the open/closed state is remembered per name.</summary>
        public string? Group;
    }

    public sealed class Model
    {
        public string Key = "";
        public string Title = "";
        public string Subtitle = "";
        public Color Accent = HudStyle.Gold;
        public Icon? TitleIcon;
        public List<Row> Rows = new();
        public List<Action> Actions = new();
        public string? Hint;
    }

    public System.Action? CloseRequested;

    /// <summary>Folding sections the player has opened (by name), kept across reloads of the card.</summary>
    private static readonly HashSet<string> OpenGroups = new();

    private VBoxContainer _body = null!;
    private Model? _model;

    public override void _Ready()
    {
        AnchorLeft = 0; AnchorRight = 0; AnchorTop = 1; AnchorBottom = 1;
        OffsetLeft = 12; OffsetRight = 12 + Width; OffsetTop = -12; OffsetBottom = -12;
        GrowVertical = GrowDirection.Begin;
        CustomMinimumSize = new Vector2(Width, 0);
        MouseFilter = MouseFilterEnum.Stop;
        AddThemeStyleboxOverride("panel", HudStyle.Box(HudStyle.CardBg, HudStyle.LineSoft, 1, 7, 0, 0));
        _body = new VBoxContainer();
        _body.AddThemeConstantOverride("separation", 0);
        AddChild(_body);
        Visible = false;
    }

    public bool IsShowing => Visible && _model is not null;
    public string CurrentKey => _model?.Key ?? "";

    public void Clear()
    {
        _model = null;
        Visible = false;
        HudStyle.Clear(_body);
    }

    public void Load(Model model)
    {
        _model = model;
        HudStyle.Clear(_body);
        Visible = true;

        // Header: accent stripe, icon, title and subtitle, close.
        var header = new PanelContainer();
        var headerBox = HudStyle.Box(HudStyle.CardHeaderBg, null, 0, 7, 12, 8);
        headerBox.CornerRadiusBottomLeft = 0;
        headerBox.CornerRadiusBottomRight = 0;
        headerBox.BorderColor = model.Accent;
        headerBox.BorderWidthTop = 3;
        header.AddThemeStyleboxOverride("panel", headerBox);
        var headerRow = new HBoxContainer();
        headerRow.AddThemeConstantOverride("separation", 8);
        if (model.TitleIcon is Icon titleIcon) headerRow.AddChild(new IconRect(titleIcon, 22, model.Accent));
        var titles = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        titles.AddThemeConstantOverride("separation", 0);
        titles.AddChild(HudStyle.Label(model.Title, 19));
        if (model.Subtitle != "") titles.AddChild(HudStyle.Label(model.Subtitle, 12, HudStyle.TextSoft));
        headerRow.AddChild(titles);
        var close = HudStyle.Button("×", () => CloseRequested?.Invoke(), ButtonKind.Quiet, 18, "Deselect (Esc)");
        close.SizeFlagsVertical = SizeFlags.ShrinkBegin;
        headerRow.AddChild(close);
        header.AddChild(headerRow);
        _body.AddChild(header);

        var content = new MarginContainer();
        content.AddThemeConstantOverride("margin_left", 12);
        content.AddThemeConstantOverride("margin_right", 12);
        content.AddThemeConstantOverride("margin_top", 8);
        content.AddThemeConstantOverride("margin_bottom", 10);
        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 5);
        content.AddChild(col);
        _body.AddChild(content);

        foreach (var row in model.Rows) col.AddChild(BuildRow(row));

        if (model.Actions.Count > 0)
        {
            col.AddChild(HudStyle.Spacer(0, 3));
            var grid = new GridContainer { Columns = 2 };
            grid.AddThemeConstantOverride("h_separation", 6);
            grid.AddThemeConstantOverride("v_separation", 6);
            var pendingNarrow = new List<Action>();
            void FlushNarrow()
            {
                foreach (var a in pendingNarrow) grid.AddChild(BuildButton(a));
                if (pendingNarrow.Count % 2 == 1) grid.AddChild(HudStyle.Spacer());
                pendingNarrow.Clear();
            }
            // Wide actions go first, each on its own line; the rest fill the two-column grid; grouped
            // actions follow in folding sections.
            foreach (var a in model.Actions.Where(a => a.Wide && a.Group is null))
            {
                var b = BuildButton(a);
                col.AddChild(b);
            }
            foreach (var a in model.Actions.Where(a => !a.Wide && a.Group is null)) pendingNarrow.Add(a);
            FlushNarrow();
            if (grid.GetChildCount() > 0) col.AddChild(grid);

            foreach (var group in model.Actions.Where(a => a.Group is not null).Select(a => a.Group!).Distinct())
            {
                var members = model.Actions.Where(a => a.Group == group).ToList();
                bool open = OpenGroups.Contains(group);
                int ready = members.Count(a => a.Enabled);
                var toggle = HudStyle.Button(open ? $"{group}: hide" : $"{group}: {ready} of {members.Count} possible - show", () =>
                {
                    if (!OpenGroups.Remove(group)) OpenGroups.Add(group);
                    if (_model is not null) Load(_model);
                }, ButtonKind.Quiet, 13);
                toggle.Alignment = HorizontalAlignment.Left;
                col.AddChild(toggle);
                if (!open) continue;
                var groupGrid = new GridContainer { Columns = 2 };
                groupGrid.AddThemeConstantOverride("h_separation", 6);
                groupGrid.AddThemeConstantOverride("v_separation", 6);
                foreach (var a in members) groupGrid.AddChild(BuildButton(a));
                if (members.Count % 2 == 1) groupGrid.AddChild(HudStyle.Spacer());
                col.AddChild(groupGrid);
            }
        }

        if (!string.IsNullOrEmpty(model.Hint))
        {
            col.AddChild(HudStyle.Spacer(0, 2));
            col.AddChild(HudStyle.Label(model.Hint!, 12, HudStyle.TextMuted, wrap: true));
        }
    }

    private static Control BuildRow(Row row)
    {
        if (row.Chips is not null)
        {
            var strip = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
            strip.AddThemeConstantOverride("separation", 12);
            foreach (var (chipIcon, text, tip) in row.Chips)
            {
                var chip = new HBoxContainer { TooltipText = tip, MouseFilter = MouseFilterEnum.Pass };
                chip.AddThemeConstantOverride("separation", 4);
                chip.AddChild(new IconRect(chipIcon, 15));
                chip.AddChild(HudStyle.Label(text, 13, HudStyle.TextSoft));
                strip.AddChild(chip);
            }
            return strip;
        }
        var line = new HBoxContainer { TooltipText = row.Tooltip, MouseFilter = row.Tooltip == "" ? MouseFilterEnum.Ignore : MouseFilterEnum.Pass };
        line.AddThemeConstantOverride("separation", 7);
        if (row.Icon is Icon icon) line.AddChild(new IconRect(icon, 16, row.IconTint));
        if (row.Progress is double progress)
        {
            var stack = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore };
            stack.AddThemeConstantOverride("separation", 2);
            var label = HudStyle.Label(row.Text, row.FontSize, row.Color, wrap: true);
            stack.AddChild(label);
            stack.AddChild(HudStyle.Bar(progress, row.BarColor ?? HudStyle.Gold, 5));
            line.AddChild(stack);
        }
        else
        {
            var label = HudStyle.Label(row.Text, row.FontSize, row.Color, wrap: true);
            label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            line.AddChild(label);
        }
        return line;
    }

    private static Button BuildButton(Action a)
    {
        string tooltip = a.Enabled ? a.Tooltip : (a.Reason == "" ? a.Tooltip : $"{a.Reason}\n\n{a.Tooltip}".Trim());
        var button = HudStyle.Button(a.Label, () => a.Commit?.Invoke(), a.Kind, 13, tooltip);
        button.Disabled = !a.Enabled;
        button.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        button.CustomMinimumSize = new Vector2(0, 30);
        button.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        // Disabled buttons swallow hover in Godot unless told otherwise; keep the tooltip reachable.
        button.MouseFilter = MouseFilterEnum.Stop;
        return button;
    }

    /// <summary>Headless: press the action with this exact label, the same Commit a click runs.</summary>
    public bool DebugPress(string label)
    {
        var action = _model?.Actions.Find(a => a.Label == label);
        if (action is null || !action.Enabled || action.Commit is null) return false;
        action.Commit();
        return true;
    }

    public string DumpUiDigest()
    {
        if (_model is null || !Visible) return "SelectionCard hidden";
        var lines = new List<string>
        {
            $"SelectionCard key={_model.Key} title=\"{_model.Title}\" subtitle=\"{_model.Subtitle}\"",
        };
        foreach (var r in _model.Rows)
        {
            string text = r.Chips is null ? r.Text : string.Join(" | ", r.Chips.Select(c => c.Text));
            lines.Add($"  row \"{text}\"{(r.Progress is double p ? $" progress={p:0.00}" : "")}");
        }
        foreach (var a in _model.Actions)
            lines.Add($"  action \"{a.Label}\" enabled={a.Enabled}{(a.Enabled ? "" : $" reason=\"{a.Reason}\"")}");
        if (_model.Hint is not null) lines.Add($"  hint \"{_model.Hint}\"");
        return string.Join('\n', lines);
    }
}
