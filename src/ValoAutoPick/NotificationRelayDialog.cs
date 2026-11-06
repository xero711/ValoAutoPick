using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace ValoAutoPick;

internal sealed class NotificationRelayDialog : DarkForm
{
    private readonly TextBox address = Input(), code = Input();
    private readonly Label receiveStatus = MessageLabel();
    private readonly Label hostStatus = MessageLabel();
    private readonly string hostCode = RelayProtocol.NewCode();
    private NotificationRelayServer? host;
    private CancellationTokenSource? hostStop;
    private RiotApi? hostApi;
    private Task? hostTask;
    internal RelayOptions? SelectedRelay { get; private set; }
    internal bool ClearRelay { get; private set; }

    internal NotificationRelayDialog()
    {
        Text = "ゲーム終了通知の連携";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(640, 470); MinimumSize = new Size(580, 430);
        BackColor = Theme.Background; ForeColor = Theme.Text;
        var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(12, 5) };
        var receive = new TabPage("操作するPC") { BackColor = Theme.Surface, ForeColor = Theme.Text, Padding = new Padding(18) };
        var send = new TabPage("ゲームを動かすPC") { BackColor = Theme.Surface, ForeColor = Theme.Text, Padding = new Padding(18) };
        tabs.TabPages.Add(receive); tabs.TabPages.Add(send); Controls.Add(tabs);

        var receiveLayout = NewLayout(8);
        receiveLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 66));
        receiveLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        receiveLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        receiveLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        receiveLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        receiveLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        receiveLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        receiveLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        receiveLayout.Controls.Add(Description("ゲーム側PCで表示されたIPアドレスと接続コードを入力してください。両方のPCを同じネットワークに接続します。"), 0, 0);
        receiveLayout.Controls.Add(Description("ゲーム側PCのIPv4アドレス"), 0, 1);
        receiveLayout.Controls.Add(address, 0, 2);
        receiveLayout.Controls.Add(Description("接続コード"), 0, 3);
        receiveLayout.Controls.Add(code, 0, 4);
        receiveLayout.Controls.Add(receiveStatus, 0, 5);
        var receiveButtons = Buttons();
        var connect = Button("設定して閉じる");
        var clear = Button("設定を解除");
        connect.Click += (_, _) =>
        {
            if (!IPAddress.TryParse(address.Text.Trim(), out var ip) || ip.AddressFamily != AddressFamily.InterNetwork || IPAddress.Any.Equals(ip) || IPAddress.Broadcast.Equals(ip) || IPAddress.IsLoopback(ip))
            { receiveStatus.Text = "ゲーム側PCのIPv4アドレスを入力してください。"; return; }
            if (!RelayProtocol.TryParseCode(code.Text, out var key))
            { receiveStatus.Text = "ゲーム側PCに表示された接続コードを入力してください。"; return; }
            SelectedRelay = new RelayOptions(ip, key);
            DialogResult = DialogResult.OK; Close();
        };
        clear.Click += (_, _) => { ClearRelay = true; DialogResult = DialogResult.OK; Close(); };
        receiveButtons.Controls.Add(connect); receiveButtons.Controls.Add(clear);
        receiveLayout.Controls.Add(receiveButtons, 0, 6); receive.Controls.Add(receiveLayout);

        var sendLayout = NewLayout(8);
        sendLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 78));
        sendLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        sendLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        sendLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        sendLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        sendLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        sendLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        sendLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        sendLayout.Controls.Add(Description("VALORANTにログインしたこのPCで通知を送信します。操作するPCに下の情報を入力し、この画面を開いたままにしてください。"), 0, 0);
        sendLayout.Controls.Add(Description("このPCのIPv4アドレス"), 0, 1);
        var addresses = Input(); addresses.Multiline = true; addresses.ReadOnly = true; addresses.Text = LocalAddresses();
        sendLayout.Controls.Add(addresses, 0, 2);
        sendLayout.Controls.Add(Description("接続コード"), 0, 3);
        var hostCodeBox = Input(); hostCodeBox.ReadOnly = true; hostCodeBox.Text = hostCode;
        sendLayout.Controls.Add(hostCodeBox, 0, 4);
        hostStatus.Text = "送信は停止中です。"; sendLayout.Controls.Add(hostStatus, 0, 5);
        var hostButtons = Buttons();
        var start = Button("通知の送信を開始");
        var stopButton = Button("送信を停止");
        start.Click += (_, _) => StartHost(); stopButton.Click += (_, _) => StopHost();
        hostButtons.Controls.Add(start); hostButtons.Controls.Add(stopButton);
        sendLayout.Controls.Add(hostButtons, 0, 6); send.Controls.Add(sendLayout);
        FormClosed += (_, _) => StopHost();
    }

    private void StartHost()
    {
        if (host != null) return;
        if (!RelayProtocol.TryParseCode(hostCode, out var key)) return;
        var server = new NotificationRelayServer(IPAddress.Any, RelayProtocol.Port, key);
        try { server.Start(); }
        catch (SocketException) { server.Dispose(); hostStatus.Text = "送信を開始できません。別の通知送信が動いていないか確認してください。"; return; }
        host = server; hostStop = new CancellationTokenSource(); hostApi = new RiotApi();
        hostStatus.Text = "Riot Clientへの接続を待っています。";
        hostTask = RiotLocalEvents.ListenAny(hostApi,
            subject => { server.SetSubject(subject); SetHostStatus("通知を送信できます。画面を開いたままにしてください。"); },
            subject => server.Publish(subject),
            () => { server.SetSubject(""); SetHostStatus("Riot Clientへの接続を待っています。"); }, hostStop.Token);
    }

    private void StopHost()
    {
        var server = host; var source = hostStop; var api = hostApi; var task = hostTask;
        host = null; hostStop = null; hostApi = null; hostTask = null;
        if (server == null) return;
        source?.Cancel(); server.Dispose();
        _ = FinishHost(task, api, source);
        SetHostStatus("送信は停止中です。");
    }

    private static async Task FinishHost(Task? task, RiotApi? api, CancellationTokenSource? source)
    {
        try { if (task != null) await task; } catch (Exception) { }
        api?.Dispose(); source?.Dispose();
    }

    private void SetHostStatus(string text) { if (!IsDisposed && !Disposing) hostStatus.Text = text; }
    private static TableLayoutPanel NewLayout(int rows) => new() { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = rows, BackColor = Theme.Surface };
    private static Label Description(string text) => new() { Text = text, Dock = DockStyle.Fill, ForeColor = Theme.Text, TextAlign = ContentAlignment.MiddleLeft };
    private static Label MessageLabel() => new() { Dock = DockStyle.Fill, ForeColor = Theme.Muted, TextAlign = ContentAlignment.MiddleLeft };
    private static TextBox Input() => new() { Dock = DockStyle.Fill, BackColor = Theme.Elevated, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle };
    private static FlowLayoutPanel Buttons() => new() { Dock = DockStyle.Fill, WrapContents = false, FlowDirection = FlowDirection.LeftToRight };
    private static Button Button(string text) => new AnimatedNativeButton { Text = text, AutoSize = true, Height = 38, FlatStyle = FlatStyle.Flat, BackColor = Theme.Elevated, ForeColor = Theme.Text, Margin = new Padding(0, 0, 12, 0) };

    private static string LocalAddresses()
    {
        try
        {
            var list = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(x => x.Address)
                .Where(ip => ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
                .Select(ip => ip.ToString()).Distinct().ToArray();
            return list.Length == 0 ? "ネットワーク接続を確認してください" : string.Join("   ", list);
        }
        catch (NetworkInformationException) { return "ネットワーク接続を確認してください"; }
    }
}
