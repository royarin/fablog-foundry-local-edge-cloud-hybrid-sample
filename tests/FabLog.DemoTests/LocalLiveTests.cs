using System.ClientModel;
using System.Diagnostics;
using System.Text.Json;
using FabLog.TrustHub;
using Microsoft.Extensions.AI;
using OpenAI;
using Xunit.Abstractions;

namespace FabLog.DemoTests;

/// <summary>
/// 🧪 Experiment, not product: the exact shift-handover prompt + digest against a LOCAL
/// model served by Foundry Local, through the same <see cref="HandoverBrain"/> parse and
/// validation as the cloud. Nothing here touches Program.cs or any default.
/// <para>
/// Opt-in. Skips itself unless <c>FABLOG_LOCAL_ENDPOINT</c> (e.g. <c>http://127.0.0.1:56493/v1</c>)
/// and <c>FABLOG_LOCAL_MODEL</c> (the loaded model id) are set. Run with
/// <c>--filter Category=LocalLive</c>. Set <c>FABLOG_LOCAL_OUT</c> to also write the raw runs as JSON.
/// </para>
/// </summary>
[Trait("Category", "LocalLive")]
public class LocalLiveTests(ITestOutputHelper output)
{
    static string? Endpoint => Environment.GetEnvironmentVariable("FABLOG_LOCAL_ENDPOINT");
    static string? Model => Environment.GetEnvironmentVariable("FABLOG_LOCAL_MODEL");

    public sealed class LocalLiveFactAttribute : FactAttribute
    {
        public LocalLiveFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Endpoint) || string.IsNullOrWhiteSpace(Model))
                Skip = "Set FABLOG_LOCAL_ENDPOINT and FABLOG_LOCAL_MODEL to run the local-model experiment.";
        }
    }

    /// <summary>Remembers what the model actually said (or how the call failed) — the brain only keeps the verdict.</summary>
    sealed class Recording(IChatClient inner) : DelegatingChatClient(inner)
    {
        public string? Reply { get; private set; }
        public string? Error { get; private set; }

        public override async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
        {
            Reply = Error = null;
            try { var r = await base.GetResponseAsync(messages, options, ct); Reply = r.Text; return r; }
            catch (Exception ex) { Error = $"{ex.GetType().Name}: {ex.Message}"; throw; }
        }
    }

    static readonly HubNote ConflictNote = new(Guid.NewGuid(), "T. Szabó", "Morning 2026-09-12",
        "ETCH-02", "A", "vacuum", FabLog.Core.Severity.Routine,
        "ETCH-02 chamber A vacuum steady after the PM ticket was raised.",
        DateTimeOffset.Parse("2026-09-12T10:00:00Z"));

    record Run(string Set, int RunNo, bool WarmUp, long WallMs, int HubMs, bool Parsed, string Source,
        string[] Ranks, string[] Confidence, string[] Why, string[] Rejections, string? FallbackReason,
        string? RawReply, string? CallError);

    [LocalLiveFact]
    public async Task The_handover_prompt_against_the_local_model()
    {
        var local = new Recording(
            new OpenAIClient(new ApiKeyCredential("not-needed"),
                    new OpenAIClientOptions { Endpoint = new Uri(Endpoint!), NetworkTimeout = TimeSpan.FromSeconds(25) })
                .GetChatClient(Model!).AsIChatClient());

        var context = HandoverContext.Load(Path.Combine(AppContext.BaseDirectory, "Data", "handover-context.json"));
        var asOf = DateTimeOffset.Parse("2026-09-12T12:20:00Z");
        List<Run> runs = [];

        foreach (var set in new[] { "A", "B" })
        {
            var store = new HubStore(Path.Combine(AppContext.BaseDirectory, "Data", "hub-notes.json"));
            if (set == "B") store.Add(ConflictNote);

            // Same construction as Program.cs: default 15 s hub deadline, same prompt, same options.
            var brain = new HandoverBrain(store, context, local, Model!, new HubFeatures { ShiftHandover = true }, now: () => asOf);

            for (var i = 0; i <= 5; i++)     // run 0 is a warm-up and is reported separately
            {
                var sw = Stopwatch.StartNew();
                var r = await brain.BriefAsync();
                sw.Stop();
                runs.Add(new Run(set, i, i == 0, sw.ElapsedMilliseconds, r.ElapsedMs, r.ReplyParsed, r.Source,
                    [.. r.Checks.Select(c => $"{c.Tool} {c.Chamber}")], [.. r.Checks.Select(c => c.Confidence)],
                    [.. r.Checks.Select(c => c.Why)], [.. r.Rejections], r.FallbackReason, local.Reply, local.Error));
                output.WriteLine($"{set}{i} {sw.ElapsedMilliseconds} ms parsed={r.ReplyParsed} source={r.Source} " +
                    $"rank={string.Join(">", r.Checks.Select(c => c.Tool + c.Chamber))} rej={r.Rejections.Count} {r.FallbackReason}");
            }
        }

        if (Environment.GetEnvironmentVariable("FABLOG_LOCAL_OUT") is { Length: > 0 } path)
            File.WriteAllText(path, JsonSerializer.Serialize(runs, new JsonSerializerOptions { WriteIndented = true }));

        // An experiment, not a gate: the only claim is that every run ended in SOME answer.
        Assert.All(runs, r => Assert.NotEmpty(r.Ranks));
    }
}
