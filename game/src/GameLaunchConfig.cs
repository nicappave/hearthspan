namespace Hearthspan.Game;

/// <summary>
/// Stage 6: the bridge that lets <see cref="MainMenu"/> hand a chosen seed/load-path/new-vs-load
/// intent to <see cref="GameSession"/> without a process relaunch — while leaving every existing
/// <c>--seed</c>/<c>--load</c>/<c>--width</c>/<c>--height</c>/<c>--civs</c>/<c>--player-civ</c>
/// shell-drivable path (<see cref="GreyBoxArgs"/>, parsed once from
/// <c>OS.GetCmdlineUserArgs()</c>) completely untouched.
///
/// <para><b>Contract:</b> each field has its own <c>Has*</c> flag. <see cref="GameSession"/>._Ready()
/// checks the flag FIRST for that one field; only when it's false does it fall back to
/// <see cref="GreyBoxArgs"/> exactly as before Stage 6. Fields are independent — a caller that only
/// sets <see cref="HasSeed"/> still gets GreyBoxArgs/default behavior for width/height/civs/etc. This
/// is what makes every pre-Stage-6 headless invocation (launched straight at
/// <c>res://scenes/Main.tscn</c>, which never touches this class at all since nothing sets any
/// <c>Has*</c> flag) behave byte-identically to before: <see cref="GameSession"/> only ever reads
/// this class's flags, and a flag that was never set is always false.</para>
///
/// <para>Two producers set this class today: <see cref="MainMenu"/>'s interactive New-Game/Load
/// flows (a real player choice, no CLI args involved), and <see cref="MainMenu"/>'s own
/// skip-menu forwarding path, which populates it FROM the same GreyBoxArgs values a direct
/// <c>Main.tscn</c> launch would have read, then forwards via
/// <c>GetTree().ChangeSceneToFile("res://scenes/Main.tscn")</c> — so a headless run that launches
/// the new default scene (<c>MainMenu.tscn</c>) with <c>--seed</c>/<c>--load</c>/etc. still ends up
/// with exactly the values it asked for, just routed through this class instead of GreyBoxArgs
/// directly on that one occasion.</para>
/// </summary>
public static class GameLaunchConfig
{
    public static bool HasSeed;
    public static ulong Seed;

    /// <summary>True means "the menu has an opinion about load-vs-new" — <see cref="LoadPath"/> null
    /// with this true is a real New Game (deliberately overriding any stray <c>--load</c> the process
    /// happened to be launched with), not "unset".</summary>
    public static bool HasLoadPath;
    public static string? LoadPath;

    public static bool HasWidth;
    public static int Width;

    public static bool HasHeight;
    public static int Height;

    public static bool HasCivCount;
    public static int CivCount;

    public static bool HasPlayerCivIndex;
    public static int PlayerCivIndex;

    /// <summary>Diagnostic only — not read as a gate anywhere. Lets <c>DumpUiDigest()</c>/logs say
    /// truthfully whether this launch went through the menu bridge at all.</summary>
    public static bool MenuLaunched;

    /// <summary>Clears every flag back to "GameSession, use GreyBoxArgs/your own defaults for
    /// everything" — called at the start of both MainMenu producer paths so a second New-Game/Load
    /// click in the same process (impossible today, since both paths immediately change scene, but
    /// cheap insurance against a future re-entrant menu) never leaks a stale field from a previous
    /// choice.</summary>
    public static void Reset()
    {
        HasSeed = false;
        Seed = 0;
        HasLoadPath = false;
        LoadPath = null;
        HasWidth = false;
        Width = 0;
        HasHeight = false;
        Height = 0;
        HasCivCount = false;
        CivCount = 0;
        HasPlayerCivIndex = false;
        PlayerCivIndex = 0;
        MenuLaunched = false;
    }
}
