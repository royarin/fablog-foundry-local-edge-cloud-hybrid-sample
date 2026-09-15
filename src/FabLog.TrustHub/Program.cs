// ─────────────────────────────────────────────────────────────────────────────
//  FabLog.TrustHub — T2. The fab's own service, in the trusted zone.
//
//  ONE job the device cannot do: reason over EVERY technician's notes. The
//  device holds one person's. That is a fact about where the data is, not about
//  how big the model is — and it is the only honest reason to move a workload.
//
//  ⚠️ It deliberately does NOT reach the vendor on the device's behalf. A broker
//  in front of the cloud is still the device's way out, which would make
//  "cloud-only" a posture the SERVER holds. The pad has its own wire.
//
//  This is a DESTINATION, never a hop. Every note arrives here in full, in every
//  posture — but not always immediately: the technician works in a cleanroom that
//  blocks this server's LAN as thoroughly as it blocks the internet, so a shift's
//  notes arrive in a burst when they walk back out.
//
//  It is trusted and it is in another room. Those are different facts.
//
//  Runs on localhost:5100. In a real fab that is a server in the building.
// ─────────────────────────────────────────────────────────────────────────────
using FabLog.Core;
using FabLog.TrustHub;
using FabLog.TrustHub.Components;

var builder = WebApplication.CreateBuilder(args);

// appsettings.Development.json is loaded by default for the Development
// environment. Load it unconditionally anyway, so local settings work on a fresh
// clone without DOTNET_ENVIRONMENT being set.
builder.Configuration.AddJsonFile("appsettings.Development.json", optional: true, reloadOnChange: true);

var t3 = builder.Configuration.GetSection("FabLog:T3").Get<T3Options>() ?? new T3Options();
var seedPath = Path.Combine(AppContext.BaseDirectory, "Data", "hub-notes.json");

builder.Services.AddSingleton(t3);
// shiftSeedToToday: true — see HubStore's ctor. The seed corpus floats to
// "yesterday night" / "this morning" relative to whenever the app actually starts,
// instead of being pinned to a calendar date that goes stale the next day.
builder.Services.AddSingleton(new HubStore(seedPath, shiftSeedToToday: true));
builder.Services.AddSingleton(sp => new HubBrain(
    sp.GetRequiredService<HubStore>(), Cloud.Create(t3), t3.Deployment));

builder.Services.AddRazorComponents().AddInteractiveServerComponents();

// ⚠️ https, and this line is the single source of truth for the URL — it
// overrides launchSettings.json's own applicationUrl entirely (both profiles
// there are kept in sync with this, not the other way round). Requires the
// ASP.NET Core dev cert once per machine: `dotnet dev-certs https --trust`.
builder.WebHost.UseUrls("https://localhost:5100");

var app = builder.Build();
app.UseStaticFiles();
app.UseAntiforgery();

// ── GET /health ──────────────────────────────────────────────────────────────
// ⚠️ NOT a reachability probe, and FabPad deliberately does not poll it. Polling
// this on loopback would keep the device's badge green through an airgap.
// Connectivity is an observation of the last real call; this endpoint is here for
// a human at a terminal, nothing else.
app.MapGet("/health", (HubStore s, T3Options o) => Results.Ok(new
{
    ok = true,
    notes = s.Count,
    seeded = s.SeedCount,
    cloudConfigured = o.IsConfigured,
    deployment = o.IsConfigured ? o.Deployment : null,
}));

// ── POST /sync ───────────────────────────────────────────────────────────────
// Every note, in full — in every posture. This server is in the fab's own
// jurisdiction; you do not redact to yourself.
//
// ⚠️ But notes do NOT all arrive the moment they are written. The technician is
// in a cleanroom, which blocks the fab's own LAN as thoroughly as the internet,
// so a shift's worth of notes lands in a burst when they walk back out. Upsert by
// id: a retried sync must not double-count a complaint, AND a note first logged
// under a cloud-only posture (blank toolId, no local model ran) must be allowed to
// be replaced by its richer re-processing. See HubStore.Add.
app.MapPost("/sync", (HubNote note, HubStore store) =>
    Results.Ok(new SyncResponse(store.Add(note), store.Count)));

// ── POST /report ─────────────────────────────────────────────────────────────
// The hybrid flow's last step: a finished shift report, submitted from outside
// the cleanroom.
//
// ⚠️ What arrives is the report with its REAL VALUES IN. The device un-masked it
// locally after the vendor wrote the prose, because this server is in the fab's
// own jurisdiction. Only the trip across the border was masked. The technician
// also keeps a local copy, which is what makes losing this call survivable.
app.MapPost("/report", (SubmittedReport report, HubStore store) =>
{
    store.AddReport(report);
    return Results.Ok(new { reports = store.ReportCount });
});

app.MapGet("/reports", (HubStore s) => Results.Ok(s.Reports));

// ── POST /query ──────────────────────────────────────────────────────────────
// The fleet-wide question. Runs here and not on the device because the DATA is
// here — not because the model is bigger.
app.MapPost("/query", async (QueryRequest req, HubBrain brain, CancellationToken ct) =>
{
    var result = await brain.AnalyseAsync(req.Question, ct);
    return result.Failed ? Results.Problem(result.Error, statusCode: 503) : Results.Ok(result);
});

app.MapGet("/notes", (HubStore s) => Results.Ok(s.All));

// Back to the seed corpus alone — 12 notes and two complaints — in one call.
app.MapPost("/reset", (HubStore s) => { s.Reset(); return Results.Ok(new { notes = s.Count }); });

// 💻 FabDesk — the supervisor's screen: a different person, in a different zone.
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();

record SyncResponse(bool Added, int Total);
record QueryRequest(string Question);

/// <summary>Exposed so the test project can drive the hub in-process via WebApplicationFactory.</summary>
public partial class Program;
