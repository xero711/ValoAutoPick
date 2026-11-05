using ValoAutoPick;

var tests = new List<(string, Func<Task>)>();
var now = DateTimeOffset.UtcNow;
Settings Config() => new() { AgentId = "agent-a" };
void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}"); }
tests.Add(("idle never selects", async () => { var api = new Fake { MatchId = null }; await new PickEngine(api, _ => { }).Tick(Config(), now, default); Equal(0, api.Selects); }));
tests.Add(("two consecutive matches and duplicate suppression", async () =>
{
    var api = new Fake(); var engine = new PickEngine(api, _ => { });
    await engine.Tick(Config(), now, default); Equal(1, api.Locks);
    api.SelfState = ""; await engine.Tick(Config(), now.AddSeconds(1), default); Equal(1, api.Locks);
    api.MatchId = null; await engine.Tick(Config(), now.AddSeconds(2), default);
    api.MatchId = "match-2"; await engine.Tick(Config(), now.AddSeconds(3), default); Equal(2, api.Locks);
    api.MatchId = "match-1"; api.SelfState = ""; await engine.Tick(Config(), now.AddSeconds(4), default); Equal(2, api.Locks);
}));
tests.Add(("selected agent is picked immediately", async () =>
{
    var api = new Fake(); var engine = new PickEngine(api, _ => { });
    await engine.Tick(Config(), now, default); Equal(1, api.Selects); Equal(1, api.Locks);
    await engine.Tick(Config(), now.AddMilliseconds(100), default); Equal(1, api.Selects); Equal(1, api.Locks);
}));
tests.Add(("a teammate picking the configured agent requires manual action", async () =>
{
    var api = new Fake { Others = [new("other", "agent-a", "locked")] };
    await new PickEngine(api, _ => { }).Tick(Config(), now, default); Equal(0, api.Selects); Equal(0, api.Locks);
}));
tests.Add(("taken agents are retried when freed", async () =>
{
    var api = new Fake { Others = [new("other", "agent-a", "locked")] }; var config = Config();
    var engine = new PickEngine(api, _ => { }); await engine.Tick(config, now, default); Equal(0, api.Selects);
    api.Others = []; await engine.Tick(config, now, default); Equal(1, api.Locks);
}));
tests.Add(("manual lock is preserved", async () => { var api = new Fake { SelfState = "locked", Selected = "manual" }; await new PickEngine(api, _ => { }).Tick(Config(), now, default); Equal(0, api.Selects); Equal("manual", api.Selected); }));
tests.Add(("select and lock immediately", async () => { var api = new Fake(); var engine = new PickEngine(api, _ => { }); await engine.Tick(Config(), now, default); await engine.Tick(Config(), now, default); Equal(1, api.Selects); Equal(1, api.Locks); }));
tests.Add(("inactive or missing self never writes", async () =>
{
    var api = new Fake { State = "provisioned" }; var engine = new PickEngine(api, _ => { }); await engine.Tick(Config(), now, default); Equal(0, api.Selects);
    api.State = "character_select_active"; api.IncludeSelf = false; await engine.Tick(Config(), now, default); Equal(0, api.Selects);
}));
tests.Add(("rejected agent is reported without selecting a different agent", async () =>
{
    var api = new Fake { RejectAgent = "agent-a" }; var engine = new PickEngine(api, _ => { });
    await engine.Tick(Config(), now, default); await engine.Tick(Config(), now, default); Equal("", api.Selected); Equal(0, api.Locks); Equal(1, api.Selects);
}));
tests.Add(("cancel after selection prevents lock", async () =>
{
    using var cts = new CancellationTokenSource(); var api = new Fake { AfterSelect = cts.Cancel }; var engine = new PickEngine(api, _ => { });
    try { await engine.Tick(Config(), now, cts.Token); throw new Exception("Should cancel"); } catch (OperationCanceledException) { }
    Equal(0, api.Locks);
}));
tests.Add(("lock response lost is not sent twice", async () =>
{
    var api = new Fake { LoseLockResponse = true }; var engine = new PickEngine(api, _ => { });
    try { await engine.Tick(Config(), now, default); } catch (HttpRequestException) { }
    await engine.Tick(Config(), now, default); Equal(1, api.Locks);
}));
tests.Add(("write retries are bounded", async () =>
{
    var api = new Fake { SelectError = 500 }; var engine = new PickEngine(api, _ => { });
    for (var i = 0; i < 12; i++) try { await engine.Tick(Config(), now, default); } catch (ApiException) { }
    Equal(6, api.Selects); Equal(0, api.Locks);
}));
tests.Add(("rate limit is surfaced for worker backoff", async () =>
{
    var api = new Fake { SelectError = 429 };
    try { await new PickEngine(api, _ => { }).Tick(Config(), now, default); throw new Exception("Expected rate limit"); }
    catch (ApiException e) { Equal(429, e.Status); }
    Equal(1, api.Selects);
}));
tests.Add(("auto queue joins only the selected eligible queue from the idle lobby", async () =>
{
    var api = new FakeQueue(); var engine = new AutoQueueEngine(api, _ => { });
    await engine.Tick(now, default); Equal(1, api.Joins); Equal(api.Snapshot.PartyId, api.JoinedParty);
    await engine.Tick(now.AddSeconds(1), default); Equal(1, api.Joins);
    await engine.Tick(now.AddSeconds(3), default); Equal(1, api.Joins);
    await engine.LeaveActiveQueue(default); Equal(1, api.Leaves); Equal("DEFAULT", api.Snapshot.State);
}));
tests.Add(("auto queue stop leaves an active search but not a found match or another leader's queue", async () =>
{
    var found = new FakeQueue { Snapshot = new("44444444-4444-4444-4444-444444444444", "MATCHMADE_GAME_STARTING", "competitive", true, 0, ["competitive"], [], true) };
    await new AutoQueueEngine(found, _ => { }).LeaveActiveQueue(default); Equal(0, found.Leaves);
    var notLeader = new FakeQueue { Snapshot = new("44444444-4444-4444-4444-444444444444", "MATCHMAKING", "competitive", false, 0, ["competitive"], [], true) };
    await new AutoQueueEngine(notLeader, _ => { }).LeaveActiveQueue(default); Equal(0, notLeader.Leaves);
    var active = new FakeQueue { Snapshot = new("44444444-4444-4444-4444-444444444444", "MATCHMAKING", "competitive", true, 0, ["competitive"], [], true) };
    await new AutoQueueEngine(active, _ => { }).LeaveActiveQueue(default); Equal(1, active.Leaves); Equal("DEFAULT", active.Snapshot.State);
}));
tests.Add(("auto queue does not join during matches or when party requirements fail", async () =>
{
    var blocked = new[]
    {
        new PartyQueueSnapshot("44444444-4444-4444-4444-444444444444", "MATCHMAKING", "competitive", true, 0, ["competitive"], []),
        new PartyQueueSnapshot("44444444-4444-4444-4444-444444444444", "DEFAULT", "competitive", false, 0, ["competitive"], []),
        new PartyQueueSnapshot("44444444-4444-4444-4444-444444444444", "DEFAULT", "competitive", true, 0, [], []),
        new PartyQueueSnapshot("44444444-4444-4444-4444-444444444444", "DEFAULT", "competitive", true, 0, ["competitive"], [], true),
        new PartyQueueSnapshot("44444444-4444-4444-4444-444444444444", "DEFAULT", "competitive", true, 0, ["competitive"], ["restricted"]),
        new PartyQueueSnapshot("44444444-4444-4444-4444-444444444444", "DEFAULT", "", true, 0, ["competitive"], []),
        new PartyQueueSnapshot("44444444-4444-4444-4444-444444444444", "DEFAULT", "competitive", true, 45, ["competitive"], [])
    };
    foreach (var snapshot in blocked)
    {
        var api = new FakeQueue { Snapshot = snapshot };
        await new AutoQueueEngine(api, _ => { }).Tick(now, default); Equal(0, api.Joins);
    }
}));
tests.Add(("auto queue cancellation prevents a join", async () =>
{
    using var cts = new CancellationTokenSource(); cts.Cancel(); var api = new FakeQueue();
    try { await new AutoQueueEngine(api, _ => { }).Tick(now, cts.Token); throw new Exception("Should cancel"); }
    catch (OperationCanceledException) { }
    Equal(0, api.Joins);
}));
tests.Add(("OAuth callback state and origin validation", () =>
{
    var state = AuthFlow.NewState();
    var fragment = $"#access_token=test-access&id_token=test-id&expires_in=3600&token_type=Bearer&state={state}";
    var valid = new Uri("https://playvalorant.com/opt_in" + fragment);
    Equal("test-access", AuthFlow.ParseCallback(valid, state, now).AccessToken);
    foreach (var invalid in new[] { new Uri("https://playvalorant.com.evil.example/opt_in" + fragment), new Uri("http://playvalorant.com/opt_in" + fragment), new Uri("https://playvalorant.com:8443/opt_in" + fragment), new Uri(valid + "&state=duplicate") })
        try { AuthFlow.ParseCallback(invalid, state, now); throw new Exception("Accepted invalid callback"); } catch (InvalidDataException) { }
    try { AuthFlow.ParseCallback(valid, "wrong", now); throw new Exception("Accepted wrong state"); } catch (InvalidDataException) { }
    Equal(false, AuthFlow.AllowedNavigation(new Uri("https://riotgames.com.evil.example/login")));
    Equal(true, AuthFlow.AllowedNavigation(new Uri("https://authenticate.riotgames.com/login")));
    Equal(true, AuthFlow.AllowedNavigation(new Uri("https://accounts.google.com/o/oauth2/auth")));
    Equal(true, AuthFlow.AllowedNavigation(new Uri("https://appleid.apple.com/auth/authorize")));
    Equal(true, AuthFlow.AllowedNavigation(new Uri("https://www.facebook.com/v20.0/dialog/oauth")));
    Equal(false, AuthFlow.AllowedNavigation(new Uri("https://accounts.google.com.evil.example/login")));
    Equal(false, AuthFlow.AllowedNavigation(new Uri("https://google.com/login")));
    Equal(false, AuthFlow.AllowedNavigation(new Uri("http://accounts.google.com/login")));
    return Task.CompletedTask;
}));
tests.Add(("settings sanitize invalid values", () =>
{
    var guid = Guid.NewGuid().ToString(); var settings = new Settings { AgentId = guid, Region = "evil" }.Normalize();
    Equal(guid, settings.AgentId); Equal("auto", settings.Region); return Task.CompletedTask;
}));
tests.Add(("legacy settings ignore malformed priority values", () =>
{
    Equal("", Storage.Parse("""{"Priority":[123]}""").AgentId);
    var id = Guid.NewGuid().ToString();
    Equal(id, Storage.Parse("{\"Priority\":[\"" + id + "\"]}").AgentId);
    return Task.CompletedTask;
}));
tests.Add(("incomplete party responses never join a queue", async () =>
{
    var invalidParties = new[]
    {
        """{"ID":"44444444-4444-4444-4444-444444444444","State":"DEFAULT"}""",
        """{"ID":"44444444-4444-4444-4444-444444444444","State":"DEFAULT","QueueEntryTime":"bad","RestrictedSeconds":0,"EligibleQueues":[],"QueueIneligibilities":[]}""",
        """{"ID":"44444444-4444-4444-4444-444444444444","State":"DEFAULT","QueueEntryTime":"0001-01-01T00:00:00Z","RestrictedSeconds":0,"EligibleQueues":[],"QueueIneligibilities":[{}]}""",
        """{"ID":"44444444-4444-4444-4444-444444444444","State":"DEFAULT","QueueEntryTime":"0001-01-01T00:00:00Z","RestrictedSeconds":"unknown","EligibleQueues":[],"QueueIneligibilities":[]}"""
    };
    foreach (var json in invalidParties)
    {
        var transport = new Transport { PartyResponse = json }; using var api = new RiotApi(transport);
        await api.Login(new AuthResult("fake-access", "fake-id", now.AddHours(1)), "auto", default);
        try { await new AutoQueueEngine(api, _ => { }).Tick(now, default); throw new Exception("Accepted incomplete party response"); }
        catch (InvalidDataException) { }
        Equal(false, transport.Calls.Any(call => call.Uri.AbsolutePath.EndsWith("/matchmaking/join")));
    }
}));
tests.Add(("malformed match players are reported as invalid data", async () =>
{
    var transport = new Transport { MatchResponse = """{"ID":"22222222-2222-2222-2222-222222222222","PregameState":"character_select_active","AllyTeam":{"Players":{}}}""" };
    using var api = new RiotApi(transport);
    await api.Login(new AuthResult("fake-access", "fake-id", now.AddHours(1)), "auto", default);
    try { await api.GetMatch("22222222-2222-2222-2222-222222222222", default); throw new Exception("Accepted malformed players"); }
    catch (InvalidDataException) { }
}));
tests.Add(("region display uses understandable names and preserves PBE", () =>
{
    Equal("アジア太平洋（東京など）", RegionDisplay.Name("ap", "ap"));
    Equal("北アメリカ", RegionDisplay.Name("na", "na"));
    Equal("ブラジル", RegionDisplay.Name("br", "na"));
    Equal("テスト環境（PBE）", RegionDisplay.Name("na", "pbe"));
    Equal("ap", new Settings { Region = "ap" }.Normalize().Region);
    return Task.CompletedTask;
}));
tests.Add(("remote account login derives region and does not leak tokens to metadata API", async () =>
{
    var transport = new Transport(); using var api = new RiotApi(transport);
    await api.Login(new AuthResult("fake-access", "fake-id", now.AddHours(1)), "auto", default);
    Equal("11111111-1111-1111-1111-111111111111", api.Subject);
    Equal("ブラジル", api.RegionLabel); Equal("Test#0000", api.AccountName);
    Equal(true, api.HasAccountSession); Equal(4, transport.Calls.Count);
    Equal(HttpMethod.Put, transport.Calls[2].Method);
    Equal("", transport.Calls[3].Authorization);
    var match = await api.GetMatchId(default); Equal("22222222-2222-2222-2222-222222222222", match);
    Equal("glz-br-1.na.a.pvp.net", transport.Calls[^1].Uri.Host);
    Equal("Bearer fake-access", transport.Calls[^1].Authorization);
    Equal("fake-entitlement", transport.Calls[^1].Entitlement);
    var snapshot = await api.GetMatch(match!, default); Equal(1, snapshot.Allies.Count);
    await api.Select(match!, "33333333-3333-3333-3333-333333333333", default);
    Equal(HttpMethod.Post, transport.Calls[^1].Method);
    await api.Lock(match!, "33333333-3333-3333-3333-333333333333", default);
    Equal(true, transport.Calls[^1].Uri.AbsolutePath.Contains("/lock/"));
    var queue = await api.GetQueueSnapshot(default);
    Equal("44444444-4444-4444-4444-444444444444", queue.PartyId); Equal("competitive", queue.QueueId);
    Equal(true, queue.IsLeader); Equal(true, queue.EligibleQueues.Contains("competitive")); Equal(false, queue.IsInQueue);
    transport.QueueEntryTime = "2026-11-05T12:00:00Z";
    Equal(true, (await api.GetQueueSnapshot(default)).IsInQueue);
    transport.QueueEntryTime = "0001-01-01T00:00:00Z";
    await api.EnterQueue(queue.PartyId, default);
    Equal(HttpMethod.Post, transport.Calls[^1].Method);
    Equal("/parties/v1/parties/44444444-4444-4444-4444-444444444444/matchmaking/join", transport.Calls[^1].Uri.AbsolutePath);
    await api.LeaveQueue(queue.PartyId, default);
    Equal(HttpMethod.Post, transport.Calls[^1].Method);
    Equal("/parties/v1/parties/44444444-4444-4444-4444-444444444444/matchmaking/leave", transport.Calls[^1].Uri.AbsolutePath);
    api.Logout(); Equal(false, api.HasAccountSession); Equal("", api.Subject);
}));
tests.Add(("remote 404 means idle and Retry-After is retained", async () =>
{
    var transport = new Transport(); using var api = new RiotApi(transport);
    await api.Login(new AuthResult("fake-access", "fake-id", now.AddHours(1)), "ap", default);
    transport.GameStatus = 404; Equal<string?>(null, await api.GetMatchId(default));
    transport.GameStatus = 429;
    try { await api.GetMatchId(default); throw new Exception("Expected 429"); }
    catch (ApiException e) { Equal(429, e.Status); Equal(17, e.RetrySeconds); }
    Equal("glz-ap-1.ap.a.pvp.net", transport.Calls[^1].Uri.Host);
}));
var failures = 0;
foreach (var (name, test) in tests) try { await test(); Console.WriteLine("PASS " + name); } catch (Exception e) { failures++; Console.WriteLine("FAIL " + name + ": " + e.Message); }
Console.WriteLine($"{tests.Count - failures}/{tests.Count} passed");
return failures == 0 ? 0 : 1;

sealed class Fake : IGameApi
{
    public string Subject => "me";
    public string? MatchId = "match-1";
    public string State = "character_select_active", SelfState = "", Selected = "";
    public bool IncludeSelf = true, LoseLockResponse;
    public List<Player> Others = [];
    public string? RejectAgent;
    public int Selects, Locks, SelectError;
    public Action? AfterSelect;
    public Task<string?> GetMatchId(CancellationToken ct) { ct.ThrowIfCancellationRequested(); return Task.FromResult(MatchId); }
    public Task<MatchSnapshot> GetMatch(string id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); var players = new List<Player>(Others);
        if (IncludeSelf) players.Add(new(Subject, Selected, SelfState));
        return Task.FromResult(new MatchSnapshot(MatchId!, State, players));
    }
    public Task Select(string match, string agent, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); Selects++;
        if (agent == RejectAgent) throw new ApiException(400);
        if (SelectError > 0) throw new ApiException(SelectError);
        Selected = agent; SelfState = "selected"; AfterSelect?.Invoke(); return Task.CompletedTask;
    }
    public Task Lock(string match, string agent, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); Locks++; Selected = agent; SelfState = "locked";
        if (LoseLockResponse) throw new HttpRequestException();
        return Task.CompletedTask;
    }
}

sealed class FakeQueue : IQueueApi
{
    public PartyQueueSnapshot Snapshot = new("44444444-4444-4444-4444-444444444444", "DEFAULT", "competitive", true, 0, ["competitive", "unrated"], []);
    public int Joins, Leaves;
    public string JoinedParty = "";
    public Task<PartyQueueSnapshot> GetQueueSnapshot(CancellationToken ct) { ct.ThrowIfCancellationRequested(); return Task.FromResult(Snapshot); }
    public Task EnterQueue(string partyId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); Joins++; JoinedParty = partyId; Snapshot = Snapshot with { State = "MATCHMAKING" }; return Task.CompletedTask;
    }
    public Task LeaveQueue(string partyId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); Leaves++; Snapshot = Snapshot with { State = "DEFAULT", IsInQueue = false }; return Task.CompletedTask;
    }
}

sealed class Transport : HttpMessageHandler
{
    public List<(Uri Uri, HttpMethod Method, string Authorization, string Entitlement)> Calls = [];
    public int GameStatus = 200;
    public string QueueEntryTime = "0001-01-01T00:00:00Z";
    public string? PartyResponse, MatchResponse;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        Calls.Add((req.RequestUri!, req.Method, req.Headers.Authorization?.ToString() ?? "", req.Headers.TryGetValues("X-Riot-Entitlements-JWT", out var headers) ? headers.Single() : ""));
        var json = req.RequestUri!.Host switch
        {
            "auth.riotgames.com" => """{"sub":"11111111-1111-1111-1111-111111111111","acct":{"game_name":"Test","tag_line":"0000"}}""",
            "entitlements.auth.riotgames.com" => """{"entitlements_token":"fake-entitlement"}""",
            "riot-geo.pas.si.riotgames.com" => """{"affinities":{"live":"br","pbe":"na"}}""",
            "valorant-api.com" => """{"data":{"riotClientVersion":"release-test"}}""",
            _ when req.RequestUri.AbsolutePath.Contains("/parties/v1/players/") => """{"CurrentPartyID":"44444444-4444-4444-4444-444444444444"}""",
            _ when req.RequestUri.AbsolutePath.EndsWith("/matchmaking/join") => """{"ID":"44444444-4444-4444-4444-444444444444"}""",
            _ when req.RequestUri.AbsolutePath.EndsWith("/matchmaking/leave") => "{}",
            _ when req.RequestUri.AbsolutePath.Contains("/parties/v1/parties/") => PartyResponse ?? """{"ID":"44444444-4444-4444-4444-444444444444","State":"DEFAULT","Members":[{"Subject":"11111111-1111-1111-1111-111111111111","IsOwner":true}],"MatchmakingData":{"QueueID":"competitive"},"QueueEntryTime":"QUEUE_ENTRY_TIME","RestrictedSeconds":0,"EligibleQueues":["competitive","unrated"],"QueueIneligibilities":[]}""".Replace("QUEUE_ENTRY_TIME", QueueEntryTime),
            _ when req.RequestUri.AbsolutePath.Contains("/players/") => """{"MatchID":"22222222-2222-2222-2222-222222222222"}""",
            _ => MatchResponse ?? """{"ID":"22222222-2222-2222-2222-222222222222","PregameState":"character_select_active","AllyTeam":{"Players":[{"Subject":"11111111-1111-1111-1111-111111111111","CharacterID":"","CharacterSelectionState":""}]}}"""
        };
        var status = req.RequestUri.Host.EndsWith(".pvp.net") ? GameStatus : 200;
        var res = new HttpResponseMessage((System.Net.HttpStatusCode)status) { Content = new StringContent(json) };
        if (status == 429) res.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(17));
        return Task.FromResult(res);
    }
}
