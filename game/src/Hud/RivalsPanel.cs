using System;
using System.Collections.Generic;
using Godot;

namespace Hearthspan.Game.Hud;

/// <summary>
/// Plan v5 M3 (D6, D10): the other peoples, top-right — a colour, a name, a leader, and where you
/// stand with them. It doubles as the map's colour key. Click a people you have met for the
/// diplomacy actions (declare war, make peace), each with the reason it is or is not possible now.
/// </summary>
public partial class RivalsPanel : PanelContainer, IUiDumpable
{
    public sealed class Rival
    {
        public int CivId;
        public string Name = "";
        public string Leader = "";
        public Color Color;
        public bool Met;
        public bool IsPlayer;
        public string Status = "";
        public Color StatusColor = HudStyle.TextSoft;
        public string Detail = "";
        public List<SelectionCard.Action> Actions = new();
    }

    private VBoxContainer _col = null!;
    private List<Rival> _rivals = new();
    private int _openCivId = -1;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Stop;
        AddThemeStyleboxOverride("panel", HudStyle.Box(HudStyle.CardBg with { A = 0.88f }, HudStyle.LineSoft, 1, 7, 10, 8));
        _col = new VBoxContainer();
        _col.AddThemeConstantOverride("separation", 4);
        AddChild(_col);
    }

    public void Load(List<Rival> rivals)
    {
        _rivals = rivals;
        Rebuild();
    }

    private void Rebuild()
    {
        HudStyle.Clear(_col);
        _col.AddChild(HudStyle.Kicker("Peoples of the world"));
        int unmet = 0;
        foreach (var r in _rivals)
        {
            if (!r.Met && !r.IsPlayer) { unmet++; continue; }
            _col.AddChild(BuildRow(r));
            if (r.CivId == _openCivId && !r.IsPlayer) _col.AddChild(BuildDetail(r));
        }
        if (unmet > 0)
        {
            _col.AddChild(HudStyle.Label(unmet == 1 ? "1 people not yet met" : $"{unmet} peoples not yet met", 12, HudStyle.TextMuted));
        }
    }

    private Control BuildRow(Rival r)
    {
        var panel = new ClickPanel();
        panel.SetStyles(HudStyle.Box(new Color(0, 0, 0, 0), null, 0, 4, 4, 3), HudStyle.Box(HudStyle.ChipBg, HudStyle.LineSoft, 1, 4, 4, 3));
        if (r.IsPlayer) panel.MouseDefaultCursorShape = CursorShape.Arrow;
        else panel.Clicked = () => { _openCivId = _openCivId == r.CivId ? -1 : r.CivId; Rebuild(); };
        panel.TooltipText = r.IsPlayer ? "Your people" : "Click for diplomacy";

        var row = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", 7);
        var swatch = new Panel { CustomMinimumSize = new Vector2(12, 12), SizeFlagsVertical = SizeFlags.ShrinkCenter, MouseFilter = MouseFilterEnum.Ignore };
        swatch.AddThemeStyleboxOverride("panel", HudStyle.Box(r.Color, new Color(0, 0, 0, 0.5f), 1, 3, 0, 0));
        row.AddChild(swatch);
        var names = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore };
        names.AddThemeConstantOverride("separation", -2);
        names.AddChild(HudStyle.Label(r.IsPlayer ? $"{Capitalise(r.Name)} (you)" : Capitalise(r.Name), 13));
        names.AddChild(HudStyle.Label(r.Leader, 11, HudStyle.TextMuted));
        row.AddChild(names);
        if (r.Status != "") row.AddChild(HudStyle.Label(r.Status, 12, r.StatusColor));
        panel.AddChild(row);
        return panel;
    }

    private Control BuildDetail(Rival r)
    {
        var box = new PanelContainer();
        box.AddThemeStyleboxOverride("panel", HudStyle.Box(HudStyle.CardHeaderBg, HudStyle.LineSoft, 1, 5, 8, 6));
        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 5);
        col.AddChild(HudStyle.Label(r.Detail, 12, HudStyle.TextSoft, wrap: true));
        foreach (var a in r.Actions)
        {
            var action = a;
            var button = HudStyle.Button(a.Label, () => action.Commit?.Invoke(), a.Kind, 13,
                a.Enabled ? a.Tooltip : $"{a.Reason}\n\n{a.Tooltip}".Trim());
            button.Disabled = !a.Enabled;
            button.MouseFilter = MouseFilterEnum.Stop;
            col.AddChild(button);
            if (!a.Enabled && a.Reason != "") col.AddChild(HudStyle.Label(a.Reason, 11, HudStyle.TextMuted, wrap: true));
        }
        box.AddChild(col);
        return box;
    }

    public void OpenDiplomacy(int civId)
    {
        _openCivId = civId;
        Rebuild();
    }

    public void CloseDiplomacy()
    {
        if (_openCivId < 0) return;
        _openCivId = -1;
        Rebuild();
    }

    private static string Capitalise(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    public string DumpUiDigest()
    {
        var lines = new List<string> { $"RivalsPanel rows={_rivals.Count} open={_openCivId}" };
        foreach (var r in _rivals)
        {
            lines.Add($"  civ={r.CivId} \"{r.Name}\" leader=\"{r.Leader}\" met={r.Met} player={r.IsPlayer} status=\"{r.Status}\"");
            foreach (var a in r.Actions) lines.Add($"    action \"{a.Label}\" enabled={a.Enabled} reason=\"{a.Reason}\"");
        }
        return string.Join('\n', lines);
    }
}
