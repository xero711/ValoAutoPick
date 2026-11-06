using System.Diagnostics;

namespace ValoAutoPick;

// One serial worker owns this state. No overlapping polling or write requests.
public sealed class PickEngine(IGameApi api, Action<string> report, Func<string, CancellationToken, Task<bool?>>? selectionReady = null, Action<string>? mapChanged = null)
{
    private static readonly TimeSpan UnknownReadinessDelay = TimeSpan.FromSeconds(12);
    private readonly HashSet<string> completed = [];
    private readonly Queue<string> history = [];
    private string? current;
    private string lastMapId = "";
    private readonly MapLifecycle maps = new(api, mapChanged);
    private DateTimeOffset fallbackPickAt;
    private int attempts;

    private void Complete(string id)
    {
        if (!completed.Add(id)) return;
        history.Enqueue(id);
        if (history.Count > 100) completed.Remove(history.Dequeue());
    }

    public async Task Tick(Settings settings, DateTimeOffset now, CancellationToken ct)
    {
        var pollStarted = Stopwatch.GetTimestamp();
        var id = await api.GetMatchId(ct);
        var observedAt = now + Stopwatch.GetElapsedTime(pollStarted);
        if (id == null)
        {
            await maps.ObserveWithoutPregame(observedAt, ct);
            if (!maps.HasMatch) current = null;
            report(maps.InCoreGame ? "試合中 · マップを表示中" : maps.HasMatch ? "試合の状態を確認中" : "マッチを検索中");
            return;
        }
        if (completed.Contains(id)) { if (current == id && lastMapId.Length > 0) mapChanged?.Invoke(lastMapId); report("この試合の処理は完了 · 次の試合も自動で検知します"); return; }
        if (current != id)
        {
            current = id; lastMapId = ""; attempts = 0; fallbackPickAt = observedAt + UnknownReadinessDelay;
            maps.StartPregame(id);
            report("マッチを検知 · 選択画面を確認中");
        }

        var match = await api.GetMatch(id, ct);
        if (match.Id != id) return;
        lastMapId = match.MapId;
        maps.SetPregameMap(match.MapId);
        if (match.State != "character_select_active") return;
        var self = match.Allies.FirstOrDefault(p => p.Subject == api.Subject);
        if (self == null) { report("参加情報を取得中"); return; }
        if (self.CharacterSelectionState == "locked")
        {
            Complete(id); report("選択確定済み · 次のマッチを検索中"); return;
        }
        var taken = match.Allies.Where(p => p.Subject != api.Subject && p.CharacterSelectionState is "selected" or "locked")
            .Select(p => p.CharacterID).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var mapKey = MapCatalog.Key(match.MapId);
        if (mapKey.Length == 0 && settings.MapAgents.Count > 0) { report("マップ情報を取得中"); return; }
        var agent = settings.MapAgents.TryGetValue(mapKey, out var configured) ? configured : settings.AgentId;
        if (string.IsNullOrWhiteSpace(agent)) { report("エージェントを設定してください"); return; }
        if (taken.Contains(agent)) { report("設定したエージェントは選択済みです · 手動で選択してください"); return; }
        if (selectionReady != null)
        {
            var ready = await selectionReady(id, ct);
            observedAt = now + Stopwatch.GetElapsedTime(pollStarted);
            if (ready == false || ready == null && observedAt < fallbackPickAt)
            {
                report("選択画面の読み込みを待っています");
                return;
            }
        }
        if (attempts >= 6) { Complete(id); report("この試合の再試行を終了 · ゲーム内で選択してください"); return; }
        ct.ThrowIfCancellationRequested();
        attempts++;
        try
        {
            if (!string.Equals(self.CharacterID, agent, StringComparison.OrdinalIgnoreCase) || self.CharacterSelectionState != "selected")
                await api.Select(id, agent, ct);
            ct.ThrowIfCancellationRequested();
            await api.Lock(id, agent, ct);
            var confirmed = await api.GetMatch(id, ct);
            var locked = confirmed.Allies.FirstOrDefault(p => p.Subject == api.Subject);
            if (locked?.CharacterSelectionState != "locked") { report("確定結果を再確認します"); return; }
            Complete(id);
            report("自動ピック・確定を確認しました");
        }
        catch (ApiException e) when (e.Status is 400 or 409)
        {
            report("選択状態を確認して再試行します");
        }
    }
}

// Share match lifecycle handling between auto-pick and queue-only monitoring.
// A missing pregame response is normal during the transition to core game.
public sealed class MapLifecycle(IGameApi api, Action<string>? mapChanged)
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan PregameExitGrace = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LobbyConfirmationDelay = TimeSpan.FromSeconds(3);
    private string? pregameId, coreId;
    private string mapId = "";
    private DateTimeOffset nextCoreCheck, lobbySince;
    private bool coreMapPending;
    public bool HasMatch => pregameId != null || coreId != null;
    public bool InCoreGame { get; private set; }

    public void StartPregame(string id)
    {
        if (pregameId == id) return;
        pregameId = id; coreId = null; mapId = ""; InCoreGame = false;
        nextCoreCheck = DateTimeOffset.MinValue; lobbySince = DateTimeOffset.MinValue;
        coreMapPending = false; mapChanged?.Invoke("");
    }
    public void SetPregameMap(string map)
    {
        if (string.IsNullOrWhiteSpace(map)) return;
        mapId = map; mapChanged?.Invoke(map);
    }
    public async Task ObserveWithoutPregame(DateTimeOffset now, CancellationToken ct)
    {
        if (now < nextCoreCheck) return;
        var id = await api.GetCoreMatchId(ct);
        nextCoreCheck = now + CheckInterval;
        if (id != null)
        {
            if (!string.Equals(coreId, id, StringComparison.OrdinalIgnoreCase))
            {
                coreId = id;
                coreMapPending = string.IsNullOrWhiteSpace(mapId) || !string.Equals(pregameId, id, StringComparison.OrdinalIgnoreCase);
            }
            InCoreGame = true; lobbySince = DateTimeOffset.MinValue;
            if (coreMapPending)
            {
                var map = await api.GetCoreMapId(id, ct);
                if (!string.IsNullOrWhiteSpace(map)) { mapId = map; coreMapPending = false; mapChanged?.Invoke(map); }
            }
            else if (!string.IsNullOrWhiteSpace(mapId)) mapChanged?.Invoke(mapId);
            return;
        }
        if (!HasMatch) return;
        var state = await api.GetPartyState(ct);
        if (state is not ("DEFAULT" or "MATCHMAKING")) { lobbySince = DateTimeOffset.MinValue; return; }
        var localState = await api.GetLocalGameFlowState(ct);
        // A matching local client is authoritative during screen transitions, even if
        // both remote endpoints briefly return 404 or party state lags behind.
        if (localState is "TransitionToPregame" or "Pregame" or "TransitionToInGame" or "InGame")
        { lobbySince = DateTimeOffset.MinValue; return; }
        if (lobbySince == DateTimeOffset.MinValue) lobbySince = now;
        if (!InCoreGame && now - lobbySince < PregameExitGrace) return;
        if (InCoreGame && localState != "MainMenu" && now - lobbySince < LobbyConfirmationDelay) return;
        pregameId = coreId = null; mapId = ""; InCoreGame = false; coreMapPending = false;
        lobbySince = DateTimeOffset.MinValue; mapChanged?.Invoke("");
    }
}

// Observe matches without issuing select or lock requests when only auto-queue is enabled.
public sealed class MapObserver(IGameApi api, Action<string> mapChanged)
{
    private readonly MapLifecycle maps = new(api, mapChanged);
    private string? current;
    public async Task Tick(CancellationToken ct, DateTimeOffset? now = null)
    {
        var id = await api.GetMatchId(ct);
        if (id == null)
        {
            await maps.ObserveWithoutPregame(now ?? DateTimeOffset.UtcNow, ct);
            if (!maps.HasMatch) current = null;
            return;
        }
        if (id != current) { current = id; maps.StartPregame(id); }
        var match = await api.GetMatch(id, ct);
        if (match.Id == id) maps.SetPregameMap(match.MapId);
    }
}
