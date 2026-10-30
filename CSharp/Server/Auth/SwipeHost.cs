using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.Networking;
using HarmonyLib;
using Microsoft.Xna.Framework;
using ScpItdMod.Core;
using ScpItdMod.Net;
using ScpItdMod.Rp;

namespace ScpItdMod.Auth {
    public static class SwipeHost {
        private const float InterestRange = 2600.0f;

        private sealed class Run {
            public uint Id;
            public Character Actor;
            public Item Reader;
            public Item Card;
            public byte Hand;
            public double Start;
            public ReachTiming Timing;
            public bool Decided;
        }

        private static readonly Dictionary<Character, Run> runs = new Dictionary<Character, Run>();
        private static readonly Dictionary<Item, double> readerBusyUntil = new Dictionary<Item, double>();
        private static readonly List<Character> done = new List<Character>();
        private static readonly Credentials scratch = new Credentials();
        private static readonly WireWriter out_ = new WireWriter(64);
        private static uint nextId = 1;

        public static int Active => runs.Count;

        public static void Attach(Harmony harmony) {
            harmony.Patch(AccessTools.Method(typeof(Item), nameof(Item.TryInteract)),
                postfix: ModuleHost.Bind(typeof(SwipeHost), nameof(InteractPostfix)));
            harmony.Patch(AccessTools.Method(typeof(GameServer), nameof(GameServer.Update)),
                postfix: ModuleHost.Bind(typeof(SwipeHost), nameof(Tick)));
            ModCommands.Register("scpitd_cardgrant", "gm: scpitd_cardgrant <card item id> <tag> — adds an access tag to one physical card", Grant);
            ModCommands.Register("scpitd_cardrevoke", "gm: scpitd_cardrevoke <card item id> <tag>", Revoke);
        }

        public static void RoundStart() {
            runs.Clear();
            readerBusyUntil.Clear();
            SwipeWorld.ClearGrants();
        }

        public static void Release() => RoundStart();

        public static bool Busy(Character c) => c != null && runs.ContainsKey(c);

        public static void InteractPostfix(Item __instance, Character user, bool __result) {
            if (!__result || user == null || !SwipeWorld.IsReader(__instance)) { return; }
            Begin(user, __instance);
        }

        private static void Begin(Character actor, Item reader) {
            double now = Timing.TotalTime;
            if (runs.TryGetValue(actor, out Run current)) {
                if (ReferenceEquals(current.Reader, reader)) { return; }
                Cancel(current, now, SwipeDeny.Moved);
                runs.Remove(actor);
            }
            Item card = SwipeWorld.Card(actor);
            SwipeFacts facts = SwipeWorld.Facts(actor, reader, card, false, RpWorld.IsHidden(actor));
            SwipeDeny deny = SwipeRules.Check(facts);
            if (deny == SwipeDeny.None && readerBusyUntil.TryGetValue(reader, out double until) && now < until) { deny = SwipeDeny.Busy; }
            if (deny != SwipeDeny.None) {
                SendTo(actor, new SwipeEvent { Kind = SwipeMsg.Denied, Actor = actor.ID, Reader = reader.ID, Deny = deny });
                return;
            }
            var run = new Run {
                Id = nextId++,
                Actor = actor,
                Reader = reader,
                Card = card,
                Hand = (byte)SwipeWorld.FreeHand(actor, card),
                Start = now,
                Timing = new ReachTiming { Approach = SwipeWorld.Approach(actor, reader) }
            };
            runs[actor] = run;
            Broadcast(run, new SwipeEvent {
                Kind = SwipeMsg.Start, Id = run.Id, Actor = actor.ID, Reader = reader.ID, Card = card.ID, Hand = run.Hand, Approach = run.Timing.Approach, Age = 0.0f
            });
        }

        public static void Tick() {
            if (runs.Count == 0) { return; }
            double now = Timing.TotalTime;
            done.Clear();
            foreach (KeyValuePair<Character, Run> pair in runs) {
                Run run = pair.Value;
                float age = (float)(now - run.Start);
                SwipeFacts facts = SwipeWorld.Facts(run.Actor, run.Reader, run.Card, false, RpWorld.IsHidden(run.Actor));
                if (!run.Decided) {
                    SwipeDeny broken = SwipeRules.Interrupted(facts);
                    if (broken != SwipeDeny.None) {
                        Cancel(run, now, broken);
                        done.Add(pair.Key);
                        continue;
                    }
                    if (age >= run.Timing.ContactAt) { Decide(run, facts, now); }
                }
                if (age >= run.Timing.Total || run.Actor.Removed) { done.Add(pair.Key); }
            }
            for (int i = 0; i < done.Count; i++) { runs.Remove(done[i]); }
        }

        private static void Decide(Run run, in SwipeFacts facts, double now) {
            run.Decided = true;
            bool granted = false;
            SwipeDeny deny;
            if (!facts.ReaderPowered) {
                deny = SwipeDeny.NoPower;
            } else {
                SwipeWorld.CredentialsOf(run.Card, scratch);
                granted = AuthWorld.ReaderPolicy(run.Reader).Allows(scratch);
                deny = granted ? SwipeDeny.None : SwipeDeny.Refused;
                run.Reader.SendSignal(new Signal(granted ? "1" : "0", sender: run.Actor, source: run.Reader), "signal_out");
            }
            readerBusyUntil[run.Reader] = now + SwipeRules.Cooldown;
            Broadcast(run, new SwipeEvent { Kind = SwipeMsg.Result, Id = run.Id, Actor = run.Actor.ID, Reader = run.Reader.ID, Granted = granted, Deny = deny });
        }

        private static void Cancel(Run run, double now, SwipeDeny why) =>
            Broadcast(run, new SwipeEvent { Kind = SwipeMsg.Cancel, Id = run.Id, Actor = run.Actor.ID, Reader = run.Reader?.ID ?? 0, Age = (float)Math.Min(10.0, now - run.Start), Deny = why });

        private static void Broadcast(Run run, in SwipeEvent e) {
            if (GameMain.Server == null) { return; }
            e.Write(out_);
            Vector2 at = run.Actor.WorldPosition;
            foreach (Client client in GameMain.Server.ConnectedClients) {
                Character viewer = client.Character;
                bool interested = viewer == null || viewer.IsDead || ReferenceEquals(viewer, run.Actor) ||
                                  Vector2.DistanceSquared(viewer.WorldPosition, at) < InterestRange * InterestRange;
                if (interested) { ModNet.SendBytes(SwipeWire.Channel, client.Connection, out_.Span, DeliveryMethod.Reliable); }
            }
        }

        private static void SendTo(Character c, in SwipeEvent e) {
            NetworkConnection connection = OwnerConnection.Of(c);
            if (connection == null) { return; }
            e.Write(out_);
            ModNet.SendBytes(SwipeWire.Channel, connection, out_.Span, DeliveryMethod.Reliable);
        }

        private static void Grant(string[] args) => Edit(args, true);

        private static void Revoke(string[] args) => Edit(args, false);

        private static void Edit(string[] args, bool grant) {
            if (args.Length < 2 || !ushort.TryParse(args[0], out ushort id) || !(Entity.FindEntityByID(id) is Item card) || card.GetComponent<Barotrauma.Items.Components.IdCard>() == null) {
                ModLog.Message("usage: <card item id> <tag>; the item must be an id card");
                return;
            }
            bool ok = grant ? SwipeWorld.Grant(card, args[1]) : SwipeWorld.Revoke(card, args[1]);
            ModLog.Message(ok ? $"card {id}: {(grant ? "+" : "-")}{args[1]}" : "nothing changed");
        }
    }
}
