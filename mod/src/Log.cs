// PZA's Wildlife for Meitou. Author: PZA. Licence: GPLv3 or later.
// The mod's log (ModContext.Log) and rule counters: every rule counts when it fires, and a line every 5 s lists the counts that changed
// (adversarial method: a rule that never logs is a failed test). The host thread (feed) and the bird thread (the rules) both count: locked.
using System.Diagnostics;
using System.Text;

namespace PZA.Wildlife.Meitou;

public static class Counters
{
    static readonly object s_lock = new();
    static readonly Dictionary<string, long> s_counts = new(StringComparer.Ordinal);
    static readonly Dictionary<string, long> s_reported = new(StringComparer.Ordinal);

    public static void Add(string rule)
    {
        lock (s_lock) s_counts[rule] = s_counts.GetValueOrDefault(rule) + 1;
    }

    public static long Get(string rule)
    {
        lock (s_lock) return s_counts.GetValueOrDefault(rule);
    }

    /// <summary>"rule=total(+new)" for each rule that fired since the last call; null when none did.</summary>
    public static string? Changes()
    {
        lock (s_lock)
        {
            var sb = new StringBuilder();
            foreach (var (rule, n) in s_counts.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                long before = s_reported.GetValueOrDefault(rule);
                if (n == before) continue;
                sb.Append(sb.Length == 0 ? "" : " ").Append(rule).Append('=').Append(n).Append("(+").Append(n - before).Append(')');
                s_reported[rule] = n;
            }
            return sb.Length == 0 ? null : sb.ToString();
        }
    }
}

public static class Log
{
    public static Action<string> Sink = _ => { };
    static long s_lastReport = Stopwatch.GetTimestamp();
    static long s_lastException;

    public static void Line(string text) => Sink("PZA's Wildlife: " + text);

    /// <summary>Once a bird step: the 5 s counter line, when anything fired.</summary>
    public static void Tick(WildCore core)
    {
        long now = Stopwatch.GetTimestamp();
        if (Stopwatch.GetElapsedTime(s_lastReport, now).TotalSeconds < 5) return;
        s_lastReport = now;
        if (Counters.Changes() is not { } changes) return;
        var phases = string.Join(" ", core.Held.GroupBy(p => p.Value.Phase).OrderBy(g => g.Key).Select(g => $"{g.Key.ToString().ToLowerInvariant()} {g.Count()}"));
        Line($"[corpses {core.CorpseCount}, birds held {core.HeldCount}{(phases.Length > 0 ? ": " + phases : "")}] {changes}");
    }

    /// <summary>A throw from a patch: counted always, logged at most once a second.</summary>
    public static void Exception(string where, Exception e)
    {
        Counters.Add("error." + where);
        long now = Stopwatch.GetTimestamp();
        if (Stopwatch.GetElapsedTime(s_lastException, now).TotalSeconds < 1) return;
        s_lastException = now;
        Line("error in " + where + ": " + e.GetType().Name + ": " + e.Message);
    }
}
