using System;
using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Oathbound.Plugin.Relay;

public enum RelaySocketState
{
    Disconnected,
    Connecting,
    Connected,
}

/// This install's one live connection to the relay (protocol/constants.json `socketAuth`). Opens while `Wanted` is
/// true, answers the relay's challenge with the device key, and reconnects with a growing, randomized delay. Every
/// event is raised through `dispatch`, which the plugin points at the framework thread. Keep-alive is the text `ping`,
/// which the relay answers `pong` without waking the hub; a connection that answers nothing within the pong deadline
/// is treated as dead.
public sealed class RelaySocket : IDisposable
{
    /// protocol/constants.json `socketAuth.keepAliveSeconds` and `pongTimeoutSeconds`.
    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PongTimeout = TimeSpan.FromSeconds(20);
    private const string Ping = "ping";
    private static readonly TimeSpan AuthTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MinBackoff = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);
    /// A connection that stayed up this long resets the backoff.
    private static readonly TimeSpan StableAfter = TimeSpan.FromSeconds(60);
    private const int MaxMessageBytes = 32 * 1024;
    /// UTC ticks of the last frame of any kind from the relay, `pong` included: proof the connection is alive.
    private long lastReceivedTicks;

    private readonly Uri socketBase;
    private readonly Func<string?> deviceKeyId;
    private readonly Func<RelayEcKeyPair> signingKey;
    private readonly Action<Action> dispatch;
    private readonly Action<string> log;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim sendLock = new(1, 1);
    private readonly SemaphoreSlim wake = new(0, int.MaxValue);
    private ClientWebSocket? socket;
    private volatile bool wanted;
    private volatile RelaySocketState state;
    private Task? loop;

    public RelaySocket(string httpsOrigin, Func<string?> deviceKeyId, Func<RelayEcKeyPair> signingKey, Action<Action> dispatch, Action<string> log)
    {
        this.log = log;
        socketBase = new Uri(httpsOrigin.Replace("https://", "wss://", StringComparison.Ordinal).Replace("http://", "ws://", StringComparison.Ordinal).TrimEnd('/') + "/v1/socket");
        this.deviceKeyId = deviceKeyId;
        this.signingKey = signingKey;
        this.dispatch = dispatch;
    }

    public RelaySocketState State => state;

    /// Relay clock minus ours, from the last challenge; used to judge a frame's age.
    public long ServerOffsetSeconds { get; private set; }

    public event Action<RelaySocketState>? StateChanged;
    public event Action<JsonElement>? MessageReceived;

    /// Connect while true, disconnect while false. Cheap to set every frame.
    public bool Wanted
    {
        get => wanted;
        set
        {
            if (wanted == value) return;
            wanted = value;
            if (value && loop is null)
                loop = Task.Run(() => RunAsync(lifetime.Token));
            else if (!value)
                _ = CloseCurrentAsync();
            wake.Release();
        }
    }

    /// False when there's no authenticated connection to send on.
    public async Task<bool> SendAsync(string json, CancellationToken ct = default)
    {
        var ws = socket;
        if (ws is null || state != RelaySocketState.Connected) return false;
        await sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (ws.State != WebSocketState.Open) return false;
            await ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or InvalidOperationException)
        {
            return false;
        }
        finally
        {
            sendLock.Release();
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var backoff = MinBackoff;
        while (!ct.IsCancellationRequested)
        {
            if (!wanted)
            {
                try { await wake.WaitAsync(ct).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
                continue;
            }

            var startedAt = DateTime.UtcNow;
            try
            {
                await ConnectAndReceiveAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                log($"Relay connection dropped: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                SetState(RelaySocketState.Disconnected);
                var old = Interlocked.Exchange(ref socket, null);
                old?.Dispose();
            }

            if (!wanted) continue;
            if (DateTime.UtcNow - startedAt >= StableAfter) backoff = MinBackoff;
            // Full jitter, so a relay restart doesn't bring every client back in the same second.
            var delay = TimeSpan.FromMilliseconds(Random.Shared.NextDouble() * backoff.TotalMilliseconds);
            backoff = TimeSpan.FromMilliseconds(Math.Min(MaxBackoff.TotalMilliseconds, backoff.TotalMilliseconds * 2));
            try { await wake.WaitAsync(delay, ct).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
        }
    }

    private async Task ConnectAndReceiveAsync(CancellationToken ct)
    {
        var device = deviceKeyId();
        if (device is null)
        {
            await Task.Delay(MaxBackoff, ct).ConfigureAwait(false);
            return;
        }

        SetState(RelaySocketState.Connecting);
        var ws = new ClientWebSocket();
        // No protocol-level keep-alive: .NET sends unsolicited pongs for it, which Cloudflare doesn't document as
        // wake-free. The text ping in KeepAliveAsync is.
        ws.Options.KeepAliveInterval = TimeSpan.Zero;
        socket = ws;
        using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(20));
            await ws.ConnectAsync(new Uri($"{socketBase}?device={device}"), connectTimeout.Token).ConfigureAwait(false);
        }

        using (var authTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            authTimeout.CancelAfter(AuthTimeout);
            using var challenge = await ReceiveAsync(ws, authTimeout.Token).ConfigureAwait(false)
                ?? throw new WebSocketException("closed before the challenge");
            var root = challenge.RootElement;
            if (root.GetProperty("type").GetString() != "challenge") throw new WebSocketException("expected a challenge");
            var nonce = root.GetProperty("nonce").GetString() ?? throw new WebSocketException("challenge without nonce");
            ServerOffsetSeconds = root.GetProperty("serverTime").GetInt64() - DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            var signature = RelayCrypto.SignRaw(signingKey(), $"oathbound-socket-v1\n{device}\n{nonce}");
            // "chat" and "match" tell the relay it may forward and store pairing chat and match chat for this install.
            var auth = JsonSerializer.Serialize(new { type = "auth", deviceKeyId = device, signature, features = new[] { "chat", "match" } });
            await ws.SendAsync(Encoding.UTF8.GetBytes(auth), WebSocketMessageType.Text, true, authTimeout.Token).ConfigureAwait(false);

            using var answer = await ReceiveAsync(ws, authTimeout.Token).ConfigureAwait(false)
                ?? throw new WebSocketException("closed during auth");
            if (answer.RootElement.GetProperty("type").GetString() != "auth-ok")
                throw new WebSocketException($"auth refused: {answer.RootElement.GetRawText()}");
        }

        SetState(RelaySocketState.Connected);
        log("Relay connection open.");
        using var connection = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var keepAlive = KeepAliveAsync(ws, connection.Token);
        try
        {
            while (!ct.IsCancellationRequested && wanted)
            {
                using var message = await ReceiveAsync(ws, ct).ConfigureAwait(false);
                if (message is null) return;
                var element = message.RootElement.Clone();
                if (element.ValueKind == JsonValueKind.Object && MessageReceived is { } handler)
                    dispatch(() => handler(element));
            }
        }
        finally
        {
            connection.Cancel();
            try { await keepAlive.ConfigureAwait(false); } catch { /* ended with the connection */ }
        }
    }

    /// Sends `ping` every <see cref="PingInterval"/>. If nothing at all arrives within <see cref="PongTimeout"/> of
    /// one, the socket is aborted, which ends the receive loop and starts the normal reconnect.
    private async Task KeepAliveAsync(ClientWebSocket ws, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(PingInterval - PongTimeout, ct).ConfigureAwait(false);
            var sentAt = DateTime.UtcNow.Ticks;
            if (!await SendAsync(Ping, ct).ConfigureAwait(false)) return;
            await Task.Delay(PongTimeout, ct).ConfigureAwait(false);
            if (Interlocked.Read(ref lastReceivedTicks) >= sentAt) continue;
            log("Relay connection stopped answering; reconnecting.");
            ws.Abort();
            return;
        }
    }

    /// Null when the relay closed the connection. Every frame received, JSON or not, stamps <see cref="lastReceivedTicks"/>.
    private async Task<JsonDocument?> ReceiveAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            var total = 0;
            while (true)
            {
                if (total == buffer.Length)
                {
                    if (buffer.Length >= MaxMessageBytes) throw new WebSocketException("message too large");
                    var bigger = ArrayPool<byte>.Shared.Rent(buffer.Length * 2);
                    Buffer.BlockCopy(buffer, 0, bigger, 0, total);
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = bigger;
                }
                var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer, total, buffer.Length - total), ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) return null;
                total += result.Count;
                if (!result.EndOfMessage) continue;
                Interlocked.Exchange(ref lastReceivedTicks, DateTime.UtcNow.Ticks);
                if (result.MessageType != WebSocketMessageType.Text) { total = 0; continue; }
                try
                {
                    return JsonDocument.Parse(new ReadOnlyMemory<byte>(buffer, 0, total));
                }
                catch (JsonException)
                {
                    // The relay's plain "pong" (already counted as liveness) or anything else that isn't JSON.
                    total = 0;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task CloseCurrentAsync()
    {
        var ws = socket;
        if (ws is null) return;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", timeout.Token).ConfigureAwait(false);
        }
        catch
        {
            ws.Abort();
        }
    }

    private void SetState(RelaySocketState next)
    {
        if (state == next) return;
        state = next;
        if (StateChanged is { } handler)
            dispatch(() => handler(next));
    }

    public void Dispose()
    {
        wanted = false;
        lifetime.Cancel();
        socket?.Abort();
        wake.Release();
        try { loop?.Wait(TimeSpan.FromSeconds(2)); } catch { /* shutting down */ }
        socket?.Dispose();
        lifetime.Dispose();
    }
}
