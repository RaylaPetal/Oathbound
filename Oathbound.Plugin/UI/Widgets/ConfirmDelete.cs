using System;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace Oathbound.Plugin.UI;

/// A Delete that asks first. The button and its popup are drawn in the same call, so they share one ID stack even
/// inside PushID rows and table cells.
public static class ConfirmDelete
{
    private const string PopupId = "confirmDelete";

    /// True only in the frame the popup's Delete is pressed. `detail` adds what else goes with it.
    public static bool Button(string id, string itemName, string label = "Delete", string? detail = null, bool small = false)
    {
        ImGui.PushID(id);
        if (small ? ImGui.SmallButton(label) : ImGui.Button(label))
            ImGui.OpenPopup(PopupId);
        var confirmed = Popup($"Delete \"{itemName}\"?", detail, "Delete");
        ImGui.PopID();
        return confirmed;
    }

    /// Removes only what came from the Sub; disabled when there's nothing imported.
    public static bool ClearImported(string id, int count, string noun)
    {
        ImGui.PushID(id);
        using (ImRaii.Disabled(count == 0))
        {
            if (ImGui.Button("Clear imported"))
                ImGui.OpenPopup(PopupId);
        }
        if (count == 0 && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip($"No {noun} from your Sub to clear.");
        var confirmed = Popup($"Remove the {count} {noun} imported from your Sub?",
            "Anything you wrote or saved yourself stays. A later sync brings the imported ones back.", "Clear imported");
        ImGui.PopID();
        return confirmed;
    }

    public static float ClearImportedWidth => Layout.ButtonWidth("Clear imported");

    /// A button filled in the destructive colour, for the step that confirms an end, revert or delete.
    public static bool DestructiveButton(string label, bool small = false)
    {
        using var colors = PushDestructive();
        return small ? ImGui.SmallButton(label) : ImGui.Button(label);
    }

    public static IDisposable PushDestructive() => ImRaii.PushColor(ImGuiCol.Button, Theme.Destructive)
        .Push(ImGuiCol.ButtonHovered, Theme.Destructive with { X = Math.Min(1f, Theme.Destructive.X + 0.12f) })
        .Push(ImGuiCol.ButtonActive, Theme.Destructive with { X = Math.Min(1f, Theme.Destructive.X + 0.2f) });

    private static bool Popup(string question, string? detail, string confirmLabel)
    {
        if (!ImGui.BeginPopup(PopupId))
            return false;
        var confirmed = false;
        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 22f);
        ImGui.TextUnformatted(question);
        if (detail is not null)
            ImGui.TextDisabled(detail);
        ImGui.PopTextWrapPos();
        if (DestructiveButton(confirmLabel))
        {
            confirmed = true;
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
            ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
        return confirmed;
    }
}
