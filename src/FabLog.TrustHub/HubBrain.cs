using System.Diagnostics;
using FabLog.Core;
using Microsoft.Extensions.AI;

namespace FabLog.TrustHub;

public sealed record TrendResult(
    TrendFinding? Finding,
    IReadOnlyList<HubNote> Sources,
    int CorpusSize,
    int ElapsedMs,
    string Model,
    string? Error = null)
{
    public bool Failed => Error is not null;
}

/// <summary>
/// The two things the hub does that the device cannot: reach T3, and reason over
/// <i>everyone's</i> notes.
/// <para>
/// Separated from <c>Program.cs</c> so it can be tested without standing up a web
/// host — the trend is an assertion about a finding, not about HTTP.
/// </para>
/// </summary>
public sealed class HubBrain(HubStore store, IChatClient cloud, string modelName)
{
    public HubStore Store => store;

    // ⚠️ There is deliberately no polish endpoint here. Forwarding the device's
    // masked payload to the vendor on its behalf would make the hub the device's
    // way out — the one property the architecture exists to prevent. The pad
    // reaches the vendor over its own wire.
    //
    // What lives here is the one workload that genuinely cannot run on a device:
    // reasoning over EVERY technician's notes. Not because the model is bigger.
    // Because the data is here.

    /// <summary>
    /// The question no single device could answer.
    /// <para>
    /// What goes to the cloud is <see cref="HubStore.AsDigest"/> — structured
    /// columns, <b>no note body</b>. The hub holds the real text of every note in
    /// the fab, and that is precisely why none of it crosses wall 2.
    /// </para>
    /// </summary>
    public async Task<TrendResult> AnalyseAsync(string question, CancellationToken ct = default)
    {
        var corpus = store.AsDigest();
        var sw = Stopwatch.StartNew();

        TrendFinding? finding;
        try
        {
            var reply = await cloud.GetResponseAsync(
                [new ChatMessage(ChatRole.System, Prompts.Trend),
                 new ChatMessage(ChatRole.User, $"Question: {question}\n\nDigest:\n{corpus}")],
                Prompts.TightOptions, ct);
            finding = Prompts.ParseTrend(reply.Text);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new TrendResult(null, [], store.Count, (int)sw.ElapsedMilliseconds, modelName,
                ex is InvalidOperationException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}");
        }
        sw.Stop();

        // ── A finding has to be backed by rows the hub is actually holding ──
        // Three ways the model can leave us without one: malformed JSON, a
        // well-formed "no trend" reply, or a confidently named tool with nothing
        // behind it. All three are the same situation — the model didn't find it
        // — and in all three the hub's own grouping is still a fact. Falling
        // through to Detect() means a bad reply costs prose, never the finding.
        TrendFinding result;
        IReadOnlyList<HubNote> sources;

        if (finding is { ToolId.Length: > 0 } named && Related(named.ToolId, named.Chamber) is { Count: > 0 } rows)
            (result, sources) = (named, rows);
        else if (Detect() is { } detected)
            (result, sources) = (detected, Related(detected.ToolId, detected.Chamber));
        else
            return new TrendResult(null, [], store.Count, (int)sw.ElapsedMilliseconds, modelName);

        // ── Counts come from the store, never from the model ────────────────
        // The supervisor acts on "3 technicians, 2 shifts". That is a fact about
        // rows the hub is holding, and it is cheap to compute exactly — so the
        // model writes the sentence and the hub owns the arithmetic. A confident
        // wrong number is the one failure mode this cannot survive.
        return new TrendResult(
            result with
            {
                Mentions = sources.Count,
                Technicians = sources.Select(n => n.Author).Distinct().Count(),
                Shifts = sources.Select(n => n.Shift).Distinct().Count(),
            },
            sources, store.Count, (int)sw.ElapsedMilliseconds, modelName);
    }

    /// <summary>Notes for one tool+chamber that are not routine — the rows behind the finding.</summary>
    public IReadOnlyList<HubNote> Related(string toolId, string chamber) =>
        [.. store.All.Where(n =>
            n.ToolId.Equals(toolId, StringComparison.OrdinalIgnoreCase)
            && (chamber.Length == 0 || n.Chamber.Equals(chamber, StringComparison.OrdinalIgnoreCase))
            && n.Severity is not Severity.Routine)];

    /// <summary>
    /// The deterministic detector, used when the cloud returns nothing parseable.
    /// Groups by tool+chamber+metric and reports the group seen by the most
    /// technicians — the rule the finding rests on:
    /// <b>two technicians is a coincidence, three is a trend.</b>
    /// </summary>
    public TrendFinding? Detect()
    {
        var group = store.All
            .Where(n => n.Severity is not Severity.Routine)
            .GroupBy(n => (n.ToolId, n.Chamber, n.Metric))
            .Select(g => new
            {
                g.Key,
                Notes = g.ToArray(),
                Technicians = g.Select(n => n.Author).Distinct().Count(),
                Shifts = g.Select(n => n.Shift).Distinct().Count(),
            })
            .Where(g => g.Technicians > 1)
            .OrderByDescending(g => g.Technicians).ThenByDescending(g => g.Notes.Length)
            .FirstOrDefault();

        if (group is null) return null;

        var (tool, chamber, metric) = group.Key;
        return new TrendFinding(
            tool, chamber, metric,
            group.Notes.Length, group.Technicians, group.Shifts,
            group.Technicians >= 3 ? Severity.Escalate : Severity.Watch,
            $"{group.Notes.Length} {metric} complaints on {tool} chamber {chamber} from "
            + $"{group.Technicians} technicians across {group.Shifts} shifts — no single note flags a problem.");
    }
}
