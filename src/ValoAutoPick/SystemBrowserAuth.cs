using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ValoAutoPick;

// The registered Riot desktop callback is http://localhost/redirect. The
// browser delivers the URL fragment only to its page, not in the HTTP request;
// the page forwards it to this loopback-only listener for state validation.
internal sealed class SystemBrowserAuth(string state, int port = 80)
{
    private const int MaxHeaderBytes = 16 * 1024;
    private const int MaxBodyBytes = 32 * 1024;
    private static readonly byte[] CallbackPage = Encoding.UTF8.GetBytes("""
        <!doctype html><meta charset="utf-8"><title>QuickPickerPro</title>
        <p id="message">ログイン結果をアプリに送信しています…</p>
        <script>
        const result = location.hash;
        history.replaceState(null, '', '/redirect');
        if (!result) {
            document.getElementById('message').textContent = 'ログイン結果がありません。アプリからやり直してください。';
        } else {
            fetch('/auth-result', {method:'POST', headers:{'Content-Type':'text/plain;charset=UTF-8'}, body:result, cache:'no-store'})
                .then(r => { document.getElementById('message').textContent = r.ok ? 'アプリに戻ってください。このタブは閉じられます。' : 'ログイン結果を確認できません。アプリからやり直してください。'; })
                .catch(() => { document.getElementById('message').textContent = 'アプリに接続できません。アプリからやり直してください。'; });
        }
        </script>
        """);
    private readonly TaskCompletionSource<AuthResult?> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal async Task<AuthResult?> Run(Func<Uri, int, Task> openBrowser, CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stop.CancelAfter(TimeSpan.FromMinutes(10));
        var listeners = new List<TcpListener>();
        var loops = new List<Task>();
        try
        {
            var addresses = await Dns.GetHostAddressesAsync("localhost", stop.Token);
            if (!SafeLocalAddresses(addresses)) throw new InvalidOperationException("localhostの接続先を確認できません");
            var ipv4 = new TcpListener(IPAddress.Loopback, port);
            ipv4.ExclusiveAddressUse = true;
            try { ipv4.Start(); listeners.Add(ipv4); }
            catch { ipv4.Stop(); throw; }
            var actualPort = ((IPEndPoint)ipv4.LocalEndpoint).Port;
            if (Socket.OSSupportsIPv6)
            {
                var ipv6 = new TcpListener(IPAddress.IPv6Loopback, actualPort);
                ipv6.ExclusiveAddressUse = true;
                try { ipv6.Start(); listeners.Add(ipv6); }
                catch { ipv6.Stop(); throw; }
            }
            foreach (var listener in listeners) loops.Add(Accept(listener, actualPort, stop.Token));
            stop.Token.ThrowIfCancellationRequested();
            await openBrowser(new Uri(AuthFlow.SystemBrowserUrl(state)), actualPort);
            return await completion.Task.WaitAsync(stop.Token);
        }
        finally
        {
            stop.Cancel();
            foreach (var listener in listeners) listener.Stop();
            foreach (var loop in loops)
                try { await loop; } catch (OperationCanceledException) { } catch (SocketException) { }
        }
    }

    internal static bool SafeLocalAddresses(IPAddress[] addresses) => addresses.Length > 0 &&
        addresses.All(address => address.Equals(IPAddress.Loopback) || address.Equals(IPAddress.IPv6Loopback));

    private async Task Accept(TcpListener listener, int actualPort, CancellationToken ct)
    {
        using var slots = new SemaphoreSlim(4, 4);
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(ct); }
            catch (ObjectDisposedException) when (ct.IsCancellationRequested) { break; }
            if (!slots.Wait(0)) { client.Dispose(); continue; }
            _ = Handle(client, actualPort, slots, ct);
        }
    }

    private async Task Handle(TcpClient client, int actualPort, SemaphoreSlim slots, CancellationToken ct)
    {
        try
        {
            using (client)
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                if (client.Client.RemoteEndPoint is not IPEndPoint peer || !IPAddress.IsLoopback(peer.Address)) return;
                var stream = client.GetStream();
                var headers = new List<byte>();
                var one = new byte[1];
                while (headers.Count < MaxHeaderBytes)
                {
                    if (await stream.ReadAsync(one, timeout.Token) != 1) return;
                    headers.Add(one[0]);
                    var n = headers.Count;
                    if (n >= 4 && headers[n - 4] == 13 && headers[n - 3] == 10 && headers[n - 2] == 13 && headers[n - 1] == 10) break;
                }
                if (headers.Count >= MaxHeaderBytes) return;
                var lines = Encoding.ASCII.GetString(headers.ToArray()).Split("\r\n");
                var request = lines[0].Split(' ');
                var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in lines.Skip(1).Where(line => line.Length != 0))
                {
                    var sep = line.IndexOf(':');
                    if (sep <= 0 || !values.TryAdd(line[..sep], line[(sep + 1)..].Trim())) return;
                }
                var expectedHost = actualPort == 80 ? "localhost" : $"localhost:{actualPort}";
                if (request.Length != 3 || !values.TryGetValue("Host", out var host) ||
                    !(host.Equals(expectedHost, StringComparison.OrdinalIgnoreCase) || actualPort == 80 && host.Equals("localhost:80", StringComparison.OrdinalIgnoreCase))) return;
                if (request[0] == "GET" && request[1] == "/redirect")
                {
                    await Reply(stream, 200, "text/html; charset=utf-8", CallbackPage, timeout.Token);
                    return;
                }
                if (request[0] != "POST" || request[1] != "/auth-result" || values.ContainsKey("Transfer-Encoding") ||
                    !values.TryGetValue("Origin", out var origin) || !origin.Equals("http://" + expectedHost, StringComparison.OrdinalIgnoreCase) ||
                    !values.TryGetValue("Content-Length", out var sizeText) || !int.TryParse(sizeText, out var size) || size is <= 0 or > MaxBodyBytes)
                    return;
                var body = new byte[size];
                await stream.ReadExactlyAsync(body, timeout.Token);
                AuthResult? result;
                try { result = AuthFlow.ParseSystemBrowserFragment(Encoding.UTF8.GetString(body), state, DateTimeOffset.UtcNow); }
                catch (Exception e) when (e is InvalidDataException or UriFormatException)
                {
                    await Reply(stream, 400, "text/plain; charset=utf-8", [], timeout.Token);
                    return;
                }
                await Reply(stream, 200, "text/plain; charset=utf-8", [], timeout.Token);
                completion.TrySetResult(result);
            }
        }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
        finally { try { slots.Release(); } catch (ObjectDisposedException) { } }
    }

    private static async Task Reply(NetworkStream stream, int status, string contentType, byte[] body, CancellationToken ct)
    {
        var response = $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Bad Request")}\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nReferrer-Policy: no-referrer\r\nX-Content-Type-Options: nosniff\r\nContent-Security-Policy: default-src 'none'; script-src 'unsafe-inline'; connect-src 'self'\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(response), ct);
        if (body.Length > 0) await stream.WriteAsync(body, ct);
    }
}
