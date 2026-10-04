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

    private static readonly (string Group, (OathCondition Condition, string Label, string Help)[] Items)[] OathChoices =
    [
        ("Rituals", [
            (OathCondition.GreetOwner, "Greet me with a gesture", "Use a gesture of your choice on you - /kneel, /bow, /beckon... - a set number of times every day or few days."),
            (OathCondition.MessageOwner, "Message me", "Send you a tell a set number of times every day or few days. Optionally it has to say something, like \"good morning\"."),
            (OathCondition.CheckIn, "Check in", "Log in at least once every day or few days."),
        ]),
        ("Manners", [
            (OathCondition.SayGoodnight, "Say goodnight before logging off", "Send you a tell in the 15 minutes before they log off. Optionally it has to say something."),
            (OathCondition.AddressOwner, "Call me by a title", "Every tell they send you has to include the word you choose, like \"Mistress\" or \"Sir\"."),
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
        ]),
    ];
    private static readonly string[] OathScopeNames = ["During their next duty", "For a set time"];
    private string rbEmoteSearch = "";
    /// The oath whose editor switched to "a modded animation" before one was picked.
    private string? rbGreetAnimationMode;
    private string? rbPerformError;
    private static readonly string[] PlaceKindNames = ["A specific place", "Any main city", "Any residential district", "Inside any house or apartment", "Any duty"];
    private static readonly string[] ThresholdDirectionNames = ["At or above", "At or below"];
    private static readonly string[] CardPileNames = ["Punishment", "Reward"];

    private enum AddKind { Saved, Ledger, RestraintTimer, UnlockRestraints, RevertAll, Toy, Typed }
    private static readonly string[] AddKindNames =
        ["One of my saved commands", "Change the ledger", "Change the restraint timer", "Unlock restraints", "Revert everything", "Toy pattern", "Type a command"];

    private string? rbEditingId;
    private string? rbRequestedTab;
    private Task<string?>? rbPublishTask;
    private string? rbPublishResult;
    private bool rbNudgeOnPublish = true;
    private int rbLedgerAward = 1;
    private string rbLedgerReason = "";
    private int lastRulebookTabFrame = -10;
    private readonly Dictionary<string, string> rbPlaceSearch = new();

    // The "+ Add" popup; only one is open at a time, so one set of fields serves every list.
    private AddKind rbAddKind;
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
        IconGlyph.Text(FontAwesomeIcon.Book, "Rulebook");
        ImGui.Separator();

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
        if (!ImGui.BeginTabBar("rbOwnerTabs"))
            return;

        if (BeginRulebookTab(RulebookOverviewTab))
        {
            DrawPublish(pairing);
            DrawOwnerReport(pairing);
            ImGui.EndTabItem();
        }
        if (BeginRulebookTab(RulebookOathsTab, draft.Oaths.Count))
        {
            DrawOathsTab(draft);
            ImGui.EndTabItem();
        }
        if (BeginRulebookTab(RulebookDeckTab, draft.Deck.Count))
        {
            DrawDeckTab(draft);
            ImGui.EndTabItem();
        }
        if (BeginRulebookTab(RulebookPlacesTab, draft.Places.Count))
        {
            DrawPlacesTab(draft);
            ImGui.EndTabItem();
        }
        if (BeginRulebookTab(RulebookPresenceTab, draft.Presence.Count))
        {
            DrawPresenceTab(draft);
            ImGui.EndTabItem();
        }
        if (BeginRulebookTab(RulebookLedgerTab, draft.Thresholds.Count))
        {
            DrawLedgerTab(draft);
            ImGui.EndTabItem();
        }
        ImGui.EndTabBar();
    }

    private bool BeginRulebookTab(string name, int? count = null)
    {
        var flags = rbRequestedTab == name ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
        if (rbRequestedTab == name)
            rbRequestedTab = null;
        var label = count is > 0 ? $"{name} ({count})###rbTab{name}" : $"{name}###rbTab{name}";
        return ImGui.BeginTabItem(label, flags);
    }

    private void DrawPublish(PairingState pairing)
    {
        var state = pairing.Rulebook;
        using var section = Section.Begin("rbPublish", "Publish");
        IconGlyph.WrappedDisabled($"Your rules run on {pairing.PeerName}'s own client, only after they accept them, and only with their Rulebook permission on. Edit them in the tabs above.");

        var unpublished = state.PublishedDigest != RulebookMailboxService.DraftDigest(state.Draft);
        if (state.PublishedVersion == 0)
            IconGlyph.WrappedDisabled("Not published yet.");
        else if (unpublished)
            IconGlyph.WrappedColored(Theme.Warning, $"You have changes that {pairing.PeerName} hasn't got yet (version {state.PublishedVersion} sent {Ago(state.PublishedAtUnixSeconds)}).");
        else if (state.PickedUpVersion >= state.PublishedVersion)
            IconGlyph.WrappedColored(Theme.Success, $"Version {state.PublishedVersion} sent {Ago(state.PublishedAtUnixSeconds)}, and {pairing.PeerName}'s plugin has picked it up.");
        else
            IconGlyph.WrappedDisabled($"Version {state.PublishedVersion} sent {Ago(state.PublishedAtUnixSeconds)}. Waiting for {pairing.PeerName}'s plugin to pick it up - it's sent again by itself if it gets lost.");
        if (!state.SubSupportsRulebook)
            IconGlyph.WrappedColored(Theme.Warning, $"{pairing.PeerName}'s plugin hasn't set up rulebooks yet (older version, or it hasn't checked in). Publishing will fail until it has.");

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
        using (ImRaii.Disabled(busy || invalid is not null))
        {
            if (ImGui.Button(busy ? "Sending..." : "Send to " + pairing.PeerName))
            {
                rbPublishResult = null;
                rbPublishTask = plugin.RulebookService.PublishAsync(pairing);
            }
        }
        ContinueRowOrWrap(ImGui.GetFrameHeight() + ImGui.CalcTextSize("Tell them now").X + Scaled(24));
        ImGui.Checkbox("Tell them now", ref rbNudgeOnPublish);
        IconGlyph.HelpMarker($"Sends one /tell with only the word \"{ChatComposer.RulebookNudgeKeyword}\", so {pairing.PeerName}'s plugin picks it up right away instead of within half an hour.");
        if (rbPublishResult is not null)
            IconGlyph.WrappedDisabled(rbPublishResult);

        var sentResets = state.PublishedDocument?.ResetCount ?? 0;
        var startingOver = state.Draft.ResetCount > sentResets;
        if (startingOver)
            IconGlyph.WrappedColored(Theme.Warning, $"Your next send starts over: when {pairing.PeerName} accepts it, their oaths, ledger score, switched-off rules and history are cleared, and every oath is offered again.");
        if (ImGui.SmallButton(startingOver ? "Don't start over" : "Start over"))
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

        if (state.LastReport is not { } report)
        {
            IconGlyph.WrappedDisabled("Nothing yet. Once they accept a rulebook, what happens shows up here by itself - usually within half an hour.");
        }
        else
        {
            ImGui.TextUnformatted($"Ledger: {report.LedgerScore}");
            ImGui.SameLine();
            ImGui.TextDisabled(report.AcceptedVersion == 0 ? "- no version accepted yet" : $"- running version {report.AcceptedVersion}");
            if (report.PendingVersion is { } pending)
                IconGlyph.WrappedDisabled($"Version {pending} is waiting for them to review it.");
            if (report.DeclinedVersion is { } declined && declined > report.AcceptedVersion)
                IconGlyph.WrappedColored(Theme.Warning, $"They declined version {declined}.");
            if (!report.PermissionOn)
                IconGlyph.WrappedColored(Theme.Warning, "Their Rulebook permission is off - nothing fires.");
            if (report.Suspended)
                IconGlyph.WrappedColored(Theme.Warning, "Paused after panic - nothing fires until they resume.");
            if (report.DisabledRuleIds.Count > 0)
            {
                var names = report.DisabledRuleIds.Select(id => state.Draft.AllRules().FirstOrDefault(r => r.Id == id).Name ?? "a removed rule").ToList();
                IconGlyph.WrappedDisabled($"They switched off: {string.Join(", ", names)}.");
            }

            using (Section.List("rbReportEvents", Scaled(160)))
            {
                foreach (var e in Enumerable.Reverse(report.Events).Take(40))
                    IconGlyph.WrappedDisabled($"{Ago(e.At)} - {e.Text}");
            }
            ImGui.TextDisabled($"Reported {Ago(report.GeneratedAt)}.");
        }

        Section.SubHeading("Right now");
        using (ImRaii.Disabled(plugin.Configuration.ActivePairing?.Id != pairing.Id))
        {
            if (ImGui.SmallButton("Draw a reward"))
                plugin.RulebookService.SendOwnerCommand(CardPiles.DrawCommand(CardPile.Reward));
            ContinueRowOrWrap(ButtonWidth("Draw a punishment"));
            if (ImGui.SmallButton("Draw a punishment"))
                plugin.RulebookService.SendOwnerCommand(CardPiles.DrawCommand(CardPile.Punishment));

            ItemWidth(90);
            ImGui.InputInt("##rbAward", ref rbLedgerAward, 1, 5);
            rbLedgerAward = Math.Clamp(rbLedgerAward, -RulebookLimits.MaxLedgerChange, RulebookLimits.MaxLedgerChange);
            ContinueRowOrWrap(Scaled(160));
            ItemWidth(160);
            ImGui.InputTextWithHint("##rbAwardReason", "reason (optional)", ref rbLedgerReason, 60);
            ContinueRowOrWrap(ButtonWidth("Penalize"));
            using (ImRaii.Disabled(rbLedgerAward == 0))
            {
                if (ImGui.SmallButton(rbLedgerAward < 0 ? "Penalize" : "Award"))
                    plugin.RulebookService.SendOwnerCommand(LedgerCommandText(rbLedgerAward, rbLedgerReason));
            }
            IconGlyph.HelpMarker("Changes their ledger score. Thresholds you set in the Ledger tab fire when it crosses them.");
        }
    }

    private static string LedgerCommandText(int delta, string reason)
    {
        var text = $"{ConsequenceValidator.LedgerWord} {(delta > 0 ? "+" : "-")}{Math.Abs(delta)}";
        var clean = reason.Replace('"', '\'').Trim();
        return clean.Length > 0 ? $"{text} {clean}" : text;
    }

    // ---- Tabs ----

    private void DrawOathsTab(RulebookDocument draft)
    {
        var state = plugin.Configuration.ActivePairing!.Rulebook;
        using (Section.Begin("rbOaths", "Oaths"))
        {
            IconGlyph.WrappedDisabled("Something your Sub swears to. They say yes to each oath on its own. Keeping or breaking it runs what you set.");
            if (draft.Oaths.Count == 0)
                IconGlyph.WrappedDisabled("No oaths yet.");
            foreach (var oath in draft.Oaths.ToList())
                DrawRuleRow(oath.Id, oath.Name, "oath", RuleText.Oath(oath), StatusOf(state, oath.Id), () => draft.Oaths.Remove(oath));
            if (ImGui.SmallButton("New oath"))
                StartNew(draft.Oaths, new Oath { Name = "Greet me every day", Condition = OathCondition.GreetOwner, Scope = OathScope.ForATime, DurationMinutes = 7 * 1440 }, o => o.Id);
        }
        if (draft.Oaths.FirstOrDefault(o => o.Id == rbEditingId) is { } editing)
            using (Section.Begin("rbEditor", "Edit oath"))
                DrawOathEditor(editing);
    }

    private void DrawDeckTab(RulebookDocument draft)
    {
        var config = plugin.Configuration;
        using (Section.Begin("rbDeckDraws", "Draw automatically"))
        {
            IconGlyph.WrappedDisabled("These apply to the whole deck. Bad things draw from your punishments, good things from your rewards.");
            var d = draft.DrawOn;
            var changed = false;
            ImGui.TextUnformatted("A punishment when:");
            var b = d.OathBroken; if (ImGui.Checkbox("an oath is broken", ref b)) { d.OathBroken = b; changed = true; }
            var w = d.Wipe; if (ImGui.Checkbox("the party wipes", ref w)) { d.Wipe = w; changed = true; }
            var de = d.Death; if (ImGui.Checkbox("their character dies", ref de)) { d.Death = de; changed = true; }
            ImGui.TextUnformatted("A reward when:");
            var k = d.OathKept; if (ImGui.Checkbox("an oath is kept", ref k)) { d.OathKept = k; changed = true; }
            var cd = d.CooldownSeconds;
            if (DrawCooldown("drawOnCooldown", ref cd, "At most one automatic draw in this many seconds.")) { d.CooldownSeconds = cd; changed = true; }
            if (changed) config.Save();
        }

        using (Section.Begin("rbDeck", $"Cards ({draft.Deck.Count}/{RulebookLimits.MaxDeckCards})"))
        {
            IconGlyph.WrappedDisabled("A draw picks one card from its pile at random. You can also draw by hand from the Overview tab.");
            foreach (var pile in new[] { CardPile.Punishment, CardPile.Reward })
            {
                var cards = draft.Deck.Where(c => c.Pile == pile).ToList();
                Section.SubHeading(pile == CardPile.Punishment ? "Punishments" : "Rewards");
                if (cards.Count == 0)
                    IconGlyph.WrappedDisabled("None yet - a draw from this pile does nothing.");
                var total = cards.Sum(c => c.Weight);
                foreach (var card in cards)
                {
                    var chance = total > 0 ? card.Weight * 100f / total : 0;
                    DrawRuleRow(card.Id, card.Name, "card",
                        [("Does", RuleText.Cap(ConsequenceText.Describe(card.Consequence))), ("Chance", $"{chance:0}%{(card.Once ? ", only once" : "")}")],
                        null, () => draft.Deck.Remove(card));
                }
                using (ImRaii.Disabled(draft.Deck.Count >= RulebookLimits.MaxDeckCards))
                {
                    var label = pile == CardPile.Punishment ? "New punishment" : "New reward";
                    if (ImGui.SmallButton(label))
                        StartNew(draft.Deck, new DeckCard { Name = label[4..], Pile = pile }, c => c.Id);
                }
            }
        }
        if (draft.Deck.FirstOrDefault(c => c.Id == rbEditingId) is { } editing)
            using (Section.Begin("rbEditor", "Edit card"))
                DrawCardEditor(draft, editing);
    }

    private void DrawPlacesTab(RulebookDocument draft)
    {
        using (Section.Begin("rbPlaces", "Places"))
        {
            IconGlyph.WrappedDisabled("Run something when your Sub enters or leaves a place.");
            if (draft.Places.Count == 0)
                IconGlyph.WrappedDisabled("No place rules yet.");
            foreach (var rule in draft.Places.ToList())
                DrawRuleRow(rule.Id, rule.Name, "place rule", RuleText.Place(rule), null, () => draft.Places.Remove(rule));
            if (ImGui.SmallButton("New place rule"))
                StartNew(draft.Places, new PlaceRule { Name = "In a city", Places = { new PlaceRef { Kind = PlaceKind.MainCity } } }, r => r.Id);
        }
        if (draft.Places.FirstOrDefault(r => r.Id == rbEditingId) is { } editing)
            using (Section.Begin("rbEditor", "Edit place rule"))
                DrawPlaceEditor(editing);
    }

    private void DrawPresenceTab(RulebookDocument draft)
    {
        using (Section.Begin("rbPresence", "Presence"))
        {
            IconGlyph.WrappedDisabled("Run something when your character comes near your Sub, or leaves.");
            if (draft.Presence.Count == 0)
                IconGlyph.WrappedDisabled("No presence rules yet.");
            foreach (var rule in draft.Presence.ToList())
                DrawRuleRow(rule.Id, rule.Name, "presence rule", RuleText.Presence(rule, "you"), null, () => draft.Presence.Remove(rule));
            if (ImGui.SmallButton("New presence rule"))
                StartNew(draft.Presence, new PresenceRule { Name = "When I arrive" }, r => r.Id);
        }
        if (draft.Presence.FirstOrDefault(r => r.Id == rbEditingId) is { } editing)
            using (Section.Begin("rbEditor", "Edit presence rule"))
                DrawPresenceEditor(editing);
    }

    private void DrawLedgerTab(RulebookDocument draft)
    {
        using (Section.Begin("rbLedger", "Ledger thresholds"))
        {
            IconGlyph.WrappedDisabled("The ledger is a running score. Oaths and your Award/Penalize buttons move it; a threshold runs something when the score reaches it.");
            if (draft.Thresholds.Count == 0)
                IconGlyph.WrappedDisabled("No thresholds yet.");
            foreach (var t in draft.Thresholds.ToList())
                DrawRuleRow(t.Id, t.Name, "threshold", RuleText.Threshold(t), null, () => draft.Thresholds.Remove(t));
            using (ImRaii.Disabled(draft.Thresholds.Count >= RulebookLimits.MaxThresholds))
            {
                if (ImGui.SmallButton("New threshold"))
                    StartNew(draft.Thresholds, new LedgerThreshold { Name = "Too many demerits", Score = -5, Direction = ThresholdDirection.AtOrBelow, DrawCard = true, ResetTo = 0 }, t => t.Id);
            }
        }
        if (draft.Thresholds.FirstOrDefault(t => t.Id == rbEditingId) is { } editing)
            using (Section.Begin("rbEditor", "Edit threshold"))
                DrawThresholdEditor(editing);
    }

    private void StartNew<T>(List<T> list, T item, Func<T, string> id)
    {
        list.Add(item);
        rbEditingId = id(item);
        plugin.Configuration.Save();
    }

    private static string? StatusOf(RulebookPairingState state, string oathId) =>
        state.LastReport?.Oaths.FirstOrDefault(o => o.Id == oathId) is { } reported ? OathStatusText(reported.Status, reported.ScopeEndsAt) : null;

    private static readonly string[] SubRuleTabs = ["Oaths", "Deck", "Places", "Presence", "Ledger"];

    private static string SubRuleTab(string kind) => kind switch
    {
        "Oath" => "Oaths",
        "Card" or "Deck" => "Deck",
        "Place rule" => "Places",
        "Presence rule" => "Presence",
        _ => "Ledger",
    };

    /// Label column on the left, wrapped text on the right, so each part of a rule reads on its own line.
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
        if (ImGui.SmallButton(editing ? "Close" : "Edit"))
            rbEditingId = editing ? null : id;
        ContinueRowOrWrap(ButtonWidth("Delete"));
        if (ImGui.SmallButton("Delete"))
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

    private static bool DrawCooldown(string id, ref int seconds, string help)
    {
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

    private void DrawOathEditor(Oath oath)
    {
        ImGui.PushID(oath.Id);
        var changed = false;
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
                _ => ("has to say (optional)", "e.g. good morning"),
            };
            ItemWidth(200);
            if (ImGui.InputTextWithHint(label, hint, ref phrase, RulebookLimits.MaxPhraseLength)) { oath.Phrase = phrase; changed = true; }
            if (condition is OathCondition.AddressOwner or OathCondition.ForbiddenWord or OathCondition.MessageOwner or OathCondition.SayGoodnight)
                IconGlyph.HelpMarker("Not case-sensitive, and it can be anywhere in the message.");
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
                    ImGui.TextUnformatted(string.IsNullOrEmpty(oath.AnimationLabel) ? "No animation picked." : oath.AnimationLabel);
                    ImGui.SameLine();
                    if (ImGui.SmallButton("Pick animation"))
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

        Section.SubHeading("If they keep it");
        changed |= DrawLedgerDelta("keptLedger", oath.KeptLedger, v => oath.KeptLedger = v);
        changed |= DrawConsequenceEditor("kept", oath.Kept);
        Section.SubHeading("If they break it");
        changed |= DrawLedgerDelta("brokenLedger", oath.BrokenLedger, v => oath.BrokenLedger = v);
        changed |= DrawConsequenceEditor("broken", oath.Broken);
        if (OathConditions.IsRitual(condition))
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

        ImGui.Spacing();
        if (ImGui.SmallButton("Offer again"))
        {
            // A new id is a new offer; the old one keeps whatever state the Sub's client gave it.
            oath.Id = RuleIds.New();
            rbEditingId = oath.Id;
            changed = true;
        }
        IconGlyph.HelpMarker("After they've kept, broken or declined it: offer the same oath again in your next send.");
        if (changed) plugin.Configuration.Save();
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

    private void DrawCardEditor(RulebookDocument draft, DeckCard card)
    {
        ImGui.PushID(card.Id);
        var changed = false;
        if (DrawName(card.Name, out var name)) { card.Name = name; changed = true; }

        var pile = (int)card.Pile;
        ItemWidth(160);
        if (ImGui.Combo("Pile##card", ref pile, CardPileNames, CardPileNames.Length)) { card.Pile = (CardPile)pile; changed = true; }
        IconGlyph.HelpMarker("Punishments come up when something bad happens, rewards when something good does.");

        var weight = card.Weight;
        ItemWidth(120);
        if (ImGui.InputInt("Weight##card", ref weight, 1, 5)) { card.Weight = Math.Clamp(weight, RulebookLimits.MinCardWeight, RulebookLimits.MaxCardWeight); changed = true; }
        var total = draft.Deck.Where(c => c.Pile == card.Pile).Sum(c => c.Weight);
        ImGui.SameLine();
        ImGui.TextDisabled($"= {(total > 0 ? card.Weight * 100f / total : 0):0}% of {(card.Pile == CardPile.Reward ? "reward" : "punishment")} draws");
        IconGlyph.HelpMarker("Higher weight comes up more often. A weight 3 card comes up three times as often as a weight 1 card.");

        var once = card.Once;
        if (ImGui.Checkbox("Only once##card", ref once)) { card.Once = once; changed = true; }
        IconGlyph.HelpMarker("Leaves the pile after it's drawn, until you send the rulebook again.");

        Section.SubHeading("Does");
        changed |= DrawConsequenceEditor("does", card.Consequence);
        if (changed) plugin.Configuration.Save();
        DrawDoneButton();
        ImGui.PopID();
    }

    private void DrawPlaceEditor(PlaceRule rule)
    {
        ImGui.PushID(rule.Id);
        var changed = false;
        if (DrawName(rule.Name, out var name)) { rule.Name = name; changed = true; }
        Section.SubHeading("Places");
        changed |= DrawPlaceList("places", rule.Places);
        Section.SubHeading("When they enter");
        changed |= DrawConsequenceEditor("enter", rule.Enter);
        Section.SubHeading("When they leave");
        changed |= DrawConsequenceEditor("leave", rule.Leave);
        ImGui.Spacing();
        var cd = rule.CooldownSeconds;
        if (DrawCooldown("placeCooldown", ref cd, "Entering or leaving again within this time does nothing.")) { rule.CooldownSeconds = cd; changed = true; }
        if (changed) plugin.Configuration.Save();
        DrawDoneButton();
        ImGui.PopID();
    }

    private void DrawPresenceEditor(PresenceRule rule)
    {
        ImGui.PushID(rule.Id);
        var changed = false;
        if (DrawName(rule.Name, out var name)) { rule.Name = name; changed = true; }
        var range = rule.RangeYalms;
        ItemWidth(160);
        if (ImGui.SliderFloat("Range (yalms)", ref range, RulebookLimits.MinRangeYalms, RulebookLimits.MaxRangeYalms, "%.0f")) { rule.RangeYalms = range; changed = true; }
        IconGlyph.HelpMarker("How close you have to be. You count as arrived half a second after coming within range, and gone a second after moving a little past it (a quarter of the range more, at least 1 yalm). Each of arrive and leave can only fire once per cooldown.");
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
        if (changed) plugin.Configuration.Save();
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

    private void DrawThresholdEditor(LedgerThreshold t)
    {
        ImGui.PushID(t.Id);
        var changed = false;
        if (DrawName(t.Name, out var name)) { t.Name = name; changed = true; }

        ImGui.TextUnformatted("When the score is");
        var direction = (int)t.Direction;
        ItemWidth(130);
        if (ImGui.Combo("##direction", ref direction, ThresholdDirectionNames, ThresholdDirectionNames.Length)) { t.Direction = (ThresholdDirection)direction; changed = true; }
        ImGui.SameLine();
        var score = t.Score;
        ItemWidth(110);
        if (ImGui.InputInt("##score", ref score, 1, 5)) { t.Score = Math.Clamp(score, RulebookLimits.MinLedger, RulebookLimits.MaxLedger); changed = true; }

        Section.SubHeading("Do");
        changed |= DrawConsequenceEditor("does", t.Consequence);
        var draw = t.DrawCard;
        if (ImGui.Checkbox($"Also draw a {CardPiles.Word(t.Pile)} card", ref draw)) { t.DrawCard = draw; changed = true; }
        IconGlyph.HelpMarker("At or above draws a reward, at or below draws a punishment.");
        var reset = t.ResetTo is not null;
        if (ImGui.Checkbox("Then reset the score", ref reset)) { t.ResetTo = reset ? 0 : null; changed = true; }
        if (t.ResetTo is { } to)
        {
            ImGui.SameLine();
            ItemWidth(110);
            if (ImGui.InputInt("to##reset", ref to, 1, 5)) { t.ResetTo = Math.Clamp(to, RulebookLimits.MinLedger, RulebookLimits.MaxLedger); changed = true; }
        }
        IconGlyph.HelpMarker("Without a reset, it fires once and fires again only after the score has left the threshold and come back.");
        ImGui.Spacing();
        var cd = t.CooldownSeconds;
        if (DrawCooldown("thresholdCooldown", ref cd, "It won't fire again within this time.")) { t.CooldownSeconds = cd; changed = true; }
        if (changed) plugin.Configuration.Save();
        DrawDoneButton();
        ImGui.PopID();
    }

    private static bool DrawLedgerDelta(string id, int value, Action<int> set)
    {
        ItemWidth(110);
        if (!ImGui.InputInt($"ledger##{id}", ref value, 1, 5))
            return false;
        set(Math.Clamp(value, -RulebookLimits.MaxLedgerChange, RulebookLimits.MaxLedgerChange));
        return true;
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
            if (ImGui.SmallButton("x"))
            {
                places.RemoveAt(i);
                changed = true;
                ImGui.PopID();
                break;
            }
            ImGui.PopID();
        }

        if (ImGui.SmallButton("+ Add place"))
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

    /// A list of what happens, plus one "+ Add" popup. Only categories a rulebook may use are offered.
    private bool DrawConsequenceEditor(string id, List<string> commands)
    {
        var changed = false;
        ImGui.PushID("cons" + id);
        if (commands.Count == 0)
            ImGui.TextDisabled("Nothing yet.");
        for (var i = 0; i < commands.Count; i++)
        {
            ImGui.PushID(i);
            ImGui.Bullet();
            ImGui.SameLine();
            ImGui.PushTextWrapPos(ImGui.GetContentRegionMax().X - ButtonWidth("x") - ImGui.GetStyle().ItemSpacing.X);
            ImGui.TextUnformatted(ConsequenceText.Describe(commands[i]));
            ImGui.PopTextWrapPos();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(commands[i]);
            ImGui.SameLine();
            if (ImGui.SmallButton("x"))
            {
                commands.RemoveAt(i);
                changed = true;
                ImGui.PopID();
                break;
            }
            ImGui.PopID();
        }

        if (ImGui.SmallButton("+ Add"))
        {
            rbAddKind = AddKind.Saved;
            rbAddSearch = "";
            rbAddPick = null;
            rbAddLock = null;
            rbAddTyped = "";
            ImGui.OpenPopup("add");
        }
        // Popups auto-size to their widest item; a long saved label would otherwise stretch it across the screen.
        ImGui.SetNextWindowSizeConstraints(new Vector2(Scaled(440), 0), new Vector2(Scaled(440), float.MaxValue));
        if (ImGui.BeginPopup("add"))
        {
            if (DrawAddPopup() is { } command)
            {
                commands.Add(command);
                changed = true;
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }
        ImGui.PopID();
        return changed;
    }

    /// Returns the command to add once the Owner clicks Add.
    private string? DrawAddPopup()
    {
        var kind = (int)rbAddKind;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.Combo("##kind", ref kind, AddKindNames, AddKindNames.Length))
            rbAddKind = (AddKind)kind;
        ImGui.Spacing();

        string? command = null;
        string? error = null;
        switch (rbAddKind)
        {
            case AddKind.Saved:
                command = DrawSavedPicker(out error);
                break;
            case AddKind.Ledger:
                ItemWidth(120);
                ImGui.InputInt("points", ref rbAddLedger, 1, 5);
                rbAddLedger = Math.Clamp(rbAddLedger, -RulebookLimits.MaxLedgerChange, RulebookLimits.MaxLedgerChange);
                ImGui.TextDisabled("Positive adds merits, negative adds demerits.");
                command = rbAddLedger == 0 ? null : LedgerCommandText(rbAddLedger, "");
                break;
            case AddKind.RestraintTimer:
                ItemWidth(120);
                ImGui.InputInt("minutes", ref rbAddTimerMinutes, 5, 30);
                rbAddTimerMinutes = Math.Clamp(rbAddTimerMinutes, -RestraintCommand.MaxTimerAdjustSeconds / 60, RestraintCommand.MaxTimerAdjustSeconds / 60);
                ImGui.TextDisabled("Negative takes time off. Only changes restraints that are on a timer.");
                command = rbAddTimerMinutes == 0 ? null : $"restraint timer {(rbAddTimerMinutes > 0 ? "+" : "-")}{Math.Abs(rbAddTimerMinutes) * 60}";
                break;
            case AddKind.UnlockRestraints:
                ImGui.TextDisabled("Takes off every restraint and its lock.");
                command = "restraint unlock";
                break;
            case AddKind.RevertAll:
                ImGui.TextDisabled("Takes off everything you can command, except the collar.");
                command = "revert all";
                break;
            case AddKind.Toy:
                var patterns = ToyControlCommand.BuiltInPatternNames.ToArray();
                rbAddToy = Math.Clamp(rbAddToy, 0, patterns.Length - 1);
                ItemWidth(200);
                ImGui.Combo("pattern", ref rbAddToy, patterns, patterns.Length);
                command = ToyControlCommand.BuildPatternCommand(patterns[rbAddToy]);
                break;
            case AddKind.Typed:
                ImGui.SetNextItemWidth(-1);
                ImGui.InputTextWithHint("##typed", "e.g. moodle apply \"Blushing\"", ref rbAddTyped, CommandSelector.MaxCommandLength);
                if (rbAddTyped.Trim().Length > 0)
                {
                    error = ConsequenceValidator.Check(rbAddTyped);
                    command = error is null ? rbAddTyped.Trim() : null;
                }
                break;
        }

        if (error is not null)
            IconGlyph.WrappedColored(Theme.Warning, error);
        if (command is not null && rbAddKind != AddKind.Typed)
            ImGui.TextDisabled($"Will: {ConsequenceText.Describe(command)}");

        ImGui.Spacing();
        string? result = null;
        using (ImRaii.Disabled(command is null))
        {
            if (ImGui.Button("Add"))
                result = command;
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
            ImGui.CloseCurrentPopup();
        return result;
    }

    private string? DrawSavedPicker(out string? error)
    {
        error = null;
        var config = plugin.Configuration;
        var choices = SavedConsequenceChoices();
        if (choices.Count == 0)
        {
            IconGlyph.WrappedDisabled("Nothing saved yet. Commands you save in the Title, Outfit, Animation, Moodles, Restraints and Custom Triggers modules show up here.");
            return null;
        }

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##search", "Search", ref rbAddSearch, 64);
        var filter = rbAddSearch.Trim();
        using (Section.List("savedChoices", Scaled(220)))
        {
            var any = false;
            foreach (var group in choices.GroupBy(c => c.Kind))
            {
                var rows = group.Where(c => filter.Length == 0 || c.Display.Contains(filter, StringComparison.OrdinalIgnoreCase) || c.Cmd.Label.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
                if (rows.Count == 0)
                    continue;
                any = true;
                ImGui.TextColored(Theme.AccentHover, group.Key);
                foreach (var (_, display, cmd) in rows)
                {
                    if (ImGui.Selectable($"{display}##{group.Key}{cmd.Command}", ReferenceEquals(rbAddPick, cmd)))
                    {
                        rbAddPick = cmd;
                        rbAddLock = cmd.LockSeconds;
                    }
                    if (ImGui.IsItemHovered() && display != cmd.Label)
                        ImGui.SetTooltip(cmd.Label);
                }
            }
            if (!any)
                ImGui.TextDisabled("Nothing matches.");
        }

        if (rbAddPick is not { } pick)
            return null;
        if (OwnerLockOption.Accepts(pick.Command))
            OwnerLockOption.Draw("rbAdd", ref rbAddLock, OwnerLockOption.IsGesture(pick.Command));
        return OwnerLockOption.Apply(OwnerMoodleOverride.ForSend(config, pick), rbAddLock);
    }

    /// Animation labels repeat the mod name; it's already the group a Sub would recognize, so it's trimmed here.
    private List<(string Kind, string Display, QuickCommand Cmd)> SavedConsequenceChoices()
    {
        var q = plugin.Configuration.QuickCommands;
        var list = new List<(string, string, QuickCommand)>();
        void Add(string kind, IEnumerable<QuickCommand> commands, Func<QuickCommand, string>? display = null)
        {
            foreach (var cmd in commands)
                if (cmd.Command.Trim().Length > 0 && ConsequenceValidator.Check(cmd.Command) is null)
                    list.Add((kind, display?.Invoke(cmd) ?? cmd.Label, cmd));
        }
        Add("Titles", q.Titles);
        Add("Outfits", q.Outfits);
        Add("Animations", q.Gestures, ShortAnimationLabel);
        Add("Moodles", q.Moodles, c => MoodlesTextFormat.StripMarkup(c.Label));
        Add("Restraints", q.Restraints.Where(r => r.RestraintRules is { Count: > 0 } || r.RestraintCatalogId is null));
        Add("Custom Triggers", q.Aliases);
        return list;
    }

    private static string ShortAnimationLabel(QuickCommand cmd)
    {
        var label = cmd.Label;
        if (cmd.GestureModName is { Length: > 0 } mod && label.StartsWith(mod, StringComparison.OrdinalIgnoreCase))
            label = label[mod.Length..].TrimStart(' ', '-', '—');
        return label.Length > 0 ? label : cmd.Label;
    }

    // ---- Sub ----

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
                IconGlyph.WrappedColored(Theme.Warning, "Your rulebooks are paused because panic was triggered. Nothing fires until you resume.");
                if (ImGui.SmallButton("Resume rulebooks"))
                    service.Resume();
            }
        }
        else if (!config.Permissions.Rulebook || !config.RulebookAcknowledged)
        {
            IconGlyph.WrappedColored(Theme.Warning, "Your Rulebook permission is off, so nothing fires. You can still review what arrives. Turn it on in Settings > Permissions.");
        }

        if (state.Pending is { } pending)
        {
            using (Section.Begin("rbSubPending", $"New version {pending.Version} to review"))
            {
                var startsOver = pending.ResetCount > state.AppliedResetCount;
                if (startsOver)
                    IconGlyph.WrappedColored(Theme.Warning, $"{pairing.PeerName ?? "Your Owner"} is starting over. Accepting clears your oaths (running ones end with no outcome), your ledger score, switched-off rules and history, and offers every oath again.");
                using (Section.List("rbSubPendingDiff", Scaled(300)))
                    DrawRulebookDiff(startsOver ? null : state.Accepted, pending, startsOver ? new Dictionary<string, OathState>() : state.Oaths, pairing.PeerName ?? "your Owner");
                if (ImGui.Button("Accept"))
                    service.AcceptPending(pairing);
                ImGui.SameLine();
                if (ImGui.Button("Decline"))
                    service.DeclinePending(pairing);
                IconGlyph.HelpMarker("Declining keeps the version you already accepted running. Your Owner sees that you declined.");
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
                    DrawRuleLines(OathText.Lines(oath));
                    IconGlyph.WrappedDisabled($"Offer ends {Until(s.OfferedUnixSeconds + (long)RulebookService.OfferLifetime.TotalSeconds)}.");
                    if (ImGui.SmallButton("I swear"))
                        service.AcceptOath(pairing, id);
                    ImGui.SameLine();
                    if (ImGui.SmallButton("Decline"))
                        service.DeclineOath(pairing, id);
                    ImGui.Separator();
                    ImGui.PopID();
                }
            }
        }

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
                    var due = s.PeriodStartUnixSeconds + Math.Max(1, terms.PeriodDays) * 86400L;
                    var done = s.PeriodCount >= terms.TimesPerPeriod;
                    IconGlyph.WrappedColored(done ? Theme.Success : Theme.Warning,
                        done ? $"Done for now - next stretch starts {Until(due)}." : $"{s.PeriodCount}/{terms.TimesPerPeriod} done - due {Until(due)}.");
                }
                if (OathConditions.IsRitual(terms.Condition) && s.ScopeEndsUnixSeconds is { } ends)
                {
                    var length = Math.Max(1, terms.PeriodDays) * 86400L;
                    var start = ends - terms.DurationMinutes * 60L;
                    var total = Math.Max(1, (int)Math.Ceiling(terms.DurationMinutes * 60.0 / length));
                    var current = Math.Clamp((int)((s.PeriodStartUnixSeconds - start) / length) + 1, 1, total);
                    var unit = OathText.Unit(terms);
                    IconGlyph.WrappedDisabled($"{char.ToUpperInvariant(unit[0])}{unit[1..]} {current} of {total}. " + (s.MissedPeriods > 0
                        ? $"{s.MissedPeriods} missed, so it ends as broken."
                        : "The kept outcome applies when the oath ends, if none is missed."));
                }
                if (terms.Condition == OathCondition.GreetOwner)
                    DrawPerform(pairing, id, terms);
            }
        }

        using (Section.Begin("rbSubLedger", "Ledger"))
        {
            ImGui.TextUnformatted($"Score: {state.LedgerScore}");
            var draws = state.Activity.Where(e => e.Kind == RulebookEventKind.CardDrawn).Reverse().Take(5).ToList();
            if (draws.Count > 0)
            {
                Section.SubHeading("Recent draws");
                foreach (var d in draws)
                    IconGlyph.WrappedDisabled($"{Ago(d.At)} - {d.Text}");
            }
        }

        using (Section.Begin("rbSubRules", "Rules"))
        {
            IconGlyph.WrappedDisabled($"Everything in the version you accepted. Only {pairing.PeerName ?? "your Owner"} can change or remove a rule. Your safeword, or turning the Rulebook permission off, pauses all of it.");
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
                            DrawRuleLines(lines);
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
                    IconGlyph.WrappedDisabled($"{Ago(e.At)} - {e.Text}");
            }
            var held = service.Queue.HeldCount(pairing.Id);
            if (held > 0)
                IconGlyph.WrappedDisabled($"{held} consequence(s) waiting until you're out of combat or the cutscene ends.");
        }
    }

    private void DrawPerform(PairingState pairing, string oathId, Oath terms)
    {
        var service = plugin.RulebookService;
        ImGui.PushID($"perform_{oathId}");
        if (service.IsPerforming(pairing, oathId))
        {
            var left = service.PerformSecondsLeft is { } seconds ? $" - {Math.Ceiling(seconds):0}s left" : "";
            IconGlyph.WrappedColored(Theme.Warning, $"Performing{left}. Stay put until it ends.");
        }
        else
        {
            var blocker = service.PerformBlocker(pairing, oathId);
            using (ImRaii.Disabled(blocker is not null))
            {
                if (ImGui.SmallButton($"Perform ({OathText.Hold(terms)})"))
                    rbPerformError = service.Perform(pairing, oathId);
            }
            var full = string.IsNullOrEmpty(terms.AnimationId) ? "" : $"\n\nAnimation: {terms.AnimationLabel}";
            IconGlyph.HelpMarker($"Plays {OathText.Greeting(terms)} at {pairing.PeerName} and holds you in place for {OathText.Hold(terms)}. It counts once the hold ends; panic or a stop before then doesn't count.{full}");
            if (blocker is not null && blocker != "Done for now.")
                IconGlyph.WrappedDisabled(blocker);
            if (rbPerformError is not null)
                IconGlyph.WrappedColored(Theme.Warning, rbPerformError);
        }
        ImGui.PopID();
    }

    private static void DrawRulebookDiff(RulebookDocument? accepted, RulebookDocument pending, IReadOnlyDictionary<string, OathState> oaths, string owner)
    {
        var before = accepted is null ? new() : RuleText.All(accepted, owner).ToDictionary(r => r.Id);
        var after = RuleText.All(pending, owner).ToDictionary(r => r.Id);
        var any = false;
        foreach (var (id, rule) in after)
        {
            if (!before.TryGetValue(id, out var old))
            {
                if (id == DrawOnSettings.RuleId && !pending.DrawOn.Any)
                    continue;
                IconGlyph.WrappedColored(Theme.Success, $"New {rule.Kind.ToLowerInvariant()}: {rule.Name}");
            }
            else if (old.Summary != rule.Summary || old.Name != rule.Name)
                IconGlyph.WrappedColored(Theme.Warning, $"Changed {rule.Kind.ToLowerInvariant()}: {rule.Name}");
            else
                continue;
            ImGui.PushID(id);
            DrawRuleLines(rule.Lines);
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
                IconGlyph.WrappedColored(Theme.Warning, $"Accepting ends your running oath \"{name}\" with no outcome.");
            else if (!RulebookService.SameTermsIgnoringName(running, next))
                IconGlyph.WrappedColored(Theme.Warning, $"Accepting switches your running oath \"{name}\" to these terms and restarts it.");
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
        OathStatus.Open => scopeEnds is { } ends ? $"Open, ends {Until(ends)}." : "Open.",
        OathStatus.Kept => "Kept.",
        OathStatus.Broken => "Broken.",
        OathStatus.Voided => "Voided.",
        _ => "",
    };

    private static string Ago(long unixSeconds)
    {
        if (unixSeconds <= 0)
            return "never";
        var span = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        return span.TotalSeconds < 60 ? "just now" : $"{RestraintLock.Format(span)} ago";
    }

    private static string Until(long unixSeconds)
    {
        var span = DateTimeOffset.FromUnixTimeSeconds(unixSeconds) - DateTimeOffset.UtcNow;
        return span <= TimeSpan.Zero ? "now" : $"in {RestraintLock.Format(span)}";
    }
}
