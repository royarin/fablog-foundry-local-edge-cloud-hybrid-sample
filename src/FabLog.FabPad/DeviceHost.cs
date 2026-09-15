using System.Net.Http;
using FabLog.Core;
using FabLog.FabPad.Demo;
using Microsoft.AI.Foundry.Local;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using FoundryConfiguration = Microsoft.AI.Foundry.Local.Configuration;

namespace FabLog.FabPad;

/// <summary>
/// The composition root — the only place in FabPad that knows a vendor SDK exists.
/// Everything above it sees <see cref="Router"/> and <see cref="IChatClient"/>.
/// </summary>
public sealed class DeviceHost
{
    readonly IConfigurationRoot _config;

    public DeviceOptions Options { get; private set; } = new();

    /// <summary>The fab's own service. A destination, never a hop.</summary>
    public HubClient Hub { get; }

    /// <summary>The device's own wire to the vendor. Nothing brokers it.</summary>
    public CloudClient Cloud { get; }

    /// <summary>The cleanroom toggle. Read in exactly one place — <see cref="AirlockHandler"/>.</summary>
    public DemoSwitches Switches { get; } = new();

    /// <summary>
    /// This device's notes — <b>and the deferred-delivery mechanism</b>. Each note carries its own
    /// <c>SyncState</c>, so <c>Notes.Pending</c> is the backlog; there is no queue beside it.
    /// </summary>
    public NoteStore Notes { get; } = new();

    public Ledger Ledger { get; } = new();

    public HardwareProbe Hardware { get; } = new();
    public Router? Router { get; private set; }

    /// <summary>The hybrid flow — compiled outside the cleanroom, at the end of the shift.</summary>
    public ShiftReporter? Reporter { get; private set; }

    /// <summary>The local copy the technician keeps, regardless of whether submission worked.</summary>
    public ShiftReport? LastReport { get; private set; }

    public void KeepReport(ShiftReport report)
    {
        LastReport = report;
        Changed?.Invoke();
    }

    public string Status { get; private set; } = "starting…";
    public bool Ready { get; private set; }
    public string? StartupError { get; private set; }

    public event Action? Changed;

    public DeviceHost()
    {
        // ⚠️ WPF has no host environment, so appsettings.Development.json is NOT
        // picked up automatically — it would need DOTNET_ENVIRONMENT=Development.
        // Load it unconditionally instead, so local settings work on a fresh clone
        // without an environment variable anyone has to remember.
        _config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .AddJsonFile("appsettings.Development.json", optional: true, reloadOnChange: true)
            .Build();

        Bind();

        Switches.PadOnNetwork = Options.Demo.PadOnNetwork;

        // 🚪 The cleanroom boundary, as a registration rather than a clause.
        //
        // BOTH of the pad's outbound clients get the handler, because a cleanroom
        // blocks the fab's own LAN exactly as well as it blocks the internet. The
        // Central System is trusted — it just isn't in this room.
        //
        // Note what is conspicuously absent: the on-device model. FoundryChatClient
        // never constructs an HttpClient at all, so it cannot be reached by this
        // mechanism even in principle — structurally, not by exception.
        Hub = new HubClient(new HttpClient(new AirlockHandler(Switches) { InnerHandler = new HttpClientHandler() })
        {
            BaseAddress = new Uri(Options.Hub),
        })
        { Shift = Options.Shift };
        Hub.Synced += () => Changed?.Invoke();

        Cloud = new CloudClient(Options.T3, Switches);
        Cloud.ReachabilityChanged += () => Changed?.Invoke();

        Switches.Changed += () => Changed?.Invoke();

        // Registered last, so a reload can never fire against a half-built host.
        // An edit to appsettings.json lands here; the in-app posture selector is
        // the primary way to switch, and this is the belt-and-braces path.
        _config.GetReloadToken().RegisterChangeCallback(OnReload, null);
    }

    void Bind() => Options = _config.GetSection("FabLog").Get<DeviceOptions>() ?? new DeviceOptions();

    void OnReload(object? _)
    {
        var before = Options.Mode;
        Bind();
        Hub.Shift = Options.Shift;
        // Re-register: change tokens are single-shot.
        _config.GetReloadToken().RegisterChangeCallback(OnReload, null);
        if (before != Options.Mode) Changed?.Invoke();
    }

    /// <summary>Set by the in-app posture selector — the path that does not depend on the config file watcher.</summary>
    public void SetMode(DemoMode mode)
    {
        Options.Mode = mode;
        Changed?.Invoke();
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Start-up. Deliberately slow: model load is measured in TENS of seconds,
    //  which is exactly why it happens here at launch and not on the first note.
    // ─────────────────────────────────────────────────────────────────────────
    public async Task StartAsync(CancellationToken ct = default)
    {
        try
        {
            using var loggerFactory = LoggerFactory.Create(b => b.AddDebug());
            var log = loggerFactory.CreateLogger("FabLog");

            // ⚠️ AppName rejects dots. "FabLog.FabPad" throws
            // "AppName contains invalid characters" — letters, numbers, spaces,
            // hyphens and underscores only.
            Report("starting Foundry Local…");
            await FoundryLocalManager.CreateAsync(new FoundryConfiguration { AppName = "FabLog" }, log, null);
            var mgr = FoundryLocalManager.Instance;

            Report("registering execution providers…");
            Hardware.Capture(mgr.DiscoverEps());
            // ⚠️ This callback reports 0–100 already (unlike DownloadAsync's 0.0–1.0
            // below) — :P0 would multiply by 100 again and print "4,700 %". :F0 + a
            // literal "%" is correct here.
            var eps = await mgr.DownloadAndRegisterEpsAsync(
                (name, pct) => Report($"registering {name} — {pct:F0}%"), null);
            Hardware.Capture(mgr.DiscoverEps(), eps.FailedEps);

            Report($"resolving {Options.T1.ModelAlias}…");
            var catalog = await mgr.GetCatalogAsync(null);

            // If the primary alias isn't in this machine's catalogue, drop to the
            // 0.5B rather than dying. That is the point of FallbackAlias being
            // config: a machine that can't serve the 1.5B still runs, just slower
            // and slightly worse.
            var model = await catalog.GetModelAsync(Options.T1.ModelAlias, null);
            if (model is null && Options.T1.FallbackAlias is { Length: > 0 } fallback)
            {
                Report($"{Options.T1.ModelAlias} unavailable — falling back to {fallback}…");
                model = await catalog.GetModelAsync(fallback, null);
            }
            if (model is null)
                throw new InvalidOperationException(
                    $"Neither '{Options.T1.ModelAlias}' nor '{Options.T1.FallbackAlias}' is in the Foundry Local catalogue. " +
                    "Run `foundry model download qwen2.5-1.5b` first.");

            var (target, id) = SelectVariant(model);
            Hardware.Resolve(id, ProviderOf(id));

            if (!await target.IsCachedAsync(null))
            {
                Report($"downloading {id}…");
                await target.DownloadAsync(p => Report($"downloading {id} — {p:P0}"), null);
            }

            Report($"loading {id}…");
            await target.LoadAsync(null);

            var t1 = await FoundryChatClient.CreateAsync(target, id);

            // ── Two tiers on the routing axis, one interface ───────────────────
            //
            // T1 and T3 — where the work may run. There is deliberately no
            // T2FabHub entry: the hub is not somewhere work is *routed*, it is
            // where every note *goes*, on every path, which is `sink` below.
            // Registering it as a tier would make the hub the device's way out,
            // and `CloudOnly` a posture the hub holds rather than the device.
            var tierMap = new Tiers(
                new Dictionary<Tier, IChatClient> { [Tier.T1Device] = t1, [Tier.T3VendorCloud] = Cloud },
                Cloud);

            var modelNames = new ModelNames(
                Device: id,
                Cloud: Options.T3.IsConfigured ? Options.T3.Deployment : "(T3 not configured)");

            // The hybrid flow. Same tiers, same ledger, same masking machinery as
            // note capture — applied to a different task, outside the cleanroom.
            Reporter = new ShiftReporter(tierMap, Ledger, modelNames, sink: Hub);

            Router = new Router(
                tierMap,
                Ledger,
                // The ledger names the model that actually ran, so it reads the
                // deployment — not a hard-coded "gpt-4o" that would still say
                // "gpt-4o" after someone repointed T3 at a different model.
                modelNames,
                sink: Hub);

            // No ping. Connectivity is discovered by the first real call failing —
            // there is nothing here to ask.
            Ready = true;
            Report("ready");
        }
        catch (Exception ex)
        {
            StartupError = $"{ex.GetType().Name}: {ex.Message}";
            Report("start-up failed");
        }
    }

    /// <summary>
    /// Alias picks the model; <see cref="T1Options.VariantPreference"/> picks the
    /// silicon. See the comment on that property for why relying on the SDK's own
    /// resolution costs 3× on a two-GPU machine.
    /// </summary>
    (IModel Model, string Id) SelectVariant(IModel model)
    {
        var variants = model.Variants;

        if (Options.T1.ForceVariant is { Length: > 0 } forced)
        {
            var hit = variants.FirstOrDefault(v => v.Id.Contains(forced, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return (hit, hit.Id);
        }

        foreach (var key in Options.T1.VariantPreference)
        {
            var hit = variants.FirstOrDefault(v => v.Id.Contains(key, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return (hit, hit.Id);
        }

        return (model, model.Id);   // nothing matched — the SDK's own choice
    }

    static string ProviderOf(string modelId) =>
        modelId.Contains("cuda", StringComparison.OrdinalIgnoreCase) ? "CUDA (discrete GPU)"
        : modelId.Contains("trtrtx", StringComparison.OrdinalIgnoreCase) ? "NvTensorRT-RTX"
        : modelId.Contains("openvino", StringComparison.OrdinalIgnoreCase) ? "OpenVINO (integrated GPU)"
        : modelId.Contains("generic-gpu", StringComparison.OrdinalIgnoreCase) ? "DirectML / generic GPU"
        : "CPU";

    void Report(string status) { Status = status; Changed?.Invoke(); }

    /// <summary>
    /// Recovery, without anyone having to ask for it.
    /// <para>
    /// ⚠️ <b>This is a drain loop, not a probe.</b> It never asks whether the device is
    /// online — there is nothing in this app that can. It <i>tries to send whatever is
    /// waiting</i>, and a send succeeding is how the device discovers the signal came
    /// back. That is what every real client has always done.
    /// </para>
    /// <para>
    /// So when the technician walks out of the cleanroom, the backlog empties on its
    /// own and nothing has to be clicked.
    /// </para>
    /// <para>
    /// ⚠️ <b>Do not be tempted to trigger this from the cleanroom toggle.</b> It would
    /// be instant, and it would be false: the app would be <i>told</i> the network was
    /// back rather than finding out, and no real device has such an event. The whole
    /// point of <see cref="AirlockHandler"/> is that nothing above it knows.
    /// </para>
    /// <para>
    /// Send first, <i>then</i> wait: delaying first costs up to a full interval before
    /// anything moves.
    /// </para>
    /// <para>
    /// ⚠️ <b>Only the Central System is retried, and only notes.</b> There is no deferred cloud
    /// work — Cloud AI is a direct call that answers or errors, because deferring a <i>delivery</i>
    /// is useful and deferring an <i>answer</i> is not.
    /// </para>
    /// </summary>
    public async Task PollAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(Math.Clamp(Options.Demo.DrainIntervalSeconds, 1, 60));

        while (!ct.IsCancellationRequested)
        {
            await SyncPendingAsync(ct);
            try { await Task.Delay(interval, ct); } catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// Send whatever has not reached the Central System yet, oldest first.
    /// <para>
    /// Serial and stop-on-first-failure, both deliberately: the pending count falls one at a time,
    /// and a note that fails must stay pending rather than be skipped over — losing one would
    /// quietly leave the fleet-wide finding a complaint short.
    /// </para>
    /// </summary>
    /// <returns>How many notes were delivered.</returns>
    public async Task<int> SyncPendingAsync(CancellationToken ct = default)
    {
        var delivered = 0;
        foreach (var note in Notes.Pending)
        {
            if (ct.IsCancellationRequested) break;

            bool ok;
            try { ok = await Hub.SyncAsync(note, ct); }
            catch { ok = false; }

            if (!ok) break;

            Notes.Upsert(note with { Sync = SyncState.Synced });
            delivered++;
        }
        return delivered;
    }
}

/// <summary>Backs the hardware panel — <c>DiscoverEps()</c> put straight on screen, unfiltered.</summary>
public sealed class HardwareProbe : IHardwareProbe
{
    IReadOnlyList<ExecutionProviderInfo> _eps = [];

    public string ResolvedModelId { get; private set; } = "";
    public string ResolvedExecutionProvider { get; private set; } = "";
    public IReadOnlyList<string> FailedProviders { get; private set; } = [];

    public IReadOnlyList<ExecutionProviderInfo> DiscoverExecutionProviders() => _eps;

    public void Capture(IEnumerable<EpInfo> eps, IEnumerable<string>? failed = null)
    {
        _eps = [.. eps.Select(e => new ExecutionProviderInfo(e.Name, e.IsRegistered))];
        if (failed is not null) FailedProviders = [.. failed];
    }

    public void Resolve(string modelId, string provider)
    {
        ResolvedModelId = modelId;
        ResolvedExecutionProvider = provider;
    }
}
