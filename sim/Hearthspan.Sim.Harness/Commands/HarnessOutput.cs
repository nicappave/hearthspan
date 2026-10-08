using System.Text.Json;

namespace Hearthspan.Sim.Harness.Commands;

/// <summary>Every subcommand emits exactly one JSON object on stdout and returns a meaningful exit
/// code — the CLI contract (CLAUDE.md, docs/development-plan.md Phase 1 item 7).</summary>
internal static class HarnessOutput
{
    // Every command already writes camelCase field names by hand in its anonymous payload types, so
    // this is a no-op for them; it matters for BalanceCommand's proposedEnvelope, which embeds a real
    // Envelope object (PascalCase C# properties) that must render with the same camelCase keys
    // Envelope.Load reads back, or a hand-copied file round-trips as silently empty.
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static int Ok(object payload)
    {
        Console.WriteLine(JsonSerializer.Serialize(payload, Options));
        return 0;
    }

    /// <summary>Like <see cref="Ok"/>, but for commands whose own claim (e.g. "this run is inside
    /// its envelope") can be false without the command itself having errored — the full payload is
    /// still emitted on stdout for diagnosis, but the exit code reflects the claim, not just whether
    /// the process ran.</summary>
    public static int Result(object payload, bool success)
    {
        Console.WriteLine(JsonSerializer.Serialize(payload, Options));
        return success ? 0 : 1;
    }

    public static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        Console.WriteLine(JsonSerializer.Serialize(new { status = "error", message }, Options));
        return 1;
    }
}
