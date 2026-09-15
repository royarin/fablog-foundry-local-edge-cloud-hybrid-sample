using FabLog.Core;

namespace FabLog.FabPad;

/// <summary>
/// This device's notes. In memory, nothing persisted.
/// <para>
/// ⚠️ <b>One technician's notes, and that is the architecture, not a shortcut.</b>
/// A device inside an airgap holds what its owner wrote. The device's refusal to
/// answer a cross-note question is true because of this class: the scope is a
/// consequence of where the data is, not of the 1.5B being too small. No bigger
/// model fixes it.
/// </para>
/// </summary>
public sealed class NoteStore
{
    readonly List<FabNote> _notes = [];
    readonly Dictionary<Guid, string> _masked = [];
    readonly Lock _gate = new();

    public event Action? Changed;

    public IReadOnlyList<FabNote> Notes { get { lock (_gate) return [.. _notes]; } }

    public int Count { get { lock (_gate) return _notes.Count; } }

    /// <summary>
    /// 📌 <b>The deferred-delivery mechanism, and it is not a queue.</b>
    /// <para>
    /// There is deliberately no queue class holding a second copy of each note that has not reached
    /// the Central System. The notes are already here, and each one already carries a
    /// <see cref="SyncState"/>. "Pending" is a fact about a note, not a separate collection — so
    /// this is a filter, and the counter on screen is its length.
    /// </para>
    /// <para>
    /// Oldest first, because that is the order they were written and the order they should arrive.
    /// </para>
    /// </summary>
    public IReadOnlyList<FabNote> Pending
    {
        get { lock (_gate) return [.. _notes.Where(n => n.Sync == SyncState.Pending).OrderBy(n => n.At)]; }
    }

    /// <summary>What the counter on screen shows while the technician is inside the cleanroom.</summary>
    public int PendingCount
    {
        get { lock (_gate) return _notes.Count(n => n.Sync == SyncState.Pending); }
    }

    public void Upsert(FabNote note, string? maskedText = null)
    {
        lock (_gate)
        {
            var i = _notes.FindIndex(n => n.Id == note.Id);
            if (i >= 0) _notes[i] = note; else _notes.Add(note);
            if (maskedText is not null) _masked[note.Id] = maskedText;
        }
        Changed?.Invoke();
    }

    /// <summary>The masked form, as sent. Kept so the payload panel shows the literal string, not a re-render.</summary>
    public string? MaskedFor(Guid id) { lock (_gate) return _masked.GetValueOrDefault(id); }

    public FabNote? Find(Guid id) { lock (_gate) return _notes.FirstOrDefault(n => n.Id == id); }

    /// <summary>How many of this device's notes mention a tool — the honest half of the device's refusal.</summary>
    public int Mentions(string toolId) =>
        Notes.Count(n =>
            (n.ToolId?.Equals(toolId, StringComparison.OrdinalIgnoreCase) ?? false)
            || n.Raw.Contains(toolId.Replace("-", ""), StringComparison.OrdinalIgnoreCase)
            || n.Raw.Contains(toolId, StringComparison.OrdinalIgnoreCase));

    public void Clear()
    {
        lock (_gate) { _notes.Clear(); _masked.Clear(); }
        Changed?.Invoke();
    }
}
