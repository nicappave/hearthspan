using System.Collections.Generic;
using Godot;

namespace Hearthspan.Game;

/// <summary>Parses Godot's user args (everything after `--` on the command line, via
/// OS.GetCmdlineUserArgs()) as `--key value` / `--key=value` pairs. This is how the harness passes a
/// worldview path, a screenshot output path, or benchmark parameters into a scene it launched as a
/// subprocess — the governing rule (CLAUDE.md) requires everything be drivable from a shell.</summary>
public static class GreyBoxArgs
{
    private static Dictionary<string, string>? _cache;

    public static Dictionary<string, string> All()
    {
        if (_cache != null) return _cache;

        var result = new Dictionary<string, string>();
        var args = OS.GetCmdlineUserArgs();
        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--")) continue;
            var body = arg[2..];
            var eq = body.IndexOf('=');
            if (eq >= 0)
            {
                result[body[..eq]] = body[(eq + 1)..];
                continue;
            }

            if (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
            {
                result[body] = args[i + 1];
                i++;
            }
            else
            {
                result[body] = "true";
            }
        }
        _cache = result;
        return result;
    }

    public static string? Get(string key, string? fallback = null) =>
        All().TryGetValue(key, out var v) ? v : fallback;

    public static int GetInt(string key, int fallback) =>
        All().TryGetValue(key, out var v) && int.TryParse(v, out var i) ? i : fallback;
}
