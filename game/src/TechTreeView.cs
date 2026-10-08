using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Hearthspan.Content;

namespace Hearthspan.Game;

/// <summary>
/// Killer spike A (docs/development-plan.md Phase 3 item 3): one custom Control, _Draw()-only, over a
/// virtualised viewport, hit-testing from the same layout data used to draw — no per-node scene-tree
/// nodes, ever (rule 5). Generates its own deterministic 2000+-node graph; a real tech tree loads
/// content JSON instead, but the render/virtualisation/hit-testing approach is identical either way,
/// which is the entire point of the spike.
/// </summary>
public partial class TechTreeView : Control, IUiDumpable
{
    // Stage 7 task 5c (docs/stage-7-plan.md): widened from 140x44 so DrawMultilineString has room to
    // wrap a name in 2 lines instead of clipping it ("Communal Organiza", "Hydraulic Engineeri" in the
    // before-shots). ColumnSpacing/RowSpacing widened to match.
    private const float NodeWidth = 170f;
    private const float NodeHeight = 46f;
    private const float ColumnSpacing = 210f;
    private const float RowSpacing = 58f;

    private const float MinZoom = 0.35f;
    private const float MaxZoom = 2.0f;
    private const float ZoomStep = 0.1f;
    private const float DragThresholdPixels = 6f;
    private const float KeepVisiblePixels = 120f;

    public enum TechStatus { Locked, Available, Researching, Completed }

    public readonly struct TechNode
    {
        public readonly int Id;
        public readonly int Era; // content era Order, for the per-node era stripe/legend colour only
        public readonly int Column; // Stage 7 task 5a: prereq-depth layer, drives Position.X
        public readonly Vector2 Position; // layout-space (pre-pan/zoom)
        public readonly int[] Prereqs;
        public readonly string TechId;
        public readonly string TechName;
        public readonly int Cost;
        public readonly TechStatus Status;

        public TechNode(int id, int era, int column, Vector2 position, int[] prereqs, string techId = "", string techName = "", int cost = 0, TechStatus status = TechStatus.Locked)
        {
            Id = id;
            Era = era;
            Column = column;
            Position = position;
            Prereqs = prereqs;
            TechId = techId;
            TechName = techName;
            Cost = cost;
            Status = status;
        }

        public Rect2 LayoutRect() => new(Position, new Vector2(NodeWidth, NodeHeight));
    }

    [Signal]
    public delegate void TechClickedEventHandler(string techId);

    private const float BorderWidth = 1.5f;

    private TechNode[] _nodes = Array.Empty<TechNode>();
    private int _eraCount = 10;
    private Vector2 _pan = Vector2.Zero;
    private float _zoom = 1f;
    private int _lastVisibleCount;
    private int _lastDrawnEdgeCount;

    // Stage 7 task 5b: pan/zoom state. _viewInitialised guards the one-time default framing in
    // LoadFromContent so a later RefreshAll (every turn, every research/build click) never yanks the
    // view back to the default while the player is looking somewhere else.
    private bool _viewInitialised;
    private Vector2 _pressPosition;
    private bool _dragMoved;
    private string? _researchingTechId;
    private readonly Dictionary<string, int> _queuePosition = new();
    private float _lastFitZoom = 1f;
    private int _columnCount = 1;
    private int _maxRowsInColumn = 1;
    private string[] _columnEraLabel = Array.Empty<string>();

    // Plan v3 task 3.7: kept from the last LoadFromContent call so _GetTooltip and the node body can
    // both answer "what does this unlock" without threading content through every draw call.
    private ContentBundle? _content;
    private ContentIndex? _contentIndex;
    private Dictionary<int, string> _eraNameByOrder = new();

    private Button _fitButton = null!;
    private Button _findResearchButton = null!;

    // Phase 4 perf fix (docs/phase-3-spikes.md Killer Spike A): the original implementation issued one
    // DrawRect (filled) + one DrawRect (outline) per visible node and one DrawLine per visible edge —
    // several thousand immediate-mode draw calls per frame at 2,000 nodes, measured at p50 ~20ms
    // windowed-offscreen (target <8ms). Fixed by batching: all node borders into one MultiMesh, all node
    // fills into a second MultiMesh (each drawn with a single DrawMultimesh call), and all edges into one
    // DrawMultiline call. Same visuals (border rect behind, inset fill rect on top approximates the old
    // filled-then-stroked look), same virtualisation/culling logic, same hit-testing — only the number of
    // draw calls per frame changes (thousands -> 3).
    private readonly QuadMesh _unitQuad = new() { Size = Vector2.One };
    private MultiMesh? _borderMultiMesh;
    private MultiMesh? _fillMultiMesh;

    public double LastDrawMicros { get; private set; }
    public double LastHitTestMicros { get; private set; }

    public override void _Ready()
    {
        ClipContents = true;
        MouseFilter = MouseFilterEnum.Stop;

        // Stage 7 task 5b: two real Button children (rule 5 has no concern with two nodes).
        _fitButton = new Button
        {
            Text = "Fit",
            Position = new Vector2(10, 10),
            Size = new Vector2(64, 28),
            TooltipText = "Zoom out to show the entire tech tree.",
        };
        _fitButton.Pressed += FitToContent;
        AddChild(_fitButton);

        _findResearchButton = new Button
        {
            Text = "Find current research",
            Position = new Vector2(82, 10),
            Size = new Vector2(200, 28),
            TooltipText = "Recentre the view on your current research target.",
        };
        _findResearchButton.Pressed += FindCurrentResearch;
        AddChild(_findResearchButton);

        // Bench/spike mode only (TechTreeSpike.tscn passes --node-count explicitly). The live game
        // (Main.tscn, via GameSession) calls LoadFromContent() itself once real content is ready, so
        // this must not clobber that with a synthetic graph.
        if (GreyBoxArgs.Get("node-count") is not null)
        {
            GenerateGraph(GreyBoxArgs.GetInt("node-count", 2000));
        }
    }

    /// <summary>Stage 7 task 5a (docs/stage-7-plan.md): longest prereq-chain depth. Content is a
    /// validated DAG (ContentLint enforces this), so recursion terminates — but a cycle guard keeps a
    /// future content bug a wrong layout, not a stack overflow.</summary>
    private static Dictionary<string, int> ComputeDepths(IEnumerable<Hearthspan.Content.Models.TechDef> techs, Dictionary<string, Hearthspan.Content.Models.TechDef> byId)
    {
        var depth = new Dictionary<string, int>();
        var visiting = new HashSet<string>();

        int Depth(string id)
        {
            if (depth.TryGetValue(id, out var d)) return d;
            if (!visiting.Add(id)) return 0; // cycle guard - should be unreachable
            var def = byId[id];
            int v = 0;
            foreach (var p in def.PrereqIds)
            {
                if (byId.ContainsKey(p)) v = Math.Max(v, Depth(p) + 1);
            }
            visiting.Remove(id);
            return depth[id] = v;
        }

        foreach (var t in techs) Depth(t.Id);
        return depth;
    }

    /// <summary>Real tech-tree load (docs/development-plan.md Phase 4 "production tech-tree UI") —
    /// same render/virtualisation/hit-testing path as the spike's synthetic graph, laid out from real
    /// content instead.
    ///
    /// <para>Stage 7 task 5a (docs/stage-7-plan.md): columns are prereq depth, not era — every edge then
    /// runs strictly left-to-right, which is what makes 107 real edges stop crossing the full height of
    /// a column. Row order within a column is a one-pass barycentre sort (mean row of each tech's
    /// prereqs in earlier columns, tie-broken by era order then id) to cut edge crossings further, fully
    /// deterministic since DumpUiDigest is golden-filed.</para></summary>
    public void LoadFromContent(ContentBundle content, HashSet<string> completedTechIds, string? researchingTechId,
        IReadOnlyList<string>? queuedAfter = null)
    {
        _researchingTechId = researchingTechId;
        // Plan v5 D7: the research queue — the target is 1, each queued tech after it the next number.
        _queuePosition.Clear();
        if (researchingTechId is not null) _queuePosition[researchingTechId] = 1;
        if (queuedAfter is not null)
        {
            for (int q = 0; q < queuedAfter.Count; q++) _queuePosition[queuedAfter[q]] = q + 2;
        }
        _content = content;
        _contentIndex = new ContentIndex(content);
        _eraCount = Math.Max(1, content.Eras.Count);
        var eraOrder = content.Eras.ToDictionary(e => e.Id, e => e.Order);
        var eraNameByOrder = content.Eras.ToDictionary(e => e.Order, e => e.Name);
        _eraNameByOrder = eraNameByOrder;
        var byId = content.Techs.ToDictionary(t => t.Id);

        var depth = ComputeDepths(content.Techs, byId);
        int maxDepth = depth.Count > 0 ? depth.Values.Max() : 0;
        _columnCount = maxDepth + 1;

        var rowByTechId = new Dictionary<string, int>();
        var columnEraOrders = new List<int>[_columnCount];
        for (int c = 0; c < _columnCount; c++) columnEraOrders[c] = new List<int>();

        var nodes = new List<TechNode>(content.Techs.Count);
        int globalIndex = 0;
        _maxRowsInColumn = 0;

        for (int column = 0; column < _columnCount; column++)
        {
            var inColumn = content.Techs.Where(t => depth[t.Id] == column).ToList();

            List<Hearthspan.Content.Models.TechDef> ordered;
            if (column == 0)
            {
                ordered = inColumn
                    .OrderBy(t => eraOrder.TryGetValue(t.EraId, out var o) ? o : 0)
                    .ThenBy(t => t.Id)
                    .ToList();
            }
            else
            {
                ordered = inColumn
                    .OrderBy(t =>
                    {
                        var prereqRows = t.PrereqIds.Where(rowByTechId.ContainsKey).Select(p => rowByTechId[p]).ToList();
                        return prereqRows.Count > 0 ? prereqRows.Average() : 0.0;
                    })
                    .ThenBy(t => eraOrder.TryGetValue(t.EraId, out var o) ? o : 0)
                    .ThenBy(t => t.Id)
                    .ToList();
            }

            _maxRowsInColumn = Math.Max(_maxRowsInColumn, ordered.Count);

            for (int row = 0; row < ordered.Count; row++)
            {
                var def = ordered[row];
                rowByTechId[def.Id] = row;
                int eraOrderValue = eraOrder.TryGetValue(def.EraId, out var eo) ? eo : 0;
                columnEraOrders[column].Add(eraOrderValue);

                var pos = new Vector2(column * ColumnSpacing, row * RowSpacing);
                // Prereq arrays need the OTHER node's global index. Every prereq is guaranteed to
                // already have one at this point (depth-by-construction puts every prereq in an earlier,
                // already-fully-processed column) - left empty here anyway and filled by the pass below,
                // simply so the index-lookup table only needs building once, after every tech exists.
                nodes.Add(new TechNode(globalIndex, eraOrderValue, column, pos, Array.Empty<int>(), def.Id, def.Name, def.Cost, StatusOf(def, completedTechIds, researchingTechId)));
                globalIndex++;
            }
        }

        // Second pass: now every tech has a dense global index (nodes list order == build order above),
        // so prereq arrays can be filled in with real indices rather than placeholders.
        var indexByTechId = new Dictionary<string, int>(nodes.Count);
        for (int i = 0; i < nodes.Count; i++) indexByTechId[nodes[i].TechId] = i;

        for (int i = 0; i < nodes.Count; i++)
        {
            var def = byId[nodes[i].TechId];
            var prereqIdx = def.PrereqIds.Where(indexByTechId.ContainsKey).Select(p => indexByTechId[p]).ToArray();
            nodes[i] = new TechNode(nodes[i].Id, nodes[i].Era, nodes[i].Column, nodes[i].Position, prereqIdx,
                nodes[i].TechId, nodes[i].TechName, nodes[i].Cost, nodes[i].Status);
        }

        // Stage 6 task 4's bugfix note (index invariant) still applies verbatim: HitTest returns a
        // node's own .Id, used directly as an index into the post-sort _nodes array by both _GuiInput
        // (TechClicked) and DumpUiDigest's tooltip sampling. Assert it rather than trust it.
        nodes.Sort((a, b) => a.Id.CompareTo(b.Id));
        _nodes = nodes.ToArray();
        for (int i = 0; i < _nodes.Length; i++)
        {
            if (_nodes[i].Id != i)
            {
                GD.PushError($"TechTreeView: node index invariant broken at {i} (Id={_nodes[i].Id}). " +
                              "HitTest results index into this array directly - see docs/stage-7-plan.md task 5a.");
            }
        }

        // Era becomes a visual band, not the x-axis (task 5a): label each column with the era of the
        // majority of its techs, or omit the header when no era has a strict majority in that column
        // (a column can span eras) rather than inventing a fake ordering.
        _columnEraLabel = new string[_columnCount];
        for (int c = 0; c < _columnCount; c++)
        {
            var group = columnEraOrders[c];
            if (group.Count == 0) { _columnEraLabel[c] = ""; continue; }
            var best = group.GroupBy(o => o).OrderByDescending(g => g.Count()).First();
            _columnEraLabel[c] = best.Count() * 2 > group.Count && eraNameByOrder.TryGetValue(best.Key, out var name) ? name : "";
        }

        InitializeDefaultView();
        QueueRedraw();
    }

    private static TechStatus StatusOf(Hearthspan.Content.Models.TechDef def, HashSet<string> completedTechIds, string? researchingTechId) =>
        completedTechIds.Contains(def.Id) ? TechStatus.Completed
        : def.Id == researchingTechId ? TechStatus.Researching
        : def.PrereqIds.All(completedTechIds.Contains) ? TechStatus.Available
        : TechStatus.Locked;

    /// <summary>Stage 7 task 5b: the opening framing for a freshly loaded tree — centred on the current
    /// research target, else the leftmost (lowest-column, then lowest-row) available tech, else column 0.
    /// Zoom 1.0 (readable), never fit-to-content (that's ~0.35, where labels are unreadable). Runs once
    /// per panel lifetime via <see cref="_viewInitialised"/> so a later reload (every RefreshAll) doesn't
    /// recentre the view while the player is looking around.</summary>
    private void InitializeDefaultView()
    {
        if (_viewInitialised) return;
        _viewInitialised = true;
        _zoom = 1f;

        TechNode target = default;
        bool found = false;
        if (_researchingTechId is not null)
        {
            foreach (var n in _nodes)
            {
                if (n.TechId == _researchingTechId) { target = n; found = true; break; }
            }
        }
        if (!found)
        {
            foreach (var n in _nodes)
            {
                if (n.Status != TechStatus.Available) continue;
                if (!found || n.Column < target.Column || (n.Column == target.Column && n.Position.Y < target.Position.Y))
                {
                    target = n; found = true;
                }
            }
        }

        // FABLE STOP 3: centring the target node left the top half of the screen empty (shot 05: the
        // frontier column's top row sat at y=445 with nothing above it). Put the target's top-left a
        // quarter of the way across and just under the era header row instead, so the whole frontier
        // column and the four columns after it are on screen at zoom 1.0.
        var targetTopLeft = found ? target.Position : Vector2.Zero;
        _pan = new Vector2(PanelSize.X * 0.22f, 110f) - targetTopLeft * _zoom;
        ClampPan();
    }

    private Vector2 PanelSize => Size.Y > 0 ? Size : new Vector2(1600f, 860f);

    private void CenterOn(Vector2 layoutPoint)
    {
        _pan = PanelSize * 0.5f - layoutPoint * _zoom;
        ClampPan();
    }

    /// <summary>"Fit" button: zoom out (floored at <see cref="MinZoom"/>) so the whole tree is visible
    /// at once, then centre on it.</summary>
    private void FitToContent()
    {
        if (_nodes.Length == 0) return;
        var (minX, minY, maxX, maxY) = LayoutBounds();
        var size = PanelSize;
        float contentW = Mathf.Max(1f, maxX - minX);
        float contentH = Mathf.Max(1f, maxY - minY);
        _zoom = Mathf.Max(MinZoom, Mathf.Min(size.X / contentW, size.Y / contentH) * 0.92f);
        _lastFitZoom = _zoom;
        CenterOn(new Vector2((minX + maxX) * 0.5f, (minY + maxY) * 0.5f));
        QueueRedraw();
    }

    /// <summary>"Find current research" button — recentres on whatever the player is researching right
    /// now (re-read at click time, not cached from load), at the current zoom. A no-op with nothing to
    /// find (no research target) rather than jumping to a fallback the button didn't promise.</summary>
    private void FindCurrentResearch()
    {
        if (_researchingTechId is null) return;
        foreach (var n in _nodes)
        {
            if (n.TechId == _researchingTechId)
            {
                CenterOn(n.Position + new Vector2(NodeWidth, NodeHeight) * 0.5f);
                QueueRedraw();
                return;
            }
        }
    }

    private (float minX, float minY, float maxX, float maxY) LayoutBounds()
    {
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        foreach (var n in _nodes)
        {
            minX = Mathf.Min(minX, n.Position.X);
            minY = Mathf.Min(minY, n.Position.Y);
            maxX = Mathf.Max(maxX, n.Position.X + NodeWidth);
            maxY = Mathf.Max(maxY, n.Position.Y + NodeHeight);
        }
        return (minX, minY, maxX, maxY);
    }

    /// <summary>Keeps at least <see cref="KeepVisiblePixels"/> of content on screen on each axis rather
    /// than letting pan drag the whole tree out of view (docs/stage-7-plan.md task 5b).</summary>
    private void ClampPan()
    {
        if (_nodes.Length == 0) return;
        var (minX, minY, maxX, maxY) = LayoutBounds();
        var size = PanelSize;

        float leftEdge = minX * _zoom + _pan.X;
        float rightEdge = maxX * _zoom + _pan.X;
        float topEdge = minY * _zoom + _pan.Y;
        float bottomEdge = maxY * _zoom + _pan.Y;

        if (rightEdge < KeepVisiblePixels) _pan.X += KeepVisiblePixels - rightEdge;
        if (leftEdge > size.X - KeepVisiblePixels) _pan.X -= leftEdge - (size.X - KeepVisiblePixels);
        if (bottomEdge < KeepVisiblePixels) _pan.Y += KeepVisiblePixels - bottomEdge;
        if (topEdge > size.Y - KeepVisiblePixels) _pan.Y -= topEdge - (size.Y - KeepVisiblePixels);
    }

    /// <summary>Stage 7 task 5b: click-vs-pan-vs-zoom, all through _GuiInput. Left-drag on empty space
    /// or middle-drag pans; left click-with-no-movement (same 6px threshold Task 1 uses for the
    /// road-drag/click distinction) still fires TechClicked; wheel zooms about the cursor position.</summary>
    public override void _GuiInput(InputEvent @event)
    {
        switch (@event)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } down:
                _pressPosition = down.Position;
                _dragMoved = false;
                break;

            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false } up:
                if (!_dragMoved)
                {
                    var idx = HitTest(up.Position);
                    if (idx is int i && !string.IsNullOrEmpty(_nodes[i].TechId))
                    {
                        EmitSignal(SignalName.TechClicked, _nodes[i].TechId);
                    }
                }
                break;

            case InputEventMouseButton { ButtonIndex: MouseButton.Middle, Pressed: true } mdown:
                _pressPosition = mdown.Position;
                _dragMoved = true; // middle-drag pans immediately - no click meaning to protect
                break;

            case InputEventMouseMotion motion:
                bool leftHeld = (motion.ButtonMask & MouseButtonMask.Left) != 0;
                bool middleHeld = (motion.ButtonMask & MouseButtonMask.Middle) != 0;
                if (!leftHeld && !middleHeld) break;
                if (!_dragMoved && motion.Position.DistanceTo(_pressPosition) > DragThresholdPixels) _dragMoved = true;
                if (_dragMoved)
                {
                    _pan += motion.Relative;
                    ClampPan();
                    QueueRedraw();
                }
                break;

            case InputEventMouseButton { ButtonIndex: MouseButton.WheelUp, Pressed: true } wheelUp:
                ZoomAboutCursor(wheelUp.Position, _zoom * (1f + ZoomStep));
                break;

            case InputEventMouseButton { ButtonIndex: MouseButton.WheelDown, Pressed: true } wheelDown:
                ZoomAboutCursor(wheelDown.Position, _zoom * (1f - ZoomStep));
                break;
        }
    }

    private void ZoomAboutCursor(Vector2 cursor, float newZoomRaw)
    {
        float newZoom = Mathf.Clamp(newZoomRaw, MinZoom, MaxZoom);
        if (Mathf.IsEqualApprox(newZoom, _zoom)) return;
        _pan = cursor - (cursor - _pan) * (newZoom / _zoom);
        _zoom = newZoom;
        ClampPan();
        QueueRedraw();
    }

    /// <summary>Deterministic layout generator: nodes fill era columns left-to-right, each node
    /// prereq-linked to 1-2 nodes in the previous era — enough structure to draw a real edge count
    /// without needing actual content JSON for a rendering-cost spike.</summary>
    public void GenerateGraph(int nodeCount)
    {
        _eraCount = Math.Max(1, (int)Math.Ceiling(nodeCount / 220.0));
        int perEra = (int)Math.Ceiling((double)nodeCount / _eraCount);
        int columns = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(perEra)));

        var nodes = new TechNode[nodeCount];
        var eraStartIndex = new int[_eraCount];
        int cursor = 0;
        for (int e = 0; e < _eraCount; e++)
        {
            eraStartIndex[e] = cursor;
            cursor += Math.Min(perEra, nodeCount - cursor);
        }

        for (int i = 0; i < nodeCount; i++)
        {
            int era = Math.Min(_eraCount - 1, i / perEra);
            int withinEra = i - eraStartIndex[era];
            int col = withinEra % columns;
            int row = withinEra / columns;

            var pos = new Vector2(era * ColumnSpacing, row * RowSpacing + (col % 2) * (RowSpacing * 0.5f));

            int[] prereqs;
            if (era == 0)
            {
                prereqs = Array.Empty<int>();
            }
            else
            {
                int prevEraStart = eraStartIndex[era - 1];
                int prevEraCount = eraStartIndex[era] - prevEraStart;
                int p0 = prevEraStart + (withinEra % Math.Max(1, prevEraCount));
                int p1 = prevEraStart + ((withinEra + 1) % Math.Max(1, prevEraCount));
                prereqs = p0 == p1 ? new[] { p0 } : new[] { p0, p1 };
            }

            nodes[i] = new TechNode(i, era, era, pos, prereqs);
        }

        _nodes = nodes;
        _viewInitialised = true; // spike mode drives pan/zoom via SetView, not the real-content default
        QueueRedraw();
    }

    public void SetView(Vector2 pan, float zoom)
    {
        _pan = pan;
        _zoom = Mathf.Clamp(zoom, MinZoom, 3f);
        QueueRedraw();
    }

    private Rect2 VisibleLayoutRect()
    {
        var topLeft = (-_pan) / _zoom;
        var size = Size / _zoom;
        return new Rect2(topLeft, size).Grow(NodeWidth); // grow so partially-visible edges still draw
    }

    public override void _Draw()
    {
        ulong start = Time.GetTicksUsec();

        // Opaque backdrop — without it the 3D map behind this Control shows through wherever nodes
        // don't cover, which read as a rendering glitch, not "the tech tree panel is open." UiScreenBg,
        // not UiPanelBg — see its doc comment (docs/stage-7-plan.md defect H): UiPanelBg's 0.88 alpha
        // is for small floating overlays, and lets the map bleed through at full screen.
        DrawRect(new Rect2(Vector2.Zero, Size), MapPalette.UiScreenBg, filled: true);

        var visible = VisibleLayoutRect();

        // Edges first (drawn under nodes), only when both endpoints are in the visible set. Batched
        // into a single DrawMultiline call instead of one DrawLine per edge.
        var edgePoints = new List<Vector2>();
        foreach (var node in _nodes)
        {
            if (!visible.Intersects(node.LayoutRect())) continue;
            foreach (var prereqId in node.Prereqs)
            {
                var prereq = _nodes[prereqId];
                edgePoints.Add(ToScreen(prereq.Position + new Vector2(NodeWidth, NodeHeight * 0.5f)));
                edgePoints.Add(ToScreen(node.Position + new Vector2(0, NodeHeight * 0.5f)));
            }
        }
        int edgeCount = edgePoints.Count / 2;
        if (edgeCount > 0)
        {
            DrawMultiline(edgePoints.ToArray(), new Color(0.5f, 0.5f, 0.55f), 1.5f);
        }

        // Nodes: two MultiMesh batches (border rects, inset fill rects) instead of two DrawRect calls
        // per node, plus a third pass for the per-node era stripe (task 5a: era is now a visual accent,
        // not the x-axis). Visually equivalent to the old filled-then-stroked rect (fill inset by the
        // border width leaves the same border-color ring visible around the edge).
        int visibleCount = 0;
        foreach (var node in _nodes)
        {
            if (visible.Intersects(node.LayoutRect())) visibleCount++;
        }

        EnsureMultiMeshCapacity(visibleCount);
        if (visibleCount > 0)
        {
            int i = 0;
            foreach (var node in _nodes)
            {
                var rect = node.LayoutRect();
                if (!visible.Intersects(rect)) continue;

                var screenPos = ToScreen(node.Position);
                var screenSize = rect.Size * _zoom;
                var color = string.IsNullOrEmpty(node.TechId) ? EraColor(node.Era) : StatusColor(node.Status);

                var center = screenPos + screenSize * 0.5f;
                var borderXform = new Transform2D(new Vector2(screenSize.X, 0f), new Vector2(0f, screenSize.Y), center);
                _borderMultiMesh!.SetInstanceTransform2D(i, borderXform);
                _borderMultiMesh.SetInstanceColor(i, color.Darkened(0.3f));

                var fillSize = new Vector2(
                    Mathf.Max(0f, screenSize.X - 2f * BorderWidth),
                    Mathf.Max(0f, screenSize.Y - 2f * BorderWidth));
                var fillXform = new Transform2D(new Vector2(fillSize.X, 0f), new Vector2(0f, fillSize.Y), center);
                _fillMultiMesh!.SetInstanceTransform2D(i, fillXform);
                _fillMultiMesh.SetInstanceColor(i, color);

                i++;
            }

            DrawMultimesh(_borderMultiMesh!, null);
            DrawMultimesh(_fillMultiMesh!, null);
        }

        // Real-content-mode era stripe: a thin coloured band on each node's left edge, since era is no
        // longer the x-axis and needs some other visual presence (task 5a).
        foreach (var node in _nodes)
        {
            if (string.IsNullOrEmpty(node.TechId)) break;
            if (!visible.Intersects(node.LayoutRect())) continue;
            var screenPos = ToScreen(node.Position);
            var screenSize = node.LayoutRect().Size * _zoom;
            DrawRect(new Rect2(screenPos, new Vector2(Mathf.Max(2f, 4f * _zoom), screenSize.Y)), EraColor(node.Era), filled: true);
        }

        // Real-content mode only (dozens of nodes, not the 2,000-node synthetic spike graph this
        // batching exists for). Task 5c: DrawMultilineString wraps instead of clipping, and labels are
        // skipped entirely below zoom 0.5 (unreadable anyway, and per-node DrawString is what dominates
        // cost at high node counts).
        if (_zoom >= 0.5f)
        {
            foreach (var node in _nodes)
            {
                if (string.IsNullOrEmpty(node.TechId)) break;
                if (!visible.Intersects(node.LayoutRect())) continue;

                var screenPos = ToScreen(node.Position);
                var screenSize = node.LayoutRect().Size * _zoom;
                int fontSize = (int)Mathf.Clamp(13 * _zoom, 8, 15);
                DrawMultilineString(MapPalette.UiFont, screenPos + new Vector2(6, screenSize.Y * 0.32f), node.TechName,
                    HorizontalAlignment.Left, screenSize.X - 12, fontSize, 2, new Color(0.05f, 0.05f, 0.05f));

                // Plan v3 task 3.7: at zoom 1.0+ there's room to fold the first unlock into the bottom
                // line rather than leaving "cost N" as the only thing a glance at the node tells you.
                var unlocks = _zoom >= 1.0f ? _contentIndex?.Unlocks(node.TechId) : null;
                string bottomLine = unlocks is { Count: > 0 } ? $"cost {node.Cost} · unlocks {unlocks[0].Name}" : $"cost {node.Cost}";
                DrawString(MapPalette.UiFont, screenPos + new Vector2(6, screenSize.Y - 5), bottomLine,
                    HorizontalAlignment.Left, screenSize.X - 10, Mathf.Max(8, fontSize - 3), new Color(0.15f, 0.15f, 0.15f));

                // Plan v5 D7: queued research wears its place in line.
                if (_queuePosition.TryGetValue(node.TechId, out int place))
                {
                    float r = Mathf.Clamp(10f * _zoom, 7f, 12f);
                    var c = screenPos + new Vector2(screenSize.X - r * 0.6f, r * 0.6f);
                    DrawCircle(c, r + 1.5f, new Color(0.10f, 0.08f, 0.06f));
                    DrawCircle(c, r, new Color(0.93f, 0.75f, 0.36f));
                    string text = place.ToString();
                    int badgeFont = (int)(r * 1.25f);
                    var size = MapPalette.UiFont.GetStringSize(text, HorizontalAlignment.Left, -1, badgeFont);
                    DrawString(MapPalette.UiFont, c + new Vector2(-size.X * 0.5f, size.Y * 0.32f), text,
                        HorizontalAlignment.Left, -1, badgeFont, new Color(0.10f, 0.08f, 0.06f));
                }
            }
        }

        // Pinned per-column era header row (task 5a) — moves horizontally with pan/zoom, fixed
        // vertically just below the Fit/Find-research buttons, so it reads like a column heading rather
        // than scrolling out of reach with the rest of the content.
        if (_nodes.Length > 0 && !string.IsNullOrEmpty(_nodes[0].TechId))
        {
            for (int c = 0; c < _columnEraLabel.Length; c++)
            {
                if (string.IsNullOrEmpty(_columnEraLabel[c])) continue;
                // FABLE STOP 3: label an era once, at its first column — "Neolithic  Neolithic" over
                // two adjacent columns read as two different things.
                if (c > 0 && _columnEraLabel[c] == _columnEraLabel[c - 1]) continue;
                float screenX = c * ColumnSpacing * _zoom + _pan.X;
                if (screenX < -ColumnSpacing * _zoom || screenX > Size.X) continue;
                DrawString(MapPalette.UiFont, new Vector2(screenX + 6, 55), _columnEraLabel[c],
                    HorizontalAlignment.Left, ColumnSpacing * _zoom - 10, 13, MapPalette.UiTextSecondary);
            }

            DrawString(MapPalette.UiFont, new Vector2(10, Size.Y - 12), "Drag to pan - scroll to zoom",
                HorizontalAlignment.Left, -1, 12, MapPalette.UiTextMuted);
        }

        // Legend (real-content mode only) — task 5c moves it to the bottom-right with an opaque
        // background so it can't sit on top of nodes the way "Size.X - 170" at the top used to.
        if (_nodes.Length > 0 && !string.IsNullOrEmpty(_nodes[0].TechId))
        {
            const float legendWidth = 150f;
            const float legendHeight = 4 * 22f + 8f;
            var legendPos = new Vector2(Size.X - legendWidth - 10f, Size.Y - legendHeight - 10f);
            DrawRect(new Rect2(legendPos, new Vector2(legendWidth, legendHeight)), MapPalette.UiPanelBg, filled: true);
            DrawRect(new Rect2(legendPos, new Vector2(legendWidth, legendHeight)), MapPalette.UiPanelBorder, filled: false, width: 1.5f);
            DrawLegendEntry(legendPos, 0, "Completed", TechStatus.Completed);
            DrawLegendEntry(legendPos, 1, "Researching", TechStatus.Researching);
            DrawLegendEntry(legendPos, 2, "Available", TechStatus.Available);
            DrawLegendEntry(legendPos, 3, "Locked", TechStatus.Locked);
        }

        _lastVisibleCount = visibleCount;
        _lastDrawnEdgeCount = edgeCount;
        LastDrawMicros = Time.GetTicksUsec() - start;
    }

    private void DrawLegendEntry(Vector2 basePos, int row, string label, TechStatus status)
    {
        var pos = basePos + new Vector2(8, 6 + row * 22);
        DrawRect(new Rect2(pos, new Vector2(16, 16)), StatusColor(status), filled: true);
        DrawString(MapPalette.UiFont, pos + new Vector2(22, 13), label, HorizontalAlignment.Left, -1, 13, MapPalette.UiTextPrimary);
    }

    /// <summary>(Re)allocates both MultiMesh instance buffers only when the visible count actually
    /// grows/shrinks, not on every frame — SetInstanceCount reallocates its backing buffer.</summary>
    private void EnsureMultiMeshCapacity(int count)
    {
        _borderMultiMesh ??= NewMultiMesh();
        _fillMultiMesh ??= NewMultiMesh();
        if (_borderMultiMesh.InstanceCount != count) _borderMultiMesh.InstanceCount = count;
        if (_fillMultiMesh.InstanceCount != count) _fillMultiMesh.InstanceCount = count;
    }

    private MultiMesh NewMultiMesh() => new()
    {
        TransformFormat = MultiMesh.TransformFormatEnum.Transform2D,
        UseColors = true,
        Mesh = _unitQuad,
    };

    private Vector2 ToScreen(Vector2 layoutPos) => layoutPos * _zoom + _pan;

    private static Color EraColor(int era)
    {
        float hue = (era * 0.11f) % 1f;
        return Color.FromHsv(hue, 0.45f, 0.85f);
    }

    private static Color StatusColor(TechStatus status) => status switch
    {
        TechStatus.Completed => new Color(0.30f, 0.75f, 0.35f),
        TechStatus.Researching => new Color(0.85f, 0.70f, 0.20f),
        TechStatus.Available => new Color(0.35f, 0.55f, 0.85f),
        _ => new Color(0.35f, 0.35f, 0.38f),
    };

    /// <summary>Stage 6 task 4: per-node tooltips. Godot Controls only ever get one static
    /// <c>TooltipText</c> string each — this custom-drawn view has 2,000+ "nodes" that are never real
    /// Control children (rule 5), so a single static string can't describe all of them. <c>Control</c>
    /// exposes exactly the escape hatch this needs: overriding <c>_GetTooltip(Vector2)</c> lets Godot's
    /// own tooltip system ask, per mouse position, what to show — and this reuses <see cref="HitTest"/>
    /// verbatim, the same layout-driven hit-testing <c>_GuiInput</c> already uses to fire clicks, so a
    /// tooltip and a click can never disagree about which node the mouse is over.</summary>
    public override string _GetTooltip(Vector2 atPosition)
    {
        var idx = HitTest(atPosition);
        if (idx is not int i) return "";
        var node = _nodes[i];
        // Synthetic spike graph (TechTreeSpike.tscn's --node-count mode) never has a TechId — nothing
        // real to say about a node that isn't a real tech, so no tooltip rather than a placeholder one.
        if (string.IsNullOrEmpty(node.TechId)) return "";

        string status = node.Status switch
        {
            TechStatus.Completed => "Completed.",
            TechStatus.Researching => "Currently your research target.",
            TechStatus.Available => "Available now — click to research it next.",
            _ => "Locked — click to queue it: every technology on the way is researched first.",
        };

        // Plan v3 task 3.7: name, era, cost, prerequisites, bonus, Unlocks — everything a player would
        // want to know before committing research to this tech, not just its name and status.
        var def = _content?.Techs.Find(t => t.Id == node.TechId);
        if (def is null) return $"{node.TechName}\n{status}";

        string era = _eraNameByOrder.TryGetValue(node.Era, out var eraName) ? eraName : "";
        string prereqs = def.PrereqIds.Count > 0
            ? string.Join(", ", def.PrereqIds.Select(id => _content!.Techs.Find(t => t.Id == id)?.Name ?? id))
            : "none";
        var unlocks = _contentIndex?.Unlocks(node.TechId) ?? Array.Empty<ContentIndex.Unlock>();
        string unlocksText = unlocks.Count > 0 ? string.Join(", ", unlocks.Select(u => u.Describe())) : "nothing yet";

        return $"{node.TechName} ({era})\nCost {node.Cost}. Prerequisites: {prereqs}.\n" +
               $"{ContentIndex.EffectText(def)}.\nUnlocks: {unlocksText}.\n{status}";
    }

    /// <summary>Hit-test against the same layout data _Draw() reads — no separate authoritative
    /// layout, so drawn position and clickable position can never drift apart.</summary>
    public int? HitTest(Vector2 screenPos)
    {
        ulong start = Time.GetTicksUsec();
        int? found = null;
        var layoutPos = (screenPos - _pan) / _zoom;
        foreach (var node in _nodes)
        {
            if (node.LayoutRect().HasPoint(layoutPos))
            {
                found = node.Id;
                break;
            }
        }
        LastHitTestMicros = Time.GetTicksUsec() - start;
        return found;
    }

    public string DumpUiDigest()
    {
        var lines = new List<string>
        {
            $"TechTreeView nodeCount={_nodes.Length} eraCount={_eraCount}",
            $"pan={_pan} zoom={_zoom:F3}",
            $"lastVisibleCount={_lastVisibleCount} lastDrawnEdgeCount={_lastDrawnEdgeCount}",
            // Draw and hit-test timings are deliberately not here: they change on every run, and a
            // digest may only assert pure functions of state (CLAUDE.md trap list). The spike runner
            // reads LastDrawMicros/LastHitTestMicros directly for its metrics.
        };

        if (_nodes.Length > 0 && !string.IsNullOrEmpty(_nodes[0].TechId))
        {
            // Stage 7 task 5: layout-shape facts that are pure functions of content, never of viewport
            // size (trap list: a dump-ui digest may only assert pure functions of sim/UI state).
            lines.Add($"lastFitZoom={_lastFitZoom:F3} columns={_columnCount} maxRowsInColumn={_maxRowsInColumn}");

            var byStatus = _nodes.GroupBy(n => n.Status).OrderBy(g => g.Key);
            lines.Add("statusCounts: " + string.Join(' ', byStatus.Select(g => $"{g.Key}={g.Count()}")));

            // Stage 6 task 4 verification: per-node tooltips are wired through _GetTooltip, not a
            // static TooltipText property (see that method's doc comment), so a dump-ui digest can't
            // just read one field off the Control the way every other panel's button-tooltip check
            // does. Instead this calls the exact same _GetTooltip(Vector2) Godot's own tooltip system
            // calls, at the exact screen position HitTest already maps back to that node — one sample
            // per status actually present, proving the mechanism produces real, non-empty text without
            // needing a simulated mouse-hover screenshot (CLAUDE.md trap list: dump-ui may only assert
            // pure functions of state, and this is one — no projection/viewport dependence at all).
            lines.Add("tooltipSamples:");
            foreach (var status in byStatus.Select(g => g.Key))
            {
                var sample = _nodes.First(n => n.Status == status && !string.IsNullOrEmpty(n.TechId));
                var screenPos = ToScreen(sample.Position + new Vector2(NodeWidth, NodeHeight) * 0.5f);
                string tooltip = _GetTooltip(screenPos).Replace("\n", " / ");
                lines.Add($"  {status} ({sample.TechId}): \"{tooltip}\"");
            }
        }

        return string.Join('\n', lines);
    }
}
