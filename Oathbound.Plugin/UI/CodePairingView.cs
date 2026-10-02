using System;
using System.Threading;
using Oathbound.Plugin.Config;
using Oathbound.Plugin.Relay;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace Oathbound.Plugin.UI;

/// All state lives in CodePairingService; this only draws it.
public sealed class CodePairingView
{
    private readonly Plugin plugin;
    private string codeInput = "";
    private bool inviteAsOwnerSide = true;
    private bool busy;
    private string? localError;

    public CodePairingView(Plugin plugin)
    {
        this.plugin = plugin;
    }

    public void Draw(PluginConfig config)
    {
        var service = plugin.CodePairingService;
        ImGui.TextWrapped("Create a code and give it to the other person any way you like (Discord, /say, in person), or enter a code someone gave you. No tells are sent, and you don't both need to be online at the same time.");
        ImGui.Spacing();

        DrawOutgoing(config, service);
        ImGui.Spacing();
        DrawIncoming(config, service);

        foreach (var accepted in service.Accepted)
            IconGlyph.WrappedColored(Theme.Warning, $"Waiting for {accepted.PeerName}@{accepted.PeerWorld} to confirm - it completes the next time they're online ({Remaining(accepted.ExpiresAt)} left).");

        if ((localError ?? service.LastError) is { Length: > 0 } error)
            IconGlyph.WrappedColored(Theme.Danger, error);
        if (service.Notice is { Length: > 0 } notice)
        {
            IconGlyph.WrappedColored(Theme.Warning, notice);
            ImGui.SameLine();
            if (ImGui.SmallButton("OK##codePairingNotice"))
                service.DismissNotice();
        }
    }

    private void DrawOutgoing(PluginConfig config, CodePairingService service)
    {
        if (service.Outgoing is not { } outgoing)
        {
            if (config.Role == PluginRole.Switch)
            {
                if (ImGui.RadioButton("They'll be my Sub##codeDirection", inviteAsOwnerSide)) inviteAsOwnerSide = true;
                ImGui.SameLine();
                if (ImGui.RadioButton("They'll be my Owner##codeDirection", !inviteAsOwnerSide)) inviteAsOwnerSide = false;
            }
            using (ImRaii.Disabled(busy))
            {
                if (ImGui.Button(busy ? "Creating..." : "Create pairing code"))
                    CreateCode(config);
            }
            IconGlyph.HelpMarker("Makes a single-use code that works for 7 days. When they enter it, you'll be asked to confirm it's really them before the pairing starts.");
            return;
        }

        var theyWillBe = outgoing.Direction == PairingDirection.OwnerSide ? "your Sub" : "your Owner";
        var formatted = PairingCodes.Format(outgoing.Code);
        ImGui.TextUnformatted($"Your pairing code (they'll be {theyWillBe}):");
        using (ImRaii.PushColor(ImGuiCol.Text, Theme.Accent))
            ImGui.TextUnformatted(formatted);
        ImGui.SameLine();
        if (ImGui.SmallButton("Copy##pairingCode"))
            ImGui.SetClipboardText(formatted);

        if (outgoing.Status == "needs-confirm")
        {
            IconGlyph.WrappedColored(Theme.Warning, $"{outgoing.PeerName}@{outgoing.PeerWorld} entered your code to become {theyWillBe}. Is this who you meant to pair with?");
            using (ImRaii.Disabled(busy))
            {
                if (ImGui.Button("Confirm##codeConfirm"))
                    Run(ct => service.ConfirmAsync(outgoing, ct));
                ImGui.SameLine();
                if (ImGui.Button("Reject##codeReject"))
                    Run(ct => service.RejectAsync(outgoing, ct));
            }
            IconGlyph.HelpMarker("Reject if it isn't the person you gave the code to - the code then stops working and nothing is paired.");
            return;
        }

        IconGlyph.WrappedDisabled($"Waiting for someone to enter it - {Remaining(outgoing.ExpiresAt)} left. You'll be asked to confirm who they are.");
        using (ImRaii.Disabled(busy))
        {
            if (ImGui.SmallButton("Cancel code##codeCancel"))
                Run(service.CancelOutgoingAsync);
        }
    }

    private void DrawIncoming(PluginConfig config, CodePairingService service)
    {
        if (service.Preview is { } preview)
        {
            var asWhat = preview.OwnDirection == PairingDirection.SubSide ? "their Sub" : "their Owner";
            IconGlyph.WrappedColored(Theme.Warning, $"{preview.Inviter.Name}@{preview.Inviter.World} wants you as {asWhat}.");
            using (ImRaii.Disabled(busy))
            {
                if (ImGui.Button("Accept##codeAccept"))
                {
                    if (CodePairingService.CurrentCharacter(config.TriggerPhrase) is { } self)
                        Run(ct => service.AcceptPreviewAsync(self, ct));
                    else
                        localError = "Log in to a character first.";
                }
                ImGui.SameLine();
                if (ImGui.Button("Decline##codeDecline"))
                    service.DeclinePreview();
            }
            IconGlyph.HelpMarker("Accepting sends them your character name and world (encrypted - the relay can't read it). The pairing starts once they confirm it's you, the next time they're online.");
            return;
        }

        ImGui.SetNextItemWidth(220f);
        ImGui.InputTextWithHint("##pairingCodeInput", "K7QM-3XRP-9DTA-WV2E", ref codeInput, 32);
        ImGui.SameLine();
        using (ImRaii.Disabled(busy || codeInput.Trim().Length == 0))
        {
            if (ImGui.Button("Enter code"))
            {
                var typed = codeInput;
                Run(async ct =>
                {
                    if (await service.LookupAsync(typed, ct))
                        codeInput = "";
                });
            }
        }
        IconGlyph.HelpMarker("Paste or type the code the other person gave you. You'll see who it's from before anything happens.");
    }

    private void CreateCode(PluginConfig config)
    {
        var direction = config.Role switch
        {
            PluginRole.Owner => PairingDirection.OwnerSide,
            PluginRole.Sub => PairingDirection.SubSide,
            _ => inviteAsOwnerSide ? PairingDirection.OwnerSide : PairingDirection.SubSide,
        };
        if (CodePairingService.CurrentCharacter(config.TriggerPhrase) is not { } self)
        {
            localError = "Log in to a character first.";
            return;
        }
        Run(ct => plugin.CodePairingService.CreateCodeAsync(direction, self, ct));
    }

    private void Run(Func<CancellationToken, System.Threading.Tasks.Task> action)
    {
        busy = true;
        localError = null;
        Plugin.FireAndForget(RunAsync(action));
    }

    private async System.Threading.Tasks.Task RunAsync(Func<CancellationToken, System.Threading.Tasks.Task> action)
    {
        try
        {
            await action(CancellationToken.None);
        }
        finally
        {
            busy = false;
        }
    }

    internal static string Remaining(long expiresAtUnix)
    {
        var left = TimeSpan.FromSeconds(Math.Max(0, expiresAtUnix - DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        return left.TotalDays >= 1 ? $"{(int)left.TotalDays}d {left.Hours}h" : left.TotalHours >= 1 ? $"{(int)left.TotalHours}h {left.Minutes}m" : $"{left.Minutes}m";
    }
}
