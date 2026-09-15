using System.Text.Json;
using FabLog.TrustHub;
using Microsoft.Extensions.AI;

namespace FabLog.DemoTests;

/// <summary>
/// The fleet-wide trend — the question no single device could answer, and the
/// one place where a plausible-looking wrong answer would be worse than a
/// visible failure.
/// <para>
/// ⚠️ The hub holds every technician's note <i>in full</i>, because it is the
/// fab's own server inside the fab's own airgap. The border it defends is the
/// one in front of the <b>vendor</b>, and that border is
/// <see cref="HubStore.AsDigest"/>.
/// </para>
/// </summary>
public class TrendTests
{
    /// <summary>The seed file is linked into this project's output; see the .csproj note.</summary>
    static HubStore Seeded() => new(Path.Combine(AppContext.BaseDirectory, "Data", "hub-notes.json"));

    static HubBrain Brain(HubStore store, Func<string, string, string>? cloud = null) =>
        new(store, new FakeChat(cloud ?? CloudReplies.NoTrend, "gpt-4o"), "gpt-4o");

    /// <summary>
    /// K. Nagy's note, as it arrives over <c>POST /sync</c> — in <b>full</b>.
    /// <para>
    /// No ⟦R1⟧ placeholders, and their absence is the trust boundary in one
    /// object: the device does not redact to its own employer's server.
    /// </para>
    /// </summary>
    static HubNote ThirdComplaint(Guid? id = null) => new(
        id ?? Guid.NewGuid(), "K. Nagy", "Night 2026-09-12", "ETCH-03", "B", "uniformity",
        Severity.Escalate,
        "ETCH-03 chamber B uniformity 94.2% on recipe RX-7, ramp 4.5s — off spec.",
        DateTimeOffset.Parse("2026-09-12T21:30:00Z"));

    // ══ the corpus is load-bearing, not incidental test data ═════════════════

    [Fact]
    public void The_hub_starts_with_twelve_notes_and_exactly_two_complaints()
    {
        // If either number drifts, there is no trend left to find. Pin both.
        var store = Seeded();

        Assert.Equal(12, store.SeedCount);
        Assert.Equal(12, store.Count);

        var etch03 = store.For("ETCH-03").Where(n => n.Severity is not Severity.Routine).ToList();
        Assert.Equal(2, etch03.Count);
        Assert.Equal(2, etch03.Select(n => n.Author).Distinct().Count());
    }

    [Fact]
    public void The_hub_holds_the_real_text_because_it_is_inside_the_airgap()
    {
        // 🏢 The seeded notes carry recipe names and yield figures verbatim —
        // exactly the values the device masks before talking to the VENDOR.
        // Different wall, different rule. You do not redact to yourself.
        var notes = Seeded().All;

        Assert.All(notes, n => Assert.DoesNotContain("⟦", n.Text));
        Assert.Contains(notes, n => n.Text.Contains("RX-7"));
        Assert.Contains(notes, n => n.Text.Contains("94.8%"));
    }

    // ══ wall 2 · what the hub gives away ═════════════════════════════════════
    #region wall 2

    [Fact]
    public void Nothing_the_hub_sends_to_the_cloud_contains_a_note_body()
    {
        // 🔒 The assertion wall 2 exists for, and the one to run if anything
        // about the hub changes.
        //
        // The hub is the richest store in the system — every technician's note,
        // in full, unmasked — and it sits one hop from a vendor. It also has no
        // local model, so it cannot redact prose. So it does not send prose. The
        // digest is columns the hub extracted itself, and the note body is simply
        // not a thing it can project.
        var store = Seeded();
        store.Add(ThirdComplaint());

        var digest = store.AsDigest();

        // Not one note body, in whole or in the distinctive part.
        Assert.All(store.All, n => Assert.DoesNotContain(n.Text, digest));
        Assert.DoesNotContain("RX-7", digest);
        Assert.DoesNotContain("94.2%", digest);
        Assert.DoesNotContain("4.5s", digest);
        Assert.DoesNotContain("second time this week", digest);

        // Not even a full name — the vendor counts distinct technicians, it does
        // not need to know which humans they are.
        Assert.DoesNotContain("Farkas", digest);
        Assert.DoesNotContain("Szabó", digest);
        Assert.Contains("tech=MF", digest);
    }

    [Fact]
    public void The_digest_is_one_line_per_note_and_eight_columns_wide()
    {
        // Structural, and deliberately rigid: a projection you cannot widen is a
        // control. If someone adds a ninth column holding a snippet "for context",
        // this fails before the promise does.
        //
        // ⚠️ The count is deliberately asserted, not derived: widening the
        // projection has to be a conscious edit to this test, never a side
        // effect. The cleanroom column earns its place — a pattern confined to
        // one cleanroom is a different finding, with a different response, from
        // one that spans two — and it is a closed set of room labels, not prose.
        var store = Seeded();

        var lines = store.AsDigest().Split('\n');

        Assert.Equal(store.Count, lines.Length);
        Assert.All(lines, l =>
        {
            var cols = l.Split(" | ");
            Assert.Equal(8, cols.Length);
            Assert.StartsWith("tech=", cols[1]);
            Assert.StartsWith("room=", cols[3]);
            Assert.StartsWith("severity=", cols[7]);
        });
    }

    [Fact]
    public void The_digest_carries_no_free_form_text_at_all()
    {
        // The claim the whole projection exists to make, asserted directly rather
        // than inferred from the column count: not one word anybody wrote crosses
        // the border. Note bodies and the technicians' own context tags are both
        // free-form, and both stay.
        var store = Seeded();
        var digest = store.AsDigest();

        foreach (var note in store.All)
        {
            Assert.DoesNotContain(note.Text, digest, StringComparison.OrdinalIgnoreCase);
            foreach (var tag in note.ContextTags)
                Assert.DoesNotContain($"={tag}", digest, StringComparison.OrdinalIgnoreCase);
        }

        // …and the author's full name never crosses either — initials are enough
        // to count distinct people, which is all the model is asked to do.
        Assert.DoesNotContain("Farkas", digest);
        Assert.DoesNotContain("Szabó", digest);
    }

    [Fact]
    public void A_field_the_device_could_not_extract_reads_as_a_dash_not_as_prose()
    {
        // A note can still reach the hub with a blank ToolId — extraction can
        // fail even when it runs (a garbled reply, a timeout) and falls back to
        // an empty field rather than guessing. The tempting fix is to fall back
        // to the note text for that column, which would put a sentence across
        // wall 2 through a side door. "-" instead.
        var store = Seeded();
        store.Add(new HubNote(Guid.NewGuid(), "K. Nagy", "Night 2026-09-12", "", "", "",
            Severity.Routine, "etch03 chmbr B unif 94.2% recipe RX-7 ramp 4.5s - off spec",
            DateTimeOffset.Parse("2026-09-12T22:00:00Z")));

        var last = store.AsDigest().Split('\n')[^1];

        Assert.Contains("tool=-", last);
        Assert.Contains("chamber=-", last);
        Assert.Contains("metric=-", last);
        Assert.DoesNotContain("94.2%", last);
    }

    #endregion

    // ══ two complaints · not yet a trend ═════════════════════════════════════

    [Fact]
    public async Task Two_technicians_is_a_coincidence_not_an_escalation()
    {
        var brain = Brain(Seeded());

        var r = await brain.AnalyseAsync("anything odd about ETCH-03 today?");

        Assert.NotNull(r.Finding);
        Assert.Equal("ETCH-03", r.Finding!.ToolId);
        Assert.Equal(2, r.Finding.Mentions);
        Assert.Equal(2, r.Finding.Technicians);
        Assert.Equal(Severity.Watch, r.Finding.Verdict);   // not yet actionable
        Assert.Equal(12, r.CorpusSize);
    }

    // ══ the third complaint · a trend ════════════════════════════════════════

    [Fact]
    public async Task The_third_note_turns_two_shrugs_into_an_escalation()
    {
        // ⚠️ The third complaint reaches the hub the moment it is written, airgap
        // or not, because the hub is inside the airgap. A lost uplink gates the
        // vendor's SENTENCE about the finding, never the finding itself — the
        // arithmetic below is the hub's own.
        var store = Seeded();
        var brain = Brain(store);
        store.Add(ThirdComplaint());

        var r = await brain.AnalyseAsync("anything odd about ETCH-03 today?");

        Assert.Equal(13, r.CorpusSize);
        Assert.NotNull(r.Finding);
        Assert.Equal(3, r.Finding!.Mentions);
        Assert.Equal(3, r.Finding.Technicians);
        Assert.Equal(3, r.Finding.Shifts);
        Assert.Equal(Severity.Escalate, r.Finding.Verdict);
    }

    [Fact]
    public async Task The_hub_owns_the_arithmetic_even_when_the_model_is_confident_and_wrong()
    {
        // The single failure mode this cannot survive: a supervisor acting on
        // "7 technicians" because a model said so. The model writes the sentence;
        // the counts are facts about rows the hub is holding.
        var store = Seeded();
        var brain = Brain(store, CloudReplies.ConfidentlyWrong);
        store.Add(ThirdComplaint());

        var r = await brain.AnalyseAsync("anything odd about ETCH-03 today?");

        Assert.Equal(3, r.Finding!.Mentions);        // model said 99
        Assert.Equal(3, r.Finding.Technicians);      // model said 7
        Assert.Equal(3, r.Finding.Shifts);           // model said 5
    }

    [Fact]
    public async Task A_malformed_cloud_reply_still_produces_the_finding()
    {
        var store = Seeded();
        var brain = Brain(store, (_, _) => "Certainly! Here is my analysis of the notes you shared.");
        store.Add(ThirdComplaint());

        var r = await brain.AnalyseAsync("anything odd about ETCH-03 today?");

        Assert.False(r.Failed);
        Assert.NotNull(r.Finding);
        Assert.Equal(Severity.Escalate, r.Finding!.Verdict);
        Assert.Contains("3 technicians", r.Finding.Finding);
    }

    [Fact]
    public async Task The_supervisor_sees_the_rows_behind_the_number_in_full()
    {
        // "3 technicians" is only credible if you can see which three — and the
        // supervisor is inside the fab, so they see the real notes. The vendor,
        // in the same breath, saw seven columns and no sentence.
        //
        // Both halves of that are on FabDesk, side by side, on purpose.
        var store = Seeded();
        var brain = Brain(store);
        store.Add(ThirdComplaint());

        var r = await brain.AnalyseAsync("anything odd about ETCH-03 today?");

        Assert.Equal(3, r.Sources.Count);
        Assert.Equal(["K. Nagy", "M. Farkas", "T. Szabó"],
            r.Sources.Select(n => n.Author).OrderBy(a => a, StringComparer.Ordinal));
        Assert.All(r.Sources, n =>
        {
            Assert.DoesNotContain("⟦", n.Text);
            Assert.Contains("RX-7", n.Text);
        });
    }

    [Fact]
    public async Task The_cloud_is_handed_the_digest_and_nothing_else()
    {
        // The insight is computed over data the vendor could read; the secret is
        // not in it. Put plainly: the vendor does the reasoning, and it has never
        // seen a sentence anyone wrote.
        var store = Seeded();
        var cloud = new FakeChat(CloudReplies.NoTrend, "gpt-4o");
        var brain = new HubBrain(store, cloud, "gpt-4o");
        store.Add(ThirdComplaint());

        await brain.AnalyseAsync("anything odd about ETCH-03 today?");

        var sent = Assert.Single(cloud.Received);
        Assert.Contains(store.AsDigest(), sent);
        Assert.Contains("severity=Escalate", sent);
        Assert.All(store.All, n => Assert.DoesNotContain(n.Text, sent));
        Assert.DoesNotContain("94.2%", sent);
        Assert.DoesNotContain("RX-7", sent);
        Assert.DoesNotContain("4.5s", sent);
    }

    // ══ the sync path that feeds it ══════════════════════════════════════════
    #region sync

    [Fact]
    public void A_retried_sync_does_not_double_count_a_complaint()
    {
        // The drain loop retries on failure. Two copies of K. Nagy's note would
        // read as "4 mentions, 3 technicians" — wrong in a way nothing flags.
        var store = Seeded();
        var id = Guid.NewGuid();

        Assert.True(store.Add(ThirdComplaint(id)));      // new
        Assert.False(store.Add(ThirdComplaint(id)));     // seen before
        Assert.Equal(13, store.Count);
    }

    [Fact]
    public void Re_syncing_a_note_replaces_it_so_the_richer_version_wins()
    {
        // 🔍 The reason Add is an upsert rather than an insert-if-absent.
        //
        // A note can sync with a BLANK tool and a Routine severity — e.g. extraction
        // failed and fell back rather than guessed. Re-sync the same note once it
        // carries ETCH-03 / Escalate, and the second sync must win. Rejecting it as
        // a duplicate would leave the blank row in place and break the trend
        // query's grouping without breaking anything visible.
        var store = Seeded();
        var id = Guid.NewGuid();

        store.Add(new HubNote(id, "K. Nagy", "Night 2026-09-12", "", "", "",
            Severity.Routine, "etch03 chmbr B unif off spec", DateTimeOffset.Parse("2026-09-12T21:30:00Z")));
        Assert.Equal(13, store.Count);
        Assert.Equal(2, store.For("ETCH-03").Count);

        store.Add(ThirdComplaint(id));

        Assert.Equal(13, store.Count);                    // still one row
        Assert.Equal(3, store.For("ETCH-03").Count);      // and now it counts
    }

    [Fact]
    public void Reset_puts_the_hub_back_to_twelve()
    {
        var store = Seeded();
        store.Add(ThirdComplaint());
        Assert.Equal(13, store.Count);

        store.Reset();

        Assert.Equal(12, store.Count);
        Assert.Equal(2, store.For("ETCH-03").Count);
    }

    [Fact]
    public void A_synced_note_round_trips_through_json_intact()
    {
        // The device POSTs this over /sync. Accented author names are the thing a
        // charset bug eats first, and "T. Szabó" is one of the three technicians
        // behind the trend.
        var note = ThirdComplaint() with { Author = "T. Szabó" };
        var json = JsonSerializer.Serialize(note, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var back = JsonSerializer.Deserialize<HubNote>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        Assert.Equal(note.Author, back.Author);
        Assert.Equal(note.Text, back.Text);
        Assert.Equal(Severity.Escalate, back.Severity);
    }

    #endregion

    // ══ T3 is wired, not faked ═══════════════════════════════════════════════

    [Fact]
    public async Task An_unconfigured_cloud_fails_loudly_instead_of_answering()
    {
        // A stub that quietly returns plausible text would make "the cloud tier
        // works" and "the cloud tier is merely wired" indistinguishable — and
        // every claim about what crosses the border rests on that difference.
        var brain = new HubBrain(Seeded(), new NotConfiguredChatClient(), "not-configured");

        var r = await brain.AnalyseAsync("anything odd about ETCH-03 today?");

        Assert.True(r.Failed);
        Assert.Null(r.Finding);
        Assert.Contains("appsettings.Development.json", r.Error);
    }

    [Theory]
    [InlineData("", "", "")]
    [InlineData("<your-foundry-endpoint>", "<deployment>", "<key>")]
    [InlineData("https://x.openai.azure.com/", "gpt-4o", "<paste-key-2-here>")]
    public void The_committed_placeholders_never_count_as_configured(string ep, string dep, string key) =>
        Assert.False(new T3Options { Endpoint = ep, Deployment = dep, ApiKey = key }.IsConfigured);

    [Fact]
    public void Real_values_do_count_as_configured() =>
        Assert.True(new T3Options
        {
            Endpoint = "https://fablog.openai.azure.com/",
            Deployment = "gpt-5.4-mini",
            ApiKey = "not-a-real-key",
        }.IsConfigured);

    /// <summary>
    /// Pasting the Foundry <i>project</i> endpoint makes every call come back
    /// <c>HTTP 400 "API version not supported"</c> — an error message pointing
    /// nowhere near its cause. The portal shows the project endpoint far more
    /// prominently than the data-plane one, so it is the easy mistake to make.
    /// <para>
    /// Both forms must now resolve to the resource root. Both host spellings are
    /// verified working against the live resource.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("https://r.services.ai.azure.com/api/projects/p", "https://r.services.ai.azure.com/")]
    [InlineData("https://r.services.ai.azure.com/", "https://r.services.ai.azure.com/")]
    [InlineData("https://r.services.ai.azure.com", "https://r.services.ai.azure.com/")]
    [InlineData("https://r.openai.azure.com/", "https://r.openai.azure.com/")]
    [InlineData("https://r.openai.azure.com/openai/deployments/x", "https://r.openai.azure.com/")]
    // The third shape the portal will hand you: the OpenAI-compatible surface.
    // Real, but for the OpenAI SDK (model in the body, no api-version). Called the
    // Azure way it 404s — verified. Stripping the path is right here too.
    [InlineData("https://r.openai.azure.com/openai/v1", "https://r.openai.azure.com/")]
    public void The_project_endpoint_is_accepted_and_reduced_to_the_resource_root(string configured, string expected) =>
        Assert.Equal(expected, new T3Options { Endpoint = configured }.ResourceEndpoint.ToString());
}

/// <summary>Canned T3 replies to <see cref="FabLog.Core.Prompts.Trend"/>.</summary>
public static class CloudReplies
{
    /// <summary>A well-formed reply that finds nothing — the fallback detector then does the work.</summary>
    public static string NoTrend(string system, string user) =>
        """{"toolId":"","chamber":"","symptom":"","mentions":0,"technicians":0,"shifts":0,"verdict":"Routine","finding":"No repeated symptom across technicians."}""";

    /// <summary>The dangerous case: right tool, invented arithmetic.</summary>
    public static string ConfidentlyWrong(string system, string user) =>
        """
        ```json
        {"toolId":"ETCH-03","chamber":"B","symptom":"uniformity","mentions":99,"technicians":7,"shifts":5,
         "verdict":"Escalate","finding":"ETCH-03 chamber B uniformity is being reported by many technicians."}
        ```
        """;
}
