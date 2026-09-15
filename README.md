# FabLog — Microsoft Foundry + Foundry Local behind one `IChatClient`

**Cloud, edge and hybrid AI in a single .NET 10 application.** FabLog runs the same
workload two ways — a small language model **on-device with Foundry Local**, and a
frontier model in **Microsoft Foundry** (the Azure service formerly named *Azure AI
Foundry*) — behind one `Microsoft.Extensions.AI` `IChatClient`. You choose which tier is
allowed to answer, and the application shows you exactly what crossed the network to get
that answer.

| | Runtime | Model | Where inference happens |
|---|---|---|---|
| **T1 · edge** | **Foundry Local** — `Microsoft.AI.Foundry.Local.WinML` | `qwen2.5-1.5b` | In-process on the device's own GPU. No local server, no port. |
| **T2 · on-prem** | ASP.NET Core + Blazor Server | — | The organisation's own network. |
| **T3 · cloud** | **Microsoft Foundry** — `Azure.AI.OpenAI` | `gpt-5.4-mini` | Azure. |

The scenario that gives those three tiers teeth is **data sovereignty at the edge**: a
semiconductor fab where a technician's shift notes are captured on a handheld device
inside a cleanroom, reach the fab's own server in full, and reach the model vendor only in
a form the fab chose to let cross.

Nothing here is simulated at the point that matters. The on-device model really runs on
the machine's GPU, the cloud model is a real Microsoft Foundry deployment, and the
"airgap" is a `DelegatingHandler` that closes the socket underneath the shipped path
rather than an `if` in the routing code.

> **Built with** .NET 10 · `Microsoft.Extensions.AI` · Foundry Local · Microsoft Foundry
> (Azure AI Foundry) · WPF + BlazorWebView · Blazor Server · xUnit — on Windows.

> **Setting up a machine to run it?** → [`SETUP.md`](SETUP.md) is the setup guide
> (models, Azure resource, keys, quota). This file is the architecture and the code map.

---

## Contents

- [The scenario](#the-scenario)
- [Architecture](#architecture)
- [The three postures](#the-three-postures)
- [The two walls — what may cross, and how](#the-two-walls--what-may-cross-and-how)
- [How a note is routed](#how-a-note-is-routed)
- [The cleanroom airlock](#the-cleanroom-airlock)
- [Repository layout](#repository-layout)
- [Quick start](#quick-start)
- [Walkthrough — run it yourself](#walkthrough--run-it-yourself)
- [Configuration](#configuration)
- [TrustHub HTTP API](#trusthub-http-api)
- [Tests](#tests)
- [Measured numbers](#measured-numbers)
- [Credentials and safety](#credentials-and-safety)
- [Invariants — read before editing](#invariants--read-before-editing)
- [License](#license)

---

## The scenario

K. Nagy is a technician on the night shift in cleanroom CR-2, carrying handheld `PAD-07`.
They write shorthand notes — `etch03 chmbr B unif 94.2% recipe RX-7 ramp 4.5s - off spec`.
Three facts shape everything in this repo:

1. **The cleanroom is RF-shielded.** It blocks the vendor's cloud *and* the fab's own LAN.
   Notes written inside it cannot go anywhere until the technician walks out.
2. **The note contains the fab's IP.** The recipe name, the ramp parameter and the yield
   figure are the trade secret; the tool number is not.
3. **The interesting question spans technicians.** "Has anyone else seen this?" cannot be
   answered by a device that holds one person's notes — not because the model is small,
   but because the data is elsewhere.

The system's job is to serve all three at once, and to be able to *prove* afterwards what
crossed which boundary.

---

## Architecture

```
                cleanroom door (RF shield)          jurisdiction boundary
                          │                                   │
  ┌───────────────────────┴───────────┐                       │
  │  T1 · FabPad  (the device)        │                       │
  │  WPF + BlazorWebView              │                       │
  │  qwen2.5-1.5b via Foundry Local   │                       │
  │  in-process — no server, no port  │                       │
  │                                   │   C2 · direct wire    │
  │   extract · classify · clean      ├───────────────────────┼──► T3 · Microsoft Foundry
  │   detect fab IP · mask · unmask   │   (masked text only)  │    gpt-5.4-mini
  │   ledger · pending notes          │                       │
  └───────────┬───────────────────────┘                       │
              │ C3 · sync: every note, in full, unredacted    │
              ▼                                               │
  ┌───────────────────────────────────┐                       │
  │  T2 · TrustHub  (the fab's server)│   digest only —       │
  │  ASP.NET Core + Blazor "FabDesk"  │   columns, no prose   │
  │  every technician's notes         ├───────────────────────┼──► T3 · Microsoft Foundry
  │  fleet-wide trend detection       │                       │    (same deployment)
  └───────────────────────────────────┘                       │
```

| Tier | Project | What it is | What it may see |
|---|---|---|---|
| **T1** | `FabLog.FabPad` | The handheld. WPF shell hosting a Blazor UI; `qwen2.5-1.5b` loaded **in-process** by Foundry Local (no local server, no port). | This technician's notes, in full. Holds the placeholder→value mapping, which never leaves. |
| **T2** | `FabLog.TrustHub` | The fab's own service on the fab's own network, plus **FabDesk**, the supervisor's screen. | *Every* technician's notes, in full, in every posture. You do not redact to yourself. |
| **T3** | Microsoft Foundry | The model vendor. Reached by **two independent callers** — the pad and the hub — over separate wires. | From the pad: the note, masked (or whole, under `CloudOnly`). From the hub: a structured digest with **no note text at all**. |

**T2 is not a hop.** It is a destination. The device does *not* reach the cloud through
the hub, and that is load-bearing: a broker in front of the vendor would still be the
device's way out, which would make `CloudOnly` a posture the *server* holds rather than
one the device can hold by itself. The routing axis is T1↔T3; T2 sits off it, as the
`INoteSink` every path writes to.

The abstraction that makes the tiers interchangeable is **`IChatClient`**
(`Microsoft.Extensions.AI`). `FabLog.Core` references *only*
`Microsoft.Extensions.AI.Abstractions` — no Foundry SDK, no Azure SDK. Each application's
composition root builds its own clients and hands them in.

---

## The three postures

A posture is what a fab's compliance officer would set. One axis — *where work may run* —
plus one border control.

```csharp
public sealed record Policy(Tier[] Allowed, bool ProtectIp);
```

| `DemoMode` | `Allowed` | `ProtectIp` | Why |
|---|---|---|---|
| `CloudOnly` | `[T3VendorCloud]` | `false` | The architecture most rooms ship. It **cannot** redact — deciding which spans are fab IP needs a local model, and there isn't one. The missing tick-box is an architecture problem. |
| `EdgeOnly` | `[T1Device]` | `false` | Nothing crosses the border, so there is nothing to mask. You do not mask to yourself. |
| `Hybrid` | `[T1Device, T3VendorCloud]` | `true` | Both permitted, so **the note decides, not the connection**. The only posture that both crosses a border and can see what it is crossing. |

Set live from FabPad's **Deployment policy** selector, or from `FabLog:Mode` in config
(`DeviceHost` watches the file and rebinds without a restart).

---

## The two walls — what may cross, and how

The two boundaries use **different mechanisms**, on purpose.

### Wall 1 · the device → the vendor: masking

`FabLog.Core/Redaction.cs`. Belt and braces, two layers, union of both:

| Layer | What it catches | Fails how |
|---|---|---|
| **Rules** — compiled regexes | recipe IDs (`RX-7`), ramp parameters (`4.5s`, `45sccm`, `450C`), yield figures (`94.2%`), defect codes (`D-1180`) | Cannot have a bad day. Runs offline, in microseconds. |
| **Model** — the T1 model, prompted | the formats the rules do not know | A malformed reply from a 1.5B degrades to **rules-only**, never to nothing. The gate never fails open. |

Detected spans become `⟦R1⟧`, `⟦R2⟧`… numbered by first appearance so the same note
always produces the same placeholders. The vendor writes prose about the placeholders;
`Redaction.Unmask` restores the real values **locally**, by dictionary lookup — not by a
second model call, because prose that reads perfectly with the wrong value in it is the
one failure nobody catches by reading it.

**Tool IDs and chambers are deliberately not masked.** `ETCH-03` and "chamber B" are the
metadata the fleet-wide trend runs on; a tool number is not a trade secret, and masking it
would destroy the finding while protecting nothing.

This is a **control, not a guarantee** — and the ledger records the exact payload, so a
miss is detectable after the fact. The alternative architecture sent the whole note and
left nothing to review.

### Wall 2 · the hub → the vendor: projection

`HubStore.AsDigest()`. The hub holds the richest store in the system, so nothing it holds
leaves verbatim. What crosses is a table of columns the hub extracted itself:

```
2026-09-14 22:10 | tech=KN | shift=Night 2026-09-14 | room=CR-2 | tool=ETCH-03 | chamber=B | metric=uniformity | severity=Escalate
```

Absent by design: **the note text** (where the IP actually lives), **the context tags**
(the technician's own words), **the device id** (identifies a physical asset), and
**author names** (personal data — initials are enough to *count* distinct people, which is
all the model is asked to do). The cleanroom label *does* cross: a pattern spanning two
rooms is a different finding from one confined to a single room.

The hub has no local model, so it does not attempt to redact prose — it simply never sends
prose. *A redactor you cannot run is not a control; a projection you cannot widen is.*

---

## How a note is routed

`FabLog.Core/Router.cs` — one public method, `ProcessAsync`. The permitted set decides;
there is no `if (mode == …)` and no connectivity check anywhere in it.

| Posture | Spans found | Path | What crosses wall 1 | Ledger reason |
|---|---|---|---|---|
| `CloudOnly` | n/a — no local model | `T3` | **the whole note, verbatim** | `mode: CloudOnly` |
| `CloudOnly`, vendor unreachable | n/a | `T3 ✖` | nothing | `vendor unreachable` — **dead, not degraded**: no local path to fall back to |
| `EdgeOnly` | not detected (`ProtectIp: false`) | `T1` | nothing | `policy: edge only` — the cloud is *never asked*, not asked and failed |
| `Hybrid`, fab IP present | yes | `T1→T3` | **masked text only** | `policy: fab IP detected` |
| `Hybrid`, fab IP present, prose not wanted | yes | `T1` | nothing | `policy: fab IP, no cloud-grade prose needed` — the network is fine and it still does not go |
| `Hybrid`, nothing proprietary | no | `T1→T3` | the note | `no fab IP in this note` |
| `Hybrid`, vendor unreachable | either | `T1` | nothing | `vendor unreachable` — **degraded, not dead**: structured, classified, masked note in hand; only the prose is missing |

**Extraction and classification are the same call in every posture** — `ExtractAndCleanAsync`,
handed a different tier's `IChatClient`. Under `CloudOnly` it runs on the vendor's model; under
`EdgeOnly` and `Hybrid` it runs on the device's. **The model changes, not the extraction logic**,
which is the point: a note logged under `CloudOnly` comes back with the same fields filled in, so
the difference between the postures is visibly about *where work ran and what crossed*, never
about one posture quietly doing less. What `CloudOnly` cannot do is *span detection* — deciding
which substrings are fab IP needs a model on the device, and in that posture there isn't one.

Every path lands in one private `RecordAsync`, which is what makes the audit trail free:

- **one `LedgerEntry`** — note id, tier, path, model, reason, elapsed ms, and the
  **literal payload** (not a hash — the UI renders it);
- **one sync attempt to T2** — the whole note, unredacted, in every posture. Returns
  `false` rather than throwing when the hub is out of range; the note stays
  `SyncState.Pending` and is retried. A note is never dropped for being out of range.

There is **no queue for cloud work**, deliberately. Deferring a *delivery* is useful —
nobody is waiting on a note reaching the fab's server. Deferring an *answer* is not: a
reply that arrives twenty minutes later, unprompted, is worse than an error. And there is
no queue object for pending notes either: `Pending` is a filter over the notes the device
already holds, because "pending" is a fact about a note, not a second copy of it.

### The other two flows

- **`DeviceScope`** (`Router.cs`) — the honest refusal. A cross-note question
  ("anything odd across the fab today?") is out of a device's scope *by construction*: it
  holds one technician's notes. The refusal says so, and still answers what it can
  ("4 from your shift, 1 mentions ETCH-03"). No bigger model fixes this; only moving the
  workload to where the data is does.
- **`ShiftReporter`** (`ShiftReport.cs`) — the hybrid flow, run *outside* the cleanroom at
  the end of a shift. The **facts table** (tool/chamber/metric/severity, grouped, counted)
  is computed on the device by arithmetic and never sent anywhere; only a **masked brief**
  travels; the narrative comes back and is re-inflated locally. The finished report — real
  values restored — is submitted to the hub, and the technician keeps a local copy whether
  or not submission worked.

### The fleet-wide finding

`HubBrain.AnalyseAsync` is the one workload that genuinely cannot run on a device. It
sends the digest, asks for a finding, and then **overrides the model's arithmetic with its
own**: mentions, distinct technicians and distinct shifts are counted from the rows the
hub is holding. The model writes the sentence; the hub owns the numbers. A confident wrong
count is the one failure that destroys the finding's credibility entirely.

If the reply is malformed, or names a tool with no rows behind it, a deterministic
`Detect()` (group by tool+chamber+metric, most distinct technicians wins) produces the
finding instead. A bad reply costs prose, never the finding. Two technicians is a
coincidence (`Watch`); three is a trend (`Escalate`).

---

## The cleanroom airlock

`FabLog.Core/Airlock.cs` — ten lines, and the whole boundary is in them.

```csharp
public sealed class AirlockHandler(IAirlock airlock) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        => airlock.PadOnNetwork
            ? base.SendAsync(request, ct)
            : throw new HttpRequestException(
                $"No such host is known. ({request.RequestUri!.Host}:{request.RequestUri.Port})",
                new SocketException((int)SocketError.HostNotFound));
}
```

Three properties make it defensible:

1. **It sits below the thing under test.** The Azure SDK, `IChatClient`, `Router`, the
   drain loop — all shipped code, unmodified, all finding out by failing.
2. **It throws what Windows throws.** `WSAHOST_NOT_FOUND` is the real error behind a
   missing NIC, so the message the UI shows is the real one.
3. **It fails in microseconds**, and identically every time — unlike a real DNS failure,
   which is 200 ms or 90 s depending on cache and proxy state.

**There is no loopback exemption, and its absence is the design.** The cleanroom boundary
is not an expression inside the handler — it is *which clients get the handler*. Both of
FabPad's outbound clients have it (`CloudClient` **and** `HubClient`), because a cleanroom
blocks the fab's own LAN exactly as well as it blocks the internet. The on-device model
conspicuously does not: `FoundryChatClient` never constructs an `HttpClient` at all, so it
is unreachable by this mechanism *structurally*.
`A_sealed_cleanroom_cuts_the_fabs_own_network_too` pins that behaviour.

### Connectivity is an observation, never a probe

There is no health poll and nothing asks whether it is online before trying.
`CloudClient` records the outcome of each attempt — `true` on any completed response
(including 401 and 429: a refusal is not a wall), `false` only on a transport failure —
and `IConnectivity` reports that to the badge, with a timestamp. **No state exists in
which the wire is down and the UI says otherwise.**

Recovery follows the same rule. `DeviceHost.PollAsync` is a **drain loop**, not a probe:
every two seconds it tries to send whatever is still pending, oldest first, serially,
stopping at the first failure. A send succeeding *is* how the device discovers the signal
came back — which is what every real client has always done, and why nobody has to click
anything when the technician walks out.

---

## Repository layout

```
FabLog/
├── FabLog.slnx                     — the solution (slnx format)
├── SETUP.md                        — setup: models, execution providers, Azure, keys, quota
├── demo.http                       — every TrustHub endpoint, for pre-flight and poking by hand
├── src/
│   ├── FabLog.Core/                — net10.0 · no SDKs, no I/O, no infrastructure
│   │   ├── Tiers.cs                — Tier, DemoMode, Policy/Policies, IConnectivity, INoteSink
│   │   ├── Router.cs               — the routing decision + DeviceScope's refusal
│   │   ├── Redaction.cs            — rules + model, mask/unmask, ⟦R1⟧
│   │   ├── Airlock.cs              — IAirlock, AirlockHandler
│   │   ├── FabNote.cs              — FabNote, IpSpan, SyncState, LedgerEntry, Ledger
│   │   ├── ShiftReport.cs          — the hybrid end-of-shift flow
│   │   ├── Prompts.cs              — six prompts + tolerant JSON parsing + normalisers
│   │   └── CloudOptions.cs         — T3Options, NotConfiguredChatClient
│   ├── FabLog.FabPad/              — net10.0-windows10.0.18362.0 · WPF + BlazorWebView
│   │   ├── DeviceHost.cs           — composition root, start-up, drain loop, HardwareProbe
│   │   ├── FoundryChatClient.cs    — Foundry Local → IChatClient adapter
│   │   ├── CloudClient.cs          — the device's own wire to T3 (+ IConnectivity)
│   │   ├── HubClient.cs            — INoteSink + IReportSink over HTTP
│   │   ├── NoteStore.cs            — this device's notes; Pending is a filter, not a queue
│   │   ├── DeviceOptions.cs        — binds FabLog:*  (Mode, T1, T3, Demo, Hub, identity)
│   │   ├── Demo/DemoSwitches.cs    — the cleanroom door, as a toggle (IAirlock)
│   │   └── Components/             — Main.razor + payload / hardware / ledger panels
│   └── FabLog.TrustHub/            — net10.0 · ASP.NET Core + Blazor Server
│       ├── Program.cs              — endpoints, DI, https://localhost:5100
│       ├── HubStore.cs             — HubNote, SubmittedReport, the seed, AsDigest()
│       ├── HubBrain.cs             — the fleet-wide finding + deterministic fallback
│       ├── Cloud.cs                — the hub's own wire to T3
│       ├── Data/hub-notes.json     — the seed corpus (12 notes — load-bearing, see below)
│       └── Components/Pages/FabDesk.razor  — the supervisor's screen
└── tests/FabLog.DemoTests/         — net10.0 · xUnit, 76 tests
```

> **The seed corpus is load-bearing, not test data.** `hub-notes.json` holds exactly
> **two** ETCH-03 chamber-B uniformity complaints, from two technicians on two shifts. Two
> is a coincidence; the third is the one written on the device. Add a third here and the
> trend is already in the file, with nothing left for the fleet-wide query to discover. It
> floats to "today" at start-up (whole-day offset, so every note keeps its time-of-day and
> its gaps), so the corpus never ages into "last week" mid-sentence.

---

## Quick start

**Prerequisites**

- .NET 10 SDK · Windows (FabPad is WPF + `Microsoft.AI.Foundry.Local.WinML`)
- The ASP.NET Core dev certificate, once per machine: `dotnet dev-certs https --trust`
- For T1: **Foundry Local**, then `foundry model download qwen2.5-1.5b` (and `qwen2.5-0.5b`
  as the fallback) — see [`SETUP.md`](SETUP.md) §1
- For T3: a **Microsoft Foundry** (Azure AI Foundry) deployment — see [`SETUP.md`](SETUP.md) §2

**Build and test — no credentials needed**

```bash
dotnet build FabLog.slnx
dotnet test  FabLog.slnx          # 70 pass without a cloud key; 6 live tests skip themselves
```

**Run — two processes, in this order**

```bash
dotnet run --project src/FabLog.TrustHub   # https://localhost:5100
dotnet run --project src/FabLog.FabPad     # the device
```

| Window | Who is looking at it |
|---|---|
| FabPad | K. Nagy, a technician, inside the airgap |
| <https://localhost:5100> | FabDesk — the shift supervisor, in the trusted zone |
| <https://localhost:5100/health> | pre-flight: expect `notes: 12`, `cloudConfigured: true` |

**Start FabPad before you need it.** Model load is measured in *tens* of seconds
(23 s on CUDA), so `DeviceHost.StartAsync` does it at launch behind a visible status line
rather than on the first keystroke. To start over: restart FabPad (its stores are in
memory) and `POST /reset` the hub — a few seconds, back to 12 notes.

Both windows up and the health check green? → [**Walkthrough**](#walkthrough--run-it-yourself).

---

## Walkthrough — run it yourself

Ten steps, ~20 minutes, in order and **without resetting in between** — the state
accumulates, and several steps only work because an earlier one left something behind. Each
one names what to paste, what to watch for, and the test that pins the same behaviour so you
can read the assertion next to the screen.

Everything here works without a cloud key except steps 1, 2, 9 and 10, which need T3
configured ([`SETUP.md`](SETUP.md) § 2). Without it those steps fail loudly with a message
naming the file to create, which is itself the behaviour described in
[Invariants](#invariants--read-before-editing) #11.

**Before you start.** Both processes running, then:

```bash
curl -X POST https://localhost:5100/reset
curl https://localhost:5100/health      # notes:12, seeded:12, cloudConfigured:true
```

In FabPad's header, confirm: `K. Nagy · <shift> <today> · PAD-07 · CR-2` — `👷 At the bench` —
(the shift is read off your wall clock: `Morning` between 06:00 and 18:00, `Night` outside it,
and a night shift carries the date it *began*) —
`🟢 Vendor reachable` — `Deployment policy: CloudOnly`. In the Hardware panel, confirm the
resolved provider is your discrete GPU and not the integrated one (see
[Configuration](#configuration)); if it is the integrated GPU, everything below still works
and is simply three times slower.

### 1 · The comfortable default

`CloudOnly` · at the bench. Tick **ask for cloud-grade prose**, paste, **Log note**:

```text
etch06 chmbr A vac pump noisier than usual, raised PM ticket, tool still in production
```

**Watch:** tier badge `T3`, reason `mode: CloudOnly`, ~1.6 s, and the note flips to `SYNCED` —
it reached the hub immediately, because the pad is still on the fab's LAN.

This is the architecture most applications ship. Nothing is wrong with it yet.

### 2 · What just left the building

Same posture, same door, no reset. **Log note**:

```text
etch06 chmbr A unif 93.8% recipe RX-9 ramp 5.2s - off spec
```

**Watch:** it works just as well — that is the problem. The **payload panel** (top right)
turns red: the note **verbatim**, `RX-9`, `5.2s`, `93.8%`, with a byte count under it. No
error, no warning, nothing objected.

There is no redact checkbox you forgot to tick. In this posture there is nothing on the
device that *could* redact — deciding which spans are fab IP needs a local model, and there
isn't one. That is an architecture problem, not a missing feature, which is why the answer
later is not "add redaction" but "add a local model".

> Note that the extracted fields came back filled in anyway. `CloudOnly` runs the *same*
> extraction call, on the vendor's model instead of the device's — see
> [How a note is routed](#how-a-note-is-routed).

*Pinned by* `RoutingTests` · `#region CloudOnly`.

### 3 · Into the cleanroom

Click the door toggle — the header flips to `🚪 In the cleanroom`. **Leave the posture on
`CloudOnly`.** Open `src/FabLog.Core/Airlock.cs` while you are here; it is ten lines. Then
**Log note**:

```text
etch03 chmbr B unif 94.2% recipe RX-7 ramp 4.5s - 3rd shift running soft, off spec
```

**Watch:** the result block turns red — reason `vendor unreachable`, path `T3 ✖`, and a
genuine `WSAHOST_NOT_FOUND` socket error underneath. The tray light goes
`🔴 Vendor unreachable since HH:mm:ss`. The pending counter reads
`🏢 1 awaiting the Central System`.

**Watch what else went quiet.** The hub is running perfectly on loopback and the pad cannot
reach it either. That is deliberate: a cleanroom blocks the fab's own LAN exactly as well as
it blocks the internet, so there is no loopback exemption in the handler. The note is *held*,
not lost.

> If `Chamber` comes back blank here, that is expected — with the vendor unreachable the
> offline fallback parser wants the word "chamber", not "chmbr". Step 4 fills it in.

*Pinned by* `RoutingTests` · `#region Offline` and
`A_sealed_cleanroom_cuts_the_fabs_own_network_too`.

### 4 · The device stands alone

Still sealed. Set **Deployment policy → `EdgeOnly`**. The text is still in the box — **Log
note** again.

**Watch:** badge `T1`, reason `policy: edge only`, ~2.9 s. Extracted:
`ETCH-03 · B · uniformity · 94.2% · Watch`. The payload panel turns **green** —
*"Nothing. No bytes crossed either boundary."* Pending → **2**.

Same binary, same call site, same `IChatClient`. One line of policy changed. And the vendor
was not tried and failed — it was **never asked**; the ledger says `policy: edge only`, not
`vendor unreachable`.

> ✅ **Check the extraction before moving on.** `ETCH-03 / B / uniformity` is what makes step
> 8 find *three* complaints instead of two. If any field is wrong, log it once more with
> blunter wording: `etch03 chamber B uniformity 94.2 percent off spec third shift running soft`.

*Pinned by* `RoutingTests` · `#region EdgeOnly`.

### 5 · Degraded, not dead

**Do not touch the door.** Set **Deployment policy → `Hybrid`**, then **Log note**:

```text
etch06 chmbr A post clean particle count 9 per wafer, recipe RX-9 ramp 5.2s - back in spec
```

**Watch:** it is **not** red. Path `T1`, reason `vendor unreachable`, an amber badge —
*"cloud unavailable — local result stands"*. Pending → **3**.

Same wall, same second, same unreachable vendor as step 3 — and the opposite outcome, because
this posture has a local path. The note is already structured, classified and masked; only
the prose is missing. **Dead, versus merely disappointed.**

Scroll the ledger: `policy: edge only` and `vendor unreachable` now sit on adjacent rows.
Two different words for two situations that look identical on screen — one is a policy
decision, the other is the wire.

⚠️ Nothing was queued for the cloud, and nothing ever is. Deferring a *delivery* is useful;
deferring an *answer* someone is standing there waiting for is worse than an error.

*Pinned by* `RoutingTests` · `#region Hybrid` and
`Nothing_bound_for_the_cloud_is_ever_stored_up`.

### 6 · The limits, stated honestly

Still sealed. In **Ask this device**, ask:

```text
anything odd about ETCH-03 today?
```

**Watch:** a refusal, not an answer — *"I can only see this device's notes — N from your
shift… A cross-technician trend needs every technician's notes, and they are not on this
device."*

That is not the model being small. It is this device holding one technician's notes. No
larger model fixes it; only moving the workload to where the data is does — which is step 8.

While you are here, the other three limits are on screen: **latency** (local ~968 ms vs
cloud ~1590 ms in the ledger — local wins), **per note** (~2.9 s, because one note is three
calls), and **compute** (`FabLog:T1:ForceVariant: "generic-cpu"` and a restart makes the same
build take 36.7 s per note — a 12.6× penalty; see [Measured numbers](#measured-numbers)).

*Pinned by* `RoutingTests` · `#region Scope`.

### 7 · Back in range

There is a **`Sync now`** button. Note where it is, then don't press it.

Click the door toggle → `👷 At the bench`. **Hands off the mouse.**

**Watch:** `🏢 3` → `2` → `1` → gone, within a few seconds, and every note flips to `SYNCED`.

Nothing polled for a signal. `DeviceHost`'s drain loop simply tries to send whatever is
pending every two seconds, oldest first, serially, stopping at the first failure — and a send
*succeeding* is how the device discovers the wire came back. Offline was not an outage; it
was a phase.

*Pinned by* `RoutingTests` · `#region Pending`.

### 8 · The question no device could answer

Switch to the browser at <https://localhost:5100> and **refresh** — FabPad syncs over HTTP
and does not push to this page, so the corpus counts are computed when the page renders.

**Watch:** the header now reads **17 notes · 3 technicians · 2 cleanrooms** — the seed's
12 notes from 2 technicians, plus the 5 you wrote. Three of those five only arrived a moment
ago, in step 7.

Expand **"All notes (17)"**. It is a feed, so it reads **newest first**: your five sit at the
top, above the seed, each with a coloured severity dot in FabPad's own three colours and the
technician's raw shorthand printed under the cleaned sentence. The seed is floated to land
behind whatever time you started the app, which is what keeps that ordering meaningful —
see `HubStore.ShiftToToday`. The evidence table further down, under a finding, stays
chronological on purpose.

Ask:

```text
anything odd about ETCH-03 today?
```

**Watch:** a red `⚠️ ESCALATE` finding — *three* uniformity complaints on ETCH-03 chamber B,
from *three* technicians across *three* shifts, with counts of `3 / 3 / 3`. No single note
flags a problem. The third complaint is the one you wrote inside the cleanroom in step 4; it
only arrived because of step 7.

Then scroll down to **"What crossed the border — everything the vendor received"**: tool,
chamber, metric, severity, initials, timestamps, counts — and **not one word anybody wrote**.
Compare it with **"The notes behind it"** directly above, which shows the full text, because
the supervisor is inside the fab's own jurisdiction and the vendor is not.

The counts are the hub's own arithmetic over the rows it holds, not the model's — the model
writes the sentence and never the numbers.

*Pinned by* `TrendTests` and `The_real_model_finds_the_trend_from_columns_alone`.

### 9 · Mask, and restore

Back to FabPad. Posture **`Hybrid`**, door **at the bench**, tray `🟢 Vendor reachable` —
nothing is forcing your hand this time. Tick **ask for cloud-grade prose** and paste **the
same note as step 2**:

```text
etch06 chmbr A unif 93.8% recipe RX-9 ramp 5.2s - off spec
```

**Watch:** the payload panel — the same panel that was red in step 2 — is now **amber**, and
where the recipe, ramp and yield figures were, it shows `⟦R1⟧`, `⟦R2⟧`, `⟦R3⟧`. Expand
*"N values held back on the device"* to see the mapping. The note still got written up.

> How many spans come back depends on the local model, which runs *in addition* to the
> regex rules and is free to flag formats the rules don't know. Erring wide is the correct
> direction for a control — but if a run masks so much that the brief reads as placeholders
> end to end, that is the 1.5B being enthusiastic, not a bug, and `Redaction.cs` is where
> the rule set lives.

Then read the side-by-side block: 🧠 *on this device* against ☁️ *written in the cloud*. Same
facts, both correct, one of them reading like a person wrote it. You don't have to choose
between them — you have to decide what is allowed to leave.

The restore is a **dictionary lookup on the device**, never a second model call. Prose that
reads perfectly with the wrong value in it is the one failure nobody catches by reading it.

> This is a **control, not a guarantee**. A 1.5B detecting fab IP will have false negatives.
> The regexes cannot have a bad day and the model catches formats the regexes don't; the
> union of the two is the wall, and the ledger records the literal payload so a miss is
> detectable afterwards. The alternative posture — step 2 — sent everything and left nothing
> to review.

*Pinned by* `RoutingTests` · `#region Hybrid` and
`The_vendor_model_reproduces_placeholders_verbatim`.

### 10 · The report, and the ledger

Scroll to **End of shift**, tick **ask the cloud to write it up**, and press
**`Compile shift report (N notes)`**.

**Watch, in this order:**

1. the **facts table** — tool/chamber/metric/severity, grouped and counted, computed on the
   device by arithmetic, and it never leaves;
2. the badges — `device→cloud` · `N ms` · `N notes` · **`N masked before it crossed`** ·
   **`submitted + saved locally`**;
3. **"What left this device"** — the masked brief, which is all that travelled;
4. the narrative, with the real values back in it.

Refresh FabDesk: the report is at the top, with its real values, because the hub is inside
the fab's jurisdiction. The technician keeps a local copy whether or not submission worked.

Finally, scroll FabPad's **ledger** — it has been filling in since step 1. Read the `Why`
column top to bottom: `mode: CloudOnly` ×2, `vendor unreachable`, `policy: edge only`,
`vendor unreachable`, `policy: fab IP detected`, then the report row. The footer counts how
many of those sent nothing at all.

That table is the artefact. Every route in the system funnels through one `RecordAsync`,
which is exactly why it is complete — nothing *can* skip it — and it records the **literal
payload**, not a hash, so what you are reading is what actually crossed.

*Pinned by* `RoutingTests` · `#region ShiftReport` and `#region Ledger`.

### The same sequence, as a test

`EndToEndTests.The_whole_flow_runs_end_to_end_in_order` runs steps 1–8 and 10 against one
device and one hub with nothing reset, asserting what the isolated tests cannot: that the
steps are **linked**. It finishes on the ledger — five entries, five paths, five reasons —
and it runs in milliseconds against deterministic fakes, with no network, no model and no
credentials:

```bash
dotnet test FabLog.slnx --filter "FullyQualifiedName~EndToEndTests"
```

---

## Configuration

One `FabLog` section, bound by both processes. Real values live only in
`appsettings.Development.json`, which is **gitignored**; the committed `appsettings.json`
carries `<…>` placeholders, and `T3Options.IsConfigured` treats any value starting with
`<` as *not configured*.

| Key | Default | Notes |
|---|---|---|
| `FabLog:Mode` | `CloudOnly` | `CloudOnly` · `EdgeOnly` · `Hybrid`. Hot-reloads; also set from the chrome selector. |
| `FabLog:Hub` | `https://localhost:5100` | A destination inside the fab, never a route out. |
| `FabLog:Author` / `:DeviceId` / `:Cleanroom` | `K. Nagy` / `PAD-07` / `CR-2` | Travel with every note. |
| `FabLog:Shift` | `Morning`/`Night` + date, computed | Both halves read off the wall clock (06:00–17:59 is Morning); a night shift keeps the date it began. Set explicitly to pin a value. |
| `FabLog:T1:ModelAlias` | `qwen2.5-1.5b` | The alias chooses the **model**. |
| `FabLog:T1:FallbackAlias` | `qwen2.5-0.5b` | Used if the primary is not in this machine's catalogue — weaker, but it runs. |
| `FabLog:T1:VariantPreference` | `["cuda-gpu","trtrtx","openvino","generic-gpu","generic-cpu"]` | The preference list chooses the **silicon**. First match wins; the SDK's own resolution is the final fallback. |
| `FabLog:T1:ForceVariant` | `""` | e.g. `generic-cpu` — the same build with no GPU, one config line, no rebuild. |
| `FabLog:T3:Endpoint` | — | **Scheme and host only.** `T3Options.ResourceEndpoint` strips any path, so all three endpoints the portal offers work. |
| `FabLog:T3:Deployment` | — | Rendered in the UI beside every cloud call. |
| `FabLog:T3:ApiKey` | — | Development file only. |
| `FabLog:T3:TimeoutSeconds` | `10` | |
| `FabLog:Demo:PadOnNetwork` | `true` | Starting position of the cleanroom door. |
| `FabLog:Demo:DrainIntervalSeconds` | `2` | How often the device *tries to send* — not a connectivity poll. |

> ⚠️ **`VariantPreference` is the setting that decides how fast T1 is.** Letting the SDK
> resolve the alias on a two-GPU machine picks the *integrated* GPU and leaves the NVIDIA
> card idle — a measured **3.4×** penalty. Check the Hardware panel: if it says *OpenVINO
> (integrated GPU)*, every local call is three times slower than it needs to be.

> ⚠️ `appsettings.Development.json` overrides the base file key-by-key and is a
> **complete** copy of it, so the committed `appsettings.json` is inert at runtime — it is
> the template and the documentation, not a live edit surface.

---

## TrustHub HTTP API

`https://localhost:5100` (set in `Program.cs`, which overrides `launchSettings.json`).
Ready-made requests for all of these are in [`demo.http`](demo.http).

| Method | Route | Purpose |
|---|---|---|
| `GET` | `/health` | `{ ok, notes, seeded, cloudConfigured, deployment }`. For a human at a terminal — **not** a reachability probe; nothing in the app polls it. |
| `POST` | `/sync` | A note, in full. **Upsert by id**: a retried sync must not double-count a complaint, and a note first logged under `CloudOnly` (blank `toolId`, no local model ran) must be allowed to be replaced by its richer re-processing. |
| `POST` | `/report` | A finished shift report — real values restored, because the hub is in the fab's own jurisdiction. |
| `GET` | `/reports` | Submitted reports, newest first. |
| `POST` | `/query` | The fleet-wide question. `503` with the name of the file to create when T3 is unconfigured — it never fakes an answer. |
| `GET` | `/notes` | Everything the hub holds. Diagnostics only. |
| `POST` | `/reset` | Back to the seed corpus alone, in one call. |

---

## Tests

```bash
dotnet test FabLog.slnx
dotnet test FabLog.slnx --filter Category=Live --logger "console;verbosity=detailed"
```

| File | Covers |
|---|---|
| `RoutingTests.cs` | one region per routing situation: `EdgeOnly`, `CloudOnly`, `Offline`, `Scope`, `Pending`, `Hybrid`, `Ledger`, `ShiftReport` |
| `TrendTests.cs` | the fleet-wide finding — the seed corpus, the hub's arithmetic, the digest |
| `EndToEndTests.cs` | the [walkthrough](#walkthrough--run-it-yourself) in sequence, one device and one hub, nothing reset |
| `LiveCloudTests.cs` | the things only a real endpoint can answer |
| `Fakes.cs` | the instruments: `FakeChat` (records every payload it was handed), `OfflineChat` (throws like a dead wire), `FakeSink`, `DeviceReplies` (scripted replies, including deliberately malformed ones) and `Rig` |

**70 tests run with no network, no model and no credentials, in ~170 ms.** They assert
what the *architecture* does with a model's answer — which tier ran, what crossed a wall,
what the ledger recorded. The numbers below measured the models; this suite checks the
routing.

The six `Category=Live` tests skip themselves while T3 is unconfigured and start running
the moment the Development file exists — no flag to remember. They answer what a fake
cannot: does the vendor model reproduce `⟦R1⟧` verbatim, what does the round trip cost,
can a real model find the trend from columns alone, and does the quota survive six
back-to-back drains.

---

## Measured numbers

On a laptop with an RTX A1000 and an Intel UHD integrated GPU, against `qwen2.5-1.5b`
locally and a live Azure deployment for the cloud. First call discarded as cold; the
median of the warm calls reported.

| Variant | Silicon | Warm median, one call | A note (3 calls) |
|---|---|---|---|
| `qwen2.5-1.5b-instruct-cuda-gpu:4` | RTX A1000 | **968 ms** | **2.9 s** |
| `…-generic-gpu:4` | DirectML | 1 399 ms | 4.2 s |
| `…-openvino-gpu:2` | Intel UHD | 3 307 ms | 9.9 s |
| `…-generic-cpu:4` | CPU | 12 219 ms | 36.7 s |

Processing one note costs three T1 calls — extract, classify, clean — which is why the
per-note column matters more than the per-call one, and why `VariantPreference` above is
worth getting right: CUDA to integrated GPU is a 3.4× penalty for free.

`LoadAsync` costs 8.6–23.2 s depending on variant, which is why start-up pre-warms rather
than loading on the first keystroke.

The cloud round trip (`gpt-5.4-mini`, Data Zone Standard EU) is **1 590 ms** warm median
over the pad's own wire; six sequential drains land in ~9 s without hitting a rate limit
at 30K TPM. Both are re-measurable against your own deployment — that is what
`Measure_the_cloud_round_trip` and `Six_back_to_back_drains_do_not_hit_a_rate_limit` in
`LiveCloudTests.cs` are for.

---

## Credentials and safety

- **Two files hold a key**, one per process, both named `appsettings.Development.json` and
  both matched by the `.gitignore` rule `appsettings.*.local.json` / `appsettings.Development.json`.
  Verify before any commit:

  ```bash
  git check-ignore -v src/FabLog.FabPad/appsettings.Development.json src/FabLog.TrustHub/appsettings.Development.json
  ```

  Two lines of output means two files ignored. No output means a key is about to be
  committed.

- **The device holds a real cloud credential**, and that is deliberate. Brokering the
  device's cloud calls through the hub would remove the key from the device and make the
  hub the device's way out — which is the property the architecture is arguing against.
  The device reaches the vendor over its own wire, so it carries its own key.
- **Rotate the key if it has been anywhere but the two ignored files.** A key shown once
  on a shared screen is a key in someone's scrollback.
- **Nothing is persisted.** Both stores are in memory; the data lives as long as the
  processes do.

---

## Invariants — read before editing

These are the properties the whole design rests on. Breaking one produces a build that
still runs and no longer means anything.

1. **`FabLog.Core` takes no infrastructure dependency.** Only
   `Microsoft.Extensions.AI.Abstractions`. Every SDK lives in a composition root. (It is
   also a hard constraint: the Foundry SDK ships for `net8.0-windows…` only, which is why
   FabPad carries the Windows moniker and Core does not.)
2. **Nothing asks whether it is online before calling.** Offline is discovered by failing.
   Adding a probe re-introduces the defect where the badge stays green through an airgap.
3. **The airlock toggle must not trigger the drain.** It would be instant and it would be a
   lie — the app would be *told* the network was back rather than finding out.
4. **Redaction happens on the device, before a destination is chosen.** The thing that
   builds the placeholder→value mapping must never be downstream of anything that might
   leak it. Unmasking is a dictionary lookup, never a model call.
5. **Tool IDs and chambers are not fab IP.** Mask them and the fleet-wide finding dies.
6. **Tool IDs and chambers are canonicalised at the boundary** (`ETCH03` → `ETCH-03`).
   Every model variant returns a different shape, and the fleet-wide finding groups by
   tool id: without this, one group of three becomes three groups of one and the trend
   disappears *silently*, behind a plausible-looking screen.
7. **The hub receives every note, in full, in every posture** — including `EdgeOnly`. The
   airgap goes around the fab, and the hub is inside it.
8. **The hub sends no prose to the vendor, ever.** Widening `AsDigest()` is widening wall 2.
9. **Counts come from the store, never from the model.** The model writes the sentence.
10. **Every route writes exactly one ledger entry, with the literal payload.** The audit
    trail is free precisely because nothing can skip `RecordAsync`.
11. **An unconfigured T3 fails loudly.** A stub that returned plausible prose would make
    "the cloud tier works" and "the cloud tier is wired" indistinguishable.

---

## License

[MIT](LICENSE) — use it, fork it, lift pieces of it into your own work.

The fab, its tools, its recipes, the seed corpus in `hub-notes.json` and every technician
in it are invented. Any resemblance to a real process is a coincidence, and none of it is
advice about running a real one.
