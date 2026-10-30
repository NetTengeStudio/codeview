using System;
using System.Collections.Generic;
using FarseerPhysics;
using FarseerPhysics.Collision.Shapes;
using FarseerPhysics.Dynamics;
using HarmonyLib;
using ScpItdMod.Weather.Material;
using N = ScpItdMod.Core;
using X = Microsoft.Xna.Framework;

namespace ScpItdMod.Weather;

internal sealed class SnowBodyAdapter
{
    private static readonly AccessTools.FieldRef<Body, X.Vector2> force = AccessTools.FieldRefAccess<Body, X.Vector2>("_force");
    private static readonly AccessTools.FieldRef<Body, float> torque = AccessTools.FieldRefAccess<Body, float>("_torque");
    private static readonly AccessTools.FieldRef<Body, float> inverseInertia = AccessTools.FieldRefAccess<Body, float>("_invI");
    private readonly Body native;
    private readonly float impulseScale;
    private SnowCollider[] shapes = Array.Empty<SnowCollider>();
    private ulong geometryStamp;
    private uint frameSerial;
    private bool frameOpen;
    internal Body Native => native;
    internal SnowBodyAdapter(Body native, float span, float massScale = 1)
    {
        this.native = native ?? throw new ArgumentNullException(nameof(native));
        if (!float.IsFinite(span) || span <= 0) throw new ArgumentOutOfRangeException(nameof(span));
        if (!float.IsFinite(massScale) || massScale <= 0 || !float.IsFinite(span / massScale) || span / massScale <= 0) throw new ArgumentOutOfRangeException(nameof(massScale));
        impulseScale = span / massScale;
    }
    internal bool TryBegin(float dt, N.Vec2 offset, out SnowRigid body)
    {
        if (!float.IsFinite(dt) || dt <= 0 || !float.IsFinite(offset.X) || !float.IsFinite(offset.Y)) throw new ArgumentException("Invalid snow frame");
        body = default;
        if (frameOpen) throw new InvalidOperationException("Previous snow body frame was not committed");
        if (!native.Enabled || native.World == null) return false;
        if (native.World.IsLocked) throw new InvalidOperationException("Snow contact must run before native integration");
        if (native.BodyType != BodyType.Dynamic) return false;
        native.Awake = true;
        ulong stamp = ShapeStamp(); if (shapes.Length == 0 || stamp != geometryStamp) { RebuildShapes(); geometryStamp = stamp; }
        if (shapes.Length == 0) return false;
        X.Vector2 velocity = native.LinearVelocity, applied = force(native);
        float inverse = 1 / native.Mass, inverseI = inverseInertia(native);
        X.Vector2 gravity = native.IgnoreGravity ? X.Vector2.Zero : native.World.Gravity * native.GravityScale;
        velocity += (gravity + applied * inverse) * dt;
        float linearDamping = Math.Clamp(1 - dt * native.LinearDamping, 0, 1), angularDamping = Math.Clamp(1 - dt * native.AngularDamping, 0, 1);
        velocity *= linearDamping;
        float angular = (native.AngularVelocity + torque(native) * inverseI * dt) * angularDamping;
        X.Vector2 centre = native.WorldCenter;
        SnowPosition previous = new SnowPosition(centre.X, centre.Y) + offset;
        N.Vec2 free = new N.Vec2(velocity.X, velocity.Y);
        body = new SnowRigid { Position = previous, Previous = previous, FrameOrigin = previous,
            Rotation = native.Rotation, PreviousRotation = native.Rotation, FrameRotation = native.Rotation,
            Velocity = free, FreeVelocity = free, AngularVelocity = angular, FreeAngularVelocity = angular,
            InverseMass = inverse * impulseScale, InverseInertia = inverseI * impulseScale, Shapes = shapes,
            FrameSeconds = dt, LinearResponseSeconds = dt * linearDamping, AngularResponseSeconds = dt * angularDamping };
        body.NativeFrame = ++frameSerial; frameOpen = true;
        return true;
    }
    internal void Commit(in SnowRigid body)
    {
        if (!frameOpen || body.NativeFrame != frameSerial) throw new InvalidOperationException("Snow rigid frame is stale or was already committed");
        if (native.World == null || !native.Enabled) { frameOpen = false; return; }
        if (native.World.IsLocked) throw new InvalidOperationException("Snow impulse must precede native integration");
        if (!float.IsFinite(body.Impulse.X) || !float.IsFinite(body.Impulse.Y) || !float.IsFinite(body.AngularImpulse) ||
            !float.IsFinite(body.Position.X) || !float.IsFinite(body.Position.Y) || !float.IsFinite(body.Rotation) ||
            !float.IsFinite(body.FrameElapsed) || !float.IsFinite(body.FrameSeconds) || body.FrameSeconds <= 0)
            throw new ArgumentException("Nonfinite snow frame cannot be committed", nameof(body));
        if (MathF.Abs(body.FrameElapsed - body.FrameSeconds) > body.FrameSeconds * .00001f) throw new InvalidOperationException("Snow rigid frame is incomplete");
        SnowPosition predicted = body.FrameOrigin + body.FreeVelocity * body.FrameSeconds + body.Impulse * (body.InverseMass * body.LinearResponseSeconds);
        N.Vec2 correction = body.Position - predicted;
        float angleCorrection = body.Rotation - (body.FrameRotation + body.FreeAngularVelocity * body.FrameSeconds + body.AngularImpulse * body.InverseInertia * body.AngularResponseSeconds);
        if (correction != N.Vec2.Zero || angleCorrection != 0)
            SnowNativeMotion.Queue(native, new X.Vector2(correction.X, correction.Y), angleCorrection, body.FrameSeconds);
        native.ApplyLinearImpulse(new X.Vector2(body.Impulse.X * impulseScale, body.Impulse.Y * impulseScale));
        native.ApplyAngularImpulse(body.AngularImpulse * impulseScale);
        frameOpen = false;
    }
    private ulong ShapeStamp()
    {
        ulong hash = 14695981039346656037;
        X.Vector2 centre = native.LocalCenter;
        hash = Hash(hash, centre.X); hash = Hash(hash, centre.Y);
        for (int i = 0; i < native.FixtureList.Count; i++)
        {
            Fixture fixture = native.FixtureList[i]; hash = (hash ^ (fixture.IsSensor ? 1UL : 0UL)) * 1099511628211;
            if (fixture.IsSensor) continue;
            if (fixture.Shape is CircleShape circle)
            {
                hash = (hash ^ 1) * 1099511628211; hash = Hash(hash, circle.Radius); hash = Hash(hash, circle.Position.X); hash = Hash(hash, circle.Position.Y);
            }
            else if (fixture.Shape is PolygonShape polygon)
            {
                hash = (hash ^ (ulong)polygon.Vertices.Count) * 1099511628211;
                for (int v = 0; v < polygon.Vertices.Count; v++) { hash = Hash(hash, polygon.Vertices[v].X); hash = Hash(hash, polygon.Vertices[v].Y); }
            }
            else throw new NotSupportedException("Snow rigid contact requires circles or convex polygons");
        }
        return hash;
    }
    private static ulong Hash(ulong hash, float value) => (hash ^ BitConverter.SingleToUInt32Bits(value)) * 1099511628211;
    private void RebuildShapes()
    {
        var result = new List<SnowCollider>(native.FixtureList.Count); X.Vector2 centre = native.LocalCenter;
        for (int i = 0; i < native.FixtureList.Count; i++)
        {
            Fixture fixture = native.FixtureList[i]; if (fixture.IsSensor) continue;
            if (fixture.Shape is CircleShape circle) result.Add(new SnowCollider(new N.Vec2(circle.Position.X - centre.X, circle.Position.Y - centre.Y), circle.Radius));
            else if (fixture.Shape is PolygonShape polygon)
            {
                N.Vec2[] vertices = new N.Vec2[polygon.Vertices.Count];
                for (int v = 0; v < vertices.Length; v++) vertices[v] = new N.Vec2(polygon.Vertices[v].X - centre.X, polygon.Vertices[v].Y - centre.Y);
                result.Add(new SnowCollider(vertices));
            }
            else throw new NotSupportedException("Snow rigid contact requires circles or convex polygons");
        }
        shapes = result.ToArray();
    }
}
