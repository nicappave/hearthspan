using System.Collections.Generic;
using Godot;

namespace Hearthspan.Game;

/// <summary>
/// Settlement name labels with zoom LOD (docs/phase-5-plan.md Wave 0, milestone A0).
///
/// <para><b>One Control, one _Draw, no per-settlement nodes.</b> The obvious implementation — a
/// Label3D per settlement — would be several hundred nodes at Wave-0 scale and six hundred by Wave 5,
/// which is precisely what rule 5 forbids and what the tech-tree spike already proved out the
/// alternative for. This projects world positions to screen itself and draws strings into a single
/// canvas item, so the cost is one draw call regardless of settlement count.</para>
///
/// <para>LOD is by tier against camera height: a metropolis is named at any zoom, a camp only when
/// you are close enough that its name is not noise. That rule is what stops a 600-settlement map from
/// turning into an unreadable wall of text — the thing labels are supposed to prevent.</para>
/// </summary>
public partial class MapLabels : Control, IUiDumpable
{
    /// <summary>
    /// Camera height at or below which a tier's names are <i>eligible</i> to be drawn.
    ///
    /// <para><b>These are generous on purpose, and they are not the density control.</b> Two earlier
    /// passes tried to make absolute height thresholds do that job and both failed in opposite
    /// directions: tight values (Hamlet 22) meant a fresh game opened onto a map with no names on it
    /// at all, and loose ones buried a mature map under overlapping text. The deeper problem is that
    /// height is the wrong variable — <see cref="CameraRig.FrameMap"/> picks a height from the map's
    /// <i>size</i>, so the same threshold means "zoomed in" on a 64x40 map and "zoomed out" on the
    /// 128x80 standard one, and any hand-tuned ladder is wrong on at least one of them.</para>
    ///
    /// <para>So density is handled where it actually lives — collision rejection in
    /// <see cref="_Draw"/> — and these thresholds only express <i>priority</i>: sort by tier first, so
    /// when two names compete for the same patch of screen the bigger place wins, and let the
    /// declutterer decide how many fit. Zooming out then sheds small places automatically, because
    /// their labels are the ones that lose collisions.</para>
    /// </summary>
    private static readonly Dictionary<SettlementTierName, float> TierVisibleBelowHeight = new()
    {
        // Plan v3 task 1.1: scaled by the same ~1.85 FOV-narrowing factor as CameraRig's heights.
        [SettlementTierName.Ruins] = 85f,
        [SettlementTierName.Camp] = 85f,
        [SettlementTierName.Hamlet] = 175f,
        [SettlementTierName.Village] = 260f,
        [SettlementTierName.Town] = float.MaxValue,
        [SettlementTierName.City] = float.MaxValue,
        [SettlementTierName.Metropolis] = float.MaxValue,
    };

    private static readonly Dictionary<SettlementTierName, int> TierFontSize = new()
    {
        [SettlementTierName.Ruins] = 10,
        [SettlementTierName.Camp] = 10,
        [SettlementTierName.Hamlet] = 11,
        [SettlementTierName.Village] = 12,
        [SettlementTierName.Town] = 13,
        [SettlementTierName.City] = 15,
        [SettlementTierName.Metropolis] = 17,
    };

    /// <summary>Hard ceiling on labels drawn in one frame. Even with LOD, a low camera over a dense
    /// region can put a lot of names on screen; this bounds the worst case rather than trusting the
    /// LOD thresholds to be perfectly tuned forever. Labels are sorted nearest-first before the cut,
    /// so what survives is what the player is actually looking at.</summary>
    public const int MaxLabelsDrawn = 120;

    private MapView? _mapView;
    private CameraRig? _camera;
    private int _lastDrawnCount;

    public void Bind(MapView mapView, CameraRig camera)
    {
        _mapView = mapView;
        _camera = camera;
        MouseFilter = MouseFilterEnum.Ignore;   // labels must never eat map clicks
    }

    public override void _Process(double delta)
    {
        // The camera moves without the world changing, so the label layer redraws on its own cadence
        // rather than only when a new snapshot lands.
        if (_mapView?.World != null) QueueRedraw();
    }

    /// <summary>Plan v5 M3: per-settlement progress for the player's own banners — growth toward the
    /// next tier and the current build — set by the session each refresh (0..1, -1 when none).</summary>
    public IReadOnlyDictionary<int, (float Growth, float Build)> Progress { get; set; } = new Dictionary<int, (float, float)>();

    private readonly struct LabelPlan
    {
        public int Id { get; init; }
        public int Tier { get; init; }
        public float Depth { get; init; }
        public Vector2 Screen { get; init; }
        public Color CivColor { get; init; }
        public string NameText { get; init; }
        public string PopText { get; init; }
        public bool Capital { get; init; }
        public bool Stale { get; init; }
        public bool Mine { get; init; }
        public int FontSize { get; init; }
    }

    /// <summary>Plan v5 M3: Civ-style banners — a plate in the owner's colour with the population in a
    /// solid chip at the left, the name, a star on capitals, and (your own, zoomed in) thin growth and
    /// build bars underneath. Still one Control, one draw pass, decluttered biggest-first.</summary>
    public override void _Draw()
    {
        _lastDrawnCount = 0;
        if (_mapView?.World == null || _camera == null) return;

        var world = _mapView.World;
        var candidates = new List<LabelPlan>();
        float cameraHeight = _camera.Height;
        var capitals = new HashSet<int>();
        foreach (var c in world.Civilizations) capitals.Add(c.CapitalSettlementId);

        foreach (var (settlement, worldPos) in _mapView.LabelAnchors)
        {
            var tier = (SettlementTierName)Mathf.Clamp(settlement.Tier, 0, 6);
            if (!TierVisibleBelowHeight.TryGetValue(tier, out var threshold) || cameraHeight > threshold) continue;
            if (_camera.IsPositionBehind(worldPos)) continue;
            var screen = _camera.UnprojectPosition(worldPos);
            if (screen.X < -120 || screen.Y < -30 || screen.X > Size.X + 120 || screen.Y > Size.Y + 30) continue;

            var civColor = settlement.OwnerId is int owner
                ? MapView.CivPalette[OwnerIndex(owner) % MapView.CivPalette.Length]
                : MapView.FreeholdColor;
            string name = MapPalette.SettlementName(settlement.Id);
            if (tier == SettlementTierName.Ruins) name += " (ruins)";
            if (settlement.Stale) name += " ?";
            candidates.Add(new LabelPlan
            {
                Id = settlement.Id,
                Tier = (int)tier,
                Depth = _camera.GlobalPosition.DistanceSquaredTo(worldPos),
                Screen = screen,
                CivColor = civColor,
                NameText = name,
                PopText = settlement.Stale || settlement.Population <= 0 ? "?" : FormatPopulation(settlement.Population),
                Capital = capitals.Contains(settlement.Id) && settlement.OwnerId is not null,
                Stale = settlement.Stale,
                Mine = settlement.OwnerId is int o && o == world.ObserverCivId,
                FontSize = TierFontSize[tier] + 2,
            });
        }

        candidates.Sort((a, b) =>
        {
            int byTier = b.Tier.CompareTo(a.Tier);
            return byTier != 0 ? byTier : a.Depth.CompareTo(b.Depth);
        });

        var placed = new List<Rect2>();
        var font = MapPalette.UiFont;
        int drawn = 0;
        bool showBars = cameraHeight < 70f;

        foreach (var plan in candidates)
        {
            if (drawn >= MaxLabelsDrawn) break;

            int popFont = plan.FontSize - 1;
            var nameSize = font.GetStringSize(plan.NameText, HorizontalAlignment.Left, -1, plan.FontSize);
            var popSize = font.GetStringSize(plan.PopText, HorizontalAlignment.Left, -1, popFont);
            float height = plan.FontSize + 9f;
            float popWidth = Mathf.Max(height, popSize.X + 10f);
            float starWidth = plan.Capital ? height * 0.75f : 0f;
            float width = popWidth + nameSize.X + starWidth + 14f;
            bool bars = showBars && plan.Mine && Progress.ContainsKey(plan.Id);
            float totalHeight = height + (bars ? 7f : 0f);
            var rect = new Rect2(plan.Screen - new Vector2(width * 0.5f, height + 4f), new Vector2(width, height));
            var bounds = new Rect2(rect.Position - new Vector2(3, 3), new Vector2(width + 6, totalHeight + 6));

            bool collides = false;
            foreach (var existing in placed)
            {
                if (existing.Intersects(bounds)) { collides = true; break; }
            }
            if (collides) continue;
            placed.Add(bounds);

            float alpha = plan.Stale ? 0.6f : 1f;
            var plate = new StyleBoxFlat
            {
                BgColor = plan.CivColor.Darkened(0.62f) with { A = 0.9f * alpha },
                BorderColor = plan.CivColor.Lightened(0.15f) with { A = 0.95f * alpha },
                AntiAliasing = true,
            };
            plate.SetBorderWidthAll(1);
            plate.SetCornerRadiusAll((int)(height * 0.5f));
            DrawStyleBox(plate, rect);

            var chip = new StyleBoxFlat { BgColor = plan.CivColor with { A = alpha }, AntiAliasing = true };
            chip.SetCornerRadiusAll((int)(height * 0.5f));
            DrawStyleBox(chip, new Rect2(rect.Position, new Vector2(popWidth, height)));
            float baseline = rect.Position.Y + height * 0.5f + popSize.Y * 0.32f;
            var popColor = plan.CivColor.Luminance > 0.6f ? new Color(0.08f, 0.07f, 0.05f, alpha) : new Color(1, 1, 1, alpha);
            DrawString(font, new Vector2(rect.Position.X + (popWidth - popSize.X) * 0.5f, baseline), plan.PopText,
                HorizontalAlignment.Left, -1, popFont, popColor);

            float x = rect.Position.X + popWidth + 7f;
            if (plan.Capital)
            {
                Icons.Draw(this, Icon.Star, new Vector2(x - 1, rect.Position.Y + (height - starWidth) * 0.5f), starWidth,
                    new Color(1f, 0.84f, 0.38f, alpha));
                x += starWidth;
            }
            DrawString(font, new Vector2(x, rect.Position.Y + height * 0.5f + nameSize.Y * 0.32f), plan.NameText,
                HorizontalAlignment.Left, -1, plan.FontSize, new Color(1f, 0.97f, 0.9f, alpha));

            if (bars && Progress.TryGetValue(plan.Id, out var progress))
            {
                float bx = rect.Position.X + height * 0.4f, bw = width - height * 0.8f, by = rect.End.Y + 2f;
                DrawBar(new Rect2(bx, by, bw, 2.5f), progress.Growth, new Color(0.45f, 0.85f, 0.42f));
                if (progress.Build >= 0) DrawBar(new Rect2(bx, by + 4f, bw, 2.5f), progress.Build, new Color(0.45f, 0.70f, 1f));
            }
            drawn++;
        }
        _lastDrawnCount = drawn;
        DrawUnitFlags(placed);
        DrawFloaters();
    }

    private readonly List<(int SettlementId, string Text, Color Color, double Born)> _floaters = new();
    private const double FloaterSeconds = 2.4;

    /// <summary>Plan v5 M3: "+3" rising off a banner after a turn — every End Turn visibly does
    /// something to the places you own.</summary>
    public void AddFloaters(IEnumerable<(int SettlementId, string Text, Color Color)> items)
    {
        double now = Time.GetTicksMsec() / 1000.0;
        _floaters.RemoveAll(f => now - f.Born > FloaterSeconds);
        foreach (var (id, text, color) in items) _floaters.Add((id, text, color, now));
    }

    private void DrawFloaters()
    {
        if (_floaters.Count == 0 || _mapView is null || _camera is null) return;
        double now = Time.GetTicksMsec() / 1000.0;
        var font = MapPalette.UiFont;
        foreach (var (id, text, color, born) in _floaters)
        {
            float age = (float)(now - born);
            if (age > FloaterSeconds) continue;
            Vector3? anchor = null;
            foreach (var (settlement, pos) in _mapView.LabelAnchors)
            {
                if (settlement.Id == id) { anchor = pos; break; }
            }
            if (anchor is not Vector3 a || _camera.IsPositionBehind(a)) continue;
            var screen = _camera.UnprojectPosition(a) + new Vector2(0, -34f - age * 16f);
            float alpha = age < 0.2f ? age / 0.2f : 1f - Mathf.Clamp((age - 1.4f) / 1.0f, 0f, 1f);
            var size = font.GetStringSize(text, HorizontalAlignment.Left, -1, 17);
            var origin = screen - new Vector2(size.X * 0.5f, 0);
            DrawString(font, origin + new Vector2(1, 1), text, HorizontalAlignment.Left, -1, 17, new Color(0, 0, 0, 0.8f * alpha));
            DrawString(font, origin, text, HorizontalAlignment.Left, -1, 17, color with { A = alpha });
        }
    }

    /// <summary>Plan v5 M3: what the session tells the flags about each visible unit.</summary>
    public readonly record struct UnitFlag(Color Color, bool Scout, bool NeedsOrders, bool Selected);

    public IReadOnlyDictionary<int, UnitFlag> UnitFlags { get; set; } = new Dictionary<int, UnitFlag>();
    private UnitView? _units;

    public void BindUnits(UnitView units) => _units = units;

    /// <summary>Plan v5 M3: a small shield over every visible unit — its owner's colour and a sword or
    /// a compass — so units can be found at any zoom; yours that are waiting for orders carry a gold
    /// rim. Drawn after the banners, in the same single pass.</summary>
    private void DrawUnitFlags(List<Rect2> banners)
    {
        if (_units is null || _camera is null) return;
        float cameraHeight = _camera.Height;
        if (cameraHeight > 140f) return;
        float size = Mathf.Clamp(26f - cameraHeight * 0.12f, 13f, 22f);
        foreach (var (id, pos) in _units.Positions())
        {
            if (!UnitFlags.TryGetValue(id, out var flag)) continue;
            var top = pos + new Vector3(0, 0.62f, 0);
            if (_camera.IsPositionBehind(top)) continue;
            var screen = _camera.UnprojectPosition(top);
            if (screen.X < -20 || screen.Y < -20 || screen.X > Size.X + 20 || screen.Y > Size.Y + 20) continue;

            var rect = new Rect2(screen - new Vector2(size * 0.5f, size), new Vector2(size, size));
            // A unit standing in a settlement would hide its banner: its flag steps below the banner.
            foreach (var banner in banners)
            {
                if (!banner.Intersects(rect)) continue;
                rect.Position = new Vector2(rect.Position.X, banner.End.Y + 1f);
            }
            var shield = new[]
            {
                rect.Position, rect.Position + new Vector2(size, 0), rect.Position + new Vector2(size, size * 0.62f),
                rect.Position + new Vector2(size * 0.5f, size), rect.Position + new Vector2(0, size * 0.62f),
            };
            var rim = flag.Selected ? new Color(1f, 0.95f, 0.7f) : flag.NeedsOrders ? new Color(0.98f, 0.78f, 0.30f) : new Color(0.08f, 0.07f, 0.05f, 0.9f);
            var outer = new Vector2[shield.Length];
            var centre = rect.Position + new Vector2(size * 0.5f, size * 0.45f);
            for (int i = 0; i < shield.Length; i++) outer[i] = centre + (shield[i] - centre) * (1f + 2.5f / size);
            DrawColoredPolygon(outer, rim);
            DrawColoredPolygon(shield, flag.Color.Darkened(0.15f));
            Icons.Draw(this, flag.Scout ? Icon.Compass : Icon.Strength, rect.Position + new Vector2(size * 0.18f, size * 0.1f), size * 0.64f,
                new Color(1f, 0.97f, 0.9f));
        }
    }

    private void DrawBar(Rect2 rect, float value, Color fill)
    {
        DrawRect(rect, new Color(0, 0, 0, 0.55f));
        if (value > 0) DrawRect(new Rect2(rect.Position, new Vector2(rect.Size.X * Mathf.Clamp(value, 0, 1), rect.Size.Y)), fill);
    }

    /// <summary>Plan v3 task 2.7 step 2: "pop 1.2k" for a thousand or more, the plain integer below
    /// that (Town's own tier threshold is 500, so this is reachable at Town).</summary>
    private static string FormatPopulation(long population) =>
        population >= 10000 ? $"{population / 1000.0:F0}k" : population >= 1000 ? $"{population / 1000.0:F1}k" : population.ToString();

    private int OwnerIndex(int ownerId) => _mapView?.World?.Civilizations.Find(c => c.Id == ownerId)?.Index ?? 0;

    public string DumpUiDigest()
    {
        if (_mapView?.World == null || _camera == null) return "MapLabels (not bound)";

        var byTier = new Dictionary<SettlementTierName, int>();
        int eligible = 0;
        foreach (var (settlement, _) in _mapView.LabelAnchors)
        {
            var tier = (SettlementTierName)Mathf.Clamp(settlement.Tier, 0, 6);
            if (_camera.Height > TierVisibleBelowHeight[tier]) continue;
            eligible++;
            byTier[tier] = byTier.GetValueOrDefault(tier) + 1;
        }

        // Reports ELIGIBLE, not drawn. Eligibility is a pure function of tier and camera height, so it
        // is identical headless and windowed and is safe to golden. The drawn count is not: it depends
        // on screen-space projection and on this Control having a resolved size, and under --headless
        // at the frame dump-ui captures, neither is settled — an early run reported drawn=1 of 7
        // eligible while the windowed screenshot of the same state showed all seven. A golden that
        // recorded that number would be pinning a rendering artifact.
        var lines = new List<string>
        {
            $"MapLabels cameraHeight={_camera.Height:F1} eligible={eligible} cap={MaxLabelsDrawn}",
        };
        foreach (SettlementTierName tier in System.Enum.GetValues<SettlementTierName>())
        {
            lines.Add($"  {tier}: eligible={byTier.GetValueOrDefault(tier)} visibleBelowHeight={FormatThreshold(TierVisibleBelowHeight[tier])}");
        }
        return string.Join('\n', lines);
    }

    private static string FormatThreshold(float value) => value >= float.MaxValue ? "always" : value.ToString("F0");
}
