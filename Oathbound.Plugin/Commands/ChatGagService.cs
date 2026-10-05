using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Oathbound.Plugin.Config;
using Oathbound.Plugin.Ipc;
using Oathbound.Plugin.Safety;
using Dalamud.Hooking;
using Dalamud.Utility.Signatures;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Component.Shell;

namespace Oathbound.Plugin.Commands;

#pragma warning disable CS0649 // assigned via reflection by Svc.Hook.InitializeFromAttributes, not by the compiler

/// The Gagged rule: rewrites outgoing chat in ShellCommandModule.ProcessChatInput (after Enter, before the server),
/// plus its optional Customize+ preset. Rewriting text the Sub typed is a heavier automation surface than anything
/// else here. Fails closed: if the signature doesn't resolve, chat is never touched.
public sealed unsafe class ChatGagService : IRestrictionEnforcer, IDisposable
{
    private const string SigProcessChatInput = "E8 ?? ?? ?? ?? FE 87 ?? ?? ?? ?? C7 87";

    public unsafe delegate void ProcessChatInputDelegate(ShellCommandModule* uiModule, Utf8String* message, nint a3);

    [Signature(SigProcessChatInput, DetourName = nameof(ProcessChatInputDetour), Fallibility = Fallibility.Auto)]
    private readonly Hook<ProcessChatInputDelegate>? processChatInputHook;

    private readonly CustomizePlusIpc customizePlusIpc;

    private bool active;

    /// Keyed per device, so releasing one device reverts only its own preset.
    private readonly Dictionary<string, Guid> activeCustomizePresets = new();

    private static readonly HashSet<string> ChatChannelCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "/say", "/s", "/yell", "/y", "/shout", "/sh",
        "/party", "/p", "/alliance", "/a", "/freecompany", "/fc",
        "/novicenetwork", "/n", "/reply", "/r",
    };

    public ChatGagService(CustomizePlusIpc customizePlusIpc)
    {
        this.customizePlusIpc = customizePlusIpc;
        Svc.Hook.InitializeFromAttributes(this);

        IsAvailable = processChatInputHook is not null;
        if (IsAvailable)
            processChatInputHook!.Enable();
        else
            Plugin.Log.Error("ChatGagService: ProcessChatInput hook failed to resolve - gag chat-mangling is disabled for this session.");
    }

    public bool IsAvailable { get; }

    /// The heaviest level among active gags; Engage stays level-free so the rule manager's reference counting is unchanged.
    public Func<GagLevel>? LevelSource { get; set; }

    public void Engage()
    {
        if (IsAvailable)
            active = true;
    }

    public void Release() => active = false;

    /// Never blocks or rolls back the device apply; a missing profile or plugin just means no preset.
    public void ApplyCustomizePreset(string deviceId, string? selector)
    {
        if (string.IsNullOrWhiteSpace(selector))
            return;
        if (ResolveProfile(selector) is not { } profileId)
        {
            Plugin.Log.Warning($"Restraint Gagged rule: Customize+ preset '{selector}' is unavailable; continuing without it.");
            return;
        }
        if (customizePlusIpc.ApplyProfile(profileId))
            activeCustomizePresets[deviceId] = profileId;
    }

    public void RevertCustomizePreset(string deviceId)
    {
        if (!activeCustomizePresets.Remove(deviceId, out var profileId))
            return;
        customizePlusIpc.RevertProfile(profileId);
    }

    /// Called from there, so ForceUnlock and panic both revert every preset.
    public void RevertAllCustomizePresetsForPanic()
    {
        foreach (var profileId in activeCustomizePresets.Values)
            customizePlusIpc.RevertProfile(profileId);
        activeCustomizePresets.Clear();
    }

    private Guid? ResolveProfile(string selector)
    {
        var profiles = customizePlusIpc.GetOwnProfiles().Profiles;
        if (Guid.TryParse(selector, out var directId) && profiles.Any(p => p.UniqueId == directId))
            return directId;

        var byName = profiles.FirstOrDefault(p => string.Equals(p.Name, selector, StringComparison.OrdinalIgnoreCase));
        if (byName.Name is not null)
            return byName.UniqueId;

        // Undo the middle-dot escape for commas before matching by name.
        if (!selector.Contains('·'))
            return null;
        var unescaped = selector.Replace('·', ',');
        var match = profiles.FirstOrDefault(p => string.Equals(p.Name, unescaped, StringComparison.OrdinalIgnoreCase));
        return match.Name is not null ? match.UniqueId : null;
    }

    private void ProcessChatInputDetour(ShellCommandModule* uiModule, Utf8String* message, nint a3)
    {
        if (!active)
        {
            processChatInputHook!.Original(uiModule, message, a3);
            return;
        }

        try
        {
            var original = message->ToString();
            if (!string.IsNullOrWhiteSpace(original))
            {
                var rewritten = RewriteOutgoingChat(original, LevelSource?.Invoke() ?? GagLevel.Heavy);
                if (!string.Equals(rewritten, original, StringComparison.Ordinal)
                    && rewritten.Length > 0
                    && rewritten.Length <= 500)
                    message->SetString(rewritten);
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "ChatGagService: failed to garble outgoing chat - sending original text.");
        }

        processChatInputHook!.Original(uiModule, message, a3);
    }

    /// Each letter becomes a muffled syllable; punctuation and spacing are kept.
    private static readonly string[] Syllables = ["mm", "mph", "hmm", "mmf", "mrph"];

    /// Rewrites speech in chat channels, keeping the slash command and a tell's recipient. Other slash commands
    /// pass through unchanged.
    internal static string RewriteOutgoingChat(string text, GagLevel level = GagLevel.Heavy)
    {
        if (string.IsNullOrWhiteSpace(text) || text[0] != '/')
            return Garble(text, level);

        var commandEnd = text.IndexOf(' ');
        if (commandEnd < 0)
            return text;

        var command = text[..commandEnd];
        int bodyStart;
        if (command.Equals("/tell", StringComparison.OrdinalIgnoreCase)
            || command.Equals("/t", StringComparison.OrdinalIgnoreCase))
        {
            // Names contain a space; ChatComposer uses Name@World, so the first whitespace after the world starts the body.
            var worldSeparator = text.IndexOf('@', commandEnd + 1);
            if (worldSeparator < 0)
                return text;

            bodyStart = FindBodyStart(text, worldSeparator + 1);
        }
        else if (ChatChannelCommands.Contains(command) || IsNumberedChatChannel(command))
        {
            bodyStart = FindBodyStart(text, commandEnd);
        }
        else
        {
            return text;
        }

        if (bodyStart >= text.Length || IsInternalProtocolMessage(text.AsSpan(bodyStart)))
            return text;

        return text[..bodyStart] + Garble(text[bodyStart..], level);
    }

    private static int FindBodyStart(string text, int searchFrom)
    {
        var separator = text.IndexOf(' ', searchFrom);
        if (separator < 0)
            return text.Length;

        var bodyStart = separator + 1;
        while (bodyStart < text.Length && text[bodyStart] == ' ')
            bodyStart++;
        return bodyStart;
    }

    private static bool IsNumberedChatChannel(string command)
    {
        foreach (var prefix in new[] { "/linkshell", "/l", "/cwlinkshell", "/cwl" })
        {
            if (command.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(command.AsSpan(prefix.Length), out var channel)
                && channel is >= 1 and <= 8)
                return true;
        }

        return false;
    }

    private static bool IsInternalProtocolMessage(ReadOnlySpan<char> body)
        => body.StartsWith("collarpair ", StringComparison.OrdinalIgnoreCase)
           || body.StartsWith("collarpairack ", StringComparison.OrdinalIgnoreCase)
           || body.StartsWith("collarunpair ", StringComparison.OrdinalIgnoreCase);

    /// Medium keeps each word's first letter and length, so the shape of the speech stays readable.
    private const string MuffleLetters = "mmhmpf";

    /// Light leaves words this short alone.
    private const int LightMinWordLength = 4;

    /// Text between `*` pairs (inline RP emotes) passes through. A simple toggle, so an unmatched `*` exempts the rest.
    internal static string Garble(string text, GagLevel level = GagLevel.Heavy)
    {
        var words = new List<(int Start, int End)>();
        var exempt = false;
        for (var i = 0; i < text.Length;)
        {
            if (text[i] == '*')
            {
                exempt = !exempt;
                i++;
            }
            else if (!exempt && char.IsLetter(text[i]))
            {
                var start = i;
                while (i < text.Length && char.IsLetter(text[i]))
                    i++;
                words.Add((start, i));
            }
            else
            {
                i++;
            }
        }

        var garbled = level == GagLevel.Light ? PickLightWords(words) : null;
        var sb = new StringBuilder();
        var syllableIndex = 0;
        var at = 0;
        for (var w = 0; w < words.Count; w++)
        {
            var (start, end) = words[w];
            sb.Append(text, at, start - at);
            at = end;
            var word = text[start..end];
            if (garbled is not null && !garbled.Contains(w))
            {
                sb.Append(word);
                continue;
            }
            if (level == GagLevel.Medium)
            {
                sb.Append(word[0]);
                for (var c = 1; c < word.Length; c++)
                    sb.Append(char.IsUpper(word[c]) ? char.ToUpperInvariant(MuffleLetters[c % MuffleLetters.Length]) : MuffleLetters[c % MuffleLetters.Length]);
                continue;
            }
            var syllable = Syllables[syllableIndex++ % Syllables.Length];
            sb.Append(char.IsUpper(word[0]) ? char.ToUpperInvariant(syllable[0]) + syllable[1..] : syllable);
        }
        sb.Append(text, at, text.Length - at);
        return sb.ToString();
    }

    /// About one longer word in three, and always at least one when there is any.
    private static HashSet<int> PickLightWords(List<(int Start, int End)> words)
    {
        var eligible = Enumerable.Range(0, words.Count).Where(w => words[w].End - words[w].Start >= LightMinWordLength).ToList();
        var picked = eligible.Where(_ => Random.Shared.Next(3) == 0).ToHashSet();
        if (picked.Count == 0 && eligible.Count > 0)
            picked.Add(eligible[Random.Shared.Next(eligible.Count)]);
        return picked;
    }

    public void Dispose()
    {
        active = false;
        processChatInputHook?.Dispose();
    }
}
