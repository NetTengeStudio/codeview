using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Networking;
using HarmonyLib;
using ScpItdMod.Core;
using ScpItdMod.Net;
using ScpItdMod.Rp;

namespace ScpItdMod.Life {
    public static class LifeHost {
        private sealed class Peer {
            public readonly RequestSessions Requests = new RequestSessions();
            public Gate Gate;
        }

        private static readonly Dictionary<ulong, Peer> peers = new Dictionary<ulong, Peer>();
        private static readonly Dictionary<Character, double> spawned = new Dictionary<Character, double>();
        private static readonly WireWriter out_ = new WireWriter(64);
        private static bool enabled = true;

        public static void Attach(Harmony harmony) {
            ModNet.ReceiveBytes(LifeModule.Channel, 16, OnRequest);
            harmony.Patch(AccessTools.Method(typeof(Character), nameof(Character.Create), new[] {
                    typeof(CharacterPrefab), typeof(Microsoft.Xna.Framework.Vector2), typeof(string), typeof(CharacterInfo), typeof(ushort),
                    typeof(bool), typeof(bool), typeof(bool), typeof(RagdollParams), typeof(bool)
                }),
                postfix: ModuleHost.Bind(typeof(LifeHost), nameof(CreatePostfix)));
            harmony.Patch(AccessTools.Method(typeof(Character), nameof(Character.Remove), Type.EmptyTypes), postfix: ModuleHost.Bind(typeof(LifeHost), nameof(RemovePostfix)));
            ModCommands.Register("scpitd_voluntarydeath", "on/off: allow players to end their character's life from the pause menu", args => {
                enabled = args.Length == 0 || !args[0].Equals("off", StringComparison.OrdinalIgnoreCase);
                ModLog.Message(enabled ? "voluntary death is allowed" : "voluntary death is disabled");
            });
        }

        public static void RoundStart() {
            spawned.Clear();
            peers.Clear();
            foreach (Character c in Character.CharacterList) { if (!c.Removed) { spawned[c] = Timing.TotalTime; } }
        }

        public static void RemovePostfix(Character __instance) {
            try { spawned.Remove(__instance); }
            catch (Exception error) { ModLog.Once("life registry remove failed: " + error.Message); }
        }
        public static void Release() { spawned.Clear(); peers.Clear(); }

        public static void CreatePostfix(Character __result) {
            try { if (__result != null) { spawned[__result] = Timing.TotalTime; } }
            catch (Exception error) { ModLog.Once("life registry add failed: " + error.Message); }
        }

        private static void OnRequest(Client sender, uint nonce, uint seq, byte[] payload) {
            ulong account = Access.Id(sender);
            if (!peers.TryGetValue(account, out Peer peer)) {
                peer = new Peer();
                peers.Add(account, peer);
            }
            if (!peer.Gate.Take(Timing.TotalTime, 0.5f, 2.0f)) { return; }
            if (!peer.Requests.Accept(sender, nonce, seq, out _)) { return; }
            var r = new WireReader(payload);
            if (r.U8() != LifeModule.Proto || r.U8() != LifeModule.Die || !r.End) { return; }

            Character c = sender.Character;
            double born = 0.0;
            bool known = c != null && spawned.TryGetValue(c, out born);
            var facts = new DeathFacts {
                Enabled = enabled,
                HasCharacter = c != null && !c.Removed,
                Alive = c != null && !c.IsDead,
                Bound = c != null && c.LockHands,
                BeingHeld = c != null && RpHost.IsTargeted(c),
                SinceSpawn = known ? Timing.TotalTime - born : double.MaxValue
            };
            DeathDeny deny = DeathRules.Check(facts);
            out_.Clear();
            out_.U8(LifeModule.Proto);
            out_.U8((byte)deny);
            ModNet.SendBytes(LifeModule.Channel, sender.Connection, out_.Span, DeliveryMethod.Reliable);
            if (deny != DeathDeny.None) { return; }
            ModLog.Message($"{c.Name} chose to die (account {account:X16})");
            c.Kill(CauseOfDeathType.Unknown, null);
        }
    }
}
