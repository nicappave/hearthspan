namespace Hearthspan.Content;

public sealed class LintResult
{
    public bool Ok => Issues.Count == 0;
    public List<string> Issues { get; } = new();

    public void Add(string issue) => Issues.Add(issue);
}
