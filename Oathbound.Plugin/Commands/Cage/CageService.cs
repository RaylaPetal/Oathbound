using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Game.ClientState.Objects.Enums;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Oathbound.Plugin.Config;
using Oathbound.Plugin.Ipc;

namespace Oathbound.Plugin.Commands;

/// The Sub's cage: armed until the Sub is in its place, then closed (walked in by vnavmesh and held inside) until its
/// timer, the holding Owner's `cage open`, panic or that pairing ending. Leaving the place re-arms it, never opens it.
/// Everything else asks IsCaged to close its own escape.
public sealed class CageService
{
    private const string LockOwner = "Cage";
    private const string ArmToken = "armfor:";
    public const string OpenWord = "open";
    public const string CloseWord = "close";
    public const string PinWord = "pin";
    /// Travels the Sub to an armed cage with Teleport, then the cage closes on arrival as usual.
    public const string BringWord = "bring";

    /// Slack before an outside Sub is walked back, so standing on the edge doesn't restart the walk.
    private const float EdgeMargin = 0.5f;
    private const float ProgressMinDelta = 0.5f;
    private static readonly TimeSpan GiveUpAfter = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan AreaSettle = TimeSpan.FromSeconds(2);
    private const uint DismountAction = 23;
    /// How far from the plot entrance Lifestream reports the house's door can be; the journey ends at the gate.
    private const float DoorSearch = 30f;
    /// Close enough to the door to open it.
    private const float DoorTouch = 3.5f;
    /// The house door's name in every client language. The plot's placard stands right by the entrance point too,
    /// so the door is found by name, not by being nearest.
    private static readonly string[] DoorNames =
    [
        "Entrance",
        "Eingang",
        "Entr\u00e9e",
        "\u30cf\u30a6\u30b9\u3078\u5165\u308b",
        "\u8fdb\u5165\u623f\u5c4b",
        "\u9032\u5165\u623f\u5c4b",
        "\uc8fc\ud0dd\uc73c\ub85c \ub4e4\uc5b4\uac00\uae30",
    ];
    private static readonly TimeSpan DoorGiveUp = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan DoorRetry = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan DoorPromptWindow = TimeSpan.FromSeconds(10);
    /// The door's "enter?" prompt in every client language; any other yes/no is never answered.
    private static readonly string[] DoorPrompts =
    [
        "Enter the estate hall?",
        "Das Geb\u00e4ude betreten?",
        "Entrer dans la maison ?",
        "\u300c\u30cf\u30a6\u30b9\u300d\u3078\u5165\u308a\u307e\u3059\u304b\uff1f",
        "\u8981\u8fdb\u5165\u8fd9\u95f4\u623f\u5c4b\u5417\uff1f",
        "\u8981\u9032\u5165\u9019\u9593\u623f\u5c4b\u55ce\uff1f",
        "'\uc8fc\ud0dd'\uc73c\ub85c \ub4e4\uc5b4\uac00\uc2dc\uaca0\uc2b5\ub2c8\uae4c?",
    ];

    public const string CagedNote = "You're caged - this stays locked until your cage opens.";

    private readonly PluginConfig config;
    private readonly MovementLockService movementLock;
    private readonly VnavmeshIpc vnavmesh;
    private readonly TeleportCommand teleport;
    private readonly LifestreamIpc lifestream;

    private DateTime? settledSince;
    private bool navUnavailableNotified;

    private bool walking;
    private bool confined;
    private CancellationTokenSource? pathCancel;
    private Task<List<Vector3>>? pendingPath;
    private bool pathStarted;
    private DateTime lastPathRequest;
    private DateTime progressAt;
    private float bestDistance;
    private bool dismountSent;

    // Bring & lock to a house: the door to open once the journey ends there.
    private Vector3? houseDoor;
    private DateTime? doorSince;
    private DateTime doorTouchedAt = DateTime.MinValue;
    private DateTime doorAnsweredAt = DateTime.MinValue;
    private CancellationTokenSource? doorPathCancel;
    private Task<List<Vector3>>? doorPath;
    private DateTime doorPathAt = DateTime.MinValue;

    // Written by OnFrameworkUpdate, read by Confine (both on the main thread).
    private Vector2 center;
    private Vector2 playerGround;
    private Vector2 half;

    public CageService(PluginConfig config, MovementLockService movementLock, VnavmeshIpc vnavmesh, TeleportCommand teleport, LifestreamIpc lifestream)
    {
        this.lifestream = lifestream;
        this.config = config;
        this.movementLock = movementLock;
        this.vnavmesh = vnavmesh;
        this.teleport = teleport;
    }

    /// Raised on every change of phase, end time or holder, and when the cage opens or is dropped.
    public event Action? StateChanged;

    public SubCageState? Cage => config.ActiveCage;

    /// From the moment the cage closes (including the walk in) until it opens.
    public bool IsCaged => config.ActiveCage is { Phase: CagePhase.Caged };

    public bool IsArmed => config.ActiveCage is { Phase: CagePhase.Armed };

    public bool IsWalkingIn => walking;

    /// `cage [lockfor:<s>] [armfor:<s>] [bring] close <place> pos:<x>,<y>,<z> <r:<x>[x<z>]|pin> "<name>"` or `cage open`.
    public LocalTestResult Handle(string rest, PairingState? source)
    {
        rest = LockTimerOption.StripSeconds(rest, out var lockSeconds);
        rest = StripArmSeconds(rest, out var armSeconds);
        rest = StripBring(rest, out var bring);
        var trimmed = rest.Trim();
        var space = trimmed.IndexOf(' ');
        var verb = space < 0 ? trimmed : trimmed[..space];
        var args = space < 0 ? "" : trimmed[(space + 1)..];

        if (verb.Equals(OpenWord, StringComparison.OrdinalIgnoreCase) && args.Length == 0)
            return HandleOpen(source);
        if (!verb.Equals(CloseWord, StringComparison.OrdinalIgnoreCase))
            return LocalTestResult.Fail($"Unrecognized \"cage\" override \"{rest.Trim()}\".");

        if (!config.Permissions.Cage || !config.CageAcknowledged)
            return LocalTestResult.Fail("Cage permission is not enabled.", "tried to cage you - Cage is turned off");
        if (!TryParseClose(args, out var place, out var position, out var cageRadius, out var cageRadiusZ, out var name))
            return LocalTestResult.Fail($"Unrecognized \"cage close\" arguments \"{args}\".");
        if (!CanEnforce(cageRadius))
            return LocalTestResult.Fail("Refused: a cage can't be enforced on this game version.", "tried to cage you, but your game version can't hold a cage");

        var existing = config.ActiveCage;
        if (existing is not null && existing.PairingId != source?.Id)
            return LocalTestResult.Fail("Refused: you're already held by another Owner's cage.", "tried to cage you, but you're already in another Owner's cage");

        var now = DateTime.UtcNow;
        var lockDuration = RestraintLock.FromSeconds(lockSeconds).Duration;
        var cage = existing ?? new SubCageState();
        cage.PairingId = source?.Id;
        cage.OwnerName = source?.PeerName ?? "Owner";
        cage.Name = name;
        cage.Place = place;
        cage.Position = position;
        cage.Radius = cageRadius;
        cage.RadiusZ = cageRadiusZ;
        cage.LockSeconds = lockDuration is { } d ? (int)d.TotalSeconds : null;
        cage.ArmExpiresAtUtc = now + TimeSpan.FromSeconds(CageLimits.ClampArmSeconds(armSeconds ?? CageLimits.DefaultArmSeconds));
        if (cage.HasClosed)
            cage.EndsAtUtc = lockDuration is { } closedLock ? now + closedLock : null;
        else
            cage.Phase = CagePhase.Armed;
        config.ActiveCage = cage;
        ReleaseClaims();
        config.Save();
        Plugin.Log.Information($"Cage \"{name}\" set by {cage.OwnerName}: {(cage.HasClosed ? "already closed" : "armed")}.");
        StateChanged?.Invoke();

        // Close straight away when the Sub is already there, rather than on the next frame.
        settledSince = DateTime.MinValue;
        OnFrameworkUpdate();
        if (IsCaged)
            return LocalTestResult.Ok($"Caged in \"{name}\".", $"put you in the cage \"{name}\"");
        if (bring)
        {
            var (traveling, why) = BringTo(place, position, source);
            if (traveling)
                return LocalTestResult.Ok($"Cage \"{name}\" armed - travelling you there now.", $"is bringing you to the cage \"{name}\"");
            return LocalTestResult.Ok($"Cage \"{name}\" armed, but you couldn't be brought there ({why}) - it closes when you're next at {CagePlaces.Describe(place)}.",
                $"set the cage \"{name}\", but couldn't bring you there: {why}");
        }
        return LocalTestResult.Ok($"Cage \"{name}\" armed - it closes when you're next at {CagePlaces.Describe(place)}.", $"set the cage \"{name}\" - it closes when you're next there");
    }

    /// Starts a Teleport journey to the cage, which needs the Sub's Teleport permission like any other. A house cage
    /// travels to its plot's entrance; going inside is the Sub's own step, and the cage closes once they do.
    private (bool Started, string Why) BringTo(CagePlace place, Vector3 position, PairingState? source)
    {
        if (BringTarget(place, position, out var why) is not { } target)
            return (false, why);
        var (success, reason) = teleport.Apply(target, source);
        if (!success)
            return (false, (reason ?? "Teleport failed to start.").TrimEnd('.'));
        if (place.Kind == CagePlaceKind.House)
        {
            houseDoor = target.Position;
            doorSince = null;
        }
        return (true, "");
    }

    private TeleportTarget? BringTarget(CagePlace place, Vector3 position, out string why)
    {
        why = "";
        var worlds = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.World>();
        switch (place.Kind)
        {
            case CagePlaceKind.Territory:
                if (Plugin.ObjectTable.LocalPlayer?.CurrentWorld.Value.Name.ExtractText() is not { Length: > 0 } here)
                {
                    why = "your character can't be read right now";
                    return null;
                }
                return new TeleportTarget(here, place.Territory, 0, 0, false, position);
            case CagePlaceKind.Ward:
                if (worlds.GetRowOrDefault(place.WorldId)?.Name.ExtractText() is not { Length: > 0 } wardWorld)
                {
                    why = "the cage's world is unknown";
                    return null;
                }
                return new TeleportTarget(wardWorld, place.Territory, 0, place.Ward, place.Subdivision, position);
            default:
                HouseId house = place.HouseId;
                if (house.IsApartment)
                {
                    why = "an apartment can't be travelled to - walk in yourself";
                    return null;
                }
                uint district = house.TerritoryTypeId;
                if (!TeleportDestinations.IsResidentialArea(district)
                    || worlds.GetRowOrDefault(house.WorldId)?.Name.ExtractText() is not { Length: > 0 } houseWorld
                    || lifestream.TryGetPlotEntrance(district, house.PlotIndex) is not { } entrance)
                {
                    why = "the house's plot couldn't be found";
                    return null;
                }
                return new TeleportTarget(houseWorld, district, 0, house.WardIndex + 1, house.PlotIndex >= 30, entrance);
        }
    }

    private LocalTestResult HandleOpen(PairingState? source)
    {
        var cage = config.ActiveCage;
        if (cage is null)
            return LocalTestResult.Fail("There's no cage to open.", "tried to open your cage, but you aren't in one");
        if (cage.PairingId != source?.Id)
            return LocalTestResult.Fail("Refused: only the Owner who caged you can open it.", "tried to open your cage, but only the Owner who caged you can");
        var wasCaged = IsCaged;
        Open(wasCaged ? $"{cage.OwnerName} opened it" : $"{cage.OwnerName} called it off");
        return wasCaged
            ? LocalTestResult.Ok($"Cage \"{cage.Name}\" opened.", $"opened your cage \"{cage.Name}\"")
            : LocalTestResult.Ok($"Armed cage \"{cage.Name}\" dropped.", $"called off the cage \"{cage.Name}\"");
    }

    /// Panic, unpair, the timer and `cage open` all end here.
    public void Open(string reason)
    {
        var cage = config.ActiveCage;
        ReleaseClaims();
        if (cage is null)
            return;
        config.ActiveCage = null;
        config.Save();
        Plugin.Log.Information($"Cage \"{cage.Name}\" ended: {reason}.");
        Notify(cage.Phase == CagePhase.Caged ? "Cage opened" : "Cage dropped", $"\"{cage.Name}\": {reason}.", NotificationType.Info);
        StateChanged?.Invoke();
    }

    public void ReleaseAllForPanic() => Open("you used panic");

    /// After something else dropped every movement claim: the next frame takes the cage's own claims again.
    public void ForgetClaims()
    {
        CancelPendingPath();
        walking = false;
        confined = false;
        pathStarted = false;
    }

    public void OnFrameworkUpdate()
    {
        var cage = config.ActiveCage;
        if (cage is null)
        {
            if (walking || confined)
                ReleaseClaims();
            return;
        }

        var now = DateTime.UtcNow;
        if (cage.PairingId is { } id && config.FindPairingById(id) is not { IsPaired: true })
        {
            Open("the pairing that caged you ended");
            return;
        }
        if (cage.HasClosed && cage.EndsAtUtc is { } end && now >= end)
        {
            Open("its time is up");
            return;
        }
        if (cage.Phase == CagePhase.Armed && !cage.HasClosed && now >= cage.ArmExpiresAtUtc)
        {
            Open("you weren't there before it stopped waiting");
            return;
        }
        // Never close a cage on a Sub who has since withdrawn the permission, re-armed or not.
        if (cage.Phase == CagePhase.Armed && (!config.Permissions.Cage || !config.CageAcknowledged))
        {
            Open("you turned the Cage permission off");
            return;
        }

        if (!RestraintCommand.CanChangeCharacter())
        {
            settledSince = null;
            PauseWalk();
            return;
        }
        settledSince ??= now;
        if (now - settledSince.Value < AreaSettle || Plugin.ObjectTable.LocalPlayer is not { } player)
            return;

        var inPlace = CagePlaces.IsIn(cage.Place);
        UpdateGeometry(cage, player.Position);
        var outside = OutsideBy() > EdgeMargin;

        if (cage.Phase == CagePhase.Armed)
        {
            if (!inPlace)
            {
                EnterHouse(cage, player, now);
                return;
            }
            if (outside && !vnavmesh.IsAvailable)
            {
                if (!navUnavailableNotified)
                {
                    navUnavailableNotified = true;
                    Notify("Cage waiting", $"\"{cage.Name}\" can't walk you in without vnavmesh. It closes once vnavmesh is available.", NotificationType.Warning);
                }
                return;
            }
            Close(cage, now);
        }
        else if (!inPlace)
        {
            Rearm(cage);
            return;
        }

        Hold(cage, player, now);
    }

    private void Close(SubCageState cage, DateTime now)
    {
        navUnavailableNotified = false;
        StopDoorWalk();
        cage.Phase = CagePhase.Caged;
        if (!cage.HasClosed)
        {
            cage.HasClosed = true;
            cage.EndsAtUtc = cage.LockSeconds is { } s ? now + TimeSpan.FromSeconds(s) : null;
        }
        config.Save();
        if (teleport.IsInProgress)
            teleport.Stop("caged");
        Plugin.Log.Information($"Cage \"{cage.Name}\" closed.");
        var until = cage.EndsAtUtc is { } end ? $"for {RestraintLock.Format(end - now)}" : "until they open it";
        Notify("Caged", $"{cage.OwnerName} has you in \"{cage.Name}\" {until}. Panic still opens it.", NotificationType.Warning);
        StateChanged?.Invoke();
    }

    private void Rearm(SubCageState cage)
    {
        ReleaseClaims();
        cage.Phase = CagePhase.Armed;
        config.Save();
        Plugin.Log.Information($"Cage \"{cage.Name}\" re-armed: the Sub left its place.");
        Notify("Cage waiting", $"You left \"{cage.Name}\". It closes again when you're next there.", NotificationType.Info);
        StateChanged?.Invoke();
    }

    private void Hold(SubCageState cage, Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter player, DateTime now)
    {
        if (FollowCommand.GameHoldsSub())
        {
            PauseWalk();
            return;
        }

        var distance = OutsideBy();
        if (walking)
        {
            UpdateWalk(cage, player, distance, now);
            return;
        }
        if (distance > EdgeMargin)
        {
            StartWalk(now, distance);
            return;
        }
        Confine(cage);
    }

    private void StartWalk(DateTime now, float distance)
    {
        Unconfine();
        movementLock.EngageSuppressInput(LockOwner);
        walking = true;
        pathStarted = false;
        dismountSent = false;
        bestDistance = distance;
        progressAt = now;
        lastPathRequest = DateTime.MinValue;
    }

    private unsafe void UpdateWalk(SubCageState cage, Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter player, float distance, DateTime now)
    {
        if (distance <= 0f)
        {
            StopWalk();
            Confine(cage);
            return;
        }

        // Combat, or the mesh still building, holds the walk without counting toward giving up.
        if (Plugin.Condition[ConditionFlag.InCombat] || !vnavmesh.TryIsReady())
        {
            progressAt = now;
            return;
        }
        if (Plugin.Condition[ConditionFlag.Mounted])
        {
            if (!dismountSent)
            {
                var actions = ActionManager.Instance();
                if (actions != null)
                    actions->UseAction(ActionType.GeneralAction, DismountAction);
                dismountSent = true;
            }
            progressAt = now;
            return;
        }

        if (pendingPath is { IsCompleted: true } done)
        {
            pendingPath = null;
            if (done.IsCompletedSuccessfully && done.Result is { Count: > 0 } path && vnavmesh.TryMoveTo(path, false))
                pathStarted = true;
        }

        if (bestDistance - distance >= ProgressMinDelta)
        {
            bestDistance = distance;
            progressAt = now;
        }
        else if (now - progressAt >= GiveUpAfter)
        {
            cage.Position = player.Position;
            config.Save();
            StopWalk();
            Plugin.Log.Information($"Cage \"{cage.Name}\" walk-in made no progress; the cage moved to the Sub.");
            Notify("Cage moved", $"You couldn't be walked into \"{cage.Name}\", so it closed where you stand.", NotificationType.Info);
            UpdateGeometry(cage, player.Position);
            Confine(cage);
            return;
        }

        var idle = pendingPath is null && (!pathStarted || !vnavmesh.TryIsRunning());
        if (idle && now - lastPathRequest >= RetryInterval)
            RequestPath(cage, player.Position, now);
    }

    private void RequestPath(SubCageState cage, Vector3 from, DateTime now)
    {
        CancelPendingPath();
        var to = vnavmesh.TryNearestPointReachable(cage.Position, 3f, 5f) ?? cage.Position;
        pathCancel = new CancellationTokenSource();
        pendingPath = vnavmesh.TryPathfind(from, to, false, pathCancel.Token);
        lastPathRequest = now;
    }

    private void PauseWalk()
    {
        if (!walking)
            return;
        CancelPendingPath();
        vnavmesh.TryStop();
        pathStarted = false;
    }

    private void StopWalk()
    {
        CancelPendingPath();
        if (walking)
            vnavmesh.TryStop();
        walking = false;
        pathStarted = false;
        movementLock.ReleaseSuppressInput(LockOwner);
    }

    private void Confine(SubCageState cage)
    {
        if (confined)
            return;
        confined = true;
        // Without the walk hook the bounds can't be held, so the Sub is held still instead.
        if (cage.IsPinned || !movementLock.IsSteerAvailable)
            movementLock.EngageImmobilize(LockOwner);
        else
            movementLock.SetConfinement(ConfineWish);
    }

    private void Unconfine()
    {
        if (!confined)
            return;
        confined = false;
        movementLock.ReleaseImmobilize(LockOwner);
        movementLock.ClearConfinement();
    }

    private void ReleaseClaims()
    {
        StopDoorWalk();
        StopWalk();
        Unconfine();
    }

    private static unsafe bool AnswerDoorPrompt()
    {
        var address = Plugin.GameGui.GetAddonByName("SelectYesno").Address;
        if (address == nint.Zero || !((AtkUnitBase*)address)->IsVisible)
            return false;
        var prompt = new AddonMaster.SelectYesno(address);
        var text = prompt.Text.Replace("\n", "").Trim();
        if (!DoorPrompts.Any(p => text.Contains(p, StringComparison.OrdinalIgnoreCase)))
            return false;
        prompt.Yes();
        return true;
    }

    /// After a Bring & lock journey ends at a house's door: dismount, open the door and accept its "enter?" prompt,
    /// the same steps a player takes. Inside, the cage's place matches and it closes as usual. Gives up after DoorGiveUp.
    private unsafe void EnterHouse(SubCageState cage, Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter player, DateTime now)
    {
        if (houseDoor is not { } plotEntrance || teleport.IsInProgress)
            return;
        doorSince ??= now;
        if (Vector3.Distance(player.Position, plotEntrance) > DoorSearch || now - doorSince.Value >= DoorGiveUp)
        {
            StopDoorWalk();
            Notify("Cage waiting", $"You couldn't be taken inside for \"{cage.Name}\". It closes once you go in.", NotificationType.Info);
            return;
        }
        movementLock.EngageSuppressInput(LockOwner);

        if (now - doorTouchedAt < DoorPromptWindow && now - doorAnsweredAt >= DoorRetry && AnswerDoorPrompt())
        {
            doorAnsweredAt = now;
            return;
        }

        var entrance = Plugin.ObjectTable
            .Where(o => o.IsTargetable && DoorNames.Any(n => o.Name.TextValue.Equals(n, StringComparison.OrdinalIgnoreCase))
                        && Vector3.Distance(o.Position, plotEntrance) <= DoorSearch)
            .OrderBy(o => Vector3.Distance(o.Position, plotEntrance))
            .FirstOrDefault();
        if (entrance is null)
            return;

        if (Vector3.Distance(player.Position, entrance.Position) > DoorTouch)
        {
            WalkToDoor(player.Position, entrance.Position, now);
            return;
        }
        if (vnavmesh.TryIsRunning())
            vnavmesh.TryStop();

        if (Plugin.Condition[ConditionFlag.Mounted])
        {
            if (now - doorTouchedAt >= DoorRetry)
            {
                var actions = ActionManager.Instance();
                if (actions != null)
                    actions->UseAction(ActionType.GeneralAction, DismountAction);
                doorTouchedAt = now;
            }
            return;
        }
        if (now - doorTouchedAt < DoorRetry)
            return;

        Plugin.TargetManager.Target = entrance;
        var targets = TargetSystem.Instance();
        if (targets != null)
            targets->InteractWithObject((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)entrance.Address, false);
        doorTouchedAt = now;
    }

    /// From the plot's gate up to the door: one path at a time, asked again only once the last one has finished.
    private void WalkToDoor(Vector3 from, Vector3 door, DateTime now)
    {
        if (doorPath is { IsCompleted: true } done)
        {
            doorPath = null;
            if (done.IsCompletedSuccessfully && done.Result is { Count: > 0 } path)
                vnavmesh.TryMoveTo(path, false);
            return;
        }
        if (doorPath is not null || vnavmesh.TryIsRunning() || now - doorPathAt < DoorRetry || !vnavmesh.TryIsReady())
            return;
        doorPathCancel?.Cancel();
        doorPathCancel?.Dispose();
        doorPathCancel = new CancellationTokenSource();
        var to = vnavmesh.TryNearestPointReachable(door, 2f, 3f) ?? door;
        doorPath = vnavmesh.TryPathfind(from, to, false, doorPathCancel.Token);
        doorPathAt = now;
    }

    private void StopDoorWalk()
    {
        if (houseDoor is null)
            return;
        houseDoor = null;
        doorPathCancel?.Cancel();
        doorPathCancel?.Dispose();
        doorPathCancel = null;
        doorPath = null;
        if (vnavmesh.TryIsRunning())
            vnavmesh.TryStop();
        movementLock.ReleaseSuppressInput(LockOwner);
    }

    /// At an edge of the rectangle, the part of the wish that crosses it is removed, so the Sub slides along it.
    private Vector2 ConfineWish(Vector2 wish)
    {
        var offset = playerGround - center;
        if ((offset.X >= half.X && wish.X > 0f) || (offset.X <= -half.X && wish.X < 0f))
            wish.X = 0f;
        if ((offset.Y >= half.Y && wish.Y > 0f) || (offset.Y <= -half.Y && wish.Y < 0f))
            wish.Y = 0f;
        return wish;
    }

    private void UpdateGeometry(SubCageState cage, Vector3 playerPosition)
    {
        center = new Vector2(cage.Position.X, cage.Position.Z);
        playerGround = new Vector2(playerPosition.X, playerPosition.Z);
        half = cage.HalfExtents;
    }

    /// How far the Sub stands outside the rectangle along the ground; 0 inside.
    private float OutsideBy()
    {
        var offset = Vector2.Abs(playerGround - center) - half;
        return Vector2.Max(offset, Vector2.Zero).Length();
    }

    private void CancelPendingPath()
    {
        pathCancel?.Cancel();
        pathCancel?.Dispose();
        pathCancel = null;
        pendingPath = null;
    }

    private bool CanEnforce(float cageRadius) =>
        movementLock.IsAvailable && movementLock.IsImmobilizeAvailable && (cageRadius <= 0 || movementLock.IsSteerAvailable);

    private static void Notify(string title, string content, NotificationType type) =>
        Plugin.NotificationManager.AddNotification(new Notification { Title = title, Content = content, Type = type });

    private static string StripBring(string rest, out bool bring)
    {
        var trimmed = rest.TrimStart();
        bring = trimmed.StartsWith(BringWord + " ", StringComparison.OrdinalIgnoreCase);
        return bring ? trimmed[(BringWord.Length + 1)..].TrimStart() : rest;
    }

    /// `armfor:<seconds>` right after any `lockfor:`. A malformed one is left in place so the verb isn't recognized.
    private static string StripArmSeconds(string rest, out int? seconds)
    {
        seconds = null;
        var trimmed = rest.TrimStart();
        if (!trimmed.StartsWith(ArmToken, StringComparison.OrdinalIgnoreCase))
            return rest;
        var end = trimmed.IndexOf(' ');
        if (end < 0 || !int.TryParse(trimmed[ArmToken.Length..end], NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
            return rest;
        seconds = parsed;
        return trimmed[(end + 1)..].TrimStart();
    }

    private static bool TryParseClose(string args, out CagePlace place, out Vector3 position, out float cageRadius, out float? cageRadiusZ, out string name)
    {
        place = null!;
        position = default;
        cageRadius = 0;
        cageRadiusZ = null;
        name = "";

        var quote = args.IndexOf('"');
        if (quote < 0 || !args.EndsWith('"') || quote == args.Length - 1)
            return false;
        name = args[(quote + 1)..^1];
        if (!CageLimits.IsValidName(name))
            return false;
        name = name.Trim();

        var tokens = args[..quote].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length != 3 || !CagePlace.TryParse(tokens[0], out place))
            return false;

        if (!tokens[1].StartsWith("pos:", StringComparison.OrdinalIgnoreCase))
            return false;
        var parts = tokens[1][4..].Split(',');
        if (parts.Length != 3
            || !float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
            || !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
            || !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var z)
            || !float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z))
            return false;
        position = new Vector3(x, y, z);

        if (tokens[2].Equals(PinWord, StringComparison.OrdinalIgnoreCase))
            return true;
        if (!tokens[2].StartsWith("r:", StringComparison.OrdinalIgnoreCase))
            return false;
        var sizes = tokens[2][2..].Split('x');
        if (sizes.Length > 2 || !TryParseRadius(sizes[0], out cageRadius))
            return false;
        if (sizes.Length == 1)
            return true;
        if (!TryParseRadius(sizes[1], out var depth))
            return false;
        cageRadiusZ = depth;
        return true;
    }

    private static bool TryParseRadius(string text, out float value) =>
        float.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value)
        && value is >= CageLimits.MinRadius and <= CageLimits.MaxRadius;

    /// `r:<x>` for a square, `r:<x>x<z>` otherwise.
    private static string ComposeSize(SavedCage cage)
    {
        if (cage.Radius <= 0)
            return PinWord;
        var half = CageLimits.HalfExtents(cage.Radius, cage.RadiusZ);
        return half.X == half.Y
            ? string.Create(CultureInfo.InvariantCulture, $"r:{half.X:0.#}")
            : string.Create(CultureInfo.InvariantCulture, $"r:{half.X:0.#}x{half.Y:0.#}");
    }

    /// The Owner's side of the grammar, so both ends agree on it.
    public static string ComposeClose(SavedCage cage, bool bring = false)
    {
        var body = string.Create(CultureInfo.InvariantCulture,
            $"cage armfor:{CageLimits.ClampArmSeconds(cage.ArmSeconds)} {(bring ? BringWord + " " : "")}{CloseWord} {cage.Place.ToWire()} pos:{cage.Position.X:0.0},{cage.Position.Y:0.0},{cage.Position.Z:0.0} {ComposeSize(cage)} \"{cage.Name.Trim()}\"");
        return cage.LockSeconds is { } seconds ? LockTimerOption.InsertSeconds(body, seconds) : body;
    }

    public static string ComposeOpen() => $"cage {OpenWord}";
}
