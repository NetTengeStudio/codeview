using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Sounds;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ScpItdMod.Core;
using ScpItdMod.Footsteps;
using ScpItdMod.Hud;
using ScpItdMod.Rp;

namespace ScpItdMod.Rec {
    public static class Visions {
        public const float StageRange = 1400.0f;
        public const float RealNearby = 500.0f;
        public const float FadeSeconds = 0.6f;
        private const float WalkStep = 0.5f;
        private const float RunStep = 0.32f;
        private const float MoveSpeed = 40.0f;
        private static readonly Identifier Psychosis = "psychosis".ToIdentifier();

        private static readonly Recall recall = new Recall();
        private static readonly Random rng = new Random();
        private static readonly TapeReader reader = new TapeReader();
        private static readonly List<FigurePose> poses = new List<FigurePose>(Tape.MaxFigures);
        private static readonly List<VoicePacket> packets = new List<VoicePacket>();
        private static readonly List<byte[]> owners = new List<byte[]>();
        private static readonly object speechKey = new object();
        private static readonly Func<WitnessSpan, bool> usable = Usable;
        private static Episode episode;
        private static bool active;
        private static double startedAt;
        private static double lastT;
        private static FigurePose pose;
        private static bool posed;
        private static object voice;
        private static float stepTimer;
        private static Vector2 lastRoot;
        private static Vector2 here;

        public static bool Active => active;

        public static int Played => recall.Played;

        public static void Attach(Harmony harmony) {
            harmony.Patch(AccessTools.Method(typeof(Character), nameof(Character.ControlLocalPlayer)),
                postfix: ModuleHost.Bind(typeof(Visions), nameof(Tick)));
            harmony.Patch(AccessTools.Method(typeof(Character), nameof(Character.Draw)),
                postfix: ModuleHost.Bind(typeof(Visions), nameof(DrawPostfix)));
        }

        public static void Reset() {
            Stop();
            recall.Reset();
        }

        public static string Describe() => active ? $"playing {episode.Duration:F1}s of #{episode.Subject.Entity}, {recall.Played} this life" : $"idle, {recall.Played} this life";

        public static void Tick(Character __instance) {
            if (!ReferenceEquals(__instance, Character.Controlled) || !ReferenceEquals(__instance, Memory.Owner)) { return; }
            double now = Timing.TotalTime;
            float strength = __instance.IsDead ? 0.0f : __instance.CharacterHealth?.GetAfflictionStrengthByIdentifier(Psychosis) ?? 0.0f;
            if (active) {
                if (strength < Recall.Threshold || __instance.IsDead) {
                    Stop();
                    return;
                }
                Play(now);
                return;
            }
            if (!recall.Due(now, strength, rng.NextDouble())) { return; }
            here = __instance.WorldPosition;
            if (recall.Plan(Memory.Index, now, strength, rng.NextDouble(), rng.NextDouble(), usable, out Episode e)) { Begin(e, now); }
        }

        private static bool Usable(WitnessSpan w) {
            if (Vector2.DistanceSquared(here, new Vector2(w.X, w.Y)) > StageRange * StageRange) { return false; }
            if (Entity.FindEntityByID(w.Subject.Entity) is Character real && !real.Removed && !real.IsDead && Capture.SubjectOf(real).Equals(w.Subject) &&
                Vector2.DistanceSquared(real.WorldPosition, here) < RealNearby * RealNearby) {
                return false;
            }
            return true;
        }

        private static void Begin(Episode e, double now) {
            episode = e;
            active = true;
            startedAt = now;
            lastT = e.From;
            posed = false;
            stepTimer = 0.0f;
            reader.Reset();
            if (e.Voice) { voice = VoiceOps.Create(Memory.Voice, e.From, e.To, e.Subject, packets, owners); }
        }

        public static void Stop() {
            if (!active && voice == null) { return; }
            active = false;
            posed = false;
            VoiceOps.Dispose(voice);
            voice = null;
            Speech.Drop(speechKey);
            reader.Reset();
        }

        private static void Play(double now) {
            double t = episode.From + (now - startedAt);
            if (t > episode.To) {
                Stop();
                return;
            }
            posed = false;
            if (reader.Read(Memory.Tape, t, poses)) {
                for (int i = 0; i < poses.Count; i++) {
                    if (!poses[i].Subject.Equals(episode.Subject)) { continue; }
                    pose = poses[i];
                    posed = true;
                    break;
                }
            }
            var root = new Vector2(pose.X, pose.Y);
            Vector2 head = root + new Vector2(0.0f, 60.0f);
            if (posed) {
                if (voice != null && !VoiceOps.Playing(voice)) { VoiceOps.Start(voice, root, Barotrauma.Networking.ChatMessage.SpeakRangeVOIP); }
                VoiceOps.Move(voice, root);
                Speech.Hold(speechKey, head, true);
                Steps(root, (float)(now - startedAt), (pose.Flags & PoseFlags.Running) != 0);
            } else {
                Speech.Hold(speechKey, head, false);
            }
            Notes(lastT, t, head, root);
            lastT = t;
        }

        private static void Steps(Vector2 root, float age, bool running) {
            float dt = FrameClock.Step;
            if (age < 0.05f) { lastRoot = root; }
            float speed = dt > 0.0f ? MathF.Abs(root.X - lastRoot.X) / dt : 0.0f;
            lastRoot = root;
            if (speed < MoveSpeed || (pose.Flags & (PoseFlags.Down | PoseFlags.Dead)) != 0) {
                stepTimer = 0.0f;
                return;
            }
            stepTimer -= dt;
            if (stepTimer > 0.0f) { return; }
            stepTimer = running ? RunStep : WalkStep;
            if (FootstepAudio.Audible(root)) { FootstepAudio.Play(FootstepMaterial.Generic, running, root, Hull.FindHull(root)); }
        }

        private static void Notes(double from, double to, Vector2 head, Vector2 root) {
            Segment s = Memory.Tape.At(to);
            if (s == null) { return; }
            for (int i = 0; i < s.Notes.Count; i++) {
                TapeNote n = s.Notes[i];
                if (n.Time < from || n.Time >= to || !n.Subject.Equals(episode.Subject)) { continue; }
                if (n.Kind == TapeEvent.Speech && n.Value == 0 && !string.IsNullOrEmpty(n.Text)) {
                    Speech.SayAt(speechKey, head, n.Text);
                } else if (n.Kind == TapeEvent.Emote) {
                    EmoteDef def = EmoteView.Catalog[(int)(n.Value & 0xFF)];
                    if (def == null || def.Sounds.Length == 0 || !SoundStore.Ready) { continue; }
                    EmoteSound sound = def.Sounds[Math.Min(def.Sounds.Length - 1, (int)(n.Value >> 8))];
                    Sound clip = SoundStore.File(sound.Path);
                    if (clip != null) { SoundPlayer.PlaySound(clip, root, 1.0f, MathF.Max(200.0f, def.Range), 1.0f, Hull.FindHull(root)); }
                    if (!string.IsNullOrEmpty(def.Bubble)) { Speech.SayAt(speechKey, head, "*" + def.Bubble + "*"); }
                }
            }
        }

        public static void DrawPostfix(Character __instance, SpriteBatch spriteBatch) {
            if (!active || !posed || !ReferenceEquals(__instance, Character.Controlled)) { return; }
            LookDesc look = Memory.Looks.Get(pose.Look);
            if (look == null) { return; }
            double age = Timing.TotalTime - startedAt;
            double left = episode.Duration - age;
            float alpha = Math.Clamp((float)Math.Min(age, left) / FadeSeconds, 0.0f, 1.0f);
            Ghosts.Draw(spriteBatch, look, pose, Color.White * alpha);
        }
    }
}
