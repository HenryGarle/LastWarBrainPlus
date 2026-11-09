using System.Text.Json;
using System.Text.Json.Nodes;
using LastWar.Client;
using LastWar.Client.Auth;
using LastWar.Server;

// Content root is pinned to the exe, not the working directory, so wwwroot is found
// however the host is launched (shortcut, service, scheduled task, another folder).
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});
// root: the folder that holds data\ (--root, or the "root" environment variable in the container).
var root = builder.Configuration["root"] ?? (OperatingSystem.IsWindows() ? @"C:\Temp\LWC" : "/srv/lastwar");
var dataDir = Path.Combine(root, "data");
// Service accounts this server logged in for an operation, kept connected a while after (see RunOnRunner).
var lingering = new System.Collections.Concurrent.ConcurrentDictionary<string, bool>();
var lastUse = new System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset>();
foreach (var d in new[] { "runners", "sessions", "chat", "api", "migration", "raw", "assets" })
    Directory.CreateDirectory(Path.Combine(dataDir, d));   // a fresh host starts with an empty data folder
// Postgres (Db / PgConfig): --pg "<connection string>", else LASTWAR_PG, else the local docker setup.
var db = LastWar.Storage.Postgres.PgConfig.Resolve(builder.Configuration["pg"], root);

var fleet = new RunnerRegistry(
    Path.Combine(root, "data", "runners"),
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".lastwar_goclient_session.json"));
var sessions = new SessionStore(
    Path.Combine(root, "data", "runners"), Path.Combine(root, "data", "sessions"));
var pending = new System.Collections.Concurrent.ConcurrentDictionary<string, PendingEmailLogin>();
var catalog = new CommandCatalog(Path.Combine(root, "data", "api", "commands.csv"));
var explorer = new DbExplorer(db);
var chatHub = new ChatHub(db, Path.Combine(root, "data", "chat", "follow.json"));
// A brand-new database has no "lastwar" schema yet (the connection's search path); make it first.
var schema = new Npgsql.NpgsqlConnectionStringBuilder(db).SearchPath?.Split(',')[0].Trim() is { Length: > 0 } sp ? sp : "lastwar";
LastWar.Storage.Db.Exec(db, $"CREATE SCHEMA IF NOT EXISTS \"{schema.Replace("\"", "")}\";");
var insights = new LastWar.Storage.Insights(db);
insights.EnsureSchema();
// Applicant captures were CSV-only before they had a table. Bring any that exist across; the
// table's key makes this a no-op for rows already there.
foreach (var csv in Directory.EnumerateFiles(Path.Combine(dataDir, "migration"), "applicants_*.csv"))
{
    var stamp = System.Text.RegularExpressions.Regex.Match(Path.GetFileName(csv), @"(\d{4}-\d{2}-\d{2})_(\d{2})(\d{2})");
    var captured = stamp.Success
        ? DateTimeOffset.Parse($"{stamp.Groups[1]}T{stamp.Groups[2]}:{stamp.Groups[3]}:00Z")
        : new DateTimeOffset(File.GetLastWriteTimeUtc(csv), TimeSpan.Zero);
    insights.ImportApplicants(csv, captured);
}

// Behind a reverse proxy (Caddy on the host): trust its X-Forwarded-For / -Proto so the client
// address, HTTPS and the Secure cookie are right. Only with --proxy true - never trust the header
// from the open internet.
var behindProxy = builder.Configuration["proxy"] == "true";
if (behindProxy)
    builder.Services.Configure<Microsoft.AspNetCore.Builder.ForwardedHeadersOptions>(o =>
    {
        o.ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto;
        o.KnownIPNetworks.Clear();   // the proxy is another container on the compose network
        o.KnownProxies.Clear();
    });

builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.WithOrigins("http://localhost:5173").AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();
if (behindProxy) app.UseForwardedHeaders();
app.UseCors();
app.UseDefaultFiles();
// index.html is never cached, so a new build shows on the next load (the hashed assets can be).
var noCacheIndex = new StaticFileOptions
{
    OnPrepareResponse = f => { if (f.File.Name == "index.html") f.Context.Response.Headers.CacheControl = "no-cache"; },
};
app.UseStaticFiles(noCacheIndex);

// ---- access: password sign-in (Access / AccessStore). The pages themselves are public; every /api
// call needs a signed-in browser - or, in dev mode, a request straight from this machine.
var access = new LastWar.Storage.AccessStore(db);
access.EnsureSchema();
var devMode = app.Environment.IsDevelopment() || builder.Configuration["dev"] == "true";
// Locked out (lost the only admin password)? Start once with --new-admin "<name>": it prints a new
// admin password and exits.   docker compose run --rm app --new-admin Henry
if (builder.Configuration["new-admin"] is { Length: > 0 } adminName)
{
    var (_, pw) = access.Create(adminName, "admin");
    Console.WriteLine($"NEW ADMIN PASSWORD for {adminName}: {pw}");
    return;
}
// A fresh install has no password that can sign in: make the first admin and print it once.
if (!devMode && !access.HasPasswordAdmin())
{
    var (_, first) = access.Create("Owner", "admin");
    Console.WriteLine($"FIRST ADMIN PASSWORD (shown once - sign in, then make your own in the Access tab): {first}");
}
if (devMode) Console.WriteLine("dev mode: requests from this machine are signed in as 'Local development' (admin)");
_ = Task.Run(async () =>
{
    while (true)
    {
        await Task.Delay(TimeSpan.FromSeconds(30));
        try { access.Flush(); } catch (Exception ex) { Console.Error.WriteLine($"access usage: {ex.Message}"); }
    }
});
app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path.Value ?? "";
    if (!path.StartsWith("/api/") || path.StartsWith("/api/auth/")) { await next(); return; }
    var key = Access.Who(ctx, access, devMode);
    if (key is null) { ctx.Response.StatusCode = 401; await ctx.Response.WriteAsJsonAsync(new { error = "sign in first", signIn = true }); return; }
    if (!key.IsAdmin && !Access.ViewerMay(path, ctx.Request.Method, key.Role))
    {
        ctx.Response.StatusCode = 403;
        await ctx.Response.WriteAsJsonAsync(new { error = "this password is for viewing stats only" });
        return;
    }
    access.Touch(key.Id, Access.Area(path, ctx.Request.Method));
    await next();
});

// ---- scheduled operations (Scheduler): the hourly activity pulse and anything else on a timetable.
var scheduler = new LastWar.Operations.Scheduler(db, dataDir);
scheduler.OnRunner = async (id, op, args, progress, ct) =>
    (await RunOnRunner(id, op, args, progress, ct))["summary"]?.ToString() ?? "done";
scheduler.SeedDefaults();
// --scheduler off: nothing runs on its own (a test copy, or a new host before guests are checked).
if (builder.Configuration["scheduler"] != "off") _ = Task.Run(() => scheduler.LoopAsync(app.Lifetime.ApplicationStopping));
else Console.WriteLine("scheduler off: scheduled jobs only run when started by hand");
app.MapGet("/api/schedule", () => new { jobs = scheduler.View(), runs = scheduler.Store.Runs(null, 100) });
app.MapPost("/api/schedule/{id}/run", (string id) => scheduler.Start(id, "by hand") ? Results.Ok() : Results.Conflict(new { error = "already running" }));
app.MapPost("/api/schedule/{id}/stop", (string id) => scheduler.Stop(id) ? Results.Ok() : Results.BadRequest(new { error = "not running" }));
app.MapPost("/api/schedule/{id}/pause", (string id) => { scheduler.Pause(id); return Results.Ok(); });
app.MapPost("/api/schedule/{id}/resume", (string id) => { scheduler.Resume(id); return Results.Ok(); });
app.MapPut("/api/schedule/{id}", (string id, ScheduleEdit e) =>
{
    if (e.EveryMinutes is < 5 or > 10080) return Results.BadRequest(new { error = "every 5 minutes to 7 days" });
    if (e.EveryMinutes is { } m) scheduler.SetEvery(id, m);
    if (e.Runner is { } r)
    {
        if (r.Length > 0 && !fleet.All.Any(x => x.Spec.Id == r)) return Results.BadRequest(new { error = $"no runner '{r}'" });
        scheduler.Store.SetArg(id, "runner", r);
    }
    return Results.Ok();
});

app.MapPost("/api/auth/login", (LoginRequest req, HttpContext ctx) =>
{
    var ip = Access.Ip(ctx);
    if (Access.Throttled(ip)) return Results.Json(new { error = "too many wrong passwords - try again in 10 minutes" }, statusCode: 429);
    if (access.Login(req.Password ?? "", ip, ctx.Request.Headers.UserAgent.ToString()) is not { } s)
    {
        Access.Failed(ip);
        return Results.Json(new { error = "that password is not recognised" }, statusCode: 401);
    }
    ctx.Response.Cookies.Append(Access.Cookie, s.Token, new CookieOptions
    {
        HttpOnly = true, SameSite = SameSiteMode.Lax, Secure = ctx.Request.IsHttps, Expires = DateTimeOffset.UtcNow.AddDays(60), IsEssential = true,
    });
    access.Touch(s.Key.Id, "sign in");
    return Results.Ok(new { name = s.Key.Name, role = s.Key.Role });
});
app.MapPost("/api/auth/logout", (HttpContext ctx) =>
{
    if (ctx.Request.Cookies.TryGetValue(Access.Cookie, out var token)) access.Logout(token);
    ctx.Response.Cookies.Delete(Access.Cookie);
    return Results.Ok();
});
app.MapGet("/api/auth/me", (HttpContext ctx) =>
    Access.Who(ctx, access, devMode) is { } k
        ? Results.Ok(new { id = k.Id, name = k.Name, role = k.Role, dev = k.Id == LastWar.Storage.AccessStore.DevKeyId })
        : Results.Json(new { error = "sign in first", signIn = true }, statusCode: 401));

// Admin: the passwords themselves. A new or reset password is returned once and never again.
app.MapGet("/api/access", () => { access.Flush(); return access.List(); });
app.MapPost("/api/access", (AccessRequest req) =>
{
    if (string.IsNullOrWhiteSpace(req.Name)) return Results.BadRequest(new { error = "give it a name" });
    var (id, password) = access.Create(req.Name, req.Role ?? "viewer");
    return Results.Ok(new { id, password });
});
app.MapPut("/api/access/{id}", (string id, AccessRequest req) => { access.Update(id, req.Name, req.Role, req.Disabled); return Results.Ok(); });
app.MapPost("/api/access/{id}/reset", (string id) => access.Reset(id) is { } p ? Results.Ok(new { id, password = p }) : Results.BadRequest(new { error = "cannot reset this one" }));
app.MapDelete("/api/access/{id}", (string id) => { access.Delete(id); return Results.Ok(); });

// ---- fleet

app.MapGet("/api/runners", () => fleet.All.Select(r => r.Snapshot()));

app.MapPost("/api/runners/reload", () => { fleet.Reload(); return fleet.All.Select(r => r.Snapshot()); });

app.MapPost("/api/runners/{id}/start", async (string id) =>
{
    var runner = fleet.Get(id);
    // One session per account: a second login evicts the first without an error.
    if (fleet.HolderOf(runner) is { } other)
        return Results.Conflict(new { error = $"'{other.Spec.Id}' already holds {runner.Spec.ConfigPath}" });
    try
    {
        await runner.StartAsync();
        // Chat rides on the session: open it now so the Chat tab is live without another click.
        // A chat failure is reported there, never as a failed start. Never on a privileged account:
        // a background poll loop is exactly the kind of use it must not get.
        if (runner.Spec.AccountTier != LastWar.Operations.AccountTier.Privileged) _ = Task.Run(async () =>
        {
            try { await chatHub.AttachAsync(runner); }
            catch (Exception ex) { Console.Error.WriteLine($"chat on {runner.Spec.Id}: {ex.Message}"); }
        });
        return Results.Ok(runner.Snapshot());
    }
    catch (Exception ex)
    {
        // A guest the server refused has spent its one login - it can never be used, so drop it.
        if (runner.Spent && ex is LoginFailedException) Forget(runner);
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapPut("/api/runners/{id}/tier", (string id, TierRequest req) =>
{
    var tier = (req.Tier ?? "").Trim().ToLowerInvariant();
    if (tier is not ("guest" or "service" or "privileged")) return Results.BadRequest(new { error = "tier must be guest, service or privileged" });
    try { return Results.Ok(fleet.SetTier(id, tier).Snapshot()); }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapPost("/api/runners/{id}/stop", async (string id) =>
{
    var runner = fleet.Get(id);
    await chatHub.Detach(id);
    await runner.StopAsync();
    // Guests are single-use: once stopped they cannot log in again, so clean up as we go.
    var snapshot = runner.Snapshot();
    if (runner.Spent) Forget(runner);
    return Results.Ok(snapshot);
});

void Forget(Runner r)
{
    try
    {
        // A guest on a server we track is kept for later (GuestKeeper); the rest go.
        if (r.Spec.Kind == "guest" && Path.GetDirectoryName(r.Spec.ConfigPath ?? "") is { Length: > 0 } home
            && LastWar.Operations.GuestKeeper.Retire(dataDir, db, home, r.Spec.Id + "_") is { } kept)
            Console.WriteLine($"kept guest {r.Spec.Id} (tracked server): {kept}");
        sessions.Delete(r.Spec.Id, withFiles: true); fleet.Remove(r.Spec.Id);
    }
    catch (Exception ex) { Console.Error.WriteLine($"cleanup of {r.Spec.Id}: {ex.Message}"); }
}

app.MapPost("/api/runners/{id}/call", async (string id, CallRequest req) =>
{
    if (string.IsNullOrWhiteSpace(req.Command)) return Results.BadRequest(new { error = "command is required" });
    var runner = fleet.Get(id);
    if (runner.State != RunnerState.Ready) return Results.BadRequest(new { error = "runner is not ready" });
    // A raw command can do anything, so on a privileged account every single call is confirmed.
    if (runner.Spec.AccountTier == LastWar.Operations.AccountTier.Privileged && req.Confirm != true)
        return Results.Json(new { error = "this is a privileged account - confirm the call to send it", confirmRequired = true }, statusCode: 403);
    return Results.Ok(await runner.CallAsync(req.Command, req.Params, req.TimeoutSeconds ?? 8));
});

// Server-sent events: the wire view, pushes and call results for one runner.
app.MapGet("/api/runners/{id}/events", async (string id, HttpContext ctx, CancellationToken ct) =>
{
    var runner = fleet.Get(id);
    ctx.Response.Headers.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";
    ctx.Response.Headers["X-Accel-Buffering"] = "no";
    await foreach (var e in runner.Subscribe(ct))
    {
        var json = JsonSerializer.Serialize(new { seq = e.Seq, at = e.At, kind = e.Kind, data = e.Data });
        await ctx.Response.WriteAsync($"data: {json}\n\n", ct);
        await ctx.Response.Body.FlushAsync(ct);
    }
});

// ---- chat: live rooms, stored history, and a feed of whatever arrives

app.MapGet("/api/chat/status", () => chatHub.Status());

// ---- Inbox (admin): an account's private chat, live from the game, and the battle reports shared in it.
var reportStore = new LastWar.Storage.BattleReportStore(db);
reportStore.EnsureSchema();
var inboxText = new LastWar.Storage.GameText(Path.Combine(root, "data", "assets"));
var inbox = new LastWar.Server.Inbox(fleet, chatHub, reportStore, inboxText);
// 2026-10-02 (Henry): no automatic Inbox collection - shares are collected only by the Collect button
// (POST /api/inbox/{runner}/collect), so no account is touched on a timer. inbox.LoopAsync is kept for if that changes.
// Derived fight rows follow the code: re-read every stored report at start (cheap - a few hundred KB each).
_ = Task.Run(() => { try { inbox.Rebuild(); } catch (Exception ex) { Console.Error.WriteLine($"report rebuild: {ex.Message}"); } });
async Task<IResult> InboxCall(Func<Task<object>> work)
{
    try { return Results.Ok(await work()); }
    catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException or ArgumentException or TimeoutException)
    { return Results.BadRequest(new { error = ex.Message }); }
}
app.MapGet("/api/inbox/{runner}/rooms", (string runner, CancellationToken ct) => InboxCall(async () => await inbox.RoomsAsync(runner, ct)));
app.MapGet("/api/inbox/{runner}/rooms/{room}/messages", (string runner, string room, long? before, CancellationToken ct) =>
    InboxCall(async () => await inbox.MessagesAsync(runner, room, before, ct)));
app.MapPost("/api/inbox/{runner}/rooms/{room}/send", (string runner, string room, InboxSend req, CancellationToken ct) => InboxCall(async () =>
{
    var t = (req.Text ?? "").Trim();
    if (t.Length is 0 or > 500) throw new ArgumentException("a message is 1-500 characters");
    return await inbox.SendAsync(runner, room, t, ct);
}));
app.MapPost("/api/inbox/{runner}/collect", (string runner, CancellationToken ct) => InboxCall(async () =>
{
    var (shares, fetched, failed) = await inbox.CollectAsync(runner, 40, ct);
    return new { shares, fetched, failed };
}));
app.MapGet("/api/inbox/status", () => new { lastCollectAt = inbox.LastCollectAt, lastError = inbox.LastError, every = 0 });   // 0 = manual only

// ---- Battle reports (admin): stored reports, one report, and what separates winners from losers.
app.MapGet("/api/reports", () => reportStore.List().Select(r => { r["mailTypeName"] = inboxText.MailTypeName(Convert.ToInt32(r["mailType"] ?? 0)); return r; }));
app.MapGet("/api/reports/analysis", () =>
{
    var (factors, upsets, fights) = reportStore.Analysis();
    var names = factors.Select(f => f.Factor).Concat(upsets.SelectMany(u => u["winnerAhead"] as IEnumerable<string> ?? [])).Distinct()
        .Select(k => (k, n: inboxText.FactorName(k))).Where(x => x.n is not null).ToDictionary(x => x.k, x => x.n);
    return new { fights, factors, upsets, splits = reportStore.Splits(), names };
});
app.MapGet("/api/reports/{uid}", (string uid, bool? json) =>
{
    var r = reportStore.Get(uid);
    if (r is null) return Results.NotFound();
    if (r["json"] is string js && js.Length > 0)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(js);
        var names = inboxText.ReportNames(doc.RootElement);
        names["factorNames"] = ((List<Dictionary<string, object?>>)r["fights"]!).SelectMany(f => f["factors"] is Dictionary<string, double> fd ? fd.Keys.ToList() : [])
            .Distinct().Select(k => (k, n: inboxText.FactorName(k))).Where(x => x.n is not null).ToDictionary(x => x.k, x => x.n!);
        r["names"] = names;
    }
    if (json != true) r.Remove("json");
    r["mailTypeName"] = inboxText.MailTypeName(Convert.ToInt32(r["mailType"] ?? 0));
    var ids = ((List<Dictionary<string, object?>>)r["fights"]!).SelectMany(f => (List<LastWar.Core.Model.FightHero>)f["heroes"]!).Select(h => (long)h.HeroId).Distinct();
    r["heroNames"] = ids.ToDictionary(i => i.ToString(), i => inboxText.HeroName(i));
    r["chipNames"] = ((List<Dictionary<string, object?>>)r["fights"]!).SelectMany(f => (List<LastWar.Core.Model.FightHero>)f["heroes"]!)
        .SelectMany(h => h.Chips ?? []).Distinct().ToDictionary(i => i.ToString(), i => inboxText.ChipName(i));
    return Results.Ok(r);
});
// Warzone Duel (SvS). ?servers=1959 | 1957-1988 | 8 servers typed (group A then B), and/or ?cfg=<event id>.
// Events (history) come from the game's table, with the capitals taken in each; the line-up shown is, best first:
// what an account in the event read (svs.status), the game's table, what was typed. Plus our strength read.
app.MapGet("/api/svs", (string? servers, long? cfg) =>
{
    var ids = new List<int>();
    foreach (var t in (servers ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        if (t.Split('-') is [var lo, var hi] && int.TryParse(lo, out var l) && int.TryParse(hi, out var h) && h >= l && h - l < 200)
            ids.AddRange(Enumerable.Range(l, h - l + 1));
        else if (int.TryParse(t, out var n) && n > 0) ids.Add(n);
    ids = ids.Distinct().ToList();
    var store = new LastWar.Storage.SvsStore(db);
    static System.Text.Json.Nodes.JsonNode? J(object? v) => v is string s && s.Length > 0 ? System.Text.Json.Nodes.JsonNode.Parse(s) : null;
    static string? ReadLineup(Dictionary<string, object?>? snap) => snap?["schedule"] is string s && s.Length > 0
        ? System.Text.Json.Nodes.JsonNode.Parse(s)?["cfg"]?["server_ids"]?.ToString() : null;
    var events = store.Events(ids, null);
    // The game can re-draw the groups when an event starts (5060 live is not 5060 in the table): an account's read wins,
    // and an event a typed server was READ in joins the list even when the table puts that server elsewhere.
    if (ids.Count > 0 && store.Latest(ids) is { } seen && events.All(e => Convert.ToInt64(e["id"]) != Convert.ToInt64(seen["cfgId"])))
        events.AddRange(store.Events([], Convert.ToInt64(seen["cfgId"])));
    if (cfg is > 0 && events.All(e => Convert.ToInt64(e["id"]) != cfg)) events.AddRange(store.Events([], cfg));
    var snaps = new Dictionary<long, Dictionary<string, object?>>();
    foreach (var e in events)
    {
        var id = Convert.ToInt64(e["id"]);
        if (Convert.ToInt64(e["reads"]) > 0 && store.LatestOf(id) is { } sn && ReadLineup(sn) is { Length: > 0 } read)
        {
            snaps[id] = sn;
            e["tableIds"] = e["serverIds"]; e["serverIds"] = read; e["read"] = true;
        }
        var members = ((string)e["serverIds"]!).Split(['|', ';'], StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToList();
        e["captures"] = store.Captures((string)e["openTime"]!, (string)e["endTime"]!, members);
    }
    // A server whose live read moved it out of its table event: drop the table event that no longer holds it.
    events = events.Where(e => ids.Count == 0 || cfg == Convert.ToInt64(e["id"]) || ids.Any(i => (";" + ((string)e["serverIds"]!).Replace('|', ';') + ";").Contains($";{i};")))
        .OrderByDescending(e => (string)e["openTime"]!).ThenBy(e => Convert.ToInt64(e["id"])).ToList();
    // Same window as an event the game showed re-drawn: the table line-ups of that pool are not to be trusted.
    var redrawn = store.RedrawnWindows();
    foreach (var e in events) if (!e.ContainsKey("read") && redrawn.Contains((string)e["openTime"]!)) e["redrawn"] = true;
    if (ids.Count == 1) events = events.Where(e => !e.ContainsKey("redrawn") || !events.Any(r => r.ContainsKey("read") && (string)r["openTime"]! == (string)e["openTime"]!)).ToList();
    var now = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
    var ev = cfg is > 0 ? events.FirstOrDefault(e => Convert.ToInt64(e["id"]) == cfg)
        : ids.Count == 8 ? null
        : events.OrderBy(e => e.ContainsKey("read") ? 0 : 1).FirstOrDefault(e => string.CompareOrdinal((string)e["openTime"]!, now) <= 0 && string.CompareOrdinal((string)e["endTime"]!, now) > 0
            && (ids.Count > 1 || ((string)e["serverIds"]!).Replace('|', ';').Split(';').Contains(ids[0].ToString()))) ?? events.FirstOrDefault();
    var snap = ev is not null ? snaps.GetValueOrDefault(Convert.ToInt64(ev["id"])) : ids.Count > 0 ? store.Latest(ids) : null;
    var snapLineup = ReadLineup(snap);
    if (snap is not null)
        foreach (var k in new[] { "schedule", "rounds", "detail", "alliances" }) snap[k] = J(snap[k]);
    var (lineup, source) = ev?["serverIds"] is string evIds ? (evIds, ev.ContainsKey("read") ? "game" : "table")
        : snapLineup is { Length: > 0 } r8 ? (r8, "game")
        : (string.Join(";", ids.Take(4)) + "|" + string.Join(";", ids.Skip(4).Take(4)), "typed");
    var all = lineup.Split(['|', ';'], StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToList();
    return Results.Ok(new { servers = all, lineup, source, @event = ev, snapshot = snap, strength = store.Strength(all), events });
});// SvS set up by hand: the 8 servers in round-1 order and the results as they come in (Henry, 2026-10-08).
app.MapGet("/api/svs/setups", () => Results.Ok(new LastWar.Storage.SvsStore(db).Setups()));
app.MapPost("/api/svs/setups", (SvsSetupRequest req) =>
{
    var ids = (req.Lineup ?? "").Split(['|', ';'], StringSplitOptions.RemoveEmptyEntries).Select(s => int.TryParse(s.Trim(), out var n) ? n : 0).ToList();
    if (ids.Count != 8 || ids.Any(n => n <= 0) || ids.Distinct().Count() != 8)
        return Results.BadRequest(new { error = "8 different server numbers are needed" });
    var lineup = string.Join(";", ids.Take(4)) + "|" + string.Join(";", ids.Skip(4));
    var results = req.Results is { } r ? System.Text.Json.JsonSerializer.Serialize(r) : "{}";
    var id = new LastWar.Storage.SvsStore(db).SaveSetup(req.Id, (req.Name ?? "").Trim(), (req.Start ?? "").Trim(), lineup, results);
    return Results.Ok(new { id });
});
app.MapDelete("/api/svs/setups/{id:long}", (long id) => { new LastWar.Storage.SvsStore(db).DeleteSetup(id); return Results.Ok(); });// SvS points by metric: what the model expects for a pair (?a=&b=&start=yyyy-mm-dd&at=ISO, at defaults to now), and the
// scores logged from the game's Weekly Results, each with the prediction made for the same moment (Henry, 2026-10-08).
app.MapGet("/api/svs/predict", (int a, int b, string start, string? at) =>
{
    var when = at is { Length: > 0 } ? DateTimeOffset.Parse(at) : DateTimeOffset.UtcNow;
    var days = (when - DateTimeOffset.Parse(start + "T00:00:00Z")).TotalDays;
    var store = new LastWar.Storage.SvsStore(db);
    var r = store.Predict(a, b, days);
    r["metrics"] = LastWar.Storage.SvsModel.MetricNames;
    // The forecast for every race day (end of day 1..5), today's kept for the record, and how it has moved.
    var (ca, cb) = store.Curve((LastWar.Storage.SvsModel.Side)((Dictionary<string, object?>)r["inputs"]!)["a"]!,
        (LastWar.Storage.SvsModel.Side)((Dictionary<string, object?>)r["inputs"]!)["b"]!);
    r["curveA"] = ca; r["curveB"] = cb;
    store.RecordForecast(start, a, b);
    static System.Text.Json.Nodes.JsonNode? J(object? v) => v is string s && s.Length > 0 ? System.Text.Json.Nodes.JsonNode.Parse(s) : null;
    var history = store.Forecasts(start, a, b);
    foreach (var h in history) { h["curve"] = J(h["curve"]); h["inputs"] = J(h["inputs"]); }
    r["history"] = history;
    var seen = store.ObservationsOf(start, a, b);
    foreach (var o in seen) { o["scoresA"] = J(o["scoresA"]); o["scoresB"] = J(o["scoresB"]); }
    r["seen"] = seen;
    return Results.Ok(r);
});
app.MapGet("/api/svs/observations", () =>
{
    var rows = new LastWar.Storage.SvsStore(db).Observations();
    foreach (var r in rows)
        foreach (var k in new[] { "scoresA", "scoresB", "predicted" })
            r[k] = r[k] is string s && s.Length > 0 ? System.Text.Json.Nodes.JsonNode.Parse(s) : null;
    return Results.Ok(new { metrics = LastWar.Storage.SvsModel.MetricNames, rows });
});
app.MapPost("/api/svs/observations", (SvsObservationRequest req) =>
{
    if (req.A <= 0 || req.B <= 0 || req.A == req.B) return Results.BadRequest(new { error = "two different servers are needed" });
    // Seven metrics (Desert Storm last - it only appears on Friday); six still accepted, Desert Storm taken as 0.
    if (req.ScoresA is not { Length: 6 or 7 } || req.ScoresB is not { Length: 6 or 7 }) return Results.BadRequest(new { error = "a score for each metric, each side" });
    double[] Pad(double[] x) => x.Length == 7 ? x : [.. x, 0];
    if (!DateOnly.TryParse(req.WeekStart, out _)) return Results.BadRequest(new { error = "week start yyyy-mm-dd" });
    var at = req.At is { Length: > 0 } ? DateTimeOffset.Parse(req.At).ToUniversalTime().ToString("O") : DateTimeOffset.UtcNow.ToString("O");
    var id = new LastWar.Storage.SvsStore(db).AddObservation(at, req.WeekStart!, req.A, req.B, Pad(req.ScoresA), Pad(req.ScoresB), req.Note ?? "");
    return Results.Ok(new { id });
});
app.MapDelete("/api/svs/observations/{id:long}", (long id) => { new LastWar.Storage.SvsStore(db).DeleteObservation(id); return Results.Ok(); });app.MapPost("/api/reports/rebuild", () => new { rebuilt = inbox.Rebuild() });
// Volume, busiest rooms/hours/people/alliances and emoji use across every stored room; days=0 = all.
app.MapGet("/api/chat/stats", (int? days) => Results.Ok(insights.ChatStats(days ?? 30)));

app.MapGet("/api/chat/rooms", () =>
{
    var followed = chatHub.Followed.ToHashSet();
    var held = chatHub.Store.Rooms();
    var states = chatHub.Store.States();
    string? State(string room) => states.TryGetValue(room, out var s) ? s.State : null;
    var rows = held.Select(r => new { room = r.Room, messages = r.Messages, newest = r.Newest, lastAt = r.LastAt, firstAt = r.FirstAt, followed = followed.Contains(r.Room), state = State(r.Room) })
        .Concat(followed.Where(f => held.All(h => h.Room != f))
            .Select(f => new { room = f, messages = 0L, newest = 0L, lastAt = "", firstAt = "", followed = true, state = State(f) }));
    // Deleted rooms have no messages left, so they only show while still followed (never, after a delete).
    return rows.OrderByDescending(r => r.followed).ThenBy(r => r.state is not null).ThenByDescending(r => r.lastAt);
});

app.MapPost("/api/chat/follow", (FollowRequest req) =>
{
    var room = (req.Room ?? "").Trim();
    if (!System.Text.RegularExpressions.Regex.IsMatch(room, @"^[A-Za-z0-9_\-]{3,80}$"))
        return Results.BadRequest(new { error = "room ids look like country_1967" });
    if (req.On ?? true) chatHub.Store.Archive(room, false);   // following again brings an archived/deleted room back into use
    chatHub.Follow(room, req.On ?? true);
    return Results.Ok(chatHub.Followed);
});

// Take a room out of use. archive (default): unfollow, keep every message, leave it out of chat
// stats; undo with DELETE. delete: remove its messages and translations for good (no undo) - the
// caller must repeat the room id as confirm. Both are admin-only (Access.ViewerMay lists neither).
app.MapPost("/api/chat/rooms/{room}/archive", (string room) => { chatHub.Archive(room, true); return Results.Ok(new { room, state = "archived" }); });
app.MapDelete("/api/chat/rooms/{room}/archive", (string room) => { chatHub.Archive(room, false); return Results.Ok(new { room, state = (string?)null }); });
app.MapPost("/api/chat/rooms/{room}/delete", (string room, string? confirm) =>
{
    if (confirm != room) return Results.BadRequest(new { error = "type the room id to confirm the delete" });
    var removed = chatHub.Delete(room);
    return Results.Ok(new { room, state = "deleted", removed });
});

app.MapGet("/api/chat/rooms/{room}/messages", (string room, long? before, int? limit, string? lang) =>
{
    var page = chatHub.Store.Page(room, before, limit ?? 150);
    if (page.Count == 0) return [];
    var held = chatHub.Store.Translations(room, lang ?? "en", page.Min(m => m.SeqId), page.Max(m => m.SeqId));
    var users = chatHub.Users(page.Select(m => m.SenderUid));
    return page.Select(m => ChatHub.Shape(m, held.TryGetValue(m.SeqId, out var t) ? t : null, users.GetValueOrDefault(m.SenderUid)));
});

// Search the whole stored history (all rooms when room is empty), newest first, 100 a page.
app.MapGet("/api/chat/search", (string q, string? room, string? before, int? limit, string? lang) =>
{
    var (found, more) = chatHub.Store.Search(q ?? "", room ?? "", before, limit ?? 100);
    var users = chatHub.Users(found.Select(m => m.SenderUid));
    var shaped = found.GroupBy(m => m.RoomId).SelectMany(g =>
    {
        var held = chatHub.Store.Translations(g.Key, lang ?? "en", g.Min(m => m.SeqId), g.Max(m => m.SeqId));
        return g.Select(m => (m, held.TryGetValue(m.SeqId, out var t) ? t : ((string, string)?)null));
    }).OrderByDescending(x => x.m.SentAt).ThenByDescending(x => x.m.SeqId)
      .Select(x => ChatHub.Shape(x.m, x.Item2, users.GetValueOrDefault(x.m.SenderUid)));
    return Results.Ok(new
    {
        messages = shaped, more,
        oldest = found.Count > 0 ? found[^1].SentAt.ToString("O") : null,
        // Where stored history for the room stops, so the page can offer to fetch further back.
        heldFrom = room is { Length: > 0 } ? chatHub.Store.OldestAt(room) : null,
    });
});
// Backfill rooms back to a date, queued one room at a time (ChatBackfillJobs).
var backfills = new LastWar.Server.ChatBackfillJobs(db, dataDir, chatHub.Store);
app.MapGet("/api/chat/backfill", () => backfills.Snapshot());
app.MapPost("/api/chat/backfill", (BackfillRequest req) =>
{
    if (req.Rooms is not { Length: > 0 } || !DateTime.TryParse(req.Until, out var until))
        return Results.BadRequest(new { error = "rooms and until (a date) are required" });
    backfills.Enqueue(req.Rooms, until.ToString("yyyy-MM-dd"));
    return Results.Ok(backfills.Snapshot());
});
app.MapPost("/api/chat/backfill/cancel", () => { backfills.CancelAll(); return Results.Ok(backfills.Snapshot()); });

// Pull older history for a room from the game on a throwaway guest (chat.backfill.guest).
app.MapPost("/api/chat/rooms/{room}/backfill", async (string room, int? pages, CancellationToken ct) =>
{
    var r = await LastWar.Operations.Operations.RunAsync("chat.backfill.guest", new LastWar.Operations.OperationContext
    {
        Args = new Dictionary<string, string> { ["room"] = room, ["pages"] = (pages ?? 20).ToString() },
        Database = db, DataDir = dataDir, Cancellation = ct,
    });
    return Results.Ok(new { summary = r.Summary, heldFrom = chatHub.Store.OldestAt(room) });
});

app.MapPost("/api/chat/rooms/{room}/messages/{seq:long}/translate", async (string room, long seq, string? lang, bool? refresh, CancellationToken ct) =>
{
    try
    {
        var t = await chatHub.TranslateAsync(room, seq, lang ?? "en", refresh ?? false, ct);
        return Results.Ok(new { seq, lang = t.TargetLang, text = t.Text, from = t.SourceLang });
    }
    catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

// Fill a hole: everything after `after` (default: newest stored) up to now.
app.MapPost("/api/chat/rooms/{room}/catchup", (string room, long? after) =>
    chatHub.CatchUp(room, after) ? Results.Ok(chatHub.Status()) : Results.BadRequest(new { error = "not live, or already catching up" }));

app.MapPost("/api/runners/{id}/chat/start", async (string id) =>
{
    var runner = fleet.Get(id);
    if (runner.State != RunnerState.Ready) return Results.BadRequest(new { error = "runner is not ready" });
    if (runner.Spec.AccountTier == LastWar.Operations.AccountTier.Privileged)
        return Results.Json(new { error = "chat is never run on a privileged account - go live on a guest" }, statusCode: 403);
    try { await chatHub.AttachAsync(runner); return Results.Ok(chatHub.Status()); }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
});

// Admin: one raw chat-socket command on a runner's chat link (research: DM room lists, report shares).
app.MapPost("/api/runners/{id}/chat/raw", async (string id, ChatRawRequest req, CancellationToken ct) =>
{
    try { return Results.Text(await chatHub.RawAsync(id, req.Command, (object?)req.Params ?? new Dictionary<string, object?>(), ct) ?? "null", "application/json"); }
    catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
});
app.MapPost("/api/runners/{id}/chat/stop", async (string id) => { await chatHub.Detach(id); return Results.Ok(chatHub.Status()); });

// Every new message, all rooms; the page filters to the room on screen.
app.MapGet("/api/chat/live", async (HttpContext ctx, CancellationToken ct) =>
{
    ctx.Response.Headers.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";
    ctx.Response.Headers["X-Accel-Buffering"] = "no";
    await ctx.Response.WriteAsync(": open\n\n", ct);
    await ctx.Response.Body.FlushAsync(ct);
    await foreach (var frame in chatHub.Subscribe(ct))
    {
        await ctx.Response.WriteAsync(frame, ct);
        await ctx.Response.Body.FlushAsync(ct);
    }
});

// ---- operations: everything the tooling can do, described so the UI needs no per-feature code

app.MapGet("/api/operations", () => LastWar.Operations.Operations.All);

app.MapPost("/api/runners/{id}/operation", async (string id, OperationRequest req, CancellationToken ct) =>
{
    try { return Results.Ok(await RunOnRunner(id, req.Operation, req.Args ?? new Dictionary<string, string>(), null, ct)); }
    catch (UnauthorizedAccessException ex) { return Results.Json(new { error = ex.Message }, statusCode: 403); }
    catch (RunnerBusyException ex) { return Results.Conflict(new { error = ex.Message }); }
    catch (RunnerStartException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

// Account policy (Operations.AccountPolicy): a privileged account only ever runs what is marked for
// it, a guest only what a guest can do - checked before anything connects. Pick a runner, not start
// one: a stopped runner is connected for this ONE run and disconnected after. No chat attach, nothing
// left running - a real account is logged in only for as long as the operation takes. The
// Operations page and scheduled jobs with a "runner" both come through here.
async Task<System.Text.Json.Nodes.JsonNode> RunOnRunner(string id, string op, IReadOnlyDictionary<string, string> args, IProgress<string>? progress, CancellationToken ct)
{
    var runner = fleet.Get(id);
    var info = LastWar.Operations.Operations.Find(op);
    if (info?.Refuses(runner.Spec.AccountTier) is { } why) throw new UnauthorizedAccessException(why);
    var startedHere = false;
    if (info is { NeedsSession: true } && runner.State != RunnerState.Ready)
    {
        if (fleet.HolderOf(runner) is { } other) throw new RunnerBusyException($"'{other.Spec.Id}' already holds this account");
        try { await runner.StartAsync(ct); await runner.SettleAsync(ct); startedHere = true; }
        catch (Exception ex)
        {
            if (runner.Spent && ex is LoginFailedException) Forget(runner);
            throw new RunnerStartException($"could not start {runner.Spec.Id}: {ex.Message}");
        }
    }
    if (startedHere && runner.Spec.AccountTier == LastWar.Operations.AccountTier.Service) lingering[runner.Spec.Id] = true;
    lastUse[runner.Spec.Id] = DateTimeOffset.UtcNow;
    try { return await runner.RunOperationAsync(op, args, db, dataDir, ct, progress); }
    finally
    {
        lastUse[runner.Spec.Id] = DateTimeOffset.UtcNow;
        if (lingering.ContainsKey(runner.Spec.Id))
        {
            // A service account we logged in stays connected for a while (Henry, 2026-10-07): logging in and out
            // for every scan or send is a pattern of its own. The next run reuses the session; after ~8 minutes
            // (jittered, inside the game's 10-minute idle window) with nothing run on it, it is logged out.
            var rid = runner.Spec.Id;
            _ = Task.Run(async () =>
            {
                var mine = lastUse[rid];
                await Task.Delay(LastWar.Client.Jitter.Of(TimeSpan.FromMinutes(8), 0.15));
                if (lastUse.TryGetValue(rid, out var last) && last == mine && lingering.TryRemove(rid, out _) && runner.State == RunnerState.Ready)
                    await runner.StopAsync();
            });
        }
        else if (startedHere)
        {
            await runner.StopAsync();
            if (runner.Spent) Forget(runner);
        }
    }
}

// ---- sessions: create a guest, or sign an account in by email

app.MapPost("/api/sessions/guest", async (NewSessionRequest req) =>
{
    try
    {
        var spec = await sessions.CreateGuestAsync(req.Name, req.Zone);
        fleet.Reload();
        return Results.Ok(new { id = spec.Id, note = spec.Note });
    }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
});

// Step one: stand up a throwaway guest connection for this new session and ask the
// account service to email a code. The connection stays open - both halves of the
// exchange have to happen on the same socket.
app.MapPost("/api/sessions/email/start", async (EmailStartRequest req) =>
{
    try
    {
        var id = SessionStore.Slug(req.Name);
        // The guest that carries the sign-in gets a folder of its own: made in the account's folder it
        // left a guest uid there, and the next attempt was refused (E011) - 2026-09-30.
        var home = sessions.TempHome(id);
        var config = await GuestBootstrap.CreateAsync(home);
        var identity = GameIdentity.LoadOrCreate(home);
        var client = await LastWarClient.ConnectAsync(config, identity);

        var login = new EmailLogin(client, identity, req.Ios == true);
        var expires = await login.RequestCodeAsync(req.Email);

        var p = new PendingEmailLogin
        {
            Id = Guid.NewGuid().ToString("n"),
            Email = req.Email,
            SessionId = id,
            TempHome = home,
            Identity = identity,
            Client = client,
            Login = login,
        };
        pending[p.Id] = p;
        return Results.Ok(new { pendingId = p.Id, sessionId = id, resendAfter = expires });
    }
    catch (Exception ex) { return Results.BadRequest(new { error = $"Could not send the code: {ex.Message}" }); }
});

// Step two: the code from the inbox. Returns the roles on the account to choose from.
app.MapPost("/api/sessions/email/verify", async (EmailVerifyRequest req) =>
{
    if (!pending.TryGetValue(req.PendingId, out var p))
        return Results.BadRequest(new { error = "that sign-in has expired - start again" });
    try
    {
        p.Roles = await p.Login.SubmitCodeAsync(p.Email, req.Code);
        return Results.Ok(new
        {
            roles = p.Roles.Select((r, i) => new
            {
                index = i, r.Zone, serverId = r.ServerId, r.GameUid,
                name = r.Name, alliance = r.Alliance, power = r.Power, level = r.Level,
            }),
        });
    }
    catch (Exception ex) { return Results.BadRequest(new { error = $"The code was not accepted: {ex.Message}" }); }
});

// Step three: redeem the one-shot login key for the chosen role and store the session.
app.MapPost("/api/sessions/email/save", async (EmailSaveRequest req) =>
{
    if (!pending.TryGetValue(req.PendingId, out var p))
        return Results.BadRequest(new { error = "that sign-in has expired - start again" });
    if (req.RoleIndex < 0 || req.RoleIndex >= p.Roles.Count)
        return Results.BadRequest(new { error = "no such role" });
    try
    {
        var role = p.Roles[req.RoleIndex];
        var config = await EmailLogin.RedeemAsync(role, p.Identity, p.Login.Ios);
        var id = SessionStore.Slug(req.Name ?? p.SessionId);
        var spec = sessions.SaveEmail(id, config, $"{role.Name} on {config.Zone}".Trim(), p.TempHome);
        fleet.Reload();
        return Results.Ok(new { id = spec.Id, zone = config.Zone, gameUid = config.GameUid });
    }
    catch (Exception ex) { return Results.BadRequest(new { error = $"Logging in as that role failed: {ex.Message}" }); }
    finally
    {
        // The guest socket has done its job either way, and the login key is spent.
        if (pending.TryRemove(req.PendingId, out var done)) await done.DisposeAsync();
    }
});

// Throwaway guest sessions nothing is using. apply=false (the default) only lists them, so the UI
// can show exactly what will go before anything is deleted. Real accounts are never touched.
HashSet<string> InUse()
{
    var inUse = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var r in fleet.All)
        if (r.State is RunnerState.Ready or RunnerState.Connecting || chatHub.Listening(r.Spec.Id)) inUse.Add(r.Spec.Id);
    foreach (var p in pending.Values) inUse.Add(p.SessionId);
    return inUse;
}

app.MapPost("/api/sessions/cleanup", (bool? apply) =>
{
    var inUse = InUse();
    try
    {
        var items = sessions.Cleanup(inUse, apply ?? false, retire: f => LastWar.Operations.GuestKeeper.Retire(dataDir, db, f));
        if (apply == true) fleet.Reload();
        return Results.Ok(new { applied = apply == true, kept = inUse.Order(), items });
    }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
});

// A session as one JSON object, to copy to the other copy of the site (local <-> hosted). Admin only.
app.MapGet("/api/sessions/{id}/export", (string id) =>
{
    var runner = fleet.All.FirstOrDefault(r => r.Spec.Id == id);
    if (runner is null) return Results.NotFound(new { error = $"no session {id}" });
    try { return Results.Ok(sessions.Export(id, runner.Spec)); }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
});
app.MapPost("/api/sessions/import", (System.Text.Json.Nodes.JsonObject body) =>
{
    var id = SessionStore.Slug((string?)body["id"] ?? "");
    if (fleet.All.FirstOrDefault(r => r.Spec.Id == id) is { State: RunnerState.Ready or RunnerState.Connecting })
        return Results.BadRequest(new { error = $"stop {id} first - it is running" });
    try { var spec = sessions.Import(body); fleet.Reload(); return Results.Ok(new { id = spec.Id, note = spec.Note }); }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapDelete("/api/sessions/{id}", async (string id, bool? files) =>
{
    var runner = fleet.All.FirstOrDefault(r => r.Spec.Id == id);
    if (runner is { State: RunnerState.Ready or RunnerState.Connecting })
        return Results.BadRequest(new { error = "stop the runner first" });
    sessions.Delete(id, files ?? false);
    fleet.Remove(id);
    return Results.Ok(new { removed = id });
});

// ---- explorer: server, alliance and player pages, all joined on uid and alliance id

app.MapGet("/api/x/overview", () => insights.Overview());
app.MapGet("/api/x/highlights", (int? days) => insights.Highlights(days ?? 7));
app.MapGet("/api/x/changes", (string? kind, int? days, int? limit) => insights.Changes(kind ?? "troops", days ?? 7, limit ?? 200));
// Favourites of whoever is signed in come first; an empty q returns just the favourites (the quick picks).
app.MapGet("/api/x/search", (string? q, HttpContext ctx) => insights.Search(q ?? "", favouritesOf: Access.Who(ctx, access, devMode)?.Id));
app.MapGet("/api/x/player/{uid}", (string uid) => insights.Player(uid));
app.MapGet("/api/x/player/{uid}/activity", (string uid) => insights.PlayerActivityReport(uid));
app.MapGet("/api/x/alliance/{id}/activity", (string id, int? days) => insights.AllianceActivity(id, Math.Clamp(days ?? 30, 1, 365)));
app.MapGet("/api/x/alliance/{id}", (string id) => insights.Alliance(id));
app.MapGet("/api/x/server/{id:int}", (int id) => insights.Server(id));
// Name changes: newest first, or everything that ever matched a name (old names still find people).
app.MapGet("/api/x/names", (string? kind, string? q, int? limit) =>
{
    var names = new LastWar.Storage.NameHistory(db);
    return q is { Length: >= 2 } ? names.Find(q, limit ?? 50) : names.Recent(limit ?? 50, kind);
});

// Live chat brings new names and sightings all day; rebuild hourly as well as after every scan.
_ = Task.Run(async () =>
{
    // Once: judge the last-seen moments stored before idle_seen existed (~1 h on hosted; carries on after a restart).
    try { new LastWar.Storage.PlayerActivity(db).JudgeBacklog(log: Console.WriteLine); }
    catch (Exception ex) { Console.Error.WriteLine($"idle_seen backlog: {ex.Message}"); }
    while (true)
    {
        try { LastWar.Storage.Derived.RebuildAll(db); }
        catch (Exception ex) { Console.Error.WriteLine($"name detection: {ex.Message}"); }
        // Backstop for guests that were never stopped cleanly (faulted, server killed): anything
        // unused and over an hour old. The hour spares a guest created but not yet started.
        try { sessions.Cleanup(InUse(), apply: true, TimeSpan.FromHours(1), f => LastWar.Operations.GuestKeeper.Retire(dataDir, db, f)); fleet.Reload(); }
        catch (Exception ex) { Console.Error.WriteLine($"session cleanup: {ex.Message}"); }
        await Task.Delay(TimeSpan.FromHours(1));
    }
});
app.MapGet("/api/x/map/{server:int}", (int server, long? scan) => insights.Map(server, scan));
// Favourite players belong to the signed-in password (the middleware above has already checked it).
app.MapGet("/api/me/favourites", (HttpContext ctx) => access.Favourites(Access.Who(ctx, access, devMode)!.Id));
// The Account page (admin only - Access.ViewerMay does not list it): my player, and the account messages go out from.
app.MapGet("/api/me/prefs", (HttpContext ctx) => access.Prefs(Access.Who(ctx, access, devMode)!.Id));
app.MapPut("/api/me/prefs", (Dictionary<string, string?> prefs, HttpContext ctx) =>
{
    var id = Access.Who(ctx, access, devMode)!.Id;
    access.SetPrefs(id, prefs);
    return access.Prefs(id);
});
// ?kind=player (default) | alliance | server
app.MapPut("/api/me/favourites/{uid}", (string uid, string? kind, HttpContext ctx) => { access.SetFavourite(Access.Who(ctx, access, devMode)!.Id, uid, true, kind ?? "player"); return Results.Ok(); });
app.MapDelete("/api/me/favourites/{uid}", (string uid, HttpContext ctx) => { access.SetFavourite(Access.Who(ctx, access, devMode)!.Id, uid, false); return Results.Ok(); });
// Cross-server rankings: players / alliances / servers on any measure, at a snapshot vs an earlier one.
app.MapGet("/api/x/rankings", (string? entity, string? metric, string? at, string? vs, string? servers, int? server, int? limit, string? chart, int? top, string? others) =>
{
    var range = (servers ?? "1957-1988").Split('-');
    int.TryParse(range[0], out var lo);
    var hi = range.Length > 1 && int.TryParse(range[1], out var h) ? h : lo;
    return insights.Rankings(entity ?? "alliances", metric ?? "", at, vs, lo, hi, server, limit ?? 100,
        chart is { Length: > 0 } ? chart.Split(',', StringSplitOptions.RemoveEmptyEntries) : null, top ?? 0, others != "hide");
});

// Up to four of a kind side by side, every metric as a time series per entity.
app.MapGet("/api/x/compare/{kind}", (string kind, string? ids) =>
{
    var list = (ids ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Distinct().Take(4).ToList();
    if (list.Count == 0) return Results.BadRequest(new { error = "pass ids=a,b" });
    return kind switch
    {
        "servers" when list.All(x => int.TryParse(x, out _)) => Results.Ok(insights.CompareServers(list.Select(int.Parse).ToList())),
        "alliances" => Results.Ok(insights.CompareAlliances(list)),
        "players" => Results.Ok(insights.ComparePlayers(list)),
        _ => Results.BadRequest(new { error = "kind is servers, alliances or players (servers take numbers)" }),
    };
});
app.MapGet("/api/x/server/{id:int}/players", (int id) => insights.ServerPlayers(id));
// Accounts made on a server since we started watching it (Insights.NewAccounts), and the per-day report.
app.MapGet("/api/x/server/{id:int}/new", (int id, int? days) => insights.NewAccounts(id, days ?? 30));
// Every server the game knows (ServerRegistry): the list with blocks, one server's story, a block's wars and groupings.
app.MapGet("/api/x/servers", () => insights.Servers());
// Events. Meteorite: ?servers=1967,1970 looks at any servers without saving; none = the saved list.
app.MapGet("/api/x/events/meteorite", (string? servers) =>
    insights.Meteorite(string.IsNullOrWhiteSpace(servers) ? null : LastWar.Operations.Operations.ParseServers(servers).ToList()));
app.MapPut("/api/events/meteorite", (TrackRequest req) =>
{
    var ids = LastWar.Operations.Operations.ParseServers(req.Servers ?? "").ToList();
    if (ids.Count > 16) return Results.BadRequest(new { error = "At most 16 servers." });
    new LastWar.Storage.ServerRegistry(db).SetEventServers("meteorite", ids);
    new LastWar.Storage.EventCalendar(db).SetUpcomingServers("meteorite", ids);   // the coming rounds are about these servers
    return Results.Ok(new { saved = ids });
});
// Event calendar: important events kept for good, and who was on during one (Insights.Calendar).
app.MapGet("/api/x/events/calendar", (DateTime? from, DateTime? to, string? kind) => insights.Calendar(from, to, kind));
app.MapGet("/api/x/events/calendar/{id}", (string id) => insights.CalendarEvent(id));
app.MapPost("/api/events/calendar", (CalendarRequest req) =>
{
    if (!LastWar.Storage.EventCalendar.Kinds.ContainsKey(req.Kind ?? "")) return Results.BadRequest(new { error = "Unknown kind." });
    if (req.Start is not { } start || req.End is not { } end || end <= start) return Results.BadRequest(new { error = "Give a start and an end after it (UTC)." });
    var servers = LastWar.Operations.Operations.ParseServers(req.Servers ?? "").ToList();
    var s = start.ToUniversalTime();
    var id = $"{req.Kind}:{s:yyyy-MM-dd-HHmm}:{Guid.NewGuid().ToString("N")[..6]}";
    new LastWar.Storage.EventCalendar(db).Upsert(new(id, req.Kind!, string.IsNullOrWhiteSpace(req.Title) ? LastWar.Storage.EventCalendar.Kinds[req.Kind!] : req.Title!.Trim(),
        s, end.ToUniversalTime(), servers, 0, "manual", req.Detail ?? ""));
    return Results.Ok(new { id });
});
app.MapDelete("/api/events/calendar/{id}", (string id) => new LastWar.Storage.EventCalendar(db).Delete(id) ? Results.Ok() : Results.NotFound());
app.MapGet("/api/x/server/{id:int}/story", (int id) => insights.ServerStory(id));
app.MapGet("/api/x/servers/wars", (int lo, int hi) => new { wars = insights.ZoneWars(lo, hi), groups = insights.EventGroups(lo, hi) });
// Tracking (admin only: Access.ViewerMay lists GETs under /api/x only). servers = "1957-2020" / "1967,1970" / list.
app.MapPut("/api/servers/track", (TrackRequest req) =>
{
    var ids = LastWar.Operations.Operations.ParseServers(req.Servers ?? "");
    var n = new LastWar.Storage.ServerRegistry(db).SetTracking(ids, string.IsNullOrWhiteSpace(req.Level) ? null : req.Level, req.Visible);
    return Results.Ok(new { changed = n });
});
// Active hours, online by hour and the per-country split: one server, and every server side by side.
app.MapGet("/api/x/server/{id:int}/online", (int id, int? days) => insights.ServerOnline(id, days ?? 30));
app.MapGet("/api/x/online", (int? days) => insights.OnlineComparison(days ?? 7));
app.MapGet("/api/x/newaccounts", (int? days) => insights.NewAccountReport(days ?? 30));
// HQ levels on the tracked servers: counts per server and who reached HQ <min>+ and when (Insights.HqLevels).
app.MapGet("/api/x/hq", (int? min) => insights.HqLevels(min ?? 31));
// Every player on every server, filtered / sorted / paged (Explore > Players; Insights.PlayerDirectory).
app.MapGet("/api/x/players", (string? q, string? servers, string? alliance, string? country, string? flag, int? hqMin, int? hqMax, long? heroMin, long? powerMin, string? seen, string? moved, string? sort, string? dir, int? page, int? size) =>
    insights.PlayerDirectory(new(q, servers, alliance, country, flag, hqMin, hqMax, heroMin, powerMin, seen, moved, sort ?? "hero", dir != "asc", page ?? 0, size ?? 100)));
// Season groups (servers.season): the season config each server in lo..hi is on.
app.MapGet("/api/x/season-groups", (int lo, int hi) => insights.SeasonGroups(lo, hi));
// One season group's dashboard (servers, seeds, strength, alliance ranking guess, top players).
app.MapGet("/api/x/season-group/{cfg:long}", (long cfg) => insights.SeasonGroup(cfg));
// Admin: a season group seen in game before the game reports it - PUT {servers:[..]} replaces the list, [] removes it.
app.MapPut("/api/season-groups/hand/{cfg:long}", (long cfg, HandSeasonGroup req) =>
{
    var servers = req.Servers ?? [];
    if (servers.Any(s => s <= 0 || s > 99999)) return Results.BadRequest(new { error = "bad server id" });
    new LastWar.Storage.ServerRegistry(db).SetHandSeasonGroup(cfg, servers);
    return Results.Ok(new { cfg, servers = servers.Distinct().Order() });
});
// Every country / flag / tracked server we know, with player counts (the country drop-downs).
app.MapGet("/api/x/countries", () => insights.PlayerFacets());
// Every alliance's hive score on every newest map, and who holds every season city / dig site.
app.MapGet("/api/x/hives", () => insights.Hives());
app.MapGet("/api/x/holders", () => insights.Holders());
// intel.sweep views: rosters with last seen / donations, territory, presidents, and a fight estimate.
app.MapGet("/api/x/alliance/{id}/intel", (string id) => insights.AllianceIntel(id));
app.MapGet("/api/x/server/{id:int}/intel", (int id) => insights.ServerIntel(id));
app.MapGet("/api/x/matchup", (string a, string b, double? steep, int? slots) => insights.Matchup(a, b, steep ?? 2, slots ?? 100));
app.MapGet("/api/x/hero-model", () => insights.HeroFit());
// ---- command reference

app.MapGet("/api/commands", (string? q, int? limit) => catalog.Search(q, limit ?? 200));
app.MapGet("/api/commands/count", () => new { total = catalog.Entries.Count });

// ---- stored data

app.MapGet("/api/db/tables", () => explorer.Tables());
app.MapGet("/api/db/count/{table}", (string table) => explorer.Count(table));
app.MapGet("/api/db/presets", () => Presets.All);
app.MapPost("/api/db/query", (QueryRequest req) =>
{
    try { return Results.Ok(explorer.Query(req.Sql, req.Limit ?? 200)); }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
});

// ---- the game's own assets (assets.extract): strings, and the emoji font for chat
var assetsDir = Path.Combine(root, "data", "assets");
var gameText = new LastWar.Storage.GameText(assetsDir);
app.MapGet("/game-assets/lwemoji.ttf", () =>
    File.Exists(Path.Combine(assetsDir, "lwemoji.ttf"))
        ? Results.File(Path.Combine(assetsDir, "lwemoji.ttf"), "font/ttf") : Results.NotFound());

// ---- mail: list (titles, bodies, attachments resolved), claim one, mark read. The account's own
// mail, so every tier may use it on itself - but only on a running runner, never started for it.
app.MapGet("/api/runners/{id}/mail", async (string id, int? pages, CancellationToken ct) =>
{
    var runner = fleet.Get(id);
    if (runner.State != RunnerState.Ready) return Results.BadRequest(new { error = "start the runner first" });
    var (mail, truncated) = await runner.WithSessionAsync(c => new LastWar.Client.Game.MailActions(c).FetchAllAsync(pages ?? 10, ct), ct);
    return Results.Ok(new
    {
        truncated, textAvailable = gameText.Available,
        mail = mail.Select(m => LastWar.Operations.MailFormat.View(m, gameText)).OrderByDescending(v => v.Sent),
    });
});
app.MapPost("/api/runners/{id}/mail/{uid}/claim", async (string id, string uid, CancellationToken ct) =>
{
    var runner = fleet.Get(id);
    if (runner.State != RunnerState.Ready) return Results.BadRequest(new { error = "start the runner first" });
    var outcome = await runner.WithSessionAsync(c => new LastWar.Client.Game.MailActions(c).ClaimOneAsync(uid, ct), ct);
    return Results.Ok(new { uid, outcome });
});
app.MapPost("/api/runners/{id}/mail/read", async (string id, ReadRequest req, CancellationToken ct) =>
{
    var runner = fleet.Get(id);
    if (runner.State != RunnerState.Ready) return Results.BadRequest(new { error = "start the runner first" });
    var outcome = await runner.WithSessionAsync(c => new LastWar.Client.Game.MailActions(c).MarkReadAsync(req.Uids, ct), ct);
    return Results.Ok(new { outcome });
});
// Sticker id -> its in-game name, for chat (<lwSticker:5:3:> is sticker 5 with a parameter, e.g. a dice roll).
app.MapGet("/api/game/stickers", () => Results.Ok(gameText.StickerNames()));
// Drone tab: readings over time, peers, and the game's own names for the drone's stat ids.
app.MapGet("/api/x/player/{uid}/drone", (string uid) =>
{
    var d = insights.PlayerDrone(uid);
    if (d.GetValueOrDefault("latest") is Dictionary<string, object?> l && l["props"] is string { Length: > 2 } json)
        d["props"] = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, double>>(json)!
            .Select(kv => new { id = kv.Key, name = gameText.T($"effectnumber_name_{kv.Key}"), value = kv.Value }).ToList();
    return d;
});

app.MapFallbackToFile("index.html", noCacheIndex);
app.Run();

record CallRequest(string Command, JsonNode? Params, int? TimeoutSeconds, bool? Confirm = null);
record TierRequest(string Tier);
record OperationRequest(string Operation, Dictionary<string, string>? Args);
record LoginRequest(string? Password);
record ScheduleEdit(int? EveryMinutes, string? Runner);
sealed class RunnerBusyException(string m) : Exception(m);
sealed class RunnerStartException(string m) : Exception(m);
record AccessRequest(string? Name, string? Role, bool? Disabled);
record BackfillRequest(string[]? Rooms, string? Until);
record ReadRequest(string[] Uids);
record NewSessionRequest(string Name, string? Zone);
record EmailStartRequest(string Name, string Email, bool? Ios);
record EmailVerifyRequest(string PendingId, string Code);
record EmailSaveRequest(string PendingId, int RoleIndex, string? Name);
record QueryRequest(string Sql, int? Limit);

record FollowRequest(string? Room, bool? On);
record TrackRequest(string? Servers, string? Level, bool? Visible);
record ChatRawRequest(string Command, System.Text.Json.JsonElement? Params);
record InboxSend(string? Text);
record CalendarRequest(string? Kind, string? Title, DateTime? Start, DateTime? End, string? Servers, string? Detail);
record HandSeasonGroup(int[]? Servers);
record SvsSetupRequest(long? Id, string? Name, string? Start, string? Lineup, Dictionary<string, int>? Results);
record SvsObservationRequest(string? At, string? WeekStart, int A, int B, double[]? ScoresA, double[]? ScoresB, string? Note);
