using Godot;

namespace Hearthspan.Game;

/// <summary>Camera pan (WASD / arrow keys / edge-scroll) + zoom (scroll wheel), orbiting a ground-plane
/// focus point. Stage 1 task 7 adds inertia to both, edge-scroll, and framing limits so panning past
/// the map edge isn't unbounded.</summary>
public partial class CameraRig : Camera3D
{
    [Export] public float PanSpeed = 20f;
    [Export] public float ZoomSpeed = 3f;
    [Export] public float MinHeight = 6f;
    [Export] public float MaxHeight = 220f;

    // Task 7: inertia. Pan/zoom accelerate toward the input direction and decay via friction rather
    // than snapping instantly, which gives the camera weight. Friction is high enough that input still
    // feels responsive (a key-up stops the camera within a few frames), not a slow ice-skating drift.
    private const float PanAcceleration = 140f;
    private const float PanFriction = 9f;
    private const float ZoomAcceleration = 26f;
    private const float ZoomFriction = 7f;

    // Edge-scroll: panning when the mouse sits within this many pixels of the viewport edge. Gated
    // behind "no harness capture args present" (see _Process) — GetMousePosition() has no concept of
    // "no real input yet" the way Input.IsKeyPressed does, so an automated windowed-offscreen shot
    // would otherwise read the OS's default/last cursor position as a permanent edge-scroll command
    // and drift the camera off a bookmark's exact framing before the screenshot is even taken.
    private const float EdgeScrollMargin = 24f;
    private readonly bool _edgeScrollEnabled =
        GreyBoxArgs.Get("shot-out") is null && GreyBoxArgs.Get("dump-ui-out") is null;

    private Vector3 _panVelocity = Vector3.Zero;
    private float _zoomVelocity = 0f;
    private int _gridWidth = 1;
    private int _gridHeight = 1;

    // Plan v3 task 3.1: right/middle-drag panning and wheel-zoom-toward-cursor. Dragging sets the
    // focus directly (screen-space mouse delta -> world-space focus delta along the camera's own
    // flattened right/forward axes) rather than going through the WASD inertia system — a drag is a
    // 1:1 "grab the ground" gesture, not an accelerating nudge.
    private bool _dragging;
    // The ground point under the cursor at the moment of the most recent wheel tick. Consumed by
    // ApplyZoomInertia every frame the height is still moving from that tick's impulse, so the focus
    // drifts toward the point the player was pointing at rather than staying fixed while height changes.
    private Vector3? _zoomFocusTarget;

    // Default pitch: eye offset from focus is (0, _height, _height * ZOffsetRatio). The Phase-3 blind
    // review flagged the world map as reading as a "hatching/scanline" glitch rather than hex tiles from
    // the default view — a low grazing angle on the flat tile tops is the classic cause of that kind of
    // moiré. The tile-mesh fix (MapView.BuildHexPrismMesh, a true non-overlapping hex prism) removes the
    // root cause, but a steeper default look-down angle is cheap insurance and also makes the hex grid
    // and tier silhouettes read more like a map, less like a diorama seen edge-on. 0.75 put the eye at
    // ~53 degrees above horizontal (~37 degrees off straight down); 0.45 pushed that to ~66 degrees.
    //
    // Stage 7 task B3 (docs/stage-7-plan.md): 0.45 read fine at the two CLOSE bookmarks (16/22 units up)
    // but fell apart at the two FAR ones — mid-region (55 up) filled only ~53% of frame height, and
    // max-zoom-out (FrameMap's whole-grid pull-back) worse still, both with a visible flat-sky-colour
    // strip at the top of frame (rays above the effective horizon, past the grid's far edge) AND at the
    // bottom (the camera's own ground-projected position trails height*ratio units behind the focus, and
    // at a large height that trailing point can itself sit past the map's near edge). A larger ratio
    // makes both worse — measured 0.60 at max-zoom-out: sky top and bottom, ~37% fill, clearly worse
    // than 0.45's baseline. A smaller ratio pulls the camera more overhead, shrinking both gaps at once
    // without visibly hurting the close bookmarks (still fill the frame edge-to-edge, hexes still read
    // as hexes, not the flagged moiré) — measured 0.30 at mid-region (~62%) and max-zoom-out (~68%),
    // both clearing the task's 60% bar; 0.45 was the one measured value that did not.
    //
    // Plan v3 task 1.1: FOV narrowed from the engine default 75 to 45 (the strategy-game look), and
    // pitch steepened further to compensate for how much less a narrower FOV sees — 0.30 (~73
    // degrees above horizontal) to 0.70 (~55 degrees), the "eye at focus + (0, h, 0.70h)" the task
    // specifies. Every height constant below is scaled by tan(37.5)/tan(22.5) ~= 1.85, the ratio
    // needed so the same world-space framing fills the frame at the new, narrower FOV.
    private const float ZOffsetRatio = 0.70f;

    private Vector3 _focus = new(60, 0, 60);
    private float _height = 90f;

    public override void _Ready()
    {
        Fov = 45f;

        // Stage 6: idempotent — safe even though MainMenu already calls this on the normal boot
        // path, and necessary on its own for a direct `Main.tscn` launch that skips the menu
        // entirely (still a fully supported path — see GameLaunchConfig's doc comment), which would
        // otherwise never load the persisted camera-speed setting at all.
        GameSettings.Load();
        ApplyTransform();
    }

    /// <summary>
    /// Centres on a grid of the given tile dimensions and pulls back far enough to hold it in frame.
    ///
    /// <para>The focus point used to be the hardcoded (60, 0, 60), which was tuned for one map size
    /// and quietly wrong for every other: on a 64x40 grid it looked at the far corner, so the A0
    /// screenshot came back with the world crammed into the top third of the frame and half the image
    /// empty background. Framing has to be derived from the map, not from a constant — the blind
    /// visual review judges what is in the picture.</para>
    /// </summary>
    public void FrameMap(int gridWidth, int gridHeight)
    {
        _gridWidth = gridWidth;
        _gridHeight = gridHeight;
        _panVelocity = Vector3.Zero;
        _zoomVelocity = 0f;

        // Matches HexLayout.ToWorld's odd-r spacing: x steps by sqrt(3), z by 1.5.
        float worldWidth = HexLayout.TileSize * Mathf.Sqrt(3f) * gridWidth;
        float worldDepth = HexLayout.TileSize * 1.5f * gridHeight;

        _focus = new Vector3(worldWidth * 0.5f, 0f, worldDepth * 0.5f);

        // Fit the larger axis with a small margin. The vertical FOV is what actually constrains the
        // view, so the width term is divided by the aspect ratio before comparing.
        float aspect = GetViewport() is Viewport viewport && viewport.GetVisibleRect().Size.Y > 0
            ? viewport.GetVisibleRect().Size.X / viewport.GetVisibleRect().Size.Y
            : 16f / 9f;
        float halfFovTan = Mathf.Tan(Mathf.DegToRad(Fov * 0.5f));
        float neededForDepth = worldDepth * 0.5f / halfFovTan;
        float neededForWidth = worldWidth * 0.5f / (halfFovTan * aspect);

        // Plan v3 task 1.1: this formula assumes a straight-down camera; the tilted eye
        // (ZOffsetRatio 0.70) sees less of the map at a given height than that assumes, so the
        // multiplier is bumped from 1.15 to compensate. Verified against shot 04 (max-zoom-out):
        // the whole 64x40 map must be inside the frame with a margin.
        _height = Mathf.Clamp(Mathf.Max(neededForDepth, neededForWidth) * 1.35f, MinHeight, MaxHeight);
        ApplyTransform();
    }

    // Stage 7 task 6 (docs/stage-7-plan.md, defect L): between "close-on-capital" (16) and "mid-region"
    // (55) — close enough that the capital and its immediate neighbours read clearly, wide enough to
    // show the surrounding terrain the player will expand into. Tuned by screenshot (18/26/34 compared),
    // not by argument. Plan v3 task 1.1: scaled by the same ~1.85 FOV-narrowing factor as every other
    // height constant in this file.
    private const float StartHeight = 26f;

    /// <summary>The opening shot: centred on a specific tile at a close, readable height, with the map
    /// dimensions still recorded so ClampFocusToMap works from the first frame. <see cref="FrameMap"/>
    /// (whole map, pulled right back) is the wrong opening for a player who does not yet know where they
    /// are — docs/stage-7-plan.md task 6. Reused directly by task B2's camera bookmarks, which is why
    /// <paramref name="height"/> is a parameter rather than always <see cref="StartHeight"/> — bookmarks
    /// resolve to the same kind of "a specific tile, a specific height" shot the opening frame is.</summary>
    /// <summary>FABLE STOP 2: re-frame on an already-resolved world-space focus at an explicit height
    /// (used by the --camera-height override after a bookmark has chosen the focus).</summary>
    public void FrameStartAtWorld(int gridWidth, int gridHeight, Vector3 focus, float height)
    {
        _gridWidth = gridWidth;
        _gridHeight = gridHeight;
        _panVelocity = Vector3.Zero;
        _zoomVelocity = 0f;
        _focus = focus;
        _height = Mathf.Clamp(height, MinHeight, MaxHeight);
        ClampFocusToMap();
        ApplyTransform();
    }

    public void FrameStart(int gridWidth, int gridHeight, int col, int row, float height = StartHeight)
    {
        _gridWidth = gridWidth;
        _gridHeight = gridHeight;
        _panVelocity = Vector3.Zero;
        _zoomVelocity = 0f;
        _focus = HexLayout.ToWorld(col, row);
        _height = height;
        ClampFocusToMap();
        ApplyTransform();
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        var input = Vector3.Zero;
        if (Input.IsKeyPressed(Key.W) || Input.IsKeyPressed(Key.Up)) input.Z -= 1;
        if (Input.IsKeyPressed(Key.S) || Input.IsKeyPressed(Key.Down)) input.Z += 1;
        if (Input.IsKeyPressed(Key.A) || Input.IsKeyPressed(Key.Left)) input.X -= 1;
        if (Input.IsKeyPressed(Key.D) || Input.IsKeyPressed(Key.Right)) input.X += 1;

        if (_edgeScrollEnabled) input += EdgeScrollInput();

        // Inertia: accelerate the velocity toward the input direction (scaled so a closer-in camera
        // pans slower than a zoomed-out one, unchanged from the grey-box feel), then always apply
        // friction — with no input this alone brings the camera to rest; with input, acceleration and
        // friction reach an equilibrium speed rather than growing unbounded.
        float speedScale = _height / MaxHeight + 0.2f;
        if (input != Vector3.Zero)
        {
            // Stage 6 settings: GameSettings.CameraSpeedMultiplier defaults to 1.0, so this is a
            // pure no-op against every pre-Stage-6 recording/screenshot unless the player has
            // actually changed the setting.
            _panVelocity += input.Normalized() * PanAcceleration * GameSettings.CameraSpeedMultiplier * speedScale * dt;
        }
        _panVelocity -= _panVelocity * Mathf.Min(PanFriction * dt, 1f);

        bool moved = false;
        if (_glideTarget is Vector3 glide)
        {
            if (input != Vector3.Zero || _dragging) _glideTarget = null;
            else
            {
                _focus = _focus.Lerp(glide, 1f - Mathf.Exp(-9f * dt));
                if (_focus.DistanceTo(glide) < 0.02f) { _focus = glide; _glideTarget = null; }
                moved = true;
            }
        }
        if (_panVelocity.LengthSquared() > 0.0001f)
        {
            _focus += _panVelocity * dt;
            moved = true;
        }

        if (ClampFocusToMap()) moved = true;

        float heightBefore = _height;
        ApplyZoomInertia(dt);
        if (_height != heightBefore) moved = true;

        if (moved) ApplyTransform();
    }

    /// <summary>Pan input from the mouse sitting near a viewport edge — only live while the viewport
    /// actually has the mouse over it, so the edge-scroll zone doesn't fire from a cursor that has
    /// simply never entered the game window.</summary>
    private Vector3 EdgeScrollInput()
    {
        var viewport = GetViewport();
        if (viewport is null) return Vector3.Zero;
        var size = viewport.GetVisibleRect().Size;
        if (size.X <= 0 || size.Y <= 0) return Vector3.Zero;

        var mouse = viewport.GetMousePosition();
        if (mouse.X < 0 || mouse.Y < 0 || mouse.X > size.X || mouse.Y > size.Y) return Vector3.Zero;

        var move = Vector3.Zero;
        if (mouse.X < EdgeScrollMargin) move.X -= 1;
        else if (mouse.X > size.X - EdgeScrollMargin) move.X += 1;
        if (mouse.Y < EdgeScrollMargin) move.Z -= 1;
        else if (mouse.Y > size.Y - EdgeScrollMargin) move.Z += 1;
        return move;
    }

    /// <summary>Framing limit: the focus point is clamped to the map's own bounds (plus a small margin
    /// so the edge doesn't feel like a wall) rather than panning into empty space forever. Returns
    /// whether clamping actually changed anything, so the caller only re-applies the transform when
    /// something moved.</summary>
    private bool ClampFocusToMap()
    {
        float worldWidth = HexLayout.TileSize * Mathf.Sqrt(3f) * _gridWidth;
        float worldDepth = HexLayout.TileSize * 1.5f * _gridHeight;
        // Stage 7 task B3 (docs/stage-7-plan.md): 8f let a manual pan carry the focus most of a tile
        // past the actual edge before stopping, on top of the framing gap task B3 already found at high
        // camera heights — the two compounded into a lot of empty background reachable by panning.
        // Tightened to a couple of tiles' worth of give (still not a hard wall the moment the edge tile
        // enters frame) rather than derived exactly from a 60%-of-frame bound, which would need to vary
        // with the live camera height/FOV rather than being a single constant — judgement call, recorded
        // per docs/stage-7-plan.md §0.4. Plan v3 task 1.1: widened 2 to 4 so the steeper-pitched camera
        // (ZOffsetRatio 0.70) can still look at the bottom map edge without the focus point itself being
        // clamped short of it.
        const float margin = 4f;

        var clamped = new Vector3(
            Mathf.Clamp(_focus.X, -margin, worldWidth + margin),
            0f,
            Mathf.Clamp(_focus.Z, -margin, worldDepth + margin));

        if (clamped.IsEqualApprox(_focus)) return false;
        _focus = clamped;
        return true;
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton mb)
        {
            if (mb.Pressed && mb.ButtonIndex == MouseButton.WheelUp)
            {
                _zoomVelocity -= ZoomAcceleration * GameSettings.CameraSpeedMultiplier;
                _zoomFocusTarget = RayToGround(mb.Position);
            }
            else if (mb.Pressed && mb.ButtonIndex == MouseButton.WheelDown)
            {
                _zoomVelocity += ZoomAcceleration * GameSettings.CameraSpeedMultiplier;
                _zoomFocusTarget = RayToGround(mb.Position);
            }
            else if (mb.ButtonIndex is MouseButton.Right or MouseButton.Middle)
            {
                _dragging = mb.Pressed;
                if (mb.Pressed) _panVelocity = Vector3.Zero;
            }
            return;
        }

        if (@event is InputEventMouseMotion motion && _dragging)
        {
            ApplyDragPan(motion.Relative);
        }
    }

    /// <summary>Right/middle-drag panning: moves the focus by the screen-space mouse delta projected
    /// onto the camera's own flattened right/forward axes, scaled by the current height so a zoomed-out
    /// drag covers more ground per pixel than a close one. Zeroes the WASD pan inertia every motion
    /// event so the two systems never fight over <c>_focus</c> mid-drag.</summary>
    private void ApplyDragPan(Vector2 relative)
    {
        var viewport = GetViewport();
        float viewportHeight = viewport is not null && viewport.GetVisibleRect().Size.Y > 0
            ? viewport.GetVisibleRect().Size.Y
            : 900f;
        float s = _height / viewportHeight * 1.6f;

        var right = GlobalTransform.Basis.X with { Y = 0 };
        var forward = (-GlobalTransform.Basis.Z) with { Y = 0 };
        if (right.LengthSquared() > 0.0001f) right = right.Normalized();
        if (forward.LengthSquared() > 0.0001f) forward = forward.Normalized();

        _panVelocity = Vector3.Zero;
        _focus += -relative.X * s * right + -relative.Y * s * forward;
        ClampFocusToMap();
        ApplyTransform();
    }

    private Vector3? RayToGround(Vector2 screenPos)
    {
        var from = ProjectRayOrigin(screenPos);
        var dir = ProjectRayNormal(screenPos);
        if (Mathf.Abs(dir.Y) < 1e-5f) return null;
        float t = -from.Y / dir.Y;
        return t < 0 ? null : from + dir * t;
    }

    private void ApplyZoomInertia(float dt)
    {
        _zoomVelocity -= _zoomVelocity * Mathf.Min(ZoomFriction * dt, 1f);
        if (Mathf.Abs(_zoomVelocity) < 0.001f)
        {
            _zoomFocusTarget = null;
            return;
        }

        float next = Mathf.Clamp(_height + _zoomVelocity * dt, MinHeight, MaxHeight);
        if (next != _height)
        {
            _height = next;
            // Zoom-toward-cursor: nudge the focus 20% of the way toward the ground point under the
            // cursor at the moment of the wheel tick, every frame the height is still moving from
            // that impulse. Good enough for a strategy camera (task 3.1) — not a precise "the pixel
            // under the cursor stays under the cursor" implementation.
            if (_zoomFocusTarget is Vector3 target)
            {
                var flat = new Vector3(target.X, 0f, target.Z);
                _focus = _focus.Lerp(flat, 0.20f);
            }
        }
        else
        {
            // Hit a hard clamp (fully zoomed in/out) — bleed off velocity instead of pinning it,
            // or scrolling further the same direction would do nothing for a noticeable pause once
            // the zoom is released back the other way.
            _zoomVelocity = 0f;
            _zoomFocusTarget = null;
        }
    }

    /// <summary>Plan v5 M2: the camera tilts more as it comes down — a close look is a landscape
    /// (about 45 degrees), a far look is a map (about 58 degrees).</summary>
    private float PitchRatio => Mathf.Lerp(1.0f, ZOffsetRatio, Mathf.Clamp((_height - MinHeight) / (70f - MinHeight), 0f, 1f));

    private void ApplyTransform()
    {
        var eye = _focus + new Vector3(0, _height, _height * PitchRatio);
        LookAtFromPosition(eye, _focus, Vector3.Up);
    }

    public Vector3 Focus => _focus;
    public float Height => _height;

    /// <summary>Stage 2 task 6: click-to-focus — the turn report (and the idle-loop button, task 7)
    /// move the camera to a settlement without changing zoom, so jumping around the map doesn't also
    /// randomise how close you're looking. Resets inertia for the same reason bookmarks do: a leftover
    /// pan/zoom velocity from before the jump would otherwise immediately carry the camera away from
    /// the place it was just asked to look at.</summary>
    /// <summary>Plan v5 M3: glides to the tile (about a third of a second) rather than cutting to
    /// it, so "Show me" and "next unit" read as travel across the map. Any pan input cancels it.</summary>
    public void FocusOn(int col, int row)
    {
        _glideTarget = HexLayout.ToWorld(col, row);
        _panVelocity = Vector3.Zero;
        _zoomVelocity = 0f;
    }

    private Vector3? _glideTarget;

    /// <summary>Plan v5 M3: the minimap's click and drag — look straight at a world point.</summary>
    public void LookAtWorld(Vector3 point)
    {
        _glideTarget = null;
        _panVelocity = Vector3.Zero;
        _focus = new Vector3(point.X, _focus.Y, point.Z);
        ClampFocusToMap();
        ApplyTransform();
    }

    // Stage 7 task B2 (docs/stage-7-plan.md): bookmarks used to be hand-picked hex coordinates in a
    // hardcoded 128x80 "reference world" (Stage 1's seed42-t100 worldview) — close-on-capital was
    // (22, 66), which simply does not exist on GameSession's actual default 64x40 map. ClampFocusToMap
    // silently dragged the focus back to the map edge, and every bookmark screenshot came back mostly
    // empty background. Bookmarks now resolve against the LIVE world (GameSession.ApplyCameraBookmark,
    // which has the player's capital and the real grid dimensions) instead of a hardcoded coordinate —
    // this class only exposes the low-level "go to this tile/height" and "fit this grid" primitives
    // (FrameStart/FrameMap) that resolution is built from.
    public static readonly string[] BookmarkNames = { "close-on-capital", "mid-region", "coastline", "max-zoom-out", "largest-settlement" };
}
