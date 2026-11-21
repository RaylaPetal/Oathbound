using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Oathbound.Plugin.Config;
using Oathbound.Plugin.Relay;

namespace Oathbound.Plugin.UI;

/// 1:1 relay chat with each paired partner, one tab per pairing. Ended pairings keep a read-only tab until cleared.
public sealed class ChatWindow : Window, IDisposable
{
    private static readonly Vector4 OutgoingBubble = Theme.Accent with { W = 0.55f };
    private static readonly Vector4 IncomingBubble = Theme.TileBg;
    private const float BubbleWidthShare = 0.78f;

    private readonly Plugin plugin;
    private ChatService Chat => plugin.Chat;

    private Guid? selected;
    /// Set when something asks for a conversation; consumed by the next tab bar so that tab is selected once.
    private Guid? requested;
    private string draft = "";
    private string? sendError;
    private int lastMessageCount = -1;
    private bool refocusInput;
    private DateTime clearArmedUntil = DateTime.MinValue;

    public ChatWindow(Plugin plugin) : base("Chat###OathboundChatWindow")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(360, 340), MaximumSize = new Vector2(float.MaxValue, float.MaxValue) };
        Size = new Vector2(440, 540);
        SizeCondition = ImGuiCond.FirstUseEver;
        plugin.Chat.OpenRequested += Open;
    }

    public void Dispose() => plugin.Chat.OpenRequested -= Open;

    public void Open(Guid pairingId)
    {
        requested = pairingId;
        IsOpen = true;
        BringToFront();
    }

    public override void PreDraw() => Theme.PushWindowStyle();
    public override void PostDraw() => Theme.PopWindowStyle();

    public override void OnClose() => Chat.FocusedPairingId = null;

    public override void Draw()
    {
        var conversations = Chat.Conversations();
        if (conversations.Count == 0)
        {
            Chat.FocusedPairingId = null;
            DrawCentered(FontAwesomeIcon.Comments, "No one to chat with yet",
                "Chat is for paired Owners and Subs. Pair from Settings, then talk here - even across worlds and data centers.");
            return;
        }

        // One partner: no tab strip, so the header carries their name instead.
        if (conversations.Count == 1)
        {
            requested = null;
            Select(conversations[0].Conversation.PairingId);
            DrawConversation(conversations[0].Conversation, conversations[0].Pairings, showName: true);
            return;
        }

        using var tabs = ImRaii.TabBar("chatTabs", ImGuiTabBarFlags.FittingPolicyScroll);
        if (!tabs) return;
        foreach (var (conversation, pairings) in conversations)
        {
            var flags = requested == conversation.PairingId ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
            if (conversation.Unread > 0) flags |= ImGuiTabItemFlags.UnsavedDocument;
            using var tab = ImRaii.TabItem($"{TabName(conversation, pairings)}###chat{conversation.PairingId:N}", flags);
            if (!tab) continue;
            if (requested == conversation.PairingId) requested = null;
            Select(conversation.PairingId);
            DrawConversation(conversation, pairings, showName: false);
        }
        if (requested is { } r && conversations.All(c => c.Conversation.PairingId != r)) requested = null;
    }

    private void Select(Guid pairingId)
    {
        if (selected == pairingId) return;
        selected = pairingId;
        draft = "";
        sendError = null;
        lastMessageCount = -1;
        clearArmedUntil = DateTime.MinValue;
    }

    private static string Name(ChatConversation conversation) =>
        string.IsNullOrWhiteSpace(conversation.PeerName) ? "Partner" : conversation.PeerName;

    private static string TabName(ChatConversation conversation, IReadOnlyList<PairingState> pairings) =>
        pairings.Count == 0 ? $"{Name(conversation)} (ended)" : Name(conversation);

    /// "your Owner", "your Sub", or both when the two of you are each other's.
    private static string Role(ChatConversation conversation, IReadOnlyList<PairingState> pairings)
    {
        var directions = pairings.Count > 0 ? pairings.Select(p => p.Direction).Distinct().ToList() : [conversation.Direction];
        if (directions.Count > 1) return "your Owner and Sub";
        return directions[0] == PairingDirection.OwnerSide ? "your Sub" : "your Owner";
    }

    // ---- conversation -----------------------------------------------------------------------------------------------

    private void DrawConversation(ChatConversation conversation, IReadOnlyList<PairingState> pairings, bool showName)
    {
        var pairing = Chat.SendPairing(pairings);
        var focused = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows);
        Chat.FocusedPairingId = focused ? conversation.PairingId : null;
        if (focused) Chat.MarkRead(conversation.PairingId);

        DrawHeader(conversation, pairings, pairing, showName);

        var style = ImGui.GetStyle();
        var footer = pairing is null ? 0f : ImGui.GetFrameHeight() + style.ItemSpacing.Y * 2 + (sendError is null ? 0f : ImGui.GetTextLineHeightWithSpacing());
        using (ImRaii.PushColor(ImGuiCol.ChildBg, Theme.CardBg))
        using (ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, Layout.Scaled(6f)))
        using (ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(Layout.Scaled(10f), Layout.Scaled(8f))))
        using (var child = ImRaii.Child("messages", new Vector2(0, -footer), false, ImGuiWindowFlags.AlwaysUseWindowPadding))
        {
            if (child) DrawMessages(conversation);
        }

        if (pairing is not null) DrawInput(conversation, pairing);
    }

    /// One compact row: the name only when no tab already shows it, then world and role, and Clear history on the
    /// right; a status line under it only when something needs saying.
    private void DrawHeader(ChatConversation conversation, IReadOnlyList<PairingState> pairings, PairingState? pairing, bool showName)
    {
        if (showName)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, Theme.AccentHover))
                ImGui.TextUnformatted(Name(conversation));
            ImGui.SameLine();
        }
        var details = string.IsNullOrWhiteSpace(conversation.PeerWorld)
            ? Role(conversation, pairings)
            : $"{conversation.PeerWorld}  ·  {Role(conversation, pairings)}";
        using (ImRaii.PushColor(ImGuiCol.Text, Theme.TextMuted))
            ImGui.TextUnformatted(details);

        if (conversation.Messages.Count > 0)
        {
            var trashSize = ImGui.GetTextLineHeight() + ImGui.GetStyle().FramePadding.Y;
            ImGui.SameLine(ImGui.GetWindowContentRegionMax().X - trashSize);
            var armed = DateTime.UtcNow < clearArmedUntil;
            using (ImRaii.PushColor(ImGuiCol.Button, Vector4.Zero, !armed))
            using (ImRaii.PushColor(ImGuiCol.Button, Theme.Danger, armed))
            {
                if (IconGlyph.CenteredButton("clearChat", FontAwesomeIcon.Trash, trashSize, armed ? new Vector4(1, 1, 1, 1) : Theme.TextMuted))
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
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(armed
                    ? "Click again to delete this conversation from this computer."
                    : "Clear history - deletes this conversation from this computer only. Your partner keeps their copy.");
        }

        if (pairing is null)
            Status(Theme.TextMuted, FontAwesomeIcon.Lock, "Your pairing has ended - this chat is read-only.");
        else if (conversation.PeerNeedsUpdate)
            Status(Theme.Warning, FontAwesomeIcon.ExclamationTriangle, $"{Name(conversation)} needs to update Oathbound before they can chat.");
        else if (!plugin.PeerMessenger.Connected)
            Status(Theme.Warning, FontAwesomeIcon.Wifi, "Reconnecting to the relay - messages can't be sent until it's back.");
        ImGui.Spacing();
    }

    private static void Status(Vector4 color, FontAwesomeIcon icon, string text)
    {
        using var _ = ImRaii.PushColor(ImGuiCol.Text, color);
        IconGlyph.Text(icon, text);
    }

    // ---- messages ---------------------------------------------------------------------------------------------------

    private void DrawMessages(ChatConversation conversation)
    {
        if (conversation.Messages.Count == 0)
        {
            DrawCentered(FontAwesomeIcon.Lock, "Say hi!",
                $"Messages are end-to-end encrypted. If {conversation.PeerName} is offline, they wait on the relay for up to 7 days.");
            lastMessageCount = 0;
            return;
        }

        DateTime? lastDay = null;
        ChatEntry? previous = null;
        var messages = conversation.Messages;
        for (var i = 0; i < messages.Count; i++)
        {
            var entry = messages[i];
            var when = DateTimeOffset.FromUnixTimeSeconds(entry.At).LocalDateTime;
            if (lastDay != when.Date)
            {
                DaySeparator(when.Date);
                lastDay = when.Date;
                previous = null;
            }

            // A run of messages from the same side within a few minutes reads as one block: tighter, one timestamp.
            var next = i + 1 < messages.Count ? messages[i + 1] : null;
            var continuesRun = previous is not null && previous.Outgoing == entry.Outgoing && entry.At - previous.At < 300;
            var endsRun = next is null || next.Outgoing != entry.Outgoing || next.At - entry.At >= 300
                || DateTimeOffset.FromUnixTimeSeconds(next.At).LocalDateTime.Date != when.Date;
            if (previous is not null && !continuesRun) ImGui.Dummy(new Vector2(0, Layout.Scaled(4f)));

            ImGui.PushID(entry.LocalId.GetHashCode());
            Bubble(entry);
            if (endsRun || entry.State == ChatEntryState.NotDelivered) Meta(entry, when);
            ImGui.PopID();
            previous = entry;
        }

        if (messages.Count != lastMessageCount)
        {
            ImGui.SetScrollHereY(1f);
            lastMessageCount = messages.Count;
        }
    }

    private static void DaySeparator(DateTime day)
    {
        var today = DateTime.Now.Date;
        var label = day == today ? "Today" : day == today.AddDays(-1) ? "Yesterday" : day.ToString(day.Year == today.Year ? "dddd, MMM d" : "MMM d, yyyy");
        ImGui.Dummy(new Vector2(0, Layout.Scaled(2f)));
        var width = ImGui.GetContentRegionAvail().X;
        var textSize = ImGui.CalcTextSize(label);
        var start = ImGui.GetCursorScreenPos();
        var y = start.Y + textSize.Y / 2;
        var gap = Layout.Scaled(8f);
        var lineColor = ImGui.GetColorU32(Theme.TextMuted with { W = 0.25f });
        var draw = ImGui.GetWindowDrawList();
        var textX = start.X + (width - textSize.X) / 2;
        draw.AddLine(new Vector2(start.X, y), new Vector2(textX - gap, y), lineColor);
        draw.AddLine(new Vector2(textX + textSize.X + gap, y), new Vector2(start.X + width, y), lineColor);
        ImGui.SetCursorScreenPos(new Vector2(textX, start.Y));
        using (ImRaii.PushColor(ImGuiCol.Text, Theme.TextMuted))
            ImGui.TextUnformatted(label);
        ImGui.Dummy(new Vector2(0, Layout.Scaled(2f)));
    }

    /// Drawn by hand so it can sit on either side and size to its text; right-click copies the text.
    private static void Bubble(ChatEntry entry)
    {
        var pad = new Vector2(Layout.Scaled(10f), Layout.Scaled(6f));
        var lineStart = ImGui.GetCursorScreenPos();
        var avail = ImGui.GetContentRegionAvail().X;
        var wrap = MathF.Max(avail * BubbleWidthShare - pad.X * 2, Layout.Scaled(60f));
        var textSize = ImGui.CalcTextSize(entry.Text, false, wrap);
        var bubble = textSize + pad * 2;
        var min = new Vector2(entry.Outgoing ? lineStart.X + avail - bubble.X : lineStart.X, lineStart.Y);
        var max = min + bubble;

        var color = entry.Outgoing ? OutgoingBubble : IncomingBubble;
        if (entry.State == ChatEntryState.NotDelivered) color = Theme.Danger with { W = 0.45f };
        else if (entry.State == ChatEntryState.Sending) color = color with { W = color.W * 0.6f };
        ImGui.GetWindowDrawList().AddRectFilled(min, max, ImGui.GetColorU32(color), Layout.Scaled(9f));

        ImGui.SetCursorScreenPos(min + pad);
        var localX = ImGui.GetCursorPosX();
        ImGui.PushTextWrapPos(localX + wrap);
        ImGui.TextUnformatted(entry.Text);
        ImGui.PopTextWrapPos();

        ImGui.SetCursorScreenPos(min);
        ImGui.InvisibleButton("bubble", bubble);
        if (ImGui.BeginPopupContextItem("bubbleMenu"))
        {
            if (ImGui.Selectable("Copy text"))
                ImGui.SetClipboardText(entry.Text);
            ImGui.EndPopup();
        }
        ImGui.SetCursorScreenPos(new Vector2(lineStart.X, max.Y + Layout.Scaled(2f)));
        ImGui.Dummy(Vector2.Zero);
    }

    private static void Meta(ChatEntry entry, DateTime when)
    {
        var parts = new List<string> { when.ToString("HH:mm") };
        if (entry.Outgoing)
        {
            parts.Add(entry.State switch
            {
                ChatEntryState.Sending => "sending…",
                ChatEntryState.Waiting => "waiting - delivered when they're online",
                ChatEntryState.Delivered => "delivered",
                ChatEntryState.NotDelivered => $"not delivered - {entry.Reason ?? "unknown reason"}",
                _ => "",
            });
        }
        var text = string.Join("  ·  ", parts.Where(p => p.Length > 0));
        var avail = ImGui.GetContentRegionAvail().X;
        var color = entry.State == ChatEntryState.NotDelivered ? Theme.StatusMissing : Theme.TextMuted;
        using var _ = ImRaii.PushColor(ImGuiCol.Text, color);
        var size = ImGui.CalcTextSize(text, false, avail * BubbleWidthShare);
        if (entry.Outgoing) ImGui.SetCursorPosX(ImGui.GetCursorPosX() + avail - size.X);
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + avail * BubbleWidthShare);
        ImGui.TextUnformatted(text);
        ImGui.PopTextWrapPos();
        ImGui.Dummy(new Vector2(0, Layout.Scaled(2f)));
    }

    // ---- input ------------------------------------------------------------------------------------------------------

    private void DrawInput(ChatConversation conversation, PairingState pairing)
    {
        ImGui.Dummy(new Vector2(0, ImGui.GetStyle().ItemSpacing.Y * 0.5f));
        if (sendError is not null)
            Status(Theme.StatusMissing, FontAwesomeIcon.ExclamationCircle, sendError);

        var count = draft.Trim().Length;
        var over = count > PeerMessageCrypto.MaxChatChars;
        var counter = count > PeerMessageCrypto.MaxChatChars - 200 ? $"{count}/{PeerMessageCrypto.MaxChatChars}" : null;
        var sendSize = ImGui.GetFrameHeight();
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var counterWidth = counter is null ? 0f : ImGui.CalcTextSize(counter).X + spacing;

        if (refocusInput)
        {
            ImGui.SetKeyboardFocusHere();
            refocusInput = false;
        }
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - sendSize - spacing - counterWidth);
        // The buffer is in bytes; the character limit is checked on send so multi-byte text isn't cut early.
        var enter = ImGui.InputTextWithHint("##chatInput", $"Message {conversation.PeerName}…", ref draft, 4096, ImGuiInputTextFlags.EnterReturnsTrue);

        if (counter is not null)
        {
            ImGui.SameLine();
            using (ImRaii.PushColor(ImGuiCol.Text, over ? Theme.StatusMissing : Theme.TextMuted))
                ImGui.TextUnformatted(counter);
        }

        ImGui.SameLine();
        var canSend = draft.Trim().Length > 0;
        bool click;
        using (ImRaii.Disabled(!canSend))
        using (ImRaii.PushColor(ImGuiCol.Button, Theme.Accent, canSend))
        using (ImRaii.PushColor(ImGuiCol.ButtonHovered, Theme.AccentHover, canSend))
            click = IconGlyph.CenteredButton("sendChat", FontAwesomeIcon.PaperPlane, sendSize, new Vector4(1, 1, 1, 1));
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Send (Enter)");

        if ((enter || click) && canSend)
        {
            sendError = Chat.Send(pairing, draft);
            if (sendError is null) draft = "";
            refocusInput = true;
        }
    }

    private static void DrawCentered(FontAwesomeIcon icon, string title, string body)
    {
        var avail = ImGui.GetContentRegionAvail();
        var wrap = MathF.Min(avail.X, Layout.Scaled(300f));
        var bodySize = ImGui.CalcTextSize(body, false, wrap);
        var lineHeight = ImGui.GetTextLineHeightWithSpacing();
        var total = lineHeight * 2 + bodySize.Y;
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + MathF.Max(0, (avail.Y - total) / 2));

        var startX = ImGui.GetCursorPosX();
        using (ImRaii.PushColor(ImGuiCol.Text, Theme.Accent))
        using (ImRaii.PushFont(Plugin.PluginInterface.UiBuilder.FontIcon))
        {
            var iconText = icon.ToIconString();
            ImGui.SetCursorPosX(startX + (avail.X - ImGui.CalcTextSize(iconText).X) / 2);
            ImGui.TextUnformatted(iconText);
        }
        ImGui.SetCursorPosX(startX + (avail.X - ImGui.CalcTextSize(title).X) / 2);
        ImGui.TextUnformatted(title);
        ImGui.SetCursorPosX(startX + (avail.X - bodySize.X) / 2);
        using (ImRaii.PushColor(ImGuiCol.Text, Theme.TextMuted))
        {
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + wrap);
            ImGui.TextUnformatted(body);
            ImGui.PopTextWrapPos();
        }
    }
}
