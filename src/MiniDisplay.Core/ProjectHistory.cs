namespace MiniDisplay.Core;

// Snapshots are immutable records; callers replace arrays instead of mutating them.
public sealed class ProjectHistory
{
    private readonly List<DisplayProject> undo = [];
    private readonly List<DisplayProject> redo = [];
    private string? lastGroup;
    public DisplayProject Current { get; private set; }
    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;

    public ProjectHistory(DisplayProject initial) { initial.Validate(); Current = initial; }
    public void BreakGroup() => lastGroup = null;
    public void Reset(DisplayProject project)
    {
        project.Validate(); Current = project; undo.Clear(); redo.Clear(); BreakGroup();
    }

    public bool Apply(DisplayProject project, string? group = null)
    {
        project.Validate();
        if (Equivalent(Current, project)) return false;
        if (group is null || group != lastGroup)
        {
            undo.Add(Current);
            if (undo.Count > 100) undo.RemoveAt(0);
        }
        Current = project; lastGroup = group; redo.Clear();
        return true;
    }

    public DisplayProject Undo()
    {
        BreakGroup();
        if (undo.Count == 0) return Current;
        redo.Add(Current); Current = undo[^1]; undo.RemoveAt(undo.Count - 1);
        return Current;
    }
    public DisplayProject Redo()
    {
        BreakGroup();
        if (redo.Count == 0) return Current;
        undo.Add(Current); Current = redo[^1]; redo.RemoveAt(redo.Count - 1);
        return Current;
    }

    public static bool Equivalent(DisplayProject a, DisplayProject b) =>
        a.Version == b.Version && a.FramesPerSecond == b.FramesPerSecond && a.AutoRotate == b.AutoRotate && a.Sources == b.Sources &&
        a.Assets.Length == b.Assets.Length && a.Assets.Zip(b.Assets).All(pair => pair.First.Id == pair.Second.Id && pair.First.Name == pair.Second.Name) &&
        a.Pages.Length == b.Pages.Length && a.Pages.Zip(b.Pages).All(pair =>
            (pair.First with { Layers = pair.Second.Layers }) == pair.Second &&
            pair.First.Layers.SequenceEqual(pair.Second.Layers));
}
