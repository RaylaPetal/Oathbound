using System;
using System.Text;

namespace Oathbound.Plugin.Commands;

/// A moodle the Owner wrote. It travels whole in the command and is applied from that data, never added to the
/// Sub's Moodles library. The same Id updates the copy the Sub already has instead of adding a second one.
[Serializable]
public sealed class CustomMoodle
{
    public const int MaxTitleLength = 48;
    public const int MaxDescriptionLength = 160;
    public const int MinSeconds = 60;
    public const int MaxSeconds = 7 * 24 * 3600;

    public Guid Id { get; set; } = Guid.NewGuid();
    /// A game status icon.
    public int IconId { get; set; }
    /// Moodles' StatusType: 0 positive, 1 negative, 2 special.
    public int Kind { get; set; }
    /// 0 = until removed.
    public int Seconds { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";

    /// Null when it can be sent, otherwise why not. Checked by the Owner's builder and again by the Sub.
    public string? Problem()
    {
        if (string.IsNullOrWhiteSpace(Title))
            return "It needs a title.";
        if (Title.Length > MaxTitleLength)
            return $"The title is at most {MaxTitleLength} characters.";
        if (Description.Length > MaxDescriptionLength)
            return $"The description is at most {MaxDescriptionLength} characters.";
        if (Title.Contains('\n') || Title.Contains('\r') || Description.Contains('\n') || Description.Contains('\r'))
            return "No line breaks.";
        if (IconId <= 0)
            return "Pick an icon.";
        if (Kind is < 0 or > 2)
            return "Pick positive, negative or special.";
        if (Seconds != 0 && Seconds is < MinSeconds or > MaxSeconds)
            return "It lasts 1 minute to 7 days, or until removed.";
        return null;
    }

    public CustomMoodle Clone() => new() { Id = Id, IconId = IconId, Kind = Kind, Seconds = Seconds, Title = Title, Description = Description };

    /// `id|icon|kind|seconds|title|description`, texts base64url so pipes, quotes and markup can't break parsing.
    public string Encode() =>
        $"{EncodeId(Id)}|{IconId}|{Kind}|{Seconds}|{B64(Title)}|{B64(Description)}";

    /// Refuses anything malformed or outside the limits, whole.
    public static bool TryDecode(string payload, out CustomMoodle moodle)
    {
        moodle = new CustomMoodle();
        var parts = payload.Trim().Split('|');
        if (parts.Length != 6
            || !TryDecodeId(parts[0], out var id)
            || !int.TryParse(parts[1], out var icon)
            || !int.TryParse(parts[2], out var kind)
            || !int.TryParse(parts[3], out var seconds)
            || !TryUnB64(parts[4], out var title)
            || !TryUnB64(parts[5], out var description))
            return false;
        moodle = new CustomMoodle { Id = id, IconId = icon, Kind = kind, Seconds = seconds, Title = title, Description = description };
        return moodle.Problem() is null;
    }

    public const string CustomWord = "custom";
    public const string RemoveWord = "remove";

    public string ApplyCommand() => $"moodle {CustomWord} {Encode()}";

    public string RemoveCommand() => $"moodle {RemoveWord} {EncodeId(Id)}";

    public static string EncodeId(Guid id) => Base64Url(id.ToByteArray());

    public static bool TryDecodeId(string text, out Guid id)
    {
        id = Guid.Empty;
        if (!TryFromBase64Url(text.Trim(), out var bytes) || bytes.Length != 16)
            return false;
        id = new Guid(bytes);
        return id != Guid.Empty;
    }

    private static string B64(string text) => Base64Url(Encoding.UTF8.GetBytes(text));

    private static bool TryUnB64(string text, out string value)
    {
        value = "";
        if (!TryFromBase64Url(text, out var bytes))
            return false;
        try
        {
            value = new UTF8Encoding(false, true).GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool TryFromBase64Url(string text, out byte[] bytes)
    {
        bytes = [];
        var s = text.Replace('-', '+').Replace('_', '/');
        s = s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
        try
        {
            bytes = Convert.FromBase64String(s);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
