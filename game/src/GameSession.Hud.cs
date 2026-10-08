using System.Collections.Generic;
using System.Linq;
using Godot;
using Hearthspan.Content.Models;
using Hearthspan.Game.Hud;
using Hearthspan.Game.Render;
using Hearthspan.Sim;
using Hearthspan.Sim.Civs;
using Hearthspan.Sim.Events;
using Hearthspan.Sim.Exploration;
using Hearthspan.Sim.Fixed;
using Hearthspan.Sim.Map;
using Hearthspan.Sim.Settlements;
using Hearthspan.Sim.Units;
using Hearthspan.Sim.Verbs;
using CardAction = Hearthspan.Game.Hud.SelectionCard.Action;
using CardRow = Hearthspan.Game.Hud.SelectionCard.Row;

namespace Hearthspan.Game;

/// <summary>
/// Plan v5 M3 (D4, D6, D8, D10): the HUD and everything the player does on the map with the mouse —
/// selecting a unit, a settlement or a tile; moving units (right-click, or left-click a blue tile);
/// founding settlements by picking a green site; the smart End Turn button; the news feed and the
/// moment cards. Split out of GameSession.cs so the session's launch flags and save/load plumbing
/// stay readable. Every mutation still goes through a World verb, then <see cref="RefreshAll"/>.
/// </summary>
public partial class GameSession
{
    // --- HUD nodes ------------------------------------------------------------------------------------
    private Control _mapHud = null!;
    private TopBar _hudTop = null!;
    private SelectionCard _card = null!;
    private EventFeed _feed = null!;
    private EndTurnPanel _endTurn = null!;
    private MomentPopup _moments = null!;
    private RivalsPanel _rivals = null!;
    private GoalChip _goal = null!;
    private Minimap _minimap = null!;
    private PanelContainer _overlayLegend = null!;
    private Label _modeBanner = null!;
    private Label _hoverTip = null!;
    private Label _flashLabel = null!;
    private Tween? _flashTween;
    private PathPreview _pathPreview = null!;
    private ConfirmationDialog _confirm = null!;
    private System.Action? _confirmAction;
    private bool _overlayOn;

    // --- selection and orders ---------------------------------------------------------------------------
    private int _selectedUnitId = -1;
    private HexCoord? _selectedTile;
    private bool _foundingMode;
    private bool _researchPromptRequested;
    private readonly HashSet<int> _sleepingUnits = new();
    private readonly HashSet<int> _skippedUnitsThisTurn = new();
    private readonly HashSet<int> _exploringUnits = new();
    private readonly HashSet<int> _reachableTiles = new();
    private (int Col, int Row) _hover = (-1, -1);

    // --- events -----------------------------------------------------------------------------------------
    private int _eventCursorTurn = -1;
    private int _eventCursor;

    private bool PromptsAllowed => GreyBoxArgs.Get("no-prompts") == null;

    private void BuildHud()
    {
        var ui = GetNode<CanvasLayer>("UI");
        int insertAt = _mapLabels.GetIndex() + 1;
        void Insert(Control c)
        {
            ui.AddChild(c);
            ui.MoveChild(c, insertAt++);
        }

        // Everything that belongs to the map view lives under one full-rect, click-through parent, so
        // opening the tech tree or build view hides it in one line.
        _mapHud = new Control { Name = "MapHud", MouseFilter = Control.MouseFilterEnum.Ignore };
        _mapHud.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        Insert(_mapHud);

        var left = new VBoxContainer { Name = "Left", Position = new Vector2(12, TopBar.Height + 10), MouseFilter = Control.MouseFilterEnum.Ignore };
        left.AddThemeConstantOverride("separation", 8);
        _mapHud.AddChild(left);
        _goal = new GoalChip { Name = "Goal" };
        left.AddChild(_goal);
        _overlayLegend = BuildOverlayLegend();
        left.AddChild(_overlayLegend);

        var right = new VBoxContainer
        {
            Name = "Right",
            AnchorLeft = 1, AnchorRight = 1,
            OffsetLeft = -318, OffsetRight = -12, OffsetTop = TopBar.Height + 10, OffsetBottom = TopBar.Height + 10,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        right.AddThemeConstantOverride("separation", 10);
        _mapHud.AddChild(right);
        _rivals = new RivalsPanel { Name = "Rivals" };
        right.AddChild(_rivals);
        _feed = new EventFeed { Name = "Feed" };
        _feed.FocusRequested = FocusOnTile;
        right.AddChild(_feed);

        _card = new SelectionCard { Name = "Card" };
        _card.CloseRequested = ClearSelection;
        _mapHud.AddChild(_card);

        // Bottom-right: the minimap over the End Turn button.
        var bottomRight = new VBoxContainer
        {
            Name = "BottomRight",
            AnchorLeft = 1, AnchorRight = 1, AnchorTop = 1, AnchorBottom = 1,
            OffsetLeft = -14 - Minimap.MapWidth - 8, OffsetRight = -14, OffsetTop = -14, OffsetBottom = -14,
            GrowHorizontal = Control.GrowDirection.Begin,
            GrowVertical = Control.GrowDirection.Begin,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        bottomRight.AddThemeConstantOverride("separation", 8);
        _mapHud.AddChild(bottomRight);
        _minimap = new Minimap { Name = "Minimap" };
        _minimap.LookAt = p => _camera.LookAtWorld(p);
        bottomRight.AddChild(_minimap);
        _minimap.Bind(_camera, (origin, dir) => _mapView.RaycastGround(origin, dir));
        _endTurn = new EndTurnPanel { Name = "EndTurn" };
        _endTurn.Pressed = OnEndTurnPressed;
        _endTurn.ForcePressed = CommitEndTurn;
        bottomRight.AddChild(_endTurn);

        _modeBanner = HudStyle.Label("", 15, HudStyle.Gold);
        _modeBanner.AnchorLeft = 0.5f;
        _modeBanner.AnchorRight = 0.5f;
        _modeBanner.OffsetLeft = -380;
        _modeBanner.OffsetRight = 380;
        _modeBanner.OffsetTop = TopBar.Height + 12;
        _modeBanner.HorizontalAlignment = HorizontalAlignment.Center;
        _modeBanner.AddThemeStyleboxOverride("normal", HudStyle.Box(HudStyle.CardBg, HudStyle.Gold, 1, 6, 14, 6));
        _modeBanner.Visible = false;
        _mapHud.AddChild(_modeBanner);

        _hoverTip = HudStyle.Label("", 13, HudStyle.Text);
        _hoverTip.AddThemeStyleboxOverride("normal", HudStyle.Box(HudStyle.CardBg with { A = 0.92f }, HudStyle.LineSoft, 1, 4, 7, 3));
        _hoverTip.Visible = false;
        _hoverTip.TopLevel = true;
        _mapHud.AddChild(_hoverTip);

        _flashLabel = HudStyle.Label("", 15, HudStyle.Text);
        _flashLabel.AnchorLeft = 0.5f;
        _flashLabel.AnchorRight = 0.5f;
        _flashLabel.AnchorTop = 1;
        _flashLabel.AnchorBottom = 1;
        _flashLabel.OffsetLeft = -330;
        _flashLabel.OffsetRight = 330;
        _flashLabel.OffsetTop = -128;
        _flashLabel.OffsetBottom = -96;
        _flashLabel.HorizontalAlignment = HorizontalAlignment.Center;
        _flashLabel.VerticalAlignment = VerticalAlignment.Center;
        _flashLabel.AddThemeStyleboxOverride("normal", HudStyle.Box(HudStyle.CardBg, HudStyle.LineSoft, 1, 6, 14, 6));
        _flashLabel.Visible = false;
        _mapHud.AddChild(_flashLabel);

        // The goal-complete toast, top centre.
        _toastLabel = HudStyle.Label("", 15, HudStyle.Gold);
        _toastLabel.AnchorLeft = 0.5f;
        _toastLabel.AnchorRight = 0.5f;
        _toastLabel.OffsetLeft = -260;
        _toastLabel.OffsetRight = 260;
        _toastLabel.OffsetTop = TopBar.Height + 52;
        _toastLabel.HorizontalAlignment = HorizontalAlignment.Center;
        _toastLabel.AddThemeStyleboxOverride("normal", HudStyle.Box(HudStyle.CardBg, HudStyle.Gold, 1, 6, 14, 6));
        _toastLabel.Visible = false;
        _mapHud.AddChild(_toastLabel);

        _hudTop = new TopBar { Name = "HudTopBar" };
        _hudTop.MenuPressed = OpenPauseMenu;
        _hudTop.TechPressed = () => TogglePanel(_techTree);
        _hudTop.BuildPressed = OpenBuildViewForSelection;
        _hudTop.OverlayPressed = ToggleOverlay;
        Insert(_hudTop);

        // Moments sit above the map HUD and the full-screen views but below dialogs and the pause menu.
        _moments = new MomentPopup { Name = "Moments" };
        _moments.FocusRequested = FocusOnTile;
        ui.AddChild(_moments);
        ui.MoveChild(_moments, _buildView.GetIndex() + 1);

        _confirm = new ConfirmationDialog { OkButtonText = "Yes", CancelButtonText = "No" };
        _confirm.Confirmed += () => { var a = _confirmAction; _confirmAction = null; a?.Invoke(); };
        AddChild(_confirm);

        _pathPreview = new PathPreview { Name = "PathPreview" };
        _mapView.AddChild(_pathPreview);
        _pathPreview.Bind((x, z) => _mapView.HeightAt(new Vector3(x, 0, z)));
    }

    private PanelContainer BuildOverlayLegend()
    {
        var panel = new PanelContainer { Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
        panel.AddThemeStyleboxOverride("panel", HudStyle.Box(HudStyle.CardBg with { A = 0.86f }, HudStyle.LineSoft, 1, 6, 10, 6));
        var col = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        col.AddChild(HudStyle.Kicker("Growth overlay"));
        foreach (var (color, text) in new[]
        {
            (new Color(0.35f, 0.85f, 0.40f), "hamlets can appear here"),
            (new Color(0.95f, 0.80f, 0.35f), "could, with an edict or road"),
            (new Color(0.35f, 0.45f, 0.75f), "unlikely"),
        })
        {
            var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
            row.AddThemeConstantOverride("separation", 6);
            row.AddChild(new ColorRect { Color = color, CustomMinimumSize = new Vector2(12, 12), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter, MouseFilter = Control.MouseFilterEnum.Ignore });
            row.AddChild(HudStyle.Label(text, 12, HudStyle.TextSoft));
            col.AddChild(row);
        }
        panel.AddChild(col);
        return panel;
    }

    private void Confirm(string title, string text, System.Action onYes)
    {
        _confirm.Title = title;
        _confirm.DialogText = text;
        _confirmAction = onYes;
        _confirm.PopupCentered(new Vector2I(460, 150));
    }

    // --- panels and toggles -------------------------------------------------------------------------------

    private void TogglePanel(Control panel) => ShowPanel(panel.Visible ? null : panel);

    private void OpenBuildViewForSelection()
    {
        if (_buildView.Visible) { ShowPanel(null); return; }
        if (_selectedSettlementId >= 0 && _world.Settlements.Find(s => s.Id == _selectedSettlementId)?.OwnerId == PlayerCiv.Id)
            _buildViewSettlementId = _selectedSettlementId;
        ShowPanel(_buildView);
        RefreshAll();
    }

    private void ToggleOverlay()
    {
        _overlayOn = !_overlayOn;
        _mapView.Overlay = _overlayOn ? OverlayMode.Growth : OverlayMode.None;
        _mapView.RefreshTileColors();
        _overlayLegend.Visible = _overlayOn;
        RefreshHud();
    }

    private void FocusOnTile(int col, int row)
    {
        ShowPanel(null);
        _camera.FocusOn(col, row);
    }

    /// <summary>A short message above the bottom edge — an order that could not be given, and why.</summary>
    private void Flash(string text, bool bad = true)
    {
        if (bad) Audio.Sfx.Play(Audio.Sound.Error);
        _flashLabel.Text = text;
        _flashLabel.AddThemeColorOverride("font_color", bad ? new Color(1f, 0.82f, 0.76f) : HudStyle.Text);
        _flashLabel.Visible = true;
        _flashLabel.Modulate = Colors.White;
        _flashTween?.Kill();
        _flashTween = CreateTween();
        _flashTween.TweenInterval(2.2);
        _flashTween.TweenProperty(_flashLabel, "modulate:a", 0f, 0.6);
        _flashTween.TweenCallback(Callable.From(() => _flashLabel.Visible = false));
        GD.Print($"GameSession: {text}");
    }

    private void SetHoverTip(string text)
    {
        _hoverTip.Text = text;
        _hoverTip.Visible = text != "";
    }

    public override void _Process(double delta)
    {
        if (_hoverTip is not null && _hoverTip.Visible)
        {
            var mouse = GetViewport().GetMousePosition();
            _hoverTip.Position = mouse + new Vector2(18, 20);
        }
    }

    // --- the HUD refresh --------------------------------------------------------------------------------

    private void RefreshHud()
    {
        var player = PlayerCiv;
        var era = EraClock.Compute(_content, player.Tech.CompletedTechIds, _world.Turn, _eraEnteredTurn);
        _hudTop.Load(BuildTopBarModel(player, era));
        _rivals.Load(BuildRivals(player));
        LoadGoal();
        var (state, title, sub) = ComputeEndTurnState();
        _endTurn.SetState(state, title, sub);
        _feed.SetTurn(_world.Turn);
        _mapLabels.UnitFlags = BuildUnitFlags();
    }

    private Dictionary<int, MapLabels.UnitFlag> BuildUnitFlags()
    {
        var needs = UnitsNeedingOrders().Select(u => u.Id).ToHashSet();
        var result = new Dictionary<int, MapLabels.UnitFlag>();
        foreach (var u in _lastSnapshot?.Units ?? new List<UnitViewDto>())
        {
            bool scout = _content.Units.Find(d => d.Id == u.DefId)?.IsScout == true;
            result[u.Id] = new MapLabels.UnitFlag(CivColorFor(u.OwnerId), scout, needs.Contains(u.Id), u.Id == _selectedUnitId);
        }
        return result;
    }

    private TopBar.Model BuildTopBarModel(Civilization player, EraClock.Result era)
    {
        var owned = _world.Settlements.Where(s => s.OwnerId == player.Id).ToList();
        long population = owned.Sum(s => s.Population);
        double production = owned.Sum(ProductionRate);
        double rate = ComputeResearchRate(player).ToDouble();
        var model = new TopBar.Model
        {
            Population = population,
            Production = production,
            Gold = player.Gold.ToDouble(),
            GoldPerTurn = player.GoldPerTurn.ToDouble(),
            ResearchRate = rate,
            TurnText = $"Turn {_world.Turn}  ·  {era.YearText}  ·  {era.EraName}",
            OverlayOn = _overlayOn,
            PopulationTooltip = $"{population:N0} people in your {owned.Count} settlement{(owned.Count == 1 ? "" : "s")}. " +
                                "Gold and research both grow with them.",
            ProductionTooltip = "Production a turn across your settlements. Each spends its own on what it is building.",
            GoldTooltip = $"Treasury {player.Gold.ToDouble():0} gold, {HudStyle.Signed(player.GoldPerTurn.ToDouble())} a turn after edict upkeep. " +
                          "Gold pays for founding settlements, edicts, roads, promotions and units.",
            ResearchTooltip = $"Research: {rate:0.0} a turn (2 + population / 50). Click to open the tech tree.",
        };
        if (player.Tech.ResearchTargetId is string id && _content.Techs.Find(t => t.Id == id) is { } tech)
        {
            double points = player.ResearchPoints.ToDouble();
            model.ResearchName = tech.Name;
            model.ResearchProgress = tech.Cost > 0 ? points / tech.Cost : 1;
            model.ResearchTurns = rate > 0 ? System.Math.Max(1, (int)System.Math.Ceiling(System.Math.Max(0, tech.Cost - points) / rate)) : -1;
            model.QueuedAfter = player.Tech.ResearchQueue.Count;
            model.ResearchTooltip = $"Researching {tech.Name}: {points:0}/{tech.Cost}, {rate:0.0} a turn. " +
                                    (model.QueuedAfter > 0 ? $"{model.QueuedAfter} more queued after it. " : "") +
                                    "Click to open the tech tree.";
        }
        return model;
    }

    private List<RivalsPanel.Rival> BuildRivals(Civilization player)
    {
        var list = new List<RivalsPanel.Rival>();
        var knownSettlements = _lastSnapshot?.Settlements ?? new List<SettlementViewDto>();
        foreach (var civ in _world.Civilizations.OrderBy(c => c.Index))
        {
            bool isPlayer = civ.Id == player.Id;
            bool met = isPlayer || player.MetCivIds.Contains(civ.Id);
            var rival = new RivalsPanel.Rival
            {
                CivId = civ.Id,
                Name = CivNaming.NameOf(_content, civ.Index),
                Leader = CivNaming.LeaderOf(_content, civ.Index),
                Color = CivColorFor(civ.Id),
                Met = met,
                IsPlayer = isPlayer,
            };
            if (!isPlayer && met)
            {
                int known = knownSettlements.Count(s => s.OwnerId == civ.Id);
                string name = CivNaming.NameOf(_content, civ.Index);
                if (_world.Wars.IsAtWar(player.Id, civ.Id))
                {
                    rival.Status = "War";
                    rival.StatusColor = HudStyle.Bad;
                    int quiet = _world.Wars.TurnsSinceFighting(player.Id, civ.Id, _world.Turn);
                    rival.Detail = $"You are at war with {name}. {quiet} turn{(quiet == 1 ? "" : "s")} since the last fighting; " +
                                   $"peace is possible after {WarState.PeaceAfterQuietTurns}.";
                    var peace = CommandPreview.EvaluateMakePeace(_world, player.Id, civ.Id);
                    int target = civ.Id;
                    rival.Actions.Add(new CardAction
                    {
                        Label = "Make peace",
                        Enabled = peace.Legal,
                        Reason = peace.Legal ? "" : peace.Reason,
                        Kind = ButtonKind.Primary,
                        Tooltip = $"End the war. A treaty then holds for {WarState.TreatyTurns} turns.",
                        Commit = () => { if (_world.TryMakePeace(PlayerCiv.Id, target)) { MarkDirty(); RefreshAll(); } },
                    });
                }
                else
                {
                    int treaty = _world.Wars.TreatyTurnsLeft(player.Id, civ.Id, _world.Turn);
                    rival.Status = treaty > 0 ? $"Treaty {treaty}" : "Peace";
                    rival.StatusColor = treaty > 0 ? HudStyle.Info : HudStyle.Good;
                    rival.Detail = known == 0
                        ? $"{rival.Leader} leads {name}. You have not seen any of their settlements yet."
                        : $"{rival.Leader} leads {name}. You know of {known} of their settlement{(known == 1 ? "" : "s")}.";
                    if (WonderProgressOf(civ, out _) is double wonderProgress)
                        rival.Detail += $" They are building the wonder: {wonderProgress * 100:0}% done.";
                    var war = CommandPreview.EvaluateDeclareWar(_world, player.Id, civ.Id);
                    int target = civ.Id;
                    string cap = CivNaming.CapitalisedNameOf(_content, civ.Index);
                    rival.Actions.Add(new CardAction
                    {
                        Label = "Declare war",
                        Enabled = war.Legal,
                        Reason = war.Legal ? "" : war.Reason,
                        Kind = ButtonKind.Danger,
                        Tooltip = "Your units may then attack theirs and pillage or raze their settlements — and they yours.",
                        Commit = () => Confirm("Declare war?", $"Declare war on {name}? {cap} will fight back, and peace needs " +
                                                               $"{WarState.PeaceAfterQuietTurns} quiet turns.",
                            () => { if (_world.TryDeclareWar(PlayerCiv.Id, target)) { MarkDirty(); RefreshAll(); } }),
                    });
                }
            }
            list.Add(rival);
        }
        return list;
    }

    private void LoadGoal()
    {
        var completed = _objectivesState.Completed;
        var next = Objectives.All.FirstOrDefault(o => !completed.Contains(o.Id));
        int index = next is null ? Objectives.All.Count : Objectives.All.ToList().IndexOf(next) + 1;
        if (next is null) _goal.Load("All goals complete", Objectives.KeepGoingSentence, "");
        else _goal.Load($"Goal {index} of {Objectives.All.Count}", next.Title, next.Hint);
    }

    // --- smart End Turn -----------------------------------------------------------------------------------

    private List<Unit> UnitsNeedingOrders()
    {
        var player = PlayerCiv;
        return _world.Units
            .Where(u => u.OwnerId == player.Id && u.MovementRemaining > 0 && u.Destination is null && !u.IsGarrisoned
                        && !_sleepingUnits.Contains(u.Id) && !_skippedUnitsThisTurn.Contains(u.Id) && !_exploringUnits.Contains(u.Id))
            .OrderBy(u => u.Id)
            .ToList();
    }

    private List<Settlement> SettlementsNeedingProduction()
    {
        var player = PlayerCiv;
        return _world.Settlements
            .Where(s => s.OwnerId == player.Id && s.Build.Queue.Count == 0)
            .Where(s => s.Id == player.CapitalSettlementId || s.Tier >= SettlementTier.Village || _manuallyTouchedSettlementIds.Contains(s.Id))
            .Where(s => !_dismissedDecisionsThisTurn.Contains($"build:{s.Id}"))
            .Where(s => AvailableBuildingsFor(s).Any(b => !b.Locked))
            .OrderByDescending(s => s.Population).ThenBy(s => s.Id)
            .ToList();
    }

    private bool AnyTechAvailable(Civilization player) =>
        _content.Techs.Any(t => ResearchEngine.IsAvailable(player.Tech, t.Id, _content));

    private (EndTurnPanel.State State, string Title, string Sub) ComputeEndTurnState()
    {
        var player = PlayerCiv;
        if (player.Tech.ResearchTargetId is null && AnyTechAvailable(player))
            return (EndTurnPanel.State.Research, "Choose research", "Your scholars have nothing to study");
        var units = UnitsNeedingOrders();
        if (units.Count > 0)
        {
            var u = units[0];
            string name = _world.UnitDefOf(u)?.Name ?? u.DefId;
            string where = NearestOwnedSettlementName(u.Location) is string place ? $" near {place}" : "";
            return (EndTurnPanel.State.UnitOrders, units.Count == 1 ? "A unit needs orders" : $"{units.Count} units need orders", $"{name}{where}");
        }
        var idle = SettlementsNeedingProduction();
        if (idle.Count > 0)
            return (EndTurnPanel.State.Production, "Choose production", $"{MapPalette.SettlementName(idle[0].Id)} is building nothing");
        return (EndTurnPanel.State.NextTurn, "Next turn", $"End turn {_world.Turn}  ·  Enter");
    }

    private string? NearestOwnedSettlementName(HexCoord tile)
    {
        var s = _world.Settlements.Where(x => x.OwnerId == PlayerCiv.Id)
            .OrderBy(x => HexCoord.Distance(x.Location, tile)).ThenBy(x => x.Id).FirstOrDefault();
        return s is null ? null : MapPalette.SettlementName(s.Id);
    }

    /// <summary>The End Turn button (and Enter): go to whatever is waiting, or end the turn.</summary>
    private void OnEndTurnPressed()
    {
        var (state, _, _) = ComputeEndTurnState();
        switch (state)
        {
            case EndTurnPanel.State.Research:
                _researchPromptRequested = true;
                _decisionPrompt.Present(new List<PendingDecision> { BuildResearchDecision(PlayerCiv) });
                break;
            case EndTurnPanel.State.UnitOrders:
                var unit = UnitsNeedingOrders()[0];
                FocusOnTile(unit.Location.Col, unit.Location.Row);
                SelectUnit(unit.Id);
                break;
            case EndTurnPanel.State.Production:
                FocusAndSelectSettlement(SettlementsNeedingProduction()[0].Id);
                break;
            default:
                CommitEndTurn();
                break;
        }
    }

    // --- selection --------------------------------------------------------------------------------------

    private void OnHexClicked(int col, int row, Vector3 hit)
    {
        var tile = new HexCoord(col, row);
        if (_foundingMode)
        {
            TryFoundAt(tile);
            return;
        }

        var snapshot = _lastSnapshot;
        int? figure = _unitView.FindNearestUnitId(hit, 0.30f);
        var settlement = snapshot?.Settlements.Find(s => s.Col == col && s.Row == row);
        var unitOnHex = snapshot?.Units.Find(u => u.Col == col && u.Row == row);

        // A selected unit plus a click on an empty blue tile: go there now.
        if (_selectedUnitId >= 0 && figure is null && settlement is null && unitOnHex is null
            && _reachableTiles.Contains(tile.Index(_world.Grid.Width)))
        {
            OrderUnit(tile);
            return;
        }
        if (figure is int id && id != _selectedUnitId)
        {
            SelectUnit(id);
            return;
        }
        if (settlement is not null)
        {
            _selectionController.SelectSettlement(col, row);
            return;
        }
        if (unitOnHex is not null && unitOnHex.Id != _selectedUnitId)
        {
            SelectUnit(unitOnHex.Id);
            return;
        }
        SelectTile(tile);
    }

    private void OnHexRightClicked(int col, int row)
    {
        if (_foundingMode)
        {
            ExitFoundingMode();
            return;
        }
        if (_selectedUnitId < 0) return;
        OrderUnit(new HexCoord(col, row));
    }

    private void OnHexHovered(int col, int row)
    {
        _hover = (col, row);
        if (col < 0)
        {
            _mapView.ClearHoverHex();
            _pathPreview.Hide3D();
            SetHoverTip("");
            return;
        }
        _mapView.SetHoverHex(col, row);
        var tile = new HexCoord(col, row);
        var player = PlayerCiv;

        if (_foundingMode)
        {
            _pathPreview.Hide3D();
            var preview = CommandPreview.EvaluateFoundCharter(_world, player.Id, player.CapitalSettlementId, tile);
            SetHoverTip(preview.Legal ? $"Found a settlement here ({preview.GoldCost.ToDouble():0} gold) · site {SiteQuality(tile)}" : preview.Reason);
            return;
        }

        var unit = _selectedUnitId >= 0 ? _world.Units.Find(u => u.Id == _selectedUnitId) : null;
        if (unit is null || unit.OwnerId != player.Id || _world.UnitDefOf(unit) is not { } def)
        {
            _pathPreview.Hide3D();
            SetHoverTip("");
            return;
        }
        if (unit.Location.Equals(tile))
        {
            _pathPreview.Hide3D();
            SetHoverTip("");
            return;
        }
        var knowledge = _world.KnowledgeFor(player.Id);
        var problem = UnitMovement.DestinationProblem(_world, unit, def, tile);
        if (problem is not null && knowledge?.IsExplored(tile) != false)
        {
            _pathPreview.Hide3D();
            SetHoverTip(problem);
            return;
        }
        var route = problem is null ? UnitMovement.PlanRoute(_world, unit, def, tile) : null;
        if (route is null || route.Count == 0)
        {
            _pathPreview.Hide3D();
            SetHoverTip(knowledge?.IsExplored(tile) == false ? "Unexplored" : "No way through from here");
            return;
        }
        bool attack = IsHostileTileForPlayer(tile);
        _pathPreview.ShowRoute(unit.Location.Col, unit.Location.Row,
            route.Select(r => new PathPreview.Step(r.Tile.Col, r.Tile.Row, r.Turn)).ToList(), attack);
        int turns = route[^1].Turn + 1;
        SetHoverTip(attack ? (turns == 1 ? "Attack" : $"Attack · {turns} turns") : turns == 1 ? "" : $"{turns} turns");
    }

    private bool IsHostileTileForPlayer(HexCoord tile)
    {
        int player = PlayerCiv.Id;
        var other = _world.Units.Find(u => u.Location.Equals(tile));
        if (other is not null) return other.OwnerId != player;
        var s = _world.Settlements.Find(x => x.Location.Equals(tile));
        return s?.OwnerId is int o && o != player;
    }

    private void SelectUnit(int unitId)
    {
        Audio.Sfx.Play(Audio.Sound.Select);
        if (_foundingMode) ExitFoundingMode();
        _selectedUnitId = unitId;
        _selectedSettlementId = -1;
        _selectedTile = null;
        _unitView.SetSelected(unitId);
        RefreshSelection();
        _selectionController.RefreshHover();
    }

    private void SelectTile(HexCoord tile)
    {
        _selectedUnitId = -1;
        _selectedSettlementId = -1;
        _selectedTile = tile;
        _unitView.SetSelected(-1);
        _mapView.SetSelectedHex(tile.Col, tile.Row);
        RefreshSelection();
    }

    private void ClearSelection()
    {
        _selectedUnitId = -1;
        _selectedSettlementId = -1;
        _selectedTile = null;
        _unitView.SetSelected(-1);
        _mapView.ClearSelectedHex();
        _pathPreview.Hide3D();
        SetHoverTip("");
        RefreshSelection();
    }

    /// <summary>Re-reads whatever is selected from the live world and redraws the card and overlays —
    /// after every refresh, since a turn or an order can change everything on it.</summary>
    private void RefreshSelection()
    {
        var player = PlayerCiv;
        if (_selectedUnitId >= 0)
        {
            var unit = _world.Units.Find(u => u.Id == _selectedUnitId);
            bool visible = unit is not null && (unit.OwnerId == player.Id || _lastSnapshot?.Units.Any(u => u.Id == unit.Id) == true);
            if (!visible)
            {
                _selectedUnitId = -1;
                _unitView.SetSelected(-1);
                _mapView.ClearSelectedHex();
                _card.Clear();
            }
            else
            {
                _mapView.SetSelectedHex(unit!.Location.Col, unit.Location.Row);
                _card.Load(BuildUnitCard(unit));
            }
        }
        else if (_selectedSettlementId >= 0)
        {
            var view = _lastSnapshot?.Settlements.Find(s => s.Id == _selectedSettlementId);
            if (view is null) { _selectedSettlementId = -1; _card.Clear(); }
            else _card.Load(BuildSettlementCard(view));
        }
        else if (_selectedTile is HexCoord tile)
        {
            _card.Load(BuildTileCard(tile));
        }
        else
        {
            _card.Clear();
        }
        ApplySelectionOverlay();
        _mapLabels.UnitFlags = BuildUnitFlags();
    }

    /// <summary>The map wash for the current mode: founding sites in green, or the selected unit's
    /// reachable tiles in blue with attackable neighbours in red.</summary>
    private void ApplySelectionOverlay()
    {
        _reachableTiles.Clear();
        var player = PlayerCiv;
        int width = _world.Grid.Width;
        Dictionary<int, byte>? overlay = null;
        if (_foundingMode)
        {
            // Legal sites, coloured by how good a home they would make: green good, yellow fair,
            // blue poor — the same viability the growth overlay shows.
            overlay = new Dictionary<int, byte>();
            var dto = _lastSnapshot;
            foreach (var t in CommandPreview.LegalFoundingSites(_world, player.Id))
            {
                int i = t.Index(width);
                double v = dto is not null && i < dto.Viability.Length ? dto.Viability[i] : 0;
                overlay[i] = dto is null ? (byte)4 : v >= dto.SpawnThreshold ? (byte)4 : v >= dto.SpawnThreshold - 0.4 ? (byte)2 : (byte)3;
            }
        }
        else if (_selectedUnitId >= 0 && _world.Units.Find(u => u.Id == _selectedUnitId) is { } unit
                 && unit.OwnerId == player.Id && _world.UnitDefOf(unit) is { } def)
        {
            overlay = new Dictionary<int, byte>();
            var reach = UnitMovement.ReachableThisTurn(_world, unit, def);
            foreach (var t in reach)
            {
                int i = t.Index(width);
                _reachableTiles.Add(i);
                overlay[i] = 6;
            }
            if (!def.IsScout && unit.MovementRemaining > 0)
            {
                var from = new List<HexCoord>(reach) { unit.Location };
                foreach (var f in from)
                {
                    foreach (var n in f.Neighbors(width, _world.Grid.Height))
                    {
                        if (!IsHostileTileForPlayer(n)) continue;
                        int owner = _world.Units.Find(u => u.Location.Equals(n))?.OwnerId
                                    ?? _world.Settlements.Find(s => s.Location.Equals(n))?.OwnerId ?? -1;
                        if (owner >= 0 && _world.Wars.IsAtWar(player.Id, owner)
                            && _world.KnowledgeFor(player.Id)?.IsVisible(n) == true) overlay[n.Index(width)] = 7;
                    }
                }
            }
        }
        _mapView.SetTileOverlay(overlay);
    }

    // --- unit orders --------------------------------------------------------------------------------------

    private void OrderUnit(HexCoord tile)
    {
        var player = PlayerCiv;
        var unit = _world.Units.Find(u => u.Id == _selectedUnitId);
        if (unit is null || unit.OwnerId != player.Id || _world.UnitDefOf(unit) is not { } def) return;
        if (unit.Location.Equals(tile)) return;
        var problem = UnitMovement.DestinationProblem(_world, unit, def, tile);
        if (problem is not null)
        {
            Flash(problem);
            return;
        }
        _exploringUnits.Remove(unit.Id);
        _sleepingUnits.Remove(unit.Id);
        _skippedUnitsThisTurn.Remove(unit.Id);
        if (!_world.TrySetUnitDestination(player.Id, unit.Id, tile))
        {
            Flash("There is no way through from here.");
            return;
        }
        Audio.Sfx.Play(Audio.Sound.Order);
        MarkDirty();
        RefreshAll();
        _selectionController.RefreshHover();
    }

    /// <summary>Auto-explore (a UI order, not a sim one): each turn, every exploring unit without a
    /// destination is sent to the nearest known ruin or the nearest edge of the map you know — through
    /// the same recorded <see cref="World.TrySetUnitDestination"/> a click uses, so saves and replays
    /// see ordinary move orders.</summary>
    private void RunAutoExplore()
    {
        var player = PlayerCiv;
        var knowledge = _world.KnowledgeFor(player.Id);
        if (knowledge is null) return;
        foreach (int id in _exploringUnits.OrderBy(i => i).ToList())
        {
            // Up to four legs a turn: a leg that arrives with moves to spare picks the next target
            // at once, instead of standing idle until next turn.
            for (int leg = 0; leg < 4; leg++)
            {
                var unit = _world.Units.Find(u => u.Id == id);
                if (unit is null || unit.OwnerId != player.Id) { _exploringUnits.Remove(id); break; }
                if (unit.Destination is not null || unit.MovementRemaining <= 0) break;
                if (!SendExploring(unit, knowledge))
                {
                    _exploringUnits.Remove(id);
                    var def = _world.UnitDefOf(unit);
                    _feed.Add(new EventFeed.Entry
                    {
                        Text = $"Your {def?.Name ?? "unit"} has nothing left to explore nearby.",
                        Icon = Icon.Compass, Color = HudStyle.TextSoft, Turn = _world.Turn,
                        FocusCol = unit.Location.Col, FocusRow = unit.Location.Row,
                    });
                    break;
                }
                MarkDirty();
            }
        }
    }

    /// <summary>One exploring leg: the nearest known unexplored ruin, or else the edge-of-the-known
    /// tile (among the nearest thirty) that would reveal the most unexplored ground for the walk —
    /// deterministic, ties to the nearer tile.</summary>
    private bool SendExploring(Unit unit, CivKnowledge knowledge)
    {
        var player = PlayerCiv;
        if (CivAi.NearestKnownRuin(_world, knowledge, unit.Location, CivAi.RuinSeekRange + 2) is HexCoord ruin
            && _world.TrySetUnitDestination(player.Id, unit.Id, ruin)) return true;

        var grid = _world.Grid;
        var candidates = CivAi.FrontierTiles(_world, knowledge, unit.Location, 30)
            .Select((tile, order) =>
            {
                int unknown = 0;
                foreach (var t in EdictState.TilesInRadius(tile, 2, grid)) if (!knowledge.IsExplored(t)) unknown++;
                int distance = HexCoord.Distance(unit.Location, tile);
                return (Tile: tile, Order: order, Score: unknown * 10 - distance * 4);
            })
            .OrderByDescending(c => c.Score).ThenBy(c => c.Order)
            .ToList();
        foreach (var c in candidates)
        {
            if (_world.TrySetUnitDestination(player.Id, unit.Id, c.Tile)) return true;
        }
        return false;
    }

    // --- founding ---------------------------------------------------------------------------------------

    private void EnterFoundingMode()
    {
        var player = PlayerCiv;
        _selectedUnitId = -1;
        _selectedSettlementId = -1;
        _selectedTile = null;
        _unitView.SetSelected(-1);
        _mapView.ClearSelectedHex();
        _card.Clear();
        _foundingMode = true;
        double cost = Charters.FoundingCostFor(Charters.OwnedCount(_world.Settlements, player.Id)).ToDouble();
        _modeBanner.Text = $"Found a settlement ({cost:0} gold): green sites are good, yellow fair, blue poor.  Esc or right-click to cancel";
        _modeBanner.Visible = true;
        ApplySelectionOverlay();
        _selectionController.RefreshHover();
    }

    private void ExitFoundingMode()
    {
        if (!_foundingMode) return;
        _foundingMode = false;
        _modeBanner.Visible = _selectionController.IsTargetingRoad;
        SetHoverTip("");
        ApplySelectionOverlay();
    }

    private void TryFoundAt(HexCoord tile)
    {
        var player = PlayerCiv;
        var preview = CommandPreview.EvaluateFoundCharter(_world, player.Id, player.CapitalSettlementId, tile);
        if (!preview.Legal)
        {
            Flash(preview.Reason);
            return;
        }
        if (!_world.TryFoundCharter(player.Id, player.CapitalSettlementId, tile)) return;
        Audio.Sfx.Play(Audio.Sound.Bell, -6f);
        ExitFoundingMode();
        MarkDirty();
        RefreshAll();
        _selectionController.SelectSettlement(tile.Col, tile.Row);
    }

    /// <summary>"good" / "fair" / "poor", from the same viability the growth overlay shows.</summary>
    private string SiteQuality(HexCoord tile)
    {
        var dto = _lastSnapshot;
        if (dto is null) return "unknown";
        int i = tile.Index(_world.Grid.Width);
        if (i < 0 || i >= dto.Viability.Length || dto.Viability[i] < 0) return "unknown";
        double v = dto.Viability[i];
        return v >= dto.SpawnThreshold ? $"good ({v:0.0})" : v >= dto.SpawnThreshold - 0.4 ? $"fair ({v:0.0})" : $"poor ({v:0.0})";
    }

    // --- cards ------------------------------------------------------------------------------------------

    private SelectionCard.Model BuildSettlementCard(SettlementViewDto view)
    {
        var player = PlayerCiv;
        var live = _world.Settlements.Find(s => s.Id == view.Id);
        bool mine = view.OwnerId == player.Id && live is not null;
        var tier = (SettlementTierName)view.Tier;
        string owner = view.OwnerId is int ownerId
            ? ownerId == player.Id
                ? (view.Id == player.CapitalSettlementId ? "your capital" : "yours")
                : CivNaming.AdjectiveOf(_content, CivIndexOf(ownerId)) + (_world.CivById(ownerId)?.CapitalSettlementId == view.Id ? " capital" : "")
            : "independent freehold";
        var model = new SelectionCard.Model
        {
            Key = $"settlement:{view.Id}",
            Title = MapPalette.SettlementName(view.Id),
            Subtitle = view.Stale ? $"{tier} · {owner} · last seen turn {view.SeenOnTurn}" : $"{tier} · {owner}",
            Accent = view.OwnerId is int o ? CivColorFor(o) : MapView.FreeholdColor,
            TitleIcon = Icon.House,
        };

        double rate = view.GrowthRatePerTurn;
        long cap = view.Capacity;
        string growth = tier == SettlementTierName.Ruins ? "in ruins"
            : rate > 0.0005 ? $"+{rate * 100:0.0}% a turn" : rate < -0.0005 ? $"{rate * 100:0.0}% a turn" : "not growing";
        model.Rows.Add(new CardRow
        {
            Icon = Icon.Population,
            Text = cap > 0 ? $"{view.Population:N0} people · room for {cap:N0} · {growth}" : $"{view.Population:N0} people · {growth}",
            Progress = cap > 0 ? System.Math.Min(1.0, view.Population / (double)cap) : null,
            BarColor = rate < 0 ? HudStyle.Bad : HudStyle.Good,
            Tooltip = "How many people the land here can feed rises with food, water, safety and trade. Growth slows as a " +
                      "settlement fills that room: raise the four sources below, with buildings and edicts, to make more.",
        });
        if (tier != SettlementTierName.Ruins && !view.Stale)
            model.Rows.Add(new CardRow { Icon = Icon.Star, Text = BuildNextTierLine(tier, view.Population, rate), FontSize = 12, Color = HudStyle.TextSoft });
        if (!view.Stale)
        {
            model.Rows.Add(new CardRow
            {
                Chips = new List<(Icon, string, string)>
                {
                    (Icon.Food, $"{view.ViabilityFood:0.00}", "Food: the fertility of the land around it."),
                    (Icon.Water, $"{view.ViabilityWater:0.00}", "Water: rivers, lakes and the sea nearby."),
                    (Icon.Safety, $"{view.ViabilitySafety:0.00}", "Safety: walls, garrisons, distance from enemies."),
                    (Icon.Trade, $"{view.ViabilityTrade:0.00}", "Trade: roads and neighbours to trade with."),
                },
            });
        }

        if (mine && live is not null)
        {
            AddOwnSettlementRows(model, live);
            AddOwnSettlementActions(model, live);
            model.Hint = live.Build.Queue.Count == 0
                ? "Nothing is being built here. Production banks until the bank is full, then sells for a quarter of its worth. Pick something above, or open the build list (B)."
                : "Gold can finish the current build at once. Found settlement picks a site within four tiles of any of yours; B opens the build list.";
        }
        else
        {
            model.Hint = view.OwnerId is int rival && rival != player.Id
                ? (_world.Wars.IsAtWar(player.Id, rival)
                    ? "At war: move a fighting unit onto it to pillage it; a unit standing in it can raze it."
                    : "Their settlements grow on their own. War lets your units pillage and raze them.")
                : "Freeholds join whoever's border grows over them.";
        }
        return model;
    }

    private void AddOwnSettlementRows(SelectionCard.Model model, Settlement s)
    {
        double production = ProductionRate(s);
        if (s.Build.Queue.Count > 0)
        {
            var def = _content.Buildings.Find(b => b.Id == s.Build.Queue[0]);
            double cost = def?.Cost ?? 1;
            double invested = s.Build.InvestedProduction.ToDouble();
            int turns = ComputeTurnsToComplete(production, (int)System.Math.Ceiling(System.Math.Max(0, cost - invested)));
            string then = s.Build.Queue.Count > 1
                ? $" · then {string.Join(", ", s.Build.Queue.Skip(1).Take(2).Select(id => _content.Buildings.Find(b => b.Id == id)?.Name ?? id))}"
                : "";
            model.Rows.Add(new CardRow
            {
                Icon = Icon.Production,
                Text = $"Building {def?.Name ?? s.Build.Queue[0]} · {HudStyle.Turns(turns)}{then}",
                Progress = cost > 0 ? invested / cost : 0,
                BarColor = HudStyle.Info,
                Tooltip = $"{HudStyle.Signed(production)} production a turn. {(def is null ? "" : ContentIndex.EffectText(def))}",
            });
        }
        else
        {
            double stored = s.Ledgers.StoredProduction.ToDouble();
            double cap = Hearthspan.Sim.Settlements.ConstructionEngine.BankCapFor(Hearthspan.Sim.Economy.EconomyEngine.ProductionFor(
                s, _content, s.OwnerId is int bankOwner ? _world.EconomyBonusFor(bankOwner) : default)).ToDouble();
            model.Rows.Add(new CardRow
            {
                Icon = Icon.Production,
                Text = $"Building nothing · {HudStyle.Signed(production)} production a turn banks here ({stored:0} of {cap:0})",
                Progress = cap > 0 ? stored / cap : 0,
                BarColor = stored >= cap - 0.5 ? HudStyle.Bad : HudStyle.Info,
                Color = HudStyle.Gold,
                Tooltip = "Banked production finishes the next thing you queue here. The bank holds five turns' worth; " +
                          "past that the surplus is sold as trade goods for a quarter of its value in gold.",
            });
        }
        if (s.Ledgers.TradeGoods.ToDouble() > 0.05)
        {
            model.Rows.Add(new CardRow
            {
                Icon = Icon.Trade,
                Text = $"Bank full: surplus sold as trade goods, {HudStyle.Signed(s.Ledgers.TradeGoods.ToDouble())} gold last turn",
                FontSize = 12,
                Color = HudStyle.TextSoft,
            });
        }
        model.Rows.Add(new CardRow
        {
            Icon = Icon.Gold,
            Text = $"{HudStyle.Signed(GoldRate(s))} gold a turn to the treasury",
            FontSize = 12,
            Color = HudStyle.TextSoft,
        });
        var edicts = _world.Edicts.Active.Where(e => e.SettlementId == s.Id)
            .Select(e => _content.Edicts.Find(d => d.Id == e.EdictId)?.Name ?? e.EdictId).ToList();
        if (edicts.Count > 0)
            model.Rows.Add(new CardRow { Icon = Icon.Flag, Text = $"Edicts: {string.Join(", ", edicts)}", FontSize = 12, Color = HudStyle.TextSoft });
    }

    private void AddOwnSettlementActions(SelectionCard.Model model, Settlement s)
    {
        var player = PlayerCiv;
        int settlementId = s.Id;

        // An empty queue gets the three cheapest things it could build, one click each.
        if (s.Build.Queue.Count == 0)
        {
            var picks = AvailableBuildingsFor(s).Where(b => !b.Locked).OrderBy(b => b.Cost).ThenBy(b => b.Id).Take(3).ToList();
            foreach (var b in picks)
            {
                string buildingId = b.Id;
                model.Actions.Add(new CardAction
                {
                    Label = $"Build {b.Name}  ({HudStyle.Turns(b.Turns)})",
                    Tooltip = b.Tooltip,
                    Kind = ButtonKind.Primary,
                    Wide = true,
                    Commit = () =>
                    {
                        if (_world.TryEnqueueBuilding(settlementId, buildingId))
                        {
                            _manuallyTouchedSettlementIds.Add(settlementId);
                            MarkDirty();
                            RefreshAll();
                        }
                    },
                });
            }
        }

        if (s.Build.Queue.Count > 0 && Hearthspan.Sim.Settlements.ConstructionEngine.BuyPrice(s, _content) is { } price)
        {
            double cost = price.ToDouble();
            bool canBuy = player.Gold.ToDouble() >= cost;
            string buildName = _content.Buildings.Find(b => b.Id == s.Build.Queue[0])?.Name ?? s.Build.Queue[0];
            model.Actions.Add(new CardAction
            {
                Label = $"Buy {buildName} now ({cost:0}g)",
                Enabled = canBuy,
                Reason = canBuy ? "" : $"Costs {cost:0} gold; your treasury holds {player.Gold.ToDouble():0}.",
                Kind = ButtonKind.Primary,
                Wide = true,
                Tooltip = "Finish it this instant for two gold per production point still owed. The world wonder cannot be bought.",
                Commit = () => { if (_world.TryBuyBuilding(PlayerCiv.Id, settlementId)) { MarkDirty(); RefreshAll(); } },
            });
        }

        model.Actions.Add(new CardAction
        {
            Label = "Build list…",
            Tooltip = "Every building for this settlement, with costs and effects (B).",
            Commit = () => { _buildViewSettlementId = settlementId; ShowPanel(_buildView); RefreshAll(); },
        });

        int owned = Charters.OwnedCount(_world.Settlements, player.Id);
        double foundCost = Charters.FoundingCostFor(owned).ToDouble();
        bool anySite = CommandPreview.LegalFoundingSites(_world, player.Id).Count > 0;
        bool afford = player.Gold.ToDouble() >= foundCost;
        model.Actions.Add(new CardAction
        {
            Label = $"Found settlement ({foundCost:0}g)",
            Enabled = anySite && afford,
            Reason = !anySite ? "No legal site within four tiles of your settlements - explore more of the land around them."
                : !afford ? $"Costs {foundCost:0} gold; your treasury holds {player.Gold.ToDouble():0}." : "",
            Tooltip = "Pick a site within four tiles of any settlement of yours. Each new settlement costs more than the last.",
            Commit = EnterFoundingMode,
        });

        var promo = CommandPreview.EvaluateGrantCharter(_world, player.Id, s.Id);
        if (promo.Legal || s.Tier < SettlementTier.Metropolis)
        {
            model.Actions.Add(new CardAction
            {
                Label = promo.Legal ? $"Promote ({promo.GoldCost.ToDouble():0}g)" : "Promote",
                Enabled = promo.Legal,
                Reason = promo.Legal ? "" : Capitalise(promo.Reason),
                Tooltip = $"Pay to lift this settlement straight to {promo.ResultingPopulation} people, the next tier. " +
                          "Only possible once it is close.",
                Commit = () => { if (_world.TryGrantCharter(PlayerCiv.Id, settlementId)) { MarkDirty(); RefreshAll(); } },
            });
        }

        bool otherOwned = _world.Settlements.Any(x => x.OwnerId == player.Id && x.Id != s.Id);
        if (otherOwned)
        {
            bool anyRoad = _world.Settlements.Any(x => x.OwnerId == player.Id && x.Id != s.Id
                && CommandPreview.EvaluateBuildRoad(_world, player.Id, s.Id, x.Id).Legal);
            model.Actions.Add(new CardAction
            {
                Label = "Road to…",
                Enabled = anyRoad,
                Reason = anyRoad ? "" : "No other settlement of yours can be reached by road from here right now.",
                Tooltip = "Then click another of your settlements. Roads carry trade, and your units move along them for free.",
                Commit = () => BeginRoadTargeting(settlementId),
            });
        }

        foreach (var def in _content.Edicts)
        {
            bool unlocked = def.RequiresTechId is null || player.Tech.IsCompleted(def.RequiresTechId);
            if (!unlocked || _world.Edicts.IsActiveAt(def.Id, s.Id)) continue;
            var quote = CommandPreview.EvaluateEnactEdict(_world, player.Id, s.Id, def.Id);
            string edictId = def.Id;
            model.Actions.Add(new CardAction
            {
                Label = $"{def.Name} ({def.Cost}g)",
                Enabled = quote.Legal,
                Reason = quote.Legal ? "" : PrettyReason(quote.Reason),
                Group = "Edicts",
                Tooltip = EdictTooltip(def),
                Commit = () => { if (_world.TryEnactEdict(PlayerCiv.Id, settlementId, edictId)) { MarkDirty(); RefreshAll(); } },
            });
        }
        foreach (var active in _world.Edicts.Active.Where(e => e.SettlementId == s.Id).ToList())
        {
            int instanceId = active.InstanceId;
            string name = _content.Edicts.Find(e => e.Id == active.EdictId)?.Name ?? active.EdictId;
            model.Actions.Add(new CardAction
            {
                Label = $"Repeal {name}",
                Group = "Edicts",
                Tooltip = "Ends the edict and its upkeep. The gold spent to enact it is not refunded.",
                Commit = () => { if (_world.TryRepealEdict(PlayerCiv.Id, instanceId)) { MarkDirty(); RefreshAll(); } },
            });
        }

        // The scout, and the two strongest fighting units available — older ones are outclassed.
        var unlockedUnits = _content.Units
            .Where(u => u.RequiresTechId is null || player.Tech.IsCompleted(u.RequiresTechId)).ToList();
        var offered = unlockedUnits.Where(u => u.IsScout)
            .Concat(unlockedUnits.Where(u => !u.IsScout).OrderByDescending(u => u.Strength).ThenBy(u => u.Cost).Take(2))
            .OrderBy(u => u.Cost).ToList();
        foreach (var unitDef in offered)
        {
            var quote = CommandPreview.EvaluateTrainUnit(_world, player.Id, s.Id, unitDef.Id);
            string unitId = unitDef.Id;
            model.Actions.Add(new CardAction
            {
                Label = $"Train {unitDef.Name} ({unitDef.Cost}g)",
                Enabled = quote.Legal,
                Reason = quote.Legal ? "" : quote.Reason,
                Tooltip = unitDef.IsScout
                    ? $"Moves {unitDef.Move}, sees {unitDef.Vision} tiles. Explores and finds ruins; cannot fight."
                    : $"Strength {unitDef.Strength}, moves {unitDef.Move}. Defends, garrisons, and attacks rivals at war.",
                Commit = () => { if (_world.TryTrainUnit(PlayerCiv.Id, settlementId, unitId)) { MarkDirty(); RefreshAll(); } },
            });
        }

        if (s.Build.Queue.Count == 0 && SettlementsNeedingProduction().Any(x => x.Id == s.Id))
        {
            model.Actions.Add(new CardAction
            {
                Label = "Leave idle this turn",
                Kind = ButtonKind.Quiet,
                Tooltip = "Stop End Turn asking about this settlement until next turn.",
                Commit = () => { _dismissedDecisionsThisTurn.Add($"build:{settlementId}"); RefreshHud(); RefreshSelection(); },
            });
        }
    }

    private SelectionCard.Model BuildUnitCard(Unit unit)
    {
        var player = PlayerCiv;
        var def = _world.UnitDefOf(unit);
        bool mine = unit.OwnerId == player.Id;
        var owner = _world.CivById(unit.OwnerId);
        string adjective = owner is null ? "" : CivNaming.AdjectiveOf(_content, owner.Index);
        string name = def?.Name ?? unit.DefId;
        var model = new SelectionCard.Model
        {
            Key = $"unit:{unit.Id}",
            Title = name,
            Subtitle = mine ? $"Your {(def?.IsScout == true ? "scout" : "fighting unit")}" : $"{adjective} unit",
            Accent = CivColorFor(unit.OwnerId),
            TitleIcon = def?.IsScout == true ? Icon.Compass : Icon.Strength,
        };
        int maxStrength = def?.Strength ?? unit.Strength;
        model.Rows.Add(new CardRow
        {
            Icon = Icon.Strength,
            Text = def?.IsScout == true ? "Cannot fight" : $"Strength {unit.Strength} of {maxStrength}",
            Progress = def?.IsScout == true ? null : maxStrength > 0 ? unit.Strength / (double)maxStrength : 0,
            BarColor = unit.Strength < maxStrength ? HudStyle.Bad : HudStyle.Good,
            Tooltip = "Strength falls in battle and never heals. A worn-down unit can be disbanded.",
        });
        var tile = unit.Location;
        int defence = TerrainRules.DefenseBonusPercent(_world.Grid, tile);
        model.Rows.Add(new CardRow
        {
            Icon = Icon.Moves,
            Text = mine ? $"{unit.MovementRemaining} of {def?.Move ?? 0} moves left · sees {def?.Vision ?? 2} tiles" : $"Moves {def?.Move ?? 0}",
        });
        model.Rows.Add(new CardRow
        {
            Icon = Icon.Flag,
            Text = $"On {TerrainName(tile).ToLowerInvariant()}{(defence > 0 ? $" · +{defence}% defence" : "")}",
            FontSize = 12,
            Color = HudStyle.TextSoft,
        });

        if (mine)
        {
            string status;
            if (unit.Destination is HexCoord dest && def is not null)
            {
                var route = UnitMovement.PlanRoute(_world, unit, def, dest);
                int turns = route is { Count: > 0 } ? route[^1].Turn + (unit.MovementRemaining > 0 ? 1 : 2) - 1 : -1;
                status = turns <= 0 ? "Marching: arrives next turn" : $"Marching: arrives in about {turns + 1} turns";
                if (_exploringUnits.Contains(unit.Id)) status = "Exploring on its own";
            }
            else if (unit.IsGarrisoned) status = "Garrisoned: guarding this tile";
            else if (_exploringUnits.Contains(unit.Id)) status = "Exploring on its own";
            else if (_sleepingUnits.Contains(unit.Id)) status = "Asleep until you wake it";
            else if (_skippedUnitsThisTurn.Contains(unit.Id)) status = "Waiting this turn";
            else if (unit.MovementRemaining <= 0) status = "Done for this turn";
            else status = "Awaiting orders";
            model.Rows.Add(new CardRow { Icon = Icon.Hourglass, Text = status, Color = status == "Awaiting orders" ? HudStyle.Gold : HudStyle.Text });
            AddUnitActions(model, unit, def);
            model.Hint = "Right-click anywhere to march there; left-click a blue tile to move now. Red tiles can be attacked.";
        }
        else
        {
            bool atWar = _world.Wars.IsAtWar(player.Id, unit.OwnerId);
            model.Hint = atWar ? "At war: attack it with a fighting unit by right-clicking it." : "Not at war with them.";
        }
        return model;
    }

    private void AddUnitActions(SelectionCard.Model model, Unit unit, UnitDef? def)
    {
        int unitId = unit.Id;
        bool exploring = _exploringUnits.Contains(unitId);
        model.Actions.Add(new CardAction
        {
            Label = exploring ? "Stop exploring" : "Explore",
            Tooltip = "Walk to the nearest known ruin, or the edge of the known map, turn after turn, until told otherwise.",
            Kind = def?.IsScout == true && !exploring ? ButtonKind.Primary : ButtonKind.Normal,
            Commit = () =>
            {
                if (_exploringUnits.Remove(unitId))
                {
                    _world.TryCancelUnitOrder(PlayerCiv.Id, unitId);
                }
                else
                {
                    _exploringUnits.Add(unitId);
                    _sleepingUnits.Remove(unitId);
                    RunAutoExplore();
                }
                MarkDirty();
                RefreshAll();
            },
        });
        if (!unit.IsGarrisoned && def?.IsScout != true)
        {
            model.Actions.Add(new CardAction
            {
                Label = "Garrison",
                Tooltip = "Dig in here: makes the land around safer, which helps nearby settlements grow, and stops asking for orders.",
                Commit = () => { if (_world.TryGarrisonUnit(PlayerCiv.Id, unitId)) { _exploringUnits.Remove(unitId); MarkDirty(); RefreshAll(); } },
            });
        }
        bool asleep = _sleepingUnits.Contains(unitId);
        model.Actions.Add(new CardAction
        {
            Label = asleep ? "Wake" : "Sleep",
            Tooltip = asleep ? "Ask for orders again." : "Stop asking for orders until woken.",
            Commit = () =>
            {
                if (!_sleepingUnits.Remove(unitId)) { _sleepingUnits.Add(unitId); _exploringUnits.Remove(unitId); }
                RefreshHud();
                RefreshSelection();
            },
        });
        if (unit.MovementRemaining > 0 && unit.Destination is null && !_skippedUnitsThisTurn.Contains(unitId))
        {
            model.Actions.Add(new CardAction
            {
                Label = "Skip turn",
                Tooltip = "Do nothing this turn.",
                Commit = () =>
                {
                    _skippedUnitsThisTurn.Add(unitId);
                    RefreshHud();
                    var next = UnitsNeedingOrders().FirstOrDefault();
                    if (next is not null) { FocusOnTile(next.Location.Col, next.Location.Row); SelectUnit(next.Id); }
                    else RefreshSelection();
                },
            });
        }
        if (unit.Destination is not null && !exploring)
        {
            model.Actions.Add(new CardAction
            {
                Label = "Cancel march",
                Tooltip = "Stop where it is.",
                Commit = () => { if (_world.TryCancelUnitOrder(PlayerCiv.Id, unitId)) { MarkDirty(); RefreshAll(); } },
            });
        }
        var here = _world.Settlements.Find(s => s.Location.Equals(unit.Location));
        if (here?.OwnerId is int hostOwner && hostOwner != PlayerCiv.Id && _world.Wars.IsAtWar(PlayerCiv.Id, hostOwner))
        {
            model.Actions.Add(new CardAction
            {
                Label = $"Raze {MapPalette.SettlementName(here.Id)}",
                Kind = ButtonKind.Danger,
                Wide = true,
                Tooltip = "Burn it: most of its people are lost and it takes many turns to recover.",
                Commit = () => { if (_world.TryRazeSettlement(PlayerCiv.Id, unitId)) { MarkDirty(); RefreshAll(); } },
            });
        }
        model.Actions.Add(new CardAction
        {
            Label = "Disband",
            Kind = ButtonKind.Danger,
            Tooltip = "Send them home for good.",
            Commit = () => Confirm("Disband unit?", $"Disband this {def?.Name ?? "unit"}? It cannot be undone.", () =>
            {
                if (_world.TryDisbandUnit(PlayerCiv.Id, unitId)) { MarkDirty(); RefreshAll(); }
            }),
        });
    }

    private SelectionCard.Model BuildTileCard(HexCoord tile)
    {
        var player = PlayerCiv;
        var knowledge = _world.KnowledgeFor(player.Id);
        if (knowledge is null || !knowledge.IsExplored(tile))
        {
            return new SelectionCard.Model
            {
                Key = $"tile:{tile.Col},{tile.Row}",
                Title = "Unexplored",
                Subtitle = "no one of your people has been here",
                Accent = HudStyle.TextMuted,
                TitleIcon = Icon.Compass,
                Hint = "Send a unit, or found settlements nearby: settlements see the land around them.",
            };
        }
        int width = _world.Grid.Width;
        int ownerId = _lastSnapshot is { } dto && dto.TileOwner.Length > tile.Index(width) ? dto.TileOwner[tile.Index(width)] : -1;
        string owner = ownerId < 0 ? "unclaimed" : ownerId == player.Id ? "your land" : $"{CivNaming.AdjectiveOf(_content, CivIndexOf(ownerId))} land";
        var model = new SelectionCard.Model
        {
            Key = $"tile:{tile.Col},{tile.Row}",
            Title = TerrainName(tile),
            Subtitle = owner,
            Accent = ownerId >= 0 ? CivColorFor(ownerId) : HudStyle.GoldDim,
            TitleIcon = Icon.Flag,
        };
        var grid = _world.Grid;
        if (grid.WaterAt(tile))
        {
            model.Rows.Add(new CardRow { Icon = Icon.Water, Text = "Water: no one can walk or live here." });
            return model;
        }
        if (!TerrainRules.IsPassable(grid, tile))
        {
            model.Rows.Add(new CardRow { Icon = Icon.Moves, Text = "Impassable peaks." });
            return model;
        }
        int cost = TerrainRules.MoveCost(grid, tile);
        int defence = TerrainRules.DefenseBonusPercent(grid, tile);
        model.Rows.Add(new CardRow { Icon = Icon.Moves, Text = $"Costs {cost} move{(cost == 1 ? "" : "s")} to enter{(defence > 0 ? $" · +{defence}% defence" : "")}" });
        model.Rows.Add(new CardRow
        {
            Icon = Icon.House,
            Text = $"Settlement site: {SiteQuality(tile)}",
            Tooltip = "Food, water, safety and trade here added up, as the growth overlay (V) shows them.",
        });
        if (_world.RuinSites.UnexploredAt(tile) is not null)
            model.Rows.Add(new CardRow { Icon = Icon.Column, Text = "Ancient ruins: send a unit here to search them.", Color = HudStyle.Gold });

        var preview = CommandPreview.EvaluateFoundCharter(_world, player.Id, player.CapitalSettlementId, tile);
        double foundCost = Charters.FoundingCostFor(Charters.OwnedCount(_world.Settlements, player.Id)).ToDouble();
        model.Actions.Add(new CardAction
        {
            Label = $"Found a settlement here ({foundCost:0} gold)",
            Enabled = preview.Legal,
            Reason = preview.Legal ? "" : preview.Reason,
            Kind = ButtonKind.Primary,
            Wide = true,
            Tooltip = "A new settlement of 8 people, paid from your treasury. It grows on its own from here.",
            Commit = () => TryFoundAt(tile),
        });
        if (!preview.Legal) model.Hint = preview.Reason;
        return model;
    }

    /// <summary>What a settlement will produce this coming turn — the sim's own formula
    /// (<see cref="Hearthspan.Sim.Economy.EconomyEngine.ProductionFor"/>), so build times are right on
    /// turn 0 too, before any ledger has been filled.</summary>
    private double ProductionRate(Settlement s) =>
        Hearthspan.Sim.Economy.EconomyEngine.ProductionFor(s, _content, s.OwnerId is int o ? _world.EconomyBonusFor(o) : default).ToDouble();

    /// <summary>Plan v5 D13: "built in Wenford" / "being built in Wenford" when another settlement of
    /// this one's people has the landmark, else null.</summary>
    private string? LandmarkElsewhere(Settlement settlement, string buildingId)
    {
        foreach (var other in _world.Settlements)
        {
            if (other.OwnerId != settlement.OwnerId || other.Id == settlement.Id) continue;
            if (other.Build.CompletedBuildingIds.Contains(buildingId)) return $"landmark: built in {MapPalette.SettlementName(other.Id)}";
            if (other.Build.Queue.Contains(buildingId)) return $"landmark: being built in {MapPalette.SettlementName(other.Id)}";
        }
        return null;
    }

    private double GoldRate(Settlement s) =>
        Hearthspan.Sim.Economy.EconomyEngine.GoldFor(s, _content, s.OwnerId is int o ? _world.EconomyBonusFor(o) : default).ToDouble();

    /// <summary>The player's banner bars: growth through the current tier (0 at its threshold, 1 at
    /// the next) and the front of the build queue (-1 when nothing is queued).</summary>
    private Dictionary<int, (float Growth, float Build)> BannerProgress()
    {
        long[] ladder = { 0, TierLadder.HamletThreshold, TierLadder.VillageThreshold, TierLadder.TownThreshold,
                          TierLadder.CityThreshold, TierLadder.MetropolisThreshold };
        var result = new Dictionary<int, (float, float)>();
        foreach (var s in _world.Settlements)
        {
            if (s.OwnerId != PlayerCiv.Id) continue;
            int tier = System.Math.Clamp((int)s.Tier - 1, 0, ladder.Length - 1);
            float growth = tier + 1 < ladder.Length
                ? (float)((s.Population - ladder[tier]) / (double)System.Math.Max(1, ladder[tier + 1] - ladder[tier]))
                : 1f;
            float build = -1f;
            if (s.Build.Queue.Count > 0 && _content.Buildings.Find(b => b.Id == s.Build.Queue[0]) is { } def && def.Cost > 0)
                build = (float)(s.Build.InvestedProduction.ToDouble() / def.Cost);
            result[s.Id] = (System.Math.Clamp(growth, 0f, 1f), build);
        }
        return result;
    }

    private string TerrainName(HexCoord tile)
    {
        var g = _world.Grid;
        if (g.WaterAt(tile)) return g.BiomeAt(tile) == Biome.Coast ? "Coastal water" : "Open sea";
        if (g.ReliefAt(tile) == Relief.Mountains) return "Mountains";
        string biome = g.BiomeAt(tile) switch
        {
            Biome.Grassland => "grassland",
            Biome.Plains => "plains",
            Biome.Desert => "desert",
            Biome.Tundra => "tundra",
            _ => "land",
        };
        string text = (g.ForestAt(tile) ? "forested " : "") + biome + (g.ReliefAt(tile) == Relief.Hills ? " hills" : "");
        return Capitalise(text);
    }

    private static string Capitalise(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    // --- the wonder race ---------------------------------------------------------------------------------

    /// <summary>civ id -> the last wonder-race stage announced for it (1 started, 2 half built, 3 nearly
    /// done). Session-only: after a load the race is simply announced again.</summary>
    private readonly Dictionary<int, int> _wonderStageAnnounced = new();

    /// <summary>Tiers one of the player's settlements has reached this session: a people's first
    /// Village, Town and City get a moment card, later ones only a line in the news (a capital's
    /// promotions always get the card).</summary>
    private readonly HashSet<int> _tiersReached = new();

    /// <summary>A rival's furthest progress (0..1) on the game-ending wonder, or null when none of its
    /// settlements is building it. Only asked about peoples the player has met — it travels as rumour.</summary>
    private double? WonderProgressOf(Civilization civ, out int settlementId)
    {
        settlementId = -1;
        var wonder = _content.Buildings.Find(b => b.Wonder);
        if (wonder is null || _world.WonderCompletedByCivId is not null) return null;
        double? best = null;
        foreach (var s in _world.Settlements)
        {
            if (s.OwnerId != civ.Id || s.Build.Queue.Count == 0 || s.Build.Queue[0] != wonder.Id) continue;
            double p = wonder.Cost > 0 ? s.Build.InvestedProduction.ToDouble() / wonder.Cost : 0;
            if (best is null || p > best) { best = p; settlementId = s.Id; }
        }
        return best;
    }

    private void CheckWonderRace()
    {
        var player = PlayerCiv;
        var wonder = _content.Buildings.Find(b => b.Wonder);
        if (wonder is null || _world.WonderCompletedByCivId is not null) return;
        foreach (var civ in _world.Civilizations.OrderBy(c => c.Index))
        {
            if (civ.Id == player.Id || !player.MetCivIds.Contains(civ.Id)) continue;
            if (WonderProgressOf(civ, out _) is not double progress) continue;
            int stage = progress >= 0.8 ? 3 : progress >= 0.5 ? 2 : 1;
            int announced = _wonderStageAnnounced.TryGetValue(civ.Id, out int a) ? a : 0;
            if (stage <= announced) continue;
            _wonderStageAnnounced[civ.Id] = stage;
            string name = CivNaming.CapitalisedNameOf(_content, civ.Index);
            switch (stage)
            {
                case 1:
                    AddNews($"{name} have begun {wonder.Name}.", Icon.Star, HudStyle.Bad);
                    AddMoment(new Hud.MomentPopup.Moment
                    {
                        Kicker = "The wonder race",
                        Sound = Audio.Sound.War,
                        Title = $"{name} build {wonder.Name}",
                        Body = $"Rumour says {CivNaming.LeaderOf(_content, civ.Index)} has set builders to {wonder.Name}. " +
                               "Whoever finishes it first claims the age, and the game. Research Monumental Engineering to race them — " +
                               "or march on the city raising it.",
                        Accent = HudStyle.Bad,
                        Icon = Icon.Star,
                    });
                    break;
                case 2:
                    AddNews($"{name}'s {wonder.Name} is half built.", Icon.Star, HudStyle.Bad);
                    break;
                default:
                    AddNews($"{name} have nearly finished {wonder.Name}!", Icon.Star, HudStyle.Bad);
                    break;
            }
        }
    }

    // --- verification script -------------------------------------------------------------------------

    /// <summary>--do: a sequence of player actions, each through the handler the real click or key
    /// uses, printing the End Turn state and the selection after every step so a whole flow can be
    /// checked from a shell. Steps: select-unit:ID, explore:ID, dest:ID:COL:ROW, select-tile:COL:ROW,
    /// select-settlement:ID, card:LABEL, found-mode, click:COL:ROW, hover:COL:ROW, hover-ruin, dest-ruin:ID,
    /// research:TECH,
    /// rival:CIVID, press (the End Turn button), end (Shift+Enter), continue (a moment card's
    /// Continue). press and end first dismiss any moment cards, as a player would.</summary>
    private void RunScript(string script)
    {
        var steps = new List<string>();
        foreach (var token in script.Split(';', System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries))
        {
            int star = token.LastIndexOf('*');
            if (star > 0 && int.TryParse(token[(star + 1)..], out int times)) steps.AddRange(Enumerable.Repeat(token[..star], times));
            else steps.Add(token);
        }
        foreach (var raw in steps)
        {
            var parts = raw.Split(':');
            int Arg(int i) => parts.Length > i && int.TryParse(parts[i], out int v) ? v : -1;
            switch (parts[0])
            {
                case "select-unit": SelectUnit(Arg(1)); break;
                case "explore": SelectUnit(Arg(1)); _card.DebugPress("Explore"); break;
                case "dest": SelectUnit(Arg(1)); OnHexRightClicked(Arg(2), Arg(3)); break;
                case "select-tile": SelectTile(new HexCoord(Arg(1), Arg(2))); break;
                case "select-settlement":
                    if (_world.Settlements.Find(x => x.Id == Arg(1)) is { } st) _selectionController.SelectSettlement(st.Location.Col, st.Location.Row);
                    break;
                case "card": GD.Print($"GameSession: do card \"{raw[5..]}\" pressed={_card.DebugPress(raw[5..])}"); break;
                case "found-mode": EnterFoundingMode(); break;
                case "click":
                    var c = new HexCoord(Arg(1), Arg(2));
                    OnHexClicked(c.Col, c.Row, HexLayout.ToWorld(c.Col, c.Row) + new Vector3(0, _mapView.GroundHeightAt(c.Col, c.Row), 0));
                    break;
                case "hover": _selectionController.HoverPinned = true; OnHexHovered(Arg(1), Arg(2)); break;
                case "dest-ruin":
                case "hover-ruin":
                {
                    // The nearest ruin the player knows of to the unit (or its selection): lets a
                    // verification script aim at a ruin without knowing the seed's coordinates.
                    int unitId = parts[0] == "dest-ruin" ? Arg(1) : _selectedUnitId;
                    var unit = _world.Units.Find(u => u.Id == unitId);
                    var knowledge = _world.KnowledgeFor(PlayerCiv.Id);
                    if (unit is null || knowledge is null) break;
                    var ruin = _world.RuinSites.Sites.Where(r => !r.Explored && knowledge.IsExplored(r.Location))
                        .OrderBy(r => HexCoord.Distance(r.Location, unit.Location)).ThenBy(r => r.Id).FirstOrDefault();
                    if (ruin is null) break;
                    if (parts[0] == "dest-ruin") { SelectUnit(unit.Id); OnHexRightClicked(ruin.Location.Col, ruin.Location.Row); }
                    else { _selectionController.HoverPinned = true; OnHexHovered(ruin.Location.Col, ruin.Location.Row); }
                    break;
                }
                case "research": OnTechClicked(parts.Length > 1 ? parts[1] : ""); break;
                case "rival": _rivals.OpenDiplomacy(Arg(1)); break;
                case "auto": AutoPlay(System.Math.Max(1, Arg(1))); break;
                case "press": _moments.DismissAll(); OnEndTurnPressed(); break;
                case "end": _moments.DismissAll(); CommitEndTurn(); break;
                case "continue": _moments.Advance(); break;
                default: GD.PrintErr($"GameSession: --do: unknown step '{raw}'"); break;
            }
            var units = string.Join(" ", _world.Units.Where(u => u.OwnerId == PlayerCiv.Id).OrderBy(u => u.Id)
                .Select(u => $"u{u.Id}@{u.Location}{(u.Destination is HexCoord d ? $"->{d}" : "")}m{u.MovementRemaining}"));
            GD.Print($"GameSession: do {raw} -> turn={_world.Turn} {_endTurn.DumpUiDigest()} | card={_card.CurrentKey} | " +
                     $"moments={_moments.Pending} | prompt={_decisionPrompt.Visible} | explored={_world.KnowledgeFor(PlayerCiv.Id)?.ExploredTileCount} | {units}");
        }
    }

    /// <summary>--do auto:N — N turns of a plain, sensible player, every action through the same
    /// handler its button uses: research the cheapest available technology when idle, build the
    /// cheapest thing in every idle settlement, keep scouts exploring and fighting units garrisoned at
    /// home, and found a settlement on the best site in reach whenever the treasury allows (at most
    /// one every five turns). A verification driver for looking at the mid and late game, not an AI.</summary>
    private void AutoPlay(int turns)
    {
        int lastFounded = -99;
        for (int t = 0; t < turns; t++)
        {
            var player = PlayerCiv;
            _moments.DismissAll();
            if (player.Tech.ResearchTargetId is null)
            {
                var next = _content.Techs.Where(x => ResearchEngine.IsAvailable(player.Tech, x.Id, _content))
                    .OrderBy(x => x.Cost).ThenBy(x => x.Id).FirstOrDefault();
                if (next is not null) OnTechClicked(next.Id);
            }
            foreach (var s in _world.Settlements.Where(x => x.OwnerId == player.Id && x.Build.Queue.Count == 0).ToList())
            {
                var pick = AvailableBuildingsFor(s).Where(b => !b.Locked).OrderBy(b => b.Cost).ThenBy(b => b.Id).FirstOrDefault();
                if (pick.Id is not null && _world.TryEnqueueBuilding(s.Id, pick.Id)) _manuallyTouchedSettlementIds.Add(s.Id);
            }
            foreach (var u in _world.Units.Where(x => x.OwnerId == player.Id).ToList())
            {
                var def = _world.UnitDefOf(u);
                if (def is null) continue;
                if (def.IsScout) _exploringUnits.Add(u.Id);
                else if (!u.IsGarrisoned && u.Destination is null) _world.TryGarrisonUnit(player.Id, u.Id);
            }
            RunAutoExplore();
            // Spend surplus gold finishing landmarks, biggest settlement first, keeping a reserve.
            foreach (var s in _world.Settlements.Where(x => x.OwnerId == player.Id && x.Build.Queue.Count > 0)
                         .OrderByDescending(x => x.Population).ThenBy(x => x.Id).ToList())
            {
                var front = _content.Buildings.Find(b => b.Id == s.Build.Queue[0]);
                if (front is null || !front.Hero) continue;
                if (Hearthspan.Sim.Settlements.ConstructionEngine.BuyPrice(s, _content) is { } price
                    && player.Gold.ToDouble() >= price.ToDouble() + 60) _world.TryBuyBuilding(player.Id, s.Id);
            }
            double cost = Charters.FoundingCostFor(Charters.OwnedCount(_world.Settlements, player.Id)).ToDouble();
            if (_world.Turn - lastFounded >= 5 && player.Gold.ToDouble() >= cost + 10 && _lastSnapshot is { } dto)
            {
                var best = CommandPreview.LegalFoundingSites(_world, player.Id)
                    .OrderByDescending(x => x.Index(_world.Grid.Width) < dto.Viability.Length ? dto.Viability[x.Index(_world.Grid.Width)] : 0)
                    .ThenBy(x => x.Index(_world.Grid.Width)).FirstOrDefault();
                if (!best.Equals(default(HexCoord)) && _world.TryFoundCharter(player.Id, player.CapitalSettlementId, best)) lastFounded = _world.Turn;
            }
            CommitEndTurn();
            _moments.DismissAll();
        }
    }

    // --- news and moments -------------------------------------------------------------------------------

    /// <summary>Turns the sim's events since the last look into feed items and moment cards. The
    /// cursor survives between calls in one turn (orders can cause events mid-turn — a ruin searched,
    /// a battle) and resets when the turn changes.</summary>
    private void ProcessNewEvents()
    {
        MapPalette.NameOverrides = CivNaming.SettlementNames(_world, _content);
        if (_eventCursorTurn != _world.Turn)
        {
            _eventCursorTurn = _world.Turn;
            _eventCursor = 0;
        }
        var events = _world.Events.ThisTurn;
        if (_eventCursor > events.Count) _eventCursor = 0;
        var player = PlayerCiv;
        var knowledge = _world.KnowledgeFor(player.Id);
        for (int i = _eventCursor; i < events.Count; i++)
        {
            var e = events[i];
            if (!WorldView.IsVisibleToObserver(e, player.Id, _world, knowledge)) continue;
            HandleEvent(e, player);
        }
        _eventCursor = events.Count;
    }

    private void AddNews(string text, Icon icon, Color color, HexCoord? focus = null, string tooltip = "")
    {
        _feed.Add(new EventFeed.Entry
        {
            Text = text, Icon = icon, Color = color, Turn = _world.Turn, Tooltip = tooltip,
            FocusCol = focus?.Col, FocusRow = focus?.Row,
        });
    }

    private void AddMoment(MomentPopup.Moment moment)
    {
        if (PromptsAllowed) _moments.Enqueue(moment);
    }

    private HexCoord? LocationOf(int settlementId) => _world.Settlements.Find(s => s.Id == settlementId)?.Location;

    private void HandleEvent(TurnEvent e, Civilization player)
    {
        bool mine = e.CivId == player.Id;
        string settlementName = e.SettlementId >= 0 ? MapPalette.SettlementName(e.SettlementId) : "";
        var where = e.SettlementId >= 0 ? LocationOf(e.SettlementId) : null;
        switch (e.Type)
        {
            case TurnEventType.TechCompleted when mine:
            {
                var tech = _content.Techs.Find(t => t.Id == e.ContentId);
                IReadOnlyList<ContentIndex.Unlock> unlocks = tech is null ? System.Array.Empty<ContentIndex.Unlock>() : _contentIndex.Unlocks(tech.Id);
                string unlockText = unlocks.Count == 0 ? "" : $" Unlocks {string.Join(", ", unlocks.Take(3).Select(u => u.Describe()))}.";
                AddNews($"Discovered {tech?.Name ?? e.ContentId}.{unlockText}", Icon.Research, HudStyle.Info);
                break;
            }
            case TurnEventType.BuildingCompleted when mine:
            {
                var def = _content.Buildings.Find(b => b.Id == e.ContentId);
                string name = def?.Name ?? e.ContentId ?? "a building";
                if (def is not null && (def.Hero || def.Wonder))
                {
                    AddNews($"{name} completed in {settlementName}!", Icon.Star, HudStyle.Gold, where);
                    AddMoment(new MomentPopup.Moment
                    {
                        Kicker = def.Wonder ? "A wonder of the world" : "A landmark rises",
                        Sound = Audio.Sound.Fanfare,
                        Title = name,
                        Body = $"The people of {settlementName} have raised the {name}. Travellers will speak of it for generations.",
                        Effect = ContentIndex.EffectText(def) is string fx && fx != "" ? fx : null,
                        Accent = HudStyle.Gold,
                        Icon = Icon.Star,
                        FocusCol = where?.Col, FocusRow = where?.Row,
                    });
                }
                else AddNews($"{settlementName} built a {name}.", Icon.Production, HudStyle.TextSoft, where);
                break;
            }
            case TurnEventType.SettlementPromoted when mine:
            {
                var tier = System.Enum.IsDefined(typeof(SettlementTierName), (int)e.Magnitude) ? (SettlementTierName)e.Magnitude : SettlementTierName.Hamlet;
                AddNews($"{settlementName} has grown into a {tier}.", Icon.House, HudStyle.Good, where);
                bool firstOfTier = _tiersReached.Add((int)tier);
                if (tier >= SettlementTierName.Village && (firstOfTier || e.SettlementId == player.CapitalSettlementId))
                {
                    AddMoment(new MomentPopup.Moment
                    {
                        Kicker = "Your people grow",
                        Sound = Audio.Sound.Fanfare,
                        Title = $"{settlementName} is now a {tier}",
                        Body = tier switch
                        {
                            SettlementTierName.Village => "Fields spread and a palisade can rise. Villages see further and draw hamlets around them.",
                            SettlementTierName.Town => "Stone walls, markets and a hall: a town anchors a whole region.",
                            SettlementTierName.City => "A city: its roads carry your people's name far across the land.",
                            _ => "It has grown into one of the great places of the world.",
                        },
                        Accent = HudStyle.Good,
                        Icon = Icon.House,
                        FocusCol = where?.Col, FocusRow = where?.Row,
                    });
                }
                break;
            }
            case TurnEventType.SettlementDemoted when mine:
                AddNews($"{settlementName} has shrunk.", Icon.House, HudStyle.Bad, where);
                break;
            case TurnEventType.SettlementStarving when mine:
                AddNews($"{settlementName} is losing people.", Icon.Food, HudStyle.Bad, where);
                break;
            case TurnEventType.SettlementFounded:
                if (mine) AddNews($"New settlement: {settlementName}.", Icon.House, HudStyle.Good, where);
                else if (e.CivId >= 0 && _world.CivById(e.CivId) is { } founder)
                    AddNews($"{CivNaming.AdjectiveOf(_content, founder.Index)} settlers founded {settlementName}.", Icon.House, HudStyle.TextSoft, where);
                break;
            case TurnEventType.FreeholdAbsorbed when mine:
                AddNews($"{settlementName} has joined your people.", Icon.House, HudStyle.Good, where);
                break;
            case TurnEventType.CivMet when mine:
            {
                var other = _world.CivById((int)e.Magnitude);
                if (other is null) break;
                string name = CivNaming.NameOf(_content, other.Index);
                string leader = CivNaming.LeaderOf(_content, other.Index);
                var capital = _world.Settlements.Find(s => s.Id == other.CapitalSettlementId);
                AddNews($"You have met {name}.", Icon.Flag, CivColorFor(other.Id));
                AddMoment(new MomentPopup.Moment
                {
                    Kicker = "First contact",
                    Sound = Audio.Sound.Peace,
                    Title = CivNaming.CapitalisedNameOf(_content, other.Index),
                    Body = $"Your people have met {name}, led by {leader}. For now there is peace between you; " +
                           $"no one may make war before turn {World.WarGraceTurns}.",
                    Accent = CivColorFor(other.Id),
                    Icon = Icon.Flag,
                });
                _ = capital;
                break;
            }
            case TurnEventType.WarDeclared:
            {
                var declarer = _world.CivById(e.CivId);
                var target = _world.CivById((int)e.Magnitude);
                if (declarer is null || target is null) break;
                if (target.Id == player.Id)
                {
                    string name = CivNaming.CapitalisedNameOf(_content, declarer.Index);
                    AddNews($"{name} declared war on you!", Icon.Swords, HudStyle.Bad);
                    AddMoment(new MomentPopup.Moment
                    {
                        Kicker = "War",
                        Sound = Audio.Sound.War,
                        Title = $"{name} declare war",
                        Body = $"{CivNaming.LeaderOf(_content, declarer.Index)} has sent warriors against you. Garrison your settlements " +
                               "and train fighting units. Peace becomes possible after ten turns without fighting.",
                        Accent = HudStyle.Bad,
                        Icon = Icon.Swords,
                    });
                }
                else if (declarer.Id == player.Id)
                    AddNews($"You declared war on {CivNaming.NameOf(_content, target.Index)}.", Icon.Swords, HudStyle.Bad);
                else
                    AddNews($"{CivNaming.CapitalisedNameOf(_content, declarer.Index)} declared war on {CivNaming.NameOf(_content, target.Index)}.", Icon.Swords, HudStyle.TextSoft);
                break;
            }
            case TurnEventType.PeaceMade:
            {
                var maker = _world.CivById(e.CivId);
                var other = _world.CivById((int)e.Magnitude);
                if (maker is null || other is null) break;
                if (other.Id == player.Id)
                {
                    string name = CivNaming.CapitalisedNameOf(_content, maker.Index);
                    AddNews($"{name} made peace with you.", Icon.Leaf, HudStyle.Good);
                    AddMoment(new MomentPopup.Moment
                    {
                        Kicker = "Peace",
                        Sound = Audio.Sound.Peace,
                        Title = $"Peace with {CivNaming.NameOf(_content, maker.Index)}",
                        Body = $"{CivNaming.LeaderOf(_content, maker.Index)} has sued for peace. A treaty now holds for {WarState.TreatyTurns} turns.",
                        Accent = HudStyle.Good,
                        Icon = Icon.Leaf,
                    });
                }
                else if (maker.Id == player.Id)
                    AddNews($"You made peace with {CivNaming.NameOf(_content, other.Index)}.", Icon.Leaf, HudStyle.Good);
                else
                    AddNews($"{CivNaming.CapitalisedNameOf(_content, maker.Index)} made peace with {CivNaming.NameOf(_content, other.Index)}.", Icon.Leaf, HudStyle.TextSoft);
                break;
            }
            case TurnEventType.RuinExplored when mine:
            {
                string content = e.ContentId ?? "";
                string title, body;
                HexCoord? focus = null;
                if (content == "gold")
                {
                    title = $"{e.Magnitude} gold";
                    body = $"Among the fallen stones your people found an old hoard: {e.Magnitude} gold for the treasury.";
                }
                else if (content.StartsWith("tech:"))
                {
                    var tech = _content.Techs.Find(t => t.Id == content[5..]);
                    title = tech?.Name ?? "Lost knowledge";
                    body = $"Carvings on the walls teach your people {tech?.Name ?? "something new"}.";
                }
                else if (content == "survivors")
                {
                    title = "Survivors";
                    body = $"{e.Magnitude} survivors living among the ruins join {settlementName}.";
                    focus = where;
                }
                else
                {
                    title = "Old maps";
                    body = "A cracked map, painted on a wall, shows the land for miles around.";
                }
                AddNews($"Ruins searched: {title}.", Icon.Column, HudStyle.Gold, focus);
                AddMoment(new MomentPopup.Moment
                {
                    Kicker = "Ancient ruins",
                    Title = title,
                    Body = body,
                    Accent = HudStyle.Gold,
                    Icon = Icon.Column,
                    FocusCol = focus?.Col, FocusRow = focus?.Row,
                });
                break;
            }
            case TurnEventType.UnitOrderCancelled when mine:
            {
                var unit = _world.Units.Find(u => u.Id == (int)e.Magnitude);
                string name = unit is null ? "unit" : _world.UnitDefOf(unit)?.Name ?? "unit";
                AddNews($"Your {name} stopped: the way ahead is blocked.", Icon.Moves, HudStyle.TextSoft, unit?.Location);
                break;
            }
            case TurnEventType.SettlementPillaged:
            case TurnEventType.SettlementRazed:
            {
                var victim = _world.Settlements.Find(s => s.Id == e.SettlementId);
                string verb = e.Type == TurnEventType.SettlementRazed ? "razed" : "pillaged";
                if (victim?.OwnerId == player.Id)
                {
                    var raider = _world.CivById(e.CivId);
                    string who = raider is null ? "Raiders" : $"{CivNaming.AdjectiveOf(_content, raider.Index)} raiders";
                    AddNews($"{who} {verb} {settlementName}!", Icon.Swords, HudStyle.Bad, where);
                    if (e.Type == TurnEventType.SettlementRazed)
                    {
                        AddMoment(new MomentPopup.Moment
                        {
                            Kicker = "Disaster",
                            Sound = Audio.Sound.War,
                            Title = $"{settlementName} burns",
                            Body = $"{who} have razed {settlementName}. Most of its people are gone; it will take many turns to recover.",
                            Accent = HudStyle.Bad,
                            Icon = Icon.Swords,
                            FocusCol = where?.Col, FocusRow = where?.Row,
                        });
                    }
                }
                else if (mine) AddNews($"Your army {verb} {settlementName}.", Icon.Swords, HudStyle.Gold, where);
                break;
            }
            case TurnEventType.UnitDestroyed when mine:
                AddNews(e.ContentId is null ? "Your unit won a battle." : $"Your {_content.Units.Find(u => u.Id == e.ContentId)?.Name ?? "unit"} fell in battle.",
                    Icon.Swords, e.ContentId is null ? HudStyle.Good : HudStyle.Bad);
                break;
        }
    }
}
