using Microsoft.Extensions.AI;

namespace FabLog.DemoTests;

/// <summary>
/// A deterministic <see cref="IChatClient"/>. <paramref name="respond"/> receives
/// (system prompt, user payload) and returns the reply.
/// <para>
/// ⚠️ <b>No test in this suite talks to a real model, and that is deliberate.</b>
/// These tests assert what the architecture <i>does with</i> a model's answer —
/// which tier ran, what crossed a wall, what was written to the ledger. Those are
/// properties of the routing, and a real model would make them flaky without
/// making them any more true. The tests that genuinely need a live endpoint are
/// tagged <c>Category=Live</c> and are opt-in.
/// </para>
/// </summary>
public sealed class FakeChat(Func<string, string, string> respond, string modelId = "fake") : IChatClient
{
    /// <summary>Every user payload this client was handed, in order. The suite's main instrument.</summary>
    public List<string> Received { get; } = [];

    public List<string> Systems { get; } = [];

    public int Calls => Received.Count;

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
    {
        var list = messages.ToList();
        var system = list.FirstOrDefault(m => m.Role == ChatRole.System)?.Text ?? "";
        var user = list.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? "";
        Systems.Add(system);
        Received.Add(user);

        return Task.FromResult(new ChatResponse(
            new ChatMessage(ChatRole.Assistant, respond(system, user))) { ModelId = modelId });
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}

/// <summary>
/// A tier that is not there.
/// <para>
/// It throws exactly what <c>AirlockHandler</c> throws, which is exactly what
/// Windows throws for a missing NIC — <c>WSAHOST_NOT_FOUND</c> wrapped in an
/// <see cref="HttpRequestException"/>. Same shape here as in the running app, so
/// a test passing is evidence about the app and not about the double.
/// </para>
/// </summary>
public sealed class OfflineChat(string message = "No such host is known.") : IChatClient
{
    public int Attempts { get; private set; }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
    {
        Attempts++;
        throw new HttpRequestException(message,
            new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.HostNotFound));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}

/// <summary>
/// The connectivity badge, as a test double — an <b>observation</b>, not a switch.
/// <para>
/// ⚠️ Deliberately carries no <c>IsReachable(Tier)</c> for the Router to consult
/// before calling out: a probe is a prediction about the next call, and a wrong
/// prediction is worse than no prediction. Nothing asks this before trying, so
/// setting it cannot change any routing decision — which is precisely the
/// property <c>Offline_is_discovered_by_failing_not_by_asking</c> pins down.
/// </para>
/// </summary>
public sealed class FakeNet(bool online = true) : IConnectivity
{
    public bool CloudReachable { get; set; } = online;
    public DateTimeOffset? UnreachableSince { get; set; }
}

/// <summary>
/// The Central System, as a test double. Records every note the device pushed and
/// the <b>literal</b> text it pushed with it.
/// <para>
/// The suite's third instrument. It must receive every note, in full, in every
/// posture — but <b>not always immediately</b>: set <see cref="Up"/> false to model
/// the technician being inside the cleanroom, where this server is as unreachable
/// as the vendor. Notes must then <i>wait</i>, never vanish.
/// </para>
/// </summary>
public sealed class FakeSink(bool up = true) : INoteSink, IReportSink
{
    /// <summary>False = the pad is inside the cleanroom and cannot reach this server.</summary>
    public bool Up { get; set; } = up;

    /// <summary>
    /// Accept this many notes, then start refusing — for asserting that a run stops
    /// on the first failure rather than skipping past it.
    /// </summary>
    public int AcceptAtMost { get; set; } = int.MaxValue;

    public List<(FabNote Note, string Text)> Received { get; } = [];

    public List<ShiftReport> Reports { get; } = [];

    public int Count => Received.Count;

    /// <summary>Records what the Central System would store: the readable sentence, not the shorthand.</summary>
    public Task<bool> SyncAsync(FabNote note, CancellationToken ct = default)
    {
        if (!Up || Received.Count >= AcceptAtMost) return Task.FromResult(false);
        Received.Add((note, note.Readable));
        return Task.FromResult(true);
    }

    public Task<bool> SyncReportAsync(ShiftReport report, CancellationToken ct = default)
    {
        if (!Up) return Task.FromResult(false);
        Reports.Add(report);
        return Task.FromResult(true);
    }
}

/// <summary>
/// Canned device replies for the seed note, shaped like what the local model
/// actually returns — including the markdown fence around the JSON and the
/// unnormalised <c>ETCH03</c>, because every measured variant produced both.
/// </summary>
public static class DeviceReplies
{
    /// <summary>
    /// The third ETCH-03 complaint — written <b>inside the cleanroom</b>, which is
    /// why it only reaches the Central System when the technician walks back out.
    /// </summary>
    public const string SeedNote = "etch03 chmbr B unif 94.2% recipe RX-7 ramp 4.5s - off spec";

    /// <summary>
    /// A note written <b>at the bench</b>, with both wires live.
    /// <para>
    /// ⚠️ Deliberately a different tool. It syncs immediately, so if it were the
    /// third ETCH-03 complaint the pattern would resolve before the reconnect step
    /// and the drain would stop meaning anything. It still carries a recipe name and
    /// a yield figure, so what leaks is just as sensitive.
    /// </para>
    /// </summary>
    public const string BenchNote = "etch06 chmbr A unif 93.8% recipe RX-9 ramp 5.2s - off spec";

    /// <summary>A note with no proprietary values in it at all — the Hybrid "this one may go" case.</summary>
    public const string BenignNote = "ETCH-01 chamber A door interlock tested OK, no issues";

    public static string For(string system, string user) =>
        system.StartsWith("You extract structured data")
            ? user.Contains("etch06", StringComparison.OrdinalIgnoreCase)
                ? """{"toolId":"ETCH06","chamber":"chamber A","metric":"uniformity","value":"93.8%","severity":"Escalate","tags":["excursion"]}"""
                : """
                  ```json
                  {"toolId":"ETCH03","chamber":"chamber B","metric":"uniformity","value":"94.2%","severity":"Escalate","tags":["excursion","follow-up"]}
                  ```
                  """
        : system.StartsWith("Rewrite this semiconductor")
            ? "ETCH-03 chamber B uniformity measured 94.2% on recipe RX-7 with a 4.5s ramp, which is off spec."
        : system.StartsWith("You flag proprietary")
            ? user.Contains("RX-9", StringComparison.Ordinal)
                ? """["RX-9","5.2s","93.8%"]"""
                : """["RX-7","4.5s","94.2%"]"""
        : system.StartsWith("You write incident reports")
            ? $"Incident report: {user}"
        : system.StartsWith("You write end-of-shift")
            ? $"Shift summary: {user}"
        : "";

    /// <summary>A model returning unusable output, so the fallback path is asserted rather than hoped for.</summary>
    public static string Garbage(string system, string user) =>
        system.StartsWith("You write incident reports") ? $"Incident report: {user}"
        : system.StartsWith("You extract structured data") ? "I'm sorry, I cannot help with that."
        : system.StartsWith("You flag proprietary") ? "no idea"
        : "";
}

/// <summary>
/// Assembles a <see cref="Router"/> the way <c>DeviceHost</c> does, so the tests
/// exercise the real wiring.
/// <para>
/// ⚠️ <b>Two tiers, not three.</b> T1 and T3 are the routing axis — where work
/// may run. The hub is <b>not</b> on it: it is a <see cref="INoteSink"/>, a
/// destination every path reaches. Registering it as a tier here would let a
/// test go green against an architecture the product deliberately does not have.
/// </para>
/// </summary>
public sealed class Rig
{
    public FakeChat Device { get; }

    /// <summary>T3 — reached over the device's own wire. Nothing brokers it.</summary>
    public IChatClient Cloud { get; }

    public FakeNet Net { get; }

    /// <summary>
    /// TrustHub. Inside the fab, inside the airgap, on every path.
    /// <para>
    /// Defaults to a <see cref="FakeSink"/> recorder. <c>EndToEndTests</c> passes a
    /// real <c>HubStore</c> instead, because those tests need the hub's actual
    /// grouping rather than a transcript of what was sent to it.
    /// </para>
    /// </summary>
    public INoteSink Sink { get; }

    public Ledger Ledger { get; } = new();

    /// <summary>
    /// This device's notes — <b>and the deferred-delivery mechanism</b>, mirroring
    /// FabPad's <c>NoteStore</c>, which the test project cannot reference (Windows TFM).
    /// <para>
    /// ⚠️ There is no separate <c>PendingQueue</c>, in the rig or in the product. A
    /// note that has not reached the Central System is simply a note whose
    /// <see cref="SyncState"/> is <c>Pending</c>.
    /// </para>
    /// </summary>
    public List<FabNote> Notes { get; } = [];

    public IReadOnlyList<FabNote> Pending =>
        [.. Notes.Where(n => n.Sync == SyncState.Pending).OrderBy(n => n.At)];

    public int PendingCount => Notes.Count(n => n.Sync == SyncState.Pending);

    public Router Router { get; }

    /// <summary>The hybrid flow, sharing the same tiers and ledger as the router.</summary>
    public ShiftReporter Reporter { get; }

    public Rig(
        Func<string, string, string>? device = null,
        IChatClient? cloud = null,
        bool online = true,
        bool hubUp = true,
        INoteSink? sink = null)
    {
        Device = new FakeChat(device ?? DeviceReplies.For, "qwen2.5-1.5b-instruct-cuda-gpu:4");
        Cloud = cloud ?? new FakeChat(DeviceReplies.For, "gpt-4o");
        Net = new FakeNet(online);
        Sink = sink ?? new FakeSink(hubUp);

        var tiers = new Tiers(
            new Dictionary<Tier, IChatClient> { [Tier.T1Device] = Device, [Tier.T3VendorCloud] = Cloud },
            Net);
        var models = new ModelNames(Device: "qwen2.5-1.5b-instruct-cuda-gpu:4", Cloud: "gpt-4o");

        Router = new Router(tiers, Ledger, models, sink: Sink);
        Reporter = new ShiftReporter(tiers, Ledger, models, sink: Sink as IReportSink);
    }

    /// <summary>
    /// 🚪 The technician walks into the cleanroom: <b>both</b> destinations go away
    /// at once, because the room blocks the fab's own LAN as well as the internet.
    /// Only available when the rig was built with the doubles that can be switched.
    /// </summary>
    public void SealCleanroom()
    {
        if (Sink is FakeSink f) f.Up = false;
        if (Cloud is SwitchableChat s) s.PadOnNetwork = false;
    }

    /// <summary>The technician walks back out. Both come back together.</summary>
    public void LeaveCleanroom()
    {
        if (Sink is FakeSink f) f.Up = true;
        if (Cloud is SwitchableChat s) s.PadOnNetwork = true;
    }

    /// <summary>
    /// Sends whatever has not reached the Central System yet, exactly as
    /// <c>DeviceHost.SyncPendingAsync</c> does: oldest first, serial, stopping on the
    /// first failure so a note is left pending rather than skipped.
    /// </summary>
    public async Task<int> SyncPendingAsync()
    {
        var delivered = 0;
        foreach (var note in Pending)
        {
            bool ok;
            try { ok = await Sink.SyncAsync(note); }
            catch { ok = false; }
            if (!ok) break;

            Upsert(note with { Sync = SyncState.Synced });
            delivered++;
        }
        return delivered;
    }

    void Upsert(FabNote note)
    {
        var i = Notes.FindIndex(n => n.Id == note.Id);
        if (i >= 0) Notes[i] = note; else Notes.Add(note);
    }

    /// <summary>
    /// <paramref name="noteId"/> re-processes an existing note: the same note, the
    /// same technician, a different policy. The hub upserts on it.
    /// </summary>
    public async Task<RouteResult> LogAsync(string raw, DemoMode mode, bool wantPolish = true, Guid? noteId = null)
    {
        var result = await Router.ProcessAsync(
            new RouteRequest(raw, "K. Nagy", wantPolish, noteId, "PAD-07", "CR-2"), mode);

        // The UI does exactly this: whatever came back is what the device now holds,
        // including its SyncState — which is the backlog.
        Upsert(result.Note);
        return result;
    }

    /// <summary>What crossed wall 2 — every payload the vendor was handed, in order.</summary>
    public IReadOnlyList<string> VendorSaw => Cloud is FakeChat f ? f.Received : [];

    /// <summary>What the fab's own server was handed. Different wall, different rules.</summary>
    public IReadOnlyList<string> HubSaw =>
        Sink is FakeSink f ? [.. f.Received.Select(r => r.Text)] : [];
}
