using System.Security.Cryptography;

namespace ValoAutoPick;

public sealed record AuthResult(string AccessToken, string IdToken, DateTimeOffset ExpiresAt);
public static class AuthFlow
{
    // Riot's desktop social sign-in currently supports Google, Apple, and Facebook.
    // Keep this list explicit: allowing arbitrary third-party navigation would let a
    // compromised login page send credentials or tokens to an unrelated site.
    private static readonly string[] SocialAuthHosts =
    [
        "accounts.google.com",
        "consent.google.com",
        "appleid.apple.com",
        "idmsa.apple.com",
        "facebook.com",
        "www.facebook.com",
        "m.facebook.com"
    ];

    public static string NewState() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    public static string Url(string state, bool silent) =>
        "https://auth.riotgames.com/authorize?redirect_uri=https%3A%2F%2Fplayvalorant.com%2Fopt_in" +
        "&client_id=play-valorant-web-prod&response_type=token%20id_token&scope=account%20openid" +
        "&nonce=" + NewState() + "&state=" + Uri.EscapeDataString(state) + (silent ? "&prompt=none" : "&prompt=login") + "&ui_locales=ja-JP";
    public static bool IsCallback(Uri uri) => uri.Scheme == "https" && uri.Host == "playvalorant.com" && uri.IsDefaultPort && uri.AbsolutePath == "/opt_in";
    public static AuthResult ParseCallback(Uri uri, string state, DateTimeOffset now)
    {
        if (!IsCallback(uri)) throw new InvalidDataException("ログイン先を確認できませんでした");
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in uri.Fragment.TrimStart('#').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = item.Split('=', 2);
            if (pair.Length != 2 || !fields.TryAdd(Uri.UnescapeDataString(pair[0]), Uri.UnescapeDataString(pair[1])))
                throw new InvalidDataException("ログイン応答が不正です");
        }
        if (!fields.TryGetValue("state", out var returnedState) || !string.Equals(state, returnedState, StringComparison.Ordinal))
            throw new InvalidDataException("ログインの確認に失敗しました · もう一度ログインしてください");
        if (!fields.TryGetValue("access_token", out var access) || string.IsNullOrWhiteSpace(access) ||
            !fields.TryGetValue("id_token", out var id) || string.IsNullOrWhiteSpace(id) ||
            !fields.TryGetValue("expires_in", out var seconds) || !int.TryParse(seconds, out var lifetime) || lifetime <= 0 ||
            !fields.TryGetValue("token_type", out var type) || !type.Equals("Bearer", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("ログインが完了しませんでした");
        return new(access, id, now.AddSeconds(Math.Min(lifetime, 86400)));
    }
    public static bool AllowedNavigation(Uri uri)
    {
        if (uri.Scheme != "https" || !uri.IsDefaultPort) return false;
        if (uri.Host == "riotgames.com" || uri.Host.EndsWith(".riotgames.com", StringComparison.OrdinalIgnoreCase)) return true;
        return SocialAuthHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase);
    }
}
