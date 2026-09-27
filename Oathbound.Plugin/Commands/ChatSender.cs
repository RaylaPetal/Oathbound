using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Oathbound.Plugin.Commands;

/// The one place in this plugin that can actually transmit a chat message - deliberately separate from
/// ChatComposer (which only ever builds text) so every call site capable of sending is grep-able in one
/// file. Every call here originates from a direct, single UI button click: one press, one command (one message, or for a custom trigger bundle too long for
/// one message, a fixed series of one message per action - see SendAll) - the
/// same shape as an FFXIV hotbar macro sending a /tell on a keypress, not the reactive/unattended
/// automation (auto-replying to observed chat with no human in the loop per message) that's the actual
/// pattern Dalamud plugin authors flag as ToS risk. Never wire this to anything that fires without that
/// per-message human click - no auto-reply, no reacting to received chat, no retry/resend loops.
public sealed class ChatSender
{
    /// collar/chat-transport "Trigger-phrase command delivery over a selectable channel": the exact set of
    /// prefixes ChatComposer.Wrap can produce - `/tell `/`/p `/`/a ` plus a numbered linkshell (`/l1`-`/l8`)
    /// or cross-world linkshell (`/cwl1`-`/cwl8`). Anything else refused, same as the tell-only check this
    /// replaces - a composed message with no captured peer identity yet is just the bare trigger+command
    /// text with no leading slash, which would otherwise get typed into whatever channel is currently
    /// active and leak the command into public chat instead of failing safely.
    private static readonly Regex ValidPrefix = new(@"^/(tell|p|a|l[1-8]|cwl[1-8]) ", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public bool Send(string text)
    {
        if (!ValidPrefix.IsMatch(text.TrimStart()))
        {
            Plugin.Log.Warning("Refused to send a command that wasn't one of this plugin's own composed channel commands - is a peer identity captured yet?");
            return false;
        }

        ECommons.Automation.Chat.SendMessage(text);
        Sent?.Invoke(text);
        return true;
    }

    /// Raised after each message is handed to the game - observers only (the Owner's toy status estimate),
    /// never a path that sends anything itself.
    public event Action<string>? Sent;

    /// Delay between the messages of one multi-message send, so the game registers each one rather than
    /// dropping them as sent too fast.
    private static readonly TimeSpan MessageSpacing = TimeSpan.FromSeconds(1);

    /// Sends the messages of one click (ChatComposer.ComposeAll) - still one press, one command: a custom
    /// trigger bundle too long for one message goes out as one message per action, spaced MessageSpacing
    /// apart. All are checked up front, so either every message is sent or none is.
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
