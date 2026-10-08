using System;
using Godot;

namespace Hearthspan.Game;

/// <summary>
/// Stage 6 task 6 (the menu and settings work): the volume/resolution/fullscreen/camera-speed widget set task 6.1
/// built once, inline, inside <c>MainMenu.BuildSettingsPanel</c>. Extracted here so
/// <see cref="PauseMenu"/>'s in-session Settings overlay can reuse the exact same controls instead of a
/// second hand-copy — the same "one shared mechanism, not two that could drift" reasoning
/// <see cref="SaveSlots"/> was pulled out of <see cref="MainMenu"/> for in Stage 6 task 2. Every widget
/// here reads and writes the same <see cref="GameSettings"/> static fields either caller already
/// depended on, so a change made from the pause menu mid-session and a change made from the main menu
/// before a game exists behave identically — same persistence (<c>user://settings.cfg</c>), same
/// <see cref="GameSettings.Apply"/> no-op-under-headless/capture-run guard.
///
/// <para>Deliberately not a <see cref="Control"/> subclass of its own: it has no state and no digest to
/// report (both call sites already read <see cref="GameSettings"/>'s own static fields directly for
/// their <c>DumpUiDigest()</c>), so a plain static builder is the simplest correct shape — the same
/// division <see cref="MapPalette"/> already uses for shared, stateless UI facts.</para>
/// </summary>
public static class SettingsFields
{
    private static readonly (int W, int H)[] ResolutionPresets =
    {
        (1152, 648), (1280, 720), (1600, 900), (1920, 1080),
    };

    /// <summary>Adds the four settings controls into <paramref name="parent"/>, top-left corner at
    /// <paramref name="origin"/> (parent-relative) — callers own their own header/close button/
    /// background panel, this only owns the fields themselves.</summary>
    public static void Build(Control parent, Vector2 origin)
    {
        float ox = origin.X, oy = origin.Y;

        // --- Volume ---
        var volumeLabel = new Label { Text = "Master Volume", Position = new Vector2(ox, oy), Size = new Vector2(240, 20) };
        parent.AddChild(volumeLabel);

        var volumeSlider = new HSlider
        {
            MinValue = 0,
            MaxValue = 100,
            Step = 1,
            Value = Math.Clamp(GameSettings.Volume * 100.0, 0, 100),
            Position = new Vector2(ox, oy + 26),
            Size = new Vector2(300, 24),
        };
        var volumeValueLabel = new Label { Text = $"{volumeSlider.Value:F0}%", Position = new Vector2(ox + 312, oy + 26), Size = new Vector2(60, 24) };
        volumeSlider.ValueChanged += v =>
        {
            GameSettings.Volume = (float)(v / 100.0);
            volumeValueLabel.Text = $"{v:F0}%";
            GameSettings.Apply();
        };
        volumeSlider.DragEnded += changed => { if (changed) GameSettings.Save(); };
        parent.AddChild(volumeSlider);
        parent.AddChild(volumeValueLabel);

        // --- Window / resolution ---
        var resLabel = new Label { Text = "Window Size", Position = new Vector2(ox, oy + 76), Size = new Vector2(240, 20) };
        parent.AddChild(resLabel);

        var resPicker = new OptionButton { Position = new Vector2(ox, oy + 102), Size = new Vector2(260, 32) };
        int selectedIndex = 0;
        for (int i = 0; i < ResolutionPresets.Length; i++)
        {
            var (w, h) = ResolutionPresets[i];
            resPicker.AddItem($"{w} x {h}" + (w == GameSettings.DefaultWindowWidth && h == GameSettings.DefaultWindowHeight ? " (default)" : ""));
            if (w == GameSettings.WindowWidth && h == GameSettings.WindowHeight) selectedIndex = i;
        }
        resPicker.Selected = selectedIndex;
        resPicker.ItemSelected += index =>
        {
            var (w, h) = ResolutionPresets[(int)index];
            GameSettings.WindowWidth = w;
            GameSettings.WindowHeight = h;
            GameSettings.Apply();
            GameSettings.Save();
        };
        parent.AddChild(resPicker);

        var fullscreenCheck = new CheckBox
        {
            Text = "Fullscreen",
            ButtonPressed = GameSettings.Fullscreen,
            Position = new Vector2(ox + 282, oy + 104),
            Size = new Vector2(140, 32),
        };
        fullscreenCheck.Toggled += pressed =>
        {
            GameSettings.Fullscreen = pressed;
            GameSettings.Apply();
            GameSettings.Save();
        };
        parent.AddChild(fullscreenCheck);

        // --- Camera speed ---
        var camLabel = new Label { Text = "Camera Speed", Position = new Vector2(ox, oy + 158), Size = new Vector2(240, 20) };
        parent.AddChild(camLabel);

        var camSlider = new HSlider
        {
            MinValue = 0.25,
            MaxValue = 3.0,
            Step = 0.25,
            Value = Math.Clamp(GameSettings.CameraSpeedMultiplier, 0.25, 3.0),
            Position = new Vector2(ox, oy + 184),
            Size = new Vector2(300, 24),
        };
        var camValueLabel = new Label { Text = $"{camSlider.Value:F2}x", Position = new Vector2(ox + 312, oy + 184), Size = new Vector2(60, 24) };
        camSlider.ValueChanged += v =>
        {
            GameSettings.CameraSpeedMultiplier = (float)v;
            camValueLabel.Text = $"{v:F2}x";
            GameSettings.Apply();
        };
        camSlider.DragEnded += changed => { if (changed) GameSettings.Save(); };
        parent.AddChild(camSlider);
        parent.AddChild(camValueLabel);
    }
}
