using Hearthspan.Sim.Harness.Commands;

// hearthspan CLI v1 (docs/development-plan.md Phase 1 item 7): one JSON object on stdout per
// invocation, diagnostics to stderr, meaningful exit codes. Never scrape a log for status.

const string knownCommands = "test, sim, hash, lint-content, balance, perf, shot, shot-diff, dump-ui, build-order, war-metric, steer, pace";

if (args.Length == 0)
{
    return HarnessOutput.Fail($"no subcommand given — expected one of: {knownCommands}");
}

var rest = args.Skip(1).ToArray();
return args[0] switch
{
    "test" => TestCommand.Run(rest),
    "sim" => SimCommand.Run(rest),
    "hash" => HashCommand.Run(rest),
    "lint-content" => LintContentCommand.Run(rest),
    "balance" => BalanceCommand.Run(rest),
    "perf" => PerfCommand.Run(rest),
    "shot" => ShotCommand.Run(rest),
    "shot-diff" => ShotDiffCommand.Run(rest),
    "dump-ui" => DumpUiCommand.Run(rest),
    "build-order" => BuildOrderCommand.Run(rest),
    "war-metric" => WarMetricCommand.Run(rest),
    "steer" => SteerCommand.Run(rest),
    "pace" => PaceCommand.Run(rest),
    _ => HarnessOutput.Fail($"unknown subcommand '{args[0]}' — expected one of: {knownCommands}"),
};
