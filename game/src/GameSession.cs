using System.Collections.Generic;
using System.Linq;
using Godot;
using Hearthspan.Content;
using Hearthspan.Content.Models;
using Hearthspan.Game.Hud;
using Hearthspan.Sim;
using Hearthspan.Sim.Civs;
using Hearthspan.Sim.Economy;
using Hearthspan.Sim.Events;
using Hearthspan.Sim.Fixed;
using Hearthspan.Sim.Map;
using Hearthspan.Sim.Settlements;
using Hearthspan.Sim.Verbs;

namespace Hearthspan.Game;

/// <summary>
/// Owns the live simulation for one play session (docs/development-plan.md Phase 4 — "the end-turn
/// loop"). <see cref="Hearthspan.Sim.World"/> is a plain C# object; holding it in-process here is not
/// a rule-4 violation — the view layer (MapView/TechTreeView/BuildView) still only ever reads a fresh
/// WorldView snapshot re-written to disk after every mutation (<see cref="RefreshAll"/>), never the
/// live World directly. That keeps every turn verifiable the same way a save file is: read the file,
/// don't trust the claim (the second-channel rule).
/// </summary>
public partial class GameSession : Node, IUiDumpable
{
    private World _world = null!;
    private ContentBundle _content = null!;
    private ContentIndex _contentIndex = null!;
    // Plan v3 task 3.9: session-tracked era-entry turn, so the displayed year advances smoothly across
    // an era's nominal span instead of jumping straight to its end the instant it's entered.
    private string _currentEraId = "";
    private int _eraEnteredTurn;
    private string _snapshotPath = null!;
    private string _savePath = null!;
    private int _playerCivIndex;

    private MapView _mapView = null!;
    private TechTreeView _techTree = null!;
    private BuildView _buildView = null!;
    private WorldViewDto? _lastSnapshot;
    private readonly ObjectivesState _objectivesState = new();
    private Label _toastLabel = null!;
    private readonly Queue<string> _pendingToasts = new();
    private bool _toastShowing;
    private MapLabels _mapLabels = null!;
    private SaveLoadPanel _saveLoadPanel = null!;
    private EndgameScreen _endgameScreen = null!;
    private PauseMenu _pauseMenu = null!;
    private DecisionPrompt _decisionPrompt = null!;
    private SelectionController _selectionController = null!;
    private CameraRig _camera = null!;
    private UnitView _unitView = null!;

    /// <summary>Which settlement the selection card is showing, or -1 (plan v5 M3: a unit or a tile
    /// can be selected instead — see GameSession.Hud.cs).</summary>
    private int _selectedSettlementId = -1;

    /// <summary>Which of the player's settlements the build view is showing. Defaults to the capital
    /// and follows the switcher from there.</summary>
    private int _buildViewSettlementId = -1;

    /// <summary>Stage 6 task 6: "unsaved progress since the last save", read by <see cref="PauseMenu"/>
    /// (via <c>IsDirtyCheck</c>) to decide whether Quit to Menu/Quit to Desktop needs to ask first. Set
    /// by <see cref="MarkDirty"/> at every real world-mutating action, cleared only by a *verified* save
    /// (<see cref="ReportSaveResult"/>) or a load.</summary>
    private bool _dirtySinceSave;

    // --- Stage 7 Part C (docs/stage-7-plan.md): the decision loop -------------------------------------

    /// <summary>Keys of decisions the player has "Not now"-ed this turn ("research", or
    /// "build:{settlementId}") — excluded until the next turn. Cleared at the start of every new turn.</summary>
    private readonly HashSet<string> _dismissedDecisionsThisTurn = new();

    /// <summary>"Stop asking this turn" — drains the whole queue rather than one key.</summary>
    private bool _suppressDecisionsThisTurn;

    /// <summary>Settlements below Village tier only ask for production (End Turn's "Choose production")
    /// once the player has queued something there themselves — camps and hamlets are the "steer, don't
    /// micromanage" tier.</summary>
    private readonly HashSet<int> _manuallyTouchedSettlementIds = new();

    public override void _Ready()
    {
        var contentRoot = ResolveContentRoot();
        _content = ContentLoader.Load(contentRoot);
        _contentIndex = new ContentIndex(_content);

        // Stage 6 task 5: --smoke is the exported build's shell-verifiable self-check (the governing
        // rule applied to packaging — CLAUDE.md, docs/development-plan-v2.md §6 Stage 6 task 5).
        // Bypasses every scene-tree node lookup below (TechTree/BuildView/SaveLoadPanel/etc.) — its
        // only real dependency is Hearthspan.Sim + the committed content bundle, so it stays correct
        // even if Main.tscn's node structure changes later, and it's cheap enough to run against a
        // freshly-exported release binary from a clean directory outside the repo.
        if (GreyBoxArgs.Get("smoke") != null)
        {
            RunSmokeAndExit();
            return;
        }

        // Stage 7 task B1 (docs/stage-7-plan.md): the shell-verifiable check the task itself decided on
        // — there's no game/-side test project to put a real unit test in, so this is a debug flag that
        // prints one JSON object and exits, same shape as --smoke just above. Needs nothing but
        // MapPalette.SettlementName itself, so it runs before content/world setup too.
        if (GreyBoxArgs.Get("debug-name-collision-check") != null)
        {
            RunNameCollisionCheckAndExit();
            return;
        }

        // Plan v3 task 1.4: confirms MapView.GroundHeight's hand-picked flat approximations for the two
        // new elevation bands (GrassHill, Stone) against what the real imported meshes measure, rather
        // than trusting the hand-picked numbers by eye — same one-JSON-line-and-quit shape as the other
        // debug flags above.
        if (GreyBoxArgs.Get("debug-ground-heights") != null)
        {
            RunGroundHeightsCheckAndExit();
            return;
        }

        // Plan v3 task 3.9: verifies EraClock's era-selection logic (not the year interpolation, which
        // needs a live turn/era-entered-turn pair a standalone check like this has no session to
        // supply) against a hand-picked completed-tech list, same one-JSON-line-and-quit shape as the
        // other debug flags above.
        if (GreyBoxArgs.Get("debug-era-check") != null)
        {
            RunEraCheckAndExit();
            return;
        }

        _snapshotPath = GreyBoxArgs.Get("worldview-out", System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hearthspan-session-worldview.json"))!;
        _savePath = GreyBoxArgs.Get("save-path", System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hearthspan-save.json"))!;
        // Stage 6: GameLaunchConfig first, GreyBoxArgs fallback — see that class's doc comment. Every
        // field below is independent, so a launch that set only (say) HasSeed still reads width/
        // height/civs/player-civ off GreyBoxArgs exactly as it always did; a launch that never touches
        // GameLaunchConfig at all (any pre-Stage-6 `Main.tscn`-direct invocation) sees every Has* flag
        // false and behaves byte-identically to before this class existed.
        _playerCivIndex = GameLaunchConfig.HasPlayerCivIndex ? GameLaunchConfig.PlayerCivIndex : GreyBoxArgs.GetInt("player-civ", 0);

        var loadPath = GameLaunchConfig.HasLoadPath ? GameLaunchConfig.LoadPath : GreyBoxArgs.Get("load");
        if (loadPath != null && System.IO.File.Exists(loadPath))
        {
            _world = SaveGame.Load(loadPath, _content);
            _playerCivIndex = _world.PlayerCivIndex;
            GD.Print($"GameSession: loaded save from {loadPath} at turn {_world.Turn}.");
        }
        else
        {
            int width = GameLaunchConfig.HasWidth ? GameLaunchConfig.Width : GreyBoxArgs.GetInt("width", 64);
            int height = GameLaunchConfig.HasHeight ? GameLaunchConfig.Height : GreyBoxArgs.GetInt("height", 40);
            ulong seed = GameLaunchConfig.HasSeed ? GameLaunchConfig.Seed : (ulong)GreyBoxArgs.GetInt("seed", 1234);
            // docs/development-plan.md Phase 4 scope: 2-3 rival civs -> 1 player + 3 rivals by default.
            int civCount = GameLaunchConfig.HasCivCount ? GameLaunchConfig.CivCount : GreyBoxArgs.GetInt("civs", 4);
            _world = new World(width, height, seed, civCount, _content, _playerCivIndex);
            GD.Print($"GameSession: new game, seed {seed}, {width}x{height}, {civCount} civs, player is civ index {_playerCivIndex}.");
        }

        // Plan v3 task 3.9: no session-tracked "entered this turn" survives a --load (SaveGame's DTO
        // doesn't carry it — see its own class doc on "only what replay can't reproduce is saved"), so
        // this approximates where in the current era's nominal span play already is, per the task's
        // own default: turn - 60 * order, clamped at 0. A fresh game's player is always at turn 0 with
        // no completed techs, so this is exactly turn 0 there too.
        var startingPlayer = PlayerCiv;
        _eraEnteredTurn = EraClock.ApproximateEraEnteredTurn(_content, startingPlayer.Tech.CompletedTechIds, _world.Turn);
        _currentEraId = EraClock.Compute(_content, startingPlayer.Tech.CompletedTechIds, _world.Turn, _eraEnteredTurn).EraId;

        _mapView = GetNode<MapView>("World");
        _mapView.HeroBuildingIds = _content.Buildings.Where(b => b.Hero).Select(b => b.Id).ToHashSet();
        _techTree = GetNode<TechTreeView>("UI/TechTree");
        _buildView = GetNode<BuildView>("UI/BuildView");
        _mapLabels = GetNode<MapLabels>("UI/MapLabels");
        _saveLoadPanel = GetNode<SaveLoadPanel>("UI/SaveLoad");
        _saveLoadPanel.SaveRequested = HandleSaveToSlot;
        _saveLoadPanel.LoadRequested = HandleLoadFromSlotName;
        _endgameScreen = GetNode<EndgameScreen>("UI/Endgame");
        _endgameScreen.MenuRequested = () => { _endgameScreen.Visible = false; OpenPauseMenu(); };
        // Stage 6 task 6: the pause menu reuses the same _saveLoadPanel above for Save/Load (no second
        // dialog) — see PauseMenu's own doc comment for the full reuse contract.
        _pauseMenu = GetNode<PauseMenu>("UI/PauseMenu");
        _pauseMenu.IsDirtyCheck = () => _dirtySinceSave;
        _pauseMenu.SaveClicked = () => _saveLoadPanel.Open(SaveLoadPanel.PanelMode.Save);
        _pauseMenu.LoadClicked = () => _saveLoadPanel.Open(SaveLoadPanel.PanelMode.Load);
        _pauseMenu.QuitToMenuRequested = HandleQuitToMenu;
        _pauseMenu.QuitToDesktopRequested = HandleQuitToDesktop;
        _decisionPrompt = GetNode<DecisionPrompt>("UI/DecisionPrompt");
        _decisionPrompt.RequestRebuild = () => { RefreshAll(); MarkDirty(); };
        _decisionPrompt.DismissRequested = key => { _dismissedDecisionsThisTurn.Add(key); _researchPromptRequested = false; };
        _decisionPrompt.StopAskingRequested = () => { _suppressDecisionsThisTurn = true; _researchPromptRequested = false; };

        _selectionController = GetNode<SelectionController>("World/Selection");
        _camera = GetNode<CameraRig>("World/Camera");
        _unitView = GetNode<UnitView>("World/Units");
        _mapLabels.Bind(_mapView, _camera);
        _mapLabels.BindUnits(_unitView);
        _mapView.Bind(_camera);
        // Stage 7 task 6 (docs/stage-7-plan.md, defect L): the opening shot lands on the player's
        // capital, not the whole map pulled back. Runs identically for a new game and a --load.
        _buildViewSettlementId = PlayerCiv.CapitalSettlementId;
        var startCapital = PlayerCapital;
        _camera.FrameStart(_world.Grid.Width, _world.Grid.Height, startCapital.Location.Col, startCapital.Location.Row);

        _techTree.TechClicked += OnTechClicked;
        _buildView.BuildingQueued += OnBuildingQueued;
        _buildView.QueueItemCancelled += OnQueueItemCancelled;
        _buildView.QueueItemMoved += OnQueueItemMoved;
        _buildView.SettlementSelected += OnSettlementSelected;
        _selectionController.SettlementSelected += OnMapSettlementSelected;
        _selectionController.HexClicked += OnHexClicked;
        _selectionController.HexRightClicked += OnHexRightClicked;
        _selectionController.HexHovered += OnHexHovered;
        _selectionController.RoadDragHover += OnRoadDragHover;
        _selectionController.RoadDragCompleted += OnRoadDragCompleted;

        AddChild(new Audio.Sfx { Name = "Sfx" });
        BuildHud();

        // --overlay growth: the headless-drivable equivalent of pressing V, set before the first
        // RefreshAll below so a --shot-out taken this same launch already shows it.
        if (GreyBoxArgs.Get("overlay") == "growth")
        {
            _overlayOn = true;
            _mapView.Overlay = OverlayMode.Growth;
            _overlayLegend.Visible = true;
        }

        RefreshAll();

        // Stage 7 task 6: the opening frame shows the capital selected — "where am I and what can I do
        // here" as one screen. An explicit --select-settlement/--select-unit below overrides it.
        _selectionController.SelectSettlement(startCapital.Location.Col, startCapital.Location.Row);

        // --load-slot <name>: the headless-drivable equivalent of clicking a slot in the Load dialog
        // (SaveLoadPanel), running through the exact same HandleLoadFromSlotName every real click uses —
        // not a parallel shortcut. Runs here, before --research/--build/--auto-turns/etc. below, so a
        // command line that combines --load-slot with those flags acts on the freshly loaded world, the
        // same order a player would actually do it in (load, then act), not the discarded fresh-game one.
        var loadSlotArg = GreyBoxArgs.Get("load-slot");
        if (loadSlotArg != null)
        {
            HandleLoadFromSlotName(loadSlotArg);
        }

        // --research/--build/--auto-turns: headless-drivable equivalents of the tech-tree click and
        // build-picker click, so the whole end-turn/research/construction loop is verifiable from a
        // shell (the governing rule) without simulating real mouse input.
        // FABLE STOP 2: both flags accept a comma-separated chain. --research sets the next id in the
        // chain whenever the player's research target is empty (each id must be available at that point;
        // an unavailable id is skipped with a stderr line); --build enqueues the next id at the capital
        // whenever its queue is empty. One launch can therefore reach a multi-tech state and several
        // completed landmarks, which the single-id form never could (Phase 2 handover, "Found, not fixed").
        var researchChain = new Queue<string>((GreyBoxArgs.Get("research") ?? "")
            .Split(',', System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries));
        var buildChain = new Queue<string>((GreyBoxArgs.Get("build") ?? "")
            .Split(',', System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries));

        void AdvanceChains()
        {
            while (researchChain.Count > 0 && PlayerCiv.Tech.ResearchTargetId == null)
            {
                string next = researchChain.Dequeue();
                if (!_world.TrySetResearchTarget(PlayerCiv.Id, next))
                    GD.PrintErr($"GameSession: --research '{next}' is not available at turn {_world.Turn}; skipped.");
            }
            while (buildChain.Count > 0 && PlayerCapital.Build.Queue.Count == 0)
            {
                string next = buildChain.Dequeue();
                if (!_world.TryEnqueueBuilding(PlayerCapital.Id, next))
                    GD.PrintErr($"GameSession: --build '{next}' could not be queued at turn {_world.Turn}; skipped.");
            }
        }

        AdvanceChains();
        int autoTurns = GreyBoxArgs.GetInt("auto-turns", 0);
        if (autoTurns == 0 && (GreyBoxArgs.Get("research") is not null || GreyBoxArgs.Get("build") is not null)) RefreshAll();
        for (int t = 0; t < autoTurns; t++)
        {
            _world.Advance();
            _skippedUnitsThisTurn.Clear();
            AdvanceChains();
            RunAutoExplore();
            ProcessNewEvents();
        }
        if (autoTurns > 0)
        {
            RefreshAll();
            MarkDirty();
        }

        // Headless verification convenience: jump straight to a panel for shot/dump-ui checks.
        // "save"/"load" (Stage 6 task 2) open SaveLoadPanel directly — the same open a real Save/Load
        // topbar click performs — so its slot list (with real timestamps) is shot/dump-ui verifiable
        // without simulating a mouse click.
        switch (GreyBoxArgs.Get("show-panel"))
        {
            case "techtree": ShowPanel(_techTree); break;
            case "buildview": ShowPanel(_buildView); break;
            case "save": _saveLoadPanel.Open(SaveLoadPanel.PanelMode.Save); break;
            case "load": _saveLoadPanel.Open(SaveLoadPanel.PanelMode.Load); break;
            // Stage 6 task 6: same convenience as "save"/"load" just above — opens the exact panel the
            // topbar "Menu" button/Esc key opens, for a shot/dump-ui check without simulating input.
            case "pause": OpenPauseMenu(); break;
        }

        // --select-settlement <id>: the headless-drivable equivalent of clicking a settlement, so the
        // selection panel (verb offers, pricing) AND the 3D map highlight (task G1) are dump-ui/shot
        // verifiable without simulating a real mouse click through Godot's input system. Routed through
        // SelectionController.SelectSettlement (same as FocusAndSelectSettlement) rather than calling
        // OnMapSettlementSelected directly, so this is what a real click actually does, not a
        // parallel shortcut that happens to update the same panel state.
        var selectArg = GreyBoxArgs.Get("select-settlement");
        if (selectArg != null && int.TryParse(selectArg, out int selectId))
        {
            var selectSettlement = _world.Settlements.Find(s => s.Id == selectId);
            if (selectSettlement is not null) _selectionController.SelectSettlement(selectSettlement.Location.Col, selectSettlement.Location.Row);
        }

        // --build-road-test <from>:<to>: the headless-drivable equivalent of a completed drag, so the
        // road-dragging commit path (Stage 2 task 3) is verifiable the same way, without simulating
        // real mouse-down/motion/mouse-up events through Godot's input system.
        var roadTestArg = GreyBoxArgs.Get("build-road-test");
        if (roadTestArg != null)
        {
            var parts = roadTestArg.Split(':');
            if (parts.Length == 2 && int.TryParse(parts[0], out int fromId) && int.TryParse(parts[1], out int toId))
            {
                OnRoadDragCompleted(fromId, toId);
            }
        }

        // Stage 4 headless-drivable equivalents of unit orders (docs/units-design.md), same pattern as
        // --select-settlement/--build-road-test above — the governing rule applies to units too.
        var trainArg = GreyBoxArgs.Get("train-unit"); // <settlementId>:<unitDefId>
        if (trainArg != null)
        {
            var parts = trainArg.Split(':');
            if (parts.Length == 2 && int.TryParse(parts[0], out int trainSettlementId))
            {
                if (_world.TryTrainUnit(PlayerCiv.Id, trainSettlementId, parts[1])) { RefreshAll(); MarkDirty(); }
            }
        }

        var moveArg = GreyBoxArgs.Get("move-unit"); // <unitId>:<col>:<row>
        if (moveArg != null)
        {
            var parts = moveArg.Split(':');
            if (parts.Length == 3 && int.TryParse(parts[0], out int moveUnitId)
                && int.TryParse(parts[1], out int moveCol) && int.TryParse(parts[2], out int moveRow))
            {
                if (_world.TryMoveUnit(PlayerCiv.Id, moveUnitId, new Hearthspan.Sim.Map.HexCoord(moveCol, moveRow))) { RefreshAll(); MarkDirty(); }
            }
        }

        var garrisonArg = GreyBoxArgs.Get("garrison-unit"); // <unitId>
        if (garrisonArg != null && int.TryParse(garrisonArg, out int garrisonUnitId))
        {
            if (_world.TryGarrisonUnit(PlayerCiv.Id, garrisonUnitId)) { RefreshAll(); MarkDirty(); }
        }

        var declareWarArg = GreyBoxArgs.Get("declare-war"); // <targetCivId>
        if (declareWarArg != null && int.TryParse(declareWarArg, out int declareWarTargetId))
        {
            if (_world.TryDeclareWar(PlayerCiv.Id, declareWarTargetId)) { RefreshAll(); MarkDirty(); }
        }

        // Plan v5 M3: headless-drivable equivalents of the new map interactions, each routed through
        // the exact handler a real click uses (a shell cannot synthesize a click that reaches
        // _UnhandledInput — CLAUDE.md trap list, Stage 7).
        //   --select-unit <id>            left-click on a unit
        //   --explore-unit <id>           the unit card's Explore button
        //   --unit-dest <id>:<col>:<row>  select the unit, then right-click the tile
        //   --select-tile <col>:<row>     left-click on bare ground
        //   --found-mode                  the settlement card's "Found settlement" button
        //   --hover <col>:<row>           the cursor resting on a tile (route preview, hover tip)
        //   --card-action <label>         press the selection card's button with that exact label
        //   --card-action-first <label>   the same, pressed before --end-turn (e.g. Garrison, then end turn)
        //   --end-turn <n>                Shift+Enter n times (end the turn whatever is waiting)
        //   --press-end-turn <n>          press the smart End Turn button n times
        //   --do "step;step;..."           a click sequence, in order (see RunScript)
        if (GreyBoxArgs.Get("explore-unit") is string exploreArg && int.TryParse(exploreArg, out int exploreId))
        {
            SelectUnit(exploreId);
            _card.DebugPress("Explore");
        }
        if (GreyBoxArgs.Get("unit-dest") is string destArg)
        {
            var parts = destArg.Split(':');
            if (parts.Length == 3 && int.TryParse(parts[0], out int destUnit) && int.TryParse(parts[1], out int destCol) && int.TryParse(parts[2], out int destRow))
            {
                SelectUnit(destUnit);
                OnHexRightClicked(destCol, destRow);
            }
        }
        if (GreyBoxArgs.Get("card-action-first") is string firstAction)
        {
            GD.Print($"GameSession: --card-action-first \"{firstAction}\" -> pressed={_card.DebugPress(firstAction)}");
        }
        // --end-turn <n>: Shift+Enter n times — the turn ends regardless of what is waiting.
        if (GreyBoxArgs.Get("end-turn") is string endArg && int.TryParse(endArg, out int endTurns))
        {
            for (int i = 0; i < endTurns; i++) CommitEndTurn();
        }
        if (GreyBoxArgs.Get("press-end-turn") is string pressArg && int.TryParse(pressArg, out int presses))
        {
            for (int i = 0; i < presses; i++)
            {
                GD.Print($"GameSession: --press-end-turn {i + 1}: {_endTurn.DumpUiDigest()}");
                OnEndTurnPressed();
            }
        }
        if (GreyBoxArgs.Get("select-unit") is string selUnitArg && int.TryParse(selUnitArg, out int selUnit)) SelectUnit(selUnit);
        if (GreyBoxArgs.Get("select-tile") is string selTileArg)
        {
            var parts = selTileArg.Split(':');
            if (parts.Length == 2 && int.TryParse(parts[0], out int tc) && int.TryParse(parts[1], out int tr)) SelectTile(new Hearthspan.Sim.Map.HexCoord(tc, tr));
        }
        if (GreyBoxArgs.Get("found-mode") != null) EnterFoundingMode();
        if (GreyBoxArgs.Get("card-action") is string cardAction)
        {
            GD.Print($"GameSession: --card-action \"{cardAction}\" -> pressed={_card.DebugPress(cardAction)}");
        }
        if (GreyBoxArgs.Get("do") is string script) RunScript(script);
        if (GreyBoxArgs.Get("hover") is string hoverArg)
        {
            var parts = hoverArg.Split(':');
            if (parts.Length == 2 && int.TryParse(parts[0], out int hc) && int.TryParse(parts[1], out int hr))
            {
                _selectionController.HoverPinned = true;
                OnHexHovered(hc, hr);
            }
        }

        // Stage 6 task 3: two verification-only triggers for EndgameScreen, following the exact same
        // "headless-drivable equivalent" pattern as every debug flag above (--train-unit, --declare-war,
        // etc.) — none of these are reachable except by launching the process with this specific
        // `--` flag on the command line, which a normal packaged play session never does; that is the
        // same level of protection every other GreyBoxArgs debug flag in this file already relies on,
        // not a new exception. Neither flag is recorded into World.Commands (unlike a real verb), so
        // neither one is replay/save-visible — they exist purely to force a screen state for `shot`/
        // `dump-ui` to capture, per CLAUDE.md's governing rule.
        //
        // --debug-force-wonder <civId>: sets World.WonderCompletedByCivId directly — the exact field
        // WonderVictory reads and Settlements.ConstructionEngine.ProcessTurn sets during real play.
        // Pass the player's own civ id for the "you won" state, or any other civ's id for "someone
        // else won".
        var debugForceWonderArg = GreyBoxArgs.Get("debug-force-wonder");
        if (debugForceWonderArg != null && int.TryParse(debugForceWonderArg, out int debugWonderCivId))
        {
            _world.WonderCompletedByCivId = debugWonderCivId;
            RefreshAll();
            MarkDirty();
        }

        // --debug-clear-player-settlements: reassigns every settlement the player owns to independent
        // (OwnerId = null) — the same field World.cs's own conquest/claim code (TryFoundCharter et al.)
        // already mutates directly, so "the player owns zero settlements" becomes true the same way a
        // real, total military collapse would leave it, for the "you are defeated" state.
        if (GreyBoxArgs.Get("debug-clear-player-settlements") != null)
        {
            foreach (var s in _world.Settlements.Where(s => s.OwnerId == PlayerCiv.Id).ToList())
            {
                s.OwnerId = null;
            }
            RefreshAll();
            MarkDirty();
        }

        // Task C1 (docs/stage-7-plan.md): the two decision-loop flags. Both read/act on the queue
        // RefreshAll already rebuilt above (_lastDecisionQueue) rather than recomputing it, so a
        // --decide immediately after --research/--build/--auto-turns sees the same queue --decisions
        // would have printed for that identical command line.
        //
        // --decisions: print the pending decision queue and exit — nothing to commit, just inspect.
        if (GreyBoxArgs.Get("decisions") != null)
        {
            GD.Print(_decisionPrompt.DumpUiDigest());
            GetTree().Quit(0);
            return;
        }

        // --decide <index>: commit option <index> of the FRONT decision, through the exact same
        // Commit a click on that button would run (DecisionPrompt.Present wires Commit the same way
        // regardless of who invokes it) — no parallel shortcut, no bypass of the real mutation path.
        var decideArg = GreyBoxArgs.Get("decide");
        if (decideArg != null && int.TryParse(decideArg, out int decideIndex))
        {
            if (_lastDecisionQueue.Count > 0 && decideIndex >= 0 && decideIndex < _lastDecisionQueue[0].Options.Count)
            {
                _lastDecisionQueue[0].Options[decideIndex].Commit();
                RefreshAll();
                MarkDirty();
            }
            else
            {
                GD.Print($"GameSession: --decide {decideIndex} out of range (queue={_lastDecisionQueue.Count}, " +
                          $"options={(_lastDecisionQueue.Count > 0 ? _lastDecisionQueue[0].Options.Count : 0)}).");
            }
        }

        // Stage 5 playthrough bugfix (docs/review/stage-5/): --save-now must run LAST, after every
        // other mutation flag above — not right after --auto-turns/--build, where it sat before. Roads
        // and unit orders (--build-road-test/--train-unit/--move-unit/--garrison-unit/--declare-war) are
        // recorded into world.Commands (see PlayerCommand.cs), and world.Commands is exactly what
        // SaveGame.Save serializes for replay — so a save written before those handlers ran silently
        // dropped them from the file: the save's own "GameSession: saved to ..." print was true (a file
        // did land) but useless (that command was gone forever the moment this process exited), the
        // exact "true but useless success report" shape CLAUDE.md's trap list warns about. Confirmed by
        // reproduction: `--declare-war 2 --save-now` in one launch left `Commands: []` in the written
        // JSON even though the live world (read back the same launch via --dump-ui-out) showed
        // `wars=1` — a load from that file would have silently lost the war declaration, and the same
        // was true for trained/moved/garrisoned units, which have no other persistence path at all
        // (SaveDto carries no Units array; units are reconstructed purely by replaying TrainUnit/
        // MoveUnit/GarrisonUnit commands on load). Research/build are unaffected by this bug either way
        // — TrySetResearchTarget/TryEnqueueBuilding aren't recorded commands; OnSave() reads
        // civ.Tech.ResearchTargetId and settlement.Build.Queue directly, and both were already set
        // earlier in this same method, before the old save-now call site too.
        if (GreyBoxArgs.Get("save-now") != null)
        {
            OnSave();
        }

        // --save-slot <name>: the headless-drivable equivalent of typing a name into the Save dialog
        // (SaveLoadPanel) and pressing Save — same ordering rule as --save-now just above (must run
        // last, for the same reason: it must see every command the flags above recorded), and it goes
        // through the exact same HandleSaveToSlot every real click uses, including the second-channel
        // write/reload/hash-compare (SaveSlots.SaveAndVerify) — no parallel shortcut, no swallowed
        // mismatch. Always overwrites without a confirmation prompt, matching --save-now's existing
        // unconditional-overwrite behavior for a scripted, deliberate headless action (a real player's
        // click still goes through SaveLoadPanel's confirm-overwrite dialog).
        var saveSlotArg = GreyBoxArgs.Get("save-slot");
        if (saveSlotArg != null)
        {
            HandleSaveToSlot(saveSlotArg);
        }

        // --save-overwrite-test <name>: a headless-drivable check of the confirm-overwrite path
        // specifically (the governing rule applied to a popup dialog, not just the save/load round
        // trip) — see SaveLoadPanel.DebugTriggerSaveAction's doc comment. Prints whether the overwrite
        // confirmation dialog showed, and leaves it open (Visible) for a follow-up --shot-out to also
        // confirm visually.
        var overwriteTestArg = GreyBoxArgs.Get("save-overwrite-test");
        if (overwriteTestArg != null)
        {
            bool showedConfirm = _saveLoadPanel.DebugTriggerSaveAction(overwriteTestArg);
            GD.Print($"GameSession: --save-overwrite-test '{overwriteTestArg}' -> " +
                      $"overwriteConfirmVisible={showedConfirm} (slotExists={SaveSlots.Exists(overwriteTestArg)}).");
        }

        // --quit-warning-test <menu|desktop>: a headless-drivable check of the quit-without-saving
        // warning (Stage 6 task 6, governing rule applied the same way --save-overwrite-test applies it
        // to the confirm-overwrite popup) — routed through PauseMenu.DebugTriggerQuit, which calls the
        // exact same RequestQuit a real "Quit to Menu"/"Quit to Desktop" click invokes. If
        // _dirtySinceSave is false at this point (no mutating flag ran earlier on this same command
        // line, and no --save-now/--save-slot cleared it), the real quit action fires immediately —
        // GetTree().Quit(0) for "desktop", ChangeSceneToFile for "menu" — same as a real click with
        // nothing unsaved would do, not a simulated no-op.
        var quitTestArg = GreyBoxArgs.Get("quit-warning-test");
        if (quitTestArg != null)
        {
            bool showedConfirm = _pauseMenu.DebugTriggerQuit(quitTestArg);
            GD.Print($"GameSession: --quit-warning-test '{quitTestArg}' -> " +
                      $"dirtySinceSave={_dirtySinceSave} quitWarningVisible={showedConfirm}.");
        }

        // Headless verification convenience: exit once the requested --auto-turns/--research/--build
        // sequence has run, instead of sitting in the live loop waiting for input that never comes.
        // Skipped when --shot-out/--dump-ui-out are also set — AppRunner (the autoload) owns quitting
        // in that case, and needs a couple of _Process frames first to actually capture the output.
        if (GreyBoxArgs.Get("quit-after-ready") != null
            && GreyBoxArgs.Get("shot-out") is null
            && GreyBoxArgs.Get("dump-ui-out") is null)
        {
            GetTree().Quit(0);
        }
    }

    /// <summary>
    /// Stage 6 task 5's export smoke check. Deliberately does NOT depend on <c>--seed</c>/<c>--load</c>/
    /// any other launch flag or a bundled save file — a totally fixed, hardcoded seed/width/height/
    /// civ-count, the same content bundle every launch already loads off disk, advanced a fixed 10
    /// turns. That makes two independent invocations of the same binary directly comparable by
    /// construction (the determinism proof <c>game/tools/smoke.ps1</c> relies on), and it needs no
    /// committed fixture save file — docs/development-plan-v2.md §6 task 5 explicitly allows either
    /// approach ("load a bundled/committed save ... or deterministically construct a fixed-seed world
    /// if no save file is easiest"); a save file would only add a second thing that could go stale.
    ///
    /// <para>Not comparable to `hearthspan hash` as an external oracle: <see cref="World"/>'s only
    /// constructor (sim/Hearthspan.Sim/World.cs) defaults <c>civCount</c> to 0 and <c>content</c> to
    /// null when not supplied, which is exactly what <c>HashCommand</c> in
    /// Hearthspan.Sim.Harness calls — a materially different world (no civs, no content-driven
    /// buildings/techs) than this method's real-content, real-civ-count world. Making the harness
    /// support a content-bundle + civ-count hash run would be a `sim/` (well, Harness-side, but still
    /// "Sim policy: zero sim/ diffs" per this stage's plan) change out of this task's scope, so this
    /// intentionally does NOT claim an external-oracle match — the acceptance test is "two independent
    /// runs of the exported exe produce the identical hash", which is itself a real, honest determinism
    /// proof per the task's own documented fallback.</para>
    /// </summary>
    private void RunSmokeAndExit()
    {
        const ulong smokeSeed = 555001;
        const int smokeWidth = 64;
        const int smokeHeight = 40;
        const int smokeCivs = 4;
        const int smokeTurns = 10;

        var world = new World(smokeWidth, smokeHeight, smokeSeed, smokeCivs, _content, 0);
        for (int t = 0; t < smokeTurns; t++)
        {
            world.Advance();
        }

        var payload = new Godot.Collections.Dictionary
        {
            ["status"] = "ok",
            ["seed"] = smokeSeed,
            ["width"] = smokeWidth,
            ["height"] = smokeHeight,
            ["civs"] = smokeCivs,
            ["turns"] = smokeTurns,
            ["stateHash"] = world.StateHash().ToString("x16"),
            ["settlementCount"] = world.Settlements.Count,
        };

        // Prefixed single line, the same convention AppRunner.ReportPerf already uses ("AppRunner: perf "
        // + json) to stay greppable out of whatever engine banner/import-scan noise a Godot-hosted
        // process also writes to stdout — matching the spirit of "one JSON object on stdout" that the
        // sim harness's CLI verbs follow literally (CLAUDE.md's CLI contract), even though this runs
        // through the Godot binary, not Hearthspan.Sim.Harness.
        GD.Print("HEARTHSPAN_SMOKE " + Json.Stringify(payload));
        GetTree().Quit(0);
    }

    /// <summary>Plan v3 task 1.4: measures every terrain mesh's real, imported <c>Mesh.GetAabb().Size.Y
    /// * TerrainScale</c> and prints it next to <see cref="MapView"/>'s hand-picked
    /// <c>GroundHeight</c> approximation for that kind, so the two new elevation-band heights
    /// (GrassHill 1.0, Stone 0.3464) are checked against the actual asset rather than trusted by eye —
    /// same reasoning as art/CONVENTIONS.md's own "measure before rescaling" rule, applied to height
    /// instead of footprint.</summary>
    private void RunGroundHeightsCheckAndExit()
    {
        var payload = MapView.DebugMeasureGroundHeights();
        GD.Print("HEARTHSPAN_GROUND_HEIGHTS " + Json.Stringify(payload));
        GetTree().Quit(0);
    }

    /// <summary>Plan v3 task 3.9's own Verify step: `--debug-era-check "toolmaking,bronze-working"`
    /// (etc.) prints the era EraClock.Compute would select for that completed-tech set, so the
    /// selection logic can be checked from a shell without a live game turn.</summary>
    private void RunEraCheckAndExit()
    {
        var techIds = (GreyBoxArgs.Get("debug-era-check") ?? "")
            .Split(',', System.StringSplitOptions.RemoveEmptyEntries)
            .ToList();
        var result = EraClock.Compute(_content, techIds, turn: 0, eraEnteredTurn: 0);
        var payload = new Godot.Collections.Dictionary { ["era"] = result.EraName, ["year"] = result.YearText };
        GD.Print(Json.Stringify(payload));
        GetTree().Quit(0);
    }

    /// <summary>Stage 7 task B1's own acceptance criterion: "verified by an actual uniqueness check,
    /// not by inspection." Checks ids 1..384 (the base onset x coda space) for exact uniqueness, then
    /// a second pass over ids 1..3072 (384 x the qualifier table's own length) to confirm the
    /// qualifier extension keeps ids past 384 distinct too, without asserting distinctness forever at
    /// unlimited scale — the task only requires "stable, distinct, plausible" past 384, not literal
    /// uniqueness at any id, and the concept brief's own endgame target (400-800 settlements) sits
    /// comfortably inside both checked ranges.</summary>
    private void RunNameCollisionCheckAndExit()
    {
        var seen384 = new System.Collections.Generic.Dictionary<string, int>();
        int collisions384 = 0;
        for (int id = 1; id <= 384; id++)
        {
            string name = MapPalette.SettlementName(id);
            if (seen384.ContainsKey(name)) collisions384++;
            else seen384[name] = id;
        }

        int extendedRange = 384 * MapPalette.SettlementNameQualifierCount;
        var seenExtended = new System.Collections.Generic.Dictionary<string, int>();
        int collisionsExtended = 0;
        for (int id = 1; id <= extendedRange; id++)
        {
            string name = MapPalette.SettlementName(id);
            if (seenExtended.ContainsKey(name)) collisionsExtended++;
            else seenExtended[name] = id;
        }

        var payload = new Godot.Collections.Dictionary
        {
            ["distinct"] = seen384.Count,
            ["collisions"] = collisions384,
            ["idsChecked"] = 384,
            ["extendedIdsChecked"] = extendedRange,
            ["extendedDistinct"] = seenExtended.Count,
            ["extendedCollisions"] = collisionsExtended,
        };
        GD.Print("HEARTHSPAN_NAME_CHECK " + Json.Stringify(payload));
        GetTree().Quit(0);
    }

    /// <summary>
    /// Stage 6 task 5 discovery: <c>res://../content</c> (the path this always used before) only
    /// resolves correctly when running from the editor/debug launch inside the repo, where
    /// <c>res://</c> maps to <c>game/</c> and its parent is the repo root. For an exported build,
    /// <c>res://</c> globalizes to the directory containing the running executable instead - there
    /// is no repo checkout there, so <c>res://../content</c> points at a directory that doesn't
    /// exist. Found by reproduction: the exported .exe, run standalone from a clean directory,
    /// printed the engine banner and then sat at ~0% CPU indefinitely (a native Windows "unhandled
    /// exception" dialog with no one there to click it, headless has no way to surface or dismiss
    /// it) instead of the expected <c>HEARTHSPAN_SMOKE</c> line.
    ///
    /// <para>Fix: try the editor/debug-relative path first (unchanged behavior, zero risk to every
    /// existing dump-ui/shot/CI invocation that already depends on it), and if that directory isn't
    /// actually on disk, fall back to a <c>content/</c> folder sitting next to the running
    /// executable itself - which is exactly where <c>game/tools/export.ps1</c> now copies the
    /// repository's <c>content/</c> tree after every export, so a real exported build is
    /// self-contained (rule 3 - plain JSON on disk, never baked into the .pck as an engine-native
    /// asset - is preserved; this only changes WHERE the loose JSON directory is found, not what it
    /// is).</para>
    /// </summary>
    internal static string ResolveContentRoot()
    {
        var editorRelative = ProjectSettings.GlobalizePath("res://../content");
        if (System.IO.Directory.Exists(editorRelative))
        {
            return editorRelative;
        }

        var exeDir = System.IO.Path.GetDirectoryName(OS.GetExecutablePath()) ?? ".";
        var besideExe = System.IO.Path.Combine(exeDir, "content");
        GD.Print($"GameSession: res://../content ({editorRelative}) not found - falling back to " +
                 $"content beside the running executable: {besideExe}.");
        return besideExe;
    }

    /// <summary>Plan v3 task 3.4: queues a toast ("Goal complete: ...") — toasts never overlap, each
    /// fading out over 3 seconds before the next (if any) takes its place.</summary>
    private void EnqueueToast(string title)
    {
        _pendingToasts.Enqueue(title);
        if (!_toastShowing) ShowNextToast();
    }

    private void ShowNextToast()
    {
        if (_pendingToasts.Count == 0) { _toastShowing = false; return; }
        _toastShowing = true;
        string title = _pendingToasts.Dequeue();
        _toastLabel.Text = title;
        _toastLabel.Modulate = Colors.White;
        _toastLabel.Visible = !_fullScreenPanelShowing;
        var tween = CreateTween();
        tween.TweenProperty(_toastLabel, "modulate:a", 0f, 3.0);
        tween.TweenCallback(Callable.From(() => { _toastLabel.Visible = false; ShowNextToast(); }));
    }

    /// <summary>Shows one full-screen view (tech tree, build view) or, with null, the map. Everything
    /// that belongs to the map view — cards, feed, End Turn, labels — lives under <c>_mapHud</c> and
    /// hides with it; the top bar stays.</summary>
    private void ShowPanel(Control? panel)
    {
        _techTree.Visible = panel == _techTree;
        _buildView.Visible = panel == _buildView;
        bool map = panel == null;
        _mapLabels.Visible = map;
        _mapHud.Visible = map;
        _fullScreenPanelShowing = !map;
        if (!map)
        {
            ExitFoundingMode();
            _pathPreview.Hide3D();
        }
    }

    private bool _fullScreenPanelShowing;

    /// <summary>Stage 6 task 6: lands on the plain map (same reasoning <see cref="ShowPanel"/> already
    /// uses for FocusAndSelectSettlement) before showing the pause overlay, so Resume always returns to
    /// a consistent screen rather than whatever TechTree/BuildView happened to be open underneath.</summary>
    private void OpenPauseMenu()
    {
        ShowPanel(null);
        _pauseMenu.Open();
    }

    private bool AnyDialogOpen => _saveLoadPanel.Visible || _pauseMenu.Visible || _endgameScreen.Visible
                                  || _decisionPrompt.Visible || _moments.IsShowing || _confirm.Visible;

    /// <summary>Plan v5 M3 keys. Esc steps back one level at a time — a moment card, a targeting
    /// mode, a full-screen view, the selection — and only then opens the pause menu. Enter presses
    /// the smart End Turn button (go to what is waiting, or end the turn); Shift+Enter ends the turn
    /// regardless. T, B and V open the tech tree, the build list and the growth overlay.</summary>
    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event.IsActionPressed("ui_cancel"))
        {
            if (_moments.IsShowing) _moments.Advance();
            else if (_selectionController.IsTargetingRoad)
            {
                _selectionController.CancelRoadTargeting();
                SyncRoadTargetingBanner();
            }
            else if (_foundingMode) ExitFoundingMode();
            else if (_pauseMenu.Visible) _pauseMenu.Close();
            else if (_techTree.Visible || _buildView.Visible) ShowPanel(null);
            else if (_card.IsShowing) ClearSelection();
            else OpenPauseMenu();
            GetViewport().SetInputAsHandled();
            return;
        }

        if (@event is InputEventKey { Pressed: true, Echo: false } key && !AnyDialogOpen)
        {
            switch (key.Keycode)
            {
                case Key.Enter or Key.KpEnter when !_fullScreenPanelShowing:
                    if (key.ShiftPressed) CommitEndTurn();
                    else OnEndTurnPressed();
                    GetViewport().SetInputAsHandled();
                    return;
                case Key.T:
                    TogglePanel(_techTree);
                    GetViewport().SetInputAsHandled();
                    return;
                case Key.B:
                    OpenBuildViewForSelection();
                    GetViewport().SetInputAsHandled();
                    return;
            }
        }

        if (@event.IsActionPressed("toggle_overlay"))
        {
            ToggleOverlay();
            GetViewport().SetInputAsHandled();
        }
    }

    /// <summary>Stage 6 task 6: PauseMenu's "Quit to Menu" button, invoked only once it has already
    /// decided the quit is safe (see <see cref="PauseMenu.RequestQuit"/>) — never called directly from a
    /// click. Same deferred-call trap-list fix <see cref="MainMenu.DeferredChangeScene"/> uses (CLAUDE.md:
    /// "Parent node is busy adding/removing children" if ChangeSceneToFile were called synchronously
    /// from inside a signal handler still unwinding through this frame's input processing).</summary>
    private void HandleQuitToMenu()
    {
        GD.Print("GameSession: quitting to main menu.");
        Callable.From(() => GetTree().ChangeSceneToFile("res://scenes/MainMenu.tscn")).CallDeferred();
    }

    /// <summary>PauseMenu's "Quit to Desktop" button, same "only called once already decided safe"
    /// contract as <see cref="HandleQuitToMenu"/>.</summary>
    private void HandleQuitToDesktop()
    {
        GD.Print("GameSession: quitting to desktop.");
        GetTree().Quit(0);
    }

    private Civilization PlayerCiv => _world.Civilizations.Single(c => c.Index == _playerCivIndex);
    private Settlement PlayerCapital => _world.Settlements.Single(s => s.Id == PlayerCiv.CapitalSettlementId);

    /// <summary>Stage 6 task 6: flips <see cref="_dirtySinceSave"/> true — called at every point in this
    /// file that performs a real, committed world mutation (never for a pure UI/selection change like
    /// <see cref="OnSettlementSelected"/> or <see cref="ShowPanel"/>, and never for a load, which starts
    /// clean by construction). See that field's own doc comment for the exact call-site list.</summary>
    private void MarkDirty() => _dirtySinceSave = true;

    /// <summary>The actual turn advance — the End Turn button when nothing is waiting, Shift+Enter, or
    /// "End turn anyway". Turn-scoped UI state resets here. Units the player lost during the turn are
    /// reported by diffing, since an enemy's victory is logged under the enemy's name.</summary>
    private void CommitEndTurn()
    {
        if (_moments.IsShowing) return;
        ExitFoundingMode();
        if (_selectionController.IsTargetingRoad)
        {
            _selectionController.CancelRoadTargeting();
            SyncRoadTargetingBanner();
        }
        var player = PlayerCiv;
        var unitsBefore = _world.Units.Where(u => u.OwnerId == player.Id)
            .Select(u => (u.Id, u.DefId, u.Location)).ToList();
        var popBefore = _world.Settlements.Where(s => s.OwnerId == player.Id).ToDictionary(s => s.Id, s => s.Population);

        _world.Advance();
        Audio.Sfx.Play(Audio.Sound.EndTurn);
        _dismissedDecisionsThisTurn.Clear();
        _suppressDecisionsThisTurn = false;
        _skippedUnitsThisTurn.Clear();

        foreach (var (id, defId, location) in unitsBefore)
        {
            if (_world.Units.Any(u => u.Id == id)) continue;
            string name = _content.Units.Find(u => u.Id == defId)?.Name ?? "unit";
            AddNews($"Your {name} was lost in battle.", Icon.Swords, HudStyle.Bad, location);
            _sleepingUnits.Remove(id);
            _exploringUnits.Remove(id);
        }
        RunAutoExplore();
        RefreshAll();
        MarkDirty();

        var floaters = new List<(int, string, Color)>();
        foreach (var s in _world.Settlements.Where(s => s.OwnerId == player.Id))
        {
            long delta = s.Population - (popBefore.TryGetValue(s.Id, out long before) ? before : 0);
            if (!popBefore.ContainsKey(s.Id)) floaters.Add((s.Id, "New!", HudStyle.Gold));
            else if (delta > 0) floaters.Add((s.Id, $"+{delta}", HudStyle.Good));
            else if (delta < 0) floaters.Add((s.Id, $"{delta}", HudStyle.Bad));
        }
        _mapLabels.AddFloaters(floaters);
    }

    private void OnTechClicked(string techId)
    {
        if (_world.TrySetResearchTarget(PlayerCiv.Id, techId)) _researchPromptRequested = false;
        RefreshAll();
        MarkDirty();
    }

    private void OnBuildingQueued(string buildingId)
    {
        if (_world.TryEnqueueBuilding(BuildViewSettlement.Id, buildingId)) _manuallyTouchedSettlementIds.Add(BuildViewSettlement.Id);
        RefreshAll();
        MarkDirty();
    }

    private void OnQueueItemCancelled(int queueIndex)
    {
        _world.TryCancelQueued(BuildViewSettlement.Id, queueIndex);
        RefreshAll();
        MarkDirty();
    }

    private void OnQueueItemMoved(int queueIndex, int delta)
    {
        _world.TryReorderQueued(BuildViewSettlement.Id, queueIndex, delta);
        RefreshAll();
        MarkDirty();
    }

    private void OnSettlementSelected(int settlementId)
    {
        _buildViewSettlementId = settlementId;
        RefreshAll();
    }

    /// <summary>The settlement the build view is pointed at, falling back to the capital if the
    /// selected one has vanished (razed in Wave 1, or absorbed).</summary>
    private Settlement BuildViewSettlement =>
        _world.Settlements.Find(s => s.Id == _buildViewSettlementId && s.OwnerId == PlayerCiv.Id) ?? PlayerCapital;

    // --- Stage 2 task 2/4: selection panel, verbs surfaced with a real preview ---

    private void OnMapSettlementSelected(int settlementId)
    {
        if (_foundingMode) ExitFoundingMode();
        _selectedSettlementId = settlementId;
        _selectedUnitId = -1;
        _selectedTile = null;
        _unitView.SetSelected(-1);
        _pathPreview.Hide3D();
        RefreshSelection();
    }

    /// <summary>Task 6 (turn report click-to-focus) and task 7 (idle-loop cycling) both reduce to the
    /// same action: move the camera to a settlement and select it, so the player lands looking at
    /// exactly the place the decision belongs to, not just told its name.</summary>
    private void FocusAndSelectSettlement(int settlementId)
    {
        var settlement = _world.Settlements.Find(s => s.Id == settlementId);
        if (settlement is null) return;
        _camera.FocusOn(settlement.Location.Col, settlement.Location.Row);
        ShowPanel(null); // the map, not TechTree/BuildView — there's nothing to focus on otherwise
        // Stage 7 task G1 (docs/stage-7-plan.md): SelectSettlement's own doc comment already claimed
        // this call site (and the idle-cycle button, which reaches here through
        // CycleToNextIdleSettlement) drives the 3D highlight — it didn't; this called
        // OnMapSettlementSelected directly instead, so a turn-report/idle-cycle focus updated the
        // selection panel but left the map with no visible highlight at all. SelectSettlement fires
        // the same SettlementSelected event (wired to OnMapSettlementSelected), so this is a strict
        // replacement, not an addition.
        _selectionController.SelectSettlement(settlement.Location.Col, settlement.Location.Row);
    }

    /// <summary>Stage 7 task B2 (docs/stage-7-plan.md): resolves a named camera bookmark against the
    /// LIVE world, called from AppRunner (which only knows the scene, not the game) instead of
    /// CameraRig.ApplyBookmark reading a hardcoded 128x80-reference-world coordinate table that the
    /// default 64x40 game never matched. Returns false for an unrecognised name.</summary>
    public bool ApplyCameraBookmark(string name)
    {
        bool ok = ApplyCameraBookmarkCore(name);
        // FABLE STOP 2: --camera-height <units> overrides the bookmark's own height (same focus), so a
        // verification launch can look at one tile from as close as it likes without a new bookmark.
        int heightOverride = GreyBoxArgs.GetInt("camera-height", 0);
        if (ok && heightOverride > 0)
        {
            var f = _camera.Focus;
            _camera.FrameStartAtWorld(_world.Grid.Width, _world.Grid.Height, f, heightOverride);
        }
        return ok;
    }

    private bool ApplyCameraBookmarkCore(string name)
    {
        var capital = PlayerCapital;
        switch (name)
        {
            case "close-on-capital":
                _camera.FrameStart(_world.Grid.Width, _world.Grid.Height, capital.Location.Col, capital.Location.Row, 18f);
                return true;
            case "mid-region":
                _camera.FrameStart(_world.Grid.Width, _world.Grid.Height, capital.Location.Col, capital.Location.Row, 60f);
                return true;
            case "largest-settlement":
            {
                // FABLE STOP 2: the biggest settlement the player can currently see (any owner), so a
                // City/Metropolis tier tile can be shot on demand instead of hoping the capital gets there.
                var view = _world.Snapshot(PlayerCiv.Id);
                var best = view.Settlements.Where(s => !s.Stale)
                    .OrderByDescending(s => s.Population).ThenBy(s => s.Id).FirstOrDefault();
                if (best == null) return false;
                _camera.FrameStart(_world.Grid.Width, _world.Grid.Height, best.Col, best.Row, 18f);
                return true;
            }
            case "coastline":
                var coast = FindNearestCoastline(capital.Location);
                _camera.FrameStart(_world.Grid.Width, _world.Grid.Height, coast.Col, coast.Row, 40f);
                return true;
            case "max-zoom-out":
                _camera.FrameMap(_world.Grid.Width, _world.Grid.Height);
                return true;
            default:
                return false;
        }
    }

    /// <summary>BFS outward from <paramref name="start"/> over the live grid's already-computed
    /// per-tile <c>WaterDistance</c> (distance in hex steps to the nearest water tile — the same field
    /// task G2 tints water with), stopping at the first tile with distance exactly 1 (land immediately
    /// next to water — a coastline tile). Falls back to <paramref name="start"/> itself if nothing
    /// qualifies within a reasonable search radius (a landlocked capital, or an unusually small map).</summary>
    private HexCoord FindNearestCoastline(HexCoord start)
    {
        const int maxVisited = 4000;
        var visited = new HashSet<HexCoord> { start };
        var queue = new Queue<HexCoord>();
        queue.Enqueue(start);

        while (queue.Count > 0 && visited.Count < maxVisited)
        {
            var current = queue.Dequeue();
            if (_world.Grid.WaterDistanceAt(current) == 1) return current;
            foreach (var neighbor in current.Neighbors(_world.Grid.Width, _world.Grid.Height))
            {
                if (visited.Add(neighbor)) queue.Enqueue(neighbor);
            }
        }

        return start;
    }

    /// <summary>Stage 2 task 3: prices the drag every hover tick via <see cref="CommandPreview"/> and
    /// hands the (world-space) path back to the drag visual — GameSession is the only place holding the
    /// live <see cref="World"/> a real preview needs, so pricing lives here, not in SelectionController.</summary>
    private void OnRoadDragHover(int fromId, int toId)
    {
        if (toId < 0)
        {
            _selectionController.HideRoadPreview();
            return;
        }

        var preview = CommandPreview.EvaluateBuildRoad(_world, PlayerCiv.Id, fromId, toId);
        var positions = preview.AffectedTiles.Select(t => GroundPoint(t.Col, t.Row)).ToList();
        if (positions.Count == 0)
        {
            // Illegal for a reason that isn't "no path" (wrong ownership, already connected) — still
            // show *something* so the drag doesn't look like it did nothing, using a two-point line
            // between the two settlements.
            var from = _world.Settlements.Find(s => s.Id == fromId);
            var to = _world.Settlements.Find(s => s.Id == toId);
            if (from is not null && to is not null)
            {
                positions = new List<Vector3> { GroundPoint(from.Location.Col, from.Location.Row), GroundPoint(to.Location.Col, to.Location.Row) };
            }
        }
        _selectionController.ShowRoadPreview(positions, preview.Legal);
    }

    /// <summary>Stage 2 task 3: commits the drag as a real <c>PlayerCommand</c> through the same
    /// <c>World.TryBuildRoad</c> every other road-building path (tests, headless args) already uses.</summary>
    private void OnRoadDragCompleted(int fromId, int toId)
    {
        if (_world.TryBuildRoad(PlayerCiv.Id, fromId, toId)) { RefreshAll(); MarkDirty(); }
        // Covers both entry paths: after a targeting-mode confirm, SelectionController has already
        // cleared IsTargetingRoad by the time this fires, so this hides the banner; after a plain drag
        // release, IsTargetingRoad was never true, so this is a harmless no-op.
        SyncRoadTargetingBanner();
    }

    /// <summary>Task C5's button-driven entry point to road-building — "Build road to..." in the
    /// selection panel calls this instead of requiring the undiscoverable press-and-drag gesture.
    /// Highlights every currently-legal target up front (all of them, not just whichever one the cursor
    /// happens to be over) so the player sees their real options before committing to one.</summary>
    private void BeginRoadTargeting(int fromSettlementId)
    {
        _selectionController.BeginRoadTargeting(fromSettlementId);
        var positions = _world.Settlements
            .Where(s => s.OwnerId == PlayerCiv.Id && s.Id != fromSettlementId)
            .Where(s => CommandPreview.EvaluateBuildRoad(_world, PlayerCiv.Id, fromSettlementId, s.Id).Legal)
            .Select(s => GroundPoint(s.Location.Col, s.Location.Row))
            .ToList();
        _selectionController.ShowRoadTargetHighlights(positions);
        SyncRoadTargetingBanner();
    }

    private void SyncRoadTargetingBanner()
    {
        if (_selectionController.IsTargetingRoad)
        {
            _modeBanner.Text = "Click another of your settlements to connect it by road.  Esc to cancel";
            _modeBanner.Visible = true;
        }
        else _modeBanner.Visible = _foundingMode;
    }

    private Vector3 GroundPoint(int col, int row) =>
        HexLayout.ToWorld(col, row) + new Vector3(0, _mapView.GroundHeightAt(col, row), 0);

    /// <summary>Plan v3 task 3.5: "Next: Town at 500 (about 27 turns)" — turns from
    /// <c>ceil(ln(threshold/pop) / ln(1+rate))</c>, thresholds read from <see cref="TierLadder"/>
    /// (never copied as a literal), "not growing" when the clamped rate is zero or negative.</summary>
    private static string BuildNextTierLine(SettlementTierName tier, long population, double rate)
    {
        (SettlementTierName Next, long Threshold)? next = tier switch
        {
            SettlementTierName.Camp => (SettlementTierName.Hamlet, TierLadder.HamletThreshold),
            SettlementTierName.Hamlet => (SettlementTierName.Village, TierLadder.VillageThreshold),
            SettlementTierName.Village => (SettlementTierName.Town, TierLadder.TownThreshold),
            SettlementTierName.Town => (SettlementTierName.City, TierLadder.CityThreshold),
            SettlementTierName.City => (SettlementTierName.Metropolis, TierLadder.MetropolisThreshold),
            _ => null,
        };
        if (next is null) return "Metropolis is the highest tier.";

        if (population <= 0 || rate <= 0)
        {
            return $"Next: {next.Value.Next} at {next.Value.Threshold}  (not growing)";
        }
        double turnsD = System.Math.Log((double)next.Value.Threshold / population) / System.Math.Log(1 + rate);
        int turns = (int)System.Math.Ceiling(turnsD);
        return $"Next: {next.Value.Next} at {next.Value.Threshold}  (about {turns} turn{(turns == 1 ? "" : "s")})";
    }

    /// <summary>Stage 6 task 4: content has no author-written flavor text anywhere (grepped every
    /// Content/Models/*.cs before writing this — TechDef/BuildingDef/EdictDef carry only ids and numeric
    /// effect fields), so a genuinely useful edict tooltip has to be composed from the same mechanical
    /// fields <see cref="ConstructionEngine"/>/<see cref="Hearthspan.Sim.Edicts.EdictEngine"/> actually
    /// read, not invented prose. The amount fraction is presented as a percent for readability, matching
    /// how <see cref="BuildingTooltip"/> already presents FoodBonusPercent et al. — an approximation of
    /// what the raw Fixed64 magnitude means to the <c>ViabilityChannel</c> it modifies via
    /// <c>Hearthspan.Sim.Map.ModifierLayer</c> (grepped <c>sim/Hearthspan.Sim/Verbs/EdictState.cs</c>
    /// and <c>Hearthspan.Sim.Map.ModifierLayer</c> for the real read path before writing this), not a
    /// literal ledger percentage, but close enough to be useful and never misleading about
    /// direction/scale.</summary>
    private static string EdictEffectLine(EdictDef def)
    {
        double amount = def.AmountDenominator == 0 ? 0 : (double)def.AmountNumerator / def.AmountDenominator;
        string sign = amount >= 0 ? "+" : "";
        string upkeep = def.UpkeepPerTurn switch
        {
            > 0 => $"Upkeep {def.UpkeepPerTurn} gold a turn.",
            < 0 => $"Pays you {-def.UpkeepPerTurn} gold a turn.",
            _ => "No upkeep.",
        };
        return $"{sign}{amount * 100:F0}% {def.Channel} within {def.Radius} tile{(def.Radius == 1 ? "" : "s")} while it stands. {upkeep}";
    }

    /// <summary>FABLE STOP 3: the hover text is the effect line plus the one sentence that explains
    /// what an edict <i>is</i> — the design-decisions rule that verbs steer the land, never command it.</summary>
    private static string EdictTooltip(EdictDef def) =>
        $"{EdictEffectLine(def)} Costs {def.Cost} gold to enact. Edicts change how attractive the land " +
        "around a settlement is: they speed or slow its growth and steer where new hamlets appear.";

    /// <summary>FABLE STOP 3: the sim's own refusal reasons name techs by content id ("requires
    /// agriculture"); the panel shows them by display name ("needs Agriculture"). Sim strings are not
    /// touched (no sim change this phase) — this is a display-side translation only.</summary>
    private string PrettyReason(string reason)
    {
        const string prefix = "requires ";
        if (!reason.StartsWith(prefix)) return reason;
        string id = reason.Substring(prefix.Length);
        string name = _content?.Techs.Find(t => t.Id == id)?.Name ?? id;
        return $"needs {name}";
    }

    private int CivIndexOf(int civId) => _world.Civilizations.Find(c => c.Id == civId)?.Index ?? civId;

    /// <summary>Stage 4: the same civ-colour convention MapView's settlement/civ-plate rendering
    /// already uses, exposed for <see cref="UnitView"/> so a unit's tint always matches the owner's
    /// settlements without a second palette to keep in sync.</summary>
    private Color CivColorFor(int civId) =>
        civId < 0 ? MapView.FreeholdColor : MapView.CivPalette[CivIndexOf(civId) % MapView.CivPalette.Length];

    /// <summary>The pre-multi-slot default path (`--save-path`/`--save-now`, unchanged), now routed
    /// through the same <see cref="SaveSlots.SaveAndVerify"/> second-channel check every other save
    /// path uses — see <see cref="ReportSaveResult"/>. `--save-now`'s ordering requirement (must run
    /// last, see the comment at its call site) is unaffected: this method still writes to exactly
    /// <see cref="_savePath"/>, nothing about the target changed, only how success is decided.</summary>
    private void OnSave()
    {
        var result = SaveSlots.SaveAndVerify(_world, _content, _savePath);
        ReportSaveResult(SaveSlots.LegacyQuickSaveLabel, result);
    }

    /// <summary>Stage 6 task 2: the Save dialog's actual Save action, also reachable headlessly via
    /// `--save-slot &lt;name&gt;` — writes to the named slot via <see cref="SaveSlots.SaveAndVerify"/>
    /// (the second-channel rule, CLAUDE.md: re-read the file, load it into a brand-new World, compare
    /// <c>StateHash</c>), and only reports success to the player if that check passed. Called by
    /// <see cref="SaveLoadPanel"/> only after any needed overwrite confirmation already happened — this
    /// method itself always writes unconditionally, matching <see cref="OnSave"/>'s existing
    /// unconditional-overwrite behavior for `--save-now`.</summary>
    private void HandleSaveToSlot(string slotName)
    {
        var path = SaveSlots.PathFor(slotName);
        var result = SaveSlots.SaveAndVerify(_world, _content, path);
        ReportSaveResult(slotName, result);
    }

    /// <summary>The one place that decides what "the player gets told 'Saved'" means — a hash match is
    /// the only thing that earns it. A mismatch is a real bug (the written file does not reproduce the
    /// live world), never swallowed: it prints loudly to stderr (GD.PrintErr, visible in any shell that
    /// launched this process) and shows as a red status line in the dialog, not a generic failure.</summary>
    private void ReportSaveResult(string slotName, SaveSlots.SaveResult result)
    {
        if (result.Verified)
        {
            // Stage 6 task 6: only a verified save clears "unsaved progress" — a save that failed its
            // own second-channel check (below) must not tell the quit-warning dialog there is nothing
            // left to lose.
            _dirtySinceSave = false;
            GD.Print($"GameSession: saved slot '{slotName}' to {result.Path} — second-channel verified, " +
                      $"live hash {result.LiveHash:x16} == reloaded hash {result.ReloadedHash:x16}.");
            _saveLoadPanel.ShowStatus($"Saved \"{slotName}\" — verified (hash {result.LiveHash:x16}).", isError: false);
        }
        else
        {
            GD.PrintErr($"GameSession: SAVE NOT CONFIRMED for slot '{slotName}' at {result.Path}: {result.Error}");
            _saveLoadPanel.ShowStatus($"Save FAILED verification: {result.Error}", isError: true);
        }
    }

    /// <summary>Stage 6 task 2: the Load dialog's actual Load action, also reachable headlessly via
    /// `--load-slot &lt;name&gt;` — looks the name up via <see cref="SaveSlots.Find"/> (the exact same
    /// shared source of truth the dialog's own slot list was built from) and, if it still exists on
    /// disk, replaces the live world the same way the initial `--load`/`GameLaunchConfig.LoadPath`
    /// handling in <see cref="_Ready"/> does.</summary>
    private void HandleLoadFromSlotName(string slotName)
    {
        var slot = SaveSlots.Find(slotName);
        if (slot is not { } found || !System.IO.File.Exists(found.Path))
        {
            GD.PrintErr($"GameSession: --load-slot/Load '{slotName}' — no such save slot found under " +
                        $"{SaveSlots.SavesDir} (or the legacy quick-save path).");
            _saveLoadPanel.ShowStatus($"No such save: \"{slotName}\".", isError: true);
            return;
        }

        _world = SaveGame.Load(found.Path, _content);
        _playerCivIndex = _world.PlayerCivIndex;
        _dirtySinceSave = false; // a freshly loaded world matches the file on disk exactly, by construction.
        ResetUiStateAfterLoad();
        RefreshAll();
        _saveLoadPanel.Close();
        GD.Print($"GameSession: loaded slot '{slotName}' from {found.Path} at turn {_world.Turn}, " +
                  $"stateHash={_world.StateHash():x16}.");
    }

    /// <summary>A loaded game starts with nothing selected, no UI-only orders (sleep, explore, skip),
    /// an empty news feed, and the loaded turn's events treated as already seen.</summary>
    private void ResetUiStateAfterLoad()
    {
        _sleepingUnits.Clear();
        _exploringUnits.Clear();
        _skippedUnitsThisTurn.Clear();
        _dismissedDecisionsThisTurn.Clear();
        _manuallyTouchedSettlementIds.Clear();
        _foundingMode = false;
        _modeBanner.Visible = false;
        _selectedUnitId = -1;
        _selectedSettlementId = -1;
        _selectedTile = null;
        _moments.DismissAll();
        _feed.ClearAll();
        _eventCursorTurn = _world.Turn;
        _eventCursor = _world.Events.ThisTurn.Count;
        _buildViewSettlementId = PlayerCiv.CapitalSettlementId;
    }

    /// <summary>Re-exports the WorldView snapshot and re-projects every view from that file — the one
    /// place the live World turns back into the read-only artifact rule 4 requires the view layer to
    /// consume. Called after every mutation (end turn, research pick, build order, unit order).</summary>
    private void RefreshAll()
    {
        MapPalette.NameOverrides = CivNaming.SettlementNames(_world, _content);
        var dto = ExportSnapshot();
        dto.Save(_snapshotPath);
        // The selection card reads the viability channels off this DTO, never recomputed in game/.
        _lastSnapshot = dto;

        var player = PlayerCiv;

        // Plan v3 task 3.9: the player's own era/year, from content. A completed tech is the only thing
        // that can advance it; entering a new era is one of the big moments.
        var eraResult = EraClock.Compute(_content, player.Tech.CompletedTechIds, _world.Turn, _eraEnteredTurn);
        if (_currentEraId != "" && eraResult.EraId != _currentEraId)
        {
            _eraEnteredTurn = _world.Turn;
            eraResult = EraClock.Compute(_content, player.Tech.CompletedTechIds, _world.Turn, _eraEnteredTurn);
            AddNews($"Your people enter the {eraResult.EraName}.", Icon.Star, HudStyle.Gold);
            AddMoment(new Hud.MomentPopup.Moment
            {
                Kicker = "A new age",
                Sound = Audio.Sound.Fanfare,
                Title = $"The {eraResult.EraName}",
                Body = $"Your people have entered the {eraResult.EraName}. New buildings, edicts and units wait in the tech tree.",
                Accent = HudStyle.Gold,
                Icon = Icon.Star,
            });
        }
        _currentEraId = eraResult.EraId;

        _mapView.LoadWorld(_snapshotPath);
        _mapLabels.Progress = BannerProgress();
        _minimap.Load(dto, CivColorFor);
        _unitView.LoadWorld(dto, CivColorFor, _mapView.GroundHeightAt,
            (x, z) => _mapView.HeightAt(new Vector3(x, 0, z)),
            id => _content.Units.Find(u => u.Id == id)?.Strength ?? 1);

        _techTree.LoadFromContent(_content, player.Tech.CompletedTechIds, player.Tech.ResearchTargetId, player.Tech.ResearchQueue);

        var target = BuildViewSettlement;
        _buildView.LoadWorld(_snapshotPath, target.Id);
        _buildView.SetAvailableBuildings(AvailableBuildingsFor(target));
        _buildView.SetQueue(target.Build.Queue, _content.Buildings.ToDictionary(b => b.Id, b => b.Name), QueueTurnsFor(target));
        _buildView.SetOwnedSettlements(OwnedSettlementLabels(), target.Id);
        _buildView.SetCompletedBuildings(target.Build.CompletedBuildingIds
            .Select(id => _content.Buildings.Find(b => b.Id == id))
            .Where(b => b is not null)
            .Select(b => (b!.Name, ContentIndex.EffectText(b)))
            .ToList());

        long legacy = _world.VictoryStandings()
            .Where(s => s.Type == Hearthspan.Sim.Victory.VictoryType.Legacy && s.CivId == player.Id)
            .Select(s => s.Score)
            .FirstOrDefault();

        // Stage 6 task 3: checked on every refresh — cheap, pure functions of already-live state.
        // --no-prompts means "no modal interruptions" for shell verification runs; the endgame overlay
        // is one, so it is not presented.
        string? winnerName = _world.WonderCompletedByCivId is int wonderCivId
            ? CivNaming.CapitalisedNameOf(_content, CivIndexOf(wonderCivId)) : null;
        if (PromptsAllowed)
            _endgameScreen.Present(DetermineEndgameState(), legacy, winnerName, EndgameSummary(eraResult));

        ProcessNewEvents();
        CheckWonderRace();

        // Plan v3 task 3.4: monotonic — evaluated every refresh, never rewound.
        foreach (var newlyCompletedId in _objectivesState.Evaluate(_world, _content, player.Id))
        {
            var objective = Objectives.All.First(o => o.Id == newlyCompletedId);
            EnqueueToast($"Goal complete: {objective.Title}");
        }

        // Stage 7 Part C: the research question is asked the moment nothing is being studied (turn 0,
        // and whenever the research queue runs dry) — the one prompt that still interrupts, because
        // research is the single thing that stalls everything when forgotten. --no-prompts suppresses
        // it unless the End Turn button asked for it explicitly.
        // Nothing else asks for attention while the game's ending is on screen.
        _lastDecisionQueue = (PromptsAllowed || _researchPromptRequested) && !_endgameScreen.Visible
            ? BuildDecisionQueue(player)
            : new List<PendingDecision>();
        _decisionPrompt.Present(_lastDecisionQueue);

        RefreshHud();
        RefreshSelection();
    }

    private List<PendingDecision> _lastDecisionQueue = new();

    /// <summary>Plan v5 M3: the only decision that still interrupts is research (production and unit
    /// orders are the End Turn button's job). Respects "Not now" and "Stop asking this turn", unless
    /// the player asked for it by pressing "Choose research".</summary>
    private List<PendingDecision> BuildDecisionQueue(Civilization player)
    {
        var queue = new List<PendingDecision>();
        if (player.Tech.ResearchTargetId is not null || !AnyTechAvailable(player))
        {
            _researchPromptRequested = false;
            return queue;
        }
        if (_researchPromptRequested || (!_suppressDecisionsThisTurn && !_dismissedDecisionsThisTurn.Contains("research")))
        {
            queue.Add(BuildResearchDecision(player));
        }
        return queue;
    }

    /// <summary>Matches CivAi.ResearchStep's own formula exactly (sim/Hearthspan.Sim/Civs/CivAi.cs) —
    /// the player earns research at the same rate an AI civ of the same population would. Shared by the
    /// research decision prompt (task C2) and the status bar's research readout (task B5) so the two
    /// can never quote a different rate for the same turn.</summary>
    private Fixed64 ComputeResearchRate(Civilization player) => CivAi.ResearchPerTurn(_world, player);

    /// <summary>-1 means "unknown" (a zero-or-negative rate can't be projected) — callers render that
    /// as text, never as a bogus turn count.</summary>
    private static int ComputeTurnsToComplete(double rate, int cost) =>
        rate > 0 ? (int)System.Math.Ceiling(cost / rate) : -1;

    /// <summary>Task C2: fires whenever the player has no research target — turn 0 of every new game,
    /// and again the instant a tech completes. Options are the 6 cheapest currently-available techs
    /// (every prereq done), each priced in turns at today's rate so "cost 45" means something; "See the
    /// full tech tree" always closes the list out to the real TechTreeView, which task 5b already
    /// centres on the frontier by itself — no extra framing call needed here. Turn 0 of a brand-new game
    /// (no completed techs yet) must not be dismissible: it is the only trigger that can ever fire
    /// before the first tech completes, so dismissing it would soft-lock a new player forever.</summary>
    private PendingDecision BuildResearchDecision(Civilization player)
    {
        double rateDouble = ComputeResearchRate(player).ToDouble();

        var options = new List<DecisionOption>();
        var available = _content.Techs
            .Where(t => ResearchEngine.IsAvailable(player.Tech, t.Id, _content))
            .OrderBy(t => t.Cost)
            .ThenBy(t => t.Id)
            .Take(6);

        foreach (var tech in available)
        {
            string techId = tech.Id;
            int turns = ComputeTurnsToComplete(rateDouble, tech.Cost);
            // Stage 7 task B5's own screenshot check caught this: a "~" prefix here read as a minus
            // sign at this UI font/size in the actual rendered screenshot ("Fire (cost 10, -8 turns)"),
            // not the "~8 turns" the source string says - the second-channel rule catching a glyph
            // legibility bug the string literal itself couldn't reveal. "about" costs a few more
            // pixels but can't be misread as a negative turn count.
            string turnsText = turns >= 0 ? $"about {turns} turn{(turns == 1 ? "" : "s")}" : "unknown time";

            // Plan v3 task 3.7: name the first thing this tech unlocks right in the option label —
            // "unlocks Encourage Agriculture edict" — with "+N more" when there's more than one, and
            // every unlock (plus the tech's own bonus) in the tooltip.
            var unlocks = _contentIndex.Unlocks(tech.Id);
            string unlockSuffix = unlocks.Count switch
            {
                0 => "",
                1 => $"  unlocks {unlocks[0].Describe()}",
                _ => $"  unlocks {unlocks[0].Describe()} and {unlocks.Count - 1} more",
            };
            string unlockTooltip = unlocks.Count > 0
                ? $" Unlocks: {string.Join(", ", unlocks.Select(u => u.Describe()))}."
                : "";

            options.Add(new DecisionOption
            {
                Label = $"{tech.Name}  (cost {tech.Cost}, {turnsText}){unlockSuffix}",
                Tooltip = $"{turnsText} to complete at your current rate of {rateDouble:0.00} research/turn. " +
                          $"{ContentIndex.EffectText(tech)}.{unlockTooltip}",
                Enabled = true,
                DisabledReason = "",
                Commit = () => _world.TrySetResearchTarget(player.Id, techId),
            });
        }

        options.Add(new DecisionOption
        {
            Label = "See the full tech tree",
            Tooltip = "Opens the tech tree, centred on what's available to research right now.",
            Enabled = true,
            DisabledReason = "",
            Commit = () => ShowPanel(_techTree),
        });

        bool turnZeroNoTechs = _world.Turn == 0 && player.Tech.CompletedTechIds.Count == 0;
        return new PendingDecision
        {
            Key = "research",
            Kind = "research",
            Title = "What should we research?",
            Body = $"Nothing is being researched. Your people produce {rateDouble:0.0} research a turn; it piles up unused until you pick something. Pick a far technology in the tech tree and every step to it is queued.",
            Options = options,
            AllowDismiss = !turnZeroNoTechs,
        };
    }

    /// <summary>Plan v5: the account of the player's game on the endgame card.</summary>
    private List<string> EndgameSummary(EraClock.Result era)
    {
        var player = PlayerCiv;
        var owned = _world.Settlements.Where(s => s.OwnerId == player.Id).ToList();
        var landmarks = owned.SelectMany(s => s.Build.CompletedBuildingIds).Distinct()
            .Select(id => _content.Buildings.Find(b => b.Id == id))
            .Where(b => b is not null && b.Hero).Select(b => b!.Name).OrderBy(n => n).ToList();
        return new List<string>
        {
            $"Turn {_world.Turn}  ·  {era.YearText}  ·  {era.EraName}",
            $"{owned.Count} settlement{(owned.Count == 1 ? "" : "s")}  ·  {owned.Sum(s => s.Population):N0} people",
            $"{player.Tech.CompletedTechIds.Count} {(player.Tech.CompletedTechIds.Count == 1 ? "technology" : "technologies")}  ·  " +
            $"{player.MetCivIds.Count} {(player.MetCivIds.Count == 1 ? "people" : "peoples")} met",
            landmarks.Count == 0 ? "No landmarks raised"
                : $"Landmarks: {string.Join(", ", landmarks.Take(4))}{(landmarks.Count > 4 ? $" and {landmarks.Count - 4} more" : "")}",
        };
    }

    /// <summary>
    /// Stage 6 task 3's endgame state — a pure read of already-committed World state, computed fresh on
    /// every <see cref="RefreshAll"/>, never cached.
    ///
    /// <para><b>Victory</b> is Stage 5's Wonder victory, the only real winnable condition in this
    /// codebase (<c>VictoryType.Dominion</c> is an enum value with no predicate anywhere — out of scope
    /// here, per the task). <see cref="Hearthspan.Sim.World.WonderCompletedByCivId"/> is the direct
    /// signal <see cref="Victory.WonderVictory"/> itself reads, rather than re-deriving anything from
    /// <see cref="Hearthspan.Sim.World.VictoryStandings"/> — checking it against the player's own civ id
    /// distinguishes "you won" from "someone else won" in one comparison.</para>
    ///
    /// <para><b>Defeat</b> has no existing notion anywhere in this codebase (grepped for
    /// defeat/eliminated/IsDefeated across sim/ and game/ — the only hits are this task's own docs and
    /// WonderVictory's doc comment mentioning who a wonder race leaves behind, not a defeat predicate).
    /// Per the plan's own open-endedness here, this picks the simplest real, checkable condition: the
    /// player's civ owns zero settlements. It is real (settlements are lost today via
    /// <see cref="Hearthspan.Sim.Settlements.PillageRaze"/>/conquest during war, an already-shipped Stage
    /// 4 mechanic — this is not a hypothetical), it needs no new sim state (a straight count over
    /// <c>World.Settlements</c>), and "holds nothing, everywhere" is the least controversial "the game is
    /// over for you" line to draw without inventing scoring thresholds or elimination rules the design
    /// docs never specified.</para>
    ///
    /// <para>Wonder is checked first: once the wonder race has concluded (for or against the player),
    /// that is treated as the game's actual ending even if the player's settlement count also happens to
    /// read zero in the same refresh — a settlement count of zero is not a more interesting fact than
    /// "the race everyone was in already finished".</para>
    /// </summary>
    private EndgameScreen.State DetermineEndgameState()
    {
        if (_world.WonderCompletedByCivId is int winnerId)
        {
            return winnerId == PlayerCiv.Id ? EndgameScreen.State.PlayerVictory : EndgameScreen.State.RivalVictory;
        }

        if (_world.Settlements.Count(s => s.OwnerId == PlayerCiv.Id) == 0)
        {
            return EndgameScreen.State.PlayerDefeat;
        }

        return EndgameScreen.State.None;
    }

    /// <summary>Player-owned settlements for the build-view switcher, biggest first so the places
    /// worth managing are at the top. Idle ones are flagged, matching the turn report's indicator —
    /// the same "here is what needs a decision" signal in both places.</summary>
    private List<(int id, string label)> OwnedSettlementLabels()
    {
        var player = PlayerCiv;
        return _world.Settlements
            .Where(s => s.OwnerId == player.Id)
            .OrderByDescending(s => s.Population)
            .ThenBy(s => s.Id)
            .Select(s => (s.Id,
                $"{MapPalette.SettlementName(s.Id)} ({s.Tier}, {s.Population})" +
                (s.Build.Queue.Count == 0 ? "  ⚑" : "")))
            .ToList();
    }

    /// <summary>The FILTERED snapshot (the player's fog), never the omniscient one — see
    /// <see cref="SnapshotExport.From"/>.</summary>
    private WorldViewDto ExportSnapshot() => SnapshotExport.From(_world, _content, PlayerCiv.Id);

    /// <summary>Stage 7 task 4 (docs/stage-7-plan.md, defect D): every buildable-but-not-completed
    /// building, unlocked first then locked (each alphabetical by id) so what the player can actually
    /// do sits at the top. Previously this filtered locked buildings out entirely, so a new civ (every
    /// building tech-gated, no research target) saw a blank column with no explanation.</summary>
    private List<BuildView.AvailableRow> AvailableBuildingsFor(Settlement settlement)
    {
        var owned = new HashSet<string>(settlement.Build.CompletedBuildingIds);
        owned.UnionWith(settlement.Build.Queue);
        var completedTechs = PlayerCiv.Tech.CompletedTechIds;
        double productionRate = ProductionRate(settlement);

        return _content.Buildings
            .Where(b => !owned.Contains(b.Id))
            .Select(b =>
            {
                bool locked = b.RequiresTechId is not null && !completedTechs.Contains(b.RequiresTechId);
                string techName = b.RequiresTechId is null
                    ? ""
                    : _content.Techs.Find(t => t.Id == b.RequiresTechId)?.Name ?? b.RequiresTechId;
                string reason = locked ? $"needs {techName}" : "";
                // Plan v5 D13: a landmark is one per people.
                if (!locked && b.Hero && !b.Wonder && LandmarkElsewhere(settlement, b.Id) is string where)
                {
                    locked = true;
                    reason = where;
                }
                int turns = ComputeTurnsToComplete(productionRate, b.Cost);
                return new BuildView.AvailableRow(b.Id, b.Name, b.Hero, b.Cost, turns, ContentIndex.EffectText(b), locked, reason, BuildingTooltip(b));
            })
            .OrderBy(r => r.Locked) // unlocked (false) first
            .ThenBy(r => r.Id)
            .ToList();
    }

    /// <summary>Plan v3 task 3.8: cumulative turns-until-complete for every item in the queue, front
    /// first — item i's figure already accounts for every item ahead of it (and the front item's own
    /// already-invested production), so it reads as "when does THIS one finish", not "how long would
    /// it take alone".</summary>
    private List<int> QueueTurnsFor(Settlement settlement)
    {
        double rate = ProductionRate(settlement);
        double invested = settlement.Build.InvestedProduction.ToDouble();
        double cumulativeCost = 0;
        var turns = new List<int>();
        foreach (var buildingId in settlement.Build.Queue)
        {
            cumulativeCost += _content.Buildings.Find(b => b.Id == buildingId)?.Cost ?? 0;
            double remaining = System.Math.Max(0, cumulativeCost - invested);
            turns.Add(ComputeTurnsToComplete(rate, (int)System.Math.Ceiling(remaining)));
        }
        return turns;
    }

    /// <summary>Stage 6 task 4 — see <see cref="EdictTooltip"/>'s doc comment for why this is composed
    /// from <see cref="BuildingDef"/>'s own numeric fields rather than any authored flavor text (there
    /// isn't any). Effect lines show only for fields that are set; a "+0%" line for an unset field
    /// would mislead.</summary>
    private static string BuildingTooltip(BuildingDef def)
    {
        var parts = new List<string> { $"Costs {def.Cost} production to build." };
        if (def.Wonder)
        {
            parts.Add("Wonder — the first civilization anywhere to finish this wins the game.");
        }
        else if (def.Hero)
        {
            parts.Add("Hero building — one of this era's individually-modeled buildings.");
        }
        if (def.FoodBonusPercent is int food) parts.Add($"+{food}% growth from food nearby while standing.");
        if (def.TradeBonusPercent is int trade) parts.Add($"+{trade}% growth from trade nearby while standing.");
        if (def.SafetyBonusPercent is int safety) parts.Add($"+{safety}% growth from safety nearby while standing.");
        if (def.ProductionBonusPercent is int prod) parts.Add($"+{prod}% to this settlement's own production.");
        if (def.GoldBonusPercent is int gold) parts.Add($"+{gold}% to this settlement's own gold.");
        return string.Join(' ', parts);
    }

    public string DumpUiDigest()
    {
        var player = PlayerCiv;
        var knowledge = _world.KnowledgeFor(player.Id);
        int owned = _world.Settlements.Count(s => s.OwnerId == player.Id);
        int independent = _world.Settlements.Count(s => s.OwnerId is null);
        long legacy = _world.VictoryStandings()
            .Where(s => s.Type == Hearthspan.Sim.Victory.VictoryType.Legacy && s.CivId == player.Id)
            .Select(s => s.Score).FirstOrDefault();
        int eligibleTiles = _mapView.World is WorldViewDto wv
            ? wv.Viability.Count(v => v >= wv.SpawnThreshold)
            : 0;

        return string.Join('\n', new[]
        {
            $"GameSession turn={_world.Turn} settlements={_world.Settlements.Count} civs={_world.Civilizations.Count}",
            $"player civIndex={_playerCivIndex} researching={player.Tech.ResearchTargetId ?? "(none)"} completedTechs={player.Tech.CompletedTechIds.Count}",
            $"ownership owned={owned} independent={independent} legacy={legacy}",
            $"knowledge exploredTiles={knowledge?.ExploredTileCount ?? -1}/{knowledge?.TileCount ?? -1}",
            $"verbs roads={_world.Roads.Roads.Count} roadTiles={_world.Roads.RoadTileCount} edicts={_world.Edicts.ActiveCount} commands={_world.Commands.Count}",
            $"units total={_world.Units.Count} yours={_world.Units.Count(u => u.OwnerId == player.Id)} wars={_world.Wars.AllSorted().Count}",
            $"events thisTurn={_world.Events.ThisTurn.Count}",
            $"buildView settlement={BuildViewSettlement.Id} buildQueue={BuildViewSettlement.Build.Queue.Count} completedBuildings={BuildViewSettlement.Build.CompletedBuildingIds.Count}",
            $"idleSettlements={_world.Settlements.Count(s => s.OwnerId == player.Id && s.Build.Queue.Count == 0)}",
            $"dirtySinceSave={_dirtySinceSave}",
            // Stage 5 gate (docs/development-plan-v2.md §6): "check that world.WonderCompletedByCivId
            // ... reflects it" in real play. WonderCompletedByCivId isn't itself part of SaveGame's DTO
            // (see SaveGame.cs's class doc — only what replay can't reproduce is saved), but it IS
            // correctly reconstructed as a byproduct of ConstructionEngine running during SaveGame.Load's
            // replay loop, so this line is truthful after --load too, not just in a fresh session.
            $"wonder completedByCivId={_world.WonderCompletedByCivId?.ToString() ?? "(none)"} youAreWinner={_world.WonderCompletedByCivId == player.Id}",
            $"endgame state={DetermineEndgameState()}",
            $"panels: techTree.Visible={_techTree.Visible} buildView.Visible={_buildView.Visible}",
            // Plan v3 task 4.6: N = known land tiles at or above SpawnEngine's real threshold — the
            // overlay's own claim of "eligible now," verifiable off the same WorldViewDto every render
            // reads, not a separate approximation.
            $"overlay={(_mapView.Overlay == OverlayMode.Growth ? "growth" : "none")} eligibleTiles={eligibleTiles}",
            "",
            "--- HUD ---",
            _hudTop.DumpUiDigest(),
            _endTurn.DumpUiDigest(),
            _goal.DumpUiDigest(),
            _card.DumpUiDigest(),
            _rivals.DumpUiDigest(),
            _feed.DumpUiDigest(),
            _moments.DumpUiDigest(),
            _minimap.DumpUiDigest(),
            Audio.Sfx.DumpPeaks(),
            $"selection unit={_selectedUnitId} settlement={_selectedSettlementId} tile={(_selectedTile is HexCoord st ? $"{st.Col},{st.Row}" : "none")} " +
            $"hover={_hover.Col},{_hover.Row} foundingMode={_foundingMode} reachableTiles={_reachableTiles.Count} exploring=[{string.Join(",", _exploringUnits.OrderBy(i => i))}] " +
            $"sleeping=[{string.Join(",", _sleepingUnits.OrderBy(i => i))}] hoverTip=\"{_hoverTip.Text}\" modeBanner=\"{(_modeBanner.Visible ? _modeBanner.Text : "")}\"",
            "",
            "--- MapLabels ---",
            _mapLabels.DumpUiDigest(),
            // TechTreeView had no digest section here before Stage 6 task 4 — LoadFromContent was
            // already called every RefreshAll, so this was always reachable, just never printed. Added
            // now because it's the only way to verify this view's per-node tooltips (_GetTooltip, not a
            // static TooltipText) from a shell without a simulated mouse hover — see that method's own
            // doc comment.
            "--- TechTreeView ---",
            _techTree.DumpUiDigest(),
            "--- BuildView ---",
            _buildView.DumpUiDigest(),
            "--- SaveLoadPanel ---",
            _saveLoadPanel.DumpUiDigest(),
            "--- PauseMenu ---",
            _pauseMenu.DumpUiDigest(),
            "--- EndgameScreen ---",
            _endgameScreen.DumpUiDigest(),
            "--- DecisionPrompt ---",
            _decisionPrompt.DumpUiDigest(),
            "--- MapView buildings ---",
            _mapView.DumpBuildingPlacements(),
            "--- UnitView ---",
            _unitView.DumpUiDigest(),
        });
    }
}
