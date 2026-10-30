using System;
using System.Collections.Generic;
using System.Xml.Linq;
using Barotrauma;
using Barotrauma.Items.Components;
using ScpItdMod.Core;

namespace ScpItdMod.Med {
    public static class MedWorld {
        public const string Asset = "Content/Data/medicine.xml";
        public const string Channel = "scpitd.med";
        public const byte Proto = 1;

        private static readonly Dictionary<Identifier, Identifier> symptomOf = new Dictionary<Identifier, Identifier>();
        private static readonly Dictionary<Identifier, Identifier> sourceOf = new Dictionary<Identifier, Identifier>();

        public static float DiagnoseSkill { get; private set; } = 30.0f;

        public static IReadOnlyDictionary<Identifier, Identifier> SymptomOf => symptomOf;

        public static void Load() {
            symptomOf.Clear();
            sourceOf.Clear();
            string path = ModAssets.Resolve(Asset);
            if (path.Length == 0 || !System.IO.File.Exists(path)) { return; }
            try {
                XElement root = XDocument.Load(path).Root;
                if (root == null) { return; }
                DiagnoseSkill = (float?)root.Attribute("diagnoseskill") ?? DiagnoseSkill;
                foreach (XElement hide in root.Elements("Hide")) {
                    string real = hide.Attribute("affliction")?.Value;
                    string symptom = hide.Attribute("symptom")?.Value;
                    if (string.IsNullOrWhiteSpace(real) || string.IsNullOrWhiteSpace(symptom)) { continue; }
                    symptomOf[real.ToIdentifier()] = symptom.ToIdentifier();
                    sourceOf[symptom.ToIdentifier()] = real.ToIdentifier();
                }
            } catch (Exception error) {
                ModLog.Error($"{Asset} cannot be read: {error.Message}");
            }
        }

        public static bool IsHidden(Identifier affliction) => symptomOf.ContainsKey(affliction);

        public static bool IsSymptom(Identifier affliction, out Identifier source) => sourceOf.TryGetValue(affliction, out source);

        public static bool CanDiagnose(Character c) {
            if (c == null) { return false; }
            if (c.GetSkillLevel(Tags.MedicalSkill) >= DiagnoseSkill) { return true; }
            foreach (Item held in c.HeldItems) {
                if (held.GetComponent<StatusHUD>() != null) { return true; }
            }
            return false;
        }

        public static void Present(Character patient, List<Identifier> found) {
            found.Clear();
            if (patient?.CharacterHealth == null || symptomOf.Count == 0) { return; }
            foreach (Affliction affliction in patient.CharacterHealth.GetAllAfflictions()) {
                if (symptomOf.ContainsKey(affliction.Identifier) && affliction.Strength > 0.0f && !found.Contains(affliction.Identifier)) {
                    found.Add(affliction.Identifier);
                }
            }
        }
    }
}
