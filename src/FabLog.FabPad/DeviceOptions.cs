using FabLog.Core;
using FabLog.FabPad.Demo;

namespace FabLog.FabPad;

public sealed class DeviceOptions
{
    /// <summary>The deployment posture. Reloads without a restart when the file changes.</summary>
    public DemoMode Mode { get; set; } = DemoMode.CloudOnly;

    public T1Options T1 { get; set; } = new();

    /// <summary>
    /// The device's own wire to the vendor.
    /// <para>
    /// Reaching Azure through the hub would quietly make <c>CloudOnly</c> a posture
    /// the <i>hub</i> holds. Same section shape as TrustHub's — both processes bind
    /// <see cref="T3Options"/> from Core — so the configuration looks identical
    /// wherever it lives.
    /// </para>
    /// <para>
    /// 🔒 Real values live in <c>appsettings.Development.json</c>, which is
    /// gitignored. The committed <c>appsettings.json</c> holds <c>&lt;…&gt;</c>
    /// placeholders, and <see cref="T3Options.IsConfigured"/> treats anything
    /// starting with <c>&lt;</c> as not configured.
    /// </para>
    /// </summary>
    public T3Options T3 { get; set; } = new();

    /// <summary>
    /// Starting state of the simulation switches — the cleanroom door and the drain
    /// interval. Everything they control is driven from here and from the UI; no
    /// external tooling is involved.
    /// </summary>
    public DemoOptions Demo { get; set; } = new();

    /// <summary>
    /// A destination inside the fab, not a route out. Every note is POSTed here in
    /// full, in every mode — including EdgeOnly, including "airgapped", because the
    /// airgap is around the building and the hub is in it.
    /// </summary>
    public string Hub { get; set; } = "https://localhost:5100";

    public string Author { get; set; } = "K. Nagy";

    /// <summary>
    /// ⚠️ Computed, not pinned — <b>the period as well as the date</b>. A literal here
    /// goes stale the day after it is written. The date was computed first and the word
    /// <c>Night</c> left hardcoded, which was worse than either: the handheld announced
    /// "Night" at half past ten in the morning while the Central System's rows for the
    /// same hour said "Morning", so the Shift column on FabDesk showed live notes
    /// refusing to group with the corpus they belong to. Both halves float now, and the
    /// seed corpus (<c>HubStore</c>, in FabLog.TrustHub) floats the same way, so the two
    /// stay in step without either being edited by hand.
    /// Set <c>FabLog:Shift</c> in config to pin a specific value instead.
    /// </summary>
    public string Shift { get; set; } = ShiftAt(DateTimeOffset.Now);

    /// <summary>
    /// The fab's two twelve-hour shifts, in the seed corpus's own vocabulary:
    /// <b>Morning</b> 06:00–17:59 and <b>Night</b> 18:00–05:59. Read off the local
    /// wall clock, because that is the one the technician holding the device is on.
    /// <para>
    /// A night shift is named for the day it <i>began</i>, so in the small hours it
    /// still carries yesterday's date — which is how the seed's own after-midnight
    /// rows are labelled, and what keeps a 02:00 note from splitting its shift in
    /// two on the supervisor's screen.
    /// </para>
    /// </summary>
    public static string ShiftAt(DateTimeOffset at)
    {
        var morning = at.Hour is >= 6 and < 18;
        var began = morning || at.Hour >= 18 ? at.Date : at.Date.AddDays(-1);
        return $"{(morning ? "Morning" : "Night")} {began:yyyy-MM-dd}";
    }

    /// <summary>
    /// Which physical handheld this is. Travels with every note.
    /// <para>
    /// Two technicians can share a shift and a tool; the device id is what
    /// distinguishes the thing that was actually in the room.
    /// </para>
    /// </summary>
    public string DeviceId { get; set; } = "PAD-07";

    /// <summary>Which cleanroom the technician is working in. The fab has more than one.</summary>
    public string Cleanroom { get; set; } = "CR-2";
}

public sealed class T1Options
{
    public string ModelAlias { get; set; } = "qwen2.5-1.5b";

    /// <summary>The smaller model. Switching is a config edit, not a rebuild.</summary>
    public string FallbackAlias { get; set; } = "qwen2.5-0.5b";

    /// <summary>
    /// ⚠️ <b>Measured, not assumed.</b> The obvious rule — "always resolve by alias,
    /// never by model ID" — silently loses on a machine with both a discrete and an
    /// integrated GPU: registering the OpenVINO provider makes alias resolution
    /// prefer <c>…-openvino-gpu</c>, which is the <i>Intel iGPU</i>, while the NVIDIA
    /// card sits idle behind <c>…-cuda-gpu</c>. That is roughly a 3× difference in
    /// first-token latency.
    /// <para>
    /// So the alias still chooses the <i>model</i> and this list chooses the
    /// <i>silicon</i>, most-preferred first. First match in the catalogue wins;
    /// if none match, the SDK's own resolution is used unchanged.
    /// </para>
    /// </summary>
    // ⚠️ Left empty on purpose. appsettings.json is the single source of truth for
    // this list — Microsoft.Extensions.Configuration's array binding *appends* to a
    // non-empty default instead of replacing it (dotnet/runtime#46988, #70150). A
    // non-empty default here would double every entry: "cuda-gpu → … → generic-cpu
    // → cuda-gpu → …".
    public string[] VariantPreference { get; set; } = [];

    /// <summary>
    /// Pin the execution provider. Set to <c>generic-cpu</c> to run the same build
    /// with no GPU at all — which is what answers "what does this cost on the
    /// hardware a fab actually has".
    /// </summary>
    public string ForceVariant { get; set; } = "";
}
