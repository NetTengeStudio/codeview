using System.Collections.Generic;
using Barotrauma;
using HarmonyLib;
using ScpItdMod.Core;
using ScpItdMod.Net;
using ScpItdMod.Rp;

namespace ScpItdMod.Med {
    public static class MedUi {
        private static readonly Dictionary<ushort, HashSet<Identifier>> diagnosed = new Dictionary<ushort, HashSet<Identifier>>();
        private static readonly List<KeyValuePair<Affliction, CharacterHealth.LimbHealth>> parked = new List<KeyValuePair<Affliction, CharacterHealth.LimbHealth>>();
        private static CharacterHealth parkedIn;

        public static void Attach(Harmony harmony) {
            ModNet.ReceiveBytes(MedWorld.Channel, 4096, OnDiagnosis);
            harmony.Patch(AccessTools.Method(typeof(Affliction), nameof(Affliction.ShouldShowIcon)),
                postfix: ModuleHost.Bind(typeof(MedUi), nameof(ShowIconPostfix)));
            harmony.Patch(AccessTools.Method(typeof(CharacterHealth), nameof(CharacterHealth.GetSuitableTreatments)),
                prefix: ModuleHost.Bind(typeof(MedUi), nameof(TreatmentsPrefix)),
                finalizer: ModuleHost.Bind(typeof(MedUi), nameof(TreatmentsFinalizer)));
        }

        public static void Reset() => diagnosed.Clear();

        public static bool Knows(Character patient, Identifier affliction) =>
            patient != null && diagnosed.TryGetValue(patient.ID, out HashSet<Identifier> set) && set.Contains(affliction);

        public static void ShowIconPostfix(Affliction __instance, Character afflictedCharacter, ref bool __result) {
            if (!__result || MedWorld.SymptomOf.Count == 0) { return; }
            Identifier id = __instance.Identifier;
            if (MedWorld.IsHidden(id)) {
                if (!Knows(afflictedCharacter, id)) { __result = false; }
            } else if (MedWorld.IsSymptom(id, out Identifier source) && Knows(afflictedCharacter, source)) {
                __result = false;
            }
        }

        public static void TreatmentsPrefix(CharacterHealth __instance, bool ignoreHiddenAfflictions) {
            parked.Clear();
            parkedIn = null;
            if (!ignoreHiddenAfflictions || MedWorld.SymptomOf.Count == 0) { return; }
            Character patient = __instance.Character;
            foreach (KeyValuePair<Affliction, CharacterHealth.LimbHealth> pair in __instance.afflictions) {
                if (MedWorld.IsHidden(pair.Key.Identifier) && !Knows(patient, pair.Key.Identifier)) { parked.Add(pair); }
            }
            if (parked.Count == 0) { return; }
            parkedIn = __instance;
            for (int i = 0; i < parked.Count; i++) { __instance.afflictions.Remove(parked[i].Key); }
        }

        public static void TreatmentsFinalizer() {
            if (parkedIn == null) { return; }
            for (int i = 0; i < parked.Count; i++) { parkedIn.afflictions[parked[i].Key] = parked[i].Value; }
            parked.Clear();
            parkedIn = null;
        }

        private static void OnDiagnosis(byte[] payload) {
            var r = new WireReader(payload);
            if (r.U8() != MedWorld.Proto) { return; }
            uint patient = r.U32();
            int count = r.Count(64);
            var set = new HashSet<Identifier>();
            for (int i = 0; i < count && !r.Failed; i++) { set.Add(r.Text(128).ToIdentifier()); }
            if (r.Failed || !r.End || patient > ushort.MaxValue) { return; }
            diagnosed[(ushort)patient] = set;
            string who = Entity.FindEntityByID((ushort)patient) is Character c ? c.Info?.DisplayName ?? c.Name : "пациент";
            RpClient.Say(set.Count == 0 ? $"Диагностика: у {who} скрытых травм нет." : $"Диагностика {who} завершена: {set.Count} травм уточнено.", GUIStyle.Green);
        }
    }
}
