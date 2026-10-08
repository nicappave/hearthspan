using Godot;

namespace Hearthspan.Game;

/// <summary>
/// Tile and settlement selection via CPU-side picking against the ground plane — never physics bodies
/// per settlement, since settlements are never nodes (rule 4; "Reserve real nodes for selection
/// highlight, camera, particles" — this script and its highlight mesh are exactly that reservation).
///
/// <para>Stage 2 task 2: a click no longer just prints a digest — it raises
/// <see cref="SettlementSelected"/>/<see cref="HexClicked"/> so <c>GameSession</c> can drive the real
/// inspection panel from it. Left as C# events (not Godot signals) since the only subscriber is
/// GameSession, in the same assembly, wired at `_Ready()` — no cross-scene-tree signal wiring needed.</para>
/// </summary>
public partial class SelectionController : Node3D
{
    private Camera3D _camera = null!;
    private MapView _mapView = null!;
    private MultiMeshInstance3D _roadPreview = null!;

    public string? LastSelectionDigest { get; private set; }

    /// <summary>Raised with the settlement id when a click lands on a settlement.</summary>
    public event System.Action<int>? SettlementSelected;

    /// <summary>Plan v5 M3: raised with (col, row, ground hit) for every plain left click on the map.
    /// GameSession decides what it means — a unit, a settlement, a tile, a founding site, or a move.</summary>
    public event System.Action<int, int, Vector3>? HexClicked;

    /// <summary>Plan v5 M3: a right click that did not turn into a camera drag — the move order.</summary>
    public event System.Action<int, int>? HexRightClicked;

    /// <summary>Plan v5 M3: the hex under the cursor changed ((-1, -1) when the cursor left the map or
    /// is over the HUD) — drives the route preview and targeting hints.</summary>
    public event System.Action<int, int>? HexHovered;

    private Vector2 _rightPressPosition;
    private bool _rightArmed;
    private Vector2? _pendingHover;
    private (int Col, int Row) _hovered = (-1, -1);

    // Stage 2 task 3: road dragging. Press-and-drag from one of the player's own settlements to
    // another; the preview line updates every frame the drag moves over a new hover target, and
    // releasing over a legal destination commits. Drag detection lives here (raw input + hex
    // hit-testing); pricing the drag is GameSession's job (it holds the live World CommandPreview
    // needs), which is why this only ever raises *ids*, never a cost or a legality verdict itself.

    private int _roadDragFromId = -1;
    private Vector2 _pressPosition;
    private bool _dragMoved;
    private const float DragThresholdPixels = 6f;

    /// <summary>Raised every hover tick while dragging: (fromSettlementId, hoveredSettlementId or -1).
    /// GameSession answers with <see cref="ShowRoadPreview"/>.</summary>
    public event System.Action<int, int>? RoadDragHover;

    /// <summary>Raised on release: (fromSettlementId, toSettlementId or -1 if released over nothing).</summary>
    public event System.Action<int, int>? RoadDragCompleted;

    // Stage 7 task C5 (docs/stage-7-plan.md): the button-driven alternative to the drag gesture above —
    // "Build road to..." in the selection panel calls BeginRoadTargeting instead of relying on a press
    // that has no on-screen affordance at all. Deliberately reuses RoadDragHover/RoadDragCompleted (the
    // same two events the drag gesture raises) rather than adding a parallel pair, so GameSession's
    // existing OnRoadDragHover/OnRoadDragCompleted handlers are the one and only commit path for a road
    // regardless of which gesture triggered it — the task's own "do not fork the commit path" rule.
    private int _roadTargetFromId = -1;
    private MultiMeshInstance3D _roadTargetHighlights = null!;

    public bool IsTargetingRoad => _roadTargetFromId >= 0;

    public void BeginRoadTargeting(int fromSettlementId) => _roadTargetFromId = fromSettlementId;

    public void CancelRoadTargeting()
    {
        _roadTargetFromId = -1;
        HideRoadPreview();
        HideRoadTargetHighlights();
    }

    public override void _Ready()
    {
        _camera = GetNode<Camera3D>("../Camera");
        _mapView = GetNode<MapView>("..");

        // Plan v5: the selection highlight is drawn by the terrain shader (MapView.SetSelectedHex).

        _roadPreview = new MultiMeshInstance3D { Visible = false };
        AddChild(_roadPreview);

        _roadTargetHighlights = new MultiMeshInstance3D { Visible = false };
        _roadTargetHighlights.MaterialOverride = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.3f, 0.9f, 0.35f, 0.85f),
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        };
        AddChild(_roadTargetHighlights);
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventMouseMotion hoverMotion) _pendingHover = hoverMotion.Position;

        // Plan v5 M3: a right press-and-release that never moved is a move order; a right drag stays
        // CameraRig's pan gesture (plan v3 task 3.1). Neither arms a road drag.
        if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Right } right)
        {
            if (right.Pressed)
            {
                _rightPressPosition = right.Position;
                _rightArmed = true;
            }
            else if (_rightArmed)
            {
                _rightArmed = false;
                if (right.Position.DistanceTo(_rightPressPosition) <= DragThresholdPixels
                    && RayToGround(right.Position) is Vector3 target)
                {
                    var (col, row) = Render.HexMath.FromWorld(target.X, target.Z);
                    HexRightClicked?.Invoke(col, row);
                }
            }
            return;
        }
        if (@event is InputEventMouseMotion rightDrag && _rightArmed
            && rightDrag.Position.DistanceTo(_rightPressPosition) > DragThresholdPixels) _rightArmed = false;
        if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Middle }) return;

        if (_roadTargetFromId >= 0)
        {
            HandleRoadTargetingInput(@event);
            return;
        }

        switch (@event)
        {
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } down:
                TryStartDragOrSelect(down.Position);
                break;
            case InputEventMouseButton { Pressed: false, ButtonIndex: MouseButton.Left }:
                if (_roadDragFromId >= 0) EndDrag();
                break;
            case InputEventMouseMotion motion when _roadDragFromId >= 0:
                UpdateDragHover(motion.Position);
                break;
        }
    }

    /// <summary>Verification only (--hover): the hovered hex was set by a launch flag, so the real
    /// cursor (wherever the offscreen window's mouse happens to be) must not replace it.</summary>
    public bool HoverPinned { get; set; }

    public override void _Process(double delta)
    {
        if (HoverPinned) return;
        // The cursor over the HUD is not over the map.
        if (GetViewport().GuiGetHoveredControl() is not null)
        {
            _pendingHover = null;
            SetHovered(-1, -1);
            return;
        }
        if (_pendingHover is not Vector2 screen) return;
        _pendingHover = null;
        if (_mapView.World is null) return;
        if (RayToGround(screen) is Vector3 hit)
        {
            var (col, row) = Render.HexMath.FromWorld(hit.X, hit.Z);
            if (col < 0 || row < 0 || col >= _mapView.World.GridWidth || row >= _mapView.World.GridHeight) SetHovered(-1, -1);
            else SetHovered(col, row);
        }
        else SetHovered(-1, -1);
    }

    private void SetHovered(int col, int row)
    {
        if (_hovered == (col, row)) return;
        _hovered = (col, row);
        HexHovered?.Invoke(col, row);
    }

    /// <summary>Forces the next frame to re-raise the hover for wherever the cursor is (after a move
    /// order changes what the route to it looks like).</summary>
    public void RefreshHover()
    {
        _hovered = (-2, -2);
        _pendingHover = GetViewport().GetMousePosition();
    }

    /// <summary>While targeting is active, every click/motion goes through here instead of the normal
    /// press-to-drag-or-select path above — a click during targeting must never also start a fresh
    /// drag or reselect the map underneath it. Escape/cancelling targeting is GameSession's job (it
    /// already owns ui_cancel for the pause menu, so it decides which one Escape means right now).</summary>
    private void HandleRoadTargetingInput(InputEvent @event)
    {
        switch (@event)
        {
            case InputEventMouseMotion motion:
                UpdateRoadTargetHover(motion.Position);
                break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } down:
                ConfirmRoadTarget(down.Position);
                break;
        }
    }

    private void UpdateRoadTargetHover(Vector2 screenPos)
    {
        var hit = RayToGround(screenPos);
        int hoverId = -1;
        if (hit is Vector3 pos && _mapView.World is not null)
        {
            if (_mapView.FindNearestSettlementId(pos, 1.2f) is int foundId) hoverId = foundId;
        }
        RoadDragHover?.Invoke(_roadTargetFromId, hoverId);
    }

    /// <summary>A click during targeting always ends targeting, successful or not — the same one-shot
    /// contract <see cref="EndDrag"/> already has for the drag gesture (release over nothing silently
    /// cancels rather than leaving the gesture half-open).</summary>
    private void ConfirmRoadTarget(Vector2 screenPos)
    {
        int fromId = _roadTargetFromId;
        _roadTargetFromId = -1;
        HideRoadPreview();
        HideRoadTargetHighlights();

        var hit = RayToGround(screenPos);
        int toId = -1;
        if (hit is Vector3 pos && _mapView.World is not null)
        {
            if (_mapView.FindNearestSettlementId(pos, 1.2f) is int foundId) toId = foundId;
        }
        if (toId >= 0 && toId != fromId)
        {
            RoadDragCompleted?.Invoke(fromId, toId);
        }
    }

    /// <summary>All of this settlement's currently-legal road targets, highlighted at once (task C5's
    /// "valid targets highlight") — computed once when targeting begins, not recomputed per hover tick,
    /// since GameSession already knows the full legal set up front (CommandPreview.EvaluateBuildRoad
    /// against every other owned settlement).</summary>
    public void ShowRoadTargetHighlights(System.Collections.Generic.IReadOnlyList<Vector3> positions)
    {
        if (positions.Count == 0) { _roadTargetHighlights.Visible = false; return; }

        var multiMesh = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            Mesh = MapView.BuildHexRingMesh(0.98f, 0.78f),
            InstanceCount = positions.Count,
        };
        for (int i = 0; i < positions.Count; i++)
        {
            multiMesh.SetInstanceTransform(i, new Transform3D(Basis.Identity, positions[i]));
        }
        _roadTargetHighlights.Multimesh = multiMesh;
        _roadTargetHighlights.Visible = true;
    }

    public void HideRoadTargetHighlights() => _roadTargetHighlights.Visible = false;

    /// <summary>A press on the player's own settlement starts a road drag instead of an immediate
    /// selection — the selection itself still fires on release-with-no-drag (a plain click), via
    /// <see cref="EndDrag"/> falling back to <see cref="TrySelect"/> when nothing was dragged over.</summary>
    private void TryStartDragOrSelect(Vector2 screenPos)
    {
        if (_mapView.World == null) return;
        var hit = RayToGround(screenPos);
        if (hit is not Vector3 pos) { TrySelect(screenPos); return; }

        var (pressCol, pressRow) = Render.HexMath.FromWorld(pos.X, pos.Z);
        if (_mapView.World.Settlements.Find(x => x.Col == pressCol && x.Row == pressRow) is { } s)
        {
            if (s.OwnerId == _mapView.World.ObserverCivId)
            {
                _roadDragFromId = s.Id;
                _pressPosition = screenPos;
                _dragMoved = false;
                return;
            }
        }
        TrySelect(screenPos);
    }

    private void UpdateDragHover(Vector2 screenPos)
    {
        if (!_dragMoved && screenPos.DistanceTo(_pressPosition) > DragThresholdPixels) _dragMoved = true;

        // Only raise a road preview once the press has actually turned into a drag — a stationary
        // press must not flash a road preview before we know whether it's a click or a drag.
        if (!_dragMoved) return;

        var hit = RayToGround(screenPos);
        int hoverId = -1;
        if (hit is Vector3 pos && _mapView.World is not null)
        {
            if (_mapView.FindNearestSettlementId(pos, 1.2f) is int foundId) hoverId = foundId;
        }
        RoadDragHover?.Invoke(_roadDragFromId, hoverId);
    }

    private void EndDrag()
    {
        int fromId = _roadDragFromId;
        bool dragMoved = _dragMoved;
        _roadDragFromId = -1;
        _dragMoved = false;
        HideRoadPreview();

        // A press that never moved is a CLICK, not a cancelled drag - select the settlement under it.
        // Before Stage 7 this method fell off the end here, so clicking your own settlement did nothing
        // at all (the only settlements a player can actually act on). See docs/stage-7-plan.md task 1.
        if (!dragMoved)
        {
            TrySelect(GetViewport().GetMousePosition());
            return;
        }

        var mousePos = GetViewport().GetMousePosition();
        var hit = RayToGround(mousePos);
        int toId = -1;
        if (hit is Vector3 pos && _mapView.World is not null)
        {
            if (_mapView.FindNearestSettlementId(pos, 1.2f) is int foundId) toId = foundId;
        }

        if (toId >= 0 && toId != fromId)
        {
            RoadDragCompleted?.Invoke(fromId, toId);
        }
        // Released over nothing/itself: silently cancels, same as dragging a card back to its own slot.
    }

    /// <summary>Draws (or updates) the drag preview: a small marker per tile in the path, green when
    /// the road would be legal, red otherwise — cheap to rebuild every hover tick since a road path is
    /// at most a few dozen tiles.
    ///
    /// Stage 7 task G1 (docs/stage-7-plan.md): flattened and dropped to sit flush with the ground — it
    /// used to be a 0.5-unit cube floating 0.3 units up, reading as a row of boxes rather than a road.</summary>
    public void ShowRoadPreview(System.Collections.Generic.IReadOnlyList<Vector3> pathPositions, bool legal)
    {
        if (pathPositions.Count == 0) { HideRoadPreview(); return; }

        var mesh = new BoxMesh { Size = new Vector3(0.5f, 0.04f, 0.5f) };
        var multiMesh = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            Mesh = mesh,
            InstanceCount = pathPositions.Count,
        };
        for (int i = 0; i < pathPositions.Count; i++)
        {
            multiMesh.SetInstanceTransform(i, new Transform3D(Basis.Identity, pathPositions[i] + new Vector3(0, 0.03f, 0)));
        }
        var color = legal ? new Color(0.3f, 0.9f, 0.35f, 0.75f) : new Color(0.9f, 0.25f, 0.25f, 0.75f);
        mesh.SurfaceSetMaterial(0, new StandardMaterial3D
        {
            AlbedoColor = color,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        });
        _roadPreview.Multimesh = multiMesh;
        _roadPreview.Visible = true;
    }

    public void HideRoadPreview() => _roadPreview.Visible = false;

    /// <summary>Plan v5: picks against the real terrain (and sea surface), not the y = 0 plane — on a
    /// hill the plane is a whole hex off.</summary>
    private Vector3? RayToGround(Vector2 screenPos)
    {
        var from = _camera.ProjectRayOrigin(screenPos);
        var dir = _camera.ProjectRayNormal(screenPos);
        return _mapView.RaycastGround(from, dir);
    }

    /// <summary>Moves the highlight to a settlement/tile and fires the matching event, without
    /// requiring an actual mouse click — the same path a click takes, callable from the idle-loop
    /// "cycle to this settlement" button (task 7) and the turn report's click-to-focus (task 6).</summary>
    public void SelectSettlement(int col, int row)
    {
        _mapView.SetSelectedHex(col, row);
        if (_mapView.World is not null && _mapView.FindNearestSettlementId(HexLayout.ToWorld(col, row), 0.5f) is int foundId
            && _mapView.World.Settlements.Find(x => x.Id == foundId) is { } s)
        {
            LastSelectionDigest = $"settlement id={s.Id} col={s.Col} row={s.Row} pop={s.Population} tier={(SettlementTierName)s.Tier} owner={s.OwnerId?.ToString() ?? "freehold"}";
            SettlementSelected?.Invoke(s.Id);
        }
    }

    private void TrySelect(Vector2 screenPos)
    {
        if (_mapView.World == null) return;
        if (RayToGround(screenPos) is not Vector3 hit) return;
        var (col, row) = Render.HexMath.FromWorld(hit.X, hit.Z);
        if (col < 0 || row < 0 || col >= _mapView.World.GridWidth || row >= _mapView.World.GridHeight) return;
        LastSelectionDigest = $"hex col={col} row={row}";
        HexClicked?.Invoke(col, row, hit);
    }
}
