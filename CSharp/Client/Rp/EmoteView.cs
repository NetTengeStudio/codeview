using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Sounds;
using FarseerPhysics;
using HarmonyLib;
using Microsoft.Xna.Framework;
using ScpItdMod.Core;
using ScpItdMod.Hud;

namespace ScpItdMod.Rp {
    public static class EmoteView {
        public const int MaxVoices = 6;

        private sealed class Run {
            public Character Actor;
            public EmoteDef Def;
            public int Variant;
            public double Start;
            public bool Posed;
            public RestPose Rest;
            public bool UseLeft;
            public readonly HandTrack Right = new HandTrack();
            public readonly HandTrack Left = new HandTrack();
            public SoundChannel Channel;
            public bool SoundStarted;
            public bool Stopping;
            public float Total;
            public float FadeAt;
            public float StopAt, StopWeight;
        }

        private struct RestPose {
            public bool Set;
            public Vec2 Right, Left;
        }
        private sealed class Stance {
            public float Weight;
            public RestPose Rest;
        }
        private static AnimController animating;
        private static byte drivenHands;
        private static readonly PhysicsBody[] claimedBodies = new PhysicsBody[6];
        private static readonly bool[] claimedWasSuppressed = new bool[6];
        private static int claimedCount;
        private static readonly Dictionary<Character, Run> runs = new Dictionary<Character, Run>();
        private static readonly List<Character> done = new List<Character>();
        private static readonly Stack<Run> pool = new Stack<Run>();
        private static readonly Dictionary<Character, Stance> stances = new Dictionary<Character, Stance>();

        public static EmoteCatalog Catalog { get; private set; } = EmoteCatalog.Parse("<Emotes/>", null);

        public static int Voices { get; private set; }

        public static event Action<Character, EmoteDef, int> Heard;

        public static void Attach(Harmony harmony) {
            Catalog = EmoteData.Load();
            harmony.Patch(AccessTools.Method(typeof(AnimController), nameof(AnimController.UpdateAnimations)),
                prefix: ModuleHost.Bind(typeof(EmoteView), nameof(AnimPrefix)),
                postfix: ModuleHost.Bind(typeof(EmoteView), nameof(AnimPostfix)),
                finalizer: ModuleHost.Bind(typeof(EmoteView), nameof(AnimFinalizer)));
            harmony.Patch(AccessTools.Method(typeof(AnimController), nameof(AnimController.HandIK),
                new[] { typeof(Limb), typeof(Vector2), typeof(float), typeof(float), typeof(float) }),
                prefix: ModuleHost.Bind(typeof(EmoteView), nameof(HandIKPrefix)));
            harmony.Patch(AccessTools.Method(typeof(GameScreen), nameof(GameScreen.Update)),
                postfix: ModuleHost.Bind(typeof(EmoteView), nameof(Tick)));
        }

        public static void Reset() {
            foreach (Run r in runs.Values) { Free(r); }
            runs.Clear();
            ReleaseClaims();
            animating = null; drivenHands = 0;
            stances.Clear();
            done.Clear();
            Voices = 0;
        }

        public static bool Busy(Character c) => c != null && runs.ContainsKey(c);
        public static double StartTime(Character c) => c != null && runs.TryGetValue(c, out Run run) ? run.Start : Timing.TotalTime;

        public static void Start(Character actor, byte index, byte variant, float age) {
            EmoteDef def = Catalog[index];
            if (actor == null || def == null || !float.IsFinite(age) || age < 0 ||
                variant >= Math.Max(1, def.Sounds.Length) || age >= Poses.Total(def.Pose, def.Duration(variant))) { return; }
            if (runs.TryGetValue(actor, out Run old)) {
                Free(old);
                runs.Remove(actor);
            }
            Run r = pool.Count > 0 ? pool.Pop() : new Run();
            r.Actor = actor;
            r.Def = def;
            r.Variant = Math.Min(variant, Math.Max(0, def.Sounds.Length - 1));
            r.Start = Timing.TotalTime - age;
            r.Posed = false; r.Rest = default;
            r.UseLeft = def.Hands == EmoteHands.One && actor.Inventory?.GetItemInLimbSlot(InvSlotType.RightHand) != null;
            r.Right.Clear();
            r.Left.Clear();
            r.SoundStarted = false;
            r.Stopping = false;
            r.StopAt = r.StopWeight = 0;
            r.Channel = null;
            r.Total = Poses.Total(def.Pose, def.Duration(r.Variant));
            r.FadeAt = Poses.FadeAt(def.Pose, def.Duration(r.Variant));
            runs[actor] = r;
            if (!string.IsNullOrEmpty(def.Bubble)) { Speech.Say(actor, "*" + def.Bubble + "*"); }
            Heard?.Invoke(actor, def, r.Variant);
        }

        public static void Stop(Character actor) {
            if (actor == null || !runs.TryGetValue(actor, out Run r) || r.Stopping) { return; }
            r.Stopping = true;
            r.Channel?.FadeOutAndDispose();
            r.Channel = null;
            float t = (float)(Timing.TotalTime - r.Start);
            r.StopAt = t;
            r.StopWeight = Poses.Weight(t, r.FadeAt);
            if (t < r.FadeAt) {
                r.FadeAt = t;
                r.Total = t + Poses.Return;
            }
        }

        public static void Tick() {
            if (stances.Count > 0) {
                done.Clear();
                foreach (Character c in stances.Keys) {
                    if (c.Removed) { done.Add(c); }
                }
                for (int i = 0; i < done.Count; i++) { stances.Remove(done[i]); }
            }
            if (runs.Count == 0) { return; }
            double now = Timing.TotalTime;
            done.Clear();
            Voices = 0;
            foreach (Run r in runs.Values) { if (r.Channel != null && r.Channel.IsPlaying) { Voices++; } }
            foreach (KeyValuePair<Character, Run> pair in runs) {
                Run r = pair.Value;
                float t = (float)(now - r.Start);
                if (pair.Key.Removed || t > r.Total + 0.05f) {
                    done.Add(pair.Key);
                    continue;
                }
                if (pair.Key.IsDead && !r.Stopping) { Stop(pair.Key); }
                if (!r.SoundStarted && !r.Stopping && t >= Poses.SoundDelay(r.Def.Pose)) {
                    r.SoundStarted = Play(r, t - Poses.SoundDelay(r.Def.Pose));
                }
                if (r.Channel != null) {
                    if (!r.Channel.IsPlaying) {
                        r.Channel.Dispose();
                        r.Channel = null;
                    } else {
                        r.Channel.Position = new Vector3(pair.Key.WorldPosition, 0.0f);
                    }
                }
            }
            for (int i = 0; i < done.Count; i++) {
                if (runs.Remove(done[i], out Run r)) { Free(r); }
            }
        }

        private static bool Play(Run r, float age) {
            if (r.Def.Sounds.Length == 0 || age >= r.Def.Sounds[r.Variant].Seconds) { return true; }
            if (!SoundStore.Ready) { return false; }
            if (Voices >= MaxVoices && !ReferenceEquals(r.Actor, Character.Controlled)) {
                Run farthest = null;
                float best = -1.0f;
                Vector2 listener = Character.Controlled?.WorldPosition ?? GameMain.GameScreen?.Cam?.Position ?? Vector2.Zero;
                foreach (Run other in runs.Values) {
                    if (other.Channel == null || !other.Channel.IsPlaying) { continue; }
                    float d = Vector2.DistanceSquared(other.Actor.WorldPosition, listener);
                    if (d > best) {
                        best = d;
                        farthest = other;
                    }
                }
                float mine = Vector2.DistanceSquared(r.Actor.WorldPosition, listener);
                if (farthest == null || mine >= best) { return false; }
                farthest.Channel.FadeOutAndDispose();
                farthest.Channel = null;
                Voices--;
            }
            EmoteSound s = r.Def.Sounds[r.Variant];
            Sound sound = SoundStore.File(s.Path);
            if (sound == null) { return false; }
            float range = MathF.Max(200.0f, r.Def.Range);
            r.Channel = SoundPlayer.PlaySound(sound, r.Actor.WorldPosition, 1.0f, range, 1.0f, r.Actor.CurrentHull);
            if (r.Channel == null || !r.Channel.IsPlaying) { r.Channel?.Dispose(); r.Channel = null; return false; }
            Voices++;
            return true;
        }

        private static void Free(Run r) {
            r.Channel?.FadeOutAndDispose();
            r.Channel = null;
            r.Actor = null;
            r.Def = null;
            if (pool.Count < 32) { pool.Push(r); }
        }

        public static void AnimPrefix(AnimController __instance, float deltaTime) {
            // Claims are per engine step. Releasing here guarantees that a skipped
            // animation or an early return cannot leave a limb suppressing vanilla
            // rotations forever.
            ReleaseClaims();
            animating = __instance;
            drivenHands = 0;
            Character c = __instance.Character;
            if (c == null || c.Removed) { return; }

            if (runs.TryGetValue(c, out Run active) && active.Def.Pose != EmotePose.None) {
                ClaimIdleHands(__instance, c, active);
            }

            if (stances.Count == 0 && !RpWorld.IsSurrendered(c)) { return; }
            stances.TryGetValue(c, out Stance stance);
            bool up = RpWorld.IsSurrendered(c) && !c.IsDead && !c.IsIncapacitated && !c.LockHands && !__instance.IsHangingWithRope;
            if (stance == null) {
                if (!up) { return; }
                stance = new Stance(); stances.Add(c, stance);
            }
            stance.Weight = Scalar.Approach(stance.Weight, up ? 1.0f : 0.0f, Poses.StanceRate, deltaTime);
            if (!up && stance.Weight < 0.01f) { stances.Remove(c); }
        }

        public static void AnimPostfix(AnimController __instance, float deltaTime) {
            if (!ReferenceEquals(animating, __instance)) { ReleaseClaims(); return; }
            // The idle humanoid branch lets the vanilla neutral arm torque run
            // before this postfix. Claims suppress those conflicting rotations for
            // the duration of UpdateAnim, then are released here so the emotion's
            // own HandIK calls are allowed to act on the same limbs.
            ReleaseClaims();
            Character actor = __instance.Character;
            if (actor != null && (runs.TryGetValue(actor, out Run run) && run.Def.Pose != EmotePose.None || stances.ContainsKey(actor))) {
                DriveMissing(__instance, LimbType.RightHand, 1);
                DriveMissing(__instance, LimbType.LeftHand, 2);
            }
            animating = null;
        }

        public static void AnimFinalizer() {
            ReleaseClaims();
            animating = null;
            drivenHands = 0;
        }

        private static void ClaimIdleHands(AnimController anim, Character c, Run run) {
            if (anim.InWater || anim.IsClimbing || c.IsDead || c.IsIncapacitated || c.LockHands || anim.IsHangingWithRope) { return; }
            if (Math.Abs(anim.TargetMovement.X) > 0.01f || Math.Abs(anim.TargetMovement.Y) > 0.01f) { return; }
            if (!run.Posed) { Prepare(anim, run); }
            if (run.UseLeft) { Claim(anim, LimbType.LeftHand, run.Right.Active); }
            else { Claim(anim, LimbType.RightHand, run.Right.Active); }
            if (!run.UseLeft) { Claim(anim, LimbType.LeftHand, run.Left.Active); }
        }

        private static void Claim(AnimController anim, LimbType type, bool active) {
            if (!active) { return; }
            Limb hand = anim.GetLimb(type);
            if (hand?.body == null || hand.Disabled) { return; }
            InvSlotType slot = type == LimbType.LeftHand ? InvSlotType.LeftHand : InvSlotType.RightHand;
            if (anim.Character?.Inventory?.GetItemInLimbSlot(slot) != null) { return; }
            ClaimBody(hand.body);
            ClaimBody(anim.GetLimb(type == LimbType.LeftHand ? LimbType.LeftForearm : LimbType.RightForearm)?.body);
            ClaimBody(anim.GetLimb(type == LimbType.LeftHand ? LimbType.LeftArm : LimbType.RightArm)?.body);
        }

        private static void ClaimBody(PhysicsBody body) {
            if (body == null || claimedCount >= claimedBodies.Length) { return; }
            for (int i = 0; i < claimedCount; i++) {
                if (ReferenceEquals(claimedBodies[i], body)) { return; }
            }
            claimedBodies[claimedCount] = body;
            claimedWasSuppressed[claimedCount] = body.SuppressSmoothRotationCalls;
            body.SuppressSmoothRotationCalls = true;
            claimedCount++;
        }

        private static bool IsClaimed(PhysicsBody body) {
            for (int i = 0; i < claimedCount; i++) {
                if (ReferenceEquals(claimedBodies[i], body)) { return true; }
            }
            return false;
        }

        private static void ReleaseClaims() {
            for (int i = 0; i < claimedCount; i++) {
                claimedBodies[i].SuppressSmoothRotationCalls = claimedWasSuppressed[i];
                claimedBodies[i] = null;
            }
            claimedCount = 0;
        }

        private static void DriveMissing(AnimController anim, LimbType type, byte bit) {
            if ((drivenHands & bit) != 0) { return; }
            Limb hand = anim.GetLimb(type);
            Character c = anim.Character;
            if (hand?.body == null || hand.Disabled || c.Removed || c.IsDead || c.IsIncapacitated || c.LockHands || anim.IsHangingWithRope ||
                c.Inventory?.GetItemInLimbSlot(type == LimbType.LeftHand ? InvSlotType.LeftHand : InvSlotType.RightHand) != null) { return; }
            bool left = type == LimbType.LeftHand;
            RestPose restPose;
            if (runs.TryGetValue(c, out Run run) && run.Def.Pose != EmotePose.None) {
                if (!run.Posed) { Prepare(anim, run); }
                if (!(left == run.UseLeft ? run.Right : run.Left).Active) { return; }
                if (!run.Rest.Set) { run.Rest = CaptureRest(anim); }
                restPose = run.Rest;
            } else if (stances.TryGetValue(c, out Stance stance)) {
                if (!stance.Rest.Set) { stance.Rest = CaptureRest(anim); }
                restPose = stance.Rest;
            } else { return; }
            if (runs.TryGetValue(c, out Run activeRun) && activeRun.Def.Pose != EmotePose.None) {
                float t = (float)(Timing.TotalTime - activeRun.Start);
                float weight = activeRun.Stopping
                    ? activeRun.StopWeight * (1 - IkBlend.Ease((t - activeRun.StopAt) / Poses.Return))
                    : Poses.Weight(t, activeRun.FadeAt);
                // At zero weight the correct owner is the engine's ordinary
                // animation, not our cached rest target. Calling HandIK here
                // would pin the hand to the old pose and undo the release.
                if (weight <= 0.0f) { return; }
            }
            LimbJoint shoulder = left ? anim.leftShoulder : anim.rightShoulder;
            if (shoulder == null) { return; }
            Vector2 anchor = shoulder.WorldAnchorA;
            Vec2 target = IkBlend.Target(default, left ? restPose.Left : restPose.Right,
                new Vec2(anchor.X, anchor.Y), anim.GetLimb(LimbType.Torso)?.Rotation ?? 0,
                anim.Dir, MathF.Max(0.01f, anim.ArmLength - 0.01f), 1);
            Vector2 rest = new Vector2(target.X, target.Y);
            anim.HandIK(hand, rest, anim.CurrentAnimationParams.ArmIKStrength, anim.CurrentAnimationParams.HandIKStrength);
        }

        private static RestPose CaptureRest(AnimController anim) {
            float rotation = anim.GetLimb(LimbType.Torso)?.Rotation ?? 0;
            Vector2 right = (anim.GetLimb(LimbType.RightHand)?.SimPosition ?? Vector2.Zero) - (anim.rightShoulder?.WorldAnchorA ?? Vector2.Zero);
            Vector2 left = (anim.GetLimb(LimbType.LeftHand)?.SimPosition ?? Vector2.Zero) - (anim.leftShoulder?.WorldAnchorA ?? Vector2.Zero);
            return new RestPose { Set = true, Right = IkBlend.Local(new Vec2(right.X, right.Y), rotation, anim.Dir), Left = IkBlend.Local(new Vec2(left.X, left.Y), rotation, anim.Dir) };
        }

        public static void HandIKPrefix(AnimController __instance, Limb hand, ref Vector2 pos) {
            // Suppressed vanilla calls still pass through this prefix. They must not
            // make the postfix believe the hand was already animated, otherwise the
            // idle emotion would be skipped after the claim is released.
            if (ReferenceEquals(animating, __instance) && hand?.body != null && !IsClaimed(hand.body)) {
                if (hand.type == LimbType.RightHand) { drivenHands |= 1; }
                else if (hand.type == LimbType.LeftHand) { drivenHands |= 2; }
            }
            if (runs.Count == 0 && stances.Count == 0) { return; }
            Character c = __instance.Character;
            if (c == null || c.Removed || c.IsDead || c.IsIncapacitated || c.LockHands || __instance.IsHangingWithRope || hand?.body == null || hand.Disabled) { return; }
            bool left = hand.type == LimbType.LeftHand;
            if (!left && hand.type != LimbType.RightHand) { return; }
            if (c.Inventory?.GetItemInLimbSlot(left ? InvSlotType.LeftHand : InvSlotType.RightHand) != null) { return; }
            LimbJoint shoulder = left ? __instance.leftShoulder : __instance.rightShoulder;
            if (shoulder == null) { return; }
            float arm = __instance.ArmLength;
            float rotation = __instance.GetLimb(LimbType.Torso)?.Rotation ?? 0;
            Vec2 local;
            float weight;
            if (runs.TryGetValue(c, out Run r) && r.Def.Pose != EmotePose.None) {
                if (!r.Posed) { Prepare(__instance, r); }
                HandTrack track = left == r.UseLeft ? r.Right : r.Left;
                if (!track.Active) { return; }
                float t = (float)(Timing.TotalTime - r.Start);
                weight = r.Stopping ? r.StopWeight * (1 - IkBlend.Ease((t - r.StopAt) / Poses.Return)) : Poses.Weight(t, r.FadeAt);
                if (weight <= 0) { return; }
                track.Sample(r.Stopping ? r.StopAt : t, out local, out _);
                local += IkBlend.Sway(t, c.ID, arm);
            } else {
                if (!stances.TryGetValue(c, out Stance stance)) { return; }
                Poses.HandsUp(arm, out Vec2 right, out Vec2 leftTarget);
                local = (left ? leftTarget : right) + IkBlend.Sway((float)Timing.TotalTime, c.ID, arm);
                weight = IkBlend.Ease(stance.Weight);
                if (weight <= 0) { return; }
            }
            if (!MathUtils.IsValid(pos)) { return; }
            Vector2 anchor = shoulder.WorldAnchorA;
            Vec2 target = IkBlend.Target(new Vec2(pos.X, pos.Y), local, new Vec2(anchor.X, anchor.Y), rotation, __instance.Dir, MathF.Max(0.01f, arm - 0.01f), weight);
            pos = new Vector2(target.X, target.Y);
        }

        private static void Prepare(AnimController anim, Run r) {
            Limb rightHand = anim.GetLimb(r.UseLeft ? LimbType.LeftHand : LimbType.RightHand);
            Limb leftHand = anim.GetLimb(r.UseLeft ? LimbType.RightHand : LimbType.LeftHand);
            LimbJoint rs = r.UseLeft ? anim.leftShoulder : anim.rightShoulder;
            LimbJoint ls = r.UseLeft ? anim.rightShoulder : anim.leftShoulder;
            if (rightHand?.body == null || rs == null) { return; }
            float dir = anim.Dir;
            float arm = anim.ArmLength;
            float rotation = anim.GetLimb(LimbType.Torso)?.Rotation ?? 0;
            Limb head = anim.GetLimb(LimbType.Head);
            Vector2 headLocal = head?.body != null ? head.SimPosition - rs.WorldAnchorA : new Vector2(0.05f, 0.3f);
            Vector2 rl = rightHand.SimPosition - rs.WorldAnchorA;
            Vector2 ll = leftHand?.body != null && ls != null ? leftHand.SimPosition - ls.WorldAnchorA : rl;
            var frame = new PoseFrame {
                Arm = arm,
                Head = IkBlend.Local(new Vec2(headLocal.X, headLocal.Y), rotation, dir),
                RightStart = IkBlend.Local(new Vec2(rl.X, rl.Y), rotation, dir),
                LeftStart = IkBlend.Local(new Vec2(ll.X, ll.Y), rotation, dir)
            };
            EmoteSound s = r.Def.Sounds.Length > 0 ? r.Def.Sounds[r.Variant] : null;
            Poses.Build(r.Def.Pose, r.Def.Duration(r.Variant), s?.Beats ?? Array.Empty<float>(), frame, r.Right, r.Left);
            r.Posed = true;
        }
    }
}
