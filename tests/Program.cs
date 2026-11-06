using ValoAutoPick;

var tests = new List<(string, Func<Task>)>();
var now = DateTimeOffset.UtcNow;
Settings Config() => new() { AgentId = "agent-a" };
void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}"); }
tests.Add(("all supported modes map IDs resolve to names", () => {
    foreach (var (key, name) in new[] { ("HURM_Alley", "ディストリクト"), ("HURM_Bowl", "カスバ"), ("HURM_Helix", "ドリフト"), ("HURM_HighTide", "グリッチ"), ("HURM_Yard", "ピアッツァ") })
        Equal(name, MapCatalog.Name("/Game/Maps/Mode/" + key));
    Equal(MapCatalog.Maps.Length, MapCatalog.Maps.Select(m => m.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count()); return Task.CompletedTask;
}));
tests.Add(("map override is selected, displayed and locked", async () => {
    var api = new Fake { MapId = "/Game/Maps/Ascent/Ascent" }; var config = Config(); config.MapAgents["ascent"] = "agent-b"; string seen = "";
    await new PickEngine(api, _ => { }, mapChanged: map => seen = map).Tick(config, now, default);
    Equal("agent-b", api.Selected); Equal(1, api.Locks); Equal("アセント", MapCatalog.Name(seen));
}));
tests.Add(("missing map waits, unknown map uses default", async () => {
    var api = new Fake(); var config = Config(); config.MapAgents["Ascent"] = "agent-b"; var engine = new PickEngine(api, _ => { });
    await engine.Tick(config, now, default); Equal(0, api.Selects);
    api.MapId = "/Game/Maps/NewMap/NewMap"; await engine.Tick(config, now, default); Equal("agent-a", api.Selected);
}));
tests.Add(("map configuration normalizes and preserves legacy default", () => {
    var id = Guid.NewGuid().ToString(); var agent = Guid.NewGuid().ToString();
    var settings = Storage.Parse("{\"AgentId\":\"" + id + "\",\"MapAgents\":{\"/Game/Maps/Ascent/Ascent\":\"" + agent + "\",\"Bad\":\"bad\"}}");
    Equal(id, settings.AgentId); Equal(agent, settings.MapAgents["ascent"]); Equal(1, settings.MapAgents.Count); return Task.CompletedTask;
}));
tests.Add(("map events reset on new matches and when leaving pregame", async () => {
    var api = new Fake { MapId = "/Game/Maps/Ascent/Ascent", State = "provisioned" }; var changes = new List<string>();
    var engine = new PickEngine(api, _ => { }, mapChanged: changes.Add);
    await engine.Tick(Config(), now, default); Equal("", changes[0]); Equal("Ascent", MapCatalog.Key(changes[1]));
    api.MatchId = "match-2"; api.MapId = "/Game/Maps/Bonsai/Bonsai";
    await engine.Tick(Config(), now, default); Equal("", changes[2]); Equal("Bonsai", MapCatalog.Key(changes[3]));
    api.MatchId = null; await engine.Tick(Config(), now, default); Equal(4, changes.Count);
    await engine.Tick(Config(), now.AddSeconds(6), default); Equal("", changes[4]);
}));
tests.Add(("auto queue map observer displays match and never picks", async () => {
    var api = new Fake { MapId = "/Game/Maps/Ascent/Ascent" }; var changes = new List<string>(); var observer = new MapObserver(api, changes.Add);
    await observer.Tick(default); Equal("Ascent", MapCatalog.Key(changes[^1])); Equal(0, api.Selects); Equal(0, api.Locks);
    api.MatchId = null; await observer.Tick(default, now); Equal("Ascent", MapCatalog.Key(changes[^1]));
    await observer.Tick(default, now.AddSeconds(6)); Equal("", changes[^1]);
}));
tests.Add(("completed match can restore map display after monitoring resumes", async () => {
    var api = new Fake { MapId = "/Game/Maps/Ascent/Ascent" }; var changes = new List<string>(); var engine = new PickEngine(api, _ => { }, mapChanged: changes.Add);
    await engine.Tick(Config(), now, default); changes.Clear(); await engine.Tick(Config(), now, default);
    Equal("Ascent", MapCatalog.Key(changes[^1])); Equal(1, api.Locks);
}));
tests.Add(("pregame map remains during transition and core game then clears in lobby", async () => {
    var api = new Fake { MapId = "/Game/Maps/Ascent/Ascent", State = "provisioned" }; var seen = new List<string>(); var engine = new PickEngine(api, _ => { }, mapChanged: seen.Add);
    await engine.Tick(Config(), now, default); Equal("Ascent", MapCatalog.Key(seen[^1]));
    api.MatchId = null; api.PartyState = "MATCHMADE_GAME_STARTING";
    await engine.Tick(Config(), now.AddSeconds(2), default); Equal("Ascent", MapCatalog.Key(seen[^1]));
    await engine.Tick(Config(), now.AddSeconds(15), default); Equal("Ascent", MapCatalog.Key(seen[^1]));
    api.CoreMatchId = "match-1"; api.PartyState = "IN_GAME";
    await engine.Tick(Config(), now.AddSeconds(17), default); Equal("Ascent", MapCatalog.Key(seen[^1]));
    await engine.Tick(Config(), now.AddMinutes(5), default); Equal("Ascent", MapCatalog.Key(seen[^1]));
    api.CoreMatchId = null; api.PartyState = "DEFAULT"; api.LocalFlow = "MainMenu";
    await engine.Tick(Config(), now.AddMinutes(5).AddSeconds(2), default); Equal("", seen[^1]);
}));
tests.Add(("queue-only observer retains map during match and clears after return", async () => {
    var api = new Fake { MapId = "/Game/Maps/Pitt/Pitt" }; var seen = new List<string>(); var observer = new MapObserver(api, seen.Add);
    await observer.Tick(default, now); api.MatchId = null; api.CoreMatchId = "match-1"; api.PartyState = "IN_GAME";
    await observer.Tick(default, now.AddSeconds(2)); Equal("Pitt", MapCatalog.Key(seen[^1]));
    api.CoreMatchId = null; await observer.Tick(default, now.AddSeconds(4)); Equal("Pitt", MapCatalog.Key(seen[^1]));
    api.PartyState = "DEFAULT"; api.LocalFlow = "MainMenu"; await observer.Tick(default, now.AddSeconds(6)); Equal("", seen[^1]);
    Equal(0, api.Selects); Equal(0, api.Locks);
}));
tests.Add(("start monitoring during game retrieves core map and retries unavailable data", async () => {
    var api = new Fake { MatchId = null, CoreMatchId = "core-1", PartyState = "IN_GAME" }; var seen = new List<string>(); var engine = new PickEngine(api, _ => { }, mapChanged: seen.Add);
    await engine.Tick(Config(), now, default); Equal(0, seen.Count);
    api.CoreMapId = "/Game/Maps/Port/Port"; await engine.Tick(Config(), now.AddSeconds(2), default); Equal("Port", MapCatalog.Key(seen[^1])); Equal(0, api.Locks);
    api.CoreMatchId = null; api.PartyState = null; await engine.Tick(Config(), now.AddSeconds(4), default); Equal("Port", MapCatalog.Key(seen[^1]));
    api.PartyState = "DEFAULT"; api.LocalFlow = "MainMenu"; await engine.Tick(Config(), now.AddSeconds(6), default); Equal("", seen[^1]);
}));
tests.Add(("core-game API reads match ID map and current party state", async () => {
    var transport = new Transport { PregameAvailable = false, CoregameAvailable = true }; using var api = new RiotApi(transport);
    await api.Login(new AuthResult("fake-access", "fake-id", now.AddHours(1)), "ap", default);
    var coreId = await api.GetCoreMatchId(default); Equal("22222222-2222-2222-2222-222222222222", coreId);
    Equal("/Game/Maps/Ascent/Ascent", await api.GetCoreMapId(coreId!, default)); Equal("DEFAULT", await api.GetPartyState(default));
    transport.CoregameAvailable = false; Equal<string?>(null, await api.GetCoreMatchId(default));
}));
tests.Add(("local game flow prevents transient lobby state from clearing the map", async () => {
    var api = new Fake { MapId = "/Game/Maps/Ascent/Ascent", State = "provisioned" }; var seen = new List<string>(); var engine = new PickEngine(api, _ => { }, mapChanged: seen.Add);
    await engine.Tick(Config(), now, default); api.MatchId = null; api.PartyState = "DEFAULT"; api.LocalFlow = "TransitionToInGame";
    await engine.Tick(Config(), now.AddSeconds(1), default); await engine.Tick(Config(), now.AddSeconds(60), default); Equal("Ascent", MapCatalog.Key(seen[^1]));
    api.LocalFlow = "InGame"; await engine.Tick(Config(), now.AddSeconds(62), default); Equal("Ascent", MapCatalog.Key(seen[^1]));
    api.LocalFlow = "MainMenu"; await engine.Tick(Config(), now.AddSeconds(64), default);
    await engine.Tick(Config(), now.AddSeconds(70), default); Equal("", seen[^1]);
}));
tests.Add(("game flow parser reads most recent state", () => {
    Equal("InGame", RiotApi.GameFlowStateFromLog("LogGameFlowStateManager: Broadcasting state changed to Pregame\nLogGameFlowStateManager: Broadcasting state changed to InGame"));
    Equal<string?>(null, RiotApi.GameFlowStateFromLog("no state")); return Task.CompletedTask;
}));
tests.Add(("temporary core-game outage does not clear map before lobby confirmation", async () => {
    var api = new Fake { MatchId = null, CoreMatchId = "core-1", CoreMapId = "/Game/Maps/Pitt/Pitt", PartyState = "IN_GAME" }; var seen = new List<string>(); var engine = new PickEngine(api, _ => { }, mapChanged: seen.Add);
    await engine.Tick(Config(), now, default); api.CoreMatchId = null; api.PartyState = "DEFAULT";
    await engine.Tick(Config(), now.AddSeconds(2), default); Equal("Pitt", MapCatalog.Key(seen[^1]));
    api.CoreMatchId = "core-1"; api.PartyState = "IN_GAME"; await engine.Tick(Config(), now.AddSeconds(4), default); Equal("Pitt", MapCatalog.Key(seen[^1]));
    api.CoreMatchId = null; api.PartyState = "DEFAULT"; await engine.Tick(Config(), now.AddSeconds(6), default); Equal("Pitt", MapCatalog.Key(seen[^1]));
    await engine.Tick(Config(), now.AddSeconds(10), default); Equal("", seen[^1]);
}));
tests.Add(("core-game rejects a different match and retries absent map", async () => {
    var transport = new Transport { CoregameAvailable = true }; using var api = new RiotApi(transport);
    await api.Login(new AuthResult("fake-access", "fake-id", now.AddHours(1)), "ap", default);
    const string id = "22222222-2222-2222-2222-222222222222";
    transport.MatchResponse = """{"MatchID":"33333333-3333-3333-3333-333333333333","MapID":"/Game/Maps/Port/Port"}""";
    try { await api.GetCoreMapId(id, default); throw new Exception("Accepted a different match"); } catch (InvalidDataException) { }
    transport.MatchResponse = """{"MatchID":"22222222-2222-2222-2222-222222222222","MapID":""}""";
    Equal<string?>(null, await api.GetCoreMapId(id, default));
}));
tests.Add(("idle never selects", async () => { var api = new Fake { MatchId = null }; await new PickEngine(api, _ => { }).Tick(Config(), now, default); Equal(0, api.Selects); }));
tests.Add(("two consecutive matches and duplicate suppression", async () =>
{
    var api = new Fake(); var engine = new PickEngine(api, _ => { });
    await engine.Tick(Config(), now, default); Equal(1, api.Locks);
    api.SelfState = ""; await engine.Tick(Config(), now.AddMilliseconds(100), default); Equal(1, api.Locks);
    api.MatchId = null; await engine.Tick(Config(), now.AddMilliseconds(200), default);
    api.MatchId = "match-2"; await engine.Tick(Config(), now.AddMilliseconds(300), default); Equal(2, api.Locks);
    api.MatchId = "match-1"; api.SelfState = ""; await engine.Tick(Config(), now.AddMilliseconds(400), default); Equal(2, api.Locks);
}));
tests.Add(("agent is selected and locked immediately when selection becomes active", async () =>
{
    var api = new Fake { State = "provisioned" }; var engine = new PickEngine(api, _ => { });
    await engine.Tick(Config(), now, default); Equal(0, api.Selects); Equal(0, api.Locks);
    api.State = "character_select_active";
    await engine.Tick(Config(), now.AddMilliseconds(1), default); Equal(1, api.Selects); Equal(1, api.Locks);
    await engine.Tick(Config(), now.AddMilliseconds(2), default); Equal(1, api.Selects); Equal(1, api.Locks);
}));
tests.Add(("pick waits until the game has entered the selection screen", async () =>
{
    var ready = false; var api = new Fake();
    var engine = new PickEngine(api, _ => { }, (_, _) => Task.FromResult<bool?>(ready));
    await engine.Tick(Config(), now, default); Equal(0, api.Selects); Equal(0, api.Locks);
    await engine.Tick(Config(), now.AddSeconds(20), default); Equal(0, api.Selects); Equal(0, api.Locks);
    ready = true;
    await engine.Tick(Config(), now.AddSeconds(20).AddMilliseconds(250), default); Equal(1, api.Selects); Equal(1, api.Locks);
}));
tests.Add(("unavailable local readiness uses a bounded fallback", async () =>
{
    var api = new Fake(); var engine = new PickEngine(api, _ => { }, (_, _) => Task.FromResult<bool?>(null));
    await engine.Tick(Config(), now, default); Equal(0, api.Selects);
    await engine.Tick(Config(), now.AddSeconds(11.9), default); Equal(0, api.Selects);
    await engine.Tick(Config(), now.AddSeconds(12.1), default); Equal(1, api.Locks);
}));
tests.Add(("game log readiness requires the current match and entered Pregame state", () =>
{
    const string id = "22222222-2222-2222-2222-222222222222";
    var initial = $"RMS /pregame/v1/matches/{id}\nLogGameFlowStateManager: Broadcasting state changed to TransitionToPregame";
    Equal<bool?>(false, RiotApi.CharacterSelectReadyFromLog(initial, id));
    var ready = initial + "\nLogGameFlowStateManager: [PlatformTime: 42] Broadcasting state changed to Pregame";
    Equal<bool?>(true, RiotApi.CharacterSelectReadyFromLog(ready, id));
    Equal<bool?>(false, RiotApi.CharacterSelectReadyFromLog(ready, "33333333-3333-3333-3333-333333333333"));
    Equal<bool?>(false, RiotApi.CharacterSelectReadyFromLog(ready + "\nLogGameFlowStateManager: Broadcasting state changed to TransitionToInGame", id));
    Equal<bool?>(null, RiotApi.CharacterSelectReadyFromLog("no state", id));
    return Task.CompletedTask;
}));
tests.Add(("a teammate picking the configured agent requires manual action", async () =>
{
    var api = new Fake { Others = [new("other", "agent-a", "locked")] };
    var engine = new PickEngine(api, _ => { });
    await engine.Tick(Config(), now, default); Equal(0, api.Selects); Equal(0, api.Locks);
}));
tests.Add(("taken agents are retried when freed", async () =>
{
    var api = new Fake { Others = [new("other", "agent-a", "locked")] }; var config = Config();
    var engine = new PickEngine(api, _ => { }); await engine.Tick(config, now, default); Equal(0, api.Selects);
    api.Others = []; await engine.Tick(config, now.AddMilliseconds(1), default); Equal(1, api.Locks);
}));
tests.Add(("manual lock is preserved", async () => { var api = new Fake { SelfState = "locked", Selected = "manual" }; var engine = new PickEngine(api, _ => { }); await engine.Tick(Config(), now, default); Equal(0, api.Selects); Equal("manual", api.Selected); }));
tests.Add(("select and lock on first active match check", async () => { var api = new Fake(); var engine = new PickEngine(api, _ => { }); await engine.Tick(Config(), now, default); Equal(1, api.Selects); Equal(1, api.Locks); }));
tests.Add(("inactive or missing self never writes", async () =>
{
    var api = new Fake { State = "provisioned" }; var engine = new PickEngine(api, _ => { }); await engine.Tick(Config(), now, default); Equal(0, api.Selects);
    api.State = "character_select_active"; api.IncludeSelf = false; await engine.Tick(Config(), now.AddMilliseconds(1), default); Equal(0, api.Selects);
}));
tests.Add(("rejected agent is reported without selecting a different agent", async () =>
{
    var api = new Fake { RejectAgent = "agent-a" }; var engine = new PickEngine(api, _ => { });
    await engine.Tick(Config(), now, default); Equal("", api.Selected); Equal(0, api.Locks); Equal(1, api.Selects);
    for (var i = 1; i <= 7; i++) await engine.Tick(Config(), now.AddMilliseconds(i), default);
    Equal(6, api.Selects); Equal(0, api.Locks);
}));
tests.Add(("selection is retried after a temporary rejection as the screen opens", async () =>
{
    var api = new Fake { RejectAgent = "agent-a" }; var engine = new PickEngine(api, _ => { });
    await engine.Tick(Config(), now, default); Equal(0, api.Locks);
    api.RejectAgent = null;
    await engine.Tick(Config(), now.AddMilliseconds(1), default); Equal(1, api.Locks);
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
    await engine.Tick(Config(), now.AddMilliseconds(1), default); Equal(1, api.Locks);
}));
tests.Add(("write retries are bounded", async () =>
{
    var api = new Fake { SelectError = 500 }; var engine = new PickEngine(api, _ => { });
    for (var i = 0; i < 12; i++) try { await engine.Tick(Config(), now.AddMilliseconds(i * 10), default); } catch (ApiException) { }
    Equal(6, api.Selects); Equal(0, api.Locks);
}));
tests.Add(("rate limit is surfaced for worker backoff", async () =>
{
    var api = new Fake { SelectError = 429 }; var engine = new PickEngine(api, _ => { });
    try { await engine.Tick(Config(), now, default); throw new Exception("Expected rate limit"); }
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
tests.Add(("party notification checks immediately and joins only after returning to an eligible lobby", async () =>
{
    var api = new FakeQueue(); var engine = new AutoQueueEngine(api, _ => { });
    api.Snapshot = api.Snapshot with { State = "IN_GAME" };
    await engine.Tick(now, default); Equal(0, api.Joins);
    await engine.Tick(now.AddMilliseconds(50), default, stateChanged: true); Equal(0, api.Joins);
    api.Snapshot = api.Snapshot with { State = "DEFAULT" };
    await engine.Tick(now.AddMilliseconds(100), default); Equal(0, api.Joins);
    await engine.Tick(now.AddMilliseconds(100), default, stateChanged: true); Equal(1, api.Joins);
    api.Snapshot = api.Snapshot with { State = "DEFAULT" }; // A stale update must not send the join twice.
    await engine.Tick(now.AddMilliseconds(200), default, stateChanged: true); Equal(1, api.Joins);
}));
tests.Add(("stopping auto queue while fetching state prevents a notification-triggered join", async () =>
{
    var enabled = true;
    var api = new FakeQueue { OnSnapshot = () => enabled = false };
    await new AutoQueueEngine(api, _ => { }).Tick(now, default, stateChanged: true, enabled: () => enabled);
    Equal(0, api.Joins);
}));
tests.Add(("only relevant local events wake the queue monitor", () =>
{
    const string subject = "11111111-1111-1111-1111-111111111111";
    string? lastPresence = null;
    byte[] Frame(string uri, object data) => System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new object[] { 8, "OnJsonApiEvent", new { uri, data } });
    string Encode(string state) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{{\"sessionLoopState\":\"{state}\",\"partyState\":\"DEFAULT\"}}"));
    var other = Frame("/chat/v4/presences", new { puuid = "22222222-2222-2222-2222-222222222222", product = "valorant", @private = Encode("MENUS") });
    Equal(RiotLocalEvents.Notification.None, RiotLocalEvents.Classify(other, subject, ref lastPresence));
    var own = Frame("/chat/v4/presences", new { puuid = subject, product = "valorant", @private = Encode("INGAME") });
    Equal(RiotLocalEvents.Notification.QueueChanged, RiotLocalEvents.Classify(own, subject, ref lastPresence));
    Equal(RiotLocalEvents.Notification.None, RiotLocalEvents.Classify(own, subject, ref lastPresence));
    var lobby = Frame("/chat/v4/presences", new { puuid = subject, product = "valorant", @private = Encode("MENUS") });
    Equal(RiotLocalEvents.Notification.QueueChanged, RiotLocalEvents.Classify(lobby, subject, ref lastPresence));
    var party = Frame("/riot-messaging-service/v1/message/ares-parties/parties/v1/parties/id", new { });
    Equal(RiotLocalEvents.Notification.QueueChanged, RiotLocalEvents.Classify(party, subject, ref lastPresence));
    var relativeParty = Frame("/riot-messaging-service/v1/message", new { resource = "parties/v1/parties/id" });
    Equal(RiotLocalEvents.Notification.QueueChanged, RiotLocalEvents.Classify(relativeParty, subject, ref lastPresence));
    return Task.CompletedTask;
}));
tests.Add(("nested current client presence wakes queue monitoring when the match ends", () =>
{
    const string subject = "11111111-1111-1111-1111-111111111111";
    string? lastPresence = null;
    byte[] Frame(string phase, string party) => System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new object[]
    {
        8, "OnJsonApiEvent", new { uri = "/chat/v4/presences", data = new { puuid = subject, product = "valorant",
            @private = Convert.ToBase64String(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
            { matchPresenceData = new { sessionLoopState = phase }, partyPresenceData = new { partyState = party } })) } }
    });
    Equal(RiotLocalEvents.Notification.QueueChanged, RiotLocalEvents.Classify(Frame("INGAME", "DEFAULT"), subject, ref lastPresence));
    Equal(RiotLocalEvents.Notification.None, RiotLocalEvents.Classify(Frame("INGAME", "DEFAULT"), subject, ref lastPresence));
    Equal(RiotLocalEvents.Notification.QueueChanged, RiotLocalEvents.Classify(Frame("MENUS", "DEFAULT"), subject, ref lastPresence));
    Equal(RiotLocalEvents.Notification.QueueChanged, RiotLocalEvents.Classify(Frame("MENUS", "MATCHMAKING"), subject, ref lastPresence));
    var malformed = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new object[] { 8, "OnJsonApiEvent", new { uri = "/chat/v4/presences", data = new { puuid = subject, product = "valorant", @private = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("[]")) } } });
    Equal(RiotLocalEvents.Notification.None, RiotLocalEvents.Classify(malformed, subject, ref lastPresence));
    return Task.CompletedTask;
}));
tests.Add(("lobby party state does not cause queue entry during an active match", async () =>
{
    var api = new FakeQueue { InMatch = true };
    var engine = new AutoQueueEngine(api, _ => { });
    await engine.Tick(now, default); Equal(0, api.Joins);
    api.InMatch = false;
    await engine.Tick(now.AddSeconds(1), default, stateChanged: true); Equal(1, api.Joins);
}));
tests.Add(("stopping auto queue while checking match state prevents joining", async () =>
{
    var enabled = true;
    var api = new FakeQueue { OnMatchCheck = () => enabled = false };
    await new AutoQueueEngine(api, _ => { }).Tick(now, default, enabled: () => enabled);
    Equal(0, api.Joins);
}));
tests.Add(("LAN relay authenticates the account and forwards a state change", async () =>
{
    const string subject = "11111111-1111-1111-1111-111111111111";
    var code = RelayProtocol.NewCode();
    Equal(true, RelayProtocol.TryParseCode(code, out var key));
    Equal(false, RelayProtocol.TryParseCode("1234", out _));
    using var server = new NotificationRelayServer(System.Net.IPAddress.Loopback, 0, key);
    server.SetSubject(subject); server.Start();
    using var cts = new CancellationTokenSource();
    var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var next = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var events = 0; var connected = false;
    var listener = NotificationRelayClient.Listen(new RelayOptions(System.Net.IPAddress.Loopback, key, server.Port), () => subject,
        () => { if (Interlocked.Increment(ref events) == 1) first.TrySetResult(); else next.TrySetResult(); },
        state => { if (state) connected = true; else if (connected) disconnected.TrySetResult(); }, cts.Token);
    await first.Task.WaitAsync(TimeSpan.FromSeconds(3));
    server.Publish(subject);
    await next.Task.WaitAsync(TimeSpan.FromSeconds(3));
    server.SetSubject("22222222-2222-2222-2222-222222222222");
    await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(3));
    cts.Cancel();
    try { await listener; } catch (OperationCanceledException) { }
}));
tests.Add(("LAN relay proof rejects a different account or pairing code", () =>
{
    var nonce1 = new byte[32]; var nonce2 = new byte[32];
    var key = new byte[16]; var wrongKey = new byte[16]; wrongKey[0] = 1;
    var proof = RelayProtocol.Mac(key, "server", nonce1, nonce2, "11111111-1111-1111-1111-111111111111");
    Equal(false, System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(proof, RelayProtocol.Mac(wrongKey, "server", nonce1, nonce2, "11111111-1111-1111-1111-111111111111")));
    Equal(false, System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(proof, RelayProtocol.Mac(key, "server", nonce1, nonce2, "22222222-2222-2222-2222-222222222222")));
    return Task.CompletedTask;
}));
tests.Add(("LAN relay closes a connection with an incorrect code", async () =>
{
    const string subject = "11111111-1111-1111-1111-111111111111";
    var key = new byte[16]; var wrongKey = new byte[16]; wrongKey[0] = 1;
    using var server = new NotificationRelayServer(System.Net.IPAddress.Loopback, 0, key);
    server.SetSubject(subject); server.Start();
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
    using var client = new System.Net.Sockets.TcpClient();
    await client.ConnectAsync(System.Net.IPAddress.Loopback, server.Port, timeout.Token);
    var stream = client.GetStream(); var clientNonce = new byte[32]; var serverNonce = new byte[32]; var proof = new byte[32];
    await stream.WriteAsync(clientNonce, timeout.Token);
    await stream.ReadExactlyAsync(serverNonce, timeout.Token); await stream.ReadExactlyAsync(proof, timeout.Token);
    await stream.WriteAsync(RelayProtocol.Mac(wrongKey, "client", clientNonce, serverNonce, subject), timeout.Token);
    Equal(0, await stream.ReadAsync(new byte[1], timeout.Token));
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
tests.Add(("system browser login accepts only its matching loopback callback", async () =>
{
    var state = AuthFlow.NewState();
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    var session = new SystemBrowserAuth(state, 0);
    var result = await session.Run(async (url, port) =>
    {
        Equal("auth.riotgames.com", url.Host);
        Equal(true, Uri.UnescapeDataString(url.Query).Contains("redirect_uri=http://localhost/redirect"));
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false });
        var baseUrl = $"http://localhost:{port}";
        using var page = await client.GetAsync(baseUrl + "/redirect", timeout.Token);
        Equal(System.Net.HttpStatusCode.OK, page.StatusCode);
        var pageText = await page.Content.ReadAsStringAsync(timeout.Token);
        Equal(true, pageText.Contains("location.hash"));
        using var invalid = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/auth-result");
        invalid.Headers.Add("Origin", baseUrl);
        invalid.Content = new StringContent("#access_token=bad&id_token=bad&expires_in=3600&token_type=Bearer&state=wrong");
        using var refused = await client.SendAsync(invalid, timeout.Token);
        Equal(System.Net.HttpStatusCode.BadRequest, refused.StatusCode);
        using var foreign = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/auth-result");
        foreign.Headers.Add("Origin", "https://unrelated.example");
        foreign.Content = new StringContent($"#access_token=bad&id_token=bad&expires_in=3600&token_type=Bearer&state={state}");
        try { using var ignored = await client.SendAsync(foreign, timeout.Token); throw new Exception("Accepted unrelated origin"); }
        catch (HttpRequestException) { }
        using var valid = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/auth-result");
        valid.Headers.Add("Origin", baseUrl);
        valid.Content = new StringContent($"#access_token=test-access&id_token=test-id&expires_in=3600&token_type=Bearer&state={state}");
        using var accepted = await client.SendAsync(valid, timeout.Token);
        Equal(System.Net.HttpStatusCode.OK, accepted.StatusCode);
    }, timeout.Token);
    Equal("test-access", result?.AccessToken);
}));
tests.Add(("canceling browser login releases its local listener", async () =>
{
    var portReady = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
    using var stop = new CancellationTokenSource();
    var run = new SystemBrowserAuth(AuthFlow.NewState(), 0).Run((_, port) => { portReady.TrySetResult(port); return Task.CompletedTask; }, stop.Token);
    var port = await portReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
    stop.Cancel();
    try { await run; throw new Exception("Expected cancellation"); } catch (OperationCanceledException) { }
    var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
    try { listener.Start(); } finally { listener.Stop(); }
}));
tests.Add(("browser callback only uses loopback addresses", async () =>
{
    Equal(true, SystemBrowserAuth.SafeLocalAddresses([System.Net.IPAddress.Loopback, System.Net.IPAddress.IPv6Loopback]));
    Equal(false, SystemBrowserAuth.SafeLocalAddresses([]));
    Equal(false, SystemBrowserAuth.SafeLocalAddresses([System.Net.IPAddress.Loopback, System.Net.IPAddress.Parse("192.0.2.1")]));
    if (!System.Net.Sockets.Socket.OSSupportsIPv6) return;
    var blocker = new System.Net.Sockets.TcpListener(System.Net.IPAddress.IPv6Loopback, 0) { ExclusiveAddressUse = true };
    blocker.Start();
    try
    {
        var port = ((System.Net.IPEndPoint)blocker.LocalEndpoint).Port;
        var opened = false;
        try
        {
            await new SystemBrowserAuth(AuthFlow.NewState(), port).Run((_, _) => { opened = true; return Task.CompletedTask; }, CancellationToken.None);
            throw new Exception("Started without securing both loopback addresses");
        }
        catch (System.Net.Sockets.SocketException) { }
        Equal(false, opened);
    }
    finally { blocker.Stop(); }
}));
tests.Add(("system browser callback validates state on OAuth errors", () =>
{
    var state = AuthFlow.NewState();
    Equal<AuthResult?>(null, AuthFlow.ParseSystemBrowserFragment($"#error=access_denied&state={state}", state, now));
    try { AuthFlow.ParseSystemBrowserFragment("#error=access_denied&state=wrong", state, now); throw new Exception("Accepted wrong state"); }
    catch (InvalidDataException) { }
    return Task.CompletedTask;
}));
tests.Add(("default browser login uses a separate private profile", () =>
{
    var browser = @"C:\Program Files\BraveSoftware\Brave-Browser\Application\brave.exe";
    Equal(browser, IsolatedBrowser.ExtractExecutable('"' + browser + "\" --single-argument %1"));
    var profile = Path.Combine(Path.GetTempPath(), "ValoAutoPick-PrivateLogin", Guid.NewGuid().ToString("N"));
    var url = new Uri("https://auth.riotgames.com/authorize?state=test");
    var start = IsolatedBrowser.CreateStartInfo(browser, profile, url);
    Equal(false, start.UseShellExecute);
    Equal(browser, start.FileName);
    Equal(true, start.ArgumentList.Contains("--user-data-dir=" + profile));
    Equal(true, start.ArgumentList.Contains("--incognito"));
    Equal(true, start.ArgumentList.Contains("--disable-sync"));
    Equal(url.AbsoluteUri, start.ArgumentList[^1]);
    var edge = IsolatedBrowser.CreateStartInfo(@"C:\Program Files\Microsoft\Edge\Application\msedge.exe", profile, url);
    Equal(true, edge.ArgumentList.Contains("--inprivate"));
    var firefox = IsolatedBrowser.CreateStartInfo(@"C:\Program Files\Mozilla Firefox\firefox.exe", profile, url);
    Equal(true, firefox.ArgumentList.Contains("-no-remote"));
    Equal(true, firefox.ArgumentList.Contains("-private-window"));
    Equal(true, firefox.ArgumentList.Contains(profile));
    try { IsolatedBrowser.CreateStartInfo(@"C:\Other\unknown.exe", profile, url); throw new Exception("Accepted an unsupported browser"); }
    catch (BrowserIsolationException) { }
    return Task.CompletedTask;
}));
tests.Add(("private browser cleanup is restricted to a generated profile", () =>
{
    var root = Path.Combine(Path.GetTempPath(), "ValoAutoPick-PrivateLogin");
    Equal(true, IsolatedBrowser.IsOwnedProfilePath(root, Path.Combine(root, Guid.NewGuid().ToString("N"))));
    Equal(false, IsolatedBrowser.IsOwnedProfilePath(root, root));
    Equal(false, IsolatedBrowser.IsOwnedProfilePath(root, Path.Combine(root, "not-a-generated-profile")));
    Equal(false, IsolatedBrowser.IsOwnedProfilePath(root, Path.Combine(root, "..", Guid.NewGuid().ToString("N"))));
    return Task.CompletedTask;
}));
tests.Add(("local account token expiry is read without accepting malformed tokens", () =>
{
    var payload = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("{\"exp\":2000000000}"))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    Equal<DateTimeOffset?>(DateTimeOffset.FromUnixTimeSeconds(2000000000), RiotApi.TokenExpiry("a." + payload + ".b"));
    Equal<DateTimeOffset?>(null, RiotApi.TokenExpiry("not-a-jwt"));
    Equal<DateTimeOffset?>(null, RiotApi.TokenExpiry("a.invalid.b"));
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
    Equal(true, Storage.Parse("{\"SystemBrowserLogin\":true}").SystemBrowserLogin);
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
tests.Add(("a previous queue timestamp does not prevent requeueing from the lobby", async () =>
{
    var transport = new Transport { QueueEntryTime = "2026-11-05T12:00:00Z", PregameAvailable = false };
    using var api = new RiotApi(transport);
    await api.Login(new AuthResult("fake-access", "fake-id", now.AddHours(1)), "auto", default);
    var engine = new AutoQueueEngine(api, _ => { });
    Equal(false, (await api.GetQueueSnapshot(default)).IsInQueue);
    await engine.Tick(now, default);
    Equal(1, transport.Calls.Count(call => call.Uri.AbsolutePath.EndsWith("/matchmaking/join")));
    transport.PartyResponse = """{"ID":"44444444-4444-4444-4444-444444444444","State":"MATCHMAKING","Members":[{"Subject":"11111111-1111-1111-1111-111111111111","IsOwner":true}],"MatchmakingData":{"QueueID":"competitive"},"QueueEntryTime":"2026-11-05T12:00:00Z","RestrictedSeconds":0,"EligibleQueues":["competitive"],"QueueIneligibilities":[]}""";
    Equal(true, (await api.GetQueueSnapshot(default)).IsInQueue);
    await engine.Tick(now.AddSeconds(3), default);
    Equal(1, transport.Calls.Count(call => call.Uri.AbsolutePath.EndsWith("/matchmaking/join")));
}));
tests.Add(("queue match checks cover in-game, pregame, and idle", async () =>
{
    var transport = new Transport { CoregameAvailable = true, PregameAvailable = false };
    using var api = new RiotApi(transport);
    await api.Login(new AuthResult("fake-access", "fake-id", now.AddHours(1)), "auto", default);
    Equal(true, await api.IsInMatch(default));
    transport.CoregameAvailable = false; transport.PregameAvailable = true;
    Equal(true, await api.IsInMatch(default));
    transport.PregameAvailable = false;
    Equal(false, await api.IsInMatch(default));
    transport.GameStatus = 403;
    try { await api.IsInMatch(default); throw new Exception("Expected failure instead of an idle state"); }
    catch (ApiException e) { Equal(403, e.Status); }
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
    var snapshot = await api.GetMatch(match!, default); Equal(1, snapshot.Allies.Count); Equal("/Game/Maps/Ascent/Ascent", snapshot.MapId);
    await api.Select(match!, "33333333-3333-3333-3333-333333333333", default);
    Equal(HttpMethod.Post, transport.Calls[^1].Method);
    await api.Lock(match!, "33333333-3333-3333-3333-333333333333", default);
    Equal(true, transport.Calls[^1].Uri.AbsolutePath.Contains("/lock/"));
    var queue = await api.GetQueueSnapshot(default);
    Equal("44444444-4444-4444-4444-444444444444", queue.PartyId); Equal("competitive", queue.QueueId);
    Equal(true, queue.IsLeader); Equal(true, queue.EligibleQueues.Contains("competitive")); Equal(false, queue.IsInQueue);
    transport.QueueEntryTime = "2026-11-05T12:00:00Z";
    Equal(false, (await api.GetQueueSnapshot(default)).IsInQueue);
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
    public string? MatchId = "match-1", CoreMatchId, CoreMapId;
    public string? PartyState = "DEFAULT", LocalFlow;
    public string State = "character_select_active", SelfState = "", Selected = "", MapId = "";
    public bool IncludeSelf = true, LoseLockResponse;
    public List<Player> Others = [];
    public string? RejectAgent;
    public int Selects, Locks, SelectError;
    public Action? AfterSelect;
    public Task<string?> GetMatchId(CancellationToken ct) { ct.ThrowIfCancellationRequested(); return Task.FromResult(MatchId); }
    public Task<string?> GetCoreMatchId(CancellationToken ct) { ct.ThrowIfCancellationRequested(); return Task.FromResult(CoreMatchId); }
    public Task<string?> GetCoreMapId(string id, CancellationToken ct) { ct.ThrowIfCancellationRequested(); return Task.FromResult(CoreMapId); }
    public Task<string?> GetPartyState(CancellationToken ct) { ct.ThrowIfCancellationRequested(); return Task.FromResult(PartyState); }
    public Task<string?> GetLocalGameFlowState(CancellationToken ct) { ct.ThrowIfCancellationRequested(); return Task.FromResult(LocalFlow); }
    public Task<MatchSnapshot> GetMatch(string id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); var players = new List<Player>(Others);
        if (IncludeSelf) players.Add(new(Subject, Selected, SelfState));
        return Task.FromResult(new MatchSnapshot(MatchId!, State, players, MapId));
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
    public bool InMatch;
    public string JoinedParty = "";
    public Action? OnSnapshot, OnMatchCheck;
    public Task<PartyQueueSnapshot> GetQueueSnapshot(CancellationToken ct) { ct.ThrowIfCancellationRequested(); OnSnapshot?.Invoke(); return Task.FromResult(Snapshot); }
    public Task<bool> IsInMatch(CancellationToken ct) { ct.ThrowIfCancellationRequested(); OnMatchCheck?.Invoke(); return Task.FromResult(InMatch); }
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
    public bool PregameAvailable = true, CoregameAvailable;
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
            _ when req.RequestUri.AbsolutePath.Contains("/core-game/v1/matches/") => MatchResponse ?? """{"MatchID":"22222222-2222-2222-2222-222222222222","MapID":"/Game/Maps/Ascent/Ascent"}""",
            _ when req.RequestUri.AbsolutePath.Contains("/players/") => """{"MatchID":"22222222-2222-2222-2222-222222222222"}""",
            _ => MatchResponse ?? """{"ID":"22222222-2222-2222-2222-222222222222","PregameState":"character_select_active","MapID":"/Game/Maps/Ascent/Ascent","AllyTeam":{"Players":[{"Subject":"11111111-1111-1111-1111-111111111111","CharacterID":"","CharacterSelectionState":""}]}}"""
        };
        var status = req.RequestUri.Host.EndsWith(".pvp.net") ? GameStatus : 200;
        if (status == 200 && req.RequestUri.AbsolutePath.Contains("/core-game/v1/players/") && !CoregameAvailable) status = 404;
        if (status == 200 && req.RequestUri.AbsolutePath.Contains("/pregame/v1/players/") && !PregameAvailable) status = 404;
        var res = new HttpResponseMessage((System.Net.HttpStatusCode)status) { Content = new StringContent(json) };
        if (status == 429) res.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(17));
        return Task.FromResult(res);
    }
}
