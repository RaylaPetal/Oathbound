using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Security.Cryptography;
using Oathbound.Plugin.Config;

namespace Oathbound.Plugin.Commands;

public static class CommandSelector
{
    public enum ResolutionStatus { Success, Missing, Ambiguous, Malformed }
    public readonly record struct GestureResolution(ResolutionStatus Status, GestureCatalogEntry? Entry);
    public const int MaxCommandLength = 400;
    public static string Quote(string value) => $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";

    public static bool TryRead(string text, out string value, out string remainder)
    {
        value = ""; remainder = "";
        var input = text.TrimStart();
        if (!input.StartsWith('"')) { value = input; return value.Length > 0; }
        var result = new StringBuilder();
        var escaped = false;
        for (var i = 1; i < input.Length; i++)
        {
            var c = input[i];
            if (escaped) { result.Append(c); escaped = false; continue; }
            if (c == '\\') { escaped = true; continue; }
            if (c != '"') { result.Append(c); continue; }
            value = result.ToString(); remainder = input[(i + 1)..].TrimStart(); return value.Length > 0;
        }
        return false;
    }

    public static T? ResolveUnique<T>(IEnumerable<T> entries, string selector, Func<T, string> id, params Func<T, string>[] labels) where T : class
    {
        var all = entries.ToList();
        var exactId = all.FirstOrDefault(e => string.Equals(id(e), selector, StringComparison.OrdinalIgnoreCase));
        if (exactId is not null) return exactId;
        var matches = all.Where(e => labels.Any(label => string.Equals(label(e), selector, StringComparison.OrdinalIgnoreCase))).Take(2).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    public static bool Fits(string command) => command.Length <= MaxCommandLength;

    public static string GestureLabel(string mod, string group, string animation, GestureTrigger? trigger) =>
        $"{mod} — {group} — {animation}" + (trigger is null ? "" : $" — {trigger.DisplayName}");

    public static string GestureSelector(GestureExportEntry entry, IEnumerable<GestureExportEntry> catalog)
    {
        var label = GestureLabel(entry.ModName, entry.GroupName, entry.AnimationName, entry.Trigger);
        var collides = catalog.Count(e => string.Equals(GestureLabel(e.ModName, e.GroupName, e.AnimationName, e.Trigger), label, StringComparison.OrdinalIgnoreCase)) > 1;
        return collides ? $"{label} #{entry.Id[..Math.Min(8, entry.Id.Length)]}" : label;
    }

    public static GestureCatalogEntry? ResolveGesture(IEnumerable<GestureCatalogEntry> entries, string selector)
        => ResolveGestureDetailed(entries, selector).Entry;

    public static GestureResolution ResolveGestureDetailed(IEnumerable<GestureCatalogEntry> entries, string input, bool requireTrigger = true)
    {
        if (!TryRead(input, out var selector, out var remainder) || remainder.Length > 0)
            return new GestureResolution(ResolutionStatus.Malformed, null);

        var all = entries.Where(e => !requireTrigger || e.Trigger is not null).ToList();
        var exactIds = all.Where(e => string.Equals(e.Id, selector, StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
        if (exactIds.Count == 1)
            return new GestureResolution(ResolutionStatus.Success, exactIds[0]);
        if (exactIds.Count > 1)
            return new GestureResolution(ResolutionStatus.Ambiguous, null);

        var hash = selector.LastIndexOf(" #", StringComparison.Ordinal);
        if (hash > 0)
        {
            var prefix = selector[(hash + 2)..];
            var byPrefix = all.Where(e => e.Id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
            if (byPrefix.Count == 1) return new GestureResolution(ResolutionStatus.Success, byPrefix[0]);
            if (byPrefix.Count > 1) return new GestureResolution(ResolutionStatus.Ambiguous, null);
        }

        // Most specific first, so a selector naming one entry exactly isn't called ambiguous because a looser name matches more.
        Func<GestureCatalogEntry, string>[] tiers =
        [
            e => GestureLabel(e.ModName, e.GroupName, e.AnimationName, e.Trigger),
            e => e.Label,
            e => e.AnimationName,
        ];
        foreach (var name in tiers)
        {
            var matches = all.Where(e => string.Equals(name(e), selector, StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
            if (matches.Count == 1) return new GestureResolution(ResolutionStatus.Success, matches[0]);
            if (matches.Count > 1) return new GestureResolution(ResolutionStatus.Ambiguous, null);
        }
        return new GestureResolution(ResolutionStatus.Missing, null);
    }

    public static string MoodleSelector(string rawName, IEnumerable<string> rawNames)
    {
        var clean = MoodlesTextFormat.StripMarkup(rawName);
        if (rawNames.Count(n => string.Equals(MoodlesTextFormat.StripMarkup(n), clean, StringComparison.OrdinalIgnoreCase)) <= 1) return clean;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawName)))[..6];
        return $"{clean} #{hash}";
    }

    public static MoodlesStatusEntry? ResolveMoodle(IEnumerable<MoodlesStatusEntry> entries, string selector)
    {
        var all = entries.ToList();
        var hashAt = selector.LastIndexOf(" #", StringComparison.Ordinal);
        if (hashAt > 0)
        {
            var wanted = selector[(hashAt + 2)..];
            var hits = all.Where(e => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(e.Name))).StartsWith(wanted, StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
            if (hits.Count == 1) return hits[0];
        }
        return ResolveUnique(all, selector, e => e.StatusId, e => e.Name, e => MoodlesTextFormat.StripMarkup(e.Name));
    }
}
