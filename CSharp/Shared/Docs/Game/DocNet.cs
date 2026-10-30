using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Networking;
using HarmonyLib;
using ScpItdMod.Core;
using ScpItdMod.Net;

namespace ScpItdMod.Docs {
    public sealed class DocNet : IModule {
        public const string Channel = "scpitd.docs";

        private static readonly List<DocType> registered = new List<DocType>();

        public static void Register(DocType type) {
            if (type == null || registered.Exists(t => t.Id == type.Id)) { return; }
            registered.Add(type);
        }

#if SERVER
        private const string File = "docs.bin";

        private static DocStore store;
        private static bool frozen;
        private static DocServer server;
        private static readonly WireWriter reply = new WireWriter(1024);
        private static readonly WireWriter disk = new WireWriter(1 << 16);
        private static readonly List<DocPush> pushes = new List<DocPush>();
        private static double nextPrune;

        public static DocStore Store => store;
        internal static bool Writable => store != null && !frozen;

        public void Attach(Harmony harmony) {
            frozen = false;
            store = new DocStore();
            for (int i = 0; i < registered.Count; i++) { store.Register(registered[i]); }
            Load();
            server = new DocServer(store);
            nextPrune = 0;
            ModNet.ReceiveBytes(Channel, DocServer.MaxRequest, OnRequest);
            harmony.Patch(AccessTools.Method(typeof(GameSession), nameof(GameSession.EndRound)),
                postfix: ModuleHost.Bind(typeof(DocNet), nameof(EndRoundPostfix)));
        }

        public void RoundStart() {
            server?.Clear();
            nextPrune = 0;
        }

        public void Release() {
            Flush();
            server = null;
            store = null;
        }

        public static void EndRoundPostfix() => Flush();

        private static bool IsConnected(object session) {
            if (session is not Client client || client.Connection == null || GameMain.Server == null) { return false; }
            var clients = GameMain.Server.ConnectedClients;
            for (int i = 0; i < clients.Count; i++) {
                if (ReferenceEquals(clients[i], client)) { return true; }
            }
            return false;
        }

        private static Actor CurrentActor(object session) {
            if (!IsConnected(session)) { return default; }
            Client client = (Client)session;
            return new Actor(Access.Id(client), Access.Roles(client), Access.Gm(client));
        }

        private static void OnRequest(Client sender, uint nonce, uint seq, byte[] payload) {
            if (server == null || !IsConnected(sender)) { return; }
            double now = Timing.TotalTime;
            if (now >= nextPrune) {
                nextPrune = now + 30.0;
                server.Prune(IsConnected);
            }
            var actor = CurrentActor(sender);
            if (!server.Handle(actor, sender, nonce, seq, payload, now, CurrentActor, reply, pushes)) { return; }
            ModNet.SendBytes(Channel, sender.Connection, reply.Span, DeliveryMethod.Reliable);
            if (pushes.Count == 0 || GameMain.Server == null) { return; }
            var clients = GameMain.Server.ConnectedClients;
            for (int p = 0; p < pushes.Count; p++) {
                for (int c = 0; c < clients.Count; c++) {
                    if (!ReferenceEquals(clients[c], pushes[p].Session) || clients[c].Connection == null) { continue; }
                    ModNet.SendBytes(Channel, clients[c].Connection, pushes[p].Payload, DeliveryMethod.Reliable);
                    break;
                }
            }
            pushes.Clear();
        }

        private static string Path() => ModStorage.ReadPath(SaveUtil.DefaultSaveFolder, File);

        private static void Load() {
            string path = Path();
            int orphans = 0;
            DocStore loaded = null;
            string used = DocFile.Read(path, data => {
                var fresh = new DocStore();
                for (int i = 0; i < registered.Count; i++) { fresh.Register(registered[i]); }
                DocError result = fresh.Load(data, out orphans);
                if (result == DocError.Ok) { loaded = fresh; }
                return result;
            }, out DocError last);
            if (used != null) {
                store = loaded;
                if (used != path) { ModLog.Error($"documents: main file rejected, loaded the backup {used}"); }
                if (orphans > 0) { ModLog.Message($"documents: {orphans} kept without a registered type"); }
                return;
            }
            if (System.IO.File.Exists(path) || System.IO.File.Exists(path + ".bak")) {
                frozen = true;
                ModLog.Error($"documents: {path} cannot be loaded ({last}); saving is disabled to keep it");
            }
        }

        public static bool Flush() {
            if (!Writable) { return false; }
            try {
                store.Save(disk);
                string root = DocFile.WriteStored(ModStorage.For(SaveUtil.DefaultSaveFolder), File, disk.Span, () => Timing.TotalTime);
                if (ModStorage.Moved(root)) { ModLog.Error($"{File}: the usual folder refuses writes, saving to {root}"); }
                return true;
            } catch (Exception error) {
                ModLog.Error($"documents cannot be saved: {error.Message}");
                return false;
            }
        }
#else
        public delegate void Answer(DocError result, byte[] payload);

        private static readonly Dictionary<ulong, Answer> waiting = new Dictionary<ulong, Answer>();
        private const int MaxWaiting = 128;

        private static readonly WireWriter request = new WireWriter(256);
        private static readonly List<ulong> stale = new List<ulong>();
        private static ulong nextReq;

        public static event Action<DocError, byte[]> Pushed;

        public void Attach(Harmony harmony) => ModNet.ReceiveBytes(Channel, 1 << 20, OnReply);

        public void RoundStart() => waiting.Clear();

        public void Release() {
            waiting.Clear();
            Pushed = null;
        }

        public static void Send(DocOp op, Action<WireWriter> body, Answer answer) {
            request.Clear();
            request.U8(DocServer.Proto);
            request.U8((byte)op);
            ulong req = ++nextReq;
            request.Var(req);
            body?.Invoke(request);
            if (answer != null) {
                if (waiting.Count >= MaxWaiting) { Forget(req); }
                waiting[req] = answer;
            }
            ModNet.SendBytes(Channel, request.Span, DeliveryMethod.Reliable);
        }

        private static void Forget(ulong req) {
            stale.Clear();
            foreach (ulong key in waiting.Keys) {
                if (key + MaxWaiting / 2 < req) { stale.Add(key); }
            }
            for (int i = 0; i < stale.Count; i++) { waiting.Remove(stale[i]); }
        }

        private static void OnReply(byte[] payload) {
            var reader = new WireReader(payload);
            if (reader.U8() != DocServer.Proto) { return; }
            ulong req = reader.Var();
            reader.U8();
            var result = (DocError)reader.U8();
            if (reader.Failed) { return; }
            int header = payload.Length - reader.Remaining;
            byte[] body = payload.AsSpan(header).ToArray();
            if (req == 0) {
                Pushed?.Invoke(result, body);
                return;
            }
            if (waiting.Remove(req, out Answer answer)) { answer(result, body); }
        }
#endif
    }
}
