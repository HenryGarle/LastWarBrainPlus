using LastWar.Client.Game;
using LastWar.Client.Scanning;
using LastWar.Core.Model;
using LastWar.Lua;
using LastWar.Protocol.Sfs;
using LastWar.Storage;

namespace LastWar.Operations;

/// <summary>
/// Every capability the tooling has, in one list, described rather than hard-coded into a UI.
///
/// The point of the shape is that neither front end knows what any of these are: the dashboard
/// renders a form from Params and a table from the result, and the CLI parses flags from the same
/// Params and prints the same table. Adding an operation means adding one entry here and nothing
/// else - no endpoint, no page, no flag parsing.
/// </summary>
public static partial class Operations
{
    sealed record Entry(OperationInfo Info, Func<OperationContext, Task<OperationResult>> Run);

    static readonly List<Entry> Entries = [];

    public static IReadOnlyList<OperationInfo> All => [.. Entries.Select(e => e.Info)];

    public static OperationInfo? Find(string id) => Entries.FirstOrDefault(e => e.Info.Id == id)?.Info;

    public static async Task<OperationResult> RunAsync(string id, OperationContext ctx)
    {
        var entry = Entries.FirstOrDefault(e => e.Info.Id == id) ?? throw new KeyNotFoundException($"no operation '{id}'");
        // "@full" / "@basic" / "@visible": the servers set to that level on the Tracking page (ServerRegistry).
        if (ctx.Args.TryGetValue("servers", out var spec) && spec.Trim().StartsWith('@'))
            ctx.Args = new Dictionary<string, string>(ctx.Args) { ["servers"] = new LastWar.Storage.ServerRegistry(ctx.Database).Resolve(spec.Trim()) };
        var result = await entry.Run(ctx);
        // Anything that stores new observations may reveal a rename or a move: rebuild the histories.
        if (id.StartsWith("scan.", StringComparison.Ordinal) || id.StartsWith("board.", StringComparison.Ordinal)
            || id is "chat.backfill" or "migration.applicants" or "migration.report")
        {
            try { LastWar.Storage.Derived.RebuildAll(ctx.Database); }
            catch (Exception ex) { ctx.Report($"history rebuild skipped: {ex.Message}"); }
        }
        return result;
    }

    /// <summary>
    /// Which accounts may run which session operations - the whole policy in one place. Anything not
    /// listed keeps the default (guest or service). Privileged appears only where an operation is
    /// about the account itself (its base, alliance duties, mail, dailies); no scan runs on it.
    /// </summary>
    static readonly Dictionary<string, AccountTier> AccountPolicy = new()
    {
        // Need an account that passes the cross-server view gate, or one that lives ON the server.
        ["scan.map"] = AccountTier.Service,
        ["scan.power"] = AccountTier.Service,
        ["refresh.map"] = AccountTier.Service,
        ["migration.applicants"] = AccountTier.Service,
        ["kingdom.queue"] = AccountTier.Service,
        // Guests can only map their own server - this is a guest measuring tool.
        ["scan.map.bench"] = AccountTier.Guest | AccountTier.Service,
        ["scan.map.probe"] = AccountTier.Guest | AccountTier.Service,
        // Map/truck scans: a guest (its own server) or lw_1 (HQ 13 again, 2026-10-06). Never privileged.
        ["secret.tasks"] = AccountTier.Guest | AccountTier.Service,   // a guest reads its own server only; the page offers lw_1
        // Guests only until the exact request the app sends for a task is proven on one (2026-11-02: 20 unproven ones on lw_2 -
        // 5 for unfinished tasks the app never opens - were among the things it did before its ban).
        ["secret.task.rewards"] = AccountTier.Guest,
        ["trucks.list"] = AccountTier.Guest | AccountTier.Service,
        ["trucks.map"] = AccountTier.Guest | AccountTier.Service,
        ["truck.share"] = AccountTier.Service,
        ["probe.map.frames"] = AccountTier.Guest,
        ["probe.map.aoi"] = AccountTier.Guest,
        ["probe.map.overview"] = AccountTier.Guest,
        // The account's own chores. A guest has nothing worth collecting and no alliance.
        ["base.buildings"] = AccountTier.Service | AccountTier.Privileged,
        ["base.collect"] = AccountTier.Service | AccountTier.Privileged,
        ["base.idle"] = AccountTier.Service | AccountTier.Privileged,
        ["alliance.help"] = AccountTier.Service | AccountTier.Privileged,
        ["alliance.gifts"] = AccountTier.Service | AccountTier.Privileged,
        ["alliance.tech"] = AccountTier.Service | AccountTier.Privileged,
        ["mail.list"] = AccountTier.Service | AccountTier.Privileged,
        ["mail.claim"] = AccountTier.Service | AccountTier.Privileged,
        ["chat.dm"] = AccountTier.Service | AccountTier.Privileged,
        ["chat.share.point"] = AccountTier.Service | AccountTier.Privileged,
        ["vip.login"] = AccountTier.Service | AccountTier.Privileged,
        ["vip.reward"] = AccountTier.Service | AccountTier.Privileged,
        ["visitors.greet"] = AccountTier.Service | AccountTier.Privileged,
        ["daily.all"] = AccountTier.Service | AccountTier.Privileged,
    };

    static void ApplyAccountPolicy()
    {
        for (var i = 0; i < Entries.Count; i++)
            if (AccountPolicy.TryGetValue(Entries[i].Info.Id, out var t))
                Entries[i] = Entries[i] with { Info = Entries[i].Info with { Accounts = t } };
    }

    static void Add(
        string id, string group, string title, string description,
        Func<OperationContext, Task<OperationResult>> run,
        bool needsSession = true, bool writes = false, AccountTier accounts = AccountTier.None, params OperationParam[] ps) =>
        Entries.Add(new Entry(
            new OperationInfo(id, group, title, description, needsSession, ps)
            {
                Writes = writes,
                // Default for a session operation: anything a throwaway or work account can do.
                // Privileged is never a default - an operation must ask for it by name.
                Accounts = accounts != AccountTier.None ? accounts : needsSession ? AccountTier.Guest | AccountTier.Service : AccountTier.None,
            }, run));

    static async Task<OperationResult> Run(Task<GameActionResult> action) =>
        OperationResult.From(await action);

    /// <summary>Accepts "1967", "1967,1970" and "1957-1988".</summary>
    public static List<int> ParseServers(string spec)
    {
        var ids = new List<int>();
        foreach (var part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var range = part.Split('-', 2);
            if (range.Length == 2 && int.TryParse(range[0], out var lo) && int.TryParse(range[1], out var hi))
                for (var i = lo; i <= hi; i++) ids.Add(i);
            else if (int.TryParse(part.Trim(), out var one)) ids.Add(one);
        }
        if (ids.Count == 0) throw new ArgumentException($"could not read any server from \"{spec}\"");
        return ids;
    }

    static Operations()
    {
        // ---- base

        Add("base.buildings", "Base", "List buildings",
            "Every building on the base, with whether its production can be collected.",
            c => Run(new BaseActions(c.Require()).ListBuildingsAsync(c.Cancellation)));

        Add("base.collect", "Base", "Collect production",
            "Collects from every building that has production waiting.",
            c => Run(new BaseActions(c.Require()).CollectResourcesAsync(c.Cancellation)), writes: true);

        Add("base.idle", "Base", "Idle rewards",
            "Armed Truck and Overlord idle rewards. Peeks by default; claiming is a separate tick.",
            c => Run(new BaseActions(c.Require()).IdleRewardAsync(c.Flag("claim"), c.Cancellation)),
            writes: true,
            ps: new OperationParam("claim", "Claim them", "bool", "false",
                Help: "Off just reports what is waiting."));

        // ---- alliance

        Add("alliance.help", "Alliance", "Help everyone",
            "One tap on every outstanding help request in the alliance.",
            c => Run(new AllianceActions(c.Require()).HelpAllAsync(c.Cancellation)), writes: true);

        Add("alliance.gifts", "Alliance", "Claim gifts",
            "Claims both gift tiers. Harmless when there is nothing to claim.",
            c => Run(new AllianceActions(c.Require()).ClaimGiftsAsync(c.Cancellation)), writes: true);

        Add("alliance.tech", "Alliance", "Donate to tech",
            "Donates to whichever technology the alliance has flagged as recommended.",
            c => Run(new AllianceActions(c.Require()).DonateTechAsync(c.Cancellation)), writes: true);

        // ---- mail

        Add("mail.list", "Mail", "List mail",
            "Mail newest first: type, title and attachments resolved through the extracted game text.",
            async c =>
            {
                var (mail, truncated) = await new MailActions(c.Require()).FetchAllAsync(c.Int("pages", 10), c.Cancellation);
                var text = Text(c);
                var views = mail.Select(m => MailFormat.View(m, text)).OrderByDescending(v => v.Sent).ToList();
                var rows = views.Select(v => (IReadOnlyList<string>)[v.TypeName, v.Title, v.From, v.Sent?.ToString("u") ?? "",
                    string.Join(", ", v.Rewards), v.RewardState, v.Read ? "read" : "unread", v.Uid]).ToList();
                return OperationResult.Table($"{views.Count} mail, {views.Count(v => v.RewardState == "unclaimed")} unclaimed{(truncated ? " (more not fetched)" : "")}",
                    ["type", "title", "from", "sent", "attached", "reward", "read", "uid"], rows);
            },
            ps: new OperationParam("pages", "Pages", "number", "10"));

        Add("mail.claim", "Mail", "Claim all mail",
            "Marks read and claims every attachment, batched the way the server expects.",
            c => Run(new MailActions(c.Require()).ClaimAllAsync(c.Cancellation)), writes: true);

        Add("chat.dm", "Chat", "Direct message a player",
            "One private chat message from this account to one player (player page, admin only). The game makes the private room if there is none.",
            async c =>
            {
                var uid = c.Text("uid").Trim();
                var text = c.Text("text").Trim();
                if (!System.Text.RegularExpressions.Regex.IsMatch(uid, "^[0-9]{6,20}$")) throw new ArgumentException("uid: digits only");
                if (text.Length is 0 or > 500) throw new ArgumentException("message: 1-500 characters");
                return await Run(DirectMessage.SendAsync(c.Require(), uid, text, c.Text("lang", "en"), c.Cancellation));
            },
            writes: true,
            ps: [new OperationParam("uid", "Recipient uid", "text", ""), new OperationParam("text", "Message", "text", ""),
                 new OperationParam("lang", "Language", "text", "en")]);

        Add("reports.fetch", "Chat", "Fetch a shared battle report",
            "Opens one battle report shared in chat (reportUid + owner uid from the share), the way the game does, and saves the raw mail under data/raw/report_<uid>.json.",
            async c =>
            {
                var uid = c.Text("reportUid").Trim();
                var owner = c.Text("toUser").Trim();
                var mail = await BattleReports.FetchSharedAsync(c.Require(), uid, owner, c.Cancellation);
                if (mail is null) return OperationResult.Text("no push.mail.read arrived within 15 s");
                var json = LastWar.Protocol.Sfs.SfsJson.Write(mail);
                var path = Path.Combine(c.DataDir, "raw", $"report_{uid}.json");
                File.WriteAllText(path, json.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver() }));
                return OperationResult.Text($"{json.ToJsonString().Length} bytes -> {path}; keys: {string.Join(" ", (json as System.Text.Json.Nodes.JsonObject)?.Select(p => p.Key) ?? [])}");
            },
            ps: [new OperationParam("reportUid", "Report uid", "text", ""), new OperationParam("toUser", "Owner uid", "text", "")]);

        Add("chat.dm.guest", "Chat", "Direct message from a throwaway guest",
            "Creates a brand-new guest, sends ONE private message from it, deletes it. Tests whether guests may message at all (world chat needs HQ 4; a new guest is HQ 1).",
            async c =>
            {
                var uid = c.Text("uid").Trim();
                var text = c.Text("text").Trim();
                if (!System.Text.RegularExpressions.Regex.IsMatch(uid, "^[0-9]{6,20}$")) throw new ArgumentException("uid: digits only");
                // With sid/x/y it sends a tappable map point instead (text = its label) - the Scans page's Send.
                int server = c.Int("sid", 0), x = c.Int("x", -1), y = c.Int("y", -1);
                var point = server > 0 && x is >= 0 and <= 999 && y is >= 0 and <= 999;
                if (text.Length is 0 or > 500) throw new ArgumentException("message: 1-500 characters");
                GameActionResult? r = null;
                await WithGuestAsync(c, "dm", async guest => r = point
                    ? await DirectMessage.SharePointAsync(guest, uid, server, x, y, text, null, c.Cancellation)
                    : await DirectMessage.SendAsync(guest, uid, text, "en", c.Cancellation));
                return OperationResult.From(r!);
            },
            needsSession: false, writes: true,
            ps: [new OperationParam("uid", "Recipient uid", "text", ""), new OperationParam("text", "Message (or the point's label)", "text", ""),
                 new OperationParam("sid", "Point: server", "number"), new OperationParam("x", "Point: x", "number"), new OperationParam("y", "Point: y", "number")]);

        Add("chat.share.point", "Chat", "Send a map position",
            "A tappable map point in a private message from this account: a player's base (give player), a hive, a server centre or any x,y on any server.",
            async c =>
            {
                var uid = c.Text("uid").Trim();
                if (!System.Text.RegularExpressions.Regex.IsMatch(uid, "^[0-9]{6,20}$")) throw new ArgumentException("uid: digits only");
                int server = c.Int("sid", 0), x = c.Int("x", -1), y = c.Int("y", -1);
                if (server <= 0 || x is < 0 or > 999 || y is < 0 or > 999) throw new ArgumentException("sid > 0 and x, y 0-999");
                var player = c.Text("player").Trim();
                return await Run(DirectMessage.SharePointAsync(c.Require(), uid, server, x, y, c.Text("label", $"#{server} {x}:{y}"),
                    System.Text.RegularExpressions.Regex.IsMatch(player, "^[0-9]{6,20}$") ? player : null, c.Cancellation));
            },
            writes: true,
            ps: [new OperationParam("uid", "Recipient uid", "text", ""), new OperationParam("sid", "Server", "number", "0"),
                 new OperationParam("x", "X", "number", "500"), new OperationParam("y", "Y", "number", "500"),
                 new OperationParam("label", "Label", "text", ""), new OperationParam("player", "Player uid (a base share)", "text", "")]);

        // ---- vip and visitors

        Add("vip.login", "Daily", "VIP login points", "The daily VIP login score.",
            c => Run(new VipActions(c.Require()).DailyLoginAsync(c.Cancellation)), writes: true);

        Add("vip.reward", "Daily", "VIP daily reward", "The daily VIP freebie.",
            c => Run(new VipActions(c.Require()).DailyRewardAsync(c.Cancellation)), writes: true);

        Add("visitors.greet", "Daily", "Greet visitors",
            "Greets every visitor currently at the base.",
            c => Run(new VisitorActions(c.Require()).GreetAllAsync(c.Cancellation)), writes: true);

        Add("daily.all", "Daily", "Run the dailies",
            "Everything that resets each day, in one pass: help, gifts, tech, VIP, visitors, idle "
            + "rewards, production and mail. Each step is independent - one failing does not stop the rest.",
            RunDailiesAsync, writes: true,
            ps: new OperationParam("claimIdle", "Claim idle rewards", "bool", "true"));

        AddReadOperations();
        AddSecretTaskOperations();
        AddTruckOperations();
        AddTruckMapOperation();
        AddMapFramesProbe();
        AddMapAoiProbe();
        AddMapOverviewProbe();
        AddSvsOperations();
        AddRetentionOperations();
        AddChatOperations();
        AddActivityOperations();
        AddMigrationOperations();
        AddWaveOperations();
        AddGuestPowerOperation();
        AddProfileOperations();
        AddRefreshOperations();
        AddReportOperations();
        AddKingdomOperations();
        AddAssetOperations();
        AddProbeOperations();
        AddServerOperations();
        AddIntelOperations();
        AddPostgresOperations();
        ApplyAccountPolicy();
    }
}
