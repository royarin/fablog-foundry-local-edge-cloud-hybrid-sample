using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace FabLog.Core;

/// <summary>
/// Belt and braces. Deterministic rules catch the formats we know; the T1 model
/// catches the ones we don't; the result is the union of both.
/// <para>
/// This is a <b>control, not a guarantee</b>, and it is worth stating plainly. The
/// ledger records the exact payload, so a miss is detectable after the fact — where
/// an architecture that sends the whole note leaves nothing to review.
/// </para>
/// </summary>
public static partial class Redaction
{
    #region The jurisdiction rule

    // ── Layer 1 · deterministic ────────────────────────────────────────────
    // Known fab-IP formats. Fast, offline, and it cannot have a bad day.
    static readonly (Regex Rx, string Kind)[] Rules =
    [
        (RecipeId(),    "recipe"),      // RX-7, RP-12  — the process recipe itself
        (RampParam(),   "parameter"),   // 4.5s, 120ms, 45sccm, 12mTorr, 450C
        (YieldFigure(), "yield"),       // 94.2%, 87 %  — uniformity and yield
        (DefectCode(),  "defect"),      // D-1180, DS-44 — defect signatures
    ];

    // ⚠️ Tool IDs and chambers are deliberately NOT fab IP.
    //
    // ETCH-03 and "chamber B" are the metadata the cross-note trend runs on.
    // Masking them would protect nothing — a tool number is not a trade secret —
    // and would destroy the very pattern that makes the supervisor's screen work.
    // The insight without the secret only exists because this line is drawn here
    // and not one step wider.
    [GeneratedRegex(@"\b[A-Z]{2,6}-\d{2,3}\b")]
    private static partial Regex ToolId();

    [GeneratedRegex(@"\bR[A-Z]-\d+\b")]
    private static partial Regex RecipeId();

    [GeneratedRegex(@"\b\d+(?:\.\d+)?\s?(?:s|ms|sccm|mTorr|Torr|°?C|kW|W|nm)\b")]
    private static partial Regex RampParam();

    [GeneratedRegex(@"\b\d+(?:\.\d+)?\s?%")]
    private static partial Regex YieldFigure();

    [GeneratedRegex(@"\bDS?-\d{2,4}\b")]
    private static partial Regex DefectCode();

    /// <summary>Rules only. Runs offline, in microseconds, and is the floor under the model.</summary>
    public static List<IpSpan> DetectByRules(string text)
    {
        var found = new List<IpSpan>();
        foreach (var (rx, kind) in Rules)
            foreach (Match m in rx.Matches(text))
            {
                // A recipe ID looks like a tool ID to a loose regex. Tool IDs stay.
                if (kind != "recipe" && ToolId().IsMatch(m.Value) && !RecipeId().IsMatch(m.Value)) continue;
                if (found.Any(s => s.Text.Equals(m.Value, StringComparison.OrdinalIgnoreCase))) continue;
                found.Add(new IpSpan(m.Value, "", kind, "rules"));
            }
        return found;
    }

    #endregion

    /// <summary>
    /// Layer 2 · the T1 model, then the union. Model failure degrades to
    /// rules-only rather than to nothing — a malformed JSON reply from a 1.5B
    /// must never open the gate.
    /// </summary>
    public static async Task<IReadOnlyList<IpSpan>> DetectAsync(
        string text, IChatClient device, CancellationToken ct = default)
    {
        var spans = DetectByRules(text);

        List<string> fromModel = [];
        try
        {
            var reply = await device.GetResponseAsync(
                [new ChatMessage(ChatRole.System, Prompts.DetectIp), new ChatMessage(ChatRole.User, text)],
                Prompts.TightOptions, ct);
            fromModel = Prompts.ParseStringArray(reply.Text);
        }
        catch
        {
            // Deliberately swallowed. The rules layer already ran, and the
            // ledger will show exactly what crossed. Never fail open.
        }

        foreach (var candidate in fromModel)
        {
            var trimmed = candidate.Trim();
            if (trimmed.Length < 2 || !text.Contains(trimmed, StringComparison.OrdinalIgnoreCase)) continue;
            if (ToolId().IsMatch(trimmed) && !RecipeId().IsMatch(trimmed)) continue;   // tool IDs stay

            var existing = spans.FindIndex(s => s.Text.Equals(trimmed, StringComparison.OrdinalIgnoreCase));
            if (existing >= 0) spans[existing] = spans[existing] with { Source = "rules+model" };
            else spans.Add(new IpSpan(trimmed, "", "model-flagged", "model"));
        }

        // Number the placeholders by first appearance so ⟦R1⟧ is stable across
        // re-processing: the same note must produce the same placeholders every
        // time, or a note routed twice cannot be compared with itself.
        return [.. spans
            .OrderBy(s => text.IndexOf(s.Text, StringComparison.OrdinalIgnoreCase))
            .Select((s, i) => s with { Placeholder = $"⟦R{i + 1}⟧" })];
    }

    /// <summary>Replace every detected span with its placeholder. The mapping never leaves the device.</summary>
    public static string Mask(string text, IReadOnlyList<IpSpan> spans)
    {
        foreach (var s in spans.OrderByDescending(s => s.Text.Length))
            text = Regex.Replace(text, Regex.Escape(s.Text), s.Placeholder, RegexOptions.IgnoreCase);
        return text;
    }

    /// <summary>Put the real values back, locally, after the cloud has done the prose.</summary>
    public static string Unmask(string text, IReadOnlyList<IpSpan> spans)
    {
        foreach (var s in spans)
            text = text.Replace(s.Placeholder, s.Text, StringComparison.Ordinal);
        return text;
    }
}
