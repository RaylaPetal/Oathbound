using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Oathbound.Plugin.Config;

namespace Oathbound.Plugin.UI;

/// The Owner's saved commands, searchable and grouped by category, for attaching one to something else.
public sealed class CommandPickerWindow : Window, IDisposable
{
    private const string AllCategories = "All";

    private readonly Plugin plugin;
    private string search = "";
    private string category = AllCategories;
    private string purpose = "";
    private Action<QuickCommand>? onSelected;

    public CommandPickerWindow(Plugin plugin) : base("Choose a command###CollarCommandPicker")
    {
        this.plugin = plugin;
        Flags = ImGuiWindowFlags.NoCollapse;
        Size = new Vector2(440, 480);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(320, 260), MaximumSize = new Vector2(800, 1000) };
    }

    /// `purpose` is one line saying what the command is for.
    public void Open(string purpose, Action<QuickCommand> selected)
    {
        this.purpose = purpose;
        onSelected = selected;
        IsOpen = true;
    }

    public void Dispose() { }

    public override void PreDraw() => Theme.PushWindowStyle();
    public override void PostDraw() => Theme.PopWindowStyle();

    public override void OnClose() => onSelected = null;

    public override void Draw()
    {
        IconGlyph.Text(FontAwesomeIcon.Terminal, "Your commands");
        if (purpose.Length > 0)
            IconGlyph.WrappedDisabled(purpose);
        ImGui.Separator();

        var groups = QuickAccessMenu.CategorizedAll(plugin.Configuration.QuickCommands).Where(g => g.Commands.Count > 0).ToList();
        if (groups.Count == 0)
        {
            IconGlyph.WrappedDisabled("No saved commands yet. Save some in the modules first.");
            return;
        }

        var categories = new[] { AllCategories }.Concat(groups.Select(g => g.Label)).ToArray();
        var index = Math.Max(0, Array.IndexOf(categories, category));
        Layout.ItemWidth(160);
        if (ImGui.Combo("##commandPickerCategory", ref index, categories, categories.Length))
            category = categories[index];
        ImGui.SameLine();
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##commandPickerSearch", "Search commands...", ref search, 128);

        var filter = search.Trim();
        var visible = groups
            .Where(g => category == AllCategories || g.Label == category)
            .Select(g => (g.Label, Commands: g.Commands.Where(c => filter.Length == 0 || c.Label.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList()))
            .Where(g => g.Commands.Count > 0)
            .ToList();
        var total = groups.Sum(g => g.Commands.Count);
        IconGlyph.WrappedDisabled($"{visible.Sum(g => g.Commands.Count)} shown / {total} commands");
        ImGui.Separator();

        using var list = ImRaii.Child("##commandPickerList", new Vector2(-1, -1));
        if (!list)
            return;
        if (visible.Count == 0)
        {
            IconGlyph.WrappedDisabled("No commands match this search.");
            return;
        }
        foreach (var (label, commands) in visible)
        {
            Section.SubHeading(label);
            foreach (var cmd in commands)
            {
                if (ImGui.Button($"Choose##commandPicker_{label}_{cmd.Label}_{cmd.Command}"))
                {
                    onSelected?.Invoke(cmd);
                    IsOpen = false;
                }
                ImGui.SameLine();
                ImGui.TextUnformatted(cmd.Label);
            }
        }
    }
}
