using System.Text.Json;
using System.Text.Json.Serialization;
using GotvPlusServer.Models;
using GotvPlusServer.Services;

var builder = WebApplication.CreateBuilder(args);

// ── Services ────────────────────────────────────────────────
var store = new FragmentStore();
builder.Services.AddSingleton(store);
builder.Services.AddSingleton<GameStateService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<GameStateService>());

// JSON options
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

// CORS — dashboard needs access
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

// Port config
var port = Environment.GetEnvironmentVariable("PORT") ?? "5000";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

var app = builder.Build();
app.UseCors();

var jsonOpts = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Converters = { new JsonStringEnumConverter() }
};

var authKey = builder.Configuration["BroadcastAuth"] ?? 
    Environment.GetEnvironmentVariable("BROADCAST_AUTH") ?? "";

// ════════════════════════════════════════════════════════════
//  1. INGEST — CS2 server POSTs fragments here
//     URL pattern: /{token}/{fragment}/start|full|delta
// ════════════════════════════════════════════════════════════

app.MapPost("/ingest/{token}/{fragment:int}/start", async (
    string token, int fragment, HttpRequest req) =>
{
    if (!CheckAuth(req)) return Results.StatusCode(403);
    
    var tps = 64;
    if (req.Query.TryGetValue("tps", out var tpsVal) && int.TryParse(tpsVal, out var parsed))
        tps = parsed;
    
    var body = await ReadBody(req);
    store.StoreStart(token, fragment, body, tps);
    return Results.Ok();
});

app.MapPost("/ingest/{token}/{fragment:int}/full", async (
    string token, int fragment, HttpRequest req) =>
{
    if (!CheckAuth(req)) return Results.StatusCode(403);
    var body = await ReadBody(req);
    store.StoreFull(token, fragment, body);
    return Results.Ok();
});

app.MapPost("/ingest/{token}/{fragment:int}/delta", async (
    string token, int fragment, HttpRequest req) =>
{
    if (!CheckAuth(req)) return Results.StatusCode(403);
    var body = await ReadBody(req);
    store.StoreDelta(token, fragment, body);
    return Results.Ok();
});

// ════════════════════════════════════════════════════════════
//  2. BROADCAST RE-SERVE — HttpBroadcastReader reads from here
//     Pattern matches what demofile-net expects
// ════════════════════════════════════════════════════════════

// Broadcast routes — mapped at BOTH /broadcast/ AND root level
// because HttpBroadcastReader may use absolute paths (/sync)
// which bypass HttpClient.BaseAddress

IResult HandleSync(HttpRequest req)
{
    if (!store.IsActive)
        return Results.StatusCode(404);
    int? frag = null;
    if (req.Query.TryGetValue("fragment", out var f) && int.TryParse(f, out var fv))
        frag = fv;
    // HttpBroadcastReader uses GetFromJsonAsync — must return JSON, not text!
    var syncData = store.GetSyncJson(frag);
    return Results.Json(syncData);
}

IResult HandleStart(int fragment)
{
    var data = store.GetStart(fragment);
    return data != null ? Results.Bytes(data, "application/octet-stream") : Results.StatusCode(404);
}

async Task<IResult> HandleFull(int fragment)
{
    // Wait up to 10s for the fragment to arrive from CS2
    for (var i = 0; i < 20; i++)
    {
        var data = store.GetFull(fragment);
        if (data != null) return Results.Bytes(data, "application/octet-stream");
        if (!store.IsActive) return Results.StatusCode(404);
        await Task.Delay(500);
    }
    return Results.StatusCode(404);
}

async Task<IResult> HandleDelta(int fragment)
{
    for (var i = 0; i < 20; i++)
    {
        var data = store.GetDelta(fragment);
        if (data != null) return Results.Bytes(data, "application/octet-stream");
        if (!store.IsActive) return Results.StatusCode(404);
        await Task.Delay(500);
    }
    return Results.StatusCode(404);
}

// /broadcast/ prefix (for manual testing, playcast clients)
app.MapGet("/broadcast/sync", (HttpRequest req) => HandleSync(req));
app.MapGet("/broadcast/{fragment:int}/start", (int fragment) => HandleStart(fragment));
app.MapGet("/broadcast/{fragment:int}/full", (int fragment) => HandleFull(fragment));
app.MapGet("/broadcast/{fragment:int}/delta", (int fragment) => HandleDelta(fragment));

// Root level (for HttpBroadcastReader which may use absolute paths)
app.MapGet("/sync", (HttpRequest req) => HandleSync(req));
app.MapGet("/{fragment:int}/start", (int fragment) => HandleStart(fragment));
app.MapGet("/{fragment:int}/full", (int fragment) => HandleFull(fragment));
app.MapGet("/{fragment:int}/delta", (int fragment) => HandleDelta(fragment));

// ════════════════════════════════════════════════════════════
//  3. REST API — Dashboard polls these
//     Same endpoints as CS2LivePlugin for compatibility
// ════════════════════════════════════════════════════════════

var gameState = app.Services.GetRequiredService<GameStateService>();

app.MapGet("/state", () => Results.Json(gameState.GetState(), jsonOpts));

app.MapGet("/players", () =>
{
    var s = gameState.GetState();
    return Results.Json(s.Players, jsonOpts);
});

app.MapGet("/score", () =>
{
    var s = gameState.GetState();
    return Results.Json(new { ct = s.CT.Score, t = s.T.Score, round = s.RoundNumber }, jsonOpts);
});

app.MapGet("/round", () =>
{
    var s = gameState.GetState();
    return Results.Json(s.CurrentRound, jsonOpts);
});

app.MapGet("/teams", () =>
{
    var s = gameState.GetState();
    return Results.Json(new { ct = s.CT, t = s.T }, jsonOpts);
});

app.MapGet("/history", () =>
{
    var s = gameState.GetState();
    return Results.Json(s.RoundHistory, jsonOpts);
});

app.MapGet("/health", () =>
{
    return Results.Json(new
    {
        status = store.IsActive ? "active" : "waiting",
        version = "gotv-plus-1.0",
        broadcastActive = store.IsActive,
        latestFragment = store.LatestFragment,
        lastReceivedAgoMs = store.IsActive
            ? (long)(DateTime.UtcNow - store.LastReceived).TotalMilliseconds
            : -1,
        connectedPlayers = gameState.GetState().Players.Count,
        timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
    });
});

// Reset endpoint for starting a new match
app.MapPost("/reset", () =>
{
    store.Reset();
    return Results.Ok(new { message = "Reset complete" });
});

// Root
app.MapGet("/", () => Results.Json(new
{
    name = "CS2 GOTV+ Live Server",
    status = store.IsActive ? "broadcast_active" : "waiting_for_broadcast",
    endpoints = new[]
    {
        "GET  /state     — full match state (dashboard polls this)",
        "GET  /players   — player list",
        "GET  /score     — scores",
        "GET  /round     — current round + bomb",
        "GET  /teams     — team info",
        "GET  /history   — round history",
        "GET  /health    — health check",
        "POST /reset     — reset for new match",
        "POST /ingest/{token}/{n}/start|full|delta — CS2 pushes here"
    }
}));

app.Run();

// ── Helpers ─────────────────────────────────────────────────

static async Task<byte[]> ReadBody(HttpRequest req)
{
    using var ms = new MemoryStream();
    await req.Body.CopyToAsync(ms);
    return ms.ToArray();
}

bool CheckAuth(HttpRequest req)
{
    if (string.IsNullOrEmpty(authKey)) return true;
    var header = req.Headers["Authorization"].FirstOrDefault();
    return header == authKey;
}
