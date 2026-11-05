using System.Drawing.Drawing2D;
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
    private readonly ThemedSelect mode = ModeCombo(), region = RegionCombo();
    private readonly SwitchToggle autoStart = new("自動で監視開始", false);
    private readonly ThemedButton login = new() { Text = "Riotにログイン", Size = new(196, 48), Compact = true };
    // Leave enough horizontal room for Japanese glyphs plus the button's text padding.
    private readonly ThemedButton logout = new() { Text = "ログアウト", Size = new(132, 48), Compact = true };
    private readonly ThemedButton relayConfig = new() { Text = "通知連携", Size = new(160, 48), Compact = true };
    private readonly ThemedButton start = new() { Text = "監視を開始", Primary = true, Font = new Font("Yu Gothic UI", 17f, FontStyle.Bold) };
    private readonly ThemedButton autoQueue = new() { Text = "自動インキュー開始", Font = new Font("Yu Gothic UI", 15f, FontStyle.Bold), AccessibleDescription = "ゲーム内で選択中のモードに自動で参加します" };
    private readonly HeroPanel hero = new();
    private readonly RoundedCard settingsPanel = new() { Dock = DockStyle.Fill };
    private readonly RoundedCard startPanel = new() { Dock = DockStyle.Fill };
    private readonly Label status = new() { Text = "停止中", ForeColor = Theme.Muted, Font = new Font("Yu Gothic UI", 9f, FontStyle.Bold), AutoSize = false, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill };
    private readonly Label detail = new() { Text = "", ForeColor = Theme.Muted, Font = new Font("Yu Gothic UI", 9), AutoSize = false, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill };
    private readonly Label queueDetail = new() { Text = "自動インキューは停止中", ForeColor = Theme.Muted, Font = new Font("Yu Gothic UI", 9), AutoSize = false, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill };
    private readonly Label accountInfo = new() { Text = "未接続", ForeColor = Theme.Muted, Font = new Font("Yu Gothic UI", 11), AutoSize = false, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill };
    private readonly NotifyIcon tray = new() { Icon = LoadAppIcon(), Text = "Valo Auto Pick", Visible = true };
    private readonly RiotApi api = new();
    private readonly CancellationTokenSource lifetime = new();
    private LoginWindow? loginWindow;
    private CancellationTokenSource? monitor;
    private Task? monitorTask;
    private Task? stopTask;
    private Task? queueLeaveTask;
    private PickEngine? engine;
    private AutoQueueEngine? queueEngine;
    private RelayOptions? relayOptions;
    private string engineSubject = "", lastStatus = "", lastQueueStatus = "";
    private bool closing, busy, stopping, pickMonitoring, autoQueueActive;
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
        ClientSize = new Size(1160, 800); MinimumSize = new Size(1000, 720); StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Background; ForeColor = Theme.Text; Font = new Font("Yu Gothic UI", 11.5f);

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
        if (previewAgentId != null) mainAgent = agents.FirstOrDefault(a => a.Id.Equals(previewAgentId, StringComparison.OrdinalIgnoreCase));
        autoStart.Checked = config.StartOnLaunch;
        mode.SelectedIndex = config.AccountMode ? 0 : 1;
        region.SelectedIndex = Math.Max(0, Array.IndexOf(RegionValues, config.Region));

        var root = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Background, AutoScroll = true };
        var header = BuildHeader(); header.Dock = DockStyle.None; header.TabIndex = 0;
        var dashboard = BuildDashboard(); dashboard.Dock = DockStyle.None; dashboard.TabIndex = 1;
        root.Controls.Add(dashboard); root.Controls.Add(header);
        Controls.Add(root);
        void ArrangeRoot()
        {
            var width = Math.Max(UiMetrics.Px(this, 912), root.ClientSize.Width - UiMetrics.Px(this, 48));
            var height = Math.Max(UiMetrics.Px(this, 680), root.ClientSize.Height - UiMetrics.Px(this, 108));
            var offset = root.AutoScrollPosition;
            header.Bounds = new Rectangle(offset.X + UiMetrics.Px(this, 24), offset.Y + UiMetrics.Px(this, 16), width, UiMetrics.Px(this, 60));
            dashboard.Bounds = new Rectangle(offset.X + UiMetrics.Px(this, 24), offset.Y + UiMetrics.Px(this, 84), width, height);
        }
        root.Layout += (_, _) => ArrangeRoot();
        ArrangeRoot();

        SetHero();
        hero.ChooseClicked += (_, _) => PickAgent();
        login.Click += async (_, _) => await SignIn();
        logout.Click += async (_, _) => await SignOut();
        start.Click += async (_, _) => await TogglePickMonitoring();
        autoQueue.Click += async (_, _) => await ToggleAutoQueue();
        relayConfig.Click += (_, _) => ConfigureRelay();
        mode.SelectedIndexChanged += async (_, _) => { SaveSettings(); await SignOut(clearBrowserSession: false); UpdateConnectionUi(); };
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
        Load += (_, _) => { ActiveControl = login.Visible ? login : mode; root.AutoScrollPosition = Point.Empty; };

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
            else if (config.AccountMode)
                await RestoreAccountSession(config);
            else if (config.StartOnLaunch && !string.IsNullOrEmpty(config.AgentId))
            { pickMonitoring = true; StartMonitor(); }
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
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = new GraphicsPath(); path.AddPolygon([new Point(3, 4), new Point(24, 4), new Point(36, 16), new Point(24, 40), new Point(3, 40), new Point(15, 22)]);
            using var brush = new SolidBrush(Theme.Red); e.Graphics.FillPath(brush, path);
            using var cut = new SolidBrush(Theme.Background); cut.Color = Color.FromArgb(95, Theme.Background);
            e.Graphics.FillPolygon(cut, [new Point(18, 10), new Point(23, 10), new Point(13, 32), new Point(8, 32)]);
        };
        brand.Controls.Add(new Label { Text = "VALBUDDY", ForeColor = Theme.Text, Font = new Font("Segoe UI", 20, FontStyle.Bold), AutoSize = true, Location = new Point(48, 2) });
        var loginBar = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(0, 5, 0, 0), Margin = Padding.Empty };
        logout.Margin = new Padding(8, 0, 0, 0); login.Margin = Padding.Empty;
        relayConfig.Margin = new Padding(0, 0, 8, 0);
        loginBar.Controls.Add(relayConfig); loginBar.Controls.Add(login); loginBar.Controls.Add(logout);
        header.Controls.Add(brand, 0, 0); header.Controls.Add(loginBar, 1, 0);
        return header;
    }

    private Control BuildDashboard()
    {
        var columns = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Theme.Background, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty, Padding = new Padding(0, 8, 0, 8) };
        columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 54)); columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 46));
        columns.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        hero.Dock = DockStyle.Fill; hero.Margin = new Padding(0, 0, 20, 0);
        settingsPanel.Margin = new Padding(0, 0, 0, 16); startPanel.Margin = Padding.Empty;

        var sidebar = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Theme.Background, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
        sidebar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, 344)); sidebar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        sidebar.Controls.Add(settingsPanel, 0, 0);
        sidebar.Controls.Add(startPanel, 0, 1);
        columns.Controls.Add(hero, 0, 0); columns.Controls.Add(sidebar, 1, 0);

        var connection = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Theme.Surface, Padding = new Padding(10, 4, 10, 4), RowCount = 7, ColumnCount = 1, Margin = Padding.Empty };
        connection.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        connection.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        connection.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        connection.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        connection.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        connection.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
        connection.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        connection.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        connection.Controls.Add(SectionTitle("接続設定"), 0, 0);
        connection.Controls.Add(FieldLabel("接続方法"), 0, 1);
        mode.Dock = DockStyle.Fill; mode.Margin = Padding.Empty; connection.Controls.Add(mode, 0, 2);
        connection.Controls.Add(accountInfo, 0, 3);
        var regionRow = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Theme.Surface, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        regionRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80)); regionRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        regionRow.Controls.Add(FieldLabel("接続地域"), 0, 0); region.Dock = DockStyle.Fill; region.Margin = Padding.Empty; regionRow.Controls.Add(region, 1, 0);
        connection.Controls.Add(regionRow, 0, 4);
        autoStart.Margin = new Padding(0, 8, 0, 0); autoStart.Dock = DockStyle.Fill; connection.Controls.Add(autoStart, 0, 5);
        settingsPanel.Controls.Add(connection);

        var action = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Theme.Surface, Padding = new Padding(10, 8, 10, 8), RowCount = 8, ColumnCount = 1, Margin = Padding.Empty };
        action.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        action.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        action.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        action.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
        action.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
        action.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        action.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        action.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
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
        status.Font = new Font("Yu Gothic UI", 11.5f, FontStyle.Bold); status.Margin = Padding.Empty;
        detail.Font = new Font("Yu Gothic UI", 11); detail.Margin = Padding.Empty;
        queueDetail.Font = new Font("Yu Gothic UI", 10); queueDetail.Margin = Padding.Empty;
        statusRow.Controls.Add(led, 0, 0); statusRow.Controls.Add(status, 1, 0);
        action.Controls.Add(statusRow, 0, 4);
        action.Controls.Add(detail, 0, 5);
        action.Controls.Add(queueDetail, 0, 6);
        startPanel.Controls.Add(action);
        return columns;
    }

    private static Label SectionTitle(string text) => new() { Text = text, ForeColor = Theme.Text, Font = new Font("Yu Gothic UI", 14f, FontStyle.Bold), Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };

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
        relayConfig.Text = relayOptions == null ? "通知連携" : "通知連携: 設定済";
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
        AccountMode = mode.SelectedIndex == 0, Region = RegionValues[Math.Clamp(region.SelectedIndex, 0, RegionValues.Length - 1)]
    }.Normalize();

    private bool SaveSettings()
    {
        try { Storage.Save(ReadSettings()); return true; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { Report("設定を保存できません"); return false; }
    }

    private void UpdateConnectionUi()
    {
        var accountMode = mode.SelectedIndex == 0;
        login.Text = api.HasAccountSession ? "接続済み" : "Riotにログイン";
        login.Primary = false;
        login.Visible = accountMode;
        login.Enabled = accountMode && !busy && monitor == null && !api.HasAccountSession;
        logout.Visible = accountMode && api.HasAccountSession;
        relayConfig.Enabled = monitor == null && !busy && !closing && !stopping;
        mode.Enabled = monitor == null && !busy;
        accountInfo.Text = accountMode ? api.HasAccountSession ? $"{api.AccountName}  ·  {api.RegionLabel}" : "Riotアカウント未接続" : "このPCのRiot Clientを使用";
        login.AccessibleName = api.HasAccountSession ? $"接続済み: {api.AccountName} / {api.RegionLabel}" : "Riotアカウントにログイン";
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
        if (busy) return;
        api.Logout(); engine = null; engineSubject = "";
        busy = true; SetInputEnabled(false); UpdateConnectionUi();
        try
        {
            loginWindow ??= new LoginWindow();
            var result = await loginWindow.Authenticate(this, false, lifetime.Token);
            if (result == null) { Report("ログインできませんでした · 再度お試しください"); return; }
            await api.Login(result, ReadSettings().Region, lifetime.Token);
            Report("アカウントを接続しました");
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { Report(LoginError(e)); }
        finally { busy = false; SetInputEnabled(true); UpdateConnectionUi(); }
        if (api.HasAccountSession && autoStart.Checked) { pickMonitoring = true; StartMonitor(); }
    }

    private async Task RestoreAccountSession(Settings config)
    {
        if (busy || closing || lifetime.IsCancellationRequested) return;
        if (!Directory.Exists(LoginWindow.ProfileFolder)) return;
        busy = true; SetInputEnabled(false); UpdateConnectionUi();
        Report("保存済みのRiotログインを確認中");
        try
        {
            loginWindow ??= new LoginWindow();
            var result = await loginWindow.Authenticate(this, true, lifetime.Token);
            if (result == null)
            {
                Report("保存済みログインを復元できません · Riotにログインしてください");
                return;
            }
            await api.Login(result, config.Region, lifetime.Token);
            Report("前回のRiotログインを復元しました");
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { Report(LoginError(e)); }
        finally { busy = false; SetInputEnabled(true); UpdateConnectionUi(); }

        if (api.HasAccountSession && config.StartOnLaunch && !string.IsNullOrEmpty(config.AgentId))
        { pickMonitoring = true; StartMonitor(); }
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
        settingsPanel.Enabled = mode.Enabled = region.Enabled = autoStart.Enabled = enabled;
        hero.Enabled = enabled;
        login.Enabled = enabled && mode.SelectedIndex == 0 && !api.HasAccountSession;
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
        var settings = ReadSettings();
        if (pickMonitoring && string.IsNullOrEmpty(settings.AgentId)) { Report("エージェントを選んでください"); return; }
        if (settings.AccountMode && !api.HasAccountSession) { Report("Riotアカウントにログインしてください"); return; }
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
        void Backoff(int milliseconds)
        {
            var until = DateTimeOffset.UtcNow.AddMilliseconds(milliseconds);
            if (until > retryAfter) retryAfter = until;
        }
        void QueueChanged()
        {
            if (!autoQueueActive) return;
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
        async Task ProcessQueueNotifications()
        {
            while (await queueSignals.Reader.WaitToReadAsync(notificationsStop.Token))
            {
                while (queueSignals.Reader.TryRead(out _)) { }
                await WaitForRetry(notificationsStop.Token);
                if (!autoQueueActive || queueEngine == null) continue;
                try { await queueEngine.Tick(DateTimeOffset.UtcNow, notificationsStop.Token, stateChanged: true, enabled: () => autoQueueActive); }
                catch (ClientUnavailableException e) { SetQueueStatus(e.Message); Backoff(ClientUnavailableIntervalMilliseconds); }
                catch (ApiException e)
                {
                    if (e.Status is 401 or 403 && settings.AccountMode) { api.Logout(); Report("ログインが失効しました"); monitor?.Cancel(); return; }
                    var message = e.Status == 429 ? "混雑中 · 自動で再接続します" : "接続を再確認中";
                    SetQueueStatus(message);
                    Backoff(e.Status == 429 ? e.RetrySeconds * 1000 : TransientFailureIntervalMilliseconds);
                }
                catch (OperationCanceledException) when (!notificationsStop.IsCancellationRequested) { SetQueueStatus("タイムアウト · 再接続中"); Backoff(TransientFailureIntervalMilliseconds); }
                catch (HttpRequestException) { SetQueueStatus("ネットワークを再確認中"); Backoff(TransientFailureIntervalMilliseconds); }
                catch (Exception e) when (e is JsonException or InvalidDataException or KeyNotFoundException)
                { SetQueueStatus("ゲームの応答を確認できません"); Backoff(10000); }
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
                    if (settings.AccountMode)
                    {
                        if (DateTimeOffset.UtcNow >= api.ExpiresAt.AddMinutes(-3))
                        {
                            Report("ログインを更新中");
                            var result = loginWindow == null ? null : await loginWindow.Authenticate(this, true, ct);
                            if (result == null) { api.Logout(); Report("再ログインが必要です"); break; }
                            await api.Login(result, settings.Region, ct); UpdateConnectionUi();
                        }
                    }
                    else await api.EnsureConnected(settings.Region, ct);
                    if (autoQueueActive && notifications == null)
                    {
                        notificationChecks = ProcessQueueNotifications();
                        notifications = RiotLocalEvents.Listen(api, QueueChanged, notificationsStop.Token);
                        if (relayOptions is { } options)
                        {
                            relayNotifications = NotificationRelayClient.Listen(options, () => api.Subject, QueueChanged,
                                connected => { if (!IsDisposed) relayConfig.Text = connected ? "通知連携: 接続中" : "通知連携: 待機中"; }, notificationsStop.Token);
                        }
                    }
                    if (pickMonitoring)
                    {
                        if (engine == null || engineSubject != api.Subject) { engine = new PickEngine(api, Report); engineSubject = api.Subject; }
                        await engine.Tick(settings, DateTimeOffset.UtcNow, ct);
                    }
                    if (autoQueueActive && queueEngine != null) await queueEngine.Tick(DateTimeOffset.UtcNow, ct, enabled: () => autoQueueActive);
                }
                catch (ClientUnavailableException e) { Report(e.Message); if (autoQueueActive) SetQueueStatus(e.Message); interval = ClientUnavailableIntervalMilliseconds; }
                catch (ApiException e)
                {
                    if (e.Status is 401 or 403 && settings.AccountMode) { api.Logout(); Report("ログインが失効しました"); break; }
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
            relayConfig.Text = relayOptions == null ? "通知連携" : "通知連携: 設定済";
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
        finally { login.Enabled = mode.SelectedIndex == 0 && !api.HasAccountSession && monitor == null && !busy; }
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
        ApiException a => $"接続できませんでした · {a.Status}",
        ClientUnavailableException c => c.Message,
        _ => "接続できませんでした · ネットワークをご確認ください"
    };
    private void Restore() { Show(); WindowState = FormWindowState.Normal; Activate(); }
    private static Label FieldLabel(string text) => new() { Text = text, Dock = DockStyle.Fill, Margin = new Padding(0, 1, 5, 0), ForeColor = Theme.Muted, TextAlign = ContentAlignment.MiddleLeft, Font = new Font("Yu Gothic UI", 11) };
    private static ThemedSelect ModeCombo() => new(["別PCから接続", "このPCのRiot Client"]);
    private static ThemedSelect RegionCombo() => new(["自動（おすすめ）", "アジア太平洋（東京など）", "北アメリカ", "ヨーロッパ", "韓国", "ブラジル", "ラテンアメリカ", "テスト環境（PBE）"])
    { AccessibleName = "アカウントの接続地域" };
}
