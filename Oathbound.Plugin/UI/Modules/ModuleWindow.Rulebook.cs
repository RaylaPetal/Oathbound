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

/// The Rulebook module: the Owner writes and publishes, the Sub reviews, accepts and switches rules off.
public sealed partial class ModuleWindow
{
    public const string RulebookOverviewTab = "Overview";
    public const string RulebookOathsTab = "Oaths";
    public const string RulebookDeckTab = "Deck";
    public const string RulebookPlacesTab = "Places";
    public const string RulebookPresenceTab = "Presence";
    public const string RulebookLedgerTab = "Ledger";
    public const string RulebookScheduleTab = "Schedule";
    public const string RulebookShopTab = "Shop";

    private static readonly (string Group, (OathCondition Condition, string Label, string Help)[] Items)[] OathChoices =
    [
        ("Rituals", [
            (OathCondition.GreetOwner, "Greet me with a gesture", "Use a gesture of your choice on you - /kneel, /bow, /beckon... - a set number of times every day or few days."),
            (OathCondition.MessageOwner, "Message me", "Send you a tell a set number of times every day or few days. Optionally it has to say something, like \"good morning\"."),
            (OathCondition.CheckIn, "Check in", "Log in at least once every day or few days."),
            (OathCondition.DutyQuota, "Complete duties", "Complete a set number of duties - any duty, or the ones you pick - every day or few days. Leaving a duty early doesn't count."),
        ]),
        ("Obedience", [
            (OathCondition.KeepItOn, "Keep my outfit on", "The outfit you last put on them has to stay on. If it comes off they're told, and have a grace time to put it back with one click."),
            (OathCondition.StayAtSide, "Stay at my side", "Whenever you're near each other, they stay within the range you set. Straying past it for longer than the grace time breaks it."),
            (OathCondition.AskBeforeLogoff, "Ask before logging off", "They may only log off soon after you grant them leave with the Grant leave button in the Overview tab."),
        ]),
        ("Manners", [
            (OathCondition.SayGoodnight, "Say goodnight before logging off", "Send you a tell in the 15 minutes before they log off. Optionally it has to say something."),
            (OathCondition.AddressOwner, "Call me by a title", "When they start a conversation with you (their first tell after 15 quiet minutes), it has to include the word you choose, like \"Mistress\" or \"Sir\". Replies after that don't need it."),
            (OathCondition.ForbiddenWord, "Never say a word", "Saying the word in any chat they type in breaks the oath."),
            (OathCondition.QuietInPublic, "Stay quiet in public", "No /say, /shout or /yell."),
        ]),
        ("Places & time", [
            (OathCondition.StayInPlaces, "Stay in certain places", "Entering anywhere else breaks it."),
            (OathCondition.AvoidPlaces, "Stay out of certain places", "Entering one of them breaks it."),
            (OathCondition.Curfew, "Curfew", "Being logged in during the hours you set breaks it."),
        ]),
        ("Duties", [
            (OathCondition.NoDeaths, "Don't die", "Their character dying breaks it."),
            (OathCondition.NoWipes, "Don't wipe", "Their party wiping breaks it."),
            (OathCondition.DutyTimeLimit, "Finish each duty in time", "A duty taking longer than the limit breaks it."),
            (OathCondition.JobLock, "Only play certain jobs", "Entering a duty on any other job breaks it - for example healers only."),
        ]),
    ];
    private static readonly string[] OathRecurrenceNames = ["Once", "Start again by itself", "Offer it again"];
    private static readonly string[] OathScopeNames = ["During their next duty", "For a set time"];
    private string rbEmoteSearch = "";
    /// The oath whose editor switched to "a modded animation" before one was picked.
    private string? rbGreetAnimationMode;
    private string? rbPerformError;
    private static readonly string[] PlaceKindNames = ["A specific place", "Any main city", "Any residential district", "Inside any house or apartment", "Any duty"];
    private static readonly string[] ThresholdDirectionNames = ["At or above", "At or below"];
    private static readonly string[] CardPileNames = ["Punishment", "Reward"];

    private enum ConsequenceKind { Title, Outfit, Animation, Moodle, Restraint, CustomTrigger, Toy, Ledger, DeckDraw, RestraintTimer, UnlockRestraints, UnlockOutfit, RevertAll, Typed, Tighten, Loosen }

    private static readonly (ConsequenceKind Kind, string Label)[] ConsequenceKinds =
    [
        (ConsequenceKind.Title, "Title"), (ConsequenceKind.Outfit, "Outfit"), (ConsequenceKind.Animation, "Animation"),
        (ConsequenceKind.Moodle, "Moodle"), (ConsequenceKind.Restraint, "Restraint"), (ConsequenceKind.CustomTrigger, "Custom Trigger"),
        (ConsequenceKind.Toy, "Toy pattern"), (ConsequenceKind.Ledger, "Points"), (ConsequenceKind.DeckDraw, "Draw a card"),
        (ConsequenceKind.RestraintTimer, "Restraint timer"), (ConsequenceKind.UnlockRestraints, "Unlock restraints"), (ConsequenceKind.UnlockOutfit, "Unlock outfit"),
        (ConsequenceKind.RevertAll, "Revert everything"), (ConsequenceKind.Typed, "Typed command"),
        (ConsequenceKind.Tighten, "Tighten the ladder"), (ConsequenceKind.Loosen, "Loosen the ladder"),
    ];

    private static readonly string[] DeckPileNames = ["Punishment", "Reward"];

    private string? rbEditingId;
    private string? rbRequestedTab;
    private Task<string?>? rbPublishTask;
    private StatusMessage? rbPublishResult;
    private int rbLedgerAward = 1;
    private string rbLedgerReason = "";
    private int lastRulebookTabFrame = -10;
    private readonly Dictionary<string, string> rbPlaceSearch = new();

    // The "+ Add" popup; only one is open at a time, so one set of fields serves every list.
    private ConsequenceKind rbPickKind;
    /// The row being changed; null adds a new one.
    private int? rbPickIndex;
    private int rbAddDeckPile;
    private string rbAddSearch = "";
    private QuickCommand? rbAddPick;
    private int? rbAddLock;
    private int rbAddLedger = 1;
    private int rbAddTimerMinutes = -10;
    private int rbAddToy;
    private string rbAddTyped = "";

    private static List<(uint Id, string Name)>? territoryRows;

    private static List<(uint Id, string Name)> TerritoryRows => territoryRows ??=
        Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>()
            .Select(t => (t.RowId, Name: t.PlaceName.ValueNullable?.Name.ExtractText() ?? ""))
            .Where(t => t.RowId > 0 && t.Name.Length > 0)
            .GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// For tours: opens the module on one of the Owner's tabs.
    public void ShowRulebookTab(string tab)
    {
        rbRequestedTab = tab;
        Show("rulebook");
    }

    private void DrawRulebookModule(bool isOwner)
    {
        var pairing = plugin.Configuration.ActivePairing;
        var direction = isOwner ? PairingDirection.OwnerSide : PairingDirection.SubSide;
        if (pairing is null || pairing.Direction != direction)
        {
            IconGlyph.WrappedDisabled(isOwner
                ? "Pick an Owner-side pairing in the header to write its rulebook."
                : "Pick a Sub-side pairing in the header to see its rulebook.");
            return;
        }

        // Opening the module picks up a waiting rulebook or report right away.
        var frame = ImGui.GetFrameCount();
        if (frame - lastRulebookTabFrame > 2)
            plugin.RulebookService.CheckNow(pairing);
        lastRulebookTabFrame = frame;

        if (isOwner)
            DrawOwnerRulebook(pairing);
        else
            DrawSubRulebook(pairing);
    }

    // ---- Owner ----

    private void DrawOwnerRulebook(PairingState pairing)
    {
        var draft = pairing.Rulebook.Draft;
        DrawPublishBar(pairing);
        if (!ImGui.BeginTabBar("rbOwnerTabs"))
            return;

        if (BeginRulebookTab(RulebookOverviewTab))
        {
            if (ShowTemplateCard(draft))
                DrawTemplateCard(draft);
            DrawPublish(pairing);
            DrawOwnerReport(pairing);
            ImGui.EndTabItem();
        }
        if (BeginRulebookTab(RulebookRulesTab, draft.Oaths.Count + draft.Places.Count + draft.Presence.Count + draft.Times.Count))
        {
            DrawRulesSection(pairing, draft);
            ImGui.EndTabItem();
        }
        if (BeginRulebookTab(RulebookPointsTab, draft.Thresholds.Count))
        {
            DrawPointsSection(pairing, draft);
            ImGui.EndTabItem();
        }
        if (BeginRulebookTab(RulebookRewardsTab, draft.Deck.Count + draft.Shop.Count))
        {
            DrawRewardsSection(draft);
            ImGui.EndTabItem();
        }
        ImGui.EndTabBar();
    }

    private bool BeginRulebookTab(string name, int? count = null)
    {
        var requested = rbRequestedTab is { } r ? SectionFor(r) : null;
        var flags = requested == name ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
        if (requested == name)
            rbRequestedTab = null;
        var label = count is > 0 ? $"{name} ({count})###rbTab{name}" : $"{name}###rbTab{name}";
        return ImGui.BeginTabItem(label, flags);
    }

    private void DrawPublish(PairingState pairing)
    {
        var state = pairing.Rulebook;
        using var section = Section.Begin("rbPublish", "Publish");
        IconGlyph.WrappedDisabled($"Your rules run on {pairing.PeerName}'s own client, only after they accept them, and only with their Rulebook permission on. Edit them in the tabs above.");

        if (state.PublishedVersion > 0)
            IconGlyph.WrappedDisabled($"Version {state.PublishedVersion} sent {TimeText.Ago(state.PublishedAtUnixSeconds)}. A lost copy is sent again by itself.");
        if (state.Draft.RequiredSchemaVersion >= 3)
            Status.Line(StatusKind.Caution, $"A title, outfit or moodle here lasts a set time. {NewSubNote}");

        var sentResets = state.PublishedDocument?.ResetCount ?? 0;
        var startingOver = state.Draft.ResetCount > sentResets;
        if (startingOver)
            Status.Line(StatusKind.Caution, $"Your next send starts over: when {pairing.PeerName} accepts it, their oaths, points, switched-off rules and history are cleared, and every oath is offered again.");
        if (ImGui.Button(startingOver ? "Don't start over" : "Start over"))
        {
            state.Draft.ResetCount = startingOver ? sentResets : sentResets + 1;
            plugin.Configuration.Save();
        }
        IconGlyph.HelpMarker($"Wipes {pairing.PeerName}'s side of this rulebook to a clean slate with your next send. Nothing changes until they accept that version.");
    }

    private void DrawOwnerReport(PairingState pairing)
    {
        var state = pairing.Rulebook;
        using var section = Section.Begin("rbReport", $"What's happening with {pairing.PeerName}");
        var reportDelivery = state.ReportDelivery;
        if (MailboxStatusText.IsProblem(reportDelivery.PeerSenderCode))
            Status.Line(StatusKind.Error, $"{pairing.PeerName}'s reports aren't arriving: their plugin {MailboxStatusText.SenderProblem(reportDelivery.PeerSenderCode, "report")}.");
        if (reportDelivery.LastFailureCode is { } reportFailure)
            Status.Line(StatusKind.Error, $"{pairing.PeerName}'s last report couldn't be read: {MailboxStatusText.OwnFailure(reportFailure)}. Their plugin sends the next one by itself.");

        if (state.LastReport is not { } report)
        {
            IconGlyph.WrappedDisabled("Nothing yet. Once they accept a rulebook, what happens shows up here by itself - usually within half an hour.");
        }
        else
        {
            ImGui.TextUnformatted($"Points: {report.LedgerScore}");
            ImGui.SameLine();
            ImGui.TextDisabled(report.AcceptedVersion == 0 ? "- no version accepted yet" : $"- running version {report.AcceptedVersion}");
            if (report.LadderStage is { } stage)
                IconGlyph.WrappedDisabled(stage == 0 ? "Ladder: free." : $"Ladder: stage {stage}, \"{report.LadderStageName}\".");
            if (report.PendingVersion is { } pending)
                IconGlyph.WrappedDisabled($"Version {pending} is waiting for them to review it.");
            if (state.FreshDecision() is { Accepted: false } decision)
                Status.Line(StatusKind.Caution, decision.Reason is { } why ? $"They declined version {decision.Version}: {why}" : $"They declined version {decision.Version}.");
            else if (state.FreshDecision() is { Accepted: true } acceptedNow)
                IconGlyph.WrappedDisabled($"They accepted version {acceptedNow.Version}; the next report shows it running.");
            else if (report.DeclinedVersion is { } declined && declined > report.AcceptedVersion)
                Status.Line(StatusKind.Caution, $"They declined version {declined}.");
            if (!report.PermissionOn)
                Status.Line(StatusKind.Caution, "Their Rulebook permission is off - nothing fires.");
            if (report.Suspended)
                Status.Line(StatusKind.Caution, "Paused after panic - nothing fires until they resume.");
            if (report.DisabledRuleIds.Count > 0)
            {
                var names = report.DisabledRuleIds.Select(id => state.Draft.AllRules().FirstOrDefault(r => r.Id == id).Name ?? "a removed rule").ToList();
                IconGlyph.WrappedDisabled($"They switched off: {string.Join(", ", names)}.");
            }

            using (Section.List("rbReportEvents", Scaled(160)))
            {
                foreach (var e in Enumerable.Reverse(report.Events).Take(40))
                    IconGlyph.WrappedDisabled($"{TimeText.Ago(e.At)} - {e.Text}");
            }
            ImGui.TextDisabled($"Reported {TimeText.Ago(report.GeneratedAt)}.");
        }

        Section.SubHeading("Right now");
        // The rulebook shown is the active pairing's, so this only blocks while the header switches.
        var notActive = plugin.Configuration.ActivePairing?.Id != pairing.Id ? "Pick this pairing in the header first." : null;
        void RightNow(string id, string label, string command, string? blocked = null) =>
            SendButton.Draw(plugin, new SendSpec(id, label)
            {
                Command = command,
                Pairing = pairing,
                BlockedReason = notActive ?? blocked,
                Custom = () =>
                {
                    plugin.RulebookService.SendOwnerCommand(command);
                    return null;
                },
            });

        RightNow("rbDrawReward", "Draw a reward", CardPiles.DrawCommand(CardPile.Reward));
        ContinueRowOrWrap(ButtonWidth("Draw a punishment"));
        RightNow("rbDrawPunishment", "Draw a punishment", CardPiles.DrawCommand(CardPile.Punishment));

        ItemWidth(90);
        ImGui.InputInt("##rbAward", ref rbLedgerAward, 1, 5);
        rbLedgerAward = Math.Clamp(rbLedgerAward, -RulebookLimits.MaxLedgerChange, RulebookLimits.MaxLedgerChange);
        ContinueRowOrWrap(Scaled(160));
        ItemWidth(160);
        ImGui.InputTextWithHint("##rbAwardReason", "reason (optional)", ref rbLedgerReason, 60);
        ContinueRowOrWrap(ButtonWidth("Penalize"));
        RightNow("rbAward", rbLedgerAward < 0 ? "Penalize" : "Award", LedgerCommandText(rbLedgerAward, rbLedgerReason),
            rbLedgerAward == 0 ? "Set how many points first." : null);
        IconGlyph.HelpMarker("Changes their points. Milestones you set in the Points tab fire when the points reach them.");

        if (state.PublishedDocument?.Ladder is { Stages.Count: > 0 })
        {
            RightNow("rbLadderUp", "Tighten", LadderCommand.UpCommand);
            ImGui.SameLine();
            RightNow("rbLadderDown", "Loosen", LadderCommand.DownCommand);
            ImGui.SameLine();
            RightNow("rbLadderFree", "Free", $"{ConsequenceValidator.LadderWord} {LadderCommand.Set} 0");
            IconGlyph.HelpMarker("Moves their ladder a stage, or takes them off it. Each stage's restraint still needs their Restraints permission.");
        }

        // The words their client knows are the ones in the version it has, not the draft.
        if ((state.PublishedDocument?.Oaths ?? []).Any(o => o.Condition == OathCondition.AskBeforeLogoff))
        {
            SendButton.Draw(plugin, new SendSpec("rbGrantLeave", "Grant leave")
            {
                Pairing = pairing,
                BlockedReason = notActive,
                Help = $"Lets {pairing.PeerName} log off for the time their oath says.",
                Custom = () =>
                {
                    plugin.RulebookService.SendGrantLeave(pairing);
                    return null;
                },
            });
            IconGlyph.HelpMarker($"Lets {pairing.PeerName} log off for the time their oath says.");
        }
    }

    private static string LedgerCommandText(int delta, string reason)
    {
        var text = $"{ConsequenceValidator.LedgerWord} {(delta > 0 ? "+" : "-")}{Math.Abs(delta)}";
        var clean = reason.Replace('"', '\'').Trim();
        return clean.Length > 0 ? $"{text} {clean}" : text;
    }

    // ---- Tabs ----

    private readonly Dictionary<string, ListDetail> rbLists = new();

    /// One tab's rules beside the selected rule's editor. Editors write into the draft directly (Publish is what sends
    /// it), so there's nothing unsaved to guard. `display` sets the list order when it differs from the draft's.
    private void DrawRuleListDetail<T>(string id, List<T> list, Func<T, string> idOf, Func<T, string> nameOf, string kind,
        Func<T, List<(string Label, string Text)>> lines, Action<T> editor, Action? toolbar, string emptyText,
        Func<T, string?>? status = null, Func<T, string?>? group = null, Func<T, string?>? badge = null, IEnumerable<T>? display = null)
    {
        if (!rbLists.TryGetValue(id, out var listDetail))
            rbLists[id] = listDetail = new ListDetail();
        // A rule just made with + New becomes the selection.
        if (rbEditingId is { } editing && list.Any(x => idOf(x) == editing) && listDetail.Selected != editing)
            listDetail.Select(editing);

        string Name(T x) => string.IsNullOrWhiteSpace(nameOf(x)) ? $"(unnamed {kind})" : nameOf(x);
        var items = (display ?? list).Select(x => new ListDetailItem(idOf(x), Name(x), badge?.Invoke(x), Group: group?.Invoke(x))).ToList();
        listDetail.Draw(id, items, toolbar, item =>
            {
                if (item is null || list.FirstOrDefault(x => idOf(x) == item.Key) is not { } rule)
                {
                    IconGlyph.WrappedDisabled(toolbar is null ? $"Choose a {kind}." : $"Choose a {kind}, or make a new one.");
                    return;
                }
                ImGui.PushID(item.Key);
                ImGui.AlignTextToFramePadding();
                ImGui.TextColored(Theme.AccentHover, Name(rule));
                ImGui.SameLine();
                if (ConfirmDelete.Button("delete", Name(rule)))
                {
                    list.Remove(rule);
                    rbEditingId = null;
                    plugin.Configuration.Save();
                    listDetail.Deleted();
                    ImGui.PopID();
                    return;
                }
                DrawRuleSummary(RuleSentence.Of(rule!, "you"), lines(rule));
                if (status?.Invoke(rule) is { } text)
                    IconGlyph.WrappedColored(Theme.AccentHover, text);
                ImGui.Separator();
                editor(rule);
                ImGui.PopID();
            },
            null, item => rbEditingId = item?.Key, emptyText);
    }

    private static readonly (DayOfWeek Day, string Label)[] WeekdayChoices =
        [(DayOfWeek.Monday, "Mon"), (DayOfWeek.Tuesday, "Tue"), (DayOfWeek.Wednesday, "Wed"), (DayOfWeek.Thursday, "Thu"), (DayOfWeek.Friday, "Fri"), (DayOfWeek.Saturday, "Sat"), (DayOfWeek.Sunday, "Sun")];

    private void StartNew<T>(List<T> list, T item, Func<T, string> id)
    {
        list.Add(item);
        rbEditingId = id(item);
        plugin.Configuration.Save();
    }

    private static string? StatusOf(RulebookPairingState state, string oathId) =>
        state.FreshOathDecision(oathId) is { } decided ? OathStatusText(decided, null)
        : state.LastReport?.Oaths.FirstOrDefault(o => o.Id == oathId) is { } reported ? OathStatusText(reported.Status, reported.ScopeEndsAt) : null;

    private static readonly string[] SubRuleTabs = ["Oaths", "Deck", "Places", "Presence", "Schedule", "Shop", "Points"];

    private static string SubRuleTab(string kind) => kind switch
    {
        "Oath" => "Oaths",
        "Card" or "Deck" => "Deck",
        "Place rule" => "Places",
        "Presence rule" => "Presence",
        "Time rule" => "Schedule",
        "Shop item" => "Shop",
        _ => "Points",
    };

    /// Label column on the left, wrapped text on the right, so each part of a rule reads on its own line.
    /// The rule's sentence, with its detailed lines one click away. Callers push the rule's id.
    private static void DrawRuleSummary(string sentence, IReadOnlyList<(string Label, string Text)> lines)
    {
        if (string.IsNullOrWhiteSpace(sentence))
        {
            DrawRuleLines(lines);
            return;
        }
        ImGui.PushTextWrapPos(0);
        ImGui.TextUnformatted(sentence);
        ImGui.PopTextWrapPos();
        if (ImGui.TreeNode("Details##ruleDetails"))
        {
            DrawRuleLines(lines);
            ImGui.TreePop();
        }
    }

    private static void DrawRuleLines(IReadOnlyList<(string Label, string Text)> lines)
    {
        if (!ImGui.BeginTable("##ruleLines", 2))
            return;
        ImGui.TableSetupColumn("label", ImGuiTableColumnFlags.WidthFixed, Scaled(78));
        ImGui.TableSetupColumn("text", ImGuiTableColumnFlags.WidthStretch);
        foreach (var (label, text) in lines)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextColored(Theme.TextMuted, label);
            ImGui.TableNextColumn();
            ImGui.PushTextWrapPos(0);
            ImGui.TextUnformatted(text);
            ImGui.PopTextWrapPos();
        }
        ImGui.EndTable();
    }

    private void DrawRuleRow(string id, string name, string kind, List<(string Label, string Text)> lines, string? status, Action remove)
    {
        ImGui.PushID(id);
        var editing = rbEditingId == id;
        ImGui.PushTextWrapPos(0);
        if (editing)
            ImGui.TextColored(Theme.AccentHover, string.IsNullOrWhiteSpace(name) ? $"(unnamed {kind})" : name);
        else
            ImGui.TextUnformatted(string.IsNullOrWhiteSpace(name) ? $"(unnamed {kind})" : name);
        ImGui.PopTextWrapPos();
        DrawRuleLines(lines);
        if (status is not null)
            IconGlyph.WrappedColored(Theme.AccentHover, status);
        if (ImGui.Button(editing ? "Close" : "Edit"))
            rbEditingId = editing ? null : id;
        ContinueRowOrWrap(ButtonWidth("Delete"));
        if (ConfirmDelete.Button("delete", string.IsNullOrWhiteSpace(name) ? $"(unnamed {kind})" : name))
        {
            remove();
            if (editing) rbEditingId = null;
            plugin.Configuration.Save();
        }
        ImGui.Separator();
        ImGui.PopID();
    }

    // ---- Editors ----

    private static bool DrawName(string current, out string updated)
    {
        updated = current;
        ItemWidth(260);
        return ImGui.InputText("Name##rbName", ref updated, RulebookLimits.MaxNameLength);
    }

    /// Every rule's default cooldown.
    private const int DefaultRuleCooldown = 60;

    /// Advanced settings show when turned on, or when this rule already uses one.
    private bool ShowAdvanced(bool ruleUsesIt) => plugin.Configuration.RulebookAdvanced || ruleUsesIt;

    /// Hidden in Simple mode while it's still the default.
    private bool DrawCooldown(string id, ref int seconds, string help)
    {
        if (!ShowAdvanced(seconds != DefaultRuleCooldown))
            return false;
        ItemWidth(120);
        var changed = ImGui.InputInt($"Cooldown (seconds)##{id}", ref seconds, 30, 300);
        seconds = Math.Max(seconds, RulebookLimits.MinCooldownSeconds);
        IconGlyph.HelpMarker(help);
        return changed;
    }

    private void DrawDoneButton()
    {
        ImGui.Spacing();
        if (ImGui.Button("Done"))
            rbEditingId = null;
    }
}
