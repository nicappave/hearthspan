using System;
using System.Collections.Generic;
using System.IO;
using Godot;

namespace Hearthspan.Game;

/// <summary>
/// Stage 6 task 1: a real front door. <c>project.godot</c>'s <c>run/main_scene</c> now points here
/// instead of at <c>Main.tscn</c> — see <see cref="GameLaunchConfig"/>'s doc comment for the full
/// bridge contract this scene is the other half of.
///
/// <para>Built entirely from code (no dense hand-authored <c>.tscn</c> tree), the same convention
/// <see cref="GameSession.BuildTopBar"/>/<see cref="BuildView"/>/<see cref="SelectionPanel"/> already
/// use for their own Button/Label children — a menu with a few dozen nodes is nowhere near rule 5's
/// "thousands of Control nodes" concern, so plain real nodes (themed automatically by
/// <c>game/ui/theme.tres</c>, set project-wide) are the simplest correct choice, not a custom
/// <c>_Draw()</c> panel.</para>
///
/// <para><b>Headless/CLI back-compat (the governing rule):</b> every pre-Stage-6 shell-drivable
/// invocation either launches <c>res://scenes/Main.tscn</c> directly (still works, completely
/// unchanged — this scene isn't in that path at all) or now launches the new default scene,
/// <c>res://scenes/MainMenu.tscn</c>, with the same <c>--seed</c>/<c>--load</c>/<c>--auto-turns</c>/
/// etc. flags it always used. <see cref="ShouldSkipMenu"/> recognizes that second case — any
/// GreyBoxArgs flag that only makes sense with a live <see cref="GameSession"/> running — and
/// <see cref="ForwardFromCliArgs"/> immediately (no click, no waiting for a frame of interactive UI)
/// populates <see cref="GameLaunchConfig"/> from those same flags and forwards to
/// <c>Main.tscn</c>, so the two entry points produce byte-identical <see cref="GameSession"/> state.
/// A screenshot/dump-ui request for the MENU itself (<c>--shot-out</c>/<c>--dump-ui-out</c> with no
/// game-intent flag) is deliberately NOT in that trigger list — that's the other real headless use of
/// this scene, proving the menu itself renders.</para>
/// </summary>
public partial class MainMenu : Control, IUiDumpable
{
    // Flags that only mean anything once a GameSession exists — presence of any one of these means
    // "this launch wants the game, not the menu", regardless of which scene project.godot points at.
    // Deliberately excludes shot-out/dump-ui-out/frames/perf-frames/camera: those are AppRunner-level
    // capture flags that legitimately apply to screenshotting/dumping THIS scene on its own.
    private static readonly string[] SkipMenuTriggerKeys =
    {
        "skip-menu", "seed", "load", "auto-turns", "research", "build", "show-panel",
        "select-settlement", "build-road-test", "train-unit", "move-unit", "garrison-unit",
        "declare-war", "width", "height", "civs", "player-civ", "save-now", "quit-after-ready",
        "worldview-out", "save-path",
        // Stage 6 task 5: --smoke lives on GameSession (Main.tscn), not this menu scene. Found by
        // reproduction on the actual exported .exe: project.godot's run/main_scene is MainMenu.tscn,
        // not Main.tscn, so without this entry a bare `Hearthspan.exe --headless -- --smoke` never
        // reaches GameSession._Ready() at all — it silently idles on the menu forever (0% CPU, no
        // output, no exit), which looks exactly like a hang from the outside. Without an explicit
        // trigger key, ShouldSkipMenu() has no way to know --smoke needs a live GameSession.
        "smoke",
        // Task C1 (docs/stage-7-plan.md): the decision-loop flags, same trap as --smoke above — any
        // flag GameSession alone recognises is invisible to a bare exported-exe launch unless it is
        // listed here too.
        "decisions", "decide", "no-prompts",
        // Task B1: same trap again for the settlement-name collision check.
        "debug-name-collision-check",
        // Plan v3 task 1.4: same trap again for the ground-heights measurement check.
        "debug-ground-heights",
        // Plan v3 task 3.9: same trap again for the era-selection check.
        "debug-era-check",
        // Plan v3 task 4.6: same trap again for the growth overlay.
        "overlay",
        // Plan v3 task 5.1: same trap again for the seven flags the export audit found missing —
        // each is a GameSession-only flag that would otherwise silently idle on the menu forever
        // under a bare exported-exe launch, exactly like every entry above it.
        "load-slot", "save-slot", "save-overwrite-test", "quit-warning-test",
        "debug-force-wonder", "debug-clear-player-settlements",
        // Plan v5: the renderer's reveal switch and the M3 map-interaction flags, same trap again.
        "debug-reveal", "select-unit", "explore-unit", "unit-dest", "select-tile", "found-mode", "hover",
        "card-action", "card-action-first", "end-turn", "press-end-turn", "do",
    };

    private const int MaxSaveSlotsShown = 8;

    private Panel _contentPanel = null!;
    private Control _newGamePanel = null!;
    private Control _loadPanel = null!;
    private Control _settingsPanel = null!;
    private Label _statusLabel = null!;
    private SpinBox _seedInput = null!;
    private Button _newGameNav = null!;
    private Button _loadNav = null!;
    private Button _settingsNav = null!;

    private readonly List<Button> _loadSlotButtons = new();
    private Label? _emptyNotice;
    private int _saveSlotCount;
    private string _currentPanelName = "new";
    private bool _forwarded;

    public override void _Ready()
    {
        GameSettings.Load();
        GameSettings.Apply();

        if (ShouldSkipMenu())
        {
            ForwardFromCliArgs();
            return;
        }

        BuildUi();
    }

    private static bool ShouldSkipMenu()
    {
        foreach (var key in SkipMenuTriggerKeys)
        {
            if (GreyBoxArgs.Get(key) != null) return true;
        }
        return false;
    }

    /// <summary>Populates <see cref="GameLaunchConfig"/> from the same GreyBoxArgs values a direct
    /// <c>Main.tscn</c> launch would have read off the command line, then forwards — only setting a
    /// field's <c>Has*</c> flag when the corresponding flag was actually present, so anything left
    /// unset falls through to <see cref="GameSession"/>'s own GreyBoxArgs-fallback defaults exactly as
    /// before, rather than this method re-hardcoding those defaults a second time (a duplicate source
    /// of truth that could silently drift from GameSession's own).</summary>
    private void ForwardFromCliArgs()
    {
        _forwarded = true;
        GameLaunchConfig.Reset();
        GameLaunchConfig.MenuLaunched = true;

        var loadArg = GreyBoxArgs.Get("load");
        if (loadArg != null)
        {
            GameLaunchConfig.HasLoadPath = true;
            GameLaunchConfig.LoadPath = loadArg;
        }

        var seedArg = GreyBoxArgs.Get("seed");
        if (seedArg != null)
        {
            GameLaunchConfig.HasSeed = true;
            GameLaunchConfig.Seed = (ulong)GreyBoxArgs.GetInt("seed", 1234);
        }

        var widthArg = GreyBoxArgs.Get("width");
        if (widthArg != null)
        {
            GameLaunchConfig.HasWidth = true;
            GameLaunchConfig.Width = GreyBoxArgs.GetInt("width", 64);
        }

        var heightArg = GreyBoxArgs.Get("height");
        if (heightArg != null)
        {
            GameLaunchConfig.HasHeight = true;
            GameLaunchConfig.Height = GreyBoxArgs.GetInt("height", 40);
        }

        var civsArg = GreyBoxArgs.Get("civs");
        if (civsArg != null)
        {
            GameLaunchConfig.HasCivCount = true;
            GameLaunchConfig.CivCount = GreyBoxArgs.GetInt("civs", 4);
        }

        var playerCivArg = GreyBoxArgs.Get("player-civ");
        if (playerCivArg != null)
        {
            GameLaunchConfig.HasPlayerCivIndex = true;
            GameLaunchConfig.PlayerCivIndex = GreyBoxArgs.GetInt("player-civ", 0);
        }

        GD.Print("MainMenu: GreyBoxArgs game-intent flag(s) present, skipping the menu and forwarding straight to Main.tscn.");
        DeferredChangeScene("res://scenes/Main.tscn");
    }

    /// <summary>New Stage 6 trap (see CLAUDE.md's trap list): calling
    /// <c>GetTree().ChangeSceneToFile()</c> directly from <c>_Ready()</c> of the scene the engine is
    /// still in the middle of adding as the tree's very first root — i.e. exactly the skip-menu path,
    /// which needs to forward before a single frame renders — reproducibly throws
    /// "Parent node is busy adding/removing children, `remove_child()` can't be called at this time."
    /// The engine treats it as non-fatal (the scene swap still lands a frame later regardless), but a
    /// spurious ERROR line on every headless skip-menu launch would be exactly the kind of "true but
    /// noisy" report the second-channel rule warns about, and printing a raw engine stack trace is not
    /// something a shell-driven verification script should have to tolerate. Wrapping the same call in
    /// <c>Callable.From(...).CallDeferred()</c> — the standard Godot fix for "can't mutate the tree
    /// right now" — pushes it to idle time, after the tree has finished the add that made it busy, and
    /// the error stops reproducing entirely (confirmed: repeated headless runs of the skip-menu path
    /// after this fix print no ERROR line). <see cref="StartNewGame"/>/<see cref="LoadSlot"/> call this
    /// too, defensively, even though those only ever fire from a Button.Pressed at normal input time
    /// (tree already settled, not mid-add) — the deferred call is free insurance either way.</summary>
    private void DeferredChangeScene(string path)
    {
        Callable.From(() => GetTree().ChangeSceneToFile(path)).CallDeferred();
    }

    // --- UI construction -------------------------------------------------------------------------

    private void BuildUi()
    {
        // Plan v5: a live world behind the menu. The dark background covers the screen until the
        // scenery has been generated (about a second), then fades away; a gradient keeps the menu
        // side of the screen readable over bright land.
        var backdrop = new MenuBackdrop { Name = "Backdrop" };
        AddChild(backdrop);
        var background = new ColorRect { Color = new Color(0.055f, 0.048f, 0.040f, 1f), MouseFilter = MouseFilterEnum.Ignore };
        background.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(background);
        var gradient = new Gradient();
        gradient.SetColor(0, new Color(0.045f, 0.038f, 0.03f, 0.92f));
        gradient.SetColor(1, new Color(0.045f, 0.038f, 0.03f, 0f));
        gradient.SetOffset(1, 1f);
        var shade = new TextureRect
        {
            Texture = new GradientTexture2D { Gradient = gradient, Width = 256, Height = 4, FillFrom = new Vector2(0.45f, 0), FillTo = new Vector2(1f, 0) },
            StretchMode = TextureRect.StretchModeEnum.Scale,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            MouseFilter = MouseFilterEnum.Ignore,
            AnchorRight = 0.85f,
            AnchorBottom = 1f,
        };
        AddChild(shade);
        backdrop.Loaded = () =>
        {
            var tween = CreateTween();
            tween.TweenProperty(background, "color:a", 0f, 1.6).SetTrans(Tween.TransitionType.Sine);
        };

        var title = new Label
        {
            Text = "HEARTHSPAN",
            Position = new Vector2(64, 46),
            Size = new Vector2(600, 60),
        };
        title.AddThemeFontSizeOverride("font_size", 46);
        title.AddThemeColorOverride("font_color", MapPalette.UiAccent);
        title.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.7f));
        title.AddThemeConstantOverride("shadow_offset_x", 2);
        title.AddThemeConstantOverride("shadow_offset_y", 2);
        AddChild(title);

        var subtitle = new Label
        {
            Text = "From Hamlet to Horizon",
            Position = new Vector2(68, 106),
            Size = new Vector2(600, 30),
        };
        subtitle.AddThemeFontSizeOverride("font_size", 16);
        subtitle.AddThemeColorOverride("font_color", MapPalette.UiTextSecondary);
        AddChild(subtitle);

        BuildNavColumn();

        _contentPanel = new Panel { Position = new Vector2(360, 190), Size = new Vector2(730, 410) };
        _contentPanel.AddThemeStyleboxOverride("panel", Hud.HudStyle.Box(Hud.HudStyle.CardBg with { A = 0.9f }, Hud.HudStyle.LineSoft, 1, 8, 0, 0));
        AddChild(_contentPanel);

        _newGamePanel = BuildNewGamePanel();
        _loadPanel = BuildLoadPanel();
        _settingsPanel = BuildSettingsPanel();
        _contentPanel.AddChild(_newGamePanel);
        _contentPanel.AddChild(_loadPanel);
        _contentPanel.AddChild(_settingsPanel);

        _statusLabel = new Label { Position = new Vector2(70, 616), Size = new Vector2(1000, 24) };
        _statusLabel.AddThemeColorOverride("font_color", MapPalette.UiTextMuted);
        AddChild(_statusLabel);

        // --menu-panel new|load|settings: the headless-drivable equivalent of clicking a nav button,
        // same pattern GameSession's own --show-panel/--select-settlement flags already document —
        // Load/Settings are otherwise unreachable from a shell without simulating a real mouse click.
        var panelArg = GreyBoxArgs.Get("menu-panel", "new")!;
        ShowPanel(panelArg is "new" or "load" or "settings" ? panelArg : "new");
    }

    private void BuildNavColumn()
    {
        const float x = 70, w = 230, h = 46, gap = 58;
        float y = 210;

        _newGameNav = MakeNavButton("New Game", new Vector2(x, y), w, h, () => ShowPanel("new"));
        y += gap;
        _loadNav = MakeNavButton("Load Game", new Vector2(x, y), w, h, () => ShowPanel("load"));
        y += gap;
        _settingsNav = MakeNavButton("Settings", new Vector2(x, y), w, h, () => ShowPanel("settings"));
        y += gap;
        MakeNavButton("Quit", new Vector2(x, y), w, h, () => GetTree().Quit(0));
    }

    private Button MakeNavButton(string text, Vector2 position, float width, float height, Action onPressed)
    {
        var button = new Button { Text = text, Position = position, Size = new Vector2(width, height) };
        button.Pressed += onPressed;
        AddChild(button);
        return button;
    }

    private void ShowPanel(string name)
    {
        _currentPanelName = name;
        _newGamePanel.Visible = name == "new";
        _loadPanel.Visible = name == "load";
        _settingsPanel.Visible = name == "settings";

        // Same "disable the button for the thing you're already looking at" convention BuildView's
        // settlement switcher uses (SetOwnedSettlements) — a cheap, consistent "you are here" cue.
        _newGameNav.Disabled = name == "new";
        _loadNav.Disabled = name == "load";
        _settingsNav.Disabled = name == "settings";

        if (name == "load") RefreshLoadSlots();
    }

    // --- New Game ----------------------------------------------------------------------------------

    private Control BuildNewGamePanel()
    {
        var panel = new Control { Size = _contentPanel.Size };

        MakeHeader("New Game", panel);

        var seedLabel = new Label { Text = "Seed", Position = new Vector2(28, 74), Size = new Vector2(200, 20) };
        panel.AddChild(seedLabel);

        // A fresh random default each time the menu opens, purely as a UI convenience (nothing here
        // touches sim/ or any of its seeded PCG32 streams — see CLAUDE.md rule 2). Once "Start New
        // Game" is pressed, whatever value sits in the field becomes the one real, fully deterministic
        // World seed, same as always. Capped to int range to match every existing --seed invocation
        // (GameSession casts GreyBoxArgs' int-parsed seed the same way), not because ulong can't hold
        // more.
        _seedInput = new SpinBox
        {
            MinValue = 1,
            MaxValue = int.MaxValue,
            Step = 1,
            Value = new Random().Next(1, int.MaxValue),
            Position = new Vector2(28, 100),
            Size = new Vector2(240, 32),
        };
        panel.AddChild(_seedInput);

        var note = new Label
        {
            Text = "A 64x40 map with four civilizations. You play the red one; the same seed\nalways gives the same world.",
            Position = new Vector2(28, 142),
            Size = new Vector2(620, 44),
        };
        note.AddThemeColorOverride("font_color", MapPalette.UiTextMuted);
        note.AddThemeFontSizeOverride("font_size", 12);
        panel.AddChild(note);

        var start = new Button { Text = "Start New Game", Position = new Vector2(28, 210), Size = new Vector2(220, 42) };
        start.Pressed += StartNewGame;
        panel.AddChild(start);

        return panel;
    }

    private void StartNewGame()
    {
        GameLaunchConfig.Reset();
        GameLaunchConfig.MenuLaunched = true;
        // Explicit, not just "unset": clicking New Game overrides any stray --load the process
        // happened to be launched with, same reasoning as GameLaunchConfig's own doc comment.
        GameLaunchConfig.HasLoadPath = true;
        GameLaunchConfig.LoadPath = null;
        GameLaunchConfig.HasSeed = true;
        GameLaunchConfig.Seed = (ulong)_seedInput.Value;

        DeferredChangeScene("res://scenes/Main.tscn");
    }

    // --- Load Game -----------------------------------------------------------------------------

    private Control BuildLoadPanel()
    {
        var panel = new Control { Size = _contentPanel.Size, Visible = false };
        MakeHeader("Load Game", panel);
        return panel;
    }

    /// <summary>Stage 6 task 2: reads the real multi-slot store via <see cref="SaveSlots"/> — the same
    /// shared source of truth <see cref="GameSession"/>'s Save/Load dialog (<see cref="SaveLoadPanel"/>)
    /// reads, so a slot saved from the in-game dialog shows up here too, and vice versa. Capped to
    /// <see cref="MaxSaveSlotsShown"/> for this panel's fixed layout — <see cref="SaveSlots.List"/>
    /// itself returns up to 20; the in-game dialog has more room and uses that default.</summary>
    private void RefreshLoadSlots()
    {
        foreach (var b in _loadSlotButtons) { _loadPanel.RemoveChild(b); b.QueueFree(); }
        _loadSlotButtons.Clear();

        if (_emptyNotice != null) { _loadPanel.RemoveChild(_emptyNotice); _emptyNotice.QueueFree(); _emptyNotice = null; }

        var slots = SaveSlots.List(MaxSaveSlotsShown);
        _saveSlotCount = slots.Count;

        if (slots.Count == 0)
        {
            _emptyNotice = new Label
            {
                Text = "No save files found yet — start a new game and use Save to create one.",
                Position = new Vector2(28, 70),
                Size = new Vector2(660, 44),
            };
            _emptyNotice.AddThemeColorOverride("font_color", MapPalette.UiTextMuted);
            _loadPanel.AddChild(_emptyNotice);
            return;
        }

        float y = 70;
        foreach (var slot in slots)
        {
            var button = new Button
            {
                Text = $"{slot.Name}  —  {slot.When:yyyy-MM-dd HH:mm}",
                Position = new Vector2(28, y),
                Size = new Vector2(660, 38),
            };
            string path = slot.Path;
            button.Pressed += () => LoadSlot(path);
            _loadPanel.AddChild(button);
            _loadSlotButtons.Add(button);
            y += 44;
        }
    }

    private void LoadSlot(string path)
    {
        if (!File.Exists(path))
        {
            _statusLabel.Text = $"That save file no longer exists: {path}";
            RefreshLoadSlots();
            return;
        }

        GameLaunchConfig.Reset();
        GameLaunchConfig.MenuLaunched = true;
        GameLaunchConfig.HasLoadPath = true;
        GameLaunchConfig.LoadPath = path;

        DeferredChangeScene("res://scenes/Main.tscn");
    }

    // --- Settings ------------------------------------------------------------------------------

    private Control BuildSettingsPanel()
    {
        var panel = new Control { Size = _contentPanel.Size, Visible = false };
        MakeHeader("Settings", panel);

        // Stage 6 task 6: the field widgets themselves now live in the shared SettingsFields builder
        // (see its doc comment) — PauseMenu's in-session Settings overlay reuses the exact same call,
        // so this is the one and only place the slider/picker layout is authored.
        SettingsFields.Build(panel, new Vector2(28, 74));

        return panel;
    }

    private Label MakeHeader(string text, Control parent)
    {
        var header = new Label { Text = text, Position = new Vector2(28, 20), Size = new Vector2(400, 32) };
        header.AddThemeFontSizeOverride("font_size", 22);
        header.AddThemeColorOverride("font_color", MapPalette.UiTextPrimary);
        parent.AddChild(header);
        return header;
    }

    public string DumpUiDigest()
    {
        if (_forwarded)
        {
            return "MainMenu forwarded=true (skip-menu path — GameLaunchConfig set, ChangeSceneToFile(\"Main.tscn\") issued)";
        }

        return string.Join('\n', new[]
        {
            $"MainMenu panel={_currentPanelName} seedDefault={(ulong)(_seedInput?.Value ?? 0)}",
            $"saveSlots count={_saveSlotCount}",
            $"settings volume={GameSettings.Volume:F2} window={GameSettings.WindowWidth}x{GameSettings.WindowHeight} fullscreen={GameSettings.Fullscreen} cameraSpeed={GameSettings.CameraSpeedMultiplier:F2}",
        });
    }
}
