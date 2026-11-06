using System.Text.Json;

namespace ValoAutoPick;

public record Agent(string Id, string Name, string Role, string RoleIconId = "")
{
    public override string ToString() => Name;
}
public static class RegionDisplay
{
    public static string Name(string region, string shard) => shard == "pbe" ? "テスト環境（PBE）" : region switch
    {
        "ap" => "アジア太平洋（東京など）",
        "na" => "北アメリカ",
        "eu" => "ヨーロッパ",
        "kr" => "韓国",
        "br" => "ブラジル",
        "latam" => "ラテンアメリカ",
        _ => "地域不明"
    };
}
public sealed class Settings
{
    public string AgentId { get; set; } = "";
    public Dictionary<string, string> MapAgents { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool StartOnLaunch { get; set; } = false;
    public string Region { get; set; } = "auto";
    public bool SystemBrowserLogin { get; set; }
    public Settings Normalize()
    {
        if (!Guid.TryParse(AgentId, out _)) AgentId = "";
        MapAgents = (MapAgents ?? new()).Where(x => !string.IsNullOrWhiteSpace(x.Key) && Guid.TryParse(x.Value, out _)).GroupBy(x => MapCatalog.Key(x.Key), StringComparer.OrdinalIgnoreCase).Where(x => x.Key.Length > 0).ToDictionary(x => x.Key, x => x.Last().Value, StringComparer.OrdinalIgnoreCase);
        if (!new[] { "auto", "ap", "na", "eu", "kr", "br", "latam", "pbe" }.Contains(Region)) Region = "auto";
        return this;
    }
}
public static class Storage
{
    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ValoAutoPick");
    public static string SettingsPath => Path.Combine(Folder, "quickpickerpro-settings.json");
    public static Settings Load()
    {
        try { return Parse(File.ReadAllText(File.Exists(SettingsPath) ? SettingsPath : Path.Combine(Folder, "settings.json"))); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }
    internal static Settings Parse(string json)
    {
        var settings = JsonSerializer.Deserialize<Settings>(json) ?? new();
        if (string.IsNullOrWhiteSpace(settings.AgentId))
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                var oldPriority = document.RootElement.EnumerateObject()
                    .FirstOrDefault(property => property.Name.Equals("Priority", StringComparison.OrdinalIgnoreCase)).Value;
                if (oldPriority.ValueKind == JsonValueKind.Array && oldPriority.GetArrayLength() > 0 && oldPriority[0].ValueKind == JsonValueKind.String)
                    settings.AgentId = oldPriority[0].GetString() ?? "";
            }
        }
        return settings.Normalize();
    }
    public static void Save(Settings settings)
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(SettingsPath + ".tmp", JsonSerializer.Serialize(settings.Normalize(), new JsonSerializerOptions { WriteIndented = true }));
        File.Move(SettingsPath + ".tmp", SettingsPath, true);
    }
}
public record Player(string Subject, string CharacterID, string CharacterSelectionState);
public record MatchSnapshot(string Id, string State, IReadOnlyList<Player> Allies, string MapId = "");
public static class MapCatalog
{
    public static readonly (string Key, string Name)[] Maps = [
        ("Ascent", "アセント"), ("Duality", "バインド"), ("Triad", "ヘイヴン"),
        ("Bonsai", "スプリット"), ("Port", "アイスボックス"), ("Foxtrot", "ブリーズ"),
        ("Canyon", "フラクチャー"), ("Pitt", "パール"), ("Jam", "ロータス"),
        ("Juliett", "サンセット"), ("Infinity", "アビス"), ("Rook", "カロード"), ("Plummet", "サミット"),
        ("HURM_Alley", "ディストリクト"), ("HURM_Bowl", "カスバ"), ("HURM_Helix", "ドリフト"), ("HURM_HighTide", "グリッチ"), ("HURM_Yard", "ピアッツァ")];
    public static string Key(string? mapId) => (mapId ?? "").Trim().TrimEnd('/').Split('/').LastOrDefault() ?? "";
    public static string Name(string? mapId) { var key = Key(mapId); return Maps.FirstOrDefault(m => m.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Name ?? (key.Length == 0 ? "取得中" : key); }
}
public record PartyQueueSnapshot(string PartyId, string State, string QueueId, bool IsLeader, int RestrictedSeconds, IReadOnlyList<string> EligibleQueues, IReadOnlyList<string> Ineligibilities, bool IsInQueue = false);
public interface IQueueApi
{
    Task<PartyQueueSnapshot> GetQueueSnapshot(CancellationToken ct);
    Task<bool> IsInMatch(CancellationToken ct);
    Task EnterQueue(string partyId, CancellationToken ct);
    Task LeaveQueue(string partyId, CancellationToken ct);
}
public interface IGameApi
{
    string Subject { get; }
    Task<string?> GetMatchId(CancellationToken ct);
    Task<string?> GetCoreMatchId(CancellationToken ct);
    Task<string?> GetCoreMapId(string matchId, CancellationToken ct);
    Task<string?> GetPartyState(CancellationToken ct);
    Task<string?> GetLocalGameFlowState(CancellationToken ct);
    Task<MatchSnapshot> GetMatch(string matchId, CancellationToken ct);
    Task Select(string matchId, string agentId, CancellationToken ct);
    Task Lock(string matchId, string agentId, CancellationToken ct);
}
public sealed class ApiException(int status, int retrySeconds = 5) : Exception($"通信応答: HTTP {status}")
{
    public int Status { get; } = status;
    public int RetrySeconds { get; } = Math.Clamp(retrySeconds, 1, 120);
}
public sealed class ClientUnavailableException(string message) : Exception(message);
