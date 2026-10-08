using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using Hearthspan.Content;
using Hearthspan.Sim;

namespace Hearthspan.Game;

/// <summary>
/// Stage 6 task 2: shared slot-storage policy for the multi-slot save system, used by both
/// <see cref="MainMenu"/>'s Load panel and <see cref="GameSession"/>'s Save/Load topbar dialog
/// (<see cref="SaveLoadPanel"/>) — one place decides where slots live and how a slot name maps to a
/// path, so the two surfaces can never disagree about what a "slot" is.
///
/// <para><b>Storage location: `user://saves/`</b> — Godot's per-user data directory (on this Windows
/// machine, `%APPDATA%/Godot/app_userdata/Hearthspan/saves/`), not a repo-adjacent folder. Two reasons:
/// (1) save games are player data, not project content — CLAUDE.md rule 3 reserves `content/` for
/// hand-authored, reviewable JSON design data; a save file is one particular playthrough's private
/// state, a different kind of artifact that has no business in a git diff. (2) Stage 6 task 5 (export)
/// ships a binary whose install directory may not be writable on the target machine — `user://` is
/// guaranteed writable post-export, a repo-adjacent path is not. This was already anticipated by task
/// 1's `MainMenu.FindSaveSlots` (see its doc comment, now superseded by this class) — this is that
/// "future proper multi-slot mechanism", promoted out of MainMenu so GameSession can share it instead
/// of re-implementing its own scan.</para>
///
/// <para>A slot's human-readable name IS its filename (sanitized to be filesystem-legal), and its
/// timestamp is the file's own last-write time. Deliberately no metadata field was added to
/// <see cref="SaveGame"/>'s DTO for this: the task scoped this as UI/plumbing over the existing save
/// format (Stage 6's documented sim policy is zero `sim/` diffs), and the filesystem already carries
/// both facts for free.</para>
/// </summary>
public static class SaveSlots
{
    /// <summary>The pre-Stage-6 default path (`%TEMP%/hearthspan-save.json`) — still what `--save-path`
    /// defaults to, and what the old single-slot Save button / `--save-now` wrote before this task. Kept
    /// as a listed slot under this label so a save made before multi-slot existed, or any
    /// `--save-path`-less headless run, doesn't just vanish from the picker.</summary>
    public const string LegacyQuickSaveLabel = "Quick Save";

    private const int DefaultMaxSlotsShown = 20;

    public readonly record struct Slot(string Path, string Name, DateTime When);

    /// <summary>Result of a save that was independently re-verified per the second-channel rule
    /// (CLAUDE.md) — see <see cref="SaveAndVerify"/>.</summary>
    public readonly record struct SaveResult(bool Verified, string Path, ulong LiveHash, ulong ReloadedHash, string? Error);

    public static string SavesDir => ProjectSettings.GlobalizePath("user://saves");

    public static void EnsureDir() => Directory.CreateDirectory(SavesDir);

    /// <summary>Filesystem-safe slot name: strips characters invalid in a Windows filename and trims
    /// whitespace, falling back to a timestamp-based name if nothing usable survives (e.g. a name that
    /// was 100% invalid characters) — a slot always gets *some* real file.</summary>
    public static string SanitizeName(string rawName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = rawName.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var cleaned = new string(chars).Trim();
        return cleaned.Length == 0 ? $"save-{DateTime.Now:yyyyMMdd-HHmmss}" : cleaned;
    }

    public static string PathFor(string slotName) => Path.Combine(SavesDir, SanitizeName(slotName) + ".json");

    public static bool Exists(string slotName) => File.Exists(PathFor(slotName));

    /// <summary>Every real slot in `user://saves/`, plus the legacy default path if it exists, newest
    /// first. This is the single source of truth every save-slot UI surface reads — no second, divergent
    /// scan anywhere else in game/.</summary>
    public static List<Slot> List(int max = DefaultMaxSlotsShown)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var slots = new List<Slot>();

        if (Directory.Exists(SavesDir))
        {
            foreach (var file in Directory.GetFiles(SavesDir, "*.json"))
            {
                var full = Path.GetFullPath(file);
                if (!seen.Add(full)) continue;
                slots.Add(new Slot(file, Path.GetFileNameWithoutExtension(file), File.GetLastWriteTime(file)));
            }
        }

        string legacyPath = Path.Combine(Path.GetTempPath(), "hearthspan-save.json");
        if (File.Exists(legacyPath) && seen.Add(Path.GetFullPath(legacyPath)))
        {
            slots.Add(new Slot(legacyPath, LegacyQuickSaveLabel, File.GetLastWriteTime(legacyPath)));
        }

        return slots.OrderByDescending(s => s.When).Take(max).ToList();
    }

    /// <summary>Look up a slot by its display name (case-insensitive) — used by `--load-slot` and by
    /// <see cref="SaveLoadPanel"/>'s slot-list clicks, both of which only have the name, not the path.</summary>
    public static Slot? Find(string slotName)
    {
        foreach (var slot in List())
        {
            if (string.Equals(slot.Name, slotName, StringComparison.OrdinalIgnoreCase)) return slot;
        }
        return null;
    }

    /// <summary>
    /// The second-channel rule (CLAUDE.md), applied to saves exactly per
    /// docs/development-plan-v2.md Stage 6 task 2: writes the file via the existing, unmodified
    /// <see cref="SaveGame.Save"/>, then independently re-reads it back into a brand-new
    /// <see cref="World"/> via <see cref="SaveGame.Load"/> — never the live world instance — and
    /// compares <see cref="World.StateHash"/>. Only a hash match counts as "saved"; a mismatch is a
    /// real bug (the file does not reproduce the live world) and is reported in
    /// <see cref="SaveResult.Error"/>, never swallowed. Every save path in game/ (the topbar Save
    /// dialog, `--save-slot`, and `--save-now`) goes through this one method, so there is exactly one
    /// place that decides what "saved" means.
    /// </summary>
    public static SaveResult SaveAndVerify(World world, ContentBundle content, string path)
    {
        EnsureDir();
        SaveGame.Save(world, path);

        World reloaded;
        try
        {
            reloaded = SaveGame.Load(path, content);
        }
        catch (Exception ex)
        {
            return new SaveResult(false, path, world.StateHash(), 0,
                $"re-load immediately after save threw: {ex.GetType().Name}: {ex.Message}");
        }

        ulong liveHash = world.StateHash();
        ulong reloadedHash = reloaded.StateHash();
        if (liveHash != reloadedHash)
        {
            return new SaveResult(false, path, liveHash, reloadedHash,
                $"state hash mismatch after save/reload: live={liveHash:x16} reloaded={reloadedHash:x16} — " +
                "the written file does not reproduce the live world. This is a save-format bug, not a UI issue.");
        }

        return new SaveResult(true, path, liveHash, reloadedHash, null);
    }
}
