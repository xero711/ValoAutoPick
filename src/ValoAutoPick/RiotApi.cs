using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ValoAutoPick;

public sealed class RiotApi : IGameApi, IQueueApi, IDisposable
{
    private readonly HttpClient remote = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(8) };
    private readonly HttpClient local = new(new HttpClientHandler
    {
        UseProxy = false, AllowAutoRedirect = false,
        // Riot Client uses a self-signed certificate. This client is used ONLY for fixed loopback URLs.
        ServerCertificateCustomValidationCallback = (request, _, _, _) => request.RequestUri is { Host: "127.0.0.1", Scheme: "https" }
    }) { Timeout = TimeSpan.FromSeconds(4) };
    private string access = "", entitlement = "", version = "", server = "", fingerprint = "";
    private DateTimeOffset refreshed;
    public string Subject { get; private set; } = "";
    public string RegionLabel { get; private set; } = "";
    public string AccountName { get; private set; } = "";
    public DateTimeOffset ExpiresAt { get; private set; }
    public bool HasAccountSession { get; private set; }
    public void Invalidate() => refreshed = DateTimeOffset.MinValue;

    public RiotApi() { }
    internal RiotApi(HttpMessageHandler testTransport)
    {
        remote.Dispose(); remote = new HttpClient(testTransport) { Timeout = TimeSpan.FromSeconds(8) };
    }

    public void Logout()
    {
        access = entitlement = Subject = AccountName = fingerprint = "";
        HasAccountSession = false; ExpiresAt = DateTimeOffset.MinValue; Invalidate();
    }

    public async Task Login(AuthResult auth, string regionSetting, CancellationToken ct)
    {
        // Tokens are deliberately held in memory only. All service URLs are fixed and HTTPS.
        using var info = await AuthRequest(HttpMethod.Get, "https://auth.riotgames.com/userinfo", auth.AccessToken, null, ct);
        var subject = Text(info.RootElement, "sub");
        if (!Guid.TryParse(subject, out _)) throw new InvalidDataException("ログイン情報を確認できませんでした");
        using var ent = await AuthRequest(HttpMethod.Post, "https://entitlements.auth.riotgames.com/api/token/v1", auth.AccessToken, "{}", ct);
        using var geo = await AuthRequest(HttpMethod.Put, "https://riot-geo.pas.si.riotgames.com/pas/v1/product/valorant", auth.AccessToken,
            JsonSerializer.Serialize(new { id_token = auth.IdToken }), ct);
        var region = Text(geo.RootElement.GetProperty("affinities"), "live");
        if (regionSetting != "auto") region = regionSetting;
        var shard = region is "latam" or "br" ? "na" : region;
        if (region == "pbe") region = "na";
        if (!new[] { "na", "eu", "ap", "kr", "latam", "br" }.Contains(region))
            throw new ClientUnavailableException("地域を検出できません · 設定で地域を指定してください");
        using var verResponse = await remote.GetAsync("https://valorant-api.com/v1/version", ct);
        verResponse.EnsureSuccessStatusCode();
        using var ver = JsonDocument.Parse(await verResponse.Content.ReadAsStringAsync(ct));
        var newVersion = Text(ver.RootElement.GetProperty("data"), "riotClientVersion");
        var newEnt = Text(ent.RootElement, "entitlements_token");
        if (newVersion.Length == 0 || newEnt.Length == 0) throw new InvalidDataException("接続情報を取得できませんでした");
        var name = "ログイン済み";
        if (info.RootElement.TryGetProperty("acct", out var acct))
        {
            var gameName = Text(acct, "game_name"); var tag = Text(acct, "tag_line");
            if (gameName.Length > 0) name = gameName + "#" + tag;
        }
        access = auth.AccessToken; entitlement = newEnt; Subject = subject; version = newVersion;
        server = $"https://glz-{region}-1.{shard}.a.pvp.net";
        RegionLabel = RegionDisplay.Name(region, shard);
        AccountName = name; ExpiresAt = auth.ExpiresAt; HasAccountSession = true;
    }

    private async Task<JsonDocument> AuthRequest(HttpMethod method, string url, string token, string? body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body != null) req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var res = await remote.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode) throw new ApiException((int)res.StatusCode);
        return JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
    }

    public async Task EnsureConnected(string regionSetting, CancellationToken ct)
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Riot Games", "Riot Client", "Config", "lockfile");
        string raw;
        try { raw = ReadShared(path, 4096).Trim(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { throw new ClientUnavailableException("Riot Clientの起動を待っています"); }
        var parts = raw.Split(':');
        if (parts.Length != 5 || !int.TryParse(parts[1], out var pid) || !int.TryParse(parts[2], out var port) || port is < 1 or > 65535 || parts[4] != "https")
            throw new ClientUnavailableException("Riot Clientの接続情報を待っています");
        try
        {
            using var process = Process.GetProcessById(pid);
            if (!process.ProcessName.StartsWith("RiotClient", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException();
        }
        catch (ArgumentException) { throw new ClientUnavailableException("Riot Clientの起動を待っています"); }
        var key = raw + "|" + regionSetting;
        if (fingerprint == key && DateTimeOffset.UtcNow - refreshed < TimeSpan.FromSeconds(30)) return;
        using var token = await LocalGet(port, parts[3], "/entitlements/v1/token", ct);
        var subject = token.RootElement.GetProperty("subject").GetString() ?? "";
        var newAccess = token.RootElement.GetProperty("accessToken").GetString() ?? "";
        var newEntitlement = token.RootElement.GetProperty("token").GetString() ?? "";
        if (!Guid.TryParse(subject, out _) || newAccess.Length == 0 || newEntitlement.Length == 0)
            throw new ClientUnavailableException("Riot Clientへのログインを待っています");
        using var sessions = await LocalGet(port, parts[3], "/product-session/v1/external-sessions", ct);
        var game = sessions.RootElement.EnumerateObject().Select(x => x.Value)
            .FirstOrDefault(x => Text(x, "productId") == "valorant" && Text(x, "phase") != "Stopped");
        if (game.ValueKind != JsonValueKind.Object)
            throw new ClientUnavailableException("VALORANTの起動を待っています");
        var deployment = "";
        if (game.TryGetProperty("launchConfiguration", out var launch) && launch.TryGetProperty("arguments", out var args))
            deployment = args.EnumerateArray().Select(a => a.GetString() ?? "").FirstOrDefault(a => a.StartsWith("-ares-deployment="))?.Split('=', 2)[1] ?? "";
        string log = "";
        var logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VALORANT", "Saved", "Logs", "ShooterGame.log");
        try { log = ReadShared(logPath, 8 * 1024 * 1024); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        var logServers = Regex.Matches(log, @"https://glz-(na|eu|ap|kr|br|latam)-1\.(na|eu|ap|kr|pbe)\.a\.pvp\.net");
        var region = regionSetting == "auto" ? deployment : regionSetting;
        var shard = region is "br" or "latam" ? "na" : region;
        if (region == "pbe") region = "na";
        if (!new[] { "na", "eu", "ap", "kr", "br", "latam" }.Contains(region))
        {
            if (logServers.Count == 0) throw new ClientUnavailableException("地域を検出できません · 設定で地域を選んでください");
            region = logServers[^1].Groups[1].Value; shard = logServers[^1].Groups[2].Value;
        }
        if (regionSetting == "auto" && Text(game, "patchlineId") == "pbe") { region = "na"; shard = "pbe"; }
        var versions = Regex.Matches(log, @"CI server version:\s*([\w.\-]+)");
        var clientVersion = Text(game, "version");
        if (!clientVersion.StartsWith("release-")) clientVersion = versions.Count > 0 ? versions[^1].Groups[1].Value : "";
        if (clientVersion.Length == 0) throw new ClientUnavailableException("ゲームのバージョン情報を待っています");
        Subject = subject; access = newAccess; entitlement = newEntitlement; version = clientVersion;
        server = $"https://glz-{region}-1.{shard}.a.pvp.net";
        fingerprint = key; refreshed = DateTimeOffset.UtcNow; RegionLabel = RegionDisplay.Name(region, shard);
    }

    internal static string ReadShared(string path, int maxBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        // Read the beginning (version) and end (latest deployment), bounded even for long sessions.
        if (stream.Length <= maxBytes) return new StreamReader(stream).ReadToEnd();
        var half = maxBytes / 2;
        var bytes = new byte[maxBytes];
        stream.ReadExactly(bytes.AsSpan(0, half));
        stream.Seek(-half, SeekOrigin.End);
        stream.ReadExactly(bytes.AsSpan(half, half));
        return Encoding.UTF8.GetString(bytes);
    }
    private async Task<JsonDocument> LocalGet(int port, string password, string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://127.0.0.1:{port}{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("riot:" + password)));
        using var response = await local.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw new ClientUnavailableException("Riot Clientへのログインを待っています");
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }
    private async Task<JsonDocument?> Request(HttpMethod method, string path, bool missingIsIdle, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, server + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
        request.Headers.Add("X-Riot-Entitlements-JWT", entitlement);
        request.Headers.Add("X-Riot-ClientVersion", version);
        request.Headers.Add("X-Riot-ClientPlatform", Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"platformType\":\"PC\",\"platformOS\":\"Windows\",\"platformOSVersion\":\"10.0.19042.1.256.64bit\",\"platformChipset\":\"Unknown\"}")));
        if (method == HttpMethod.Post) request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        using var response = await remote.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound && missingIsIdle) return null;
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) Invalidate();
            var retry = response.Headers.RetryAfter?.Delta?.TotalSeconds
                ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow)?.TotalSeconds ?? 5;
            throw new ApiException((int)response.StatusCode, (int)Math.Ceiling(retry));
        }
        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
    }
    public async Task<string?> GetMatchId(CancellationToken ct)
    {
        using var result = await Request(HttpMethod.Get, $"/pregame/v1/players/{Subject}", true, ct);
        if (result == null) return null;
        var id = Text(result.RootElement, "MatchID");
        return Guid.TryParse(id, out _) ? id : null;
    }
    public async Task<MatchSnapshot> GetMatch(string matchId, CancellationToken ct)
    {
        ValidateId(matchId);
        using var result = await Request(HttpMethod.Get, $"/pregame/v1/matches/{matchId}", false, ct);
        var root = result!.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("マッチ情報が不正です");
        var players = new List<Player>();
        if (root.TryGetProperty("AllyTeam", out var team) && team.ValueKind == JsonValueKind.Object && team.TryGetProperty("Players", out var list))
        {
            if (list.ValueKind != JsonValueKind.Array) throw new InvalidDataException("参加者情報が不正です");
            players.AddRange(list.EnumerateArray().Select(p => new Player(Text(p, "Subject"), Text(p, "CharacterID"), Text(p, "CharacterSelectionState"))));
        }
        return new(Text(root, "ID"), Text(root, "PregameState"), players);
    }
    public async Task Select(string matchId, string agentId, CancellationToken ct) => await Write("select", matchId, agentId, ct);
    public async Task Lock(string matchId, string agentId, CancellationToken ct) => await Write("lock", matchId, agentId, ct);
    public async Task<PartyQueueSnapshot> GetQueueSnapshot(CancellationToken ct)
    {
        using var player = await Request(HttpMethod.Get, $"/parties/v1/players/{Subject}", true, ct);
        if (player == null) throw new ClientUnavailableException("パーティー情報を取得できません");
        var partyId = Text(player.RootElement, "CurrentPartyID");
        ValidateId(partyId);
        using var party = await Request(HttpMethod.Get, $"/parties/v1/parties/{partyId}", false, ct);
        var root = party!.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !string.Equals(Text(root, "ID"), partyId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("パーティー情報が不正です");
        var state = Text(root, "State");
        var queueId = root.TryGetProperty("MatchmakingData", out var matchmaking) ? Text(matchmaking, "QueueID") : "";
        var entryTime = Text(root, "QueueEntryTime");
        if (!DateTimeOffset.TryParse(entryTime, out var queuedAt))
            throw new InvalidDataException("キュー状態が不正です");
        var hasQueueEntry = queuedAt.Year > 1;
        var isLeader = false;
        if (root.TryGetProperty("Members", out var members) && members.ValueKind == JsonValueKind.Array)
        {
            var self = members.EnumerateArray().FirstOrDefault(member => Text(member, "Subject") == Subject);
            isLeader = self.ValueKind == JsonValueKind.Object && self.TryGetProperty("IsOwner", out var owner) && owner.ValueKind == JsonValueKind.True;
        }
        if (!root.TryGetProperty("RestrictedSeconds", out var seconds) || seconds.ValueKind != JsonValueKind.Number ||
            !seconds.TryGetInt32(out var restricted) || restricted < 0)
            throw new InvalidDataException("キュー制限情報が不正です");
        return new PartyQueueSnapshot(partyId, state, queueId, isLeader, restricted,
            StringArray(root, "EligibleQueues"), StringArray(root, "QueueIneligibilities"), state == "MATCHMAKING" || hasQueueEntry);
    }
    public async Task EnterQueue(string partyId, CancellationToken ct)
    {
        ValidateId(partyId);
        using var result = await Request(HttpMethod.Post, $"/parties/v1/parties/{partyId}/matchmaking/join", false, ct);
    }
    public async Task LeaveQueue(string partyId, CancellationToken ct)
    {
        ValidateId(partyId);
        using var result = await Request(HttpMethod.Post, $"/parties/v1/parties/{partyId}/matchmaking/leave", false, ct);
    }
    private async Task Write(string action, string matchId, string agentId, CancellationToken ct)
    {
        ValidateId(matchId); ValidateId(agentId);
        using var result = await Request(HttpMethod.Post, $"/pregame/v1/matches/{matchId}/{action}/{agentId}", false, ct);
    }
    private static void ValidateId(string id) { if (!Guid.TryParse(id, out _)) throw new InvalidDataException("識別情報が不正です"); }
    private static string[] StringArray(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"{name} が不正です");
        var result = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
                throw new InvalidDataException($"{name} が不正です");
            result.Add(item.GetString()!);
        }
        return result.ToArray();
    }
    private static string Text(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    public void Dispose() { remote.Dispose(); local.Dispose(); }
}
