using System.Linq;
using System.Text.RegularExpressions;
using Dalamud.Game.Chat;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling.Payloads;

namespace Oathbound.Plugin.Games;

/// A roll result read from chat. `Max` is 0 for a bare `/random`, which shows no range.
public readonly record struct RollLine(bool Self, string? Name, string? World, int Value, int Max, RollChannel Channel);

/// Reads the game's own roll results, never anything a player typed. Only the numbers and the roller's payload are
/// used, not the words around them, so it works in every client language.
public static partial class RollReader
{
    /// The log kind of every `/random` result; the bits above it only say whose roll it was.
    private const int RandomLogKind = 0x4A;

    [GeneratedRegex("[0-9]+")]
    private static partial Regex Numbers();

    public static bool TryRead(IHandleableChatMessage message, out RollLine roll)
    {
        roll = default;
        if (((int)message.LogKind & 0x7F) == RandomLogKind)
            return TryReadRandom(message, out roll);
        if (message.LogKind == XivChatType.Party)
            return TryReadDice(message, out roll);
        return false;
    }

    private static bool TryReadRandom(IHandleableChatMessage message, out RollLine roll)
    {
        roll = default;
        var text = message.Message.TextValue;
        LogRaw("random", message);
        var numbers = Numbers().Matches(text).Select(m => int.TryParse(m.Value, out var n) ? n : -1).Where(n => n >= 0).ToList();
        if (numbers.Count is 0 or > 2)
            return false;
        // The roll never exceeds its range, so the larger number is the range whatever order the language uses.
        var value = numbers.Min();
        var max = numbers.Count == 2 ? numbers.Max() : 0;
        var player = message.Message.Payloads.OfType<PlayerPayload>().FirstOrDefault();
        roll = player is null
            ? new RollLine(true, null, null, value, max, RollChannel.Nearby)
            : new RollLine(false, player.PlayerName, player.World.Value.Name.ExtractText(), value, max, RollChannel.Nearby);
        return true;
    }

    /// A `/dice` result arrives as a party message, so only the game's own icon in it tells it apart from the same
    /// text typed by hand: typed chat can't carry an icon payload.
    private static bool TryReadDice(IHandleableChatMessage message, out RollLine roll)
    {
        roll = default;
        var text = message.Message.TextValue;
        if (!Numbers().IsMatch(text))
            return false;
        LogRaw("dice", message);
        if (!message.Message.Payloads.OfType<IconPayload>().Any())
            return false;
        var numbers = Numbers().Matches(text).Select(m => int.TryParse(m.Value, out var n) ? n : -1).Where(n => n >= 0).ToList();
        // "(1-N) V": the range's leading 1 says nothing.
        if (numbers.Count == 3 && numbers[0] == 1)
            numbers.RemoveAt(0);
        if (numbers.Count != 2)
            return false;
        var value = numbers.Min();
        var max = numbers.Max();

        var me = Plugin.ObjectTable.LocalPlayer;
        var player = message.Sender.Payloads.OfType<PlayerPayload>().FirstOrDefault();
        if (player is null)
        {
            // Your own party lines usually carry no player payload, only your name.
            if (me is null || !message.Sender.TextValue.Contains(me.Name.TextValue, System.StringComparison.OrdinalIgnoreCase))
                return false;
            roll = new RollLine(true, null, null, value, max, RollChannel.Party);
            return true;
        }
        var world = player.World.Value.Name.ExtractText();
        var self = me is not null && string.Equals(player.PlayerName, me.Name.TextValue, System.StringComparison.OrdinalIgnoreCase)
            && string.Equals(world, me.HomeWorld.Value.Name.ExtractText(), System.StringComparison.OrdinalIgnoreCase);
        roll = self
            ? new RollLine(true, null, null, value, max, RollChannel.Party)
            : new RollLine(false, player.PlayerName, world, value, max, RollChannel.Party);
        return true;
    }

    /// For checking in game how real roll results differ from typed text.
    private static void LogRaw(string what, IHandleableChatMessage message) =>
        Plugin.Log.Debug($"Roll line ({what}): kind {(int)message.LogKind:X4}, sender [{string.Join(", ", message.Sender.Payloads.Select(p => p.Type))}] \"{message.Sender.TextValue}\", "
            + $"message [{string.Join(", ", message.Message.Payloads.Select(p => p.Type))}] \"{message.Message.TextValue}\".");
}
