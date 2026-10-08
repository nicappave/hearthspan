namespace Hearthspan.Sim.Settlements;

/// <summary>
/// Ordered low-to-high. Ruins is a floor rung below Camp (docs/design-decisions.md decision 3:
/// razing/collapse leaves a scarred site, not a deleted entity) that a settlement can fall into and
/// later regrow from — never used for tiles that were never settled.
/// </summary>
public enum SettlementTier
{
    Ruins = 0,
    Camp = 1,
    Hamlet = 2,
    Village = 3,
    Town = 4,
    City = 5,
    Metropolis = 6,
}
