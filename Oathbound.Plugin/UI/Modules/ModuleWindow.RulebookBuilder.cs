using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Oathbound.Plugin.Config;
using Oathbound.Plugin.Rulebook;
using static Oathbound.Plugin.UI.Layout;

namespace Oathbound.Plugin.UI;

/// The Owner's rulebook as three sections - Rules, Points, Rewards & Punishments - and the "+ Add rule" wizard.
public sealed partial class ModuleWindow
{
    public const string RulebookRulesTab = "Rules";
    public const string RulebookPointsTab = "Points";
    public const string RulebookRewardsTab = "Rewards & Punishments";

    /// Older tab names, from tours and links, open the section that now holds them.
    private static string SectionFor(string tab) => tab switch
    {
        RulebookOathsTab or RulebookPlacesTab or RulebookPresenceTab or RulebookScheduleTab => RulebookRulesTab,
        RulebookLedgerTab => RulebookPointsTab,
        RulebookDeckTab or RulebookShopTab => RulebookRewardsTab,
        _ => tab,
    };

    private sealed record RuleEntry(string Id, string Name, string Kind, object Rule, Action Remove, Action Duplicate, Action<EditorPart> Edit, string? Status);

    private static readonly string[] RuleKindFilters = ["All rules", "Oaths", "Places", "Presence", "Schedule"];
    private int rbRuleFilter;
    private readonly ListDetail rbRulesList = new();

    private List<RuleEntry> RuleEntries(RulebookDocument draft, RulebookPairingState state)
    {
        var list = new List<RuleEntry>();
        void Dup<T>(List<T> into, T rule, Func<T, string> name, Action<T, string> setId, Action<T, string> setName)
        {
            var copy = JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(rule))!;
            setId(copy, RuleIds.New());
            setName(copy, $"{name(rule)} (copy)".Trim());
            into.Add(copy);
            plugin.Configuration.Save();
        }
        if (rbRuleFilter is 0 or 1)
            foreach (var o in draft.Oaths)
                list.Add(new RuleEntry(o.Id, o.Name, "Oath", o, () => draft.Oaths.Remove(o),
                    () => Dup(draft.Oaths, o, x => x.Name, (x, v) => x.Id = v, (x, v) => x.Name = v), p => DrawOathEditor(o, p), StatusOf(state, o.Id)));
        if (rbRuleFilter is 0 or 2)
            foreach (var r in draft.Places)
                list.Add(new RuleEntry(r.Id, r.Name, "Place", r, () => draft.Places.Remove(r),
                    () => Dup(draft.Places, r, x => x.Name, (x, v) => x.Id = v, (x, v) => x.Name = v), p => DrawPlaceEditor(r, p), null));
        if (rbRuleFilter is 0 or 3)
            foreach (var r in draft.Presence)
                list.Add(new RuleEntry(r.Id, r.Name, "Presence", r, () => draft.Presence.Remove(r),
                    () => Dup(draft.Presence, r, x => x.Name, (x, v) => x.Id = v, (x, v) => x.Name = v), p => DrawPresenceEditor(r, p), null));
        if (rbRuleFilter is 0 or 4)
            foreach (var t in draft.Times)
                list.Add(new RuleEntry(t.Id, t.Name, "Schedule", t, () => draft.Times.Remove(t),
                    () => Dup(draft.Times, t, x => x.Name, (x, v) => x.Id = v, (x, v) => x.Name = v), p => DrawTimeEditor(t, p), null));
        return list;
    }

    private void DrawRulesSection(PairingState pairing, RulebookDocument draft)
    {
        using var section = Section.Begin("rbRules", "Rules");
        if (rbWizard is not null)
        {
            DrawRuleWizard(draft);
            return;
        }
        IconGlyph.WrappedDisabled("What your Sub has to do, and what happens when they do it or don't. They say yes to each oath on its own.");
        DrawSimpleAdvancedToggle();
        if (draft.Times.Count > 0)
            IconGlyph.WrappedColored(Theme.Warning, $"Rules at a time of day or at login: {NewSubNote}");
        if (ShowTemplateCard(draft))
            DrawTemplateCard(draft);

        var entries = RuleEntries(draft, pairing.Rulebook);
        var items = entries.Select(e => new ListDetailItem(e.Id, string.IsNullOrWhiteSpace(e.Name) ? $"(unnamed {e.Kind.ToLowerInvariant()})" : e.Name, e.Kind,
            Tooltip: RuleSentence.Of(e.Rule, "you"))).ToList();
        if (rbEditingId is { } editing && entries.Any(e => e.Id == editing) && rbRulesList.Selected != editing)
            rbRulesList.Select(editing);
        rbRulesList.Draw("rbRulesList", items, () =>
        {
            if (ImGui.Button("+ Add rule"))
                rbWizard = new RuleWizardState();
            ContinueRowOrWrap(Scaled(140));
            ItemWidth(140);
            ImGui.Combo("##ruleFilter", ref rbRuleFilter, RuleKindFilters, RuleKindFilters.Length);
            ContinueRowOrWrap(ButtonWidth("Templates..."));
            if (ImGui.Button("Templates..."))
            {
                rbShowTemplates = !ShowTemplateCard(draft);
                rbTemplatesDismissed = !rbShowTemplates;
                rbTemplateConfirm = null;
            }
        }, item =>
        {
            if (item is null || entries.FirstOrDefault(e => e.Id == item.Key) is not { } entry)
            {
                IconGlyph.WrappedDisabled("Choose a rule, or use + Add rule.");
                return;
            }
            ImGui.PushID(entry.Id);
            ImGui.PushTextWrapPos(0);
            ImGui.TextColored(Theme.AccentHover, RuleSentence.Of(entry.Rule, "you"));
            ImGui.PopTextWrapPos();
            if (entry.Status is { } status)
                IconGlyph.WrappedColored(Theme.AccentHover, status);
            if (ImGui.Button("Duplicate"))
                entry.Duplicate();
            ImGui.SameLine();
            if (ImGui.Button("Delete"))
            {
                entry.Remove();
                rbEditingId = null;
                plugin.Configuration.Save();
                rbRulesList.Select(null);
                ImGui.PopID();
                return;
            }
            ImGui.Separator();
            entry.Edit(EditorPart.All);
            ImGui.PopID();
        }, null, item => rbEditingId = item?.Key, "No rules yet. Use + Add rule, or start from a template.");
    }

    private static int TotalRules(RulebookDocument d) =>
        d.Oaths.Count + d.Places.Count + d.Presence.Count + d.Times.Count + d.Deck.Count + d.Shop.Count + d.Thresholds.Count;

    private void DrawPointsSection(PairingState pairing, RulebookDocument draft)
    {
        DrawPointsPanel("rbOwnerPoints", PointsSummary.From(draft, pairing.PeerName ?? "you"), pairing.Rulebook.LastReport?.LedgerScore, sub: false);
        DrawLedgerTab(draft);
    }

    private void DrawRewardsSection(RulebookDocument draft)
    {
        DrawSimpleAdvancedToggle();
        DrawDeckTab(draft);
        DrawShopTab(draft);
    }

    // ---- Templates ----

    private RulebookTemplates.Template? rbTemplateConfirm;
    private bool rbShowTemplates;
    /// The empty-state card was closed with Start empty; Templates... brings it back.
    private bool rbTemplatesDismissed;

    private bool ShowTemplateCard(RulebookDocument draft) => rbShowTemplates || (TotalRules(draft) == 0 && !rbTemplatesDismissed);

    private void CloseTemplateCard()
    {
        rbShowTemplates = false;
        rbTemplatesDismissed = true;
        rbTemplateConfirm = null;
    }
    private bool rbTemplatesChecked;

    /// Fills the draft only; nothing reaches the Sub until the Owner publishes.
    private void ApplyTemplate(RulebookDocument draft, RulebookTemplates.Template template)
    {
        var built = template.Build();
        draft.Oaths = built.Oaths;
        draft.Deck = built.Deck;
        draft.Places = built.Places;
        draft.Presence = built.Presence;
        draft.Thresholds = built.Thresholds;
        draft.Times = built.Times;
        draft.Shop = built.Shop;
        draft.DrawOn = built.DrawOn;
        rbEditingId = null;
        plugin.Configuration.Save();
    }

    /// Offered on an empty draft; over a draft with rules it asks first.
    private void DrawTemplateCard(RulebookDocument draft)
    {
        using var section = Section.Begin("rbTemplates", "Start from a template");
        var empty = TotalRules(draft) == 0;
#if DEBUG
        if (!rbTemplatesChecked)
        {
            rbTemplatesChecked = true;
            foreach (var t in RulebookTemplates.All)
                if (RulebookValidation.Check(t.Build()) is { } problem)
                    Plugin.Log.Error($"Rulebook template \"{t.Name}\" doesn't validate: {problem}");
        }
#endif
        IconGlyph.WrappedDisabled(empty
            ? "Pick one to fill your rulebook with a ready set of oaths, cards, a shop and a milestone - then change anything you like. Nothing is sent until you publish."
            : "Replaces everything in your draft. Nothing is sent until you publish.");
        foreach (var template in RulebookTemplates.All)
        {
            ImGui.PushID(template.Name);
            if (ImGui.Button(template.Name))
            {
                if (empty)
                {
                    ApplyTemplate(draft, template);
                    CloseTemplateCard();
                }
                else
                    rbTemplateConfirm = template;
            }
            ImGui.SameLine();
            IconGlyph.WrappedDisabled(template.Description);
            ImGui.PopID();
        }
        if (rbTemplateConfirm is null && ImGui.Button(empty ? "Start empty##template" : "Close##template"))
            CloseTemplateCard();
        if (rbTemplateConfirm is { } pending)
        {
            IconGlyph.WrappedColored(Theme.Warning, $"Replace your whole draft with \"{pending.Name}\"?");
            if (ImGui.Button("Replace##template"))
            {
                ApplyTemplate(draft, pending);
                CloseTemplateCard();
            }
            ImGui.SameLine();
            if (ImGui.Button("Keep my draft##template"))
                rbTemplateConfirm = null;
        }
    }

    // ---- Simple / Advanced ----

    private void DrawSimpleAdvancedToggle()
    {
        var advanced = plugin.Configuration.RulebookAdvanced;
        if (ImGui.Checkbox("Show advanced settings", ref advanced))
        {
            plugin.Configuration.RulebookAdvanced = advanced;
            plugin.Configuration.Save();
        }
        IconGlyph.HelpMarker("Strikes, streak bonuses, repeating and re-offering oaths, cooldowns and card weights. Off, new rules use sensible defaults; a rule that already uses one still shows it.");
    }

    // ---- Wizard ----

    private sealed class RuleWizardState
    {
        public int Step;
        public object? Rule;
    }

    private RuleWizardState? rbWizard;

    private sealed record WizardChoice(string Group, string Label, string Help, Func<object> Make);

    private static readonly string[] WizardStepNames = ["What should they do?", "The details", "What happens?", "Check it"];

    private static IEnumerable<WizardChoice> WizardChoices()
    {
        foreach (var (group, items) in OathChoices)
        {
            var name = group == "Rituals" ? "Daily rituals" : group;
            foreach (var (condition, label, help) in items)
                yield return new WizardChoice(name, label, help, () => new Oath
                {
                    Name = label,
                    Condition = condition,
                    Scope = OathScope.ForATime,
                    DurationMinutes = 7 * 1440,
                });
            if (group == "Places & time")
                yield return new WizardChoice(name, "When they enter or leave a place", "Run something each time they arrive at or leave a place you pick.",
                    () => new PlaceRule { Name = "In a city", Places = { new PlaceRef { Kind = PlaceKind.MainCity } } });
        }
        yield return new WizardChoice("When I'm near", "When I arrive or leave", "Run something when your character comes near them, or goes away - leash them, greet them, give points.",
            () => new PresenceRule { Name = "When I arrive" });
        yield return new WizardChoice("At a time of day", "At a set time", "Run something at a time of day on the days you pick, in their local time.",
            () => new TimeRule { Name = "Bedtime" });
        yield return new WizardChoice("At a time of day", "Each time they log in", "Run something every time they log in.",
            () => new TimeRule { Name = "On login", Trigger = TimeTrigger.OnLogin });
    }

    private void DrawWizardEditor(object rule, EditorPart part)
    {
        switch (rule)
        {
            case Oath o: DrawOathEditor(o, part); break;
            case PlaceRule r: DrawPlaceEditor(r, part); break;
            case PresenceRule r: DrawPresenceEditor(r, part); break;
            case TimeRule t: DrawTimeEditor(t, part); break;
        }
    }

    private static void AddToDraft(RulebookDocument draft, object rule)
    {
        switch (rule)
        {
            case Oath o: draft.Oaths.Add(o); break;
            case PlaceRule r: draft.Places.Add(r); break;
            case PresenceRule r: draft.Presence.Add(r); break;
            case TimeRule t: draft.Times.Add(t); break;
        }
    }

    private static void RemoveFromDraft(RulebookDocument draft, object rule)
    {
        switch (rule)
        {
            case Oath o: draft.Oaths.Remove(o); break;
            case PlaceRule r: draft.Places.Remove(r); break;
            case PresenceRule r: draft.Presence.Remove(r); break;
            case TimeRule t: draft.Times.Remove(t); break;
        }
    }

    private void DrawRuleWizard(RulebookDocument draft)
    {
        var wizard = rbWizard!;
        ImGui.TextColored(Theme.TextMuted, $"Step {wizard.Step + 1} of {WizardStepNames.Length}");
        ImGui.SameLine();
        ImGui.TextUnformatted(WizardStepNames[wizard.Step]);
        ImGui.Separator();

        switch (wizard.Step)
        {
            case 0:
                foreach (var group in WizardChoices().GroupBy(c => c.Group))
                {
                    Section.SubHeading(group.Key);
                    foreach (var choice in group)
                    {
                        var full = choice.Make() is TimeRule && draft.Times.Count >= RulebookLimits.MaxTimeRules;
                        using (ImRaii.Disabled(full))
                        {
                            if (ImGui.Selectable($"{choice.Label}##wiz{choice.Group}{choice.Label}"))
                            {
                                wizard.Rule = choice.Make();
                                wizard.Step = 1;
                            }
                        }
                        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                            ImGui.SetTooltip(full ? "The schedule is full." : choice.Help);
                    }
                }
                break;
            case 1:
                DrawWizardEditor(wizard.Rule!, EditorPart.Details);
                break;
            case 2:
                IconGlyph.WrappedDisabled("Give or take points, draw a card, or run something. You can leave either side empty.");
                DrawWizardEditor(wizard.Rule!, EditorPart.Outcomes);
                break;
            case 3:
                ImGui.PushTextWrapPos(0);
                ImGui.TextColored(Theme.AccentHover, RuleSentence.Of(wizard.Rule!, "you"));
                ImGui.PopTextWrapPos();
                break;
        }

        ImGui.Spacing();
        ImGui.Separator();
        string? problem = null;
        if (wizard.Step == 3)
        {
            // Checked as part of the draft, the same way Publish checks it.
            AddToDraft(draft, wizard.Rule!);
            problem = RulebookValidation.Check(draft);
            RemoveFromDraft(draft, wizard.Rule!);
            if (problem is not null)
                IconGlyph.WrappedColored(Theme.Warning, $"Fix this first: {problem}");
        }
        if (ImGui.Button("Cancel##wizard"))
        {
            rbWizard = null;
            return;
        }
        if (wizard.Step > 0)
        {
            ImGui.SameLine();
            if (ImGui.Button("Back##wizard"))
                wizard.Step--;
        }
        if (wizard.Step is 1 or 2)
        {
            ImGui.SameLine();
            if (ImGui.Button("Next##wizard"))
                wizard.Step++;
        }
        if (wizard.Step == 3)
        {
            ImGui.SameLine();
            using (ImRaii.Disabled(problem is not null))
            {
                if (ImGui.Button("Add rule##wizard"))
                {
                    AddToDraft(draft, wizard.Rule!);
                    rbEditingId = wizard.Rule switch { Oath o => o.Id, PlaceRule r => r.Id, PresenceRule r => r.Id, TimeRule t => t.Id, _ => null };
                    plugin.Configuration.Save();
                    rbWizard = null;
                }
            }
        }
    }
}
