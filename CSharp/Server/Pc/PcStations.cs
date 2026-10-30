using ScpItdMod.Chess;
using ScpItdMod.Durak;
using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Networking;
using Microsoft.Xna.Framework;
using ScpItdMod.Auth;
using ScpItdMod.Core;
using ScpItdMod.Net;

namespace ScpItdMod.Pc {
    public static class PcStations {
        private sealed class Entry {
            public Item Item;
            public Client User;
            public readonly Station State = new Station();
            public uint Epoch, Revision, SentState, SentGame;
            public double NextResync;
            public bool Powered;
            public HashSet<Client> Viewers = new HashSet<Client>(), Next = new HashSet<Client>();
        }

        private static readonly Dictionary<ushort, Entry> entries = new Dictionary<ushort, Entry>();
        private static readonly List<ushort> removed = new List<ushort>();
        private static readonly WireWriter writer = new WireWriter(DisplayWire.MaxReply);
        private static readonly WireWriter game = new WireWriter(DisplayWire.MaxReply - 32);
        private static uint epoch = (uint)System.Security.Cryptography.RandomNumberGenerator.GetInt32(1, int.MaxValue);
        private static int terminals, boards, printers;
        private static double nextTick;

        public static void Attach() { terminals = Tagged.Watch(PcHost.TerminalTag); boards = Tagged.Watch(Chess.ChessWorld.Tag); printers = Tagged.Watch(PrinterWorld.Tag); }

        private static bool Connected(Client client) {
            if (client?.Connection == null || GameMain.Server == null) { return false; }
            var clients = GameMain.Server.ConnectedClients;
            for (int i = 0; i < clients.Count; i++) { if (ReferenceEquals(clients[i], client)) { return true; } }
            return false;
        }

        private static Entry Find(Item item) {
            if (entries.TryGetValue(item.ID, out Entry entry) && ReferenceEquals(entry.Item, item)) { return entry; }
            if (entry != null) { ChessHost.RemoveTable(entry.Item); }
            if (++epoch == 0) { epoch++; }
            entry = new Entry { Item = item, Epoch = epoch };
            entries[item.ID] = entry;
            return entry;
        }
        public static uint Epoch(Item item) => Find(item).Epoch;

        public static PcError Use(Client client, ushort id) {
            if (client == null || Access.Id(client) == 0 || !(Entity.FindEntityByID(id) is Item item) || !PcWorld.CanUse(client.Character, item)) { return PcError.Far; }
            if (!SwipeWorld.Powered(item)) { return PcError.NoPower; }
            Entry entry = Find(item);
            if (entry.User != null && (entry.State.Expired(Timing.TotalTime) || !PcWorld.CanUse(entry.User.Character, item) || !Connected(entry.User))) {
                entry.User = null; entry.State.Release();
            }
            if (entry.User != null && !ReferenceEquals(entry.User, client) || !entry.State.Use(Access.Id(client), Timing.TotalTime)) { return PcError.Busy; }
            entry.User = client;
            return PcError.Ok;
        }

        public static void Leave(Client client, ushort id) {
            if (entries.TryGetValue(id, out Entry entry) && ReferenceEquals(entry.User, client)) {
                entry.User = null;
                entry.State.Release();
            }
        }

        public static bool Present(Client client, ushort id, PcApp app) {
            if (Use(client, id) != PcError.Ok || app > PcApp.Meteo) { return false; }
            uint room = app == PcApp.Durak ? DurakHost.Room(Access.Id(client)) : app == PcApp.Chess ? ChessHost.Room(Access.Id(client)) : 0;
            return entries[id].State.Present(Access.Id(client), app, room);
        }
        public static bool ViewCamera(Client client, ushort id, ushort camera, double time, byte speed, bool live, bool playing) =>
            entries.TryGetValue(id, out Entry entry) && (camera == 0 || Rec.CamHost.CanView(client, camera)) &&
            entry.State.ViewCamera(Access.Id(client), camera, time, speed, live, playing, Timing.TotalTime);

        public static bool CameraSource(Client viewer, ushort id, out ushort camera, out double time) {
            camera = 0; time = 0;
            if (!entries.TryGetValue(id, out Entry entry) || entry.Item.Removed || !SwipeWorld.Powered(entry.Item) || entry.State.App != PcApp.Cameras || entry.State.Camera == 0 ||
                viewer?.Character == null || viewer.Character.Removed || Vector2.DistanceSquared(viewer.Character.WorldPosition, entry.Item.WorldPosition) > 1800 * 1800) { return false; }
            camera = entry.State.Camera; time = entry.State.CameraTime(Timing.TotalTime); return true;
        }

        public static void Tick() {
            if (GameMain.Server == null || Timing.TotalTime < nextTick) { return; }
            nextTick = Timing.TotalTime + 0.25;
            IReadOnlyList<Item> items = Tagged.Of(terminals);
            for (int i = 0; i < items.Count; i++) { Find(items[i]); }
            items = Tagged.Of(boards);
            for (int i = 0; i < items.Count; i++) { Find(items[i]); }
            items = Tagged.Of(printers);
            for (int i = 0; i < items.Count; i++) { Find(items[i]); }
            removed.Clear();
            foreach (var pair in entries) {
                Entry entry = pair.Value; Item item = entry.Item;
                bool board = Chess.ChessWorld.IsBoard(item);
                bool printer = item.HasTag(PrinterWorld.Tag);
                if (item.Removed || !board && !printer && !item.HasTag(PcHost.TerminalTag)) { ChessHost.RemoveTable(item); removed.Add(pair.Key); continue; }
                bool powered = SwipeWorld.Powered(item);
                if (!powered && !board && !printer) { entry.User = null; entry.State.Stop(); }
                if (entry.User != null && (entry.State.Expired(Timing.TotalTime) || !powered || !PcWorld.CanUse(entry.User.Character, item) || !Connected(entry.User))) {
                    entry.User = null; entry.State.Release();
                }
                Station state = entry.State;
                if (entry.User != null && (state.App == PcApp.Durak || state.App == PcApp.Chess)) {
                    ulong owner = Access.Id(entry.User);
                    uint activeRoom = state.App == PcApp.Durak ? DurakHost.Room(owner) : ChessHost.Room(owner);
                    state.Present(owner, state.App, activeRoom);
                }
                PcApp app = board ? PcApp.Chess : printer ? PcApp.Printers : state.App;
                uint room = board ? ChessHost.TableRoom(item) : state.Room;
                PrinterView printing = printer ? PcHost.Printer(item) : default;
                uint revision = printer ? printing.Key : app == PcApp.Durak ? DurakHost.PublicRevision(room) : app == PcApp.Chess ? ChessHost.PublicRevision(room) : 0;
                if (entry.User == null && !board && !printer && room != 0 && revision == 0) { state.Stop(); app = PcApp.Desktop; room = 0; }
                bool changed = entry.SentState != state.Revision || entry.SentGame != revision || entry.Powered != powered;
                if (changed || entry.Revision == 0) { if (++entry.Revision == 0) { entry.Revision++; } }
                bool resync = DisplayCadence.Due(Timing.TotalTime, entry.NextResync);
                if (resync) { entry.NextResync = DisplayCadence.Next(Timing.TotalTime); }
                bool encoded = false;
                var clients = GameMain.Server.ConnectedClients;
                for (int i = 0; i < clients.Count; i++) {
                    Client client = clients[i]; Character character = client.Character;
                    if (client.Connection == null || character == null || character.Removed ||
                        Vector2.DistanceSquared(character.WorldPosition, item.WorldPosition) > 1800 * 1800 && !Rec.CamHost.Observes(client, item)) { continue; }
                    entry.Next.Add(client);
                    if (!DisplayCadence.ShouldSend(changed, !entry.Viewers.Contains(client), resync)) { continue; }
                    if (!encoded) {
                        game.Clear();
                        if (printer) { printing.Write(game); }
                        if ((powered || board) && room != 0) {
                            if (app == PcApp.Durak) { DurakHost.Public(game, room, entry.Revision); }
                            else if (app == PcApp.Chess) { ChessHost.Public(game, room, entry.Revision); }
                        }
                        DisplayWire.Write(writer, new DisplayState(item.ID, entry.Epoch, entry.Revision, app, powered, state.Owner != 0), game.Span);
                        encoded = true;
                    }
                    ModNet.SendBytes(DisplayWire.Channel, client.Connection, writer.Span, DeliveryMethod.Reliable);
                }
                HashSet<Client> old = entry.Viewers; entry.Viewers = entry.Next; entry.Next = old; entry.Next.Clear();
                entry.SentState = state.Revision; entry.SentGame = revision; entry.Powered = powered;
            }
            for (int i = 0; i < removed.Count; i++) { entries.Remove(removed[i]); }
        }

        public static void Clear() { entries.Clear(); removed.Clear(); nextTick = 0; }
    }
}
