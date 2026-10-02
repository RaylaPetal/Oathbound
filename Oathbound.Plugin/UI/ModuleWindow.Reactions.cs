using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Oathbound.Plugin.Commands;
using Oathbound.Plugin.Config;
using Oathbound.Plugin.Ipc;
using Oathbound.Plugin.Safety;

namespace Oathbound.Plugin.UI;

/// The Reactions module. Available to both roles; nothing here involves a paired peer.
public sealed partial class ModuleWindow
{
    private ReactionRule? reactionDraft;
    private string? reactionError;
    private string reactionEmoteSearch = "";
    private string reactionGestureSearch = "";
    private string reactionModSearch = "";
    private IReadOnlyList<MoodlesStatus>? reactionMoodles;
    private Dictionary<string, string>? reactionModList;

    private static readonly string[] ReactionTriggerNames = ["Emote used on me", "Chat phrase after my trigger word"];
    private static readonly string[] ReactionDirectionNames = ["Any direction", "In front of me", "Behind me"];

    private static List<(uint Id, string Name, string Command)>? emoteRows;

    /// Cached once.
    private static List<(uint Id, string Name, string Command)> EmoteRows => emoteRows ??=
        Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Emote>()
            .Where(e => e.RowId > 0 && e.Name.ExtractText().Length > 0)
            .Select(e => (e.RowId, Name: e.Name.ExtractText(), Command: e.TextCommand.ValueNullable?.Command.ExtractText() ?? ""))
            .Where(e => e.Command.Length > 0)
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string EmoteLabel(uint id) =>
        EmoteRows.FirstOrDefault(e => e.Id == id) is { Name: not null } row && row.Id != 0 ? $"{row.Name} ({row.Command})" : "(no emote chosen)";

    private void DrawReactionsModule()
    {
        var config = plugin.Configuration;
        var reactions = plugin.ReactionService;
        IconGlyph.Text(FontAwesomeIcon.Magic, "Reactions");
        ImGui.Separator();
        IconGlyph.WrappedDisabled("Make your own character react when someone uses an emote on you, or says a phrase after your trigger word. Reactions run only on your own client and aren't tied to any Owner command.");

        if (plugin.RuntimeState.ReactionsSuspended)
        {
            using (Section.Begin("reactionsSuspended"))
            {
                IconGlyph.WrappedColored(Theme.Warning, "Reactions are paused because panic was triggered. Nothing below will fire until you resume them.");
                if (ImGui.SmallButton("Resume reactions"))
                    plugin.RuntimeState.ReactionsSuspended = false;
            }
        }

        if (reactions.ActiveMods.Count > 0)
        {
            using (Section.Begin("reactionActiveMods", "Mods turned on by reactions"))
            {
                foreach (var mod in reactions.ActiveMods.ToList())
                {
                    ImGui.TextUnformatted(mod.Name.Length > 0 ? mod.Name : mod.Directory);
                    ContinueRowOrWrap(ButtonWidth("Turn off"));
                    if (ImGui.SmallButton($"Turn off##reactionMod{mod.ReactionId}"))
                        reactions.TurnOff(mod);
                }
            }
        }

        using (Section.Begin("reactionList", "Your reactions"))
        {
            if (config.Reactions.Count == 0)
                IconGlyph.WrappedDisabled("No reactions yet.");

            foreach (var rule in config.Reactions.ToList())
            {
                ImGui.PushID(rule.Id);
                var enabled = rule.Enabled;
                if (ImGui.Checkbox("##enabled", ref enabled))
                {
                    rule.Enabled = enabled;
                    config.Save();
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(enabled ? "Enabled - click to pause this reaction" : "Paused - click to enable");
                ImGui.SameLine();
                ImGui.PushTextWrapPos(0);
                ImGui.TextUnformatted(ReactionLabel(rule));
                ImGui.PopTextWrapPos();
                IconGlyph.WrappedDisabled(ReactionSummary(rule) + LastFiredText(rule));

                if (ImGui.SmallButton(reactionDraft?.Id == rule.Id ? "Editing" : "Edit"))
                    StartReactionEdit(rule);
                ContinueRowOrWrap(ButtonWidth("Delete"));
                if (ImGui.SmallButton("Delete"))
                {
                    config.Reactions.Remove(rule);
                    config.Save();
                    if (reactionDraft?.Id == rule.Id)
                        reactionDraft = null;
                }
                ImGui.Separator();
                ImGui.PopID();
            }

            var newReaction = reactionDraft is null && ImGui.SmallButton("New reaction");
            TutorialService.Anchor(TutorialAnchors.ReactionNew);
            if (newReaction)
            {
                reactionDraft = new ReactionRule();
                reactionError = null;
            }
        }

        if (reactionDraft is { } draft)
            DrawReactionEditor(draft);
    }

    private void StartReactionEdit(ReactionRule rule)
    {
        // Edited as a copy, so Cancel leaves the saved reaction untouched.
        reactionDraft = JsonSerializer.Deserialize<ReactionRule>(JsonSerializer.Serialize(rule));
        reactionError = null;
    }

    private string LastFiredText(ReactionRule rule)
    {
        if (plugin.ReactionService.LastFired(rule.Id) is not { } ticks)
            return "";
        var ago = TimeSpan.FromMilliseconds(Environment.TickCount64 - ticks);
        return ago.TotalMinutes < 1 ? $" - fired {(int)ago.TotalSeconds}s ago" : ago.TotalHours < 1 ? $" - fired {(int)ago.TotalMinutes}m ago" : $" - fired {(int)ago.TotalHours}h ago";
    }

    private static string ReactionLabel(ReactionRule rule) =>
        rule.Name.Trim().Length > 0 ? rule.Name.Trim()
        : rule.TriggerKind == ReactionTriggerKind.Emote ? EmoteLabel(rule.EmoteId)
        : rule.ChatPhrase.Trim().Length > 0 ? $"\"{rule.ChatPhrase.Trim()}\"" : "(no phrase set)";

    private string ReactionSummary(ReactionRule rule)
    {
        var when = rule.TriggerKind == ReactionTriggerKind.Emote
            ? $"When {(rule.AllowAnyone ? "anyone" : "a paired player")} uses {EmoteLabel(rule.EmoteId)} on me" +
              (rule.Direction == ReactionDirection.Any ? "" : rule.Direction == ReactionDirection.Behind ? " from behind" : " from the front")
            : $"When {(rule.AllowAnyone ? "anyone" : "a paired player")} says \"{plugin.Configuration.TriggerPhrase.Trim()} {rule.ChatPhrase.Trim()}\"";

        var actions = new List<string>();
        if (rule.Gesture is { } g) actions.Add($"play {g.DisplayName}{(rule.KeepFacing ? " (keep facing)" : "")}");
        if (rule.ItemId is > 0) actions.Add($"wear {(rule.ItemLabel.Length > 0 ? rule.ItemLabel : "an item")}");
        if (!string.IsNullOrWhiteSpace(rule.ModDirectory)) actions.Add($"turn on {(rule.ModName.Length > 0 ? rule.ModName : rule.ModDirectory)}");
        if (rule.MoodleId is not null) actions.Add($"apply {MoodlesTextFormat.StripMarkup(rule.MoodleLabel)}");
        if (!string.IsNullOrWhiteSpace(rule.ChatMessage)) actions.Add($"say \"{rule.ChatMessage.Trim()}\"");
        return $"{when}: {(actions.Count == 0 ? "no action" : string.Join(", ", actions))} (cooldown {rule.EffectiveCooldownSeconds}s)";
    }

    private void DrawReactionEditor(ReactionRule draft)
    {
        var config = plugin.Configuration;
        var isNew = config.Reactions.All(r => r.Id != draft.Id);
        using var editor = Section.Begin("reactionEditor", isNew ? "New reaction" : "Edit reaction");

        var name = draft.Name;
        ImGui.SetNextItemWidth(260);
        if (ImGui.InputTextWithHint("Name (optional)##reaction", "Shown in the list", ref name, 64))
            draft.Name = name;

        Section.SubHeading("When");
        var kind = (int)draft.TriggerKind;
        ImGui.SetNextItemWidth(260);
        if (ImGui.Combo("Trigger##reaction", ref kind, ReactionTriggerNames, ReactionTriggerNames.Length))
            draft.TriggerKind = (ReactionTriggerKind)kind;

        if (draft.TriggerKind == ReactionTriggerKind.Emote)
        {
            if (DrawEmoteCombo("Emote##reactionTrigger", draft.EmoteId, ref reactionEmoteSearch, out var picked))
                draft.EmoteId = picked.Id;
            IconGlyph.HelpMarker("Fires when another player uses this emote while they have you targeted.");

            var direction = (int)draft.Direction;
            ImGui.SetNextItemWidth(260);
            if (ImGui.Combo("From##reactionDirection", ref direction, ReactionDirectionNames, ReactionDirectionNames.Length))
                draft.Direction = (ReactionDirection)direction;
            IconGlyph.HelpMarker("Where the other player has to be standing, compared to the way you're facing. In front and behind are each a 120-degree slice; the sides count as neither.");
        }
        else
        {
            var trigger = config.TriggerPhrase.Trim();
            if (trigger.Length == 0)
                IconGlyph.WrappedColored(Theme.Warning, "You don't have a trigger word yet - set one in Settings > Identity & Pairing first.");
            var phrase = draft.ChatPhrase;
            ImGui.SetNextItemWidth(260);
            if (ImGui.InputTextWithHint("Phrase##reaction", "e.g. kneel", ref phrase, 64))
                draft.ChatPhrase = phrase;
            IconGlyph.HelpMarker("Fires when someone's chat message starts with your trigger word followed by this phrase, in say, yell, shout, tells, party, alliance, free company, linkshells or cross-world linkshells.");
            if (trigger.Length > 0 && phrase.Trim().Length > 0)
                IconGlyph.WrappedDisabled($"Someone has to say: {trigger} {phrase.Trim()}");
        }

        Section.SubHeading("Who can trigger it");
        var anyone = draft.AllowAnyone ? 1 : 0;
        if (ImGui.RadioButton("Only people I'm paired with##reactionWho", ref anyone, 0))
            draft.AllowAnyone = false;
        ImGui.SameLine();
        if (ImGui.RadioButton("Anyone##reactionWho", ref anyone, 1))
            draft.AllowAnyone = true;
        IconGlyph.HelpMarker("Paired means anyone you own or who owns you. With Anyone, strangers can set this reaction off too.");

        Section.SubHeading("Then (pick at least one)");
        DrawReactionGesture(draft);
        DrawGatedReactionAction(DependencyId.Glamourer, () => DrawReactionItem(draft));
        DrawGatedReactionAction(DependencyId.Penumbra, () => DrawReactionMod(draft));
        DrawGatedReactionAction(DependencyId.Moodles, () => DrawReactionMoodle(draft));
        DrawReactionChat(draft);

        Section.SubHeading("Cooldown");
        var cooldown = draft.CooldownSeconds;
        ImGui.SetNextItemWidth(160);
        if (ImGui.SliderInt("seconds##reactionCooldown", ref cooldown, ReactionRule.MinimumCooldownSeconds, 300))
            draft.CooldownSeconds = cooldown;
        IconGlyph.HelpMarker("The reaction won't fire again until this much time has passed since it last did.");
        if (!string.IsNullOrWhiteSpace(draft.ChatMessage) && draft.CooldownSeconds < ReactionRule.MinimumChatCooldownSeconds)
            IconGlyph.WrappedDisabled($"Reactions that reply in chat wait at least {ReactionRule.MinimumChatCooldownSeconds}s.");

        ImGui.Spacing();
        if (reactionError is { } error)
            IconGlyph.WrappedColored(Theme.Warning, error);

        if (ImGui.SmallButton(isNew ? "Add reaction" : "Save reaction"))
        {
            reactionError = ValidateReaction(draft);
            if (reactionError is null)
            {
                var index = config.Reactions.FindIndex(r => r.Id == draft.Id);
                if (index >= 0) config.Reactions[index] = draft;
                else config.Reactions.Add(draft);
                config.Save();
                reactionDraft = null;
            }
        }
        ContinueRowOrWrap(ButtonWidth("Cancel"));
        if (ImGui.SmallButton("Cancel##reaction"))
        {
            reactionDraft = null;
            reactionError = null;
        }
        ContinueRowOrWrap(ButtonWidth("Test actions"));
        using (ImRaii.Disabled(!draft.HasAnyAction))
        {
            if (ImGui.SmallButton("Test actions##reaction"))
                plugin.ReactionService.Fire(draft);
        }
        IconGlyph.HelpMarker("Runs this reaction's actions once right now, on yourself, without waiting for the trigger. A chat message is really sent.");
    }

    /// Null when the draft can be saved.
    private string? ValidateReaction(ReactionRule draft)
    {
        if (draft.TriggerKind == ReactionTriggerKind.Emote && draft.EmoteId == 0)
            return "Choose the emote that triggers this reaction.";
        if (draft.TriggerKind == ReactionTriggerKind.ChatPhrase)
        {
            var phrase = draft.ChatPhrase.Trim();
            if (phrase.Length == 0)
                return "Type the phrase that triggers this reaction.";
            if (PhraseCollision(phrase) is { } word)
                return $"\"{word}\" is already one of your command words, so it would also run that command. Pick a different phrase.";
        }
        if (!draft.HasAnyAction)
            return "Pick at least one thing to happen.";
        return null;
    }

    /// Those words are read as commands by the same trigger word, so a reaction must never share one.
    private string? PhraseCollision(string phrase)
    {
        var config = plugin.Configuration;
        var words = new List<string>();
        words.AddRange(config.Aliases.Titles.Select(a => a.Alias));
        words.AddRange(config.Aliases.Outfits.Select(a => a.Alias));
        words.AddRange(config.Aliases.Gestures.Select(a => a.Alias));
        words.AddRange(config.Aliases.Moodles.Select(a => a.Alias));
        words.AddRange(config.Aliases.CustomTriggers.Select(a => a.Alias));
        words.AddRange(config.RestraintMapping.Devices.Values.Select(d => d.Name));
        words.AddRange(config.RestraintMapping.ConfiguredMods.Select(m => m.Alias));

        var first = phrase.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        foreach (var candidate in new[] { phrase, first })
        {
            if (IsReserved(candidate))
                return candidate;
            if (words.Any(w => w.Trim().Length > 0 && string.Equals(w.Trim(), candidate, StringComparison.OrdinalIgnoreCase)))
                return candidate;
        }
        return null;
    }

    /// Each plugin-backed action is gated on its own plugin; the rest stay usable.
    private void DrawGatedReactionAction(DependencyId required, Action draw)
    {
        var blocked = DependencyGates.FeatureBlockedReason(plugin, required);
        using (ImRaii.Disabled(blocked is not null))
            draw();
        if (blocked is not null)
            IconGlyph.WrappedColored(Theme.StatusMissing, blocked);
    }

    private void DrawReactionGesture(ReactionRule draft)
    {
        using var _ = ImRaii.PushId("reactionGesture");
        ImGui.TextUnformatted("Play a gesture");
        var current = draft.Gesture is { } g ? g.DisplayName : "None";
        ImGui.SetNextItemWidth(260);
        if (ImGui.BeginCombo("##gesture", current))
        {
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##search", "Filter emotes...", ref reactionGestureSearch, 32);
            if (ImGui.Selectable("None", draft.Gesture is null))
                draft.Gesture = null;
            foreach (var row in FilterEmotes(reactionGestureSearch))
            {
                var command = row.Command.TrimStart('/');
                if (ImGui.Selectable($"{row.Name} ({row.Command})##{row.Id}", draft.Gesture?.SlashCommand == command))
                    draft.Gesture = new GestureTrigger { Kind = GestureTriggerKind.SlashCommand, SlashCommand = command };
            }
            ImGui.EndCombo();
        }

        var catalog = plugin.Configuration.GestureMapping.LocalCatalog;
        if (catalog.Count > 0 && DependencyGates.FeatureBlockedReason(plugin, DependencyId.Penumbra) is null)
        {
            ContinueRowOrWrap(ButtonWidth("From my animations..."));
            if (ImGui.SmallButton("From my animations..."))
            {
                plugin.AnimationPickerWindow.Open(entry =>
                {
                    if (reactionDraft is not { } d || entry.Trigger is null) return;
                    // A modded animation needs its mod on to play, so picking one fills in both.
                    d.Gesture = entry.Trigger;
                    d.ModDirectory = entry.ModDirectory;
                    d.ModName = entry.ModName;
                    d.ModSelections = entry.GroupSelections.ToDictionary(x => x.Key, x => x.Value.ToList());
                });
            }
            IconGlyph.HelpMarker("Pick one of your scanned animation mods: this sets the gesture and also turns that mod on (with that animation's options) when the reaction fires.");
        }

        if (draft.Gesture is not null)
        {
            var keep = draft.KeepFacing;
            if (ImGui.Checkbox("Keep my current facing", ref keep))
                draft.KeepFacing = keep;
            IconGlyph.HelpMarker("Normally a gesture turns you toward whoever you have targeted. This briefly clears your target so you stay facing the way you are, then targets them again.");
        }
    }

    private void DrawReactionItem(ReactionRule draft)
    {
        using var _ = ImRaii.PushId("reactionItem");
        ImGui.TextUnformatted("Put on an item");
        ImGui.SameLine();
        ImGui.TextDisabled(draft.ItemId is > 0 ? $"{draft.ItemLabel} ({draft.ItemSlot})" : "None");
        if (ImGui.SmallButton("Choose item..."))
        {
            plugin.ItemPickerWindow.Open((itemId, itemName) =>
            {
                if (reactionDraft is not { } d || GlamourerIpc.GetItemSlot(itemId) is not { } slot) return;
                d.ItemId = itemId;
                d.ItemSlot = slot;
                d.ItemLabel = itemName;
            });
        }
        if (draft.ItemId is > 0)
        {
            ContinueRowOrWrap(ButtonWidth("Clear"));
            if (ImGui.SmallButton("Clear"))
            {
                draft.ItemId = null;
                draft.ItemSlot = null;
                draft.ItemLabel = "";
            }
        }
        IconGlyph.HelpMarker("Put on through Glamourer and simply left on - not locked. Change it whenever you like. If an outfit, restraint or collar is holding that slot, the item is skipped.");
    }

    private void DrawReactionMod(ReactionRule draft)
    {
        using var _ = ImRaii.PushId("reactionMod");
        ImGui.TextUnformatted("Turn on a mod");
        var current = string.IsNullOrWhiteSpace(draft.ModDirectory) ? "None" : draft.ModName.Length > 0 ? draft.ModName : draft.ModDirectory!;
        ImGui.SetNextItemWidth(260);
        if (ImGui.BeginCombo("##mod", current))
        {
            reactionModList ??= plugin.PenumbraIpc.TryGetModList() ?? new Dictionary<string, string>();
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##search", "Filter mods...", ref reactionModSearch, 64);
            if (ImGui.Selectable("None", string.IsNullOrWhiteSpace(draft.ModDirectory)))
            {
                draft.ModDirectory = null;
                draft.ModName = "";
                draft.ModSelections = new();
            }
            if (reactionModList.Count == 0)
                ImGui.TextDisabled("Penumbra isn't available.");
            var search = reactionModSearch.Trim();
            foreach (var (directory, modName) in reactionModList
                         .Where(m => search.Length == 0 || m.Value.Contains(search, StringComparison.OrdinalIgnoreCase) || m.Key.Contains(search, StringComparison.OrdinalIgnoreCase))
                         .OrderBy(m => m.Value, StringComparer.OrdinalIgnoreCase).Take(200))
            {
                if (ImGui.Selectable($"{modName}##{directory}", draft.ModDirectory == directory))
                {
                    draft.ModDirectory = directory;
                    draft.ModName = modName;
                    draft.ModSelections = CurrentModOptions(directory);
                }
            }
            ImGui.EndCombo();
        }
        else
            reactionModList = null; // re-read the mod list next time the combo opens

        if (!string.IsNullOrWhiteSpace(draft.ModDirectory))
        {
            ContinueRowOrWrap(ButtonWidth("Capture current options"));
            if (ImGui.SmallButton("Capture current options"))
                draft.ModSelections = CurrentModOptions(draft.ModDirectory!);
            var chosen = draft.ModSelections.Where(g => g.Value.Count > 0).Select(g => $"{g.Key}: {string.Join(", ", g.Value)}").ToList();
            IconGlyph.WrappedDisabled(chosen.Count == 0 ? "Options: the mod's defaults." : $"Options: {string.Join("; ", chosen)}");
        }
        IconGlyph.HelpMarker("Turned on until you turn it off again from the \"Mods turned on by reactions\" list above, or panic. Set the mod's options in Penumbra the way you want them, then click Capture current options.");
    }

    private Dictionary<string, List<string>> CurrentModOptions(string directory)
    {
        if (plugin.PenumbraIpc.TryGetLocalPlayerCollectionId() is not { } collection)
            return new();
        var (_, selections) = plugin.PenumbraIpc.TryGetCurrentSettings(collection, directory);
        return selections?.ToDictionary(x => x.Key, x => x.Value.ToList()) ?? new();
    }

    private void DrawReactionMoodle(ReactionRule draft)
    {
        using var _ = ImRaii.PushId("reactionMoodle");
        ImGui.TextUnformatted("Apply a moodle");
        var current = draft.MoodleId is null ? "None" : MoodlesTextFormat.StripMarkup(draft.MoodleLabel);
        ImGui.SetNextItemWidth(260);
        if (ImGui.BeginCombo("##moodle", current))
        {
            reactionMoodles ??= plugin.MoodlesIpc.GetOwnStatuses() is { Status: MoodlesScanStatus.Success } result ? result.Statuses : [];
            if (ImGui.Selectable("None", draft.MoodleId is null))
            {
                draft.MoodleId = null;
                draft.MoodleLabel = "";
            }
            if (reactionMoodles.Count == 0)
                ImGui.TextDisabled("Moodles isn't available, or you have no statuses.");
            foreach (var status in reactionMoodles.OrderBy(s => MoodlesTextFormat.StripMarkup(s.Name), StringComparer.OrdinalIgnoreCase))
            {
                if (ImGui.Selectable($"{MoodlesTextFormat.StripMarkup(status.Name)}##{status.Id}", draft.MoodleId == status.Id))
                {
                    draft.MoodleId = status.Id;
                    draft.MoodleLabel = status.Name;
                }
            }
            ImGui.EndCombo();
        }
        else
            reactionMoodles = null;
        IconGlyph.HelpMarker("One of your own Moodles statuses. It lasts as long as you set it to in Moodles.");
    }

    private static void DrawReactionChat(ReactionRule draft)
    {
        using var _ = ImRaii.PushId("reactionChat");
        ImGui.TextUnformatted("Reply in chat");
        var text = draft.ChatMessage;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputTextWithHint("##chat", "e.g. /s Thank you! or /em blushes", ref text, 400))
            draft.ChatMessage = text;
        IconGlyph.HelpMarker("Sent automatically, exactly as if you typed it - start with /s, /p, /em and so on to pick the channel. This is an automatic chat reply, the kind of automation game rules frown on most; see the README.");
    }

    private static IEnumerable<(uint Id, string Name, string Command)> FilterEmotes(string search)
    {
        var s = search.Trim();
        return EmoteRows.Where(e => s.Length == 0 || e.Name.Contains(s, StringComparison.OrdinalIgnoreCase) || e.Command.Contains(s, StringComparison.OrdinalIgnoreCase));
    }

    private static bool DrawEmoteCombo(string label, uint selected, ref string search, out (uint Id, string Name, string Command) picked)
    {
        picked = default;
        var changed = false;
        ImGui.SetNextItemWidth(260);
        if (ImGui.BeginCombo(label, selected == 0 ? "Choose an emote..." : EmoteLabel(selected)))
        {
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##search", "Filter emotes...", ref search, 32);
            foreach (var row in FilterEmotes(search))
            {
                if (ImGui.Selectable($"{row.Name} ({row.Command})##{row.Id}", row.Id == selected))
                {
                    picked = row;
                    changed = true;
                }
            }
            ImGui.EndCombo();
        }
        return changed;
    }
}
