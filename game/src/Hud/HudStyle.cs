using System;
using Godot;

namespace Hearthspan.Game.Hud;

public enum ButtonKind { Normal, Primary, Danger, Quiet }

/// <summary>
/// Plan v5 M3 (D10): one look for the whole HUD. Dark bronze panels with a gold hairline, warm
/// parchment text, gold for "act on this", red for danger. Every HUD piece builds its controls through
/// these factories so a colour or a margin changes in one place.
/// </summary>
public static class HudStyle
{
    public static readonly Color BarBg = new(0.075f, 0.064f, 0.052f, 0.95f);
    public static readonly Color CardBg = new(0.094f, 0.081f, 0.066f, 0.95f);
    public static readonly Color CardHeaderBg = new(0.135f, 0.113f, 0.088f, 1f);
    public static readonly Color ChipBg = new(0.14f, 0.12f, 0.095f, 0.9f);
    public static readonly Color Line = new(0.60f, 0.46f, 0.25f, 0.85f);
    public static readonly Color LineSoft = new(0.52f, 0.40f, 0.23f, 0.45f);
    public static readonly Color Gold = new(0.93f, 0.75f, 0.36f);
    public static readonly Color GoldDim = new(0.62f, 0.49f, 0.27f);
    public static readonly Color Text = new(0.94f, 0.90f, 0.81f);
    public static readonly Color TextSoft = new(0.80f, 0.75f, 0.66f);
    public static readonly Color TextMuted = new(0.58f, 0.53f, 0.46f);
    public static readonly Color Good = new(0.58f, 0.86f, 0.50f);
    public static readonly Color Bad = new(0.94f, 0.47f, 0.40f);
    public static readonly Color Info = new(0.58f, 0.78f, 0.96f);

    public static StyleBoxFlat Box(Color bg, Color? border = null, int borderWidth = 1, int radius = 6,
        float padX = 10, float padY = 8)
    {
        var box = new StyleBoxFlat
        {
            BgColor = bg,
            BorderColor = border ?? Colors.Transparent,
            ContentMarginLeft = padX,
            ContentMarginRight = padX,
            ContentMarginTop = padY,
            ContentMarginBottom = padY,
            AntiAliasing = true,
        };
        box.SetBorderWidthAll(border is null ? 0 : borderWidth);
        box.SetCornerRadiusAll(radius);
        return box;
    }

    /// <summary>A card: the standard floating panel.</summary>
    public static StyleBoxFlat CardBox(float padX = 12, float padY = 10) => Box(CardBg, LineSoft, 1, 7, padX, padY);

    public static Label Label(string text, int size = 14, Color? color = null, bool wrap = false)
    {
        var label = new Label { Text = text, MouseFilter = Control.MouseFilterEnum.Ignore };
        label.AddThemeFontSizeOverride("font_size", size);
        label.AddThemeColorOverride("font_color", color ?? Text);
        if (wrap) label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        return label;
    }

    /// <summary>Small letter-spaced capitals above a section ("RIVALS", "GOAL 3 OF 12").</summary>
    public static Label Kicker(string text, Color? color = null)
    {
        var label = Label(text.ToUpperInvariant(), 11, color ?? GoldDim);
        return label;
    }

    public static Button Button(string text, Action onPressed, ButtonKind kind = ButtonKind.Normal,
        int fontSize = 14, string tooltip = "")
    {
        var button = new Button { Text = text, TooltipText = tooltip, FocusMode = Control.FocusModeEnum.None };
        button.AddThemeFontSizeOverride("font_size", fontSize);
        Style(button, kind);
        button.Pressed += () => Audio.Sfx.Play(Audio.Sound.Click);
        button.Pressed += onPressed;
        return button;
    }

    public static void Style(Button button, ButtonKind kind)
    {
        foreach (var name in new[] { "normal", "hover", "pressed", "disabled", "focus" }) button.RemoveThemeStyleboxOverride(name);
        foreach (var name in new[] { "font_color", "font_hover_color", "font_pressed_color", "font_disabled_color" }) button.RemoveThemeColorOverride(name);

        Color bg, border, text;
        switch (kind)
        {
            case ButtonKind.Primary:
                bg = new Color(0.36f, 0.26f, 0.11f, 0.97f); border = Gold; text = new Color(1f, 0.93f, 0.74f);
                break;
            case ButtonKind.Danger:
                bg = new Color(0.30f, 0.12f, 0.10f, 0.95f); border = new Color(0.80f, 0.36f, 0.30f); text = new Color(1f, 0.84f, 0.80f);
                break;
            case ButtonKind.Quiet:
                bg = new Color(0f, 0f, 0f, 0f); border = new Color(0f, 0f, 0f, 0f); text = TextSoft;
                break;
            default:
                bg = new Color(0.19f, 0.155f, 0.12f, 0.96f); border = new Color(0.52f, 0.40f, 0.23f, 0.7f); text = Text;
                break;
        }

        var normal = Box(bg, border, 1, 5, 10, 5);
        var hover = Box(bg.Lightened(0.12f) with { A = Mathf.Max(bg.A, 0.35f) }, border.Lightened(0.2f), 1, 5, 10, 5);
        var pressed = Box(bg.Darkened(0.2f) with { A = Mathf.Max(bg.A, 0.5f) }, border, 1, 5, 10, 5);
        var disabled = Box(new Color(0.12f, 0.105f, 0.09f, kind == ButtonKind.Quiet ? 0f : 0.75f), new Color(0.35f, 0.29f, 0.2f, 0.4f), 1, 5, 10, 5);
        button.AddThemeStyleboxOverride("normal", normal);
        button.AddThemeStyleboxOverride("hover", hover);
        button.AddThemeStyleboxOverride("pressed", pressed);
        button.AddThemeStyleboxOverride("disabled", disabled);
        button.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        button.AddThemeColorOverride("font_color", text);
        button.AddThemeColorOverride("font_hover_color", text.Lightened(0.15f));
        button.AddThemeColorOverride("font_pressed_color", text);
        button.AddThemeColorOverride("font_disabled_color", TextMuted);
    }

    public static Control Spacer(float width = 0, float height = 0, bool expand = false)
    {
        var spacer = new Control { CustomMinimumSize = new Vector2(width, height), MouseFilter = Control.MouseFilterEnum.Ignore };
        if (expand) spacer.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        return spacer;
    }

    /// <summary>A thin progress bar in the given colour (no percentage text).</summary>
    public static ProgressBar Bar(double value, Color fill, float height = 6)
    {
        var bar = new ProgressBar
        {
            MinValue = 0,
            MaxValue = 1,
            Step = 0.001,
            Value = Math.Clamp(value, 0, 1),
            ShowPercentage = false,
            CustomMinimumSize = new Vector2(0, height),
            MouseFilter = Control.MouseFilterEnum.Ignore,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
        };
        bar.AddThemeStyleboxOverride("background", Box(new Color(0f, 0f, 0f, 0.45f), null, 0, 3, 0, 0));
        bar.AddThemeStyleboxOverride("fill", Box(fill, null, 0, 3, 0, 0));
        return bar;
    }

    public static void Clear(Node node)
    {
        foreach (var child in node.GetChildren())
        {
            node.RemoveChild(child);
            child.QueueFree();
        }
    }

    /// <summary>"+1.5" / "-0.4" / "0".</summary>
    public static string Signed(double value, string format = "0.#") =>
        value > 0.0001 ? "+" + value.ToString(format) : value < -0.0001 ? value.ToString(format) : "0";

    public static string Turns(int turns) => turns < 0 ? "never at this rate" : turns == 1 ? "1 turn" : $"{turns} turns";
}

/// <summary>An <see cref="Icon"/> glyph as a layout-friendly control.</summary>
public partial class IconRect : Control
{
    private Icon _kind;
    private Color? _tint;

    public IconRect() { MouseFilter = MouseFilterEnum.Ignore; }

    public IconRect(Icon kind, float size, Color? tint = null) : this()
    {
        _kind = kind;
        _tint = tint;
        CustomMinimumSize = new Vector2(size, size);
        SizeFlagsVertical = SizeFlags.ShrinkCenter;
    }

    public void Set(Icon kind, Color? tint = null)
    {
        _kind = kind;
        _tint = tint;
        QueueRedraw();
    }

    public override void _Draw() => Icons.Draw(this, _kind, Vector2.Zero, Mathf.Min(Size.X, Size.Y), _tint);
}
