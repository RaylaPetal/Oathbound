using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Oathbound.Plugin.Config;
using Oathbound.Plugin.Relay;

namespace Oathbound.Plugin.Commands;

/// A partner still on a version from before live messaging sends commands and notices as tells, which this version
/// never applies. Recognizes those tells from a paired partner, hides them, marks the pairing as needing an update and
/// says so once per session. Reads incoming tells only, and sends nothing.
public sealed class LegacyTellDetector : IDisposable
{
    private readonly PluginConfig config;
    private readonly PeerMessenger messenger;
    private readonly HashSet<Guid> explained = new();

    public LegacyTellDetector(PluginConfig config, PeerMessenger messenger)
    {
        this.config = config;
        this.messenger = messenger;
        Plugin.ChatGui.ChatMessage += OnChatMessage;
    }

    public void Dispose() => Plugin.ChatGui.ChatMessage -= OnChatMessage;

    private void OnChatMessage(Dalamud.Game.Chat.IHandleableChatMessage message)
    {
        if (message.LogKind != XivChatType.TellIncoming)
            return;
        var (name, world) = NameAndWorld(message.Sender);
        if (name is null)
            return;
        var text = message.Message.TextValue.Trim();

        foreach (var pairing in config.ActivePairings.Where(p => world is not null ? p.IsConfirmedCharacter(name, world) : p.HasConfirmedName(name)).ToList())
        {
            // A partner whose frames reach us has updated; a tell that looks like a command is just them typing.
            if (pairing.PeerRelayState == PeerRelayState.Reachable || !LooksLegacy(text, pairing))
                continue;
            message.PreventOriginal();
            messenger.SetRelayState(pairing, PeerRelayState.NeedsUpdate);
            if (explained.Add(pairing.Id))
                OathboundChat.Print($"{name} sent you something from an older Oathbound, so it wasn't applied. "
                    + "They need to update Oathbound for anything to work between you - \"Ask them to update\" in Settings sends them a reminder.");
            return;
        }
    }

    private bool LooksLegacy(string text, PairingState pairing)
    {
        if (CommandComposer.LegacyTellKeywords.Any(k => StartsWithWord(text, k)))
            return true;
        // An older Owner's command starts with this Sub's trigger phrase.
        var trigger = config.TriggerPhrase.Trim();
        return pairing.Direction == PairingDirection.SubSide && trigger.Length > 0 && StartsWithWord(text, trigger);
    }

    private static bool StartsWithWord(string text, string word) =>
        text.StartsWith(word, StringComparison.OrdinalIgnoreCase) && (text.Length == word.Length || char.IsWhiteSpace(text[word.Length]));

    private static (string? Name, string? World) NameAndWorld(SeString sender)
    {
        var player = sender.Payloads.OfType<PlayerPayload>().FirstOrDefault();
        if (player is not null)
            return (player.PlayerName, player.World.Value.Name.ExtractText());
        var text = sender.TextValue.Trim();
        var at = text.IndexOf('@');
        return at >= 0 ? (text[..at].Trim(), text[(at + 1)..].Trim()) : (text.Length > 0 ? text : null, null);
    }
}
