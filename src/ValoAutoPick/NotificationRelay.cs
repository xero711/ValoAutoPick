using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;

namespace ValoAutoPick;

internal sealed record RelayOptions(IPAddress Address, byte[] Key, int Port = RelayProtocol.Port);

internal static class RelayProtocol
{
    internal const int Port = 42776;
    internal const int NonceSize = 32;
    internal const int MacSize = 32;
    internal const int FrameSize = 1 + sizeof(ulong) + MacSize;

    internal static string NewCode()
    {
        var raw = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        return string.Join("-", Enumerable.Range(0, 8).Select(i => raw.Substring(i * 4, 4)));
    }

    internal static bool TryParseCode(string code, out byte[] key)
    {
        key = [];
        var raw = new string(code.Where(c => c != '-' && !char.IsWhiteSpace(c)).ToArray());
        if (raw.Length != 32) return false;
        try { key = Convert.FromHexString(raw); return true; }
        catch (FormatException) { return false; }
    }

    internal static byte[] Mac(byte[] key, string purpose, byte[] clientNonce, byte[] serverNonce, string subject, ReadOnlySpan<byte> frame = default)
    {
        using var input = new MemoryStream();
        input.Write(Encoding.ASCII.GetBytes(purpose));
        input.Write(clientNonce); input.Write(serverNonce);
        input.Write(Encoding.ASCII.GetBytes(Guid.Parse(subject).ToString("D")));
        input.Write(frame);
        return HMACSHA256.HashData(key, input.GetBuffer().AsSpan(0, (int)input.Length));
    }
}

internal sealed class NotificationRelayServer : IDisposable
{
    private readonly TcpListener listener;
    private readonly byte[] key;
    private readonly CancellationTokenSource stop = new();
    private readonly SemaphoreSlim slots = new(4, 4);
    private readonly object sync = new();
    private readonly HashSet<Peer> peers = [];
    private string subject = "";

    internal NotificationRelayServer(IPAddress address, int port, byte[] key)
    {
        listener = new TcpListener(address, port);
        this.key = key.ToArray();
    }

    internal int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
    internal void Start() { listener.Start(); _ = AcceptLoop(); }

    internal void SetSubject(string value)
    {
        value = Guid.TryParse(value, out var parsed) ? parsed.ToString("D") : "";
        Peer[] old;
        lock (sync)
        {
            if (subject == value) return;
            subject = value; old = peers.ToArray();
        }
        foreach (var peer in old) peer.Close();
    }

    internal void Publish(string value)
    {
        if (!Guid.TryParse(value, out var parsed)) return;
        Peer[] targets;
        lock (sync)
        {
            if (subject != parsed.ToString("D")) return;
            targets = peers.ToArray();
        }
        foreach (var peer in targets) peer.Signal();
    }

    private async Task AcceptLoop()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stop.Token);
                if (!slots.Wait(0)) { client.Dispose(); continue; }
                _ = Serve(client);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException) { }
    }

    private async Task Serve(TcpClient client)
    {
        Peer? peer = null;
        try
        {
            using (client)
            {
                string current;
                lock (sync) current = subject;
                if (current.Length == 0) return;
                client.NoDelay = true;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                var stream = client.GetStream();
                var clientNonce = new byte[RelayProtocol.NonceSize];
                await stream.ReadExactlyAsync(clientNonce, timeout.Token);
                var serverNonce = RandomNumberGenerator.GetBytes(RelayProtocol.NonceSize);
                var serverProof = RelayProtocol.Mac(key, "server", clientNonce, serverNonce, current);
                await stream.WriteAsync(serverNonce, timeout.Token);
                await stream.WriteAsync(serverProof, timeout.Token);
                var proof = new byte[RelayProtocol.MacSize];
                await stream.ReadExactlyAsync(proof, timeout.Token);
                if (!CryptographicOperations.FixedTimeEquals(proof, RelayProtocol.Mac(key, "client", clientNonce, serverNonce, current))) return;
                peer = new Peer(client, key, clientNonce, serverNonce, current);
                lock (sync)
                {
                    if (subject != current || stop.IsCancellationRequested) return;
                    peers.Add(peer);
                }
                peer.Signal();
                await peer.SendLoop(stop.Token);
            }
        }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
        finally
        {
            if (peer != null) { lock (sync) peers.Remove(peer); }
            slots.Release();
        }
    }

    public void Dispose()
    {
        stop.Cancel(); listener.Stop();
        Peer[] old; lock (sync) { subject = ""; old = peers.ToArray(); }
        foreach (var peer in old) peer.Close();
    }

    private sealed class Peer(TcpClient client, byte[] key, byte[] clientNonce, byte[] serverNonce, string subject)
    {
        private readonly Channel<bool> signals = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
        internal void Signal() => signals.Writer.TryWrite(true);
        internal void Close() => client.Close();

        internal async Task SendLoop(CancellationToken ct)
        {
            ulong sequence = 0;
            var stream = client.GetStream();
            while (!ct.IsCancellationRequested)
            {
                var changed = false;
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(10));
                    try { await signals.Reader.ReadAsync(timeout.Token); changed = true; }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
                }
                var frame = new byte[RelayProtocol.FrameSize];
                frame[0] = changed ? (byte)1 : (byte)0;
                BinaryPrimitives.WriteUInt64BigEndian(frame.AsSpan(1, sizeof(ulong)), ++sequence);
                var mac = RelayProtocol.Mac(key, "frame", clientNonce, serverNonce, subject, frame.AsSpan(0, 9));
                mac.CopyTo(frame, 9);
                await stream.WriteAsync(frame, ct);
            }
        }
    }
}

internal static class NotificationRelayClient
{
    internal static async Task Listen(RelayOptions options, Func<string> currentSubject, Action changed, Action<bool> status, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var subject = currentSubject();
                if (Guid.TryParse(subject, out _)) await ListenConnection(options, subject, currentSubject, changed, status, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or CryptographicException or ObjectDisposedException) { }
            finally { status(false); }
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
    }

    private static async Task ListenConnection(RelayOptions options, string subject, Func<string> currentSubject, Action changed, Action<bool> status, CancellationToken ct)
    {
        using var client = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        await client.ConnectAsync(options.Address, options.Port, timeout.Token);
        var stream = client.GetStream();
        var clientNonce = RandomNumberGenerator.GetBytes(RelayProtocol.NonceSize);
        await stream.WriteAsync(clientNonce, timeout.Token);
        var serverNonce = new byte[RelayProtocol.NonceSize];
        var serverProof = new byte[RelayProtocol.MacSize];
        await stream.ReadExactlyAsync(serverNonce, timeout.Token);
        await stream.ReadExactlyAsync(serverProof, timeout.Token);
        if (!CryptographicOperations.FixedTimeEquals(serverProof, RelayProtocol.Mac(options.Key, "server", clientNonce, serverNonce, subject)))
            throw new CryptographicException("Notification relay authentication failed");
        await stream.WriteAsync(RelayProtocol.Mac(options.Key, "client", clientNonce, serverNonce, subject), timeout.Token);
        status(true);
        ulong previous = 0;
        while (!ct.IsCancellationRequested)
        {
            using var receiveTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            receiveTimeout.CancelAfter(TimeSpan.FromSeconds(25));
            var frame = new byte[RelayProtocol.FrameSize];
            await stream.ReadExactlyAsync(frame, receiveTimeout.Token);
            var sequence = BinaryPrimitives.ReadUInt64BigEndian(frame.AsSpan(1, sizeof(ulong)));
            if ((frame[0] != 0 && frame[0] != 1) || sequence != previous + 1 ||
                !CryptographicOperations.FixedTimeEquals(frame.AsSpan(9), RelayProtocol.Mac(options.Key, "frame", clientNonce, serverNonce, subject, frame.AsSpan(0, 9))))
                throw new CryptographicException("Invalid notification relay frame");
            if (!string.Equals(subject, currentSubject(), StringComparison.OrdinalIgnoreCase)) break;
            previous = sequence;
            if (frame[0] == 1) changed();
        }
    }
}
