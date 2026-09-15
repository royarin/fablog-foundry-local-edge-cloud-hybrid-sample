# FabLog — setup

Everything needed to run FabLog, in the order you need it.

The build and 70 of the 76 tests work **with no credentials at all**, so a fresh clone is
green without any of section 2. This document covers the two things that do need
provisioning: the models on the device (**Foundry Local**, T1) and the model in the cloud
(**Microsoft Foundry** — formerly Azure AI Foundry — T3).

For what the system *is* and why it is built this way, see [`README.md`](README.md).

```
FabLog/
├── src/FabLog.Core       — tiers, router, prompts, redaction, the airlock  (no SDK, no I/O)
├── src/FabLog.FabPad     — T1 · the device. WPF + BlazorWebView. Its OWN wire to edge and cloud.
├── src/FabLog.TrustHub   — T2 · the fab's service + FabDesk. Inside the airgap, trusted with full notes.
└── tests/FabLog.DemoTests— the routing rules, as assertions
```

> ⚠️ **Both processes hold a cloud credential**, one gitignored
> `appsettings.Development.json` each — the pad reaches the vendor over its own wire,
> never through the hub. Verify both are ignored before any commit:
> `git check-ignore -v src/FabLog.*/appsettings.Development.json`.

---

## 0 · Check it builds before you provision anything

**Once per machine — trust the ASP.NET Core dev certificate.** TrustHub serves
`https://localhost:5100` (set in `Program.cs`; `launchSettings.json`'s own URL is kept in
sync with it, not the other way round). Kestrel uses the standard dev cert automatically;
it just has to be trusted first, or every FabPad→hub call fails on the TLS handshake
before it ever reaches your code:

```bash
dotnet dev-certs https --trust
```

```bash
dotnet build FabLog.slnx
dotnet test  FabLog.slnx
```

On a machine without `appsettings.Development.json`: **70 passed, 6 skipped** — the six
live cloud tests skip themselves and print the reason why. Once section 2 is done:
**76 passed, 0 skipped**.

---

## 1 · Foundry Local (T1)

### 1.1 Runtime

Foundry Local runs **in-process**. There is no local server, no port and no endpoint to
configure — `Microsoft.AI.Foundry.Local.WinML` 1.2.4 is a NuGet reference and the model
loads inside `FabPad.exe`. Nothing to provision, nothing to start.

### 1.2 Models to have on disk

```bash
foundry model download qwen2.5-1.5b      # primary
foundry model download qwen2.5-0.5b      # fallback — see below
```

| Setting | Value | Why |
|---|---|---|
| `FabLog:T1:ModelAlias` | `qwen2.5-1.5b` | Everything the device does is extraction into a fixed schema, a closed-enum classification, and a short constrained rewrite. A 1.5B does all three. |
| `FabLog:T1:FallbackAlias` | `qwen2.5-0.5b` | If the primary alias is not in the machine's catalogue, `DeviceHost` drops to this rather than failing to start. Slower and weaker, but it runs. |

If **neither** is present the app stops with the exact `foundry model download` command to
run. It does not start half-working.

Disk: budget ~2 GB per variant. The app will download on first run if the variant is not
cached, but that turns a cold start into a multi-minute wait — run the two commands above
in advance.

### 1.3 ⚠️ Execution providers — pick the silicon explicitly

**This is the one setting that decides how fast T1 is.** On a two-GPU machine (measured on
an RTX A1000 + Intel UHD laptop), letting the SDK resolve the alias by itself picks the
**integrated** GPU and leaves the NVIDIA card idle — a measured **3.4×** penalty.

So the alias chooses the *model* and `VariantPreference` chooses the *silicon*:

```jsonc
"VariantPreference": [ "cuda-gpu", "trtrtx", "openvino", "generic-gpu", "generic-cpu" ]
```

First match in `model.Variants` wins; the SDK's own choice is the final fallback. The
per-variant numbers are in [`README.md`](README.md#measured-numbers).

Two things to know when moving to a different machine:

- `NvTensorRTRTXExecutionProvider` **failed to register** on the laptop these numbers came
  from, so the `trtrtx` variant the CLI advertises never appeared in `model.Variants`. It
  stays in the preference list because it costs nothing and is the fastest option where it
  does work.
- The Hardware panel shows `DiscoverEps()` verbatim, including failures. Check it once
  after any hardware or driver change: if the resolved provider says *OpenVINO (integrated
  GPU)*, every local call is three times slower than it needs to be.

### 1.4 Running the same build on CPU

```jsonc
"ForceVariant": "generic-cpu"
```

One config line, no rebuild: **968 ms → 12 219 ms per call**, 2.9 s → 36.7 s per note.
Processing one note is three T1 calls, so the per-note figure is the one a technician
would actually feel.

### 1.5 The device's two outbound connections

`FabLog.FabPad` opens two independent connections, and neither goes through the other:

| | Where | What crosses it | If it's down |
|---|---|---|---|
| **C2 — Azure, direct** | `FabLog:T3` | This note's polish — whole under `CloudOnly`, masked under `Hybrid` | `CloudOnly` is dead; `Hybrid` degrades to T1; sync is unaffected |
| **C3 — the hub** | `FabLog:Hub` → `https://localhost:5100` | Sync: **the full note, unredacted**, never a prompt | The note stays `SyncState.Pending` and is retried. Never dropped. |

The pad calls Azure **directly** because `CloudOnly` has to be a posture the pad can hold
by itself. Routing it via the hub would make the hub the pad's route to the cloud whatever
the ledger says.

> ⚠️ **Both connections sit behind the airlock, and there is no loopback exemption.**
> `AirlockHandler` is registered on `CloudClient` *and* `HubClient`: when the cleanroom is
> sealed, the hub on loopback is as unreachable as the vendor in another jurisdiction,
> because a cleanroom RF-shields against the fab's own LAN too. The boundary is *which
> clients get the handler*, not a condition inside it.
> `A_sealed_cleanroom_cuts_the_fabs_own_network_too` in `RoutingTests.cs` pins this.
>
> There is also no network adapter to disable and no elevated PowerShell involved: the
> airgap is a toggle in FabPad's chrome, backed by a `DelegatingHandler` that throws
> `SocketException(11001)` below the Azure client. Nothing above the socket knows it
> exists.

> ⚠️ **There is no reachability probe, deliberately.** A probe predicts the next call; the
> truth is the last one. `CloudClient` records the outcome of each attempt — false on a
> transport exception, true on any completed response including 401 and 429 — and the
> badge reports that, with a timestamp. Recovery is found by `DeviceHost`'s drain loop
> trying to send, which is how a real store-and-forward queue behaves. **No state exists
> in which the wire is down and the UI says otherwise.**

**The pad's configuration is split across two files:**

```jsonc
// src/FabLog.FabPad/appsettings.json — COMMITTED, placeholders only
"T3": {
  "Endpoint":   "<see appsettings.Development.json>",
  "Deployment": "gpt-5.4-mini",
  "ApiKey":     "<see appsettings.Development.json>"
}
```

```jsonc
// src/FabLog.FabPad/appsettings.Development.json — GITIGNORED
// A COMPLETE copy of the committed file, with the real endpoint and key in place
// of the placeholders. Everything the app needs to run locally is in this one file.
{
  "FabLog": {
    "Mode": "CloudOnly",
    "Hub": "https://localhost:5100",
    "T3": { "Endpoint": "https://…", "Deployment": "gpt-5.4-mini", "ApiKey": "…" },
    "Demo": { "PadOnNetwork": true, "DrainIntervalSeconds": 2 },
    "T1": { "…": "…" },
    "Author": "K. Nagy", "Shift": "…", "DeviceId": "PAD-07", "Cleanroom": "CR-2"
  }
}
```

`T3Options.IsConfigured` rejects any value beginning with `<`, so the committed file reads
as *not configured* — the placeholders are load-bearing, not decorative.

> ⚠️ **The Development file is a complete copy, and one consequence matters.**
> `Development` overrides the base file key-by-key, so **the committed
> `appsettings.json` is inert at runtime**: editing it while the app is running changes
> nothing, because every one of its keys is overridden. It remains the committed template,
> the documentation, and what you restore a fresh clone from.
>
> The only file whose edits take effect is the one holding the API key. Change posture
> with the **`Deployment policy` selector in FabPad's chrome** instead — it is the
> supported way, and `DeviceHost` also watches the config file and rebinds `FabLog:Mode`
> without a restart.

---

## 2 · Microsoft Foundry (T3)

One deployment, reached by **two independent callers** — the pad and the hub.

### 2.1 What to create

| | Value | Notes |
|---|---|---|
| Resource | **Microsoft Foundry** project (or Azure OpenAI resource) | Either works; both callers reach it over the Azure OpenAI data plane. |
| Model | `gpt-5.4-mini` | What the measured numbers came from. Any chat-completions deployment works — the code reads the name from config and does not care. |
| Deployment name | Anything | It renders in the ledger and the hardware panel, so a readable name is worth it. |
| Deployment type | **Data Zone Standard (EU)** | Keeps the data in-region, which is the point of the exercise, and keeps the round trip short. Global Standard works if quota is tight. |
| Region | Nearest EU region — **Sweden Central** or **West Europe** | Latency lands directly on the pause after a cloud call — measured **1.6 s** on the current deployment. |

> A `mini` is enough. It reproduces placeholders verbatim and finds the ETCH-03 trend from
> the digest's columns alone, both verified against a live endpoint. If someone asks *"why
> not just run the mini locally too?"*, the answer is not a size answer: **it isn't about
> model size, it's about which data the tier can see.** A device holding one technician's
> notes cannot find a fleet-wide trend at any parameter count.

### 2.2 Quota — size it for the burst, not the average

Total consumption is tiny (~15 calls, ~5 000 tokens for a full run), but there is one
burst that matters. Azure enforces TPM in **10-second windows**, at roughly `TPM ÷ 6`
tokens per window:

| Workload | Calls | ~Tokens | Window |
|---|---|---|---|
| The queue draining | 6 sequential polish calls | ~1 500 | can land inside ~10 s |
| The fleet-wide query | 1 `/query` over 13 notes | ~850 | one window |

At **10K TPM** you get ~1 667 tokens per window — the drain lands *on the edge*, and a 429
on item four leaves the pending count frozen with no way to explain it. So:

- **Minimum: 10K TPM** (≈ 60 RPM)
- **Recommended: 30K TPM** (≈ 180 RPM) — ~5 000 tokens per window, ~3× headroom

`Six_back_to_back_drains_do_not_hit_a_rate_limit` in the test suite is this requirement as
an assertion. Run it once against the real deployment.

### 2.3 Wiring it up — the two files that hold a key

Create **both** of these. Each is gitignored by the same rule
(`appsettings.[Dd]evelopment.json`, bracketed case so a lowercase spelling cannot slip
through), and between them they are the only places in the system where a cloud credential
exists.

```jsonc
// src/FabLog.TrustHub/appsettings.Development.json — the hub's own wire to the vendor
{
  "FabLog": {
    "T3": {
      "Endpoint": "https://your-resource.services.ai.azure.com/",
      "Deployment": "gpt-5.4-mini",
      "ApiKey": "paste-your-key-here"
    }
  }
}
```

```jsonc
// src/FabLog.FabPad/appsettings.Development.json — the device's own wire to the vendor
{
  "FabLog": {
    "T3": {
      "Endpoint": "https://your-resource.services.ai.azure.com/",
      "ApiKey": "paste-your-key-here"
    }
  }
}
```

Same resource, same deployment, two independent callers — see §1.5. Both ignored files are
complete copies of their committed templates, so either process can be run from its own
single file with nothing missing.

Verify both are ignored:

```bash
git check-ignore -v src/FabLog.FabPad/appsettings.Development.json \
                    src/FabLog.TrustHub/appsettings.Development.json
```

Two lines of output means two files ignored. **No output means a key is about to be
committed** — stop and fix `.gitignore` before going further.

> ⚠️ **The endpoint trap.** The portal will hand you **three different endpoints**, all
> real, all for different clients. Only the bare root is the one this app wants:
>
> | What the portal calls it | Shape | For |
> |---|---|---|
> | **Azure OpenAI endpoint** ✅ | `https://{res}.openai.azure.com/` | **`AzureOpenAIClient` — this app** |
> | Project endpoint | `https://{res}.services.ai.azure.com/api/projects/{proj}` | Azure AI Projects / Agents SDK |
> | OpenAI-compatible endpoint | `https://{res}.openai.azure.com/openai/v1` | The plain **OpenAI** SDK — model in the body, no api-version |
>
> All three verified live: the first works; the third works *if called the OpenAI way* and
> **404s** if called the Azure way; the second produces **HTTP 400 "API version not
> supported"** — an error that points nowhere near its cause and costs you twenty minutes
> chasing api-versions you do not have a problem with.
>
> **`T3Options.ResourceEndpoint` strips the path, so all three work.** Paste whichever the
> portal gives you. Both host spellings (`services.ai.azure.com`, `openai.azure.com`) are
> equivalent and both verified. The rule, if you want one: **scheme and host only — if
> your endpoint has a path on it, the path is not doing you any good here.**

The committed `appsettings.json` holds `<…>` placeholders. `T3Options.IsConfigured`
rejects any value starting with `<`, so a half-filled template reads as *not configured*
rather than as a broken endpoint.

If it is not configured, neither caller fakes an answer: the hub's `/query` returns 503
with a message naming the file to create, and the pad surfaces the same refusal rather
than a plausible-looking polish. A stub that quietly returned plausible prose would make
"the cloud tier works" and "the cloud tier is wired" indistinguishable.

### 2.4 🔑 Key discipline

1. Use **Key 2**, not Key 1 — Key 1 stays untouched so anything else on the resource keeps
   working while you rotate.
2. **Rotate Key 2 the moment it has been anywhere but those two gitignored files** — a
   shared screen, a screen recording, a pasted snippet, a chat message.

No user-secrets manager, no environment variables, no key vault — just the two gitignored
files. Every layer of indirection is a layer that has to be explained, and a machine you
hand to someone else should not depend on an environment variable they have to remember.

The device holds a real key too, so rotation is the whole of the defence. Do it the same
day, not the following week.

---

## 3 · Running both processes

Two processes, in this order:

```bash
# terminal 1 — the hub, on https://localhost:5100
dotnet run --project src/FabLog.TrustHub

# terminal 2 — the device
dotnet run --project src/FabLog.FabPad
```

| URL / window | Who is looking at it |
|---|---|
| FabPad window | K. Nagy, a technician, inside the airgap |
| <https://localhost:5100> | FabDesk — the shift supervisor, in the trusted zone |
| <https://localhost:5100/health> | your own pre-flight check |

**Start FabPad before you need it.** `LoadAsync` takes 8–23 seconds and
`DeviceHost.StartAsync` does it at launch behind a visible status line, deliberately, so
it never happens on the first keystroke.

### Pre-flight

```bash
curl https://localhost:5100/health
# { "ok":true, "notes":12, "seeded":12, "cloudConfigured":true, "deployment":"gpt-5.4-mini" }
```

`notes: 12` and `cloudConfigured: true` are the two to check. If `notes` is 13, an earlier
run already synced a note — reset it:

```bash
curl -X POST https://localhost:5100/reset
```

Back to 12 notes and two ETCH-03 complaints, in one call. Ready-made requests for every
endpoint are in [`demo.http`](demo.http).

Once both windows are up, [`README.md`](README.md#walkthrough--run-it-yourself) has a
ten-step walkthrough — what to paste, what to watch for, and the test that pins each
behaviour.

### Starting over

Restart FabPad (its note store, pending notes and ledger are all in memory) and
`POST /reset` the hub. Nothing is persisted anywhere; the data lives as long as the
processes do.

---

## 4 · Tests

```bash
dotnet test FabLog.slnx
```

| File | Covers |
|---|---|
| `RoutingTests.cs` | one region per routing situation: `EdgeOnly`, `CloudOnly`, `Offline`, `Scope`, `Pending`, `Hybrid`, `Ledger`, `ShiftReport` |
| `TrendTests.cs` | the fleet-wide finding — the seed corpus, the hub's arithmetic, the digest |
| `EndToEndTests.cs` | the whole flow in sequence, one device and one hub, nothing reset |
| `LiveCloudTests.cs` | the things only a real endpoint can tell you |

Everything except `LiveCloudTests` runs on deterministic fake `IChatClient`s: no network,
no model, no credentials, ~170 ms. They assert what the *architecture* does with a model's
answer — which tier ran, what crossed a wall, what the ledger recorded.

`LiveCloudTests` skip themselves while T3 is unconfigured and start running the moment
§2.3 exists — no flag to remember. They answer the four questions a fake cannot, all four
verified against a live deployment:

1. **Does the vendor model reproduce `⟦R1⟧` verbatim?** Yes, all three placeholders,
   inventing no values. This is the one that can break masking invisibly — right screen,
   right round trip, and a restored note still full of `⟦R1⟧`.
2. **What does the round trip cost?** **1 590 ms warm median** over the pad's own wire.
3. **Can a real model find the trend from the digest's columns alone?** Yes — eight
   columns, no note text, and it still finds ETCH-03 chamber B.
4. **Does the quota survive the burst?** Six sequential calls in **~9 s**, no 429. And
   with the uplink cut, a real endpoint and a real key still fail at the socket.

`dotnet test` alone will not *show* you those numbers — xUnit only prints captured output
for failing tests. To see them:

```bash
dotnet test FabLog.slnx --filter Category=Live --logger "console;verbosity=detailed"
```

⚠️ These tests spend real tokens against your deployment. Re-run them when the deployment,
region or model changes; they are the only part of the suite whose answers can go stale
without the code changing.
