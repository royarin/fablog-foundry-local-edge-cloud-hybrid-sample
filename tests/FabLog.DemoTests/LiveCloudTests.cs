using System.Diagnostics;
using Azure.AI.OpenAI;
using FabLog.TrustHub;
using Microsoft.Extensions.AI;
using Xunit.Abstractions;

namespace FabLog.DemoTests;

/// <summary>
/// The only tests that touch Azure. They <b>skip themselves</b> when no real
/// credential is present, so <c>dotnet test</c> stays green on a machine with no
/// config — and starts exercising the cloud the moment
/// <c>appsettings.Development.json</c> exists, with no flag anyone has to
/// remember. (See <see cref="LiveFactAttribute"/>.)
/// <para>
/// They exist because four things about T3 are <i>unknowable</i> from a fake:
/// </para>
/// <list type="number">
/// <item>Does the vendor model reproduce <c>⟦R1⟧</c> verbatim, or does it
/// helpfully rewrite the placeholder? If it rewrites, the local unmask silently
/// fails and the technician reads a report full of ⟦R1⟧.</item>
/// <item>Can a real model find the trend from <b>columns alone</b>? The hub sends
/// a digest with no note body — a prompt that needs prose to work would break the
/// fleet-wide query in a way no fake can show.</item>
/// <item>What does the round trip actually cost? It is the one latency number
/// that cannot be measured locally.</item>
/// <item>Does the deployment's quota survive six back-to-back drains? A 429 in
/// the middle of the queue leaves the count stuck with nothing to explain it.</item>
/// </list>
/// <para>
/// ⚠️ The polish round trip is the <i>pad's</i>, not the hub's, so these drive the
/// vendor client directly. It is built by <see cref="Cloud.Create"/> either way:
/// the same <c>AzureOpenAIClient</c>, the same options, the same airlock.
/// </para>
/// </summary>
[Trait("Category", "Live")]
public class LiveCloudTests(ITestOutputHelper output)
{
    static T3Options Config() => LiveConfig.T3;

    /// <summary>The vendor, wired exactly as the Central System wires it.</summary>
    static IChatClient Vendor(T3Options o) => Cloud.Create(o);

    /// <summary>
    /// The vendor as <b>FabPad</b> wires it — with an <see cref="AirlockHandler"/>
    /// under the SDK.
    /// <para>
    /// Reproduced here rather than referenced: the test project cannot reference
    /// FabPad (Windows TFM). These are the same two lines <c>CloudClient</c> uses,
    /// and the point of the test below is that the handler sits beneath a
    /// <i>real</i> client talking to a <i>real</i> endpoint.
    /// </para>
    /// </summary>
    static IChatClient PadVendor(T3Options o, IAirlock airlock) =>
        new AzureOpenAIClient(
                o.ResourceEndpoint,
                new System.ClientModel.ApiKeyCredential(o.ApiKey),
                new AzureOpenAIClientOptions
                {
                    Transport = new System.ClientModel.Primitives.HttpClientPipelineTransport(
                        new HttpClient(new AirlockHandler(airlock) { InnerHandler = new HttpClientHandler() })
                        {
                            Timeout = TimeSpan.FromSeconds(Math.Max(o.TimeoutSeconds, 1)),
                        }),
                    NetworkTimeout = TimeSpan.FromSeconds(o.TimeoutSeconds),
                })
            .GetChatClient(o.Deployment)
            .AsIChatClient();

    sealed class Sealed : IAirlock
    {
        public bool PadOnNetwork => false;
    }

    /// <summary>
    /// The polish round trip, as the pad performs it. <c>CloudClient.TrySendAsync</c>
    /// sends precisely these two messages — the test project cannot reference
    /// FabPad (Windows TFM), so the call is reproduced rather than invoked.
    /// </summary>
    static async Task<string> PolishAsync(IChatClient vendor, string masked, CancellationToken ct = default)
    {
        var reply = await vendor.GetResponseAsync(
            [new ChatMessage(ChatRole.System, Prompts.Polish), new ChatMessage(ChatRole.User, masked)],
            Prompts.ProseOptions, ct);
        return reply.Text ?? "";
    }

    /// <summary>A masked note exactly as the pad hands it over: real shape, no real values.</summary>
    const string Masked =
        "ETCH-03 chamber B uniformity ⟦R1⟧ on recipe ⟦R2⟧, ramp ⟦R3⟧ — off spec.";

    [LiveFact]
    public async Task The_vendor_model_reproduces_placeholders_verbatim()
    {
        // If this fails, masking is broken in the worst possible way: the route
        // LOOKS right — masked payload, cloud round trip, prose comes back — and
        // the technician's restored note still has ⟦R1⟧ in it.
        using var vendor = Vendor(Config());

        var polished = await PolishAsync(vendor, Masked);
        output.WriteLine($"model reply:\n{polished}\n");

        Assert.Contains("⟦R1⟧", polished);
        Assert.Contains("⟦R2⟧", polished);
        Assert.Contains("⟦R3⟧", polished);

        // …and it did not invent values to fill them in.
        Assert.DoesNotContain("94.2", polished);
        Assert.DoesNotContain("RX-7", polished);
    }

    [LiveFact]
    public async Task Measure_the_cloud_round_trip()
    {
        var o = Config();
        using var vendor = Vendor(o);

        var ms = new List<long>();
        for (var i = 0; i < 5; i++)
        {
            var sw = Stopwatch.StartNew();
            await PolishAsync(vendor, Masked);
            sw.Stop();
            ms.Add(sw.ElapsedMilliseconds);
            output.WriteLine($"  call {i + 1}: {sw.ElapsedMilliseconds} ms");
        }

        // Run 1 is cold — same rule the device measurements use.
        var warm = ms.Skip(1).Order().ToList();
        var median = warm[warm.Count / 2];
        output.WriteLine($"\ndeployment : {o.Deployment}");
        output.WriteLine($"warm median: {median} ms   (warm: {string.Join(", ", warm)})");

        // Not a pass/fail threshold — a tripwire. Above this the pause after
        // pressing send is long enough to read as a hang.
        Assert.True(median < 8000, $"T3 round trip is {median} ms — check the deployment or shorten the prompt.");
    }

    [LiveFact]
    public async Task Six_back_to_back_drains_do_not_hit_a_rate_limit()
    {
        // This is the quota ask in SETUP.md, as a test. The pad drains its queue
        // serially, straight to the vendor; a 429 on item four leaves the count
        // stuck with no way to explain it.
        using var vendor = Vendor(Config());

        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 6; i++)
        {
            var reply = await PolishAsync(vendor, $"{Masked} (queued note {i + 1})");
            Assert.False(string.IsNullOrWhiteSpace(reply));
        }
        sw.Stop();

        output.WriteLine($"6 sequential polish calls: {sw.ElapsedMilliseconds} ms total");
    }

    [LiveFact]
    public async Task The_real_model_finds_the_trend_from_columns_alone()
    {
        // 🔍 The question a fake cannot answer: the hub sends a DIGEST —
        // timestamps, initials, shift, tool, chamber, metric, severity, and not
        // one word anybody wrote. If a real model needs the prose to spot the
        // pattern, the whole projection is the wrong design.
        var o = Config();
        var store = new HubStore(SeedPath);
        var brain = new HubBrain(store, Vendor(o), o.Deployment);

        store.Add(new HubNote(Guid.NewGuid(), "K. Nagy", "Night 2026-09-12", "ETCH-03", "B",
            "uniformity", Severity.Escalate,
            "ETCH-03 chamber B uniformity 94.2% on recipe RX-7, ramp 4.5s — off spec.",
            DateTimeOffset.Parse("2026-09-12T21:30:00Z")));

        output.WriteLine($"digest sent:\n{store.AsDigest()}\n");

        var r = await brain.AnalyseAsync("anything odd about ETCH-03 today?");

        output.WriteLine($"{r.ElapsedMs} ms · {r.Model}");
        output.WriteLine($"finding: {r.Finding?.Finding}");

        Assert.False(r.Failed);
        Assert.Equal("ETCH-03", r.Finding!.ToolId);

        // Whatever the model claimed, these three came from the store.
        Assert.Equal(3, r.Finding.Mentions);
        Assert.Equal(3, r.Finding.Technicians);
        Assert.Equal(3, r.Sources.Count);

        // The sentence a supervisor reads must not contain a value the vendor
        // never had — the model can only have hallucinated it.
        Assert.DoesNotContain("94.2", r.Finding.Finding);
        Assert.DoesNotContain("RX-7", r.Finding.Finding);

        // 🔍 …and it must not contain a specific COUNT either.
        //
        // Found live against a real deployment: the model wrote "across three
        // technicians over two shifts" while the hub was holding three shifts.
        // The counters on FabDesk are the hub's arithmetic and were right — so
        // the screen would have contradicted itself at the exact moment someone
        // is deciding whether to believe it.
        //
        // Prompts.Trend now forbids a number and asks for "multiple" instead.
        // Vague is fine: it cannot disagree with a counter. Specific and wrong
        // is the only failure mode here, and the hub owns every specific number
        // on that screen.
        var sentence = r.Finding.Finding.Replace(r.Finding.ToolId, "", StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(sentence, char.IsDigit);
        Assert.All(
            (string[])["one ", "two ", "three ", "four ", "five ", "six "],
            w => Assert.DoesNotContain(w, sentence.ToLowerInvariant()));
    }

    [LiveFact]
    public async Task A_wrong_key_fails_as_an_error_not_as_an_answer()
    {
        var o = Config();
        var brain = new HubBrain(new HubStore(SeedPath),
            Cloud.Create(new T3Options
            {
                Endpoint = o.Endpoint,
                Deployment = o.Deployment,
                ApiKey = "definitely-not-the-key",
            }), o.Deployment);

        var r = await brain.AnalyseAsync("anything odd about ETCH-03 today?");

        output.WriteLine($"error: {r.Error}");
        Assert.True(r.Failed);
        Assert.Null(r.Finding);
    }

    [LiveFact]
    public async Task A_sealed_cleanroom_beats_a_real_endpoint()
    {
        // 🚪 Against the live client. Everything here is real — real endpoint, real
        // key, real SDK — and the call still fails at the socket, because the
        // handler is below all of it. This is the assertion that offline means a
        // closed socket and not a routing shortcut.
        var o = Config();
        using var vendor = PadVendor(o, new Sealed());

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => PolishAsync(vendor, Masked));
        output.WriteLine($"error: {ex.GetType().Name}: {ex.Message}");

        Assert.True(
            Unwrap(ex).Any(e => e is System.Net.Sockets.SocketException or HttpRequestException),
            $"expected a transport failure, got {ex.GetType().Name}");

        static IEnumerable<Exception> Unwrap(Exception? e)
        {
            for (; e is not null; e = e.InnerException) yield return e;
        }
    }

    static string SeedPath => Path.Combine(AppContext.BaseDirectory, "Data", "hub-notes.json");
}
