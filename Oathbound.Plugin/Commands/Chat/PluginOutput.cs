using System;
using System.Collections.Generic;

namespace Oathbound.Plugin.Commands;

/// Chat lines and emotes this plugin produced itself (automatic tells, Reactions, an Owner's Custom Trigger chat
/// or animation), so rulebook oaths only judge what the Sub chose to say or do.
public static class PluginOutput
{
    private static readonly TimeSpan LineWindow = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan EmoteWindow = TimeSpan.FromSeconds(3);

    private static readonly object Gate = new();
    private static readonly List<(string Command, DateTime At)> Lines = new();
    private static DateTime lastEmote = DateTime.MinValue;

    /// The full text handed to the game, channel command included.
    public static void RecordChat(string command)
    {
        lock (Gate)
        {
            Prune();
            Lines.Add((command.Trim(), DateTime.UtcNow));
        }
    }

    public static void RecordEmote()
    {
        lock (Gate) lastEmote = DateTime.UtcNow;
    }

    /// True (and forgotten) when `line` is the echo of something this plugin just sent. The echo only carries the
    /// message body, so it's matched against the end of the sent command.
    public static bool ConsumeEcho(string line)
    {
        var body = line.Trim();
        if (body.Length == 0)
            return false;
        lock (Gate)
        {
            Prune();
            var index = Lines.FindIndex(l => l.Command.EndsWith(body, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
                return false;
            Lines.RemoveAt(index);
            return true;
        }
    }

    public static bool EmoteJustPlayed
    {
        get
        {
            lock (Gate) return DateTime.UtcNow - lastEmote < EmoteWindow;
        }
    }

    private static void Prune()
    {
        var cutoff = DateTime.UtcNow - LineWindow;
        Lines.RemoveAll(l => l.At < cutoff);
    }
}
