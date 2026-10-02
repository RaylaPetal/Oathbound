using System;
using System.Linq;
using System.Numerics;
using Oathbound.Plugin.Config;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace Oathbound.Plugin.UI;

/// Its own roomy window so mod/group/option names stay readable instead of squeezed into a combo.
public sealed class AnimationPickerWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private string search = "";
    private Action<GestureCatalogEntry>? onSelected;
    private Action<GestureExportEntry>? onImportedSelected;
    private bool importedMode;
    private bool includeTriggerless;

    public AnimationPickerWindow(Plugin plugin) : base("Add animation###CollarAnimationPicker")
    {
        this.plugin = plugin;
        Flags = ImGuiWindowFlags.NoCollapse;
        Size = new Vector2(720, 620);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(520, 420), MaximumSize = new Vector2(1100, 1000) };
    }

    public void Open(Action<GestureCatalogEntry> selected)
    {
        importedMode = false;
        includeTriggerless = false;
        onSelected = selected;
        onImportedSelected = null;
        IsOpen = true;
    }

    public void OpenImported(Action<GestureExportEntry> selected)
    {
        importedMode = true;
        includeTriggerless = false;
        onImportedSelected = selected;
        onSelected = null;
        IsOpen = true;
    }

    public void OpenForRestraint(Action<GestureCatalogEntry> selected)
    {
        Open(selected);
        includeTriggerless = true;
    }

    public void OpenImportedForRestraint(Action<GestureExportEntry> selected)
    {
        OpenImported(selected);
        includeTriggerless = true;
    }

    public void Dispose() { }

    public override void PreDraw() => Theme.PushWindowStyle();
    public override void PostDraw() => Theme.PopWindowStyle();

    public override void Draw()
    {
        IconGlyph.Text(FontAwesomeIcon.TheaterMasks, "Animation Library");
        ImGui.SameLine();
        IconGlyph.WrappedDisabled("Choose the exact named option and trigger for this alias.");
        ImGui.Separator();

        const float buttonWidth = 82;
        ImGui.SetNextItemWidth(Math.Max(180, ImGui.GetContentRegionAvail().X - buttonWidth - ImGui.GetStyle().ItemSpacing.X));
        ImGui.InputTextWithHint("##animationPickerSearch", "Search mod, group, animation, command, or pose...", ref search, 128);
        ImGui.SameLine();
        if (ImGui.Button("Rescan", new Vector2(buttonWidth, 0))) plugin.GestureCommand.Rescan();

        if (importedMode)
        {
            DrawImported();
            return;
        }

        var all = plugin.Configuration.GestureMapping.LocalCatalog.Values.ToList();
        if (all.Count == 0)
        {
            IconGlyph.WrappedColored(Theme.Warning, "No animations scanned. Select Penumbra mods in Settings, rescan, then return here.");
            return;
        }

        var filter = search.Trim();
        var visible = all.Where(e => filter.Length == 0
            || e.ModName.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || e.GroupName.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || e.AnimationName.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || (e.Trigger?.Label.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)).ToList();

        IconGlyph.WrappedDisabled($"{visible.Count} shown / {all.Count} discovered");
        ImGui.Separator();
        using var child = ImRaii.Child("animationPickerResults", Vector2.Zero, false);
        if (visible.Count == 0) { IconGlyph.WrappedDisabled("No animations match this search."); return; }

        foreach (var mod in visible.GroupBy(e => new { e.ModDirectory, e.ModName }).OrderBy(g => g.Key.ModName, StringComparer.OrdinalIgnoreCase))
        {
            var first = mod.First();
            var flags = filter.Length > 0 ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None;
            if (!ImGui.CollapsingHeader($"{mod.Key.ModName}##pickerMod_{mod.Key.ModDirectory}", flags)) continue;
            ImGui.Indent();
            IconGlyph.WrappedDisabled(first.ModEnabled ? "Enabled in Penumbra" : "Disabled in Penumbra — enabled temporarily when used");

            foreach (var group in mod.GroupBy(e => new { e.GroupName, e.GroupOrder }).OrderBy(g => g.Key.GroupOrder))
            {
                var groupOpen = group.Count() <= 4 || filter.Length > 0
                    ? ImGuiTreeNodeFlags.DefaultOpen
                    : ImGuiTreeNodeFlags.None;
                if (!ImGui.TreeNodeEx($"{group.Key.GroupName}##pickerGroup_{mod.Key.ModDirectory}_{group.Key.GroupOrder}", groupOpen)) continue;

                foreach (var option in group.GroupBy(e => new { e.AnimationName, e.OptionOrder }).OrderBy(g => g.Key.OptionOrder))
                {
                    ImGui.TextUnformatted(option.Key.AnimationName);
                    foreach (var entry in option.OrderBy(e => e.TriggerOrder))
                    {
                        ImGui.Indent();
                        if (entry.Trigger is null)
                        {
                            IconGlyph.WrappedDisabled("Enable this Penumbra option only (idle/walk)");
                            if (includeTriggerless)
                            {
                                ImGui.SameLine();
                                if (ImGui.SmallButton($"Choose##pickerChoose_{entry.Id}"))
                                {
                                    onSelected?.Invoke(entry);
                                    IsOpen = false;
                                }
                            }
                        }
                        else
                        {
                            IconGlyph.WrappedColored(Theme.Accent, entry.Trigger.Label);
                            ImGui.SameLine();
                            if (ImGui.SmallButton($"Choose##pickerChoose_{entry.Id}"))
                            {
                                onSelected?.Invoke(entry);
                                IsOpen = false;
                            }
                        }
                        ImGui.Unindent();
                    }
                }
                ImGui.TreePop();
            }
            ImGui.Unindent();
        }
    }

    private void DrawImported()
    {
        var all = plugin.Configuration.GestureMapping.ImportedPeerCatalog.Values.ToList();
        if (all.Count == 0)
        {
            IconGlyph.WrappedColored(Theme.Warning, "No Sub animation library imported. Import the Sub's catalog in Owner controls first.");
            return;
        }
        var filter = search.Trim();
        var visible = all.Where(e => (includeTriggerless || e.Trigger is not null) && (filter.Length == 0 || e.ModName.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || e.GroupName.Contains(filter, StringComparison.OrdinalIgnoreCase) || e.AnimationName.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || (e.Trigger?.Label.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false))).ToList();
        IconGlyph.WrappedDisabled($"{visible.Count} shown / {all.Count} imported from Sub");
        ImGui.Separator();
        using var child = ImRaii.Child("importedAnimationPickerResults", Vector2.Zero, false);
        foreach (var mod in visible.GroupBy(e => e.ModName).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!ImGui.CollapsingHeader($"{mod.Key}##imported_{mod.Key}", filter.Length > 0 ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None)) continue;
            foreach (var entry in mod.OrderBy(e => e.GroupOrder).ThenBy(e => e.OptionOrder))
            {
                if (ImGui.SmallButton($"Choose##importedChoose_{entry.Id}"))
                {
                    onImportedSelected?.Invoke(entry);
                    IsOpen = false;
                }
                ImGui.SameLine();
                var mode = entry.Trigger is null ? "Enable option only" : entry.Trigger.Label;
                var optionName = entry.AnimationName.Length <= 64 ? entry.AnimationName : $"{entry.AnimationName[..61]}...";
                ImGui.TextWrapped($"{optionName} · {mode}");
                if (ImGui.IsItemHovered() && entry.AnimationName.Length > 64)
                    ImGui.SetTooltip(entry.DisplayLabel);
            }
        }
    }
}
