using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Oathbound.Plugin.Config;
using Oathbound.Plugin.Relay;

namespace Oathbound.Plugin.UI;

/// 1:1 relay chat, one conversation per partner. With several partners a rail of avatars switches between them; an
/// ended pairing keeps its conversation, read-only, until it's cleared.
public sealed class ChatWindow : Window, IDisposable
{
    private static readonly Vector4 PaneBg = new(0.09f, 0.09f, 0.12f, 1f);
    private static readonly Vector4 BarBg = new(0.14f, 0.13f, 0.18f, 1f);
    private static readonly Vector4 Outgoing = new(0.38f, 0.22f, 0.55f, 1f);
    private static readonly Vector4 Incoming = new(0.19f, 0.19f, 0.25f, 1f);
    private static readonly Vector4 Failed = new(0.45f, 0.18f, 0.20f, 1f);
    private static readonly Vector4 MetaText = new(0.80f, 0.78f, 0.86f, 0.70f);
    private static readonly Vector4 Read = new(0.55f, 0.85f, 1.00f, 1f);
    private static readonly Vector4[] AvatarColors =
    [
        new(0.55f, 0.33f, 0.75f, 1f), new(0.80f, 0.40f, 0.55f, 1f), new(0.30f, 0.55f, 0.75f, 1f),
        new(0.35f, 0.65f, 0.50f, 1f), new(0.80f, 0.55f, 0.30f, 1f), new(0.60f, 0.45f, 0.35f, 1f),
    ];
    private const float BubbleWidthShare = 0.75f;
    private const long RunSeconds = 300;

    private readonly Plugin plugin;
    private ChatService Chat => plugin.Chat;

    private Guid? selected;
    private Guid? requested;
    private string draft = "";
    private string? sendError;
    private int lastMessageCount = -1;
    private bool refocusInput;
    private DateTime clearArmedUntil = DateTime.MinValue;
    /// A picture opened full size; shown as a modal over the window until clicked away.
    private string? viewerPath;
    private bool viewerOpening;

    public ChatWindow(Plugin plugin) : base("Chat###OathboundChatWindow")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(360, 380), MaximumSize = new Vector2(float.MaxValue, float.MaxValue) };
        Size = new Vector2(460, 560);
        SizeCondition = ImGuiCond.FirstUseEver;
        TitleBarButtons.Add(new TitleBarButton
        {
            Icon = FontAwesomeIcon.QuestionCircle,
            Click = _ => plugin.Tutorial.Start("chat", plugin.Configuration.ResolveActiveDirection() == PairingDirection.OwnerSide),
            ShowTooltip = () => ImGui.SetTooltip("Tour of Chat"),
        });
        plugin.Chat.OpenRequested += Open;
    }

    public void Dispose() => plugin.Chat.OpenRequested -= Open;

    public void Open(Guid pairingId)
    {
        requested = pairingId;
        IsOpen = true;
        BringToFront();
    }

    public override void PreDraw()
    {
        Theme.PushWindowStyle();
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
    }

    public override void PostDraw()
    {
        ImGui.PopStyleVar();
        Theme.PopWindowStyle();
    }

    public override void OnClose() => Chat.FocusedPairingId = null;

    public override void Draw()
    {
        // Title-bar buttons aren't ImGui items, so the ? button's area is reported by position.
        var windowPos = ImGui.GetWindowPos();
        var titleBarHeight = ImGui.GetFrameHeight();
        TutorialService.AnchorRect(TutorialAnchors.ChatHelp, windowPos + new Vector2(ImGui.GetWindowWidth() - titleBarHeight * 3f, 0),
            windowPos + new Vector2(ImGui.GetWindowWidth(), titleBarHeight));

        var conversations = Chat.Conversations();
        if (conversations.Count == 0)
        {
            Chat.FocusedPairingId = null;
            Pane("empty", Vector2.Zero, PaneBg, Layout.Scaled(16f), () => Centered(FontAwesomeIcon.Comments, "No one to chat with yet",
                "Chat is for paired Owners and Subs. Pair from Settings, then talk here - even across worlds and data centers."));
            return;
        }

        if (requested is { } r && conversations.Any(c => c.Conversation.PairingId == r)) Select(r);
        requested = null;
        if (selected is null || conversations.All(c => c.Conversation.PairingId != selected))
            Select((conversations.FirstOrDefault(c => c.Conversation.Unread > 0).Conversation ?? conversations[0].Conversation).PairingId);
        var current = conversations.First(c => c.Conversation.PairingId == selected);

        if (conversations.Count > 1)
        {
            DrawRail(conversations);
            ImGui.SameLine(0, 0);
        }
        Pane("conversation", Vector2.Zero, PaneBg, 0f, () => DrawConversation(current.Conversation, current.Pairings));
        DrawViewer();
    }

    private void DrawViewer()
    {
        const string id = "chatPictureViewer";
        if (viewerPath is null) return;
        if (viewerOpening)
        {
            ImGui.OpenPopup(id);
            viewerOpening = false;
        }
        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.GetCenter(), ImGuiCond.Always, new Vector2(0.5f));
        using var padding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(Layout.Scaled(6f)));
        using var bg = ImRaii.PushColor(ImGuiCol.PopupBg, PaneBg);
        var open = true;
        if (!ImGui.BeginPopupModal(id, ref open, ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoMove))
        {
            viewerPath = null;
            return;
        }
        if (File.Exists(viewerPath) && Plugin.TextureProvider.GetFromFile(viewerPath).TryGetWrap(out var wrap, out _) && wrap.Width > 0)
        {
            var limit = viewport.Size * 0.85f;
            var scale = MathF.Min(1f, MathF.Min(limit.X / wrap.Width, limit.Y / wrap.Height));
            ImGui.Image(wrap.Handle, new Vector2(wrap.Width * scale, wrap.Height * scale));
        }
        else
        {
            ImGui.TextUnformatted("This picture couldn't be loaded.");
        }
        if (ImGui.IsItemClicked() || ImGui.IsKeyPressed(ImGuiKey.Escape) || (ImGui.IsMouseClicked(ImGuiMouseButton.Left) && !ImGui.IsWindowHovered()))
        {
            ImGui.CloseCurrentPopup();
            viewerPath = null;
        }
        ImGui.EndPopup();
    }

    private void Select(Guid pairingId)
    {
        if (selected == pairingId) return;
        selected = pairingId;
        draft = "";
        sendError = null;
        lastMessageCount = -1;
        clearArmedUntil = DateTime.MinValue;
        refocusInput = true;
    }

    // ---- partner rail -----------------------------------------------------------------------------------------------

    private void DrawRail(IReadOnlyList<(ChatConversation Conversation, IReadOnlyList<PairingState> Pairings)> conversations)
    {
        var width = Layout.Scaled(58f);
        Pane("rail", new Vector2(width, 0), BarBg, 0f, () => DrawRailItems(conversations, width));
        TutorialService.Anchor(TutorialAnchors.ChatPartners);
    }

    private void DrawRailItems(IReadOnlyList<(ChatConversation Conversation, IReadOnlyList<PairingState> Pairings)> conversations, float width)
    {
        ImGui.Dummy(new Vector2(0, Layout.Scaled(6f)));
        var size = Layout.Scaled(40f);
        foreach (var (conversation, pairings) in conversations)
        {
            ImGui.SetCursorPosX((width - size) / 2);
            var pos = ImGui.GetCursorScreenPos();
            if (ImGui.InvisibleButton($"rail{conversation.PairingId:N}", new Vector2(size, size)))
            {
                Select(conversation.PairingId);
                refocusInput = true;
            }
            var hovered = ImGui.IsItemHovered();
            var isSelected = selected == conversation.PairingId;
            var draw = ImGui.GetWindowDrawList();
            var center = pos + new Vector2(size / 2);
            if (isSelected || hovered)
                draw.AddCircle(center, size / 2 + Layout.Scaled(2.5f), ImGui.GetColorU32(isSelected ? Theme.AccentHover : Theme.TextMuted with { W = 0.5f }), 0, Layout.Scaled(2f));
            Avatar(draw, center, size / 2, conversation, Ended(conversation, pairings));
            if (conversation.Unread > 0)
                Badge(draw, center + new Vector2(size * 0.36f, -size * 0.36f), conversation.Unread);
            if (hovered)
                Tip(Ended(conversation, pairings) ? $"{Name(conversation)} (ended)" : $"{Name(conversation)} - {Role(conversation, pairings)}");
            ImGui.Dummy(new Vector2(0, Layout.Scaled(4f)));
        }
    }

    // ---- conversation -----------------------------------------------------------------------------------------------

    private void DrawConversation(ChatConversation conversation, IReadOnlyList<PairingState> pairings)
    {
        var match = Chat.MatchFor(conversation);
        var pairing = conversation.IsMatch ? null : Chat.SendPairing(pairings);
        var writable = conversation.IsMatch ? match is { IsActive: true } : pairing is not null;
        var focused = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows);
        Chat.FocusedPairingId = focused ? conversation.PairingId : null;
        if (focused) Chat.MarkRead(conversation.PairingId);

        DrawTopBar(conversation, pairings);
        DrawNotice(conversation, pairing, match);

        var inputBar = writable ? InputBarHeight() : 0f;
        Pane("messages", new Vector2(0, -inputBar), PaneBg, Layout.Scaled(12f), () => DrawMessages(conversation, pairings));
        TutorialService.Anchor(TutorialAnchors.ChatMessages);

        if (writable) DrawInputBar(conversation, pairing, match);
    }

    /// A pairing chat whose pairings all ended, or a match chat whose match ended.
    private bool Ended(ChatConversation conversation, IReadOnlyList<PairingState> pairings) =>
        conversation.IsMatch ? Chat.MatchFor(conversation) is not { IsActive: true } : pairings.Count == 0;

    private void DrawTopBar(ChatConversation conversation, IReadOnlyList<PairingState> pairings)
    {
        var height = Layout.Scaled(52f);
        var start = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(start, start + new Vector2(width, height), ImGui.GetColorU32(BarBg));
        draw.AddLine(start + new Vector2(0, height), start + new Vector2(width, height), ImGui.GetColorU32(Theme.Accent with { W = 0.35f }));

        var radius = Layout.Scaled(17f);
        var padX = Layout.Scaled(12f);
        var avatarCenter = start + new Vector2(padX + radius, height / 2);
        Avatar(draw, avatarCenter, radius, conversation, Ended(conversation, pairings));

        var textX = padX + radius * 2 + Layout.Scaled(10f);
        var lineHeight = ImGui.GetTextLineHeight();
        var subtitle = conversation.IsMatch
            ? Ended(conversation, pairings) ? "match ended" : string.IsNullOrWhiteSpace(conversation.PeerWorld) ? "Looking for pair match" : $"{conversation.PeerWorld}  ·  match"
            : pairings.Count == 0
            ? "pairing ended"
            : string.IsNullOrWhiteSpace(conversation.PeerWorld) ? Role(conversation, pairings) : $"{conversation.PeerWorld}  ·  {Role(conversation, pairings)}";
        var small = ImGui.GetFontSize() * 0.86f;
        var top = (height - lineHeight - small - Layout.Scaled(2f)) / 2;
        draw.AddText(start + new Vector2(textX, top), ImGui.GetColorU32(new Vector4(1, 1, 1, 0.95f)), Name(conversation));
        draw.AddText(ImGui.GetFont(), small, start + new Vector2(textX, top + lineHeight + Layout.Scaled(2f)), ImGui.GetColorU32(Theme.TextMuted), subtitle);

        if (conversation.Messages.Count > 0)
        {
            var buttonSize = Layout.Scaled(30f);
            ImGui.SetCursorScreenPos(start + new Vector2(width - padX - buttonSize, (height - buttonSize) / 2));
            var armed = DateTime.UtcNow < clearArmedUntil;
            if (GhostIcon("clearChat", FontAwesomeIcon.TrashAlt, buttonSize, armed ? Theme.Destructive : Theme.TextMuted, armed))
            {
                if (armed)
                {
                    Chat.History.Clear(conversation.PairingId);
                    clearArmedUntil = DateTime.MinValue;
                    lastMessageCount = -1;
                }
                else
                {
                    clearArmedUntil = DateTime.UtcNow.AddSeconds(4);
                }
            }
            if (ImGui.IsItemHovered())
                Tip(armed
                    ? "Click again to delete this conversation from this computer."
                    : "Clear history - deletes this conversation from this computer only. Your partner keeps their copy.");
        }

        TutorialService.AnchorRect(TutorialAnchors.ChatHeader, start, start + new Vector2(width, height));
        ImGui.SetCursorScreenPos(start + new Vector2(0, height + 1));
        ImGui.Dummy(new Vector2(width, 0));
    }

    /// A slim strip under the top bar, only when something needs saying.
    private void DrawNotice(ChatConversation conversation, PairingState? pairing, LfpMatchState? match)
    {
        (Vector4 Color, FontAwesomeIcon Icon, string Text)? notice =
            conversation.IsMatch && match is not { IsActive: true } ? (Theme.TextMuted, FontAwesomeIcon.Lock, "This match has ended - this chat is read-only.")
            : !conversation.IsMatch && pairing is null ? (Theme.TextMuted, FontAwesomeIcon.Lock, "Your pairing has ended - this chat is read-only.")
            : conversation.PeerNeedsUpdate ? (Theme.Caution, FontAwesomeIcon.ExclamationTriangle, $"{Name(conversation)} needs to update Oathbound before they can chat.")
            : !plugin.PeerMessenger.Connected ? (Theme.Caution, FontAwesomeIcon.Wifi, "Reconnecting to the relay - messages can't be sent until it's back.")
            : null;
        if (notice is not { } n) return;

        var start = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var pad = new Vector2(Layout.Scaled(12f), Layout.Scaled(5f));
        var height = ImGui.GetTextLineHeight() + pad.Y * 2;
        ImGui.GetWindowDrawList().AddRectFilled(start, start + new Vector2(width, height), ImGui.GetColorU32(n.Color with { W = 0.12f }));
        ImGui.SetCursorScreenPos(start + pad);
        using (ImRaii.PushColor(ImGuiCol.Text, n.Color))
            IconGlyph.Text(n.Icon, n.Text);
        ImGui.SetCursorScreenPos(start + new Vector2(0, height));
        ImGui.Dummy(new Vector2(width, 0));
    }

    // ---- messages ---------------------------------------------------------------------------------------------------

    private void DrawMessages(ChatConversation conversation, IReadOnlyList<PairingState> pairings)
    {
        var messages = conversation.Messages;
        if (messages.Count == 0)
        {
            Centered(conversation.IsMatch ? FontAwesomeIcon.Heart : FontAwesomeIcon.Lock, conversation.IsMatch ? "It's a match - say hi!" : "Say hi!",
                conversation.IsMatch
                    ? $"Messages are end-to-end encrypted. Your character stays private until you share it with the + button."
                    : $"Messages are end-to-end encrypted. If {Name(conversation)} is offline, yours wait on the relay for up to 7 days.");
            lastMessageCount = 0;
            return;
        }

        ImGui.Dummy(new Vector2(0, Layout.Scaled(4f)));
        DateTime? lastDay = null;
        ChatEntry? previous = null;
        foreach (var entry in messages)
        {
            var when = DateTimeOffset.FromUnixTimeSeconds(entry.At).LocalDateTime;
            if (lastDay != when.Date)
            {
                DayPill(when.Date);
                lastDay = when.Date;
                previous = null;
            }
            var sameRun = previous is not null && previous.Outgoing == entry.Outgoing && entry.At - previous.At < RunSeconds;
            if (previous is not null)
                ImGui.Dummy(new Vector2(0, sameRun ? Layout.Scaled(1f) : Layout.Scaled(7f)));

            ImGui.PushID(entry.LocalId.GetHashCode());
            Action? retry = Chat.CanRetry(conversation, entry, pairings) ? () => RetryMessage(conversation, entry, pairings) : null;
            if (entry.Image != ChatImageState.None) ImageBubble(entry, when, retry);
            else Bubble(entry, when, retry);
            if (entry is { Outgoing: false, Special: MatchPlaintext.InviteKind, InviteCode: { } code }) InviteActions(code);
            ImGui.PopID();
            previous = entry;
        }
        ImGui.Dummy(new Vector2(0, Layout.Scaled(6f)));

        if (messages.Count != lastMessageCount)
        {
            ImGui.SetScrollHereY(1f);
            lastMessageCount = messages.Count;
        }
    }

    private static void DayPill(DateTime day)
    {
        var today = DateTime.Now.Date;
        var label = day == today ? "Today" : day == today.AddDays(-1) ? "Yesterday" : day.ToString(day.Year == today.Year ? "dddd, MMM d" : "MMM d, yyyy");
        var small = ImGui.GetFontSize() * 0.86f;
        var textSize = ImGui.CalcTextSize(label) * 0.86f;
        var pad = new Vector2(Layout.Scaled(10f), Layout.Scaled(3f));
        ImGui.Dummy(new Vector2(0, Layout.Scaled(6f)));
        var width = ImGui.GetContentRegionAvail().X;
        var start = ImGui.GetCursorScreenPos();
        var min = start + new Vector2((width - textSize.X) / 2 - pad.X, 0);
        var size = textSize + pad * 2;
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(min, min + size, ImGui.GetColorU32(BarBg), size.Y / 2);
        draw.AddText(ImGui.GetFont(), small, min + pad, ImGui.GetColorU32(Theme.TextMuted), label);
        ImGui.Dummy(new Vector2(width, size.Y + Layout.Scaled(6f)));
    }

    /// Drawn by hand so it sizes to its text and sits on its side; the time and delivery tick live in its corner,
    /// beside short text or under long text. Right-click copies.
    private static void Bubble(ChatEntry entry, DateTime when, Action? retry)
    {
        var font = ImGui.GetFont();
        var small = ImGui.GetFontSize() * 0.78f;
        var scale = small / ImGui.GetFontSize();
        var pad = new Vector2(Layout.Scaled(11f), Layout.Scaled(7f));
        var gap = Layout.Scaled(8f);
        var lineStart = ImGui.GetCursorScreenPos();
        var avail = ImGui.GetContentRegionAvail().X;
        var wrap = MathF.Max(avail * BubbleWidthShare - pad.X * 2, Layout.Scaled(80f));

        var time = when.ToString("HH:mm");
        var tick = entry.Outgoing ? TickFor(entry.State) : null;
        var timeSize = ImGui.CalcTextSize(time) * scale;
        Vector2 tickSize = Vector2.Zero;
        if (tick is { } t)
            using (ImRaii.PushFont(Plugin.PluginInterface.UiBuilder.FontIcon))
                tickSize = ImGui.CalcTextSize(t.Icon.ToIconString()) * (small / ImGui.GetFontSize());
        var metaWidth = timeSize.X + (tick is null ? 0 : tickSize.X + Layout.Scaled(4f));
        var metaHeight = MathF.Max(timeSize.Y, tickSize.Y);

        var textSize = ImGui.CalcTextSize(entry.Text, false, wrap);
        var oneLine = textSize.Y < ImGui.GetTextLineHeight() * 1.5f;
        var inline = oneLine && textSize.X + gap + metaWidth <= wrap;
        var content = inline
            ? new Vector2(textSize.X + gap + metaWidth, textSize.Y)
            : new Vector2(MathF.Max(textSize.X, metaWidth), textSize.Y + metaHeight + Layout.Scaled(2f));
        var bubble = content + pad * 2;
        var min = new Vector2(entry.Outgoing ? lineStart.X + avail - bubble.X : lineStart.X, lineStart.Y);
        var max = min + bubble;

        var color = entry.State == ChatEntryState.NotDelivered ? Failed : entry.Outgoing ? Outgoing : Incoming;
        if (entry.State == ChatEntryState.Sending) color = color with { W = 0.7f };
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(min, max, ImGui.GetColorU32(color), Layout.Scaled(12f));

        ImGui.SetCursorScreenPos(min + pad);
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + wrap);
        ImGui.TextUnformatted(entry.Text);
        ImGui.PopTextWrapPos();

        var metaPos = new Vector2(max.X - pad.X - metaWidth, max.Y - pad.Y - metaHeight + (inline ? Layout.Scaled(1f) : 0));
        draw.AddText(font, small, metaPos + new Vector2(0, (metaHeight - timeSize.Y) / 2), ImGui.GetColorU32(MetaText), time);
        if (tick is { } k)
            draw.AddText(Plugin.PluginInterface.UiBuilder.FontIcon, small, metaPos + new Vector2(timeSize.X + Layout.Scaled(4f), (metaHeight - tickSize.Y) / 2),
                ImGui.GetColorU32(k.Color), k.Icon.ToIconString());

        ImGui.SetCursorScreenPos(min);
        ImGui.InvisibleButton("bubble", bubble);
        if (tick is { } h && ImGui.IsItemHovered())
            Tip(h.Tooltip);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, PopupPadding);
        var menuOpen = ImGui.BeginPopupContextItem("bubbleMenu");
        ImGui.PopStyleVar();
        if (menuOpen)
        {
            if (ImGui.Selectable("Copy text"))
                ImGui.SetClipboardText(entry.Text);
            if (retry is not null && ImGui.Selectable("Retry"))
                retry();
            ImGui.EndPopup();
        }
        ImGui.SetCursorScreenPos(new Vector2(lineStart.X, max.Y));
        FailureLine(entry, avail);
    }

    /// Ends a bubble's row: the reason under a message that wasn't delivered, else nothing.
    private static void FailureLine(ChatEntry entry, float avail)
    {
        if (entry is { State: ChatEntryState.NotDelivered, Reason: { } reason })
        {
            var text = $"Not delivered - {reason}.";
            var size = ImGui.CalcTextSize(text, false, avail * BubbleWidthShare) * 0.86f;
            ImGui.Dummy(new Vector2(0, Layout.Scaled(2f)));
            var pos = ImGui.GetCursorScreenPos() + new Vector2(entry.Outgoing ? avail - size.X : 0, 0);
            ImGui.GetWindowDrawList().AddText(ImGui.GetFont(), ImGui.GetFontSize() * 0.86f, pos, ImGui.GetColorU32(Theme.Error), text, avail * BubbleWidthShare);
            ImGui.Dummy(new Vector2(0, size.Y));
        }
        else
        {
            ImGui.Dummy(Vector2.Zero);
        }
    }

    /// The time and delivery tick for a bubble's corner: its size, and a function drawing it with its right/bottom edge
    /// at a point.
    private static (Vector2 Size, Action<ImDrawListPtr, Vector2> Draw) Meta(ChatEntry entry, DateTime when)
    {
        var small = ImGui.GetFontSize() * 0.78f;
        var scale = small / ImGui.GetFontSize();
        var time = when.ToString("HH:mm");
        var tick = entry.Outgoing ? TickFor(entry.State) : null;
        var timeSize = ImGui.CalcTextSize(time) * scale;
        var tickSize = Vector2.Zero;
        if (tick is { } t)
            using (ImRaii.PushFont(Plugin.PluginInterface.UiBuilder.FontIcon))
                tickSize = ImGui.CalcTextSize(t.Icon.ToIconString()) * (small / ImGui.GetFontSize());
        var gap = tick is null ? 0f : Layout.Scaled(4f);
        var size = new Vector2(timeSize.X + gap + tickSize.X, MathF.Max(timeSize.Y, tickSize.Y));
        return (size, (draw, bottomRight) =>
        {
            var pos = bottomRight - size;
            draw.AddText(ImGui.GetFont(), small, pos + new Vector2(0, (size.Y - timeSize.Y) / 2), ImGui.GetColorU32(MetaText), time);
            if (tick is { } k)
                draw.AddText(Plugin.PluginInterface.UiBuilder.FontIcon, small, pos + new Vector2(timeSize.X + gap, (size.Y - tickSize.Y) / 2),
                    ImGui.GetColorU32(k.Color), k.Icon.ToIconString());
        });
    }

    /// A picture in a slim bubble, its caption (if any) under it, and the time over its corner. Click to view full size.
    private void RetryMessage(ChatConversation conversation, ChatEntry entry, IReadOnlyList<PairingState> pairings)
    {
        if (Chat.Retry(conversation, entry, pairings) is { } error)
            SendNotice.Failed(error, error);
    }

    private void ImageBubble(ChatEntry entry, DateTime when, Action? retry)
    {
        var pad = Layout.Scaled(4f);
        var textPad = new Vector2(Layout.Scaled(8f), Layout.Scaled(5f));
        var rounding = Layout.Scaled(12f);
        var lineStart = ImGui.GetCursorScreenPos();
        var avail = ImGui.GetContentRegionAvail().X;
        var maxSize = new Vector2(MathF.Min(avail * 0.62f, Layout.Scaled(280f)), Layout.Scaled(300f));
        var source = entry.ImageWidth > 0 && entry.ImageHeight > 0 ? new Vector2(entry.ImageWidth, entry.ImageHeight) : new Vector2(4, 3);
        var imageSize = source * MathF.Min(maxSize.X / source.X, maxSize.Y / source.Y);
        imageSize = Vector2.Max(imageSize, new Vector2(Layout.Scaled(120f), Layout.Scaled(60f)));

        var (metaSize, drawMeta) = Meta(entry, when);
        var hasCaption = entry.Text.Length > 0;
        var captionSize = hasCaption ? ImGui.CalcTextSize(entry.Text, false, imageSize.X - textPad.X * 2) : Vector2.Zero;
        var footer = hasCaption ? textPad.Y + captionSize.Y + Layout.Scaled(2f) + metaSize.Y + textPad.Y : pad;
        var bubble = new Vector2(imageSize.X + pad * 2, pad + imageSize.Y + footer);
        var min = new Vector2(entry.Outgoing ? lineStart.X + avail - bubble.X : lineStart.X, lineStart.Y);
        var max = min + bubble;

        var color = entry.State == ChatEntryState.NotDelivered ? Failed : entry.Outgoing ? Outgoing : Incoming;
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(min, max, ImGui.GetColorU32(color), rounding);

        var imageMin = min + new Vector2(pad);
        var imageMax = imageMin + imageSize;
        var path = ChatImageService.PathOf(entry.ImageFile);
        var ready = entry.Image == ChatImageState.Ready && path is not null && File.Exists(path)
            && Plugin.TextureProvider.GetFromFile(path).TryGetWrap(out var wrap, out _) && wrap.Width > 0;
        if (ready)
        {
            Plugin.TextureProvider.GetFromFile(path!).TryGetWrap(out var texture, out _);
            draw.AddImageRounded(texture!.Handle, imageMin, imageMax, Vector2.Zero, Vector2.One, 0xFFFFFFFF, rounding - pad);
        }
        else
        {
            draw.AddRectFilled(imageMin, imageMax, ImGui.GetColorU32(new Vector4(0, 0, 0, 0.25f)), rounding - pad);
            var (icon, label) = entry.Image switch
            {
                ChatImageState.Preparing => (FontAwesomeIcon.CloudUploadAlt, "Sending picture…"),
                ChatImageState.Downloading => (FontAwesomeIcon.CloudDownloadAlt, "Loading picture…"),
                ChatImageState.Unavailable when entry.Outgoing => (FontAwesomeIcon.EyeSlash, "Picture not sent"),
                ChatImageState.Unavailable => (FontAwesomeIcon.EyeSlash, "Picture no longer available"),
                _ => (FontAwesomeIcon.Image, "Loading…"),
            };
            var iconSize = ImGui.GetFontSize() * 1.6f;
            var small = ImGui.GetFontSize() * 0.86f;
            var labelSize = ImGui.CalcTextSize(label) * 0.86f;
            var center = (imageMin + imageMax) / 2;
            IconAt(draw, icon, iconSize, center - new Vector2(0, iconSize / 2 + Layout.Scaled(2f)), Theme.TextMuted);
            draw.AddText(ImGui.GetFont(), small, center + new Vector2(-labelSize.X / 2, Layout.Scaled(2f)), ImGui.GetColorU32(Theme.TextMuted), label);
        }

        if (hasCaption)
        {
            ImGui.SetCursorScreenPos(new Vector2(min.X + pad + textPad.X, imageMax.Y + textPad.Y));
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + imageSize.X - textPad.X * 2);
            ImGui.TextUnformatted(entry.Text);
            ImGui.PopTextWrapPos();
            drawMeta(draw, max - new Vector2(pad + textPad.X, textPad.Y));
        }
        else
        {
            // Over the picture's corner, on a dark chip so it reads on any image.
            var chipPad = new Vector2(Layout.Scaled(6f), Layout.Scaled(2f));
            var corner = imageMax - new Vector2(Layout.Scaled(6f));
            draw.AddRectFilled(corner - metaSize - chipPad * 2, corner, ImGui.GetColorU32(new Vector4(0, 0, 0, 0.5f)), Layout.Scaled(8f));
            drawMeta(draw, corner - chipPad);
        }

        ImGui.SetCursorScreenPos(min);
        if (ImGui.InvisibleButton("picture", bubble) && ready)
        {
            viewerPath = path;
            viewerOpening = true;
        }
        if (ImGui.IsItemHovered())
        {
            if (ready) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            if (TickFor(entry.State) is { } tick && entry.Outgoing) Tip(ready ? $"{tick.Tooltip}\nClick to view full size." : tick.Tooltip);
            else if (ready) Tip("Click to view full size.");
        }
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, PopupPadding);
        var menuOpen = ImGui.BeginPopupContextItem("pictureMenu");
        ImGui.PopStyleVar();
        if (menuOpen)
        {
            if (ready && ImGui.Selectable("View full size"))
            {
                viewerPath = path;
                viewerOpening = true;
            }
            if (ready && ImGui.Selectable("Save picture..."))
            {
                var extension = Path.GetExtension(path!);
                plugin.FileDialogManager.SaveFileDialog("Save picture", $"Picture{{{extension}}}", $"oathbound-{when:yyyyMMdd-HHmm}", extension, (ok, target) =>
                {
                    if (!ok) return;
                    try { File.Copy(path!, target, overwrite: true); }
                    catch (Exception ex) { Plugin.Log.Warning(ex, "Chat picture couldn't be saved."); }
                });
            }
            if (hasCaption && ImGui.Selectable("Copy caption"))
                ImGui.SetClipboardText(entry.Text);
            if (entry is { Image: ChatImageState.Downloading } && ImGui.Selectable("Try loading again"))
                Chat.RetryImage(entry);
            if (retry is not null && ImGui.Selectable("Retry"))
                retry();
            ImGui.EndPopup();
        }
        ImGui.SetCursorScreenPos(new Vector2(lineStart.X, max.Y));
        FailureLine(entry, avail);
    }

    /// Opening an invite runs the normal code pairing: they see who it's from and accept, then the inviter confirms.
    private void InviteActions(string code)
    {
        if (ImGui.SmallButton("Open invite"))
        {
            plugin.SettingsWindow.ShowTab(SettingsTab.Identity);
            Plugin.FireAndForget(plugin.CodePairingService.LookupAsync(code, System.Threading.CancellationToken.None));
        }
        if (ImGui.IsItemHovered())
            Tip("Opens it in Settings > Identity & Pairing. You'll see the inviting character and choose to accept or decline.");
        ImGui.Dummy(new Vector2(0, Layout.Scaled(2f)));
    }

    private static (FontAwesomeIcon Icon, Vector4 Color, string Tooltip)? TickFor(ChatEntryState state) => state switch
    {
        ChatEntryState.Sending => (FontAwesomeIcon.Clock, MetaText, "Sending…"),
        ChatEntryState.Waiting => (FontAwesomeIcon.Check, MetaText, "Waiting on the relay - they'll get it when they're online."),
        ChatEntryState.Delivered => (FontAwesomeIcon.CheckDouble, Read, "Delivered"),
        ChatEntryState.NotDelivered => (FontAwesomeIcon.ExclamationCircle, Theme.Error, "Not delivered"),
        _ => null,
    };

    // ---- input ------------------------------------------------------------------------------------------------------

    private string? InputHint()
    {
        var count = draft.Trim().Length;
        return sendError ?? (count > PeerMessageCrypto.MaxChatChars - 200 ? $"{count}/{PeerMessageCrypto.MaxChatChars} characters" : null);
    }

    /// The pill and send button, plus a line under them for the counter or a send error when there is one.
    private float InputBarHeight() => Layout.Scaled(54f) + (InputHint() is null ? 0f : ImGui.GetFontSize() * 0.8f + Layout.Scaled(4f));

    private void DrawInputBar(ChatConversation conversation, PairingState? pairing, LfpMatchState? match)
    {
        var height = InputBarHeight();
        var start = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(start, start + new Vector2(width, height), ImGui.GetColorU32(BarBg));

        var pad = Layout.Scaled(10f);
        var button = Layout.Scaled(34f);
        var fieldHeight = button;
        var count = draft.Trim().Length;
        var over = count > PeerMessageCrypto.MaxChatChars;
        var hint = InputHint();
        var rowHeight = Layout.Scaled(54f);

        // The single-line field is vertically padded into a pill the same height as the send button.
        var framePadY = (fieldHeight - ImGui.GetTextLineHeight()) / 2;
        ImGui.SetCursorScreenPos(start + new Vector2(pad, (rowHeight - button) / 2));
        if (match is not null) DrawMatchActions(match, button);
        else DrawAttach(pairing!, button);
        var fieldX = pad + button + Layout.Scaled(6f);
        ImGui.SetCursorScreenPos(start + new Vector2(fieldX, (rowHeight - fieldHeight) / 2));
        using (ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, fieldHeight / 2))
        using (ImRaii.PushStyle(ImGuiStyleVar.FramePadding, new Vector2(Layout.Scaled(14f), framePadY)))
        using (ImRaii.PushColor(ImGuiCol.FrameBg, Incoming))
        using (ImRaii.PushColor(ImGuiCol.FrameBgHovered, Incoming with { X = Incoming.X + 0.03f, Y = Incoming.Y + 0.03f, Z = Incoming.Z + 0.03f }))
        using (ImRaii.PushColor(ImGuiCol.FrameBgActive, Incoming with { X = Incoming.X + 0.05f, Y = Incoming.Y + 0.05f, Z = Incoming.Z + 0.05f }))
        using (ImRaii.PushColor(ImGuiCol.Border, Vector4.Zero))
        {
            if (refocusInput)
            {
                ImGui.SetKeyboardFocusHere();
                refocusInput = false;
            }
            ImGui.SetNextItemWidth(width - fieldX - pad * 2 - button);
            // The buffer is in bytes; the character limit is checked on send so multi-byte text isn't cut early.
            var entered = ImGui.InputTextWithHint("##chatInput", $"Message {Name(conversation)}", ref draft, 4096, ImGuiInputTextFlags.EnterReturnsTrue);
            TutorialService.Anchor(TutorialAnchors.ChatInput);
            if (entered) Send(pairing, match);
        }

        var canSend = count > 0 && !over;
        ImGui.SetCursorScreenPos(start + new Vector2(width - pad - button, (rowHeight - button) / 2));
        var center = ImGui.GetCursorScreenPos() + new Vector2(button / 2);
        if (ImGui.InvisibleButton("send", new Vector2(button)) && canSend) Send(pairing, match);
        var hovered = ImGui.IsItemHovered();
        var fill = canSend ? (hovered ? Theme.AccentHover : Theme.Accent) : Incoming;
        draw.AddCircleFilled(center, button / 2, ImGui.GetColorU32(fill));
        IconAt(draw, FontAwesomeIcon.PaperPlane, ImGui.GetFontSize() * 0.95f, center, canSend ? new Vector4(1, 1, 1, 1) : Theme.TextMuted);
        if (hovered) Tip(over ? $"Too long - at most {PeerMessageCrypto.MaxChatChars} characters." : "Send (Enter)");

        if (hint is not null)
        {
            var small = ImGui.GetFontSize() * 0.8f;
            var color = sendError is not null || over ? Theme.Error : Theme.TextMuted;
            draw.AddText(ImGui.GetFont(), small, start + new Vector2(fieldX + Layout.Scaled(14f), rowHeight - Layout.Scaled(4f)), ImGui.GetColorU32(color), hint);
        }

        ImGui.SetCursorScreenPos(start + new Vector2(0, height));
    }

    /// A paperclip that offers a picture from a file or a snapshot of the game screen. Whatever's typed goes with it as
    /// its caption.
    private void DrawAttach(PairingState pairing, float size)
    {
        var attach = GhostIcon("attach", FontAwesomeIcon.Paperclip, size, Theme.TextMuted, false);
        TutorialService.Anchor(TutorialAnchors.ChatAttach);
        if (attach) ImGui.OpenPopup("attachMenu");
        if (ImGui.IsItemHovered()) Tip("Send a picture");

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, PopupPadding);
        var open = ImGui.BeginPopup("attachMenu");
        ImGui.PopStyleVar();
        if (!open) return;
        var pairingId = pairing.Id;
        if (ImGui.Selectable("Choose a picture..."))
        {
            plugin.FileDialogManager.OpenFileDialog("Send a picture", "Pictures{.png,.jpg,.jpeg}", (ok, path) =>
            {
                if (ok) SendPicture(pairingId, path, deleteSource: false);
            });
        }
        if (ImGui.Selectable("Take a snapshot of the game"))
        {
            plugin.Snapshot.Open(file => SendPicture(pairingId, Path.Combine(ImageTile.Folder, file), deleteSource: true));
        }
        ImGui.EndPopup();
    }

    private void SendPicture(Guid pairingId, string path, bool deleteSource)
    {
        if (plugin.Configuration.FindPairingById(pairingId) is not { IsPaired: true } pairing) return;
        Chat.SendImage(pairing, path, draft, deleteSource);
        draft = "";
        sendError = null;
        lastMessageCount = -1;
        refocusInput = true;
    }

    /// Share the character, or send a pairing invite made for this match. A match never sends pictures.
    private void DrawMatchActions(LfpMatchState match, float size)
    {
        var open = GhostIcon("matchActions", FontAwesomeIcon.Plus, size, Theme.TextMuted, false);
        if (open) ImGui.OpenPopup("matchMenu");
        if (ImGui.IsItemHovered()) Tip("Share your character or invite them to pair");

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, PopupPadding);
        var menu = ImGui.BeginPopup("matchMenu");
        ImGui.PopStyleVar();
        if (!menu) return;
        if (ImGui.Selectable(match.SharedMyCharacter ? "Share my character again" : "Share my character"))
        {
            sendError = Chat.ShareCharacter(match);
            lastMessageCount = -1;
        }
        if (ImGui.IsItemHovered()) Tip("Sends the character you're logged in on - name and world - to this match only.");
        var role = plugin.Configuration.Role;
        if (role != PluginRole.Sub && ImGui.Selectable("Invite them to pair as my Sub")) Invite(match, PairingDirection.OwnerSide);
        if (role != PluginRole.Owner && ImGui.Selectable("Invite them to pair as my Owner")) Invite(match, PairingDirection.SubSide);
        ImGui.EndPopup();
    }

    /// Makes a pairing code (replacing any unused one, as in Settings) and sends it sealed to the match.
    private void Invite(LfpMatchState match, PairingDirection mine)
    {
        if (CodePairingService.CurrentCharacter(plugin.Configuration.TriggerPhrase) is not { } self)
        {
            sendError = "Log in to a character first.";
            return;
        }
        var receiverRole = mine == PairingDirection.OwnerSide ? "sub" : "owner";
        var service = plugin.CodePairingService;
        Plugin.FireAndForget(System.Threading.Tasks.Task.Run(async () =>
        {
            var code = await service.CreateCodeAsync(mine, self, System.Threading.CancellationToken.None).ConfigureAwait(false);
            await Plugin.Framework.RunOnFrameworkThread(() =>
            {
                if (code is null)
                {
                    sendError = service.LastError ?? "The pairing code couldn't be made.";
                    return;
                }
                Chat.SendMatchInvite(match, code, receiverRole);
                lastMessageCount = -1;
            }).ConfigureAwait(false);
        }));
    }

    private void Send(PairingState? pairing, LfpMatchState? match)
    {
        if (draft.Trim().Length == 0) return;
        sendError = match is not null ? Chat.SendMatch(match, draft) : Chat.Send(pairing!, draft);
        if (sendError is null) draft = "";
        refocusInput = true;
    }

    // ---- pieces -----------------------------------------------------------------------------------------------------

    /// The window runs with zero padding so its panes reach the edges; popups and tooltips get it back.
    private static Vector2 PopupPadding => new(Layout.Scaled(8f), Layout.Scaled(6f));

    private static void Tip(string text)
    {
        using var _ = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, PopupPadding);
        ImGui.SetTooltip(text);
    }

    private static string Name(ChatConversation conversation) =>
        string.IsNullOrWhiteSpace(conversation.PeerName) ? "Partner" : conversation.PeerName;

    /// "your Owner", "your Sub", or both when the two of you are each other's.
    private static string Role(ChatConversation conversation, IReadOnlyList<PairingState> pairings)
    {
        if (conversation.IsMatch) return "your match";
        var directions = pairings.Count > 0 ? pairings.Select(p => p.Direction).Distinct().ToList() : [conversation.Direction];
        if (directions.Count > 1) return "your Owner and Sub";
        return directions[0] == PairingDirection.OwnerSide ? "your Sub" : "your Owner";
    }

    private static void Avatar(ImDrawListPtr draw, Vector2 center, float radius, ChatConversation conversation, bool ended)
    {
        var name = Name(conversation);
        var hash = 0;
        foreach (var c in name) hash = hash * 31 + c;
        var color = AvatarColors[(hash & 0x7fffffff) % AvatarColors.Length];
        if (ended) color = color with { W = 0.45f };
        draw.AddCircleFilled(center, radius, ImGui.GetColorU32(color));
        var initial = char.ToUpperInvariant(name.TrimStart().FirstOrDefault('?')).ToString();
        var size = radius * 1.05f;
        var textSize = ImGui.CalcTextSize(initial) * (size / ImGui.GetFontSize());
        draw.AddText(ImGui.GetFont(), size, center - textSize / 2, ImGui.GetColorU32(new Vector4(1, 1, 1, ended ? 0.6f : 1f)), initial);
    }

    private static void Badge(ImDrawListPtr draw, Vector2 center, int unread)
    {
        var text = unread > 99 ? "99+" : unread.ToString();
        var small = ImGui.GetFontSize() * 0.78f;
        var textSize = ImGui.CalcTextSize(text) * 0.78f;
        var radius = MathF.Max(textSize.Y * 0.6f, textSize.X * 0.5f + Layout.Scaled(3f));
        draw.AddCircleFilled(center, radius + Layout.Scaled(1.5f), ImGui.GetColorU32(BarBg));
        draw.AddCircleFilled(center, radius, ImGui.GetColorU32(Theme.Notify));
        draw.AddText(ImGui.GetFont(), small, center - textSize / 2, ImGui.GetColorU32(new Vector4(1, 1, 1, 1)), text);
    }

    /// Draws a Font Awesome glyph centered on its drawn shape: centering its advance box sits most icons visibly low or
    /// off to one side.
    private static unsafe void IconAt(ImDrawListPtr draw, FontAwesomeIcon icon, float size, Vector2 center, Vector4 color)
    {
        var text = icon.ToIconString();
        var font = Plugin.PluginInterface.UiBuilder.FontIcon;
        Vector2 glyphMin, glyphMax;
        using (ImRaii.PushFont(font))
        {
            var glyph = ImGui.GetFont().FindGlyph(text[0]);
            var scale = size / ImGui.GetFontSize();
            glyphMin = new Vector2(glyph->X0, glyph->Y0) * scale;
            glyphMax = new Vector2(glyph->X1, glyph->Y1) * scale;
        }
        var pos = center - (glyphMin + glyphMax) / 2;
        draw.AddText(font, size, new Vector2(MathF.Round(pos.X), MathF.Round(pos.Y)), ImGui.GetColorU32(color), text);
    }

    /// A borderless round icon button that only shows its background on hover (or while armed).
    private static bool GhostIcon(string id, FontAwesomeIcon icon, float size, Vector4 color, bool highlighted)
    {
        var pos = ImGui.GetCursorScreenPos();
        var clicked = ImGui.InvisibleButton(id, new Vector2(size));
        var hovered = ImGui.IsItemHovered();
        var draw = ImGui.GetWindowDrawList();
        var center = pos + new Vector2(size / 2);
        if (hovered || highlighted)
            draw.AddCircleFilled(center, size / 2, ImGui.GetColorU32(highlighted ? color with { W = 0.25f } : new Vector4(1, 1, 1, 0.08f)));
        IconAt(draw, icon, ImGui.GetFontSize() * 0.95f, center, hovered ? Theme.AccentHover : color);
        return clicked;
    }

    /// A borderless, square child with its own background; `draw` runs only while it's visible.
    private static void Pane(string id, Vector2 size, Vector4 background, float padding, Action draw)
    {
        using var color = ImRaii.PushColor(ImGuiCol.ChildBg, background);
        using var style = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(padding, padding))
            .Push(ImGuiStyleVar.ChildRounding, 0f)
            .Push(ImGuiStyleVar.ChildBorderSize, 0f);
        using var child = ImRaii.Child(id, size, false, padding > 0 ? ImGuiWindowFlags.AlwaysUseWindowPadding : ImGuiWindowFlags.None);
        if (child) draw();
    }

    private static void Centered(FontAwesomeIcon icon, string title, string body)
    {
        var avail = ImGui.GetContentRegionAvail();
        var wrap = MathF.Min(avail.X, Layout.Scaled(280f));
        var bodySize = ImGui.CalcTextSize(body, false, wrap);
        var iconSize = ImGui.GetFontSize() * 2.2f;
        var total = iconSize + ImGui.GetTextLineHeightWithSpacing() * 1.5f + bodySize.Y;
        var origin = ImGui.GetCursorPos();
        var top = origin.Y + MathF.Max(0, (avail.Y - total) / 2);
        var draw = ImGui.GetWindowDrawList();
        var screen = ImGui.GetCursorScreenPos() + new Vector2(0, top - origin.Y);

        var glyphText = icon.ToIconString();
        Vector2 glyph;
        using (ImRaii.PushFont(Plugin.PluginInterface.UiBuilder.FontIcon))
            glyph = ImGui.CalcTextSize(glyphText) * (iconSize / ImGui.GetFontSize());
        draw.AddText(Plugin.PluginInterface.UiBuilder.FontIcon, iconSize, screen + new Vector2((avail.X - glyph.X) / 2, 0), ImGui.GetColorU32(Theme.Accent), glyphText);

        ImGui.SetCursorPos(new Vector2(origin.X + (avail.X - ImGui.CalcTextSize(title).X) / 2, top + iconSize + Layout.Scaled(8f)));
        ImGui.TextUnformatted(title);
        ImGui.SetCursorPosX(origin.X + (avail.X - bodySize.X) / 2);
        using (ImRaii.PushColor(ImGuiCol.Text, Theme.TextMuted))
        {
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + wrap);
            ImGui.TextUnformatted(body);
            ImGui.PopTextWrapPos();
        }
    }
}
