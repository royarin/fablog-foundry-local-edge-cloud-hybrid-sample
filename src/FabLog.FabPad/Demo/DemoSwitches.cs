using FabLog.Core;

namespace FabLog.FabPad.Demo;

/// <summary>
/// The simulation's control surface, in one object.
/// <para>
/// <b>Everything the app can be made to do at runtime is a toggle or a config
/// value, and every toggle is backed by a real mechanism.</b> Nothing depends on
/// disabling a network adapter, and nothing requires a rebuild.
/// </para>
/// <para>
/// The initial state of each switch comes from <c>FabLog:Demo</c>, so "how the app
/// starts" is configuration too — the offline half can be exercised by editing a
/// file, without touching the UI.
/// </para>
/// </summary>
public sealed class DemoSwitches : IAirlock
{
    bool _padOnNetwork = true;

    /// <summary>
    /// 🚪 <b>The cleanroom door</b>, as one bool — and it is a <i>position</i>, not a
    /// network state.
    /// <para>
    /// It does <i>not</i> tell the app it is offline — there is no such property for
    /// it to set. It is read in exactly one place, <see cref="AirlockHandler"/>,
    /// which sits below the Azure client and fails the socket. Everything above that
    /// is the real path finding out by failing, which is the only way any real client
    /// has ever discovered a dead network.
    /// </para>
    /// <para>
    /// The <c>if</c> lives in the socket, not in the routing code — which is why the
    /// UI labels this for a person rather than a wire: <b>technician at the bench /
    /// in the cleanroom</b>.
    /// </para>
    /// </summary>
    public bool PadOnNetwork
    {
        get => _padOnNetwork;
        set
        {
            if (_padOnNetwork == value) return;
            _padOnNetwork = value;
            Changed?.Invoke();
        }
    }

    /// <summary>True while the technician is inside the cleanroom — the inverse, for the UI to bind to.</summary>
    public bool InCleanroom
    {
        get => !PadOnNetwork;
        set => PadOnNetwork = !value;
    }

    public event Action? Changed;

    public void Toggle() => PadOnNetwork = !PadOnNetwork;
}

/// <summary>Binds <c>FabLog:Demo</c>.</summary>
public sealed class DemoOptions
{
    /// <summary>Start the app already inside the cleanroom, without touching the UI.</summary>
    public bool PadOnNetwork { get; set; } = true;

    /// <summary>
    /// How often the drain loop retries whatever is waiting.
    /// <para>
    /// ⚠️ This is <b>not</b> a poll for connectivity — there is no such thing in this
    /// app. It is how often the device <i>tries to send</i>, and a send succeeding is
    /// how it discovers the signal is back. Two seconds makes recovery prompt without
    /// turning discovery into a fiction; every failed attempt costs microseconds
    /// through <see cref="AirlockHandler"/> and writes nothing.
    /// </para>
    /// </summary>
    public int DrainIntervalSeconds { get; set; } = 2;
}
