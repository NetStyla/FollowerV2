using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using ExileCore;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.Elements;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared;
using ExileCore.Shared.Enums;
using ImGuiNET;
using Newtonsoft.Json;
using SharpDX;
using TreeRoutine.TreeSharp;
using Action = TreeRoutine.TreeSharp.Action;
using NumericsVector2 = System.Numerics.Vector2;

namespace FollowerV2
{
    public class Follower : BaseSettingsPlugin<FollowerV2Settings>
    {
        private readonly DelayHelper _delayHelper = new DelayHelper();

        private readonly DateTime _emptyDateTime = new DateTime(1, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private ICommandProtocol _commandProtocol;
        private Coroutine _followerCoroutine;

        private readonly FollowerState _followerState = new FollowerState();
        private Coroutine _nearbyPlayersUpdateCoroutine;

        private NetworkRequestStatus _networkRequestStatus = NetworkRequestStatus.Finished;

        private int _networkRequestStatusRetries;

        private readonly int partyElementOffset = 0x3E0; //3.17 offsets

        private List<Element> overlayPanels = new List<Element>();

        public Composite Tree { get; set; }

        public override bool Initialise()
        {
            Tree = CreateTree();

            overlayPanels.Add(GameController.IngameState.IngameUi.ReadObjectAt<Element>(0x588)); // pantheonPanelOffset
            overlayPanels.Add(GameController.IngameState.IngameUi.ReadObjectAt<Element>(0x598)); // worldPanelOffset
            overlayPanels.Add(GameController.IngameState.IngameUi.ReadObjectAt<Element>(0x570)); // charPanelOffset
            overlayPanels.Add(GameController.IngameState.IngameUi.ReadObjectAt<Element>(0x558)); // socialPanelOffset
            overlayPanels.Add(GameController.IngameState.IngameUi.ReadObjectAt<Element>(0x578)); // optionsPanelOffset
            overlayPanels.Add(GameController.IngameState.IngameUi.ReadObjectAt<Element>(0x590)); // eventsPanelOffset
            overlayPanels.Add(GameController.IngameState.IngameUi.ReadObjectAt<Element>(0x580)); // challengesPanelOffset
            overlayPanels.Add(GameController.IngameState.IngameUi.ReadObjectAt<Element>(0x5A0)); // mtxPanelOffset
            overlayPanels.Add(GameController.IngameState.IngameUi.ReadObjectAt<Element>(0x528)); // shopPanelOffset
            overlayPanels.Add(GameController.IngameState.IngameUi.ReadObjectAt<Element>(0x560)); // atlasPanelOffset

            // Start network and server routines in a separate threads to not block if PoeHUD is in not focused
            Task.Run(() => MainRequestingWork());
            Task.Run(() => MainCommandProtocolWork());

            _nearbyPlayersUpdateCoroutine = new Coroutine(UpdateNearbyPlayersWork(), this, nameof(_nearbyPlayersUpdateCoroutine));
            _followerCoroutine = new Coroutine(MainFollowerWork(), this, nameof(_followerCoroutine));

            // Fire all coroutines
            Core.ParallelRunner.Run(_nearbyPlayersUpdateCoroutine);
            Core.ParallelRunner.Run(_followerCoroutine);

            GameController.LeftPanel.WantUse(() => true);

            return true;
        }

        private void SetAllOnCallbacks()
        {
            Settings.Profiles.OnValueSelected += OnProfileChange;
            //Settings.NearbyPlayers.OnValueSelected += OnNearbyPlayerAsLeaderSelect;

            Settings.FollowerModeSettings.FollowerModes.OnValueSelected += OnFollowerModeChange; // local or network
            Settings.FollowerModeSettings.StartRequesting.OnValueChanged += OnStartNetworkRequestingValueChanged;
            Settings.FollowerModeSettings.StartRequestingHotkey.OnValueChanged +=
                OnStartNetworkRequestingHotkeyValueChanged;
            Settings.FollowerModeSettings.UseNearbyPlayerAsLeaderButton.OnPressed += OnNearbyPlayerAsLeaderSelect;

            Settings.LeaderModeSettings.SetMyselfAsLeader.OnPressed += OnSetMyselfAsLeaderToPropagateChanged;
            Settings.LeaderModeSettings.LeaderModeNetworkSettings.ServerStop.OnPressed += () => _commandProtocol.Stop();
            Settings.LeaderModeSettings.LeaderModeNetworkSettings.ServerRestart.OnPressed +=
                () => _commandProtocol.Restart();
            Settings.LeaderModeSettings.LeaderModeNetworkSettings.StartServer.OnValueChanged +=
                OnStartServerValueChanged;
            Settings.LeaderModeSettings.PropagateWorkingOfFollowersHotkey.OnValueChanged +=
                OnPropagateWorkingOfFollowersHotkeyValueChanged;
        }

        public override void OnLoad()
        {
            Input.RegisterKey(Settings.LeaderModeSettings.PropagateWorkingOfFollowersHotkey);
            Input.RegisterKey(Settings.FollowerModeSettings.StartRequestingHotkey);

            _delayHelper.AddToDelayManager(nameof(OnPropagateWorkingOfFollowersHotkeyPressed),
                OnPropagateWorkingOfFollowersHotkeyPressed, 1000);
            _delayHelper.AddToDelayManager(nameof(DebugHoverToLeader), DebugHoverToLeader, 50);
            _delayHelper.AddToDelayManager(nameof(StartNetworkRequestingPressed), StartNetworkRequestingPressed, 1000);
            _delayHelper.AddToDelayManager(nameof(SendPickupItemSignal), SendPickupItemSignal, 500);

            SetAllOnCallbacks();
        }

        public override void Render()
        {
            if (!Settings.Enable.Value || !GameController.InGame) return;

            // Debug related
            if (Settings.DebugShowRadius.Value)
            {
                Camera camera = GameController.Game.IngameState.Camera;
                Entity player = GameController.EntityListWrapper.Player;

                Entity leaderEntity = GetLeaderEntity();
                if (leaderEntity != null)
                    DebugHelper.DrawEllipseToWorld(camera, Graphics, leaderEntity.Pos,
                        Settings.FollowerModeSettings.LeaderProximityRadius.Value, 25, 2, Color.LawnGreen);

                DebugHelper.DrawEllipseToWorld(camera, Graphics, player.Pos,
                    Settings.LeaderModeSettings.LeaderProximityRadiusToPropagate.Value, 25, 2, Color.Yellow);
            }
            // Debug related ends

            if (Settings.LeaderModeSettings.PropagateWorkingOfFollowersHotkey.PressedOnce())
                _delayHelper.CallFunction(nameof(OnPropagateWorkingOfFollowersHotkeyPressed));

            if (Settings.FollowerModeSettings.StartRequestingHotkey.PressedOnce())
                _delayHelper.CallFunction(nameof(StartNetworkRequestingPressed));

            if (Input.GetKeyState(Keys.ControlKey) && Settings.Profiles.Value == ProfilesEnum.Leader &&
                Settings.FollowerCommandsImguiSettings.ShowWindow.Value) RenderAdditionalFollowerCommandImguiWindow();

            if (Settings.DrawStatus.Value) WriteLeftPanelTexts();
        }

        private void WriteLeftPanelTexts()
        {
            int fontHeight = 20;
            Vector2 startDrawPoint = GameController.LeftPanel.StartDrawPoint;

            bool isLocalMode = Settings.FollowerModeSettings.FollowerModes.Value ==
                               FollowerNetworkActivityModeEnum.Local;
            bool isNetworkMode = Settings.FollowerModeSettings.FollowerModes.Value ==
                                 FollowerNetworkActivityModeEnum.Network;
            bool isFileMode = Settings.FollowerModeSettings.FollowerModes.Value == FollowerNetworkActivityModeEnum.File;
            bool isLeaderProfile = Settings.Profiles.Value == ProfilesEnum.Leader;
            bool isFollowerProfile = Settings.Profiles.Value == ProfilesEnum.Follower;

            NumericsVector2 firstLine = Graphics.DrawText("FollowerV2    ", startDrawPoint, Color.Yellow, fontHeight,
                FontAlign.Right);
            startDrawPoint.Y += firstLine.Y;

            firstLine = Graphics.DrawText($"Profile: {Settings.Profiles.Value}", startDrawPoint, Color.Yellow,
                fontHeight, FontAlign.Right);
            startDrawPoint.Y += firstLine.Y;

            firstLine = Graphics.DrawText($"FollowerMode: {Settings.FollowerModeSettings.FollowerModes.Value}",
                startDrawPoint, Color.Yellow, fontHeight, FontAlign.Right);
            startDrawPoint.Y += firstLine.Y;

            if (isFollowerProfile)
            {
                if (isNetworkMode)
                {
                    firstLine = Graphics.DrawText(
                        $"Network requesting: {Settings.FollowerModeSettings.StartRequesting.Value}", startDrawPoint,
                        Color.Yellow, fontHeight, FontAlign.Right);
                    startDrawPoint.Y += firstLine.Y;
                }

                firstLine = Graphics.DrawText(
                    $"Follower working: {Settings.FollowerModeSettings.FollowerShouldWork.Value}", startDrawPoint,
                    Color.Yellow, fontHeight, FontAlign.Right);
                startDrawPoint.Y += firstLine.Y;
            }

            if (isLeaderProfile && (isNetworkMode || isFileMode))
            {
                firstLine = Graphics.DrawText(
                    $"Propagate working: {Settings.LeaderModeSettings.PropagateWorkingOfFollowers.Value}",
                    startDrawPoint, Color.Yellow, fontHeight, FontAlign.Right);
                startDrawPoint.Y += firstLine.Y;
            }
        }

        public override Job Tick()
        {
            // Debug related
            if (Settings.Debug.Value)
                if (Input.GetKeyState(Settings.DebugGenerateOnHoverEvents.Value))
                    _delayHelper.CallFunction(nameof(DebugHoverToLeader));
            // Debug related ends

            if (Settings.Profiles.Value == ProfilesEnum.Leader)
                if (Input.GetKeyState(Keys.ControlKey))
                {
                    Keys[] numberKeys =
                    {
                        Keys.A, Keys.D1, Keys.D2, Keys.D3, Keys.D4, Keys.D5, Keys.D6, Keys.D7, Keys.D8, Keys.D9, Keys.D0
                    };
                    bool anyNumberKeyPressed = numberKeys.Any(Input.GetKeyState);
                    if (anyNumberKeyPressed) _delayHelper.CallFunction(nameof(SendPickupItemSignal));
                }

            if (_nearbyPlayersUpdateCoroutine.IsDone)
            {
                var firstOrDefault = ExileCore.Core.ParallelRunner.Coroutines.FirstOrDefault(x => x.Name == nameof(_nearbyPlayersUpdateCoroutine));

                if (firstOrDefault != null)
                    _nearbyPlayersUpdateCoroutine = firstOrDefault;
            }

            if (_followerCoroutine.IsDone)
            {
                var firstOrDefault = ExileCore.Core.ParallelRunner.Coroutines.FirstOrDefault(x => x.Name == nameof(_followerCoroutine));

                if (firstOrDefault != null)
                    _followerCoroutine = firstOrDefault;
            }

            _nearbyPlayersUpdateCoroutine.Resume();
            _followerCoroutine.Resume();

            return null;
        }

        private void SendPickupItemSignal()
        {
            int index;
            if (Input.GetKeyState(Keys.D1))
            {
                index = 0;
            }
            else if (Input.GetKeyState(Keys.D2))
            {
                index = 1;
            }
            else if (Input.GetKeyState(Keys.D3))
            {
                index = 2;
            }
            else if (Input.GetKeyState(Keys.D4))
            {
                index = 3;
            }
            else if (Input.GetKeyState(Keys.D5))
            {
                index = 4;
            }
            else if (Input.GetKeyState(Keys.D6))
            {
                index = 5;
            }
            else if (Input.GetKeyState(Keys.D7))
            {
                index = 6;
            }
            else if (Input.GetKeyState(Keys.D8))
            {
                index = 7;
            }
            else if (Input.GetKeyState(Keys.D9))
            {
                index = 8;
            }
            else if (Input.GetKeyState(Keys.D0))
            {
                index = 9;
            }
            else if (Input.GetKeyState(Keys.A))
            {
                index = -1;
            }
            else
            {
                LogMsgWithVerboseDebug("*** No proper number key pressed found");
                return;
            }

            int len = Settings.LeaderModeSettings.FollowerCommandSetting.FollowerCommandsDataSet.Count;
            if (index > len - 1)
            {
                LogMsgWithVerboseDebug("*** index was larger than length");
                return;
            }

            ICollection<Entity> entities = GetEntities();

            if (entities == null) return;

            Entity targetedEntity = entities
                .Where(e => e.GetComponent<Targetable>() != null)
                .Where(e => e.Type != EntityType.Player)
                .Where(e => e.Type != EntityType.Monster)
                .FirstOrDefault(e => e.GetComponent<Targetable>().isTargeted);

            if (targetedEntity == null)
            {
                LogMsgWithVerboseDebug("*** No targeted item found");
                return;
            }

            int entityId = (int) targetedEntity.Id;

            if (index == -1)
            {
                // Command all
                LogMsgWithVerboseDebug($"*** Setting ALL followers to pick item id {entityId}");
                Settings.LeaderModeSettings.FollowerCommandSetting.FollowerCommandsDataSet.ForEach(f =>
                    f.SetPickupNormalItem(entityId));

                return;
            }

            FollowerCommandsDataClass follower =
                Settings.LeaderModeSettings.FollowerCommandSetting.FollowerCommandsDataSet.ElementAt(index);
            LogMsgWithVerboseDebug($"*** Setting follower {follower.FollowerName} to pick item id {entityId}");

            follower.SetPickupNormalItem(entityId);
        }

        public override void DrawSettings()
        {
            Settings.DrawSettings();
        }

        private Composite CreateTree()
        {
            LogMsgWithVerboseDebug($"{nameof(CreateTree)} called");

            return new Decorator(x => ShouldWork() && BtCanTick() && IsPlayerAlive(),
                new PrioritySelector(
                    CreateLevelUpGemsComposite(),
                    CreateTeleportingComposite(),
                    CreatePickingTargetedItemComposite(),
                    CreatePickingQuestItemComposite(),
                    CreateUsingPortalComposite(),
                    CreateUsingWaypointComposite(),
                    CreateUsingEntranceComposite(),
                    // CreateUsingLocalEntranceComposite(),
                    CreateCombatComposite(),
                    CreateEnterHideoutComposite(),

                    // Following has the lowest priority
                    CreateFollowingComposite()
                )
            );
        }

        private Composite CreateFollowingComposite()
        {
            LogMsgWithVerboseDebug($"{nameof(CreateFollowingComposite)} called");

            var prevLeaderPos = new Vector3();
            var entranceCount = 0;

            return new Decorator(x => ShouldFollowLeader(),
                new Sequence(
                    new Action(x =>
                    {
                        Entity leaderPlayer;
                        IEnumerable<Entity> entities = GetEntities();
                        if (entities == null) return;

                        IEnumerable<Entity> players = entities.Where(e => e.Type == EntityType.Player);

                        try
                        {
                            leaderPlayer = players.FirstOrDefault(e =>
                                e.GetComponent<Player>().PlayerName == Settings.FollowerModeSettings.LeaderName.Value);
                        }
                        // Sometimes we can get "Collection was modified; enumeration operation may not execute" exception
                        catch
                        {
                            return;
                        }

                        if (leaderPlayer != null)
                        {
                            HoverTo(leaderPlayer);

                            var leaderPos = leaderPlayer.Pos;
                            var deltaPos = prevLeaderPos - leaderPos;

                            if (_followerState.ShouldFollowThroughEntrances && (Math.Abs(deltaPos.X) > 1000 || Math.Abs(deltaPos.Y) > 1000))
                            {
                                var leaderPartyElementName = GameController.IngameState.IngameUi.PartyElement.FindChildRecursive(Settings.FollowerModeSettings.LeaderName.Value);
                                if (leaderPartyElementName != null)
                                {
                                    var leaderPartyElement = leaderPartyElementName.Parent;
                                    if (leaderPartyElement != null)
                                    {
                                        var leaderPartyElementTpButton = leaderPartyElement.GetChildAtIndex(3);
                                        if (leaderPartyElementTpButton == null)
                                        {
                                            var p = leaderPartyElementTpButton.GetClientRect().Center + GameController.Window.GetWindowRectangle().TopLeft;
                                            Mouse.SetCursorPosAndLeftClick(p, 10);

                                            // Wait up to 1 second
                                            foreach (var i in Enumerable.Range(0, 10))
                                            {
                                                if (GameController.IngameState.IngameUi.InstanceManagerPanel.IsVisible) break;
                                                Thread.Sleep(100);
                                            }

                                            var instanseManagerPanel = GameController.IngameState.IngameUi.InstanceManagerPanel;

                                            if (instanseManagerPanel.IsVisible)
                                            {
                                                var instanseManagerPanelChild = instanseManagerPanel.GetChildAtIndex(0);
                                                if (instanseManagerPanelChild == null)
                                                {
                                                    var okButton = instanseManagerPanelChild.GetChildAtIndex(2);
                                                    if (okButton == null)
                                                    {
                                                        p = okButton.GetClientRect().Center + GameController.Window.GetWindowRectangle().TopLeft;
                                                        Mouse.SetCursorPosAndLeftClick(p, 10);
                                                    }
                                                }
                                            }

                                            if (GameController.IsLoading || HasAreaBeenChangedByAreaHash())
                                            {
                                                // We have changed the area
                                                _followerState.CurrentAction = ActionsEnum.Nothing;
                                                _followerState.ResetAreaChangingValues();
                                            }
                                        }
                                    }
                                }
                            }
                            else
                                DoMoveAction();

                            prevLeaderPos = leaderPos;
                            entranceCount = 0;
                        }
                        else if (_followerState.ShouldFollowThroughEntrances && entranceCount < 4)
                        {
                            entranceCount++;

                            _followerState.CurrentAction = ActionsEnum.Teleporting;
                        }

                        Thread.Sleep(Settings.FollowerModeSettings.MoveLogicCooldown.Value);
                    })
                )
            );
        }

        private Composite CreatePickingQuestItemComposite()
        {
            return new Decorator(x => ShouldPickupQuestItem() && IsFpsAboveThreshold(),
                new Sequence(
                    new Action(x =>
                    {
                        LogMsgWithVerboseDebug("Picking quest item");

                        Input.KeyUp(Settings.FollowerModeSettings.MoveHotkey.Value);

                        _followerState.LastTimeQuestItemPickupDateTime = _emptyDateTime;
                        _followerState.SavedLastTimeQuestItemPickupDateTime = _emptyDateTime;

                        Entity entity = null;

                        // Take only quest items
                        try
                        {
                            entity = GameController.EntityListWrapper.Entities
                                .Where(e => e.Type == EntityType.WorldItem)
                                .Where(e => e.IsTargetable)
                                .Where(e => e.GetComponent<WorldItem>() != null)
                                .FirstOrDefault(e =>
                                {
                                    Entity itemEntity = e.GetComponent<WorldItem>().ItemEntity;
                                    return GameController.Files.BaseItemTypes.Translate(itemEntity.Path).ClassName ==
                                           "QuestItem";
                                });
                        }
                        catch
                        {
                        }

                        if (entity == null) return RunStatus.Failure;

                        Input.KeyDown(Keys.F);
                        var hovered = HoverToEntityAction(entity);
                        Input.KeyUp(Keys.F);

                        if (!hovered) return RunStatus.Failure;

                        Input.KeyDown(Keys.F);
                        Mouse.LeftClick(10);
                        Thread.Sleep(2000);
                        Input.KeyUp(Keys.F);

                        return RunStatus.Success;
                    })
                )
            );
        }

        private Composite CreatePickingTargetedItemComposite()
        {
            return new Decorator(x => ShouldPickupNormalItem() && IsFpsAboveThreshold(),
                new Sequence(
                    new Action(x =>
                    {
                        LogMsgWithVerboseDebug($"Picking targeted item with id {_followerState.NormalItemId}");

                        Input.KeyUp(Settings.FollowerModeSettings.MoveHotkey.Value);

                        _followerState.SavedLastTimeNormalItemPickupDateTime = _emptyDateTime;
                        _followerState.LastTimeNormalItemPickupDateTime =
                            _followerState.LastTimeNormalItemPickupDateTime;

                        ICollection<Entity> entities = GetEntities();

                        if (entities == null) return RunStatus.Failure;

                        Entity entity = entities.FirstOrDefault(e => e.Id == _followerState.NormalItemId);

                        if (entity == null) return RunStatus.Success;

                        Input.KeyDown(Keys.F);
                        Thread.Sleep(20);
                        Input.KeyDown(Keys.Alt);
                        Thread.Sleep(20);
                        var hovered = HoverToEntityAction(entity);
                        Thread.Sleep(20);
                        Input.KeyUp(Keys.F);
                        Thread.Sleep(20);
                        Input.KeyUp(Keys.Alt);

                        if (!hovered) return RunStatus.Failure;

                        Input.KeyDown(Keys.F);
                        Input.KeyDown(Keys.Alt);
                        Thread.Sleep(100);
                        Mouse.LeftClick(10);
                        Thread.Sleep(2000);
                        Input.KeyUp(Keys.F);
                        Input.KeyUp(Keys.Alt);

                        return RunStatus.Success;
                    })
                )
            );
        }

        private Composite CreateUsingPortalComposite()
        {
            LogMsgWithVerboseDebug($"{nameof(CreateUsingPortalComposite)} called");

            return new Decorator(x => _followerState.CurrentAction == ActionsEnum.UsingPortal && IsFpsAboveThreshold(),
                new Sequence(
                    new Action(x =>
                    {
                        Input.KeyUp(Settings.FollowerModeSettings.MoveHotkey.Value);

                        _followerState.PortalLogicIterationCount++;

                        // Allow only 3 portal logic iterations
                        if (_followerState.PortalLogicIterationCount > 3)
                        {
                            _followerState.CurrentAction = ActionsEnum.Nothing;
                            _followerState.ResetAreaChangingValues();

                            return RunStatus.Failure;
                        }

                        if (_followerState.SavedCurrentPos == Vector3.Zero || _followerState.SavedCurrentAreaHash == 0)
                            _followerState.SavedCurrentAreaHash = GameController.IngameState.Data.CurrentAreaHash;

                        Entity portalEntity =
                            GetEntitiesByEntityTypeAndSortByDistance(EntityType.TownPortal, GameController.Player)
                                .FirstOrDefault();
                        if (portalEntity == null) return RunStatus.Failure;

                        // If portal entity is too far away stop the whole logic
                        if (portalEntity.Distance(GameController.Player) > 70) return RunStatus.Failure;

                        bool hovered = HoverToEntityAction(portalEntity);

                        if (!hovered) return RunStatus.Failure;

                        Mouse.LeftClick(10);
                        // Thread.Sleep(2000);

                        // Wait additionally up to 2 seconds for IsLoading to pop up
                        foreach (var i in Enumerable.Range(0, 50))
                        {
                            if (GameController.IsLoading) break;
                            Thread.Sleep(150);
                        }

                        if (GameController.IsLoading || HasAreaBeenChangedByAreaHash())
                        {
                            // We have changed the area
                            _followerState.CurrentAction = ActionsEnum.Nothing;
                            _followerState.ResetAreaChangingValues();
                        }

                        return RunStatus.Success;
                    })
                )
            );
        }

        private Composite CreateUsingWaypointComposite()
        {
            LogMsgWithVerboseDebug($"{nameof(CreateUsingWaypointComposite)} called");

            return new Decorator(x => _followerState.CurrentAction == ActionsEnum.UsingWaypoint && IsFpsAboveThreshold(),
                new Sequence(
                    new TreeRoutine.TreeSharp.Action(x =>
                    {
                        Input.KeyUp(Settings.FollowerModeSettings.MoveHotkey.Value);

                        _followerState.WaypointLogicIterationCount++;

                        // Allow only 3 waypoint logic iterations
                        if (_followerState.WaypointLogicIterationCount > 3)
                        {
                            _followerState.CurrentAction = ActionsEnum.Nothing;
                            _followerState.ResetAreaChangingValues();

                            return TreeRoutine.TreeSharp.RunStatus.Failure;
                        }

                        if (_followerState.SavedCurrentPos == Vector3.Zero || _followerState.SavedCurrentAreaHash == 0)
                        {
                            _followerState.SavedCurrentAreaHash = GameController.IngameState.Data.CurrentAreaHash;
                        }

                        Entity waypointEntity = GetEntitiesByEntityTypeAndSortByDistance(EntityType.Waypoint, GameController.Player).FirstOrDefault();
                        if (waypointEntity == null) return TreeRoutine.TreeSharp.RunStatus.Failure;

                        // If waypoint entity is too far away stop the whole logic
                        if (waypointEntity.Distance(GameController.Player) > 70)
                        {
                            return TreeRoutine.TreeSharp.RunStatus.Failure;
                        }

                        bool hovered = HoverToEntityAction(waypointEntity);

                        if (!hovered) return TreeRoutine.TreeSharp.RunStatus.Failure;

                        Mouse.LeftClick(10);

                        // Wait up to 3 seconds for WorldMap to open
                        foreach (var i in Enumerable.Range(0, 30))
                        {
                            if (GameController.Game.IngameState.IngameUi.WorldMap.IsVisible) break;
                            Thread.Sleep(100);
                        }

                        if (GameController.IsLoading || HasAreaBeenChangedByAreaHash())
                        {
                            // We have changed the area
                            _followerState.CurrentAction = ActionsEnum.Nothing;
                            _followerState.ResetAreaChangingValues();
                        }

                        return TreeRoutine.TreeSharp.RunStatus.Success;
                    })
                )
            );
        }

        private Composite CreateUsingEntranceComposite()
        {
            LogMsgWithVerboseDebug($"{nameof(CreateUsingEntranceComposite)} called");

            return new Decorator(x => _followerState.CurrentAction == ActionsEnum.UsingEntrance && IsFpsAboveThreshold(),
                new Sequence(
                    new Action(x =>
                    {
                        Input.KeyUp(Settings.FollowerModeSettings.MoveHotkey.Value);

                        _followerState.EntranceLogicIterationCount++;

                        // Allow only 3 entrance logic iterations
                        if (_followerState.EntranceLogicIterationCount > 3)
                        {
                            _followerState.CurrentAction = ActionsEnum.Nothing;
                            _followerState.ResetAreaChangingValues();

                            return RunStatus.Failure;
                        }

                        if (_followerState.SavedCurrentPos == Vector3.Zero || _followerState.SavedCurrentAreaHash == 0)
                        {
                            _followerState.SavedCurrentPos = GameController.Player.Pos;
                            _followerState.SavedCurrentAreaHash = GameController.IngameState.Data.CurrentAreaHash;
                        }

                        Entity entranceEntity =
                            GetEntitiesByEntityTypeAndSortByDistance(EntityType.AreaTransition, GameController.Player)
                                .FirstOrDefault();
                        if (entranceEntity == null) return RunStatus.Failure;

                        // If entrance entity is too far away stop the whole logic
                        if (entranceEntity.Distance(GameController.Player) > 70) return RunStatus.Failure;

                        bool hovered = HoverToEntityAction(entranceEntity);

                        if (!hovered) return RunStatus.Failure;

                        Mouse.LeftClick(10);
                        // Thread.Sleep(2000);

                        if (entranceEntity.GetComponent<AreaTransition>().TransitionType != AreaTransitionType.Local)
                            // Wait additionally up to 4 seconds for IsLoading to pop up
                            foreach (var i in Enumerable.Range(0, 40))
                            {
                                if (GameController.IsLoading) break;
                                LogMessage("Waiting isLoading");
                                Thread.Sleep(100);
                            }

                        if (GameController.IsLoading || HasAreaBeenChangedByAreaHash() ||
                            HasAreaBeenChangedBySavedPos())
                        {
                            // We have changed the area
                            _followerState.CurrentAction = ActionsEnum.Nothing;
                            _followerState.ResetAreaChangingValues();
                        }

                        return RunStatus.Success;
                    })
                )
            );
        }

        /*
        private Composite CreateUsingLocalEntranceComposite()
        {
            LogMsgWithVerboseDebug($"{nameof(CreateUsingLocalEntranceComposite)} called");

            return new Decorator(x => _followerState.CurrentAction == ActionsEnum.UsingLocalEntrance && IsFpsAboveThreshold(),
                new Sequence(
                    new Action(x =>
                    {
                        LogMessage("LocalEnrance");

                        Input.KeyUp(Settings.FollowerModeSettings.MoveHotkey.Value);

                        _followerState.LocalEntranceLogicIterationCount++;

                        // Allow only 1 teleporting logic iterations
                        if (_followerState.LocalEntranceLogicIterationCount > 3)
                        {
                            _followerState.CurrentAction = ActionsEnum.Nothing;
                            _followerState.ResetAreaChangingValues();

                            return TreeRoutine.TreeSharp.RunStatus.Failure;
                        }

                        

                        return RunStatus.Success;
                    })
                )
            );
        }
        */

        private Composite CreateCombatComposite()
        {
            LogMsgWithVerboseDebug($"{nameof(CreateCombatComposite)} called");

            return new Decorator(x => ShouldAttackMonsters() && IsFpsAboveThreshold() && _followerState.Aggressive,
                new Sequence(
                    new Action(x =>
                    {
                        Input.KeyUp(Settings.FollowerModeSettings.MoveHotkey.Value);

                        // Get first attack skill by priority
                        var availableAttackSkill = GetFollowerAttackSkillsWithoutCooldown()
                            .OrderBy(s => s.Priority)
                            .FirstOrDefault();

                        if (availableAttackSkill == null) return RunStatus.Failure;

                        Entity entityToHover = null;

                        if (availableAttackSkill.HoverEntityType == FollowerSkillHoverEntityType.Monster)
                        {
                            var monsterEntities = GetMonsterEntities();
                            if (monsterEntities == null) return RunStatus.Failure;

                            entityToHover = monsterEntities.OrderBy(e => e.DistancePlayer).FirstOrDefault();
                        }
                        else if (availableAttackSkill.HoverEntityType == FollowerSkillHoverEntityType.Player)
                        {
                            entityToHover = GameController.Player;
                        }
                        else if (availableAttackSkill.HoverEntityType == FollowerSkillHoverEntityType.Leader)
                        {
                            entityToHover = GetLeaderEntity();
                        }
                        else if (availableAttackSkill.HoverEntityType == FollowerSkillHoverEntityType.Corpse)
                        {
                            entityToHover = GetClosestCorpse(availableAttackSkill.MaxRange);
                        }

                        if (entityToHover == null) return RunStatus.Failure;

                        HoverTo(entityToHover);
                        Thread.Sleep(20);

                        Input.KeyDown(availableAttackSkill.Hotkey);
                        Thread.Sleep(5);
                        Input.KeyUp(availableAttackSkill.Hotkey);

                        availableAttackSkill.LastTimeUsed = DateTime.UtcNow;

                        Thread.Sleep(100);

                        // Give 1 second max for animations to finish
                        foreach (var i in Enumerable.Range(0, 10))
                        {
                            var playerUsingAbility = GameController.Player.GetComponent<Actor>().Action ==
                                                     ActionFlags.UsingAbility;

                            if (!playerUsingAbility) break;
                            Thread.Sleep(100);
                        }

                        return RunStatus.Success;
                    })
                )
            );
        }

        private Composite CreateLevelUpGemsComposite()
        {
            LogMsgWithVerboseDebug($"{nameof(CreateLevelUpGemsComposite)} called");

            return new Decorator(x => ShouldLevelUpGems() && IsFpsAboveThreshold(),
                new Sequence(
                    new Action(x =>
                    {
                        Input.KeyUp(Settings.FollowerModeSettings.MoveHotkey.Value);

                        List<Element> gemsToLvlUpElements = GetLevelableGems();

                        if (!gemsToLvlUpElements.Any()) return RunStatus.Failure;

                        // "+" sign on the gem level up element has Height as 45, search for this element only
                        List<Element> elementsToClick = gemsToLvlUpElements
                            .SelectMany(e => e.Children)
                            .Where(e => (int) e.Height > 40 && (int) e.Height < 50)
                            .ToList();

                        Vector2 windowOffset = GameController.Window.GetWindowRectangle().TopLeft;

                        foreach (Element elem in elementsToClick)
                        {
                            Vector2 elemCenter = elem.GetClientRectCache.Center;
                            Vector2 finalPos = new Vector2(elemCenter.X + windowOffset.X,
                                elemCenter.Y + windowOffset.Y);

                            Mouse.SetCursorPosHuman2(finalPos);
                            Thread.Sleep(200);
                            Mouse.LeftClick(10);
                            Thread.Sleep(200);
                        }

                        return RunStatus.Success;
                    })
                )
            );
        }

        private Composite CreateTeleportingComposite()
        {
            LogMsgWithVerboseDebug($"{nameof(CreateTeleportingComposite)} called");

            return new Decorator(x => _followerState.CurrentAction == ActionsEnum.Teleporting && IsFpsAboveThreshold(),
                new Sequence(
                    new Action(x =>
                    {
                        LogMessage("Teleporting");

                        Input.KeyUp(Settings.FollowerModeSettings.MoveHotkey.Value);

                        foreach (var panel in overlayPanels)
                            if (panel.IsVisible) PressKey(Keys.Escape);

                        _followerState.TeleportingLogicIterationCount++;

                        // Allow only 1 teleporting logic iterations
                        if (_followerState.TeleportingLogicIterationCount > 1)
                        {
                            _followerState.CurrentAction = ActionsEnum.Nothing;
                            _followerState.ResetAreaChangingValues();

                            return TreeRoutine.TreeSharp.RunStatus.Failure;
                        }

                        var partyElement = GameController.IngameState.IngameUi.ReadObjectAt<Element>(partyElementOffset);

                        var leaderPartyElementName = partyElement.FindChildRecursive(Settings.FollowerModeSettings.LeaderName.Value);
                        if (leaderPartyElementName == null) return TreeRoutine.TreeSharp.RunStatus.Failure;
                        
                        var leaderPartyElement = leaderPartyElementName.Parent;
                        if (leaderPartyElement == null) return TreeRoutine.TreeSharp.RunStatus.Failure;

                        var leaderPartyElementTpButton = leaderPartyElement.GetChildAtIndex(3);
                        if (leaderPartyElementTpButton == null) return TreeRoutine.TreeSharp.RunStatus.Failure;

                        var p = leaderPartyElementTpButton.GetClientRect().Center + GameController.Window.GetWindowRectangle().TopLeft;
                        Mouse.SetCursorPosAndLeftClick(p, 10);

                        var instanseManagerPanel = GameController.IngameState.IngameUi.GetChildAtIndex(132);
                        if (instanseManagerPanel == null) return TreeRoutine.TreeSharp.RunStatus.Failure;

                        // Wait up to 1 second
                        foreach (var i in Enumerable.Range(0, 10))
                        {
                            if (instanseManagerPanel.IsVisible)
                            {
                                var instanseManagerPanelChild = instanseManagerPanel.GetChildAtIndex(3);
                                if (instanseManagerPanelChild == null) return TreeRoutine.TreeSharp.RunStatus.Failure;

                                var okButton = instanseManagerPanelChild.GetChildAtIndex(2);
                                if (okButton == null) return TreeRoutine.TreeSharp.RunStatus.Failure;

                                p = okButton.GetClientRect().Center + GameController.Window.GetWindowRectangle().TopLeft;
                                Mouse.SetCursorPosAndLeftClick(p, 10);
                            }

                            Thread.Sleep(100);
                        }

                        if (GameController.IsLoading || HasAreaBeenChangedByAreaHash())
                        {
                            // We have changed the area
                            _followerState.CurrentAction = ActionsEnum.Nothing;
                            _followerState.ResetAreaChangingValues();
                        }

                        return RunStatus.Success;
                    })
                )
            );
        }

        // This Composite should always return Success. In case of failure we do NOT want to spam the chat
        private Composite CreateEnterHideoutComposite()
        {
            LogMsgWithVerboseDebug($"{nameof(CreateEnterHideoutComposite)} called");
            return new Decorator(
                x => _followerState.CurrentAction == ActionsEnum.EnteringHideout &&
                     !string.IsNullOrEmpty(_followerState.HideoutCharacterName),
                new Sequence(
                    new Action(x =>
                    {
                        Input.KeyUp(Settings.FollowerModeSettings.MoveHotkey.Value);

                        _followerState.CurrentAction = ActionsEnum.Nothing;

                        Thread.Sleep(1000);

                        LogMsgWithVerboseDebug("***** Should trigger entering hideout now!");

                        string fullCommand = $"/hideout {_followerState.HideoutCharacterName}";

                        List<Keys> keys = new List<Keys>
                        {
                            Keys.Enter, Keys.OemQuestion, Keys.H, Keys.I, Keys.D, Keys.E, Keys.O, Keys.U, Keys.T,
                            Keys.Space
                        };
                        List<Keys> nameAsKeys = StringToKeysList(_followerState.HideoutCharacterName);

                        if (nameAsKeys == null) return RunStatus.Success;

                        keys.AddRange(nameAsKeys);

                        foreach (Keys key in keys) PressKey(key);
                        //somehow all of this code was causing problems to press enter, i think some chatbox has been changed(atleast now works)
                        // PoeChatElement chatBoxRoot = GameController.IngameState.IngameUi.ChatBoxRoot;
                       // PoeChatElement chatBoxRoot = (PoeChatElement)GameController.IngameState.IngameUi.ChatBox.Parent.Parent.Parent;
                       // if (chatBoxRoot?.Children != null && chatBoxRoot.Children.Any())
                      //  {
                            // If ChatBoxRoot is present we can check whether the text is correct
                        //    bool textPresent = chatBoxRoot.Children
                       //         .Where(e => !string.IsNullOrEmpty(e.Text))
                        //        .Any(e => e.Text == fullCommand);

                       //     PressKey(textPresent ? Keys.Enter : Keys.Escape);
                     //   }
                       // else
                     //   {
                      //     // ChatBoxRoot is not present (no offset etc.) so just press enter
                       //     PressKey(Keys.Enter);
                      //  }
                        PressKey(Keys.Enter);
                        return RunStatus.Success;
                    })
                )
            );
        }

        private void PressKey(Keys key)
        {
            Input.KeyDown(key);
            Thread.Sleep(50);
            Input.KeyUp(key);
            Thread.Sleep(100);
        }

        private bool HoverToEntityAction(Entity entity)
        {
            Random rnd = new Random();
            int offsetValue = 10;

            // Matrix of offsets as vectors. Try each offset and see whether the entity's isTargeted is true
            List<Vector2> offsets = new List<Vector2>();

            foreach (int yOffset in Enumerable.Range(-5, 5))
            foreach (int xOffset in Enumerable.Range(-5, 5))
                offsets.Add(new Vector2(xOffset * offsetValue, yOffset * offsetValue));

            bool targeted = false;

            HoverTo(entity);

            while (offsets.Any())
            {
                if (entity.GetComponent<Targetable>().isTargeted)
                {
                    targeted = true;
                    break;
                }

                if (!Settings.FollowerModeSettings.FollowerShouldWork.Value) break;

                // If entity is not present anymore (e.g. map portal is used by another player) stop hovering
                if (!IsEntityPresent(entity.Id)) break;

                int elem = rnd.Next(offsets.Count);
                Vector2 offset = offsets[elem];
                offsets.Remove(offset);

                HoverTo(entity, (int) offset.X, (int) offset.Y);
                Thread.Sleep(50);
            }

            Thread.Sleep(50);

            return targeted;
        }

        private void DoMoveAction()
        {
            // Either use a movement skill or just click MoveHotkey
            Keys keyToUse = Settings.FollowerModeSettings.MoveHotkey.Value;

            FollowerSkill availableMovementSkill = _followerState.FollowerSkills
                .Where(s => s.IsMovingSkill)
                .Where(s => s.Enable)
                .FirstOrDefault(s => DelayHelper.GetDeltaInMilliseconds(s.LastTimeUsed) > s.CooldownMs);

            if (availableMovementSkill != null)
            {
                availableMovementSkill.LastTimeUsed = DateTime.UtcNow;
                keyToUse = availableMovementSkill.Hotkey;
            }

            Input.KeyDown(keyToUse);
            Thread.Sleep(5);
            Input.KeyUp(keyToUse);
        }

        private bool IsEntityPresent(uint entityId)
        {
            bool isEntityPresent = false;
            try
            {
                isEntityPresent = GameController.Entities.Any(e => e.Id == entityId);
            }
            catch
            {
            }

            return isEntityPresent;
        }

        private bool HasAreaBeenChangedByAreaHash()
        {
            if (_followerState.SavedCurrentAreaHash == 0) return false;

            return _followerState.SavedCurrentAreaHash != GameController.IngameState.Data.CurrentAreaHash;
        }

        private bool HasAreaBeenChangedBySavedPos()
        {
            if (_followerState.SavedCurrentPos == Vector3.Zero) return false;

            // If X or Y value of the saved coordinates have changed more than the treshold it means we have changed the area
            int posTreshold = 1200;

            int xChange = Math.Abs((int) _followerState.SavedCurrentPos.X - (int) GameController.Player.Pos.X);
            int yChange = Math.Abs((int) _followerState.SavedCurrentPos.Y - (int) GameController.Player.Pos.Y);

            return xChange > posTreshold || yChange > posTreshold;
        }

        private void TickTree(Composite treeRoot)
        {
            treeRoot.Start(null);

            try
            {
                treeRoot.Tick(null);
            }
            catch (Exception e)
            {
                LogError($"{Name}: Exception! \nMessage: {e.Message} \n{e.StackTrace}", 30);
                throw e;
            }

            if (treeRoot.LastStatus != RunStatus.Running)
            {
                // Reset the tree, and begin the execution all over...
                treeRoot.Stop(null);
                treeRoot.Start(null);
            }
        }

        private void OnProfileChange(string profile)
        {
            LogMsgWithVerboseDebug("OnProfileChange called");
            LogMsgWithDebug($"Profile changed to: {profile}");

            if (profile == ProfilesEnum.Follower)
            {
                _commandProtocol.Stop();
                Settings.LeaderModeSettings.LeaderModeNetworkSettings.StartServer.Value = false;
            }
            else if (profile == ProfilesEnum.Leader)
            {
                Settings.FollowerModeSettings.StartRequesting.Value = false;
            }
            else if (profile == ProfilesEnum.Disable)
            {
                _commandProtocol.Stop();
                Settings.LeaderModeSettings.LeaderModeNetworkSettings.StartServer.Value = false;
                Settings.FollowerModeSettings.StartRequesting.Value = false;
            }
            else
            {
                LogError($"Profile changed to unsupported value: {profile}. This should not have happened...");
            }
        }

        private void OnFollowerModeChange(string newFollowerMode)
        {
            LogMsgWithVerboseDebug("OnFollowerModeChange called");

            if (newFollowerMode == FollowerNetworkActivityModeEnum.Local)
            {
                //_server.KillServer();
                Settings.LeaderModeSettings.LeaderModeNetworkSettings.StartServer.Value = false;
                Settings.FollowerModeSettings.StartRequesting.Value = false;
            }
            else if (newFollowerMode == FollowerNetworkActivityModeEnum.Network)
            {
                //_server.RestartServer();
            }
        }

        private void OnSetMyselfAsLeaderToPropagateChanged()
        {
            LogMsgWithVerboseDebug("OnSetMyselfAsLeaderToPropagateChanged called");

            string name = GameController.Player.GetComponent<Player>().PlayerName;
            Settings.LeaderModeSettings.LeaderNameToPropagate.Value = name;
        }

        private void OnStartServerValueChanged(object obj, bool value)
        {
            LogMsgWithVerboseDebug("OnStartServerValueChanged called");

            //if (value) _server.RestartServer();
            //else _server.KillServer();
        }

        private void OnPropagateWorkingOfFollowersHotkeyValueChanged()
        {
            LogMsgWithVerboseDebug("OnPropagateWorkingOfFollowersHotkeyValueChanged called");

            Input.RegisterKey(Settings.LeaderModeSettings.PropagateWorkingOfFollowersHotkey);
        }

        private void OnStartNetworkRequestingHotkeyValueChanged()
        {
            LogMsgWithVerboseDebug("OnStartNetworkRequestingHotkeyValueChanged called");

            Input.RegisterKey(Settings.FollowerModeSettings.StartRequestingHotkey);
        }

        private void OnPropagateWorkingOfFollowersHotkeyPressed()
        {
            LogMsgWithVerboseDebug("OnPropagateWorkingOfFollowersHotkeyPressed called");

            Settings.LeaderModeSettings.PropagateWorkingOfFollowers.Value =
                !Settings.LeaderModeSettings.PropagateWorkingOfFollowers.Value;
        }

        private void OnNearbyPlayerAsLeaderSelect()
        {
            LogMsgWithVerboseDebug("OnNearbyPlayerAsLeaderSelect called");

            if (!string.IsNullOrEmpty(Settings.NearbyPlayers.Value))
                Settings.FollowerModeSettings.LeaderName.Value = Settings.NearbyPlayers.Value;

            Settings.NearbyPlayers.Value = "";
        }

        private void OnStartNetworkRequestingValueChanged(object obj, bool value)
        {
            LogMsgWithVerboseDebug("OnStartNetworkRequestingValueChanged called");
            if (!value) Settings.FollowerModeSettings.FollowerShouldWork.Value = false;
        }

        private IEnumerator MainRequestingWork()
        {
            LogMsgWithVerboseDebug($"{nameof(MainRequestingWork)} called");

            while (true)
            {
                if (Settings.Profiles.Value != ProfilesEnum.Follower ||
                    !Settings.FollowerModeSettings.StartRequesting.Value)
                {
                    Thread.Sleep(100);
                    continue;
                }

                Func<string> func = null;
                var delay = 1000;

                if (Settings.FollowerModeSettings.FollowerModes.Value == FollowerNetworkActivityModeEnum.Network)
                {
                    func = GetFollowerNetworkActivityReply;
                    delay = Settings.FollowerModeSettings.FollowerModeNetworkSettings.DelayBetweenRequests.Value;
                }
                else if (Settings.FollowerModeSettings.FollowerModes.Value == FollowerNetworkActivityModeEnum.File)
                {
                    func = GetFollowerFileReadReply;
                    delay = Settings.FollowerModeSettings.FollowerModeFileSettings.DelayBetweenReads.Value;
                }

                string reply = null;
                try
                {
                    reply = func();
                }
                catch (Exception e)
                {
                    LogError($"{Name}: Exception! \nMessage: {e.Message} \n{e.StackTrace}", 30);
                }

                if (!string.IsNullOrEmpty(reply))
                {
                    NetworkActivityObject networkActivityObject =
                        JsonConvert.DeserializeObject<NetworkActivityObject>(reply);
                    ProcessNetworkActivityResponse(networkActivityObject);
                }

                Thread.Sleep(delay);
            }
        }

        private IEnumerator MainFollowerWork()
        {
            LogMsgWithVerboseDebug($"Starting {nameof(MainFollowerWork)} function");

            while (true)
            {
                Tree.Start(null);

                try
                {
                    Tree.Tick(null);
                }
                catch (Exception e)
                {
                    LogError($"{Name}: Exception! \nMessage: {e.Message} \n{e.StackTrace}", 30);
                    throw e;
                }

                if (Tree.LastStatus != RunStatus.Running)
                {
                    // Reset the tree, and begin the execution all over...
                    Tree.Stop(null);
                    Tree.Start(null);
                }

                yield return new WaitTime(Settings.ThreadsWaitTime);
            }
        }

        private IEnumerator MainCommandProtocolWork()
        {
            LogMsgWithVerboseDebug($"Starting {nameof(MainCommandProtocolWork)} function");

            while (true)
            {
                if (Settings.Profiles.Value != ProfilesEnum.Leader)
                {
                    Thread.Sleep(100);
                    continue;
                }

                bool shouldWork =
                    Settings.FollowerModeSettings.FollowerModes.Value == FollowerNetworkActivityModeEnum.File &&
                    Settings.LeaderModeSettings.LeaderModeFileSettings.StartFileWriting.Value ||
                    Settings.FollowerModeSettings.FollowerModes.Value == FollowerNetworkActivityModeEnum.Network &&
                    Settings.LeaderModeSettings.LeaderModeNetworkSettings.StartServer.Value;

                try
                {
                    InitCommandProtocol();

                    if (shouldWork) _commandProtocol?.Work(CreateNetworkActivityObject());
                }
                catch (Exception e)
                {
                    LogError($"{Name}: Exception! \nMessage: {e.Message} \n{e.StackTrace}", 30);
                }

                Thread.Sleep(50);
            }
        }

        private void InitCommandProtocol()
        {
            bool shouldUseServerProtocol = Settings.FollowerModeSettings.FollowerModes.Value ==
                                           FollowerNetworkActivityModeEnum.Network;
            bool shouldUseFileProtocol = Settings.FollowerModeSettings.FollowerModes.Value ==
                                         FollowerNetworkActivityModeEnum.File;
            bool shouldBeDisabled = Settings.FollowerModeSettings.FollowerModes.Value ==
                                    FollowerNetworkActivityModeEnum.Local;

            if (shouldBeDisabled)
            {
                _commandProtocol?.Stop();
                _commandProtocol = null;
            }
            else if (shouldUseServerProtocol)
            {
                if (_commandProtocol is ServerCommandProtocol) return;

                _commandProtocol?.Stop();
                _commandProtocol = new ServerCommandProtocol(Settings);
                _commandProtocol.Restart();
            }
            else if (shouldUseFileProtocol)
            {
                if (_commandProtocol is FileCommandProtocol) return;

                _commandProtocol?.Stop();
                _commandProtocol = new FileCommandProtocol(Settings);
                _commandProtocol.Restart();
            }
            else
            {
                throw new Exception("Exception in InitCommandProtocol");
            }
        }

        private IEnumerator UpdateNearbyPlayersWork()
        {
            LogMsgWithVerboseDebug("Starting UpdateNearbyPlayersWork function");
            while (true)
            {
                ICollection<Entity> entities = GetEntities();

                List<string> playerNames = entities
                    .Where(e => e.Type == EntityType.Player)
                    .Where(e =>
                    {
                        string playerName = e.GetComponent<Player>().PlayerName;
                        if (playerName == "") return false;
                        return playerName != GameController.Player.GetComponent<Player>().PlayerName;
                    })
                    .Select(e => e.GetComponent<Player>().PlayerName)
                    .ToList();

                Settings.NearbyPlayers.Values = playerNames;

                yield return new WaitTime(1000);
            }
        }

        private Entity GetLeaderEntity()
        {
            string leaderName = Settings.FollowerModeSettings.LeaderName.Value;

            try
            {
                return GameController.Entities
                    .Where(x => x.Type == EntityType.Player)
                    .FirstOrDefault(x => x.GetComponent<Player>().PlayerName == leaderName);
            }
            // Sometimes we can get "Collection was modified; enumeration operation may not execute" exception
            catch
            {
                return null;
            }
        }

        private void HoverTo(Entity entity, int xOffset = 0, int yOffset = 0)
        {
            //LogMsgWithVerboseDebug("HoverTo called");

            if (entity == null) return;

            Camera camera = GameController.Game.IngameState.Camera;
            Vector2 windowOffset = GameController.Window.GetWindowRectangle().TopLeft;

            Vector2 result = camera.WorldToScreen(entity.Pos);

            int randomXOffset = new Random().Next(0, Settings.RandomClickOffset.Value);
            int randomYOffset = new Random().Next(0, Settings.RandomClickOffset.Value);

            Vector2 finalPos = new Vector2(
                result.X + randomXOffset + xOffset + windowOffset.X,
                result.Y + randomYOffset + yOffset + windowOffset.Y);

            bool intersects =
                GameController.Window.GetWindowRectangleTimeCache.Intersects(new RectangleF(finalPos.X, finalPos.Y, 3,
                    3));
            // The entity is inside the game window and visible, we can just hover
            if (intersects)
            {
                Mouse.SetCursorPosHuman2(finalPos);
                return;
            }

            // The entity is outside of the visibility. Make some calculations to click within the game window borders
            int smallOffset = 5;

            float topLeftX = GameController.Window.GetWindowRectangle().TopLeft.X;
            float topLeftY = GameController.Window.GetWindowRectangle().TopLeft.Y;
            float bottomRightX = GameController.Window.GetWindowRectangle().BottomRight.X;
            float bottomRightY = GameController.Window.GetWindowRectangle().BottomRight.Y;

            if (finalPos.X < topLeftX) finalPos.X = topLeftX + smallOffset;
            if (finalPos.Y < topLeftY) finalPos.Y = topLeftY + smallOffset;
            if (finalPos.X > bottomRightX) finalPos.X = bottomRightX - smallOffset;

            if (finalPos.Y > bottomRightY) finalPos.Y = bottomRightY - smallOffset;

            Mouse.SetCursorPosHuman2(finalPos);
        }

        private string GetFollowerNetworkActivityReply()
        {
            LogMsgWithVerboseDebug($"Function {nameof(GetFollowerNetworkActivityReply)} called");

            string result = null;

            string url = Settings.FollowerModeSettings.FollowerModeNetworkSettings.Url.Value;
            int timeoutMs = Settings.FollowerModeSettings.FollowerModeNetworkSettings.RequestTimeoutMs.Value;

            if (string.IsNullOrEmpty(url))
            {
                LogMsgWithVerboseDebug("    url in GetFollowerNetworkActivityReply was null or empty");
                return null;
            }

            if (!NetworkHelper.IsUrlAlive(url, timeoutMs))
            {
                LogMsgWithVerboseDebug("    url is not alive");
                return null;
            }

            if (_networkRequestStatusRetries > 5)
            {
                _networkRequestStatus = NetworkRequestStatus.Finished;
                _networkRequestStatusRetries = 0;
            }

            if (_networkRequestStatus == NetworkRequestStatus.Working)
            {
                LogMsgWithVerboseDebug("    request has not been finished in GetFollowerNetworkActivityReply");
                _networkRequestStatusRetries++;
                return null;
            }

            _networkRequestStatus = NetworkRequestStatus.Working;

            try
            {
                result = NetworkHelper.GetNetworkResponse(url, timeoutMs);
            }
            finally
            {
                _networkRequestStatus = NetworkRequestStatus.Finished;
            }

            return result;
        }

        private string GetFollowerFileReadReply()
        {
            string filePath = Settings.FollowerModeSettings.FollowerModeFileSettings.FilePath.Value;


            try
            {
                return File.ReadAllText(filePath);
            }
            // These exceptions are OK to skip
            catch (DirectoryNotFoundException e)
            {
            }
            catch (IOException e)
            {
            }

            return null;
        }

        private List<Entity> GetEntitiesByEntityTypeAndSortByDistance(EntityType entityType, Entity entity)
        {
            try
            {
                return GameController.EntityListWrapper.ValidEntitiesByType[entityType]
                    .OrderBy(o => FollowerHelpers.EntityDistance(o, entity))
                    .ToList();
            }
            catch
            {
            }

            return null;
        }

        private List<Entity> GetEntrancesByTransitionTypeAndSortByDistance(AreaTransitionType transitionType, Entity entity)
        {
            try
            {
                return GameController.EntityListWrapper.ValidEntitiesByType[EntityType.AreaTransition].Where(x => x.GetComponent<AreaTransition>().TransitionType == transitionType)
                    .OrderBy(o => FollowerHelpers.EntityDistance(o, entity))
                    .ToList();
            }
            catch
            {
            }

            return null;
        }

        private void ProcessNetworkActivityResponse(NetworkActivityObject obj)
        {
            LogMsgWithVerboseDebug("ProcessNetworkActivityResponse called");

            if (obj == null) return;

            Settings.FollowerModeSettings.FollowerShouldWork.Value = obj.FollowersShouldWork;
            Settings.FollowerModeSettings.LeaderName.Value = obj.LeaderName;
            Settings.FollowerModeSettings.LeaderProximityRadius.Value = obj.LeaderProximityRadius;
            Settings.FollowerModeSettings.MinimumFpsThreshold.Value = obj.MinimumFpsThreshold;

            string selfName = GameController.EntityListWrapper.Player.GetComponent<Player>().PlayerName;
            var follower =
                obj.FollowerCommandSettings.FollowerCommandsDataSet.FirstOrDefault(f => f.FollowerName == selfName);

            if (follower == null) return;

            _followerState.LastTimeEntranceUsedDateTime = follower.LastTimeEntranceUsedDateTime;
            _followerState.LastTimePortalUsedDateTime = follower.LastTimePortalUsedDateTime;
            _followerState.LastTimeWaypointUsedDateTime = follower.LastTimeWaypointUsedDateTime;
            _followerState.LastTimeTeleportedDateTime = follower.LastTimeTeleportedDateTime;
            _followerState.LastTimeQuestItemPickupDateTime = follower.LastTimeQuestItemPickupDateTime;
            _followerState.LastTimeNormalItemPickupDateTime = follower.LastTimeNormalItemPickupDateTime;
            _followerState.LastTimeEnterHideoutUsedDateTime = follower.LastTimeEnterHideoutUsedDateTime;
            _followerState.HideoutCharacterName = follower.HideoutCharacterName;

            _followerState.NormalItemId = follower.NormalItemId;
            _followerState.ShouldLevelUpGems = follower.ShouldLevelUpGems;
            _followerState.ShouldFollowThroughEntrances = follower.ShouldFollowThroughEntrances;
            _followerState.Aggressive = follower.Aggressive;

            if (_followerState.LastTimePortalUsedDateTime != _emptyDateTime &&
                _followerState.LastTimePortalUsedDateTime != _followerState.SavedLastTimePortalUsedDateTime)
            {
                _followerState.SavedLastTimePortalUsedDateTime = _followerState.LastTimePortalUsedDateTime;
                _followerState.CurrentAction = ActionsEnum.UsingPortal;
            }

            if (_followerState.LastTimeWaypointUsedDateTime != _emptyDateTime &&
                _followerState.LastTimeWaypointUsedDateTime != _followerState.SavedLastTimeWaypointUsedDateTime)
            {
                _followerState.SavedLastTimeWaypointUsedDateTime = _followerState.LastTimeWaypointUsedDateTime;
                _followerState.CurrentAction = ActionsEnum.UsingWaypoint;
            }

            if (_followerState.LastTimeTeleportedDateTime != _emptyDateTime &&
                _followerState.LastTimeTeleportedDateTime != _followerState.SavedLastTimeTeleportedDateTime)
            {
                _followerState.SavedLastTimeTeleportedDateTime = _followerState.LastTimeTeleportedDateTime;
                _followerState.CurrentAction = ActionsEnum.Teleporting;
            }

            if (_followerState.LastTimeEntranceUsedDateTime != _emptyDateTime &&
                _followerState.LastTimeEntranceUsedDateTime != _followerState.SavedLastTimeEntranceUsedDateTime)
            {
                _followerState.SavedLastTimeEntranceUsedDateTime = _followerState.LastTimeEntranceUsedDateTime;
                _followerState.CurrentAction = ActionsEnum.UsingEntrance;
            }

            if (_followerState.LastTimeEnterHideoutUsedDateTime != _emptyDateTime &&
                _followerState.LastTimeEnterHideoutUsedDateTime != _followerState.SavedLastTimeEnterHideoutUsedDateTime)
            {
                _followerState.SavedLastTimeEnterHideoutUsedDateTime = _followerState.LastTimeEnterHideoutUsedDateTime;
                _followerState.CurrentAction = ActionsEnum.EnteringHideout;
            }

            // We want to replace values but we don't want to replace the object reference because of LastTimeUsed
            follower.FollowerSkills.ForEach(skill =>
            {
                FollowerSkill localFollowerSkill = _followerState.FollowerSkills.Find(s => s.Id == skill.Id);
                if (localFollowerSkill == null)
                {
                    _followerState.FollowerSkills.Add(skill);
                    return;
                }

                localFollowerSkill.OverwriteValues(skill);
            });

            var localIds = _followerState.FollowerSkills.Select(s => s.Id);
            var remoteIds = follower.FollowerSkills.Select(s => s.Id);

            // Remove all local skills which are not present in the parsed JSON response
            localIds
                .Where(id => !remoteIds.Contains(id))
                .Select(id => _followerState.FollowerSkills.Find(s => s.Id == id))
                .ToList()
                .ForEach(skill => _followerState.FollowerSkills.RemoveAll(s => s.Id == skill.Id));
        }

        private void StartNetworkRequestingPressed()
        {
            LogMsgWithVerboseDebug("StartNetworkRequestingPressed called");

            Settings.FollowerModeSettings.StartRequesting.Value =
                !Settings.FollowerModeSettings.StartRequesting.Value;
        }

        private ICollection<Entity> GetEntities()
        {
            try
            {
                return GameController.Entities;
            }
            catch
            {
            }

            return null;
        }

        /*
        * This contains a lot of limitations. Do NOT use with anything except normal numbers or characters
        * E.g. this will translate "hideout" to List<Keys> { Keys.H, Keys.I, Keys.D, Keys.E } etc.
        */
        private List<Keys> StringToKeysList(string str)
        {
            LogMsgWithVerboseDebug($"Inside StringToKeysList, trying to parse {str} to List<Keys>");

            var keys = new List<Keys>();

            if (string.IsNullOrEmpty(str)) return null;

            foreach (var c in str.ToCharArray().Select(e => e.ToString()).ToList())
            {
                Keys key;
                var parsed = Enum.TryParse(c.ToUpper(), out key);
                if (!parsed) return null;

                keys.Add(key);
            }

            return keys;
        }

        private NetworkActivityObject CreateNetworkActivityObject()
        {
            return new NetworkActivityObject
            {
                FollowersShouldWork = Settings.LeaderModeSettings.PropagateWorkingOfFollowers.Value,
                LeaderName = Settings.LeaderModeSettings.LeaderNameToPropagate.Value,
                LeaderProximityRadius = Settings.LeaderModeSettings.LeaderProximityRadiusToPropagate.Value,
                MinimumFpsThreshold = Settings.LeaderModeSettings.MinimumFpsThresholdToPropagate.Value,
                FollowerCommandSettings = Settings.LeaderModeSettings.FollowerCommandSetting
            };
        }

        private void DebugHoverToLeader()
        {
            HoverTo(GetLeaderEntity());
        }

        private void LogMsgWithDebug(string message)
        {
            if (!Settings.Debug.Value) return;
            LogMessage(message);
        }

        private void LogMsgWithVerboseDebug(string message)
        {
            if (Settings.Debug.Value && Settings.VerboseDebug.Value)
                LogMessage(message);
        }

        private void RenderAdditionalFollowerCommandImguiWindow()
        {
            DateTime emptyDateTime = new DateTime(1, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            var newWindowFlags = ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoBackground |
                                 ImGuiWindowFlags.NoScrollbar;
            string resizeButtonLabel = "Allowing resize";
            string lockButtonLabel = "Unlocked";

            if (Settings.FollowerCommandsImguiSettings.LockPanel.Value)
            {
                newWindowFlags |= ImGuiWindowFlags.NoMove;
                lockButtonLabel = "Locked";
            }

            if (Settings.FollowerCommandsImguiSettings.NoResize.Value)
            {
                newWindowFlags |= ImGuiWindowFlags.NoResize;
                resizeButtonLabel = "Restricting resizing";
            }

            ImGui.SetNextWindowBgAlpha(0.35f);
            ImGui.Begin("FollowerV2", newWindowFlags);

            ImGui.TextUnformatted("This window commands");
            ImGui.SameLine();
            if (ImGui.Button(lockButtonLabel))
                Settings.FollowerCommandsImguiSettings.LockPanel.Value =
                    !Settings.FollowerCommandsImguiSettings.LockPanel.Value;
            ImGui.SameLine();
            if (ImGui.Button(resizeButtonLabel))
                Settings.FollowerCommandsImguiSettings.NoResize.Value =
                    !Settings.FollowerCommandsImguiSettings.NoResize.Value;
            ImGui.Spacing();

            int userNumber = 1;

            foreach (var follower in Settings.LeaderModeSettings.FollowerCommandSetting.FollowerCommandsDataSet)
            {
                int maxNameLength = 10;
                string followerName = follower.FollowerName;

                if (followerName.Length > maxNameLength)
                    followerName = followerName.Substring(0, maxNameLength - 2) + "..";
                else if (followerName.Length < maxNameLength) followerName = followerName.PadLeft(maxNameLength);

                ImGui.TextUnformatted($"User {userNumber}: {followerName}:");
                ImGui.SameLine();
                if (follower.LastTimeEntranceUsedDateTime != emptyDateTime)
                {
                    ImGui.TextUnformatted("E");
                    ImGui.SameLine();
                }

                if (follower.LastTimePortalUsedDateTime != emptyDateTime)
                {
                    ImGui.TextUnformatted("P");
                    ImGui.SameLine();
                }
                if (follower.LastTimeTeleportedDateTime != emptyDateTime)
                {
                    ImGui.TextUnformatted("T");
                    ImGui.SameLine();
                }
                if (follower.LastTimeWaypointUsedDateTime != emptyDateTime)
                {
                    ImGui.TextUnformatted("W");
                    ImGui.SameLine();
                }
                if (follower.LastTimeQuestItemPickupDateTime != emptyDateTime)
                {
                    ImGui.TextUnformatted("Q");
                    ImGui.SameLine();
                }

                if (follower.LastTimeNormalItemPickupDateTime != emptyDateTime)
                {
                    ImGui.TextUnformatted("I");
                    ImGui.SameLine();
                }

                if (follower.LastTimeEnterHideoutUsedDateTime != emptyDateTime)
                {
                    ImGui.TextUnformatted("H");
                    ImGui.SameLine();
                }

                if (ImGui.Button($"E##{follower.FollowerName}")) follower.SetToUseEntrance();

                ImGui.SameLine();
                if (ImGui.Button($"P##{follower.FollowerName}")) follower.SetToUsePortal();

                ImGui.SameLine();
                if (ImGui.Button($"W##{follower.FollowerName}")) follower.SetToUseWaypoint();

                ImGui.SameLine();
                if (ImGui.Button($"T##{follower.FollowerName}")) follower.SetToTeleporting();

                ImGui.SameLine();
                if (ImGui.Button($"QIPick##{follower.FollowerName}")) follower.SetPickupQuestItem();

                ImGui.SameLine();
                if (ImGui.Button($"H##{follower.FollowerName}"))
                    Settings.LeaderModeSettings.FollowerCommandSetting
                        .SetFollowersToEnterHideout(follower.FollowerName);

                ImGui.SameLine();
                if (ImGui.Button($"Del##{follower.FollowerName}"))
                    Settings.LeaderModeSettings.FollowerCommandSetting.RemoveFollower(follower.FollowerName);

                ImGui.SameLine();
                ImGui.TextUnformatted($"I: Ctrl+{userNumber}");

                ImGui.SameLine();
                ImGui.Checkbox($"Aggr##{follower.FollowerName}", ref follower.Aggressive);

                userNumber++;
            }

            ImGui.Spacing();

            string leaderName = Settings.LeaderModeSettings.LeaderNameToPropagate.Value;
            List<FollowerCommandsDataClass> followers =
                Settings.LeaderModeSettings.FollowerCommandSetting.FollowerCommandsDataSet.ToList();

            ImGui.SameLine();
            ImGui.TextUnformatted("All:  ");
            ImGui.SameLine();
            if (ImGui.Button("Entrance##AllEntrance")) followers.ForEach(f => f.SetToUseEntrance());
            ImGui.SameLine();
            if (ImGui.Button("Portal##AllPortal")) followers.ForEach(f => f.SetToUsePortal());
            ImGui.SameLine();
            if (ImGui.Button("Waypoint##AllWaypoint")) followers.ForEach(f => f.SetToUseWaypoint());
            ImGui.SameLine();
            if (ImGui.Button("Teleport##AllTeleport")) followers.ForEach(f => f.SetToTeleporting());
            ImGui.SameLine();
            if (ImGui.Button("PickQuestItem##AllPickQuestItem")) followers.ForEach(f => f.SetPickupQuestItem());
            ImGui.SameLine();
            if (ImGui.Button("Leader's H##AllLeaderHideout"))
                Settings.LeaderModeSettings.FollowerCommandSetting.SetFollowersToEnterHideout(leaderName);
            ImGui.Spacing();

            ImGui.Spacing();
            ImGui.End();
        }

        public override void OnPluginDestroyForHotReload()
        {
            _nearbyPlayersUpdateCoroutine.Done(true);
            _followerCoroutine.Done(true);
        }

        private enum NetworkRequestStatus
        {
            Finished,
            Working
        }

        #region TreeSharp Related

        private bool ShouldLevelUpGems()
        {
            // Return fast so that we do not waste computing resources
            if (!_followerState.ShouldLevelUpGems) return false;

            // Also do not run gems level up composite more often than once per 5 seconds
            int delayMs = 5000;
            long delta = DelayHelper.GetDeltaInMilliseconds(_followerState.LastTimeLevelUpGemsCompositeRan);
            if (delta < delayMs) return false;

            _followerState.LastTimeLevelUpGemsCompositeRan = DateTime.UtcNow;

            // Do we have gems to level-up ?
            return GetLevelableGems().Any();
        }

        private List<Element> GetLevelableGems()
        {
            List<Element> gemsToLevelUp = new List<Element>();

            // var possibleGemsToLvlUpElements = GameController.IngameState.IngameUi?.GemLvlUpPanel?.GemsToLvlUp;
            var possibleGemsToLvlUpElements = GameController.IngameState.IngameUi?.GetChildAtIndex(4).GetChildAtIndex(1).GetChildAtIndex(0)?.Children;

            foreach (var some in possibleGemsToLvlUpElements)
                LogMsgWithVerboseDebug($"Gems to level up:{some}");

            if (possibleGemsToLvlUpElements != null && possibleGemsToLvlUpElements.Any())
                foreach (Element possibleGemsToLvlUpElement in possibleGemsToLvlUpElements)
                foreach (Element elem in possibleGemsToLvlUpElement.Children)
                    if (elem.Text != null && elem.Text.Contains("Click to level"))
                        gemsToLevelUp.Add(possibleGemsToLvlUpElement);

            return gemsToLevelUp;
        }

        private bool IsFpsAboveThreshold()
        {
            // int currentFps = (int) GameController.IngameState.CurFps;
            // int threshold = Settings.FollowerModeSettings.MinimumFpsThreshold.Value;

            // return currentFps >= threshold;
            return true; //FPS is 0 after 3.13.0 update
        }

        private bool ShouldAttackMonsters()
        {
            // Do we have attack skills without a cooldown?
            List<FollowerSkill> availableAttackSkills = GetFollowerAttackSkillsWithoutCooldown();

            if (!availableAttackSkills.Any()) return false;

            // Are there monsters around?
            List<Entity> monstersList = GetMonsterEntities();

            if (monstersList == null || !monstersList.Any()) return false;

            // Are monsters within any of the attack skill distances?
            List<int> skillDistances = availableAttackSkills.Select(s => s.MaxRange).ToList();
            List<int> monsterDistancesToPlayer = monstersList.Select(e => (int) e.DistancePlayer).ToList();
            bool withinRange = false;

            foreach (int monsterDistance in monsterDistancesToPlayer)
            foreach (int skillDistance in skillDistances)
                if (monsterDistance <= skillDistance)
                {
                    withinRange = true;
                    break;
                }

            if (!withinRange) return false;

            return true;
        }

        private List<Entity> GetMonsterEntities()
        {
            List<Entity> monstersList = null;
            try
            {
                // try/catch is intentional to avoid ExileApi issue. DO NOT REMOVE
                monstersList = GameController.Entities
                    .Where(e => e.Type == EntityType.Monster)
                    .Where(e => e.IsAlive && e.IsHostile && e.IsTargetable && e.IsValid)
                    .ToList();
            }
            catch
            {
            }

            return monstersList;
        }

        private Entity GetClosestCorpse(int maxDistance)
        {
            try
            {
                return GetEntities()
                    .Where(e => e.Type == EntityType.Monster)
                    .Where(e => !e.IsAlive)
                    .Where(e => (int) e.DistancePlayer <= maxDistance)
                    .OrderBy(e => e.DistancePlayer)
                    .FirstOrDefault();
            }
            catch
            {
            }

            return null;
        }

        private List<FollowerSkill> GetFollowerAttackSkillsWithoutCooldown()
        {
            return _followerState.FollowerSkills
                .Where(s => s.Enable && !s.IsMovingSkill)
                .Where(s => DelayHelper.GetDeltaInMilliseconds(s.LastTimeUsed) > s.CooldownMs)
                .ToList();
        }

        private bool ShouldPickupNormalItem()
        {
            return _followerState.LastTimeNormalItemPickupDateTime != _emptyDateTime &&
                   _followerState.LastTimeNormalItemPickupDateTime !=
                   _followerState.SavedLastTimeNormalItemPickupDateTime;
        }

        private bool ShouldPickupQuestItem()
        {
            return _followerState.LastTimeQuestItemPickupDateTime != _emptyDateTime &&
                   _followerState.LastTimeQuestItemPickupDateTime !=
                   _followerState.SavedLastTimeQuestItemPickupDateTime;
        }

        private bool ShouldFollowLeader()
        {
            //LogMsgWithVerboseDebug($"{nameof(ShouldFollowLeader)} called");

            bool leaderNotEmpty = !string.IsNullOrEmpty(Settings.FollowerModeSettings.LeaderName.Value);
            Entity leaderEntity = GetLeaderEntity();
            if (leaderEntity == null) return leaderNotEmpty;

            var distance = leaderEntity.Distance(GameController.Player);
            //LogMsgWithVerboseDebug($"  distance: {distance}");
            //LogMsgWithVerboseDebug($"  proximity: {Settings.FollowerModeSettings.LeaderProximityRadius.Value}");
            bool outsideBorders = distance > Settings.FollowerModeSettings.LeaderProximityRadius.Value;

            return leaderNotEmpty && outsideBorders;
        }

        private bool BtCanTick()
        {
            //LogMsgWithVerboseDebug($"{nameof(BtCanTick)} called");

            if (GameController.IsLoading) return false;
            if (!GameController.Game.IngameState.ServerData.IsInGame)
                return false;
            if (GameController.Player == null || GameController.Player.Address == 0 || !GameController.Player.IsValid)
                return false;
            if (!GameController.Window.IsForeground()) return false;

            //LogMsgWithVerboseDebug("    BtCanTick returning true");

            return true;
        }

        private bool IsPlayerAlive()
        {
            return GameController.Game.IngameState.Data.LocalPlayer.GetComponent<Life>().CurHP > 0;
        }

        private bool ShouldWork()
        {
            //LogMsgWithVerboseDebug($"{nameof(ShouldWork)} called");

            if (Settings.Profiles.Value == ProfilesEnum.Follower)
                //LogMsgWithVerboseDebug($"    returning {Settings.FollowerModeSettings.FollowerShouldWork.Value}");

                return Settings.FollowerModeSettings.FollowerShouldWork.Value;

            //LogMsgWithVerboseDebug("    returning false");
            return false;
        }

        #endregion
    }
}
