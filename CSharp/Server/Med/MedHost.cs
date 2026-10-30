using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Networking;
using HarmonyLib;
using ScpItdMod.Core;
using ScpItdMod.Net;

namespace ScpItdMod.Med {
    public static class MedHost {
        private const double TickSeconds = 1.0;

        private static readonly Dictionary<Identifier, float> strongest = new Dictionary<Identifier, float>();
        private static readonly List<Identifier> found = new List<Identifier>();
        private static readonly WireWriter out_ = new WireWriter(256);
        private static double nextTick;

        public static void Attach(Harmony harmony) =>
            harmony.Patch(AccessTools.Method(typeof(GameServer), nameof(GameServer.Update)),
                postfix: ModuleHost.Bind(typeof(MedHost), nameof(Tick)));

        public static void Tick() {
            if (MedWorld.SymptomOf.Count == 0) { return; }
            double now = Timing.TotalTime;
            if (now < nextTick) { return; }
            nextTick = now + TickSeconds;
            var humans = Humans.All;
            for (int i = 0; i < humans.Count; i++) {
                Character c = humans[i];
                if (c.Removed || c.CharacterHealth == null || !c.IsHuman) { continue; }
                Mirror(c);
            }
        }

        private static void Mirror(Character c) {
            strongest.Clear();
            foreach (Affliction affliction in c.CharacterHealth.GetAllAfflictions()) {
                if (!MedWorld.SymptomOf.TryGetValue(affliction.Identifier, out Identifier symptom)) { continue; }
                strongest.TryGetValue(symptom, out float current);
                if (affliction.Strength > current) { strongest[symptom] = affliction.Strength; }
            }
            foreach (Identifier symptom in MedWorld.SymptomOf.Values) {
                strongest.TryGetValue(symptom, out float target);
                Affliction existing = c.CharacterHealth.GetAffliction(symptom);
                float have = existing?.Strength ?? 0.0f;
                if (System.Math.Abs(have - target) < 1.0f) { continue; }
                if (existing != null) {
                    existing.Strength = target;
                } else if (target > 0.0f && AfflictionPrefab.Prefabs.TryGet(symptom, out AfflictionPrefab prefab)) {
                    c.CharacterHealth.ApplyAffliction(null, prefab.Instantiate(target));
                }
            }
        }

        public static void Diagnose(Character doctor, Character patient) {
            if (doctor == null || patient == null || GameMain.Server == null) { return; }
            MedWorld.Present(patient, found);
            out_.Clear();
            out_.U8(MedWorld.Proto);
            out_.U32(patient.ID);
            out_.Var((ulong)found.Count);
            for (int i = 0; i < found.Count; i++) { out_.Text(found[i].Value); }
            foreach (Client client in GameMain.Server.ConnectedClients) {
                if (!ReferenceEquals(client.Character, doctor)) { continue; }
                ModNet.SendBytes(MedWorld.Channel, client.Connection, out_.Span, DeliveryMethod.Reliable);
            }
        }
    }
}
