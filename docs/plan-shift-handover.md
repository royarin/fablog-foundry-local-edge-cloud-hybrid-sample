# Plan: Shift handover brief (hub → cloud, "derived signals, not raw values")

Status: **plan only, nothing built.** Written 2026-11-26 for review before the 17:00 talk.
Line numbers are from the working tree as read today.

---

## 0. What I found, and three places where the brief and the code disagree

### Confirmed (file:line)

| Item | Finding |
|---|---|
| `HubStore.AsDigest` | `src/FabLog.TrustHub/HubStore.cs:275-284`. 8 columns: timestamp, `tech=<initials>`, `shift`, `room`, `tool`, `chamber`, `metric`, `severity`. No note text, tags, device id or full author name (rationale comment at `:286-301`). One line per note over `All` (chronological, `:196-199`). |
| Digest is pinned by a test | `TrendTests.cs:102-126` asserts exactly 8 columns; `:71-99` and `:129-149` assert no body, no tags, no names. **`AsDigest` must not change.** The handover digest has to be a separate projection. |
| `HubBrain.AnalyseAsync` | `HubBrain.cs:47-98`. Sends `Prompts.Trend` + `Question:…Digest:…` with `Prompts.TightOptions` (`:55-58`). Malformed JSON, a "no trend" reply, or a tool with no rows all fall through to `Detect()` (`:78-83`). Counts (`Mentions`, `Technicians`, `Shifts`) are overwritten from the store (`:90-97`), so "the hub owns the arithmetic" already exists as a pattern. |
| `Detect()` | `HubBrain.cs:113-138`. Groups by tool+chamber+metric over non-Routine notes; ≥2 technicians. The model for the handover fallback. |
| `Prompts.Trend` / options | `src/FabLog.Core/Prompts.cs:104-118`. `TightOptions` = `MaxOutputTokens 200, Temperature 0, Seed 42` at `:18`. `ExtractJsonObject` at `:126` is reusable. |
| Hub's cloud wire | `src/FabLog.TrustHub/Cloud.cs:40-53` (`Cloud.Create(T3Options)`), registered at `Program.cs:40-41`. Timeout comes from `T3Options.TimeoutSeconds`, default 10 (`CloudOptions.cs:26`, `TrustHub/appsettings.json`). |
| Hub endpoints | `Program.cs`: `GET /health :60`, `POST /sync :79`, `POST /report :90`, `GET /reports :96`, `POST /query :101`, `GET /notes :107`, `POST /reset :110`. FabDesk is mapped at `:113`. |
| Seed | `src/FabLog.TrustHub/Data/hub-notes.json`: 12 notes, 2 technicians, exactly **two** ETCH-03 B uniformity complaints. Loaded and day-shifted in `HubStore.cs:116-189`. Pinned by `TrendTests.cs:41-53` and `:333-343`. |
| FabDesk | `Components/Pages/FabDesk.razor`. The existing button is **Ask**, in `<div class="ask">` at `:50-54`. Result card `:121-132`, "what crossed the border" `<pre class="digest">` at `:183-184`. Styles in `wwwroot/app.css` (`.ask :32`, `.finding :45`, `.error :112`). |
| Tests | `Fakes.cs:17-45` `FakeChat(respond)` records `Received`/`Systems`/`Calls`; `OfflineChat` `:56`; `NotConfiguredChatClient` (Core). Offline tests build `new HubBrain(store, new FakeChat(...), "gpt-4o")` (`TrendTests.cs:23-24`). Live tests: 7 `[LiveFact]` in `LiveCloudTests.cs` under `[Trait("Category","Live")]`; `LiveFact.cs:45-51` skips when `T3` is not configured. Seed is linked into the test output via `DemoTests.csproj:35`. Counts match your 99 / 92 / 7. |

### Where the brief and the code disagree

1. **The hub has no `CloudClient` and no `AirlockHandler`, by design.** `CloudClient` and `AirlockHandler` are FabPad's (`DeviceHost.cs:87` and `:94`; `Airlock.cs:66`). The hub calls the vendor through plain `Cloud.Create`, and `Cloud.cs:15-25` says in writing that a hub-side airlock is deliberately absent. So "blocked by posture/airlock" **cannot happen on the hub** and I will not add one. Fallback triggers on the hub are: not configured, transport/HTTP error, timeout, unparseable reply, every check rejected. If you want to *show* the fallback on stage, that needs a demo-only switch (open question 1).
2. **Notes carry no numeric reading.** `HubNote` has `Metric` and `Severity` but the value (94.5 %, 3.8 nm) only exists inside free text. Drift in σ and a 3-shift trend cannot be derived from notes without parsing prose, which I will not do. The plan puts **per-shift readings in the new context seed** instead (open question 2).
3. **The seed has no conflicting notes** (no chamber with both a Routine and a Watch/Escalate note), and `hub-notes.json` cannot be edited without breaking pinned tests. The conflict signal is computed on any data, but the demo conflict comes from one extra note pushed via `/sync` (a `demo.http` snippet, wiped by `/reset`).

Also: the working tree is not clean (`CloudOptions.cs`, `CloudClient.cs`, FabPad `appsettings.json`, four csproj version bumps from yesterday's package update, untracked `CloudClientTests.cs`, `measurements-2026-11-25.md`). Build on a branch cut from that state, or commit/stash first (open question 5).

---

## 1. Design in one paragraph

Add a second hub capability next to `HubBrain`, not inside it: `HandoverBrain`. Pure functions compute **facts** from the store plus a new context file; each fact line gets an evidence id. Only those derived lines (a new projection, `HandoverFacts.ToDigest()`) go to the cloud. The model returns ranked checks as JSON; the hub **validates** every check against what it sent (ids, tool/chamber, numbers) and falls back to its own scoring if nothing survives. `HubStore`, `HubBrain`, `AsDigest`, `/query` and the Ask flow are not edited, so "switch off = no change" is true by construction. The feature sits behind `Hub:Features:ShiftHandover` (default `false`).

## 2. Files to add / change

**Add**

| File | Why |
|---|---|
| `src/FabLog.TrustHub/Data/handover-context.json` | Fictional baselines (mean, σ, `worseWhen`), last-3-shift reading means, PM log (`lastPmDaysAgo`, `intervalDays`, `lastCleanDaysAgo`), WIP lots per chamber. **Relative offsets, not dates**, so it never goes stale (hub-notes.json needs a day-shift routine for the same reason, `HubStore.cs:128-189`). Copied to output by the existing `Data\*.json` rule in `TrustHub.csproj`. |
| `src/FabLog.TrustHub/HandoverFacts.cs` | Records + loader + pure maths: drift σ, trend, PM state, lots at risk, conflicts, evidence ids, hub score, `ToDigest()`. No I/O beyond reading the JSON, no model. |
| `src/FabLog.TrustHub/HandoverBrain.cs` | Cloud call, JSON parse, validation, rejection log, fallback, `HandoverResult`. Ctor `(HubStore, HandoverFacts-provider, IChatClient, string model, HandoverOptions)`. Holds last result + rejection log; `Reset()` clears them. |
| `tests/FabLog.DemoTests/HandoverTests.cs` | Offline tests (section 7). |
| `docs/plan-shift-handover.md` | This file. |

**Change**

| File | Change | Why |
|---|---|---|
| `src/FabLog.Core/Prompts.cs` | Add `Handover` prompt and `HandoverOptions` (`MaxOutputTokens` ~1200, `Temperature 0`, `Seed 42`, JSON response format). Update the "Six prompts" doc line. | `TightOptions` caps output at 200 tokens (`:18`); three checks in JSON will not fit, and a reasoning-style deployment may also spend that budget on thinking. Prompts live here by convention. |
| `src/FabLog.TrustHub/Program.cs` | Bind `Hub:Features:ShiftHandover`; always register `HandoverOptions`, `HandoverFacts`, `HandoverBrain`; map `POST /handover` **only when enabled**; `/reset` also calls `HandoverBrain.Reset()`. The handover brain gets its own cloud client from `Cloud.Create` with `TimeoutSeconds = max(configured, 25)`. | Keep `/query` untouched; the default 10 s timeout is too tight for a ~400-token JSON answer (see risks). |
| `src/FabLog.TrustHub/appsettings.json` | Add `"Hub": { "Features": { "ShiftHandover": false } }`. | Default off. Flip to `true` in the gitignored `appsettings.Development.json` for the talk. |
| `src/FabLog.TrustHub/Components/Pages/FabDesk.razor` | New **Shift handover brief** button beside **Ask** (`:50-54`), shown only when enabled. Card, evidence table, "what crossed the border" block, rejections list. | Item 6. |
| `src/FabLog.TrustHub/wwwroot/app.css` | A few rules (`.handover`, `.confidence-*`, `.chip`). | Styling. |
| `tests/FabLog.DemoTests/FabLog.DemoTests.csproj` | One `<None Include=… Link="Data\handover-context.json">` next to `:35`. | Tests read the seed from the output folder. |
| `tests/FabLog.DemoTests/LiveCloudTests.cs` | One new `[LiveFact]` in the existing class (inherits `Category=Live`). | Item "one live test". |
| `demo.http` | `POST /handover` and the conflict-note `/sync` snippet. | Pre-flight and the stage conflict. |

**Not touched:** `HubStore.cs`, `HubBrain.cs`, `hub-notes.json`, everything in FabPad.

No new NuGet package. (`Mvc.Testing` is not referenced, so there is no in-process HTTP test of the endpoint; see section 7.)

## 3. Facts the hub computes before any model call

For every tool+chamber that has a signal (|drift| ≥ 1σ, or a non-Routine note in the last 3 shifts, or PM overdue/due ≤ 7 d):

- **Drift** = (latest shift mean − baseline mean) / σ, signed. `worseWhen: low|high` per metric says which sign is bad. Rounded to 1 decimal.
- **Trend** over the last 3 shift means: `worsening` (monotonic move in the bad direction), `improving`, or `flat`.
- **PM state**: days since last PM vs interval → `due in N d` or `overdue by N d`; `last clean N d ago`.
- **Lots at risk**: queued next shift, count only.
- **Notes signal**: count of non-Routine notes in the last 3 shifts, distinct technicians, worst severity. No text.
- **Conflict**: same tool+chamber, last 2 shifts, one Routine and one Watch/Escalate from different technicians → label `Watch vs Routine`.

Evidence ids are assigned in a fixed order (tool, chamber alphabetical), so the same data always yields the same ids: `n#` note/process signals, `pm#`, `wip#`, `x#` conflicts.

## 4. Exact digest the cloud would receive (example)

Proposed seed values, **fictional, and hand-computed here; the build's step 1 test regenerates and pins them**. Example "now" = 2026-11-26. Rows only exist for ETCH-02 A, ETCH-03 B, ETCH-05 C and LITHO-01 A; the other seeded chambers are quiet and omitted.

```
# SHIFT HANDOVER DIGEST — derived signals only; no note text, no raw process values
n1   | ETCH-02 A  | vacuum      | drift=+2.7σ | trend=worsening (3 shifts)
n2   | ETCH-02 A  | notes       | non-routine=1 | technicians=1 | worst=Watch
n3   | ETCH-03 B  | uniformity  | drift=-3.3σ | trend=worsening (3 shifts)
n4   | ETCH-03 B  | notes       | non-routine=2 | technicians=2 | worst=Watch
n5   | ETCH-05 C  | throughput  | drift=-2.0σ | trend=worsening (3 shifts)
n6   | ETCH-05 C  | notes       | non-routine=1 | technicians=1 | worst=Watch
n7   | LITHO-01 A | overlay     | drift=+2.7σ | trend=worsening (3 shifts)
n8   | LITHO-01 A | notes       | non-routine=1 | technicians=1 | worst=Watch
pm1  | ETCH-02 A  | last PM 49d ago | interval 42d | overdue by 7d
pm2  | ETCH-03 B  | last PM 21d ago | interval 60d | due in 39d | last clean 6d ago
pm3  | ETCH-05 C  | last PM 27d ago | interval 60d | due in 33d
pm4  | LITHO-01 A | last PM 14d ago | interval 30d | due in 16d
wip1 | ETCH-02 A  | next shift lots=6
wip2 | ETCH-03 B  | next shift lots=9
wip3 | ETCH-05 C  | next shift lots=4
wip4 | LITHO-01 A | next shift lots=7
```

With the stage conflict note added (a second technician logs ETCH-02 A vacuum as Routine) one line is added and `n2` changes:

```
n2   | ETCH-02 A  | notes       | non-routine=1 | technicians=2 | worst=Watch
x1   | ETCH-02 A  | conflicting signals | Watch vs Routine | 2 technicians
```

What is **not** in it: note text, tags, device ids, names/initials, baseline mean or σ, the shift readings themselves, dates. A new wall-2 test asserts all of that, mirroring `TrendTests.cs:71-149`.

**Hub fallback ranking on this data** (score = drift pts `min(|worse-side drift|,4)×10` + trend 10 if worsening + PM 25 overdue / 15 due ≤7d + lots `min(n,10)×2` + notes `Escalate 15, else 5×technicians capped 10`):
ETCH-02 A 79 · ETCH-03 B 71 · LITHO-01 A 56 · ETCH-05 C 43. The interesting stage case is the cloud promoting ETCH-03 B (recent PM, so the drift is *unexplained*, and 9 lots exposed) above the pump that is simply overdue. **I cannot promise the model does that; I will measure it in step 6 and only say on stage what I have seen.**

## 5. Prompt draft (`Prompts.Handover`)

```
You advise a semiconductor fab shift supervisor. You are given a digest of DERIVED
signals for the next shift. Each line starts with an evidence id (n1, pm2, wip3, x1…).
There is no note text and no raw process value; do not ask for either or invent them.
drift is in sigma versus the chamber's own baseline; the sign is already resolved into
"worsening" or not in the trend field.

Task: answer "What should the next shift check first, and why?"
Rank the 2-3 most important checks. Weigh competing evidence: a drifting chamber with a
recent PM is a different problem from one that is overdue for PM; lots at risk raise
urgency; conflicting signals lower your confidence.

Rules:
- Use only tool/chamber pairs and evidence ids that appear in the digest.
- Every check must cite at least two evidence ids that belong to that tool/chamber.
- Quote a number only if it appears in the digest, exactly as written. Never compute,
  round, convert or infer any other number, in digits or in words.
- "action" is one imperative sentence a technician can do. "why" is at most two sentences.
- "confidence" is "high", "medium" or "low". "would_change_if" names one observable
  thing, without numbers, that would reorder the ranking.
- Reply with ONLY this JSON, no fence, no commentary:
{"checks":[{"rank":1,"tool":string,"chamber":string,"action":string,"why":string,
 "evidence":[string],"confidence":"high"|"medium"|"low","would_change_if":string}]}
```

User message: `Digest:\n<HandoverFacts.ToDigest()>`. Lines are sent in the fixed order above so identical data produces an identical prompt.

## 6. Validation and fallback (`HandoverBrain`)

Per check, in order; every rejection or edit is appended to a log shown on FabDesk (`rank 2 · ETCH-09 C — rejected: tool/chamber not in digest`):

1. **Shape**: JSON parsed with `Prompts.ExtractJsonObject`; max 3 checks; `confidence` must be one of three (otherwise coerced to `low` and logged).
2. **Subject exists**: tool+chamber must be a candidate in the digest.
3. **Evidence exists**: every id must be in the set sent, **and** belong to that tool+chamber; at least one id required. Otherwise reject.
4. **Numbers**: collect digits (and spelled numbers two-twelve) in `action`, `why`, `would_change_if`, after removing evidence ids. Any value not in the set of numbers in the digest → strip that sentence and log; if a field becomes empty → reject the check.
5. **Hub owns rank, source and display numbers**: ranks are renumbered 1..n in array order; the card prints the hub's own drift/PM/lots figures next to the model's words.

**Fallback** (`source = "hub ranking (no cloud)"`, with the reason): cloud not configured, exception (`VendorErrors.Describe`), hard deadline hit (15 s `CancellationTokenSource`, independent of SDK retries), unparseable reply, or zero checks survived. The hub's top 3 by score, `confidence` derived from agreement (conflict present → low), `action`/`why` templated from the facts, evidence ids attached. A partial pass (1-2 survivors) is shown as cloud with the rejections listed, not silently padded.

## 7. Tests

**Offline (fake cloud), in `HandoverTests.cs`:**

- Drift maths: σ sign and rounding for `worseWhen` low and high; trend worsening/improving/flat; PM due/overdue boundary; lots; conflict detected and not detected.
- Evidence ids deterministic and stable across two builds of the same data.
- **Digest wall-2**: no note text, tags, names/initials, device ids, baselines or readings; exact seed digest pinned (like the 8-column test).
- Validation: unknown tool, unknown id, id from another chamber, invented number stripped, field emptied → reject, bad confidence coerced, >3 checks truncated.
- Malformed JSON → fallback with reason; `OfflineChat` / `NotConfiguredChatClient` / a fake that never returns (deadline) → fallback; all checks rejected → fallback; partial pass → cloud with rejections.
- Fallback ranking pinned on the seed (79/71/56/43 order) and label text.
- **Switch off = no change**: `Enabled=false` → `BriefAsync` makes 0 cloud calls (`FakeChat.Calls == 0`) and returns "disabled"; `AsDigest()` and `AnalyseAsync` outputs on the seed are identical to today; existing tests unmodified.
- Reset clears the last brief and rejection log.

**Live (1, `[LiveFact]`):** real digest for the seed (+ the conflict note) against the configured deployment; asserts `Failed == false`, ≥1 validated check, `source == cloud`, every cited id is in the digest, no number outside the digest in the prose; prints the digest, JSON and latency to the test output so it can be put on screen.

**Not covered by a test:** the HTTP routing of `/handover` when off (404) and FabDesk rendering. Without `Mvc.Testing` (a new package) these are checked by hand via `demo.http` and the browser.

## 8. Build order

Each step ends with something runnable. Times are estimates; **total 180 min**.

| # | Step | Ends with | Min |
|---|---|---|---|
| 0 | Branch, run the 99 tests as baseline, confirm dev-config deployment + timeout (open question 4) | green baseline | 10 |
| 1 | `handover-context.json` + `HandoverFacts` (drift, trend, PM, lots, conflict, ids, `ToDigest`) + csproj link + tests for maths and wall-2 | `dotnet test` green; digest printed and compared with section 4 | 35 |
| 2 | Hub scoring + fallback brief + tests | fallback ranking pinned | 15 |
| 3 | `Prompts.Handover`/options, `HandoverBrain` (call, parse, validate, deadline, rejection log) + fake-cloud tests | all offline tests green | 35 |
| 4 | Config switch, `Program.cs` wiring, `/handover`, `/reset`, "off = no change" tests, `demo.http` | `curl POST /handover` works with the flag on, 404 off; `/query` unchanged | 15 |
| 5 | FabDesk button, card, evidence table, digest block, rejections, CSS | clickable in the browser, fallback label visible with the key removed | 30 |
| 6 | Live test; run the live brief 5×, check JSON validity, top-1 stability, latency; tune prompt once | **checkpoint evidence** (section 10) | 25 |
| 7 | Rehearse the story, capture a screen recording of a good run, tidy | recording saved | 15 |

Cut order if behind: step 7 polish, rejections panel styling, spelled-number check, `x#` conflict row on stage.

## 9. Risks and handling

| Risk | Handling |
|---|---|
| Invalid / fenced / truncated JSON | `ExtractJsonObject`, JSON response format, 1200-token cap (the existing 200 would truncate), no retry (retry doubles latency) → fallback with reason shown. |
| Ranking flips between runs | Temperature 0 + seed 42 (best effort, not a guarantee); fixed digest order so the prompt is byte-identical; the prompt names the tie-break (lots at risk, then PM state); last brief held until `/reset` so a page refresh does not re-roll; step 6 measures top-1 over 5 runs. If it flips, the stage answer is the recording plus the pinned hub ranking. |
| Latency / timeout | Hub deadline 15 s; handover client timeout ≥25 s because the configured 10 s is for short replies and the SDK retries ("after N attempts", `VendorErrors.cs:15`); spinner with elapsed time; step 6 records p50/p max. |
| Model cites a real id for the wrong chamber, or invents a number | Validation rules 3 and 4; rejections are logged and shown (useful on stage as "the hub caught this"). |
| Wall-2 regression (a new projection quietly widens) | A dedicated pinned digest test, same style as `TrendTests.cs:102-126`. |
| Seed dates go stale | Context file uses relative offsets. |
| Breaking the existing demo | Additive; `HubStore`/`HubBrain`/`/query` untouched; flag default off; work on a branch. |
| Reasoning-style deployment rejects `Temperature`/`Seed` | The Trend call already sends the same options against the configured deployment (`HubBrain.cs:55-58`); step 6 confirms with the new call. |

## 10. Go / no-go checkpoint: 14:30

**Needs to work end to end:**

1. All existing tests plus new offline tests green.
2. Flag on in `appsettings.Development.json`: FabDesk shows **Shift handover brief** next to **Ask**; one click returns a ranked card with source label `cloud` in under ~10 s.
3. Evidence ids on the card map to rows; the digest block is on screen.
4. Five consecutive live runs: valid JSON each time and at least 2 validated checks each time.
5. With the key removed the same click yields `hub ranking (no cloud)`.
6. `/reset` then Ask (the existing fleet question) behaves exactly as today.

**Start by 11:45 at the latest** (steps 0-6 = 165 min) to reach 14:30.

**If it is not met:** set the flag to `false` and run Demo 05 exactly as today. If only the cloud path is flaky but the hub path works, show the digest and the hub ranking labelled as such and say what is and is not cloud. Use the step-7 recording only if clearly presented as a recording.

## 11. Open questions

1. A stage-only switch to force the fallback (a dev-config flag, so you can show it without editing keys)? I recommend yes; it is ~5 lines and does not add a hub airlock.
2. OK to hold per-shift readings in the new context seed rather than parsing note text?
3. OK that the demo conflict is one extra `/sync` note rather than a change to `hub-notes.json` (pinned by tests)?
4. I did not open `appsettings.Development.json` (secrets). Is the deployment `gpt-5.4-mini` and what is `TimeoutSeconds`?
5. The tree has uncommitted work (yours plus yesterday's package bumps). Branch from the dirty state, or commit/stash first?
6. What time will you actually start? Anything after 11:45 breaks the 14:30 checkpoint.

---

## 12. As built — deviations from the plan above (2026-11-26)

- **Approved changes applied:** hub ranking always shown beside the cloud's (card + "Cloud vs hub ranking" table); `Hub:Features:ForceHandoverFallback`; digits-only number check (sentence removed + logged, spelled numbers not checked); LITHO-01 A trend flat, ETCH-05 C last clean 1d ago; step 7 skipped; go/no-go moved to 15:00.
- **Re-pinned hub order after the seed change:** ETCH-02 A 79 · ETCH-03 B 71 · LITHO-01 A 46 · ETCH-05 C 43 (was 56 for LITHO-01 A; it lost the 10 trend points). Pinned in `HandoverTests`.
- **Window is 36 h, not "last 2/3 shifts"**, for notes and conflicts alike. The day-shifted live seed puts the ETCH-02 A Watch note ~16 h before "now", outside a 16 h conflict window.
- **Conflict example fixed:** `technicians=` on the notes line counts non-Routine notes only, so with the stage conflict note `n2` still reads `technicians=1`; the `x1` line carries "2 technicians".
- **Tests:** 46 new offline tests + 1 live; full suite 146 passed (99 existing).
- **Verified by hand on the running hub:** flag off → `/handover` is 404 and `/query` unchanged; flag on → card renders; forced fallback; key removed → "hub ranking (no cloud)"; wrong key → fallback. Env-var overrides of `FabLog:T3` do **not** work (Program.cs adds `appsettings.Development.json` last); to rehearse "no key" edit that file.
- Untouched, as required: `HubStore.cs`, `HubBrain.cs`, `AsDigest`, `/query`.
- **Prompt tune (the one allowed):** added a rule that an `x` (conflicting signals) line caps that chamber at medium/low and must be mentioned in "why". Result: ETCH-02 A medium in 5/5 conflict runs (conflict named in 4/5 `why`), but seed-only confidence inflated to high for ETCH-03 B / LITHO-01 A and one run put ETCH-05 C third.
