using Hearthspan.Content;
using Hearthspan.Content.Models;
using Hearthspan.Sim.Civs;
using Hearthspan.Sim.Economy;
using Hearthspan.Sim.Events;
using Hearthspan.Sim.Exploration;
using Hearthspan.Sim.Fixed;
using Hearthspan.Sim.Hashing;
using Hearthspan.Sim.Rng;
using Hearthspan.Sim.Settlements;
using Hearthspan.Sim.Map;
using Hearthspan.Sim.Units;
using Hearthspan.Sim.Verbs;
using Hearthspan.Sim.Victory;

namespace Hearthspan.Sim;

/// <summary>
/// The pure simulation root. Takes a seed and exposes Advance() — never references Godot, never
/// depends on wall-clock time or Dictionary/HashSet iteration order (docs/development-plan.md rules
/// 1-2). Everything the rendering layer needs comes out through <see cref="WorldView"/> snapshots.
/// </summary>
public sealed class World
{
    public HexGrid Grid { get; }
    public List<Settlement> Settlements { get; } = new();
    public List<Civilization> Civilizations { get; } = new();
    public ModifierLayer Modifiers { get; } = new();
    public RngStreams Rng { get; }
    public ulong Seed { get; }
    public int Turn { get; private set; }

    /// <summary>The influence / culture field (docs/design-decisions.md decision 2), added in Wave 0.
    /// Recomputed after settlements move population each turn; drives borders, the owner a newly
    /// emerged settlement is born under, and freehold absorption.</summary>
    public InfluenceField Influence { get; }

    /// <summary>Structured per-turn events (docs/phase-5-plan.md Wave 0). Folded into
    /// <see cref="StateHash"/> per D10.</summary>
    public EventLog Events { get; }

    public RoadNetwork Roads { get; } = new();
    public EdictState Edicts { get; } = new();

    /// <summary>Stage 4 (docs/units-design.md). Every live military unit — one per hex by construction
    /// (§2), never stacked. Sorted by Id wherever hashed/projected (rule 2).</summary>
    public List<Unit> Units { get; } = new();

    public WarState Wars { get; } = new();
    public PillageRaze PillageRaze { get; } = new();

    /// <summary>Plan v5 D5: ancient ruins scattered at world creation (civ worlds only).</summary>
    public Ruins RuinSites { get; } = new();

    /// <summary>Wave-0 victory scaffolding, extended in Stage 5 with the first winnable condition:
    /// Legacy still never completes (docs/design-decisions.md decision 4), but Wonder does — see
    /// <see cref="WonderCompletedByCivId"/> and <see cref="Victory.WonderVictory"/>.</summary>
    public IReadOnlyList<IVictoryPredicate> VictoryPredicates { get; } = new IVictoryPredicate[] { new LegacyVictory(), new WonderVictory() };

    /// <summary>Stage 5 (docs/content-design.md §4) — the id of the civ that completed the Great
    /// Lighthouse, null until someone does. Set exactly once, by
    /// <see cref="Settlements.ConstructionEngine.ProcessTurn"/> the instant a <c>wonder: true</c>
    /// building's construction is banked for the first time; every other civ's investment in the same
    /// building is refunded rather than allowed to complete a second time.</summary>
    public int? WonderCompletedByCivId { get; set; }

    /// <summary>Real tech/building definitions, optional — null keeps every pre-Phase-4 code path
    /// (goldens, balance envelope, the civCount>0 scale test) byte-for-byte unchanged, since research
    /// and construction only ever run when this is supplied (see <see cref="ResearchEngine"/>,
    /// <see cref="ConstructionEngine"/>).</summary>
    public ContentBundle? Content { get; }

    /// <summary>-1 when no civ is player-controlled (every existing civCount>0 test/spike). 0..civCount-1
    /// marks that civ's <see cref="Civilization.IsPlayerControlled"/> so <see cref="CivAi"/> leaves its
    /// research target and build queues to the UI instead of auto-picking (Phase 4 slice).</summary>
    public int PlayerCivIndex { get; }

    /// <summary>One-time fingerprint of the static terrain, folded into every turn's state hash so
    /// the two-process determinism check also covers mapgen, not just turn-to-turn simulation.</summary>
    public ulong GridFingerprint { get; }

    /// <summary>Plan v5 D2: every civ's treasury at world creation — enough for one founding charter
    /// on turn 0.</summary>
    public const int StartingGold = 30;

    /// <summary>Plan v5 D6: no civ may declare war before this turn.</summary>
    public const int WarGraceTurns = 30;

    private readonly Dictionary<int, CivKnowledge> _knowledge = new();
    private readonly Dictionary<int, SettlementTier> _lastTier = new();
    private readonly Dictionary<int, int> _lastTileOwner = new();
    private readonly List<PlayerCommand> _commands = new();
    private bool _replaying;
    private int _nextSettlementId = 1;
    private int _nextCivId = 1;
    private int _nextUnitId = 1;

    /// <summary>Every web-steering command the player has issued, in order. See
    /// <see cref="PlayerCommand"/> for why the verbs need this and research/build picks do not.</summary>
    public IReadOnlyList<PlayerCommand> Commands => _commands;

    private void Record(in PlayerCommand command)
    {
        if (!_replaying) _commands.Add(command);
    }

    /// <summary>Re-issues every command recorded for <paramref name="turn"/>. Called by
    /// <see cref="SaveGame.Load"/> between Advance() calls so each verb lands on the turn it
    /// originally did. Commands issued during replay are not re-recorded — the log being rebuilt is
    /// the one being replayed.
    ///
    /// <para>Returns how many commands <b>failed</b> to re-apply. A command that succeeded when it
    /// was issued and fails on replay means the replayed world has diverged from the saved one, and
    /// <see cref="SaveGame.Load"/> turns a non-zero count into a thrown error rather than handing
    /// back a world that quietly isn't the one that was saved. Second-channel rule: never trust the
    /// load's own claim of success.</para></summary>
    public int ReplayCommandsForTurn(IReadOnlyList<PlayerCommand> commands, int turn)
    {
        int failed = 0;
        _replaying = true;
        try
        {
            foreach (var command in commands)
            {
                if (command.Turn != turn) continue;
                if (Apply(command)) _commands.Add(command);
                else failed++;
            }
        }
        finally
        {
            _replaying = false;
        }
        return failed;
    }

    private bool Apply(in PlayerCommand command) => command.Type switch
    {
        PlayerCommandType.BuildRoad => TryBuildRoad(command.CivId, command.SettlementId, command.TargetId),
        PlayerCommandType.EnactEdict => TryEnactEdict(command.CivId, command.SettlementId, command.ContentId ?? ""),
        PlayerCommandType.RepealEdict => TryRepealEdict(command.CivId, command.TargetId),
        PlayerCommandType.GrantCharter => TryGrantCharter(command.CivId, command.SettlementId),
        PlayerCommandType.FoundCharter => TryFoundCharter(command.CivId, command.SettlementId, command.Tile),
        PlayerCommandType.TrainUnit => TryTrainUnit(command.CivId, command.SettlementId, command.ContentId ?? ""),
        PlayerCommandType.EnqueueBuilding => TryEnqueueBuilding(command.SettlementId, command.ContentId ?? ""),
        PlayerCommandType.MoveUnit => TryMoveUnit(command.CivId, command.UnitId, command.Tile),
        PlayerCommandType.GarrisonUnit => TryGarrisonUnit(command.CivId, command.UnitId),
        PlayerCommandType.RazeSettlement => TryRazeSettlement(command.CivId, command.UnitId),
        PlayerCommandType.DeclareWar => TryDeclareWar(command.CivId, command.TargetId),
        PlayerCommandType.DisbandUnit => TryDisbandUnit(command.CivId, command.UnitId),
        PlayerCommandType.SetUnitDestination => TrySetUnitDestination(command.CivId, command.UnitId, command.Tile),
        PlayerCommandType.SetResearch => TrySetResearchQueue(command.CivId, command.ContentId ?? ""),
        PlayerCommandType.MakePeace => TryMakePeace(command.CivId, command.TargetId),
        PlayerCommandType.CancelUnitOrder => TryCancelUnitOrder(command.CivId, command.UnitId),
        PlayerCommandType.BuyBuilding => TryBuyBuilding(command.CivId, command.SettlementId),
        _ => false,
    };

    /// <summary>Consecutive turns a freehold must sit inside one civ's borders before it is absorbed.
    /// A pure damping term: instant absorption would let the first civ to reach a region swallow every
    /// freehold in it the turn its border arrives, which is exactly decision 2's snowball. Wave 2 adds
    /// the post-founding resistance period on top of this.</summary>
    public const int FreeholdAbsorptionTurns = 12;

    /// <summary>civCount defaults to 0 — the original, golden-committed behavior. Passing civCount > 0
    /// opts into <see cref="CivAi"/> founding that many civilizations at construction time. content
    /// and playerCivIndex are Phase-4 opt-ins (see <see cref="Content"/>, <see cref="PlayerCivIndex"/>)
    /// — both default to "off" so every pre-Phase-4 caller is unaffected.</summary>
    public World(int width, int height, ulong seed, int civCount = 0, ContentBundle? content = null, int playerCivIndex = -1)
    {
        Seed = seed;
        Rng = new RngStreams(seed);
        Grid = MapGenerator.Generate(width, height, seed);
        GridFingerprint = StateHasher.HashGrid(Grid);
        Turn = 0;
        Content = content;
        PlayerCivIndex = playerCivIndex;
        Events = new EventLog(Rng);
        Influence = new InfluenceField(width, height, Math.Max(civCount, 1));

        if (civCount > 0)
        {
            CivAi.FoundCivilizations(this, civCount, playerCivIndex);
            RefreshCivSlots();
            foreach (var civ in Civilizations)
            {
                _knowledge[civ.Id] = new CivKnowledge(width, height);
                civ.Gold = Fixed64.FromInt(StartingGold);
            }
            PlaceStartingUnits();
            RuinSites.Place(Grid, Settlements, Rng.Get(RngStreamNames.Ruins));
            // Capitals survey and observe before turn 1 so a civ is never blind on the turn it acts.
            UpdateKnowledge(surveyNewlyFounded: Settlements);
            RecomputeInfluence();
            UpdateContacts();
        }
    }

    internal int AllocateSettlementId() => _nextSettlementId++;
    internal int AllocateCivId() => _nextCivId++;
    internal int AllocateUnitId() => _nextUnitId++;

    /// <summary>This civ's view of the map. Every civ has one (D9: the AI is fogged too), so this is
    /// sim input, not a presentation filter.</summary>
    public CivKnowledge? KnowledgeFor(int civId) => _knowledge.TryGetValue(civId, out var k) ? k : null;

    public Civilization? CivById(int civId)
    {
        foreach (var civ in Civilizations)
        {
            if (civ.Id == civId) return civ;
        }
        return null;
    }

    public Civilization? OwnerOf(Settlement settlement) =>
        settlement.OwnerId is int id ? CivById(id) : null;

    public UnitDef? UnitDefOf(Unit unit) => Content?.Units.Find(d => d.Id == unit.DefId);

    public void Advance()
    {
        Events.BeginTurn();
        Turn++;

        Edicts.ChargeUpkeep(Settlements, Civilizations);
        RefreshUnitMovement();
        ContinueUnitOrders();
        PillageRaze.ProcessTurn(Modifiers);

        // Stage 5 (docs/content-design.md §0): each civ's tech-derived Food/Production/Gold
        // multiplier, computed once per turn here — never inside the per-settlement loop below, per
        // the doc's own performance note — then looked up per settlement by owner.
        var civEconomyBonuses = ComputeCivEconomyBonuses();
        var income = new Dictionary<int, Fixed64>();

        foreach (var settlement in Settlements)
        {
            var tierBefore = settlement.Tier;
            var storedFoodBefore = settlement.Ledgers.StoredFood;
            int completedBefore = settlement.Build.CompletedBuildingIds.Count;

            var owner = OwnerOf(settlement);
            var civBonus = owner is not null && civEconomyBonuses.TryGetValue(owner.Id, out var bonus)
                ? bonus
                : CivEconomyBonus.None; // freehold, or an owner with no bonus computed — decision 2.

            EconomyEngine.ProcessTurn(settlement, Grid, Content, civBonus, owner);
            if (owner is not null) income[owner.Id] = income.GetValueOrDefault(owner.Id) + settlement.Ledgers.Gold.Produced;
            GrowthEngine.ProcessTurn(settlement, Grid, Modifiers);
            ConstructionEngine.ProcessTurn(settlement, this);
            // Plan v5 D13: content worlds only, so the content-free golden chain never sees it.
            if (Content is not null)
            {
                var tradeGoods = ConstructionEngine.CapBank(settlement, owner);
                if (owner is not null && tradeGoods > Fixed64.Zero) income[owner.Id] = income.GetValueOrDefault(owner.Id) + tradeGoods;
            }
            RuinsRecovery.ProcessTurn(settlement, Modifiers);

            ReportSettlementChanges(settlement, tierBefore, storedFoodBefore, completedBefore);
        }

        foreach (var civ in Civilizations)
        {
            civ.GoldPerTurn = income.GetValueOrDefault(civ.Id) - Fixed64.FromInt(Edicts.UpkeepFor(civ.Id));
        }

        if (Turn % TradeLinks.RefreshIntervalTurns == 0)
        {
            TradeLinks.Refresh(Settlements, Roads);
        }

        var settlementRng = Rng.Get(RngStreamNames.Settlement);
        var founded = SpawnEngine.TrySpawn(Grid, Settlements, Modifiers, settlementRng, Turn, ref _nextSettlementId);
        AssignBirthOwnership(founded);

        if (Civilizations.Count > 0)
        {
            CivAi.ProcessTurn(this);
            RecomputeInfluence();
            ProcessFreeholdAbsorption();
            UpdateKnowledge(surveyNewlyFounded: null);
            UpdateContacts();
        }
    }

    /// <summary>Every unit's movement budget resets to its definition's <c>Move</c> at the top of each
    /// turn (docs/units-design.md §3) — a no-op walk when <see cref="Content"/> is unset (no
    /// civCount>0 + content test/golden defines units, so this never touches pre-Stage-4 goldens) or a
    /// unit's definition has gone dangling (content edited out from under a save; leave it immobile
    /// rather than throw).</summary>
    private void RefreshUnitMovement()
    {
        if (Content is null) return;
        foreach (var unit in Units)
        {
            var def = Content.Units.Find(d => d.Id == unit.DefId);
            unit.MovementRemaining = def?.Move ?? 0;
        }
    }

    /// <summary>Plan v5 D4: every unit with a standing order walks toward it with this turn's fresh
    /// movement, in unit-id order. An order that can no longer be followed (the way is shut, the
    /// target became a friend, a peace was made) is cancelled and reported.</summary>
    private void ContinueUnitOrders()
    {
        if (Content is null) return;
        var ordered = new List<Unit>(Units);
        ordered.Sort((a, b) => a.Id.CompareTo(b.Id));
        foreach (var unit in ordered)
        {
            if (unit.Destination is not HexCoord destination) continue;
            if (!Units.Contains(unit)) continue; // lost in a fight earlier this loop
            FollowOrder(unit, destination);
        }
    }

    /// <summary>One turn's worth of walking toward a standing order. Returns the move's outcome.</summary>
    private MoveOutcome FollowOrder(Unit unit, HexCoord destination)
    {
        if (unit.Location.Equals(destination))
        {
            unit.Destination = null;
            return MoveOutcome.Moved;
        }
        var result = UnitMovement.TryMove(this, unit.OwnerId, unit.Id, destination);
        if (result.Outcome == MoveOutcome.Illegal)
        {
            unit.Destination = null;
            Events.Add(TurnEventType.UnitOrderCancelled, Turn, -1, unit.OwnerId, unit.Id);
            return result.Outcome;
        }
        ReportMoveOutcome(unit, result);
        if (!Units.Contains(unit) || unit.Location.Equals(destination) || result.Outcome != MoveOutcome.Moved)
        {
            unit.Destination = null;
        }
        return result.Outcome;
    }

    /// <summary>Stage 5 (docs/content-design.md §0): every civ's Food/Production/Gold tech multiplier
    /// for this turn, summed once over <see cref="Civs.TechState.CompletedTechIds"/> rather than
    /// per-settlement (the doc's own performance note — this would otherwise be an O(civs × settlements
    /// × techs) scan for no reason). Summing a <see cref="HashSet{T}"/> of tech ids into plain
    /// <c>int</c> percentages is order-independent (integer addition is commutative/exact — rule 2's
    /// no-iteration-order-dependence concern doesn't apply to a sum), so no sort is needed here the way
    /// hashed/projected output requires elsewhere. Empty when <see cref="Content"/> is unset, so every
    /// settlement's lookup below misses and falls back to <see cref="CivEconomyBonus.None"/> —
    /// byte-for-byte identical to pre-Stage-5 behavior on that path.</summary>
    /// <summary>Plan v5 M3: one civ's tech bonuses, for the UI's production and gold projections.</summary>
    public CivEconomyBonus EconomyBonusFor(int civId) =>
        ComputeCivEconomyBonuses().TryGetValue(civId, out var bonus) ? bonus : CivEconomyBonus.None;

    private Dictionary<int, CivEconomyBonus> ComputeCivEconomyBonuses()
    {
        var result = new Dictionary<int, CivEconomyBonus>();
        if (Content is null) return result;

        foreach (var civ in Civilizations)
        {
            int food = 0, production = 0, gold = 0;
            foreach (var techId in civ.Tech.CompletedTechIds)
            {
                var def = Content.Techs.Find(t => t.Id == techId);
                if (def is null) continue;
                food += def.FoodBonusPercent ?? 0;
                production += def.ProductionBonusPercent ?? 0;
                gold += def.GoldBonusPercent ?? 0;
            }
            result[civ.Id] = new CivEconomyBonus(food, production, gold);
        }
        return result;
    }

    /// <summary>Emits the settlement-scoped events for one settlement's turn. Tier transitions are
    /// diffed against the previous turn's tier rather than read off a stored flag, matching
    /// <see cref="TierLadder"/>'s "tier is never stored" rule — a demotion is as reportable as a
    /// promotion because both are just this turn's population landing in a different band.</summary>
    private void ReportSettlementChanges(Settlement settlement, SettlementTier tierBefore, Fixed64 storedFoodBefore, int completedBefore)
    {
        if (!_lastTier.TryGetValue(settlement.Id, out var known))
        {
            _lastTier[settlement.Id] = settlement.Tier;
            known = tierBefore;
        }

        var tierNow = settlement.Tier;
        if (tierNow != known)
        {
            Events.Add(
                tierNow > known ? TurnEventType.SettlementPromoted : TurnEventType.SettlementDemoted,
                Turn,
                settlement.Id,
                settlement.OwnerId ?? -1,
                (long)tierNow);
            _lastTier[settlement.Id] = tierNow;
        }

        // Starving is reported on the edge into deficit, not every turn it stays there — a settlement
        // that has been hungry for eighty turns should not drown the report panel.
        if (settlement.Ledgers.StoredFood < Fixed64.Zero && storedFoodBefore >= Fixed64.Zero)
        {
            Events.Add(TurnEventType.SettlementStarving, Turn, settlement.Id, settlement.OwnerId ?? -1,
                settlement.Ledgers.StoredFood.ToInt());
        }

        int completedNow = settlement.Build.CompletedBuildingIds.Count;
        for (int i = completedBefore; i < completedNow; i++)
        {
            Events.Add(TurnEventType.BuildingCompleted, Turn, settlement.Id, settlement.OwnerId ?? -1,
                magnitude: 0, contentId: settlement.Build.CompletedBuildingIds[i]);
        }
    }

    /// <summary>A settlement that emerges inside a civ's borders is born under that flag; one that
    /// emerges in open country is born a freehold (docs/design-decisions.md decision 2). This is the
    /// influence field's most visible consequence and the reason the owned:independent ratio is a
    /// Wave-0 envelope metric.</summary>
    private void AssignBirthOwnership(List<Settlement> founded)
    {
        foreach (var settlement in founded)
        {
            int owner = Civilizations.Count > 0 ? Influence.OwnerAt(settlement.Location) : -1;
            settlement.OwnerId = owner >= 0 ? owner : null;
            settlement.OriginCivId = settlement.OwnerId;
            _lastTier[settlement.Id] = settlement.Tier;

            Events.Add(TurnEventType.SettlementFounded, Turn, settlement.Id, owner, settlement.Population);
            if (owner < 0)
            {
                Events.Add(TurnEventType.FreeholdAppeared, Turn, settlement.Id, -1, settlement.Population);
            }
            else if (_knowledge.TryGetValue(owner, out var knowledge))
            {
                knowledge.Survey(settlement.Location, CivKnowledge.FoundingSurveyRadius);
            }
        }
    }

    /// <summary>Called by <see cref="CivAi"/> the moment it plants a settlement, so the founding is
    /// reported and the new site surveys its surroundings in the same turn it appears rather than a
    /// turn later. Kept as an explicit callback rather than a post-hoc diff of the settlement list:
    /// the AI already knows exactly what it founded, and re-deriving that by comparison would be both
    /// slower and a place for the two to drift apart.</summary>
    internal void OnCivFoundedSettlement(Settlement settlement, int civId)
    {
        _lastTier[settlement.Id] = settlement.Tier;
        Events.Add(TurnEventType.SettlementFounded, Turn, settlement.Id, civId, settlement.Population);
        if (_knowledge.TryGetValue(civId, out var knowledge))
        {
            knowledge.Survey(settlement.Location, CivKnowledge.FoundingSurveyRadius);
            knowledge.ObserveFrom(settlement.Location, CivKnowledge.VisionRadiusForTier(settlement.Tier));
        }
    }

    /// <summary>A freehold that sits inside one civ's borders for <see cref="FreeholdAbsorptionTurns"/>
    /// consecutive turns joins it. Pressure resets the moment the border moves off, so a contested
    /// frontier genuinely stalls absorption rather than merely slowing it.</summary>
    private void ProcessFreeholdAbsorption()
    {
        foreach (var settlement in Settlements)
        {
            if (settlement.OwnerId is not null) continue;

            int claimant = Influence.OwnerAt(settlement.Location);
            if (claimant < 0 || Influence.IsContestedAt(settlement.Location))
            {
                settlement.AbsorbingCivId = null;
                settlement.AbsorptionPressure = 0;
                continue;
            }

            if (settlement.AbsorbingCivId != claimant)
            {
                settlement.AbsorbingCivId = claimant;
                settlement.AbsorptionPressure = 0;
            }

            settlement.AbsorptionPressure++;
            if (settlement.AbsorptionPressure < FreeholdAbsorptionTurns) continue;

            settlement.OwnerId = claimant;
            settlement.AbsorbingCivId = null;
            settlement.AbsorptionPressure = 0;
            Events.Add(TurnEventType.FreeholdAbsorbed, Turn, settlement.Id, claimant, settlement.Population);
        }
    }

    private void RefreshCivSlots()
    {
        var slots = new List<(int, int)>(Civilizations.Count);
        foreach (var civ in Civilizations) slots.Add((civ.Index, civ.Id));
        Influence.SetCivSlots(slots);
    }

    private int SlotOfCiv(int civId)
    {
        foreach (var civ in Civilizations)
        {
            if (civ.Id == civId) return civ.Index;
        }
        return -1;
    }

    /// <summary>Recomputes the influence field and reports tiles that changed hands. Border-shift
    /// events are aggregated into one event per civ per turn rather than one per tile — a hundred
    /// tiles flipping is one thing happening, and an unaggregated version would swamp both the report
    /// panel and the events-per-turn envelope metric.</summary>
    private void RecomputeInfluence()
    {
        Influence.Recompute(Settlements, SlotOfCiv);

        var gained = new Dictionary<int, int>();
        for (int i = 0; i < Influence.OwnerCivId.Length; i++)
        {
            int now = Influence.OwnerCivId[i];
            int before = _lastTileOwner.TryGetValue(i, out int stored) ? stored : -1;
            if (now == before) continue;

            _lastTileOwner[i] = now;
            if (now >= 0) gained[now] = gained.GetValueOrDefault(now) + 1;
        }

        // Iterate civs, not the dictionary — no sim output may depend on Dictionary order (rule 2).
        foreach (var civ in Civilizations)
        {
            if (gained.TryGetValue(civ.Id, out int tiles) && tiles > 0)
            {
                Events.Add(TurnEventType.BorderShifted, Turn, -1, civ.Id, tiles);
            }
        }
    }

    /// <summary>Rebuilds every civ's fog. Currently-visible tiles decay to remembered first, then each
    /// civ's own settlements and (plan v5 D4) units re-light what they can still see — so losing sight
    /// of a region leaves a stale memory of it rather than erasing it (D8 option (c)).</summary>
    private void UpdateKnowledge(IReadOnlyList<Settlement>? surveyNewlyFounded)
    {
        foreach (var civ in Civilizations)
        {
            if (!_knowledge.TryGetValue(civ.Id, out var knowledge)) continue;
            knowledge.DecayVisibleToRemembered();

            foreach (var settlement in Settlements)
            {
                if (settlement.OwnerId != civ.Id) continue;
                knowledge.ObserveFrom(settlement.Location, CivKnowledge.VisionRadiusForTier(settlement.Tier));
            }

            foreach (var unit in Units)
            {
                if (unit.OwnerId != civ.Id) continue;
                knowledge.ObserveFrom(unit.Location, UnitDefOf(unit)?.Vision ?? 2);
            }

            if (surveyNewlyFounded is not null)
            {
                foreach (var settlement in surveyNewlyFounded)
                {
                    if (settlement.OwnerId != civ.Id) continue;
                    knowledge.Survey(settlement.Location, CivKnowledge.FoundingSurveyRadius);
                }
            }

            foreach (var settlement in Settlements)
            {
                if (knowledge.IsVisible(settlement.Location)) knowledge.RecordSighting(settlement, Turn);
            }
        }
    }

    /// <summary>Plan v5 D4: a unit that just stepped onto a tile sees around it at once — the map
    /// unfolds as the unit walks, not a turn later — and anything it sees is remembered and may be a
    /// first contact.</summary>
    internal void OnUnitStepped(Unit unit, UnitDef def)
    {
        if (!_knowledge.TryGetValue(unit.OwnerId, out var knowledge)) return;
        knowledge.ObserveFrom(unit.Location, def.Vision);
        foreach (var settlement in Settlements)
        {
            if (HexCoord.Distance(settlement.Location, unit.Location) > def.Vision) continue;
            knowledge.RecordSighting(settlement, Turn);
        }
        var civ = CivById(unit.OwnerId);
        if (civ is not null) UpdateContactsFor(civ, knowledge);
    }

    /// <summary>Plan v5 D6: first contact. A civ meets another the first time one of the other's
    /// settlements or units stands on a tile it can currently see. Meeting is mutual and reported once
    /// per side.</summary>
    private void UpdateContacts()
    {
        foreach (var civ in Civilizations)
        {
            if (_knowledge.TryGetValue(civ.Id, out var knowledge)) UpdateContactsFor(civ, knowledge);
        }
    }

    private void UpdateContactsFor(Civilization civ, CivKnowledge knowledge)
    {
        foreach (var settlement in Settlements)
        {
            if (settlement.OwnerId is not int other || other == civ.Id) continue;
            if (civ.MetCivIds.Contains(other)) continue;
            if (knowledge.IsVisible(settlement.Location)) Meet(civ, other);
        }
        foreach (var unit in Units)
        {
            if (unit.OwnerId == civ.Id || civ.MetCivIds.Contains(unit.OwnerId)) continue;
            if (knowledge.IsVisible(unit.Location)) Meet(civ, unit.OwnerId);
        }
    }

    private void Meet(Civilization civ, int otherCivId)
    {
        var other = CivById(otherCivId);
        if (other is null) return;
        civ.MetCivIds.Add(other.Id);
        other.MetCivIds.Add(civ.Id);
        Events.Add(TurnEventType.CivMet, Turn, -1, civ.Id, other.Id);
        Events.Add(TurnEventType.CivMet, Turn, -1, other.Id, civ.Id);
    }

    public bool HaveMet(int civA, int civB) => CivById(civA)?.MetCivIds.Contains(civB) ?? false;

    /// <summary>Plan v5 D4: every civ starts with a Warrior on its capital and a Scout on the first
    /// free passable tile beside it (neighbour order). Free, unrecorded — part of world creation, so
    /// every replay creates them identically.</summary>
    private void PlaceStartingUnits()
    {
        if (Content is null) return;
        var warrior = Content.Units.Find(u => u.Id == "warrior");
        var scout = Content.Units.Find(u => u.Id == "scout");
        foreach (var civ in Civilizations)
        {
            var capital = Settlements.Find(s => s.Id == civ.CapitalSettlementId);
            if (capital is null) continue;
            if (warrior is not null) SpawnUnit(warrior, civ.Id, capital.Location);
            if (scout is not null && FreeTileNear(capital.Location) is HexCoord tile) SpawnUnit(scout, civ.Id, tile);
        }
    }

    private Unit SpawnUnit(UnitDef def, int civId, HexCoord tile)
    {
        var unit = new Unit(AllocateUnitId(), def.Id, civId, tile, def.Strength, def.Move);
        Units.Add(unit);
        return unit;
    }

    /// <summary>The tile itself if free and passable, else the first free passable neighbour in
    /// <see cref="HexCoord.Neighbors"/> order, else null.</summary>
    private HexCoord? FreeTileNear(HexCoord tile)
    {
        if (TerrainRules.IsPassable(Grid, tile) && !Units.Exists(u => u.Location.Equals(tile))) return tile;
        foreach (var n in tile.Neighbors(Grid.Width, Grid.Height))
        {
            if (TerrainRules.IsPassable(Grid, n) && !Units.Exists(u => u.Location.Equals(n))) return n;
        }
        return null;
    }

    public WorldView Snapshot() => WorldView.From(this);

    /// <summary>Snapshot filtered to one civ's knowledge — what the player is allowed to see. Passing
    /// -1 (or a civ id with no knowledge state) returns the unfiltered omniscient view, which is what
    /// the harness, goldens and the balance runner use.</summary>
    public WorldView Snapshot(int observerCivId) => WorldView.From(this, observerCivId);

    public ulong StateHash() => StateHasher.Hash(this);

    /// <summary>Current Legacy standings, one per civ, in civ-index order.</summary>
    public IReadOnlyList<VictoryStanding> VictoryStandings()
    {
        var standings = new List<VictoryStanding>();
        foreach (var predicate in VictoryPredicates)
        {
            foreach (var civ in Civilizations)
            {
                standings.Add(predicate.Evaluate(this, civ));
            }
        }
        return standings;
    }

    // --- Research -------------------------------------------------------------------------------

    /// <summary>The one entry point the view layer (tech-tree UI) uses to change what a civ is
    /// researching. Plan v5 D7: an alias for <see cref="TrySetResearchQueue"/> — clicking an available
    /// tech queues a one-step path — so every research choice is a recorded command.</summary>
    public bool TrySetResearchTarget(int civId, string techId) => TrySetResearchQueue(civId, techId);

    /// <summary>Plan v5 D7: research <paramref name="techId"/>, queueing every unresearched
    /// prerequisite first. Recorded, so a save replays research exactly.</summary>
    public bool TrySetResearchQueue(int civId, string techId)
    {
        if (Content is null) return false;
        var civ = CivById(civId);
        if (civ is null || !ResearchEngine.TrySetQueue(civ, techId, Content)) return false;
        Record(new PlayerCommand(PlayerCommandType.SetResearch, Turn, civId, contentId: techId));
        return true;
    }

    /// <summary>Completes a tech immediately (a ruin's gift), reporting it like a researched one and
    /// dropping it from the civ's target/queue if it was waiting there.</summary>
    internal void CompleteTechFor(Civilization civ, string techId)
    {
        if (!civ.Tech.CompletedTechIds.Add(techId)) return;
        if (civ.Tech.ResearchTargetId == techId)
        {
            civ.Tech.ResearchTargetId = null;
            // Bank nothing lost: points already spent toward it stay banked for the next target.
            while (civ.Tech.ResearchQueue.Count > 0 && Content is not null)
            {
                var next = civ.Tech.ResearchQueue[0];
                civ.Tech.ResearchQueue.RemoveAt(0);
                if (ResearchEngine.IsAvailable(civ.Tech, next, Content)) { civ.Tech.ResearchTargetId = next; break; }
            }
        }
        else
        {
            civ.Tech.ResearchQueue.Remove(techId);
        }
        Events.Add(TurnEventType.TechCompleted, Turn, -1, civ.Id, 0, techId);
    }

    // --- Construction ---------------------------------------------------------------------------

    /// <summary>The one entry point the view layer (build view) uses to queue construction — validates
    /// the building exists, its tech (if any) is completed by the settlement's owner, and the settlement
    /// exists, against real content rather than trusting the caller.</summary>
    public bool TryEnqueueBuilding(int settlementId, string buildingId)
    {
        if (Content is null) return false;
        var settlement = Settlements.Find(s => s.Id == settlementId);
        if (settlement is null) return false;

        var def = Content.Buildings.Find(b => b.Id == buildingId);
        if (def is null) return false;

        if (def.RequiresTechId is not null)
        {
            var owner = OwnerOf(settlement);
            if (owner is null || !owner.Tech.IsCompleted(def.RequiresTechId)) return false;
        }
        if (BuildingProblem(settlement, def) is not null) return false;

        settlement.Build.Enqueue(buildingId);
        Record(new PlayerCommand(PlayerCommandType.EnqueueBuilding, Turn, settlement.OwnerId ?? -1, settlementId, contentId: buildingId));
        return true;
    }

    /// <summary>Plan v5 D13: why this building may not be queued here, or null when it may. A building
    /// is built once per settlement; a landmark (<c>hero</c>, not the world wonder) once per people —
    /// completed or queued anywhere of theirs.</summary>
    public string? BuildingProblem(Settlement settlement, Content.Models.BuildingDef def)
    {
        if (settlement.Build.CompletedBuildingIds.Contains(def.Id)) return "Already built here.";
        if (settlement.Build.Queue.Contains(def.Id)) return "Already queued here.";
        if (def.Hero && !def.Wonder && settlement.OwnerId is int ownerId)
        {
            foreach (var other in Settlements)
            {
                if (other.OwnerId != ownerId || other.Id == settlement.Id) continue;
                if (other.Build.CompletedBuildingIds.Contains(def.Id)) return $"A landmark: your people already have it (settlement {other.Id}).";
                if (other.Build.Queue.Contains(def.Id)) return $"A landmark: it is already being built (settlement {other.Id}).";
            }
        }
        return null;
    }

    /// <summary>Plan v5 D13: pays gold to finish the front of a settlement's queue at once — 2 gold for
    /// every production point still owed, at least 5; never the world wonder. Recorded.</summary>
    public bool TryBuyBuilding(int civId, int settlementId, bool record = true)
    {
        if (Content is null) return false;
        var settlement = Settlements.Find(s => s.Id == settlementId);
        if (settlement is null || settlement.OwnerId != civId) return false;
        var civ = CivById(civId);
        if (civ is null) return false;
        if (ConstructionEngine.BuyPrice(settlement, Content) is not Fixed64 price) return false;
        if (!civ.TrySpend(price)) return false;

        var def = ConstructionEngine.CompleteFront(settlement, this);
        Events.Add(TurnEventType.BuildingCompleted, Turn, settlement.Id, civId, 0, def.Id);
        if (record) Record(new PlayerCommand(PlayerCommandType.BuyBuilding, Turn, civId, settlementId));
        return true;
    }

    /// <summary>Removes a queued building. Index 0 is the in-progress item; cancelling it forfeits the
    /// production already invested, which is why the build view labels it as a cancel rather than a
    /// reorder.</summary>
    public bool TryCancelQueued(int settlementId, int queueIndex)
    {
        var settlement = Settlements.Find(s => s.Id == settlementId);
        if (settlement is null) return false;
        if (queueIndex < 0 || queueIndex >= settlement.Build.Queue.Count) return false;

        settlement.Build.Queue.RemoveAt(queueIndex);
        if (queueIndex == 0) settlement.Build.InvestedProduction = Fixed64.Zero;
        return true;
    }

    /// <summary>Moves a queued building one slot earlier or later. Invested production stays with
    /// slot 0 rather than with the item, per <see cref="BuildQueue.InvestedProduction"/>'s contract —
    /// so promoting an item into slot 0 inherits the partial progress already banked there.</summary>
    public bool TryReorderQueued(int settlementId, int queueIndex, int delta)
    {
        var settlement = Settlements.Find(s => s.Id == settlementId);
        if (settlement is null) return false;

        var queue = settlement.Build.Queue;
        int target = queueIndex + delta;
        if (queueIndex < 0 || queueIndex >= queue.Count) return false;
        if (target < 0 || target >= queue.Count) return false;

        (queue[queueIndex], queue[target]) = (queue[target], queue[queueIndex]);
        return true;
    }

    // --- Web verbs (decision 1), paid from the treasury (plan v5 D1) ---------------------------

    /// <summary>Roads verb (decision 1). Both endpoints must be owned by the civ and the path must be
    /// walkable land. Cost is paid from the civ's treasury; a road the treasury cannot afford is not
    /// built.</summary>
    public bool TryBuildRoad(int civId, int fromSettlementId, int toSettlementId, bool record = true)
    {
        var from = Settlements.Find(s => s.Id == fromSettlementId);
        var to = Settlements.Find(s => s.Id == toSettlementId);
        if (from is null || to is null) return false;
        if (from.OwnerId != civId || to.OwnerId != civId) return false;
        var civ = CivById(civId);
        if (civ is null) return false;

        var preview = CommandPreview.EvaluateBuildRoad(this, civId, fromSettlementId, toSettlementId);
        if (!preview.Legal) return false;

        var road = Roads.TryBuild(from, to, Grid, Modifiers, civId, Turn);
        if (road is null) return false;

        civ.TrySpend(Fixed64.FromInt(road.Tiles.Count) * RoadGoldPerTile);
        Events.Add(TurnEventType.RoadBuilt, Turn, from.Id, civId, road.Tiles.Count);
        if (record) Record(new PlayerCommand(PlayerCommandType.BuildRoad, Turn, civId, fromSettlementId, toSettlementId));
        return true;
    }

    public static readonly Fixed64 RoadGoldPerTile = Fixed64.FromInt(3);

    /// <summary>Edict verb (decision 1). Validates the definition exists, its tech is researched, and
    /// the settlement belongs to the civ; <see cref="EdictState.TryEnact"/> owns affordability.</summary>
    public bool TryEnactEdict(int civId, int settlementId, string edictId, bool record = true)
    {
        if (Content is null) return false;
        var settlement = Settlements.Find(s => s.Id == settlementId);
        if (settlement is null || settlement.OwnerId != civId) return false;

        var def = Content.Edicts.Find(e => e.Id == edictId);
        if (def is null) return false;

        var civ = CivById(civId);
        if (civ is null) return false;
        if (def.RequiresTechId is not null && !civ.Tech.IsCompleted(def.RequiresTechId)) return false;

        var instance = Edicts.TryEnact(def, settlement, civ, Grid, Modifiers, Turn);
        if (instance is null) return false;

        Events.Add(TurnEventType.EdictEnacted, Turn, settlementId, civId, def.Cost, def.Id);
        if (record) Record(new PlayerCommand(PlayerCommandType.EnactEdict, Turn, civId, settlementId, contentId: def.Id));
        return true;
    }

    public bool TryRepealEdict(int civId, int instanceId)
    {
        if (Content is null) return false;
        var instance = Edicts.Active.FirstOrDefault(e => e.InstanceId == instanceId);
        if (instance is null || instance.CivId != civId) return false;

        if (!Edicts.TryRepeal(instanceId, Content, Settlements, Grid, Modifiers)) return false;
        Events.Add(TurnEventType.EdictRepealed, Turn, instance.SettlementId, civId, 0, instance.EdictId);
        Record(new PlayerCommand(PlayerCommandType.RepealEdict, Turn, civId, instance.SettlementId, instanceId));
        return true;
    }

    /// <summary>Promotion charter (decision 1) — buys a settlement over a tier threshold it is
    /// already near, paid from the treasury.</summary>
    public bool TryGrantCharter(int civId, int settlementId)
    {
        var settlement = Settlements.Find(s => s.Id == settlementId);
        if (settlement is null || settlement.OwnerId != civId) return false;
        var civ = CivById(civId);
        if (civ is null) return false;

        var spent = Charters.TryGrantPromotion(settlement, civ);
        if (spent is not Fixed64 cost) return false;

        Events.Add(TurnEventType.CharterGranted, Turn, settlementId, civId, cost.ToInt());
        Record(new PlayerCommand(PlayerCommandType.GrantCharter, Turn, civId, settlementId));
        return true;
    }

    /// <summary>Founding charter (decision 1's deliberate "settle here" verb). The site must be
    /// explored, settleable land within <see cref="Charters.FoundingReach"/> of a settlement the civ
    /// owns (plan v5 D8). <paramref name="payingSettlementId"/> names the settlement the charter is
    /// issued from (it must be the civ's own); the gold comes from the treasury.</summary>
    public bool TryFoundCharter(int civId, int payingSettlementId, HexCoord tile, bool record = true)
    {
        var payer = Settlements.Find(s => s.Id == payingSettlementId);
        if (payer is null || payer.OwnerId != civId) return false;
        var civ = CivById(civId);
        if (civ is null) return false;
        var cost = Charters.FoundingCostFor(Charters.OwnedCount(Settlements, civId));
        if (!civ.CanAfford(cost)) return false;

        var knowledge = KnowledgeFor(civId);
        if (knowledge is null) return false;
        if (!Charters.CanFoundAt(tile, Grid, Settlements, knowledge, civId)) return false;

        civ.TrySpend(cost);

        var settlement = new Settlement(AllocateSettlementId(), tile, Charters.FoundingPopulation, Turn)
        {
            OwnerId = civId,
            OriginCivId = civId,
        };
        Settlements.Add(settlement);
        _lastTier[settlement.Id] = settlement.Tier;

        knowledge.Survey(tile, CivKnowledge.FoundingSurveyRadius);
        knowledge.ObserveFrom(tile, CivKnowledge.VisionRadiusForTier(settlement.Tier));

        Events.Add(TurnEventType.SettlementFounded, Turn, settlement.Id, civId, settlement.Population);
        Events.Add(TurnEventType.CharterGranted, Turn, settlement.Id, civId, cost.ToInt());
        if (record) Record(new PlayerCommand(PlayerCommandType.FoundCharter, Turn, civId, payingSettlementId, col: tile.Col, row: tile.Row));
        return true;
    }

    // --- Stage 4: units (docs/units-design.md) ---------------------------------------------------

    /// <summary>Trains a unit at a settlement for an immediate gold cost from the treasury —
    /// deliberately not queued against Production the way buildings are (see the design note in the
    /// Stage 4 review): a unit's id is referenced by every later Move/Garrison/Raze/Disband command,
    /// so its creation must itself be a recorded, replayed command. Plan v5: the new unit appears on
    /// the settlement's tile, or on the first free passable tile beside it when that is taken.
    ///
    /// <para><paramref name="record"/> defaults to true (the player-facing path). <see cref="Civs.CivAi"/>
    /// calls this with <c>record: false</c> — AI actions are already deterministically reproduced by
    /// replaying <see cref="Advance"/> itself; recording them too would replay them twice.</para></summary>
    public bool TryTrainUnit(int civId, int settlementId, string unitDefId, bool record = true)
    {
        if (Content is null) return false;
        var settlement = Settlements.Find(s => s.Id == settlementId);
        if (settlement is null || settlement.OwnerId != civId) return false;

        var def = Content.Units.Find(u => u.Id == unitDefId);
        if (def is null) return false;

        var civ = CivById(civId);
        if (civ is null) return false;
        if (def.RequiresTechId is not null && !civ.Tech.IsCompleted(def.RequiresTechId)) return false;

        if (FreeTileNear(settlement.Location) is not HexCoord tile) return false;
        if (!civ.TrySpend(Fixed64.FromInt(def.Cost))) return false;

        SpawnUnit(def, civId, tile);

        Events.Add(TurnEventType.UnitTrained, Turn, settlementId, civId, magnitude: 0, contentId: def.Id);
        if (record) Record(new PlayerCommand(PlayerCommandType.TrainUnit, Turn, civId, settlementId, contentId: def.Id));
        return true;
    }

    /// <summary>Move (§3-5) — pathfinds, spends this turn's movement, and resolves combat/pillage on
    /// arrival exactly as <see cref="UnitMovement.TryMove"/> describes. The event recorded depends on
    /// the outcome so the turn report can distinguish a quiet repositioning from a fight. Plan v5: a
    /// direct move replaces any standing order the unit had.
    /// <paramref name="record"/>: see <see cref="TryTrainUnit"/>'s doc comment — AI-issued moves pass
    /// false for the same reason.</summary>
    public bool TryMoveUnit(int civId, int unitId, HexCoord destination, bool record = true)
    {
        var unit = Units.Find(u => u.Id == unitId);
        if (unit is null || unit.OwnerId != civId) return false;

        var result = UnitMovement.TryMove(this, civId, unitId, destination);
        if (result.Outcome == MoveOutcome.Illegal) return false;
        unit.Destination = null;

        ReportMoveOutcome(unit, result);
        if (record) Record(new PlayerCommand(PlayerCommandType.MoveUnit, Turn, civId, col: destination.Col, row: destination.Row, unitId: unitId));
        return true;
    }

    private void ReportMoveOutcome(Unit unit, MoveResult result)
    {
        switch (result.Outcome)
        {
            case MoveOutcome.CombatLost:
                Events.Add(TurnEventType.UnitDestroyed, Turn, -1, unit.OwnerId, magnitude: unit.Id, contentId: unit.DefId);
                break;
            case MoveOutcome.CombatWon:
                Events.Add(TurnEventType.UnitDestroyed, Turn, -1, unit.OwnerId, magnitude: unit.Id);
                break;
            case MoveOutcome.Pillaged:
                var pillaged = Settlements.Find(s => s.Location.Equals(result.FinalLocation));
                Events.Add(TurnEventType.SettlementPillaged, Turn, pillaged?.Id ?? -1, unit.OwnerId);
                break;
        }
    }

    /// <summary>Plan v5 D4: orders a unit to a destination it may take several turns to reach. It walks
    /// as far as this turn's movement allows at once; <see cref="Advance"/> carries it on each turn
    /// after. Legal when the destination is a legal target at all
    /// (<see cref="UnitMovement.DestinationProblem"/>) and a route exists. Recorded.</summary>
    public bool TrySetUnitDestination(int civId, int unitId, HexCoord destination, bool record = true)
    {
        var unit = Units.Find(u => u.Id == unitId);
        if (unit is null || unit.OwnerId != civId) return false;
        var def = UnitDefOf(unit);
        if (def is null) return false;
        if (unit.Location.Equals(destination)) return false;
        if (UnitMovement.DestinationProblem(this, unit, def, destination) is not null) return false;
        if (UnitMovement.FindPath(this, unit, destination) is null) return false;

        if (record) Record(new PlayerCommand(PlayerCommandType.SetUnitDestination, Turn, civId, col: destination.Col, row: destination.Row, unitId: unitId));
        unit.Destination = destination;
        if (unit.IsGarrisoned) Garrison.Remove(unit, Grid, Modifiers);
        if (unit.MovementRemaining > 0) FollowOrder(unit, destination);
        return true;
    }

    /// <summary>Plan v5 M3: cancels a unit's standing move order so it stays where it is. Legal only
    /// while it has one. Recorded, like the order it cancels.</summary>
    public bool TryCancelUnitOrder(int civId, int unitId, bool record = true)
    {
        var unit = Units.Find(u => u.Id == unitId);
        if (unit is null || unit.OwnerId != civId || unit.Destination is null) return false;
        unit.Destination = null;
        if (record) Record(new PlayerCommand(PlayerCommandType.CancelUnitOrder, Turn, civId, unitId: unitId));
        return true;
    }

    /// <summary>Garrison (§6) — writes Safety into <see cref="ModifierLayer"/> at the unit's current
    /// hex. Refused if the unit is already garrisoned (idempotent no-op, not an error the player needs
    /// a reason for — re-issuing Garrison on an already-garrisoned unit is a harmless double-click).
    /// Plan v5: garrisoning cancels any standing order.
    /// <paramref name="record"/>: see <see cref="TryTrainUnit"/>'s doc comment.</summary>
    public bool TryGarrisonUnit(int civId, int unitId, bool record = true)
    {
        if (Content is null) return false;
        var unit = Units.Find(u => u.Id == unitId);
        if (unit is null || unit.OwnerId != civId || unit.IsGarrisoned) return false;

        var def = Content.Units.Find(d => d.Id == unit.DefId);
        if (def is null) return false;

        unit.Destination = null;
        Garrison.Apply(unit, def, Grid, Modifiers);
        if (record) Record(new PlayerCommand(PlayerCommandType.GarrisonUnit, Turn, civId, unitId: unitId));
        return true;
    }

    /// <summary>Raze (§7) — a deliberate second command, only legal while the razing unit already
    /// occupies an enemy settlement's own hex with no defending unit present (if there were one, it
    /// would already have had to be defeated in combat to get here). Applies the population cut and
    /// the raze-scale recovery clock; never automatic on arrival, unlike pillage.
    /// <paramref name="record"/>: see <see cref="TryTrainUnit"/>'s doc comment.</summary>
    public bool TryRazeSettlement(int civId, int unitId, bool record = true)
    {
        var unit = Units.Find(u => u.Id == unitId);
        if (unit is null || unit.OwnerId != civId) return false;

        var settlement = Settlements.Find(s => s.Location.Equals(unit.Location));
        if (settlement is null || settlement.OwnerId is not int ownerId || ownerId == civId) return false;
        if (!Wars.IsAtWar(civId, ownerId)) return false;
        if (Units.Exists(u => u.Id != unit.Id && u.Location.Equals(unit.Location))) return false;

        var tierBefore = settlement.Tier;
        PillageRaze.ApplyRaze(settlement, Modifiers);
        Wars.RecordFight(civId, ownerId, Turn);
        Events.Add(TurnEventType.SettlementRazed, Turn, settlement.Id, civId, magnitude: (long)tierBefore);
        if (record) Record(new PlayerCommand(PlayerCommandType.RazeSettlement, Turn, civId, unitId: unitId));
        return true;
    }

    /// <summary>Declares war (§8) — formalises <see cref="Wars"/> so combat/pillage/raze become legal
    /// between the two civs. Symmetric and idempotent. Plan v5 D6: only against a civ you have met,
    /// never before <see cref="WarGraceTurns"/>, never while a peace treaty holds.
    /// <paramref name="record"/>: see <see cref="TryTrainUnit"/>'s doc comment.</summary>
    public bool TryDeclareWar(int civId, int targetCivId, bool record = true)
    {
        if (CivById(civId) is null) return false;
        if (CivById(targetCivId) is null) return false;
        if (WarProblem(civId, targetCivId) is not null) return false;
        if (!Wars.Declare(civId, targetCivId, Turn)) return false;

        Events.Add(TurnEventType.WarDeclared, Turn, -1, civId, magnitude: targetCivId);
        if (record) Record(new PlayerCommand(PlayerCommandType.DeclareWar, Turn, civId, targetId: targetCivId));
        return true;
    }

    /// <summary>Why <paramref name="civId"/> may not declare war on <paramref name="targetCivId"/> now,
    /// or null when it may.</summary>
    public string? WarProblem(int civId, int targetCivId)
    {
        if (civId == targetCivId) return "That is you.";
        if (Wars.IsAtWar(civId, targetCivId)) return "You are already at war with them.";
        if (!HaveMet(civId, targetCivId)) return "You have not met them yet.";
        if (Turn < WarGraceTurns) return $"No one may declare war before turn {WarGraceTurns}.";
        int treaty = Wars.TreatyTurnsLeft(civId, targetCivId, Turn);
        if (treaty > 0) return $"Your peace treaty holds for {treaty} more turn{(treaty == 1 ? "" : "s")}.";
        return null;
    }

    /// <summary>Plan v5 D6: ends a war that has been quiet for <see cref="WarState.PeaceAfterQuietTurns"/>
    /// turns. Either side may make it. A standing order that would now attack becomes illegal and is
    /// cancelled the next time the unit tries to follow it.</summary>
    public bool TryMakePeace(int civId, int targetCivId, bool record = true)
    {
        if (CivById(civId) is null || CivById(targetCivId) is null) return false;
        if (!Wars.MakePeace(civId, targetCivId, Turn)) return false;
        Events.Add(TurnEventType.PeaceMade, Turn, -1, civId, targetCivId);
        if (record) Record(new PlayerCommand(PlayerCommandType.MakePeace, Turn, civId, targetId: targetCivId));
        return true;
    }

    /// <summary>Disbands a unit outright — the only way to shed a permanently weakened unit (§5: no
    /// strength regeneration) without waiting for it to die in a fight it might win.
    /// <paramref name="record"/>: see <see cref="TryTrainUnit"/>'s doc comment.</summary>
    public bool TryDisbandUnit(int civId, int unitId, bool record = true)
    {
        var unit = Units.Find(u => u.Id == unitId);
        if (unit is null || unit.OwnerId != civId) return false;

        if (unit.IsGarrisoned) Garrison.Remove(unit, Grid, Modifiers);
        Units.Remove(unit);
        if (record) Record(new PlayerCommand(PlayerCommandType.DisbandUnit, Turn, civId, unitId: unitId));
        return true;
    }
}
