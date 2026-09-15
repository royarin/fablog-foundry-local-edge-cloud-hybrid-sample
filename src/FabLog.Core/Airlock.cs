using System.Net.Sockets;

namespace FabLog.Core;

/// <summary>
/// <b>Where the pad is</b> — not what the network is doing.
/// <para>
/// The name matters. <c>InternetUp</c> would be a statement about a network, and
/// invites <i>"so buy better wifi"</i>. A cleanroom is not an outage: it is a room
/// that blocks all radio on purpose, and the technician walked into it. One bool,
/// and it describes a <b>position</b>.
/// </para>
/// <para>
/// FabPad's <c>DemoSwitches</c> implements it from the cleanroom toggle. Nothing in
/// TrustHub implements it — the hub is never inside the cleanroom.
/// </para>
/// </summary>
public interface IAirlock
{
    /// <summary>True when the pad is outside the cleanroom and has radio. False when the door is sealed.</summary>
    bool PadOnNetwork { get; }
}

/// <summary>
/// The whole offline simulation, in a dozen lines: a <see cref="DelegatingHandler"/>
/// that refuses to dial when the pad is inside the cleanroom.
///
/// <para>
/// The obvious alternative — <c>Disable-NetAdapter -Name *</c> in an elevated shell —
/// proves the wrong claim. A cut cable shows the <i>wire</i> is down, but the thing
/// worth ruling out is <c>if (offline) { queue(); }</c> sitting in the app, on the
/// other side of a boundary a terminal never crosses.
/// </para>
///
/// <para><b>Three properties make the handler the better mechanism:</b></para>
/// <list type="number">
///   <item>It sits <b>below the thing under test</b>. The Azure client, the
///     <c>IChatClient</c>, the <c>Router</c>, the sync path — all real code,
///     unmodified, all finding out by failing.</item>
///   <item>It <b>throws what Windows throws.</b> <c>WSAHOST_NOT_FOUND</c> is the real
///     error behind a missing NIC, so the message the UI renders is the real one.</item>
///   <item>It is small enough to read end to end, so nothing about the simulation has
///     to be taken on trust.</item>
/// </list>
///
/// <para>
/// ⚠️ <b>There is no loopback exemption, and its absence is the design.</b> A clause
/// like <c>airlock.PadOnNetwork || request.RequestUri.IsLoopback</c> would keep
/// TrustHub reachable from inside the cleanroom, contradicting the premise the whole
/// scenario rests on: a cleanroom RF-shields against <b>all</b> radio, the fab's own
/// LAN included.
/// </para>
/// <para>
/// So the cleanroom boundary is not an expression inside this class — it is
/// <b>which clients get this handler</b>. FabPad's two outbound clients both have it
/// (<c>CloudClient</c> and <c>HubClient</c>); the on-device model does not, because
/// <c>FoundryChatClient</c> never constructs an <see cref="HttpClient"/> at all. The
/// local model is out of this mechanism's reach <i>structurally</i>, not by exception.
/// </para>
/// <para>
/// It also fails in microseconds, and does so identically every run. A real DNS
/// failure with adapters down takes anywhere from 200 ms to 90 s depending on cache
/// and proxy state — not a useful variable to carry into a latency comparison.
/// </para>
/// </summary>
public sealed class AirlockHandler(IAirlock airlock) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
        => airlock.PadOnNetwork
            ? base.SendAsync(request, ct)
            : throw new HttpRequestException(
                $"No such host is known. ({request.RequestUri!.Host}:{request.RequestUri.Port})",
                new SocketException((int)SocketError.HostNotFound));
}
