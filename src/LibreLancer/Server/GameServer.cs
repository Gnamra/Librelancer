// MIT License - Copyright (c) Callum McGing
// This file is subject to the terms and conditions defined in
// LICENSE, which is part of this source code package

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LibreLancer.Data;
using LibreLancer.Data.Save;
using LibreLancer.Database;
using LibreLancer.GameData;
using LibreLancer.GameData.World;
using LibreLancer.Net;
using LibreLancer.Net.Protocol;
using Microsoft.EntityFrameworkCore.Design;

namespace LibreLancer.Server
{
    public class GameServer
    {
        public string ServerName = "Librelancer Server";
        public string ServerDescription = "Description of the server is here.";
        public string ServerNews = "News of the server goes here";
        public string LoginUrl = null;

        public bool SendDebugInfo = false;
        public string DebugInfo { get; private set; }

        public IDesignTimeDbContextFactory<LibreLancerContext> DbContextFactory;
        public GameDataManager GameData;
        public ServerDatabase Database;
        public ResourceManager Resources;
        public WorldProvider Worlds;
        public ServerPerformance PerformanceStats;

        public BaselinePrice[] BaselineGoodPrices;

        volatile bool running = false;

        public GameListener Listener;
        private Thread gameThread;

        public List<Player> ConnectedPlayers = new List<Player>();
        public Player LocalPlayer;

        public ConcurrentHashSet<long> CharactersInUse = new ConcurrentHashSet<long>();

        private bool needLoadData = true;


        private string debugInfoForFrame = "";
        public void ReportDebugInfo(string info)
        {
            debugInfoForFrame = info;
        }

        public GameServer(string fldir)
        {
            Resources = new ServerResourceManager();
            GameData = new GameDataManager(fldir, Resources);
            Listener = new GameListener(this);
        }

        public GameServer(GameDataManager gameData)
        {
            Resources = new ServerResourceManager();
            GameData = gameData;
            needLoadData = false;
        }

        public SaveGame NewCharacter(string name, int factionIndex)
        {
            var fac = GameData.Ini.NewCharDB.Factions[factionIndex];
            var pilot = GameData.Ini.NewCharDB.Pilots.First(x =>
                x.Nickname.Equals(fac.Pilot, StringComparison.OrdinalIgnoreCase));
            var package = GameData.Ini.NewCharDB.Packages.First(x =>
                x.Nickname.Equals(fac.Package, StringComparison.OrdinalIgnoreCase));
            //TODO: initial_rep = %%FACTION%%
            //does this have any effect in FL?

            var src = new StringBuilder(
                Encoding.UTF8.GetString(FlCodec.ReadFile(GameData.VFS.Resolve("EXE\\mpnewcharacter.fl"))));

            src.Replace("%%NAME%%", SavePlayer.EncodeName(name));
            src.Replace("%%BASE_COSTUME%%", pilot.Body);
            src.Replace("%%COMM_COSTUME%%", pilot.Comm);
            //Changing voice breaks in vanilla (commented out in mpnewcharacter)
            src.Replace("%%VOICE%%", pilot.Voice);
            //TODO: pilot comm_anim (not in vanilla mpnewcharacter)
            //TODO: pilot body_anim (not in vanilla mpnewcharacter)
            src.Replace("%%MONEY%%", package.Money.ToString());
            src.Replace("%%HOME_SYSTEM%%", GameData.Bases.Get(fac.Base).System);
            src.Replace("%%HOME_BASE%%", fac.Base);

            var pkgStr = new StringBuilder();
            pkgStr.Append("ship_archetype = ").AppendLine(package.Ship);
            var loadout = GameData.Ini.Loadouts.Loadouts.First(x =>
                x.Nickname.Equals(package.Loadout, StringComparison.OrdinalIgnoreCase));
            //do loadout
            foreach (var x in loadout.Equip)
            {
                pkgStr.AppendLine(new PlayerEquipment()
                {
                    Item = new HashValue(x.Nickname),
                    Hardpoint = x.Hardpoint ?? ""
                }.ToString());
            }

            foreach (var x in loadout.Cargo)
            {
                pkgStr.AppendLine(new PlayerCargo()
                {
                    Item = new HashValue(x.Nickname),
                    Count = x.Count
                }.ToString());
            }

            //append
            src.Replace("%%PACKAGE%%", pkgStr.ToString());
            var initext = src.ToString();
            return SaveGame.FromString($"mpnewcharacter: {fac.Nickname}", initext);
        }

        public void Start()
        {
            running = true;
            gameThread = new Thread(GameThread);
            gameThread.Name = "Game Server";
            gameThread.Start();
        }

        public void Stop()
        {
            running = false;
            gameThread.Join();
        }

        public void AdminChanged(long id, bool isAdmin)
        {
            foreach (var p in GetPlayers())
            {
                if (p.Character?.ID == id)
                {
                    p.Character.Admin = isAdmin;
                    break;
                }
            }
        }


        Dictionary<StarSystem, ServerWorld> worlds = new Dictionary<StarSystem, ServerWorld>();
        ConcurrentQueue<Action> worldRequests = new ConcurrentQueue<Action>();
        ConcurrentQueue<IPacket> localPackets = new ConcurrentQueue<IPacket>();

        public void OnLocalPacket(IPacket pkt)
        {
            localPackets.Enqueue(pkt);
        }

        public void WorldReady(ServerWorld world)
        {
            worldRequests.Enqueue(() =>
            {
                var sysName = this.GameData.GetString(world.System.IdsName);
                FLLog.Info("Server", "Spun up " + world.System.Nickname + " (" + sysName + ")");
                worlds.Add(world.System, world);
            });
        }

        void InitBaselinePrices()
        {
            var bp = new List<BaselinePrice>();
            foreach (var good in GameData.AllGoods)
            {
                bp.Add(new BaselinePrice()
                {
                    GoodCRC = CrcTool.FLModelCrc(good.Ini.Nickname),
                    Price = (ulong) good.Ini.Price
                });
            }

            BaselineGoodPrices = bp.ToArray();
        }

        public void SystemChatMessage(Player source, string message)
        {
            var s = source.System;
            foreach (var p in GetPlayers())
            {
                if (p.System.Equals(s, StringComparison.OrdinalIgnoreCase))
                    p.RemoteClient.ReceiveChatMessage(ChatCategory.System, source.Name, message);
            }
        }

        IEnumerable<Player> GetPlayers()
        {
            lock (ConnectedPlayers)
            {
                return ConnectedPlayers.ToArray();
            }
        }

        public IEnumerable<Player> AllPlayers => GetPlayers();

        public Player GetConnectedPlayer(string name) =>
            GetPlayers().FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));


        private FixedTimestepLoop processingLoop;

        public double TotalTime => processingLoop.TotalTime.TotalSeconds;

        //FromSeconds creates an inaccurate timespan
        static readonly TimeSpan RATE_60 = TimeSpan.FromTicks(166667);
        static readonly TimeSpan RATE_30 = TimeSpan.FromTicks(333333);

        void Process(TimeSpan time, TimeSpan totalTime)
        {
            var startTime = serverTiming.Elapsed;
            while (!localPackets.IsEmpty && localPackets.TryDequeue(out var local))
                LocalPlayer.ProcessPacketDirect(local);
            Action a;
            if (worldRequests.Count > 0 && worldRequests.TryDequeue(out a))
                a();
            //Update
            if (!(LocalPlayer?.World?.Paused ?? false))
            {
                LocalPlayer?.UpdateMissionRuntime(time.TotalSeconds);
            }
            ConcurrentBag<StarSystem> toSpinDown = new ConcurrentBag<StarSystem>();
            debugInfoForFrame = "";
            foreach (var w in worlds)
            {
                if (!w.Value.Update(time.TotalSeconds, totalTime.TotalSeconds))
                    toSpinDown.Add(w.Key);
            }

            DebugInfo = debugInfoForFrame;
            Listener?.Server?.TriggerUpdate(); //Send packets asap
            //Remove
            if (toSpinDown.Count > 0)
            {
                foreach (var w in toSpinDown)
                {
                    if (worlds[w].PlayerCount <= 0)
                    {
                        Worlds.RemoveWorld(w);
                        worlds[w].Finish();
                        worlds.Remove(w);
                        var wName = GameData.GetString(w.IdsName);
                        FLLog.Info("Server", $"Shut down world {w.Nickname} ({wName})");
                    }
                }
            }

            bool was30 = processingLoop.TimeStep == RATE_30;
            processingLoop.TimeStep = worlds.Count > 0 ? RATE_60 : RATE_30;
            var updateDuration = serverTiming.Elapsed - startTime;
            PerformanceStats?.AddEntry((float)updateDuration.TotalMilliseconds);
            if (updateDuration > RATE_60 && !was30)
            {
                FLLog.Warning("Server", $"Running slow: update took {updateDuration.TotalMilliseconds:F2}ms");
            }
            if (!running) processingLoop.Stop();
        }

        private Stopwatch serverTiming;

        void GameThread()
        {
            if (needLoadData)
            {
                FLLog.Info("Server", "Loading Game Data...");
                GameData.LoadData(null);
                FLLog.Info("Server", "Finished Loading Game Data");
            }
            Worlds = new WorldProvider(this);
            serverTiming = Stopwatch.StartNew();
            InitBaselinePrices();
            Database = new ServerDatabase(this);
            Listener?.Start();
            double lastTime = 0;
            processingLoop = new FixedTimestepLoop(Process);
            processingLoop.Start();
            Listener?.Stop();
        }
    }
}
