using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FabLog.Core;

namespace FabLog.TrustHub;

// ─────────────────────────────────────────────────────────────────────────────
//  Shift handover — the hub's half. Everything in this file is computed BEFORE a
//  model is involved, from rows the hub already holds plus handover-context.json.
//  What may cross wall 2 is HandoverSnapshot.ToDigest(): derived signals only
//  (drift in sigma, trend, PM state, lot COUNTS) — never a note, a name, a
//  baseline or a raw reading. "Send derived signals, not raw values."
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>One chamber's context. Stays in the hub; only what is derived from it leaves.</summary>
public sealed record ChamberContext(
    string Tool, string Chamber, string Metric,
    double BaselineMean, double Sigma, string WorseWhen,
    IReadOnlyList<double> ShiftMeans,
    int LastPmDaysAgo, int PmIntervalDays, int? LastCleanDaysAgo, int LotsNextShift)
{
    public bool WorseWhenLow => WorseWhen.Equals("low", StringComparison.OrdinalIgnoreCase);
}

public sealed record HandoverContext(IReadOnlyList<ChamberContext> Chambers)
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>A missing file is an empty context, not a crash: the feature then has nothing to rank.</summary>
    public static HandoverContext Load(string path) =>
        File.Exists(path)
            ? JsonSerializer.Deserialize<HandoverContext>(File.ReadAllText(path), Json) ?? new([])
            : new([]);
}

/// <summary>
/// One fact line with a stable id. <see cref="Notes"/> are the hub's own rows behind a
/// notes/conflict line — shown to the supervisor (inside the fab), never sent.
/// </summary>
public sealed record Evidence(string Id, string Tool, string Chamber, string Line, IReadOnlyList<Guid> Notes);

public sealed record ChamberFacts(
    string Tool, string Chamber, string Metric,
    double DriftSigma, bool WorseWhenLow, string Trend,
    int PmDueInDays, int Lots,
    int NonRoutine, int Technicians, Severity? Worst, bool Conflict,
    IReadOnlyList<string> EvidenceIds)
{
    // ── The hub's own ranking. Constants live here so tests can pin them. ──
    double WorseSideDrift => WorseWhenLow ? -DriftSigma : DriftSigma;
    public int DriftPoints => (int)Math.Round(Math.Clamp(WorseSideDrift, 0, 4) * 10, MidpointRounding.AwayFromZero);
    public int TrendPoints => Trend == "worsening" ? 10 : 0;
    public int PmPoints => PmDueInDays < 0 ? 25 : PmDueInDays <= 7 ? 15 : 0;
    public int LotPoints => Math.Min(Lots, 10) * 2;
    public int NotePoints => Worst == Severity.Escalate ? 15 : NonRoutine > 0 ? Math.Min(Technicians * 5, 10) : 0;
    public int Score => DriftPoints + TrendPoints + PmPoints + LotPoints + NotePoints;
}

public sealed record HandoverSnapshot(IReadOnlyList<ChamberFacts> Chambers, IReadOnlyList<Evidence> Evidence)
{
    public const string Header =
        "# SHIFT HANDOVER DIGEST — derived signals only; no note text, no raw process values";

    /// <summary>Highest score first; ties broken by tool then chamber so the order is deterministic.</summary>
    public IReadOnlyList<ChamberFacts> Ranked =>
        [.. Chambers.OrderByDescending(c => c.Score)
                    .ThenBy(c => c.Tool, StringComparer.Ordinal).ThenBy(c => c.Chamber, StringComparer.Ordinal)];

    /// <summary>The exact text handed to the vendor. Fixed order, so identical data gives an identical prompt.</summary>
    public string ToDigest() =>
        Header + "\n" + string.Join("\n", Evidence.Select(e =>
            $"{e.Id,-4} | {e.Tool + " " + e.Chamber,-10} | {e.Line}"));

    /// <summary>Every number the vendor was given. A digit in its answer that is not here was invented.</summary>
    public HashSet<double> AllowedNumbers() =>
        [.. Evidence.SelectMany(e => Regex.Matches(e.Line, @"\d+(?:\.\d+)?"))
                    .Select(m => double.Parse(m.Value, CultureInfo.InvariantCulture))];
}

public static class HandoverFacts
{
    /// <summary>"Last 3 shifts" — generous on purpose: the day-shifted seed must stay inside it at any start time.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromHours(36);

    public static HandoverSnapshot Build(HubStore store, HandoverContext ctx, DateTimeOffset asOf)
    {
        var recent = store.All.Where(n => n.At >= asOf - Window).ToList();
        List<ChamberFacts> chambers = [];
        List<Evidence> ns = [], pms = [], wips = [], xs = [];

        foreach (var c in ctx.Chambers.OrderBy(c => c.Tool, StringComparer.Ordinal).ThenBy(c => c.Chamber, StringComparer.Ordinal))
        {
            var drift = Drift(c);
            var trend = Trend(c);
            var dueIn = c.PmIntervalDays - c.LastPmDaysAgo;

            var rows = recent.Where(n =>
                n.ToolId.Equals(c.Tool, StringComparison.OrdinalIgnoreCase)
                && n.Chamber.Equals(c.Chamber, StringComparison.OrdinalIgnoreCase)).ToList();
            var bad = rows.Where(n => n.Severity is not Severity.Routine).ToList();
            var routine = rows.Where(n => n.Severity is Severity.Routine).ToList();
            Severity? worst = bad.Count == 0 ? null : bad.Any(n => n.Severity == Severity.Escalate) ? Severity.Escalate : Severity.Watch;
            var conflict = bad.Count > 0 && routine.Count > 0
                && bad.Any(b => routine.Any(r => r.Author != b.Author));

            // A chamber with nothing to say is not sent at all.
            if (Math.Abs(drift) < 1 && bad.Count == 0 && dueIn > 7) continue;

            List<string> ids = [];
            string Add(List<Evidence> list, string prefix, string line, IReadOnlyList<Guid>? notes = null)
            {
                var id = $"{prefix}{list.Count + 1}";
                list.Add(new Evidence(id, c.Tool, c.Chamber, line, notes ?? []));
                ids.Add(id);
                return id;
            }

            Add(ns, "n", $"{c.Metric} | drift={Signed(drift)}σ | trend={trend} (3 shifts)");
            if (bad.Count > 0)
                Add(ns, "n", $"notes | non-routine={bad.Count} | technicians={bad.Select(b => b.Author).Distinct().Count()} | worst={worst}",
                    [.. bad.Select(b => b.Id)]);
            Add(pms, "pm", $"last PM {c.LastPmDaysAgo}d ago | interval {c.PmIntervalDays}d | "
                + (dueIn < 0 ? $"overdue by {-dueIn}d" : $"due in {dueIn}d")
                + (c.LastCleanDaysAgo is { } cl ? $" | last clean {cl}d ago" : ""));
            Add(wips, "wip", $"next shift lots={c.LotsNextShift}");
            if (conflict)
                Add(xs, "x", $"conflicting signals | {worst} vs Routine | {rows.Select(r => r.Author).Distinct().Count()} technicians",
                    [.. rows.Select(r => r.Id)]);

            chambers.Add(new ChamberFacts(
                c.Tool, c.Chamber, c.Metric, drift, c.WorseWhenLow, trend, dueIn, c.LotsNextShift,
                bad.Count, bad.Select(b => b.Author).Distinct().Count(), worst, conflict, ids));
        }

        return new HandoverSnapshot(chambers, [.. ns, .. pms, .. wips, .. xs]);
    }

    /// <summary>(latest shift mean − baseline) / σ, signed, one decimal. The only form of the reading that leaves the hub.</summary>
    public static double Drift(ChamberContext c) =>
        c.Sigma <= 0 || c.ShiftMeans.Count == 0
            ? 0
            : Math.Round((c.ShiftMeans[^1] - c.BaselineMean) / c.Sigma, 1, MidpointRounding.AwayFromZero);

    /// <summary>worsening / improving: BOTH of the last two steps move the same way. Anything else is flat.</summary>
    public static string Trend(ChamberContext c)
    {
        if (c.ShiftMeans.Count < 3) return "flat";
        var m = c.ShiftMeans;
        double a = m[^2] - m[^3], b = m[^1] - m[^2];
        var worse = c.WorseWhenLow ? -1 : 1;
        if (a * worse > 0 && b * worse > 0) return "worsening";
        if (a * worse < 0 && b * worse < 0) return "improving";
        return "flat";
    }

    public static string Signed(double d) => d.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture);
}
