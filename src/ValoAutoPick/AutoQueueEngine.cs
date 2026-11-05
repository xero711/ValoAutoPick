namespace ValoAutoPick;

public sealed class AutoQueueEngine(IQueueApi api, Action<string> report)
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(8);
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private DateTimeOffset nextCheck;
    private DateTimeOffset joinPendingUntil;

    public async Task Tick(DateTimeOffset now, CancellationToken ct, bool stateChanged = false, Func<bool>? enabled = null)
    {
        await operationGate.WaitAsync(ct);
        try { await TickCore(now, ct, stateChanged, enabled); }
        finally { operationGate.Release(); }
    }

    private async Task TickCore(DateTimeOffset now, CancellationToken ct, bool stateChanged, Func<bool>? enabled)
    {
        if (enabled?.Invoke() == false) return;
        if (now < joinPendingUntil) return;
        if (!stateChanged && now < nextCheck) return;
        nextCheck = now + PollInterval;

        PartyQueueSnapshot party;
        try { party = await api.GetQueueSnapshot(ct); }
        catch (ClientUnavailableException e) { report(e.Message); return; }
        catch (ApiException e) when (e.Status == 404) { report("パーティー情報を待っています"); return; }
        if (party.State == "MATCHMADE_GAME_STARTING")
        {
            report("マッチが見つかりました · ゲーム内で承諾してください");
            return;
        }
        if (party.State == "IN_GAME")
        {
            report("試合終了後、すぐにキューへ参加します");
            return;
        }
        if (party.IsInQueue)
        {
            report("マッチを検索中");
            return;
        }
        if (party.State == "MATCHMAKING")
        {
            report("マッチを検索中");
            return;
        }
        if (party.State != "DEFAULT")
        {
            report("試合終了後に自動でキューへ参加します");
            return;
        }
        if (party.RestrictedSeconds > 0)
        {
            report($"キュー制限中 · 残り約{Math.Ceiling(party.RestrictedSeconds / 60d):0}分");
            return;
        }
        if (party.QueueId.Length == 0)
        {
            report("ゲーム内でモードを選択してください");
            return;
        }
        if (!party.IsLeader)
        {
            report("パーティーリーダーになると自動で参加します");
            return;
        }
        if (!party.EligibleQueues.Contains(party.QueueId, StringComparer.OrdinalIgnoreCase))
        {
            report("参加可能なモードを確認中です");
            return;
        }
        if (party.Ineligibilities.Count > 0)
        {
            report("キュー参加条件を満たしていません");
            return;
        }

        ct.ThrowIfCancellationRequested();
        if (enabled?.Invoke() == false) return;
        try
        {
            await api.EnterQueue(party.PartyId, ct);
            nextCheck = now + TimeSpan.FromSeconds(2);
            joinPendingUntil = nextCheck;
            report("自動でキューに参加しました");
        }
        catch (ApiException e) when (e.Status is 400 or 409)
        {
            report("キュー状態を確認中です");
        }
    }

    public async Task LeaveActiveQueue(CancellationToken ct)
    {
        await operationGate.WaitAsync(ct);
        try
        {
            PartyQueueSnapshot party;
            try { party = await api.GetQueueSnapshot(ct); }
            catch (ClientUnavailableException e) { report(e.Message); throw; }
            catch (ApiException e) when (e.Status == 404) { report("パーティー情報を確認できません · VALORANTで状態を確認してください"); return; }

            if (party.State == "MATCHMADE_GAME_STARTING")
            {
                report("マッチ成立中 · ゲーム内で承諾または辞退してください");
                return;
            }
            if (!party.IsInQueue && party.State != "MATCHMAKING")
            {
                report("キャンセル対象の検索はありません");
                nextCheck = DateTimeOffset.MinValue;
                joinPendingUntil = DateTimeOffset.MinValue;
                return;
            }
            if (!party.IsLeader)
            {
                report("パーティーリーダーがキュー検索を停止してください");
                return;
            }

            ct.ThrowIfCancellationRequested();
            await api.LeaveQueue(party.PartyId, ct);
            nextCheck = DateTimeOffset.MinValue;
            joinPendingUntil = DateTimeOffset.MinValue;
            report("キュー検索をキャンセルしました");
        }
        finally { operationGate.Release(); }
    }
}
