namespace Sharplet.Provider.Mock;

/// <summary>
/// The kubelet restart backoff schedule behind the mock <c>crashloop</c> behavior: after each
/// failed container the kubelet waits 10s, 20s, 40s, ... doubling per attempt, capped at 5m.
/// Both helpers are pure functions of elapsed time, so the mock — which re-reads the pod from
/// the API server on every status tick — derives a climbing restart count without provider state.
/// </summary>
public static class CrashLoopBackoff
{
    /// <summary>The delay the kubelet applies before the first restart of a failed container.</summary>
    public static readonly TimeSpan FirstRestartDelay = TimeSpan.FromSeconds(10);

    /// <summary>The maximum per-attempt restart delay the kubelet applies.</summary>
    public static readonly TimeSpan MaxRestartDelay = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The number of restarts the kubelet has performed <paramref name="elapsed"/> after a
    /// container first started: restart <c>n</c> happens once the per-attempt delays (10s, 20s,
    /// 40s, ..., capped at 5m) add up to <paramref name="elapsed"/>.
    /// </summary>
    public static int RestartCountAfter(TimeSpan elapsed)
    {
        if (elapsed <= TimeSpan.Zero)
        {
            return 0;
        }

        int restarts = 0;
        TimeSpan consumed = TimeSpan.Zero;
        TimeSpan delay = FirstRestartDelay;
        while (true)
        {
            consumed += delay;
            if (consumed > elapsed)
            {
                break;
            }

            restarts++;
            delay = NextDelay(delay);
        }

        return restarts;
    }

    /// <summary>
    /// The delay the kubelet applies before the restart that follows <paramref name="restarts"/>
    /// restarts: 10s after none, then doubling per restart, capped at 5m.
    /// </summary>
    public static TimeSpan NextRestartDelay(int restarts)
    {
        TimeSpan delay = FirstRestartDelay;
        for (int i = 0; i < restarts; i++)
        {
            delay = NextDelay(delay);
        }

        return delay;
    }

    /// <summary>
    /// Formats a duration the way the kubelet does in the CrashLoopBackOff message:
    /// <c>10s</c>, <c>1m20s</c>, <c>5m0s</c>.
    /// </summary>
    public static string FormatDuration(TimeSpan duration)
    {
        int seconds = (int)Math.Round(duration.TotalSeconds);
        if (seconds < 60)
        {
            return $"{seconds}s";
        }

        return $"{seconds / 60}m{seconds % 60}s";
    }

    private static TimeSpan NextDelay(TimeSpan delay)
        => delay * 2 > MaxRestartDelay ? MaxRestartDelay : delay * 2;
}
