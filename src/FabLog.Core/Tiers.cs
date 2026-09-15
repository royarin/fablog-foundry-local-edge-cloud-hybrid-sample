using Microsoft.Extensions.AI;

namespace FabLog.Core;

#region Three tiers, one interface

// T3 sits BESIDE T1, not behind T2. The device has its own wire to the vendor,
// because `CloudOnly` has to be a posture the device can hold by itself — route
// it through the hub and the hub is the device's way out, whatever the ledger
// column says.
//
// T2 is not on this axis at all. The Central System is the fab's own service on
// the fab's own network: every note reaches it, in full, in every posture —
// though not always immediately, because the cleanroom blocks the fab's LAN too.
// It is a destination, never a hop.
public enum Tier { T1Device, T2FabHub, T3VendorCloud }

// Three postures, one axis: where the work may run.
public enum DemoMode { CloudOnly, EdgeOnly, Hybrid }

// A deployment posture — what a fab's compliance officer would set.
//   Allowed   : where work may run at all
//   ProtectIp : whether fab IP is masked before crossing a boundary
public sealed record Policy(Tier[] Allowed, bool ProtectIp);

public static class Policies
{
    // ── Read the ProtectIp column downward. It is true on exactly one row, and
    //    that is not an oversight — redaction is a BORDER control.
    public static readonly IReadOnlyDictionary<DemoMode, Policy> ByMode = new Dictionary<DemoMode, Policy>
    {
        // Straight to the vendor, no local path.
        //
        // ProtectIp is false because a cloud-only architecture *cannot* redact:
        // deciding which spans are fab IP needs a local model, and there isn't
        // one. That is an architectural consequence, not a missing tick-box.
        [DemoMode.CloudOnly] = new([Tier.T3VendorCloud], ProtectIp: false),

        // Nothing crosses the border, whatever the network is doing — so there
        // is nothing to redact. You do not mask to yourself.
        [DemoMode.EdgeOnly] = new([Tier.T1Device], ProtectIp: false),

        // Both permitted — so the *note* decides, not the connection. The only
        // posture that both crosses a border and can see what it is crossing.
        [DemoMode.Hybrid] = new([Tier.T1Device, Tier.T3VendorCloud], ProtectIp: true),
    };

    public static Policy For(DemoMode mode) => ByMode[mode];
}

// Takes its clients — never builds them. Keeps FabLog.Core free of any
// infrastructure SDK; construction happens in each app's composition root.
public sealed class Tiers(
    IReadOnlyDictionary<Tier, IChatClient> clients,   // all tiers, same type
    IConnectivity net)
{
    public IChatClient this[Tier t] => clients[t];
    public bool Has(Tier t) => clients.ContainsKey(t);

    /// <summary>
    /// For the <b>badge</b>, and for the ledger's wording after the fact.
    /// <para>
    /// Deliberately not consulted before a cloud call. See <see cref="IConnectivity"/>.
    /// </para>
    /// </summary>
    public bool Online => net.CloudReachable;

    public IConnectivity Net => net;
}

#endregion

/// <summary>
/// An <b>observation</b>, not a probe.
/// <para>
/// A probe is a <i>prediction</i> about the next call, when the only thing anyone
/// can actually know is how the <i>last</i> one went. So nothing in FabLog asks
/// whether it is online before trying: <see cref="Router"/> attempts the call and
/// catches, the client records what happened, and this reports it. That is how
/// every real client discovers a dead network, and it means no routing flag exists
/// that could disagree with the transport.
/// </para>
/// </summary>
public interface IConnectivity
{
    /// <summary>Did the most recent attempt to reach T3 complete? Any HTTP status counts — 200, 401, 429. Only a transport failure is "no".</summary>
    bool CloudReachable { get; }

    /// <summary>When it last stopped completing. Lets the badge say "unreachable since 11:14:02" instead of guessing.</summary>
    DateTimeOffset? UnreachableSince { get; }
}

/// <summary>
/// Where the fab's own notes go: <b>every note, in full, in every posture</b>.
/// <para>
/// Not a tier and not a hop — the Central System is a destination on the fab's own
/// network, in the fab's own jurisdiction. It is trusted with the unredacted note
/// precisely because it is the fab's: you do not redact to yourself.
/// </para>
/// <para>
/// ⚠️ <b>Trusted is not the same as reachable.</b> It sits in a different room, and
/// the cleanroom blocks the fab's own LAN exactly as well as it blocks the internet.
/// So this returns <c>false</c> rather than throwing, and the caller <b>queues</b>
/// what it could not deliver. A note is never dropped for being out of range.
/// </para>
/// </summary>
public interface INoteSink
{
    /// <summary>
    /// Returns <c>false</c> when the note could not be delivered — the caller leaves it
    /// <see cref="SyncState.Pending"/> and it is retried later.
    /// <para>
    /// ⚠️ It takes the <b>whole note</b> and no separate text argument: the note already
    /// carries both the readable sentence (<see cref="FabNote.Readable"/>) and the
    /// technician's shorthand (<see cref="FabNote.Raw"/>), and the sink sends both. A
    /// separate text parameter quietly decides which of the two the Central System stores.
    /// </para>
    /// </summary>
    Task<bool> SyncAsync(FabNote note, CancellationToken ct = default);
}

/// <summary>What <c>DiscoverEps()</c> reports, lifted out of the SDK so Core stays dependency-free.</summary>
public sealed record ExecutionProviderInfo(string Name, bool IsRegistered);

/// <summary>The hardware panel's data source. Implemented in FabPad against the Foundry Local SDK.</summary>
public interface IHardwareProbe
{
    IReadOnlyList<ExecutionProviderInfo> DiscoverExecutionProviders();
    string ResolvedModelId { get; }
    string ResolvedExecutionProvider { get; }
}
