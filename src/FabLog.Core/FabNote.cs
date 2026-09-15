namespace FabLog.Core;

public enum Severity { Routine, Watch, Escalate }

/// <summary>
/// Whether this note has reached the Central System yet — and <b>nothing else</b>.
/// <para>
/// ⚠️ There is deliberately no third value meaning "this one is never going". That is not a case:
/// <i>every</i> note is owed to the fab's own server, whatever posture produced it. Whether
/// anything crossed the jurisdiction boundary is a different question, and the decision log
/// already answers it exactly, per route.
/// </para>
/// <para>
/// Two values, one question. <see cref="Pending"/> is also the whole of the deferred-sync
/// mechanism: the note IS the queue entry.
/// </para>
/// </summary>
public enum SyncState { Pending, Synced }

/// <summary>A span of the raw note that must not cross a boundary unmasked.</summary>
/// <param name="Placeholder">What the cloud sees instead — <c>⟦R1⟧</c>.</param>
/// <param name="Source">Which detection layer caught it: <c>rules</c>, <c>model</c>, or <c>rules+model</c>.</param>
public sealed record IpSpan(string Text, string Placeholder, string Kind, string Source);

/// <summary>
/// A technician's note, and everything needed to make sense of it later.
/// <para>
/// <b>The metadata is not decoration.</b> A note written in an airgapped room may
/// not reach the Central System for hours, and when it does it lands beside notes
/// from other people, other devices, other cleanrooms. <i>Who wrote it, when, on
/// what, and about which part of the fab</i> is what makes a late-arriving note
/// interpretable instead of merely present.
/// </para>
/// </summary>
/// <param name="DeviceId">
/// Which handheld captured this. Two technicians can share a shift and a tool; the
/// device is what distinguishes the physical thing that was in the room.
/// </param>
/// <param name="Cleanroom">
/// Which cleanroom the note was written in. The fab has more than one, and a trend
/// confined to a single room means something different from one that spans them.
/// </param>
/// <param name="Tags">
/// Free-form context tags captured with the note — <c>maintenance</c>,
/// <c>handover</c>, <c>follow-up</c>. Deliberately not a closed enum: this is the
/// technician's own vocabulary, and the value is that it survives the trip.
/// </param>
public sealed record FabNote(
    Guid Id,
    string Raw,
    string Author,
    DateTimeOffset At,
    string? Cleaned = null,
    string? ToolId = null,
    string? Chamber = null,
    string? Metric = null,
    string? Value = null,
    Severity Severity = Severity.Routine,
    IReadOnlyList<IpSpan>? IpSpans = null,
    SyncState Sync = SyncState.Pending,
    string? Polished = null,
    string DeviceId = "",
    string Cleanroom = "",
    IReadOnlyList<string>? Tags = null)
{
    public IReadOnlyList<IpSpan> Spans => IpSpans ?? [];

    public IReadOnlyList<string> ContextTags => Tags ?? [];

    /// <summary>
    /// The note as a person should read it: the on-device model's sentence where it produced one,
    /// the technician's own shorthand where it did not.
    /// <para>
    /// This is what the Central System stores as the note's text — and <see cref="Raw"/> travels
    /// with it, because a converted note must never <i>replace</i> what somebody actually wrote in
    /// the system of record.
    /// </para>
    /// </summary>
    public string Readable => string.IsNullOrWhiteSpace(Cleaned) ? Raw : Cleaned!;

    public static FabNote New(string raw, string author, string deviceId = "", string cleanroom = "") =>
        new(Guid.NewGuid(), raw, author, DateTimeOffset.UtcNow, DeviceId: deviceId, Cleanroom: cleanroom);
}

#region The audit trail

// Every Router path writes one. The router already knows all of these facts at the
// moment it decides, so the audit trail costs nothing to produce.
public sealed record LedgerEntry(
    Guid NoteId,
    Tier Tier,              // where the user-visible answer was produced
    string Path,            // "T3" / "T1" / "T1→T3" / "T3 ✖" — the whole hop
    string Model,
    // 🔍 The column that earns the ledger its keep. "policy: edge only" and "vendor
    // unreachable" are DIFFERENT WORDS for two situations that look identical on
    // screen: a choice the company made, and a building the technician walked into.
    string Reason,
    int ElapsedMs,
    string PayloadSent,     // the LITERAL text, not a hash — the UI renders this verbatim
    DateTimeOffset At)
{
    /// <summary>Nothing crossed a boundary at all. The strongest row in the table.</summary>
    public bool NothingLeftTheDevice => PayloadSent.Length == 0;
}

#endregion

/// <summary>In-memory and append-only. Nothing is persisted: the data lives as long as the process does.</summary>
public sealed class Ledger
{
    readonly List<LedgerEntry> _entries = [];
    readonly Lock _gate = new();

    public void Write(LedgerEntry e) { lock (_gate) _entries.Add(e); }

    public IReadOnlyList<LedgerEntry> Entries { get { lock (_gate) return _entries.ToArray(); } }

    public IReadOnlyList<LedgerEntry> For(Guid noteId) =>
        Entries.Where(e => e.NoteId == noteId).ToArray();

    public void Clear() { lock (_gate) _entries.Clear(); }
}
