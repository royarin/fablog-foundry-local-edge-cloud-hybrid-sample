using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FabLog.Core;

namespace FabLog.FabPad;

/// <summary>
/// TrustHub, from the device's point of view: <b>a destination on the fab's own
/// network</b> — not a tier, not a hop, not a way out.
/// <para>
/// Deliberately <b>neither</b> an <see cref="Microsoft.Extensions.AI.IChatClient"/>
/// nor an <see cref="IConnectivity"/>. Both would put the hub on the device's route
/// to the vendor:
/// </para>
/// <list type="bullet">
///   <item><b>Not IChatClient.</b> The pad reaches Azure over its own wire
///     (<see cref="CloudClient"/>). A broker in front of the cloud is still the
///     device's way out.</item>
///   <item><b>Not IConnectivity.</b> Polling <c>/health</c> on <i>loopback</i> would
///     keep the badge green through an airgap. Connectivity is an observation of the
///     last T3 attempt instead.</item>
///   <item><b>The text it sends is the raw note.</b> Unredacted, in every posture,
///     including edge-only. The Central System is in the fab's own jurisdiction;
///     you do not mask to yourself, and masking here would only degrade the
///     fleet-wide finding.</item>
/// </list>
/// <para>
/// ⚠️ <b>Its <see cref="HttpClient"/> carries an <c>AirlockHandler</c>, and that is
/// the cleanroom boundary.</b> The Central System is trusted but it is in another
/// room, so from inside the cleanroom this fails exactly like the vendor does — and
/// the caller queues the note rather than losing it. There is deliberately nothing
/// here that tells the hub the door has sealed: the pad cannot reach it to say so,
/// and the hub does not need telling — it never leaves the network.
/// </para>
/// </summary>
public sealed class HubClient(HttpClient http) : INoteSink, IReportSink
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>The shift label travels with every note — the trend counts distinct shifts, not just distinct authors.</summary>
    public string Shift { get; set; } = "";

    /// <summary>Raised after each sync so the UI can show the hub's row count climbing while the pad is "airgapped".</summary>
    public event Action? Synced;

    /// <summary>
    /// Called from <see cref="Router"/> on <b>every</b> note, on every path, after
    /// the stopwatch stops. Returns <c>false</c> rather than throwing, and the caller
    /// <b>queues</b> what did not land — from inside the cleanroom this always fails,
    /// and a note must never be lost for being out of range.
    /// </summary>
    public async Task<bool> SyncAsync(FabNote note, CancellationToken ct = default)
    {
        try
        {
            using var res = await http.PostAsJsonAsync("/sync", new
            {
                id = note.Id,
                author = note.Author,
                shift = Shift,
                toolId = note.ToolId ?? "",
                chamber = note.Chamber ?? "",
                metric = note.Metric ?? "",
                severity = note.Severity.ToString(),
                // 📝 BOTH texts travel, and the pair is deliberate.
                //
                // `text` is what a person should read — the on-device model's sentence
                // where it produced one. It is what the supervisor sees.
                //
                // `raw` is the technician's own shorthand, kept beside it. The Central
                // System is the fab's system of record, and a rewritten version must
                // never REPLACE what somebody actually wrote there — that is precisely
                // what an auditor objects to, in a system whose closing argument is an
                // audit trail.
                text = note.Readable,
                raw = note.Raw,
                at = note.At,
                // Who, when, ON WHAT, and about WHERE. A note that travels this far
                // without the last two is harder to act on than it needs to be.
                deviceId = note.DeviceId,
                cleanroom = note.Cleanroom,
                tags = note.ContextTags,
            }, Json, ct);
            if (res.IsSuccessStatusCode) Synced?.Invoke();
            return res.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    /// <summary>
    /// Submit a finished shift report — the hybrid flow's last step.
    /// <para>
    /// ⚠️ <c>report.Final</c> is the <b>un-masked</b> text: the device restored the
    /// real values locally after the vendor wrote the prose. This server is in the
    /// fab's own jurisdiction and receives the whole thing; only the border crossing
    /// was masked. The technician keeps a local copy regardless, which is what makes
    /// a failure here survivable rather than lossy.
    /// </para>
    /// </summary>
    public async Task<bool> SyncReportAsync(ShiftReport report, CancellationToken ct = default)
    {
        try
        {
            using var res = await http.PostAsJsonAsync("/report", new
            {
                author = report.Author,
                shift = report.Shift,
                deviceId = report.DeviceId,
                cleanroom = report.Cleanroom,
                compiledAt = report.CompiledAt,
                noteCount = report.NoteCount,
                cloudUsed = report.CloudUsed,
                text = report.Final,
            }, Json, ct);
            if (res.IsSuccessStatusCode) Synced?.Invoke();
            return res.IsSuccessStatusCode;
        }
        catch { return false; }
    }

}
