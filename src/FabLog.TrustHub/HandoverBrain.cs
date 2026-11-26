using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using FabLog.Core;
using Microsoft.Extensions.AI;

namespace FabLog.TrustHub;

/// <summary>Binds <c>Hub:Features</c>. Both default to off: with them off the app behaves exactly as before.</summary>
public sealed class HubFeatures
{
    public bool ShiftHandover { get; set; }

    /// <summary>Stage switch: never call the cloud, show the hub's own ranking. (Not an airlock — the hub has none.)</summary>
    public bool ForceHandoverFallback { get; set; }
}

public sealed record Check(
    int Rank, string Tool, string Chamber, string Action, string Why,
    IReadOnlyList<string> Evidence, string Confidence, string WouldChangeIf);

public sealed record HubRankRow(int Rank, string Tool, string Chamber, string Metric, int Score);

public sealed record HandoverResult(
    IReadOnlyList<Check> Checks,
    string Source,                       // "cloud" | "hub" | "disabled"
    string SourceLabel,                  // what the card prints
    string? FallbackReason,
    IReadOnlyList<HubRankRow> HubRanking,
    IReadOnlyList<string> Rejections,
    bool ReplyParsed,
    string Digest,
    IReadOnlyList<Evidence> Evidence,
    int ElapsedMs,
    string Model,
    DateTimeOffset At)
{
    public bool FromCloud => Source == "cloud";
}

/// <summary>
/// The hub's second cloud workload: "what should the next shift check first, and why?"
/// <para>
/// Facts first (<see cref="HandoverFacts"/>), model second, hub last: every check the
/// model returns is validated against what was actually sent, and if nothing survives
/// the hub's own ranking is shown instead, labelled as such. Deliberately separate from
/// <see cref="HubBrain"/> so the fleet question cannot change.
/// </para>
/// <para>
/// ⚠️ There is no airlock here, and no "posture": the hub is never in the cleanroom
/// (see Cloud.cs). The cloud being unavailable is discovered by failing, as everywhere else.
/// </para>
/// </summary>
public sealed class HandoverBrain(
    HubStore store, HandoverContext context, IChatClient cloud, string modelName, HubFeatures features,
    TimeSpan? deadline = null, Func<DateTimeOffset>? now = null)
{
    public const int MaxChecks = 3;
    const string HubLabel = "hub ranking (no cloud)";

    readonly TimeSpan _deadline = deadline ?? TimeSpan.FromSeconds(15);
    readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.Now);
    volatile HandoverResult? _last;

    public bool Enabled => features.ShiftHandover;

    /// <summary>The last brief, kept until <c>/reset</c> so a page refresh does not re-roll the model.</summary>
    public HandoverResult? Last => _last;

    public void Reset() => _last = null;

    public async Task<HandoverResult> BriefAsync(CancellationToken ct = default)
    {
        var at = _now();
        var snap = HandoverFacts.Build(store, context, at);
        var digest = snap.ToDigest();
        var hubRank = snap.Ranked.Select((c, i) => new HubRankRow(i + 1, c.Tool, c.Chamber, c.Metric, c.Score)).ToList();

        HandoverResult Done(IReadOnlyList<Check> checks, string source, string label, string? reason,
            List<string> rejections, bool parsed, Stopwatch clock) =>
            _last = new HandoverResult(checks, source, label, reason, hubRank, rejections, parsed,
                digest, snap.Evidence, (int)clock.ElapsedMilliseconds, modelName, at);

        var sw = Stopwatch.StartNew();
        if (!features.ShiftHandover)
            return new HandoverResult([], "disabled", "disabled", null, [], [], false, "", [], 0, modelName, at);

        if (features.ForceHandoverFallback)
            return Done(Fallback(snap), "hub", HubLabel, "forced by Hub:Features:ForceHandoverFallback", [], false, sw);

        string? text;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_deadline);
            var reply = await cloud.GetResponseAsync(
                [new ChatMessage(ChatRole.System, Prompts.Handover),
                 new ChatMessage(ChatRole.User, $"Digest:\n{digest}")],
                Prompts.HandoverOptions, cts.Token);
            text = reply.Text;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;   // the caller gave up; that is not a slow cloud
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            return Done(Fallback(snap), "hub", HubLabel,
                $"cloud did not answer within {_deadline.TotalSeconds:0} s", [], false, sw);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return Done(Fallback(snap), "hub", HubLabel, VendorErrors.Describe(ex), [], false, sw);
        }
        sw.Stop();

        var rejections = new List<string>();
        var parsed = TryParse(text, out var candidates);
        var checks = parsed ? Validate(candidates, snap, rejections) : [];

        if (checks.Count > 0)
            return Done(checks, "cloud", $"cloud · {modelName}", null, rejections, parsed, sw);

        var why = parsed ? "every check from the cloud was rejected by the hub" : "the cloud's reply was not valid JSON";
        return Done(Fallback(snap), "hub", HubLabel, why, rejections, parsed, sw);
    }

    // ── parse ───────────────────────────────────────────────────────────────

    static bool TryParse(string? raw, out List<Check> checks)
    {
        checks = [];
        var json = Prompts.ExtractJsonObject(raw);
        if (json is null) return false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("checks", out var arr) || arr.ValueKind is not JsonValueKind.Array)
                return false;
            var rank = 0;
            foreach (var e in arr.EnumerateArray())
            {
                if (e.ValueKind is not JsonValueKind.Object) continue;
                checks.Add(new Check(++rank,
                    Prompts.NormaliseToolId(Str(e, "tool")), Prompts.NormaliseChamber(Str(e, "chamber")),
                    Str(e, "action"), Str(e, "why"),
                    e.TryGetProperty("evidence", out var ev) && ev.ValueKind is JsonValueKind.Array
                        ? [.. ev.EnumerateArray().Where(x => x.ValueKind is JsonValueKind.String).Select(x => x.GetString()!.Trim())]
                        : [],
                    Str(e, "confidence").ToLowerInvariant(), Str(e, "would_change_if")));
            }
            return true;
        }
        catch (JsonException) { return false; }
    }

    static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind is JsonValueKind.String ? p.GetString()?.Trim() ?? "" : "";

    // ── validate: the model proposes, the hub disposes ──────────────────────

    static readonly Regex Digits = new(@"\d+(?:\.\d+)?", RegexOptions.Compiled);
    static readonly Regex IdsAndTools = new(@"\b(?:n|pm|wip|x)\d+\b|\b[A-Za-z]{2,6}-\d{1,3}\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex Sentences = new(@"(?<=[.!?])\s+", RegexOptions.Compiled);

    List<Check> Validate(List<Check> proposed, HandoverSnapshot snap, List<string> log)
    {
        var allowed = snap.AllowedNumbers();
        var byId = snap.Evidence.ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase);
        List<Check> kept = [];

        if (proposed.Count > MaxChecks)
            log.Add($"{proposed.Count - MaxChecks} check(s) beyond the first {MaxChecks} dropped");

        foreach (var c in proposed.Take(MaxChecks))
        {
            var who = $"rank {c.Rank} · {c.Tool} {c.Chamber}";

            if (!snap.Chambers.Any(f => f.Tool == c.Tool && f.Chamber == c.Chamber))
            { log.Add($"{who} — rejected: tool/chamber is not in the digest"); continue; }

            if (kept.Any(k => k.Tool == c.Tool && k.Chamber == c.Chamber))
            { log.Add($"{who} — rejected: duplicate of a higher-ranked check"); continue; }

            if (c.Evidence.Count == 0)
            { log.Add($"{who} — rejected: cites no evidence"); continue; }

            var unknown = c.Evidence.Where(id => !byId.ContainsKey(id)).ToList();
            if (unknown.Count > 0)
            { log.Add($"{who} — rejected: evidence id(s) not sent: {string.Join(", ", unknown)}"); continue; }

            var foreign = c.Evidence.Where(id => byId[id].Tool != c.Tool || byId[id].Chamber != c.Chamber).ToList();
            if (foreign.Count > 0)
            { log.Add($"{who} — rejected: evidence id(s) belong to another chamber: {string.Join(", ", foreign)}"); continue; }

            var action = Strip(c.Action, allowed, log, who, "action");
            var why = Strip(c.Why, allowed, log, who, "why");
            var change = Strip(c.WouldChangeIf, allowed, log, who, "would_change_if");
            if (action.Length == 0 || why.Length == 0)
            { log.Add($"{who} — rejected: nothing left of action/why after stripping"); continue; }

            var confidence = c.Confidence is "high" or "medium" or "low" ? c.Confidence : "low";
            if (confidence != c.Confidence) log.Add($"{who} — confidence '{c.Confidence}' is not high/medium/low; shown as low");

            kept.Add(new Check(kept.Count + 1, c.Tool, c.Chamber, action, why,
                [.. c.Evidence.Select(id => byId[id].Id)], confidence, change));
        }
        return kept;
    }

    /// <summary>
    /// Digits only: a sentence containing a digit the hub never supplied is removed and the
    /// removal is logged. Tool ids and evidence ids are not numbers and are ignored.
    /// </summary>
    static string Strip(string text, HashSet<double> allowed, List<string> log, string who, string field)
    {
        List<string> keep = [];
        foreach (var s in Sentences.Split(text).Where(s => s.Length > 0))
        {
            var invented = Digits.Matches(IdsAndTools.Replace(s, ""))
                .Select(m => m.Value)
                .FirstOrDefault(v => !allowed.Contains(double.Parse(v, CultureInfo.InvariantCulture)));
            if (invented is null) keep.Add(s);
            else log.Add($"{who} — {field}: sentence removed, '{invented}' was never supplied");
        }
        return string.Join(" ", keep);
    }

    // ── the hub's own ranking ───────────────────────────────────────────────

    /// <summary>Top three by score, wording templated from the facts. No model, so it cannot be wrong about a number.</summary>
    static List<Check> Fallback(HandoverSnapshot snap) =>
        [.. snap.Ranked.Take(MaxChecks).Select((f, i) =>
        {
            var pm = snap.Evidence.First(e => e.Id.StartsWith("pm") && f.EvidenceIds.Contains(e.Id)).Line;
            var action = f.PmDueInDays < 0
                ? $"Inspect {f.Tool} chamber {f.Chamber} and do the overdue PM before it runs more lots."
                : f.DriftPoints >= 20
                    ? $"Re-check {f.Metric} on {f.Tool} chamber {f.Chamber} against baseline before loading the next lot."
                    : $"Review {f.Tool} chamber {f.Chamber} {f.Metric} at the start of the shift.";
            var why = $"{f.Metric} drift {HandoverFacts.Signed(f.DriftSigma)}σ and {f.Trend}; {f.Lots} lots queued next shift; {pm}.";
            var confidence = f.Conflict ? "low" : f.Score >= 70 ? "high" : f.Score >= 45 ? "medium" : "low";
            return new Check(i + 1, f.Tool, f.Chamber, action, why, f.EvidenceIds, confidence,
                "the first measurement of the shift reads back inside its normal range");
        })];
}
