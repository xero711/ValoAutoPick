using System.Drawing.Drawing2D;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using System.Threading.Channels;

namespace ValoAutoPick;

public sealed class MainWindow : DarkForm
{
    // Keep the idle poll short enough to catch the start of character select quickly,
    // while still leaving the server a reasonable amount of breathing room. The
    // worker is serial, so this never creates overlapping requests.
    private const int FastPollIntervalMilliseconds = 250;
    private const int ClientUnavailableIntervalMilliseconds = 1500;
    private const int TransientFailureIntervalMilliseconds = 2500;

    private static readonly byte[] AppIconBytes = LoadAppIconBytes();
    private static readonly string[] RegionValues = ["auto", "ap", "na", "eu", "kr", "br", "latam", "pbe"];
    private readonly ThemedSelect region = RegionCombo();
    private readonly SwitchToggle autoStart = new("自動で監視開始", false);
    private readonly ThemedButton login = new() { Text = "Riotにログイン", Size = new(176, 43), Compact = true };
    // Leave enough horizontal room for Japanese glyphs plus the button's text padding.
    private readonly ThemedButton logout = new() { Text = "ログアウト", Size = new(119, 43), Compact = true };
    private readonly ThemedButton start = new() { Text = "監視を開始", Primary = true, Font = new Font("Yu Gothic UI", 15.5f, FontStyle.Bold) };
    private readonly ThemedButton autoQueue = new() { Text = "自動インキュー開始", Font = new Font("Yu Gothic UI", 13.5f, FontStyle.Bold), AccessibleDescription = "ゲーム内で選択中のモードに自動で参加します" };
    private readonly HeroPanel hero = new();
    private readonly RoundedCard settingsPanel = new() { Dock = DockStyle.Fill };
    private readonly RoundedCard startPanel = new() { Dock = DockStyle.Fill };
    private readonly Label status = new() { Text = "停止中", ForeColor = Theme.Muted, Font = new Font("Yu Gothic UI", 8.2f, FontStyle.Bold), AutoSize = false, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill };
    private readonly Label detail = new() { Text = "", ForeColor = Theme.Muted, Font = new Font("Yu Gothic UI", 8.2f), AutoSize = false, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill };
    private readonly Label queueDetail = new() { Text = "自動インキューは停止中", ForeColor = Theme.Muted, Font = new Font("Yu Gothic UI", 8.2f), AutoSize = false, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill };
    private readonly Label accountInfo = new() { Text = "Riotアカウント: 未接続", ForeColor = Theme.Muted, Font = new Font("Yu Gothic UI", 9.5f), AutoSize = false, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill };
    private readonly NotifyIcon tray = new() { Icon = LoadAppIcon(), Text = "Valo Auto Pick", Visible = true };
    private readonly RiotApi api = new();
    private readonly CancellationTokenSource lifetime = new();
    private LoginWindow? loginWindow;
    private CancellationTokenSource? loginAttempt;
    private CancellationTokenSource? monitor;
    private Task? monitorTask;
    private Task? stopTask;
    private Task? queueLeaveTask;
    private PickEngine? engine;
    private AutoQueueEngine? queueEngine;
    private RelayOptions? relayOptions;
    private string engineSubject = "", lastStatus = "", lastQueueStatus = "";
    private bool closing, busy, stopping, pickMonitoring, autoQueueActive, systemBrowserLogin;
    private Panel? statusLed;
    private List<Agent> agents = [];
    private Agent? mainAgent;
    private readonly string? smokePath;
    private readonly string? previewAgentId;

    public MainWindow(string? smokePath = null, bool loginSmokeTest = false, string? previewAgentId = null, bool pickerSmokeTest = false)
    {
        this.smokePath = smokePath;
        this.previewAgentId = previewAgentId;
        Text = "ValBuddy";
        Icon = LoadAppIcon();
        ClientSize = new Size(940, 648); MinimumSize = new Size(820, 565); StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Background; ForeColor = Theme.Text; Font = new Font("Yu Gothic UI", 10.5f);

        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("ValoAutoPick.agents.json");
        if (resource == null) throw new InvalidOperationException("エージェント一覧を読み込めません。");
        agents = JsonSerializer.Deserialize<List<Agent>>(resource) ?? [];
        try
        {
            var cached = JsonSerializer.Deserialize<List<Agent>>(File.ReadAllText(Path.Combine(Storage.Folder, "agents.json")));
            if (cached is { Count: >= 10 } && cached.All(a => a != null && Guid.TryParse(a.Id, out _) && !string.IsNullOrWhiteSpace(a.Name))) agents = cached;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }

        var config = Storage.Load(); RestoreAgent(config);
        systemBrowserLogin = config.SystemBrowserLogin;
        if (previewAgentId != null) mainAgent = agents.FirstOrDefault(a => a.Id.Equals(previewAgentId, StringComparison.OrdinalIgnoreCase));
        autoStart.Checked = config.StartOnLaunch;
        region.SelectedIndex = Math.Max(0, Array.IndexOf(RegionValues, config.Region));

        var root = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Background, AutoScroll = true };
        var header = BuildHeader(); header.Dock = DockStyle.None; header.TabIndex = 0;
        var dashboard = BuildDashboard(); dashboard.Dock = DockStyle.None; dashboard.TabIndex = 1;
        root.Controls.Add(dashboard); root.Controls.Add(header);
        Controls.Add(root);
        void ArrangeRoot()
        {
            var width = Math.Max(UiMetrics.Px(this, 738), root.ClientSize.Width - UiMetrics.Px(this, 39));
            var height = Math.Max(UiMetrics.Px(this, 551), root.ClientSize.Height - UiMetrics.Px(this, 87));
            var offset = root.AutoScrollPosition;
            header.Bounds = new Rectangle(offset.X + UiMetrics.Px(this, 20), offset.Y + UiMetrics.Px(this, 13), width, UiMetrics.Px(this, 49));
            dashboard.Bounds = new Rectangle(offset.X + UiMetrics.Px(this, 20), offset.Y + UiMetrics.Px(this, 68), width, height);
        }
        root.Layout += (_, _) => ArrangeRoot();
        ArrangeRoot();

        SetHero();
        hero.ChooseClicked += (_, _) => PickAgent();
        login.Click += async (_, _) => await SignIn();
        logout.Click += async (_, _) => await SignOut();
        start.Click += async (_, _) => await TogglePickMonitoring();
        autoQueue.Click += async (_, _) => await ToggleAutoQueue();
        region.SelectedIndexChanged += async (_, _) => { SaveSettings(); if (api.HasAccountSession) await SignOut(clearBrowserSession: false); };
        autoStart.CheckedChanged += (_, _) => SaveSettings();
        Resize += (_, _) => { if (WindowState == FormWindowState.Minimized) { Hide(); tray.ShowBalloonTip(1500, "Valo Auto Pick", monitor != null ? autoQueueActive ? "自動インキューを監視中です" : "自動ピックを監視中です" : "監視は停止中です", ToolTipIcon.Info); } };
        tray.DoubleClick += (_, _) => Restore();
        var menu = new ContextMenuStrip();
        menu.Items.Add("開く", null, (_, _) => Restore());
        menu.Items.Add("監視を停止", null, async (_, _) => await StopMonitor());
        menu.Items.Add("終了", null, (_, _) => Close());
        tray.ContextMenuStrip = menu;
        UpdateConnectionUi();
        Load += (_, _) => { ActiveControl = login.Visible ? login : region; root.AutoScrollPosition = Point.Empty; };

        Shown += async (_, _) =>
        {
            if (smokePath != null)
            {
                if (loginSmokeTest)
                {
                    try
                    {
                        loginWindow = new LoginWindow();
                        var loginTask = loginWindow.Authenticate(this, false, lifetime.Token);
                        await Task.Delay(12000);
                        await loginWindow.CaptureDiagnostic(smokePath);
                        lifetime.Cancel(); await loginTask;
                    }
                    catch (Exception e) { File.WriteAllText(Path.ChangeExtension(smokePath, ".error.txt"), e.GetType().Name); }
                    Close(); return;
                }
                if (pickerSmokeTest)
                {
                    using var picker = new AgentPickerDialog(agents, mainAgent?.Id);
                    picker.Shown += async (_, _) =>
                    {
                        await Task.Delay(900);
                        picker.TopMost = true;
                        picker.Activate();
                        using var shot = new Bitmap(picker.Width, picker.Height);
                        using (var graphics = Graphics.FromImage(shot)) graphics.CopyFromScreen(picker.Location, Point.Empty, picker.Size);
                        shot.Save(smokePath);
                        picker.DialogResult = DialogResult.Cancel;
                        picker.Close();
                    };
                    picker.ShowDialog(this); Close(); return;
                }
                Hide(); Show(); TopMost = true; Activate();
                await Task.Delay(700);
                using var bitmap = new Bitmap(Width, Height);
                using (var graphics = Graphics.FromImage(bitmap)) graphics.CopyFromScreen(Location, Point.Empty, Size);
                bitmap.Save(smokePath); Close();
            }
            else
                await RestoreAccountSession(config);
        };
        FormClosing += async (_, e) =>
        {
            if (closing) return;
            e.Cancel = true; closing = true; lifetime.Cancel(); await StopMonitor();
            loginWindow?.Dispose(); api.Dispose(); tray.Dispose(); BeginInvoke(Close);
        };
    }

    private static byte[] LoadAppIconBytes()
    {
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("ValoAutoPick.Assets.ValoAutoPick-icon.ico");
        if (resource == null) return [];
        using var copy = new MemoryStream();
        resource.CopyTo(copy);
        return copy.ToArray();
    }

    private static Icon LoadAppIcon()
    {
        if (AppIconBytes.Length == 0) return SystemIcons.Application;
        using var stream = new MemoryStream(AppIconBytes, writable: false);
        using var source = new Icon(stream);
        return new Icon(source, source.Size);
    }

    private Control BuildHeader()
    {
        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Theme.Background, Margin = Padding.Empty };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var brand = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Background };
        brand.Paint += (_, e) =>
        {
            int S(int value) => UiMetrics.Px(brand, value);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = new GraphicsPath(); path.AddPolygon([new Point(S(3), S(4)), new Point(S(24), S(4)), new Point(S(36), S(16)), new Point(S(24), S(40)), new Point(S(3), S(40)), new Point(S(15), S(22))]);
            using var brush = new SolidBrush(Theme.Red); e.Graphics.FillPath(brush, path);
            using var cut = new SolidBrush(Theme.Background); cut.Color = Color.FromArgb(95, Theme.Background);
            e.Graphics.FillPolygon(cut, [new Point(S(18), S(10)), new Point(S(23), S(10)), new Point(S(13), S(32)), new Point(S(8), S(32))]);
        };
        brand.Controls.Add(new Label { Text = "VALBUDDY", ForeColor = Theme.Text, Font = new Font("Segoe UI", 18, FontStyle.Bold), AutoSize = true, Location = new Point(43, 2) });
        var loginBar = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(0, 4, 0, 0), Margin = Padding.Empty };
        logout.Margin = new Padding(7, 0, 0, 0); login.Margin = Padding.Empty;
        loginBar.Controls.Add(login); loginBar.Controls.Add(logout);
        header.Controls.Add(brand, 0, 0); header.Controls.Add(loginBar, 1, 0);
        return header;
    }

    private Control BuildDashboard()
    {
        var columns = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Theme.Background, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty, Padding = new Padding(0, 6, 0, 6) };
        columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 54)); columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 46));
        columns.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        hero.Dock = DockStyle.Fill; hero.Margin = new Padding(0, 0, 16, 0);
        settingsPanel.Margin = new Padding(0, 0, 0, 13); startPanel.Margin = Padding.Empty;

        var sidebar = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Theme.Background, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
        sidebar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, 252)); sidebar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        sidebar.Controls.Add(settingsPanel, 0, 0);
        sidebar.Controls.Add(startPanel, 0, 1);
        columns.Controls.Add(hero, 0, 0); columns.Controls.Add(sidebar, 1, 0);

        var connection = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Theme.Surface, Padding = new Padding(8, 4, 8, 4), RowCount = 5, ColumnCount = 1, Margin = Padding.Empty };
        connection.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        connection.RowStyles.Add(new RowStyle(SizeType.Absolute, 31));
        connection.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        connection.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        connection.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        connection.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        connection.Controls.Add(SectionTitle("接続設定"), 0, 0);
        accountInfo.Font = new Font("Yu Gothic UI", 9.5f, FontStyle.Bold);
        connection.Controls.Add(accountInfo, 0, 1);
        var regionRow = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Theme.Surface, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        regionRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 94)); regionRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        regionRow.Controls.Add(FieldLabel("接続地域"), 0, 0); region.Dock = DockStyle.Fill; region.Margin = Padding.Empty; regionRow.Controls.Add(region, 1, 0);
        connection.Controls.Add(regionRow, 0, 2);
        autoStart.Margin = new Padding(0, 6, 0, 0); autoStart.Dock = DockStyle.Fill; connection.Controls.Add(autoStart, 0, 3);
        settingsPanel.Controls.Add(connection);

        var action = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Theme.Surface, Padding = new Padding(8, 6, 8, 6), RowCount = 8, ColumnCount = 1, Margin = Padding.Empty };
        action.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        action.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        action.RowStyles.Add(new RowStyle(SizeType.Absolute, 31));
        action.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        action.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        action.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        action.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        action.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        action.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        action.Controls.Add(SectionTitle("自動ピック"), 0, 1);
        start.Dock = DockStyle.Fill; start.Margin = new Padding(0, 0, 0, 5); start.Text = pickMonitoring ? "監視を停止" : "監視を開始"; action.Controls.Add(start, 0, 2);
        autoQueue.Dock = DockStyle.Fill; autoQueue.Margin = Padding.Empty; action.Controls.Add(autoQueue, 0, 3);
        var statusRow = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Theme.Surface, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        statusRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 22)); statusRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        statusRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var led = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty }; statusLed = led;
        led.Paint += (_, e) =>
        {
            var color = monitor == null ? Theme.Muted : Theme.Green;
            var diameter = UiMetrics.Px(led, 10);
            using var brush = new SolidBrush(color); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias; e.Graphics.FillEllipse(brush, UiMetrics.Px(led, 2), (led.Height - diameter) / 2, diameter, diameter);
        };
        status.Font = new Font("Yu Gothic UI", 10.5f, FontStyle.Bold); status.Margin = Padding.Empty;
        detail.Font = new Font("Yu Gothic UI", 10); detail.Margin = Padding.Empty;
        queueDetail.Font = new Font("Yu Gothic UI", 9); queueDetail.Margin = Padding.Empty;
        statusRow.Controls.Add(led, 0, 0); statusRow.Controls.Add(status, 1, 0);
        action.Controls.Add(statusRow, 0, 4);
        action.Controls.Add(detail, 0, 5);
        action.Controls.Add(queueDetail, 0, 6);
        startPanel.Controls.Add(action);
        return columns;
    }

    private static Label SectionTitle(string text) => new() { Text = text, ForeColor = Theme.Text, Font = new Font("Yu Gothic UI", 12.5f, FontStyle.Bold), Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };

    private void RestoreAgent(Settings config)
    {
        Agent? Find(string id) => agents.FirstOrDefault(a => a.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        mainAgent = string.IsNullOrWhiteSpace(config.AgentId) ? null : Find(config.AgentId);
    }
    private void SetHero() { hero.Agent = mainAgent; }
    private void ConfigureRelay()
    {
        using var dialog = new NotificationRelayDialog();
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (dialog.ClearRelay) relayOptions = null;
        else if (dialog.SelectedRelay != null) relayOptions = dialog.SelectedRelay;
    }
    private void PickAgent()
    {
        using var dialog = new AgentPickerDialog(agents, mainAgent?.Id);
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.SelectedAgent == null) return;
        mainAgent = dialog.SelectedAgent;
        SetHero();
        SaveSettings();
        UpdateConnectionUi();
    }

    private Settings ReadSettings() => new Settings
    {
        AgentId = mainAgent?.Id ?? "",
        StartOnLaunch = autoStart.Checked,
        Region = RegionValues[Math.Clamp(region.SelectedIndex, 0, RegionValues.Length - 1)],
        SystemBrowserLogin = systemBrowserLogin
    }.Normalize();

    private bool SaveSettings()
    {
        try { Storage.Save(ReadSettings()); return true; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { Report("設定を保存できません"); return false; }
    }

    private void UpdateConnectionUi()
    {
        login.Text = api.HasAccountSession ? "接続済み" : loginAttempt != null ? "ログインを中止" : "Riotにログイン";
        login.Primary = false;
        login.Visible = true;
        login.Enabled = !closing && (loginAttempt != null || !busy && monitor == null && !api.HasAccountSession);
        logout.Visible = api.HasAccountSession;
        accountInfo.Text = api.HasAccountSession ? $"Riotアカウント: 接続済み · {api.AccountName} · {api.RegionLabel}" : "Riotアカウント: 未接続";
        accountInfo.ForeColor = api.HasAccountSession ? Theme.Green : Theme.Muted;
        login.AccessibleName = api.HasAccountSession ? $"接続済み: {api.AccountName} / {api.RegionLabel}" : loginAttempt != null ? "ログインを中止" : "Riotアカウントにログイン";
        start.Text = stopping ? "停止中…" : pickMonitoring ? "監視を停止" : "監視を開始";
        start.Enabled = !busy && !closing && !stopping;
        autoQueue.Text = stopping ? "停止中…" : autoQueueActive ? "自動インキュー停止" : "自動インキュー開始";
        autoQueue.Primary = autoQueueActive;
        autoQueue.Enabled = !busy && !closing && !stopping;
        queueDetail.ForeColor = autoQueueActive ? Theme.Green : Theme.Muted;
        if (!autoQueueActive) SetQueueStatus("自動インキューは停止中");
        start.Invalidate();
        autoQueue.Invalidate();
    }

    private async Task SignIn()
    {
        if (loginAttempt != null) { loginAttempt.Cancel(); return; }
        if (busy) return;
        api.Logout(); engine = null; engineSubject = "";
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        loginAttempt = attempt;
        busy = true; SetInputEnabled(false); UpdateConnectionUi();
        try
        {
            Report("ブラウザーでRiotにログインしてください");
            var result = await new SystemBrowserAuth(AuthFlow.NewState()).Run((url, _) => IsolatedBrowser.Open(url), attempt.Token);
            if (result == null) { Report("ログインできませんでした · 再度お試しください"); return; }
            await api.Login(result, ReadSettings().Region, attempt.Token);
            systemBrowserLogin = true;
            SaveSettings();
            Report("アカウントを接続しました");
        }
        catch (OperationCanceledException)
        { if (!closing) Report(attempt.IsCancellationRequested ? "ログインを中止しました" : "ログインがタイムアウトしました"); }
        catch (SocketException) { Report("ブラウザーログイン用のポート80を使用できません"); }
        catch (Exception e) { Report(LoginError(e)); }
        finally { loginAttempt = null; busy = false; SetInputEnabled(true); UpdateConnectionUi(); }
        if (api.HasAccountSession && autoStart.Checked) { pickMonitoring = true; StartMonitor(); }
    }

    private Task RestoreAccountSession(Settings config)
    {
        if (!busy && !closing && !lifetime.IsCancellationRequested &&
            (config.SystemBrowserLogin || Directory.Exists(LoginWindow.ProfileFolder)))
            Report("ブラウザーでのログインが必要です");
        return Task.CompletedTask;
    }

    private async Task SignOut(bool clearBrowserSession = true)
    {
        if (busy) return;
        busy = true;
        try
        {
            await StopMonitor(); api.Logout(); engine = null; engineSubject = "";
            if (clearBrowserSession && loginWindow != null) await loginWindow.ClearSession();
        }
        catch { Report("ログイン情報を消去できませんでした · アプリを終了してください"); }
        finally { busy = false; UpdateConnectionUi(); }
    }

    private void SetInputEnabled(bool enabled)
    {
        settingsPanel.Enabled = region.Enabled = autoStart.Enabled = enabled;
        hero.Enabled = enabled;
        login.Enabled = enabled && !api.HasAccountSession;
        logout.Enabled = enabled && api.HasAccountSession;
        start.Enabled = !busy && !closing && !stopping;
        autoQueue.Enabled = !busy && !closing && !stopping;
    }

    private async Task TogglePickMonitoring()
    {
        if (pickMonitoring)
        {
            pickMonitoring = false;
            await StopMonitor();
            return;
        }
        if (mainAgent == null) { Report("エージェントを選んでください"); return; }
        pickMonitoring = true;
        if (monitor == null) StartMonitor();
        if (monitor == null) pickMonitoring = false;
        UpdateConnectionUi();
    }

    private async Task ToggleAutoQueue()
    {
        if (autoQueueActive)
        {
            var activeQueue = queueEngine;
            autoQueueActive = false;
            if (!pickMonitoring) await StopMonitor(activeQueue);
            else
            {
                UpdateConnectionUi();
                SetQueueStatus("キュー検索をキャンセル中");
                await CancelQueueSearch(activeQueue);
                Report("自動インキューを停止しました");
            }
            return;
        }
        autoQueueActive = true;
        SetQueueStatus("キュー情報を確認中");
        if (monitor == null) StartMonitor();
        if (monitor == null) autoQueueActive = false;
        UpdateConnectionUi();
        if (autoQueueActive) Report("自動インキューを開始しました");
    }

    private void StartMonitor()
    {
        if (busy || stopping || monitor != null || !pickMonitoring && !autoQueueActive) return;
        if (!RiotApi.IsValorantRunning())
        {
            pickMonitoring = false;
            autoQueueActive = false;
            Report("ゲームが起動されていないので監視を実行できません");
            UpdateConnectionUi();
            return;
        }
        var settings = ReadSettings();
        if (pickMonitoring && string.IsNullOrEmpty(settings.AgentId)) { Report("エージェントを選んでください"); return; }
        if (!api.HasAccountSession) { Report("Riotアカウントにログインしてください"); return; }
        if (!SaveSettings()) return;
        monitor = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        queueEngine = new AutoQueueEngine(api, SetQueueStatus);
        SetInputEnabled(false);
        status.Text = "監視中"; status.ForeColor = Theme.Green;
        statusLed?.Invalidate();
        monitorTask = RunMonitor(settings, monitor.Token);
        UpdateConnectionUi();
    }

    private async Task RunMonitor(Settings settings, CancellationToken ct)
    {
        Report("マッチを検索中");
        using var notificationsStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var queueSignals = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
        Task? notifications = null;
        Task? relayNotifications = null;
        Task? notificationChecks = null;
        var retryAfter = DateTimeOffset.MinValue;
        var queueRetryAfter = DateTimeOffset.MinValue;
        var nextQueuePulse = DateTimeOffset.MinValue;
        var queueStateChanged = false;
        void Backoff(int milliseconds)
        {
            var until = DateTimeOffset.UtcNow.AddMilliseconds(milliseconds);
            if (until > retryAfter) retryAfter = until;
        }
        void QueueBackoff(int milliseconds)
        {
            var until = DateTimeOffset.UtcNow.AddMilliseconds(milliseconds);
            if (until > queueRetryAfter) queueRetryAfter = until;
        }
        void QueueChanged()
        {
            if (!autoQueueActive) return;
            queueStateChanged = true;
            queueSignals.Writer.TryWrite(true);
        }
        async Task WaitForRetry(CancellationToken token)
        {
            while (true)
            {
                var remaining = retryAfter - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero) return;
                await Task.Delay(remaining, token);
            }
        }
        async Task WaitForQueueRetry(CancellationToken token)
        {
            while (true)
            {
                var until = retryAfter > queueRetryAfter ? retryAfter : queueRetryAfter;
                var remaining = until - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero) return;
                await Task.Delay(remaining, token);
            }
        }
        async Task ProcessQueueNotifications()
        {
            while (await queueSignals.Reader.WaitToReadAsync(notificationsStop.Token))
            {
                while (queueSignals.Reader.TryRead(out _)) { }
                var stateChanged = queueStateChanged;
                queueStateChanged = false;
                await WaitForQueueRetry(notificationsStop.Token);
                if (!autoQueueActive || queueEngine == null) continue;
                try { await queueEngine.Tick(DateTimeOffset.UtcNow, notificationsStop.Token, stateChanged: stateChanged, enabled: () => autoQueueActive); }
                catch (ClientUnavailableException e) { SetQueueStatus(e.Message); QueueBackoff(ClientUnavailableIntervalMilliseconds); }
                catch (ApiException e)
                {
                    if (e.Status is 401 or 403) { api.Logout(); Report("ログインが失効しました"); monitor?.Cancel(); return; }
                    var message = e.Status == 429 ? "混雑中 · 自動で再接続します" : "接続を再確認中";
                    SetQueueStatus(message);
                    if (e.Status == 429) Backoff(e.RetrySeconds * 1000);
                    else QueueBackoff(TransientFailureIntervalMilliseconds);
                }
                catch (OperationCanceledException) when (!notificationsStop.IsCancellationRequested) { SetQueueStatus("タイムアウト · 再接続中"); QueueBackoff(TransientFailureIntervalMilliseconds); }
                catch (HttpRequestException) { SetQueueStatus("ネットワークを再確認中"); QueueBackoff(TransientFailureIntervalMilliseconds); }
                catch (Exception e) when (e is JsonException or InvalidDataException or KeyNotFoundException)
                { SetQueueStatus("ゲームの応答を確認できません"); QueueBackoff(10000); }
            }
        }
        try
        {
            while (!ct.IsCancellationRequested && (pickMonitoring || autoQueueActive))
            {
                var interval = pickMonitoring ? FastPollIntervalMilliseconds : ClientUnavailableIntervalMilliseconds;
                try
                {
                    await WaitForRetry(ct);
                    if (DateTimeOffset.UtcNow >= api.ExpiresAt.AddMinutes(-3))
                    {
                        Report("ログインを更新中");
                        if (!await api.TryRefreshAccountFromLocal(settings.Region, ct))
                        { api.Logout(); Report("再ログインが必要です"); break; }
                        UpdateConnectionUi();
                    }
                    if (autoQueueActive && notifications == null)
                    {
                        notificationChecks = ProcessQueueNotifications();
                        notifications = RiotLocalEvents.Listen(api, QueueChanged, notificationsStop.Token);
                        if (relayOptions is { } options)
                        {
                            relayNotifications = NotificationRelayClient.Listen(options, () => api.Subject, QueueChanged,
                                _ => { }, notificationsStop.Token);
                        }
                    }
                    if (pickMonitoring)
                    {
                        if (engine == null || engineSubject != api.Subject) { engine = new PickEngine(api, Report, api.IsCharacterSelectReady); engineSubject = api.Subject; }
                        await engine.Tick(settings, DateTimeOffset.UtcNow, ct);
                    }
                    if (autoQueueActive && DateTimeOffset.UtcNow >= nextQueuePulse)
                    {
                        nextQueuePulse = DateTimeOffset.UtcNow.AddMilliseconds(ClientUnavailableIntervalMilliseconds);
                        queueSignals.Writer.TryWrite(false);
                    }
                }
                catch (ClientUnavailableException e) { Report(e.Message); if (autoQueueActive) SetQueueStatus(e.Message); interval = ClientUnavailableIntervalMilliseconds; }
                catch (ApiException e)
                {
                    if (e.Status is 401 or 403) { api.Logout(); Report("ログインが失効しました"); break; }
                    var message = e.Status == 429 ? "混雑中 · 自動で再接続します" : e.Status == 404 ? "マッチを検索中" : "接続を再確認中";
                    Report(message);
                    if (autoQueueActive) SetQueueStatus(message);
                    interval = e.Status == 429 ? e.RetrySeconds * 1000 : TransientFailureIntervalMilliseconds;
                    if (e.Status == 429) Backoff(interval);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { Report("タイムアウト · 再接続中"); if (autoQueueActive) SetQueueStatus("タイムアウト · 再接続中"); interval = TransientFailureIntervalMilliseconds; }
                catch (HttpRequestException) { Report("ネットワークを再確認中"); if (autoQueueActive) SetQueueStatus("ネットワークを再確認中"); interval = TransientFailureIntervalMilliseconds; }
                catch (Exception e) when (e is JsonException or InvalidDataException or KeyNotFoundException)
                { Report("ゲームの応答を確認できません"); if (autoQueueActive) SetQueueStatus("ゲームの応答を確認できません"); interval = 10000; }
                await Task.Delay(interval, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch { Report("監視を停止しました · 再接続してお試しください"); }
        finally
        {
            notificationsStop.Cancel();
            if (notifications != null) { try { await notifications; } catch (Exception) { } }
            if (relayNotifications != null) { try { await relayNotifications; } catch (Exception) { } }
            if (notificationChecks != null) { try { await notificationChecks; } catch (Exception) { } }
            monitor?.Dispose(); monitor = null;
            monitorTask = null; queueEngine = null; pickMonitoring = false; autoQueueActive = false;
            SetInputEnabled(true);
            status.Text = "停止中"; status.ForeColor = Theme.Muted;
            statusLed?.Invalidate();
            UpdateConnectionUi();
        }
    }

    private Task StopMonitor(AutoQueueEngine? queueToCancel = null)
    {
        if (stopTask is { IsCompleted: false }) return stopTask;
        var activeQueue = queueToCancel ?? (autoQueueActive ? queueEngine : null);
        pickMonitoring = false; autoQueueActive = false;
        var source = monitor;
        if (source == null && activeQueue == null) { UpdateConnectionUi(); return Task.CompletedTask; }

        stopping = true;
        status.Text = "停止中"; status.ForeColor = Theme.Muted;
        Report("監視を停止しています");
        statusLed?.Invalidate();
        UpdateConnectionUi();
        try { source?.Cancel(); }
        catch (ObjectDisposedException) { }
        stopTask = CompleteStop(monitorTask, activeQueue);
        return stopTask;
    }

    private async Task CompleteStop(Task? running, AutoQueueEngine? activeQueue)
    {
        string? finalQueueStatus = null;
        try
        {
            if (running != null) await running;
            if (activeQueue != null)
            {
                await CancelQueueSearch(activeQueue);
                finalQueueStatus = lastQueueStatus;
            }
            else if (queueLeaveTask is { IsCompleted: false }) await queueLeaveTask;
        }
        finally
        {
            stopping = false;
            stopTask = null;
            UpdateConnectionUi();
            if (finalQueueStatus != null) SetQueueStatus(finalQueueStatus);
        }
        Report("監視を停止しました");
    }

    private Task CancelQueueSearch(AutoQueueEngine? activeQueue)
    {
        if (activeQueue == null) return Task.CompletedTask;
        if (queueLeaveTask is { IsCompleted: false }) return queueLeaveTask;
        queueLeaveTask = LeaveQueueSearch(activeQueue);
        return queueLeaveTask;
    }

    private async Task LeaveQueueSearch(AutoQueueEngine activeQueue)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await activeQueue.LeaveActiveQueue(timeout.Token);
        }
        catch
        {
            SetQueueStatus("キューをキャンセルできません · VALORANT側で確認してください");
            Report("キューの停止を確認できませんでした");
        }
        finally { queueLeaveTask = null; }
    }

    private async Task RefreshAgents()
    {
        login.Enabled = false;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            using var data = JsonDocument.Parse(await http.GetStringAsync("https://valorant-api.com/v1/agents?isPlayableCharacter=true&language=ja-JP", lifetime.Token));
            var updated = data.RootElement.GetProperty("data").EnumerateArray().Select(a =>
            {
                var role = a.TryGetProperty("role", out var r) && r.ValueKind == JsonValueKind.Object ? r : default;
                return new Agent(a.GetProperty("uuid").GetString()!, a.GetProperty("displayName").GetString()!, role.ValueKind == JsonValueKind.Object && role.TryGetProperty("displayName", out var name) ? name.GetString() ?? "" : "",
                    role.ValueKind == JsonValueKind.Object && role.TryGetProperty("uuid", out var id) ? id.GetString() ?? "" : "");
            }).Where(a => Guid.TryParse(a.Id, out _)).ToList();
            if (updated.Count < 10) throw new InvalidDataException();
            Directory.CreateDirectory(Storage.Folder);
            File.WriteAllText(Path.Combine(Storage.Folder, "agents.json"), JsonSerializer.Serialize(updated));
            var selection = mainAgent?.Id; agents = updated;
            mainAgent = selection == null ? null : agents.FirstOrDefault(a => a.Id == selection);
            SetHero();
            Report("エージェント一覧を更新しました");
        }
        catch { Report("一覧を更新できませんでした"); }
        finally { login.Enabled = !api.HasAccountSession && monitor == null && !busy; }
    }

    private void Report(string text)
    {
        if (IsDisposed || text == lastStatus) return;
        lastStatus = text; detail.Text = text; detail.Invalidate();
    }
    private void SetQueueStatus(string text)
    {
        if (IsDisposed || text == lastQueueStatus) return;
        lastQueueStatus = text; queueDetail.Text = text; queueDetail.Invalidate();
    }
    private static string LoginError(Exception e) => e switch
    {
        Microsoft.Web.WebView2.Core.WebView2RuntimeNotFoundException => "ログインにはMicrosoft Edge WebView2 Runtimeが必要です",
        BrowserIsolationException b => b.Message,
        ApiException a => $"接続できませんでした · {a.Status}",
        ClientUnavailableException c => c.Message,
        _ => "接続できませんでした · ネットワークをご確認ください"
    };
    private void Restore() { Show(); WindowState = FormWindowState.Normal; Activate(); }
    private static Label FieldLabel(string text) => new() { Text = text, Dock = DockStyle.Fill, Margin = new Padding(0, 1, 5, 0), ForeColor = Theme.Muted, TextAlign = ContentAlignment.MiddleLeft, Font = new Font("Yu Gothic UI", 10) };
    private static ThemedSelect RegionCombo() => new(["自動（おすすめ）", "アジア太平洋（東京など）", "北アメリカ", "ヨーロッパ", "韓国", "ブラジル", "ラテンアメリカ", "テスト環境（PBE）"])
    { AccessibleName = "アカウントの接続地域" };
}
