using System;
using Godot;

namespace Hearthspan.Game.Hud;

/// <summary>
/// Plan v5 M3 (D10): the current goal, top-left under the bar — one line saying what to aim for
/// next and a short hint how. Click to fold it to the title alone. Replaces the objectives card
/// (four goals and a paragraph of flavour) with a compact chip that keeps the map clear.
/// </summary>
public partial class GoalChip : ClickPanel, IUiDumpable
{
    private Label _kicker = null!, _title = null!, _hint = null!;
    private bool _collapsed;
    private string _titleText = "", _hintText = "";

    public override void _Ready()
    {
        SetStyles(HudStyle.Box(HudStyle.CardBg with { A = 0.86f }, HudStyle.LineSoft, 1, 6, 10, 6),
                  HudStyle.Box(HudStyle.CardBg with { A = 0.95f }, HudStyle.Line, 1, 6, 10, 6));
        MouseDefaultCursorShape = CursorShape.Arrow;
        CustomMinimumSize = new Vector2(300, 0);
        var col = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        col.AddThemeConstantOverride("separation", 1);
        _kicker = HudStyle.Kicker("");
        _title = HudStyle.Label("", 14);
        _hint = HudStyle.Label("", 12, HudStyle.TextSoft, wrap: true);
        _hint.CustomMinimumSize = new Vector2(360, 0);
        col.AddChild(_kicker);
        col.AddChild(_title);
        col.AddChild(_hint);
        AddChild(col);
        Clicked = () => { _collapsed = !_collapsed; _hint.Visible = !_collapsed && _hintText != ""; };
        TooltipText = "Your current goal. Click to fold or unfold the hint.";
    }

    public void Load(string kicker, string title, string hint)
    {
        _titleText = title;
        _hintText = hint;
        _kicker.Text = kicker.ToUpperInvariant();
        _title.Text = title;
        _hint.Text = hint;
        _hint.Visible = !_collapsed && hint != "";
    }

    public string DumpUiDigest() => $"GoalChip kicker=\"{_kicker.Text}\" title=\"{_titleText}\" hint=\"{_hintText}\" collapsed={_collapsed}";
}
