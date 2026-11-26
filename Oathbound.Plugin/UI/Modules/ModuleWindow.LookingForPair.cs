using System;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Oathbound.Plugin.Config;
using Oathbound.Plugin.Relay;

namespace Oathbound.Plugin.UI;

/// collar/looking-for-pair: a profile, browsing, private likes, matches and blocks. Nothing here sends a command; a
/// match only opens a chat.
public sealed partial class ModuleWindow
{
    private const string LfpRules =
        "Looking for pair is for adults (18+) only.\n" +
        "- Be honest and kind. No harassment, hate or threats.\n" +
        "- Don't put your character's name, world or any real-life details in your profile.\n" +
        "- Nothing sexual in your picture.\n" +
        "- Consent comes first: a match is a conversation, not a promise.\n" +
        "- Block anyone who makes you uncomfortable. They'll never see you again.";

    private LfpProfileFields? lfpDraft;
    private string lfpDraftVisibility = "public";
    private bool lfpDraftHidden;
    private bool lfpAdultChecked;
    private bool lfpRulesChecked;
    private string lfpCodeInput = "";
    private string? lfpTab;

    private LookingForPairService Lfp => plugin.LookingForPair;

    private void DrawLookingForPairModule()
    {
        var state = plugin.Configuration.LookingForPair;
        if (!Lfp.Attested)
        {
            DrawLfpGate();
            if (state.Matches.Count > 0)
                using (Section.Begin("lfpMatchesOnly", "Your matches"))
                    DrawLfpMatches(state);
            return;
        }

        if (Lfp.LastError is { } error) Status.Line(StatusKind.Error, error);
        else if (Lfp.LastNotice is { } notice) Status.Line(StatusKind.Ok, notice);

        using (Section.Begin("lfpTabs"))
        {
            if (!ImGui.BeginTabBar("lfpTabBar")) return;
            LfpTab("Profile", "profile", () => DrawLfpProfile(state));
            LfpTab("Browse", "browse", DrawLfpBrowse);
            var unseen = Lfp.UnseenMatches;
            LfpTab(unseen > 0 ? $"Matches ({unseen} new)###lfpMatches" : "Matches###lfpMatches", "matches", () => DrawLfpMatches(state));
            LfpTab("Liked", "liked", DrawLfpLiked);
            LfpTab("Blocked", "blocked", DrawLfpBlocked);
            ImGui.EndTabBar();
        }
    }

    private void LfpTab(string label, string id, Action draw)
    {
        if (!ImGui.BeginTabItem(label)) return;
        if (lfpTab != id)
        {
            lfpTab = id;
            Lfp.DismissMessages();
            if (id == "browse" && !Lfp.BrowseLoaded) Lfp.LoadBrowse();
            if (id == "liked") Lfp.LoadLikes();
            if (id == "blocked") Lfp.LoadBlocks();
        }
        draw();
        ImGui.EndTabItem();
    }

    // ---- gate --------------------------------------------------------------------------------------------------------

    private void DrawLfpGate()
    {
        using var section = Section.Begin("lfpGate", "Before you start");
        IconGlyph.WrappedDisabled("Make a profile others can find, browse theirs, and like the ones you're interested in. When two people like each other it's a match, and you can chat. Nobody sees your character until you choose to share it.");
        ImGui.Spacing();
        using (ImRaii.PushColor(ImGuiCol.Text, Theme.TextMuted))
            ImGui.TextWrapped(LfpRules);
        ImGui.Spacing();
        Layout.WrappedCheckbox("I am 18 or older##lfpAdult", ref lfpAdultChecked);
        Layout.WrappedCheckbox("I accept these community rules##lfpRules", ref lfpRulesChecked);
        using (ImRaii.Disabled(!lfpAdultChecked || !lfpRulesChecked))
        {
            if (ImGui.Button("Continue##lfpAttest"))
                Lfp.Attest();
        }
        IconGlyph.HelpMarker("Remembered on this install. You can withdraw it later from the Profile tab, which deletes your profile.");
    }

    // ---- profile -----------------------------------------------------------------------------------------------------

    private void DrawLfpProfile(LookingForPairState state)
    {
        if (lfpDraft is null)
        {
            lfpDraft = state.Fields.Clone();
            lfpDraftVisibility = state.Visibility;
            lfpDraftHidden = state.Hidden;
        }
        var draft = lfpDraft;

        using (Section.Begin("lfpProfileStatus"))
        {
            if (!state.Published)
            {
                Status.Line(StatusKind.Info, "You don't have a profile yet. Fill it in and save to be found.");
            }
            else
            {
                var listed = Lfp.OwnProfile?.Listed ?? true;
                var where = state.Hidden ? "Hidden - nobody can see it." : !listed ? "Not listed - it hasn't been refreshed in 60 days." : state.Visibility == "public" ? "Public - listed in Browse." : "Private - only people with your code can see it.";
                Status.Line(state.Hidden || !listed ? StatusKind.Caution : StatusKind.Ok, where);
                if (state.ProfileCode is { } code)
                {
                    var formatted = code.Length == 10 ? $"{code[..5]}-{code[5..]}" : code;
                    ImGui.TextUnformatted("Your profile code:");
                    ImGui.SameLine();
                    ImGui.TextColored(Theme.Accent, formatted);
                    ImGui.SameLine();
                    if (ImGui.SmallButton("Copy##lfpCode")) ImGui.SetClipboardText(formatted);
                    ImGui.SameLine();
                    using (ImRaii.Disabled(Lfp.Busy))
                        if (ImGui.SmallButton("New code##lfpNewCode")) Lfp.NewCode();
                    IconGlyph.HelpMarker("Anyone you give this code to can see your profile and like it, even when it's private. A new code stops the old one working.");
                }
            }
        }

        using (Section.Begin("lfpPicture", "Profile picture"))
        {
            var size = Layout.Scaled(112f);
            if (Lfp.OwnPicturePath is { } path) LfpImage(path, size);
            else LfpPlaceholder(draft.DisplayName.Length > 0 ? draft.DisplayName : "?", size);
            ImGui.SameLine();
            ImGui.BeginGroup();
            IconGlyph.WrappedDisabled("Optional. Shown on your profile in Browse, code lookups and your matches. It's shrunk to 512 px, and everyone who can see your profile can see it - no character name, nothing sexual.");
            using (ImRaii.Disabled(Lfp.Busy))
            {
                if (ImGui.Button(Lfp.OwnPicturePath is null ? "Upload a picture...##lfpPic" : "Change picture...##lfpPic"))
                    plugin.FileDialogManager.OpenFileDialog("Profile picture", "Pictures{.png,.jpg,.jpeg}", (ok, picked) =>
                    {
                        if (ok) Lfp.SetPicture(picked);
                    });
                if (Lfp.OwnPicturePath is not null)
                {
                    ImGui.SameLine();
                    if (ImGui.Button("Remove##lfpPicRemove")) Lfp.RemovePicture();
                }
            }
            if (!state.Published && Lfp.OwnPicturePath is not null)
                ImGui.TextColored(Theme.TextMuted, "Uploaded when you create your profile.");
            ImGui.EndGroup();
        }

        using (Section.Begin("lfpProfileEditor", "Your profile"))
        {
            IconGlyph.WrappedDisabled("Don't put your character's name or world here - you can share it with a match later, in your chat.");
            var name = draft.DisplayName;
            Layout.ItemWidth(240);
            if (ImGui.InputTextWithHint("Display name##lfpName", "2-32 characters", ref name, 128)) draft.DisplayName = name;
            var pronouns = draft.Pronouns ?? "";
            Layout.ItemWidth(240);
            if (ImGui.InputTextWithHint("Pronouns (optional)##lfpPronouns", "she/her", ref pronouns, 96)) draft.Pronouns = pronouns;
            draft.Race = LfpCombo("Race##lfpRace", LookingForPairService.Races, LookingForPairService.RaceNames, draft.Race, null)!;
            draft.Gender = LfpCombo("Gender##lfpGender", LookingForPairService.Genders, LookingForPairService.GenderNames, draft.Gender, null)!;
            draft.Role = LfpCombo("I am##lfpRole", LookingForPairService.Roles, LookingForPairService.RoleNames, draft.Role, null)!;
            draft.Seeking = LfpCombo("Looking for##lfpSeeking", LookingForPairService.Seeking, LookingForPairService.SeekingNames, draft.Seeking, null)!;
            draft.DataCenter = LfpCombo("Data center##lfpDc", LookingForPairService.DataCenters, LookingForPairService.DataCenterNames, draft.DataCenter, "Any / not saying");
            var bio = draft.Bio;
            if (ImGui.InputTextMultiline("##lfpBio", ref bio, 4000, new Vector2(-1, Layout.Scaled(110f)))) draft.Bio = bio;
            var bioChars = bio.Trim().EnumerateRunes().Count();
            ImGui.TextColored(bioChars > 1000 ? Theme.Error : Theme.TextMuted, $"Bio: {bioChars}/1000");

            if (ImGui.RadioButton("Public##lfpVis", lfpDraftVisibility == "public")) lfpDraftVisibility = "public";
            ImGui.SameLine();
            if (ImGui.RadioButton("Private (code only)##lfpVis", lfpDraftVisibility == "private")) lfpDraftVisibility = "private";
            IconGlyph.HelpMarker("Public profiles are listed in Browse. A private one is only seen by people you give your profile code to.");
            Layout.WrappedCheckbox("Hide my profile for now##lfpHidden", ref lfpDraftHidden);

            var problem = LookingForPairService.Validate(draft);
            if (problem is not null && draft.DisplayName.Length > 0) Status.Line(StatusKind.Caution, problem);
            using (ImRaii.Disabled(Lfp.Busy || problem is not null))
            {
                if (ImGui.Button(state.Published ? "Save changes##lfpSave" : "Create profile##lfpSave"))
                    Lfp.SaveProfile(draft, lfpDraftVisibility, lfpDraftHidden);
            }
            if (state.Published)
            {
                ImGui.SameLine();
                if (ImGui.Button("Undo edits##lfpUndo")) lfpDraft = null;
            }
        }

        if (state.Published)
            using (Section.Begin("lfpPreview", "How others see you"))
            {
                var picture = Lfp.OwnProfile?.Picture;
                var size = Layout.Scaled(72f);
                if (picture is not null && Lfp.OwnPicturePath is { } own) LfpImage(own, size);
                else LfpPlaceholder(state.Fields.DisplayName, size);
                ImGui.SameLine();
                ImGui.BeginGroup();
                DrawLfpSummaryText(state.Fields, null);
                ImGui.EndGroup();
                if (state.Fields.Bio.Length > 0) ImGui.TextWrapped(state.Fields.Bio);
            }

        using (Section.Begin("lfpProfileSettings", "Settings"))
        {
            var show = state.ShowPictures;
            if (Layout.WrappedCheckbox("Show profile pictures##lfpShowPics", ref show))
            {
                state.ShowPictures = show;
                plugin.Configuration.Save();
            }
            if (state.Published && ConfirmDelete.Button("lfpDelete", "your profile", "Delete profile",
                    "Your profile, picture and pending likes are removed from the relay. Matches and their chats stay."))
                Lfp.DeleteProfile();
            if (state.Published) ImGui.SameLine();
            if (ImGui.Button("Withdraw 18+ confirmation##lfpWithdraw")) ImGui.OpenPopup("lfpWithdrawConfirm");
            if (ImGui.BeginPopup("lfpWithdrawConfirm"))
            {
                ImGui.TextUnformatted("Withdraw your confirmation? Your profile is deleted.");
                if (ConfirmDelete.DestructiveButton("Withdraw"))
                {
                    Lfp.WithdrawAttestation();
                    lfpAdultChecked = lfpRulesChecked = false;
                    lfpDraft = null;
                    ImGui.CloseCurrentPopup();
                }
                ImGui.EndPopup();
            }
        }
    }

    private static string? LfpCombo(string label, string[] ids, string[] names, string? current, string? noneLabel)
    {
        Layout.ItemWidth(240);
        var preview = current is null ? noneLabel ?? "" : LookingForPairService.NameOf(ids, names, current);
        if (!ImGui.BeginCombo(label, preview)) return current;
        var picked = current;
        if (noneLabel is not null && ImGui.Selectable(noneLabel, current is null)) picked = null;
        for (var i = 0; i < ids.Length; i++)
            if (ImGui.Selectable(names[i], ids[i] == current)) picked = ids[i];
        ImGui.EndCombo();
        return picked;
    }

    // ---- browse ------------------------------------------------------------------------------------------------------

    private void DrawLfpBrowse()
    {
        using (Section.Begin("lfpCodeLookup", "Have a profile code?"))
        {
            Layout.ItemWidth(160);
            ImGui.InputTextWithHint("##lfpCodeInput", "ABCDE-FGHJK", ref lfpCodeInput, 24);
            ImGui.SameLine();
            using (ImRaii.Disabled(Lfp.Busy || lfpCodeInput.Trim().Length == 0))
                if (ImGui.Button("Look up##lfpLookup")) Lfp.LookupCode(lfpCodeInput);
            if (Lfp.CodeResult is { } found)
            {
                DrawLfpCard(found, "code");
                if (ImGui.SmallButton("Close##lfpCodeClose")) Lfp.ClearCodeResult();
            }
        }

        using (Section.Begin("lfpBrowseFilters", "Filters"))
        {
            var changed = false;
            changed |= LfpFilter("Role##lfpFRole", LookingForPairService.Roles, LookingForPairService.RoleNames, () => Lfp.FilterRole, v => Lfp.FilterRole = v);
            ImGui.SameLine();
            changed |= LfpFilter("Seeking##lfpFSeek", LookingForPairService.Seeking, LookingForPairService.SeekingNames, () => Lfp.FilterSeeking, v => Lfp.FilterSeeking = v);
            changed |= LfpFilter("Data center##lfpFDc", LookingForPairService.DataCenters, LookingForPairService.DataCenterNames, () => Lfp.FilterDataCenter, v => Lfp.FilterDataCenter = v);
            ImGui.SameLine();
            changed |= LfpFilter("Race##lfpFRace", LookingForPairService.Races, LookingForPairService.RaceNames, () => Lfp.FilterRace, v => Lfp.FilterRace = v);
            changed |= LfpFilter("Gender##lfpFGender", LookingForPairService.Genders, LookingForPairService.GenderNames, () => Lfp.FilterGender, v => Lfp.FilterGender = v);
            ImGui.SameLine();
            using (ImRaii.Disabled(Lfp.Busy))
                if (ImGui.Button("Refresh##lfpRefresh") || changed) Lfp.LoadBrowse();
        }

        using (Section.Begin("lfpBrowseList"))
        {
            if (!plugin.Configuration.LookingForPair.Published)
                Status.Line(StatusKind.Info, "You can look around now; create your profile to like someone.");
            if (Lfp.BrowseLoaded && Lfp.Browse.Count == 0)
                IconGlyph.WrappedDisabled(Lfp.NextCursor is null ? "No profiles to show. Try other filters, or check back later." : "Everyone on this page was skipped - load more.");
            foreach (var profile in Lfp.Browse.ToList())
                DrawLfpCard(profile, "browse");
            if (Lfp.NextCursor is not null)
                using (ImRaii.Disabled(Lfp.Busy))
                    if (ImGui.Button("Load more##lfpMore")) Lfp.LoadBrowse(more: true);
        }
    }

    private static bool LfpFilter(string label, string[] ids, string[] names, Func<string?> get, Action<string?> set)
    {
        var before = get();
        Layout.ItemWidth(130);
        var picked = LfpComboNarrow(label, ids, names, before);
        if (picked == before) return false;
        set(picked);
        return true;
    }

    private static string? LfpComboNarrow(string label, string[] ids, string[] names, string? current)
    {
        var preview = current is null ? "Any" : LookingForPairService.NameOf(ids, names, current);
        if (!ImGui.BeginCombo(label, preview)) return current;
        var picked = current;
        if (ImGui.Selectable("Any", current is null)) picked = null;
        for (var i = 0; i < ids.Length; i++)
            if (ImGui.Selectable(names[i], ids[i] == current)) picked = ids[i];
        ImGui.EndCombo();
        return picked;
    }

    /// One profile with Like, Skip (browse only) and Block.
    private void DrawLfpCard(LfpProfileWire profile, string context)
    {
        using var id = ImRaii.PushId($"{context}{profile.ProfileId}");
        using (LfpItem())
        {
            DrawLfpSummary(profile.Fields.ToFields(), profile.Picture, null);
            using (ImRaii.Disabled(Lfp.Busy))
            {
                if (ImGui.Button("Like")) Lfp.Like(profile);
                if (context == "browse")
                {
                    ImGui.SameLine();
                    if (ImGui.Button("Skip")) Lfp.Skip(profile);
                }
                ImGui.SameLine();
                if (ImGui.Button("Block")) ImGui.OpenPopup("lfpBlockConfirm");
            }
            if (ImGui.BeginPopup("lfpBlockConfirm"))
            {
                ImGui.TextUnformatted($"Block {profile.Fields.DisplayName}? Neither of you will see the other again.");
                if (ConfirmDelete.DestructiveButton("Block"))
                {
                    Lfp.BlockProfile(profile);
                    ImGui.CloseCurrentPopup();
                }
                ImGui.EndPopup();
            }
        }
    }

    /// Picture, name, pronouns, what they are and seek, and the bio.
    private void DrawLfpSummary(LfpProfileFields f, string? picture, string? extraLine)
    {
        var size = Layout.Scaled(72f);
        if (Lfp.PictureFor(picture) is { } path) LfpImage(path, size);
        else LfpPlaceholder(f.DisplayName, size);
        ImGui.SameLine();
        ImGui.BeginGroup();
        DrawLfpSummaryText(f, extraLine);
        ImGui.EndGroup();
        if (f.Bio.Length > 0) ImGui.TextWrapped(f.Bio);
    }

    private static void DrawLfpSummaryText(LfpProfileFields f, string? extraLine)
    {
        ImGui.TextUnformatted(f.DisplayName);
        if (!string.IsNullOrWhiteSpace(f.Pronouns))
        {
            ImGui.SameLine();
            ImGui.TextColored(Theme.TextMuted, $"({f.Pronouns})");
        }
        var dc = f.DataCenter is null ? "" : $" · {LookingForPairService.NameOf(LookingForPairService.DataCenters, LookingForPairService.DataCenterNames, f.DataCenter)}";
        ImGui.TextColored(Theme.TextMuted,
            $"{LookingForPairService.NameOf(LookingForPairService.Races, LookingForPairService.RaceNames, f.Race)} · {LookingForPairService.NameOf(LookingForPairService.Genders, LookingForPairService.GenderNames, f.Gender)}{dc}");
        ImGui.TextColored(Theme.AccentHover,
            $"{LookingForPairService.NameOf(LookingForPairService.Roles, LookingForPairService.RoleNames, f.Role)}, looking for {LookingForPairService.NameOf(LookingForPairService.Seeking, LookingForPairService.SeekingNames, f.Seeking)}");
        if (extraLine is not null) ImGui.TextColored(Theme.Accent, extraLine);
    }

    /// A list entry: its content, then a separator under it.
    private static IDisposable LfpItem()
    {
        ImGui.Spacing();
        ImGui.BeginGroup();
        return new LfpItemScope();
    }

    private sealed class LfpItemScope : IDisposable
    {
        public void Dispose()
        {
            ImGui.EndGroup();
            ImGui.Spacing();
            ImGui.Separator();
        }
    }

    private static void LfpImage(string path, float size)
    {
        if (File.Exists(path) && Plugin.TextureProvider.GetFromFile(path).TryGetWrap(out var wrap, out _) && wrap.Width > 0)
        {
            var scale = MathF.Min(size / wrap.Width, size / wrap.Height);
            ImGui.Image(wrap.Handle, new Vector2(wrap.Width * scale, wrap.Height * scale));
        }
        else
        {
            ImGui.Dummy(new Vector2(size));
        }
    }

    private static void LfpPlaceholder(string name, float size)
    {
        var pos = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(pos, pos + new Vector2(size), ImGui.GetColorU32(Theme.Accent with { W = 0.35f }), Layout.Scaled(8f));
        var initial = char.ToUpperInvariant(name.TrimStart().FirstOrDefault('?')).ToString();
        var fontSize = size * 0.5f;
        var textSize = ImGui.CalcTextSize(initial) * (fontSize / ImGui.GetFontSize());
        draw.AddText(ImGui.GetFont(), fontSize, pos + (new Vector2(size) - textSize) / 2, ImGui.GetColorU32(new Vector4(1, 1, 1, 0.9f)), initial);
        ImGui.Dummy(new Vector2(size));
    }

    // ---- matches, likes, blocks --------------------------------------------------------------------------------------

    private void DrawLfpMatches(LookingForPairState state)
    {
        var active = state.Matches.Where(m => m.IsActive).OrderBy(m => m.Seen).ThenByDescending(m => m.CreatedAt).ToList();
        var ended = state.Matches.Where(m => !m.IsActive).OrderByDescending(m => m.EndedAt).ToList();
        if (active.Count == 0 && ended.Count == 0)
            IconGlyph.WrappedDisabled("No matches yet. When someone you liked likes you back, they show up here and you can chat.");

        foreach (var match in active)
        {
            using var id = ImRaii.PushId(match.LocalId.ToString("N"));
            using (LfpItem())
            {
                if (!match.Seen) Status.Pill(StatusKind.Ok, "New match!");
                var character = match.PeerCharacterName is { } n ? $"Character: {n} @ {match.PeerCharacterWorld}" : null;
                DrawLfpSummary(match.Peer, match.PeerPicture, character);
                ImGui.TextColored(Theme.TextMuted, $"Matched {TimeText.Ago(match.CreatedAt)}");
                var unread = plugin.Chat.UnreadFor(match.LocalId);
                if (ImGui.Button(unread > 0 ? $"Chat ({unread})" : "Chat")) plugin.Chat.RequestOpen(match.LocalId);
                ImGui.SameLine();
                using (ImRaii.Disabled(Lfp.Busy))
                {
                    if (ImGui.Button("Unmatch")) ImGui.OpenPopup("lfpUnmatch");
                    ImGui.SameLine();
                    if (ImGui.Button("Block")) ImGui.OpenPopup("lfpBlockMatch");
                }
                if (ImGui.BeginPopup("lfpUnmatch"))
                {
                    ImGui.TextUnformatted($"Unmatch {match.Peer.DisplayName}? The chat ends for both of you.");
                    if (ConfirmDelete.DestructiveButton("Unmatch"))
                    {
                        Lfp.Unmatch(match);
                        ImGui.CloseCurrentPopup();
                    }
                    ImGui.EndPopup();
                }
                if (ImGui.BeginPopup("lfpBlockMatch"))
                {
                    ImGui.TextUnformatted($"Block {match.Peer.DisplayName}? The chat ends and neither of you will see the other again.");
                    if (ConfirmDelete.DestructiveButton("Block"))
                    {
                        Lfp.BlockMatch(match);
                        ImGui.CloseCurrentPopup();
                    }
                    ImGui.EndPopup();
                }
            }
        }
        if (lfpTab == "matches" || !Lfp.Attested) Lfp.MarkMatchesSeen();

        if (ended.Count > 0)
        {
            Section.SubHeading("Ended");
            foreach (var match in ended)
            {
                using var id = ImRaii.PushId(match.LocalId.ToString("N"));
                ImGui.TextColored(Theme.TextMuted, $"{match.Label} - ended {TimeText.Ago(match.EndedAt ?? 0)}");
                ImGui.SameLine();
                if (plugin.Chat.History.Find(match.LocalId) is { Messages.Count: > 0 } && ImGui.SmallButton("Chat history"))
                    plugin.Chat.RequestOpen(match.LocalId);
                ImGui.SameLine();
                if (ConfirmDelete.Button("lfpForget", match.Label, "Forget", "Removes it and its chat history from this computer.", small: true))
                {
                    plugin.Chat.History.Clear(match.LocalId);
                    Lfp.Forget(match);
                }
            }
        }
    }

    private void DrawLfpLiked()
    {
        using var section = Section.Begin("lfpLiked");
        IconGlyph.WrappedDisabled("Only you see who you liked. They're told only if they like you back.");
        if (Lfp.Likes.Count == 0) IconGlyph.WrappedDisabled("You haven't liked anyone who hasn't answered yet.");
        foreach (var profile in Lfp.Likes.ToList())
        {
            using var id = ImRaii.PushId(profile.ProfileId);
            ImGui.TextUnformatted(profile.Fields.DisplayName);
            ImGui.SameLine();
            using (ImRaii.Disabled(Lfp.Busy))
                if (ImGui.SmallButton("Withdraw like")) Lfp.Unlike(profile);
        }
    }

    private void DrawLfpBlocked()
    {
        using var section = Section.Begin("lfpBlocked");
        if (Lfp.Blocks.Count == 0) IconGlyph.WrappedDisabled("You haven't blocked anyone.");
        foreach (var block in Lfp.Blocks.ToList())
        {
            using var id = ImRaii.PushId(block.BlockId);
            ImGui.TextUnformatted($"{block.DisplayName} - blocked {TimeText.Ago(block.CreatedAt)}");
            ImGui.SameLine();
            using (ImRaii.Disabled(Lfp.Busy))
                if (ImGui.SmallButton("Unblock")) Lfp.Unblock(block);
        }
    }
}
