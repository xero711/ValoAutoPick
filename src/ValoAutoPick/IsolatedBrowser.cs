using System.Diagnostics;
using Microsoft.Win32;

namespace ValoAutoPick;

internal sealed class BrowserIsolationException(string message) : Exception(message);

internal static class IsolatedBrowser
{
    internal enum BrowserKind { Chromium, Edge, Firefox }

    internal static Task Open(Uri url)
    {
        if (url.Scheme != Uri.UriSchemeHttps || !url.IsDefaultPort ||
            !url.Host.Equals("auth.riotgames.com", StringComparison.OrdinalIgnoreCase))
            throw new BrowserIsolationException("ログイン先を確認できませんでした");
        var (executable, kind) = DefaultBrowser();
        if (HasUserDataDirectoryPolicy(kind, executable))
            throw new BrowserIsolationException("既定ブラウザーの設定によりログイン情報を分離できません");

        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ValoAutoPick-PrivateLogin"));
        if (HasReparseAncestor(root)) throw new BrowserIsolationException("一時ブラウザープロファイルを作成できません");
        Directory.CreateDirectory(root);
        if (HasReparseAncestor(root)) throw new BrowserIsolationException("一時ブラウザープロファイルを作成できません");
        var profile = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        if (HasReparseAncestor(profile)) throw new BrowserIsolationException("一時ブラウザープロファイルを作成できません");
        try
        {
            var process = Process.Start(CreateStartInfo(executable, profile, url))
                ?? throw new BrowserIsolationException("既定ブラウザーを起動できませんでした");
            _ = CleanupWhenClosed(process, root, profile);
        }
        catch
        {
            TryCleanup(root, profile);
            throw;
        }
        return Task.CompletedTask;
    }

    internal static (string Executable, BrowserKind Kind) DefaultBrowser()
    {
        if (!OperatingSystem.IsWindows()) throw new BrowserIsolationException("既定ブラウザーを確認できません");
        using var choice = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice");
        var progId = choice?.GetValue("ProgId") as string;
        if (string.IsNullOrWhiteSpace(progId) || progId.Contains('\\'))
            throw new BrowserIsolationException("既定ブラウザーを確認できません");
        using var commandKey = Registry.ClassesRoot.OpenSubKey(progId + @"\shell\open\command");
        var command = commandKey?.GetValue(null) as string;
        var executable = ExtractExecutable(command);
        if (executable == null || !Path.IsPathFullyQualified(executable) || !File.Exists(executable))
            throw new BrowserIsolationException("既定ブラウザーを確認できません");
        return (executable, KindOf(executable));
    }

    internal static string? ExtractExecutable(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        command = command.TrimStart();
        string path;
        if (command[0] == '"')
        {
            var end = command.IndexOf('"', 1);
            if (end < 2) return null;
            path = command[1..end];
        }
        else
        {
            var end = command.IndexOfAny([' ', '\t']);
            path = end < 0 ? command : command[..end];
        }
        return Environment.ExpandEnvironmentVariables(path);
    }

    internal static BrowserKind KindOf(string executable) => Path.GetFileName(executable).ToLowerInvariant() switch
    {
        "brave.exe" or "chrome.exe" or "chromium.exe" or "vivaldi.exe" => BrowserKind.Chromium,
        "msedge.exe" => BrowserKind.Edge,
        "firefox.exe" => BrowserKind.Firefox,
        _ => throw new BrowserIsolationException("既定ブラウザーでログイン情報を分離できません")
    };

    internal static ProcessStartInfo CreateStartInfo(string executable, string profile, Uri url)
    {
        var kind = KindOf(executable);
        var start = new ProcessStartInfo(executable) { UseShellExecute = false };
        if (kind == BrowserKind.Firefox)
        {
            start.ArgumentList.Add("-no-remote");
            start.ArgumentList.Add("-profile");
            start.ArgumentList.Add(profile);
            start.ArgumentList.Add("-private-window");
        }
        else
        {
            start.ArgumentList.Add("--user-data-dir=" + profile);
            start.ArgumentList.Add(kind == BrowserKind.Edge ? "--inprivate" : "--incognito");
            start.ArgumentList.Add("--no-first-run");
            start.ArgumentList.Add("--no-default-browser-check");
            start.ArgumentList.Add("--disable-sync");
            start.ArgumentList.Add("--disable-extensions");
        }
        start.ArgumentList.Add(url.AbsoluteUri);
        return start;
    }

    private static bool HasUserDataDirectoryPolicy(BrowserKind kind, string executable)
    {
        if (!OperatingSystem.IsWindows() || kind == BrowserKind.Firefox) return false;
        var name = Path.GetFileName(executable).ToLowerInvariant();
        var policy = name switch
        {
            "brave.exe" => @"Software\Policies\BraveSoftware\Brave",
            "msedge.exe" => @"Software\Policies\Microsoft\Edge",
            "chrome.exe" => @"Software\Policies\Google\Chrome",
            "chromium.exe" => @"Software\Policies\Chromium",
            "vivaldi.exe" => @"Software\Policies\Vivaldi",
            _ => ""
        };
        using var user = Registry.CurrentUser.OpenSubKey(policy);
        using var machine = Registry.LocalMachine.OpenSubKey(policy);
        return user?.GetValue("UserDataDir") != null || machine?.GetValue("UserDataDir") != null;
    }

    private static async Task CleanupWhenClosed(Process process, string root, string profile)
    {
        try { await process.WaitForExitAsync().ConfigureAwait(false); }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { return; }
        finally { process.Dispose(); }
        TryCleanup(root, profile);
    }

    internal static bool IsOwnedProfilePath(string root, string profile)
    {
        var parent = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var candidate = Path.GetFullPath(profile).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(Path.GetDirectoryName(candidate), parent, StringComparison.OrdinalIgnoreCase) &&
            Guid.TryParseExact(Path.GetFileName(candidate), "N", out _);
    }

    private static bool HasReparseAncestor(string path)
    {
        for (var directory = new DirectoryInfo(path); directory != null; directory = directory.Parent)
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0) return true;
        return false;
    }

    private static bool ContainsReparsePoint(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
            foreach (var entry in Directory.EnumerateFileSystemEntries(pending.Pop()))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) return true;
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
            }
        return false;
    }

    private static void TryCleanup(string root, string profile)
    {
        try
        {
            if (!IsOwnedProfilePath(root, profile) || !Directory.Exists(profile) ||
                HasReparseAncestor(profile) || ContainsReparsePoint(profile)) return;
            Directory.Delete(profile, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
    }
}
