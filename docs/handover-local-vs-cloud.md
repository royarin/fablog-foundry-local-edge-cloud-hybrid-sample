# Shift handover — local model vs cloud

2026-11-26. Experiment only: the exact `Prompts.Handover` prompt, `HandoverOptions` and seed digest, through the same `HandoverBrain` parse + validation, against **qwen2.5-1.5b, CUDA variant** (`qwen2.5-1.5b-instruct-cuda-gpu:4`, loaded in the Foundry Local CLI service, OpenAI-compatible endpoint `http://127.0.0.1:56493/v1`, RTX 5070 Ti). Test: `tests/FabLog.DemoTests/LocalLiveTests.cs` (`Category=LocalLive`, opt-in via `FABLOG_LOCAL_ENDPOINT` / `FABLOG_LOCAL_MODEL`). No change to Program.cs, the prompt, or flag defaults.

Set A = plain seed. Set B = seed + the conflict note. 5 counted runs per set, preceded by one uncounted warm-up per set (1.84 / 0.91 s). Latency is wall-clock around `BriefAsync`. Rank columns show **what the model proposed** (`tool / chamber` exactly as it wrote them); the card would show the hub's fallback instead.

| set | run | latency s | JSON valid | source | rank 1 | rank 2 | rank 3 | confidence per rank | rejections |
|---|---|---|---|---|---|---|---|---|---|
| A | 1 | 0.84 | yes | hub | ETCH / ETCH-02 A | ETCH / ETCH-03 B | — | high / medium | rejected x2 (tool/chamber not in the digest) |
| A | 2 | 0.85 | yes | hub | ETCH / ETCH-02 A | ETCH / ETCH-03 B | — | high / medium | rejected x2 (tool/chamber not in the digest) |
| A | 3 | 0.85 | yes | hub | ETCH / ETCH-02 A | ETCH / ETCH-03 B | — | high / medium | rejected x2 (tool/chamber not in the digest) |
| A | 4 | 0.85 | yes | hub | ETCH / ETCH-02 A | ETCH / ETCH-03 B | — | high / medium | rejected x2 (tool/chamber not in the digest) |
| A | 5 | 0.92 | yes | hub | ETCH / ETCH-02 A | ETCH / ETCH-03 B | — | high / medium | rejected x2 (tool/chamber not in the digest) |
| B | 1 | 0.94 | yes | hub | ETCH / ETCH-02 A | ETCH / ETCH-03 B | — | high / medium | rejected x2 (tool/chamber not in the digest) |
| B | 2 | 0.89 | yes | hub | ETCH / ETCH-02 A | ETCH / ETCH-03 B | — | high / medium | rejected x2 (tool/chamber not in the digest) |
| B | 3 | 0.94 | yes | hub | ETCH / ETCH-02 A | ETCH / ETCH-03 B | — | high / medium | rejected x2 (tool/chamber not in the digest) |
| B | 4 | 0.94 | yes | hub | ETCH / ETCH-02 A | ETCH / ETCH-03 B | — | high / medium | rejected x2 (tool/chamber not in the digest) |
| B | 5 | 0.92 | yes | hub | ETCH / ETCH-02 A | ETCH / ETCH-03 B | — | high / medium | rejected x2 (tool/chamber not in the digest) |

- **Fell back to the hub ranking:** 10 of 10 runs (reason each time: "every check from the cloud was rejected by the hub"). Fallback order shown to the user: ETCH-02 A > ETCH-03 B > LITHO-01 A, identical to the hub's own.
- **Rejection reasons:** all 20 rejections are the same: `rank 1 · ETCH A — rejected: tool/chamber is not in the digest` and `rank 2 · ETCH B — …`. The model wrote `"tool": "ETCH"` and `"chamber": "ETCH-02 A"` (the whole label in the chamber field), so the hub could not match either check to a candidate.
- **Determinism:** temperature 0 + seed 42 gave a byte-identical reply on all 6 runs of each set.
- **Latency (10 counted runs):** p50 0.91 s, max 0.94 s, min 0.84 s.

## Verbatim, run 1 (the reply parsed, so no parse failure to show)

**Set A, run 1: raw reply**

```
```json
{
    "checks": [
        {
            "rank": 1,
            "tool": "ETCH",
            "chamber": "ETCH-02 A",
            "action": "Check for drift and trend issues.",
            "why": "A drifting chamber with worsening trend indicates immediate action needed.",
            "evidence": ["n1", "n2"],
            "confidence": "high"
        },
        {
            "rank": 2,
            "tool": "ETCH",
            "chamber": "ETCH-03 B",
            "action": "Monitor for drift and trend issues.",
            "why": "Overdue PM suggests urgent action on chamber health.",
            "evidence": ["pm1", "pm2"],
            "confidence": "medium"
        }
    ]
}
```
```

**Set B, run 1: raw reply**

```
```json
{
    "checks": [
        {
            "rank": 1,
            "tool": "ETCH",
            "chamber": "ETCH-02 A",
            "action": "Check for drifting vacuum levels.",
            "why": "A drifting vacuum with worsening trend indicates potential issues like contamination.",
            "evidence": ["n1", "pm1"],
            "confidence": "high"
        },
        {
            "rank": 2,
            "tool": "ETCH",
            "chamber": "ETCH-03 B",
            "action": "Monitor for drifting uniformity levels.",
            "why": "Overdue PM suggests poor maintenance, while recent trends indicate ongoing issues.",
            "evidence": ["n3", "pm2"],
            "confidence": "medium"
        }
    ]
}
```
```

The `why` of rank 1 in set B, verbatim: "A drifting vacuum with worsening trend indicates potential issues like contamination."

## What else the replies show

- Reply is wrapped in a `json` markdown fence (the brain's extractor handles that) and has **no `would_change_if`** field, although the prompt asks for one; the hub does not require it.
- Only 2 checks, where the prompt asks for 2-3.
- Set B: the model **ignored the conflict** (no `x1` cited, confidence `high` for ETCH-02 A, no mention of conflicting signals), i.e. it did not follow the tuned rule.
- Rank 2's reasoning is wrong on the facts: "Overdue PM" for ETCH-03 B (it is due in 39d; the overdue one is ETCH-02 A), and it cites `pm1` (ETCH-02 A's) for ETCH-03 B.
- Not tested: what a lenient field mapping would have recovered. By reading, rank 1 (ETCH-02 A, n1/n2 or n1/pm1) would have passed the id checks; rank 2 would still fail on its evidence id.

## Summary: local qwen2.5-1.5b (CUDA) vs cloud gpt-5.4-mini

1. **JSON validity:** both 10/10 valid; but usable after validation is 0/10 local (every check rejected on tool/chamber) vs 10/10 cloud.
2. **Rejections:** local 20 across 10 runs (every check, same cause); cloud 0 across 20 runs. The hub's validation did its job, and the user would have seen the hub ranking labelled "no cloud".
3. **Latency:** local p50 0.91 s / max 0.94 s vs cloud p50 2.12 s / max 2.76 s (cloud figures from `docs/handover-live-runs.md`).
