// MIT License - Copyright (c) Callum McGing
// This file is subject to the terms and conditions defined in
// LICENSE, which is part of this source code package

using System;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using LibreLancer.Client;
using LibreLancer.Client.Components;
using LibreLancer.GameData;
using LibreLancer.GameData.World;
using LibreLancer.Infocards;
using LibreLancer.Input;
using LibreLancer.Interface;
using LibreLancer.Net;
using LibreLancer.Render;
using LibreLancer.Render.Cameras;
using LibreLancer.Sounds.VoiceLines;
using LibreLancer.Thn;
using LibreLancer.World;
using LibreLancer.World.Components;

namespace LibreLancer
{
	public class SpaceGameplay : GameState
    {
        const string DEBUG_TEXT =
@"{3} ({4})
Camera Position: (X: {0:0.00}, Y: {1:0.00}, Z: {2:0.00})
C# Memory Usage: {5}
Velocity: {6}
Selected Object: {7}
Pitch: {8:0.00}
Yaw: {9:0.00}
Roll: {10:0.00}
Mouse Flight: {11}
World Time: {12:F2}
";
		private const float ROTATION_SPEED = 1f;
		StarSystem sys;
		public GameWorld world;
        public FreelancerGame FlGame => Game;

		SystemRenderer sysrender;
		bool wireframe = false;
		bool textEntry = false;
		string currentText = "";
		public GameObject player;
		ShipPhysicsComponent control;
        ShipSteeringComponent steering;
        ShipInputComponent shipInput;
        WeaponControlComponent weapons;
		PowerCoreComponent powerCore;
        CHealthComponent playerHealth;
        public SelectedTargetComponent Selection;
        private ContactList contactList;

		public float Velocity = 0f;
		const float MAX_VELOCITY = 80f;
        Cursor cur_arrow;
		Cursor cur_cross;
		Cursor cur_reticle;
		Cursor current_cur;
		CGameSession session;
        bool loading = true;
        LoadingScreen loader;
        public Cutscene Thn;
        private UiContext ui;
        private UiWidget widget;
        private LuaAPI uiApi;

        private bool pausemenu = false;
        private bool paused = false;
        private int nextObjectiveUpdate = 0;

		public SpaceGameplay(FreelancerGame g, CGameSession session) : base(g)
        {
			FLLog.Info("Game", "Entering system " + session.PlayerSystem);
            g.ResourceManager.ClearTextures(); //Do before loading things
            g.ResourceManager.ClearMeshes();
            Game.Ui.MeshDisposeVersion++;
            this.session = session;
            sys = g.GameData.Systems.Get(session.PlayerSystem);
            ui = Game.Ui;
            ui.GameApi = uiApi = new LuaAPI(this);
            nextObjectiveUpdate = session.CurrentObjective.Ids;
            session.ObjectiveUpdated = () => nextObjectiveUpdate = session.CurrentObjective.Ids;
            session.OnUpdateInventory = session.OnUpdatePlayerShip = null; //we should clear these handlers better
            loader = new LoadingScreen(g, g.GameData.LoadSystemResources(sys));
            loader.Init();
        }

        ChaseCamera _chaseCamera;
        TurretViewCamera _turretViewCamera;
        ICamera activeCamera;
        private bool isTurretView = false;

        void FinishLoad()
        {
            Game.Saves.Selected = -1;
            var shp = Game.GameData.Ships.Get(session.PlayerShip);
            //Set up player object + camera
            player = new GameObject(shp, Game.ResourceManager, true, true);
            player.Nickname = "player";
            control = new ShipPhysicsComponent(player) {Ship = shp};
            shipInput = new ShipInputComponent(player) {BankLimit = shp.MaxBankAngle};
            weapons = new WeaponControlComponent(player);
            pilotcomponent = new AutopilotComponent(player);
            steering = new ShipSteeringComponent(player);
            Selection = new SelectedTargetComponent(player);
            player.Components.Add(Selection);
            //Order components in terms of inputs (very important)
            player.Components.Add(pilotcomponent);
            player.Components.Add(shipInput);
            //takes input from pilot and shipinput
            player.Components.Add(steering);
            //takes input from steering
            player.Components.Add(control);
            player.Components.Add(weapons);
            player.Components.Add(new CDamageFuseComponent(player, shp.Fuses, shp.Explosion));
            player.Components.Add(new CPlayerCargoComponent(player, session));
            player.SetLocalTransform(session.PlayerOrientation * Matrix4x4.CreateTranslation(session.PlayerPosition));
            playerHealth = new CHealthComponent(player);
            playerHealth.MaxHealth = shp.Hitpoints;
            playerHealth.CurrentHealth = shp.Hitpoints;
            player.Components.Add(playerHealth);
            player.Flags |= GameObjectFlags.Player;
            if(shp.Mass < 0)
            {
                FLLog.Error("Ship", "Mass < 0");
            }

            player.Tag = GameObject.ClientPlayerTag;
            foreach (var equipment in session.Items.Where(x => !string.IsNullOrEmpty(x.Hardpoint)))
            {
                EquipmentObjectManager.InstantiateEquipment(player, Game.ResourceManager, Game.Sound, EquipmentType.LocalPlayer, equipment.Hardpoint, equipment.Equipment);
            }
            powerCore = player.GetComponent<PowerCoreComponent>();
            if (powerCore == null) throw new Exception("Player launched without a powercore equipped!");
            _chaseCamera = new ChaseCamera(Game.RenderContext.CurrentViewport, Game.GameData.Ini.Cameras);
            _turretViewCamera = new TurretViewCamera(Game.RenderContext.CurrentViewport, Game.GameData.Ini.Cameras);
            _turretViewCamera.CameraOffset = new Vector3(0, 0, shp.ChaseOffset.Length());
            _chaseCamera.ChasePosition = session.PlayerPosition;
            _chaseCamera.ChaseOrientation = player.LocalTransform.ClearTranslation();
            var offset = shp.ChaseOffset;

            _chaseCamera.DesiredPositionOffset = offset;
            if (shp.CameraHorizontalTurnAngle > 0)
                _chaseCamera.HorizontalTurnAngle = shp.CameraHorizontalTurnAngle;
            if (shp.CameraVerticalTurnUpAngle > 0)
                _chaseCamera.VerticalTurnUpAngle = shp.CameraVerticalTurnUpAngle;
            if (shp.CameraVerticalTurnDownAngle > 0)
                _chaseCamera.VerticalTurnDownAngle = shp.CameraVerticalTurnDownAngle;
            _chaseCamera.Reset();

            activeCamera = _chaseCamera;

            sysrender = new SystemRenderer(_chaseCamera, Game.ResourceManager, Game);
            sysrender.ZOverride = true; //Draw all with regular Z
            world = new GameWorld(sysrender, () => session.WorldTime);
            world.LoadSystem(sys, Game.ResourceManager, false);
            session.WorldReady();
            player.World = world;
            world.AddObject(player);
            player.Register(world.Physics);
            cur_arrow = Game.ResourceManager.GetCursor("arrow");
            cur_cross = Game.ResourceManager.GetCursor("cross");
            cur_reticle = Game.ResourceManager.GetCursor("fire_neutral");
            current_cur = cur_cross;
            Game.Keyboard.TextInput += Game_TextInput;
            Game.Keyboard.KeyDown += Keyboard_KeyDown;
            Game.Mouse.MouseDown += Mouse_MouseDown;
            Game.Mouse.MouseUp += Mouse_MouseUp;
            player.World = world;
            world.MessageBroadcasted += World_MessageBroadcasted;
            Game.Sound.ResetListenerVelocity();
            contactList = new ContactList(this);
            ui.OpenScene("hud");
            FadeIn(0.5, 0.5);
            updateStartDelay = 3;
        }

        public override void OnSettingsChanged() =>
            sysrender.Settings = Game.Config.Settings;


        protected override void OnActionDown(InputAction obj)
        {
            if(obj == InputAction.USER_SCREEN_SHOT) Game.Screenshots.TakeScreenshot();
            if(obj == InputAction.USER_FULLSCREEN) Game.ToggleFullScreen();
            if(!ui.KeyboardGrabbed && obj == InputAction.USER_CHAT)
               ui.ChatboxEvent();
        }

        RepAttitude GetRepToPlayer(GameObject obj)
        {
            if ((obj.Flags & GameObjectFlags.Friendly) == GameObjectFlags.Friendly) return RepAttitude.Friendly;
            if ((obj.Flags & GameObjectFlags.Neutral) == GameObjectFlags.Neutral) return RepAttitude.Neutral;
            if ((obj.Flags & GameObjectFlags.Hostile) == GameObjectFlags.Hostile) return RepAttitude.Hostile;
            if (obj.SystemObject != null)
            {
                var rep = session.PlayerReputations.GetReputation(obj.SystemObject.Reputation);
                if (rep <= Faction.HostileThreshold) return RepAttitude.Hostile;
                if (rep >= Faction.FriendlyThreshold) return RepAttitude.Friendly;
            }
            return RepAttitude.Neutral;
        }

        private int updateStartDelay = -1;

        [WattleScript.Interpreter.WattleScriptUserData]

        public class ContactList : IContactListData
        {
            readonly record struct Contact(GameObject obj, float distance, string display);

            private Contact[] Contacts = Array.Empty<Contact>();
            private SpaceGameplay game;
            private Vector3 playerPos;
            private Func<GameObject, bool> contactFilter;

            public ContactList()
            {
                contactFilter = AllFilter;
            }

            string GetDistanceString(float distance)
            {
                if (distance < 1000)
                    return $"{(int)distance}m";
                else if (distance < 10000)
                    return string.Format("{0:F1}k", distance / 1000);
                else if (distance < 90000)
                    return $"{((int) distance) / 1000}k";
                else
                    return "FAR";
            }


            Contact GetContact(GameObject obj)
            {
                var distance = Vector3.Distance(playerPos, Vector3.Transform(Vector3.Zero, obj.WorldTransform));
                var name = obj.Name.GetName(game.Game.GameData, playerPos);
                if (obj.Kind == GameObjectKind.Ship &&
                    obj.TryGetComponent<CFactionComponent>(out var fac))
                {
                    var fn = game.Game.GameData.GetString(fac.Faction.IdsShortName);
                    if(!string.IsNullOrWhiteSpace(fn))
                        name = $"{fn} - {name}";
                }
                return new Contact(obj, distance, $"{GetDistanceString(distance)} - {name}");
            }


            bool AllFilter(GameObject o) => true;
            bool ShipFilter(GameObject o) => o.Kind == GameObjectKind.Ship;
            bool StationFilter(GameObject o) => o.Kind == GameObjectKind.Solar;

            bool LootFilter(GameObject o) => false;

            bool ImportantFilter(GameObject o)
            {
                return game.Selection.Selected == o ||
                       (o.Flags & GameObjectFlags.Important) == GameObjectFlags.Important ||
                       o.Kind == GameObjectKind.Waypoint ||
                       game.GetRepToPlayer(o) == RepAttitude.Hostile;
            }

            public void SetFilter(string filter)
            {
                contactFilter = AllFilter;
                switch (filter)
                {
                    case "important":
                        contactFilter = ImportantFilter;
                        break;
                    case "ship":
                        contactFilter = ShipFilter;
                        break;
                    case "station":
                        contactFilter = StationFilter;
                        break;
                    case "loot":
                        contactFilter = LootFilter;
                        break;
                    case "all":
                        break;
                    default:
                        FLLog.Warning("Ui", $"Unknown contact list filter {filter}, defaulting to all");
                        break;
                }
            }

            public void UpdateList()
            {
                playerPos = Vector3.Transform(Vector3.Zero, game.player.WorldTransform);
                Contacts = game.world.Objects.Where(x => x != game.player &&
                                                         (x.Kind == GameObjectKind.Ship ||
                                                           x.Kind == GameObjectKind.Solar ||
                                                         x.Kind == GameObjectKind.Waypoint) &&
                                                         !string.IsNullOrWhiteSpace(x.Name?.GetName(game.Game.GameData, Vector3.Zero)))
                    .Where(contactFilter)
                    .Select(GetContact)
                    .OrderBy(x => x.distance).ToArray();
            }

            public ContactList(SpaceGameplay game)
            {
                this.game = game;
            }

            public int Count => Contacts.Length;
            public bool IsSelected(int index)
            {
                return game.Selection.Selected == Contacts[index].obj;
            }

            public void SelectIndex(int index)
            {
                game.Selection.Selected = Contacts[index].obj;
            }

            public string Get(int index)
            {
                return Contacts[index].display;
            }

            public RepAttitude GetAttitude(int index)
            {
                return game.GetRepToPlayer(Contacts[index].obj);
            }

            public bool IsWaypoint(int index)
            {
                return Contacts[index].obj.Kind == GameObjectKind.Waypoint;
            }

        }



        private int frameCount = 0;
        [WattleScript.Interpreter.WattleScriptUserData]
        public class LuaAPI
        {
            SpaceGameplay g;
            public LuaAPI(SpaceGameplay gameplay)
            {
                this.g = gameplay;
            }

            public ContactList GetContactList() => g.contactList;
            public KeyMapTable GetKeyMap()
            {
                var table = new KeyMapTable(g.Game.InputMap, g.Game.GameData.Ini.Infocards);
                table.OnCaptureInput += (k) =>
                {
                    g.Input.KeyCapture = k;
                };
                return table;
            }
            public GameSettings GetCurrentSettings() => g.Game.Config.Settings.MakeCopy();

            public int GetObjectiveStrid() => g.session.CurrentObjective.Ids;
            public void ApplySettings(GameSettings settings)
            {
                g.Game.Config.Settings = settings;
                g.Game.Config.Save();
            }

            public void Respawn()
            {
                g.session.RpcServer.Respawn();
            }

            public void PopupFinish(string id)
            {
                g.waitObjectiveFrames = 30;
                g.session.RpcServer.ClosedPopup(id);
                Resume();
            }

            public int CruiseCharge() => g.control.EngineState == EngineStates.CruiseCharging ? (int)(g.control.ChargePercent * 100) : -1;
            public bool IsMultiplayer() => g.session.Multiplayer;

            public SaveGameFolder SaveGames() => g.Game.Saves;
            public void DeleteSelectedGame() => g.Game.Saves.TryDelete(g.Game.Saves.Selected);

            public void LoadSelectedGame()
            {
                g.FadeOut(0.2, () =>
                {
                    g.session.OnExit();
                    var embeddedServer = new EmbeddedServer(g.Game.GameData);
                    var session = new CGameSession(g.Game, embeddedServer);
                    embeddedServer.StartFromSave(g.Game.Saves.SelectedFile);
                    g.Game.ChangeState(new NetWaitState(session, g.Game));
                });
            }

            public void SaveGame(string description)
            {
                g.session.Save(description);
            }

            public void Resume()
            {
                g.session.Resume();
                g.pausemenu = false;
                g.paused = false;
            }

            public DisplayFaction[] GetPlayerRelations() => g.session.GetUIRelations();

            public void QuitToMenu()
            {
                g.session.QuitToMenu();
            }
            public Maneuver[] GetManeuvers()
            {
                return g.Game.GameData.GetManeuvers().ToArray();
            }

            public Infocard CurrentInfocard()
            {
                if (g.Selection.Selected?.SystemObject != null)
                {
                    int ids = 0;
                    if (g.Selection.Selected.SystemObject.IdsInfo.Length > 0) {
                        ids = g.Selection.Selected.SystemObject.IdsInfo[0];
                    }
                    return g.Game.GameData.GetInfocard(ids, g.Game.Fonts);
                }
                return null;
            }

            public string CurrentInfoString() => g.Selection.Selected?.Name?.GetName(g.Game.GameData, Vector3.Zero);

            public string SelectionName()
            {
                return g.Selection.Selected?.Name?.GetName(g.Game.GameData, g.player.PhysicsComponent.Body.Position) ?? "NULL";
            }

            public TargetShipWireframe SelectionWireframe() => g.Selection.Selected != null ? g.targetWireframe : null;

            public bool SelectionVisible()
            {
                return g.Selection.Selected != null && g.ScreenPosition(g.Selection.Selected).visible;
            }

            public float SelectionHealth()
            {
                if (g.Selection.Selected == null) return -1;
                if (!g.Selection.Selected.TryGetComponent<CHealthComponent>(out var health))
                    return -1;
                return MathHelper.Clamp(health.CurrentHealth / health.MaxHealth, 0, 1);
            }

            public float SelectionShield()
            {
                if (g.Selection.Selected == null) return -1;
                CShieldComponent shield;
                if ((shield = g.Selection.Selected.GetFirstChildComponent<CShieldComponent>()) == null) return -1;
                return shield.ShieldPercent;
            }

            public string SelectionReputation()
            {
                if (g.Selection.Selected == null)
                    return "neutral";
                var rep = g.GetRepToPlayer(g.Selection.Selected);
                return rep switch
                {
                    RepAttitude.Friendly => "friendly",
                    RepAttitude.Hostile => "hostile",
                    _ => "neutral"
                };
            }


            public Vector2 SelectionPosition()
            {
                if (g.Selection.Selected == null) return new Vector2(-1000, -1000);
                var (pos, visible) = g.ScreenPosition(g.Selection.Selected);
                if (visible) {
                    return new Vector2(
                        g.ui.PixelsToPoints(pos.X),
                        g.ui.PixelsToPoints(pos.Y)
                    );
                } else {
                    return new Vector2(-1000, -1000);
                }
            }

            public void PopulateNavmap(Navmap nav)
            {
                nav.PopulateIcons(g.ui, g.sys);
            }

            public ChatSource GetChats() => g.session.Chats;
            public double GetCredits() => g.session.Credits;

            public float GetPlayerHealth() => g.playerHealth.CurrentHealth / g.playerHealth.MaxHealth;

            public float GetPlayerShield()
            {
                return g.player.GetFirstChildComponent<CShieldComponent>()?.ShieldPercent ?? -1;
            }

            public float GetPlayerPower() =>  g.powerCore.CurrentEnergy / g.powerCore.Equip.Capacity;

            private string activeManeuver = "FreeFlight";
            public string GetActiveManeuver() => activeManeuver;
            public LuaCompatibleDictionary GetManeuversEnabled()
            {
                var dict = new LuaCompatibleDictionary();
                dict.Set("FreeFlight", true);
                dict.Set("Goto", g.Selection.Selected != null);
                dict.Set("Dock", g.Selection.Selected?.GetComponent<CDockComponent>() != null);
                dict.Set("Formation", g.Selection.Selected != null && g.Selection.Selected.Kind == GameObjectKind.Ship);
                return dict;
            }
            public void HotspotPressed(string e)
            {
                if (g.ManeuverSelect(e))
                {
                    activeManeuver = e;
                }
            }

            public void ChatEntered(ChatCategory category, string text)
            {
                g.session.OnChat(category, text);
            }

            public UiEquippedWeapon[] GetWeapons() => g.weapons.GetUiElements().ToArray();

            internal void SetManeuver(string m)
            {
                activeManeuver = m;
            }
            public int ThrustPercent() => ((int)(g.powerCore.CurrentThrustCapacity / g.powerCore.Equip.ThrustCapacity * 100));
            public int Speed() => ((int)g.player.PhysicsComponent.Body.LinearVelocity.Length());
        }
		void World_MessageBroadcasted(GameObject sender, GameMessageKind kind)
		{
			switch (kind)
			{
				case GameMessageKind.ManeuverFinished:
                    uiApi.SetManeuver("FreeFlight");
					break;
			}
		}

        protected override void OnUnload()
		{
			Game.Keyboard.TextInput -= Game_TextInput;
			Game.Keyboard.KeyDown -= Keyboard_KeyDown;
            Game.Mouse.MouseDown -= Mouse_MouseDown;
			sysrender?.Dispose();
            world?.Dispose();
		}

		void Keyboard_KeyDown(KeyEventArgs e)
		{
            if (ui.KeyboardGrabbed)
            {
                ui.OnKeyDown(e.Key, (e.Modifiers & KeyModifiers.Control) != 0);
            }
            else if (e.Key == Keys.Escape && ui.WantsEscape())
            {
                ui.OnEscapePressed();
            }
            else
            {
                if (!pausemenu && e.Key == Keys.F1)
                {
                    pausemenu = true;
                    if(!session.Multiplayer)
                        paused = true;
                    session.Pause();
                    ui.Event("Pause");
                }
                #if DEBUG
                if (e.Key == Keys.R && (e.Modifiers & KeyModifiers.Control) != 0)
                    world.RenderDebugPoints = !world.RenderDebugPoints;
                #endif
            }
        }

		void Game_TextInput(string text)
		{
			ui.OnTextEntry(text);
		}

		bool dogoto = false;
		public AutopilotComponent pilotcomponent = null;

        bool ManeuverSelect(string e)
		{
			switch (e)
			{
				case "FreeFlight":
                    pilotcomponent.Cancel();
					return true;
				case "Dock":
					if (Selection.Selected == null) return false;
					CDockComponent d;
					if ((d = Selection.Selected.GetComponent<CDockComponent>()) != null)
					{
                        pilotcomponent.StartDock(Selection.Selected);
                        session.RpcServer.RequestDock(Selection.Selected);
						return true;
					}
					return false;
				case "Goto":
					if (Selection.Selected == null) return false;
                    pilotcomponent.GotoObject(Selection.Selected);
					return true;
                case "Formation":
                    session.RpcServer.EnterFormation(Selection.Selected.NetID);
                    return true;
			}
			return false;
		}


        public bool ShowHud = true;
        //Set to true when the mission system selection.Selected music on launch
        public bool RtcMusic = false;
        private bool musicTriggered = false;

        public override void Update(double delta)
		{
            if(loading)
            {
                if(loader.Update(delta))
                {
                    loading = false;
                    loader = null;
                    FinishLoad();
                }
                return;
            }
            if (ShowHud && (Thn == null || !Thn.Running))
            {
                contactList.UpdateList();
                ui.Update(Game);
            }
            if(ui.KeyboardGrabbed)
                Game.EnableTextInput();
            else
                Game.DisableTextInput();
            steering.Tick = (int) Game.CurrentTick;
            world.Update(paused ? 0 : delta);
            if (session.Update()) return;
            if (updateStartDelay == 0) {
                session.GameplayUpdate(this, delta);
                if (!musicTriggered)
                {
                    if (!RtcMusic) Game.Sound.PlayMusic(sys.MusicSpace, 0);
                    musicTriggered = true;
                }
            }
            UpdateCamera(delta);
            if (Thn != null && Thn.Running)
            {
                sysrender.Camera = Thn.CameraHandle;
            }
            else
                sysrender.Camera = activeCamera;
            if (frameCount < 2)
            {
                frameCount++;
                if (frameCount == 2) {
                    session.BeginUpdateProcess();
                }
            }
            else
            {
                if (session.Popups.Count > 0 && session.Popups.TryDequeue(out var popup))
                {
                    FLLog.Debug("Space", "Displaying popup");
                    if(!session.Multiplayer)
                        paused = true;
                    session.Pause();
                    ui.Event("Popup", popup.Title, popup.Contents, popup.ID);
                }
            }
            if (Selection.Selected != null && !Selection.Selected.Flags.HasFlag(GameObjectFlags.Exists)) Selection.Selected = null; //Object has been blown up/despawned
		}

		bool thrust = false;

		void UpdateCamera(double delta)
        {
            activeCamera = isTurretView ? _turretViewCamera : _chaseCamera;
            _chaseCamera.Viewport = Game.RenderContext.CurrentViewport;
            _turretViewCamera.Viewport = Game.RenderContext.CurrentViewport;
            if(Thn == null || !Thn.Running)
			    ProcessInput(delta);
            //Has to be here or glitches
            if (!Dead)
            {
                _turretViewCamera.ChasePosition = Vector3.Transform(Vector3.Zero, player.LocalTransform);
                _chaseCamera.ChasePosition = Vector3.Transform(Vector3.Zero, player.LocalTransform);
                _chaseCamera.ChaseOrientation = player.LocalTransform.ClearTranslation();
            }
            _turretViewCamera.Update(delta);
            _chaseCamera.Update(delta);
            if ((Thn == null ||
                 !Thn.Running)) //HACK: Cutscene also updates the listener so we don't do it if one is running
            {
                Game.Sound.UpdateListener(delta, _chaseCamera.Position, _chaseCamera.CameraForward, _chaseCamera.CameraUp);
            }
            else
            {
                Thn.Update(paused ? 0 : delta);
                ((ThnCamera)Thn.CameraHandle).DefaultZ(); //using Thn Z here is just asking for trouble
            }
        }

		bool mouseFlight = false;

        protected override void OnActionUp(InputAction action)
        {
			if (ui.KeyboardGrabbed || paused) return;
			switch (action)
			{
				case InputAction.USER_CRUISE:
                    steering.Cruise = !steering.Cruise;
					break;
				case InputAction.USER_TURN_SHIP:
					mouseFlight = !mouseFlight;
					break;
                case InputAction.USER_AUTO_TURRET:
                    isTurretView = !isTurretView;
                    break;
            }
		}

        private bool isLeftDown = false;


        void Mouse_MouseDown(MouseEventArgs e)
        {
            if((e.Buttons & MouseButtons.Left) > 0)
            {
                if(!(Game.Debug.CaptureMouse) && !ui.MouseWanted(Game.Mouse.X, Game.Mouse.Y))
                {
                    var newSelection = world.GetSelection(activeCamera, player, Game.Mouse.X, Game.Mouse.Y, Game.Width, Game.Height);
                    if (newSelection != null) Selection.Selected = newSelection;
                    isLeftDown = true;
                }
                else
                {
                    isLeftDown = false;
                }
            }
        }

        private void Mouse_MouseUp(MouseEventArgs e)
        {
            if ((e.Buttons & MouseButtons.Left) > 0)
            {
                isLeftDown = false;
            }
        }

		const float ACCEL = 85;

        public bool Dead = false;
        public void Killed()
        {
            Dead = true;
            Explode(player);
            world.RemoveObject(player);
            ui.Event("Killed");
        }

        public void Explode(GameObject obj)
        {
            if (obj.TryGetComponent<CDamageFuseComponent>(out var df) &&
                df.Explosion?.Effect != null)
            {
                var pfx = df.Explosion.Effect.GetEffect(FlGame.ResourceManager);
                sysrender.SpawnTempFx(pfx, Vector3.Transform(Vector3.Zero, obj.WorldTransform));
            }
        }

        public void StopShip()
        {
            shipInput.Throttle = 0;
            shipInput.AutopilotThrottle = 0;
            shipInput.MouseFlight = false;
            _chaseCamera.MouseFlight = false;
            _turretViewCamera.PanControls = Vector2.Zero;
            pilotcomponent.Cancel();
            steering.Thrust = false;
            shipInput.Reverse = false;
            steering.Cruise = false;
        }
		void ProcessInput(double delta)
        {
            if (Dead) {
                current_cur = cur_arrow;
                return;
            }
            if (paused) return;
            Input.Update();

            if (!ui.MouseWanted(Game.Mouse.X, Game.Mouse.Y))
            {
                shipInput.Throttle = MathHelper.Clamp(shipInput.Throttle + (Game.Mouse.Wheel / 3f), 0, 1);
            }

            shipInput.Reverse = false;

			if (!ui.KeyboardGrabbed)
            {
				if (Input.IsActionDown(InputAction.USER_INC_THROTTLE))
				{
                    shipInput.Throttle += (float)(delta);
					shipInput.Throttle = MathHelper.Clamp(shipInput.Throttle, 0, 1);
				}

				else if (Input.IsActionDown(InputAction.USER_DEC_THROTTLE))
				{
                    shipInput.Throttle -= (float)(delta);
                    shipInput.Throttle = MathHelper.Clamp(shipInput.Throttle, 0, 1);
				}
                steering.Thrust = Input.IsActionDown(InputAction.USER_AFTERBURN);
                shipInput.Reverse = Input.IsActionDown(InputAction.USER_MANEUVER_BRAKE_REVERSE);
            }

			StrafeControls strafe = StrafeControls.None;
            if (!ui.KeyboardGrabbed)
			{
				if (Input.IsActionDown(InputAction.USER_MANEUVER_SLIDE_EVADE_LEFT)) strafe |= StrafeControls.Left;
				if (Input.IsActionDown(InputAction.USER_MANEUVER_SLIDE_EVADE_RIGHT)) strafe |= StrafeControls.Right;
				if (Input.IsActionDown(InputAction.USER_MANEUVER_SLIDE_EVADE_UP)) strafe |= StrafeControls.Up;
				if (Input.IsActionDown(InputAction.USER_MANEUVER_SLIDE_EVADE_DOWN)) strafe |= StrafeControls.Down;
            }

			var pc = player.PhysicsComponent;
            shipInput.Viewport = new Vector2(Game.Width, Game.Height);
            shipInput.Camera = _chaseCamera;
            if ((isLeftDown || mouseFlight) && control.Active)
			{
                var mX = Game.Mouse.X;
                var mY = Game.Mouse.Y;
                if (isTurretView)
                {
                    _turretViewCamera.PanControls = new Vector2(
                        2f * (mX / (float)Game.Width) -1f, -(2f * (mY / (float)Game.Height) - 1f)
                    );
                    shipInput.MouseFlight = false;
                    _chaseCamera.MouseFlight = false;
                }
                else
                {
                    _chaseCamera.MousePosition = new Vector2(
                        mX, Game.Height - mY
                    );
                    shipInput.MouseFlight = true;
                    shipInput.MousePosition = new Vector2(mX, mY);
                    _chaseCamera.MouseFlight = true;
                    _turretViewCamera.PanControls = Vector2.Zero;
                }
            }
			else
			{
                shipInput.MouseFlight = false;
                _chaseCamera.MouseFlight = false;
                _turretViewCamera.PanControls = Vector2.Zero;
            }
			control.CurrentStrafe = strafe;

            GetCameraMatrices(out var cameraView, out var cameraProjection);

            var obj = world.GetSelection(activeCamera, player, Game.Mouse.X, Game.Mouse.Y, Game.Width, Game.Height);
            if (ui.MouseWanted(Game.Mouse.X, Game.Mouse.Y))
                current_cur = cur_arrow;
            else {
                current_cur = obj == null ? cur_cross : cur_reticle;
            }
            var end = Vector3Ex.UnProject(new Vector3(Game.Mouse.X, Game.Mouse.Y, 1f), cameraProjection, cameraView, new Vector2(Game.Width, Game.Height));
            var start = Vector3Ex.UnProject(new Vector3(Game.Mouse.X, Game.Mouse.Y, 0), cameraProjection, cameraView, new Vector2(Game.Width, Game.Height));
            var dir = (end - start).Normalized();
            var tgt = start + (dir * 400);
            weapons.AimPoint = tgt;

            if (world.Physics.PointRaycast(player.PhysicsComponent.Body, start, dir, 1000, out var contactPoint, out var po)) {
                weapons.AimPoint = contactPoint;
            }


            if (Input.IsActionDown(InputAction.USER_FIRE_WEAPONS))
                weapons.FireAll();
            if(Input.IsActionDown(InputAction.USER_LAUNCH_MISSILES))
                weapons.FireMissiles();
            for (int i = 0; i < 10; i++)
            {
                if (Input.IsActionDown(InputAction.USER_FIRE_WEAPON1 + i))
                    weapons.FireIndex(i);
            }

            if (world.Projectiles.HasQueued)
            {
                session.RpcServer.FireProjectiles(world.Projectiles.GetQueue());
            }

            if (world.Projectiles.HasMissilesQueued)
            {
                session.RpcServer.FireMissiles(world.Projectiles.GetMissileQueue());
            }
        }

        public void StartTradelane()
        {
            player.GetComponent<ShipPhysicsComponent>().Active = false;
            player.GetComponent<WeaponControlComponent>().Enabled = false;
            pilotcomponent.Cancel();
        }

        public void TradelaneDisrupted()
        {
            Game.Sound.PlayVoiceLine(VoiceLines.NnVoiceName, VoiceLines.NnVoice.TradeLaneDisrupted);
            EndTradelane();
        }

        public void EndTradelane()
        {
            player.GetComponent<ShipPhysicsComponent>().Active = true;
            player.GetComponent<WeaponControlComponent>().Enabled = true;
        }


        void GetCameraMatrices(out Matrix4x4 view, out Matrix4x4 projection)
        {
            view = activeCamera.View;
            projection = activeCamera.Projection;
        }

        void GetViewProjection(out Matrix4x4 vp)
        {
            vp = activeCamera.ViewProjection;
        }


        (Vector2 pos, bool visible) ScreenPosition(GameObject obj)
        {
            GetViewProjection(out var vp);
            var worldPos = Vector3.Transform(Vector3.Zero, obj.WorldTransform);
            var clipSpace = Vector4.Transform(new Vector4(worldPos, 1), vp);
            var ndc = clipSpace / clipSpace.W;
            var viewSize = new Vector2(Game.Width, Game.Height);
            var windowSpace = new Vector2(
                ((ndc.X + 1.0f) / 2.0f) * Game.Width,
                ((1.0f - ndc.Y) / 2.0f) * Game.Height
            );
            return (windowSpace, ndc.Z < 1);
        }

        private GameObject missionWaypoint;

        void UpdateObjectiveObjects()
        {
            if (missionWaypoint != null)
            {
                if (Selection.Selected == missionWaypoint)
                    Selection.Selected = null;
                world.RemoveObject(missionWaypoint);
                missionWaypoint = null;
            }
            if ((session.CurrentObjective.Kind == ObjectiveKind.Object ||
                session.CurrentObjective.Kind == ObjectiveKind.NavMarker) &&
                sys.Nickname.Equals(session.CurrentObjective.System, StringComparison.OrdinalIgnoreCase))
            {
                var pos = session.CurrentObjective.Kind == ObjectiveKind.Object
                    ? Vector3.Transform(Vector3.Zero, world.GetObject(session.CurrentObjective.Object).WorldTransform)
                    : session.CurrentObjective.Position;
                if (pos != Vector3.Zero)
                {
                    var waypointArch = Game.GameData.GetSolarArchetype("waypoint");
                    missionWaypoint = new GameObject(waypointArch, Game.ResourceManager);
                    missionWaypoint.Name = new ObjectName(1091); //Mission Waypoint
                    missionWaypoint.SetLocalTransform(Matrix4x4.CreateTranslation(pos));
                    missionWaypoint.World = world;
                    world.AddObject(missionWaypoint);
                    missionWaypoint.Register(world.Physics);
                }
            }
        }

        private TargetShipWireframe targetWireframe = new TargetShipWireframe();

		//RigidBody debugDrawBody;
        private int waitObjectiveFrames = 120;
		public override unsafe void Draw(double delta)
		{
            RenderMaterial.VertexLighting = false;
            if (loading)
            {
                loader.Draw(delta);
                return;
            }

            if (Selection.Selected != null) {
                targetWireframe.Model = Selection.Selected.RigidModel;
                var lookAt = Matrix4x4.CreateLookAt(Vector3.Transform(Vector3.Zero, player.LocalTransform),
                    Vector3.Transform(Vector3.UnitZ * 4, player.LocalTransform), Vector3.UnitY);

                targetWireframe.Matrix = (lookAt * Selection.Selected.LocalTransform).ClearTranslation();
            }

            if (updateStartDelay > 0) updateStartDelay--;
            if (waitObjectiveFrames > 0) waitObjectiveFrames--;
            world.RenderUpdate(delta);
            sysrender.DebugRenderer.StartFrame(Game.RenderContext);

            sysrender.Draw(Game.Width, Game.Height);

            sysrender.DebugRenderer.Render();

            if ((Thn == null || !Thn.Running) && ShowHud)
            {
                ui.Visible = true;
                if (nextObjectiveUpdate != 0 && waitObjectiveFrames <= 0)
                {
                    ui.Event("ObjectiveUpdate", nextObjectiveUpdate);
                    nextObjectiveUpdate = 0;
                    UpdateObjectiveObjects();
                }
                ui.RenderWidget(delta);
            }
            else
            {
                ui.Visible = false;
            }

            if (Thn != null && Thn.Running)
            {
                var pct = Cutscene.LETTERBOX_HEIGHT;
                int h = (int) (Game.Height * pct);
                Game.RenderContext.Renderer2D.FillRectangle(new Rectangle(0, 0, Game.Width, h), Color4.Black);
                Game.RenderContext.Renderer2D.FillRectangle(new Rectangle(0, Game.Height - h, Game.Width, h), Color4.Black);
            }
            session.SetDebug(Game.Debug.Enabled);
            Game.Debug.Draw(delta, () =>
            {
                string sel_obj = "None";
                if (Selection.Selected != null)
                {
                    if (Selection.Selected.Name == null)
                        sel_obj = "unknown object";
                    else
                        sel_obj = Selection.Selected.Name?.GetName(Game.GameData, player.PhysicsComponent.Body.Position) ?? "unknown object";
                    sel_obj = $"{sel_obj} ({Selection.Selected.Nickname ?? "null nickname"})";
                }
                var systemName = Game.GameData.GetString(sys.IdsName);
                var text = string.Format(DEBUG_TEXT, activeCamera.Position.X, activeCamera.Position.Y, activeCamera.Position.Z,
                    sys.Nickname, systemName, DebugDrawing.SizeSuffix(GC.GetTotalMemory(false)), Velocity, sel_obj,
                    control.Steering.X, control.Steering.Y, control.Steering.Z, mouseFlight, session.WorldTime);
                ImGui.Text(text);
                var dbgT = session.GetSelectedDebugInfo();
                if(!string.IsNullOrWhiteSpace(dbgT))
                    ImGui.Text(dbgT);
                ImGui.Text($"input queue: {session.UpdateQueueCount}");
                if (session.Multiplayer)
                {
                    var floats = new float[session.UpdatePacketSizes.Count];
                    for (int i = 0; i < session.UpdatePacketSizes.Count; i++)
                        floats[i] = session.UpdatePacketSizes[i];
                    fixed (float* f = floats)
                    {
                        ImGui.TextUnformatted($"last ack sent: {session.LastAck}");
                        ImGui.TextUnformatted($"update packet size: {floats[floats.Length - 1]}");
                        ImGui.PlotLines("update packet size", ref floats[0], floats.Length);
                    }
                }

                bool hasDebug = world.Physics.DebugRenderer != null;
                ImGui.Checkbox("Draw hitboxes", ref hasDebug);
                ImGui.BeginDisabled(!hasDebug);
                ImGui.Checkbox("Draw raycasts", ref world.Physics.ShowRaycasts);
                ImGui.EndDisabled();
                if (hasDebug)
                    world.Physics.DebugRenderer = sysrender.DebugRenderer;
                else
                    world.Physics.DebugRenderer = null;
                //ImGuiNET.ImGui.Text(pilotcomponent.ThrottleControl.Current.ToString());
            }, () =>
            {
                Game.Debug.MissionWindow(session.GetTriggerInfo());
            });
            if ((Thn == null || !Thn.Running) && ShowHud)
            {
                current_cur.Draw(Game.RenderContext.Renderer2D, Game.Mouse, Game.TotalTime);
            }
            DoFade(delta);
		}

        public override void Exiting()
        {
            session.OnExit();
        }

    }
}
