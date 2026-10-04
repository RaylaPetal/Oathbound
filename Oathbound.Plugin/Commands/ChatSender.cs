using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Oathbound.Plugin.Commands;

/// The one place that can transmit chat. Every call comes from a single button click: one press, one command.
/// Never wire it to anything that fires without that click. A Reaction's chat reply is the one deliberate exception,
/// and it lives outside this class.
public sealed class ChatSender
{
    /// Exactly the prefixes ChatComposer can produce. A message without one would land in the active channel and leak
    /// the command into public chat.
    private static readonly Regex ValidPrefix = new(@"^/(tell|p|a|l[1-8]|cwl[1-8]) ", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public bool Send(string text)
    {
        if (!ValidPrefix.IsMatch(text.TrimStart()))
        {
            Plugin.Log.Warning("Refused to send a command that wasn't one of this plugin's own composed channel commands - is a peer identity captured yet?");
            return false;
        }

        PluginOutput.RecordChat(text);
        ECommons.Automation.Chat.SendMessage(text);
        Sent?.Invoke(text);
        return true;
    }

    /// Observers only; never a path that sends.
    public event Action<string>? Sent;

    /// The game drops messages sent too fast.
    private static readonly TimeSpan MessageSpacing = TimeSpan.FromSeconds(1);

    /// All messages are checked up front, so either every one is sent or none is.
    public bool SendAll(IReadOnlyList<string> messages)
    {
        if (messages.Count == 0)
            return false;
        if (!messages.All(m => ValidPrefix.IsMatch(m.TrimStart())))
        {
            Plugin.Log.Warning("Refused to send a command that wasn't one of this plugin's own composed channel commands - is a peer identity captured yet?");
            return false;
        }

        Send(messages[0]);
        for (var i = 1; i < messages.Count; i++)
        {
            var message = messages[i];
            _ = Plugin.Framework.RunOnTick(() => Send(message), MessageSpacing * i);
        }
        return true;
    }
}
