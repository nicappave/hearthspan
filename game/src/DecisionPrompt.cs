using System.Collections.Generic;
using Godot;

namespace Hearthspan.Game;

/// <summary>
/// Stage 7 task C1 (docs/stage-7-plan.md): the one presenter every decision kind (research, build,
/// more later) shares, added mid-plan so that choices arrive as popups. Shows the front of a freshly
/// rebuilt <see cref="PendingDecision"/> queue GameSession hands it every RefreshAll; GameSession owns
/// all the sim-reading/formatting, this class owns only "how a decision looks on screen."
///
/// <para>Built the same way PauseMenu/SaveLoadPanel already are: a full-rect input-blocking scrim, real
/// Button children for the options, added to Main.tscn's true-modal band (after TechTree/BuildView,
/// alongside SaveLoad/PauseMenu) so it draws over even a full-screen panel — a pending decision should
/// interrupt whatever the player is looking at, not wait for them to close it first.</para>
///
/// <para>Three ways out of a decision, exactly as C1 specifies: choosing an option runs its
/// <see cref="DecisionOption.Commit"/> and asks GameSession to rebuild (a fresh RefreshAll naturally
/// advances to whatever's next); "Not now" skips just this one decision for the rest of this turn;
/// "Stop asking this turn" drains the whole queue for the rest of this turn. Both dismiss buttons are
/// hidden when the front decision says <see cref="PendingDecision.AllowDismiss"/> is false (task C2:
/// turn 0's research prompt cannot be dismissed into a permanently unblockable game).</para>
/// </summary>
public partial class DecisionPrompt : Control, IUiDumpable
{
    private const float PanelW = 680f;
    private const float OptionHeight = 34f;
    private const float OptionGap = 6f;

    private ColorRect _scrim = null!;
    private Panel _bg = null!;
    private Label _titleLabel = null!;
    private Label _bodyLabel = null!;
    private readonly List<Button> _optionButtons = new();
    private Button _notNowButton = null!;
    private Button _stopAskingButton = null!;

    private IReadOnlyList<PendingDecision> _queue = System.Array.Empty<PendingDecision>();

    /// <summary>GameSession wires this in _Ready() — called whenever the player picks an option, "Not
    /// now"s, or "Stop asking"s, so GameSession can rebuild the queue against the new (dismiss-set or
    /// world-state) reality rather than this class guessing what comes next.</summary>
    public System.Action? RequestRebuild;

    /// <summary>Keyed the same way GameSession's dismiss-tracking is ("research", "build:{id}") — set
    /// when "Not now" is pressed on the current front decision.</summary>
    public System.Action<string>? DismissRequested;

    public System.Action? StopAskingRequested;

    public override void _Ready()
    {
        Visible = false;
        MouseFilter = MouseFilterEnum.Stop;

        _scrim = new ColorRect { Color = new Color(0, 0, 0, 0.55f), MouseFilter = MouseFilterEnum.Stop };
        _scrim.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(_scrim);

        _bg = new Panel { Position = new Vector2(460, 150), Size = new Vector2(PanelW, 120) };
        // Plan v5 M3: the HUD's card look, a gold edge on top.
        var card = Hud.HudStyle.Box(Hud.HudStyle.CardBg with { A = 0.98f }, Hud.HudStyle.Line, 1, 9, 0, 0);
        card.BorderColor = Hud.HudStyle.Gold;
        card.BorderWidthTop = 3;
        _bg.AddThemeStyleboxOverride("panel", card);
        AddChild(_bg);

        _titleLabel = new Label { Position = new Vector2(20, 14), Size = new Vector2(PanelW - 40, 26) };
        _titleLabel.AddThemeFontSizeOverride("font_size", 18);
        _titleLabel.AddThemeColorOverride("font_color", MapPalette.UiTextPrimary);
        _bg.AddChild(_titleLabel);

        _bodyLabel = new Label
        {
            Position = new Vector2(20, 44),
            Size = new Vector2(PanelW - 40, 40),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        _bodyLabel.AddThemeFontSizeOverride("font_size", 13);
        _bodyLabel.AddThemeColorOverride("font_color", MapPalette.UiTextSecondary);
        _bg.AddChild(_bodyLabel);
    }

    /// <summary>Rebuilds the presenter against a freshly computed queue. Always shows the FRONT
    /// decision — never an index into a queue that might already be stale by the next call, since
    /// GameSession never caches this list either (task C1's own acceptance criterion).</summary>
    public void Present(IReadOnlyList<PendingDecision> queue)
    {
        _queue = queue;
        if (_queue.Count == 0)
        {
            Visible = false;
            return;
        }

        Visible = true;
        var decision = _queue[0];

        _titleLabel.Text = decision.Title;
        _bodyLabel.Text = decision.Body;
        // The body is as tall as its text needs (it used to be a fixed 40px and clipped).
        float bodyHeight = MapPalette.UiFont.GetMultilineStringSize(decision.Body, HorizontalAlignment.Left, PanelW - 40, 13).Y;
        _bodyLabel.Size = new Vector2(PanelW - 40, bodyHeight + 4f);
        float viewportWidth = GetViewportRect().Size.X > 1 ? GetViewportRect().Size.X : 1600f;
        _bg.Position = new Vector2(Mathf.Round((viewportWidth - PanelW) * 0.5f), 150);

        foreach (var b in _optionButtons) { _bg.RemoveChild(b); b.QueueFree(); }
        _optionButtons.Clear();
        if (_notNowButton is not null) { _bg.RemoveChild(_notNowButton); _notNowButton.QueueFree(); }
        if (_stopAskingButton is not null) { _bg.RemoveChild(_stopAskingButton); _stopAskingButton.QueueFree(); }

        float y = 44f + _bodyLabel.Size.Y + 10f;
        foreach (var option in decision.Options)
        {
            var button = new Button
            {
                Text = option.Enabled ? option.Label : $"{option.Label} — {option.DisabledReason}",
                Disabled = !option.Enabled,
                Position = new Vector2(20, y),
                Size = new Vector2(PanelW - 40, OptionHeight),
                TooltipText = option.Tooltip,
                ClipText = true,
                Alignment = HorizontalAlignment.Left,
            };
            Hud.HudStyle.Style(button, Hud.ButtonKind.Normal);
            var commit = option.Commit;
            button.Pressed += () => { commit(); RequestRebuild?.Invoke(); };
            _bg.AddChild(button);
            _optionButtons.Add(button);
            y += OptionHeight + OptionGap;
        }

        if (decision.AllowDismiss)
        {
            _notNowButton = new Button { Text = "Not now", Position = new Vector2(20, y), Size = new Vector2((PanelW - 52) * 0.5f, 30) };
            _notNowButton.TooltipText = "Skip this one decision for this turn only — it'll come back next turn.";
            string key = decision.Key;
            _notNowButton.Pressed += () => { DismissRequested?.Invoke(key); RequestRebuild?.Invoke(); };
            _bg.AddChild(_notNowButton);

            _stopAskingButton = new Button { Text = "Stop asking this turn", Position = new Vector2(20 + (PanelW - 52) * 0.5f + 12, y), Size = new Vector2((PanelW - 52) * 0.5f, 30) };
            _stopAskingButton.TooltipText = "Don't ask again this turn. The End Turn button still shows what is waiting.";
            _stopAskingButton.Pressed += () => { StopAskingRequested?.Invoke(); RequestRebuild?.Invoke(); };
            _bg.AddChild(_stopAskingButton);
            y += 36f;
        }

        _bg.Size = new Vector2(PanelW, y + 16f);
    }

    public string DumpUiDigest()
    {
        if (_queue.Count == 0) return "DecisionPrompt pending=0";
        var d = _queue[0];
        var lines = new List<string>
        {
            $"DecisionPrompt pending={_queue.Count} kind={d.Kind} title=\"{d.Title}\" allowDismiss={d.AllowDismiss} settlementId={d.SettlementId}",
            $"options=[{string.Join(", ", System.Linq.Enumerable.Select(d.Options, o => $"{o.Label}(enabled={o.Enabled})"))}]",
        };
        return string.Join('\n', lines);
    }
}
