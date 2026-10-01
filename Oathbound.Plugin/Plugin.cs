using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Oathbound.Plugin.Commands;
using Oathbound.Plugin.Config;
using Oathbound.Plugin.Ipc;
using Oathbound.Plugin.Relay;
using Oathbound.Plugin.Safety;
using Oathbound.Plugin.UI;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Game.Command;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Game.Text;
using Dalamud.IoC;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace Oathbound.Plugin;

public sealed class Plugin : IDalamudPlugin
{
    private int pendingRestraintCleanup;
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static ITargetManager TargetManager { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IKeyState KeyState { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static INotificationManager NotificationManager { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IDtrBar DtrBar { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static IGameConfig GameConfig { get; private set; } = null!;
    [PluginService] internal static IPartyList PartyList { get; private set; } = null!;

    private const string CommandName = "/oathbound";
    private const string PanicCommandName = "/oathboundpanic";
    private const string SettingsCommandName = "/oathboundsettings";

    /// Backward-compatible aliases (collar/pairing, collar/ui-organization delta specs) - each points at
    /// the exact same handler delegate as its primary command above, so existing macros/keybinds on the
    /// old names keep working identically after the rename, with no behavior duplicated between them.
    /// `ShorthandCommandName` only aliases the main window toggle, not panic/settings - those already have
    /// full mnemonic names and are lower-frequency, so a shorthand adds more accidental-trigger risk than
    /// convenience there.
    private const string LegacyCommandName = "/collar";
    private const string LegacyPanicCommandName = "/collarpanic";
    private const string LegacySettingsCommandName = "/collarsettings";
    private const string ShorthandCommandName = "/ob";

    public PluginConfig Configuration { get; }
    public CatalogStore CatalogStore { get; } = new();

    public readonly WindowSystem WindowSystem = new("Oathbound");

    /// collar/catalog-sync: the one native save/open dialog instance backing Export/"Import commands" -
    /// owned here rather than per-window since both Settings (export) and CollarWindow (import) need it,
    /// same "shared, drawn every frame from the UiBuilder.Draw hook" shape WindowSystem already has.
    public readonly FileDialogManager FileDialogManager = new();
    private CollarWindow CollarWindow { get; }
    public ModuleWindow ModuleWindow { get; }
    /// collar/ui-organization "Sub Control window stays docked to the main window": must stay registered
    /// with `WindowSystem` immediately after `CollarWindow` (see the registration call below) so it always
    /// reads `CollarWindow`'s current-frame position, not the previous frame's - Dear ImGui windows here are
    /// drawn in registration order.
    public SubControlWindow SubControlWindow { get; }
    internal SettingsWindow SettingsWindow { get; }
    private WelcomeWindow WelcomeWindow { get; }
    public AnimationPickerWindow AnimationPickerWindow { get; }
    public CustomizePresetPickerWindow CustomizePresetPickerWindow { get; }
    public ItemPickerWindow ItemPickerWindow { get; }
    public FavoritesWindow FavoritesWindow { get; }
    private QuickAccessMenuHost QuickAccessMenuHost { get; }
    private RecoveryCodeWindow RecoveryCodeWindow { get; }

    /// collar/ui-organization "A server info bar entry always opens the quick-access menu": the
    /// only access point for QuickAccessMenu (the on-screen button was removed).
    private readonly IDtrBarEntry favoritesDtrEntry;

    /// Shown only while a toy is running (Sub: actual state; Owner: estimate from what was sent), so both
    /// sides can see at a glance that something is playing without opening the window.
    private readonly IDtrBarEntry toyStatusDtrEntry;
    private long nextToyStatusDtrUpdateTicks;

    /// collar/onboarding: owns the guided-tutorial sequence and step index; CollarWindow only reads
    /// `CurrentStep` each frame and exposes `SetActiveModuleForTutorial` for this to call.
    public TutorialDriver TutorialDriver { get; }

    public SubRuntimeState RuntimeState { get; }

    public GlamourerIpc GlamourerIpc { get; }
    public SlotLockManager SlotLockManager { get; }
    public HonorificIpc HonorificIpc { get; }
    public PenumbraIpc PenumbraIpc { get; }
    private readonly TemporaryModSettingsCoordinator temporaryModSettings;
    public MoodlesIpc MoodlesIpc { get; }
    public CustomizePlusIpc CustomizePlusIpc { get; }
    public IntifaceIpc IntifaceIpc { get; }
    public LifestreamIpc LifestreamIpc { get; }
    public VnavmeshIpc VnavmeshIpc { get; }
    public DependencyStatusService DependencyStatus { get; }
    public MovementLockService MovementLockService { get; }
    public WalkOnlyService WalkOnlyService { get; }
    public ActionBlockService ActionBlockService { get; }
    public ChatGagService ChatGagService { get; }
    public RestrictionRuleManager RestrictionRuleManager { get; }
    public ToyTriggerEvaluator ToyTriggerEvaluator { get; }
    public EmoteWatcher EmoteWatcher { get; }
    public ReactionService ReactionService { get; }

    public DeviceIdentityService DeviceIdentityService { get; }
    public RelayClient RelayClient { get; }
    public PairingService PairingService { get; }
    public CodePairingService CodePairingService { get; }
    public BackupService BackupService { get; }
    public RevocationService RevocationService { get; }
    public TitleCommand TitleCommand { get; }
    public OutfitCommand OutfitCommand { get; }
    public GestureCommand GestureCommand { get; }
    public FollowCommand FollowCommand { get; }
    public CollarCommand CollarCommand { get; }
    public MoodlesCommand MoodlesCommand { get; }
    public RestraintCommand RestraintCommand { get; }
    public ToyControlCommand ToyControlCommand { get; }
    public CustomTriggerCommand CustomTriggerCommand { get; }
    public TeleportCommand TeleportCommand { get; }
    public CatalogSyncService CatalogSyncService { get; }
    public CatalogSyncRelayService CatalogSyncRelayService { get; }
    public CatalogMailboxService CatalogMailboxService { get; }
    public CatalogAutoSync CatalogAutoSync { get; }
    public ChatComposer ChatComposer { get; }
    public ChatSender ChatSender { get; }
    public OwnerToyStatusTracker OwnerToyStatus { get; }
    public OwnerStatusEstimateTracker OwnerStatusEstimates { get; }
    public StatusIndicatorState StatusIndicators { get; }
    private readonly LeashRenderer leashRenderer;
    private readonly LeashTravelWatcher leashTravelWatcher;
    private readonly StatusIconRenderer statusIconRenderer;
    public ChatCommandListener ChatCommandListener { get; }

    public PanicHandler PanicHandler { get; }

    private bool panicHotkeyWasPressed;
    private DateTime nextRevocationOutboxRetryUtc = DateTime.MinValue;
    private DateTime nextRevocationCheckUtc = DateTime.MinValue;
    private DateTime nextPairStatusCheckUtc = DateTime.MinValue;

    /// collar/relay-service "requests stop on logout/disposal": recurring background relay work (the
    /// outbox retry and the missed-revocation check) is cancelled and restarted on every logout, and
    /// cancelled for good in Dispose(). One-shot user-triggered relay actions (Send Invitation, Accept,
    /// panic's revocation publish) are intentionally not tied to this - they're already bounded by
    /// RelayClient's own HTTP timeout, and cancelling a button click the user just made because they
    /// happened to log out mid-request would be a worse experience than just letting it time out normally.
    private CancellationTokenSource relayBackgroundWorkCts = new();

    public Plugin()
    {
        ECommons.ECommonsMain.Init(PluginInterface, this);

        Configuration = PluginInterface.GetPluginConfig() as PluginConfig ?? new PluginConfig();
        CatalogStore.LoadOrMigrate(Configuration);
        MigrateConfiguration();

        RuntimeState = new SubRuntimeState(Configuration);
        EmoteWatcher = new EmoteWatcher();

        GlamourerIpc = new GlamourerIpc();
        SlotLockManager = new SlotLockManager(Configuration, GlamourerIpc);
        HonorificIpc = new HonorificIpc();
        PenumbraIpc = new PenumbraIpc();
        MoodlesIpc = new MoodlesIpc();
        CustomizePlusIpc = new CustomizePlusIpc();
        IntifaceIpc = new IntifaceIpc();
        LifestreamIpc = new LifestreamIpc();
        VnavmeshIpc = new VnavmeshIpc();
        DependencyStatus = new DependencyStatusService(GlamourerIpc, PenumbraIpc, HonorificIpc, MoodlesIpc, CustomizePlusIpc, LifestreamIpc, VnavmeshIpc, IntifaceIpc);
        MovementLockService = new MovementLockService();
        WalkOnlyService = new WalkOnlyService();
        ActionBlockService = new ActionBlockService(WalkOnlyService);
        WalkOnlyService.SprintInterceptorAvailable = ActionBlockService.IsAvailable;
        ChatGagService = new ChatGagService(CustomizePlusIpc);
        RestrictionRuleManager = new RestrictionRuleManager();
        RestrictionRuleManager.RegisterEnforcer(RestraintRuleKind.ForcedPose, new MovementLockEnforcer(MovementLockService, "Restraints"));
        RestrictionRuleManager.RegisterEnforcer(RestraintRuleKind.WalkOnly, WalkOnlyService);
        RestrictionRuleManager.RegisterEnforcer(RestraintRuleKind.ActionBlock, ActionBlockService);
        RestrictionRuleManager.RegisterEnforcer(RestraintRuleKind.Gagged, ChatGagService);
        RestrictionRuleManager.RegisterEnforcer(RestraintRuleKind.FullBodyCuffed, new MovementLockEnforcer(MovementLockService, "RestraintsFullBody"));

        DeviceIdentityService = new DeviceIdentityService(Configuration);
        DeviceIdentityService.EnsureIdentity();
        RelayClient = new RelayClient(Configuration, DeviceIdentityService);
        RevocationService = new RevocationService(Configuration, RelayClient, DeviceIdentityService);

        TitleCommand = new TitleCommand(HonorificIpc, RuntimeState);
        // Built before Outfit/Follow/Restraint/Collar: each of them holds its attached moodle through it
        // (collar/attached-moodles).
        MoodlesCommand = new MoodlesCommand(Configuration, MoodlesIpc, CatalogStore, new AttachedMoodleLedger(Configuration, MoodlesIpc));
        OutfitCommand = new OutfitCommand(Configuration, GlamourerIpc, SlotLockManager, RuntimeState, MoodlesCommand);
        temporaryModSettings = new TemporaryModSettingsCoordinator(PenumbraIpc);
        GestureCommand = new GestureCommand(Configuration, PenumbraIpc, temporaryModSettings, CatalogStore);
        ReactionService = new ReactionService(Configuration, RuntimeState, EmoteWatcher, GlamourerIpc, SlotLockManager, PenumbraIpc, temporaryModSettings, MoodlesIpc, RestrictionRuleManager);
        // Before Follow: the leash rides Teleport's journey across areas (collar/leash-travel).
        TeleportCommand = new TeleportCommand(Configuration, LifestreamIpc, VnavmeshIpc, MovementLockService);
        FollowCommand = new FollowCommand(Configuration, MovementLockService, RuntimeState, MoodlesCommand, TeleportCommand);
        CollarCommand = new CollarCommand(Configuration, SlotLockManager, RuntimeState, MoodlesCommand);
        RestraintCommand = new RestraintCommand(Configuration, GlamourerIpc, PenumbraIpc, SlotLockManager, RestrictionRuleManager, RuntimeState, temporaryModSettings, ChatGagService, CatalogStore, MoodlesCommand);
        ToyControlCommand = new ToyControlCommand(IntifaceIpc, RuntimeState, Configuration);
        ToyTriggerEvaluator = new ToyTriggerEvaluator(Configuration, ToyControlCommand, RuntimeState, RestrictionRuleManager, EmoteWatcher);
        CustomTriggerCommand = new CustomTriggerCommand(Configuration, TitleCommand, OutfitCommand, GestureCommand, MoodlesCommand, RestraintCommand);
        CatalogSyncService = new CatalogSyncService(Configuration, OutfitCommand, GestureCommand, MoodlesCommand, RestraintCommand, CatalogStore);
        ChatComposer = new ChatComposer(Configuration);
        ChatSender = new ChatSender();
        OwnerToyStatus = new OwnerToyStatusTracker(Configuration, ChatSender);
        OwnerStatusEstimates = new OwnerStatusEstimateTracker(Configuration, ChatSender);
        leashTravelWatcher = new LeashTravelWatcher(Configuration, OwnerStatusEstimates, ChatComposer, ChatSender);
        StatusIndicators = new StatusIndicatorState(Configuration, RuntimeState, RestraintCommand, RestrictionRuleManager, FollowCommand, OwnerStatusEstimates);
        leashRenderer = new LeashRenderer(Configuration, StatusIndicators);
        statusIconRenderer = new StatusIconRenderer(Configuration, StatusIndicators);
        PairingService = new PairingService(Configuration, RelayClient, DeviceIdentityService, ChatComposer, ChatSender, CollarCommand, RevocationService);
        PairingService.PairingEnded += QueueRestraintCleanup;
        PairingService.PairingEnded += TeleportCommand.StopIfSourcePairingEnded;
        // collar/pairing: a pairing the relay reports as unpaired gets exactly the teardown a verified unpair
        // notice gets (PairingEnded above then queues the restraint cleanup and teleport stop).
        RevocationService.EndPairingLocally = PairingService.EndFromVerifiedPeerNotice;
        CodePairingService = new CodePairingService(Configuration, RelayClient, DeviceIdentityService, PairingService, CollarCommand);
        BackupService = new BackupService(Configuration, RelayClient, DeviceIdentityService, RevocationService);
        PairingService.BeforeIdentityReset = BackupService.DeleteForIdentityResetAsync;
        RevocationService.PairingRevoked += QueueRestraintCleanup;
        RevocationService.PairingRevoked += TeleportCommand.StopIfSourcePairingEnded;
        CatalogSyncRelayService = new CatalogSyncRelayService(Configuration, RelayClient, DeviceIdentityService, ChatComposer, ChatSender, CatalogSyncService);
        CatalogMailboxService = new CatalogMailboxService(Configuration, RelayClient, DeviceIdentityService, CatalogSyncService);
        CatalogAutoSync = new CatalogAutoSync(Configuration, CatalogMailboxService, CatalogSyncService, OutfitCommand, GestureCommand, RestraintCommand, MoodlesCommand,
            () => relayBackgroundWorkCts.Token);
        ChatCommandListener = new ChatCommandListener(Configuration, PairingService, CatalogSyncRelayService, TitleCommand, OutfitCommand, GestureCommand, FollowCommand, CollarCommand, MoodlesCommand, RestraintCommand, ToyControlCommand, CustomTriggerCommand, TeleportCommand);

        PanicHandler = new PanicHandler(PairingService, GlamourerIpc, SlotLockManager, HonorificIpc, MovementLockService, RestrictionRuleManager, RestraintCommand, ToyControlCommand, RuntimeState, CollarCommand, ActionBlockService.Visuals, MoodlesCommand.Ledger, GestureCommand, ReactionService, TeleportCommand);
        PanicHandler.AfterLocalRevert = CustomTriggerCommand.ForgetEffects;

        ModuleWindow = new ModuleWindow(this);
        CollarWindow = new CollarWindow(this, ModuleWindow);
        SubControlWindow = new SubControlWindow(this, CollarWindow);
        SettingsWindow = new SettingsWindow(this);
        TutorialDriver = new TutorialDriver(this, CollarWindow);
        WelcomeWindow = new WelcomeWindow(this);
        AnimationPickerWindow = new AnimationPickerWindow(this);
        CustomizePresetPickerWindow = new CustomizePresetPickerWindow(this);
        ItemPickerWindow = new ItemPickerWindow(this);
        FavoritesWindow = new FavoritesWindow(this);
        QuickAccessMenuHost = new QuickAccessMenuHost(this);
        RecoveryCodeWindow = new RecoveryCodeWindow(this);

        favoritesDtrEntry = DtrBar.Get("Oathbound Quick Access");
        favoritesDtrEntry.Text = ((char)SeIconChar.BoxedStar).ToString();
        favoritesDtrEntry.Tooltip = "Favorited Collar commands";
        favoritesDtrEntry.OnClick = _ => QuickAccessMenu.Toggle();
        favoritesDtrEntry.Shown = true;

        toyStatusDtrEntry = DtrBar.Get("Oathbound Toy Status");
        toyStatusDtrEntry.Shown = false;
        toyStatusDtrEntry.OnClick = _ => ModuleWindow.IsOpen = true;

        WindowSystem.AddWindow(CollarWindow);
        // collar/ui-organization "Sub Control window stays docked to the main window": registered
        // immediately after CollarWindow so its PreDraw reads this frame's (not last frame's) position -
        // see SubControlWindow's own field declaration above for the full reasoning.
        WindowSystem.AddWindow(SubControlWindow);
        WindowSystem.AddWindow(ModuleWindow);
        WindowSystem.AddWindow(SettingsWindow);
        WindowSystem.AddWindow(WelcomeWindow);
        WindowSystem.AddWindow(AnimationPickerWindow);
        WindowSystem.AddWindow(CustomizePresetPickerWindow);
        WindowSystem.AddWindow(ItemPickerWindow);
        WindowSystem.AddWindow(FavoritesWindow);
        WindowSystem.AddWindow(QuickAccessMenuHost);
        WindowSystem.AddWindow(RecoveryCodeWindow);

        // collar/onboarding "Welcome window appears once on first plugin load": shown before CollarWindow
        // is ever opened for the first time, and never again once completed/dismissed.
        if (!Configuration.HasCompletedWelcome)
            WelcomeWindow.IsOpen = true;

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the Oathbound window.",
        });
        CommandManager.AddHandler(ShorthandCommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Shorthand for /oathbound - opens the Oathbound window.",
        });
        CommandManager.AddHandler(LegacyCommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Alias for /oathbound - opens the Oathbound window.",
        });
        CommandManager.AddHandler(PanicCommandName, new CommandInfo(OnPanicCommand)
        {
            HelpMessage = "Immediately unpair and revert all collar state. Append your safeword if one is configured in Settings, e.g. /oathboundpanic red.",
        });
        CommandManager.AddHandler(LegacyPanicCommandName, new CommandInfo(OnPanicCommand)
        {
            HelpMessage = "Alias for /oathboundpanic.",
        });
        CommandManager.AddHandler(SettingsCommandName, new CommandInfo(OnSettingsCommand)
        {
            HelpMessage = "Open Oathbound settings (role, pairing, aliases).",
        });
        CommandManager.AddHandler(LegacySettingsCommandName, new CommandInfo(OnSettingsCommand)
        {
            HelpMessage = "Alias for /oathboundsettings.",
        });

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.Draw += FileDialogManager.Draw;
        PluginInterface.UiBuilder.Draw += leashRenderer.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += SettingsWindow.Toggle;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;

        Framework.Update += OnFrameworkUpdate;
        ClientState.Login += OnLogin;
        ClientState.Logout += OnLogout;

        // collar/pairing "check the relay at login...": one check at startup, independent of the
        // low-frequency periodic schedule below (which is seeded past its own interval so it doesn't
        // immediately double up on this one).
        nextRevocationCheckUtc = DateTime.UtcNow.AddHours(6);
        FireAndForget(RevocationService.CheckForMissedRevocationAsync(relayBackgroundWorkCts.Token));

        Log.Information("Oathbound loaded.");
    }

    /// collar/pairing "check the relay at login": a fresh login is exactly the "at login" moment the spec
    /// means, distinct from the plugin merely loading (which can happen mid-session on a reload).
    private void OnLogin()
    {
        // collar/pairing: check every pairing against the relay right away, and pick up code invitations.
        nextPairStatusCheckUtc = DateTime.MinValue;
        CodePairingService.PollSoon();
        nextRevocationCheckUtc = DateTime.UtcNow.AddHours(6);
        FireAndForget(RevocationService.CheckForMissedRevocationAsync(relayBackgroundWorkCts.Token));
        CatalogAutoSync.OnLogin();
    }

    /// collar/relay-service "requests stop on logout": cancels any in-flight recurring relay background
    /// work and starts a fresh token source so work resumed after the next login isn't pre-cancelled.
    private void OnLogout(int type, int code)
    {
        RestraintCommand.ReleaseAllBoundAnimationsForPanic();
        RestrictionRuleManager.ReleaseAllForPanic();
        RelayClient.CancelPendingRequests();
        relayBackgroundWorkCts.Cancel();
        relayBackgroundWorkCts.Dispose();
        relayBackgroundWorkCts = new CancellationTokenSource();
    }

    public void Dispose()
    {
        PairingService.PairingEnded -= QueueRestraintCleanup;
        PairingService.PairingEnded -= TeleportCommand.StopIfSourcePairingEnded;
        RevocationService.PairingRevoked -= QueueRestraintCleanup;
        RevocationService.PairingRevoked -= TeleportCommand.StopIfSourcePairingEnded;
        TeleportCommand.Stop("plugin unloading");
        DependencyStatus.Dispose();
        RestraintCommand.ReleaseAllBoundAnimationsForPanic();
        Framework.Update -= OnFrameworkUpdate;
        ClientState.Login -= OnLogin;
        ClientState.Logout -= OnLogout;
        CatalogAutoSync.Dispose();
        relayBackgroundWorkCts.Cancel();
        relayBackgroundWorkCts.Dispose();

        RelayClient.Dispose();

        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.Draw -= FileDialogManager.Draw;
        PluginInterface.UiBuilder.Draw -= leashRenderer.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= SettingsWindow.Toggle;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;

        FileDialogManager.Reset();
        WindowSystem.RemoveAllWindows();
        CollarWindow.Dispose();
        SettingsWindow.Dispose();
        WelcomeWindow.Dispose();
        AnimationPickerWindow.Dispose();
        CustomizePresetPickerWindow.Dispose();
        ItemPickerWindow.Dispose();
        QuickAccessMenuHost.Dispose();
        favoritesDtrEntry.Remove();
        toyStatusDtrEntry.Remove();
        OwnerToyStatus.Dispose();
        OwnerStatusEstimates.Dispose();
        leashRenderer.Dispose();
        statusIconRenderer.Dispose();

        CommandManager.RemoveHandler(CommandName);
        CommandManager.RemoveHandler(ShorthandCommandName);
        CommandManager.RemoveHandler(LegacyCommandName);
        CommandManager.RemoveHandler(PanicCommandName);
        CommandManager.RemoveHandler(LegacyPanicCommandName);
        CommandManager.RemoveHandler(SettingsCommandName);
        CommandManager.RemoveHandler(LegacySettingsCommandName);

        ChatCommandListener.Dispose();
        MovementLockService.Dispose();
        ActionBlockService.Dispose();
        ChatGagService.Dispose();
        IntifaceIpc.Dispose();
        ToyTriggerEvaluator.Dispose();
        ReactionService.Dispose();
        SlotLockManager.Dispose();
        GlamourerIpc.Dispose();
        // After every feature above has released what it could: drops any claim still held, since a locked
        // temporary setting can only be removed with Oathbound's own key.
        temporaryModSettings.Dispose();
        PenumbraIpc.Dispose();

        ECommons.ECommonsMain.Dispose();
    }

    private void OnCommand(string command, string args) => ToggleMainUi();

    /// The safeword mechanic (collar/pairing): with no PanicSafeword configured, panic still triggers
    /// unconditionally - an unconfigured safeword must never be the reason panic stops working. With one
    /// configured, the typed word has to match (case-insensitive) - there's no visible button anymore, see
    /// CollarWindow's header.
    private void OnPanicCommand(string command, string args)
    {
        var safeword = Configuration.PanicSafeword;
        if (!string.IsNullOrWhiteSpace(safeword) && !string.Equals(args.Trim(), safeword.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            Log.Information("Panic word didn't match the configured safeword - ignored.");
            return;
        }

        PanicHandler.Panic();
    }

    private void OnSettingsCommand(string command, string args) => SettingsWindow.Toggle();

    public void ToggleSettingsUi() => SettingsWindow.Toggle();

    private void ToggleMainUi() => CollarWindow.Toggle();

    /// collar/ui-organization "A movable on-screen button opens the quick-access favorites menu": the
    /// menu's "Open main window" control.
    public void OpenMainWindow() => CollarWindow.OpenMainWindow();

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (Interlocked.Exchange(ref pendingRestraintCleanup, 0) != 0)
            RestraintCommand.ForceUnlock();
        // The panic hotkey is a plain edge-detected key check - deliberately simple so it keeps working
        // even if everything else about the plugin (chat parsing, IPC) is broken.
        if (Configuration.PanicHotkey != VirtualKey.NO_KEY)
        {
            var isPressed = KeyState[Configuration.PanicHotkey];
            if (isPressed && !panicHotkeyWasPressed)
                PanicHandler.Panic();
            panicHotkeyWasPressed = isPressed;
        }

        GestureCommand.OnFrameworkUpdate();
        RestraintCommand.OnFrameworkUpdate();
        ToyControlCommand.OnFrameworkUpdate();
        EmoteWatcher.Poll();
        ToyTriggerEvaluator.OnFrameworkUpdate();
        ReactionService.OnFrameworkUpdate();
        MovementLockService.OnFrameworkUpdate();
        FollowCommand.OnFrameworkUpdate();
        WalkOnlyService.OnFrameworkUpdate();
        ActionBlockService.OnFrameworkUpdate();
        MoodlesCommand.Ledger.OnFrameworkUpdate();
        CollarCommand.OnFrameworkUpdate();
        TeleportCommand.OnFrameworkUpdate();
        leashTravelWatcher.OnFrameworkUpdate();
        TitleCommand.OnFrameworkUpdate();

        var utcNow = DateTime.UtcNow;
        if (utcNow >= nextRevocationOutboxRetryUtc)
        {
            nextRevocationOutboxRetryUtc = utcNow.AddSeconds(30);
            FireAndForget(RevocationService.RetryOutboxAsync(relayBackgroundWorkCts.Token));
        }
        // collar/pairing "check... on a low-frequency bounded schedule": no more often than every six
        // hours, with jitter so many clients don't all poll on the same clock edge.
        if (utcNow >= nextRevocationCheckUtc)
        {
            nextRevocationCheckUtc = utcNow.AddHours(6).AddSeconds(Random.Shared.Next(0, 1800));
            FireAndForget(RevocationService.CheckForMissedRevocationAsync(relayBackgroundWorkCts.Token));
        }
        // collar/pairing "Unpairing reaches the other person even when they are offline": at login (see OnLogin)
        // and every pairStatusPollIntervalSeconds while logged in. Skipped while not logged in, so a
        // title-screen session doesn't spend relay requests.
        if (utcNow >= nextPairStatusCheckUtc && ClientState.IsLoggedIn)
        {
            nextPairStatusCheckUtc = utcNow.AddSeconds(RelayProtocolConstants.PairStatusPollIntervalSeconds).AddSeconds(Random.Shared.Next(0, 120));
            FireAndForget(RevocationService.CheckPairStatusAsync(relayBackgroundWorkCts.Token));
        }
        if (ClientState.IsLoggedIn)
        {
            CodePairingService.OnFrameworkUpdate(relayBackgroundWorkCts.Token);
            BackupService.OnFrameworkUpdate(relayBackgroundWorkCts.Token);
            // collar/pairing-recovery: show a newly issued recovery code once.
            if (BackupService.ShouldShowCodeDialog && !RecoveryCodeWindow.IsOpen)
                RecoveryCodeWindow.IsOpen = true;
        }
        // collar/catalog-sync automatic sync: hourly Sub rescans/publishes and Owner mailbox checks.
        CatalogAutoSync.OnFrameworkUpdate();
        UpdateToyStatusDtr();
    }

    private void UpdateToyStatusDtr()
    {
        var now = Environment.TickCount64;
        if (now < nextToyStatusDtrUpdateTicks) return;
        nextToyStatusDtrUpdateTicks = now + 500;

        if (UI.ToyStatusView.Dtr(ToyControlCommand, OwnerToyStatus.ForActivePairing) is { } status)
        {
            toyStatusDtrEntry.Text = status.Text;
            toyStatusDtrEntry.Tooltip = status.Tooltip;
            toyStatusDtrEntry.Shown = true;
        }
        else if (toyStatusDtrEntry.Shown)
            toyStatusDtrEntry.Shown = false;
    }

    private void QueueRestraintCleanup() => Interlocked.Exchange(ref pendingRestraintCleanup, 1);

    private void MigrateConfiguration()
    {
        var changed = false;
        if (Configuration.Version < 2)
        {
            Configuration.Version = 2;
            changed = true;
        }
        if (Configuration.Version < 3)
        {
            Configuration.MigrateFolderScopes();
            Configuration.Version = 3;
            changed = true;
        }
        // collar/onboarding: an install that predates the Welcome window/guided tutorial is treated as
        // already welcomed - it must never pop Welcome or auto-launch a tutorial in front of a user who is
        // already mid-session with a paired, configured setup (design.md's "Existing installs are treated
        // as already welcomed").
        if (Configuration.Version < 4)
        {
            Configuration.HasCompletedWelcome = true;
            Configuration.HasSeenOwnerTutorial = true;
            Configuration.HasSeenSubTutorial = true;
            Configuration.Version = 4;
            changed = true;
        }
        // collar/multi-pairing: move the old single `Pairing` field into the new `Pairings` list, active,
        // so an already-paired install keeps working identically after upgrading. A never-paired install's
        // default `Pairing` object carries no real state, so it's discarded rather than added as a dead
        // entry.
        if (Configuration.Version < 5)
        {
            if (Configuration.Pairing is { } legacy &&
                (!string.IsNullOrWhiteSpace(legacy.PeerName) || !string.IsNullOrWhiteSpace(legacy.PairIdHash)))
            {
                legacy.Direction = Configuration.Role == PluginRole.Owner
                    ? PairingDirection.OwnerSide
                    : PairingDirection.SubSide;
                Configuration.Pairings.Add(legacy);
                Configuration.ActivePairingId = legacy.Id;
            }
            Configuration.Pairing = null;
            Configuration.Version = 5;
            changed = true;
        }
        if (Configuration.Version < 6)
        {
            Configuration.MigrateLegacyGagRules();
            Configuration.Version = 6;
            changed = true;
        }
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (Configuration.PendingRelayOperations.RemoveAll(o => o.ExpiresAt <= now) > 0)
            changed = true;

        foreach (var cmd in Configuration.QuickCommands.Gestures)
        {
            if (cmd.Target is null || !Configuration.GestureMapping.ImportedPeerCatalog.TryGetValue(cmd.Target, out var entry)) continue;
            // collar/animation-labels: entries imported before display labels existed saved the old +1 pose
            // numbering as their Label - relabel them, but only while the Owner hasn't renamed them.
            if (cmd.Label == entry.Label && entry.Label != entry.DisplayLabel)
            {
                cmd.Label = entry.DisplayLabel;
                changed = true;
            }
            var readable = $"gesture {CommandSelector.Quote(CommandSelector.GestureSelector(entry, Configuration.GestureMapping.ImportedPeerCatalog.Values))}";
            if (cmd.Command == readable) continue;
            cmd.Command = readable;
            changed = true;
        }
        foreach (var cmd in Configuration.QuickCommands.Moodles)
        {
            if (cmd.Target is null || !cmd.Command.StartsWith("moodle apply ", StringComparison.OrdinalIgnoreCase)) continue;
            var readable = $"moodle apply {CommandSelector.Quote(MoodlesTextFormat.StripMarkup(cmd.Target))}";
            if (cmd.Command == readable) continue;
            cmd.Command = readable;
            changed = true;
        }
        foreach (var cmd in Configuration.QuickCommands.Restraints.Where(c => c.RestraintRules is { Count: > 0 }))
        {
            foreach (var rule in cmd.RestraintRules!.Where(r => r.AnimationId is not null && r.AnimationLabel is null))
                if (Configuration.GestureMapping.ImportedPeerCatalog.TryGetValue(rule.AnimationId!, out var entry))
                    rule.AnimationLabel = CommandSelector.GestureSelector(entry, Configuration.GestureMapping.ImportedPeerCatalog.Values);
            var readable = cmd.RestraintCatalogId is { } catalogId && cmd.RestraintItemId is { } itemId
                ? RestraintCommand.BuildCatalogLockCommand(catalogId, cmd.Label, itemId, cmd.RestraintRules!)
                : RestraintCommand.BuildLockCommand(cmd.Label, cmd.RestraintRules!);
            if (cmd.Command == readable) continue;
            cmd.Command = readable;
            changed = true;
        }
        if (changed) Configuration.Save();
    }

    internal static void FireAndForget(Task task) =>
        _ = task.ContinueWith(t =>
        {
            if (t.Exception is { } ex)
                Log.Error(ex, "Unhandled error in a collar command.");
        }, TaskScheduler.Default);
}
