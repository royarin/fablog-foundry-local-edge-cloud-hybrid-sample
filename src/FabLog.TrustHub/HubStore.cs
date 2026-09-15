using System.Text.Json;
using System.Text.Json.Serialization;
using FabLog.Core;

namespace FabLog.TrustHub;

/// <summary>
/// A note as the hub holds it — and it holds the <b>real text</b>.
/// <para>
/// ⚠️ <b>The text is not masked, and that is the trust boundary working.</b>
/// Redacting before talking to the hub would put a border between a technician
/// and their own employer's server, inside their own employer's building. It
/// would also make the fleet-wide trend worse for no gain: the hub would be
/// grouping on text it had been deliberately prevented from reading.
/// </para>
/// <para>
/// The airgap goes around the <i>fab</i>, and the hub is inside it. So: every
/// note, in full, in every posture — including EdgeOnly, including while the pad
/// calls itself airgapped. Redaction is a <b>border</b> control, and the border
/// is wall 2, which is where <see cref="HubStore.AsDigest"/> stands.
/// </para>
/// </summary>
/// <param name="DeviceId">
/// Which handheld captured it. Arrives with the note and is never inferred here —
/// the Central System cannot know what it was not told.
/// </param>
/// <param name="Cleanroom">
/// Which cleanroom it came from. A pattern confined to one room means something
/// different from one that spans two, and only this field can tell them apart.
/// </param>
/// <param name="Tags">The technician's own context labels, carried through intact.</param>
/// <param name="Text">
/// The note as a person should read it — the on-device model's sentence where it produced one.
/// This is the <i>converted</i> note that gets synchronised, and it is what FabDesk renders.
/// </param>
/// <param name="Raw">
/// The technician's own shorthand, kept beside the converted text.
/// <para>
/// ⚠️ <b>Both, not either.</b> This server is the fab's system of record, and a rewritten version
/// must never <i>replace</i> what somebody actually wrote in one. Falls back to
/// <see cref="Text"/> for seed rows written before the distinction existed.
/// </para>
/// </param>
public sealed record HubNote(
    Guid Id,
    string Author,
    string Shift,
    string ToolId,
    string Chamber,
    string Metric,
    [property: JsonConverter(typeof(JsonStringEnumConverter<Severity>))] Severity Severity,
    string Text,
    DateTimeOffset At,
    string DeviceId = "",
    string Cleanroom = "",
    IReadOnlyList<string>? Tags = null,
    string Raw = "")
{
    public IReadOnlyList<string> ContextTags => Tags ?? [];

    /// <summary>The original shorthand, or the readable text when a row predates the split.</summary>
    public string Original => string.IsNullOrWhiteSpace(Raw) ? Text : Raw;
}

/// <summary>
/// A submitted shift report, as the Central System holds it.
/// <para>
/// ⚠️ <b>What arrives here is the FINISHED report — real values restored.</b> The
/// device un-masked it locally before submitting, because the Central System is in
/// the fab's own jurisdiction and is trusted with the whole thing. Only the trip to
/// the vendor was masked. That asymmetry is the entire architecture in one object.
/// </para>
/// </summary>
public sealed record SubmittedReport(
    string Author,
    string Shift,
    string DeviceId,
    string Cleanroom,
    DateTimeOffset CompiledAt,
    int NoteCount,
    bool CloudUsed,
    string Text);

/// <summary>
/// In memory, seeded from <c>Data/hub-notes.json</c>. Nothing is persisted: the
/// data lives as long as the process does.
/// <para>
/// ⚠️ <b>The seed corpus is load-bearing.</b> It holds exactly <b>two</b> ETCH-03
/// chamber-B uniformity complaints, from two technicians on two shifts. Two is
/// not a trend — the third is the one written on the device, and it reaches the
/// hub immediately, because the hub is inside the airgap. Add a third complaint
/// here and the trend query has nothing left to discover.
/// </para>
/// </summary>
public sealed class HubStore
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = true,
    };

    readonly List<HubNote> _notes = [];
    readonly List<HubNote> _seed;
    readonly List<SubmittedReport> _reports = [];
    readonly Lock _gate = new();

    /// <param name="seedPath">Where <c>hub-notes.json</c> lives.</param>
    /// <param name="shiftSeedToToday">
    /// ⚠️ <b>Defaults to <c>false</c> on purpose — tests rely on the seed's exact,
    /// literal dates and must not shift under them.</b> The running app
    /// (<c>Program.cs</c>) passes <c>true</c>: a seed pinned to a calendar date
    /// reads as "yesterday and this morning" on exactly one day, and quietly ages
    /// into "last week" after that.
    /// </param>
    public HubStore(string seedPath, bool shiftSeedToToday = false)
    {
        _seed = File.Exists(seedPath)
            ? JsonSerializer.Deserialize<List<HubNote>>(File.ReadAllText(seedPath), Json) ?? []
            : [];

        if (shiftSeedToToday && _seed.Count > 0)
            _seed = ShiftToToday(_seed);

        Reset();
    }

    /// <summary>How far behind "now" the newest seeded note is guaranteed to land.</summary>
    static readonly TimeSpan Headroom = TimeSpan.FromHours(1);

    /// <summary>
    /// Moves the whole seed by a whole-day offset, anchored on its own latest date,
    /// so it always reads as "yesterday night" and "this morning" relative to
    /// whatever day the app happens to start on.
    /// <para>
    /// A whole-day offset, not a duration — every note keeps its exact time-of-day
    /// and its exact gap from every other note. The "two shifts, two days, nobody
    /// overlapping" structure the trend query depends on is preserved by
    /// construction, not re-derived, and <c>SeedCount</c>/<c>notes:12</c> on
    /// <c>/health</c> never moves either.
    /// </para>
    /// <para>
    /// ⚠️ <b>…and then one conditional slide, which is what makes the supervisor's
    /// newest-first list mean anything.</b> The seed's morning shift ends at 11:20
    /// UTC. A note written now is stamped <c>UtcNow</c>, so on any run that starts
    /// before that it sorts into the MIDDLE of the seed: the browse list puts a
    /// seeded note on top and buries the one just written. So when the day-shifted
    /// seed would still end at or after now, the whole block slides back by the
    /// difference plus <see cref="Headroom"/>. Relative spacing survives — it is
    /// still ONE offset applied to every row.
    /// </para>
    /// <para>
    /// It also keeps a freshly synced note last in the ascending sources table under
    /// a trend query, which holds only while that note is newer than every seeded
    /// note for the same tool and chamber. Without the slide it stopped being newer
    /// for any start earlier than 09:02 local.
    /// </para>
    /// <para>
    /// The slide costs time-of-day fidelity on the oldest rows — a note labelled
    /// <c>Night</c> can show an early-evening clock time. Nothing reads a seed note's
    /// clock aloud, and the <c>Shift</c> column still reads correctly. Valid for any
    /// start from roughly <b>07:40 local</b>; earlier than that the slide would
    /// exceed 6h41m and relabel the morning rows onto the previous day. Written down
    /// rather than guarded: the seed is a fixture in <c>hub-notes.json</c>, and
    /// re-cutting its timestamps is the fix if you need an earlier start.
    /// </para>
    /// </summary>
    static List<HubNote> ShiftToToday(List<HubNote> seed)
    {
        var now = DateTimeOffset.Now;
        var newest = seed.Max(n => n.At);

        // Whole days first — this is the part that keeps every time-of-day exact.
        var offset = TimeSpan.FromDays(now.Date.Subtract(newest.Date).Days);

        // Then, only if the seed would still outrank a note written right now,
        // slide the whole block behind us by the smallest amount that fixes it.
        var tail = newest + offset;
        if (tail > now - Headroom) offset -= tail - (now - Headroom);

        if (offset == TimeSpan.Zero) return seed;

        return [.. seed.Select(n =>
        {
            var shiftedAt = n.At + offset;
            var period = n.Shift.Split(' ', 2)[0];
            return n with { At = shiftedAt, Shift = $"{period} {shiftedAt:yyyy-MM-dd}" };
        })];
    }

    /// <summary>How many notes the hub was seeded with, before any device synced.</summary>
    public int SeedCount => _seed.Count;

    public int Count { get { lock (_gate) return _notes.Count; } }

    public IReadOnlyList<HubNote> All
    {
        get { lock (_gate) return [.. _notes.OrderBy(n => n.At)]; }
    }

    /// <summary>
    /// <b>Upsert</b>, keyed by note id. Idempotent, and the later version wins.
    /// <para>
    /// ⚠️ <b>Upsert, not insert-or-reject, and the difference is not cosmetic.</b>
    /// Rejecting an id it had already seen would be correct if a note synced
    /// exactly once. Every note syncs the moment it is written — so a note first
    /// logged in CloudOnly arrives with a blank <c>ToolId</c> and <c>Severity</c>
    /// (there was no local model to extract them), and re-processing the same note
    /// in Hybrid would be <i>rejected</i>, leaving the blank row in place and
    /// silently breaking the trend query's grouping.
    /// </para>
    /// <para>
    /// Replacing in place keeps the property that actually matters — a retried
    /// sync cannot double-count a complaint — while letting the richer version of
    /// a note win. Returns true when the note was new.
    /// </para>
    /// </summary>
    public bool Add(HubNote note)
    {
        lock (_gate)
        {
            var i = _notes.FindIndex(n => n.Id == note.Id);
            if (i < 0) { _notes.Add(note); return true; }
            _notes[i] = note;
            return false;
        }
    }

    public IReadOnlyList<HubNote> For(string toolId) =>
        [.. All.Where(n => n.ToolId.Equals(toolId, StringComparison.OrdinalIgnoreCase))];

    // ── Submitted shift reports ─────────────────────────────────────────────
    // Seeded with nothing: the one the supervisor sees is the one compiled live.

    public IReadOnlyList<SubmittedReport> Reports
    {
        get { lock (_gate) return [.. _reports.OrderByDescending(r => r.CompiledAt)]; }
    }

    public int ReportCount { get { lock (_gate) return _reports.Count; } }

    /// <summary>Latest submission per author+shift wins — re-compiling must not stack up duplicates.</summary>
    public void AddReport(SubmittedReport report)
    {
        lock (_gate)
        {
            var i = _reports.FindIndex(r => r.Author == report.Author && r.Shift == report.Shift);
            if (i >= 0) _reports[i] = report; else _reports.Add(report);
        }
    }

    /// <summary>Back to the seed. Bound to <c>POST /reset</c> so a run can be started over in one call.</summary>
    public void Reset()
    {
        lock (_gate) { _notes.Clear(); _notes.AddRange(_seed); _reports.Clear(); }
    }

    /// <summary>
    /// 🔒 <b>Wall 2, expressed as a projection</b> — and the method to point at
    /// when someone asks what the hub gives away.
    /// <para>
    /// The hub holds every technician's note in full. That is exactly why nothing
    /// it holds may leave verbatim: it is the richest store in the system and it
    /// sits one hop from a vendor. So what crosses wall 2 is a <b>digest</b> —
    /// structured columns the hub extracted itself, and <b>no note body at all</b>.
    /// </para>
    /// <para>
    /// This is a different mechanism from the device's, on purpose. The pad masks
    /// spans because it has a local model that can find them. The hub has no local
    /// model, so it does not attempt to redact prose — it simply never sends prose.
    /// A redactor you cannot run is not a control; a projection you cannot widen is.
    /// </para>
    /// <para>
    /// The net effect: the vendor does the reasoning, and has never seen a
    /// sentence anyone wrote.
    /// </para>
    /// </summary>
    public string AsDigest() =>
        string.Join("\n", All.Select(n => string.Join(" | ",
            n.At.ToString("yyyy-MM-dd HH:mm"),
            $"tech={Initials(n.Author)}",
            $"shift={n.Shift}",
            $"room={Blank(n.Cleanroom)}",
            $"tool={Blank(n.ToolId)}",
            $"chamber={Blank(n.Chamber)}",
            $"metric={Blank(n.Metric)}",
            $"severity={n.Severity}")));

    // ⚠️ Note what is NOT in that projection, and why each one is absent:
    //
    //   • the note text      — the whole point. Free-form prose is where the fab's
    //                          IP actually lives, and this store holds it in full.
    //   • the context tags   — also free-form, also the technician's own words.
    //                          Generic-looking today is not a guarantee about
    //                          tomorrow, and the finding does not need them.
    //   • the device id      — identifies a physical asset. Adds nothing to a
    //                          cross-technician pattern.
    //   • author names       — personal data crossing a border, which is the other
    //                          regulation in the room. Initials are enough to COUNT
    //                          distinct people, which is all the model is asked to do.
    //
    // The cleanroom DOES cross: it is a closed set of room labels, and without it a
    // pattern spanning two cleanrooms cannot be told apart from one confined to a
    // single room — which is a different finding with a different response.

    /// <summary>Even the author is reduced — the vendor needs to count distinct technicians, not name them.</summary>
    static string Initials(string author) =>
        string.Concat(author.Split([' ', '.'], StringSplitOptions.RemoveEmptyEntries)
            .Select(p => char.ToUpperInvariant(p[0]))) is { Length: > 0 } s ? s : "?";

    static string Blank(string? s) => string.IsNullOrWhiteSpace(s) ? "-" : s;
}
