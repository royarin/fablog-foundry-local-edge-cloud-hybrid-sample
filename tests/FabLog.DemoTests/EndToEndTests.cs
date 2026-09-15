using FabLog.TrustHub;
using Microsoft.Extensions.AI;

namespace FabLog.DemoTests;

/// <summary>
/// The whole system as one test.
/// <para>
/// <see cref="RoutingTests"/> and <see cref="TrendTests"/> each prove a claim in
/// isolation. These prove the thing they cannot: that the steps are
/// <b>linked</b> — that the note written on the device is the one the hub counts
/// in its trend, that the queue drains to the Central System and not into the
/// trend, and that the ledger is the record of all of it.
/// </para>
/// <para>
/// The failures worth fearing are link failures, not step failures: a tool ID
/// that doesn't group, a note lost by a retry, a queue that drains into the wrong
/// store. Those only show up when the steps run in order, against one device and
/// one hub, with nothing reset in between.
/// </para>
/// <para>
/// ⚠️ The pending queue is the device's backlog to the <b>Central System</b>, not
/// to the vendor. Cloud work is never stored up — see
/// <c>Nothing_bound_for_the_cloud_is_ever_stored_up</c>.
/// </para>
/// </summary>
public class EndToEndTests
{
    static string SeedPath => Path.Combine(AppContext.BaseDirectory, "Data", "hub-notes.json");

    [Fact]
    public async Task The_whole_flow_runs_end_to_end_in_order()
    {
        var hub = new HubStore(SeedPath);
        var brain = new HubBrain(hub, new FakeChat(CloudReplies.NoTrend, "gpt-4o"), "gpt-4o");

        // The pad and the Central System, wired the way DeviceHost wires them: the
        // server is an INoteSink in the fab's own jurisdiction, never a tier.
        var vendor = new SwitchableChat();
        var central = new HubSink(hub, "Night 2026-09-12");
        var device = new Rig(cloud: vendor, sink: central);

        // ══ 1 · at the bench · the leak ═════════════════════════════════════
        // The technician has not gowned up yet, so both wires are live.
        //
        // ⚠️ A DIFFERENT TOOL on purpose. This note must not be the third ETCH-03
        // complaint: it syncs immediately (the door is open), and the pattern would
        // resolve before the reconnect step ever runs.
        var leaked = await device.LogAsync(DeviceReplies.BenchNote, DemoMode.CloudOnly);
        Assert.Equal(DeviceReplies.BenchNote, leaked.PayloadSent);
        Assert.Contains("RX-9", leaked.PayloadSent);

        // It reached the Central System at once — the pad is still on the LAN.
        // ToolId is populated too: CloudOnly now shares the local path's
        // extraction/classification call (on the vendor tier, not the device),
        // so this stopped being the one field a cloud-only posture left blank.
        Assert.Equal(13, hub.Count);
        Assert.Equal(0, device.PendingCount);
        Assert.Equal("ETCH-06", Assert.Single(hub.All, n => n.Id == leaked.Note.Id).ToolId);

        // ══ 2 · into the cleanroom ══════════════════════════════════════════
        // ⚠️ BOTH destinations go away together. The room blocks the fab's own LAN
        // as thoroughly as it blocks the internet — trust and distance are
        // different questions, and this is the line that says so.
        vendor.PadOnNetwork = false;
        central.Reachable = false;

        var dead = await device.LogAsync(DeviceReplies.SeedNote, DemoMode.CloudOnly);
        Assert.True(dead.Failed);
        Assert.Null(dead.Output);
        Assert.Equal("T3 ✖", dead.Path);

        // Nothing was LOST, though — the note is held, waiting for a server it will
        // reach in thirty seconds. The Central System's count has not moved.
        Assert.Equal(13, hub.Count);
        Assert.Equal(1, device.PendingCount);

        // ══ 3 · edge only · it just works ═══════════════════════════════════
        var edge = await device.LogAsync(DeviceReplies.SeedNote, DemoMode.EdgeOnly);
        Assert.Equal("", edge.PayloadSent);
        Assert.NotNull(edge.Output);
        Assert.Equal("ETCH-03", edge.Note.ToolId);

        // The vendor was never even asked — not tried and failed, NOT PERMITTED.
        // (Step 1's note reached it from the bench — three calls, extract, clean
        // and polish, since CloudOnly shares the local path's extraction call —
        // and nothing has reached the vendor since: step 2's attempt died before
        // SwitchableChat ever records it, and step 3 doesn't touch the cloud.)
        Assert.False(edge.Degraded);
        Assert.Equal(3, vendor.Received.Count);
        Assert.Equal(2, device.PendingCount);        // …but both notes still owe the server
        var theNote = edge.Note;                      // ← the third complaint

        // ══ 4 · the honest refusal ══════════════════════════════════════════
        const string question = "anything odd about ETCH-03 today?";
        Assert.True(DeviceScope.IsCrossNoteQuestion(question));
        Assert.Contains("not on this device", DeviceScope.Refuse(1, theNote.Author, 1, theNote.ToolId!));

        // And the Central System cannot answer it either — not yet. It is holding
        // two of the three complaints, and two is a coincidence.
        var midway = brain.Detect();
        Assert.Equal(2, midway!.Technicians);
        Assert.Equal(Severity.Watch, midway.Verdict);

        // ══ 5 · hybrid, still sealed · DEGRADED, NOT DEAD ═══════════════════
        // The same note, re-processed under a different posture — which is why
        // the stores upsert.
        //
        // ⭐ The contrast the whole system turns on: the vendor is exactly as
        // unreachable as it was in step 2, and this time it costs only the prose.
        // Nothing is queued — the technician asked and did not get an answer.
        var degraded = await device.LogAsync(DeviceReplies.SeedNote, DemoMode.Hybrid, noteId: theNote.Id);
        Assert.True(degraded.Degraded);
        Assert.False(degraded.Failed);        // ← the difference from step 2
        Assert.NotNull(degraded.Output);      // the local answer stands
        Assert.Equal("ETCH-03", degraded.Note.ToolId);

        // Nothing crossed, so nothing is recorded as having crossed.
        Assert.Equal("", degraded.PayloadSent);

        // Re-processing did not inflate the backlog: pending is a property of a
        // note, so the same note cannot be owed twice.
        Assert.Equal(2, device.PendingCount);

        // ══ 6 · out of the cleanroom · the pending notes go, by themselves ══
        vendor.PadOnNetwork = true;
        central.Reachable = true;

        var delivered = await device.SyncPendingAsync();

        Assert.Equal(2, delivered);
        Assert.Equal(0, device.PendingCount);

        // ⭐ That is what completes the picture: 13 → 15.
        Assert.Equal(15, hub.Count);

        // ══ 7 · the question no device could answer ═════════════════════════
        // And it only became answerable once the backlog reached the hub.
        var after = await brain.AnalyseAsync(question);
        Assert.Equal(15, after.CorpusSize);
        Assert.Equal(3, after.Finding!.Mentions);
        Assert.Equal(3, after.Finding.Technicians);
        Assert.Equal(Severity.Escalate, after.Finding.Verdict);
        Assert.Equal(3, after.Sources.Count);

        // The supervisor reads the real notes; the vendor read columns.
        Assert.All(after.Sources, n => Assert.Contains("RX-7", n.Text));
        Assert.DoesNotContain("RX-7", hub.AsDigest());
        Assert.DoesNotContain("94.2%", hub.AsDigest());

        // The corpus spans two cleanrooms; the finding is confined to one.
        Assert.Equal(2, hub.All.Select(n => n.Cleanroom).Where(c => c.Length > 0).Distinct().Count());

        // ══ 8 · the shift report · outside, connected, still masked ═════════
        var report = await device.Reporter.CompileAsync(
            [leaked.Note, theNote], DemoMode.Hybrid,
            "K. Nagy", "Night 2026-09-12", "PAD-07", "CR-2");

        Assert.True(report.CloudUsed);
        Assert.True(report.Submitted);
        Assert.NotEmpty(report.Facts);

        // The masked brief crossed; the real values did not.
        Assert.Contains("⟦R1⟧", report.PayloadSent);
        Assert.DoesNotContain("RX-7", report.PayloadSent);

        // …and the finished report, restored locally, has them back.
        Assert.Contains("RX-7", report.Final);

        // ══ 9 · the ledger ══════════════════════════════════════════════════
        var ledger = device.Ledger.Entries;

        Assert.Equal(5, ledger.Count);
        Assert.Equal(["T3", "T3 ✖", "T1", "T1", "device→cloud"], ledger.Select(e => e.Path));

        Assert.Contains("RX-9", ledger[0].PayloadSent);          // everything left
        Assert.Equal("mode: CloudOnly", ledger[0].Reason);
        Assert.Equal("vendor unreachable", ledger[1].Reason);    // the wire decided
        Assert.Equal("policy: edge only", ledger[2].Reason);     // policy decided
        Assert.True(ledger[2].NothingLeftTheDevice);
        Assert.Equal("vendor unreachable", ledger[3].Reason);
        Assert.StartsWith("shift report", ledger[4].Reason);

        // 🔍 Rows 2 and 3 are the pair: identical on screen, different words.
        Assert.Equal(ledger[2].Tier, ledger[3].Tier);
        Assert.Equal(ledger[2].PayloadSent, ledger[3].PayloadSent);
        Assert.NotEqual(ledger[2].Reason, ledger[3].Reason);
    }

    [Fact]
    public async Task The_central_system_receives_every_note_in_every_posture_once_back_in_range()
    {
        // 🏢 Three postures, one trip into the cleanroom, six notes — and the
        // Central System ends up with all six, in full, unredacted.
        //
        // ⚠️ NOT "they arrive immediately" — they cannot: the server is trusted,
        // and it is in another room. What is guaranteed is that none is ever LOST.
        var hub = new HubStore(SeedPath);
        var vendor = new SwitchableChat { PadOnNetwork = false };
        var central = new HubSink(hub, "Night 2026-09-12") { Reachable = false };
        var device = new Rig(cloud: vendor, sink: central);

        foreach (var mode in (DemoMode[])[DemoMode.CloudOnly, DemoMode.EdgeOnly, DemoMode.Hybrid])
        {
            await device.LogAsync(DeviceReplies.SeedNote, mode);
            await device.LogAsync(DeviceReplies.BenchNote, mode);
        }

        // Sealed: nothing has arrived, and nothing has been dropped.
        Assert.Equal(12, hub.Count);
        Assert.Equal(6, device.PendingCount);

        // Back in range: everything lands, on its own.
        vendor.PadOnNetwork = true;
        central.Reachable = true;
        await device.SyncPendingAsync();

        Assert.Equal(18, hub.Count);
        Assert.Equal(0, device.PendingCount);
        Assert.All(hub.All.Skip(12), n => Assert.DoesNotContain("⟦", n.Text));
    }

    [Fact]
    public async Task A_failed_sync_is_never_lost()
    {
        // The failure mode being guarded against: a sync path that swallows the
        // exception AND discards the return value. A note that failed to sync then
        // vanishes — the device still shows it, the server does not have it, and
        // the fleet-wide finding is quietly one complaint short. Nothing looks
        // broken, which is what makes it worth a dedicated test.
        var hub = new HubStore(SeedPath);
        var central = new HubSink(hub, "Night 2026-09-12") { Reachable = false };
        var device = new Rig(sink: central);

        var note = await device.LogAsync(DeviceReplies.SeedNote, DemoMode.EdgeOnly);

        Assert.Equal(12, hub.Count);                  // did not arrive
        Assert.Equal(1, device.PendingCount);       // …but it is waiting

        central.Reachable = true;
        await device.SyncPendingAsync();

        Assert.Equal(13, hub.Count);
        Assert.Equal(0, device.PendingCount);
        Assert.Contains(hub.All, n => n.Id == note.Note.Id);
    }

    [Fact]
    public async Task A_sealed_cleanroom_degrades_the_cloud_half_instead_of_queueing_it()
    {
        // ⭐ The central contrast, as one test. Same wall, same instant, two
        // postures, two completely different outcomes:
        //
        //   • cloud-only → DEAD. No local path exists; the call is the only path.
        //   • hybrid     → DEGRADED. The note is structured, classified and masked;
        //                  only the prose is missing.
        //
        // And in neither case is cloud work stored up to send later. Deferring a
        // DELIVERY is useful; deferring an ANSWER is not.
        var hub = new HubStore(SeedPath);
        var vendor = new SwitchableChat { PadOnNetwork = false };
        var central = new HubSink(hub, "Night 2026-09-12") { Reachable = false };
        var device = new Rig(cloud: vendor, sink: central);

        var dead = await device.LogAsync(DeviceReplies.SeedNote, DemoMode.CloudOnly);
        Assert.True(dead.Failed);
        Assert.Null(dead.Output);

        var degraded = await device.LogAsync(DeviceReplies.SeedNote, DemoMode.Hybrid);
        Assert.False(degraded.Failed);
        Assert.True(degraded.Degraded);
        Assert.NotNull(degraded.Output);                 // the local answer stands
        Assert.Equal("ETCH-03", degraded.Note.ToolId);

        // Both are still owed to the Central System — that backlog is about
        // DISTANCE and has nothing to do with either posture.
        Assert.Equal(2, device.PendingCount);
    }

    [Fact]
    public async Task The_hub_can_be_reset_in_one_call()
    {
        // A second run must start from exactly the state the first one did.
        var hub = new HubStore(SeedPath);
        var brain = new HubBrain(hub, new FakeChat(CloudReplies.NoTrend, "gpt-4o"), "gpt-4o");

        hub.Add(new HubNote(Guid.NewGuid(), "K. Nagy", "Night 2026-09-12", "ETCH-03", "B",
            "uniformity", Severity.Escalate, "ETCH-03 chamber B uniformity 94.2% — off spec.",
            DateTimeOffset.Parse("2026-09-12T21:30:00Z")));
        Assert.Equal(Severity.Escalate, (await brain.AnalyseAsync("trend?")).Finding!.Verdict);

        hub.Reset();

        Assert.Equal(12, hub.Count);
        Assert.Equal(Severity.Watch, (await brain.AnalyseAsync("trend?")).Finding!.Verdict);
    }
}

/// <summary>
/// The vendor, behind the cleanroom door. Flip <see cref="PadOnNetwork"/> and the
/// next call throws what Windows throws — the cleanroom toggle, in one property.
/// </summary>
public sealed class SwitchableChat : IChatClient
{
    /// <summary>False = the pad is inside the cleanroom. A position, not a network state.</summary>
    public bool PadOnNetwork { get; set; } = true;

    public List<string> Received { get; } = [];

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
    {
        if (!PadOnNetwork)
            throw new HttpRequestException("No such host is known.",
                new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.HostNotFound));

        var user = messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? "";
        Received.Add(user);
        return Task.FromResult(new ChatResponse(
            new ChatMessage(ChatRole.Assistant, $"Incident report: {user}")) { ModelId = "gpt-4o" });
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}

/// <summary>
/// A real <see cref="HubStore"/> behind the device's <see cref="INoteSink"/> — the
/// same mapping <c>HubClient.SyncAsync</c> POSTs and <c>POST /sync</c> stores, minus
/// the HTTP. These tests need the Central System's actual grouping, not a recording
/// of what was sent to it.
/// <para>
/// <see cref="Reachable"/> false models the technician being inside the cleanroom,
/// where this server is exactly as far away as the vendor.
/// </para>
/// </summary>
public sealed class HubSink(HubStore store, string shift) : INoteSink, IReportSink
{
    public bool Reachable { get; set; } = true;

    public List<ShiftReport> Reports { get; } = [];

    public Task<bool> SyncAsync(FabNote note, CancellationToken ct = default)
    {
        if (!Reachable) return Task.FromResult(false);

        // The same mapping HubClient POSTs: the readable sentence as the note's text,
        // the technician's shorthand kept beside it.
        store.Add(new HubNote(
            note.Id, note.Author, shift,
            note.ToolId ?? "", note.Chamber ?? "", note.Metric ?? "",
            note.Severity, note.Readable, note.At,
            note.DeviceId, note.Cleanroom, note.ContextTags, note.Raw));
        return Task.FromResult(true);
    }

    public Task<bool> SyncReportAsync(ShiftReport report, CancellationToken ct = default)
    {
        if (!Reachable) return Task.FromResult(false);
        Reports.Add(report);
        store.AddReport(new SubmittedReport(
            report.Author, report.Shift, report.DeviceId, report.Cleanroom,
            report.CompiledAt, report.NoteCount, report.CloudUsed, report.Final));
        return Task.FromResult(true);
    }
}
