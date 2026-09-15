using System.Text.Json;
using Microsoft.Extensions.AI;

namespace FabLog.Core;

/// <summary>
/// Six prompts — four the device initiates, one for the shift report, one the
/// Central System owns.
/// <para>
/// Designed for a 1.5B: extraction into a fixed schema, classification into a
/// closed enum, short constrained rewrites. Never free-form summarisation on the
/// local model — that is the cloud's job, and the split is the whole architecture.
/// </para>
/// </summary>
public static class Prompts
{
    /// <summary>Hard caps so a CPU-only run cannot drag. Temperature 0 keeps results reproducible.</summary>
    public static readonly ChatOptions TightOptions = new() { MaxOutputTokens = 200, Temperature = 0f, Seed = 42 };

    public static readonly ChatOptions ProseOptions = new() { MaxOutputTokens = 320, Temperature = 0.2f };

    // ── 1 · device: structure + severity + context tags in one call ──────────
    //
    // Context tags are what make a note written hours ago in a sealed room
    // interpretable when it finally lands beside everyone else's. A suggested
    // vocabulary keeps a 1.5B on the rails without making it a closed enum —
    // the technician's own words are the point.
    public const string Extract = """
        You extract structured data from semiconductor fab technician shorthand.
        Reply with ONLY a JSON object. No prose, no markdown fence, no explanation.
        {"toolId":string,"chamber":string,"metric":string,"value":string,"severity":"Routine"|"Watch"|"Escalate","tags":[string]}
        severity: Routine = normal reading. Watch = drifting or near limit. Escalate = out of spec or a stoppage.
        tags: 1-3 short lowercase context labels. Prefer these when they fit:
        maintenance, calibration, excursion, handover, follow-up, routine-check, consumable.
        Use "" for anything the note does not state, and [] if no tag fits.
        """;

    // ── 2 · device: shorthand → a readable log sentence ─────────────────────
    public const string Clean = """
        Rewrite this semiconductor fab shorthand as ONE clear log sentence in plain English.
        Keep every tool ID, chamber, number and unit exactly as written. Invent nothing.
        Reply with the sentence only.
        """;

    // ── 3 · device: the model half of the span detector ─────────────────────
    public const string DetectIp = """
        You flag proprietary semiconductor process IP in a technician's note.
        Proprietary: process recipe names/IDs, recipe parameters with units, yield and
        uniformity figures, defect signature codes.
        NOT proprietary: tool IDs (ETCH-03), chamber letters, dates, shift names, people.
        Reply with ONLY a JSON array of the exact substrings, copied verbatim from the note.
        Example: ["RX-7","4.5s","94.2%"]
        If there are none reply [].
        """;

    // ── 4 · device asks, hub forwards to the cloud: cloud-grade prose ───────
    public const string Polish = """
        You write incident reports for a semiconductor fab.
        Turn the note into a short professional incident report: one paragraph, max 90 words.
        Some values appear as placeholders like ⟦R1⟧. Reproduce every placeholder EXACTLY
        as written. Never guess, expand or describe what a placeholder might contain.
        Reply with the report only.
        """;

    // ── 5 · the shift report's narrative — the hybrid flow's cloud half ──────
    //
    // The technician is OUTSIDE the cleanroom with a working connection, and this
    // is still all the cloud gets: a masked brief. The facts table that goes with
    // it was computed on the device and never leaves. The cloud writes prose about
    // numbers it has never seen.
    public const string Report = """
        You write end-of-shift summary reports for a semiconductor fab.
        You are given a shift's log entries as a bulleted list.
        Write a professional shift summary: 2-3 short paragraphs, max 180 words.
        Lead with anything out of spec or needing follow-up; note routine items briefly.
        Some values appear as placeholders like ⟦R1⟧. Reproduce every placeholder EXACTLY
        as written. Never guess, expand or describe what a placeholder might contain.
        Do not invent tools, numbers or events that are not in the entries.
        Reply with the report only.
        """;

    // ── 6 · the hub's own prompt — the one workload that is not on the device ──
    //
    // ⚠️ It says "a structured digest", not "notes", because no note text crosses
    // wall 2. What the vendor receives is HubStore's digest: columns the hub
    // extracted itself. The vendor does the reasoning without ever seeing a
    // sentence anyone wrote — so the prompt must not invite it to ask for one.
    public const string Trend = """
        You analyse a structured digest of maintenance activity from a semiconductor fab.
        Each line is one note, reduced to columns: timestamp, technician initials, shift,
        tool, chamber, metric, severity. The note text itself is NOT provided and you must
        not ask for it or invent it. "-" means the field was not extracted.
        Find whether any single tool+chamber shows a repeated non-Routine severity across
        MULTIPLE distinct technicians.
        Reply with ONLY a JSON object:
        {"toolId":string,"chamber":string,"symptom":string,"mentions":number,"technicians":number,"shifts":number,"verdict":"Routine"|"Watch"|"Escalate","finding":string}
        "symptom" is the metric column. "finding" is one sentence a shift supervisor would
        act on, describing the pattern only.
        Do NOT put a specific count in "finding" — no number of mentions, technicians or
        shifts, in digits or in words. Say "multiple technicians", never "three technicians".
        If no tool is reported by more than one technician, set verdict to "Routine".
        """;

    // ─────────────────────────────────────────────────────────────────────────
    //  Deterministic parsing. A 1.5B model WILL occasionally emit a markdown
    //  fence, a preamble, or a trailing sentence — handle it, don't hope.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Pull the first balanced JSON object out of a reply, ignoring fences and chatter.</summary>
    public static string? ExtractJsonObject(string? raw) => ExtractBalanced(raw, '{', '}');

    public static string? ExtractJsonArray(string? raw) => ExtractBalanced(raw, '[', ']');

    static string? ExtractBalanced(string? raw, char open, char close)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var start = raw.IndexOf(open);
        if (start < 0) return null;

        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var i = start; i < raw.Length; i++)
        {
            var c = raw[i];
            if (escaped) { escaped = false; continue; }
            if (c == '\\' && inString) { escaped = true; continue; }
            if (c == '"') { inString = !inString; continue; }
            if (inString) continue;
            if (c == open) depth++;
            else if (c == close && --depth == 0) return raw[start..(i + 1)];
        }
        return null;
    }

    /// <summary>Parse the extraction reply. Returns <c>null</c> on anything malformed — the caller falls back.</summary>
    public static Extracted? ParseExtracted(string? raw)
    {
        var json = ExtractJsonObject(raw);
        if (json is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            // Tags may come back empty — the caller fills them from DeriveTags,
            // which needs the note text and so cannot be done here.
            return new Extracted(
                NormaliseToolId(Str(root, "toolId")),
                NormaliseChamber(Str(root, "chamber")),
                Str(root, "metric"), Str(root, "value"),
                Enum.TryParse<Severity>(Str(root, "severity"), ignoreCase: true, out var sev) ? sev : Severity.Routine,
                StrArray(root, "tags"));
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// <c>etch03</c>, <c>ETCH03</c>, <c>Etch 03</c> → <c>ETCH-03</c>.
    /// <para>
    /// ⚠️ <b>Not cosmetic.</b> Measured across execution providers, every model
    /// variant returned the tool ID in a different shape — and the fleet-wide trend
    /// <b>groups by tool ID</b>. A device that syncs <c>ETCH03</c> while the hub
    /// holds <c>ETCH-03</c> yields two groups of one instead of one group of three:
    /// the trend fails silently, with a plausible-looking screen. Canonicalise at
    /// the boundary.
    /// </para>
    /// </summary>
    public static string NormaliseToolId(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var m = System.Text.RegularExpressions.Regex.Match(
            raw.Trim(), @"^([A-Za-z]{2,6})[\s\-_]?(\d{1,3})$");
        return m.Success
            ? $"{m.Groups[1].Value.ToUpperInvariant()}-{int.Parse(m.Groups[2].Value):00}"
            : raw.Trim().ToUpperInvariant();
    }

    /// <summary>"chamber B", "Chamber-B", "b" → "B". Same reason as the tool ID.</summary>
    public static string NormaliseChamber(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var m = System.Text.RegularExpressions.Regex.Match(raw, @"([A-Za-z])\s*$");
        return m.Success ? m.Groups[1].Value.ToUpperInvariant() : raw.Trim().ToUpperInvariant();
    }

    public static List<string> ParseStringArray(string? raw)
    {
        var json = ExtractJsonArray(raw);
        if (json is null) return [];
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind is not JsonValueKind.Array
                ? []
                : [.. doc.RootElement.EnumerateArray()
                        .Where(e => e.ValueKind is JsonValueKind.String)
                        .Select(e => e.GetString()!)
                        .Where(s => !string.IsNullOrWhiteSpace(s))];
        }
        catch (JsonException) { return []; }
    }

    public static TrendFinding? ParseTrend(string? raw)
    {
        var json = ExtractJsonObject(raw);
        if (json is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            // Same canonicalisation as the device side: the hub looks the tool up
            // in its own store by this string, so "ETCH03" here means zero rows.
            return new TrendFinding(
                NormaliseToolId(Str(root, "toolId")), NormaliseChamber(Str(root, "chamber")),
                Str(root, "symptom"),
                Num(root, "mentions"), Num(root, "technicians"), Num(root, "shifts"),
                Enum.TryParse<Severity>(Str(root, "verdict"), ignoreCase: true, out var v) ? v : Severity.Routine,
                Str(root, "finding"));
        }
        catch (JsonException) { return null; }
    }

    static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind is JsonValueKind.String ? p.GetString() ?? "" : "";

    /// <summary>A string array property, tolerant of the model omitting it or filling it with junk.</summary>
    static List<string> StrArray(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind is JsonValueKind.Array
            ? [.. p.EnumerateArray()
                   .Where(x => x.ValueKind is JsonValueKind.String)
                   .Select(x => x.GetString()!.Trim().ToLowerInvariant())
                   .Where(s => s.Length is > 0 and <= 24)
                   .Distinct()
                   .Take(3)]
            : [];

    static int Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind is JsonValueKind.Number && p.TryGetInt32(out var i) ? i : 0;

    /// <summary>
    /// The fallback when the model returns nothing usable. Deterministic, offline,
    /// and good enough that a malformed reply never costs the note its structure.
    /// </summary>
    public static Extracted FallbackExtract(string raw)
    {
        var tool = System.Text.RegularExpressions.Regex.Match(raw, @"\b([A-Z]{2,6})[\s-]?(\d{2,3})\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var chamber = System.Text.RegularExpressions.Regex.Match(raw, @"\bch(?:a?m?b(?:er)?)?\.?\s*([A-C])\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var pct = System.Text.RegularExpressions.Regex.Match(raw, @"\b\d+(?:\.\d+)?\s?%");

        var lower = raw.ToLowerInvariant();
        var severity =
            lower.Contains("off spec") || lower.Contains("out of spec") || lower.Contains("down") || lower.Contains("stopp")
                ? Severity.Escalate
                : lower.Contains("drift") || lower.Contains("watch") || lower.Contains("trend") || lower.Contains("margin")
                    ? Severity.Watch
                    : Severity.Routine;

        return new Extracted(
            tool.Success ? NormaliseToolId($"{tool.Groups[1].Value}{tool.Groups[2].Value}") : "",
            chamber.Success ? chamber.Groups[1].Value.ToUpperInvariant() : "",
            lower.Contains("unif") ? "uniformity" : lower.Contains("particle") ? "particle count" : "",
            pct.Success ? pct.Value : "",
            severity,
            DeriveTags(raw, severity));
    }

    /// <summary>
    /// Context tags without a model. Keyword-driven and deterministic, so a note
    /// still carries its context when the model returns nothing usable — the same
    /// belt-and-braces rule the span detector follows.
    /// </summary>
    public static List<string> DeriveTags(string raw, Severity severity)
    {
        var lower = raw.ToLowerInvariant();
        List<string> tags = [];

        if (lower.Contains("replac") || lower.Contains("swap") || lower.Contains("pm ") || lower.Contains("clean"))
            tags.Add("maintenance");
        if (lower.Contains("calib") || lower.Contains("zero") || lower.Contains("align"))
            tags.Add("calibration");
        if (lower.Contains("off spec") || lower.Contains("out of spec") || lower.Contains("excursion"))
            tags.Add("excursion");
        if (lower.Contains("day shift") || lower.Contains("next shift") || lower.Contains("handover") || lower.Contains("asked"))
            tags.Add("handover");
        if (lower.Contains("keep an eye") || lower.Contains("monitor") || lower.Contains("watch") || lower.Contains("recheck"))
            tags.Add("follow-up");
        if (lower.Contains("conditioner") || lower.Contains("filter") || lower.Contains("consumable"))
            tags.Add("consumable");

        if (tags.Count == 0)
            tags.Add(severity == Severity.Routine ? "routine-check" : "follow-up");

        return [.. tags.Distinct().Take(3)];
    }
}

public sealed record Extracted(
    string ToolId, string Chamber, string Metric, string Value, Severity Severity,
    IReadOnlyList<string>? TagList = null)
{
    /// <summary>Context tags for the note — never null, so callers need no guard.</summary>
    public IReadOnlyList<string> Tags => TagList ?? [];
}

public sealed record TrendFinding(
    string ToolId, string Chamber, string Symptom,
    int Mentions, int Technicians, int Shifts,
    Severity Verdict, string Finding);
