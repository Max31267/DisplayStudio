namespace MiniDisplay.Core;

// Uses elapsed, monotonic time; clock/timezone changes cannot alter page duration.
public sealed class PagePlayback
{
    public Guid CurrentPageId { get; private set; }
    private TimeSpan startedAt;
    private Guid? interruption, returnPage;
    private TimeSpan interruptionUntil;

    public void Select(Guid id, TimeSpan now) { CurrentPageId = id; startedAt = now; interruption = returnPage = null; }
    public bool Interrupt(DisplayProject project, Guid id, TimeSpan now, TimeSpan duration)
    {
        if (!project.AutoRotate || duration <= TimeSpan.Zero || !project.Pages.Any(page => page.Id == id && page.Enabled)) return false;
        if (interruption is null) returnPage = CurrentPageId;
        interruption = id; interruptionUntil = now + duration; CurrentPageId = id; startedAt = now; return true;
    }

    public DisplayPage Update(DisplayProject project, TimeSpan now)
    {
        var pages = project.Pages.Where(page => page.Enabled).ToArray();
        if (pages.Length == 0) throw new InvalidOperationException("Нет включённых страниц.");
        if (interruption is Guid temporary)
        {
            var selected = pages.FirstOrDefault(page => page.Id == temporary);
            if (project.AutoRotate && now < interruptionUntil && selected is not null) return selected;
            Guid restore = !project.AutoRotate && selected is not null ? selected.Id : returnPage is Guid previous && pages.Any(page => page.Id == previous) ? previous : pages[0].Id;
            Select(restore, now);
        }
        int index = Array.FindIndex(pages, page => page.Id == CurrentPageId);
        if (index < 0 || now < startedAt)
        {
            index = Math.Max(index, 0); Select(pages[index].Id, now);
        }
        if (!project.AutoRotate || pages.Length == 1)
        {
            startedAt = now;
            return pages[index];
        }
        double cycle = pages.Sum(page => page.DurationSeconds);
        double elapsed = (now - startedAt).TotalSeconds;
        if (elapsed >= cycle) startedAt += TimeSpan.FromSeconds(Math.Floor(elapsed / cycle) * cycle);
        while (now - startedAt >= TimeSpan.FromSeconds(pages[index].DurationSeconds))
        {
            startedAt += TimeSpan.FromSeconds(pages[index].DurationSeconds);
            index = (index + 1) % pages.Length;
        }
        CurrentPageId = pages[index].Id;
        return pages[index];
    }

    public int RemainingSeconds(DisplayPage page, TimeSpan now) =>
        Math.Max(0, (int)Math.Ceiling(interruption == page.Id ? (interruptionUntil - now).TotalSeconds : page.DurationSeconds - (now - startedAt).TotalSeconds));
}
