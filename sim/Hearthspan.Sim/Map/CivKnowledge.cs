using Hearthspan.Sim.Settlements;

namespace Hearthspan.Sim.Map;

/// <summary>Three-level fog (docs/phase-5-plan.md §8 decision D8, answered 28 July 2026: option (c)).
/// <see cref="Unknown"/> renders black and hides everything; <see cref="Remembered"/> renders terrain
/// and the <i>last-seen</i> settlement record, desaturated and possibly stale; <see cref="Visible"/>
/// is live truth.</summary>
public enum TileKnowledge : byte
{
    Unknown = 0,
    Remembered = 1,
    Visible = 2,
}

/// <summary>What a civ last saw at a settlement's tile. Kept separately from the live
/// <see cref="Settlement"/> precisely so a remembered record can be <i>wrong</i> — that staleness is
/// the entire point of the three-level model over the two-level one.</summary>
public readonly struct RememberedSettlement
{
    public readonly int SettlementId;
    public readonly HexCoord Location;
    public readonly SettlementTier Tier;
    public readonly int OwnerCivId;   // -1 = freehold
    public readonly int SeenOnTurn;

    public RememberedSettlement(int settlementId, HexCoord location, SettlementTier tier, int ownerCivId, int seenOnTurn)
    {
        SettlementId = settlementId;
        Location = location;
        Tier = tier;
        OwnerCivId = ownerCivId;
        SeenOnTurn = seenOnTurn;
    }
}

/// <summary>
/// One civ's knowledge of the map. <b>Every</b> civ has one, not just the player: D9 was answered
/// "AI is fogged too" (docs/phase-5-plan.md §8), which makes knowledge a sim input rather than a
/// presentation filter — <see cref="Civs.CivAi"/> may only settle toward land it has actually
/// explored. That in turn makes this state part of the determinism contract: it is rebuilt from the
/// same settlement list every turn in the same order, and it is folded into the state hash.
/// </summary>
public sealed class CivKnowledge
{
    private readonly TileKnowledge[] _tiles;
    private readonly Dictionary<int, RememberedSettlement> _remembered = new();

    public int Width { get; }
    public int Height { get; }

    public CivKnowledge(int width, int height)
    {
        Width = width;
        Height = height;
        _tiles = new TileKnowledge[width * height];
    }

    public TileKnowledge At(HexCoord tile) => _tiles[tile.Index(Width)];
    public TileKnowledge AtIndex(int index) => _tiles[index];
    public int TileCount => _tiles.Length;

    public bool IsExplored(HexCoord tile) => _tiles[tile.Index(Width)] != TileKnowledge.Unknown;
    public bool IsVisible(HexCoord tile) => _tiles[tile.Index(Width)] == TileKnowledge.Visible;

    public int ExploredTileCount
    {
        get
        {
            int n = 0;
            for (int i = 0; i < _tiles.Length; i++)
            {
                if (_tiles[i] != TileKnowledge.Unknown) n++;
            }
            return n;
        }
    }

    /// <summary>Remembered settlement records in ascending settlement-id order. The backing store is
    /// a Dictionary, so <b>never</b> enumerate it directly — hashing, the view snapshot and every
    /// AI read go through this sorted accessor (docs/development-plan.md rule 2: no dependence on
    /// Dictionary iteration order).</summary>
    public IReadOnlyList<RememberedSettlement> RememberedSettlementsById()
    {
        var list = new List<RememberedSettlement>(_remembered.Count);
        foreach (var kv in _remembered) list.Add(kv.Value);
        list.Sort((a, b) => a.SettlementId.CompareTo(b.SettlementId));
        return list;
    }

    public bool TryGetRemembered(int settlementId, out RememberedSettlement record) =>
        _remembered.TryGetValue(settlementId, out record);

    /// <summary>Demotes every currently-visible tile to remembered. Run at the start of each turn's
    /// vision pass; whatever is still in sight is immediately re-promoted by
    /// <see cref="ObserveFrom"/>, and whatever is not stays as a stale memory.</summary>
    public void DecayVisibleToRemembered()
    {
        for (int i = 0; i < _tiles.Length; i++)
        {
            if (_tiles[i] == TileKnowledge.Visible) _tiles[i] = TileKnowledge.Remembered;
        }
    }

    /// <summary>Marks tiles within <paramref name="radius"/> of <paramref name="origin"/> live.</summary>
    public void ObserveFrom(HexCoord origin, int radius) => Mark(origin, radius, TileKnowledge.Visible);

    /// <summary>Marks tiles within <paramref name="radius"/> explored but not live — the one-time
    /// survey a new settlement performs of its wider surroundings. Without this, Wave 0 would have no
    /// reachable "remembered" state at all: settlements never move, so a tile that is ever visible
    /// stays visible until the settlement providing sight demotes or dies. The survey ring is what
    /// gives every civ a stale-knowledge frontier from turn 1, which is exactly what D8 option (c)
    /// was chosen for. Wave 1's mobile units and razing make the state churn properly.</summary>
    public void Survey(HexCoord origin, int radius) => Mark(origin, radius, TileKnowledge.Remembered);

    private void Mark(HexCoord origin, int radius, TileKnowledge level)
    {
        int minRow = Math.Max(0, origin.Row - radius);
        int maxRow = Math.Min(Height - 1, origin.Row + radius);
        int minCol = Math.Max(0, origin.Col - radius - 1);
        int maxCol = Math.Min(Width - 1, origin.Col + radius + 1);

        for (int row = minRow; row <= maxRow; row++)
        {
            for (int col = minCol; col <= maxCol; col++)
            {
                var tile = new HexCoord(col, row);
                if (HexCoord.Distance(origin, tile) > radius) continue;
                int i = tile.Index(Width);
                if (_tiles[i] < level) _tiles[i] = level;
            }
        }
    }

    /// <summary>Records what a settlement looks like right now, overwriting any older memory of it.
    /// Only called for settlements standing on a currently-visible tile.</summary>
    public void RecordSighting(Settlement settlement, int turn)
    {
        _remembered[settlement.Id] = new RememberedSettlement(
            settlement.Id,
            settlement.Location,
            settlement.Tier,
            settlement.OwnerId ?? -1,
            turn);
    }

    /// <summary>Sight radius by tier — bigger places see further. Deliberately shorter than
    /// <see cref="InfluenceField.RadiusForTier"/> at every rung so a civ's border always extends past
    /// what it can actually watch, which is what makes a frontier a frontier.</summary>
    public static int VisionRadiusForTier(SettlementTier tier) => tier switch
    {
        SettlementTier.Ruins => 0,
        SettlementTier.Camp => 2,
        SettlementTier.Hamlet => 3,   // plan v5 D2 (was 2)
        SettlementTier.Village => 3,
        SettlementTier.Town => 4,     // plan v5 D2 (was 3)
        SettlementTier.City => 4,
        SettlementTier.Metropolis => 5,
        _ => 2,
    };

    /// <summary>One-time survey radius when a settlement is founded — wider than any tier's sight
    /// radius, so founding always leaves a ring of remembered-but-unwatched land around it.</summary>
    public const int FoundingSurveyRadius = 7;
}
