using Microsoft.Web.WebView2.WinForms;
using Microsoft.Web.WebView2.Core;

namespace ValoAutoPick;

public sealed class LoginWindow : DarkForm
{
    private readonly WebView2 browser = new() { Dock = DockStyle.Fill };
    private readonly Label status = new()
    {
        Dock = DockStyle.Top,
        Height = 88,
        Padding = new Padding(14, 8, 14, 8),
        TextAlign = ContentAlignment.MiddleLeft,
        Font = new Font("Yu Gothic UI", 10.5f),
        Text = "Riotのログイン画面を読み込み中…",
        BackColor = Color.FromArgb(20, 26, 37),
        ForeColor = Color.White
    };
    private TaskCompletionSource<AuthResult?>? pending;
    private string state = "";
    private bool silent;
    private bool disposing;
    public static string ProfileFolder => Path.Combine(Storage.Folder, "LoginBrowser");

    public LoginWindow()
    {
        Text = "Riot アカウントでログイン"; Size = new(560, 780); MinimumSize = new(480, 650); StartPosition = FormStartPosition.CenterParent;
        Controls.Add(browser); Controls.Add(status);
        browser.CreationProperties = new CoreWebView2CreationProperties
        {
            // Keep the Riot web login cookie in WebView2's per-user profile so a
            // later app launch can silently obtain a fresh short-lived token.
            UserDataFolder = ProfileFolder,
            IsInPrivateModeEnabled = false
        };
        FormClosing += (_, e) =>
        {
            if (!disposing) { e.Cancel = true; Finish(null); }
        };
    }
    public async Task<AuthResult?> Authenticate(Form owner, bool silentLogin, CancellationToken ct)
    {
        if (pending != null) throw new InvalidOperationException("ログイン処理中です");
        silent = silentLogin; state = AuthFlow.NewState();
        pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = pending.Task;
        using var registration = ct.Register(() => { if (IsHandleCreated && !IsDisposed) BeginInvoke(() => Finish(null)); });
        try
        {
            if (!silent) Show(owner);
            await browser.EnsureCoreWebView2Async();
            var core = browser.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsWebMessageEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.NewWindowRequested -= OpenTrustedPopup; core.NewWindowRequested += OpenTrustedPopup;
            core.NavigationStarting -= Navigation; core.NavigationStarting += Navigation;
            core.DownloadStarting -= BlockDownload; core.DownloadStarting += BlockDownload;
            ct.ThrowIfCancellationRequested();
            core.Navigate(AuthFlow.Url(state, silent));
            var timeout = Task.Delay(silent ? TimeSpan.FromSeconds(25) : TimeSpan.FromMinutes(10), ct);
            if (await Task.WhenAny(task, timeout) != task) Finish(null);
            return await task;
        }
        catch { Finish(null); throw; }
    }
    private void Navigation(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (e.Uri == "about:blank") return;
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri)) { e.Cancel = true; return; }
        if (AuthFlow.IsCallback(uri))
        {
            e.Cancel = true;
            try { Finish(AuthFlow.ParseCallback(uri, state, DateTimeOffset.UtcNow)); }
            catch (InvalidDataException) { Finish(null); }
            return;
        }
        if (!AuthFlow.AllowedNavigation(uri)) { e.Cancel = true; status.Text = "Riot、Google、Apple、Facebookのログイン画面以外への移動は無効です。"; return; }
        status.Text = "接続先: " + uri.Host + "\nパスワードはRiot公式画面に入力され、アプリには保存されません。";
        if (silent && (uri.AbsolutePath.Contains("/login") || uri.Host == "authenticate.riotgames.com"))
        { e.Cancel = true; Finish(null); }
    }
    private void OpenTrustedPopup(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) || !AuthFlow.AllowedNavigation(uri))
        {
            e.Handled = true;
            status.Text = "許可されていないログイン先への移動をブロックしました。";
            return;
        }

        // Social sign-in buttons may request a popup. Keep the flow in the same
        // controlled WebView so the callback and state validation remain intact.
        e.Handled = true;
        browser.CoreWebView2?.Navigate(uri.AbsoluteUri);
    }
    private static void BlockDownload(object? sender, CoreWebView2DownloadStartingEventArgs e) => e.Cancel = true;
    private void Finish(AuthResult? result)
    {
        var completion = pending; pending = null;
        browser.CoreWebView2?.Stop(); Hide();
        completion?.TrySetResult(result);
    }
    public async Task ClearSession()
    {
        Finish(null);
        if (browser.CoreWebView2 != null) await browser.CoreWebView2.Profile.ClearBrowsingDataAsync();
    }
    public async Task CaptureDiagnostic(string path)
    {
        if (browser.CoreWebView2 == null) throw new InvalidOperationException("Browser not initialized");
        using var output = File.Create(path);
        await browser.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, output);
        var source = new Uri(browser.Source.AbsoluteUri);
        // No query, fragment, page text, cookies, or credentials in diagnostics.
        File.WriteAllText(Path.ChangeExtension(path, ".txt"), "Login page host: " + source.Host + "\nHTTPS: " + (source.Scheme == "https"));
    }
    protected override void Dispose(bool disposingManaged)
    {
        disposing = true;
        if (disposingManaged) { pending?.TrySetResult(null); browser.Dispose(); }
        base.Dispose(disposingManaged);
    }
}
