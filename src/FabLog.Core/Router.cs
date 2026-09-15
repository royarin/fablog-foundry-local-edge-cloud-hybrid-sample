using System.Diagnostics;
using Microsoft.Extensions.AI;

namespace FabLog.Core;

public sealed record ModelNames(string Device = "qwen2.5-1.5b", string Cloud = "gpt-4o");

/// <param name="DeviceId">Which handheld this was captured on — travels with the note.</param>
/// <param name="Cleanroom">Which cleanroom the technician is in. The fab has more than one.</param>
public sealed record RouteRequest(
    string Raw,
    string Author = "K. Nagy",
    bool WantPolish = true,
    Guid? NoteId = null,
    string DeviceId = "",
    string Cleanroom = "");

/// <param name="Degraded">
/// The local answer stands, but the cloud half did not happen — permitted, attempted, unreachable.
/// <b>Not an error</b>: the note is structured, classified and masked, and only the prose is
/// missing. Retrying is one click away.
/// </param>
public sealed record RouteResult(
    FabNote Note,
    Tier Tier,
    string Path,
    string Reason,
    int ElapsedMs,
    string PayloadSent,
    string? Output,
    bool Degraded = false,
    string? Error = null)
{
    /// <summary>
    /// The technician got <b>nothing usable</b>.
    /// <para>
    /// ⚠️ Not simply "an error was recorded". A degraded Hybrid route also carries an
    /// <see cref="Error"/> — the cloud genuinely did fail, and the message is worth showing — but
    /// the structured, classified note is in hand and the route did its job.
    /// </para>
    /// </summary>
    public bool Failed => Error is not null && Output is null;
}

/// <summary>
/// One method: everything the device does with a note passes through here.
/// <para>
/// ⚠️ <b>There is no queue for cloud work, and its absence is a decision.</b> Deferring a
/// <i>delivery</i> is useful — nobody is waiting on a note reaching the Central System, so it can
/// travel whenever it can. Deferring an <i>answer</i> is not: the technician asked the cloud a
/// question and is standing there waiting for the reply. One that arrives twenty minutes later,
/// unprompted, is worse than an error.
/// </para>
/// <para>
/// So the cloud is a plain call that either answers or fails, and "pending" is a property of a note
/// (<see cref="SyncState"/>) rather than a second collection holding a copy of it.
/// </para>
/// </summary>
public sealed class Router(
    Tiers tiers,
    Ledger ledger,
    ModelNames models,
    INoteSink? sink = null)
{
    public Ledger Ledger => ledger;

    public async Task<RouteResult> ProcessAsync(RouteRequest req, DemoMode mode, CancellationToken ct = default)
    {
        var policy = Policies.For(mode);
        var sw = Stopwatch.StartNew();

        // ── The permitted set decides. There is no `if (mode == …)` here, and
        //    there is no `if (offline)` either — see RecordAsync. ──
        var localAllowed = policy.Allowed.Contains(Tier.T1Device);
        var cloudAllowed = policy.Allowed.Contains(Tier.T3VendorCloud);

        return localAllowed
            ? await WithLocalPathAsync(req, policy, cloudAllowed, sw, ct)
            : await CloudOnlyAsync(req, sw, ct);
    }

    #region No local path

    // CloudOnly. The permitted set has ONE element and it is past wall 1.
    //
    // Note what is NOT here: no redaction. A cloud-only architecture *cannot*
    // redact — deciding which spans are fab IP requires a local model, and there
    // isn't one. Redaction is a consequence of having T1, not a feature someone
    // forgot to tick.
    //
    // Note what else is NOT here: a connectivity check. We call, and we find out.
    // Offline is a `catch`, not an `if`.
    async Task<RouteResult> CloudOnlyAsync(RouteRequest req, Stopwatch sw, CancellationToken ct)
    {
        // The same extraction/classification call as the local path — only the tier
        // executing it changes. Skipping it here would ship every CloudOnly note with
        // an empty ToolId/Chamber/Metric/Tags and Severity stuck at Routine, degrading
        // the report's grouping and the fleet-wide trend query for no architectural
        // reason.
        var cloud = tiers[Tier.T3VendorCloud];
        var (cleaned, extracted) = await ExtractAndCleanAsync(cloud, req.Raw, ct);
        var tags = extracted.Tags.Count > 0
            ? extracted.Tags
            : Prompts.DeriveTags(req.Raw, extracted.Severity);

        var note = FabNote.New(req.Raw, req.Author, req.DeviceId, req.Cleanroom) with
        {
            Id = req.NoteId ?? Guid.NewGuid(),
            Cleaned = cleaned,
            ToolId = extracted.ToolId,
            Chamber = extracted.Chamber,
            Metric = extracted.Metric,
            Value = extracted.Value,
            Severity = extracted.Severity,
            Tags = tags,
            // IpSpans stays empty: redaction needs a local model to draw the border
            // with, and CloudOnly by definition has none. That divergence from the
            // local path is architectural, not a gap.
        };

        try
        {
            var reply = await cloud.GetResponseAsync(
                [new ChatMessage(ChatRole.System, Prompts.Polish), new ChatMessage(ChatRole.User, req.Raw)],
                Prompts.ProseOptions, ct);
            sw.Stop();

            // 🔍 The payload is the WHOLE note, verbatim. That just left the building.
            return await RecordAsync(note with { Polished = reply.Text },
                Tier.T3VendorCloud, "T3", models.Cloud, "mode: CloudOnly",
                sw.ElapsedMilliseconds, payload: req.Raw, output: reply.Text, ct: ct);
        }
        catch (Exception ex)
        {
            // 🔍 Nothing catches this *usefully*. There is no local path to fall back
            // to — the permitted set has one element and it is past the wall. **Dead,
            // not degraded**: the same failure under Hybrid merely costs the prose.
            sw.Stop();
            return await RecordAsync(note, Tier.T3VendorCloud, "T3 ✖", models.Cloud, "vendor unreachable",
                sw.ElapsedMilliseconds, payload: "", output: null, error: Describe(ex), ct: ct);
        }
    }

    #endregion

    // Everything below has a local path, so T1 always runs FIRST: structure,
    // classify and detect spans before any tier is chosen. Masking happens on the
    // device — the device never depends on the hub behaving well.
    async Task<RouteResult> WithLocalPathAsync(
        RouteRequest req, Policy policy, bool cloudAllowed, Stopwatch sw, CancellationToken ct)
    {
        var device = tiers[Tier.T1Device];

        var (cleaned, extracted) = await ExtractAndCleanAsync(device, req.Raw, ct);
        var spans = policy.ProtectIp ? await Redaction.DetectAsync(req.Raw, device, ct) : [];

        // Context tags: the model's if it gave any, deterministic keywords if not.
        // Same belt-and-braces rule the span detector follows — a note must not
        // lose its context because a 1.5B had an off moment.
        var tags = extracted.Tags.Count > 0
            ? extracted.Tags
            : Prompts.DeriveTags(req.Raw, extracted.Severity);

        var note = FabNote.New(req.Raw, req.Author, req.DeviceId, req.Cleanroom) with
        {
            Id = req.NoteId ?? Guid.NewGuid(),
            Cleaned = cleaned,
            ToolId = extracted.ToolId,
            Chamber = extracted.Chamber,
            Metric = extracted.Metric,
            Value = extracted.Value,
            Severity = extracted.Severity,
            IpSpans = spans,
            Tags = tags,
        };

        var masked = spans.Count > 0 ? Redaction.Mask(req.Raw, spans) : req.Raw;

        // ── Local-only · the vendor is not in the permitted set ─────────────
        //
        // 🔍 The cloud is not attempted at all — not tried and failed, *never asked*.
        // That is why this row reads identically whether the technician is at the
        // bench or sealed in a cleanroom, and why the decision log needs two
        // different words: `policy: edge only` here, `vendor unreachable` below.
        // One is a decision the company made; the other is a building.
        if (!cloudAllowed)
        {
            sw.Stop();
            return await RecordAsync(note,
                Tier.T1Device, "T1", models.Device, "policy: edge only",
                sw.ElapsedMilliseconds, payload: "", output: cleaned, ct: ct);
        }

        #region Hybrid

        // ── Hybrid · both tiers permitted, so the NOTE decides ──────────────
        if (spans.Count > 0)
        {
            // Hard rule. The network is fine and it still doesn't go.
            if (!req.WantPolish)
            {
                sw.Stop();
                return await RecordAsync(note, Tier.T1Device, "T1", models.Device,
                    "policy: fab IP, no cloud-grade prose needed",
                    sw.ElapsedMilliseconds, payload: "", output: cleaned, ct: ct);
            }

            try
            {
                // 🔍 The device masks → the vendor writes → the device restores.
                //
                // The restore is a dictionary lookup, NOT a model call: ⟦R1⟧ → RX-7
                // is exact and already held here. Putting a model in that step buys
                // nothing to reason about and risks the one failure that is hardest
                // to spot — prose that reads perfectly with the wrong value in it.
                var reply = await tiers[Tier.T3VendorCloud].GetResponseAsync(
                    [new ChatMessage(ChatRole.System, Prompts.Polish), new ChatMessage(ChatRole.User, masked)],
                    Prompts.ProseOptions, ct);
                var restored = Redaction.Unmask(reply.Text ?? "", spans);
                sw.Stop();

                // 🔍 The payload is the MASKED text.
                return await RecordAsync(note with { Polished = restored },
                    Tier.T1Device, "T1→T3", $"{models.Device} + {models.Cloud}",
                    "policy: fab IP detected", sw.ElapsedMilliseconds, payload: masked, output: restored, ct: ct);
            }
            catch (Exception ex)
            {
                // 🔍 DEGRADED, NOT DEAD — and the difference from CloudOnly is the
                // local model. Permitted, attempted, unreachable; the structured,
                // classified, masked note is already in hand and only the prose is
                // missing. Nothing is queued: the technician asked a question and
                // did not get an answer, and retrying is one click away.
                sw.Stop();
                return await RecordAsync(note, Tier.T1Device, "T1", models.Device,
                    "vendor unreachable", sw.ElapsedMilliseconds, payload: "", output: cleaned,
                    degraded: true, error: Describe(ex), ct: ct);
            }
        }

        #endregion

        // ── Hybrid · nothing proprietary in this note ───────────────────────
        if (!req.WantPolish)
        {
            sw.Stop();
            return await RecordAsync(note,
                Tier.T1Device, "T1", models.Device, "no cloud-grade prose needed",
                sw.ElapsedMilliseconds, payload: "", output: cleaned, ct: ct);
        }

        try
        {
            var reply = await tiers[Tier.T3VendorCloud].GetResponseAsync(
                [new ChatMessage(ChatRole.System, Prompts.Polish), new ChatMessage(ChatRole.User, req.Raw)],
                Prompts.ProseOptions, ct);
            sw.Stop();
            return await RecordAsync(note with { Polished = reply.Text },
                Tier.T3VendorCloud, "T1→T3", $"{models.Device} + {models.Cloud}",
                "no fab IP in this note", sw.ElapsedMilliseconds, payload: req.Raw, output: reply.Text, ct: ct);
        }
        catch (Exception ex)
        {
            // Degraded, not dead — same as the masked branch above.
            sw.Stop();
            return await RecordAsync(note, Tier.T1Device, "T1", models.Device,
                "vendor unreachable", sw.ElapsedMilliseconds, payload: "", output: cleaned,
                degraded: true, error: Describe(ex), ct: ct);
        }
    }

    // ── Every path ends here. That is what makes the audit trail free, and it
    //    is also the one place the Central System is spoken to. ─────────────
    async Task<RouteResult> RecordAsync(FabNote note, Tier tier, string path, string model, string reason,
        long ms, string payload, string? output, bool degraded = false, string? error = null,
        CancellationToken ct = default)
    {
        ledger.Write(new LedgerEntry(note.Id, tier, path, model, reason, (int)ms, payload, DateTimeOffset.UtcNow));

        // 🏢 Sync to the Central System: every note, every posture, unredacted. It is
        // the fab's own server, in the fab's own jurisdiction — you do not redact to
        // yourself, and redacting here would only make the fleet-wide finding worse.
        //
        // ⚠️ But it is in a different ROOM, and the cleanroom blocks the fab's own LAN
        // as thoroughly as it blocks the internet. Trust and reachability are different
        // questions. So this can fail — and when it does the note simply stays Pending
        // and is retried later. It is never dropped.
        //
        // Swallowing both the exception and the return value would lose a note
        // silently: the device would still show it, and the fleet-wide finding would be
        // quietly one complaint short with nothing looking broken.
        //
        // 📌 The note's own SyncState IS the deferred-delivery mechanism. There is no
        // queue holding a second copy of it; "pending" is a fact about a note.
        //
        // After sw.Stop() on purpose: the sync must not show up in the latency numbers.
        var delivered = false;
        if (sink is not null)
        {
            try { delivered = await sink.SyncAsync(note, ct); }
            catch { delivered = false; }
        }

        var settled = note with { Sync = delivered ? SyncState.Synced : SyncState.Pending };
        return new RouteResult(settled, tier, path, reason, (int)ms, payload, output, degraded, error);
    }

    // The one method CloudOnly and the local path both call, differing only in which
    // tier's IChatClient they hand it: the model changes, not the extraction logic.
    // Sequential on purpose — the device runs one prompt at a time.
    async Task<(string Cleaned, Extracted Extracted)> ExtractAndCleanAsync(
        IChatClient tier, string raw, CancellationToken ct)
    {
        var extracted = await ExtractAsync(tier, raw, ct);
        var cleaned = await CleanAsync(tier, raw, ct);
        return (cleaned, extracted);
    }

    async Task<Extracted> ExtractAsync(IChatClient device, string raw, CancellationToken ct)
    {
        try
        {
            var reply = await device.GetResponseAsync(
                [new ChatMessage(ChatRole.System, Prompts.Extract), new ChatMessage(ChatRole.User, raw)],
                Prompts.TightOptions, ct);
            // A 1.5B *will* occasionally emit malformed JSON. Handle it, don't hope.
            return Prompts.ParseExtracted(reply.Text) ?? Prompts.FallbackExtract(raw);
        }
        catch { return Prompts.FallbackExtract(raw); }
    }

    async Task<string> CleanAsync(IChatClient device, string raw, CancellationToken ct)
    {
        try
        {
            var reply = await device.GetResponseAsync(
                [new ChatMessage(ChatRole.System, Prompts.Clean), new ChatMessage(ChatRole.User, raw)],
                Prompts.TightOptions, ct);
            return string.IsNullOrWhiteSpace(reply.Text) ? raw : reply.Text.Trim();
        }
        catch { return raw; }
    }

    static string Describe(Exception ex) =>
        ex is OperationCanceledException or TimeoutException
            ? "Service unavailable — the request timed out."
            : $"{ex.GetType().Name}: {ex.Message}";
}

/// <summary>
/// The honest refusal. A device inside an airgap holds ONE technician's notes, so
/// a cross-note question is out of its scope by construction — not because the
/// model is small. No model fixes this; only moving the workload to where the data
/// is does.
/// </summary>
public static class DeviceScope
{
    public static bool IsCrossNoteQuestion(string question)
    {
        var q = question.ToLowerInvariant();
        return q.Contains("anything odd") || q.Contains("trend") || q.Contains("pattern")
            || q.Contains("across") || q.Contains("anyone else") || q.Contains("today")
            || q.Contains("this week") || q.Contains("all notes");
    }

    public static string Refuse(int localNoteCount, string author, int mentions, string toolId) =>
        $"I can only see this device's notes — {localNoteCount} from your shift, all written by {author}. " +
        (mentions > 0
            ? $"{mentions} mention{(mentions == 1 ? "" : "s")} {toolId}. "
            : $"None mention {toolId}. ") +
        "A cross-technician trend needs every technician's notes, and they are not on this device.";
}
