using System;
using System.Collections.Generic;
using Godot;

namespace Hearthspan.Game;

/// <summary>
/// Stage 6 task 2: the real multi-slot save/load dialog that <see cref="GameSession"/>'s Save/Load
/// topbar buttons open, over <see cref="SaveSlots"/>' shared storage policy — the same source of truth
/// <see cref="MainMenu"/>'s own Load panel reads, so the two surfaces never disagree about what a
/// "slot" is.
///
/// <para>Built from real nodes (Panel/Label/LineEdit/Button/ScrollContainer/ConfirmationDialog), the
/// same convention <see cref="MainMenu"/>/<see cref="SelectionPanel"/> already use for their own small,
/// bounded button lists — nowhere near rule 5's "thousands of Control nodes" concern.</para>
///
/// <para>A dumb renderer over callbacks, same division of responsibility <see cref="SelectionPanel"/>/
/// <see cref="BuildView"/> already use: this panel only knows how to list slots (via
/// <see cref="SaveSlots.List"/>), take a name, and ask for a Yes/No on overwrite. It has never heard of
/// <c>World</c>/<c>SaveGame</c> — GameSession owns the live world and does the actual
/// <see cref="SaveSlots.SaveAndVerify"/>/<c>SaveGame.Load</c> call, then reports the result back via
/// <see cref="ShowStatus"/>.</para>
/// </summary>
public partial class SaveLoadPanel : Control, IUiDumpable
{
    public enum PanelMode { Save, Load }

    private const float PanelW = 460f;
    private const float PanelH = 470f;

    private Panel _bg = null!;
    private Label _header = null!;
    private LineEdit _nameInput = null!;
    private Button _actionButton = null!;
    private Label _statusLabel = null!;
    private VBoxContainer _slotList = null!;
    private ConfirmationDialog _overwriteConfirm = null!;

    private PanelMode _mode = PanelMode.Save;
    private string? _pendingOverwriteName;
    private int _slotCount;

    /// <summary>GameSession wires these up — the name typed/confirmed for a Save action, or the slot
    /// name clicked for a Load action. Neither callback is invoked until any overwrite confirmation
    /// (Save) has already been resolved.</summary>
    public Action<string>? SaveRequested;
    public Action<string>? LoadRequested;

    public override void _Ready()
    {
        Visible = false;
        MouseFilter = MouseFilterEnum.Stop;
        BuildUi();
    }

    private void BuildUi()
    {
        // Full-rect input-blocking scrim so the map/topbar behind the dialog can't be clicked while
        // it's open — same reasoning TechTree/BuildView already apply by filling the screen themselves.
        var scrim = new ColorRect { Color = new Color(0, 0, 0, 0.45f), MouseFilter = MouseFilterEnum.Stop };
        scrim.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(scrim);

        _bg = new Panel { Position = new Vector2(346, 89), Size = new Vector2(PanelW, PanelH) };
        AddChild(_bg);

        _header = new Label { Position = new Vector2(20, 14), Size = new Vector2(300, 28) };
        _header.AddThemeFontSizeOverride("font_size", 20);
        _header.AddThemeColorOverride("font_color", MapPalette.UiTextPrimary);
        _bg.AddChild(_header);

        var closeButton = new Button { Text = "Close", Position = new Vector2(PanelW - 90, 16), Size = new Vector2(74, 28) };
        closeButton.Pressed += Close;
        _bg.AddChild(closeButton);

        _nameInput = new LineEdit { Position = new Vector2(20, 54), Size = new Vector2(320, 30), PlaceholderText = "Save name" };
        _bg.AddChild(_nameInput);

        _actionButton = new Button { Text = "Save", Position = new Vector2(350, 54), Size = new Vector2(90, 30) };
        _actionButton.Pressed += OnActionPressed;
        _bg.AddChild(_actionButton);

        _statusLabel = new Label { Position = new Vector2(20, 90), Size = new Vector2(PanelW - 40, 20) };
        _statusLabel.AddThemeFontSizeOverride("font_size", 12);
        _bg.AddChild(_statusLabel);

        var scroll = new ScrollContainer { Position = new Vector2(20, 116), Size = new Vector2(PanelW - 40, PanelH - 136) };
        _bg.AddChild(scroll);
        _slotList = new VBoxContainer { CustomMinimumSize = new Vector2(PanelW - 60, 0) };
        _slotList.AddThemeConstantOverride("separation", 6);
        scroll.AddChild(_slotList);

        _overwriteConfirm = new ConfirmationDialog { Title = "Overwrite save?" };
        _overwriteConfirm.Confirmed += OnOverwriteConfirmed;
        _overwriteConfirm.Canceled += () => _pendingOverwriteName = null;
        AddChild(_overwriteConfirm);
    }

    // --- Open/close ----------------------------------------------------------------------------

    public void Open(PanelMode mode)
    {
        _mode = mode;
        Visible = true;
        _statusLabel.Text = "";
        _header.Text = mode == PanelMode.Save ? "Save Game" : "Load Game";
        _nameInput.Visible = mode == PanelMode.Save;
        _actionButton.Visible = mode == PanelMode.Save;
        if (mode == PanelMode.Save)
        {
            _nameInput.Text = $"save-{DateTime.Now:yyyy-MM-dd HHmm}";
        }
        RefreshSlotList();
    }

    public void Close() => Visible = false;

    // --- Slot list -------------------------------------------------------------------------------

    private void RefreshSlotList()
    {
        // Clear every existing child unconditionally (not just tracked Buttons) — this also removes
        // the "No saved games yet" placeholder label from a previous empty refresh, so it never
        // accumulates duplicates across repeated Open()/ShowStatus() calls.
        foreach (var child in _slotList.GetChildren())
        {
            _slotList.RemoveChild(child);
            child.QueueFree();
        }

        var slots = SaveSlots.List();
        _slotCount = slots.Count;

        if (slots.Count == 0)
        {
            var empty = new Label { Text = "No saved games yet." };
            empty.AddThemeColorOverride("font_color", MapPalette.UiTextMuted);
            _slotList.AddChild(empty);
            return;
        }

        foreach (var slot in slots)
        {
            var button = new Button
            {
                Text = $"{slot.Name}   —   {slot.When:yyyy-MM-dd HH:mm}",
                CustomMinimumSize = new Vector2(PanelW - 60, 32),
            };
            if (_mode == PanelMode.Save)
            {
                string name = slot.Name;
                button.Pressed += () => _nameInput.Text = name;
            }
            else
            {
                string name = slot.Name;
                button.Pressed += () => LoadRequested?.Invoke(name);
            }
            _slotList.AddChild(button);
        }
    }

    // --- Save flow, with confirm-overwrite ---------------------------------------------------------

    private void OnActionPressed()
    {
        var name = _nameInput.Text.Trim();
        if (name.Length == 0)
        {
            ShowStatus("Enter a name for this save.", true);
            return;
        }

        if (SaveSlots.Exists(name))
        {
            _pendingOverwriteName = name;
            _overwriteConfirm.DialogText = $"A save named \"{SaveSlots.SanitizeName(name)}\" already exists. Overwrite it?";
            _overwriteConfirm.PopupCentered();
            return;
        }

        SaveRequested?.Invoke(name);
    }

    private void OnOverwriteConfirmed()
    {
        if (_pendingOverwriteName != null) SaveRequested?.Invoke(_pendingOverwriteName);
        _pendingOverwriteName = null;
    }

    /// <summary>Headless verification hook for the confirm-overwrite path (the governing rule: even a
    /// popup dialog must be checkable from a shell, not just eyeballed in a screenshot). Mirrors
    /// exactly what a real click on the Save button does — sets the name field and invokes the same
    /// <see cref="OnActionPressed"/> the button's <c>Pressed</c> signal calls — then reports whether
    /// the overwrite confirmation is now showing, so `--save-overwrite-test` (GameSession) can assert
    /// it without simulating a real mouse click. Does not click "Yes"/"No" itself — the popup is left
    /// open exactly as a real click would leave it, for a screenshot to also confirm visually.</summary>
    public bool DebugTriggerSaveAction(string name)
    {
        Open(PanelMode.Save);
        _nameInput.Text = name;
        OnActionPressed();
        return _overwriteConfirm.Visible;
    }

    /// <summary>GameSession calls this after a save attempt (verified or not) / a load attempt, so the
    /// player sees the real outcome rather than trusting that a click "did something" — the second-
    /// channel rule made visible in the UI itself, not just in a log line nobody reads.</summary>
    public void ShowStatus(string text, bool isError)
    {
        _statusLabel.Text = text;
        _statusLabel.AddThemeColorOverride("font_color", isError ? new Color(0.86f, 0.34f, 0.30f, 1f) : MapPalette.UiAccent);
        if (Visible) RefreshSlotList();
    }

    public string DumpUiDigest() =>
        $"SaveLoadPanel visible={Visible} mode={_mode} slots={_slotCount} status=\"{_statusLabel?.Text}\"";
}
