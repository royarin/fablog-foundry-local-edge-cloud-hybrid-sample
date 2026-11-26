# Shift handover — live runs (gpt-5.4-mini, via `POST /handover`)

2026-11-26. Hub on the current branch **including the prompt tune** (conflict rule). Flag on via env var, no code or prompt changed for these runs. Runs were sequential; `/reset` before each set, none inside a set.

- Set A: `/reset`, then 5 runs on the plain seed.
- Set B: `/reset`, `/sync` the demo.http conflict note (ETCH-02 A, Routine, second technician), then 5 runs.

| set | run | latency s | JSON valid | source | rank 1 | rank 2 | rank 3 | confidence per rank | rejections |
|---|---|---|---|---|---|---|---|---|---|
| A | 1 | 2.76 | yes | cloud | ETCH-02 A | ETCH-03 B | LITHO-01 A | high / high / medium | 0 |
| A | 2 | 2.12 | yes | cloud | ETCH-02 A | ETCH-03 B | LITHO-01 A | high / high / medium | 0 |
| A | 3 | 2.35 | yes | cloud | ETCH-02 A | ETCH-03 B | LITHO-01 A | high / high / medium | 0 |
| A | 4 | 2.13 | yes | cloud | ETCH-02 A | ETCH-03 B | LITHO-01 A | high / high / medium | 0 |
| A | 5 | 2.69 | yes | cloud | ETCH-02 A | ETCH-03 B | LITHO-01 A | high / high / medium | 0 |
| B | 1 | 2.33 | yes | cloud | ETCH-02 A | ETCH-03 B | LITHO-01 A | medium / high / high | 0 |
| B | 2 | 2.11 | yes | cloud | ETCH-02 A | ETCH-03 B | LITHO-01 A | medium / high / high | 0 |
| B | 3 | 2.06 | yes | cloud | ETCH-02 A | ETCH-03 B | ETCH-05 C | medium / high / high | 0 |
| B | 4 | 2.06 | yes | cloud | ETCH-02 A | ETCH-03 B | LITHO-01 A | medium / high / high | 0 |
| B | 5 | 2.06 | yes | cloud | ETCH-02 A | ETCH-03 B | ETCH-05 C | medium / high / high | 0 |

Latency is wall-clock around the HTTP call. Hub ranking (both sets): ETCH-02 A 79 > ETCH-03 B 71 > LITHO-01 A 46 > ETCH-05 C 43.

## Summary

- Cloud rank 1 differed from the hub's rank 1 (ETCH-02 A): **0 of 10** runs.
- Latency over all 10 runs: **p50 2.125 s, max 2.76 s** (min 2.06 s).
- JSON valid 10/10, source cloud 10/10, rejections 0 in every run.
- Rank 3: set A always LITHO-01 A; set B LITHO-01 A in 3/5 and ETCH-05 C in 2/5.
- Confidence: set A high / high / medium in 5/5; set B medium / high / high in 5/5.

## "why" — one typical run per set (run 1), verbatim

**Set A, run 1**

1. ETCH-02 A: Vacuum is drifting +2.7σ and worsening over 3 shifts, and this chamber is overdue for PM by 7d. It also has 6 next-shift lots at risk, so this is the most urgent check.
2. ETCH-03 B: Uniformity is drifting -3.3σ and worsening over 3 shifts, with 9 next-shift lots at risk. PM is not due yet, so this looks more like a process drift issue than maintenance overdue.
3. LITHO-01 A: Overlay is +2.7σ off baseline, but the trend is flat over 3 shifts and PM is not due for 16d. With 7 lots next shift, it is worth checking, but it is less urgent than the worsening etch chambers.

**Set B, run 1**

1. ETCH-02 A: Vacuum is drifting +2.7σ and worsening over 3 shifts, and the chamber is overdue for PM by 7d. It also has 6 lots next shift, so the risk is immediate; signals conflict between Watch and Routine, so confidence is not high.
2. ETCH-03 B: Uniformity is drifting -3.3σ and worsening over 3 shifts, with 9 lots at risk next shift. PM is not due yet, but the signal is persistent and this chamber has the most lots at risk among the remaining chambers.
3. LITHO-01 A: Overlay is +2.7σ off baseline, but the trend is flat over 3 shifts and PM is still due in 16d. With 7 lots next shift, it is important but less urgent than the two ETCH chambers.

