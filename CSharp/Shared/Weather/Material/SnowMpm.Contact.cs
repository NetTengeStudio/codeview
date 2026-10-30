using System;
using ScpItdMod.Core;

namespace ScpItdMod.Weather.Material;

internal sealed partial class SnowMpm
{
    private struct RigidContact { internal int Particle, Body, Shape; internal Vec2 Impulse; }
    private RigidContact[] contacts = new RigidContact[256];
    private Vec2[] terrainImpulses = new Vec2[4096];
    private SnowBoundary[] boundaries = new SnowBoundary[4096];
    private int contactCount, touchCount;
    internal int LastContacts => contactCount;
    internal int LastTouching => touchCount;
    private int[] touching = new int[4096];
    private Vec2 GridVelocity(int p, out float inverseMass)
    {
        Stencil(p); Vec2 velocity = default; inverseMass = 0;
        for (int j = 0; j < 9; j++)
        {
            int id = p * 9 + j; ref readonly Node node = ref nodes[particleNodes[id]];
            if (node.Mass <= 0) continue;
            float weight = nodeWeights[id]; velocity += node.Velocity * weight; inverseMass += weight * weight / node.Mass;
        }
        return velocity;
    }
    private void GridImpulse(int p, Vec2 impulse)
    {
        Stencil(p);
        for (int j = 0; j < 9; j++)
        {
            int id = p * 9 + j; ref Node node = ref nodes[particleNodes[id]];
            if (node.Mass > 0) node.Velocity += impulse * (nodeWeights[id] / node.Mass);
        }
    }
    // The distance to the terrain can fall by no more than the particle has travelled, so a sample far from every wall stays a valid lower bound and the geometry is queried again only when the particle comes within reach.
    private SnowBoundary Boundary(int p, float motion)
    {
        ref Status state = ref status[p]; float moved = SnowPosition.Distance(points[p].Position, state.Sampled), bound = SnowConstitutive.Spacing * .5f + motion + .06f;
        if (state.Clearance - moved > bound) return new SnowBoundary(state.Clearance - moved, default);
        // A particle that has not moved since the last query is at exactly the same distance and normal: the dormant snow around a moving body does not ask the terrain again every substep.
        if (moved == 0 && state.Normal != default) return new SnowBoundary(state.Clearance, state.Normal);
        SnowBoundary hit = terrain.Sample(points[p].Position); state.Clearance = hit.Distance; state.Sampled = points[p].Position; state.Normal = hit.Normal; return hit;
    }
    private float contactMotion; private int boundCount; private bool[] nearBody = new bool[4096];
    private SnowPosition[] boundCentres = new SnowPosition[8]; private float[] boundReach = new float[8];
    // Distance to the terrain for each participant, and whether any body is close enough to need the exact shape test; each range writes only its own entries.
    private void SetupBoundaries(int begin, int end)
    {
        float motion = contactMotion;
        for (int i = begin; i < end; i++)
        {
            int p = participants[i]; boundaries[i] = Boundary(p, motion);
            bool near = false;
            for (int k = 0; k < boundCount && !near; k++) near = SnowPosition.DistanceSquared(points[p].Position, boundCentres[k]) < boundReach[k];
            nearBody[i] = near;
        }
    }
    private void Contact(float dt, Span<SnowRigid> bodies)
    {
        if (boundaries.Length < participantCount) Reserve();
        Array.Clear(terrainImpulses, 0, participantCount); contactCount = 0; touchCount = 0;
        long c0 = System.Diagnostics.Stopwatch.GetTimestamp(); float motion = 0;
        for (int n = 0; n < nodeCount; n++) motion = MathF.Max(motion, nodes[n].Velocity.Length() * dt);
        contactMotion = motion; boundCount = 0;
        for (int b = 0; b < bodies.Length; b++)
        {
            if (bodies[b].Shapes == null) continue;
            for (int s = 0; s < bodies[b].Shapes.Length; s++)
            {
                if (boundCount == boundCentres.Length) { Array.Resize(ref boundCentres, boundCount * 2); Array.Resize(ref boundReach, boundCount * 2); }
                float bound = bodies[b].Shapes[s].Bound + SnowConstitutive.Spacing * .5f + motion;
                boundCentres[boundCount] = bodies[b].Position; boundReach[boundCount++] = bound * bound;
            }
        }
        if (participantCount > 0) terrain.Sample(points[participants[0]].Position);
        fork.Run(participantCount, 256, setupBoundaries);
        for (int i = 0; i < participantCount; i++)
        {
            if (boundaries[i].Distance < SnowConstitutive.Spacing * .5f + motion) touching[touchCount++] = i;
            if (!nearBody[i]) continue;
            int p = participants[i]; ref readonly SnowPoint point = ref points[p];
            for (int b = 0; b < bodies.Length; b++)
            {
                ref readonly SnowRigid body = ref bodies[b]; if (body.Shapes == null) continue;
                for (int s = 0; s < body.Shapes.Length; s++)
                {
                    float bound = body.Shapes[s].Bound + SnowConstitutive.Spacing * .5f + motion;
                    if (SnowPosition.DistanceSquared(point.Position, body.Position) >= bound * bound) continue;
                    if (body.Shapes[s].Sample(body.Local(point.Position)).Distance >= SnowConstitutive.Spacing * .5f + motion + .01f) continue;
                    if (contactCount == contacts.Length) Array.Resize(ref contacts, checked(contacts.Length * 2));
                    contacts[contactCount++] = new RigidContact { Particle = p, Body = b, Shape = s };
                }
            }
        }
        long c1 = System.Diagnostics.Stopwatch.GetTimestamp(); Profile[7] += c1 - c0;
        for (int iteration = 0; iteration < 8; iteration++)
        {
            for (int t = 0; t < touchCount; t++)
            {
                int i = touching[t], p = participants[i]; ref readonly SnowPoint point = ref points[p]; ref readonly SnowBoundary hit = ref boundaries[i];
                float radius = SnowConstitutive.Spacing * .5f;
                if (hit.Distance >= radius + motion && terrainImpulses[i] == Vec2.Zero) continue;
                Vec2 velocity = GridVelocity(p, out float inverse); if (inverse <= 0) continue;
                float gap = hit.Distance + Vec2.Dot(velocity, hit.Normal) * dt - radius;
                ref Vec2 accumulated = ref terrainImpulses[i]; if (gap >= 0 && accumulated == Vec2.Zero) continue;
                float previous = Vec2.Dot(accumulated, hit.Normal);
                float normal = MathF.Max(0, previous - gap / (dt * inverse));
                GridImpulse(p, hit.Normal * (normal - previous)); velocity = GridVelocity(p, out _);
                Vec2 tangent = new(-hit.Normal.Y, hit.Normal.X);
                float beforeTangent = Vec2.Dot(accumulated, tangent), friction = .5f * point.SolidFraction;
                float tangential = Math.Clamp(beforeTangent - Vec2.Dot(velocity, tangent) / inverse, -friction * normal, friction * normal);
                GridImpulse(p, tangent * (tangential - beforeTangent)); accumulated = hit.Normal * normal + tangent * tangential;
            }
            for (int i = 0; i < contactCount; i++)
            {
                ref RigidContact contact = ref contacts[i]; ref SnowRigid body = ref bodies[contact.Body];
                SolveRigid(contact.Particle, body.Shapes[contact.Shape], ref body, ref contact.Impulse, dt);
            }
        }
        Profile[8] += System.Diagnostics.Stopwatch.GetTimestamp() - c1;
    }
    private void SolveRigid(int p, SnowCollider shape, ref SnowRigid body, ref Vec2 accumulated, float dt)
    {
        Vec2 velocity = GridVelocity(p, out float inverse); if (inverse <= 0) return;
        SnowPosition next = points[p].Position + velocity * dt;
        SnowBoundary hit = shape.Sample(body.Local(next)); float gap = hit.Distance - SnowConstitutive.Spacing * .5f;
        if (gap >= 0 && accumulated == Vec2.Zero) return;
        float c = MathF.Cos(body.Rotation), s = MathF.Sin(body.Rotation);
        Vec2 normal = new(c * hit.Normal.X - s * hit.Normal.Y, s * hit.Normal.X + c * hit.Normal.Y);
        float inverseMass = body.ContactInverseMass, inverseInertia = body.ContactInverseInertia;
        Vec2 arm = next - body.Position;
        float angular = arm.X * normal.Y - arm.Y * normal.X;
        float previousNormal = Vec2.Dot(accumulated, normal);
        float updatedNormal = MathF.Max(0, previousNormal - gap / (dt * (inverse + inverseMass + angular * angular * inverseInertia)));
        ApplyRigidImpulse(p, ref body, arm, normal * (updatedNormal - previousNormal), dt);
        velocity = GridVelocity(p, out _); Vec2 tangent = new(-normal.Y, normal.X);
        Vec2 relative = velocity - ((body.Position - body.Previous) + new Vec2(-arm.Y, arm.X) * (body.Rotation - body.PreviousRotation)) / dt;
        float previousTangent = Vec2.Dot(accumulated, tangent); angular = arm.X * tangent.Y - arm.Y * tangent.X;
        float friction = .45f * points[p].SolidFraction;
        float updatedTangent = Math.Clamp(previousTangent - Vec2.Dot(relative, tangent) / (inverse + inverseMass + angular * angular * inverseInertia), -friction * updatedNormal, friction * updatedNormal);
        ApplyRigidImpulse(p, ref body, arm, tangent * (updatedTangent - previousTangent), dt);
        accumulated = normal * updatedNormal + tangent * updatedTangent;
    }
    private void ApplyRigidImpulse(int p, ref SnowRigid body, Vec2 arm, Vec2 impulse, float dt)
    {
        GridImpulse(p, impulse); body.Position -= impulse * (body.ContactInverseMass * dt); body.Impulse -= impulse;
        float angular = arm.X * impulse.Y - arm.Y * impulse.X;
        body.Rotation -= angular * body.ContactInverseInertia * dt; body.AngularImpulse -= angular;
    }
}
