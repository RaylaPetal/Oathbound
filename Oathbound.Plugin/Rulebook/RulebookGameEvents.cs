using System;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.DutyState;

namespace Oathbound.Plugin.Rulebook;

/// Local game events the rulebook reacts to, raised on the framework thread. Shared by every pairing.
public sealed class RulebookGameEvents : IDisposable
{
    public event Action<uint>? DutyStarted;
    public event Action? DutyWiped;
    public event Action? DutyCompleted;
    /// The character left the territory of a started duty without it completing.
    public event Action? DutyAbandoned;
    public event Action? LocalDeath;
    /// (from, to) once the character is ready in the new territory; from is 0 right after login.
    public event Action<uint, uint>? TerritoryEntered;

    private uint readyTerritory;
    private uint dutyTerritory;
    private bool dutyCompleted;
    private bool wasAlive;

    public RulebookGameEvents()
    {
        Plugin.DutyState.DutyStarted += OnDutyStarted;
        Plugin.DutyState.DutyWiped += OnDutyWiped;
        Plugin.DutyState.DutyCompleted += OnDutyCompleted;
        Plugin.ClientState.Logout += OnLogout;
    }

    public void Dispose()
    {
        Plugin.DutyState.DutyStarted -= OnDutyStarted;
        Plugin.DutyState.DutyWiped -= OnDutyWiped;
        Plugin.DutyState.DutyCompleted -= OnDutyCompleted;
        Plugin.ClientState.Logout -= OnLogout;
    }

    public uint CurrentTerritory => readyTerritory;

    public bool InStartedDuty => dutyTerritory != 0 && !dutyCompleted;

    /// Logging out is not leaving: no leave consequences, and the next login counts as entering.
    private void OnLogout(int type, int code)
    {
        readyTerritory = 0;
        dutyTerritory = 0;
        wasAlive = false;
    }

    private void OnDutyStarted(IDutyStateEventArgs args)
    {
        dutyTerritory = Plugin.ClientState.TerritoryType;
        dutyCompleted = false;
        DutyStarted?.Invoke(dutyTerritory);
    }

    private void OnDutyWiped(IDutyStateEventArgs args) => DutyWiped?.Invoke();

    private void OnDutyCompleted(IDutyStateEventArgs args)
    {
        dutyCompleted = true;
        DutyCompleted?.Invoke();
    }

    public void OnFrameworkUpdate()
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (!Plugin.ClientState.IsLoggedIn || player is null
            || Plugin.Condition[ConditionFlag.BetweenAreas] || Plugin.Condition[ConditionFlag.BetweenAreas51])
            return;

        var territory = (uint)Plugin.ClientState.TerritoryType;
        if (territory != readyTerritory)
        {
            var from = readyTerritory;
            readyTerritory = territory;
            if (dutyTerritory != 0 && territory != dutyTerritory)
            {
                var abandoned = !dutyCompleted;
                dutyTerritory = 0;
                if (abandoned)
                    DutyAbandoned?.Invoke();
            }
            TerritoryEntered?.Invoke(from, territory);
        }

        // Edge only, so lying dead doesn't count again every frame.
        var alive = player.CurrentHp > 0 || player.MaxHp == 0;
        if (wasAlive && !alive)
            LocalDeath?.Invoke();
        wasAlive = alive;
    }

    /// Combat, cutscenes and area changes hold back consequences that change what the party sees.
    public static bool ShouldHoldConsequences() =>
        Plugin.ObjectTable.LocalPlayer is null
        || Plugin.Condition[ConditionFlag.InCombat]
        || Plugin.Condition[ConditionFlag.BetweenAreas] || Plugin.Condition[ConditionFlag.BetweenAreas51]
        || Plugin.Condition[ConditionFlag.OccupiedInCutSceneEvent]
        || Plugin.Condition[ConditionFlag.WatchingCutscene] || Plugin.Condition[ConditionFlag.WatchingCutscene78];
}

public static class RulebookPlaces
{
    // TerritoryIntendedUse rows.
    private const uint CityAreaUse = 0;
    private const uint ResidentialAreaUse = 13;
    private const uint HousingInteriorUse = 14;

    public static bool Matches(PlaceRef place, uint territory)
    {
        if (territory == 0)
            return false;
        var row = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>().GetRowOrDefault(territory);
        if (row is not { } t)
            return false;
        return place.Kind switch
        {
            PlaceKind.Territory => place.TerritoryId == territory,
            PlaceKind.MainCity => t.TerritoryIntendedUse.RowId == CityAreaUse,
            PlaceKind.Residential => t.TerritoryIntendedUse.RowId == ResidentialAreaUse,
            PlaceKind.HouseInterior => t.TerritoryIntendedUse.RowId == HousingInteriorUse,
            PlaceKind.Duty => t.ContentFinderCondition.RowId != 0,
            _ => false,
        };
    }

    public static bool MatchesAny(System.Collections.Generic.IEnumerable<PlaceRef> places, uint territory)
    {
        foreach (var p in places)
            if (Matches(p, territory))
                return true;
        return false;
    }

    public static string Describe(PlaceRef place) => place.Kind switch
    {
        PlaceKind.Territory => string.IsNullOrWhiteSpace(place.Name) ? TerritoryName(place.TerritoryId) : place.Name,
        PlaceKind.MainCity => "any main city",
        PlaceKind.Residential => "any residential district",
        PlaceKind.HouseInterior => "inside any house or apartment",
        PlaceKind.Duty => "any duty",
        _ => "?",
    };

    public static string TerritoryName(uint territory)
    {
        var row = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>().GetRowOrDefault(territory);
        var name = row?.PlaceName.ValueNullable?.Name.ExtractText();
        return string.IsNullOrWhiteSpace(name) ? $"territory {territory}" : name;
    }
}
