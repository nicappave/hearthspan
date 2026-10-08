namespace Hearthspan.Game;

/// <summary>Scenes implement this to produce a text digest of their UI/layout state for
/// `hearthspan dump-ui` — verified as text, never pixels (docs/development-plan.md rule 5).</summary>
public interface IUiDumpable
{
    string DumpUiDigest();
}
