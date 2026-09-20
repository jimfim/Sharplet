using Xunit;

namespace Sharplet.Provider.Mock.Tests;

public class CrashLoopBackoffTests
{
    [Fact]
    public void RestartCountAfter_UsesTheKubeletBackoffSchedule()
    {
        // Restarts happen at t=10s, t=30s (+20s), t=70s (+40s), t=150s (+80s), t=310s (+160s),
        // t=610s (+5m, the cap), then every 5m.
        Assert.Equal(0, CrashLoopBackoff.RestartCountAfter(TimeSpan.FromSeconds(0)));
        Assert.Equal(0, CrashLoopBackoff.RestartCountAfter(TimeSpan.FromSeconds(9)));
        Assert.Equal(1, CrashLoopBackoff.RestartCountAfter(TimeSpan.FromSeconds(10)));
        Assert.Equal(1, CrashLoopBackoff.RestartCountAfter(TimeSpan.FromSeconds(29)));
        Assert.Equal(2, CrashLoopBackoff.RestartCountAfter(TimeSpan.FromSeconds(30)));
        Assert.Equal(2, CrashLoopBackoff.RestartCountAfter(TimeSpan.FromSeconds(69)));
        Assert.Equal(3, CrashLoopBackoff.RestartCountAfter(TimeSpan.FromSeconds(70)));
        Assert.Equal(4, CrashLoopBackoff.RestartCountAfter(TimeSpan.FromSeconds(150)));
        Assert.Equal(5, CrashLoopBackoff.RestartCountAfter(TimeSpan.FromSeconds(310)));
        Assert.Equal(6, CrashLoopBackoff.RestartCountAfter(TimeSpan.FromSeconds(610)));
        Assert.Equal(7, CrashLoopBackoff.RestartCountAfter(TimeSpan.FromSeconds(1000)));
    }

    [Fact]
    public void RestartCountAfter_ContinuesEveryFiveMinutesPastTheCap()
    {
        Assert.Equal(8, CrashLoopBackoff.RestartCountAfter(TimeSpan.FromSeconds(1210)));
        Assert.Equal(10, CrashLoopBackoff.RestartCountAfter(TimeSpan.FromSeconds(1810)));
    }

    [Fact]
    public void NextRestartDelay_DoublesPerRestartCappedAtFiveMinutes()
    {
        Assert.Equal(TimeSpan.FromSeconds(10), CrashLoopBackoff.NextRestartDelay(0));
        Assert.Equal(TimeSpan.FromSeconds(20), CrashLoopBackoff.NextRestartDelay(1));
        Assert.Equal(TimeSpan.FromSeconds(40), CrashLoopBackoff.NextRestartDelay(2));
        Assert.Equal(TimeSpan.FromSeconds(80), CrashLoopBackoff.NextRestartDelay(3));
        Assert.Equal(TimeSpan.FromSeconds(160), CrashLoopBackoff.NextRestartDelay(4));
        Assert.Equal(TimeSpan.FromMinutes(5), CrashLoopBackoff.NextRestartDelay(5));
        Assert.Equal(TimeSpan.FromMinutes(5), CrashLoopBackoff.NextRestartDelay(50));
    }

    [Fact]
    public void FormatDuration_UsesTheKubeletDurationFormat()
    {
        Assert.Equal("10s", CrashLoopBackoff.FormatDuration(TimeSpan.FromSeconds(10)));
        Assert.Equal("59s", CrashLoopBackoff.FormatDuration(TimeSpan.FromSeconds(59)));
        Assert.Equal("1m0s", CrashLoopBackoff.FormatDuration(TimeSpan.FromSeconds(60)));
        Assert.Equal("1m20s", CrashLoopBackoff.FormatDuration(TimeSpan.FromSeconds(80)));
        Assert.Equal("2m40s", CrashLoopBackoff.FormatDuration(TimeSpan.FromSeconds(160)));
        Assert.Equal("5m0s", CrashLoopBackoff.FormatDuration(TimeSpan.FromMinutes(5)));
    }
}
