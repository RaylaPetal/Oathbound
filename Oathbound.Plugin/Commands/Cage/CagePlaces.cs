using FFXIVClientStructs.FFXIV.Client.Game;
using Oathbound.Plugin.Config;

namespace Oathbound.Plugin.Commands;

/// Reads where the local character is, as a cage place. A house is identified by
/// HousingManager.GetCurrentIndoorHouseId, whose id packs world, territory, ward, plot and apartment room.
public static class CagePlaces
{
    /// Null with a reason when this spot can't hold a cage.
    public static CagePlace? Capture(out string label, out string? error)
    {
        label = "";
        error = null;
        var player = Plugin.ObjectTable.LocalPlayer;
        var territory = Plugin.ClientState.TerritoryType;
        if (player is null || territory == 0)
        {
            error = "Could not read your current location - try again once you've finished loading in.";
            return null;
        }
        if (TeleportDestinations.InDuty() || TeleportDestinations.IsInnRoom(territory))
        {
            error = "Cages can't be placed in duties, inn rooms or other instanced areas.";
            return null;
        }

        var place = Current();
        if (place is null)
        {
            error = TeleportDestinations.IsInsideHousing()
                ? "This house or apartment can't be told apart from others here - try inside the house itself."
                : "Could not read your current location - try again in a moment.";
            return null;
        }
        label = Describe(place);
        return place;
    }

    /// The place of whichever kind the character is in, or null if it can't be read or isn't a cage place.
    public static unsafe CagePlace? Current()
    {
        var territory = Plugin.ClientState.TerritoryType;
        if (Plugin.ObjectTable.LocalPlayer is not { } player || territory == 0 || TeleportDestinations.InDuty())
            return null;

        if (TeleportDestinations.IsInsideHousing())
        {
            var housing = HousingManager.Instance();
            if (housing == null)
                return null;
            ulong id = housing->GetCurrentIndoorHouseId();
            return id is 0 or ulong.MaxValue ? null : new CagePlace { Kind = CagePlaceKind.House, HouseId = id };
        }
        if (TeleportDestinations.IsResidentialArea(territory))
        {
            if (TeleportDestinations.CurrentWard() is not { } ward)
                return null;
            return new CagePlace
            {
                Kind = CagePlaceKind.Ward,
                WorldId = player.CurrentWorld.RowId,
                Territory = territory,
                Ward = ward.Ward,
                Subdivision = ward.Subdivision,
            };
        }
        return new CagePlace { Kind = CagePlaceKind.Territory, Territory = territory };
    }

    public static bool IsIn(CagePlace place) => Current() is { } here && here.SameAs(place);

    /// An apartment has no plot entrance to travel to, so it can't be brought to.
    public static bool IsApartment(CagePlace place) => place.Kind == CagePlaceKind.House && ((HouseId)place.HouseId).IsApartment;

    public static string Describe(CagePlace place)
    {
        var territories = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>();
        switch (place.Kind)
        {
            case CagePlaceKind.Territory:
                return territories.GetRowOrDefault(place.Territory)?.PlaceName.ValueNullable?.Name.ExtractText() is { Length: > 0 } name ? name : $"Zone {place.Territory}";
            case CagePlaceKind.Ward:
                var district = territories.GetRowOrDefault(place.Territory)?.PlaceName.ValueNullable?.Name.ExtractText() ?? "Residential district";
                var world = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.World>().GetRowOrDefault(place.WorldId)?.Name.ExtractText() ?? "";
                return $"{district} ward {place.Ward}{(place.Subdivision ? " (subdivision)" : "")}{(world.Length > 0 ? $", {world}" : "")}";
            default:
                HouseId house = place.HouseId;
                var houseWorld = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.World>().GetRowOrDefault(house.WorldId)?.Name.ExtractText() ?? "";
                var where = house.IsApartment
                    ? $"apartment {house.RoomNumber}, ward {house.WardIndex + 1}"
                    : $"ward {house.WardIndex + 1} plot {house.PlotIndex + 1}{(house.RoomNumber > 0 ? $" room {house.RoomNumber}" : "")}";
                return $"House: {where}{(houseWorld.Length > 0 ? $", {houseWorld}" : "")}";
        }
    }
}
