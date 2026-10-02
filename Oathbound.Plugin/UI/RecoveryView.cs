using System;
using System.Numerics;
using System.Threading;
using Oathbound.Plugin.Relay;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace Oathbound.Plugin.UI;

public sealed class RecoveryView
{
    private readonly Plugin plugin;
    private bool revealed;
    private bool confirmingRegenerate;
    private string restoreInput = "";
    private bool restoring;
    private bool needsReplaceConfirm;
    private string? restoreMessage;
    private bool restoreMessageIsError;

    public RecoveryView(Plugin plugin)
    {
        this.plugin = plugin;
    }

    public void Draw()
    {
        var backup = plugin.BackupService;
        IconGlyph.Text(FontAwesomeIcon.Key, "Recovery code");
        ImGui.Separator();
        ImGui.TextWrapped("Your recovery code restores your pairings after a reinstall or on a new PC - your partners won't need to do anything. Keep it somewhere safe: without it, a lost install can't be restored and you'd have to pair again.");
        ImGui.Spacing();

        if (backup.HasCode)
        {
            var code = backup.GetFormattedCode();
            if (code is null)
            {
                IconGlyph.WrappedColored(Theme.Danger, "Your recovery code can't be read on this machine. Regenerate it below.");
            }
            else
            {
                ImGui.TextUnformatted(revealed ? code : "****-****-****-****-****-****-**");
                ImGui.SameLine();
                if (ImGui.SmallButton(revealed ? "Hide##recoveryReveal" : "Reveal##recoveryReveal"))
                    revealed = !revealed;
                ImGui.SameLine();
                if (ImGui.SmallButton("Copy##recoveryCopy"))
                    ImGui.SetClipboardText(code);
            }

            if (backup.LastError is { Length: > 0 } error)
                IconGlyph.WrappedColored(Theme.Warning, error);
            else if (backup.IsUpToDate && backup.LastUploadAt is { } at)
                IconGlyph.WrappedColored(Theme.Success, $"Backup up to date (saved {at.ToLocalTime():g}).");
            else
                IconGlyph.WrappedDisabled("Backup is being updated...");

            using (ImRaii.Disabled(confirmingRegenerate))
            {
                if (ImGui.SmallButton("Regenerate code"))
                    confirmingRegenerate = true;
            }
            if (confirmingRegenerate)
            {
                IconGlyph.WrappedColored(Theme.Danger, "Your current code will stop working and a new one will be shown. Continue?");
                if (ImGui.Button("Regenerate##recoveryRegenerateConfirm"))
                {
                    backup.Regenerate();
                    confirmingRegenerate = false;
                    revealed = true;
                }
                ImGui.SameLine();
                if (ImGui.Button("Cancel##recoveryRegenerateCancel"))
                    confirmingRegenerate = false;
            }
        }
        else
        {
            IconGlyph.WrappedDisabled("You'll get a recovery code as soon as your first pairing completes.");
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("Restore from a recovery code");
        ImGui.SetNextItemWidth(300f);
        ImGui.InputTextWithHint("##restoreCode", "XXXX-XXXX-XXXX-XXXX-XXXX-XXXX-XX", ref restoreInput, 48);
        ImGui.SameLine();
        using (ImRaii.Disabled(restoring || restoreInput.Trim().Length == 0))
        {
            if (ImGui.Button(restoring ? "Restoring..." : "Restore"))
                StartRestore(replaceConfirmed: false);
        }
        IconGlyph.HelpMarker("Brings back your identity and pairings from the backup on the relay. Don't restore the same code on two PCs at once - the last one restored is the one that works.");

        if (needsReplaceConfirm)
        {
            IconGlyph.WrappedColored(Theme.Danger, "This install already has pairings under a different identity. Restoring replaces them - those partners would need to unpair on their side. Restore anyway?");
            if (ImGui.Button("Restore anyway##restoreReplace"))
                StartRestore(replaceConfirmed: true);
            ImGui.SameLine();
            if (ImGui.Button("Cancel##restoreReplaceCancel"))
                needsReplaceConfirm = false;
        }
        if (restoreMessage is { Length: > 0 } message)
            IconGlyph.WrappedColored(restoreMessageIsError ? Theme.Danger : Theme.Success, message);

        ImGui.Spacing();
        IconGlyph.WrappedDisabled("The code is stored with the same protection as your device key - under Wine that protection isn't real, so treat this PC's config folder as sensitive.");
    }

    private void StartRestore(bool replaceConfirmed)
    {
        restoring = true;
        needsReplaceConfirm = false;
        restoreMessage = null;
        var typed = restoreInput;
        Plugin.FireAndForget(RestoreAsync(typed, replaceConfirmed));
    }

    private async System.Threading.Tasks.Task RestoreAsync(string typed, bool replaceConfirmed)
    {
        try
        {
            var (outcome, restored, dropped) = await plugin.BackupService.RestoreAsync(typed, replaceConfirmed, CancellationToken.None);
            restoreMessageIsError = outcome != RestoreOutcome.Restored;
            switch (outcome)
            {
                case RestoreOutcome.Restored:
                    restoreInput = "";
                    restoreMessage = dropped > 0
                        ? $"Restored {restored} pairing(s). {dropped} had been ended while you were away and were dropped."
                        : $"Restored {restored} pairing(s).";
                    break;
                case RestoreOutcome.NeedsConfirmation:
                    needsReplaceConfirm = true;
                    restoreMessage = null;
                    break;
                case RestoreOutcome.InvalidCode:
                    restoreMessage = "That isn't a recovery code - it's 26 letters and numbers.";
                    break;
                case RestoreOutcome.NotFound:
                    restoreMessage = "No backup was found for that code.";
                    break;
                default:
                    restoreMessage = plugin.BackupService.LastError ?? "Restore failed.";
                    break;
            }
        }
        finally
        {
            restoring = false;
        }
    }
}

/// Opens by itself until the player confirms they saved the code.
public sealed class RecoveryCodeWindow : Window
{
    private readonly Plugin plugin;

    public RecoveryCodeWindow(Plugin plugin) : base("Save your recovery code###OathboundRecoveryCode")
    {
        this.plugin = plugin;
        Flags = ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.AlwaysAutoResize;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(420, 0), MaximumSize = new Vector2(560, 400) };
    }

    public override void PreDraw() => Theme.PushWindowStyle();
    public override void PostDraw() => Theme.PopWindowStyle();

    public override void Draw()
    {
        var code = plugin.BackupService.GetFormattedCode();
        ImGui.TextWrapped("This code restores your Oathbound identity and pairings if you reinstall the plugin or move to a new PC. It's the only way to get them back - save it somewhere safe, like a password manager.");
        ImGui.Spacing();
        using (ImRaii.PushColor(ImGuiCol.Text, Theme.Accent))
            ImGui.TextUnformatted(code ?? "(unavailable)");
        ImGui.SameLine();
        if (code is not null && ImGui.SmallButton("Copy##recoveryDialogCopy"))
            ImGui.SetClipboardText(code);
        ImGui.Spacing();
        IconGlyph.WrappedDisabled("You can see it again any time in Settings > Identity & Pairing > Recovery code.");
        ImGui.Spacing();
        if (ImGui.Button("I saved it"))
        {
            plugin.BackupService.AcknowledgeCode();
            IsOpen = false;
        }
    }

    /// Closing counts as seen; the code stays available in Settings.
    public override void OnClose()
    {
        if (!plugin.Configuration.Recovery.CodeAcknowledged)
            plugin.BackupService.AcknowledgeCode();
    }
}
