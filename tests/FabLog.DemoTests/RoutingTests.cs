namespace FabLog.DemoTests;

/// <summary>
/// <b>The routing claims, as assertions.</b> One region per posture, plus the
/// cross-cutting behaviour each one has to keep.
/// <para>
/// These are not unit tests of convenient units. Each one asserts something a
/// reader is asked to believe about where data went — and it asserts it about
/// the <i>payload</i> and the <i>ledger</i>, because those are the two things
/// the UI actually shows.
/// </para>
/// <para>
/// ⚠️ The three architectural invariants under test: T3 sits beside T1 rather
/// than behind T2, the hub is inside the airgap and gets every note in full, and
/// offline is discovered by failing rather than by asking. An assertion that
/// passes against a system violating any of those is the most dangerous kind of
/// green.
/// </para>
/// </summary>
public class RoutingTests
{
    // ══ Edge only · the note that never leaves ═══════════════════════════════
    #region EdgeOnly

    [Fact]
    public async Task Edge_only_note_is_processed_and_nothing_crosses_the_border()
    {
        var rig = new Rig(cloud: new OfflineChat());

        var r = await rig.LogAsync(DeviceReplies.SeedNote, DemoMode.EdgeOnly);

        Assert.Equal(Tier.T1Device, r.Tier);
        Assert.Equal("", r.PayloadSent);              // the claim, literally
        Assert.False(r.Failed);
        Assert.NotNull(r.Output);

        // and it genuinely did the work, it did not merely decline to send
        Assert.Equal("ETCH-03", r.Note.ToolId);
        Assert.Equal("B", r.Note.Chamber);
        Assert.Equal(Severity.Escalate, r.Note.Severity);
    }

    [Fact]
    public async Task The_central_system_gets_the_note_unredacted_even_under_a_local_only_posture()
    {
        // 🏢 The least intuitive assertion in the suite. "Nothing leaves" means
        // nothing crosses the BORDER. The fab's own server is in the fab's own
        // jurisdiction, so it gets the note unredacted — and that is what lets the
        // supervisor see a trend the device cannot.
        var rig = new Rig(cloud: new OfflineChat());

        await rig.LogAsync(DeviceReplies.SeedNote, DemoMode.EdgeOnly);

        var stored = Assert.Single(rig.HubSaw);
        Assert.Contains("RX-7", stored);             // unredacted, on purpose
        Assert.DoesNotContain("⟦", stored);
    }

    [Fact]
    public async Task The_central_system_gets_the_converted_sentence_and_the_shorthand_beside_it()
    {
        // 📝 Notes are converted AND THEN synchronised — so the text the server
        // stores is the sentence the on-device model produced, not the shorthand
        // it was given.
        //
        // But the original travels too. This server is the fab's system of record,
        // and a rewritten version must never REPLACE what somebody actually wrote in
        // one — which is exactly what an auditor objects to, in a system whose
        // closing argument is an audit trail.
        var rig = new Rig(cloud: new OfflineChat());

        var r = await rig.LogAsync(DeviceReplies.SeedNote, DemoMode.EdgeOnly);
        var note = Assert.Single(rig.Notes);

        // What the supervisor reads: the converted sentence.
        Assert.Equal(note.Cleaned, note.Readable);
        Assert.NotEqual(DeviceReplies.SeedNote, note.Readable);
        Assert.Equal(note.Readable, Assert.Single(rig.HubSaw));

        // What is kept beside it: the technician's own words, untouched.
        Assert.Equal(DeviceReplies.SeedNote, note.Raw);
        Assert.Equal(DeviceReplies.SeedNote, r.Note.Raw);
    }

    [Fact]
    public async Task The_hub_is_not_on_the_route_out()
    {
        // The hub is a destination, never a hop. It is not in the tier dictionary
        // at all, so there is no arrangement of policy that could make a note
        // travel to the vendor THROUGH it.
        var rig = new Rig();

        await rig.LogAsync(DeviceReplies.SeedNote, DemoMode.CloudOnly);

        // Three calls now (extract, clean, polish) — CloudOnly shares the local
        // path's extraction/classification call, on the vendor tier — but all
        // three still go straight to the vendor, never through the hub.
        Assert.Equal(3, rig.VendorSaw.Count);          // the pad reached the vendor itself
        Assert.Single(rig.HubSaw);                     // and told the hub, separately, once
    }

    [Fact]
    public async Task Tool_id_is_normalised_so_the_hub_can_group_it_later()
    {
        // Measured: every model variant returns ETCH03 / etch03, never ETCH-03.
        // The hub groups the trend BY TOOL ID, so this is the difference between
        // one group of three and three groups of one.
        var rig = new Rig();

        var r = await rig.LogAsync(DeviceReplies.SeedNote, DemoMode.EdgeOnly);

        Assert.Equal("ETCH-03", r.Note.ToolId);
    }

    [Fact]
    public async Task Survives_a_model_that_returns_garbage()
    {
        // The 1.5B model does this, measurably often. Routing must not stall.
        var rig = new Rig(device: DeviceReplies.Garbage);

        var r = await rig.LogAsync(DeviceReplies.SeedNote, DemoMode.EdgeOnly);

        Assert.False(r.Failed);
        Assert.Equal("ETCH-03", r.Note.ToolId);        // via Prompts.FallbackExtract
        Assert.Equal(Severity.Escalate, r.Note.Severity);
        Assert.Equal("", r.PayloadSent);
    }

    [Fact]
    public async Task An_unreachable_central_system_defers_the_note_instead_of_dropping_it()
    {
        // ⚠️ Surviving an unreachable server is not enough on its own: a route
        // that silently throws the note away also "survives". That failure mode
        // — swallow, no retry, no record — leaves the device showing a note the
        // server does not have, and the fleet-wide finding quietly one complaint
        // short. Nothing looks broken, which is what makes it dangerous.
        var rig = new Rig(hubUp: false);

        var r = await rig.LogAsync(DeviceReplies.SeedNote, DemoMode.EdgeOnly);

        Assert.False(r.Failed);        // the route still completes
        Assert.Empty(rig.HubSaw);      // …and the note has not arrived

        // …but it is HELD, not lost. That is the assertion that matters.
        Assert.Equal(1, rig.PendingCount);
        Assert.Equal(SyncState.Pending, r.Note.Sync);
        Assert.Equal(r.Note.Id, Assert.Single(rig.Pending).Id);
    }

    #endregion

    // ══ Cloud only · the leak ════════════════════════════════════════════════
    #region CloudOnly

    [Fact]
    public async Task Cloud_only_sends_the_whole_note_verbatim()
    {
        // The starkest case in the suite. If this assertion ever goes green by
        // sending something masked, it is no longer testing what it claims to.
        var rig = new Rig();

        var r = await rig.LogAsync(DeviceReplies.SeedNote, DemoMode.CloudOnly);

        Assert.Equal(DeviceReplies.SeedNote, r.PayloadSent);
        Assert.Contains("RX-7", r.PayloadSent);
        Assert.Contains("94.2%", r.PayloadSent);
        Assert.Contains("4.5s", r.PayloadSent);
        Assert.DoesNotContain("⟦", r.PayloadSent);
        Assert.Equal("mode: CloudOnly", r.Reason);
        Assert.Equal("T3", r.Path);

        // and the vendor was handed exactly that, not a re-derivation of it — on
        // every one of the three calls (extract, clean, polish) it now makes,
        // sharing the same extraction/classification call as the local path.
        Assert.Equal(3, rig.VendorSaw.Count);
        Assert.All(rig.VendorSaw, p => Assert.Equal(r.PayloadSent, p));
    }

    [Fact]
    public async Task Cloud_only_cannot_redact_because_no_local_model_ran()
    {
        // Redaction is a CONSEQUENCE of having T1, not a feature someone forgot
        // to tick. That is why this is an architecture problem and not a bug.
        var rig = new Rig();

        await rig.LogAsync(DeviceReplies.SeedNote, DemoMode.CloudOnly);

        Assert.Equal(0, rig.Device.Calls);
        Assert.False(Policies.For(DemoMode.CloudOnly).ProtectIp);
    }

    #endregion

    // ══ Losing the network ═══════════════════════════════════════════════════
    #region Offline

    [Fact]
    public async Task Cloud_only_offline_fails_with_nothing_to_fall_back_to()
    {
        var rig = new Rig(cloud: new OfflineChat());

        var r = await rig.LogAsync(DeviceReplies.SeedNote, DemoMode.CloudOnly);

        Assert.True(r.Failed);
        Assert.Equal("vendor unreachable", r.Reason);

        // The prose is dead, but the fields are not: ExtractAsync/CleanAsync each
        // catch their own failed call and fall back to a deterministic, offline
        // parse (Prompts.FallbackExtract / the raw text) — so this note is NOT
        // blank the way it was before CloudOnly shared the local path's
        // extraction call. It still cannot redact (no local model ran, so no
        // span detection either), which is the actual, architectural difference.
        Assert.Equal("ETCH-03", r.Note.ToolId);
        Assert.Empty(r.Note.Spans);
        Assert.Equal("T3 ✖", r.Path);
        Assert.Null(r.Output);
        Assert.False(r.Degraded);              // not even queued — nothing owns the retry
    }

    [Fact]
    public async Task Edge_only_offline_is_unaffected()
    {
        // Same lost network, same instant. The only difference is the permitted set.
        var rig = new Rig(cloud: new OfflineChat());

        var r = await rig.LogAsync(DeviceReplies.SeedNote, DemoMode.EdgeOnly);

        Assert.False(r.Failed);
        Assert.Equal("policy: edge only", r.Reason);
        Assert.Equal(Tier.T1Device, r.Tier);
    }

    [Fact]
    public async Task Offline_is_discovered_by_failing_not_by_asking()
    {
        // 🔍 The claim the whole control surface rests on: there is no "am I
        // online" property in FabLog for anything to consult, or to lie about.
        //
        // The connectivity double says the vendor is DOWN. The cloud client says
        // it is UP. If anything consulted the former, no call would ever be
        // attempted — so three attempts (extract, clean, polish — CloudOnly now
        // shares the local path's extraction call) still prove nothing asks
        // before trying.
        var cloud = new FakeChat(DeviceReplies.For, "gpt-4o");
        var rig = new Rig(cloud: cloud, online: false);

        var r = await rig.LogAsync(DeviceReplies.SeedNote, DemoMode.CloudOnly);

        Assert.Equal(3, cloud.Received.Count);
        Assert.False(r.Failed);
        Assert.Equal("mode: CloudOnly", r.Reason);

        // …and the mirror image: the double says UP, the wire is down, and the
        // router still finds out the only way anyone ever can. All three calls
        // hit the same dead wire — extract and clean fail softly to their own
        // fallbacks, and the polish call is the one that surfaces as r2.Failed.
        var offline = new OfflineChat();
        var rig2 = new Rig(cloud: offline, online: true);

        var r2 = await rig2.LogAsync(DeviceReplies.SeedNote, DemoMode.CloudOnly);

        Assert.Equal(3, offline.Attempts);
        Assert.True(r2.Failed);
    }

    [Fact]
    public async Task A_sealed_cleanroom_cuts_the_fabs_own_network_too()
    {
        // 🚪 ⚠️ There is deliberately NO loopback exemption.
        //
        // A cleanroom RF-shields against ALL radio, the fab's own LAN included.
        // Letting loopback survive would mean the app syncs happily from inside a
        // room that is supposed to block wireless by design — which contradicts
        // the premise the whole scenario rests on.
        //
        // The boundary is WHICH CLIENTS GET THIS HANDLER, and both of the pad's
        // outbound clients do.
        var airlock = new TestAirlock { PadOnNetwork = false };
        var handler = new AirlockHandler(airlock) { InnerHandler = new StubHandler() };
        using var http = new HttpClient(handler);

        // The Central System, one room away: gone.
        var toCentral = await Assert.ThrowsAsync<HttpRequestException>(
            () => http.GetAsync("http://localhost:5100/sync"));
        Assert.Contains("No such host is known", toCentral.Message);

        // The vendor, past the border: gone, with WSAHOST_NOT_FOUND — exactly what
        // a missing NIC produces, so the error the user sees is the real one.
        var toVendor = await Assert.ThrowsAsync<HttpRequestException>(
            () => http.GetAsync("https://example.openai.azure.com/openai"));
        Assert.Equal(
            (int)System.Net.Sockets.SocketError.HostNotFound,
            Assert.IsType<System.Net.Sockets.SocketException>(toVendor.InnerException).ErrorCode);

        // Back at the bench, the same handler is a no-op for both.
        airlock.PadOnNetwork = true;
        Assert.Equal(System.Net.HttpStatusCode.OK,
            (await http.GetAsync("http://localhost:5100/sync")).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.OK,
            (await http.GetAsync("https://example.openai.azure.com/openai")).StatusCode);
    }

    [Fact]
    public async Task The_local_model_keeps_working_with_the_cleanroom_sealed()
    {
        // The other half of the boundary: the on-device model is untouched,
        // because it is an in-process call with no transport for the handler to
        // sit under. Sealed room, full local result.
        var rig = new Rig(cloud: new OfflineChat());

        var r = await rig.LogAsync(DeviceReplies.SeedNote, DemoMode.EdgeOnly);

        Assert.False(r.Failed);
        Assert.Equal("ETCH-03", r.Note.ToolId);
        Assert.NotNull(r.Output);
    }

    #endregion

    // ══ Scope · the honest refusal, and not-permitted vs unreachable ═════════
    #region Scope

    [Theory]
    [InlineData("anything odd about ETCH-03 today?")]
    [InlineData("any trend on chamber B?")]
    [InlineData("did anyone else see this across the fab?")]
    public void Cross_note_questions_are_recognised(string question) =>
        Assert.True(DeviceScope.IsCrossNoteQuestion(question));

    [Fact]
    public void Refusal_states_the_scope_and_still_answers_what_it_can()
    {
        var answer = DeviceScope.Refuse(localNoteCount: 4, author: "K. Nagy", mentions: 1, toolId: "ETCH-03");

        Assert.Contains("4", answer);
        Assert.Contains("K. Nagy", answer);
        Assert.Contains("ETCH-03", answer);
        // The point: the limit is the DATA on this device, not the model's size.
        Assert.Contains("not on this device", answer);
        Assert.DoesNotContain("too small", answer);
    }

    [Fact]
    public async Task Local_only_never_even_asks_the_cloud()
    {
        // The network is perfect. The vendor is reachable. It is still not called —
        // because it is NOT PERMITTED, which is a different thing from unreachable
        // and gets a different word in the log.
        var rig = new Rig(online: true);

        var r = await rig.LogAsync(DeviceReplies.SeedNote, DemoMode.EdgeOnly);

        Assert.Empty(rig.VendorSaw);        // not tried and failed — never asked
        Assert.False(r.Degraded);
        Assert.Equal("policy: edge only", r.Reason);
        Assert.Equal(SyncState.Synced, r.Note.Sync);   // the server was reachable
    }

    [Fact]
    public async Task The_ledger_records_not_permitted_distinctly_from_unreachable()
    {
        // 🔍 Two rows that look IDENTICAL on screen — same tier, same empty
        // payload, same local answer — where the `reason` column is the only
        // thing that distinguishes them.
        //
        // That column is the ledger's whole reason to exist: an auditor needs to
        // know whether you chose not to send, or merely failed to.
        var rig = new Rig(cloud: new OfflineChat());

        var refused = await rig.LogAsync(DeviceReplies.SeedNote, DemoMode.EdgeOnly);
        var blocked = await rig.LogAsync(DeviceReplies.SeedNote, DemoMode.Hybrid);

        Assert.Equal(refused.Tier, blocked.Tier);
        Assert.Equal("", refused.PayloadSent);
        Assert.Equal("", blocked.PayloadSent);

        Assert.Equal("policy: edge only", refused.Reason);
        Assert.Equal("vendor unreachable", blocked.Reason);
        Assert.NotEqual(refused.Reason, blocked.Reason);

        // …and only one of them even tried
        Assert.False(refused.Degraded);
        Assert.True(blocked.Degraded);
    }

    #endregion

    // ══ Deferred delivery · notes wait, answers do not ═══════════════════════
    #region Pending

    [Fact]
    public async Task Nothing_bound_for_the_cloud_is_ever_stored_up()
    {
        // ⚠️ Deferring a DELIVERY is useful — nobody is waiting on a note reaching
        // the Central System. Deferring an ANSWER is not: the technician asked a
        // question and is standing there. A reply that turns up twenty minutes
        // later is worse than an error, so there is nothing to store up.
        //
        // Here the vendor is gone but the server is reachable — the bench with a
        // dead uplink — so nothing is pending at all.
        var rig = new Rig(cloud: new OfflineChat());

        for (var i = 0; i < 6; i++) await rig.LogAsync(DeviceReplies.SeedNote, DemoMode.Hybrid);

        Assert.Equal(0, rig.PendingCount);          // the server got all six
        Assert.Equal(6, rig.HubSaw.Count);
        Assert.Empty(rig.VendorSaw);                // and the vendor got none, ever

        // Every one of them degraded rather than failing: the local answer stands.
        Assert.All(rig.Notes, n => Assert.Equal(SyncState.Synced, n.Sync));
    }

    [Fact]
    public async Task Pending_notes_go_oldest_first_and_a_failure_stops_the_run()
    {
        // Serial and stop-on-first-failure, both deliberate: the counter has to be
        // seen falling one at a time, and a note that fails must stay pending rather
        // than be skipped past — losing one would quietly leave the fleet-wide
        // finding a complaint short.
        var sink = new FakeSink(up: false);
        var rig = new Rig(sink: sink);

        for (var i = 0; i < 3; i++) await rig.LogAsync(DeviceReplies.SeedNote, DemoMode.EdgeOnly);
        Assert.Equal(3, rig.PendingCount);

        var oldest = rig.Pending[0];

        // Back in range, but the server accepts only one before refusing.
        sink.Up = true;
        sink.AcceptAtMost = 1;

        Assert.Equal(1, await rig.SyncPendingAsync());

        Assert.Equal(oldest.Id, Assert.Single(sink.Received).Note.Id);   // oldest went first
        Assert.Equal(2, rig.PendingCount);                               // the rest still waiting
        Assert.DoesNotContain(rig.Pending, n => n.Id == oldest.Id);      // and not the delivered one

        // Lift the cap and the remainder lands.
        sink.AcceptAtMost = int.MaxValue;
        Assert.Equal(2, await rig.SyncPendingAsync());
        Assert.Equal(0, rig.PendingCount);
    }

    [Fact]
    public async Task A_pending_note_is_retried_until_it_lands()
    {
        var sink = new FakeSink(up: false);
        var rig = new Rig(sink: sink);

        var r = await rig.LogAsync(DeviceReplies.SeedNote, DemoMode.EdgeOnly);
        Assert.Equal(SyncState.Pending, r.Note.Sync);
        Assert.Equal(1, rig.PendingCount);

        // Still sealed: the retry achieves nothing and loses nothing.
        Assert.Equal(0, await rig.SyncPendingAsync());
        Assert.Equal(1, rig.PendingCount);

        // Back in range: it goes, on the next attempt, with nobody re-typing it.
        sink.Up = true;
        Assert.Equal(1, await rig.SyncPendingAsync());
        Assert.Equal(0, rig.PendingCount);
        Assert.Equal(r.Note.Id, Assert.Single(sink.Received).Note.Id);
    }

    #endregion

    // ══ Hybrid · connected, and it still doesn't go ══════════════════════════
    #region Hybrid

    [Fact]
    public async Task Hybrid_online_sends_only_the_masked_text()
    {
        var rig = new Rig();

        var r = await rig.LogAsync(DeviceReplies.SeedNote, DemoMode.Hybrid);

        // The counterpart to the cloud-only case: same panel, opposite content.
        Assert.DoesNotContain("RX-7", r.PayloadSent);
        Assert.DoesNotContain("94.2%", r.PayloadSent);
        Assert.DoesNotContain("4.5s", r.PayloadSent);
        Assert.Contains("⟦R1⟧", r.PayloadSent);
        Assert.Equal("policy: fab IP detected", r.Reason);
        Assert.Equal("T1→T3", r.Path);

        // and the vendor was handed exactly that, not a re-derivation of it
        Assert.Equal(r.PayloadSent, Assert.Single(rig.VendorSaw));
    }

    [Fact]
    public async Task The_two_walls_have_different_rules_in_the_same_breath()
    {
        // 🔍 One note, written once, going to two destinations at the same moment
        // under two different rules:
        //
        //   the fab's own server, same jurisdiction → UNREDACTED
        //   the vendor, across the border           → MASKED
        //
        // Redaction is a border control. There is exactly one border.
        var rig = new Rig();

        var r = await rig.LogAsync(DeviceReplies.SeedNote, DemoMode.Hybrid);

        Assert.Contains("RX-7", Assert.Single(rig.HubSaw));       // in full, to our own server
        Assert.Contains("⟦R1⟧", Assert.Single(rig.VendorSaw));    // masked, across the border
        Assert.DoesNotContain("RX-7", rig.VendorSaw[0]);
        Assert.Equal(r.PayloadSent, rig.VendorSaw[0]);
    }

    [Fact]
    public async Task The_network_is_fine_and_it_still_does_not_go()
    {
        // The offline tests are the network deciding. This is POLICY deciding,
        // with the network perfectly healthy.
        var rig = new Rig(online: true);
        Assert.True(rig.Net.CloudReachable);

        var r = await rig.LogAsync(DeviceReplies.SeedNote, DemoMode.Hybrid);

        Assert.DoesNotContain("RX-7", r.PayloadSent);
    }

    [Fact]
    public async Task The_unmasked_values_are_restored_locally()
    {
        var rig = new Rig();

        var r = await rig.LogAsync(DeviceReplies.SeedNote, DemoMode.Hybrid);

        // The technician reads real numbers; the vendor never saw them.
        Assert.Contains("RX-7", r.Note.Polished);
        Assert.Contains("94.2%", r.Note.Polished);
        Assert.DoesNotContain("⟦R", r.Note.Polished);
    }

    [Fact]
    public async Task A_note_with_no_fab_ip_is_allowed_through_whole()
    {
        // Proves the masking is a decision about the NOTE, not a blanket rule —
        // otherwise "policy decides" would just be "policy blocks".
        var rig = new Rig(
            device: (system, _) => system.StartsWith("You flag proprietary") ? "[]"
                : system.StartsWith("You extract structured data")
                    ? """{"toolId":"ETCH-01","chamber":"A","metric":"interlock","value":"OK","severity":"Routine"}"""
                : system.StartsWith("Rewrite this semiconductor") ? DeviceReplies.BenignNote
                : "");

        var r = await rig.LogAsync(DeviceReplies.BenignNote, DemoMode.Hybrid);

        Assert.Empty(r.Note.Spans);
        Assert.Equal(DeviceReplies.BenignNote, r.PayloadSent);
        Assert.Equal("no fab IP in this note", r.Reason);
    }

    [Fact]
    public async Task A_vendor_failure_mid_route_degrades_without_losing_the_note()
    {
        // Connected, policy permits, and Azure 502s. The technician still walks
        // away with a structured, classified note.
        var rig = new Rig(cloud: new OfflineChat("502 Bad Gateway"));

        var r = await rig.LogAsync(DeviceReplies.SeedNote, DemoMode.Hybrid);

        Assert.True(r.Degraded);
        Assert.False(r.Failed);                 // degraded is not failed
        Assert.Equal("vendor unreachable", r.Reason);
        Assert.Equal("", r.PayloadSent);        // nothing made it across
        Assert.NotNull(r.Output);               // the technician still got their note
        Assert.Equal("ETCH-03", r.Note.ToolId);
        Assert.Single(rig.HubSaw);              // and the fab's own server has it
    }

    #endregion

    // ══ The ledger ═══════════════════════════════════════════════════════════
    #region Ledger

    [Fact]
    public async Task Every_route_writes_exactly_one_ledger_entry()
    {
        var rig = new Rig();

        await rig.LogAsync(DeviceReplies.SeedNote, DemoMode.CloudOnly);
        await rig.LogAsync(DeviceReplies.SeedNote, DemoMode.Hybrid);
        await rig.LogAsync(DeviceReplies.SeedNote, DemoMode.EdgeOnly);
        await rig.LogAsync(DeviceReplies.SeedNote, DemoMode.CloudOnly);

        Assert.Equal(4, rig.Ledger.Entries.Count);
    }

    [Fact]
    public async Task The_table_tells_the_whole_story_without_narration()
    {
        var online = new Rig();
        await online.LogAsync(DeviceReplies.SeedNote, DemoMode.CloudOnly);     // the leak
        await online.LogAsync(DeviceReplies.SeedNote, DemoMode.Hybrid);        // masked
        await online.LogAsync(DeviceReplies.SeedNote, DemoMode.EdgeOnly);      // nothing

        var rows = online.Ledger.Entries;

        Assert.Equal("T3", rows[0].Path);
        Assert.Contains("RX-7", rows[0].PayloadSent);
        Assert.False(rows[0].NothingLeftTheDevice);

        Assert.Equal("T1→T3", rows[1].Path);
        Assert.Contains("⟦R1⟧", rows[1].PayloadSent);
        Assert.DoesNotContain("RX-7", rows[1].PayloadSent);

        Assert.Equal("T1", rows[2].Path);
        Assert.True(rows[2].NothingLeftTheDevice);

        // The fourth row only exists when the wire is gone — and it is the one
        // that says a cloud-only architecture has nowhere to fall back to.
        var offline = new Rig(cloud: new OfflineChat());
        await offline.LogAsync(DeviceReplies.SeedNote, DemoMode.CloudOnly);

        var dead = Assert.Single(offline.Ledger.Entries);
        Assert.Equal("T3 ✖", dead.Path);
        Assert.True(dead.NothingLeftTheDevice);       // nothing left, but nothing worked either
        Assert.Equal("vendor unreachable", dead.Reason);
    }

    [Fact]
    public async Task The_ledger_records_the_literal_payload_not_a_hash()
    {
        // An auditor's question is "show me what left", not "prove it was hashed".
        // The panel renders this string directly, so it has to BE the string.
        var rig = new Rig();

        var r = await rig.LogAsync(DeviceReplies.SeedNote, DemoMode.CloudOnly);

        Assert.Equal(r.PayloadSent, Assert.Single(rig.Ledger.For(r.Note.Id)).PayloadSent);
    }

    [Fact]
    public async Task Every_entry_names_a_reason_a_human_can_read()
    {
        var rig = new Rig();
        await rig.LogAsync(DeviceReplies.SeedNote, DemoMode.CloudOnly);
        await rig.LogAsync(DeviceReplies.SeedNote, DemoMode.Hybrid);
        await rig.LogAsync(DeviceReplies.SeedNote, DemoMode.EdgeOnly);

        Assert.All(rig.Ledger.Entries, e =>
        {
            Assert.False(string.IsNullOrWhiteSpace(e.Reason));
            Assert.False(string.IsNullOrWhiteSpace(e.Model));
            Assert.False(string.IsNullOrWhiteSpace(e.Path));
        });
    }

    #endregion

    // ══ The shift report · the hybrid flow, and a different job ══════════════
    #region ShiftReport

    static IReadOnlyList<FabNote> AShift() =>
    [
        FabNote.New(DeviceReplies.SeedNote, "K. Nagy", "PAD-07", "CR-2") with
        {
            Cleaned = "ETCH-03 chamber B uniformity measured 94.2% on recipe RX-7 with a 4.5s ramp, off spec.",
            ToolId = "ETCH-03", Chamber = "B", Metric = "uniformity", Value = "94.2%",
            Severity = Severity.Escalate, Tags = ["excursion"],
        },
        FabNote.New(DeviceReplies.BenignNote, "K. Nagy", "PAD-07", "CR-2") with
        {
            Cleaned = DeviceReplies.BenignNote,
            ToolId = "ETCH-01", Chamber = "A", Metric = "interlock",
            Severity = Severity.Routine, Tags = ["routine-check"],
        },
    ];

    [Fact]
    public async Task The_report_masks_fab_ip_before_anything_crosses_and_restores_it_after()
    {
        // ⭐ The hybrid flow's whole claim, in one test. Connected the entire time
        // — nothing is forcing anyone's hand — and the proprietary values still do
        // not cross.
        var rig = new Rig();

        var report = await rig.Reporter.CompileAsync(
            AShift(), DemoMode.Hybrid, "K. Nagy", "Night 2026-09-12", "PAD-07", "CR-2");

        Assert.True(report.CloudUsed);

        // What crossed: masked.
        Assert.Contains("⟦R1⟧", report.PayloadSent);
        Assert.DoesNotContain("RX-7", report.PayloadSent);
        Assert.DoesNotContain("94.2%", report.PayloadSent);

        // What the vendor was handed is exactly that — not a re-derivation.
        Assert.Contains("⟦R1⟧", Assert.Single(rig.VendorSaw));
        Assert.DoesNotContain("RX-7", rig.VendorSaw[0]);

        // What the technician reads: the real values, restored locally.
        Assert.Contains("RX-7", report.Final);
    }

    [Fact]
    public async Task The_reports_facts_are_computed_on_the_device_and_never_sent()
    {
        // The half of the report that never leaves. Grouping and counting are
        // arithmetic — no model involved, so they cannot have a bad day.
        var rig = new Rig();

        var report = await rig.Reporter.CompileAsync(
            AShift(), DemoMode.Hybrid, "K. Nagy", "Night 2026-09-12", "PAD-07", "CR-2");

        Assert.Equal(2, report.Facts.Count);

        // Worst severity first, so the thing needing action leads.
        Assert.Equal("ETCH-03", report.Facts[0].ToolId);
        Assert.Equal(Severity.Escalate, report.Facts[0].Severity);

        // And the facts table itself is in no payload anywhere.
        Assert.All(rig.VendorSaw, sent => Assert.DoesNotContain("94.2%", sent));
    }

    [Fact]
    public async Task A_local_only_posture_compiles_the_report_without_the_cloud_at_all()
    {
        // Same task, same code, one config value. The facts are complete either
        // way — what is lost is the prose, never the report.
        var rig = new Rig();

        var report = await rig.Reporter.CompileAsync(
            AShift(), DemoMode.EdgeOnly, "K. Nagy", "Night 2026-09-12", "PAD-07", "CR-2");

        Assert.False(report.CloudUsed);
        Assert.Equal("", report.PayloadSent);
        Assert.Empty(rig.VendorSaw);
        Assert.NotEmpty(report.Facts);
        Assert.Contains("RX-7", report.Final);      // still complete, locally
    }

    [Fact]
    public async Task A_report_that_cannot_be_submitted_is_still_kept_locally()
    {
        // The safety net: the technician keeps a local copy. That is what makes a
        // failed submission survivable rather than lossy — so the report object
        // must be complete and usable even when the server never saw it.
        var rig = new Rig(hubUp: false);

        var report = await rig.Reporter.CompileAsync(
            AShift(), DemoMode.Hybrid, "K. Nagy", "Night 2026-09-12", "PAD-07", "CR-2");

        Assert.False(report.Submitted);
        Assert.NotEmpty(report.Facts);
        Assert.Contains("RX-7", report.Final);
    }

    [Fact]
    public async Task The_report_is_written_to_the_decision_log_like_everything_else()
    {
        var rig = new Rig();

        await rig.Reporter.CompileAsync(
            AShift(), DemoMode.Hybrid, "K. Nagy", "Night 2026-09-12", "PAD-07", "CR-2");

        var row = Assert.Single(rig.Ledger.Entries);
        Assert.StartsWith("shift report", row.Reason);
        Assert.Equal("device→cloud", row.Path);
        Assert.Contains("⟦R1⟧", row.PayloadSent);
        Assert.DoesNotContain("RX-7", row.PayloadSent);
    }

    #endregion
}

/// <summary>An <see cref="IAirlock"/> a test can flip. The cleanroom toggle, minus the UI.</summary>
sealed class TestAirlock : IAirlock
{
    /// <summary>False = the pad is inside the cleanroom. A position, not a network state.</summary>
    public bool PadOnNetwork { get; set; } = true;
}

/// <summary>Bottom of the pipeline: answers 200 to anything that reaches it.</summary>
sealed class StubHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
        Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
}
