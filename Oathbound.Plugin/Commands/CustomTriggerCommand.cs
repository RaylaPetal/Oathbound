using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;
using Oathbound.Plugin.Config;
using Oathbound.Plugin.Safety;
using Oathbound.Plugin.UI;
using ECommons.Automation;

namespace Oathbound.Plugin.Commands;

/// collar/custom-triggers: applies a Custom Trigger's bundled actions in sequence, dispatching each to its
/// own category's existing apply method and checking that category's own permission (and, for Restraints/
/// Gesture, the existing ToS acknowledgement; for Chat, the new dedicated permission/acknowledgement pair)
/// immediately before calling it - never bypassing a check that action would already require if triggered
/// on its own (design.md's "orchestrator, not a reimplementation" decision). An action whose permission
/// isn't met is skipped, not treated as an error - the rest of the bundle's permitted actions still apply.
public sealed class CustomTriggerCommand
{
    private readonly PluginConfig config;
    private readonly TitleCommand title;
    private readonly OutfitCommand outfit;
    private readonly GestureCommand gesture;
    private readonly MoodlesCommand moodles;
    private readonly RestraintCommand restraints;

    public CustomTriggerCommand(PluginConfig config, TitleCommand title, OutfitCommand outfit, GestureCommand gesture, MoodlesCommand moodles, RestraintCommand restraints)
    {
        this.config = config;
        this.title = title;
        this.outfit = outfit;
        this.gesture = gesture;
        this.moodles = moodles;
        this.restraints = restraints;
    }

    /// `restraintLock` (collar/restraint-lock-timer) only reaches the restraint action's Force* call - every
    /// other action kind ignores it.
    public LocalTestResult Apply(List<CustomTriggerAction> actions, RestraintLock restraintLock = default)
    {
        var applied = new List<string>();
        var skipped = new List<string>();
        var effects = config.CustomTriggerEffects;

        foreach (var action in actions)
        {
            switch (action.Kind)
            {
                case CustomTriggerActionKind.Title:
                    if (!config.Permissions.Title) { skipped.Add("title (permission)"); break; }
                    title.Apply(new TitleAliasDefinition { Text = action.TitleText, IsPrefix = action.TitleIsPrefix, Color = action.TitleColor, Glow = action.TitleGlow });
                    effects.Title = true;
                    applied.Add("title");
                    break;

                case CustomTriggerActionKind.Outfit:
                    if (!config.Permissions.Outfit) { skipped.Add("outfit (permission)"); break; }
                    // A Sub-defined trigger (via ResolveAlias) always carries a real DesignId captured
                    // from this same client's own WardrobeMapping, so it goes through Apply exactly like a
                    // plain outfit alias. An Owner-authored ad-hoc `customtrigger cast` bundle only ever
                    // has the design name (the Owner has no access to the Sub's WardrobeMapping ids), so it
                    // falls back to the same name-based ForceApply an Owner's plain `outfit lock <name>`
                    // override already uses - this is the fix for that gap, not the original Group 4 design.
                    var (outfitOk, _) = action.OutfitDesignId != Guid.Empty
                        ? outfit.Apply(new OutfitAliasDefinition { DesignId = action.OutfitDesignId, DesignName = action.OutfitDesignName, Locked = true })
                        : outfit.ForceApply(action.OutfitDesignName);
                    if (outfitOk)
                    {
                        effects.Outfit = true;
                        applied.Add("outfit");
                    }
                    else
                        skipped.Add("outfit (force-locked, not found, or apply failed)");
                    break;

                case CustomTriggerActionKind.Gesture:
                    if (!(config.Permissions.Gesture && config.TosAcknowledged)) { skipped.Add("gesture (permission/acknowledgement)"); break; }
                    // Same real-id-vs-name-only split as Outfit/Restraint above: `GestureAliasDefinition`'s
                    // own name-fallback additionally requires a ModDirectory match this action doesn't
                    // carry, so a no-id Owner ad-hoc action goes through the plain name/label match
                    // `ForceApply` already uses for a `gesture <name>` override instead.
                    var gestureOk = action.GestureId.Length > 0
                        ? gesture.Apply(new GestureAliasDefinition { GestureId = action.GestureId, AnimationName = action.GestureAnimationName })
                        : gesture.ForceApply(action.GestureAnimationName);
                    if (gestureOk)
                    {
                        effects.Gesture = true;
                        applied.Add("gesture");
                    }
                    else
                        skipped.Add("gesture (not found or failed to play)");
                    break;

                case CustomTriggerActionKind.Moodle:
                    if (!config.Permissions.Moodles) { skipped.Add("moodle (permission)"); break; }
                    if (moodles.Apply(new MoodlesAliasDefinition { StatusId = action.MoodleStatusId, StatusName = action.MoodleStatusName }))
                    {
                        // Held under the same per-status "manual" ledger source Apply itself uses, so revert can
                        // release exactly this status and nothing else.
                        if ((Guid.TryParse(action.MoodleStatusId, out var statusId) || moodles.TryResolveStatusId(action.MoodleStatusName, out statusId))
                            && !effects.MoodleStatusIds.Contains(statusId))
                            effects.MoodleStatusIds.Add(statusId);
                        applied.Add("moodle");
                    }
                    else
                        skipped.Add("moodle (not found or failed to apply)");
                    break;

                case CustomTriggerActionKind.Restraint:
                    if (!(config.Permissions.Restraints && config.TosAcknowledged)) { skipped.Add("restraint (permission/acknowledgement)"); break; }
                    // Which devices this action added, whatever path it takes - revert releases exactly these.
                    var activeBefore = restraints.ActiveDeviceIds.ToHashSet();
                    // Both branches are apply-only because the bundle itself arrived as an Owner command.
                    // In particular, do not route stable IDs through the Sub self-service Toggle method:
                    // Toggle is rejected by an Owner force-lock and made multi-restraint bundles depend on
                    // unrelated prior runtime state.
                    // A self-contained action (a shared copy) carries its own rules - applied as-is, with no
                    // lookup of the Sub's restraints, so it still works after the original was deleted.
                    var restraintOk = action.RestraintRules is { } inlineRules
                        ? action.RestraintRulesOnly
                            ? restraints.ForceApplyAdHoc(action.RestraintSlot, action.RestraintItemId == 0 ? null : action.RestraintItemId, action.RestraintDeviceName, inlineRules, restraintLock: restraintLock)
                            : restraints.ForceApplyCatalog(action.RestraintCatalogId, action.RestraintItemId, inlineRules, restraintLock: restraintLock)
                        : action.RestraintCatalogId.Length > 0
                        ? restraints.ForceApplyCatalog(action.RestraintCatalogId, action.RestraintItemId,
                            config.RestraintMapping.ConfiguredMods.FirstOrDefault(x => x.CatalogId == action.RestraintCatalogId)?.Rules ?? [], restraintLock: restraintLock)
                        : config.RestraintMapping.Devices.ContainsKey(action.RestraintDeviceId)
                            ? restraints.ForceApplyById(action.RestraintDeviceId, restraintLock)
                            : restraints.ForceApply(action.RestraintDeviceName, restraintLock: restraintLock);
                    if (restraintOk)
                    {
                        foreach (var id in restraints.ActiveDeviceIds.Where(id => !activeBefore.Contains(id) && !effects.RestraintDeviceIds.Contains(id)))
                            effects.RestraintDeviceIds.Add(id);
                        applied.Add($"restraint \"{action.RestraintDeviceName}\"");
                    }
                    else
                        skipped.Add($"restraint \"{action.RestraintDeviceName}\" ({restraints.LastFailureReason ?? "apply failed"})");
                    break;

                case CustomTriggerActionKind.Chat:
                    if (!(config.Permissions.CustomChatMessages && config.CustomChatAcknowledged)) { skipped.Add("chat (permission/acknowledgement)"); break; }
                    if (action.ChatText.Trim().Length > 0)
                    {
                        Chat.SendMessage(action.ChatText);
                        applied.Add("chat");
                    }
                    else
                    {
                        skipped.Add("chat (no text configured)");
                    }
                    break;
            }
        }

        config.Save();
        var skippedSuffix = skipped.Count > 0 ? $" (skipped: {string.Join(", ", skipped)})" : "";
        return applied.Count > 0
            ? LocalTestResult.Ok($"Applied: {string.Join(", ", applied)}{skippedSuffix}")
            : LocalTestResult.Fail($"Nothing applied{skippedSuffix}");
    }

    /// collar/custom-triggers "Revert custom triggers" (`customtrigger revert`): undoes what Custom Triggers
    /// applied since the last revert, and nothing else - the title and outfit they set, a playing animation,
    /// the moodles they added, and the restraint devices they put on. Leash, toy, collar, and anything applied
    /// by its own command are untouched (that's "revert all"). A sent chat message can't be undone. Each step
    /// is isolated so one failing IPC never stops the rest.
    public LocalTestResult RevertEffects()
    {
        var effects = config.CustomTriggerEffects;
        if (!effects.Any)
            return LocalTestResult.Fail("Nothing to revert - no Custom Trigger effects are active.");

        var done = new List<string>();
        var failed = new List<string>();
        void Step(string name, bool applies, Action action)
        {
            if (!applies) return;
            try { action(); done.Add(name); }
            catch (Exception ex)
            {
                Plugin.Log.Warning(ex, $"customtrigger revert: {name} step failed - continuing.");
                failed.Add(name);
            }
        }

        // Restraints first, like revert all: releasing their slot lock can hand a slot back to the outfit.
        Step("restraints", effects.RestraintDeviceIds.Count > 0, () => restraints.ReleaseDevices(effects.RestraintDeviceIds));
        Step("outfit", effects.Outfit, () => outfit.RevertToBase());
        Step("title", effects.Title, title.ForceClear);
        Step("animation", effects.Gesture, gesture.ResetActiveTemporary);
        Step("moodles", effects.MoodleStatusIds.Count > 0, () =>
        {
            foreach (var statusId in effects.MoodleStatusIds)
                moodles.Ledger.Release(AttachedMoodleLedger.ManualSource(statusId));
        });

        ForgetEffects();
        var summary = $"Reverted Custom Trigger effects: {string.Join(", ", done)}.";
        return failed.Count == 0 ? LocalTestResult.Ok(summary) : LocalTestResult.Fail($"{summary} Failed: {string.Join(", ", failed)}.");
    }

    /// Called when those effects are already gone another way (revert all, panic), so a later revert never
    /// undoes something applied afterwards by a plain command.
    public void ForgetEffects()
    {
        config.CustomTriggerEffects = new CustomTriggerEffectsState();
        config.Save();
    }

    /// design.md "customtrigger cast wire shape": mirrors `RestraintCommand.BuildWearCommand`'s quoted-label
    /// + token-list structure. Each non-chat action is one `kind=value` segment joined by ';'; any free-text
    /// field (title text, and every category's own name) is base64-encoded so it can't collide with the '|'/
    /// ';'/'=' delimiters - ids (guids, gesture/moodle/restraint ids) are left raw since they're plugin-
    /// generated and never contain those characters. The chat action, if present, is always last and its raw
    /// text consumes the remainder of the line (deliberately not delimited - see design.md's "pragmatic, not
    /// fully general" note). At most one action per kind is supported in this ad-hoc wire encoding (the
    /// Sub-alias path has no such limit, since it stores the action list directly rather than encoding it).
    public static string BuildCastCommand(string label, List<CustomTriggerAction> actions)
    {
        var segments = new List<string>();
        string? chatSegment = null;

        foreach (var action in actions)
        {
            switch (action.Kind)
            {
                case CustomTriggerActionKind.Title:
                    // design.md "customtrigger cast bundle's title= segment": glow is an optional trailing
                    // 4th part, appended only when actually set - an action with no glow still encodes as
                    // exactly 3 parts, so an old Sub's strict `parts.Length != 3` check still accepts it;
                    // only a glow-styled Title action requires both sides to be on this version or newer.
                    var titleSegment = $"title={EncodeText(action.TitleText)}|{(action.TitleIsPrefix ? 1 : 0)}|{FormatColor(action.TitleColor)}";
                    if (action.TitleGlow is { } glow)
                        titleSegment += $"|{FormatColor(glow)}";
                    segments.Add(titleSegment);
                    break;
                case CustomTriggerActionKind.Outfit:
                    segments.Add($"outfit={action.OutfitDesignId}|{EncodeText(action.OutfitDesignName)}");
                    break;
                case CustomTriggerActionKind.Gesture:
                    segments.Add($"gesture={action.GestureId}|{EncodeText(action.GestureAnimationName)}");
                    break;
                case CustomTriggerActionKind.Moodle:
                    segments.Add($"moodle={action.MoodleStatusId}|{EncodeText(action.MoodleStatusName)}");
                    break;
                case CustomTriggerActionKind.Restraint:
                    if (action.RestraintRules is { } rules)
                    {
                        // Self-contained: a third part carries the rules, and a rules-only restraint uses a
                        // `wear:` reference instead of a device id - nothing on the Sub's side is looked up.
                        // An older Sub's strict 2-part check rejects this whole bundle (fails closed).
                        var reference = action.RestraintRulesOnly
                            ? $"wear:{action.RestraintSlot?.ToString() ?? "-"}:{(action.RestraintItemId == 0 ? "-" : action.RestraintItemId.ToString())}"
                            : $"catalog:{action.RestraintCatalogId}:{action.RestraintItemId}";
                        segments.Add($"restraint={reference}|{EncodeText(action.RestraintDeviceName)}|{EncodeText(RestraintCommand.EncodeRuleTokens(rules))}");
                        break;
                    }
                    segments.Add(action.RestraintCatalogId.Length > 0
                        ? $"restraint=catalog:{action.RestraintCatalogId}:{action.RestraintItemId}|{EncodeText(action.RestraintDeviceName)}"
                        : $"restraint={action.RestraintDeviceId}|{EncodeText(action.RestraintDeviceName)}");
                    break;
                case CustomTriggerActionKind.Chat:
                    chatSegment = $"chat={action.ChatText}";
                    break;
            }
        }

        if (chatSegment is not null)
            segments.Add(chatSegment);

        return $"customtrigger cast \"{label}\" {string.Join(';', segments)}";
    }

    /// Parses the remainder of a `customtrigger cast ...` command (after the "cast " prefix) into a label
    /// and action list. Fails closed (returns false) on any malformed segment, unknown kind, or a bundle
    /// that ends up with zero actions - an empty ad-hoc trigger is meaningless, same rationale as
    /// `RestraintCommand.TryParseWearCommand` requiring at least one rule.
    public static bool TryParseCastCommand(string remainder, out string label, out List<CustomTriggerAction> actions)
    {
        label = "";
        actions = new List<CustomTriggerAction>();

        var trimmed = remainder.Trim();
        if (!trimmed.StartsWith('"'))
            return false;

        var closing = trimmed.IndexOf('"', 1);
        if (closing < 0)
            return false;

        label = trimmed[1..closing];
        if (label.Length == 0)
            return false;

        var tail = trimmed[(closing + 1)..].Trim();
        if (tail.Length == 0)
            return false;

        SplitChatTail(tail, out var beforeChat, out var chatText);

        if (beforeChat.Length > 0)
        {
            foreach (var segment in beforeChat.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = segment.IndexOf('=');
                if (eq < 0)
                    return false;

                var kind = segment[..eq];
                var value = segment[(eq + 1)..];
                var parts = value.Split('|');

                switch (kind.ToLowerInvariant())
                {
                    case "title":
                        // design.md: parts.Length 3 is the legacy (no glow) shape; 4 carries an optional
                        // glow as its last part, empty when the encoder had no glow to send.
                        if ((parts.Length != 3 && parts.Length != 4) || !TryDecodeText(parts[0], out var titleText) || titleText.Length == 0)
                            return false;
                        if (!int.TryParse(parts[1], out var prefixFlag))
                            return false;
                        if (!TryParseColor(parts[2], out var color))
                            return false;
                        Vector3? glow = null;
                        if (parts.Length == 4 && parts[3].Length > 0)
                        {
                            if (!TryParseColor(parts[3], out var g))
                                return false;
                            glow = g;
                        }
                        actions.Add(new CustomTriggerAction { Kind = CustomTriggerActionKind.Title, TitleText = titleText, TitleIsPrefix = prefixFlag != 0, TitleColor = color, TitleGlow = glow });
                        break;

                    case "outfit":
                        if (parts.Length != 2 || !Guid.TryParse(parts[0], out var designId) || !TryDecodeText(parts[1], out var designName) || designName.Length == 0)
                            return false;
                        actions.Add(new CustomTriggerAction { Kind = CustomTriggerActionKind.Outfit, OutfitDesignId = designId, OutfitDesignName = designName });
                        break;

                    // Gesture/moodle/restraint ids may be empty: the Owner's bundle editor only knows names
                    // (it has no access to the Sub's catalogs), and Apply already falls back to a name
                    // match when the id is empty.
                    case "gesture":
                        if (parts.Length != 2 || !TryDecodeText(parts[1], out var animName) || animName.Length == 0)
                            return false;
                        actions.Add(new CustomTriggerAction { Kind = CustomTriggerActionKind.Gesture, GestureId = parts[0], GestureAnimationName = animName });
                        break;

                    case "moodle":
                        if (parts.Length != 2 || !TryDecodeText(parts[1], out var statusName) || statusName.Length == 0)
                            return false;
                        actions.Add(new CustomTriggerAction { Kind = CustomTriggerActionKind.Moodle, MoodleStatusId = parts[0], MoodleStatusName = statusName });
                        break;

                    case "restraint":
                        if ((parts.Length != 2 && parts.Length != 3) || !TryDecodeText(parts[1], out var deviceName) || deviceName.Length == 0)
                            return false;
                        if (parts.Length == 3)
                        {
                            if (parts[0].Length == 0)
                                return false;
                            // Self-contained restraint (see BuildCastCommand): its rules travel inline.
                            if (!TryDecodeText(parts[2], out var ruleTokens))
                                return false;
                            var inlineRules = RestraintCommand.DecodeRuleTokens(ruleTokens);
                            if (inlineRules.Count == 0)
                                return false;
                            var reference = parts[0].Split(':');
                            if (reference.Length != 3)
                                return false;
                            if (reference[0].Equals("catalog", StringComparison.OrdinalIgnoreCase))
                            {
                                if (!ulong.TryParse(reference[2], out var inlineItemId) || inlineItemId == 0) return false;
                                actions.Add(new CustomTriggerAction { Kind = CustomTriggerActionKind.Restraint, RestraintCatalogId = reference[1], RestraintItemId = inlineItemId, RestraintDeviceName = deviceName, RestraintRules = inlineRules });
                            }
                            else if (reference[0].Equals("wear", StringComparison.OrdinalIgnoreCase))
                            {
                                Glamourer.Api.Enums.ApiEquipSlot? wearSlot = null;
                                if (reference[1] != "-")
                                {
                                    if (!Enum.TryParse<Glamourer.Api.Enums.ApiEquipSlot>(reference[1], out var parsedSlot)) return false;
                                    wearSlot = parsedSlot;
                                }
                                ulong wearItem = 0;
                                if (reference[2] != "-" && !ulong.TryParse(reference[2], out wearItem)) return false;
                                actions.Add(new CustomTriggerAction { Kind = CustomTriggerActionKind.Restraint, RestraintDeviceName = deviceName, RestraintRules = inlineRules, RestraintRulesOnly = true, RestraintSlot = wearSlot, RestraintItemId = wearItem });
                            }
                            else
                            {
                                return false;
                            }
                            break;
                        }
                        if (parts[0].StartsWith("catalog:", StringComparison.OrdinalIgnoreCase))
                        {
                            var catalogParts = parts[0].Split(':');
                            if (catalogParts.Length != 3 || !ulong.TryParse(catalogParts[2], out var itemId) || itemId == 0) return false;
                            actions.Add(new CustomTriggerAction { Kind = CustomTriggerActionKind.Restraint, RestraintCatalogId = catalogParts[1], RestraintItemId = itemId, RestraintDeviceName = deviceName });
                        }
                        else
                            actions.Add(new CustomTriggerAction { Kind = CustomTriggerActionKind.Restraint, RestraintDeviceId = parts[0], RestraintDeviceName = deviceName });
                        break;

                    default:
                        return false;
                }
            }
        }

        if (chatText is { Length: > 0 })
            actions.Add(new CustomTriggerAction { Kind = CustomTriggerActionKind.Chat, ChatText = chatText });

        return actions.Count > 0;
    }

    private const string CastPrefix = "customtrigger cast ";

    /// Splits a full `customtrigger cast` command into one single-action `cast` command per action, each
    /// carrying the same label and its original segment text unchanged - used when the whole bundle won't
    /// fit in one chat message (see ChatComposer.ComposeAll). Every Sub version that understands the bundle
    /// also understands a one-action bundle, and the Sub applies actions one at a time anyway, so sending
    /// them as separate tells changes nothing on the receiving side. Null when `command` isn't a valid cast
    /// command.
    public static List<string>? SplitCastCommand(string command)
    {
        const string categoryWord = "customtrigger ";
        const string castWord = "cast ";
        var trimmed = command.Trim();
        if (!trimmed.StartsWith(categoryWord, StringComparison.OrdinalIgnoreCase))
            return null;

        // collar/restraint-lock-timer: a timed bundle reads `customtrigger lockfor:N cast ...` - every part
        // carries the same option, so whichever part holds the restraint still locks with the timer.
        var afterCategory = LockTimerOption.Strip(trimmed[categoryWord.Length..], out var restraintLock).TrimStart();
        if (!afterCategory.StartsWith(castWord, StringComparison.OrdinalIgnoreCase))
            return null;

        var remainder = afterCategory[castWord.Length..].Trim();
        if (!TryParseCastCommand(remainder, out var label, out _))
            return null;

        SplitChatTail(remainder[(remainder.IndexOf('"', 1) + 1)..].Trim(), out var beforeChat, out var chatText);
        var head = $"{CastPrefix}\"{label}\" ";
        var parts = beforeChat.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(segment => head + segment).ToList();
        if (chatText is { Length: > 0 })
            parts.Add($"{head}chat={chatText}");
        return parts.Select(part => LockTimerOption.Insert(part, restraintLock)).ToList();
    }

    /// The chat action, if present, is always last and consumes the rest of the line (see BuildCastCommand).
    private static void SplitChatTail(string tail, out string beforeChat, out string? chatText)
    {
        chatText = null;
        if (tail.StartsWith("chat=", StringComparison.OrdinalIgnoreCase))
        {
            beforeChat = "";
            chatText = tail["chat=".Length..];
            return;
        }

        var chatMarker = tail.IndexOf(";chat=", StringComparison.OrdinalIgnoreCase);
        if (chatMarker >= 0)
        {
            beforeChat = tail[..chatMarker];
            chatText = tail[(chatMarker + ";chat=".Length)..];
        }
        else
        {
            beforeChat = tail;
        }
    }

    /// One-line human-readable summary of a single bundled action - shared by the Sub-side UI's own draft
    /// list (`CollarWindow.SummarizeCustomTriggerAction`) and `CatalogSyncService`'s Aliases export
    /// description, so both places describe a Custom Trigger's contents identically rather than each
    /// re-deriving their own text.
    public static string Summarize(CustomTriggerAction a) => CommandPresentation.Action(a);

    private static string EncodeText(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    private static bool TryDecodeText(string encoded, out string text)
    {
        text = "";
        try
        {
            text = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string FormatColor(Vector3 color) =>
        $"{color.X.ToString(CultureInfo.InvariantCulture)},{color.Y.ToString(CultureInfo.InvariantCulture)},{color.Z.ToString(CultureInfo.InvariantCulture)}";

    private static bool TryParseColor(string token, out Vector3 color)
    {
        color = new Vector3(1, 1, 1);
        var parts = token.Split(',');
        if (parts.Length != 3
            || !float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var r)
            || !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var g)
            || !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var b))
            return false;

        color = new Vector3(r, g, b);
        return true;
    }
}
