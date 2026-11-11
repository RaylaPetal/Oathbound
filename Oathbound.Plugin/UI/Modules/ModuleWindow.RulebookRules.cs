using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Oathbound.Plugin.Commands;
using Oathbound.Plugin.Config;
using Oathbound.Plugin.Relay;
using Oathbound.Plugin.Rulebook;
using static Oathbound.Plugin.UI.Layout;

namespace Oathbound.Plugin.UI;

public sealed partial class ModuleWindow
{
    /// Which part of a rule's editor to draw: the wizard shows details and outcomes as separate steps.
    private enum EditorPart { All, Details, Outcomes }

    private void DrawTimeEditor(TimeRule rule, EditorPart part = EditorPart.All)
    {
        ImGui.PushID(rule.Id);
        var changed = false;
        if (part != EditorPart.Outcomes)
        {
            if (DrawName(rule.Name, out var name)) { rule.Name = name; changed = true; }
            var trigger = (int)rule.Trigger;
            ItemWidth(200);
            if (ImGui.Combo("##trigger", ref trigger, TimeTriggerNames, TimeTriggerNames.Length)) { rule.Trigger = (TimeTrigger)trigger; changed = true; }
            if (rule.Trigger == TimeTrigger.AtTime)
            {
                changed |= DrawClock("at", rule.Minutes, v => rule.Minutes = v);
                IconGlyph.HelpMarker($"Your Sub's local time. If they're not logged in then, it still runs when they log in within {RulebookLimits.TimeRuleWindowMinutes} minutes; later than that, it's skipped for the day.");
                var first = true;
                foreach (var (day, label) in WeekdayChoices)
                {
                    var width = ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X + ImGui.CalcTextSize(label).X;
                    if (!first)
                        ContinueRowOrWrap(width);
                    first = false;
                    var on = TimeRules.On(rule.Weekdays, day);
                    if (ImGui.Checkbox($"{label}##day{(int)day}", ref on))
                    {
                        rule.Weekdays = on ? rule.Weekdays | (1 << (int)day) : rule.Weekdays & ~(1 << (int)day);
                        changed = true;
                    }
                }
            }
        }
        if (part != EditorPart.Details)
        {
            Section.SubHeading("Do");
            changed |= DrawConsequenceEditor("does", rule.Consequence);
            ImGui.Spacing();
            var cd = rule.CooldownSeconds;
            if (DrawCooldown("timeCooldown", ref cd, "It won't run again within this time.")) { rule.CooldownSeconds = cd; changed = true; }
        }
        if (changed) plugin.Configuration.Save();
        if (part == EditorPart.All)
            DrawDoneButton();
        ImGui.PopID();
    }

    private void DrawOathEditor(Oath oath, EditorPart part = EditorPart.All)
    {
        ImGui.PushID(oath.Id);
        var changed = false;
        if (part != EditorPart.Outcomes)
        {
            if (DrawName(oath.Name, out var name)) { oath.Name = name; changed = true; }

            // Drafts made before scopes were tied to duty oaths may hold a combination the editor can no longer show.
            if (!OathConditions.IsDuty(oath.Condition) && oath.Scope == OathScope.NextDuty)
            {
                oath.Scope = OathScope.ForATime;
                changed = true;
            }

            Section.SubHeading("They swear to");
            changed |= DrawOathConditionPicker(oath);
            var condition = oath.Condition;
            if (OathConditions.TakesPhrase(condition))
            {
                var phrase = oath.Phrase;
                var (label, hint) = condition switch
                {
                    OathCondition.AddressOwner => ("word they must use", "e.g. Mistress"),
                    OathCondition.ForbiddenWord => ("forbidden word", "e.g. no"),
                    OathCondition.AskBeforeLogoff => ("word that grants leave", "e.g. granted"),
                    _ => ("has to say (optional)", "e.g. good morning"),
                };
                ItemWidth(200);
                if (ImGui.InputTextWithHint(label, hint, ref phrase, RulebookLimits.MaxPhraseLength)) { oath.Phrase = phrase; changed = true; }
                if (condition is OathCondition.AddressOwner or OathCondition.ForbiddenWord or OathCondition.MessageOwner or OathCondition.SayGoodnight)
                    IconGlyph.HelpMarker("Not case-sensitive, and it can be anywhere in the message.");
                else if (condition == OathCondition.AskBeforeLogoff)
                    IconGlyph.HelpMarker("Any /tell from you that contains it grants leave - the Grant leave button in the Overview tab sends just the word. Not case-sensitive.");
            }
            switch (condition)
            {
                case OathCondition.GreetOwner:
                {
                    var useAnimation = !string.IsNullOrEmpty(oath.AnimationId) || rbGreetAnimationMode == oath.Id;
                    if (ImGui.RadioButton("a gesture##greetVanilla", !useAnimation) && useAnimation)
                    {
                        oath.AnimationId = "";
                        oath.AnimationLabel = "";
                        rbGreetAnimationMode = null;
                        changed = true;
                    }
                    ImGui.SameLine();
                    if (ImGui.RadioButton("a modded animation##greetModded", useAnimation) && !useAnimation)
                        rbGreetAnimationMode = oath.Id;

                    if (!useAnimation)
                    {
                        if (DrawEmoteCombo("gesture##oathEmote", oath.EmoteId, ref rbEmoteSearch, out var picked)) { oath.EmoteId = picked.Id; changed = true; }
                        IconGlyph.HelpMarker("It counts when they use it while targeting you, so they have to be near you. They can also press Perform on the oath, which plays it at you and holds them for the time below; that counts once the hold ends.");
                    }
                    else
                    {
                        ImGui.AlignTextToFramePadding();
                        ImGui.TextUnformatted(string.IsNullOrEmpty(oath.AnimationLabel) ? "No animation picked." : oath.AnimationLabel);
                        ImGui.SameLine();
                        if (ImGui.Button("Pick animation"))
                        {
                            var target = oath;
                            plugin.AnimationPickerWindow.OpenImported(entry =>
                            {
                                target.AnimationId = entry.Id;
                                target.AnimationLabel = entry.DisplayLabel;
                                target.EmoteId = 0;
                                plugin.Configuration.Save();
                            });
                        }
                        IconGlyph.HelpMarker("From your Sub's own animations (sync their catalog first). They press Perform on the oath while targeting you: their plugin turns the mod on, plays it at you and holds them in place for the time below. It counts once the hold ends.");
                    }

                    var hold = oath.HoldSeconds;
                    ItemWidth(100);
                    if (ImGui.InputInt("seconds held##greetHold", ref hold, 5, 30)) { oath.HoldSeconds = Math.Clamp(hold, GestureCommand.MinHoldSeconds, GestureCommand.MaxHoldSeconds); changed = true; }
                    IconGlyph.HelpMarker("How long Perform keeps them in place before the animation stops and they can move again. Stopping early (panic, or your Stop animation) doesn't count.");
                    break;
                }
                case OathCondition.DutyTimeLimit:
                    var limit = oath.TimeLimitMinutes;
                    ItemWidth(120);
                    if (ImGui.InputInt("minutes per duty", ref limit, 5, 15)) { oath.TimeLimitMinutes = Math.Clamp(limit, 1, 600); changed = true; }
                    break;
                case OathCondition.StayInPlaces or OathCondition.AvoidPlaces:
                    changed |= DrawPlaceList("oathPlaces", oath.Places);
                    break;
                case OathCondition.Curfew:
                    changed |= DrawClock("from", oath.CurfewStartMinutes, v => oath.CurfewStartMinutes = v);
                    changed |= DrawClock("until", oath.CurfewEndMinutes, v => oath.CurfewEndMinutes = v);
                    IconGlyph.HelpMarker("Their local time. Being logged in at any point inside this window breaks the oath.");
                    break;
                case OathCondition.DutyQuota:
                    ImGui.TextUnformatted("Which duties");
                    IconGlyph.HelpMarker("Leave the list empty for any duty. A duty counts once it's completed.");
                    changed |= DrawPlaceList("quotaDuties", oath.Places);
                    break;
                case OathCondition.JobLock:
                    changed |= DrawJobPicker(oath.JobIds);
                    break;
                case OathCondition.KeepItOn:
                {
                    var minutes = Math.Max(1, oath.GraceSeconds / 60);
                    ItemWidth(100);
                    if (ImGui.InputInt("minutes to put it back##keepGrace", ref minutes, 1, 5))
                    {
                        oath.GraceSeconds = Math.Clamp(minutes * 60, RulebookLimits.MinKeepItOnGraceSeconds, RulebookLimits.MaxKeepItOnGraceSeconds);
                        changed = true;
                    }
                    IconGlyph.HelpMarker("The outfit is whichever one you last sent them (or a rule of yours put on). Restraints taking a slot, and your own commands, never count as taking it off.");
                    break;
                }
                case OathCondition.StayAtSide:
                {
                    var range = oath.RangeYalms;
                    ItemWidth(160);
                    if (ImGui.SliderFloat("Range (yalms)##sideRange", ref range, RulebookLimits.MinRangeYalms, RulebookLimits.MaxRangeYalms, "%.0f")) { oath.RangeYalms = range; changed = true; }
                    var grace = oath.GraceSeconds;
                    ItemWidth(100);
                    if (ImGui.InputInt("seconds to come back##sideGrace", ref grace, 5, 30))
                    {
                        oath.GraceSeconds = Math.Clamp(grace, RulebookLimits.MinSideGraceSeconds, RulebookLimits.MaxSideGraceSeconds);
                        changed = true;
                    }
                    var skip = oath.SkipInDuties;
                    if (ImGui.Checkbox("Not in duties##sideSkip", ref skip)) { oath.SkipInDuties = skip; changed = true; }
                    IconGlyph.HelpMarker("Only counts while you're both loaded in the same place. You being in another area never breaks it.");
                    break;
                }
                case OathCondition.AskBeforeLogoff:
                {
                    var window = oath.LeaveWindowMinutes;
                    ItemWidth(100);
                    if (ImGui.InputInt("minutes leave lasts##leaveWindow", ref window, 5, 15))
                    {
                        oath.LeaveWindowMinutes = Math.Clamp(window, RulebookLimits.MinLeaveWindowMinutes, RulebookLimits.MaxLeaveWindowMinutes);
                        changed = true;
                    }
                    IconGlyph.HelpMarker("A normal logout without leave breaks it. Closing the game or crashing can't be told apart, so it never does.");
                    break;
                }
            }

            if (OathConditions.IsRitual(condition))
            {
                if (OathConditions.CountsTimes(condition))
                {
                    var times = oath.TimesPerPeriod;
                    ItemWidth(100);
                    if (ImGui.InputInt("times##ritualTimes", ref times, 1, 1)) { oath.TimesPerPeriod = Math.Clamp(times, 1, RulebookLimits.MaxTimesPerPeriod); changed = true; }
                    ImGui.SameLine();
                }
                ImGui.TextUnformatted("every");
                ImGui.SameLine();
                var every = oath.PeriodDays;
                ItemWidth(100);
                if (ImGui.InputInt("day(s)##ritualEvery", ref every, 1, 1)) { oath.PeriodDays = Math.Clamp(every, 1, RulebookLimits.MaxPeriodDays); changed = true; }
                IconGlyph.HelpMarker("Doing it more often than this doesn't count extra. A missed stretch doesn't end the oath, but it ends as broken if any stretch was missed.");
            }
            else if (oath.LedgerPerDone != 0 || oath.LedgerPerMissed != 0)
            {
                // Hidden amounts left over from a ritual condition would make the oath fail validation.
                oath.LedgerPerDone = 0;
                oath.LedgerPerMissed = 0;
                changed = true;
            }

            if (OathConditions.IsDuty(condition))
            {
                var scope = (int)oath.Scope;
                ItemWidth(260);
                if (ImGui.Combo("##scope", ref scope, OathScopeNames, OathScopeNames.Length)) { oath.Scope = (OathScope)scope; changed = true; }
            }
            if (oath.Scope == OathScope.ForATime)
                changed |= DrawOathDuration(oath);

        }
        if (part != EditorPart.Details)
        {
            Section.SubHeading("If they keep it");
            changed |= DrawLedgerDelta("keptLedger", oath.KeptLedger, v => oath.KeptLedger = v);
            changed |= DrawConsequenceEditor("kept", oath.Kept);
            Section.SubHeading("If they break it");
            changed |= DrawLedgerDelta("brokenLedger", oath.BrokenLedger, v => oath.BrokenLedger = v);
            changed |= DrawConsequenceEditor("broken", oath.Broken);
            if (OathConditions.IsRitual(oath.Condition))
            {
                Section.SubHeading("Each time");
                ImGui.TextUnformatted("done");
                ImGui.SameLine();
                changed |= DrawLedgerDelta("perDoneLedger", oath.LedgerPerDone, v => oath.LedgerPerDone = v);
                ImGui.TextUnformatted("missed");
                ImGui.SameLine();
                changed |= DrawLedgerDelta("perMissedLedger", oath.LedgerPerMissed, v => oath.LedgerPerMissed = v);
                IconGlyph.HelpMarker("Applied while the oath runs: once for each time it counts, and once for each time still missing when a stretch ends. Use a negative number for missed.");
            }
            IconGlyph.WrappedDisabled("To also draw a card, tick it in the Deck tab under Draw automatically.");

            if (ShowAdvanced(oath.Strikes > 0 || oath.LedgerPerStrike != 0 || oath.StreakEvery > 0 || oath.Recurrence != OathRecurrence.Once))
                changed |= DrawOathRepeats(oath);
            if (oath.NeedsNewSub)
                IconGlyph.WrappedColored(Theme.Warning, "Needs your Sub on this version of Oathbound or newer - an older one refuses the whole rulebook until they update.");

        }
        if (part == EditorPart.All)
        {
            ImGui.Spacing();
            if (ImGui.Button("Offer again"))
            {
                // A new id is a new offer; the old one keeps whatever state the Sub's client gave it.
                oath.Id = RuleIds.New();
                rbEditingId = oath.Id;
                changed = true;
            }
            IconGlyph.HelpMarker("After they've kept, broken or declined it: offer the same oath again in your next send.");
        }
        if (changed) plugin.Configuration.Save();
        if (part == EditorPart.All)
            DrawDoneButton();
        ImGui.PopID();
    }

    private static string OathConditionLabel(OathCondition condition) =>
        OathChoices.SelectMany(g => g.Items).FirstOrDefault(i => i.Condition == condition).Label ?? condition.ToString();

    /// Grouped, so the D/s rituals come first and duty-only oaths are clearly separate.
    private static bool DrawOathConditionPicker(Oath oath)
    {
        var changed = false;
        ItemWidth(260);
        if (ImGui.BeginCombo("##condition", OathConditionLabel(oath.Condition)))
        {
            foreach (var (group, items) in OathChoices)
            {
                ImGui.TextColored(Theme.AccentHover, group);
                foreach (var (condition, label, help) in items)
                {
                    if (ImGui.Selectable($"  {label}", oath.Condition == condition))
                    {
                        oath.Condition = condition;
                        // Only duty oaths can be scoped to a duty; a ritual needs at least one full repeat to judge.
                        if (!OathConditions.IsDuty(condition))
                            oath.Scope = OathScope.ForATime;
                        if (OathConditions.IsRitual(condition) && oath.DurationMinutes < oath.PeriodDays * 1440)
                            oath.DurationMinutes = 7 * 1440;
                        switch (condition)
                        {
                            case OathCondition.KeepItOn:
                                oath.GraceSeconds = 5 * 60;
                                break;
                            case OathCondition.StayAtSide:
                                oath.GraceSeconds = 30;
                                break;
                            case OathCondition.AskBeforeLogoff when string.IsNullOrWhiteSpace(oath.Phrase):
                                oath.Phrase = "granted";
                                break;
                        }
                        changed = true;
                    }
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(help);
                }
            }
            ImGui.EndCombo();
        }
        var current = OathChoices.SelectMany(g => g.Items).FirstOrDefault(i => i.Condition == oath.Condition).Help;
        if (current is not null)
            IconGlyph.WrappedDisabled(current);
        return changed;
    }

    /// Rituals are counted in days; everything else in hours.
    private static bool DrawOathDuration(Oath oath)
    {
        ImGui.TextUnformatted("for");
        ImGui.SameLine();
        ItemWidth(110);
        if (OathConditions.IsRitual(oath.Condition))
        {
            var days = Math.Max(1, oath.DurationMinutes / 1440);
            if (!ImGui.InputInt("day(s)##oathDays", ref days, 1, 7))
                return false;
            oath.DurationMinutes = Math.Clamp(days, oath.PeriodDays, RulebookLimits.MaxOathMinutes / 1440) * 1440;
            return true;
        }
        var hours = Math.Max(1, oath.DurationMinutes / 60);
        var changed = ImGui.InputInt("hour(s)##oathHours", ref hours, 1, 24);
        if (changed)
            oath.DurationMinutes = Math.Clamp(hours * 60, RulebookLimits.MinOathMinutes, RulebookLimits.MaxOathMinutes);
        ImGui.SameLine();
        ImGui.TextDisabled($"= {RestraintLock.Format(TimeSpan.FromMinutes(oath.DurationMinutes))}");
        return changed;
    }

    /// A whole role toggles with its button; single jobs underneath.
    private static bool DrawJobPicker(List<uint> jobIds)
    {
        var changed = false;
        ImGui.TextUnformatted("Allowed jobs");
        IconGlyph.HelpMarker("A base class counts as the jobs it becomes, so Conjurer counts for White Mage.");
        foreach (var role in new[] { JobRole.Tank, JobRole.Healer, JobRole.Melee, JobRole.Ranged, JobRole.Caster })
        {
            ImGui.PushID((int)role);
            var members = Jobs.OfRole(role).ToList();
            var all = members.Count > 0 && members.All(jobIds.Contains);
            var label = char.ToUpperInvariant(Jobs.RoleName(role)[0]) + Jobs.RoleName(role)[1..] + "s";
            if (ImGui.Checkbox(label, ref all))
            {
                jobIds.RemoveAll(members.Contains);
                if (all)
                    jobIds.AddRange(members);
                changed = true;
            }
            foreach (var job in Jobs.All.Where(j => j.Role == role))
            {
                var width = ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X + ImGui.CalcTextSize(job.Abbreviation).X;
                ContinueRowOrWrap(width);
                var on = jobIds.Contains(job.Id);
                if (ImGui.Checkbox($"{job.Abbreviation}##job{job.Id}", ref on))
                {
                    if (on) jobIds.Add(job.Id);
                    else jobIds.Remove(job.Id);
                    changed = true;
                }
            }
            ImGui.PopID();
        }
        return changed;
    }

    /// Strikes, streak and recurrence. Values the current condition can't use are cleared, or validation would refuse them.
    private static bool DrawOathRepeats(Oath oath)
    {
        var changed = false;
        var canStrike = OathConditions.CanStrike(oath.Condition);
        if (!canStrike && (oath.Strikes != 0 || oath.LedgerPerStrike != 0))
        {
            oath.Strikes = 0;
            oath.LedgerPerStrike = 0;
            changed = true;
        }

        Section.SubHeading("Leniency and repeats");
        if (canStrike)
        {
            var strikes = oath.Strikes;
            ItemWidth(100);
            if (ImGui.InputInt("strikes allowed##strikes", ref strikes, 1, 1)) { oath.Strikes = Math.Clamp(strikes, 0, RulebookLimits.MaxStrikes); changed = true; }
            IconGlyph.HelpMarker("Slips before it breaks. With 2, the first two only count as strikes and the third breaks it. 0 breaks it on the first.");
            if (oath.Strikes > 0)
            {
                ImGui.TextUnformatted("each strike");
                ImGui.SameLine();
                changed |= DrawLedgerDelta("perStrikeLedger", oath.LedgerPerStrike, v => oath.LedgerPerStrike = v);
            }
        }

        var recurrence = (int)oath.Recurrence;
        ItemWidth(200);
        if (ImGui.Combo("when it ends##recurrence", ref recurrence, OathRecurrenceNames, OathRecurrenceNames.Length)) { oath.Recurrence = (OathRecurrence)recurrence; changed = true; }
        IconGlyph.HelpMarker("Start again by itself: once kept or broken, a new run opens straight away - they swear to that when they swear to the oath, and can stop it renewing. Offer it again: they're asked again each time.");

        var canStreak = OathConditions.IsRitual(oath.Condition) || oath.Recurrence != OathRecurrence.Once;
        if (!canStreak && (oath.StreakEvery != 0 || oath.StreakLedger != 0 || oath.StreakDrawReward))
        {
            oath.StreakEvery = 0;
            oath.StreakLedger = 0;
            oath.StreakDrawReward = false;
            changed = true;
        }
        if (canStreak)
        {
            var every = oath.StreakEvery;
            ItemWidth(100);
            var unit = OathConditions.IsRitual(oath.Condition) ? $"{OathText.Unit(oath)}s done in a row" : "runs kept in a row";
            if (ImGui.InputInt($"streak: every##streakEvery", ref every, 1, 1))
            {
                oath.StreakEvery = every <= 0 ? 0 : Math.Clamp(every, RulebookLimits.MinStreak, RulebookLimits.MaxStreak);
                changed = true;
            }
            ImGui.SameLine();
            ImGui.TextUnformatted(unit);
            IconGlyph.HelpMarker("A bonus each time the streak reaches this many, then again at twice as many, and so on. A miss or a broken run starts it over. 0 = no streak.");
            if (oath.StreakEvery > 0)
            {
                changed |= DrawLedgerDelta("streakLedger", oath.StreakLedger, v => oath.StreakLedger = v);
                var draw = oath.StreakDrawReward;
                if (ImGui.Checkbox("and draw a reward card##streakDraw", ref draw)) { oath.StreakDrawReward = draw; changed = true; }
            }
        }
        return changed;
    }

    private void DrawPlaceEditor(PlaceRule rule, EditorPart part = EditorPart.All)
    {
        ImGui.PushID(rule.Id);
        var changed = false;
        if (part != EditorPart.Outcomes)
        {
            if (DrawName(rule.Name, out var name)) { rule.Name = name; changed = true; }
            Section.SubHeading("Places");
            changed |= DrawPlaceList("places", rule.Places);
        }
        if (part != EditorPart.Details)
        {
            Section.SubHeading("When they enter");
            changed |= DrawConsequenceEditor("enter", rule.Enter);
            Section.SubHeading("When they leave");
            changed |= DrawConsequenceEditor("leave", rule.Leave);
            ImGui.Spacing();
            var cd = rule.CooldownSeconds;
            if (DrawCooldown("placeCooldown", ref cd, "Entering or leaving again within this time does nothing.")) { rule.CooldownSeconds = cd; changed = true; }
        }
        if (changed) plugin.Configuration.Save();
        if (part == EditorPart.All)
            DrawDoneButton();
        ImGui.PopID();
    }

    private void DrawPresenceEditor(PresenceRule rule, EditorPart part = EditorPart.All)
    {
        ImGui.PushID(rule.Id);
        var changed = false;
        if (part != EditorPart.Outcomes)
        {
            if (DrawName(rule.Name, out var name)) { rule.Name = name; changed = true; }
            var range = rule.RangeYalms;
            ItemWidth(160);
            if (ImGui.SliderFloat("Range (yalms)", ref range, RulebookLimits.MinRangeYalms, RulebookLimits.MaxRangeYalms, "%.0f")) { rule.RangeYalms = range; changed = true; }
            IconGlyph.HelpMarker("How close you have to be. You count as arrived half a second after coming within range, and gone a second after moving a little past it (a quarter of the range more, at least 1 yalm). Each of arrive and leave can only fire once per cooldown.");
            var skipDuties = rule.SkipInDuties;
            if (ImGui.Checkbox("Not in duties", ref skipDuties)) { rule.SkipInDuties = skipDuties; changed = true; }
            IconGlyph.HelpMarker("Does nothing inside dungeons, trials, raids and other instanced content. Being near each other once the duty ends counts as arriving.");
        }
        if (part != EditorPart.Details)
        {
            Section.SubHeading("When you arrive");
            changed |= DrawConsequenceEditor("arrive", rule.Arrive);
            var leash = rule.LeashOnArrive;
            if (ImGui.Checkbox("Leash them to you", ref leash)) { rule.LeashOnArrive = leash; changed = true; }
            IconGlyph.HelpMarker("Needs their Follow permission (and Teleport for the leash to follow you across areas). Their client tells yours, so the leash travels with you like one you sent.");
            if (rule.LeashOnArrive)
            {
                ImGui.SameLine();
                var length = rule.LeashLengthYalms;
                ItemWidth(110);
                if (ImGui.SliderInt("yalms##presenceLeash", ref length, LengthOption.MinYalms, LengthOption.MaxYalms)) { rule.LeashLengthYalms = length; changed = true; }
                ImGui.Indent();
                var pauseInDuties = rule.LeashPauseInDuties;
                if (ImGui.Checkbox("Pause the leash in duties##presenceLeash", ref pauseInDuties)) { rule.LeashPauseInDuties = pauseInDuties; changed = true; }
                IconGlyph.HelpMarker("While your Sub is in a duty, this leash pauses as if you were apart, and picks back up once they leave. Needs your Sub on this version of Oathbound or newer - an older one keeps the leash on.");
                ImGui.Unindent();
            }
            changed |= DrawPresenceTell("arriveTell", rule.ArriveTell, v => rule.ArriveTell = v);
            Section.SubHeading("When you leave");
            changed |= DrawConsequenceEditor("depart", rule.Depart);
            var unleash = rule.UnleashOnDepart;
            if (ImGui.Checkbox("Take the leash off", ref unleash)) { rule.UnleashOnDepart = unleash; changed = true; }
            IconGlyph.HelpMarker("Only a leash to you. While leashed they're pulled along, so this mostly fires when you teleport away or the leash pauses.");
            changed |= DrawPresenceTell("departTell", rule.DepartTell, v => rule.DepartTell = v);
            ImGui.Spacing();
            var cd = rule.CooldownSeconds;
            if (DrawCooldown("presenceCooldown", ref cd, "Arriving or leaving again within this time does nothing.")) { rule.CooldownSeconds = cd; changed = true; }
        }
        if (changed) plugin.Configuration.Save();
        if (part == EditorPart.All)
            DrawDoneButton();
        ImGui.PopID();
    }

    private static bool DrawPresenceTell(string id, string value, Action<string> set)
    {
        ItemWidth(260);
        var changed = ImGui.InputTextWithHint($"they /tell you##{id}", "optional, e.g. I'm here", ref value, RulebookLimits.MaxPresenceTellLength);
        if (changed)
            set(value);
        IconGlyph.HelpMarker("Their client sends this to you as a /tell, nowhere else. Needs their Custom chat messages permission and its acknowledgement.");
        return changed;
    }

    private static bool DrawClock(string label, int minutes, Action<int> set)
    {
        var hours = minutes / 60;
        var mins = minutes % 60;
        var changed = false;
        ImGui.TextUnformatted(label);
        ImGui.SameLine();
        ItemWidth(90);
        if (ImGui.InputInt($"##h{label}", ref hours, 1, 3)) changed = true;
        ImGui.SameLine();
        ImGui.TextUnformatted(":");
        ImGui.SameLine();
        ItemWidth(90);
        if (ImGui.InputInt($"##m{label}", ref mins, 5, 15)) changed = true;
        if (changed)
            set(Math.Clamp(hours, 0, 23) * 60 + Math.Clamp(mins, 0, 59));
        return changed;
    }

    private bool DrawPlaceList(string id, List<PlaceRef> places)
    {
        var changed = false;
        ImGui.PushID(id);
        for (var i = 0; i < places.Count; i++)
        {
            ImGui.PushID(i);
            ImGui.BulletText(RulebookPlaces.Describe(places[i]));
            ContinueRowOrWrap(ButtonWidth("x"));
            if (ImGui.Button("x"))
            {
                places.RemoveAt(i);
                changed = true;
                ImGui.PopID();
                break;
            }
            ImGui.PopID();
        }

        if (ImGui.Button("+ Add place"))
            ImGui.OpenPopup("addPlace");
        // Popups auto-size to their widest item; a long saved label would otherwise stretch it across the screen.
        ImGui.SetNextWindowSizeConstraints(new Vector2(Scaled(320), 0), new Vector2(Scaled(320), float.MaxValue));
        if (ImGui.BeginPopup("addPlace"))
        {
            for (var kind = 1; kind < PlaceKindNames.Length; kind++)
                if (ImGui.Selectable(PlaceKindNames[kind]))
                {
                    places.Add(new PlaceRef { Kind = (PlaceKind)kind });
                    changed = true;
                }
            ImGui.Separator();
            var search = rbPlaceSearch.GetValueOrDefault(id, "");
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputTextWithHint("##search", "Search a specific place", ref search, 60))
                rbPlaceSearch[id] = search;
            if (search.Trim().Length >= 2)
            {
                using (Section.List("placeResults", Scaled(200)))
                {
                    foreach (var (tid, tname) in TerritoryRows.Where(t => t.Name.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase)).Take(60))
                        if (ImGui.Selectable($"{tname}##t{tid}"))
                        {
                            places.Add(new PlaceRef { Kind = PlaceKind.Territory, TerritoryId = tid, Name = tname });
                            changed = true;
                            ImGui.CloseCurrentPopup();
                        }
                }
            }
            else
            {
                ImGui.TextDisabled("Type at least 2 letters.");
            }
            ImGui.EndPopup();
        }
        ImGui.PopID();
        return changed;
    }

    // ---- Consequences ----
}
