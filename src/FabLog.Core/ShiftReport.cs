using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.AI;

namespace FabLog.Core;

/// <summary>One row of the report's facts table — computed on the device, and it never leaves.</summary>
public sealed record ReportFact(
    string ToolId, string Chamber, string Metric, string Value, Severity Severity, int NoteCount);

/// <summary>
/// The shift report: the <b>hybrid</b> flow, and a genuinely different job from
/// capturing a note.
/// <para>
/// Note capture happens inside the cleanroom, where there is no connection at all —
/// so it can never exercise the case "connected, and choosing not to send". Writing
/// up the shift happens <b>outside</b> the cleanroom, at the end, with full
/// connectivity and nothing forcing anyone's hand. That is the only place the
/// jurisdiction boundary is honoured <i>by choice</i> rather than by circumstance.
/// </para>
/// <para>
/// The split within one artefact:
/// </para>
/// <list type="bullet">
///   <item><b>Facts stay.</b> The tool/chamber/metric/severity table is computed on
///     the device from its own notes and is never sent anywhere.</item>
///   <item><b>Only a masked brief travels.</b> Proprietary spans are replaced with
///     placeholders before anything crosses.</item>
///   <item><b>The prose comes back and is re-inflated locally.</b> The
///     placeholder→value mapping never leaves.</item>
/// </list>
/// </summary>
public sealed record ShiftReport(
    string Author,
    string Shift,
    string DeviceId,
    string Cleanroom,
    DateTimeOffset CompiledAt,
    IReadOnlyList<ReportFact> Facts,
    IReadOnlyList<IpSpan> Spans,
    string Brief,
    string? Narrative,
    string Final,
    int NoteCount,
    int ElapsedMs,
    bool CloudUsed,
    string? Error = null,
    bool Submitted = false)
{
    public bool Failed => Error is not null;

    /// <summary>What actually crossed the jurisdiction boundary — empty when nothing did.</summary>
    public string PayloadSent => CloudUsed ? Brief : "";
}

/// <summary>Where a finished report goes. Same server as the notes, different shape.</summary>
public interface IReportSink
{
    Task<bool> SyncReportAsync(ShiftReport report, CancellationToken ct = default);
}

/// <summary>
/// Compiles a shift's notes into one submittable report.
/// <para>
/// Deliberately <b>not</b> in <see cref="Router"/>. The router has to stay readable
/// end to end; this is a longer routine for a different task. They share the same
/// machinery — span detection, masking, the same <see cref="IChatClient"/>
/// abstraction — which is the point: the hybrid split was not built for this flow,
/// it simply applies to it.
/// </para>
/// </summary>
public sealed class ShiftReporter(
    Tiers tiers,
    Ledger ledger,
    ModelNames models,
    IReportSink? sink = null)
{
    /// <param name="notes">This shift's notes, as captured on this device.</param>
    /// <param name="wantNarrative">
    /// Whether to ask the cloud for prose at all. A product decision, not a policy
    /// one — the facts table is complete either way.
    /// </param>
    public async Task<ShiftReport> CompileAsync(
        IReadOnlyList<FabNote> notes,
        DemoMode mode,
        string author,
        string shift,
        string deviceId,
        string cleanroom,
        bool wantNarrative = true,
        CancellationToken ct = default)
    {
        var policy = Policies.For(mode);
        var sw = Stopwatch.StartNew();

        // ── 1 · The facts, on the device, deterministically ─────────────────
        // No model in this step at all. Grouping and counting are arithmetic, and
        // arithmetic cannot have a bad day. It is also the half of the report that
        // never crosses anything, so it is the half that gets to be exact.
        var facts = BuildFacts(notes);

        // ── 2 · The narrative brief — the only thing that could ever travel ──
        var brief = BuildBrief(notes);

        // ── 3 · Detect and mask, on the device, BEFORE a destination is chosen ─
        // Same rule as note capture: the thing that builds the placeholder→value
        // mapping must not be downstream of anything that might leak it.
        var device = tiers[Tier.T1Device];
        var spans = policy.ProtectIp
            ? await Redaction.DetectAsync(brief, device, ct)
            : [];

        var masked = spans.Count > 0 ? Redaction.Mask(brief, spans) : brief;

        // ── 4 · The cloud writes the prose — from the masked brief, or not at all ─
        var cloudAllowed = policy.Allowed.Contains(Tier.T3VendorCloud);

        if (!cloudAllowed || !wantNarrative)
        {
            sw.Stop();
            var reason = !cloudAllowed ? "policy: edge only" : "no cloud-grade prose requested";
            return await FinishAsync(
                new ShiftReport(author, shift, deviceId, cleanroom, DateTimeOffset.UtcNow,
                    facts, spans, masked, Narrative: null, Final: brief,
                    notes.Count, (int)sw.ElapsedMilliseconds, CloudUsed: false),
                Tier.T1Device, "device", models.Device, reason, payload: "", ct);
        }

        try
        {
            var reply = await tiers[Tier.T3VendorCloud].GetResponseAsync(
                [new ChatMessage(ChatRole.System, Prompts.Report), new ChatMessage(ChatRole.User, masked)],
                Prompts.ProseOptions, ct);

            // ── 5 · Re-inflate locally. The mapping never left. ──────────────
            var restored = Redaction.Unmask(reply.Text ?? "", spans);
            sw.Stop();

            return await FinishAsync(
                new ShiftReport(author, shift, deviceId, cleanroom, DateTimeOffset.UtcNow,
                    facts, spans, masked, reply.Text, restored,
                    notes.Count, (int)sw.ElapsedMilliseconds, CloudUsed: true),
                Tier.T1Device, "device→cloud", $"{models.Device} + {models.Cloud}",
                spans.Count > 0 ? "policy: fab IP masked before it crossed" : "no fab IP in this report",
                payload: masked, ct);
        }
        catch (Exception ex)
        {
            // The report is compiled outside the cleanroom, so this is not the
            // modelled case — but the facts are already complete on the device and
            // the technician keeps them regardless. Prose is what is lost, never
            // the report.
            sw.Stop();
            return await FinishAsync(
                new ShiftReport(author, shift, deviceId, cleanroom, DateTimeOffset.UtcNow,
                    facts, spans, masked, Narrative: null, Final: brief,
                    notes.Count, (int)sw.ElapsedMilliseconds, CloudUsed: false,
                    Error: ex is OperationCanceledException or TimeoutException
                        ? "The narrative service timed out — the report stands without it."
                        : $"{ex.GetType().Name}: {ex.Message}"),
                Tier.T1Device, "device", models.Device, "vendor unreachable", payload: "", ct);
        }
    }

    // Every path lands here: one log row, then submit. Same shape as note capture,
    // for the same reason — the audit trail is free if nothing can skip it.
    async Task<ShiftReport> FinishAsync(
        ShiftReport report, Tier tier, string path, string model, string reason, string payload,
        CancellationToken ct)
    {
        ledger.Write(new LedgerEntry(
            Guid.Empty, tier, path, model, $"shift report · {reason}",
            report.ElapsedMs, payload, DateTimeOffset.UtcNow));

        // Submission is best-effort and deliberately not queued. This flow runs
        // OUTSIDE the cleanroom, with a connection, by definition — and the
        // technician keeps a local copy either way. A report that failed to submit
        // is visible and re-submittable, never gone.
        var submitted = false;
        if (sink is not null)
        {
            try { submitted = await sink.SyncReportAsync(report, ct); }
            catch { submitted = false; }
        }

        return report with { Submitted = submitted };
    }

    /// <summary>
    /// The facts table. Grouped by tool and chamber, worst severity wins, counts are
    /// exact — and none of it is ever sent anywhere.
    /// </summary>
    public static IReadOnlyList<ReportFact> BuildFacts(IReadOnlyList<FabNote> notes) =>
        [.. notes
            .Where(n => !string.IsNullOrWhiteSpace(n.ToolId))
            .GroupBy(n => (Tool: n.ToolId!, Chamber: n.Chamber ?? ""))
            .Select(g => new ReportFact(
                g.Key.Tool,
                g.Key.Chamber,
                g.Select(n => n.Metric).FirstOrDefault(m => !string.IsNullOrWhiteSpace(m)) ?? "",
                g.Select(n => n.Value).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "",
                g.Max(n => n.Severity),
                g.Count()))
            .OrderByDescending(f => f.Severity)
            .ThenBy(f => f.ToolId)];

    /// <summary>
    /// The narrative brief: the shift's notes as one block of prose for the cloud to
    /// write up. Cleaned text where the device produced it, raw where it did not.
    /// </summary>
    public static string BuildBrief(IReadOnlyList<FabNote> notes)
    {
        var sb = new StringBuilder();
        foreach (var n in notes.OrderBy(n => n.At))
        {
            var line = string.IsNullOrWhiteSpace(n.Cleaned) ? n.Raw : n.Cleaned!;
            sb.Append("- ").AppendLine(line.Trim());
        }
        return sb.ToString().TrimEnd();
    }
}
