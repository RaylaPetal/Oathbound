using System;
using System.Globalization;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Oathbound.Plugin.Ipc;

namespace Oathbound.Plugin.Commands;

/// `Ward` is 1-based, 0 outside a residential district; `Instance` is 0 when the zone isn't split.
public sealed record TeleportTarget(string World, uint Territory, int Instance, int Ward, bool Subdivision, Vector3 Position)
{
    public bool IsHousingWard => Ward > 0;

    /// `world:"<name>" terr:<u32> inst:<0-9> ward:<0-30> sub:<0|1> pos:<x>,<y>,<z>` - fixed order, invariant culture.
    public string ToPayload() => string.Create(CultureInfo.InvariantCulture,
        $"world:\"{World.Trim()}\" terr:{Territory} inst:{Instance} ward:{Ward} sub:{(Subdivision ? 1 : 0)} pos:{Position.X:0.0},{Position.Y:0.0},{Position.Z:0.0}");

    /// Strict: every token required, in order, so an older Owner's form fails closed with a reason.
    public static bool TryParse(string rest, out TeleportTarget target)
    {
        target = null!;
        const string worldPrefix = "world:\"";
        var trimmed = rest.Trim();
        if (!trimmed.StartsWith(worldPrefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var afterPrefix = trimmed[worldPrefix.Length..];
        var closing = afterPrefix.IndexOf('"');
        if (closing <= 0)
            return false;
        var world = afterPrefix[..closing];

        var tokens = afterPrefix[(closing + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length != 5)
            return false;

        if (!TryValue(tokens[0], "terr:", out var terrText) || !uint.TryParse(terrText, NumberStyles.None, CultureInfo.InvariantCulture, out var territory) || territory == 0)
            return false;
        if (!TryValue(tokens[1], "inst:", out var instText) || !int.TryParse(instText, NumberStyles.None, CultureInfo.InvariantCulture, out var instance) || instance > 9)
            return false;
        if (!TryValue(tokens[2], "ward:", out var wardText) || !int.TryParse(wardText, NumberStyles.None, CultureInfo.InvariantCulture, out var ward) || ward > 30)
            return false;
        if (!TryValue(tokens[3], "sub:", out var subText) || subText is not ("0" or "1"))
            return false;
        if (!TryValue(tokens[4], "pos:", out var posText))
            return false;

        var parts = posText.Split(',');
        if (parts.Length != 3
            || !float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
            || !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
            || !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var z)
            || !float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z))
            return false;

        target = new TeleportTarget(world, territory, instance, ward, subText == "1", new Vector3(x, y, z));
        return true;
    }

    private static bool TryValue(string token, string prefix, out string value)
    {
        value = token.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? token[prefix.Length..] : "";
        return value.Length > 0;
    }
}

/// Game-data lookups for the Sub's journey. Read-only.
public static class TeleportDestinations
{
    /// TerritoryIntendedUse: 13 = outdoor residential district, 14 = housing interiors.
    private const uint ResidentialAreaUse = 13;
    private const uint HousingInteriorUse = 14;

    /// Lifestream's ResidentialAetheryteKind values with the district names its parser accepts.
    private static readonly (int Kind, string Name)[] Districts =
    [
        (8, "Mist"),
        (2, "Lavender Beds"),
        (9, "Goblet"),
        (111, "Shirogane"),
        (70, "Empyreum"),
    ];

    private const int PlotsPerDivision = 30;

    public static bool IsHousingInterior(uint territory) => IntendedUse(territory) == HousingInteriorUse;

    public static bool IsResidentialArea(uint territory) => IntendedUse(territory) == ResidentialAreaUse;

    /// Private instances nobody else can be brought into.
    public static bool IsInnRoom(uint territory) => IntendedUse(territory) == (uint)ECommons.ExcelServices.TerritoryIntendedUseEnum.Inn;

    /// The bound-by-duty flags can lag a load; the territory's own duty link can't.
    public static bool InDuty() =>
        Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BoundByDuty]
        || Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BoundByDuty56]
        || Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BoundByDuty95]
        || Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>().GetRowOrDefault(Plugin.ClientState.TerritoryType)?.ContentFinderCondition.RowId is > 0;

    private static uint? IntendedUse(uint territory) =>
        Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>().GetRowOrDefault(territory)?.TerritoryIntendedUse.RowId;

    /// Null if none. Compared on the ground plane, since a map marker has no height.
    public static (uint AetheryteId, float Distance)? FindNearestAttunedAetheryte(uint territory, Vector3 ownerPosition)
    {
        (uint, float)? best = null;
        var owner2D = new Vector2(ownerPosition.X, ownerPosition.Z);
        foreach (var row in Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Aetheryte>())
        {
            if (!row.IsAetheryte || row.Territory.RowId != territory || !IsAttuned(row.RowId))
                continue;
            if (ResolvePosition(row) is not { } position)
                continue;

            var distance = Vector2.Distance(owner2D, position);
            if (best is null || distance < best.Value.Item2)
                best = (row.RowId, distance);
        }
        return best;
    }

    private static unsafe bool IsAttuned(uint aetheryteId)
    {
        var ui = UIState.Instance();
        return ui != null && ui->IsAetheryteUnlocked(aetheryteId);
    }

    private static Vector2? ResolvePosition(Lumina.Excel.Sheets.Aetheryte row)
    {
        var level = row.Level[0];
        if (level.RowId != 0 && level.ValueNullable is { } lv)
            return new Vector2(lv.X, lv.Z);

        // Marker coordinates are 0..2048 texture pixels on the aetheryte's map.
        if (row.Map.ValueNullable is not { } map)
            return null;
        var markers = Plugin.DataManager.GetSubrowExcelSheet<Lumina.Excel.Sheets.MapMarker>();
        if (markers.GetRowOrDefault(map.MapMarkerRange) is not { } range)
            return null;
        foreach (var marker in range)
        {
            // DataType 3 = aetheryte marker, DataKey = Aetheryte row.
            if (marker.DataType != 3 || marker.DataKey.RowId != row.RowId)
                continue;
            var scale = map.SizeFactor / 100f;
            return new Vector2((marker.X - 1024f) / scale - map.OffsetX, (marker.Y - 1024f) / scale - map.OffsetY);
        }
        return null;
    }

    /// Asks Lifestream which territory each district is, so no ids are hardcoded.
    public static string? FindDistrictName(LifestreamIpc lifestream, uint territory)
    {
        foreach (var (kind, name) in Districts)
            if (lifestream.TryGetResidentialTerritory(kind) == territory)
                return name;
        return null;
    }

    /// 1-30 main division, 31-60 subdivision. Null when Lifestream has no plot data.
    public static int? FindNearestPlot(LifestreamIpc lifestream, uint territory, bool subdivision, Vector3 ownerPosition)
    {
        var first = subdivision ? PlotsPerDivision : 0;
        int? best = null;
        var bestDistance = float.MaxValue;
        for (var index = first; index < first + PlotsPerDivision; index++)
        {
            if (lifestream.TryGetPlotEntrance(territory, index) is not { } entrance)
                continue;
            var distance = Vector3.Distance(entrance, ownerPosition);
            if (distance >= bestDistance)
                continue;
            best = index + 1;
            bestDistance = distance;
        }
        return best;
    }

    /// The Sub's own estate teleport (private, Free Company, shared or apartment) on the current world in that
    /// district, 1-based ward and division, nearest the Owner. Null if there is none.
    public static unsafe (uint AetheryteId, byte SubIndex)? FindEstateInWard(LifestreamIpc lifestream, uint territory, int ward, bool subdivision, Vector3 ownerPosition)
    {
        var telepo = Telepo.Instance();
        var worldId = Plugin.ObjectTable.LocalPlayer?.CurrentWorld.RowId;
        if (telepo == null || worldId is null)
            return null;
        telepo->UpdateAetheryteList();

        (uint, byte)? best = null;
        var bestDistance = float.MaxValue;
        foreach (ref readonly var info in telepo->TeleportList.AsSpan())
        {
            // HouseId rather than Ward/Plot: the game fills those two only for shared estates.
            var house = info.HouseId;
            if (house.TerritoryTypeId != territory || house.WorldId != worldId || house.IsWorkshop || house.WardIndex + 1 != ward)
                continue;
            var houseSubdivision = house.IsApartment ? house.ApartmentDivision == 1 : house.PlotIndex >= PlotsPerDivision;
            if (houseSubdivision != subdivision)
                continue;

            // An apartment has no plot entrance, so any plot in the ward wins over it.
            var distance = !house.IsApartment && lifestream.TryGetPlotEntrance(territory, house.PlotIndex) is { } entrance
                ? Vector3.Distance(entrance, ownerPosition)
                : float.MaxValue - 1;
            Plugin.Log.Debug($"Estate teleport candidate: aetheryte {info.AetheryteId} sub {info.SubIndex}, ward {house.WardIndex + 1}, plot {house.PlotIndex}, apartment {house.IsApartment}.");
            if (distance >= bestDistance)
                continue;
            best = (info.AetheryteId, info.SubIndex);
            bestDistance = distance;
        }
        return best;
    }

    /// Null when not in a residential district.
    public static unsafe (int Ward, bool Subdivision)? CurrentWard()
    {
        var housing = HousingManager.Instance();
        if (housing == null || !IsResidentialArea(Plugin.ClientState.TerritoryType))
            return null;
        var ward = housing->GetCurrentWard();
        if (ward < 0)
            return null;
        return (ward + 1, housing->GetCurrentDivision() == 2);
    }

    public static unsafe bool IsInsideHousing()
    {
        var housing = HousingManager.Instance();
        return (housing != null && housing->IsInside()) || IsHousingInterior(Plugin.ClientState.TerritoryType);
    }

    public static unsafe int CurrentPublicInstance()
    {
        var ui = UIState.Instance();
        return ui == null ? 0 : (int)ui->PublicInstance.InstanceId;
    }
}
