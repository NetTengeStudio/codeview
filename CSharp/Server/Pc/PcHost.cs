using ScpItdMod.Chess;
using ScpItdMod.Durak;
using System;
using System.Collections.Generic;
using System.Globalization;
using FarseerPhysics;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.Networking;
using HarmonyLib;
using Microsoft.Xna.Framework;
using ScpItdMod.Auth;
using ScpItdMod.Core;
using ScpItdMod.Docs;
using ScpItdMod.Net;
using ScpItdMod.Paper;
using ScpItdMod.People;

namespace ScpItdMod.Pc {
    public static class PcHost {
        public const float Reach = PcWorld.Reach;
        public const float PrinterRange = 1600.0f;
        public const double StepSeconds = 0.25;
        private static readonly Guid serverIdentity = Guid.NewGuid();
        private const string HostPrefix = "scpitd_host_";
        private const string LocalPrefix = "scpitd_vfs_";

        public static readonly Identifier TerminalTag = PcWire.TerminalTag.ToIdentifier();
        public static readonly Identifier PrinterTag = PcWire.PrinterTag.ToIdentifier();

        private sealed class Peer {
            public readonly RequestSessions Requests = new RequestSessions();
            public Gate Gate;
        }

        private static readonly Dictionary<ulong, Peer> peers = new Dictionary<ulong, Peer>();
        private static readonly WireWriter reply = new WireWriter(8192);
        private static readonly PrintQueue queue = new PrintQueue();
        private static readonly PcPlace place = new PcPlace();
        private static readonly List<(string, string)> subjects = new List<(string, string)>();
        private static readonly Dictionary<string, string> names = new Dictionary<string, string>(StringComparer.Ordinal);
        private static PcCore core;
        private static DocStore coreStore;
        private static int terminals = -1;
        private static int printers = -1;
        private static uint nextLocal = 1;
        private static double nextStep;
        private static bool migrated;

        public static void Attach(Harmony harmony) {
            DocNet.Register(PcCore.Node());
            DocNet.Register(PcCore.Dossier());
            terminals = Tagged.Watch(TerminalTag);
            printers = Tagged.Watch(PrinterTag);
            ModNet.ReceiveBytes(PcWire.Channel, PcLimits.MaxRequest, OnRequest);
            PcStations.Attach();
            DurakHost.Attach();
            ChessHost.Attach();
            harmony.Patch(AccessTools.Method(typeof(GameServer), nameof(GameServer.Update)),
                postfix: ModuleHost.Bind(typeof(PcHost), nameof(Tick)));
            ModCommands.Register("scpitd_pc", "server: computers, disks and the print queue", _ => Report());
        }

        public static void RoundStart() {
            core?.ClearRequests();
            DurakHost.Clear();
            ChessHost.Clear();
            PcStations.Clear();
            peers.Clear();
            queue.Clear();
        }

        public static void Release() {
            RoundStart();
            core = null;
            coreStore = null;
        }

        private static PcCore Core() {
            DocStore store = DocNet.Store;
            if (store == null) { return null; }
            if (core == null || !ReferenceEquals(coreStore, store)) {
                core = new PcCore(store, queue) { SubjectName = SubjectName, Subjects = Subjects };
                coreStore = store;
                if (!migrated) {
                    migrated = true;
                    PcMigrate.Run(core, Timing.TotalTime);
                }
            }
            return core;
        }

        public static PcError Publish(Item computer, string folder, string name, string body, double now) {
            PcCore c = Core();
            return c == null ? PcError.Invalid : c.Publish(DiskOf(computer), folder, name, body, now, out _);
        }

        private static string SubjectName(string key) {
            foreach (PeopleHost.Record r in PeopleHost.Live.Values) {
                if (Key(r) == key) {
                    string n = Shown(r);
                    names[key] = n;
                    return n;
                }
            }
            return names.TryGetValue(key, out string known) ? known : "Нет в списках смены";
        }

        private static string Key(PeopleHost.Record r) => r.Account.ToString(CultureInfo.InvariantCulture) + ":" + r.Profile.ToString(CultureInfo.InvariantCulture);

        private static string Shown(PeopleHost.Record r) => r.Hidden ? r.Alias : r.Character?.Info?.Name ?? r.Alias ?? "";

        private static IReadOnlyList<(string, string)> Subjects() {
            subjects.Clear();
            foreach (PeopleHost.Record r in PeopleHost.Live.Values) {
                if (r.Character == null || r.Character.Removed || r.Profile == 0) { continue; }
                subjects.Add((Key(r), Shown(r)));
                if (subjects.Count >= 64) { break; }
            }
            subjects.Sort((a, b) => string.Compare(a.Item2, b.Item2, StringComparison.CurrentCultureIgnoreCase));
            return subjects;
        }

        public static string DiskOf(Item computer) {
            foreach (Identifier tag in computer.GetTags()) {
                string v = tag.Value;
                if (v.StartsWith(HostPrefix, StringComparison.OrdinalIgnoreCase)) { return "host:" + v.Substring(HostPrefix.Length).ToLowerInvariant(); }
            }
            foreach (Identifier tag in computer.GetTags()) {
                string v = tag.Value;
                if (v.StartsWith(LocalPrefix, StringComparison.OrdinalIgnoreCase)) { return "local:" + v.Substring(LocalPrefix.Length); }
            }
            PcCore c = Core();
            while (c != null && c.DiskNodes("local:" + nextLocal.ToString(CultureInfo.InvariantCulture)) > 0) { nextLocal++; }
            string id = nextLocal.ToString(CultureInfo.InvariantCulture);
            nextLocal++;
            computer.AddTag((LocalPrefix + id).ToIdentifier());
            return "local:" + id;
        }

        private static PrinterState StateOf(Item printer) {
            if (printer == null || printer.Removed) { return PrinterState.Gone; }
            if (!SwipeWorld.Powered(printer) || printer.ParentInventory != null) { return PrinterState.Off; }
            ItemContainer tray = printer.GetComponent<ItemContainer>();
            if (tray == null || BlankIn(tray) == null) { return PrinterState.NoPaper; }
            return queue.Head(printer.ID, PcStations.Epoch(printer))?.State == JobState.Printing ? PrinterState.Busy : PrinterState.Ready;
        }

        public static PrinterView Printer(Item printer) {
            PrintJob job = queue.Head(printer.ID, PcStations.Epoch(printer));
            return new PrinterView(StateOf(printer), job?.Printed ?? 0, job?.Pages.Length ?? 0, job == null ? 0 : queue.Progress(job, Timing.TotalTime));
        }

        private static Item BlankIn(ItemContainer tray) {
            foreach (Item item in tray.Inventory.AllItems) {
                if (PaperModule.IsPaper(item) && PaperHost.Blank(item)) { return item; }
            }
            return null;
        }

        private static void Fill(Item computer) {
            place.Computer = computer.ID; place.Server = serverIdentity; place.Epoch = PcStations.Epoch(computer);
            place.Name = computer.Name;
            place.Disk = DiskOf(computer);
            place.Powered = SwipeWorld.Powered(computer);
            place.ServerOnline = Meteo.ServerRacks.HostOnline(place.Disk);
            place.Printers.Clear();
            string host = place.Disk.StartsWith("host:", StringComparison.Ordinal) ? place.Disk : null;
            IReadOnlyList<Item> list = Tagged.Of(printers);
            for (int i = 0; i < list.Count && place.Printers.Count < 8; i++) {
                Item p = list[i];
                bool linked = host != null && DiskTag(p) == host ||
                    ReferenceEquals(p.Submarine, computer.Submarine) && Vector2.Distance(p.WorldPosition, computer.WorldPosition) <= PrinterRange;
                if (!linked) { continue; }
                place.Printers.Add(new PcPrinter { Id = p.ID, Epoch = PcStations.Epoch(p), Name = p.Name, State = StateOf(p) });
            }
        }

        private static string DiskTag(Item item) {
            foreach (Identifier tag in item.GetTags()) {
                string v = tag.Value;
                if (v.StartsWith(HostPrefix, StringComparison.OrdinalIgnoreCase)) { return "host:" + v.Substring(HostPrefix.Length).ToLowerInvariant(); }
            }
            return null;
        }

        private static PcActor ActorOf(Client sender, Character me) {
            Item card = SwipeWorld.Card(me);
            var cred = new Credentials();
            SwipeWorld.CredentialsOf(card, cred);
            return new PcActor {
                Id = Access.Id(sender), Profile = PeopleHost.Of(me)?.Profile ?? 0, Clearance = cred.Clearance, Card = card != null, Gm = Access.Gm(sender),
                Name = me.Info?.Name ?? me.Name
            };
        }

        private static void OnRequest(Client sender, uint nonce, uint seq, byte[] payload) {
            Character me = sender.Character;
            if (me == null || me.IsDead || me.Removed || !RpWorldConscious(me)) { return; }
            ulong account = Access.Id(sender);
            if (!peers.TryGetValue(account, out Peer peer)) {
                peer = new Peer();
                peers.Add(account, peer);
            }
            double now = Timing.TotalTime;
            if (!peer.Gate.Take(now, 12.0f, 24.0f)) { return; }
            if (!peer.Requests.Accept(sender, nonce, seq, out _)) { return; }
            var r = new WireReader(payload);
            if (!PcWire.ReadRequest(ref r, out PcOp op, out uint token, out ushort computerId)) { return; }
            PcCore c = Core();
            if (c == null) { return; }
            if (!(Entity.FindEntityByID(computerId) is Item computer) || computer.Removed || !computer.HasTag(TerminalTag)) { return; }
            reply.Clear();
            if (op == PcOp.Leave) {
                if (r.End) { PcStations.Leave(sender, computerId); }
                return;
            }
            PcError access = PcStations.Use(sender, computerId);
            if (access != PcError.Ok) {
                PcWire.Reply(reply, PcReply.Done, token, access);
                reply.Var(0);
            } else if (op == PcOp.Present) {
                PcApp app = (PcApp)r.U8();
                ushort camera = r.U16(); double time = BitConverter.Int64BitsToDouble((long)r.U64()); byte speed = r.U8(); bool live = r.Bool(), playing = r.Bool();
                if (r.Failed || !r.End || app > PcApp.Meteo || !double.IsFinite(time) || time < 0 || speed >= Rec.Playback.Speeds.Length) { return; }
                bool presented = PcStations.Present(sender, computerId, app);
                if (presented && app == PcApp.Cameras) { presented = PcStations.ViewCamera(sender, computerId, camera, time, speed, live, playing); }
                PcWire.Reply(reply, PcReply.Done, token, presented ? PcError.Ok : PcError.Denied);
                reply.Var(0);
            } else {
                Fill(computer);
                c.Handle(ActorOf(sender, me), place, op, token, ref r, now, reply, peer.Requests.Nonce);
            }
            if (reply.Length > 0) { ModNet.SendBytes(PcWire.Channel, sender.Connection, reply.Span, DeliveryMethod.Reliable); }
        }

        private static bool RpWorldConscious(Character c) => Rp.RpWorld.Conscious(c);

        public static void Tick() {
            Meteo.ServerRacks.Update(Timing.TotalTime);
            DurakHost.Tick();
            ChessHost.Tick();
            PcStations.Tick();
            double now = Timing.TotalTime;
            if (now < nextStep || queue.Jobs.Count == 0) { return; }
            nextStep = now + StepSeconds;
            IReadOnlyList<PrintJob> jobs = queue.Jobs;
            for (int i = 0; i < jobs.Count; i++) {
                PrintJob j = jobs[i];
                if (!PrintQueue.Active(j)) { continue; }
                Item printer = Entity.FindEntityByID(j.Printer) as Item;
                if (printer != null && !printer.HasTag(PrinterTag)) { printer = null; }
                PrinterState state = printer == null ? PrinterState.Gone : StateOf(printer);
                if (state == PrinterState.Busy) { state = PrinterState.Ready; }
                if (!queue.Step(j.Printer, printer == null ? 0 : PcStations.Epoch(printer), state, now, out PrintJob ready, out int page)) { continue; }
                Produce(printer, ready, page, now);
            }
            queue.Prune(now);
        }

        private static void Produce(Item printer, PrintJob job, int page, double now) {
            string body = job.Pages[page];
            ItemContainer tray = printer.GetComponent<ItemContainer>();
            Item blank = tray == null ? null : BlankIn(tray);
            if (blank == null) { return; }
            if (!PaperHost.Imprint(blank, job.Account, body, now)) {
                queue.Fail(job, PcError.Invalid, now);
                return;
            }
            blank.Drop(null);
            blank.SetTransform(printer.SimPosition + ConvertUnits.ToSimUnits(PrinterWorld.Output(printer) - printer.WorldPosition), -ItemPose.Angle(printer));
            queue.PagePrinted(job, now);
        }

        private static void Report() {
            int active = 0;
            foreach (PrintJob j in queue.Jobs) {
                if (PrintQueue.Active(j)) { active++; }
            }
            ModLog.Message($"computers {Tagged.Of(terminals).Count}, printers {Tagged.Of(printers).Count}, print jobs {queue.Jobs.Count} ({active} active), docs {DocNet.Store?.Count ?? 0}");
        }
    }
}
