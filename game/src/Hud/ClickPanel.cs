using System;
using Godot;

namespace Hearthspan.Game.Hud;

/// <summary>A panel that behaves like a button — hover highlight, click callback — but lays out
/// arbitrary children (icon + text + bar), which a <see cref="Button"/> cannot.</summary>
public partial class ClickPanel : PanelContainer
{
    private StyleBoxFlat _normal = HudStyle.Box(HudStyle.ChipBg, null, 0, 5, 8, 4);
    private StyleBoxFlat _hover = HudStyle.Box(HudStyle.ChipBg.Lightened(0.1f), HudStyle.Line, 1, 5, 8, 4);
    private bool _hovering;

    public Action? Clicked;

    public ClickPanel()
    {
        MouseFilter = MouseFilterEnum.Stop;
        MouseDefaultCursorShape = CursorShape.PointingHand;
        AddThemeStyleboxOverride("panel", _normal);
        MouseEntered += () => { _hovering = true; AddThemeStyleboxOverride("panel", _hover); };
        MouseExited += () => { _hovering = false; AddThemeStyleboxOverride("panel", _normal); };
    }

    public void SetStyles(StyleBoxFlat normal, StyleBoxFlat hover)
    {
        _normal = normal;
        _hover = hover;
        AddThemeStyleboxOverride("panel", _hovering ? _hover : _normal);
    }

    public override void _GuiInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left })
        {
            if (Clicked is not null) Audio.Sfx.Play(Audio.Sound.Click);
            Clicked?.Invoke();
            AcceptEvent();
        }
    }
}
