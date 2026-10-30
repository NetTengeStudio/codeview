using System;
using System.Collections.Generic;
using Barotrauma;
using FarseerPhysics.Dynamics;
using ScpItdMod.Core;
using ScpItdMod.Weather.Material;
using Vector2 = Microsoft.Xna.Framework.Vector2;

namespace ScpItdMod.Weather {
    // Characters meet the snow through their feet. The animation decides where a foot goes and how far the floor under it is; the material decides how far a loaded foot sinks,
    // what it has to push aside on the way and what it leaves behind. A foot that cannot get through slows the character by the force it is actually met with.
    internal static class SnowCharacters {
        // The weight a person puts on the snow. The ragdoll is not it: HumanDefaultRagdoll weighs about 10 kg, so a boot loaded with it presses ~1.5 kPa, less than the 180 kg/m3 pack already carries.
        internal const float BodyMass = PressLoad.BodyMass;
        private const float BootSpan = PressLoad.BootWidth, Memory = .3f, MaximumFrame = .1f;
        private sealed class Walker {
            internal readonly SnowFoot[] Feet = new SnowFoot[2];
            internal readonly float[] Sinkage = new float[2];
            internal float Resistance;
            internal uint Seen;
        }
        private static readonly Dictionary<Character, Walker> walkers = new Dictionary<Character, Walker>();
        private static readonly List<Character> gone = new List<Character>();
        private static uint serial;
        private static readonly bool[] stance = new bool[2];
        internal static int Feet { get; private set; }
        internal static int Standing { get; private set; }
        internal static readonly int[] Reasons = new int[4];
        internal static float Gap { get; private set; }
        // The controlled character's own feet, last frame: why each does or does not press and how far its sole lies above the snow.
        internal static readonly int[] YourReason = { -1, -1 };
        internal static readonly float[] YourGap = new float[2];
        internal static void Clear() { walkers.Clear(); Milliseconds = 0; }

        private static bool Foot(Character c, int side, out Vec2 sole, out float length, out float height) {
            Limb limb = c.AnimController?.GetLimb(side == 0 ? LimbType.LeftFoot : LimbType.RightFoot);
            Body body = limb?.body?.FarseerBody;
            return SnowFootGeometry.Read(body, c.Submarine?.Position ?? Vector2.Zero, WeatherService.UnitsPerMetre, out sole, out length, out height);
        }
        // Whether the walk animation has this foot on the ground (HumanoidAnimController.UpdateStanding): a character that stands still, or one the gait does not describe, stands on both.
        private static bool Stance(Character c, int side) {
            if (c.AnimController is not HumanoidAnimController human || !human.OnGround || MathUtils.NearlyEqual(human.TargetMovement.X, 0) || human.StepSize is not Vector2 step) return true;
            return SnowFoot.Stance(human.WalkPos, step.Y, human.FootMoveOffset.Y, side);
        }


        // A pressed footprint costs a few particles, so the only limit is a sanity cap: the characters nearest to a player. A character controlled from a client is stepped here as well (its
        // resistance sets its speed on the authority), but its footprints are pressed by that client and arrive through WeatherNetwork.
        private const int Budget = 16;
        internal static float Milliseconds { get; private set; }
        private static readonly List<Character> chosen = new List<Character>();
        private static readonly List<(float Distance, Character Who)> ranked = new List<(float, Character)>();
        private static void Select(IReadOnlyList<Character> people) {
            ranked.Clear(); chosen.Clear();
            for (int i = 0; i < people.Count; i++) {
                Character c = people[i];
                if (c.Removed || c.IsDead || !c.Enabled || c.AnimController == null || c.AnimController.InWater || c.AnimController.IsClimbing || !WeatherService.InZone(c.WorldPosition)) continue;
#if CLIENT
                if (!WeatherService.Authority && !ReferenceEquals(c, Character.Controlled)) continue;
#endif
                Vector2 foot = WeatherService.FootPoint(c);
                if (!SnowRuntime.Surface(new Vec2(foot.X, foot.Y) / WeatherService.UnitsPerMetre, SnowContactModel.FootAbove, SnowContactModel.FootBelow, out float low, out float high) || high - low < SnowSkin.MinimumDepth) continue;
                float nearest = float.MaxValue;
                for (int j = 0; j < people.Count; j++) {
                    Character other = people[j]; if (other.Removed || other.IsDead || !(ReferenceEquals(other, Character.Controlled) || other.IsRemotePlayer)) continue;
                    nearest = Math.Min(nearest, Vector2.DistanceSquared(other.WorldPosition, c.WorldPosition));
                }
                ranked.Add((nearest, c));
            }
            ranked.Sort((a, b) => a.Distance.CompareTo(b.Distance));
            for (int i = 0; i < ranked.Count && i < Budget; i++) chosen.Add(ranked[i].Who);
        }

        internal static void Step(SnowMpm material, float dt) {
            if (!float.IsFinite(dt) || dt <= 0) return;
            IReadOnlyList<Character> people = Humans.All; serial++;
            int count = 0, standing = 0; Array.Clear(Reasons); long begin = System.Diagnostics.Stopwatch.GetTimestamp();
            Select(people); YourReason[0] = YourReason[1] = -1;
            for (int i = 0; i < chosen.Count; i++) {
                Character c = chosen[i];
                if (!walkers.TryGetValue(c, out Walker walker)) walkers[c] = walker = new Walker();
                walker.Seen = serial;
                float mass = BodyMass;
                int carrying = 0;
                for (int side = 0; side < 2; side++) { stance[side] = Stance(c, side); if (stance[side]) carrying++; }
                for (int side = 0; side < 2; side++) {
                    if (!Foot(c, side, out Vec2 sole, out float length, out float height)) { walker.Feet[side] = null; continue; }
                    SnowFoot foot = walker.Feet[side];
                    if (foot == null) foot = walker.Feet[side] = new SnowFoot(length, height, BootSpan);
                    else foot.Resize(length, height);
                    foot.Mass = mass; foot.Load = stance[side] ? mass / carrying : 0;
                    foot.Step(material, Math.Min(dt, MaximumFrame), new SnowPosition(sole.X, sole.Y), stance[side], sole.Y - SnowContactModel.FootBelow, sole.Y + SnowContactModel.FootAbove, !c.IsRemotePlayer);
                    count++; if (foot.Planted) standing++; Reasons[foot.Reason]++; if (!float.IsNaN(foot.Gap)) Gap = foot.Gap;
                    if (ReferenceEquals(c, Character.Controlled)) { YourReason[side] = foot.Reason; YourGap[side] = foot.Gap; }
                }
            }
            gone.Clear(); foreach (var pair in walkers) if (pair.Value.Seen != serial) gone.Add(pair.Key);
            for (int i = 0; i < gone.Count; i++) walkers.Remove(gone[i]);
            Feet = count; Standing = standing;
            // The material itself moves only where a shovel or a deposit woke it.
            if (material.ActiveCount > 0)
                for (float remaining = Math.Min(dt, MaximumFrame); remaining > 1e-6f;) {
                    float chunk = Math.Min(remaining, SnowConstitutive.MaximumStep);
                    material.Step(chunk, Span<SnowRigid>.Empty);
                    remaining -= chunk;
                }
            Milliseconds += ((System.Diagnostics.Stopwatch.GetTimestamp() - begin) * 1000f / System.Diagnostics.Stopwatch.Frequency - Milliseconds) * Math.Min(1, dt / .25f);
            foreach (var pair in walkers) Resolve(pair.Key, pair.Value, dt);
        }

        // Resistance is the force the snow opposes to the legs swung forward through it; sinkage is how far the sole lies below the snow around it.
        private static void Resolve(Character c, Walker walker, float dt) {
            float force = 0;
            for (int side = 0; side < 2; side++) {
                SnowFoot foot = walker.Feet[side]; if (foot == null) { walker.Sinkage[side] = 0; continue; }
                Vec2 sole = new Vec2(foot.Sole.X, foot.Sole.Y);
                if (foot.Reason == 1) force += Wading(sole, foot.Height);
                float left = Top(sole.X - .45f, sole.Y), right = Top(sole.X + .45f, sole.Y);
                walker.Sinkage[side] = float.IsNaN(left) || float.IsNaN(right) ? 0 : Math.Max(0, (left + right) * .5f - sole.Y);
            }
            walker.Resistance += (force - walker.Resistance) * Math.Min(1, dt / Memory);
        }
        // A swinging leg pushes through the snow it is buried in with the crush strength of that snow on its projected area: the force of a post driven through the layer, which the flat solver cannot give.
        private static float Wading(Vec2 sole, float boot) {
            if (!SnowRuntime.Surface(sole, SnowContactModel.FootAbove, SnowContactModel.FootBelow, out float low, out float high)) return 0;
            float buried = Math.Clamp(high - sole.Y - boot, 0, SnowFoot.LegHeight); if (buried <= 0) return 0;
            float density = SnowRuntime.Pack.Material.TopDensity(sole.X, SnowSkin.Step * 2, low - .01f, high + .01f, high, .1f);
            return density > 0 ? SnowConstitutive.CrushStrength(density) * SnowFoot.LegWidth * buried : 0;
        }
        private static float Top(float x, float y) => SnowRuntime.Surface(new Vec2(x, y), SnowContactModel.FootAbove, SnowContactModel.FootBelow, out _, out float high) ? high : float.NaN;

        // Speed fraction of a character: the thrust a leg can give is a fixed share of body weight, so any resistance above it holds the walk back in proportion.
        internal static float Speed(Character c) {
            if (c == null || c.AnimController == null || c.AnimController.IsClimbing || !walkers.TryGetValue(c, out Walker walker)) return 1;
            return SnowWalkSpeed.Fraction(walker.Resistance, BodyMass);
        }
        internal static float SinkageAt(Vec2 point) {
            float best = 0, nearest = .16f;
            foreach (var pair in walkers) for (int side = 0; side < 2; side++) {
                if (pair.Value.Feet[side] == null) continue;
                Vec2 sole = new Vec2(pair.Value.Feet[side].Sole.X, pair.Value.Feet[side].Sole.Y); float d = Vec2.Distance(sole, point);
                if (d < nearest) { nearest = d; best = pair.Value.Sinkage[side]; }
            }
            return best;
        }
    }
}
