using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Oathbound.Plugin.Config;

namespace Oathbound.Plugin.Commands;

/// Sends exactly two kinds of game chat: the Games Roll button's commands, and the "Ask them to update" reminder to a
/// partner still on a version from before live messaging. Every call comes from a single button click. Reactions
/// replies and Custom Trigger chat actions send their own lines; commands and notices between paired players never
/// go through chat at all.
public sealed class ChatSender
{
    /// `/dice` rolls in the active channel, so a party roll first switches it with a bare `/p`.
    private static readonly Regex RollCommand = new(@"^(/p|/(random|dice) [0-9]{1,3})$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly TimeSpan ReminderCooldown = TimeSpan.FromMinutes(1);
    private readonly Dictionary<Guid, DateTime> lastReminderUtc = new();

    public const string UpdateReminderText =
        "[Oathbound] I've updated Oathbound - commands between us now need you to update too. Please update the plugin in /xlplugins.";

    public bool SendRoll(string command)
    {
        if (!RollCommand.IsMatch(command.Trim()))
        {
            Plugin.Log.Warning("Refused to send something that isn't a game roll command.");
            return false;
        }
        PluginOutput.RecordChat(command);
        ECommons.Automation.Chat.SendMessage(command);
        return true;
    }

    /// Seconds until the reminder can be sent again for this pairing; 0 when it can.
    public int ReminderWaitSeconds(PairingState pairing) =>
        lastReminderUtc.TryGetValue(pairing.Id, out var last) && DateTime.UtcNow - last < ReminderCooldown
            ? (int)Math.Ceiling((ReminderCooldown - (DateTime.UtcNow - last)).TotalSeconds)
            : 0;

    /// One plain tell to the partner's current character, at most once a minute per pairing.
    public bool SendUpdateReminder(PairingState pairing)
    {
        if (pairing is not { IsPaired: true, PeerName: { Length: > 0 } name, PeerWorld: { Length: > 0 } world } || ReminderWaitSeconds(pairing) > 0)
            return false;
        lastReminderUtc[pairing.Id] = DateTime.UtcNow;
        var text = $"/tell {name}@{world} {UpdateReminderText}";
        PluginOutput.RecordChat(text);
        ECommons.Automation.Chat.SendMessage(text);
        return true;
    }
}
