using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;

namespace ScpItdMod.Rp {
    public enum EmoteKind : byte { Hands, Voice, Body }

    public enum EmoteHands : byte { None, One, Both }

    public enum EmotePose : byte { None, Salute, Clap, Snap, Thump, Knock, Wave, Point, Mouth, Face }

    public enum EmoteDeny : byte { None, Unknown, Dead, Unconscious, Bound, HandsBusy, Muted, Cooldown, Busy, Healthy, Hidden }

    public sealed class EmoteSound {
        public string Name;
        public string Path;
        public string Gender;
        public float Seconds;
        public float[] Beats = Array.Empty<float>();
    }

    public sealed class EmoteDef {
        public byte Index;
        public string Id;
        public string Title;
        public EmoteKind Kind;
        public string Bubble;
        public EmoteHands Hands;
        public bool Voice;
        public EmotePose Pose;
        public float Range;
        public float Cooldown;
        public float Seconds;
        public float MaxVitality = 1.0f;
        public EmoteSound[] Sounds = Array.Empty<EmoteSound>();

        public float Duration(int variant) => Sounds.Length == 0 ? Seconds : Sounds[Math.Clamp(variant, 0, Sounds.Length - 1)].Seconds;

        public string KindTitle => Kind switch { EmoteKind.Hands => "Руки", EmoteKind.Voice => "Голос", _ => "Тело" };
    }

    public struct EmoteFacts {
        public bool Alive;
        public bool Conscious;
        public bool Bound;
        public bool HandBusy;
        public bool BothHandsBusy;
        public bool AnyHandBusy;
        public bool Muted;
        public bool Hidden;
        public bool Emoting;
        public float Vitality;
        public double SinceLast;
    }

    public sealed class EmoteCatalog {
        public const string SoundFolder = "Content/Sound/Emotes/";
        public const int MaxEmotes = 64;

        private readonly List<EmoteDef> defs = new List<EmoteDef>();
        private readonly Dictionary<string, EmoteDef> byId = new Dictionary<string, EmoteDef>(StringComparer.OrdinalIgnoreCase);
        private byte[] fingerprint = Array.Empty<byte>();
        public ReadOnlySpan<byte> Fingerprint => fingerprint;

        public readonly List<string> Problems = new List<string>();

        public int Count => defs.Count;

        public EmoteDef this[int index] => (uint)index < (uint)defs.Count ? defs[index] : null;

        public EmoteDef Find(string id) => id != null && byId.TryGetValue(id, out EmoteDef d) ? d : null;

        public IReadOnlyList<EmoteDef> All => defs;

        public static EmoteCatalog Parse(string xml, string manifestJson) {
            var c = new EmoteCatalog();
            var manifest = new Dictionary<string, (float Seconds, float[] Beats)>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(manifestJson)) {
                using JsonDocument doc = JsonDocument.Parse(manifestJson);
                foreach (JsonProperty p in doc.RootElement.EnumerateObject()) {
                    float seconds = p.Value.GetProperty("seconds").GetSingle();
                    if (!float.IsFinite(seconds) || seconds <= 0) { throw new InvalidDataException($"{p.Name}: invalid sound duration"); }
                    var beats = new List<float>();
                    if (p.Value.TryGetProperty("onsets", out JsonElement on)) {
                        foreach (JsonElement b in on.EnumerateArray()) {
                            float beat = b.GetSingle();
                            if (!float.IsFinite(beat) || beat < 0 || beat > seconds || beats.Count > 0 && beat < beats[beats.Count - 1]) {
                                throw new InvalidDataException($"{p.Name}: invalid sound onset");
                            }
                            beats.Add(beat);
                        }
                    }
                    manifest[p.Name] = (seconds, beats.ToArray());
                }
            }
            XElement root;
            try {
                root = XDocument.Parse(xml).Root;
            } catch (Exception error) {
                c.Problems.Add("emotes.xml: " + error.Message);
                return c;
            }
            foreach (XElement e in root?.Elements("Emote") ?? Array.Empty<XElement>()) {
                string id = (string)e.Attribute("id");
                if (string.IsNullOrWhiteSpace(id) || c.byId.ContainsKey(id)) {
                    c.Problems.Add($"emote without id or duplicated: {id}");
                    continue;
                }
                if (c.defs.Count >= MaxEmotes) {
                    c.Problems.Add("too many emotes");
                    break;
                }
                var d = new EmoteDef {
                    Index = (byte)c.defs.Count,
                    Id = id,
                    Title = (string)e.Attribute("title") ?? id,
                    Bubble = (string)e.Attribute("bubble") ?? "",
                    Kind = Enum.TryParse((string)e.Attribute("category"), true, out EmoteKind kind) ? kind : EmoteKind.Body,
                    Hands = Enum.TryParse((string)e.Attribute("hands"), true, out EmoteHands hands) ? hands : EmoteHands.None,
                    Voice = string.Equals((string)e.Attribute("voice"), "true", StringComparison.OrdinalIgnoreCase),
                    Pose = Enum.TryParse((string)e.Attribute("pose"), true, out EmotePose pose) ? pose : EmotePose.None,
                    Range = Number(e, "range", 600.0f),
                    Cooldown = Number(e, "cooldown", 1.0f),
                    Seconds = Number(e, "seconds", 1.0f),
                    MaxVitality = Number(e, "maxvitality", 1.0f)
                };
                var sounds = new List<EmoteSound>();
                foreach (XElement s in e.Elements("Sound")) {
                    string name = (string)s.Attribute("name");
                    if (string.IsNullOrEmpty(name) || !manifest.TryGetValue(name, out var info)) {
                        c.Problems.Add($"{id}: sound '{name}' is not in the built manifest");
                        continue;
                    }
                    sounds.Add(new EmoteSound { Name = name, Path = SoundFolder + name + ".ogg", Gender = (string)s.Attribute("gender"), Seconds = info.Seconds, Beats = info.Beats });
                }
                d.Sounds = sounds.ToArray();
                if (d.Sounds.Length > 0) { d.Seconds = d.Sounds[0].Seconds; }
                c.defs.Add(d);
                c.byId[id] = d;
            }
            c.fingerprint = c.Hash();
            return c;
        }

        private byte[] Hash() {
            using var bytes = new MemoryStream();
            using (var w = new BinaryWriter(bytes, System.Text.Encoding.UTF8, true)) {
                w.Write(defs.Count);
                foreach (EmoteDef d in defs) {
                    w.Write(d.Id); w.Write(d.Title); w.Write(d.Bubble);
                    w.Write((byte)d.Kind); w.Write((byte)d.Hands); w.Write(d.Voice); w.Write((byte)d.Pose);
                    w.Write(d.Range); w.Write(d.Cooldown); w.Write(d.Seconds); w.Write(d.MaxVitality);
                    w.Write(d.Sounds.Length);
                    foreach (EmoteSound s in d.Sounds) {
                        w.Write(s.Name); w.Write(s.Gender ?? ""); w.Write(s.Seconds); w.Write(s.Beats.Length);
                        foreach (float beat in s.Beats) { w.Write(beat); }
                    }
                }
            }
            return SHA256.HashData(bytes.GetBuffer().AsSpan(0, checked((int)bytes.Length)));
        }

        private static float Number(XElement e, string name, float fallback) =>
            float.TryParse((string)e.Attribute(name), NumberStyles.Float, CultureInfo.InvariantCulture, out float v) && float.IsFinite(v) ? v : fallback;
    }

    public static class EmoteRules {
        public const int MaxVariants = 8;

        public static EmoteDeny Check(EmoteDef d, in EmoteFacts f) {
            if (d == null) { return EmoteDeny.Unknown; }
            if (!f.Alive) { return EmoteDeny.Dead; }
            if (!f.Conscious) { return EmoteDeny.Unconscious; }
            if (f.Hidden && (d.Voice || d.Hands != EmoteHands.None)) { return EmoteDeny.Hidden; }
            if (f.Emoting) { return EmoteDeny.Busy; }
            if (f.SinceLast < d.Cooldown) { return EmoteDeny.Cooldown; }
            if (d.Hands != EmoteHands.None) {
                if (f.Bound) { return EmoteDeny.Bound; }
                if (f.HandBusy || f.BothHandsBusy || (d.Hands == EmoteHands.Both && f.AnyHandBusy)) { return EmoteDeny.HandsBusy; }
            }
            if (d.Voice && f.Muted) { return EmoteDeny.Muted; }
            if (f.Vitality > d.MaxVitality) { return EmoteDeny.Healthy; }
            return EmoteDeny.None;
        }

        public static bool Interrupted(EmoteDef d, in EmoteFacts f) {
            var relaxed = f;
            relaxed.Emoting = false;
            relaxed.SinceLast = double.MaxValue;
            relaxed.HandBusy = false;
            relaxed.Vitality = 0.0f;
            return Check(d, relaxed) != EmoteDeny.None;
        }

        public static int Variant(EmoteDef d, string gender, uint seed) {
            if (d.Sounds.Length <= 1) { return 0; }
            Span<int> pool = stackalloc int[Math.Min(d.Sounds.Length, MaxVariants)];
            int n = 0;
            for (int i = 0; i < d.Sounds.Length && n < pool.Length; i++) {
                string g = d.Sounds[i].Gender;
                if (string.IsNullOrEmpty(g) || gender == null || string.Equals(g, gender, StringComparison.OrdinalIgnoreCase)) { pool[n++] = i; }
            }
            if (n == 0) { return (int)(seed % (uint)d.Sounds.Length); }
            return pool[(int)(Mix(seed) % (uint)n)];
        }

        private static uint Mix(uint x) {
            x ^= x >> 16;
            x *= 0x7feb352d;
            x ^= x >> 15;
            x *= 0x846ca68b;
            x ^= x >> 16;
            return x;
        }

        public static string Explain(EmoteDeny d) => d switch {
            EmoteDeny.Unknown => "Неизвестная эмоция",
            EmoteDeny.Dead => "Вы мертвы",
            EmoteDeny.Unconscious => "Вы без сознания",
            EmoteDeny.Bound => "Руки связаны",
            EmoteDeny.HandsBusy => "Руки заняты",
            EmoteDeny.Muted => "Вы не можете издать звук",
            EmoteDeny.Cooldown => "Слишком часто",
            EmoteDeny.Busy => "Уже выполняется",
            EmoteDeny.Healthy => "Только при тяжёлом состоянии",
            EmoteDeny.Hidden => "В укрытии вас выдаст",
            _ => ""
        };
    }
}
