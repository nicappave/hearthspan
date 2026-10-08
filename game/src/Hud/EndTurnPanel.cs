using System;
using Godot;

namespace Hearthspan.Game.Hud;

/// <summary>
/// Plan v5 M3 (D10): the smart End Turn button, bottom-right. It always says the one thing to do
/// next — "Choose research", "A unit needs orders", "Choose production" — and pressing it (or Enter)
/// takes you straight there; only when nothing is waiting does it read "Next turn". A small link
/// under it (or Shift+Enter) ends the turn regardless. Every press of Enter therefore does something
/// you can see.
/// </summary>
public partial class EndTurnPanel : VBoxContainer, IUiDumpable
{
    public enum State { NextTurn, Research, UnitOrders, Production }

    public Action? Pressed;
    public Action? ForcePressed;

    private ClickPanel _main = null!;
    private Label _title = null!, _sub = null!;
    private Button _force = null!;
    private State _state = State.NextTurn;
    private string _titleText = "", _subText = "";

    public override void _Ready()
    {
        AnchorLeft = 1; AnchorRight = 1; AnchorTop = 1; AnchorBottom = 1;
        OffsetLeft = -272; OffsetRight = -14; OffsetTop = -14; OffsetBottom = -14;
        GrowVertical = GrowDirection.Begin;
        GrowHorizontal = GrowDirection.Begin;
        MouseFilter = MouseFilterEnum.Ignore;
        AddThemeConstantOverride("separation", 4);

        _force = HudStyle.Button("End turn anyway   Shift+Enter", () => ForcePressed?.Invoke(), ButtonKind.Quiet, 12,
            "End the turn without dealing with what is waiting");
        _force.SizeFlagsHorizontal = SizeFlags.ShrinkEnd;
        AddChild(_force);

        _main = new ClickPanel { CustomMinimumSize = new Vector2(258, 66) };
        _main.Clicked = () => Pressed?.Invoke();
        var stack = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore, Alignment = AlignmentMode.Center };
        stack.AddThemeConstantOverride("separation", 0);
        _title = HudStyle.Label("", 20);
        _title.HorizontalAlignment = HorizontalAlignment.Center;
        _sub = HudStyle.Label("", 12, HudStyle.TextSoft);
        _sub.HorizontalAlignment = HorizontalAlignment.Center;
        stack.AddChild(_title);
        stack.AddChild(_sub);
        _main.AddChild(stack);
        AddChild(_main);
        SetState(State.NextTurn, "Next turn", "Enter");
    }

    public State CurrentState => _state;

    public void SetState(State state, string title, string sub)
    {
        _state = state;
        _titleText = title;
        _subText = sub;
        _title.Text = title;
        _sub.Text = sub;
        bool next = state == State.NextTurn;
        var fill = next ? new Color(0.38f, 0.27f, 0.10f, 0.98f) : new Color(0.13f, 0.18f, 0.24f, 0.97f);
        var border = next ? HudStyle.Gold : HudStyle.Info;
        var normal = HudStyle.Box(fill, border, 2, 8, 12, 6);
        var hover = HudStyle.Box(fill.Lightened(0.12f), border.Lightened(0.2f), 2, 8, 12, 6);
        _main.SetStyles(normal, hover);
        _title.AddThemeColorOverride("font_color", next ? new Color(1f, 0.93f, 0.72f) : new Color(0.86f, 0.93f, 1f));
        _force.Visible = !next;
        _main.TooltipText = next
            ? "End the turn: every settlement produces and grows, units follow their orders, rivals move (Enter)"
            : "Take me there (Enter). Shift+Enter ends the turn anyway.";
    }

    public override void _Process(double delta)
    {
        if (_state == State.NextTurn) { _main.Modulate = Colors.White; return; }
        float pulse = 0.5f + 0.5f * MathF.Sin(Time.GetTicksMsec() / 1000f * 2.6f);
        _main.Modulate = new Color(1f, 1f, 1f, 0.86f + 0.14f * pulse);
    }

    public string DumpUiDigest() => $"EndTurnPanel state={_state} title=\"{_titleText}\" sub=\"{_subText}\" visible={Visible}";
}
