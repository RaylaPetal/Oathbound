using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Dalamud.Bindings.ImGui;
using Oathbound.Plugin.Commands;
using Oathbound.Plugin.Config;

namespace Oathbound.Plugin.UI;

/// The pieces an Owner's pick-at-send mod restraint offers, and the copies that send them.
public static class RestraintPieces
{
    // Keyed by the imported entry itself: a new catalog import replaces the entry, so the list is rebuilt then.
    private static readonly ConditionalWeakTable<RestraintCatalogExportEntry, List<RestraintPieceNamer.Piece>> Cache = new();

    /// The piece chosen in each combo this session, by restraint label.
    private static readonly Dictionary<string, ulong> Picked = new();

    public static bool IsPickAtSend(QuickCommand cmd) => cmd.RestraintPickAtSend && cmd.RestraintCatalogId is not null;

    public static IReadOnlyList<RestraintPieceNamer.Piece> Of(PluginConfig config, QuickCommand cmd) =>
        cmd.RestraintCatalogId is { } id && config.RestraintMapping.ImportedPeerCatalog.GetValueOrDefault(id) is { } entry
            ? Cache.GetValue(entry, e => RestraintPieceNamer.ListPieces(e.ChangedItemIds, e.ItemNames))
            : [];

    /// One sendable copy per piece, for places that store a command instead of sending it.
    public static IEnumerable<QuickCommand> Expand(PluginConfig config, QuickCommand cmd) =>
        Of(config, cmd).Select(p => cmd.ForPiece(p.ItemId, p.Name)).Where(c => c.Command.Length > 0);

    /// Draws the piece combo; returns the copy that sends the chosen piece, or null while none is chosen.
    public static QuickCommand? DrawCombo(PluginConfig config, string id, QuickCommand cmd, float width)
    {
        var pieces = Of(config, cmd);
        var picked = Picked.TryGetValue(cmd.Label, out var p) ? p : cmd.RestraintLastPick ?? 0;
        var current = pieces.FirstOrDefault(x => x.ItemId == picked);
        ImGui.SetNextItemWidth(width);
        if (ImGui.BeginCombo($"##piece_{id}", current.Name ?? (pieces.Count == 0 ? "No pieces shared" : "Choose a piece...")))
        {
            foreach (var piece in pieces)
            {
                if (ImGui.Selectable($"{piece.Name}##piece_{id}_{piece.ItemId}", piece.ItemId == picked))
                {
                    Picked[cmd.Label] = piece.ItemId;
                    current = piece;
                }
                if (piece.Name != piece.GameName && ImGui.IsItemHovered())
                    ImGui.SetTooltip(piece.GameName);
            }
            ImGui.EndCombo();
        }
        return current.Name is null ? null : Send(cmd, current);
    }

    /// Remembers a sent piece so it's preselected next time.
    public static void Sent(PluginConfig config, QuickCommand cmd, QuickCommand sent)
    {
        if (sent.RestraintItemId is not { } itemId || cmd.RestraintLastPick == itemId)
            return;
        cmd.RestraintLastPick = itemId;
        config.Save();
    }

    private static QuickCommand Send(QuickCommand cmd, RestraintPieceNamer.Piece piece) => cmd.ForPiece(piece.ItemId, piece.Name);
}
