using System;
using Godot;

namespace Hearthspan.Game.Hud;

/// <summary>
/// Plan v5 M3 (D10): the one bar across the top. Left: menu, then the civilization's totals as icon
/// chips (people, production, treasury with its income, research with the current technology's
/// progress). Right: turn, year and era, then the three views. Replaces the old button row, status
/// line and resource strip, which together took three rows.
/// </summary>
public partial class TopBar : PanelContainer, IUiDumpable
{
    public const float Height = 40f;

    public sealed class Model
    {
        public long Population;
        public double Production;
        public double Gold;
        public double GoldPerTurn;
        public double ResearchRate;
        public string? ResearchName;
        public double ResearchProgress;
        public int ResearchTurns = -1;
        public int QueuedAfter;
        public string TurnText = "";
        public bool OverlayOn;
        public string PopulationTooltip = "";
        public string ProductionTooltip = "";
        public string GoldTooltip = "";
        public string ResearchTooltip = "";
    }

    public Action? MenuPressed;
    public Action? TechPressed;
    public Action? BuildPressed;
    public Action? OverlayPressed;

    private Label _pop = null!, _prod = null!, _gold = null!, _goldDelta = null!, _research = null!, _turn = null!;
    private ClickPanel _popChip = null!, _prodChip = null!, _goldChip = null!, _researchChip = null!;
    private ProgressBar _researchBar = null!;
    private Button _overlayButton = null!;
    private Button _techButton = null!;
    private bool _researchMissing;
    private Model _last = new();

    public override void _Ready()
    {
        AnchorLeft = 0; AnchorRight = 1; AnchorTop = 0; AnchorBottom = 0;
        OffsetLeft = 0; OffsetRight = 0; OffsetTop = 0; OffsetBottom = Height;
        MouseFilter = MouseFilterEnum.Stop;
        var bg = HudStyle.Box(HudStyle.BarBg, null, 0, 0, 10, 4);
        bg.BorderColor = HudStyle.Line;
        bg.BorderWidthBottom = 1;
        AddThemeStyleboxOverride("panel", bg);

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        AddChild(row);

        row.AddChild(HudStyle.Button("Menu", () => MenuPressed?.Invoke(), ButtonKind.Quiet, 14, "Save, load, settings, quit (Esc)"));
        row.AddChild(Divider());

        (_popChip, _pop) = Chip(Icon.Population, row);
        (_prodChip, _prod) = Chip(Icon.Production, row);
        (_goldChip, _gold) = Chip(Icon.Gold, row);
        _goldDelta = HudStyle.Label("", 13, HudStyle.TextSoft);
        ((HBoxContainer)_gold.GetParent()).AddChild(_goldDelta);

        _researchChip = new ClickPanel { CustomMinimumSize = new Vector2(250, 0) };
        _researchChip.Clicked = () => TechPressed?.Invoke();
        var researchRow = new HBoxContainer();
        researchRow.AddThemeConstantOverride("separation", 7);
        researchRow.MouseFilter = MouseFilterEnum.Ignore;
        researchRow.AddChild(new IconRect(Icon.Research, 18));
        var researchCol = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore };
        researchCol.AddThemeConstantOverride("separation", 1);
        _research = HudStyle.Label("", 13);
        _research.ClipText = true;
        _researchBar = HudStyle.Bar(0, HudStyle.Info, 4);
        researchCol.AddChild(_research);
        researchCol.AddChild(_researchBar);
        researchRow.AddChild(researchCol);
        _researchChip.AddChild(researchRow);
        row.AddChild(_researchChip);

        row.AddChild(HudStyle.Spacer(expand: true));
        _turn = HudStyle.Label("", 14, HudStyle.TextSoft);
        row.AddChild(_turn);
        row.AddChild(Divider());
        _techButton = HudStyle.Button("Tech  T", () => TechPressed?.Invoke(), ButtonKind.Normal, 13, "The tech tree: choose and queue research (T)");
        row.AddChild(_techButton);
        row.AddChild(HudStyle.Button("Build  B", () => BuildPressed?.Invoke(), ButtonKind.Normal, 13, "Queue buildings in your settlements (B)"));
        _overlayButton = HudStyle.Button("Growth  V", () => OverlayPressed?.Invoke(), ButtonKind.Normal, 13,
            "Show where new hamlets are likely to appear: green now, yellow with help, blue unlikely (V)");
        row.AddChild(_overlayButton);
    }

    private static Control Divider() => new ColorRect
    {
        Color = HudStyle.LineSoft,
        CustomMinimumSize = new Vector2(1, 22),
        SizeFlagsVertical = SizeFlags.ShrinkCenter,
        MouseFilter = MouseFilterEnum.Ignore,
    };

    private static (ClickPanel, Label) Chip(Icon icon, Container parent)
    {
        var chip = new ClickPanel();
        chip.SetStyles(HudStyle.Box(new Color(0, 0, 0, 0), null, 0, 5, 6, 4), HudStyle.Box(HudStyle.ChipBg, HudStyle.LineSoft, 1, 5, 6, 4));
        chip.MouseDefaultCursorShape = CursorShape.Arrow;
        var row = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", 5);
        row.AddChild(new IconRect(icon, 18));
        var label = HudStyle.Label("", 15);
        row.AddChild(label);
        chip.AddChild(row);
        parent.AddChild(chip);
        return (chip, label);
    }

    public void Load(Model m)
    {
        _last = m;
        _pop.Text = m.Population.ToString("N0");
        _popChip.TooltipText = m.PopulationTooltip;
        _prod.Text = HudStyle.Signed(m.Production);
        _prodChip.TooltipText = m.ProductionTooltip;
        _gold.Text = m.Gold.ToString("0");
        _goldDelta.Text = $"({HudStyle.Signed(m.GoldPerTurn)})";
        _goldDelta.AddThemeColorOverride("font_color", m.GoldPerTurn < 0 ? HudStyle.Bad : HudStyle.TextSoft);
        _goldChip.TooltipText = m.GoldTooltip;
        _researchChip.TooltipText = m.ResearchTooltip;

        _researchMissing = m.ResearchName is null;
        if (m.ResearchName is null)
        {
            _research.Text = "Choose research";
            _research.AddThemeColorOverride("font_color", HudStyle.Gold);
            _researchBar.Value = 0;
        }
        else
        {
            string turns = m.ResearchTurns >= 0 ? $" · {HudStyle.Turns(m.ResearchTurns)}" : "";
            string queued = m.QueuedAfter > 0 ? $"  +{m.QueuedAfter}" : "";
            _research.Text = $"{m.ResearchName}{turns}{queued}";
            _research.AddThemeColorOverride("font_color", HudStyle.Text);
            _researchBar.Value = Math.Clamp(m.ResearchProgress, 0, 1);
        }
        _turn.Text = m.TurnText;
        HudStyle.Style(_overlayButton, m.OverlayOn ? ButtonKind.Primary : ButtonKind.Normal);
        HudStyle.Style(_techButton, m.ResearchName is null ? ButtonKind.Primary : ButtonKind.Normal);
    }

    public override void _Process(double delta)
    {
        if (!_researchMissing) { _researchChip.Modulate = Colors.White; return; }
        float pulse = 0.5f + 0.5f * MathF.Sin(Time.GetTicksMsec() / 1000f * 3.2f);
        _researchChip.Modulate = new Color(1f, 1f, 1f, 0.72f + 0.28f * pulse);
    }

    public string DumpUiDigest() =>
        $"TopBar pop={_last.Population} production={_last.Production:0.0} gold={_last.Gold:0} goldPerTurn={_last.GoldPerTurn:0.0} " +
        $"research=\"{_last.ResearchName ?? "(none)"}\" researchTurns={_last.ResearchTurns} queuedAfter={_last.QueuedAfter} " +
        $"turn=\"{_last.TurnText}\" overlay={_last.OverlayOn}";
}
