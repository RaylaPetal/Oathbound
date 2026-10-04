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
using Dalamud.Game.Text.SeStringHandling;
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
    [PluginService] internal static IDutyState DutyState { get; private set; } = null!;

    private const string CommandName = "/ob";
    private const string PanicCommandName = "/obpanic";
    private const string SettingsCommandName = "/obsettings";

    private const string LongCommandName = "/oathbound";
    private const string LongPanicCommandName = "/oathboundpanic";
    private const string LongSettingsCommandName = "/oathboundsettings";

    public PluginConfig Configuration { get; }
    public CatalogStore CatalogStore { get; } = new();

    public readonly WindowSystem WindowSystem = new("Oathbound");

    /// Shared by Settings (export) and CollarWindow (import).
    public readonly FileDialogManager FileDialogManager = new();
    internal CollarWindow CollarWindow { get; }
    public ModuleWindow ModuleWindow { get; }
    /// Must be registered right after CollarWindow: windows draw in registration order, and it docks to CollarWindow's current-frame position.
    public SubControlWindow SubControlWindow { get; }
    internal SettingsWindow SettingsWindow { get; }
    private WelcomeWindow WelcomeWindow { get; }
    public AnimationPickerWindow AnimationPickerWindow { get; }
    public CustomizePresetPickerWindow CustomizePresetPickerWindow { get; }
    public ItemPickerWindow ItemPickerWindow { get; }
    public FavoritesWindow FavoritesWindow { get; }
    private QuickAccessMenuHost QuickAccessMenuHost { get; }
    private RecoveryCodeWindow RecoveryCodeWindow { get; }

    /// The only access point for QuickAccessMenu.
    private readonly IDtrBarEntry favoritesDtrEntry;

    /// Shown only while a toy is running.
    private readonly IDtrBarEntry toyStatusDtrEntry;
    private long nextToyStatusDtrUpdateTicks;

    public TutorialService Tutorial { get; }

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
    public RulebookMailboxService RulebookMailboxService { get; }
    public Rulebook.RulebookService RulebookService { get; }
    public ChatComposer ChatComposer { get; }
    public ChatSender ChatSender { get; }
    public OwnerToyStatusTracker OwnerToyStatus { get; }
    public OwnerStatusEstimateTracker OwnerStatusEstimates { get; }
    public OwnerCollarStatusStore OwnerCollarStatus { get; }
    public RelayActivity RelayActivity { get; } = new();
    private readonly CollarStatusReporter collarStatusReporter;
    public StatusIndicatorState StatusIndicators { get; }
    private readonly LeashRenderer leashRenderer;
    private readonly LeashTravelWatcher leashTravelWatcher;
    private readonly LeashOffNotifier leashOffNotifier;
    private readonly StatusIconRenderer statusIconRenderer;
    public ChatCommandListener ChatCommandListener { get; }

    public PanicHandler PanicHandler { get; }

    private bool panicHotkeyWasPressed;
    private DateTime nextRevocationOutboxRetryUtc = DateTime.MinValue;
    private DateTime nextRevocationCheckUtc = DateTime.MinValue;
    private DateTime nextPairStatusCheckUtc = DateTime.MinValue;

    /// Recurring relay work is cancelled on logout and disposal. One-shot user actions aren't tied to it; RelayClient's timeout bounds them.
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
        // Built before Outfit/Follow/Restraint/Collar, which hold their attached moodles through it.
        MoodlesCommand = new MoodlesCommand(Configuration, MoodlesIpc, CatalogStore, new AttachedMoodleLedger(Configuration, MoodlesIpc));
        OutfitCommand = new OutfitCommand(Configuration, GlamourerIpc, SlotLockManager, RuntimeState, MoodlesCommand);
        temporaryModSettings = new TemporaryModSettingsCoordinator(PenumbraIpc);
        GestureCommand = new GestureCommand(Configuration, PenumbraIpc, temporaryModSettings, CatalogStore, MovementLockService);
        ReactionService = new ReactionService(Configuration, RuntimeState, EmoteWatcher, GlamourerIpc, SlotLockManager, PenumbraIpc, temporaryModSettings, MoodlesIpc, RestrictionRuleManager);
        // Before Follow: the leash rides Teleport's journey across areas.
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
        OwnerCollarStatus = new OwnerCollarStatusStore(Configuration);
        RevocationService.PairStatusFetched += OwnerCollarStatus.Update;
        RevocationService.PairStatusFetched += RelayActivity.Update;
        collarStatusReporter = new CollarStatusReporter(Configuration, RelayClient, SlotLockManager, GlamourerIpc, RuntimeState);
        leashTravelWatcher = new LeashTravelWatcher(Configuration, OwnerStatusEstimates, ChatComposer, ChatSender);
        leashOffNotifier = new LeashOffNotifier(Configuration, FollowCommand, ChatComposer, ChatSender);
        StatusIndicators = new StatusIndicatorState(Configuration, RuntimeState, RestraintCommand, RestrictionRuleManager, FollowCommand, OwnerStatusEstimates);
        leashRenderer = new LeashRenderer(Configuration, StatusIndicators, FollowCommand);
        statusIconRenderer = new StatusIconRenderer(StatusIndicators);
        PairingService = new PairingService(Configuration, RelayClient, DeviceIdentityService, ChatComposer, ChatSender, CollarCommand, RevocationService);
        PairingService.PairingEnded += QueueRestraintCleanup;
        PairingService.PairingEnded += TeleportCommand.StopIfSourcePairingEnded;
        PairingService.PairingEnded += GestureCommand.StopIfSourcePairingEnded;
        // A pairing the relay reports as unpaired gets the same teardown as a verified unpair notice.
        RevocationService.EndPairingLocally = PairingService.EndFromVerifiedPeerNotice;
        CodePairingService = new CodePairingService(Configuration, RelayClient, DeviceIdentityService, PairingService, CollarCommand);
        BackupService = new BackupService(Configuration, RelayClient, DeviceIdentityService, RevocationService);
        PairingService.BeforeIdentityReset = BackupService.DeleteForIdentityResetAsync;
        RevocationService.PairingRevoked += QueueRestraintCleanup;
        RevocationService.PairingRevoked += TeleportCommand.StopIfSourcePairingEnded;
        RevocationService.PairingRevoked += GestureCommand.StopIfSourcePairingEnded;
        CatalogSyncRelayService = new CatalogSyncRelayService(Configuration, RelayClient, DeviceIdentityService, ChatComposer, ChatSender, CatalogSyncService);
        CatalogMailboxService = new CatalogMailboxService(Configuration, RelayClient, DeviceIdentityService, CatalogSyncService);
        CatalogAutoSync = new CatalogAutoSync(Configuration, CatalogMailboxService, CatalogSyncService, OutfitCommand, GestureCommand, RestraintCommand, MoodlesCommand,
            () => relayBackgroundWorkCts.Token);
        RevocationService.PairStatusFetched += CatalogAutoSync.OnPairStatus;
        ChatCommandListener = new ChatCommandListener(Configuration, PairingService, CatalogSyncRelayService, TitleCommand, OutfitCommand, GestureCommand, FollowCommand, CollarCommand, MoodlesCommand, RestraintCommand, ToyControlCommand, CustomTriggerCommand, TeleportCommand, leashOffNotifier, OwnerStatusEstimates);

        PanicHandler = new PanicHandler(PairingService, GlamourerIpc, SlotLockManager, HonorificIpc, MovementLockService, RestrictionRuleManager, RestraintCommand, ToyControlCommand, RuntimeState, FollowCommand, ActionBlockService.Visuals, MoodlesCommand.Ledger, GestureCommand, ReactionService, TeleportCommand);
        PanicHandler.AfterLocalRevert = CustomTriggerCommand.ForgetEffects;

        RulebookMailboxService = new RulebookMailboxService(Configuration, RelayClient, DeviceIdentityService);
        RulebookService = new Rulebook.RulebookService(Configuration, RulebookMailboxService, ChatCommandListener, ChatComposer, ChatSender, EmoteWatcher, GestureCommand, () => relayBackgroundWorkCts.Token);
        RevocationService.PairStatusFetched += RulebookService.OnPairStatus;
        PanicHandler.OnPanic = RulebookService.OnPanic;

        ModuleWindow = new ModuleWindow(this);
        CollarWindow = new CollarWindow(this, ModuleWindow);
        SubControlWindow = new SubControlWindow(this, CollarWindow);
        SettingsWindow = new SettingsWindow(this);
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
        Tutorial = new TutorialService(this, on => quickAccessHighlight = on, () => favoritesDtrEntry.UserHidden);

        toyStatusDtrEntry = DtrBar.Get("Oathbound Toy Status");
        toyStatusDtrEntry.Shown = false;
        toyStatusDtrEntry.OnClick = _ => ModuleWindow.IsOpen = true;

        WindowSystem.AddWindow(CollarWindow);
        // Right after CollarWindow - see SubControlWindow.
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

        if (!Configuration.HasCompletedWelcome)
            WelcomeWindow.IsOpen = true;

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the Oathbound window.",
        });
        CommandManager.AddHandler(PanicCommandName, new CommandInfo(OnPanicCommand)
        {
            HelpMessage = "Your safeword: immediately remove everything applied to you except a locked collar (your pairings stay). Append your safeword if one is configured in Settings, e.g. /obpanic red.",
        });
        CommandManager.AddHandler(SettingsCommandName, new CommandInfo(OnSettingsCommand)
        {
            HelpMessage = "Open Oathbound settings (role, pairing, safeword).",
        });
        // The long names stay working for existing macros and keybinds, but aren't advertised.
        CommandManager.AddHandler(LongCommandName, new CommandInfo(OnCommand) { ShowInHelp = false });
        CommandManager.AddHandler(LongPanicCommandName, new CommandInfo(OnPanicCommand) { ShowInHelp = false });
        CommandManager.AddHandler(LongSettingsCommandName, new CommandInfo(OnSettingsCommand) { ShowInHelp = false });

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.Draw += FileDialogManager.Draw;
        PluginInterface.UiBuilder.Draw += leashRenderer.Draw;
        // After every window, so anchors reported this frame are current.
        PluginInterface.UiBuilder.Draw += Tutorial.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += SettingsWindow.Toggle;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;

        Framework.Update += OnFrameworkUpdate;
        ClientState.Login += OnLogin;
        ClientState.Logout += OnLogout;

        // One check at startup; the periodic schedule is seeded past its interval so it doesn't double up.
        nextRevocationCheckUtc = DateTime.UtcNow.AddHours(6);
        FireAndForget(RevocationService.CheckForMissedRevocationAsync(relayBackgroundWorkCts.Token));

        Log.Information("Oathbound loaded.");
    }

    /// A real login, not just a plugin reload mid-session.
    private void OnLogin()
    {
        // Check every pairing against the relay right away, and pick up code invitations.
        nextPairStatusCheckUtc = DateTime.MinValue;
        CodePairingService.PollSoon();
        nextRevocationCheckUtc = DateTime.UtcNow.AddHours(6);
        FireAndForget(RevocationService.CheckForMissedRevocationAsync(relayBackgroundWorkCts.Token));
        CatalogAutoSync.OnLogin();
    }

    /// Fresh token source so work resumed after the next login isn't pre-cancelled.
    private void OnLogout(int type, int code)
    {
        RestraintCommand.ReleaseAllBoundAnimationsForPanic();
        RestrictionRuleManager.ReleaseAllForPanic();
        Configuration.FlushPendingSave(force: true);
        RelayClient.CancelPendingRequests();
        relayBackgroundWorkCts.Cancel();
        relayBackgroundWorkCts.Dispose();
        relayBackgroundWorkCts = new CancellationTokenSource();
    }

    public void Dispose()
    {
        PairingService.PairingEnded -= QueueRestraintCleanup;
        PairingService.PairingEnded -= TeleportCommand.StopIfSourcePairingEnded;
        PairingService.PairingEnded -= GestureCommand.StopIfSourcePairingEnded;
        RevocationService.PairingRevoked -= QueueRestraintCleanup;
        RevocationService.PairingRevoked -= TeleportCommand.StopIfSourcePairingEnded;
        RevocationService.PairingRevoked -= GestureCommand.StopIfSourcePairingEnded;
        TeleportCommand.Stop("plugin unloading");
        GestureCommand.Stop();
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
        PluginInterface.UiBuilder.Draw -= Tutorial.Draw;
        Tutorial.Dispose();
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
        RevocationService.PairStatusFetched -= OwnerCollarStatus.Update;
        RevocationService.PairStatusFetched -= RelayActivity.Update;
        RevocationService.PairStatusFetched -= CatalogAutoSync.OnPairStatus;
        RevocationService.PairStatusFetched -= RulebookService.OnPairStatus;
        RulebookService.Dispose();
        OwnerStatusEstimates.Dispose();
        leashOffNotifier.Dispose();
        leashRenderer.Dispose();
        statusIconRenderer.Dispose();

        CommandManager.RemoveHandler(CommandName);
        CommandManager.RemoveHandler(PanicCommandName);
        CommandManager.RemoveHandler(SettingsCommandName);
        CommandManager.RemoveHandler(LongCommandName);
        CommandManager.RemoveHandler(LongPanicCommandName);
        CommandManager.RemoveHandler(LongSettingsCommandName);

        ChatCommandListener.Dispose();
        MovementLockService.Dispose();
        ActionBlockService.Dispose();
        ChatGagService.Dispose();
        IntifaceIpc.Dispose();
        ToyTriggerEvaluator.Dispose();
        ReactionService.Dispose();
        SlotLockManager.Dispose();
        GlamourerIpc.Dispose();
        // After every feature has released what it could: a locked temporary setting can only be removed with Oathbound's own key.
        temporaryModSettings.Dispose();
        PenumbraIpc.Dispose();
        // Last, so whatever the features above changed while releasing is written too.
        Configuration.FlushPendingSave(force: true);

        ECommons.ECommonsMain.Dispose();
    }

    private void OnCommand(string command, string args) => ToggleMainUi();

    /// With no safeword configured panic always triggers; with one, the typed word must match (case-insensitive).
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

    private static readonly string QuickAccessText = ((char)SeIconChar.BoxedStar).ToString();
    /// Set by the tutorial while it points at the Quick Access entry.
    private bool quickAccessHighlight;
    private bool? quickAccessShownHighlighted;

    /// Blinks the entry while the tutorial points at it, and restores it after.
    private void UpdateQuickAccessText()
    {
        var lit = quickAccessHighlight && Environment.TickCount64 / 500 % 2 == 0;
        if (quickAccessShownHighlighted == lit)
            return;
        quickAccessShownHighlighted = lit;
        favoritesDtrEntry.Text = lit
            ? new SeStringBuilder().AddUiForeground(500).AddUiGlow(501).AddText($"{QuickAccessText} Oathbound <<").AddUiGlowOff().AddUiForegroundOff().Build()
            : quickAccessHighlight ? $"{QuickAccessText} Oathbound <<" : QuickAccessText;
    }

    public void OpenMainWindow() => CollarWindow.OpenMainWindow();

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (Interlocked.Exchange(ref pendingRestraintCleanup, 0) != 0)
            RestraintCommand.ForceUnlock();
        // A plain edge-detected key check, so it keeps working even if chat parsing or IPC is broken.
        if (Configuration.PanicHotkey != VirtualKey.NO_KEY)
        {
            var isPressed = KeyState[Configuration.PanicHotkey];
            if (isPressed && !panicHotkeyWasPressed)
                PanicHandler.Panic();
            panicHotkeyWasPressed = isPressed;
        }

        UpdateQuickAccessText();
        collarStatusReporter.OnFrameworkUpdate(relayBackgroundWorkCts.Token);
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
        // At most every six hours, with jitter so clients don't all poll at once.
        if (utcNow >= nextRevocationCheckUtc)
        {
            nextRevocationCheckUtc = utcNow.AddHours(6).AddSeconds(Random.Shared.Next(0, 1800));
            FireAndForget(RevocationService.CheckForMissedRevocationAsync(relayBackgroundWorkCts.Token));
        }
        // Skipped while not logged in, so a title-screen session doesn't spend relay requests.
        if (utcNow >= nextPairStatusCheckUtc && ClientState.IsLoggedIn)
        {
            nextPairStatusCheckUtc = utcNow.AddSeconds(RelayProtocolConstants.PairStatusPollIntervalSeconds).AddSeconds(Random.Shared.Next(0, 120));
            FireAndForget(RevocationService.CheckPairStatusAsync(relayBackgroundWorkCts.Token));
        }
        if (ClientState.IsLoggedIn)
        {
            CodePairingService.OnFrameworkUpdate(relayBackgroundWorkCts.Token);
            BackupService.OnFrameworkUpdate(relayBackgroundWorkCts.Token);
            if (BackupService.ShouldShowCodeDialog && !RecoveryCodeWindow.IsOpen)
                RecoveryCodeWindow.IsOpen = true;
        }
        CatalogAutoSync.OnFrameworkUpdate();
        RulebookService.OnFrameworkUpdate();
        UpdateToyStatusDtr();
        Configuration.FlushPendingSave();
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
        // Installs that predate the Welcome window are treated as already welcomed.
        if (Configuration.Version < 4)
        {
            Configuration.HasCompletedWelcome = true;
            Configuration.HasSeenOwnerTutorial = true;
            Configuration.HasSeenSubTutorial = true;
            Configuration.Version = 4;
            changed = true;
        }
        // Move the old single Pairing into Pairings. A never-paired default carries no state and is discarded.
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
            // Entries imported before display labels saved the old pose numbering as Label - relabel unless renamed.
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
        if (changed) Configuration.SaveNow();
    }

    internal static void FireAndForget(Task task) =>
        _ = task.ContinueWith(t =>
        {
            if (t.Exception is { } ex)
                Log.Error(ex, "Unhandled error in a collar command.");
        }, TaskScheduler.Default);
}
