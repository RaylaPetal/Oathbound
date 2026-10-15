using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using ECommons.Automation;
using Oathbound.Plugin.Config;
using Oathbound.Plugin.Ipc;
using Oathbound.Plugin.Safety;

namespace Oathbound.Plugin.Commands;

/// Tracked so it can be turned off again from the window, by panic, or on unload.
public sealed record ActiveReactionMod(string ReactionId, Guid Collection, string Directory, string Name);

/// Local automatic reactions to emotes or a chat trigger phrase; never driven by or reported to a peer.
/// Events are queued and acted on from the framework tick. Only the first matching reaction per event fires.
/// A chat action replies automatically, guarded only by its cooldown (at least 10 s for chat).
public sealed class ReactionService : IDisposable
{
    private static readonly XivChatType[] WatchedChatTypes =
    [
        XivChatType.Say, XivChatType.Yell, XivChatType.Shout, XivChatType.CustomEmote,
        XivChatType.TellIncoming, XivChatType.Party, XivChatType.CrossParty, XivChatType.Alliance, XivChatType.FreeCompany,
        XivChatType.Ls1, XivChatType.Ls2, XivChatType.Ls3, XivChatType.Ls4,
        XivChatType.Ls5, XivChatType.Ls6, XivChatType.Ls7, XivChatType.Ls8,
        XivChatType.CrossLinkShell1, XivChatType.CrossLinkShell2, XivChatType.CrossLinkShell3,
        XivChatType.CrossLinkShell4, XivChatType.CrossLinkShell5, XivChatType.CrossLinkShell6,
        XivChatType.CrossLinkShell7, XivChatType.CrossLinkShell8,
    ];

    private readonly record struct ChatEvent(string Text, string SenderName, string? SenderWorld);

    private readonly PluginConfig config;
    private readonly SubRuntimeState runtimeState;
    private readonly EmoteWatcher emoteWatcher;
    private readonly GlamourerIpc glamourer;
    private readonly SlotLockManager slotLocks;
    private readonly PenumbraIpc penumbra;
    private readonly TemporaryModSettingsCoordinator modSettings;
    private readonly MoodlesIpc moodles;
    private readonly RestrictionRuleManager restrictionRules;

    private readonly ConcurrentQueue<ChatEvent> chatEvents = new();
    private readonly Dictionary<string, long> lastFiredTicks = new();
    private readonly List<ActiveReactionMod> activeMods = new();

    public IReadOnlyList<ActiveReactionMod> ActiveMods => activeMods;

    public long? LastFired(string reactionId) => lastFiredTicks.TryGetValue(reactionId, out var t) ? t : null;

    public ReactionService(PluginConfig config, SubRuntimeState runtimeState, EmoteWatcher emoteWatcher, GlamourerIpc glamourer, SlotLockManager slotLocks,
        PenumbraIpc penumbra, TemporaryModSettingsCoordinator modSettings, MoodlesIpc moodles, RestrictionRuleManager restrictionRules)
    {
        this.config = config;
        this.runtimeState = runtimeState;
        this.emoteWatcher = emoteWatcher;
        this.glamourer = glamourer;
        this.slotLocks = slotLocks;
        this.penumbra = penumbra;
        this.modSettings = modSettings;
        this.moodles = moodles;
        this.restrictionRules = restrictionRules;
        Plugin.ChatGui.ChatMessage += OnChatMessage;
    }

    private void OnChatMessage(Dalamud.Game.Chat.IChatMessage message)
    {
        if (Array.IndexOf(WatchedChatTypes, message.LogKind) < 0)
            return;
        var (name, world) = ExtractNameAndWorld(message.Sender);
        if (name is null)
            return;
        chatEvents.Enqueue(new ChatEvent(message.Message.TextValue.Trim(), name, world));
    }

    /// After EmoteWatcher.Poll.
    public void OnFrameworkUpdate()
    {
        var chatThisTick = new List<ChatEvent>();
        while (chatEvents.TryDequeue(out var chat))
            chatThisTick.Add(chat);

        if (runtimeState.ReactionsSuspended || config.Reactions.Count == 0)
            return;
        var localPlayer = Plugin.ObjectTable.LocalPlayer;
        if (localPlayer is null)
            return;
        var localName = localPlayer.Name.TextValue;

        foreach (var emote in emoteWatcher.ThisTick)
        {
            if (emote.TargetObjectId != localPlayer.GameObjectId)
                continue;
            var match = config.Reactions.FirstOrDefault(r =>
                r is { Enabled: true, TriggerKind: ReactionTriggerKind.Emote } && r.HasAnyAction &&
                r.EmoteId != 0 && r.EmoteId == emote.EmoteId &&
                SourceAllowed(r, emote.SourceName, emote.SourceWorld) &&
                DirectionClassifier.Matches(r.Direction, localPlayer.Position, localPlayer.Rotation, emote.SourcePosition));
            if (match is not null)
                TryFire(match);
        }

        foreach (var chat in chatThisTick)
        {
            if (string.Equals(chat.SenderName, localName, StringComparison.OrdinalIgnoreCase))
                continue; // the user's own messages never trigger their own reactions
            if (PhraseAfterTriggerWord(chat.Text) is not { } said)
                continue;
            var match = config.Reactions.FirstOrDefault(r =>
                r is { Enabled: true, TriggerKind: ReactionTriggerKind.ChatPhrase } && r.HasAnyAction &&
                PhraseMatches(said, r.ChatPhrase) &&
                SourceAllowed(r, chat.SenderName, chat.SenderWorld));
            if (match is not null)
                TryFire(match);
        }
    }

    /// Null when the message doesn't start with the trigger word.
    private string? PhraseAfterTriggerWord(string text)
    {
        var trigger = config.TriggerPhrase.Trim();
        if (trigger.Length == 0 || !text.StartsWith(trigger, StringComparison.OrdinalIgnoreCase))
            return null;
        var rest = text[trigger.Length..];
        return rest.Length > 0 && char.IsWhiteSpace(rest[0]) ? rest.Trim() : null;
    }

    /// The phrase must be the whole remainder or its leading words.
    public static bool PhraseMatches(string said, string phrase)
    {
        var wanted = Normalize(phrase);
        var got = Normalize(said);
        return wanted.Length > 0 && (got.Equals(wanted, StringComparison.OrdinalIgnoreCase) ||
            got.StartsWith(wanted + " ", StringComparison.OrdinalIgnoreCase));
    }

    private static string Normalize(string text) => string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    /// Paired characters only unless the reaction allows anyone. A sender with no world matches on name alone.
    private bool SourceAllowed(ReactionRule rule, string name, string? world)
    {
        if (rule.AllowAnyone)
            return true;
        return config.Pairings.Any(p => p.IsPaired &&
            string.Equals(p.PeerName, name, StringComparison.OrdinalIgnoreCase) &&
            (string.IsNullOrEmpty(world) || string.Equals(p.PeerWorld, world, StringComparison.OrdinalIgnoreCase)));
    }

    private void TryFire(ReactionRule rule)
    {
        var now = Environment.TickCount64;
        if (lastFiredTicks.TryGetValue(rule.Id, out var last) && now - last < rule.EffectiveCooldownSeconds * 1000L)
            return;
        lastFiredTicks[rule.Id] = now;
        Fire(rule);
    }

    /// Each action is independent: one failing never stops the others.
    public void Fire(ReactionRule rule)
    {
        Run("gesture", () => PlayGesture(rule));
        Run("item", () => EquipItem(rule));
        Run("mod", () => EnableMod(rule));
        Run("moodle", () => { if (rule.MoodleId is { } id) moodles.ApplyStatus(id); });
        Run("chat", () =>
        {
            if (string.IsNullOrWhiteSpace(rule.ChatMessage)) return;
            PluginOutput.RecordChat(rule.ChatMessage.Trim());
            Chat.SendMessage(rule.ChatMessage.Trim());
        });
    }

    private static void Run(string what, Action action)
    {
        try { action(); }
        catch (Exception ex) { Plugin.Log.Warning(ex, $"Reaction {what} action failed."); }
    }

    /// Skipped while a restraint holds a pose. Keep facing clears the target around the gesture and restores it next frame.
    private void PlayGesture(ReactionRule rule)
    {
        if (rule.Gesture is not { } gesture)
            return;
        if (restrictionRules.IsActive(RestraintRuleKind.ForcedPose) || restrictionRules.IsActive(RestraintRuleKind.ArmsCuffed) ||
            restrictionRules.IsActive(RestraintRuleKind.LegsCuffed) || restrictionRules.IsActive(RestraintRuleKind.FullBodyCuffed))
            return;

        var previousTarget = rule.KeepFacing ? Plugin.TargetManager.Target : null;
        if (previousTarget is not null)
            Plugin.TargetManager.Target = null;
        GestureCommand.Play(gesture);
        if (previousTarget is not null)
            Plugin.Framework.RunOnTick(() =>
            {
                if (Plugin.TargetManager.Target is null)
                    Plugin.TargetManager.Target = previousTarget;
            }, delayTicks: 1);
    }

    /// Never into a slot another feature holds locked.
    private void EquipItem(ReactionRule rule)
    {
        if (rule is not { ItemSlot: { } slot, ItemId: > 0 and var itemId } || slotLocks.GetLockedValue(slot) is not null)
            return;
        glamourer.SetItemOnce(slot, itemId, [0, 0]);
    }

    private void EnableMod(ReactionRule rule)
    {
        if (string.IsNullOrWhiteSpace(rule.ModDirectory) || penumbra.TryGetLocalPlayerCollectionId() is not { } collection)
            return;
        var selections = rule.ModSelections.ToDictionary(x => x.Key, x => (IReadOnlyList<string>)x.Value);
        if (!modSettings.Acquire(OwnerKey(rule.Id), collection, rule.ModDirectory, selections))
            return;
        activeMods.RemoveAll(m => m.ReactionId == rule.Id);
        activeMods.Add(new ActiveReactionMod(rule.Id, collection, rule.ModDirectory, rule.ModName));
        penumbra.TryRedrawLocalPlayer();
    }

    /// Releasing the claim restores whatever was underneath.
    public void TurnOff(ActiveReactionMod mod)
    {
        modSettings.Release(OwnerKey(mod.ReactionId), mod.Collection, mod.Directory);
        activeMods.Remove(mod);
        penumbra.TryRedrawLocalPlayer();
    }

    /// Suspends every reaction and turns off every reaction mod.
    public void ReleaseAllForPanic()
    {
        runtimeState.ReactionsSuspended = true;
        foreach (var mod in activeMods.ToList())
            TurnOff(mod);
    }

    private static string OwnerKey(string reactionId) => $"reaction:{reactionId}";

    private static (string? Name, string? World) ExtractNameAndWorld(SeString sender)
    {
        var playerPayload = sender.Payloads.OfType<PlayerPayload>().FirstOrDefault();
        if (playerPayload is not null)
            return (playerPayload.PlayerName, playerPayload.World.Value.Name.ExtractText());
        var text = sender.TextValue.Trim();
        var atIndex = text.IndexOf('@');
        return atIndex >= 0 ? (text[..atIndex].Trim(), text[(atIndex + 1)..].Trim()) : (text.Length > 0 ? text : null, null);
    }

    public void Dispose()
    {
        Plugin.ChatGui.ChatMessage -= OnChatMessage;
        // Claims themselves are released by TemporaryModSettingsCoordinator.Dispose.
        activeMods.Clear();
    }
}
