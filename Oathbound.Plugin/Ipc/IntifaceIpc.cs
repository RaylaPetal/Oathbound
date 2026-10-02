using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Buttplug.Client;
using Buttplug.Core.Messages;

namespace Oathbound.Plugin.Ipc;

/// Talks to Intiface Central over WebSocket via the Buttplug client - a standalone app, not a Dalamud plugin.
/// Fail-closed and fire-and-forget: callers poll IsConnected/IsConnecting instead of awaiting.
public sealed class IntifaceIpc : IDisposable
{
    private readonly ButtplugClient client = new("Oathbound");
    private CancellationTokenSource? connectionCts;

    public bool IsConnected => client.Connected;
    public bool IsConnecting { get; private set; }
    public string? LastError { get; private set; }
    public int ConnectedDeviceCount => IsConnected ? client.Devices.Length : 0;

    public void Connect(string address)
    {
        if (IsConnecting || IsConnected) return;
        IsConnecting = true;
        LastError = null;
        connectionCts = new CancellationTokenSource();
        _ = ConnectAsync(address, connectionCts.Token);
    }

    private async Task ConnectAsync(string address, CancellationToken token)
    {
        try
        {
            if (!Uri.TryCreate(address, UriKind.Absolute, out var uri))
            {
                LastError = "Invalid Intiface address.";
                return;
            }
            var connector = new ButtplugWebsocketConnector(uri);
            await client.ConnectAsync(connector, token);
            await client.StartScanningAsync(token);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Plugin.Log.Warning(ex, "IntifaceIpc: failed to connect to Intiface Central.");
        }
        finally
        {
            IsConnecting = false;
        }
    }

    public void Disconnect()
    {
        connectionCts?.Cancel();
        if (!IsConnected) return;
        _ = DisconnectAsync();
    }

    private async Task DisconnectAsync()
    {
        try { await client.DisconnectAsync(); }
        catch (Exception ex) { Plugin.Log.Warning(ex, "IntifaceIpc: error while disconnecting from Intiface Central."); }
    }

    /// `intensityFraction` 0.0-1.0 on every vibrate feature of every device. A per-device failure doesn't affect the others.
    public void VibrateAll(double intensityFraction)
    {
        if (!IsConnected) return;
        _ = VibrateAllAsync(Math.Clamp(intensityFraction, 0.0, 1.0));
    }

    private async Task VibrateAllAsync(double intensityFraction)
    {
        var command = new DeviceOutputCommand(OutputType.Vibrate, PercentOrSteps.FromPercent(intensityFraction), null);
        foreach (var device in client.Devices)
        {
            foreach (var feature in device.GetFeaturesWithOutput(OutputType.Vibrate))
            {
                try { await feature.RunOutputAsync(command, CancellationToken.None); }
                catch (Exception ex) { Plugin.Log.Warning(ex, $"IntifaceIpc: failed to vibrate device '{device.Name}'."); }
            }
        }
    }

    /// Protocol-level stop for everything; used by stop and panic.
    public void StopAll()
    {
        if (!IsConnected) return;
        _ = StopAllAsync();
    }

    private async Task StopAllAsync()
    {
        try { await client.StopAllDevicesAsync(CancellationToken.None); }
        catch (Exception ex) { Plugin.Log.Warning(ex, "IntifaceIpc: failed to stop all devices."); }
    }

    public void Dispose()
    {
        connectionCts?.Cancel();
        client.Dispose();
    }
}
