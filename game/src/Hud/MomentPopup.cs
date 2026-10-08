using System;
using System.Collections.Generic;
using Godot;

namespace Hearthspan.Game.Hud;

/// <summary>
/// Plan v5 M3 (D10): big moments get a card of their own so they register (meeting a people, a
/// landmark rising, a ruin's secret, war and peace, a new era, a settlement growing into a village or
/// a town). One at a time, queued, dismissed with Enter; "Show me" flies the camera there.
/// </summary>
public partial class MomentPopup : Control, IUiDumpable
{
    public sealed class Moment
    {
        public string Kicker = "";
        public string Title = "";
        public string Body = "";
        public string? Effect;
        public Color Accent = HudStyle.Gold;
        public Icon Icon = Icon.Star;
        public int? FocusCol, FocusRow;
        public Audio.Sound Sound = Audio.Sound.Bell;
    }

    public Action<int, int>? FocusRequested;
    public Action? Closed;

    private readonly Queue<Moment> _queue = new();
    private Moment? _current;
    private PanelContainer _card = null!;
    private Label _kicker = null!, _title = null!, _body = null!, _effect = null!;
    private IconRect _icon = null!;
    private Button _continue = null!, _showMe = null!;
    private StyleBoxFlat _cardBox = null!;
    private readonly List<string> _shownTitles = new();

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        Visible = false;

        AddChild(new ColorRect { Color = new Color(0, 0, 0, 0.38f), MouseFilter = MouseFilterEnum.Stop, AnchorRight = 1, AnchorBottom = 1 });

        var center = new CenterContainer { AnchorRight = 1, AnchorBottom = 1, MouseFilter = MouseFilterEnum.Ignore };
        AddChild(center);
        _card = new PanelContainer { CustomMinimumSize = new Vector2(540, 0) };
        _cardBox = HudStyle.Box(HudStyle.CardBg with { A = 0.98f }, HudStyle.Line, 1, 9, 26, 20);
        _card.AddThemeStyleboxOverride("panel", _cardBox);
        center.AddChild(_card);

        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 8);
        _card.AddChild(col);
        _kicker = HudStyle.Kicker("");
        _kicker.HorizontalAlignment = HorizontalAlignment.Center;
        col.AddChild(_kicker);
        var titleRow = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        titleRow.AddThemeConstantOverride("separation", 10);
        _icon = new IconRect(Icon.Star, 30);
        titleRow.AddChild(_icon);
        _title = HudStyle.Label("", 26);
        titleRow.AddChild(_title);
        col.AddChild(titleRow);
        _body = HudStyle.Label("", 15, HudStyle.TextSoft, wrap: true);
        _body.HorizontalAlignment = HorizontalAlignment.Center;
        _body.CustomMinimumSize = new Vector2(488, 0);
        col.AddChild(_body);
        _effect = HudStyle.Label("", 14, HudStyle.Gold, wrap: true);
        _effect.HorizontalAlignment = HorizontalAlignment.Center;
        _effect.CustomMinimumSize = new Vector2(488, 0);
        col.AddChild(_effect);
        col.AddChild(HudStyle.Spacer(0, 4));

        var buttons = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        buttons.AddThemeConstantOverride("separation", 12);
        _showMe = HudStyle.Button("Show me", ShowMe, ButtonKind.Normal, 15);
        _showMe.CustomMinimumSize = new Vector2(130, 36);
        _continue = HudStyle.Button("Continue", Advance, ButtonKind.Primary, 15);
        _continue.CustomMinimumSize = new Vector2(150, 36);
        _continue.FocusMode = FocusModeEnum.All;
        buttons.AddChild(_showMe);
        buttons.AddChild(_continue);
        col.AddChild(buttons);
    }

    public bool IsShowing => Visible;
    public int Pending => _queue.Count + (_current is null ? 0 : 1);

    public void Enqueue(Moment moment)
    {
        _queue.Enqueue(moment);
        if (_current is null) ShowNext();
    }

    private void ShowNext()
    {
        if (_queue.Count == 0)
        {
            _current = null;
            Visible = false;
            Closed?.Invoke();
            return;
        }
        var m = _current = _queue.Dequeue();
        _shownTitles.Add(m.Title);
        _kicker.Text = m.Kicker.ToUpperInvariant();
        _kicker.AddThemeColorOverride("font_color", m.Accent.Darkened(0.1f));
        _title.Text = m.Title;
        _icon.Set(m.Icon, m.Accent);
        _body.Text = m.Body;
        _effect.Text = m.Effect ?? "";
        _effect.Visible = !string.IsNullOrEmpty(m.Effect);
        _showMe.Visible = m.FocusCol is not null;
        _cardBox.BorderColor = m.Accent;
        _cardBox.BorderWidthTop = 4;
        _continue.Text = _queue.Count > 0 ? $"Continue  ({_queue.Count} more)" : "Continue";
        Visible = true;
        _card.Scale = new Vector2(0.96f, 0.96f);
        _card.PivotOffset = _card.Size * 0.5f;
        _card.Modulate = new Color(1, 1, 1, 0);
        var tween = CreateTween().SetParallel();
        tween.TweenProperty(_card, "scale", Vector2.One, 0.18).SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
        tween.TweenProperty(_card, "modulate:a", 1f, 0.15);
        _continue.CallDeferred(Control.MethodName.GrabFocus);
        Audio.Sfx.Play(m.Sound);
    }

    public void Advance() => ShowNext();

    public void DismissAll()
    {
        _queue.Clear();
        ShowNext();
    }

    private void ShowMe()
    {
        if (_current?.FocusCol is int col && _current.FocusRow is int row) FocusRequested?.Invoke(col, row);
        ShowNext();
    }

    public string DumpUiDigest()
    {
        var current = _current is null ? "(none)" : $"\"{_current.Title}\" kicker=\"{_current.Kicker}\" body=\"{_current.Body}\" effect=\"{_current.Effect}\"";
        return $"MomentPopup visible={Visible} current={current} queued={_queue.Count} shownThisSession={_shownTitles.Count}";
    }
}
