﻿// MIT License - Copyright (c) Callum McGing
// This file is subject to the terms and conditions defined in
// LICENSE, which is part of this source code package

using System;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ImGuiNET;
using LibreLancer.Net;
using LiteNetLib;
using LiteNetLib.Utils;

namespace LibreLancer
{
	public class GameListener
    {
        static GameListener()
        {
            NetDebug.Logger = new NetDebugLogger();
        }
        
        private GameServer game;
        private NetHpidWriter hpids;
        static readonly object TagConnecting = new object();

        public int Port = LNetConst.DEFAULT_PORT;
        public int MaxConnections = 200;
        public string AppIdentifier = LNetConst.DEFAULT_APP_IDENT;
        
        private bool running = false;
		Thread netThread;
		public NetManager Server;
        private HttpClient http;
        private string loginUrl;

		public GameListener(GameServer srv)
        {
            this.game = srv;
        }

		public void Start()
		{
			running = true;
            netThread = new Thread(NetThread);
            netThread.Name = "Server Listener";
            netThread.Start();
		}

        void NetThread()
        {
            http = new HttpClient();
            EventBasedNetListener listener = new EventBasedNetListener();
            EventBasedNetListener broadcastListener = new EventBasedNetListener();
            NetManager broadcastServer = new NetManager(broadcastListener);
            var unique = Guid.NewGuid();
            hpids = new NetHpidWriter();
            listener.ConnectionRequestEvent += request =>
            {
                
                if (!new PacketReader(request.Data).TryGetString(out var key))
                {
                    FLLog.Debug("Server", $"Connect with no key {request.RemoteEndPoint}");
                    request.Reject();
                    return;
                }
                if (key != AppIdentifier + GeneratedProtocol.PROTOCOL_HASH)
                {
                    FLLog.Debug("Server", $"Connect with bad key {request.RemoteEndPoint}");
                    request.Reject();
                    return;
                }
                if (Server.ConnectedPeersCount > MaxConnections)
                {
                    request.Reject();
                    return;
                }
                if (!string.IsNullOrEmpty(game.LoginUrl))
                {
                    var reqReader = new PacketReader(request.Data);
                    if (!reqReader.TryGetString(out var token))
                    {
                        var dw = new PacketWriter();
                        dw.Put("TokenRequired?" + game.LoginUrl);
                        FLLog.Debug("Server", "Sending TokenRequired");
                        request.Reject(dw);
                    }
                    else
                    {
                        Task.Run(async () =>
                        {
                            var guid = await http.VerifyToken(game.LoginUrl, token);
                            if (guid == Guid.Empty)
                            {
                                FLLog.Info("Login", $"Login failed for {request.RemoteEndPoint}");
                                var dw = new PacketWriter();
                                dw.Put("Login failure");
                                request.Reject(dw);
                            }
                            else
                            {
                                var peer = request.Accept();
                                var remote = new RemotePacketClient(peer, hpids);
                                remote.SendPacket(new SetStringsPacket() { Data = hpids.GetData() }, PacketDeliveryMethod.ReliableOrdered, true);
                                var p = new Player(remote,
                                    game, guid);
                                peer.Tag = p;
                                Task.Run(() => p.DoAuthSuccess());
                                lock (game.ConnectedPlayers)
                                {
                                    game.ConnectedPlayers.Add(p);
                                }
                            }
                        });
                    }
                }
                else
                {
                    RequestPlayerGuid(request.Accept());
                }
            };
            listener.PeerConnectedEvent += peer =>
            {
                FLLog.Info("Server", $"Connected: {peer.EndPoint}");
            };
            listener.PeerDisconnectedEvent += (peer, info) =>
            {
                FLLog.Info("Server", $"Disconnected: {peer.EndPoint}");
                if (peer.Tag is Player player)
                {
                    player.Disconnected();
                    lock (game.ConnectedPlayers)
                    {
                        game.ConnectedPlayers.Remove(player);
                    }
                }
            };
            broadcastListener.ConnectionRequestEvent += request => request.RejectForce();
            broadcastListener.NetworkReceiveUnconnectedEvent += (point, reader, type) =>
            {
                if (type == UnconnectedMessageType.Broadcast)
                {
                    new PacketReader(reader).TryGetULong(out ulong key);
                    if (key != LNetConst.BROADCAST_KEY) return;
                    var dw = new PacketWriter();
                    dw.Put((byte) 1);
                    dw.Put(unique);
                    dw.PutVariableUInt32((uint)Port);
                    dw.Put(game.ServerName);
                    dw.Put(game.ServerDescription);
                    dw.Put(game.GameData.DataVersion);
                    dw.PutVariableUInt32((uint) Server.ConnectedPeersCount);
                    dw.PutVariableUInt32((uint) MaxConnections);
                    broadcastServer.SendUnconnectedMessage(dw, point);
                }
            };
            listener.NetworkReceiveUnconnectedEvent += (point, msg, type) =>
            {
                try
                {
                    var reader = new PacketReader(msg);
                    if (type == UnconnectedMessageType.BasicMessage)
                    {
                        if (!reader.TryGetUInt(out uint magic)) return;
                        if (magic != LNetConst.PING_MAGIC) return;
                        var dw = new PacketWriter();
                        dw.Put((byte) 0);
                        Server.SendUnconnectedMessage(dw, point);
                    }
                }
                finally
                {
                    msg.Recycle();
                }
            };
            listener.NetworkReceiveEvent += (peer, msg, channel, method) =>
            {
                try
                {
                    NetHpidReader hpidReader = null;
                    if (peer.Tag is Player pl) hpidReader = pl.HpidReader;
                    var reader = new PacketReader(msg, hpidReader);
                    var pkt = Packets.Read(reader);
                    if (peer.Tag == TagConnecting)
                    {
                        if (pkt is AuthenticationReplyPacket auth)
                        {
                            if (auth.Guid == Guid.Empty)
                            {
                                var dw = new PacketWriter();
                                dw.Put("bad GUID");
                                peer.Disconnect(dw);
                            }
                            else
                            {
                                var remote = new RemotePacketClient(peer, hpids);
                                remote.SendPacket(new SetStringsPacket() { Data = hpids.GetData() }, PacketDeliveryMethod.ReliableOrdered, true);
                                var p = new Player(remote, game, auth.Guid);
                                peer.Tag = p;
                                Task.Run(() => p.DoAuthSuccess());
                                lock (game.ConnectedPlayers)
                                {
                                    game.ConnectedPlayers.Add(p);
                                }
                            }
                        }
                        else
                        {
                            var dw = new PacketWriter();
                            dw.Put("Invalid packet");
                            peer.Disconnect(dw);
                        }
                    }
                    else if (pkt is SetStringsPacket set)
                    {
                        hpidReader?.SetStrings(set.Data);
                    }
                    else if (pkt is AddStringPacket add)
                    {
                        hpidReader?.AddString(add.ToAdd);
                    }
                    else
                    {
                        var player = (Player) peer.Tag;
                        player.EnqueuePacket(pkt);
                    }
                }
                #if !DEBUG
                catch (Exception e)
                {
                    FLLog.Warning("Server", $"Error when reading packet {e}");
                    var dw = new NetDataWriter();
                    dw.Put("Packet processing error");
                    peer.Disconnect(dw);
                    if (peer.Tag is Player p)
                        p.Disconnected();
                }
                #endif
                finally
                {
                    msg.Recycle();
                }
            };
            listener.DeliveryEvent += (peer, data) =>
            {
                if (data is Action onAck)
                    onAck();
            };
            hpids.OnAddString += s =>
            {
                foreach (var p in Server.ConnectedPeerList.ToArray())
                {
                    if (p.Tag is Player player)
                    {
                        (player.Client as RemotePacketClient)?.SendPacket(new AddStringPacket() { ToAdd = s }, PacketDeliveryMethod.ReliableOrdered, true);
                    }
                }
            };
            broadcastServer.ReuseAddress = true;
            broadcastServer.IPv6Mode = IPv6Mode.SeparateSocket;
            broadcastServer.BroadcastReceiveEnabled = true;
            broadcastServer.UnsyncedEvents = true;
            broadcastServer.Start(LNetConst.BROADCAST_PORT);
            Server = new NetManager(listener);
            Server.IPv6Mode = IPv6Mode.SeparateSocket;
            Server.UnconnectedMessagesEnabled = true;
            Server.ChannelsCount = 3;
            Server.UnsyncedEvents = true;
            Server.Start(Port);
            FLLog.Info("Server", "Listening on port " + Port);
            var sw = Stopwatch.StartNew();
            var last = 0.0;
            ServerLoop sendLoop = null;
            sendLoop = new ServerLoop((time,_) =>
            {
                foreach (var p in Server.ConnectedPeerList.ToArray())
                {
                    if (p.Tag is Player player && p.ConnectionState == ConnectionState.Connected) 
                    {
                        player.ProcessPacketQueue();
                        (player.Client as RemotePacketClient)?.Update(time.TotalSeconds);
                    }
                }

                if (!running) sendLoop.Stop();
            });
            sendLoop.Start();
            http.Dispose();
            Server.Stop();
            broadcastServer.Stop();
        }
        
        void RequestPlayerGuid(NetPeer peer)
        {
            var msg = new PacketWriter();
            msg.Put((byte)1);
            Packets.Write(msg, new GuidAuthenticationPacket());
            peer.Send(msg, DeliveryMethod.ReliableOrdered);
			peer.Tag = TagConnecting;
		}

		public void Stop()
		{
			running = false;
			netThread.Join();
        }
	}
}
