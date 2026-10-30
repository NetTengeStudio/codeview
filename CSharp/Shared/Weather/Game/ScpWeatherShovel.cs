using System;
using Barotrauma;
using Barotrauma.Items.Components;
using Microsoft.Xna.Framework;
using ScpItdMod.Core;
using ScpItdMod.Weather.Material;
#if CLIENT
using Barotrauma.Particles;
using Microsoft.Xna.Framework.Graphics;
#endif

namespace ScpItdMod.Weather {
    // The snow shovel. Use starts a stroke; the snow is worked at the moment the blade gets there (ShovelMotion.Contact), from where the cursor is then:
    // a dig cuts into the snow under the cursor as deep as the stroke's work can cut that snow and keeps the slice on the blade; with Aim held the stroke tips the
    // load out over the snow under the cursor when that is within a hand (it streams off as the blade passes the angle the snow can no longer hold) and throws it
    // towards the cursor when it is not, at the speed the blade's edge actually reaches. The load is carried as mass per metre of span, 12 kg at most, is seen
    // on the blade, and is poured out where the shovel is dropped. The server decides what a stroke does and tells every peer, which plays the same motion
    // (ShovelRig) from the same parameters; the controlling client starts its stroke at once and takes the server's parameters when they arrive.
    public sealed class ScpWeatherShovel : ItemComponent
#if CLIENT
        , IDrawableComponent
#endif
    {
        public const float BladeSpan = .35f, Bite = .4f, CutDepth = .2f, CapacityKg = 12, WorkWatts = 160, HandReach = 1.9f;
        // Distance from the hands to the middle of the blade (m): the arm of the blade's swing.
        private const float BladeArm = .6f, StreamAngle = 30, StreamSpan = 40;
        private SnowPayload payload;
        private readonly Holdable holdable;
        private readonly Vector2 holdPosition, aimPosition;
        private readonly float holdAngle, aimAngle;
        private readonly ShovelRig rig = new ShovelRig();
        private ShovelStroke stroke; private float elapsed, duration, bite = 1, strokeLoad, poured; private bool acted, moved; private Character striker;
        private SnowPayload pouring; private double pouringTotal; private Vec2 pourAt;
        // The load share every peer shows on the blade: the server's own, sent with each stroke and each change.
        private float shownLoad;
        public ScpWeatherShovel(Item item, ContentXElement element) : base(item, element) {
            IsActive = true;
            holdable = item.GetComponent<Holdable>() ?? throw new InvalidOperationException("Snow shovel requires a Holdable component");
            holdPosition = holdable.HoldPos; aimPosition = holdable.AimPos;
            holdAngle = holdable.HoldAngle; aimAngle = holdable.AimAngle;
        }
        private Vec2 Position => new Vec2(item.WorldPosition.X, item.WorldPosition.Y) / WeatherService.UnitsPerMetre;
        private float LoadShare => Math.Clamp((float)payload.Mass * BladeSpan / CapacityKg, 0, 1);
        private Character Holder => item.GetRootInventoryOwner() as Character;
        private void Restore() {
            if (!moved) return;
            holdable.HoldPos = holdPosition; holdable.AimPos = aimPosition; holdable.HoldAngle = holdAngle; holdable.AimAngle = aimAngle; moved = false; rig.Reset();
        }
        private void End() { if (pouring.Mass > 0 && WeatherService.Authority) { stroke = ShovelStroke.None; Stream(); } stroke = ShovelStroke.None; striker = null; }
        // What the cursor means to the shovel right now: the snow surface it points at and whether that is within a hand and in sight.
        private bool Target(Character character, out Vec2 origin, out Vec2 cursor, out Vec2 surface, out float density) {
            origin = new Vec2(character.WorldPosition.X, character.WorldPosition.Y) / WeatherService.UnitsPerMetre;
            cursor = new Vec2(character.CursorWorldPosition.X, character.CursorWorldPosition.Y) / WeatherService.UnitsPerMetre;
            surface = cursor; density = SnowPhysics.FreshDensity;
            return SnowRuntime.Ready && SnowShovel.Aim(SnowRuntime.Pack, cursor, out surface, out density) && Vec2.DistanceSquared(origin, surface) <= HandReach * HandReach
                && WeatherService.Grid.SegmentClear(origin, new Vec2(surface.X, surface.Y + .01f));
        }
        private bool Full => payload.Mass >= CapacityKg / BladeSpan - SnowConstitutive.FreshDensity * SnowConstitutive.Area * .5f;
        // Work density of cutting and lifting this snow (J/m3), and the share of the full cut a stroke's strike can make in it: snow that takes more
        // work than the strike carries stops the blade short.
        private double WorkDensity(Vec2 surface, float density) {
            Vector2 strength = WeatherBov.SnowStrength(SnowPhysics.Compaction(density));
            float lift = MathF.Max(0, item.WorldPosition.Y / WeatherService.UnitsPerMetre - surface.Y);
            return SnowPhysics.ExcavationWorkDensity(density, CutDepth, lift, strength.X, strength.Y);
        }
        private float StrikeBite(Vec2 surface, float density) {
            double strike = WorkWatts * ShovelMotion.Duration(ShovelStroke.Dig, LoadShare) * .5;
            double depth = strike / (WorkDensity(surface, density) * BladeSpan * Bite);
            return (float)Math.Clamp(depth / CutDepth, .08, 1);
        }

        public override void Update(float deltaTime, Camera cam) {
            float dt = MathF.Min(MathF.Max(0, deltaTime), .05f);
            if (dt <= 0) return;
            Character holder = Holder;
            if (holder == null || item.ParentInventory == null) { End(); Restore(); Spill(); return; }
            float load = WeatherService.Authority ? LoadShare : shownLoad;
            if (stroke != ShovelStroke.None && (striker == null || striker.IsDead || striker.IsIncapacitated || !striker.HasEquippedItem(item))) End();
            if (stroke != ShovelStroke.None) {
                elapsed += dt; float share = Math.Clamp(elapsed / duration, 0, 1);
                ShovelMotion.Drive(rig, stroke, share, strokeLoad, bite, dt);
                if (!acted && share >= ShovelMotion.Contact(stroke)) {
                    acted = true;
                    if (stroke == ShovelStroke.Dig && bite < 1) rig.Recoil(1 - bite);
                    if (WeatherService.Authority) Work(striker);
                }
                if (stroke == ShovelStroke.Pour && acted && WeatherService.Authority) Stream();
                if (elapsed >= duration && rig.Settled(1.5f) || elapsed >= duration + .5f) End();
            } else {
                Vector2 velocity = holder.AnimController?.Collider?.LinearVelocity ?? Vector2.Zero;
                ShovelMotion.Rest(load, Timing.TotalTime, velocity.X * (holder.AnimController?.Dir ?? 1), out float tx, out float ty, out float ta);
                rig.Step(dt, tx, ty, ta, ShovelMotion.LoadedFrequency(load), ShovelMotion.Damping, 0);
            }
            Vector2 shift = new Vector2(rig.X, rig.Y);
            holdable.HoldPos = holdPosition + shift; holdable.AimPos = aimPosition + shift; holdable.HoldAngle = holdAngle + rig.Angle; holdable.AimAngle = aimAngle + rig.Angle; moved = true;
        }
        // A load left on a shovel that is dropped or destroyed is poured out where it lies.
        private void Spill() {
            if (payload.Mass <= 0 || !WeatherService.Authority || !SnowRuntime.Ready) return;
            Bath.BathModule.Accept(Position, ref payload);
            if (payload.Mass > 0 && (!SnowShovel.Pour(SnowRuntime.Pack, WeatherService.Current.Temperature, Position, Bite, ref payload) || payload.Mass > 0)) SnowShovel.Export(ref payload);
            Announce(ShovelStroke.None);
            WeatherNetwork.SnowChanged();
        }
        public override void RemoveComponentSpecific() { Spill(); base.RemoveComponentSpecific(); }
        public override bool Use(float deltaTime, Character character = null) {
            if (character == null || character.IsDead || character.IsIncapacitated || !character.HasEquippedItem(item) || !WeatherService.Active || stroke != ShovelStroke.None || item.Condition <= 0) return false;
            bool near = Target(character, out _, out _, out Vec2 surface, out float density), aiming = character.IsKeyDown(InputType.Aim), server = WeatherService.Authority;
            float load = server ? LoadShare : shownLoad;
            // The server knows the load and refuses a stroke that could do nothing; a client plays what the aim and the load it was told suggest.
            ShovelStroke kind = aiming ? (load <= 0 ? ShovelStroke.None : near ? ShovelStroke.Pour : ShovelStroke.Throw) : near && !(server && Full) ? ShovelStroke.Dig : ShovelStroke.None;
            if (kind == ShovelStroke.None) return false;
            Begin(kind, kind == ShovelStroke.Dig && server ? StrikeBite(surface, density) : 1, load, character);
            if (server) Announce(kind);
            return true;
        }
        private void Begin(ShovelStroke kind, float strikeBite, float load, Character character) {
            stroke = kind; striker = character; elapsed = 0; acted = false; bite = strikeBite; strokeLoad = load; poured = 0;
            duration = ShovelMotion.Duration(kind, load);
        }
        // A stroke or a new load as the server decided it, on a peer: a stroke the controlling client already predicted keeps its timing and takes the server's bite and load.
        internal void Remote(ShovelStroke kind, float strikeBite, float load, Character character) {
            shownLoad = load;
            if (kind == ShovelStroke.None || WeatherService.Authority) return;
            if (stroke == kind && ReferenceEquals(striker, character)) { bite = strikeBite; strokeLoad = load; return; }
            Begin(kind, strikeBite, load, character);
        }
        private void Announce(ShovelStroke kind) {
            shownLoad = LoadShare;
            WeatherNetwork.Shovel(item, kind, bite, shownLoad, kind == ShovelStroke.None ? null : striker);
        }

        // The blade has arrived: change the snow. Runs on the server only, with the cursor and the character where they are now.
        private void Work(Character character) {
            if (!SnowRuntime.Ready || character == null) return;
            SnowPack pack = SnowRuntime.Pack; bool near = Target(character, out Vec2 origin, out Vec2 cursor, out Vec2 surface, out float density);
            switch (stroke) {
                case ShovelStroke.Dig: {
                    if (!near || Full) return;
                    // The work of a stroke bounds what it can lift: denser snow is cut thinner, and snow lifted higher costs more.
                    double energyMass = WorkWatts * ShovelMotion.Duration(ShovelStroke.Dig, 0) * density / (WorkDensity(surface, density) * BladeSpan);
                    double capacity = CapacityKg / BladeSpan - payload.Mass;
                    SnowPayload scoop = SnowShovel.Dig(pack, surface, Bite, CutDepth * bite, Math.Min(capacity, energyMass));
                    if (scoop.Mass <= 0) return;
                    payload.Add(scoop);
                    WeatherNetwork.SnowBurst(surface, new Vec2(0, 1.2f + 1.5f * bite), MathF.Min(.4f, (float)scoop.Mass * BladeSpan * .05f));
                    break;
                }
                case ShovelStroke.Pour:
                    if (!near || payload.Mass <= 0) return;
                    pouring = payload; pouringTotal = payload.Mass; payload = default; pourAt = surface;
                    return;
                case ShovelStroke.Throw: {
                    if (payload.Mass <= 0) return;
                    Vec2 direction = Vec2.Normalize(cursor - origin); if (direction.LengthSquared() < .5f) direction = Vec2.UnitY;
                    float kilograms = (float)payload.Mass * BladeSpan;
                    float movingMass = kilograms + (item.body?.FarseerBody.Mass ?? 1) +
                        (character.AnimController?.GetLimb(LimbType.LeftHand)?.body?.FarseerBody.Mass ?? 0) + (character.AnimController?.GetLimb(LimbType.RightHand)?.body?.FarseerBody.Mass ?? 0);
                    // The snow leaves at the speed the blade's edge has when it lets go, no faster than the stroke's work can give the load and the tool.
                    float edge = MathF.Abs(rig.AngularVelocity) * MathF.PI / 180 * BladeArm + MathF.Sqrt(rig.SpeedX * rig.SpeedX + rig.SpeedY * rig.SpeedY) / WeatherService.UnitsPerMetre;
                    float energy = MathF.Sqrt(2 * WorkWatts * ShovelMotion.Duration(ShovelStroke.Throw, 0) / MathF.Max(.1f, movingMass));
                    Vec2 velocity = direction * MathF.Min(edge, energy);
                    if (!WeatherService.Flights.Launch(origin, velocity, ref payload, BladeSpan)) return;
                    WeatherNetwork.SnowBurst(origin, velocity, kilograms);
                    break;
                }
                default: return;
            }
            Announce(ShovelStroke.None);
            WeatherNetwork.SnowChanged();
        }
        // A tipped load leaves the blade as the blade passes the angle at which the snow can no longer hold on it: the share poured follows the tilt, so the
        // snow runs off in a stream over a few frames instead of dropping as one block.
        private void Stream() {
            if (pouring.Mass <= 0 || !SnowRuntime.Ready) return;
            float share = Math.Clamp((rig.Angle - StreamAngle) / StreamSpan, 0, 1);
            if (stroke == ShovelStroke.None || elapsed >= duration) share = 1;
            if (share <= poured) return;
            double part = share >= 1 ? pouring.Mass : Math.Min(pouring.Mass, pouringTotal * (share - poured));
            var portion = new SnowPayload { Mass = part, Volume = pouring.Volume * Math.Min(1, part / pouring.Mass) };
            pouring.Mass -= portion.Mass; pouring.Volume -= portion.Volume; poured = share;
            Bath.BathModule.Accept(pourAt, ref portion);
            if (portion.Mass > 0) SnowShovel.Pour(SnowRuntime.Pack, WeatherService.Current.Temperature, pourAt, Bite * .5f, ref portion);
            payload.Add(portion);
            if (share >= 1) { payload.Add(pouring); pouring = default; }
            WeatherNetwork.SnowBurst(new Vec2(pourAt.X, pourAt.Y + .3f), new Vec2(0, -.6f), MathF.Min(.4f, (float)(part * BladeSpan * .05)));
            Announce(ShovelStroke.None);
            WeatherNetwork.SnowChanged();
        }

#if CLIENT
        // The load is seen on the blade: a mound of snow whose size follows the mass it carries, sitting on the face of the blade and tipping with it.
        public Vector2 DrawSize => new Vector2(140);
        private static Sprite lump;
        public void Draw(SpriteBatch spriteBatch, bool editing, float itemDepth = -1, Color? overrideColor = null) {
            if (shownLoad <= .01f || item.body == null || item.HiddenInGame) return;
            if (lump == null) { if (!ParticlePrefab.Prefabs.TryGet("scpitd_snow_clump".ToIdentifier(), out ParticlePrefab prefab) || prefab.Sprites.Count == 0) return; lump = prefab.Sprites[0]; }
            float rotation = item.body.DrawRotation, dir = item.body.Dir, depth = (itemDepth < 0 ? item.GetDrawDepth() : itemDepth) - .0005f;
            Vector2 along = new Vector2(MathF.Cos(rotation), MathF.Sin(rotation)), up = new Vector2(-along.Y, along.X) * dir;
            Vector2 blade = item.body.DrawPosition - along * 36 * item.Scale * 2 * dir;
            float size = 9 + 9 * MathF.Sqrt(shownLoad);
            for (int k = 0; k < 3; k++) {
                float offset = (k - 1) * size * .55f, rise = (1 - MathF.Abs(k - 1) * .45f) * size * .45f;
                Vector2 at = blade + along * offset + up * rise;
                float scale = size * (k == 1 ? 1.15f : .85f) / lump.SourceRect.Width;
                lump.Draw(spriteBatch, new Vector2(at.X, -at.Y), new Color(232, 240, 247), lump.Origin, -rotation + k * .9f, scale, SpriteEffects.None, depth);
            }
        }
#endif
    }
}
