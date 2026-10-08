using Godot;

namespace Hearthspan.Game;

/// <summary>
/// Stage 6 task 3: the victory/defeat screen, fed by <see cref="Hearthspan.Sim.World.WonderCompletedByCivId"/>
/// (Stage 5's Wonder victory — see sim/Hearthspan.Sim/Victory/WonderVictory.cs's own doc comment for
/// why the Great Lighthouse is the only real ending wired up here: <c>VictoryType.Dominion</c> exists
/// as an enum value only, with no predicate anywhere in this codebase, and building one is explicitly
/// out of scope for this task) and by <see cref="GameSession.DetermineEndgameState"/>'s own simplest
/// checkable defeat condition (the player's civ owns zero settlements — see that method's doc comment
/// for why).
///
/// <para>Presents, never halts — per docs/content-design.md §4: "this doc doesn't need to invent a
/// 'stop the game' mechanism, matching how <c>LegacyVictory</c> already works today (<c>Achieved</c>
/// computed, nothing halts on it, by design)." A "Continue Playing" button dismisses the panel; the
/// live <c>World</c> underneath keeps advancing exactly as it does behind SaveLoadPanel/TechTreeView —
/// this is a read-only presentation over <see cref="GameSession.RefreshAll"/>'s already-computed state,
/// the same "the view layer only ever reads a fresh projection" discipline every other panel in this
/// file follows.</para>
///
/// <para>Built from real Control nodes (Panel/Label/Button), the same convention
/// <see cref="SaveLoadPanel"/> already uses for its own small, bounded set of controls — this is
/// nowhere near rule 5's "thousands of Control nodes" concern, so it does not need a custom
/// <c>_Draw()</c>.</para>
/// </summary>
public partial class EndgameScreen : Control, IUiDumpable
{
    /// <summary>The four states <see cref="GameSession.DetermineEndgameState"/> can compute. Ordering
    /// intentionally does not encode priority here — that decision lives entirely in GameSession, since
    /// this class only ever renders whatever state it is handed.</summary>
    public enum State
    {
        /// <summary>No screen — the task's third required state ("still playing").</summary>
        None,

        /// <summary>The player's own civ completed the Great Lighthouse — <c>World.WonderCompletedByCivId</c>
        /// equals the player civ's id. The task's "you won" state.</summary>
        PlayerVictory,

        /// <summary><c>World.WonderCompletedByCivId</c> is set to a civ other than the player's own —
        /// the task's "someone else won" state. Not framed as the player's personal defeat (they may
        /// still hold every settlement they ever had); it is the wonder race concluding without them.</summary>
        RivalVictory,

        /// <summary>The player's civ owns zero settlements — this task's chosen defeat condition (see
        /// <see cref="GameSession.DetermineEndgameState"/> for why this one, specifically).</summary>
        PlayerDefeat,
    }

    private const float PanelW = 620f;

    private static readonly Color VictoryColor = new(0.93f, 0.75f, 0.36f, 1f); // HudStyle.Gold
    private static readonly Color DefeatColor = new(0.90f, 0.40f, 0.34f, 1f);

    private PanelContainer _card = null!;
    private StyleBoxFlat _cardBox = null!;
    private Label _kicker = null!;
    private Label _header = null!;
    private Label _body = null!;
    private VBoxContainer _summary = null!;
    private Label _legacyLabel = null!;
    private Button _continueButton = null!;
    private Button _menuButton = null!;

    /// <summary>GameSession wires this: "Main menu" leaves the game (through the pause menu's own
    /// quit path, so an unsaved game is still asked about).</summary>
    public System.Action? MenuRequested;

    /// <summary>The state last actually presented, distinct from a dismissed-but-still-current state —
    /// used only to decide whether a newly computed state should re-open a panel the player closed.</summary>
    private State _lastState = State.None;
    private bool _dismissedForCurrentState;

    public State CurrentState { get; private set; } = State.None;

    public override void _Ready()
    {
        Visible = false;
        MouseFilter = MouseFilterEnum.Stop;
        BuildUi();
    }

    /// <summary>Plan v5: a centred card in the HUD's style — kicker, a large title, what happened, a
    /// short account of the player's game, their Legacy, and two ways on. Full-rect scrim behind it
    /// so the map and HUD cannot be clicked while it is up.</summary>
    private void BuildUi()
    {
        var scrim = new ColorRect { Color = new Color(0, 0, 0, 0.62f), MouseFilter = MouseFilterEnum.Stop };
        scrim.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(scrim);

        var center = new CenterContainer { MouseFilter = MouseFilterEnum.Ignore };
        center.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(center);
        _card = new PanelContainer { CustomMinimumSize = new Vector2(PanelW, 0) };
        _cardBox = Hud.HudStyle.Box(Hud.HudStyle.CardBg with { A = 0.98f }, Hud.HudStyle.Line, 1, 10, 34, 26);
        _cardBox.BorderWidthTop = 4;
        _card.AddThemeStyleboxOverride("panel", _cardBox);
        center.AddChild(_card);

        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 10);
        _card.AddChild(col);
        _kicker = Hud.HudStyle.Kicker("");
        _kicker.HorizontalAlignment = HorizontalAlignment.Center;
        col.AddChild(_kicker);
        _header = Hud.HudStyle.Label("", 44);
        _header.HorizontalAlignment = HorizontalAlignment.Center;
        col.AddChild(_header);
        _body = Hud.HudStyle.Label("", 16, Hud.HudStyle.TextSoft, wrap: true);
        _body.HorizontalAlignment = HorizontalAlignment.Center;
        _body.CustomMinimumSize = new Vector2(PanelW - 68, 0);
        col.AddChild(_body);
        _summary = new VBoxContainer();
        _summary.AddThemeConstantOverride("separation", 3);
        col.AddChild(_summary);
        _legacyLabel = Hud.HudStyle.Label("", 17, Hud.HudStyle.Gold);
        _legacyLabel.HorizontalAlignment = HorizontalAlignment.Center;
        col.AddChild(_legacyLabel);
        col.AddChild(Hud.HudStyle.Spacer(0, 4));

        var buttons = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        buttons.AddThemeConstantOverride("separation", 14);
        _menuButton = Hud.HudStyle.Button("Main menu", () => MenuRequested?.Invoke(), Hud.ButtonKind.Normal, 15);
        _menuButton.CustomMinimumSize = new Vector2(160, 38);
        _continueButton = Hud.HudStyle.Button("Continue playing", () => { Visible = false; _dismissedForCurrentState = true; }, Hud.ButtonKind.Primary, 15);
        _continueButton.CustomMinimumSize = new Vector2(200, 38);
        _continueButton.FocusMode = FocusModeEnum.All;
        buttons.AddChild(_menuButton);
        buttons.AddChild(_continueButton);
        col.AddChild(buttons);
    }

    /// <summary>Called from <see cref="GameSession.RefreshAll"/> with the freshly (re-)computed
    /// endgame state on every refresh — a pure function of world state computed entirely by the
    /// caller, never derived in here. Re-opens the panel only when the state actually changes from
    /// what was last presented, so a player who clicks Continue isn't yanked back to this screen on
    /// every subsequent turn merely because the same already-concluded state is still true.</summary>
    public void Present(State state, long legacyStanding, string? winnerName, System.Collections.Generic.IReadOnlyList<string>? summary = null)
    {
        CurrentState = state;

        if (state == State.None)
        {
            Visible = false;
            _lastState = State.None;
            _dismissedForCurrentState = false;
            return;
        }

        bool isNewState = state != _lastState;
        _lastState = state;
        if (isNewState) _dismissedForCurrentState = false;

        Color accent;
        switch (state)
        {
            case State.PlayerVictory:
                accent = VictoryColor;
                _kicker.Text = "THE AGE IS YOURS";
                _header.Text = "Victory";
                _body.Text = "Your people raised the Great Lighthouse, the wonder that closes this age. No rival will race you to it now.";
                break;
            case State.RivalVictory:
                accent = DefeatColor;
                _kicker.Text = "THE WONDER RACE IS OVER";
                _header.Text = "Game over";
                _body.Text = winnerName is not null
                    ? $"{winnerName} completed the Great Lighthouse first. The age is theirs."
                    : "Another people completed the Great Lighthouse first. The age is theirs.";
                break;
            default:
                accent = DefeatColor;
                _kicker.Text = "YOUR PEOPLE ARE SCATTERED";
                _header.Text = "Defeated";
                _body.Text = "Your people hold no settlements. There is no way back from here.";
                break;
        }
        _cardBox.BorderColor = accent;
        _header.AddThemeColorOverride("font_color", accent);
        _kicker.AddThemeColorOverride("font_color", accent.Darkened(0.15f));

        foreach (var child in _summary.GetChildren()) { _summary.RemoveChild(child); child.QueueFree(); }
        if (summary is not null)
        {
            foreach (var line in summary)
            {
                var label = Hud.HudStyle.Label(line, 14, Hud.HudStyle.Text);
                label.HorizontalAlignment = HorizontalAlignment.Center;
                _summary.AddChild(label);
            }
        }
        _legacyLabel.Text = $"Legacy {legacyStanding}";

        if (!_dismissedForCurrentState)
        {
            bool wasVisible = Visible;
            Visible = true;
            if (!wasVisible && isNewState)
            {
                Audio.Sfx.Play(state == State.PlayerVictory ? Audio.Sound.Fanfare : Audio.Sound.War);
                _continueButton.CallDeferred(Control.MethodName.GrabFocus);
            }
        }
    }

    public string DumpUiDigest() =>
        $"EndgameScreen visible={Visible} state={CurrentState} header=\"{_header?.Text}\" " +
        $"legacyLine=\"{_legacyLabel?.Text}\" summaryLines={_summary?.GetChildCount() ?? 0} dismissed={_dismissedForCurrentState}";
}
