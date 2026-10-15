using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Oathbound.Plugin.Commands;
using Oathbound.Plugin.Config;
using Oathbound.Plugin.Ipc;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Glamourer.Api.Enums;
using static Oathbound.Plugin.UI.Layout;

namespace Oathbound.Plugin.UI;

public sealed partial class ModuleWindow
{
    private string newTitleAlias = "";
    private string newTitleText = "";
    private bool newTitleIsPrefix;

    private Vector3 newTitleColor = new(1, 1, 1);
    private bool newTitleHasGlow;

    private Vector3 newTitleGlow = new(1, 1, 1);

    private string newTitleQuickText = "";
    private bool newTitleQuickIsPrefix;

    private Vector3 newTitleQuickColor = new(1, 1, 1);
    private bool newTitleQuickHasGlow;

    private Vector3 newTitleQuickGlow = new(1, 1, 1);

    private readonly ListDetail titleList = new();
    private const string NewTitleKey = "new:title";

    private void DrawTitleModule()
    {
        var config = plugin.Configuration;
        IconGlyph.Text(FontAwesomeIcon.Heading, "Title Aliases");
        ImGui.Separator();
        DrawFixedWordRow("titleFixed", new FixedWordInfo("Clear title", ControlWords.ClearTitle,
            "Removes your current title. It can't be renamed, so your Owner always knows it."));

        var titles = config.Aliases.Titles;
        var items = titles.Select((t, i) => new ListDetailItem($"title:{i}", t.Alias, t.IsPrefix ? "prefix" : null, Tooltip: t.Text)).ToList();
        if (titleList.Selected == NewTitleKey)
            items.Add(new ListDetailItem(NewTitleKey, "New title alias"));

        using (Section.Begin("titleAliases", "Your title aliases"))
        {
            titleList.Draw("titles", items, () =>
                {
                    if (!ImGui.Button("+ New##titleAlias"))
                        return;
                    newTitleAlias = "";
                    newTitleText = "";
                    titleList.Select(NewTitleKey);
                },
                item => DrawTitleAliasDetail(config, titles, item),
                () => titleList.Selected == NewTitleKey && (newTitleAlias.Trim().Length > 0 || newTitleText.Trim().Length > 0),
                null, "No title aliases yet. Use + New to add one.");
        }
    }

    /// How a title reads next to a name, in its own colour.
    private static void DrawTitlePreview(string text, bool isPrefix, Vector3 color)
    {
        ImGui.TextDisabled("Preview:");
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(color, 1f), isPrefix ? $"«{text}»" : "Your Name");
        ImGui.SameLine();
        ImGui.TextColored(isPrefix ? Theme.TextMuted : new Vector4(color, 1f), isPrefix ? "Your Name" : $"«{text}»");
    }

    private void DrawTitleAliasDetail(PluginConfig config, List<TitleAliasDefinition> titles, ListDetailItem? item)
    {
        if (item is null)
        {
            IconGlyph.WrappedDisabled("Choose a title alias, or use + New to add one.");
            return;
        }
        if (item.Key == NewTitleKey)
        {
            DrawTitleAddForm(config, titles);
            return;
        }
        if (!int.TryParse(item.Key["title:".Length..], out var index) || index >= titles.Count)
            return;

        var t = titles[index];
        ImGui.PushID($"titleAlias_{index}");
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.AccentHover, t.Alias);
        ImGui.SameLine();
        if (ImGui.Button("Delete"))
        {
            titles.RemoveAt(index);
            config.Save();
            titleList.Select(null);
            ImGui.PopID();
            return;
        }
        DrawTitlePreview(t.Text, t.IsPrefix, t.Color);

        if (DrawDeferredTextInput("Alias##titleEdit", (t, "alias"), t.Alias, 32, out var alias) && alias.Trim().Length > 0 && !IsReserved(alias))
        {
            t.Alias = alias.Trim();
            config.Save();
        }
        IconGlyph.HelpMarker("Short word the Owner types after the trigger phrase to apply this title.");
        if (DrawDeferredTextInput("Title text##titleEdit", (t, "text"), t.Text, 64, out var text) && text.Trim().Length > 0)
        {
            t.Text = text.Trim();
            config.Save();
        }
        var isPrefix = t.IsPrefix;
        if (ImGui.Checkbox("Prefix (not suffix)##titleEdit", ref isPrefix))
        {
            t.IsPrefix = isPrefix;
            config.Save();
        }
        var color = t.Color;
        if (ImGui.ColorEdit3("Color##titleEdit", ref color))
        {
            t.Color = color;
            config.Save();
        }
        var hasGlow = t.Glow is not null;
        var glow = t.Glow ?? new Vector3(1, 1, 1);
        var glowBefore = (hasGlow, glow);
        DrawGlowPicker("titleEdit", ref hasGlow, ref glow);
        if ((hasGlow, glow) != glowBefore)
        {
            t.Glow = hasGlow ? glow : null;
            config.Save();
        }
        ImGui.PopID();
    }

    private void DrawTitleAddForm(PluginConfig config, List<TitleAliasDefinition> titles)
    {
        using var box = Section.Begin("titleAdd", "Add a title alias");
        ImGui.InputText("Alias##newTitle", ref newTitleAlias, 32);
        IconGlyph.HelpMarker("Short word the Owner types after the trigger phrase to apply this title, e.g. \"command goodgirl\".");
        ImGui.InputText("Title text##newTitle", ref newTitleText, 64);
        IconGlyph.HelpMarker("The exact title text applied via Honorific.");
        ImGui.Checkbox("Prefix (not suffix)##newTitle", ref newTitleIsPrefix);
        IconGlyph.HelpMarker("Show the title before your name instead of after it.");
        ImGui.ColorEdit3("Color##newTitle", ref newTitleColor);
        IconGlyph.HelpMarker("Honorific title color.");
        DrawGlowPicker("newTitle", ref newTitleHasGlow, ref newTitleGlow);
        if (newTitleText.Trim().Length > 0)
            DrawTitlePreview(newTitleText.Trim(), newTitleIsPrefix, newTitleColor);
        DrawReservedWordWarning(newTitleAlias);
        if (ImGui.Button("Add title alias") && newTitleAlias.Trim().Length > 0 && newTitleText.Trim().Length > 0 && !IsReserved(newTitleAlias))
        {
            titles.Add(new TitleAliasDefinition { Alias = newTitleAlias.Trim(), Text = newTitleText.Trim(), IsPrefix = newTitleIsPrefix, Color = newTitleColor, Glow = newTitleHasGlow ? newTitleGlow : null });
            config.Save();
            newTitleAlias = "";
            newTitleText = "";
            titleList.Select($"title:{titles.Count - 1}");
        }
    }

    private void DrawTitleQuickSection(bool canSend)
    {
        IconGlyph.Text(FontAwesomeIcon.Heading, "Title");
        ImGui.Separator();
        var quick = plugin.Configuration.QuickCommands.Titles;
        DrawCommandRow("titleQuickFixed", canSend, new RowCommand("Clear title", "title clear", FixedActionIds.ClearTitle, "Removes your Sub's title."));

        using (Section.Begin("titleQuickSaved", "Saved titles"))
        {
            DrawSavedQuickListDetail("titleQuick", quick, canSend, cmd => new ListDetailItem("", cmd.Label, cmd.TitleIsPrefix ? "prefix" : null),
                "No saved titles yet. Use + New to make one.", "New title", () => DrawTitleQuickAddForm(quick), () => newTitleQuickText.Trim().Length > 0,
                cmd =>
                {
                    if (cmd.Command.StartsWith("title style ", StringComparison.OrdinalIgnoreCase)
                        && TitleCommand.TryParseStyleCommand(cmd.Command["title style ".Length..], out var text, out var prefix, out var color, out _))
                        DrawTitlePreview(text, prefix, color);
                });
        }
    }

    private int? newTitleQuickLock;

    private void DrawTitleQuickAddForm(List<QuickCommand> quick)
    {
        using var addBox = Section.Begin("titleQuickAdd", "Add a title command");
        ItemWidth(220);
        ImGui.InputText("##newQuickTitle", ref newTitleQuickText, 64);
        IconGlyph.HelpMarker("The exact title text applied via Honorific.");
        ImGui.Checkbox("Prefix (not suffix)##newQuickTitle", ref newTitleQuickIsPrefix);
        IconGlyph.HelpMarker("Show the title before your Sub's name instead of after it.");
        ImGui.ColorEdit3("Color##newQuickTitle", ref newTitleQuickColor);
        IconGlyph.HelpMarker("Honorific title color - matches the Sub's own Title alias color picker.");
        DrawGlowPicker("newQuickTitle", ref newTitleQuickHasGlow, ref newTitleQuickGlow);
        OwnerLockOption.Draw("newQuickTitle", ref newTitleQuickLock, gesture: false, ownerLock: true);
        if (newTitleQuickText.Trim().Length > 0)
            DrawTitlePreview(newTitleQuickText.Trim(), newTitleQuickIsPrefix, newTitleQuickColor);
        if (ImGui.Button("Save title##quickTitle") && newTitleQuickText.Trim().Length > 0)
        {
            var text = newTitleQuickText.Trim();
            var glow = newTitleQuickHasGlow ? newTitleQuickGlow : (Vector3?)null;
            quick.Add(new QuickCommand
            {
                Label = text,
                Command = TitleCommand.BuildStyleCommand(text, newTitleQuickIsPrefix, newTitleQuickColor, glow),
                TitleIsPrefix = newTitleQuickIsPrefix,
                TitleColor = newTitleQuickColor,
                TitleGlow = glow,
                LockSeconds = newTitleQuickLock,
            });
            plugin.Configuration.Save();
            newTitleQuickText = "";
            newTitleQuickLock = null;
            newTitleQuickIsPrefix = false;
            newTitleQuickColor = new Vector3(1, 1, 1);
            newTitleQuickHasGlow = false;
            newTitleQuickGlow = new Vector3(1, 1, 1);
            QuickList("titleQuick").Select($"titleQuick:{quick.Count - 1}");
        }
        IconGlyph.HelpMarker("Saves a title that applies and locks on your Sub, until you clear it or for the time you set. Your Sub's own titles can't replace it meanwhile.");
    }
}
