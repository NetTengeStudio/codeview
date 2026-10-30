using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Networking;
using HarmonyLib;
using Microsoft.Xna.Framework;
using ScpItdMod.Auth;
using ScpItdMod.Core;
using ScpItdMod.Net;
using ScpItdMod.People;

namespace ScpItdMod.Rp {
    public static class RpHost {
        public const string Channel = "scpitd.rp";
        private const float OfferSeconds = 12.0f;
        private const float HideDrift = 40.0f;
        private const double StateSeconds = 0.25;
        private const double WatchSeconds = 0.25;

        private sealed class Peer {
            public readonly RequestSessions Requests = new RequestSessions();
            public Gate Gate;
        }

        private sealed class Offer {
            public uint Id;
            public Character From;
            public Character To;
            public Item Item;
            public double Until;
        }

        private sealed class EmoteRun {
            public ulong Id;
            public EmoteDef Def;
            public byte Variant;
            public double Start;
            public double End;
            public readonly HashSet<NetworkConnection> Observers = new HashSet<NetworkConnection>();
        }

        public static event Action<Character, EmoteDef, int> EmoteStarted;

        private static readonly Dictionary<Character, RpRun> running = new Dictionary<Character, RpRun>();
        private static readonly Dictionary<Character, EmoteRun> emoting = new Dictionary<Character, EmoteRun>();
        private static readonly Dictionary<(Character, byte), double> lastEmote = new Dictionary<(Character, byte), double>();
        private static readonly Dictionary<Character, Vector2> entries = new Dictionary<Character, Vector2>();
        private static readonly Dictionary<ulong, Peer> peers = new Dictionary<ulong, Peer>();
        private static readonly List<Offer> offers = new List<Offer>();
        private static readonly List<Character> finished = new List<Character>();
        private static readonly List<Character> expired = new List<Character>();
        private static readonly List<ushort> surrenderedIds = new List<ushort>();
        private static readonly List<ushort> hiddenIds = new List<ushort>();
        private static readonly List<Item> spill = new List<Item>();
        private static readonly WireWriter out_ = new WireWriter(256);
        private static readonly Random rng = new Random();
        private static uint nextOffer = 1;
        private static ulong nextEmote;
        private static bool stateDirty;
        private static double nextState;
        private static double nextWatch;

        public static EmoteCatalog Catalog { get; private set; } = EmoteCatalog.Parse("<Emotes/>", null);

        public static void Attach(Harmony harmony) {
            ModNet.ReceiveBytes(Channel, RpWire.MaxRequest, OnRequest);
            harmony.Patch(AccessTools.Method(typeof(GameServer), nameof(GameServer.Update)),
                postfix: ModuleHost.Bind(typeof(RpHost), nameof(Tick)));
        }

        internal static void LoadCatalog() => Catalog = EmoteData.Load();

        public static bool IsTargeted(Character c) {
            if (c == null) { return false; }
            foreach (RpRun run in running.Values) {
                if (run.Target == c.ID) { return true; }
            }
            return false;
        }

        public static bool HandsBusy(Character c) =>
            c != null && (running.TryGetValue(c, out RpRun r) && RpRules.Timed(r.Act) || SwipeHost.Busy(c) ||
                          emoting.TryGetValue(c, out EmoteRun e) && e.Def.Hands != EmoteHands.None);

        public static void RoundStart() {
            running.Clear();
            emoting.Clear();
            lastEmote.Clear();
            entries.Clear();
            offers.Clear();
            peers.Clear();
            RpWorld.Clear();
            stateDirty = true;
        }

        public static void Release() => RoundStart();

        private static void OnRequest(Client sender, uint nonce, uint seq, byte[] payload) {
            Character actor = sender.Character;
            ulong account = Access.Id(sender);
            if (!peers.TryGetValue(account, out Peer peer)) {
                peer = new Peer();
                peers.Add(account, peer);
            }
            if (!peer.Gate.Take(Timing.TotalTime, 6.0f, 12.0f)) { return; }
            if (!peer.Requests.Accept(sender, nonce, seq, out _)) { return; }

            var r = new WireReader(payload);
            if (r.U8() != RpWire.Proto) { return; }
            var msg = (RpMsg)r.U8();
            if (msg == RpMsg.Synchronize) {
                if (r.Failed || !r.End) { return; }
                RpWorld.CollectState(surrenderedIds, hiddenIds);
                RpWire.State(out_, surrenderedIds, hiddenIds, (uint)RpWorld.Revision);
                ModNet.SendBytes(Channel, sender.Connection, out_.Span, DeliveryMethod.Reliable);
                foreach (var pair in emoting) { Observe(pair.Key, pair.Value, sender, true); }
                return;
            }
            if (actor == null || actor.Removed) { return; }
            switch (msg) {
                case RpMsg.Act: {
                    var act = (RpAct)r.U8();
                    if (!RpWire.ReadEntity(ref r, out ushort targetId) || !RpWire.ReadEntity(ref r, out ushort itemId) || !r.End) { return; }
                    Begin(sender, actor, act, targetId == 0 ? null : Entity.FindEntityByID(targetId), itemId == 0 ? null : Entity.FindEntityByID(itemId) as Item);
                    break;
                }
                case RpMsg.Cancel:
                    if (!r.End) { return; }
                    if (running.TryGetValue(actor, out RpRun run)) { End(actor, run, RpEnd.Cancelled, RpDeny.None); }
                    if (emoting.ContainsKey(actor)) { StopEmote(actor, EmoteDeny.None); }
                    break;
                case RpMsg.Answer: {
                    uint id = (uint)r.Count(int.MaxValue);
                    bool accept = r.Bool();
                    if (r.Failed || !r.End) { return; }
                    Answer(actor, id, accept);
                    break;
                }
                case RpMsg.Emote: {
                    byte index = r.U8();
                    ReadOnlySpan<byte> catalog = r.Bytes(32);
                    if (r.Failed || !r.End) { return; }
                    EmoteTrace.Log("server recv", "emote #" + index + " from " + sender.Name);
                    if (!catalog.SequenceEqual(Catalog.Fingerprint)) {
                        EmoteTrace.Log("server deny", "client catalog differs (server has " + Catalog.Count + " emotes)");
                        Send(sender, w => RpWire.Note(w, "Каталоги эмоций клиента и сервера различаются. Обновите мод."));
                        return;
                    }
                    StartEmote(sender, actor, Catalog[index]);
                    break;
                }
            }
        }

        private static EmoteFacts EmoteFactsOf(Character actor, EmoteDef def) {
            double since = def != null && lastEmote.TryGetValue((actor, def.Index), out double last) ? Timing.TotalTime - last : double.MaxValue;
            bool right = actor.Inventory?.GetItemInLimbSlot(InvSlotType.RightHand) != null;
            bool left = actor.Inventory?.GetItemInLimbSlot(InvSlotType.LeftHand) != null;
            return new EmoteFacts {
                Alive = !actor.IsDead && !actor.Removed,
                Conscious = RpWorld.Conscious(actor),
                Bound = actor.LockHands,
                HandBusy = (running.TryGetValue(actor, out RpRun r) && RpRules.Timed(r.Act)) || SwipeHost.Busy(actor),
                BothHandsBusy = right && left,
                AnyHandBusy = right || left,
                Muted = actor.SpeechImpediment >= 100.0f,
                Hidden = RpWorld.IsHidden(actor),
                Emoting = emoting.ContainsKey(actor),
                Vitality = actor.MaxVitality > 0.0f ? actor.Vitality / actor.MaxVitality : 1.0f,
                SinceLast = since
            };
        }

        private static void StartEmote(Client sender, Character actor, EmoteDef def) {
            EmoteDeny deny = EmoteRules.Check(def, EmoteFactsOf(actor, def));
            if (deny != EmoteDeny.None) {
                EmoteTrace.Log("server deny", (def?.Id ?? "?") + ": " + deny);
                Send(sender, w => RpWire.Note(w, EmoteRules.Explain(deny)));
                return;
            }
            string gender = Gender(actor);
            int variant = EmoteRules.Variant(def, gender, (uint)rng.Next());
            double now = Timing.TotalTime;
            float total = Poses.Total(def.Pose, def.Duration(variant));
            var run = new EmoteRun { Id = ++nextEmote, Def = def, Variant = (byte)variant, Start = now, End = now + total };
            emoting[actor] = run;
            lastEmote[(actor, def.Index)] = now;
            if (GameMain.Server != null) {
                foreach (Client client in GameMain.Server.ConnectedClients) { Observe(actor, run, client, false); }
            }
            EmoteTrace.Log("server start", def.Id + " run " + run.Id + " for " + actor.Name + ", " + (GameMain.Server?.ConnectedClients.Count ?? 0) + " clients");
            EmoteStarted?.Invoke(actor, def, variant);
        }

        private static string Gender(Character c) {
            var tags = c.Info?.Head?.Preset?.TagSet;
            if (tags == null) { return null; }
            foreach (Identifier t in tags) {
                if (t == "female") { return "female"; }
                if (t == "male") { return "male"; }
            }
            return null;
        }

        private static void StopEmote(Character actor, EmoteDeny why) {
            if (!emoting.Remove(actor, out EmoteRun run)) { return; }
            RpWire.EmoteStop(out_, actor.ID, run.Id, why);
            foreach (NetworkConnection observer in run.Observers) {
                ModNet.SendBytes(Channel, observer, out_.Span, DeliveryMethod.Reliable);
            }
        }

        private static void Begin(Client sender, Character actor, RpAct act, Entity target, Item item) {
            if (act == RpAct.Examine || act == RpAct.None) { return; }
            if (RpRules.OnSelf(act)) { target = actor; }
            if (act != RpAct.Give && act != RpAct.Present && act != RpAct.Detain) { item = null; }
            if (act == RpAct.Detain && item == null) { item = RpWorld.HeldCuffs(actor); }
            if (act == RpAct.Detain && !RpWorld.IsCuffs(item)) { item = null; }
            bool busy = running.ContainsKey(actor) || SwipeHost.Busy(actor) || (RpRules.Timed(act) && emoting.TryGetValue(actor, out EmoteRun e) && e.Def.Hands != EmoteHands.None);
            RpFacts facts = RpWorld.Facts(actor, RpRules.OnSelf(act) ? null : target, item, busy);
            if (act == RpAct.Leave || act == RpAct.Surrender) { facts.HasTarget = true; }
            RpDeny deny = RpRules.Check(act, facts);
            if (deny != RpDeny.None) {
                Send(sender, w => RpWire.Ended(w, act, RpEnd.Denied, deny));
                return;
            }
            if (act == RpAct.Surrender) {
                RpWorld.SetSurrendered(actor, !RpWorld.IsSurrendered(actor));
                stateDirty = true;
                Send(sender, w => RpWire.Ended(w, act, RpEnd.Done, RpDeny.None));
                return;
            }
            if (act == RpAct.Introduce) {
                bool learned = target is Character listener && PeopleHost.Introduce(actor, listener);
                Send(sender, w => RpWire.Note(w, learned ? "Вы представились." : "Этот человек уже знает ваше имя."));
                if (learned) { Note((Character)target, $"Вам представились: {PeopleHost.Of(actor)?.Real ?? actor.Name}."); }
                return;
            }
            if (act == RpAct.Give) {
                MakeOffer(actor, (Character)target, item);
                return;
            }
            if (act == RpAct.Present) {
                SendTo((Character)target, w => RpWire.Shown(w, actor.ID, item.ID));
                Send(sender, w => RpWire.Ended(w, act, RpEnd.Done, RpDeny.None));
                return;
            }
            float duration = act == RpAct.Search && target is Character searched ? SearchRules.Duration(RpWorld.StateOf(searched)) : RpRules.Duration(act);
            var run = new RpRun {
                Act = act, Actor = actor.ID, Target = target?.ID ?? 0, Item = item?.ID ?? 0,
                Start = Timing.TotalTime, Duration = duration
            };
            running[actor] = run;
            if (emoting.TryGetValue(actor, out EmoteRun handEmote) && handEmote.Def.Hands != EmoteHands.None) { StopEmote(actor, EmoteDeny.HandsBusy); }
            Send(sender, w => RpWire.Started(w, act, actor.ID, run.Target, run.Duration));
            if (target is Character victim && !ReferenceEquals(victim, actor)) {
                SendTo(victim, w => RpWire.Started(w, act, actor.ID, victim.ID, run.Duration));
            }
            if (act == RpAct.Hide) { entries[actor] = actor.WorldPosition; }
        }

        public static void Tick() {
            double now = Timing.TotalTime;
            finished.Clear();
            foreach (KeyValuePair<Character, RpRun> pair in running) {
                Character actor = pair.Key;
                RpRun run = pair.Value;
                Entity target = run.Target == 0 ? null : Entity.FindEntityByID(run.Target);
                Item item = run.Item == 0 ? null : Entity.FindEntityByID(run.Item) as Item;
                RpFacts facts = RpWorld.Facts(actor, RpRules.OnSelf(run.Act) ? null : target, item, false);
                if (RpRules.OnSelf(run.Act)) { facts.HasTarget = true; }
                if (actor.Removed || RpRules.Interrupted(run.Act, facts)) {
                    finished.Add(actor);
                    End(actor, run, RpEnd.Interrupted, RpRules.Check(run.Act, facts), defer: true);
                    continue;
                }
                if (run.Finished(now)) {
                    finished.Add(actor);
                    Complete(actor, run, target, item, now);
                }
            }
            for (int i = 0; i < finished.Count; i++) { running.Remove(finished[i]); }

            finished.Clear();
            foreach (KeyValuePair<Character, EmoteRun> pair in emoting) {
                if (now >= pair.Value.End || pair.Key.Removed) {
                    finished.Add(pair.Key);
                    continue;
                }
                if (EmoteRules.Interrupted(pair.Value.Def, EmoteFactsOf(pair.Key, pair.Value.Def))) { finished.Add(pair.Key); }
            }
            for (int i = 0; i < finished.Count; i++) {
                Character c = finished[i];
                if (emoting.TryGetValue(c, out EmoteRun er)) { StopEmote(c, now < er.End ? EmoteDeny.Unconscious : EmoteDeny.None); }
            }

            for (int i = offers.Count - 1; i >= 0; i--) {
                Offer offer = offers[i];
                bool gone = offer.Item.Removed || !RpWorld.Owns(offer.From, offer.Item) || offer.To.Removed || offer.To.IsDead;
                if (now <= offer.Until && !gone) { continue; }
                offers.RemoveAt(i);
                SendTo(offer.From, w => RpWire.Ended(w, RpAct.Give, gone ? RpEnd.Interrupted : RpEnd.Expired, RpDeny.None));
            }

            if (now >= nextWatch) {
                nextWatch = now + WatchSeconds;
                if (GameMain.Server != null) {
                    foreach (var pair in emoting) {
                        foreach (Client client in GameMain.Server.ConnectedClients) { Observe(pair.Key, pair.Value, client, false); }
                    }
                }
                KeepHidden();
                DropStaleSurrender();
                RpWorld.ExpirePermits(expired);
                for (int i = 0; i < expired.Count; i++) {
                    Character searcher = expired[i];
                    SendTo(searcher, w => RpWire.Permit(w, 0, 0.0f));
                }
            }
            if (stateDirty && now >= nextState) {
                stateDirty = false;
                nextState = now + StateSeconds;
                BroadcastState();
            }
        }

        private static void Complete(Character actor, RpRun run, Entity target, Item item, double now) {
            RpEnd end = RpEnd.Done;
            switch (run.Act) {
                case RpAct.Search:
                    if (target is Character searched) {
                        RpWorld.Grant(actor, searched, now + SearchRules.PermitSeconds);
                        SendTo(actor, w => RpWire.Permit(w, searched.ID, SearchRules.PermitSeconds));
                        Note(searched, "Вас обыскивают.");
                    }
                    break;
                case RpAct.Detain:
                    if (!(target is Character bound) || !Cuff(actor, bound, item)) { end = RpEnd.Interrupted; }
                    break;
                case RpAct.Free:
                    if (!(target is Character freed) || !Uncuff(actor, freed)) { end = RpEnd.Interrupted; }
                    break;
                case RpAct.Hide:
                    if (target is Item hideout && RpWorld.IsHideout(hideout) && !RpWorld.Occupied(hideout, actor)) {
                        RpWorld.SetHidden(actor, hideout);
                        RpWorld.SetSurrendered(actor, false);
                        actor.TeleportTo(hideout.WorldPosition);
                        StopEmote(actor, EmoteDeny.Hidden);
                        stateDirty = true;
                    } else {
                        end = RpEnd.Interrupted;
                    }
                    break;
                case RpAct.Leave:
                    Reveal(actor, false);
                    break;
                case RpAct.Diagnose:
                    if (target is Character patient) { Med.MedHost.Diagnose(actor, patient); } else { end = RpEnd.Interrupted; }
                    break;
            }
            End(actor, run, end, RpDeny.None, defer: true);
        }

        private static bool Cuff(Character actor, Character target, Item cuffs) {
            cuffs ??= RpWorld.HeldCuffs(actor);
            if (!RpWorld.IsCuffs(cuffs) || !RpWorld.Owns(actor, cuffs) || target.Inventory == null || target.LockHands) { return false; }
            spill.Clear();
            foreach (Item held in target.HeldItems) {
                if (!held.HasTag(RpWorld.CuffTag)) { spill.Add(held); }
            }
            for (int i = 0; i < spill.Count; i++) {
                Item held = spill[i];
                if (!target.Inventory.TryPutItem(held, target, new List<InvSlotType> { InvSlotType.Any })) { held.Drop(target); }
            }
            spill.Clear();
            if (!target.Inventory.TryPutItem(cuffs, actor, cuffs.AllowedSlots)) { return false; }
            RpWorld.SetSurrendered(target, false);
            stateDirty = true;
            Note(target, "Вас сковали наручниками.");
            return true;
        }

        private static bool Uncuff(Character actor, Character target) {
            Item cuffs = RpWorld.BoundCuffs(target);
            if (cuffs == null) { return false; }
            if (!actor.Inventory.TryPutItem(cuffs, actor, CharacterInventory.AnySlot)) { cuffs.Drop(target); }
            Note(target, "С вас сняли наручники.");
            return true;
        }

        private static void MakeOffer(Character from, Character to, Item item) {
            for (int i = offers.Count - 1; i >= 0; i--) {
                if (ReferenceEquals(offers[i].From, from)) { offers.RemoveAt(i); }
            }
            var offer = new Offer { Id = nextOffer++, From = from, To = to, Item = item, Until = Timing.TotalTime + OfferSeconds };
            offers.Add(offer);
            SendTo(to, w => RpWire.Offer(w, offer.Id, from.ID, item.Name));
            SendTo(from, w => RpWire.Note(w, $"Вы протягиваете: {item.Name}. Ждём ответа."));
        }

        private static void Answer(Character responder, uint id, bool accept) {
            int index = -1;
            for (int i = 0; i < offers.Count; i++) {
                if (offers[i].Id == id && ReferenceEquals(offers[i].To, responder)) {
                    index = i;
                    break;
                }
            }
            if (index < 0) { return; }
            Offer offer = offers[index];
            offers.RemoveAt(index);
            if (!accept) {
                SendTo(offer.From, w => RpWire.Ended(w, RpAct.Give, RpEnd.Declined, RpDeny.None));
                return;
            }
            RpFacts facts = RpWorld.Facts(offer.From, responder, offer.Item, false);
            RpDeny deny = RpRules.Check(RpAct.Give, facts);
            if (deny == RpDeny.None && responder.Inventory.TryPutItem(offer.Item, responder, CharacterInventory.AnySlot)) {
                SendTo(offer.From, w => RpWire.Ended(w, RpAct.Give, RpEnd.Done, RpDeny.None));
                SendTo(responder, w => RpWire.Note(w, $"Вы взяли: {offer.Item.Name}."));
            } else {
                RpDeny why = deny == RpDeny.None ? RpDeny.NoRoom : deny;
                SendTo(offer.From, w => RpWire.Ended(w, RpAct.Give, RpEnd.Interrupted, why));
                SendTo(responder, w => RpWire.Note(w, "Взять не получилось: " + RpRules.Explain(why)));
            }
        }

        private static void KeepHidden() {
            if (RpWorld.Hidden.Count == 0) { return; }
            List<Character> reveal = null;
            foreach (KeyValuePair<Character, Item> pair in RpWorld.Hidden) {
                Character c = pair.Key;
                Item hideout = pair.Value;
                bool found = false;
                foreach (Character other in Character.CharacterList) {
                    if (!ReferenceEquals(other, c) && ReferenceEquals(other.SelectedItem, hideout)) {
                        found = true;
                        break;
                    }
                }
                if (c.Removed || c.IsDead || hideout == null || hideout.Removed || found) {
                    (reveal ??= new List<Character>()).Add(c);
                    continue;
                }
                if (Vector2.DistanceSquared(c.WorldPosition, hideout.WorldPosition) > HideDrift * HideDrift) { c.TeleportTo(hideout.WorldPosition); }
            }
            if (reveal == null) { return; }
            for (int i = 0; i < reveal.Count; i++) { Reveal(reveal[i], true); }
        }

        private static void Reveal(Character c, bool found) {
            if (!RpWorld.IsHidden(c)) { return; }
            Item hideout = RpWorld.HideoutOf(c);
            RpWorld.SetHidden(c, null);
            stateDirty = true;
            if (!c.Removed && !c.IsDead) {
                Vector2 exit = entries.TryGetValue(c, out Vector2 at) ? at : hideout?.WorldPosition ?? c.WorldPosition;
                if (Hull.FindHull(exit, c.CurrentHull) == null && hideout != null && !hideout.Removed) { exit = hideout.WorldPosition; }
                c.TeleportTo(exit);
            }
            entries.Remove(c);
            if (found && !c.Removed && !c.IsDead) { Note(c, "Вас обнаружили."); }
        }

        private static void DropStaleSurrender() {
            List<Character> drop = null;
            foreach (Character c in RpWorld.Surrendered) {
                bool armed = c.IsKeyDown(InputType.Aim) || c.IsKeyDown(InputType.Shoot);
                if (c.Removed || c.IsDead || c.LockHands || armed) { (drop ??= new List<Character>()).Add(c); }
            }
            if (drop == null) { return; }
            for (int i = 0; i < drop.Count; i++) { RpWorld.SetSurrendered(drop[i], false); }
            stateDirty = true;
        }

        private static void End(Character actor, RpRun run, RpEnd end, RpDeny deny, bool defer = false) {
            if (!defer) { running.Remove(actor); }
            SendTo(actor, w => RpWire.Ended(w, run.Act, end, deny));
            if (run.Target != 0 && Entity.FindEntityByID(run.Target) is Character target && !ReferenceEquals(target, actor)) {
                SendTo(target, w => RpWire.Ended(w, run.Act, end, deny));
            }
        }

        private static void BroadcastState() {
            if (GameMain.Server == null) { return; }
            RpWorld.CollectState(surrenderedIds, hiddenIds);
            RpWire.State(out_, surrenderedIds, hiddenIds, (uint)RpWorld.Revision);
            foreach (Client client in GameMain.Server.ConnectedClients) {
                ModNet.SendBytes(Channel, client.Connection, out_.Span, DeliveryMethod.Reliable);
            }
        }

        private static void Observe(Character actor, EmoteRun run, Client client, bool refresh) {
            if (client.Connection == null || !refresh && run.Observers.Contains(client.Connection)) { return; }
            float radius = MathF.Max(600.0f, run.Def.Range * 1.5f);
            Character viewer = client.Character;
            if (viewer != null && !viewer.IsDead && !ReferenceEquals(viewer, actor) &&
                Vector2.DistanceSquared(viewer.WorldPosition, actor.WorldPosition) >= radius * radius) { EmoteTrace.Log("server skip", client.Name + " is out of range of " + run.Def.Id); return; }
            float age = (float)(Timing.TotalTime - run.Start);
            if (age < 0 || Timing.TotalTime >= run.End) { return; }
            RpWire.EmoteStart(out_, actor.ID, run.Id, run.Def.Index, run.Variant, age, Catalog.Fingerprint);
            ModNet.SendBytes(Channel, client.Connection, out_.Span, DeliveryMethod.Reliable);
            run.Observers.Add(client.Connection);
        }

        private static void Note(Character c, string text) => SendTo(c, w => RpWire.Note(w, text));

        private static void SendTo(Character c, Action<WireWriter> write) {
            NetworkConnection connection = OwnerConnection.Of(c);
            if (connection == null) { return; }
            write(out_);
            ModNet.SendBytes(Channel, connection, out_.Span, DeliveryMethod.Reliable);
        }

        private static void Send(Client client, Action<WireWriter> write) {
            write(out_);
            ModNet.SendBytes(Channel, client.Connection, out_.Span, DeliveryMethod.Reliable);
        }
    }
}
