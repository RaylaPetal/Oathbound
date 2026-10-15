using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Oathbound.Plugin.Commands;
using Oathbound.Plugin.Config;
using Oathbound.Plugin.Ipc;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Glamourer.Api.Enums;
using static Oathbound.Plugin.UI.Layout;

namespace Oathbound.Plugin.UI;

public sealed partial class ModuleWindow
{
    private int toyVibrateIntensity = 50;
    private bool toyVibrateHasDuration;
    private bool toyVibrateIsPermanent;
    private int toyVibrateDurationSeconds = 10;
    private string toyIntifaceAddress = "";

    /// Non-null while editing an existing pattern in place.
    private string toyPatternNameInput = "";
    private bool toyPatternLoopInput;

    private readonly List<PatternStep> toyPatternStepsInput = new();
    private string? toyPatternEditingId;
    private string? toyPatternError;

    private ToyTriggerKind toyTriggerKindInput = ToyTriggerKind.HealthPercent;
    private int toyTriggerHealthThresholdInput = 50;
    private RestraintRuleKind toyTriggerRestrictionKindInput = RestraintRuleKind.Gagged;

    private readonly HashSet<uint> toyTriggerSpellJobIdsInput = new();

    private readonly HashSet<uint> toyTriggerSpellActionIdsInput = new();
    private string toyTriggerSpellJobSearch = "";
    private string toyTriggerSpellActionSearch = "";
    private bool toyTriggerUsePatternInput;
    private int toyTriggerIntensityInput = 50;
    private bool toyTriggerHasDurationInput;
    private int toyTriggerDurationSecondsInput = 10;
    private string toyTriggerPatternNameInput = "";
    private int toyTriggerCooldownInput = 5;
    private string? editingToyTriggerId;

    private readonly HashSet<uint> toyTriggerEmoteIdsInput = new();
    private string toyTriggerEmoteSearch = "";

    private readonly List<string> toyTriggerSourcePlayersInput = new();
    private string toyTriggerSourcePlayerInput = "";

    private void DrawToyControlModule()
    {
        IconGlyph.Text(FontAwesomeIcon.Plug, "Toy Control (Intiface)");
        ImGui.Separator();
        var config = plugin.Configuration;
        if (!config.ToyControlAcknowledged)
            IconGlyph.WrappedColored(Theme.Warning, "Needs the Toy Control acknowledgement and permission in Settings first.");

        if (toyIntifaceAddress.Length == 0)
            toyIntifaceAddress = config.IntifaceAddress;

        var connectionBox = Section.Begin("toyConnection", "Connection");
        var intiface = plugin.IntifaceIpc;
        ItemWidth(260);
        using (ImRaii.Disabled(intiface.IsConnected || intiface.IsConnecting))
            ImGui.InputText("Intiface address##toyControl", ref toyIntifaceAddress, 128);
        IconGlyph.HelpMarker("Intiface Central's address. Only change it if you changed it in Intiface.");

        if (intiface.IsConnected)
        {
            ContinueRowOrWrap(ButtonWidth("Disconnect"));
            if (ImGui.Button("Disconnect##toyControl"))
                intiface.Disconnect();
        }
        else
        {
            var connectLabel = intiface.IsConnecting ? "Connecting..." : "Connect";
            ContinueRowOrWrap(ButtonWidth(connectLabel));
            using (ImRaii.Disabled(intiface.IsConnecting || toyIntifaceAddress.Trim().Length == 0))
            if (ImGui.Button($"{connectLabel}##toyControl"))
            {
                config.IntifaceAddress = toyIntifaceAddress.Trim();
                config.Save();
                intiface.Connect(config.IntifaceAddress);
            }
        }

        var status = intiface.IsConnected ? $"Connected - {intiface.ConnectedDeviceCount} device(s)" : intiface.IsConnecting ? "Connecting..." : "Not connected";
        IconGlyph.WrappedDisabled(status);
        if (intiface.LastError is { Length: > 0 } error)
            IconGlyph.WrappedColored(Theme.Warning, error);

        ImGui.Spacing();
        using (ImRaii.Disabled(!intiface.IsConnected || plugin.CageService.IsCaged))
        if (ImGui.Button("Stop now##toyControlLocal"))
            plugin.ToyControlCommand.ForceStop();
        IconGlyph.HelpMarker(plugin.CageService.IsCaged ? "You can't stop it yourself while you're caged - panic still stops everything." : "Stops every device right away.");
        connectionBox.Dispose();

        using (Section.Begin("toyStatus", "Status"))
            ToyStatusView.DrawSub(plugin.ToyControlCommand, intiface.IsConnected);

        var limitsBox = Section.Begin("toyLimits", "Limits");
        var defaultMaxDuration = config.DefaultMaxDurationSeconds;
        ItemWidth(160);
        if (ImGui.SliderInt("Default max duration (s)##toyControlDefaultMax", ref defaultMaxDuration, 1, ToyControlCommand.MaxDurationSeconds))
        {
            config.DefaultMaxDurationSeconds = defaultMaxDuration;
            config.Save();
        }
        IconGlyph.HelpMarker($"How long a command runs if it has no duration. Never more than {ToyControlCommand.MaxDurationSeconds}s.");

        var permanentBackstop = config.PermanentBackstopSeconds;
        ItemWidth(160);
        if (ImGui.InputInt("Permanent-mode backstop (s)##toyControlPermanentBackstop", ref permanentBackstop))
        {
            config.PermanentBackstopSeconds = Math.Max(1, permanentBackstop);
            config.Save();
        }
        IconGlyph.HelpMarker("The limit for permanent-mode commands. Default 4 hours.");
        limitsBox.Dispose();

        using (Section.Begin("toyPatterns"))
            DrawToyPatternEditor();

        using (Section.Begin("toyTriggers"))
            DrawToyTriggerEditor();
    }

    private readonly ListDetail toyPatternList = new();
    private const string NewPatternKey = "new:pattern";

    /// The Owner's saved patterns are sent by value, since the Sub may not have them; `ownerCanSend` enables that.
    private void DrawToyPatternEditor(bool? ownerCanSend = null)
    {
        var config = plugin.Configuration;
        Section.SubHeading("Custom patterns");
        var items = config.ToyPatterns.Select(p => new ListDetailItem($"pattern:{p.Id}", p.Name,
            $"{p.Steps.Count} step{(p.Steps.Count == 1 ? "" : "s")}{(p.Loop ? ", loops" : "")}")).ToList();
        if (toyPatternList.Selected == NewPatternKey)
            items.Add(new ListDetailItem(NewPatternKey, "New pattern"));

        toyPatternList.Draw("toyPatterns", items,
            () =>
            {
                if (!ImGui.Button("+ New##toyPattern"))
                    return;
                ResetToyPatternInput();
                toyPatternList.Select(NewPatternKey);
            },
            item =>
            {
                if (item is null)
                {
                    IconGlyph.WrappedDisabled("Choose a pattern, or use + New to make one.");
                    return;
                }
                var pattern = config.ToyPatterns.FirstOrDefault(p => $"pattern:{p.Id}" == item.Key);
                if (pattern is not null)
                {
                    ImGui.AlignTextToFramePadding();
                    ImGui.TextColored(Theme.AccentHover, pattern.Name);
                    ImGui.SameLine();
                    if (ImGui.Button($"Delete##toyPattern{pattern.Id}"))
                    {
                        config.ToyPatterns.Remove(pattern);
                        // Disable, rather than orphan, triggers that named this pattern.
                        foreach (var rule in config.ToyTriggerRules.Where(r => string.Equals(r.PatternName, pattern.Name, StringComparison.OrdinalIgnoreCase)))
                            rule.Enabled = false;
                        config.Save();
                        ResetToyPatternInput();
                        toyPatternList.Select(null);
                        return;
                    }
                    if (ownerCanSend is { } canSend)
                    {
                        ImGui.SameLine();
                        DrawSendOnly(ToyControlCommand.BuildCustomSequenceCommand(pattern.Steps, pattern.Loop), canSend, $"toyPatternSend{pattern.Id}", "Send");
                        IconGlyph.HelpMarker("Sends this pattern to your Sub. They don't need it saved.");
                    }
                }
                DrawToyPatternForm();
            },
            () => toyPatternList.Selected == NewPatternKey
                ? toyPatternNameInput.Trim().Length > 0 || toyPatternStepsInput.Count > 0
                : config.ToyPatterns.FirstOrDefault(p => p.Id == toyPatternEditingId) is { } edited
                  && (edited.Name != toyPatternNameInput.Trim() || edited.Loop != toyPatternLoopInput
                      || !edited.Steps.Select(x => (x.IntensityPercent, x.DurationMs)).SequenceEqual(toyPatternStepsInput.Select(x => (x.IntensityPercent, x.DurationMs)))),
            item =>
            {
                ResetToyPatternInput();
                if (config.ToyPatterns.FirstOrDefault(p => $"pattern:{p.Id}" == item?.Key) is { } pattern)
                    LoadToyPatternDraft(pattern);
            },
            "No custom patterns yet. Use + New to make one.");
    }

    private void LoadToyPatternDraft(ToyPattern pattern)
    {
        toyPatternEditingId = pattern.Id;
        toyPatternNameInput = pattern.Name;
        toyPatternLoopInput = pattern.Loop;
        toyPatternStepsInput.Clear();
        toyPatternStepsInput.AddRange(pattern.Steps.Select(s => new PatternStep { IntensityPercent = s.IntensityPercent, DurationMs = s.DurationMs }));
    }

    private void DrawToyPatternForm()
    {
        var config = plugin.Configuration;
        ImGui.Spacing();
        IconGlyph.WrappedDisabled(toyPatternEditingId is null ? "New pattern" : "Editing pattern");
        ItemWidth(200);
        ImGui.InputText("Name##toyPatternName", ref toyPatternNameInput, 64);
        IconGlyph.HelpMarker("Can't match a built-in or another saved pattern.");
        ImGui.Checkbox("Loop##toyPatternLoop", ref toyPatternLoopInput);
        IconGlyph.HelpMarker("Repeat the steps until stopped.");
        ImGui.Spacing();

        ImGui.Indent();
        for (var i = 0; i < toyPatternStepsInput.Count; i++)
        {
            var step = toyPatternStepsInput[i];
            IconGlyph.WrappedDisabled($"Step {i + 1}");
            ItemWidth(140);
            var intensity = step.IntensityPercent;
            if (ImGui.SliderInt($"Intensity##toyPatternStep{i}", ref intensity, 0, 100, "%d%%"))
                step.IntensityPercent = intensity;
            IconGlyph.HelpMarker("How strong the vibration is while this step is active.");

            ContinueRowOrWrap(160);
            ItemWidth(120);
            var durationMs = step.DurationMs;
            if (ImGui.InputInt($"ms##toyPatternStepDuration{i}", ref durationMs))
                step.DurationMs = Math.Max(0, durationMs);
            IconGlyph.HelpMarker("Step length in milliseconds. 0 holds until stopped.");

            ContinueRowOrWrap(ButtonWidth("Up"));
            using (ImRaii.Disabled(i == 0))
            if (ImGui.Button($"Up##toyPatternStep{i}"))
            {
                (toyPatternStepsInput[i - 1], toyPatternStepsInput[i]) = (toyPatternStepsInput[i], toyPatternStepsInput[i - 1]);
                break;
            }

            ContinueRowOrWrap(ButtonWidth("Down"));
            using (ImRaii.Disabled(i == toyPatternStepsInput.Count - 1))
            if (ImGui.Button($"Down##toyPatternStep{i}"))
            {
                (toyPatternStepsInput[i + 1], toyPatternStepsInput[i]) = (toyPatternStepsInput[i], toyPatternStepsInput[i + 1]);
                break;
            }

            ContinueRowOrWrap(ButtonWidth("Remove"));
            if (ImGui.Button($"Remove##toyPatternStep{i}"))
            {
                toyPatternStepsInput.RemoveAt(i);
                break;
            }

            if (i < toyPatternStepsInput.Count - 1)
                ImGui.Separator();
        }
        ImGui.Unindent();

        ImGui.Spacing();
        if (ImGui.Button("Add step##toyPattern"))
            toyPatternStepsInput.Add(new PatternStep { IntensityPercent = 50, DurationMs = 500 });

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        if (ImGui.Button("Save pattern##toyPattern"))
        {
            var name = toyPatternNameInput.Trim();
            if (name.Length == 0)
                toyPatternError = "A pattern needs a name.";
            else if (toyPatternStepsInput.Count == 0)
                toyPatternError = "A pattern needs at least one step.";
            else if (ToyControlCommand.IsBuiltInPattern(name))
                toyPatternError = "That name is reserved for a built-in pattern.";
            else if (config.ToyPatterns.Any(p => p.Id != toyPatternEditingId && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
                toyPatternError = "Another custom pattern already has that name.";
            else
            {
                toyPatternError = null;
                var existing = toyPatternEditingId is { } id ? config.ToyPatterns.FirstOrDefault(p => p.Id == id) : null;
                var target = existing ?? new ToyPattern();
                var oldName = existing?.Name;
                target.Name = name;
                target.Loop = toyPatternLoopInput;
                target.Steps = toyPatternStepsInput.Select(s => new PatternStep { IntensityPercent = s.IntensityPercent, DurationMs = s.DurationMs }).ToList();
                if (existing is null)
                    config.ToyPatterns.Add(target);
                else if (!string.Equals(oldName, name, StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var rule in config.ToyTriggerRules.Where(r => string.Equals(r.PatternName, oldName, StringComparison.OrdinalIgnoreCase)))
                        rule.PatternName = name;
                }
                config.Save();
                LoadToyPatternDraft(target);
                toyPatternList.Select($"pattern:{target.Id}");
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel##toyPattern"))
        {
            if (config.ToyPatterns.FirstOrDefault(p => p.Id == toyPatternEditingId) is { } original)
                LoadToyPatternDraft(original);
            else
            {
                ResetToyPatternInput();
                toyPatternList.Select(null);
            }
        }
        if (toyPatternError is { Length: > 0 } error)
            IconGlyph.WrappedColored(Theme.Warning, error);
    }

    private void ResetToyPatternInput()
    {
        toyPatternEditingId = null;
        toyPatternNameInput = "";
        toyPatternLoopInput = false;
        toyPatternStepsInput.Clear();
        toyPatternError = null;
    }

    /// Gated on ToyTriggersAcknowledged only; ToyControl and its acknowledgement cover Owner commands.
    private void DrawToyTriggerEditor()
    {
        Section.SubHeading("Automatic triggers");

        var config = plugin.Configuration;

        if (!config.ToyTriggersAcknowledged)
        {
            IconGlyph.WrappedColored(Theme.Warning, "Needs the Automatic Triggers acknowledgement in Settings first.");
            return;
        }

        var runtimeState = plugin.RuntimeState;
        if (runtimeState.ToyTriggersSuspended)
        {
            IconGlyph.WrappedColored(Theme.Warning, "Triggers are suspended (panic was triggered). They will not fire again until you resume them.");
            if (ImGui.Button("Resume triggers##toyTriggers"))
                runtimeState.ToyTriggersSuspended = false;
            IconGlyph.HelpMarker("Turns triggers back on after a panic.");
            ImGui.Spacing();
        }

        var items = config.ToyTriggerRules.Select(r => new ListDetailItem($"trigger:{r.Id}", DescribeTrigger(r), r.Enabled ? null : "off",
            Tooltip: DescribeTrigger(r, full: true))).ToList();
        if (toyTriggerList.Selected == NewTriggerKey)
            items.Add(new ListDetailItem(NewTriggerKey, "New trigger"));

        toyTriggerList.Draw("toyTriggers", items,
            () =>
            {
                if (!ImGui.Button("+ New##toyTrigger"))
                    return;
                ResetToyTriggerDraft();
                toyTriggerList.Select(NewTriggerKey);
            },
            item =>
            {
                if (item is null)
                {
                    IconGlyph.WrappedDisabled("Choose a trigger, or use + New to make one.");
                    return;
                }
                if (config.ToyTriggerRules.FirstOrDefault(r => $"trigger:{r.Id}" == item.Key) is { } rule)
                {
                    var enabled = rule.Enabled;
                    if (ImGui.Checkbox($"On##toyTriggerEnabled{rule.Id}", ref enabled))
                    {
                        rule.Enabled = enabled;
                        config.Save();
                    }
                    ImGui.SameLine();
                    if (ImGui.Button($"Delete##toyTrigger{rule.Id}"))
                    {
                        config.ToyTriggerRules.Remove(rule);
                        config.Save();
                        ResetToyTriggerDraft();
                        toyTriggerList.Select(null);
                        return;
                    }
                    if (rule.PatternName is { Length: > 0 } targetPattern && !IsKnownPatternName(config, targetPattern))
                        IconGlyph.WrappedColored(Theme.Warning, $"\"{targetPattern}\" no longer exists - this trigger needs reconfiguration.");
                }
                DrawToyTriggerForm();
            },
            () => toyTriggerList.Selected != NewTriggerKey && ToyTriggerDraftDiffers(),
            item =>
            {
                ResetToyTriggerDraft();
                if (config.ToyTriggerRules.FirstOrDefault(r => $"trigger:{r.Id}" == item?.Key) is { } rule)
                    LoadToyTriggerDraft(rule);
            },
            "No triggers yet. Use + New to make one.");
    }

    private readonly ListDetail toyTriggerList = new();
    private const string NewTriggerKey = "new:trigger";

    /// Compares through a scratch rule built from the draft, so every field counts.
    private bool ToyTriggerDraftDiffers()
    {
        if (editingToyTriggerId is not { } id || plugin.Configuration.ToyTriggerRules.FirstOrDefault(r => r.Id == id) is not { } rule)
            return false;
        var draft = new ToyTriggerRule();
        ApplyToyTriggerDraft(draft);
        return DescribeTrigger(draft, full: true) != DescribeTrigger(rule, full: true) || draft.CooldownSeconds != rule.CooldownSeconds;
    }

    private void DrawToyTriggerForm()
    {
        var config = plugin.Configuration;
        ImGui.Spacing();
        var editingRule = editingToyTriggerId is { } editingId ? config.ToyTriggerRules.FirstOrDefault(r => r.Id == editingId) : null;
        if (editingToyTriggerId is not null && editingRule is null)
            ResetToyTriggerDraft();
        IconGlyph.WrappedDisabled(editingRule is null ? "New trigger" : "Edit trigger");
        ImGui.Spacing();
        var kindNames = new[] { "Health drops below %", "Hit (damage from anything)", "A restriction becomes active", "Spell cast on you", "Emote used on you" };
        var kindIndex = (int)toyTriggerKindInput;
        ItemWidth(220);
        if (ImGui.Combo("Condition##toyTriggerKind", ref kindIndex, kindNames, kindNames.Length))
            toyTriggerKindInput = (ToyTriggerKind)kindIndex;
        IconGlyph.HelpMarker("What fires this trigger. Runs only on your client.");

        if (toyTriggerKindInput == ToyTriggerKind.HealthPercent)
        {
            ItemWidth(120);
            ImGui.SliderInt("Threshold %##toyTriggerHealth", ref toyTriggerHealthThresholdInput, 1, 100);
            IconGlyph.HelpMarker("Fires when your health drops to this percent. It has to rise above it to fire again.");
        }
        else if (toyTriggerKindInput == ToyTriggerKind.RestrictionActive)
        {
            var restrictionNames = Enum.GetNames<RestraintRuleKind>();
            var restrictionIndex = (int)toyTriggerRestrictionKindInput;
            ItemWidth(200);
            if (ImGui.Combo("Restriction##toyTriggerRestriction", ref restrictionIndex, restrictionNames, restrictionNames.Length))
                toyTriggerRestrictionKindInput = (RestraintRuleKind)restrictionIndex;
            IconGlyph.HelpMarker("Fires when this restriction starts on you.");
        }
        else if (toyTriggerKindInput == ToyTriggerKind.PlayerDamage)
        {
            IconGlyph.WrappedDisabled("Fires when you take damage. Add players below to only react to them.");
            DrawToyTriggerSourcePlayers();
        }
        else if (toyTriggerKindInput == ToyTriggerKind.SpellCastOnYou)
        {
            IconGlyph.WrappedDisabled("Fires when a player uses an action on you. Narrow it by job or skill below.");
            DrawToySpellJobFilter();
            DrawToySpellActionFilter();
            DrawToyTriggerSourcePlayers();
        }
        else
        {
            IconGlyph.WrappedDisabled("Fires when a player emotes at you. Narrow it by emote or player below.");
            DrawToyEmoteFilter();
            DrawToyTriggerSourcePlayers();
        }

        ImGui.Checkbox("Named pattern (instead of a fixed intensity)##toyTriggerUsePattern", ref toyTriggerUsePatternInput);
        IconGlyph.HelpMarker("Off: a plain vibration. On: play a named pattern.");
        if (toyTriggerUsePatternInput)
        {
            ItemWidth(180);
            ImGui.InputText("Pattern name##toyTriggerPattern", ref toyTriggerPatternNameInput, 64);
            IconGlyph.HelpMarker("A built-in (weak/medium/strong/pulse) or saved pattern name. Unknown names never fire.");
        }
        else
        {
            ItemWidth(140);
            ImGui.SliderInt("Intensity##toyTriggerIntensity", ref toyTriggerIntensityInput, 0, 100, "%d%%");
            IconGlyph.HelpMarker("How strong the vibration is when this trigger fires.");

            ImGui.Checkbox("Duration##toyTriggerHasDuration", ref toyTriggerHasDurationInput);
            IconGlyph.HelpMarker("How long it vibrates. Unchecked uses the default limit.");
            if (toyTriggerHasDurationInput)
            {
                ImGui.SameLine();
                ItemWidth(120);
                ImGui.SliderInt("seconds##toyTriggerDuration", ref toyTriggerDurationSecondsInput, 1, ToyControlCommand.MaxDurationSeconds);
            }
        }

        ItemWidth(120);
        ImGui.SliderInt("Cooldown (s)##toyTriggerCooldown", ref toyTriggerCooldownInput, 2, 300);
        IconGlyph.HelpMarker("Minimum time between firings (at least 2 seconds).");

        ImGui.Spacing();
        if (editingRule is not null)
        {
            if (ImGui.Button("Save trigger##toyTrigger"))
            {
                ApplyToyTriggerDraft(editingRule);
                config.Save();
                LoadToyTriggerDraft(editingRule);
            }
            ContinueRowOrWrap(ButtonWidth("Cancel"));
            if (ImGui.Button("Cancel##toyTrigger"))
                LoadToyTriggerDraft(editingRule);
        }
        else if (ImGui.Button("Add trigger##toyTrigger"))
        {
            var rule = new ToyTriggerRule { Enabled = true };
            ApplyToyTriggerDraft(rule);
            config.ToyTriggerRules.Add(rule);
            config.Save();
            LoadToyTriggerDraft(rule);
            toyTriggerList.Select($"trigger:{rule.Id}");
        }
    }

    /// Keeps the rule's Id and Enabled state so the evaluator's cooldown tracking carries over.
    private void ApplyToyTriggerDraft(ToyTriggerRule rule)
    {
        rule.Kind = toyTriggerKindInput;
        rule.HealthPercentThreshold = toyTriggerHealthThresholdInput;
        rule.RestrictionKind = toyTriggerRestrictionKindInput;
        rule.SpellJobIds = toyTriggerSpellJobIdsInput.ToList();
        rule.SpellActionIds = toyTriggerSpellActionIdsInput.ToList();
        rule.EmoteIds = toyTriggerEmoteIdsInput.ToList();
        rule.SourcePlayers = toyTriggerSourcePlayersInput.ToList();
        rule.IntensityPercent = toyTriggerUsePatternInput ? null : toyTriggerIntensityInput;
        rule.PatternName = toyTriggerUsePatternInput ? toyTriggerPatternNameInput.Trim() : null;
        rule.DurationSeconds = toyTriggerUsePatternInput || !toyTriggerHasDurationInput ? null : toyTriggerDurationSecondsInput;
        rule.CooldownSeconds = Math.Max(2, toyTriggerCooldownInput);
    }

    private void LoadToyTriggerDraft(ToyTriggerRule rule)
    {
        editingToyTriggerId = rule.Id;
        toyTriggerKindInput = rule.Kind;
        toyTriggerHealthThresholdInput = rule.HealthPercentThreshold;
        toyTriggerRestrictionKindInput = rule.RestrictionKind;
        toyTriggerSpellJobIdsInput.Clear();
        toyTriggerSpellJobIdsInput.UnionWith(rule.SpellJobIds);
        toyTriggerSpellActionIdsInput.Clear();
        toyTriggerSpellActionIdsInput.UnionWith(rule.SpellActionIds);
        toyTriggerEmoteIdsInput.Clear();
        toyTriggerEmoteIdsInput.UnionWith(rule.EmoteIds);
        toyTriggerSourcePlayersInput.Clear();
        toyTriggerSourcePlayersInput.AddRange(rule.SourcePlayers);
        toyTriggerSpellJobSearch = "";
        toyTriggerSpellActionSearch = "";
        toyTriggerEmoteSearch = "";
        toyTriggerSourcePlayerInput = "";
        toyTriggerUsePatternInput = rule.PatternName is { Length: > 0 };
        toyTriggerPatternNameInput = rule.PatternName ?? "";
        toyTriggerIntensityInput = rule.IntensityPercent ?? 50;
        toyTriggerHasDurationInput = rule.DurationSeconds is not null;
        toyTriggerDurationSecondsInput = rule.DurationSeconds ?? 10;
        toyTriggerCooldownInput = rule.CooldownSeconds;
    }

    private void ResetToyTriggerDraft()
    {
        editingToyTriggerId = null;
        toyTriggerKindInput = ToyTriggerKind.HealthPercent;
        toyTriggerHealthThresholdInput = 50;
        toyTriggerRestrictionKindInput = RestraintRuleKind.Gagged;
        toyTriggerSpellJobIdsInput.Clear();
        toyTriggerSpellActionIdsInput.Clear();
        toyTriggerEmoteIdsInput.Clear();
        toyTriggerSourcePlayersInput.Clear();
        toyTriggerSpellJobSearch = "";
        toyTriggerSpellActionSearch = "";
        toyTriggerEmoteSearch = "";
        toyTriggerSourcePlayerInput = "";
        toyTriggerUsePatternInput = false;
        toyTriggerPatternNameInput = "";
        toyTriggerIntensityInput = 50;
        toyTriggerHasDurationInput = false;
        toyTriggerDurationSecondsInput = 10;
        toyTriggerCooldownInput = 5;
    }

    /// Checking none means "any emote".
    private void DrawToyEmoteFilter()
    {
        ItemWidth(200);
        ImGui.InputTextWithHint("##toyTriggerEmoteSearch", "Filter emotes...", ref toyTriggerEmoteSearch, 32);
        IconGlyph.HelpMarker("Which emote(s) this trigger reacts to. Leave every emote unchecked to react to any emote aimed at you.");
        if (toyTriggerEmoteIdsInput.Count > 0)
        {
            ContinueRowOrWrap(ButtonWidth("Clear"));
            if (ImGui.Button("Clear##toyTriggerEmotes"))
                toyTriggerEmoteIdsInput.Clear();
        }

        using var _ = ImRaii.Child("toyTriggerEmoteList", new Vector2(0, Scaled(110)), true);
        var search = toyTriggerEmoteSearch.Trim();
        foreach (var emote in Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Emote>()
                     .Where(e => e.RowId > 0 && e.Name.ExtractText().Length > 0)
                     .Select(e => (e.RowId, Name: e.Name.ExtractText(), Command: e.TextCommand.ValueNullable?.Command.ExtractText() ?? ""))
                     .Where(e => search.Length == 0 || e.Name.Contains(search, StringComparison.OrdinalIgnoreCase) || e.Command.Contains(search, StringComparison.OrdinalIgnoreCase))
                     .OrderByDescending(e => toyTriggerEmoteIdsInput.Contains(e.RowId))
                     .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
        {
            var isChecked = toyTriggerEmoteIdsInput.Contains(emote.RowId);
            var label = emote.Command.Length > 0 ? $"{emote.Name} ({emote.Command})" : emote.Name;
            if (ImGui.Checkbox($"{label}##toyTriggerEmote{emote.RowId}", ref isChecked))
            {
                if (isChecked) toyTriggerEmoteIdsInput.Add(emote.RowId);
                else toyTriggerEmoteIdsInput.Remove(emote.RowId);
            }
        }
    }

    private void DrawToyTriggerSourcePlayers()
    {
        Section.SubHeading("Only from these players");
        IconGlyph.HelpMarker("Empty = anyone. \"Name Surname\" matches any world; add @World for just one.");

        foreach (var entry in toyTriggerSourcePlayersInput.ToList())
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(entry);
            ContinueRowOrWrap(ButtonWidth("Remove"));
            if (ImGui.Button($"Remove##toyTriggerSource{entry}"))
                toyTriggerSourcePlayersInput.Remove(entry);
        }

        void AddPlayer(string value)
        {
            var trimmed = value.Trim();
            if (trimmed.Length > 0 && !toyTriggerSourcePlayersInput.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
                toyTriggerSourcePlayersInput.Add(trimmed);
        }

        ItemWidth(220);
        var submitted = ImGui.InputTextWithHint("##toyTriggerSourceInput", "Name Surname[@World]", ref toyTriggerSourcePlayerInput, 64, ImGuiInputTextFlags.EnterReturnsTrue);
        ContinueRowOrWrap(ButtonWidth("Add"));
        if ((ImGui.Button("Add##toyTriggerSource") || submitted) && toyTriggerSourcePlayerInput.Trim().Length > 0)
        {
            AddPlayer(toyTriggerSourcePlayerInput);
            toyTriggerSourcePlayerInput = "";
        }

        var target = Plugin.TargetManager.Target as Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter;
        using (ImRaii.Disabled(target is null))
        {
            if (ImGui.Button("Add my target##toyTriggerSource") && target is not null)
                AddPlayer($"{target.Name.TextValue}@{target.HomeWorld.ValueNullable?.Name.ExtractText()}");
        }
        if (target is null && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Target a player character first.");

        foreach (var pairing in plugin.Configuration.ActivePairings)
        {
            var peer = $"{pairing.PeerName}@{pairing.PeerWorld}";
            if (toyTriggerSourcePlayersInput.Contains(peer, StringComparer.OrdinalIgnoreCase)) continue;
            var label = $"Add {pairing.PeerName}";
            ContinueRowOrWrap(ButtonWidth(label));
            if (ImGui.Button($"{label}##toyTriggerSourcePeer{pairing.Id}"))
                AddPlayer(peer);
        }
    }

    /// Checking none means "any job".
    private void DrawToySpellJobFilter()
    {
        ItemWidth(200);
        ImGui.InputTextWithHint("##toyTriggerSpellJobSearch", "Filter jobs...", ref toyTriggerSpellJobSearch, 32);
        IconGlyph.HelpMarker("Which caster job(s) this trigger reacts to. Leave every job unchecked to match any job.");

        using var _ = ImRaii.Child("toyTriggerSpellJobList", new Vector2(0, Scaled(90)), true);
        var search = toyTriggerSpellJobSearch.Trim();
        foreach (var job in Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.ClassJob>()
                     .Where(j => j.RowId > 0 && j.Role > 0 && j.Abbreviation.ExtractText().Length > 0)
                     .Where(j => search.Length == 0 || j.Name.ExtractText().Contains(search, StringComparison.OrdinalIgnoreCase) || j.Abbreviation.ExtractText().Contains(search, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(j => j.Abbreviation.ExtractText()))
        {
            var isChecked = toyTriggerSpellJobIdsInput.Contains(job.RowId);
            if (ImGui.Checkbox($"{job.Abbreviation.ExtractText()} - {job.Name.ExtractText()}##toyTriggerSpellJob{job.RowId}", ref isChecked))
            {
                if (isChecked) toyTriggerSpellJobIdsInput.Add(job.RowId);
                else toyTriggerSpellJobIdsInput.Remove(job.RowId);
            }
        }
    }

    /// Thousands of rows, so it needs search text first (capped at 100). Checking none means "any action".
    private void DrawToySpellActionFilter()
    {
        ItemWidth(200);
        ImGui.InputTextWithHint("##toyTriggerSpellActionSearch", "Search skill/spell name...", ref toyTriggerSpellActionSearch, 32);
        IconGlyph.HelpMarker("Which skills fire this trigger. None checked = any. Type to search.");

        var search = toyTriggerSpellActionSearch.Trim();
        if (search.Length == 0)
        {
            foreach (var actionId in toyTriggerSpellActionIdsInput.ToList())
            {
                var selectedName = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>().GetRowOrDefault(actionId)?.Name.ExtractText() ?? actionId.ToString();
                TextWithActions($"Selected: {selectedName}", ButtonWidth("Remove"));
                if (ImGui.Button($"Remove##toyTriggerSpellAction{actionId}"))
                    toyTriggerSpellActionIdsInput.Remove(actionId);
            }
            return;
        }

        using var _ = ImRaii.Child("toyTriggerSpellActionList", new Vector2(0, Scaled(90)), true);
        foreach (var action in Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>()
                     .Where(a => a.IsPlayerAction && a.Name.ExtractText().Contains(search, StringComparison.OrdinalIgnoreCase))
                     .Take(100))
        {
            var isChecked = toyTriggerSpellActionIdsInput.Contains(action.RowId);
            if (ImGui.Checkbox($"{action.Name.ExtractText()}##toyTriggerSpellAction{action.RowId}", ref isChecked))
            {
                if (isChecked) toyTriggerSpellActionIdsInput.Add(action.RowId);
                else toyTriggerSpellActionIdsInput.Remove(action.RowId);
            }
        }
    }

    private static bool IsKnownPatternName(PluginConfig config, string name) =>
        ToyControlCommand.IsBuiltInPattern(name) ||
        config.ToyPatterns.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    private static string DescribeTrigger(ToyTriggerRule rule, bool full = false)
    {
        var condition = rule.Kind switch
        {
            ToyTriggerKind.HealthPercent => $"Health <= {rule.HealthPercentThreshold}%",
            ToyTriggerKind.PlayerDamage => "Hit",
            ToyTriggerKind.RestrictionActive => $"{rule.RestrictionKind} active",
            ToyTriggerKind.SpellCastOnYou => DescribeSpellFilter(rule, full),
            ToyTriggerKind.EmoteOnYou => DescribeEmoteFilter(rule, full),
            _ => rule.Kind.ToString(),
        };
        if ((rule.Kind is ToyTriggerKind.PlayerDamage or ToyTriggerKind.SpellCastOnYou or ToyTriggerKind.EmoteOnYou) && rule.SourcePlayers.Count > 0)
            condition += $" from {(!full && rule.SourcePlayers.Count > 2 ? $"{rule.SourcePlayers.Count} players" : string.Join(", ", rule.SourcePlayers))}";
        var action = rule.PatternName is { Length: > 0 } name ? $"pattern \"{name}\"" : $"{rule.IntensityPercent ?? 0}% vibrate{(rule.DurationSeconds is { } d ? $" for {d}s" : "")}";
        return $"{condition} -> {action} (cooldown {rule.CooldownSeconds}s)";
    }

    private static string DescribeEmoteFilter(ToyTriggerRule rule, bool full)
    {
        if (rule.EmoteIds.Count == 0)
            return "Any emote on you";
        if (!full && rule.EmoteIds.Count > 3)
            return $"Emote on you ({rule.EmoteIds.Count} emotes)";
        return $"Emote on you ({string.Join(", ", rule.EmoteIds.Select(id => Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Emote>().GetRowOrDefault(id)?.Name.ExtractText() ?? id.ToString()))})";
    }

    /// `full` lists every one, for the hover tooltip.
    private static string DescribeSpellFilter(ToyTriggerRule rule, bool full = false)
    {
        const int maxListed = 3;
        if (rule.SpellJobIds.Count == 0 && rule.SpellActionIds.Count == 0)
            return "Any spell cast on you";

        var jobs = rule.SpellJobIds.Count == 0 ? "any job" :
            !full && rule.SpellJobIds.Count > maxListed ? $"{rule.SpellJobIds.Count} jobs" :
            string.Join('/', rule.SpellJobIds.Select(id => Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.ClassJob>().GetRowOrDefault(id)?.Abbreviation.ExtractText() ?? id.ToString()));
        var actions = rule.SpellActionIds.Count == 0 ? "any action" :
            !full && rule.SpellActionIds.Count > maxListed ? $"{rule.SpellActionIds.Count} actions" :
            string.Join(", ", rule.SpellActionIds.Select(id => Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>().GetRowOrDefault(id)?.Name.ExtractText() ?? id.ToString()));
        return $"Spell cast on you ({jobs}, {actions})";
    }

    private void DrawToyControlQuickSection(bool canSend)
    {
        IconGlyph.Text(FontAwesomeIcon.Plug, "Toy Control");
        ImGui.Separator();
        IconGlyph.WrappedDisabled($"Commands stop after at most {ToyControlCommand.MaxDurationSeconds} seconds.");

        using (Section.Begin("toyQuickStatus", "Status"))
            ToyStatusView.DrawOwner(plugin.OwnerToyStatus.ForActivePairing);

        var vibrateBox = Section.Begin("toyQuickVibrate", "Vibrate");
        ItemWidth(200);
        ImGui.SliderInt("Intensity##toyControl", ref toyVibrateIntensity, 0, 100, "%d%%");

        if (ImGui.Checkbox("Permanent (runs until stopped)##toyControlPermanent", ref toyVibrateIsPermanent) && toyVibrateIsPermanent)
            toyVibrateHasDuration = false;
        if (toyVibrateIsPermanent)
            IconGlyph.WrappedDisabled("Still capped by the Sub's own configured permanent-mode backstop ceiling, not truly unbounded.");
        else
        {
            ImGui.Checkbox("Duration##toyControlHasDuration", ref toyVibrateHasDuration);
            if (toyVibrateHasDuration)
            {
                ImGui.SameLine();
                ItemWidth(120);
                ImGui.SliderInt("seconds##toyControlDuration", ref toyVibrateDurationSeconds, 1, ToyControlCommand.MaxDurationSeconds);
            }
        }

        var duration = toyVibrateIsPermanent ? ToyDuration.Permanent : toyVibrateHasDuration ? ToyDuration.Bounded(toyVibrateDurationSeconds) : ToyDuration.Unspecified;
        var vibrateCommand = ToyControlCommand.BuildVibrateCommand(toyVibrateIntensity, duration);
        DrawSendOnly(vibrateCommand, canSend, "toyControlVibrate", "Send vibrate");
        vibrateBox.Dispose();

        var patternsBox = Section.Begin("toyQuickPatterns", "Patterns & stop");
        for (var i = 0; i < ToyControlCommand.BuiltInPatternNames.Count; i++)
        {
            var name = ToyControlCommand.BuiltInPatternNames[i];
            var label = char.ToUpperInvariant(name[0]) + name[1..];
            if (i > 0) ContinueRowOrWrap(ButtonWidth(label));
            DrawSendOnly(ToyControlCommand.BuildPatternCommand(name), canSend, $"toyControl{label}", label);
        }

        ImGui.Spacing();
        DrawSendOnly(ToyControlCommand.BuildStopCommand(), canSend, "toyControlStop", "Send stop");
        patternsBox.Dispose();

        using (Section.Begin("toyQuickCustomPatterns"))
            DrawToyPatternEditor(canSend);
    }
}
