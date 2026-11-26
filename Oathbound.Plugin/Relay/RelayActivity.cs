using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Oathbound.Plugin.Config;

namespace Oathbound.Plugin.Relay;

/// The relay's head counts for the header: who's online now (from the live connection) and how many installs used it
/// today or yesterday (from the pair status check). Neither names anyone. Framework thread; in memory only.
public sealed class RelayActivity
{
    /// Older than this and a count is hidden rather than shown as current.
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(2);
    private static readonly TimeSpan OnlineMaxAge = TimeSpan.FromMinutes(40);
    /// protocol/constants.json presence: refresh intervals, and the relay ignores one sooner than 20s after the last.
    private static readonly TimeSpan Refresh = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan RefreshWindowOpen = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan HelloRetry = TimeSpan.FromMinutes(5);

    private readonly PluginConfig config;
    private readonly RelayClient relay;
    private readonly RelaySocket socket;
    private long computedAt;
    private long onlineAt;
    private DateTime lastRefreshUtc = DateTime.MinValue;
    private DateTime nextHelloUtc = DateTime.MinValue;
    private bool helloRunning;

    public RelayActivity(PluginConfig config, RelayClient relay, RelaySocket socket)
    {
        this.config = config;
        this.relay = relay;
        this.socket = socket;
        socket.MessageReceived += OnMessage;
        socket.StateChanged += state => { if (state == RelaySocketState.Connected) lastRefreshUtc = DateTime.UtcNow; };
    }

    public int? ActiveDevices { get; private set; }
    public int? OnlineDevices { get; private set; }

    /// The relay has this install's key on file, so its live connection can sign in. Persisted per device key.
    public bool Introduced => config.PresenceHelloDeviceKeyId is { } id && id == config.DeviceIdentity.DeviceKeyId;

    public void Update(PairingState pairing, PairEnvelope pair)
    {
        if (pair.RelayActiveDevices is not { } count || pair.RelayActiveDevicesAt is not { } at || at < computedAt)
            return;
        ActiveDevices = count;
        computedAt = at;
    }

    public int? Current =>
        ActiveDevices is { } count && DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(computedAt) < MaxAge ? count : null;

    public int? OnlineNow =>
        OnlineDevices is { } count && DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(onlineAt) < OnlineMaxAge ? count : null;

    /// Once a frame while logged in. `windowOpen` asks for a fresher count while the header is on screen.
    public void Tick(bool windowOpen)
    {
        if (!Introduced)
        {
            Hello();
            return;
        }
        if (socket.State != RelaySocketState.Connected) return;
        if (DateTime.UtcNow - lastRefreshUtc < (windowOpen ? RefreshWindowOpen : Refresh)) return;
        lastRefreshUtc = DateTime.UtcNow;
        _ = socket.SendAsync("{\"type\":\"presence\"}");
    }

    private void Hello()
    {
        if (helloRunning || DateTime.UtcNow < nextHelloUtc || config.DeviceIdentity.DeviceKeyId is not { } device) return;
        helloRunning = true;
        nextHelloUtc = DateTime.UtcNow + HelloRetry;
        _ = Task.Run(async () =>
        {
            try
            {
                await relay.PresenceHelloAsync(CancellationToken.None).ConfigureAwait(false);
                await Plugin.Framework.RunOnFrameworkThread(() =>
                {
                    config.PresenceHelloDeviceKeyId = device;
                    config.Save();
                }).ConfigureAwait(false);
            }
            catch (RelayException ex)
            {
                Plugin.Log.Information($"Relay hello failed ({ex.Code}); trying again later.");
            }
            finally
            {
                helloRunning = false;
            }
        });
    }

    private void OnMessage(JsonElement message)
    {
        if (!message.TryGetProperty("type", out var type) || type.GetString() != "presence") return;
        if (message.TryGetProperty("online", out var online) && online.TryGetInt32(out var count)
            && message.TryGetProperty("onlineAt", out var at) && at.TryGetInt64(out var when) && when >= onlineAt)
        {
            OnlineDevices = count;
            onlineAt = when;
        }
    }
}
