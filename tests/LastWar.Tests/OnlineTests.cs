using LastWar.Storage;

namespace LastWar.Tests;

public class OnlineTests
{
    // Hours are whole UTC hours since 1970. A player on at 20:00 on two days and at 09:00 today is
    // active 24 h / 7 d / 30 d with 2 / 3 / 3 hours; the typical-day count at 20:00 is 2 days' worth.
    [Fact]
    public void ActiveHoursAndTypicalDayCountDistinctHours()
    {
        var now = new DateTimeOffset(2026, 9, 30, 12, 30, 0, TimeSpan.Zero);
        int H(int daysAgo, int hour) => (int)((now.UtcDateTime.Date.AddDays(-daysAgo).AddHours(hour) - DateTime.UnixEpoch).TotalHours);
        var w = Insights.OnlineWindow.Of(now, 7, now.AddDays(-60));
        Assert.Equal(7, w.Days);

        var acc = new Insights.OnlineAcc(w);
        acc.Add([H(0, 9), H(1, 20), H(2, 20), H(20, 5)]);   // the 20-day-old hour is outside the 7-day window
        acc.Add(null);                                        // known, never seen
        Assert.Equal((2, 1, 1, 1), (acc.Players, acc.Active24, acc.Active7, acc.Active30));
        Assert.Equal((2L, 3L, 4L), (acc.Hours24, acc.Hours7, acc.Hours30));   // yesterday 20:00 is inside the last 24 h
        Assert.Equal(2, acc.HourDays[20]);
        Assert.Equal(0, acc.HourDays[5]);
        Assert.Equal(1, acc.PerDay[^1]);   // today
        Assert.Equal(3, acc.PerDay.Sum());

        var one = Insights.OnlineOf([H(0, 9), H(1, 20), H(2, 20)], (int)((now.UtcDateTime - DateTime.UnixEpoch).TotalHours));
        Assert.Equal(2, one["hours24"]);
        Assert.Equal(3, one["days7"]);
    }

    // The window never reaches back before our first profile reading: days before it would read as "nobody on".
    [Fact]
    public void WindowStartsAtFirstReading()
    {
        var now = new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(4, Insights.OnlineWindow.Of(now, 30, now.AddDays(-3)).Days);
    }
}
