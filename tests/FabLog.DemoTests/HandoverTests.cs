using FabLog.TrustHub;
using Microsoft.Extensions.AI;

namespace FabLog.DemoTests;

/// <summary>
/// The shift handover brief. Facts are computed by the hub before any model call, the
/// model proposes a ranking, and the hub validates it against what it actually sent.
/// <para>
/// ⚠️ The digest asserted here is a SECOND projection across wall 2, next to
/// <see cref="HubStore.AsDigest"/>. It is pinned as rigidly as the first: widening it
/// has to be a conscious edit to this file.
/// </para>
/// </summary>
public class HandoverTests
{
    /// <summary>Just after the newest seeded note (11:20Z), the same relation the day-shifted live seed keeps.</summary>
    static readonly DateTimeOffset AsOf = DateTimeOffset.Parse("2026-09-12T12:20:00Z");

    static string SeedPath => Path.Combine(AppContext.BaseDirectory, "Data", "hub-notes.json");
    static HandoverContext Context() =>
        HandoverContext.Load(Path.Combine(AppContext.BaseDirectory, "Data", "handover-context.json"));

    static HubStore Seeded() => new(SeedPath);

    static HandoverBrain Brain(IChatClient cloud, HubStore? store = null, HubFeatures? features = null, TimeSpan? deadline = null) =>
        new(store ?? Seeded(), Context(), cloud, "gpt-5.4-mini",
            features ?? new HubFeatures { ShiftHandover = true }, deadline, () => AsOf);

    static HandoverBrain Brain(Func<string, string, string> reply, HubStore? store = null, HubFeatures? features = null) =>
        Brain(new FakeChat(reply, "gpt-5.4-mini"), store, features);

    static HandoverSnapshot Snapshot(HubStore? store = null) => HandoverFacts.Build(store ?? Seeded(), Context(), AsOf);

    // ── one check as the model would write it ───────────────────────────────
    static string C(string tool, string chamber, string ids, string action = "Inspect the chamber before loading lots.",
        string why = "Drift is worsening.", string confidence = "high", string change = "a clean first wafer") =>
        $$"""{"rank":1,"tool":"{{tool}}","chamber":"{{chamber}}","action":"{{action}}","why":"{{why}}","evidence":[{{ids}}],"confidence":"{{confidence}}","would_change_if":"{{change}}"}""";

    static Func<string, string, string> Says(params string[] checks) =>
        (_, _) => $$"""{"checks":[{{string.Join(",", checks)}}]}""";

    static string E02A => C("ETCH-02", "A", "\"n1\",\"pm1\"");
    static string E03B => C("ETCH-03", "B", "\"n3\",\"n4\",\"wip2\"");

    // ══ drift, trend, PM ═════════════════════════════════════════════════════

    static ChamberContext Ctx(string worse, params double[] means) =>
        new("T-1", "A", "m", 10.0, 0.5, worse, means, 10, 30, null, 1);

    [Theory]
    [InlineData("low", 9.0, -2.0)]
    [InlineData("high", 11.0, 2.0)]
    [InlineData("high", 10.0, 0.0)]
    [InlineData("low", 9.67, -0.7)]        // (9.67-10)/0.5 = -0.66 → -0.7
    [InlineData("high", 10.74, 1.5)]       // 1.48 → 1.5
    public void Drift_is_latest_mean_minus_baseline_over_sigma_to_one_decimal(string worse, double latest, double expected) =>
        Assert.Equal(expected, HandoverFacts.Drift(Ctx(worse, 10, 10, latest)));

    [Theory]
    [InlineData("low", 10.0, 9.5, 9.0, "worsening")]
    [InlineData("high", 10.0, 10.5, 11.0, "worsening")]
    [InlineData("low", 9.0, 9.5, 10.0, "improving")]
    [InlineData("high", 11.0, 10.5, 10.0, "improving")]
    [InlineData("high", 10.0, 9.0, 10.0, "flat")]     // down then up
    [InlineData("high", 10.0, 10.0, 11.0, "flat")]    // a pause is not a trend
    public void Trend_needs_both_steps_to_move_the_same_way(string worse, double a, double b, double c, string expected) =>
        Assert.Equal(expected, HandoverFacts.Trend(Ctx(worse, a, b, c)));

    [Fact]
    public void Fewer_than_three_shifts_is_flat() =>
        Assert.Equal("flat", HandoverFacts.Trend(Ctx("low", 9.0, 8.0)));

    [Fact]
    public void Pm_state_reads_overdue_and_due_in_from_days_since_and_interval()
    {
        var lines = Snapshot().Evidence.Where(e => e.Id.StartsWith("pm")).ToDictionary(e => e.Tool, e => e.Line);

        Assert.Equal("last PM 49d ago | interval 42d | overdue by 7d", lines["ETCH-02"]);
        Assert.Equal("last PM 21d ago | interval 60d | due in 39d | last clean 6d ago", lines["ETCH-03"]);
        Assert.Equal("last PM 27d ago | interval 60d | due in 33d | last clean 1d ago", lines["ETCH-05"]);
        Assert.Equal("last PM 14d ago | interval 30d | due in 16d", lines["LITHO-01"]);
    }

    // ══ the digest: what crosses wall 2 ══════════════════════════════════════

    [Fact]
    public void The_seed_digest_is_pinned_exactly()
    {
        // Pinned on purpose. If a line, a column or a number changes, this fails first.
        const string expected = """
            # SHIFT HANDOVER DIGEST — derived signals only; no note text, no raw process values
            n1   | ETCH-02 A  | vacuum | drift=+2.7σ | trend=worsening (3 shifts)
            n2   | ETCH-02 A  | notes | non-routine=1 | technicians=1 | worst=Watch
            n3   | ETCH-03 B  | uniformity | drift=-3.3σ | trend=worsening (3 shifts)
            n4   | ETCH-03 B  | notes | non-routine=2 | technicians=2 | worst=Watch
            n5   | ETCH-05 C  | throughput | drift=-2.0σ | trend=worsening (3 shifts)
            n6   | ETCH-05 C  | notes | non-routine=1 | technicians=1 | worst=Watch
            n7   | LITHO-01 A | overlay | drift=+2.7σ | trend=flat (3 shifts)
            n8   | LITHO-01 A | notes | non-routine=1 | technicians=1 | worst=Watch
            pm1  | ETCH-02 A  | last PM 49d ago | interval 42d | overdue by 7d
            pm2  | ETCH-03 B  | last PM 21d ago | interval 60d | due in 39d | last clean 6d ago
            pm3  | ETCH-05 C  | last PM 27d ago | interval 60d | due in 33d | last clean 1d ago
            pm4  | LITHO-01 A | last PM 14d ago | interval 30d | due in 16d
            wip1 | ETCH-02 A  | next shift lots=6
            wip2 | ETCH-03 B  | next shift lots=9
            wip3 | ETCH-05 C  | next shift lots=4
            wip4 | LITHO-01 A | next shift lots=7
            """;

        Assert.Equal(expected.ReplaceLineEndings("\n"), Snapshot().ToDigest());
    }

    [Fact]
    public void The_handover_digest_carries_no_note_text_name_tag_device_baseline_or_reading()
    {
        var store = Seeded();
        var digest = Snapshot(store).ToDigest();

        Assert.All(store.All, n =>
        {
            Assert.DoesNotContain(n.Text, digest);
            Assert.DoesNotContain(n.Author, digest);
            Assert.DoesNotContain(n.DeviceId, digest);
            foreach (var tag in n.ContextTags) Assert.DoesNotContain($"={tag}", digest, StringComparison.OrdinalIgnoreCase);
        });
        Assert.DoesNotContain("Farkas", digest);
        Assert.DoesNotContain("Szabó", digest);
        Assert.DoesNotContain("RX-7", digest);

        // Baselines and the shift readings themselves stay in the hub — only drift leaves.
        // (A standalone number: "-2.0σ" is a drift, not the baseline 2.0.)
        static bool Leaks(string digest, double v) => System.Text.RegularExpressions.Regex.IsMatch(
            digest, $@"(?<![\d.+\-]){System.Text.RegularExpressions.Regex.Escape(v.ToString("0.0#", System.Globalization.CultureInfo.InvariantCulture))}(?![\d.])");
        foreach (var c in Context().Chambers)
        {
            Assert.False(Leaks(digest, c.BaselineMean), $"baseline {c.BaselineMean} leaked");
            foreach (var m in c.ShiftMeans) Assert.False(Leaks(digest, m), $"reading {m} leaked");
        }
    }

    [Fact]
    public void Every_digest_line_starts_with_a_stable_evidence_id_and_ids_are_deterministic()
    {
        var a = Snapshot();
        var b = Snapshot();

        Assert.Equal(a.ToDigest(), b.ToDigest());
        Assert.All(a.ToDigest().Split('\n').Skip(1), l => Assert.Matches(@"^(n|pm|wip|x)\d+\s+\| ", l));
        Assert.Equal(a.Evidence.Count, a.Evidence.Select(e => e.Id).Distinct().Count());
    }

    [Fact]
    public void A_chamber_with_nothing_to_say_is_not_sent()
    {
        // ETCH-01 A is in the context (stable, PM due in 50 d) and has only a Routine note.
        var snap = Snapshot();

        Assert.DoesNotContain(snap.Chambers, c => c.Tool == "ETCH-01");
        Assert.DoesNotContain("ETCH-01", snap.ToDigest());
    }

    // ══ conflicting signals ══════════════════════════════════════════════════

    static HubNote RoutineVacuum(string author) => new(Guid.NewGuid(), author, "Morning 2026-09-12",
        "ETCH-02", "A", "vacuum", FabLog.Core.Severity.Routine, "vacuum looks steady after the PM ticket",
        DateTimeOffset.Parse("2026-09-12T10:00:00Z"));

    [Fact]
    public void Opposing_notes_from_different_technicians_are_a_conflict_in_labels_only()
    {
        var store = Seeded();                       // M. Farkas: Watch on ETCH-02 A
        store.Add(RoutineVacuum("T. Szabó"));

        var snap = Snapshot(store);

        var x = Assert.Single(snap.Evidence, e => e.Id.StartsWith("x"));
        Assert.Equal("conflicting signals | Watch vs Routine | 2 technicians", x.Line);
        Assert.Contains("x1   | ETCH-02 A  | conflicting signals | Watch vs Routine | 2 technicians", snap.ToDigest());
        Assert.DoesNotContain("steady", snap.ToDigest());
        Assert.True(snap.Chambers.Single(c => c.Tool == "ETCH-02").Conflict);
    }

    [Fact]
    public void One_technician_contradicting_themselves_is_not_a_conflict()
    {
        var store = Seeded();
        store.Add(RoutineVacuum("M. Farkas"));

        Assert.DoesNotContain(Snapshot(store).Evidence, e => e.Id.StartsWith("x"));
    }

    // ══ the hub's own ranking ════════════════════════════════════════════════

    [Fact]
    public void The_hub_ranking_on_the_seed_is_pinned()
    {
        var ranked = Snapshot().Ranked.Select(c => (c.Tool, c.Chamber, c.Score)).ToList();

        // drift pts + trend 10 + PM (25 overdue) + lots×2 + notes
        Assert.Equal(
            [("ETCH-02", "A", 79), ("ETCH-03", "B", 71), ("LITHO-01", "A", 46), ("ETCH-05", "C", 43)],
            ranked);
    }

    [Fact]
    public void An_escalate_note_lifts_the_hub_score()
    {
        var store = Seeded();
        store.Add(new HubNote(Guid.NewGuid(), "K. Nagy", "Night 2026-09-12", "ETCH-03", "B", "uniformity",
            FabLog.Core.Severity.Escalate, "off spec", DateTimeOffset.Parse("2026-09-12T11:30:00Z")));

        var b = Snapshot(store).Chambers.Single(c => c.Tool == "ETCH-03");

        Assert.Equal(15, b.NotePoints);
        Assert.Equal(76, b.Score);
    }

    // ══ the cloud path ═══════════════════════════════════════════════════════

    [Fact]
    public async Task A_valid_cloud_ranking_is_shown_with_the_hub_ranking_beside_it()
    {
        var cloud = new FakeChat(Says(E03B, E02A), "gpt-5.4-mini");
        var r = await Brain(cloud).BriefAsync();

        Assert.True(r.FromCloud);
        Assert.Equal("cloud · gpt-5.4-mini", r.SourceLabel);
        Assert.Equal(["ETCH-03", "ETCH-02"], r.Checks.Select(c => c.Tool));
        Assert.Equal([1, 2], r.Checks.Select(c => c.Rank));
        Assert.Empty(r.Rejections);

        // The comparison is always there, not only on fallback.
        Assert.Equal(4, r.HubRanking.Count);
        Assert.Equal("ETCH-02", r.HubRanking[0].Tool);
        Assert.Equal(79, r.HubRanking[0].Score);
        Assert.Equal(Snapshot().ToDigest(), r.Digest);
    }

    [Fact]
    public async Task The_cloud_is_handed_the_handover_digest_and_the_handover_prompt()
    {
        var cloud = new FakeChat(Says(E03B), "gpt-5.4-mini");
        var store = Seeded();
        await Brain(cloud, store).BriefAsync();

        var sent = Assert.Single(cloud.Received);
        Assert.Equal($"Digest:\n{Snapshot(store).ToDigest()}", sent);
        Assert.StartsWith("You advise a semiconductor fab shift supervisor", Assert.Single(cloud.Systems));
        Assert.All(store.All, n => Assert.DoesNotContain(n.Text, sent));
        Assert.DoesNotContain("RX-7", sent);
        Assert.DoesNotContain("94.5", sent);
    }

    [Fact]
    public async Task A_check_for_a_tool_that_is_not_in_the_digest_is_rejected_and_logged()
    {
        var r = await Brain(Says(E03B, C("ETCH-09", "C", "\"n1\",\"pm1\""))).BriefAsync();

        Assert.Equal(["ETCH-03"], r.Checks.Select(c => c.Tool));
        Assert.Contains(r.Rejections, l => l.Contains("ETCH-09") && l.Contains("not in the digest"));
    }

    [Fact]
    public async Task A_check_citing_an_evidence_id_that_was_never_sent_is_rejected()
    {
        var r = await Brain(Says(E02A, C("ETCH-03", "B", "\"n3\",\"n99\""))).BriefAsync();

        Assert.Equal(["ETCH-02"], r.Checks.Select(c => c.Tool));
        Assert.Contains(r.Rejections, l => l.Contains("n99") && l.Contains("not sent"));
    }

    [Fact]
    public async Task A_real_id_attached_to_the_wrong_chamber_is_rejected()
    {
        // n1 exists — it is ETCH-02's. Citing it for ETCH-03 is the plausible-looking lie.
        var r = await Brain(Says(E02A, C("ETCH-03", "B", "\"n3\",\"n1\""))).BriefAsync();

        Assert.Equal(["ETCH-02"], r.Checks.Select(c => c.Tool));
        Assert.Contains(r.Rejections, l => l.Contains("another chamber") && l.Contains("n1"));
    }

    [Fact]
    public async Task A_check_with_no_evidence_is_rejected()
    {
        var r = await Brain(Says(E02A, C("ETCH-03", "B", ""))).BriefAsync();

        Assert.Single(r.Checks);
        Assert.Contains(r.Rejections, l => l.Contains("cites no evidence"));
    }

    [Fact]
    public async Task A_sentence_with_an_invented_digit_is_removed_and_logged_but_the_check_survives()
    {
        var why = "Drift is worsening at +2.7σ. Expect 88 lots to be scrapped.";
        var r = await Brain(Says(C("ETCH-02", "A", "\"n1\",\"pm1\"", why: why))).BriefAsync();

        var c = Assert.Single(r.Checks);
        Assert.Equal("Drift is worsening at +2.7σ.", c.Why);
        Assert.Contains(r.Rejections, l => l.Contains("'88'") && l.Contains("why"));
    }

    [Fact]
    public async Task Digits_the_hub_supplied_tool_ids_and_evidence_ids_are_not_invented()
    {
        // 2.7, 49, 42, 7 and 6 are all in the digest; "ETCH-02", "n1", "pm1" are labels, not numbers.
        var action = "Do the PM on ETCH-02 chamber A, overdue by 7d, before the 6 lots (see n1 and pm1).";
        var r = await Brain(Says(C("ETCH-02", "A", "\"n1\",\"pm1\"", action: action))).BriefAsync();

        Assert.Equal(action, Assert.Single(r.Checks).Action);
        Assert.Empty(r.Rejections);
    }

    [Fact]
    public async Task A_number_that_only_appears_for_another_chamber_still_counts_as_supplied()
    {
        // Documented limit: the check is "was this digit in the digest", not "is it this chamber's".
        var r = await Brain(Says(C("ETCH-02", "A", "\"n1\",\"pm1\"", why: "Nine lots are exposed at 9 lots."))).BriefAsync();

        Assert.Single(r.Checks);
    }

    [Fact]
    public async Task When_stripping_leaves_nothing_the_check_is_rejected()
    {
        var r = await Brain(Says(E03B, C("ETCH-02", "A", "\"n1\",\"pm1\"", why: "It will fail within 77 minutes."))).BriefAsync();

        Assert.Equal(["ETCH-03"], r.Checks.Select(c => c.Tool));
        Assert.Contains(r.Rejections, l => l.Contains("nothing left"));
    }

    [Fact]
    public async Task An_unrecognised_confidence_is_shown_as_low_and_logged()
    {
        var r = await Brain(Says(C("ETCH-02", "A", "\"n1\",\"pm1\"", confidence: "certain"))).BriefAsync();

        Assert.Equal("low", Assert.Single(r.Checks).Confidence);
        Assert.Contains(r.Rejections, l => l.Contains("certain"));
    }

    [Fact]
    public async Task More_than_three_checks_are_cut_to_three_and_the_hub_renumbers_the_ranks()
    {
        var r = await Brain(Says(E02A, E03B,
            C("ETCH-05", "C", "\"n5\",\"wip3\""), C("LITHO-01", "A", "\"n7\",\"wip4\""))).BriefAsync();

        Assert.Equal(3, r.Checks.Count);
        Assert.Equal([1, 2, 3], r.Checks.Select(c => c.Rank));
        Assert.Contains(r.Rejections, l => l.Contains("dropped"));
    }

    [Fact]
    public async Task A_partial_pass_stays_cloud_and_shows_what_was_rejected()
    {
        var r = await Brain(Says(E02A, C("ETCH-03", "B", "\"zz9\""))).BriefAsync();

        Assert.True(r.FromCloud);
        Assert.Single(r.Checks);
        Assert.NotEmpty(r.Rejections);
        Assert.Null(r.FallbackReason);
    }

    // ══ the fallback path ════════════════════════════════════════════════════

    static void AssertHubRanking(HandoverResult r)
    {
        Assert.Equal("hub", r.Source);
        Assert.Equal("hub ranking (no cloud)", r.SourceLabel);
        Assert.Equal(["ETCH-02", "ETCH-03", "LITHO-01"], r.Checks.Select(c => c.Tool));
        Assert.Equal(["high", "high", "medium"], r.Checks.Select(c => c.Confidence));
        Assert.All(r.Checks, c =>
        {
            Assert.NotEmpty(c.Evidence);
            Assert.All(c.Evidence, id => Assert.Contains(r.Evidence, e => e.Id == id && e.Tool == c.Tool && e.Chamber == c.Chamber));
        });
        Assert.NotNull(r.FallbackReason);
    }

    [Fact]
    public async Task A_malformed_reply_falls_back_to_the_hub_ranking()
    {
        var r = await Brain((_, _) => "Certainly! Here is my ranking of the checks.").BriefAsync();

        AssertHubRanking(r);
        Assert.False(r.ReplyParsed);
        Assert.Contains("not valid JSON", r.FallbackReason);
    }

    [Fact]
    public async Task A_fenced_reply_is_still_parsed()
    {
        var r = await Brain((_, _) => "```json\n" + Says(E02A)("", "") + "\n```").BriefAsync();

        Assert.True(r.FromCloud);
        Assert.True(r.ReplyParsed);
    }

    [Fact]
    public async Task Every_check_rejected_falls_back_to_the_hub_ranking()
    {
        var r = await Brain(Says(C("ETCH-09", "C", "\"n1\""), C("ETCH-03", "B", "\"n99\""))).BriefAsync();

        AssertHubRanking(r);
        Assert.True(r.ReplyParsed);
        Assert.Equal(2, r.Rejections.Count);
        Assert.Contains("rejected", r.FallbackReason);
    }

    [Fact]
    public async Task A_vendor_that_cannot_be_reached_falls_back_to_the_hub_ranking()
    {
        var r = await Brain(new OfflineChat()).BriefAsync();

        AssertHubRanking(r);
        Assert.Contains("unreachable", r.FallbackReason);
    }

    [Fact]
    public async Task An_unconfigured_cloud_falls_back_to_the_hub_ranking()
    {
        var r = await Brain(new NotConfiguredChatClient()).BriefAsync();

        AssertHubRanking(r);
        Assert.False(string.IsNullOrWhiteSpace(r.FallbackReason));
    }

    sealed class HangingChat : IChatClient
    {
        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new ChatResponse();
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [Fact]
    public async Task A_cloud_that_never_answers_is_cut_off_by_the_hubs_own_deadline()
    {
        var r = await Brain(new HangingChat(), deadline: TimeSpan.FromMilliseconds(150)).BriefAsync();

        AssertHubRanking(r);
        Assert.Contains("did not answer", r.FallbackReason);
    }

    [Fact]
    public async Task A_caller_that_cancels_is_not_mistaken_for_a_slow_cloud()
    {
        using var cts = new CancellationTokenSource(100);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Brain(new HangingChat(), deadline: TimeSpan.FromSeconds(30)).BriefAsync(cts.Token));
    }

    [Fact]
    public async Task The_stage_switch_forces_the_hub_ranking_without_calling_the_cloud()
    {
        var cloud = new FakeChat(Says(E03B), "gpt-5.4-mini");
        var r = await Brain(cloud, features: new HubFeatures { ShiftHandover = true, ForceHandoverFallback = true }).BriefAsync();

        Assert.Equal(0, cloud.Calls);
        AssertHubRanking(r);
        Assert.Contains("ForceHandoverFallback", r.FallbackReason);
    }

    // ══ switch off = no change ═══════════════════════════════════════════════

    [Fact]
    public async Task With_the_switch_off_the_brain_does_nothing_and_never_calls_the_cloud()
    {
        var cloud = new FakeChat(Says(E03B), "gpt-5.4-mini");
        var brain = Brain(cloud, features: new HubFeatures());

        var r = await brain.BriefAsync();

        Assert.False(brain.Enabled);
        Assert.Equal("disabled", r.Source);
        Assert.Empty(r.Checks);
        Assert.Equal(0, cloud.Calls);
        Assert.Null(brain.Last);
    }

    [Fact]
    public async Task The_existing_fleet_question_and_digest_are_the_same_with_the_feature_on_or_off()
    {
        // Same store, same fake: building and running the handover brain must not disturb
        // HubBrain, the 8-column AsDigest, or the store.
        var store = Seeded();
        var before = store.AsDigest();
        var trendBefore = await new HubBrain(store, new FakeChat(CloudReplies.NoTrend), "gpt-4o").AnalyseAsync("anything odd about ETCH-03 today?");

        await Brain(Says(E03B), store).BriefAsync();

        var trendAfter = await new HubBrain(store, new FakeChat(CloudReplies.NoTrend), "gpt-4o").AnalyseAsync("anything odd about ETCH-03 today?");
        Assert.Equal(before, store.AsDigest());
        Assert.Equal(12, store.Count);
        Assert.Equal(trendBefore.Finding, trendAfter.Finding);
        Assert.All(store.AsDigest().Split('\n'), l => Assert.Equal(8, l.Split(" | ").Length));
    }

    [Fact]
    public void Both_feature_switches_default_to_off()
    {
        var f = new HubFeatures();

        Assert.False(f.ShiftHandover);
        Assert.False(f.ForceHandoverFallback);
    }

    [Fact]
    public async Task Reset_clears_the_last_brief()
    {
        var brain = Brain(Says(E03B));
        await brain.BriefAsync();
        Assert.NotNull(brain.Last);

        brain.Reset();

        Assert.Null(brain.Last);
    }
}
