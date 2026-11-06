using Microsoft.Web.WebView2.WinForms;
using Microsoft.Web.WebView2.Core;
using System.Net.Sockets;

namespace ValoAutoPick;

public sealed class LoginWindow : DarkForm
{
    private readonly WebView2 browser = new() { Dock = DockStyle.Fill };
    private readonly ThemedButton googleLogin = new() { Text = "Googleでログイン（ブラウザー）", Dock = DockStyle.Fill, Compact = true };
    private readonly Label status = new()
    {
        Dock = DockStyle.Top,
        Height = 88,
        Padding = new Padding(14, 8, 14, 8),
        TextAlign = ContentAlignment.MiddleLeft,
        Font = new Font("Yu Gothic UI", 10.5f),
        Text = "Googleログインは上のボタンを使ってください。\nRiotのログイン画面を読み込み中…",
        BackColor = Color.FromArgb(20, 26, 37),
        ForeColor = Color.White
    };
    private TaskCompletionSource<AuthResult?>? pending;
    private string state = "";
    private CancellationToken authenticationToken;
    private CancellationTokenSource? browserLoginStop;
    private readonly Dictionary<CoreWebView2, Form> popups = [];
    private bool silent;
    private bool disposing;
    internal bool UsedSystemBrowser { get; private set; }
    public static string ProfileFolder => Path.Combine(Storage.Folder, "LoginBrowser");

    public LoginWindow()
    {
        Text = "Riot アカウントでログイン"; Size = new(560, 780); MinimumSize = new(480, 650); StartPosition = FormStartPosition.CenterParent;
        var googleBar = new Panel { Dock = DockStyle.Top, Height = 62, Padding = new Padding(14, 7, 14, 7), BackColor = Color.FromArgb(20, 26, 37) };
        googleBar.Controls.Add(googleLogin);
        Controls.Add(browser); Controls.Add(status); Controls.Add(googleBar);
        googleLogin.Click += async (_, _) => await StartGoogleLogin();
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
        silent = silentLogin; state = AuthFlow.NewState(); authenticationToken = ct;
        pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = pending.Task;
        using var registration = ct.Register(() => { if (IsHandleCreated && !IsDisposed) BeginInvoke(() => Finish(null)); });
        try
        {
            if (!silent) Show(owner);
            await browser.EnsureCoreWebView2Async();
            var core = browser.CoreWebView2;
            Configure(core);
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
        if (IsGoogle(uri))
        {
            e.Cancel = true;
            if (silent) Finish(null);
            else
            {
                if (sender is CoreWebView2 popupCore && popups.TryGetValue(popupCore, out var popup) && !popup.IsDisposed)
                    BeginInvoke(popup.Close);
                _ = StartGoogleLogin();
            }
            return;
        }
        if (AuthFlow.IsCallback(uri))
        {
            e.Cancel = true;
            if (browserLoginStop != null) return;
            try { var result = AuthFlow.ParseCallback(uri, state, DateTimeOffset.UtcNow); UsedSystemBrowser = false; Finish(result); }
            catch (InvalidDataException) { Finish(null); }
            return;
        }
        if (!AuthFlow.AllowedNavigation(uri)) { e.Cancel = true; status.Text = "Riot、Google、Apple、Facebookのログイン画面以外への移動は無効です。"; return; }
        status.Text = "接続先: " + uri.Host + "\nGoogleログインは上のボタンを使ってください。";
        if (silent && (uri.AbsolutePath.Contains("/login") || uri.Host == "authenticate.riotgames.com"))
        { e.Cancel = true; Finish(null); }
    }
    private void Configure(CoreWebView2 core)
    {
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsWebMessageEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.NewWindowRequested -= OpenTrustedPopup; core.NewWindowRequested += OpenTrustedPopup;
        core.NavigationStarting -= Navigation; core.NavigationStarting += Navigation;
        core.DownloadStarting -= BlockDownload; core.DownloadStarting += BlockDownload;
    }

    private async void OpenTrustedPopup(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var googleUri) && IsGoogle(googleUri))
        {
            e.Handled = true;
            if (!silent) _ = StartGoogleLogin();
            return;
        }
        var blank = string.IsNullOrEmpty(e.Uri) || e.Uri.Equals("about:blank", StringComparison.OrdinalIgnoreCase);
        if (!blank && (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) || !AuthFlow.AllowedNavigation(uri)))
        {
            e.Handled = true;
            status.Text = "許可されていないログイン先への移動をブロックしました。";
            return;
        }

        e.Handled = true;
        if (silent) { Finish(null); return; }
        using var deferral = e.GetDeferral();
        Form? popup = null;
        try
        {
            popup = new DarkForm { Text = "ログイン", Size = new Size(520, 700), MinimumSize = new Size(420, 500), StartPosition = FormStartPosition.CenterParent };
            var popupBrowser = new WebView2 { Dock = DockStyle.Fill };
            popup.Controls.Add(popupBrowser);
            popup.Show(this);
            await popupBrowser.EnsureCoreWebView2Async(browser.CoreWebView2.Environment);
            if (pending == null || popup.IsDisposed) { popup.Dispose(); return; }
            var popupCore = popupBrowser.CoreWebView2;
            Configure(popupCore);
            popups.Add(popupCore, popup);
            popup.FormClosed += (_, _) => { popups.Remove(popupCore); popup.Dispose(); };
            popupCore.WindowCloseRequested += (_, _) => { if (!popup.IsDisposed) popup.Close(); };
            e.NewWindow = popupCore;
        }
        catch (Exception e2) when (e2 is InvalidOperationException or ObjectDisposedException or System.Runtime.InteropServices.COMException)
        {
            popup?.Dispose();
            if (!IsDisposed) status.Text = "ログイン画面を開けませんでした。もう一度お試しください。";
        }
    }
    private static bool IsGoogle(Uri uri) => uri.Scheme == "https" && uri.IsDefaultPort &&
        (uri.Host.Equals("accounts.google.com", StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("consent.google.com", StringComparison.OrdinalIgnoreCase));

    private async Task StartGoogleLogin()
    {
        if (pending == null || silent || browserLoginStop != null) return;
        var attemptState = state;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(authenticationToken);
        browserLoginStop = stop; googleLogin.Enabled = false; browser.Enabled = false;
        try
        {
            status.Text = "ブラウザーを開きます。Riotの画面でGoogleログインを選んでください。";
            var result = await new SystemBrowserAuth(attemptState).Run((url, _) => IsolatedBrowser.Open(url), stop.Token);
            if (pending == null || attemptState != state) return;
            if (result == null) { status.Text = "Googleログインがキャンセルされました。もう一度お試しください。"; return; }
            UsedSystemBrowser = true;
            Finish(result);
        }
        catch (OperationCanceledException) { }
        catch (SocketException)
        { if (!IsDisposed) status.Text = "ブラウザーログインに必要なlocalhostのポート80を使用できません。"; }
        catch (BrowserIsolationException e)
        { if (!IsDisposed) status.Text = e.Message; }
        catch (Exception e) when (e is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        { if (!IsDisposed) status.Text = "ブラウザーを開けませんでした。もう一度お試しください。"; }
        finally
        {
            if (ReferenceEquals(browserLoginStop, stop)) browserLoginStop = null;
            if (!IsDisposed) { googleLogin.Enabled = true; browser.Enabled = true; }
        }
    }
    private static void BlockDownload(object? sender, CoreWebView2DownloadStartingEventArgs e) => e.Cancel = true;
    private void Finish(AuthResult? result)
    {
        var completion = pending; pending = null;
        browserLoginStop?.Cancel();
        foreach (var popup in popups.Values.ToArray()) popup.Close();
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
        if (disposingManaged) { browserLoginStop?.Cancel(); pending?.TrySetResult(null); foreach (var popup in popups.Values.ToArray()) popup.Dispose(); popups.Clear(); browser.Dispose(); }
        base.Dispose(disposingManaged);
    }
}
