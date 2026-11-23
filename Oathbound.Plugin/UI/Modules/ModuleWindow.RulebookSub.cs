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
    /// The pending version whose Decline was pressed, waiting for the Sub to confirm with an optional reason.
    private int? rbDeclineVersion;
    private string rbDeclineReason = "";

    private void DrawSubRulebook(PairingState pairing)
    {
        var config = plugin.Configuration;
        var service = plugin.RulebookService;
        var state = pairing.Rulebook;
        IconGlyph.WrappedDisabled($"Rules {pairing.PeerName} wrote for you. Nothing runs until you accept it; once accepted, only they can change or remove a rule.");

        if (config.RulebookSuspended)
        {
            using (Section.Begin("rbSubSuspended"))
            {
                Status.Line(StatusKind.Caution, "Your rulebooks are paused because panic was triggered. Nothing fires until you resume.");
                if (ImGui.Button("Resume rulebooks"))
                    service.Resume();
            }
        }
        else if (!config.Permissions.Rulebook || !config.RulebookAcknowledged)
        {
            Status.Line(StatusKind.Caution, config.RulebookAcknowledged
                ? "Your Rulebook permission is off, so nothing fires. You can still review what arrives. Turn it on in Settings > Permissions."
                : "Your Rulebook permission is off, so nothing fires. You can still review what arrives. Read its acknowledgement in Settings > ToS, then turn it on in Settings > Permissions.");
            DrawOpenPermissionButton(config.RulebookAcknowledged, "rulebookPerm");
        }

        DrawSubRulebookDelivery(pairing, state);
        service.MarkSeen(pairing);

        if (state.Pending is { } pending)
        {
            using (Section.Begin("rbSubPending", $"New version {pending.Version} to review"))
            {
                var startsOver = pending.ResetCount > state.AppliedResetCount;
                if (startsOver)
                    Status.Line(StatusKind.Caution, $"{pairing.PeerName ?? "Your Owner"} is starting over. Accepting clears your oaths (running ones end with no outcome), your points, switched-off rules and history, and offers every oath again.");
                using (Section.List("rbSubPendingDiff", Scaled(300)))
                    DrawRulebookDiff(startsOver ? null : state.Accepted, pending, startsOver ? new Dictionary<string, OathState>() : state.Oaths, pairing.PeerName ?? "your Owner");
                using (ImRaii.Disabled(plugin.CageService.IsCaged))
                {
                    if (rbDeclineVersion != pending.Version)
                    {
                        if (ImGui.Button("Accept"))
                            service.AcceptPending(pairing);
                        ImGui.SameLine();
                        if (ImGui.Button("Decline"))
                        {
                            rbDeclineVersion = pending.Version;
                            rbDeclineReason = "";
                        }
                    }
                    else
                    {
                        Layout.ItemWidth(Scaled(320));
                        ImGui.InputTextWithHint("##rbDeclineReason", "Reason for your Owner (optional)", ref rbDeclineReason, CommandComposer.DecisionReasonMaxChars);
                        if (ImGui.Button($"Decline v{pending.Version}"))
                        {
                            service.DeclinePending(pairing, rbDeclineReason);
                            rbDeclineVersion = null;
                            rbDeclineReason = "";
                        }
                        ImGui.SameLine();
                        if (ImGui.Button("Cancel##rbDecline"))
                            rbDeclineVersion = null;
                    }
                }
                IconGlyph.HelpMarker(plugin.CageService.IsCaged
                    ? "You can accept or decline this once your cage opens."
                    : "Declining keeps the version you already accepted running. Your Owner is told you declined, with your reason if you give one.");
            }
        }

        if (state.Accepted is not { } accepted)
        {
            if (state.Pending is null)
                IconGlyph.WrappedDisabled("No rulebook yet. When your Owner publishes one, it shows up here.");
            return;
        }

        var offers = state.Oaths.Where(kv => kv.Value.Status == OathStatus.Offered).ToList();
        if (offers.Count > 0)
        {
            using (Section.Begin("rbSubOffers", "Oaths offered to you"))
            {
                foreach (var (id, s) in offers)
                {
                    if (accepted.Oaths.FirstOrDefault(o => o.Id == id) is not { } oath)
                        continue;
                    ImGui.PushID(id);
                    ImGui.TextUnformatted(string.IsNullOrWhiteSpace(oath.Name) ? "Oath" : oath.Name);
                    DrawRuleSummary(RuleSentence.For(oath), OathText.Lines(oath));
                    if (OathConditions.IsRitual(oath.Condition))
                        IconGlyph.WrappedDisabled("Resets at midnight - today counts as the first day.");
                    IconGlyph.WrappedDisabled($"Offer ends {TimeText.Until(s.OfferedUnixSeconds + (long)RulebookService.OfferLifetime.TotalSeconds)}.");
                    if (ImGui.Button("I swear"))
                        service.AcceptOath(pairing, id);
                    ImGui.SameLine();
                    if (ImGui.Button("Decline"))
                        service.DeclineOath(pairing, id);
                    ImGui.Separator();
                    ImGui.PopID();
                }
            }
        }

        DrawToday(pairing, accepted);
        DrawPointsPanel("rbSubPoints", PointsSummary.From(accepted, pairing.PeerName ?? "your Owner"), state.LedgerScore, sub: true);
        DrawSubLadder(pairing, accepted);
        if (ImGui.CollapsingHeader("Your oaths, all rules, the shop and activity###rbSubAll"))
            TwoColumns("rbSubColumns", () => DrawSubRulebookOaths(pairing, state, accepted), () => DrawSubRulebookRules(pairing, state, accepted, service));
    }

    /// Delivery problems in either direction; each stays until the next item gets through.
    private static void DrawSubRulebookDelivery(PairingState pairing, RulebookPairingState state)
    {
        var owner = pairing.PeerName ?? "Your Owner";
        if (state.RulebookDelivery.LastFailureCode is { } failure)
            Status.Line(StatusKind.Error, $"The last rulebook from {owner} couldn't be read: {MailboxStatusText.OwnFailure(failure)}. Their plugin has been told.");
        if (MailboxStatusText.IsProblem(state.RulebookDelivery.PeerSenderCode))
            IconGlyph.WrappedDisabled($"{owner}'s plugin {MailboxStatusText.SenderProblem(state.RulebookDelivery.PeerSenderCode, "rulebook")}.");
        if (state.ReportUploadError is { } error)
            Status.Line(StatusKind.Error, $"Your activity report to {owner} couldn't be sent: {error} It's tried again by itself.");
        if (Relay.MailboxStatusService.Evaluate(state.ReportDelivery, state.NextReportSequence) == Relay.MailboxSentState.Rejected)
            Status.Line(StatusKind.Error, $"{owner}'s plugin couldn't read your last activity report: {MailboxStatusText.Reject(state.ReportDelivery.PeerReceiptCode)}."
                + (Relay.MailboxStatusService.ShouldResend(state.ReportDelivery, state.NextReportSequence) ? " It's sent again by itself." : ""));
    }

    private void DrawSubRulebookOaths(PairingState pairing, RulebookPairingState state, RulebookDocument accepted)
    {
        var open = state.Oaths.Where(kv => kv.Value.Status == OathStatus.Open && kv.Value.Terms is not null).ToList();
        using (Section.Begin("rbSubOpen", "Your oaths"))
        {
            if (open.Count == 0)
                IconGlyph.WrappedDisabled("No open oaths.");
            foreach (var (id, s) in open)
            {
                var terms = s.Terms!;
                ImGui.TextUnformatted(string.IsNullOrWhiteSpace(terms.Name) ? "Oath" : terms.Name);
                IconGlyph.WrappedDisabled($"{OathText.Describe(terms)}. {OathStatusText(s.Status, s.ScopeEndsUnixSeconds)}" +
                    (terms.Scope == OathScope.NextDuty && !s.DutyEntered ? " Starts with your next duty." : ""));
                if (OathConditions.CountsTimes(terms.Condition))
                {
                    var due = RitualCalendar.RepeatEnd(s.PeriodStartUnixSeconds, terms.PeriodDays);
                    var done = s.PeriodCount >= terms.TimesPerPeriod;
                    IconGlyph.WrappedColored(done ? Theme.Ok : Theme.Caution,
                        done ? $"Done for now - next stretch starts {TimeText.Until(due)}." : $"{s.PeriodCount}/{terms.TimesPerPeriod} done - due {TimeText.Until(due)}.");
                }
                if (OathConditions.IsRitual(terms.Condition) && s.ScopeEndsUnixSeconds is { } ends)
                {
                    var period = Math.Max(1, terms.PeriodDays);
                    var days = RitualCalendar.DurationDays(terms.DurationMinutes);
                    var firstDay = RitualCalendar.LocalDate(ends).AddDays(-days);
                    var total = Math.Max(1, (days + period - 1) / period);
                    var current = Math.Clamp((RitualCalendar.LocalDate(s.PeriodStartUnixSeconds) - firstDay).Days / period + 1, 1, total);
                    var unit = OathText.Unit(terms);
                    IconGlyph.WrappedDisabled($"{char.ToUpperInvariant(unit[0])}{unit[1..]} {current} of {total}, resets at midnight. " + (s.MissedPeriods > 0
                        ? $"{s.MissedPeriods} missed, so it ends as broken."
                        : "The kept outcome applies when the oath ends, if none is missed."));
                }
                if (terms.Condition == OathCondition.GreetOwner)
                    DrawPerform(pairing, id, terms);
                DrawOpenOathExtras(pairing, id, s, terms);
            }
        }

        if (accepted.Shop.Count > 0)
            DrawSubShop(pairing, accepted);

        var draws = state.Activity.Where(e => e.Kind == RulebookEventKind.CardDrawn).Reverse().Take(5).ToList();
        if (draws.Count > 0)
        {
            using (Section.Begin("rbSubLedger", "Recent card draws"))
            {
                foreach (var d in draws)
                    IconGlyph.WrappedDisabled($"{TimeText.Ago(d.At)} - {d.Text}");
            }
        }
    }

    private void DrawSubRulebookRules(PairingState pairing, RulebookPairingState state, RulebookDocument accepted, Rulebook.RulebookService service)
    {
        using (Section.Begin("rbSubRules", "Rules"))
        {
            IconGlyph.WrappedDisabled($"Everything in the version you accepted. Only {pairing.PeerName ?? "your Owner"} can change or remove a rule. Your safeword, or turning the Rulebook permission off, pauses all of it.");
            var sentences = RuleSentence.ById(accepted, pairing.PeerName ?? "your Owner");
            var groups = RuleText.All(accepted, pairing.PeerName ?? "your Owner")
                .Where(r => r.Id != DrawOnSettings.RuleId || accepted.DrawOn.Any)
                .GroupBy(r => SubRuleTab(r.Kind))
                .OrderBy(g => Array.IndexOf(SubRuleTabs, g.Key))
                .ToList();
            if (groups.Count > 0 && ImGui.BeginTabBar("rbSubRuleTabs"))
            {
                foreach (var group in groups)
                {
                    // The deck's draw settings aren't a rule of their own, so they don't add to the count.
                    var count = group.Count(r => r.Id != DrawOnSettings.RuleId);
                    if (!ImGui.BeginTabItem(count > 0 ? $"{group.Key} ({count})###rbSubTab{group.Key}" : $"{group.Key}###rbSubTab{group.Key}"))
                        continue;
                    using (Section.List($"rbSubRules{group.Key}", Scaled(300)))
                    {
                        var first = true;
                        foreach (var (id, _, name, _, lines) in group)
                        {
                            if (!first)
                                ImGui.Separator();
                            first = false;
                            ImGui.PushID(id);
                            ImGui.PushTextWrapPos(0);
                            ImGui.TextColored(Theme.AccentHover, name);
                            ImGui.PopTextWrapPos();
                            DrawRuleSummary(sentences.GetValueOrDefault(id, ""), lines);
                            ImGui.PopID();
                        }
                    }
                    ImGui.EndTabItem();
                }
                ImGui.EndTabBar();
            }
        }

        using (Section.Begin("rbSubActivity", "Activity"))
        {
            using (Section.List("rbSubActivityList", Scaled(200)))
            {
                if (state.Activity.Count == 0)
                    IconGlyph.WrappedDisabled("Nothing yet.");
                foreach (var e in Enumerable.Reverse(state.Activity).Take(50))
                    IconGlyph.WrappedDisabled($"{TimeText.Ago(e.At)} - {e.Text}");
            }
            var held = service.Queue.HeldCount(pairing.Id);
            if (held > 0)
                IconGlyph.WrappedDisabled($"{held} consequence(s) waiting until you're out of combat or the cutscene ends.");
        }
    }

    private string? rbPutBackError;
    private StatusMessage? rbShopResult;
    private string? rbShopConfirm;

    /// Strikes, streak, renewal and the per-condition status (grace time, leave) under an open oath.
    private void DrawOpenOathExtras(PairingState pairing, string oathId, OathState s, Oath terms)
    {
        var service = plugin.RulebookService;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        ImGui.PushID("extras" + oathId);
        if (terms.Strikes > 0)
        {
            var left = terms.Strikes - s.StrikesUsed;
            IconGlyph.WrappedColored(left == 0 ? Theme.Caution : Theme.Muted,
                left == 0 ? "No strikes left - the next slip breaks it." : $"{left} of {terms.Strikes} strikes left.");
        }
        if (terms.StreakEvery > 0)
            IconGlyph.WrappedDisabled($"Streak: {s.Streak} in a row (bonus every {terms.StreakEvery}).");

        switch (terms.Condition)
        {
            case OathCondition.KeepItOn:
            {
                var outfitName = pairing.Rulebook.OwnerOutfit?.DesignName;
                if (outfitName is null)
                    IconGlyph.WrappedDisabled($"Nothing to keep on until {pairing.PeerName} puts an outfit on you.");
                else if (s.GraceEndsUnixSeconds is { } graceEnds)
                    Status.Line(StatusKind.Caution, $"{outfitName} came off - put it back on {TimeText.Until(graceEnds)}.");
                else
                    IconGlyph.WrappedDisabled($"Keeping on: {outfitName}.");
                if (outfitName is not null && ImGui.Button("Put it back on"))
                    rbPutBackError = service.PutBackOn(pairing);
                if (rbPutBackError is not null)
                    Status.Line(StatusKind.Error, rbPutBackError);
                break;
            }
            case OathCondition.StayAtSide when s.GraceEndsUnixSeconds is { } sideEnds:
                Status.Line(StatusKind.Caution, $"Too far from {pairing.PeerName} - get back within {terms.RangeYalms:0} yalms {TimeText.Until(sideEnds)}.");
                break;
            case OathCondition.AskBeforeLogoff:
                IconGlyph.WrappedColored(s.LeaveGrantedUntilUnixSeconds > now ? Theme.Ok : Theme.Muted,
                    s.LeaveGrantedUntilUnixSeconds > now
                        ? $"You have leave to log off until {TimeText.Date(s.LeaveGrantedUntilUnixSeconds)}."
                        : $"Ask {pairing.PeerName} before you log off.");
                break;
        }

        if (terms.Recurrence != OathRecurrence.Once)
        {
            if (s.StopRenewing)
                IconGlyph.WrappedDisabled("This is the last run - it won't start again.");
            else
            {
                IconGlyph.WrappedDisabled(terms.Recurrence == OathRecurrence.Renew ? "Starts again by itself when it ends." : "Offered to you again when it ends.");
                if (ImGui.Button("Stop renewing"))
                    service.StopRenewing(pairing, oathId);
                IconGlyph.HelpMarker("This run still counts and is judged as usual; it just won't start again afterwards.");
            }
        }
        ImGui.PopID();
    }

    private void DrawSubShop(PairingState pairing, RulebookDocument accepted)
    {
        var service = plugin.RulebookService;
        using var section = Section.Begin("rbSubShop", "Shop");
        IconGlyph.WrappedDisabled($"Spend your points ({pairing.Rulebook.LedgerScore}). Only you can buy something here.");
        foreach (var item in accepted.Shop)
        {
            ImGui.PushID(item.Id);
            ImGui.TextUnformatted(string.IsNullOrWhiteSpace(item.Name) ? "Item" : item.Name);
            DrawRuleSummary(RuleSentence.For(item), RuleText.Shop(item));
            var blocker = service.BuyBlocker(pairing, item);
            if (rbShopConfirm == item.Id)
            {
                ImGui.TextUnformatted($"Spend {item.Price} points?");
                ImGui.SameLine();
                using (ImRaii.Disabled(blocker is not null))
                {
                    if (ImGui.Button("Yes, buy it"))
                    {
                        rbShopResult = service.Buy(pairing, item.Id) is { } buyError ? StatusMessage.Error(buyError) : StatusMessage.Ok($"Bought \"{item.Name}\".");
                        rbShopConfirm = null;
                    }
                }
                ImGui.SameLine();
                if (ImGui.Button("Cancel"))
                    rbShopConfirm = null;
            }
            else
            {
                using (ImRaii.Disabled(blocker is not null))
                {
                    if (ImGui.Button($"Buy ({item.Price})"))
                        rbShopConfirm = item.Id;
                }
                if (blocker is not null)
                {
                    ImGui.SameLine();
                    ImGui.TextDisabled(blocker);
                }
            }
            ImGui.Separator();
            ImGui.PopID();
        }
        Status.Line(rbShopResult);
    }

    /// The Sub's open oaths in the main window's header, a line each with the action it needs, so the Rulebook doesn't
    /// have to be opened to keep them.
    public void DrawHeaderOaths()
    {
        var config = plugin.Configuration;
        var subPairings = config.Pairings.Where(p => p.IsPaired && p.Direction == PairingDirection.SubSide && p.Rulebook.Accepted is not null).ToList();
        var rows = subPairings
            .SelectMany(p => p.Rulebook.Oaths.Where(kv => kv.Value.Status == OathStatus.Open && kv.Value.Terms is not null).Select(kv => (Pairing: p, Id: kv.Key, State: kv.Value)))
            .ToList();
        if (rows.Count == 0)
            return;

        ImGui.Spacing();
        ImGui.Separator();
        IconGlyph.Text(FontAwesomeIcon.Scroll, "Your oaths");
        if (config.RulebookSuspended)
            Status.Line(StatusKind.Caution, "Paused since panic - resume them in the Rulebook.");
        foreach (var (pairing, id, s) in rows)
        {
            var terms = s.Terms!;
            ImGui.PushID($"headerOath_{pairing.Id}_{id}");
            var name = string.IsNullOrWhiteSpace(terms.Name) ? "Oath" : terms.Name;
            if (subPairings.Count > 1)
                name += $" ({pairing.PeerName})";
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(name);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"{OathText.Describe(terms)}. {OathStatusText(s.Status, s.ScopeEndsUnixSeconds)}");
            if (HeaderOathStatus(pairing, s, terms) is { } status)
            {
                ImGui.SameLine();
                ImGui.TextColored(status.Color, status.Text);
            }

            if (terms.Condition == OathCondition.GreetOwner)
            {
                ImGui.Indent();
                DrawPerform(pairing, id, terms);
                ImGui.Unindent();
            }
            else if (terms.Condition == OathCondition.KeepItOn && s.GraceEndsUnixSeconds is not null && pairing.Rulebook.OwnerOutfit?.DesignName is not null)
            {
                ContinueRowOrWrap(ButtonWidth("Put it back on"));
                if (ImGui.Button("Put it back on"))
                    rbPutBackError = plugin.RulebookService.PutBackOn(pairing);
                if (rbPutBackError is not null)
                    Status.Line(StatusKind.Error, rbPutBackError);
            }
            ImGui.PopID();
        }
    }

    private static (string Text, Vector4 Color)? HeaderOathStatus(PairingState pairing, OathState s, Oath terms)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (terms.Scope == OathScope.NextDuty && !s.DutyEntered)
            return ("starts with your next duty", Theme.TextMuted);
        switch (terms.Condition)
        {
            case OathCondition.KeepItOn when s.GraceEndsUnixSeconds is { } graceEnds:
                return ($"came off - put it back on {TimeText.Until(graceEnds)}", Theme.Caution);
            case OathCondition.StayAtSide when s.GraceEndsUnixSeconds is { } sideEnds:
                return ($"too far - get back {TimeText.Until(sideEnds)}", Theme.Caution);
            case OathCondition.AskBeforeLogoff:
                return s.LeaveGrantedUntilUnixSeconds > now
                    ? ($"leave to log off until {TimeText.Date(s.LeaveGrantedUntilUnixSeconds)}", Theme.Ok)
                    : ($"ask {pairing.PeerName} before you log off", Theme.TextMuted);
        }
        if (OathConditions.CountsTimes(terms.Condition))
        {
            var due = RitualCalendar.RepeatEnd(s.PeriodStartUnixSeconds, terms.PeriodDays);
            return s.PeriodCount >= terms.TimesPerPeriod
                ? ("done for now", Theme.Ok)
                : ($"{s.PeriodCount}/{terms.TimesPerPeriod} - due {TimeText.Until(due)}", Theme.Caution);
        }
        return null;
    }

    private void DrawPerform(PairingState pairing, string oathId, Oath terms)
    {
        var service = plugin.RulebookService;
        ImGui.PushID($"perform_{oathId}");
        if (service.IsPerforming(pairing, oathId))
        {
            var left = service.PerformSecondsLeft is { } seconds ? $" - {Math.Ceiling(seconds):0}s left" : "";
            Status.Line(StatusKind.Caution, $"Performing{left}. Stay put until it ends.");
        }
        else
        {
            var blocker = service.PerformBlocker(pairing, oathId);
            using (ImRaii.Disabled(blocker is not null))
            {
                if (ImGui.Button($"Perform ({OathText.Hold(terms)})"))
                    rbPerformError = service.Perform(pairing, oathId);
            }
            var full = string.IsNullOrEmpty(terms.AnimationId) ? "" : $"\n\nAnimation: {terms.AnimationLabel}";
            IconGlyph.HelpMarker($"Plays {OathText.Greeting(terms)} at {pairing.PeerName} and holds you in place for {OathText.Hold(terms)}. It counts once the hold ends; panic or a stop before then doesn't count.{full}");
            if (blocker is not null && blocker != "Done for now.")
                IconGlyph.WrappedDisabled(blocker);
            if (rbPerformError is not null)
                Status.Line(StatusKind.Error, rbPerformError);
        }
        ImGui.PopID();
    }

    private static void DrawRulebookDiff(RulebookDocument? accepted, RulebookDocument pending, IReadOnlyDictionary<string, OathState> oaths, string owner)
    {
        var before = accepted is null ? new() : RuleText.All(accepted, owner).ToDictionary(r => r.Id);
        var after = RuleText.All(pending, owner).ToDictionary(r => r.Id);
        var sentences = RuleSentence.ById(pending, owner);
        var any = false;
        foreach (var (id, rule) in after)
        {
            if (!before.TryGetValue(id, out var old))
            {
                if (id == DrawOnSettings.RuleId && !pending.DrawOn.Any)
                    continue;
                Status.Line(StatusKind.Ok, $"New {rule.Kind.ToLowerInvariant()}: {rule.Name}");
            }
            else if (old.Summary != rule.Summary || old.Name != rule.Name)
                Status.Line(StatusKind.Caution, $"Changed {rule.Kind.ToLowerInvariant()}: {rule.Name}");
            else
                continue;
            ImGui.PushID(id);
            DrawRuleSummary(sentences.GetValueOrDefault(id, ""), rule.Lines);
            ImGui.PopID();
            any = true;
        }
        foreach (var (id, rule) in before.Where(kv => !after.ContainsKey(kv.Key)))
        {
            IconGlyph.WrappedColored(Theme.TextMuted, $"Removed {rule.Kind.ToLowerInvariant()}: {rule.Name}");
            any = true;
        }
        foreach (var (id, s) in oaths.Where(kv => kv.Value.Status == OathStatus.Open && kv.Value.Terms is not null))
        {
            var running = s.Terms!;
            var name = string.IsNullOrWhiteSpace(running.Name) ? "oath" : running.Name;
            if (pending.Oaths.FirstOrDefault(o => o.Id == id) is not { } next)
                Status.Line(StatusKind.Caution, $"Accepting ends your running oath \"{name}\" with no outcome.");
            else if (!RulebookService.SameTermsIgnoringName(running, next))
                Status.Line(StatusKind.Caution, $"Accepting switches your running oath \"{name}\" to these terms and restarts it.");
            else
                continue;
            any = true;
        }
        if (!any)
            IconGlyph.WrappedDisabled("Nothing changed compared to the version you accepted.");
    }

    private static string OathStatusText(OathStatus status, long? scopeEnds) => status switch
    {
        OathStatus.Offered => "Offered, waiting for an answer.",
        OathStatus.Declined => "Declined.",
        OathStatus.Expired => "Offer expired.",
        OathStatus.Withdrawn => "Withdrawn.",
        OathStatus.Open => scopeEnds is { } ends ? $"Open, ends {TimeText.Until(ends)}." : "Open.",
        OathStatus.Kept => "Kept.",
        OathStatus.Broken => "Broken.",
        OathStatus.Voided => "Voided.",
        _ => "",
    };
}
