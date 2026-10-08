namespace Hearthspan.Sim.Harness.Commands;

/// <summary>Minimal `--key value` parser — no third-party CLI framework, this is a ~300-line thin
/// adapter (docs/development-plan.md tooling backlog).</summary>
internal sealed class ArgParser
{
    private readonly Dictionary<string, string> _values = new();

    public static ArgParser Parse(string[] args)
    {
        var parser = new ArgParser();
        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--")) continue;
            var key = arg[2..];
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
            {
                parser._values[key] = args[i + 1];
                i++;
            }
            else
            {
                parser._values[key] = "true";
            }
        }
        return parser;
    }

    public string? GetOrDefault(string key, string? fallback = null) =>
        _values.TryGetValue(key, out var v) ? v : fallback;

    public int GetInt(string key, int fallback) =>
        _values.TryGetValue(key, out var v) && int.TryParse(v, out var i) ? i : fallback;

    public ulong GetULong(string key, ulong fallback) =>
        _values.TryGetValue(key, out var v) && ulong.TryParse(v, out var u) ? u : fallback;

    /// <summary>Every parsed --key value pair not in <paramref name="exclude"/> — used by commands
    /// that forward unrecognized flags straight through to a Godot subprocess (e.g. dump-ui/spike
    /// commands passing --node-count through to a spike scene).</summary>
    public IEnumerable<KeyValuePair<string, string>> AllPassthrough(ISet<string> exclude)
    {
        foreach (var kv in _values)
        {
            if (!exclude.Contains(kv.Key)) yield return kv;
        }
    }
}
