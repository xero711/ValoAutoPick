using System.Diagnostics;

namespace ValoAutoPick;

// One serial worker owns this state. No overlapping polling or write requests.
public sealed class PickEngine(IGameApi api, Action<string> report)
{
    private static readonly TimeSpan PickDelay = TimeSpan.FromSeconds(3);
    private readonly HashSet<string> completed = [];
    private readonly Queue<string> history = [];
    private string? current;
    private DateTimeOffset pickAt;
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
        if (id == null) { report("マッチを検索中"); return; }
        if (completed.Contains(id)) { report("この試合の処理は完了 · 次の試合も自動で検知します"); return; }
        if (current != id)
        {
            current = id; attempts = 0; pickAt = observedAt + PickDelay;
            report("マッチを検知 · 3秒後に自動ピックします");
        }
        if (observedAt < pickAt) return;

        var match = await api.GetMatch(id, ct);
        if (match.Id != id || match.State != "character_select_active") return;
        var self = match.Allies.FirstOrDefault(p => p.Subject == api.Subject);
        if (self == null) { report("参加情報を取得中"); return; }
        if (self.CharacterSelectionState == "locked")
        {
            Complete(id); report("選択確定済み · 次のマッチを検索中"); return;
        }
        var taken = match.Allies.Where(p => p.Subject != api.Subject && p.CharacterSelectionState is "selected" or "locked")
            .Select(p => p.CharacterID).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var agent = settings.AgentId;
        if (string.IsNullOrWhiteSpace(agent)) { report("エージェントを設定してください"); return; }
        if (taken.Contains(agent)) { report("設定したエージェントは選択済みです · 手動で選択してください"); return; }
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
            Complete(id); report("エージェントを選べませんでした · 手動で選択してください");
        }
    }
}
