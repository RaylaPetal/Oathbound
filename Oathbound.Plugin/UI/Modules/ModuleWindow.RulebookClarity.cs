using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Oathbound.Plugin.Config;
using Oathbound.Plugin.Rulebook;
using static Oathbound.Plugin.UI.Layout;

namespace Oathbound.Plugin.UI;

public sealed partial class ModuleWindow
{
    /// Where points come from and go. `balance` null = no report yet (Owner side).
    private static void DrawPointsPanel(string id, PointsSummary summary, int? balance, bool sub)
    {
        using var section = Section.Begin(id, "Points");
        if (balance is { } b)
            ImGui.TextUnformatted(sub ? $"You have {b} points." : $"Their balance: {b} points (as last reported).");
        else
            IconGlyph.WrappedDisabled("Their balance shows once their plugin reports in.");

        if (summary.Empty)
        {
            IconGlyph.WrappedDisabled(sub ? "This rulebook doesn't use points." : "Nothing here uses points yet. Give or take points as an outcome of a rule, and add shop items to spend them on.");
            return;
        }

        void Lines(string heading, System.Collections.Generic.IReadOnlyList<PointsSummary.Line> lines, Vector4 color)
        {
            if (lines.Count == 0)
                return;
            Section.SubHeading(heading);
            foreach (var l in lines)
            {
                ImGui.TextColored(color, $"{(l.Points > 0 ? "+" : "")}{l.Points}");
                ImGui.SameLine();
                IconGlyph.WrappedDisabled($"{l.Rule} - {l.When}");
            }
        }
        Lines(sub ? "You earn points" : "They earn points", summary.Earn, Theme.Success);
        Lines(sub ? "You lose points" : "They lose points", summary.Lose, Theme.StatusMissing);

        if (summary.Spend.Count > 0)
        {
            Section.SubHeading(sub ? "You can spend them on" : "They can spend them on");
            foreach (var item in summary.Spend)
            {
                var name = string.IsNullOrWhiteSpace(item.Name) ? "Shop item" : item.Name.Trim();
                ImGui.TextColored(Theme.Warning, $"{item.Price}");
                ImGui.SameLine();
                if (balance is { } have)
                    IconGlyph.WrappedDisabled(have >= item.Price ? $"{name} - can afford" : $"{name} - {item.Price - have} more needed");
                else
                    IconGlyph.WrappedDisabled(name);
            }
        }

        if (summary.Thresholds.Count > 0)
        {
            Section.SubHeading("Milestones");
            foreach (var t in summary.Thresholds)
            {
                var distance = balance is { } have
                    ? t.Direction == ThresholdDirection.AtOrAbove
                        ? have >= t.Score ? "reached" : $"{t.Score - have} to go"
                        : have <= t.Score ? "reached" : $"{have - t.Score} above it"
                    : null;
                IconGlyph.WrappedDisabled(RuleSentence.For(t) + (distance is null ? "" : $" ({distance})"));
            }
        }
    }

    private void DrawToday(PairingState pairing, RulebookDocument accepted)
    {
        var today = TodayView.Build(pairing.Rulebook, DateTime.UtcNow);
        using var section = Section.Begin("rbSubToday", "Today");
        var balance = pairing.Rulebook.LedgerScore;
        var affordable = accepted.Shop.Count(i => i.Price <= balance);
        ImGui.TextUnformatted($"Balance: {balance} points");
        if (accepted.Shop.Count > 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(affordable > 0 ? Theme.Success : Theme.TextMuted,
                affordable == 0 ? "- nothing in the shop yet" : $"- {affordable} shop item{(affordable == 1 ? "" : "s")} you can afford");
        }

        if (today.Empty && today.DoneToday.Count == 0)
        {
            IconGlyph.WrappedDisabled("Nothing due and nothing at risk right now.");
            return;
        }
        if (today.Empty)
            IconGlyph.WrappedColored(Theme.Success, "All done for now.");

        void Items(string heading, System.Collections.Generic.IReadOnlyList<TodayView.Item> items, Vector4 color, FontAwesomeIcon icon)
        {
            if (items.Count == 0)
                return;
            Section.SubHeading(heading);
            foreach (var item in items)
            {
                using (ImRaii.PushFont(Plugin.PluginInterface.UiBuilder.FontIcon))
                    ImGui.TextColored(color, icon.ToIconString());
                ImGui.SameLine();
                var when = item.When is { } at ? $" - {Until(at)}" : "";
                var stake = item.Stake is null ? "" : $" ({item.Stake})";
                IconGlyph.WrappedDisabled($"{item.Name}: {item.What}{when}{stake}");
            }
        }
        Items("Due now", today.DueNow, Theme.Warning, FontAwesomeIcon.Circle);
        Items("At risk", today.AtRisk, Theme.StatusMissing, FontAwesomeIcon.ExclamationTriangle);
        if (today.DoneToday.Count > 0)
        {
            Section.SubHeading("Done");
            foreach (var done in today.DoneToday)
            {
                using (ImRaii.PushFont(Plugin.PluginInterface.UiBuilder.FontIcon))
                    ImGui.TextColored(Theme.Success, FontAwesomeIcon.CheckCircle.ToIconString());
                ImGui.SameLine();
                IconGlyph.WrappedDisabled(done);
            }
        }
    }

    /// Rules added, removed or changed since the last send.
    private static int UnpublishedChanges(RulebookPairingState state)
    {
        var draft = RuleText.All(state.Draft, "you").ToDictionary(r => r.Id, r => r.Name + "\n" + r.Summary);
        if (state.PublishedDocument is not { } published)
            return draft.Count(r => r.Key != DrawOnSettings.RuleId);
        var sent = RuleText.All(published, "you").ToDictionary(r => r.Id, r => r.Name + "\n" + r.Summary);
        return draft.Count(r => !sent.TryGetValue(r.Key, out var old) || old != r.Value) + sent.Keys.Count(k => !draft.ContainsKey(k));
    }

    /// Draft -> Sent -> Picked up -> Accepted, with the Publish button.
    private void DrawPublishBar(PairingState pairing)
    {
        var state = pairing.Rulebook;
        var report = state.LastReport;
        var unpublished = state.PublishedDigest != Relay.RulebookMailboxService.DraftDigest(state.Draft);
        var changes = unpublished ? Math.Max(1, UnpublishedChanges(state)) : 0;
        var sent = state.PublishedVersion > 0;
        var pickedUp = sent && state.PickedUpVersion >= state.PublishedVersion;
        var accepted = sent && report?.AcceptedVersion >= state.PublishedVersion;
        var declined = sent && report?.DeclinedVersion is { } d && d >= state.PublishedVersion;

        using var card = Section.Begin("rbPublishBar");
        void Step(string text, bool doneStep, bool warn = false)
        {
            ImGui.TextColored(warn ? Theme.Warning : doneStep ? Theme.Success : Theme.TextMuted, text);
            ImGui.SameLine();
        }
        void Arrow()
        {
            ImGui.TextColored(Theme.TextMuted, "->");
            ImGui.SameLine();
        }
        Step(changes > 0 ? $"Draft: {changes} unpublished change{(changes == 1 ? "" : "s")}" : "Draft: up to date", changes == 0, warn: changes > 0);
        Arrow();
        Step(sent ? $"Sent v{state.PublishedVersion}" : "Not sent yet", sent);
        Arrow();
        Step("Picked up", pickedUp);
        Arrow();
        if (!state.SubSupportsRulebook)
            Step($"{pairing.PeerName}'s plugin can't receive rulebooks yet", false, warn: true);
        else if (declined)
            Step("Declined", false, warn: true);
        else
            Step(accepted ? "Accepted" : "Waiting to be accepted", accepted);
        ImGui.NewLine();

        var invalid = RulebookValidation.Check(state.Draft);
        if (invalid is not null)
            IconGlyph.WrappedColored(Theme.Warning, $"Fix this first: {invalid}");

        if (rbPublishTask is { IsCompleted: true } done)
        {
            rbPublishResult = done.Result ?? $"Sent version {state.PublishedVersion}.";
            if (done.Result is null && rbNudgeOnPublish)
                plugin.RulebookService.SendNudge(pairing);
            rbPublishTask = null;
        }
        var busy = rbPublishTask is { IsCompleted: false };
        using (ImRaii.Disabled(busy || invalid is not null || !unpublished && sent))
        {
            if (ImGui.Button(busy ? "Sending..." : sent ? "Publish changes" : "Publish"))
            {
                rbPublishResult = null;
                rbPublishTask = plugin.RulebookService.PublishAsync(pairing);
            }
        }
        ContinueRowOrWrap(ImGui.GetFrameHeight() + ImGui.CalcTextSize("Tell them now").X + Scaled(24));
        ImGui.Checkbox("Tell them now", ref rbNudgeOnPublish);
        IconGlyph.HelpMarker($"Sends one /tell with only the word \"{Commands.ChatComposer.RulebookNudgeKeyword}\", so {pairing.PeerName}'s plugin picks it up right away instead of within half an hour.");
        if (rbPublishResult is not null)
            IconGlyph.WrappedDisabled(rbPublishResult);
    }
}
