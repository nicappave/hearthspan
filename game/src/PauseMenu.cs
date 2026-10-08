using System;
using Godot;

namespace Hearthspan.Game;

/// <summary>
/// Stage 6 task 6 (the menu and settings work): the in-session pause menu — Resume, Save, Load, Settings, Quit to
/// Menu, Quit to Desktop — reached by Esc or the topbar's "Menu" button
/// (<see cref="GameSession.OpenPauseMenu"/>/<see cref="GameSession._UnhandledInput"/>).
///
/// <para><b>Reuse, not rebuild</b> (per this task's own instruction): Save/Load do not duplicate task
/// 6.2's dialog — this panel just closes itself and asks <see cref="GameSession"/> to open the exact same
/// <see cref="SaveLoadPanel"/> the topbar's own Save/Load buttons already open (<see cref="SaveClicked"/>/
/// <see cref="LoadClicked"/>), including its own confirm-overwrite dialog, second-channel save
/// verification, etc. — nothing about that path changes here. Settings reuses
/// <see cref="SettingsFields.Build"/>, the widget set extracted out of task 6.1's MainMenu settings panel
/// specifically so this overlay would not need a second hand-copy.</para>
///
/// <para><b>The one genuinely new interaction here:</b> quitting (to menu or to desktop) is gated on
/// <see cref="IsDirtyCheck"/> — a delegate GameSession supplies that reads its own "dirty since last
/// save" flag. Built the same way <see cref="SaveLoadPanel"/>'s own confirm-overwrite dialog is: this
/// panel owns the confirmation decision (show a <see cref="ConfirmationDialog"/>, wait for Yes/No), and
/// only invokes <see cref="QuitToMenuRequested"/>/<see cref="QuitToDesktopRequested"/> — which
/// GameSession wires to the actual `ChangeSceneToFile`/`GetTree().Quit()` calls — once that decision is
/// final. A quit with nothing unsaved proceeds immediately, exactly as a real player would expect; a
/// quit with unsaved changes always asks first.</para>
///
/// <para>Built from real nodes (Panel/Label/Button/ConfirmationDialog), the same convention
/// <see cref="MainMenu"/>/<see cref="SaveLoadPanel"/> already use for their own small, bounded button
/// lists — nowhere near rule 5's "thousands of Control nodes" concern.</para>
/// </summary>
public partial class PauseMenu : Control, IUiDumpable
{
    private const float PanelW = 360f;
    private const float PanelH = 372f;
    private const float SettingsPanelW = 460f;
    private const float SettingsPanelH = 300f;

    private Panel _bg = null!;
    private Panel _settingsBg = null!;
    private ConfirmationDialog _quitConfirm = null!;

    private string? _pendingQuitTarget; // "menu" | "desktop"

    /// <summary>GameSession wires all of these in <c>_Ready()</c> — see that class for what each one
    /// actually does. None of them are invoked until this panel has already decided the action is safe
    /// (Save/Load/Settings need no gate at all; Quit needs <see cref="IsDirtyCheck"/> to say "false", or
    /// the confirm dialog's Yes to be clicked).</summary>
    public Func<bool>? IsDirtyCheck;
    public Action? ResumeRequested;
    public Action? SaveClicked;
    public Action? LoadClicked;
    public Action? QuitToMenuRequested;
    public Action? QuitToDesktopRequested;

    public override void _Ready()
    {
        Visible = false;
        MouseFilter = MouseFilterEnum.Stop;
        BuildUi();
    }

    private void BuildUi()
    {
        // Full-rect input-blocking scrim so the map/topbar behind the dialog can't be clicked while
        // it's open — same reasoning SaveLoadPanel/TechTree/BuildView already apply.
        var scrim = new ColorRect { Color = new Color(0, 0, 0, 0.55f), MouseFilter = MouseFilterEnum.Stop };
        scrim.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(scrim);

        BuildMainPanel();
        BuildSettingsPanel();
        BuildQuitConfirm();
    }

    private void BuildMainPanel()
    {
        _bg = new Panel { Position = new Vector2(396, 138), Size = new Vector2(PanelW, PanelH) };
        AddChild(_bg);

        var header = new Label { Text = "Paused", Position = new Vector2(20, 14), Size = new Vector2(300, 28) };
        header.AddThemeFontSizeOverride("font_size", 22);
        header.AddThemeColorOverride("font_color", MapPalette.UiTextPrimary);
        _bg.AddChild(header);

        float y = 60f;
        MakeButton(_bg, "Resume", y, () => { Close(); ResumeRequested?.Invoke(); }); y += 50f;
        MakeButton(_bg, "Save", y, () => { Close(); SaveClicked?.Invoke(); }); y += 50f;
        MakeButton(_bg, "Load", y, () => { Close(); LoadClicked?.Invoke(); }); y += 50f;
        MakeButton(_bg, "Settings", y, () => { _bg.Visible = false; _settingsBg.Visible = true; }); y += 50f;
        MakeButton(_bg, "Quit to Menu", y, () => RequestQuit("menu")); y += 50f;
        MakeButton(_bg, "Quit to Desktop", y, () => RequestQuit("desktop"));
    }

    private void BuildSettingsPanel()
    {
        // A second panel, not a second scene/overlay — toggled against _bg (see the Settings button
        // above and the Back button below) so exactly one of the two is ever visible while PauseMenu
        // itself is open, sharing the one outer scrim rather than stacking a second one.
        _settingsBg = new Panel
        {
            Position = new Vector2(346, 174),
            Size = new Vector2(SettingsPanelW, SettingsPanelH),
            Visible = false,
        };
        AddChild(_settingsBg);

        var header = new Label { Text = "Settings", Position = new Vector2(20, 14), Size = new Vector2(300, 28) };
        header.AddThemeFontSizeOverride("font_size", 20);
        header.AddThemeColorOverride("font_color", MapPalette.UiTextPrimary);
        _settingsBg.AddChild(header);

        var backButton = new Button { Text = "Back", Position = new Vector2(SettingsPanelW - 90, 16), Size = new Vector2(74, 28) };
        backButton.Pressed += () => { _settingsBg.Visible = false; _bg.Visible = true; };
        _settingsBg.AddChild(backButton);

        SettingsFields.Build(_settingsBg, new Vector2(20, 60));
    }

    private void BuildQuitConfirm()
    {
        _quitConfirm = new ConfirmationDialog
        {
            Title = "Quit without saving?",
            DialogText = "You have unsaved progress since your last save. Quit anyway?",
        };
        _quitConfirm.Confirmed += OnQuitConfirmed;
        _quitConfirm.Canceled += () => _pendingQuitTarget = null;
        AddChild(_quitConfirm);
    }

    private static Button MakeButton(Control parent, string text, float y, Action onPressed)
    {
        var button = new Button { Text = text, Position = new Vector2(20, y), Size = new Vector2(PanelW - 40, 38) };
        button.Pressed += onPressed;
        parent.AddChild(button);
        return button;
    }

    // --- Open/close ------------------------------------------------------------------------------

    public void Open()
    {
        Visible = true;
        _bg.Visible = true;
        _settingsBg.Visible = false;
    }

    public void Close() => Visible = false;

    // --- Quit, gated on unsaved changes -------------------------------------------------------------

    /// <summary>Shared by the real "Quit to Menu"/"Quit to Desktop" buttons and
    /// <see cref="DebugTriggerQuit"/> — no parallel shortcut. Unknown dirty state (an unwired
    /// <see cref="IsDirtyCheck"/>) is treated as dirty, the fail-safe direction: asking when nothing was
    /// actually lost is a minor annoyance, quitting silently over real unsaved progress is a real bug.</summary>
    private void RequestQuit(string target)
    {
        bool dirty = IsDirtyCheck?.Invoke() ?? true;
        if (!dirty)
        {
            InvokeQuit(target);
            return;
        }

        _pendingQuitTarget = target;
        _quitConfirm.PopupCentered();
    }

    private void OnQuitConfirmed()
    {
        if (_pendingQuitTarget is { } target) InvokeQuit(target);
        _pendingQuitTarget = null;
    }

    private void InvokeQuit(string target)
    {
        if (target == "menu") QuitToMenuRequested?.Invoke();
        else QuitToDesktopRequested?.Invoke();
    }

    /// <summary>Headless verification hook for the quit-without-saving warning (the governing rule: even
    /// a popup dialog gated on a session-only flag must be checkable from a shell, not just eyeballed in
    /// a screenshot) — mirrors <see cref="SaveLoadPanel.DebugTriggerSaveAction"/>'s exact pattern. Opens
    /// the panel and calls <see cref="RequestQuit"/>, the same method the button's Pressed signal calls,
    /// then reports whether the confirm dialog is now showing.
    ///
    /// <para>When <see cref="IsDirtyCheck"/> reports "not dirty" this does NOT show a dialog — the real
    /// quit action fires immediately, exactly as a real click would, which for `--quit-warning-test
    /// desktop` means the process actually exits and for `--quit-warning-test menu` means the scene
    /// actually changes. That is deliberate: it proves the "no warning when nothing would be lost"
    /// half of the contract for real, not just by absence of a dialog.</para></summary>
    public bool DebugTriggerQuit(string target)
    {
        Open();
        RequestQuit(target);
        return _quitConfirm.Visible;
    }

    public string DumpUiDigest() =>
        $"PauseMenu visible={Visible} settingsVisible={_settingsBg.Visible} quitConfirmVisible={_quitConfirm.Visible}";
}
