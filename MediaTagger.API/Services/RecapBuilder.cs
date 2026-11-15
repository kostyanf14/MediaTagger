namespace MediaTagger.API.Services;

/// <summary>Groups a library the way a photo app’s memories do. Dates are calendar days.</summary>
public static class RecapBuilder
{
    /// <summary>Enough to fill a row or two; the rest is only a count.</summary>
    public const int Shown = 40;

    public sealed record Shot(string FilePath, string FileName, string Type, DateTime Taken);
    public sealed record Group(string Id, string Title, int Count, IReadOnlyList<Shot> Items);

    public static IReadOnlyList<Group> Build(IEnumerable<Shot> shots, DateTime today)
    {
        var day = today.Date;
        var dated = shots.Where(s => s.Taken != default).ToList();

        Group Make(string id, string title, Func<DateTime, bool> match)
        {
            var all = dated.Where(s => match(s.Taken.Date)).OrderByDescending(s => s.Taken).ToList();
            return new Group(id, title, all.Count, all.Take(Shown).ToList());
        }

        var monthStart = new DateTime(day.Year, day.Month, 1);
        var lastMonthStart = monthStart.AddMonths(-1);
        var anniversary = day.AddYears(-1);

        var groups = new[]
        {
            Make("on-this-day", "Цей день у різні роки",
                d => d.Month == day.Month && d.Day == day.Day && d.Year != day.Year),
            Make("last-week", "Останній тиждень",
                d => d >= day.AddDays(-6) && d <= day),
            Make("last-month", "Минулий місяць",
                d => d >= lastMonthStart && d < monthStart),
            Make("month-last-year", "Цей місяць рік тому",
                d => d.Year == day.Year - 1 && d.Month == day.Month),
            Make("a-year-ago", "Рік тому",
                d => d >= anniversary.AddDays(-3) && d <= anniversary.AddDays(3)
                    && !(d.Month == day.Month && d.Day == day.Day))
        };
        return groups.Where(g => g.Count > 0).ToList();
    }
}
