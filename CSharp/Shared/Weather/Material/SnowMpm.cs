using System;
using System.Collections.Generic;
using ScpItdMod.Core;

namespace ScpItdMod.Weather.Material;

internal struct SnowPoint
{
    internal SnowPosition Position, Previous;
    internal Vec2 Velocity;
    internal SnowMatrix Elastic, Affine;
    internal double Mass, Enthalpy;
    internal float ReferenceVolume, Plastic, Bond, Refrozen, Stain;
    internal float ReferenceDensity => (float)(Mass / ReferenceVolume);
    internal float Density => (float)(Mass / (ReferenceVolume * Plastic * Elastic.Determinant));
    internal float Water => (float)Math.Clamp(Enthalpy / SnowConstitutive.LatentHeat, 0, 1);
    internal float SolidFraction => 1 - Water;
    internal float Radius => SnowConstitutive.Radius(ReferenceVolume * Plastic * Elastic.Determinant);
    internal float Temperature => (float)(Enthalpy < 0 ? Enthalpy / SnowConstitutive.IceHeat : Enthalpy <= SnowConstitutive.LatentHeat ? 0 : (Enthalpy - SnowConstitutive.LatentHeat) / SnowConstitutive.WaterHeat);
}

internal sealed partial class SnowMpm
{
    internal const float CellSize = .12f, MaximumStep = 1 / 480f;
    private const float InverseCell = 1 / CellSize;
    // Below a centimetre per second and a tenth of a strain per second the snow is at rest for play: it neither shows nor feels the motion, and waking its neighbours would make a settled pack chain-wake from mm/s numerical creep.
    internal const float QuietSpeed = .05f, QuietAcceleration = .5f, QuietStrain = .5f, RestSeconds = .25f;
    private struct Link { internal int Cell, Next, Previous; internal float Weight; }
    private struct Status { internal int ActiveSlot; internal uint Stamp; internal uint Stencil; internal float Still, Awake, Clearance; internal SnowPosition Sampled; internal bool Frozen; internal Vec2 Normal; internal SnowMatrix TransferStress; internal uint PinStamp, PinEpoch; internal bool Pinned; }
    private struct Cell
    {
        internal int X, Y, Head, N0, N1, N2, N3;
        internal uint NodeStamp, ScanStamp, CentreStamp;
        internal Vec2 CentreVelocity; internal SnowMatrix CentreGradient;
        internal double Mass, Px, Py, Ga, Gb, Gc, Gd, Ta, Tb, Tc, Td;
    }
    private struct Node { internal int X, Y; internal float Mass; internal Vec2 Velocity; }
    private readonly SnowIndex cellIndex = new(4096);
    private readonly SnowIndex nodeIndex = new(4096);
    private SnowPaged<Cell> cells;
    private SnowPaged<Node> nodes;
    private SnowPoint[] points;
    private Link[] links;
    private Status[] status;
    private int[] active, participants, particleNodes;
    private SnowPaged<int> freeCells;
    private float[] nodeWeights;
    private int[] freePoints = new int[64]; private int freePointCount;
    private int pointCount, cellCount, nodeCount, activeCount, participantCount, freeCellCount;
    private uint stamp;
    private ISnowTerrain terrain;
    private readonly Vec2 gravity;
    internal bool SleepingEnabled { get; set; } = true;
    // Raised when a particle is created, removed or put somewhere else outside the solver, so replication knows where the material changed for a reason other than motion.
    internal Action<Vec2> Touched;
    // Raised when a press (SnowPrint) compacts a particle: a change that every replica reproduces from the same press, so replication only has to confirm it.
    internal Action<Vec2> Pressed;
    // Picture and floor are rebuilt only where the material changed: every metre of the map has a revision that advances when a particle there moves, is created or is removed.
    internal const float ChunkMetres = 1;
    private uint[] chunkRevision = new uint[64]; private int chunkBase;
    private uint RevisionOf(int chunk) { int at = chunk - chunkBase; return (uint)at < (uint)chunkRevision.Length ? chunkRevision[at] : 0; }
    private readonly List<int> restedChunks = new();
    private static int ChunkOf(double x) => (int)Math.Floor(x / ChunkMetres);
    private void Mark(int chunk)
    {
        int at = chunk - chunkBase;
        if ((uint)at >= (uint)chunkRevision.Length)
        {
            // The table covers the chunks touched so far and grows toward the one that missed, keeping a revision where it was.
            int low = Math.Min(chunkBase, chunk), high = Math.Max(chunkBase + chunkRevision.Length, chunk + 1), size = chunkRevision.Length;
            while (size < high - low) size *= 2;
            if (chunk < chunkBase) low = chunkBase + chunkRevision.Length - size;
            var grown = new uint[size]; Array.Copy(chunkRevision, 0, grown, chunkBase - low, chunkRevision.Length);
            chunkRevision = grown; chunkBase = low; at = chunk - chunkBase;
        }
        chunkRevision[at]++;
    }
    internal void Touch(Vec2 at) { Mark(ChunkOf(at.X)); Touched?.Invoke(at); }
    internal ulong Stamp(float left, float right)
    {
        ulong hash = 14695981039346656037;
        for (int chunk = ChunkOf(left), last = ChunkOf(right); chunk <= last; chunk++) hash = (hash ^ RevisionOf(chunk)) * 1099511628211;
        return hash;
    }
    internal ReadOnlySpan<int> Awake => active.AsSpan(0, activeCount);
    internal int Count => pointCount;
    internal int ActiveCount => activeCount;
    private Vec2[] gatheredVelocity = Array.Empty<Vec2>(); private SnowMatrix[] gatheredGradient = Array.Empty<SnowMatrix>(); private bool[] moving = Array.Empty<bool>();
    internal int LastMovers { get; private set; }
    internal readonly long[] Profile = new long[9];
    internal bool IsAwake(int index) => status[index].ActiveSlot >= 0;
    internal int LastParticipants { get; private set; }
    internal int LastNodes { get; private set; }
    internal uint Revision { get; private set; }
    internal ReadOnlySpan<SnowPoint> Points => points.AsSpan(0, pointCount);
    internal ref readonly SnowPoint Point(int index) => ref points[index];
    internal long BufferBytes => (long)points.Length * System.Runtime.CompilerServices.Unsafe.SizeOf<SnowPoint>() +
        (long)cells.Capacity * System.Runtime.CompilerServices.Unsafe.SizeOf<Cell>() + (long)nodes.Capacity * System.Runtime.CompilerServices.Unsafe.SizeOf<Node>() +
        (long)links.Length * System.Runtime.CompilerServices.Unsafe.SizeOf<Link>() + (long)status.Length * System.Runtime.CompilerServices.Unsafe.SizeOf<Status>() +
        (active.LongLength + participants.LongLength + freeCells.Capacity + particleNodes.LongLength) * sizeof(int) + nodeWeights.LongLength * sizeof(float);

    internal SnowMpm(ISnowTerrain terrain, int capacity = 4096, Vec2? gravity = null)
    {
        this.terrain = terrain ?? throw new ArgumentNullException(nameof(terrain));
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        this.gravity = gravity ?? new Vec2(0, -SnowConstitutive.Gravity);
        points = new SnowPoint[capacity]; status = new Status[capacity]; links = new Link[checked(capacity * 4)];
        active = new int[capacity]; participants = new int[capacity]; cells = new SnowPaged<Cell>(); freeCells = new SnowPaged<int>();
        nodes = new SnowPaged<Node>(); particleNodes = new int[checked(capacity * 9)]; nodeWeights = new float[particleNodes.Length];
        cellIndex.EnsureCapacity(capacity); nodeIndex.EnsureCapacity(capacity);
        setupBoundaries = SetupBoundaries; settleFrontier = SettleFrontier; gatherNodes = GatherNodes; gatherParticles = GatherParticles; advanceParticles = AdvanceParticles;
    }
    internal int Seed(in SnowPoint value, bool resting = false)
    {
        if (!(value.Mass > 0) || !(value.ReferenceVolume > 0) || !double.IsFinite(value.Mass) || !double.IsFinite(value.Enthalpy) ||
            !double.IsFinite(value.Position.PreciseX) || !double.IsFinite(value.Position.PreciseY) || !(value.Plastic > 0) || !(value.Elastic.Determinant > 0) ||
            value.ReferenceDensity > SnowConstitutive.MaximumDensity || !float.IsFinite(value.Velocity.LengthSquared())) throw new ArgumentException("Invalid MPM material point");
        int id;
        if (freePointCount > 0) id = freePoints[--freePointCount];
        else { EnsurePoints(pointCount + 1); id = pointCount++; }
        points[id] = value; points[id].Previous = value.Position; status[id] = default; status[id].ActiveSlot = -1;
        Touch(new Vec2((float)value.Position.PreciseX, (float)value.Position.PreciseY));
        Insert(id); if (!resting) Wake(id); Revision++; return id;
    }
    internal void SetTerrain(ISnowTerrain value)
    {
        terrain = value ?? throw new ArgumentNullException(nameof(value));
        // Only snow whose distance to the terrain actually changed, or whose old distance is no longer exact, has to react: a rebuilt map must not wake the whole pack.
        for (int i = 0; i < pointCount; i++)
        {
            if (!(points[i].Mass > 0)) continue;
            float old = status[i].Clearance; bool exact = old > 0 && status[i].Sampled.PreciseX == points[i].Position.PreciseX && status[i].Sampled.PreciseY == points[i].Position.PreciseY;
            SnowBoundary hit = terrain.Sample(points[i].Position); status[i].Clearance = hit.Distance; status[i].Sampled = points[i].Position; status[i].Normal = hit.Normal;
            if (!exact || MathF.Abs(hit.Distance - old) > .005f) { status[i].Frozen = false; Wake(i); }
        }
    }
    // Seeding already measured the distance to the terrain; recording it lets a later terrain change tell which resting particles it really touched.
    internal void Sampled(int id, float distance) { status[id].Clearance = distance; status[id].Sampled = points[id].Position; status[id].Normal = default; }
    // Snow that stays unsettled outside every interaction (bodies, tools, weather loading) is frozen after SettleSeconds: waking its sleeping neighbours again would let an old transient hold the whole pack awake. Wake(centre, radius) thaws it.
    internal const float SettleSeconds = 3, InteractionHorizon = 6;
    private readonly List<(Vec2 Centre, float Radius, float Until)> interest = new();
    private float clock;
    internal void Disturb(Vec2 centre, float radius, float seconds)
    {
        // The terrain around the place has changed (a door was opened), and what held buried snow there may be gone: no pin is trusted until it has been read again.
        pinEpoch++;
        Wake(centre, radius); interest.Add((centre, radius, clock + seconds));
    }
    private void Settle(Span<SnowRigid> bodies)
    {
        for (int i = interest.Count - 1; i >= 0; i--) if (interest[i].Until <= clock) interest.RemoveAt(i);
        for (int i = activeCount - 1; i >= 0; i--)
        {
            int p = active[i]; if (status[p].Awake < SettleSeconds) continue;
            Vec2 at = new((float)points[p].Position.PreciseX, (float)points[p].Position.PreciseY); bool near = false;
            for (int b = 0; b < bodies.Length && !near; b++)
                near = bodies[b].Shapes != null && SnowPosition.DistanceSquared(points[p].Position, bodies[b].Position) < MathF.Pow(bodies[b].Horizon > 0 ? bodies[b].Horizon : InteractionHorizon, 2);
            for (int d = 0; d < interest.Count && !near; d++) near = (at - interest[d].Centre).LengthSquared() < (interest[d].Radius + InteractionHorizon) * (interest[d].Radius + InteractionHorizon);
            if (near || !Rests(p)) continue;
            points[p].Velocity = default; points[p].Affine = default; status[p].Frozen = true; Sleep(p);
        }
    }
    // Snow buried deeper than BuriedDepth under the surface is not simulated even when the grid around it moves: it carries the layers above as a fixed base until a dig brings
    // the surface down to it. Observers play no part in the physics: every particle is simulated the same whether a camera sees it or not.
    internal float BuriedDepth = .3f;
    // The buried test reads the column above the particle and the sides beside it; it is cached until something in its metre of the map changes or the terrain does (pinEpoch).
    private uint PinStamp(int p) => RevisionOf(ChunkOf(points[p].Position.PreciseX)) + 1;
    private uint pinEpoch;
    private bool PinValid(int p) => status[p].PinStamp == PinStamp(p) && status[p].PinEpoch == pinEpoch;
    private bool Pinned(int p)
    {
        Span<float> lows = stackalloc float[PinRows], highs = stackalloc float[PinRows];
        return Pinned(p, lows, highs);
    }
    // The column above a particle is at most PinReach metres tall, so a stack buffer holds its rows and the test can run on any thread.
    private const float PinReach = 4; private const int PinRows = (int)(PinReach / CellSize) + 3;
    private bool Pinned(int p, Span<float> lows, Span<float> highs)
    {
        ref readonly SnowPoint point = ref points[p];
        ref Status s = ref status[p]; uint stampNow = PinStamp(p);
        if (PinValid(p)) return s.Pinned;
        float x = (float)point.Position.PreciseX, y = (float)point.Position.PreciseY;
        s.Pinned = Column(x, SnowConstitutive.Spacing, y, y + PinReach, lows, highs, out _, out float high) && high - y > BuriedDepth && Enclosed(x, y, lows, highs);
        s.PinStamp = stampNow; s.PinEpoch = pinEpoch; return s.Pinned;
    }
    // Buried snow is a fixed base only while snow or terrain stands at its sides: a face open to the air (a door that was shut, a wall that is gone, a pit beside it) has nothing to hold it
    // and slumps like any other snow. The sides are read at the reach the rest test uses for terrain.
    private bool Enclosed(float x, float y, Span<float> lows, Span<float> highs)
    {
        for (int side = -1; side <= 1; side += 2)
        {
            float sx = x + side * RestClearance;
            if (Column(sx, SnowConstitutive.Spacing * .75f, y - SnowConstitutive.Spacing, y + SnowConstitutive.Spacing, lows, highs, out _, out _)) continue;
            if (terrain.Sample(new SnowPosition(sx, y)).Distance > RestClearance) return false;
        }
        return true;
    }
    // Snow may come to rest only on something: the terrain, or a body of snow that reaches the terrain or is deeper than RestReach. Slowness alone is no support: a clump decelerated by the
    // grid damping passes the quiet test in mid-air, and a particle put to sleep there stays where it is for good.
    internal const float RestReach = .6f, RestClearance = SnowConstitutive.Spacing * 2.5f;
    private const int RestRows = (int)(RestReach / CellSize) + 3;
    private bool Rests(int p, Span<float> lows, Span<float> highs)
    {
        double px = points[p].Position.PreciseX, py = points[p].Position.PreciseY;
        if (!Column((float)px, SnowConstitutive.Spacing * .75f, (float)py - RestReach, (float)py + SnowConstitutive.Spacing, lows, highs, out float low, out float high)) return false;
        if (high < py - SurfaceGap) return false;
        if (low <= py - RestReach + SnowConstitutive.Spacing) return true;
        return terrain.Sample(new SnowPosition(px, low)).Distance <= RestClearance;
    }
    private bool Rests(int p)
    {
        Span<float> lows = stackalloc float[RestRows], highs = stackalloc float[RestRows];
        return Rests(p, lows, highs);
    }
    internal void Wake(Vec2 centre, float radius)
    {
        if (!float.IsFinite(radius) || radius < 0 || !float.IsFinite(centre.LengthSquared())) throw new ArgumentOutOfRangeException(nameof(radius));
        int x0 = Coordinate(centre.X - radius) - 1, x1 = Coordinate(centre.X + radius) + 1;
        int y0 = Coordinate(centre.Y - radius) - 1, y1 = Coordinate(centre.Y + radius) + 1;
        float bound = radius + CellSize;
        for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++)
            if (cellIndex.TryGetValue(Key(x, y), out int c))
                for (int link = cells[c].Head; link >= 0; link = links[link].Next)
                {
                    int p = link / 4;
                    if (SnowPosition.DistanceSquared(points[p].Position, centre) <= bound * bound && !Pinned(p)) { status[p].Frozen = false; Wake(p); }
                }
    }
    // A body wakes only the snow it can reach within the step. Waking everything inside its bounding circle would keep the whole neighbourhood of a tall leg awake every frame, settled or not.
    private void WakeTouching(in SnowRigid body, SnowCollider shape, float travel)
    {
        float c = MathF.Cos(body.Rotation), sn = MathF.Sin(body.Rotation), margin = SnowConstitutive.Spacing + travel, reach = shape.Reach + margin;
        SnowPosition centre = body.Position + new Vec2(c * shape.Centre.X - sn * shape.Centre.Y, sn * shape.Centre.X + c * shape.Centre.Y);
        int x0 = Coordinate(centre.PreciseX - reach) - 1, x1 = Coordinate(centre.PreciseX + reach) + 1, y0 = Coordinate(centre.PreciseY - reach) - 1, y1 = Coordinate(centre.PreciseY + reach) + 1;
        float bound = reach + CellSize;
        for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++)
            if (cellIndex.TryGetValue(Key(x, y), out int cell))
                for (int link = cells[cell].Head; link >= 0; link = links[link].Next)
                {
                    int p = link / 4;
                    if (status[p].ActiveSlot >= 0 || SnowPosition.DistanceSquared(points[p].Position, centre) > bound * bound) continue;
                    if (shape.Sample(body.Local(points[p].Position)).Distance <= margin) { status[p].Frozen = false; Wake(p); }
                }
    }
    internal void Step(float seconds, Span<SnowRigid> bodies)
    {
        if (!float.IsFinite(seconds) || seconds <= 0 || seconds > SnowConstitutive.MaximumStep) throw new ArgumentOutOfRangeException(nameof(seconds));
        int steps = (int)MathF.Ceiling(seconds / MaximumStep);
        float dt = seconds / steps;
        LastParticipants = 0; LastNodes = 0; LastMovers = 0;
        for (int b = 0; b < bodies.Length; b++)
        {
            ref readonly SnowRigid body = ref bodies[b];
            if (body.Shapes == null) continue;
            for (int s = 0; s < body.Shapes.Length; s++) WakeTouching(body, body.Shapes[s], body.Velocity.Length() * seconds);
        }
        for (int step = 0; step < steps; step++)
        {
            for (int b = 0; b < bodies.Length; b++) bodies[b].BeginStep(dt);
            if (activeCount > 0)
            {
                Substep(dt, bodies);
            }
            for (int b = 0; b < bodies.Length; b++) bodies[b].EndStep(dt);
        }
        clock += seconds;
        for (int i = 0; i < activeCount; i++) { status[active[i]].Awake += seconds; Mark(ChunkOf(points[active[i]].Position.PreciseX)); }
        // Settle sleeps snow too, so its chunks are marked after it runs; marking them before would leave them stale until the next step.
        Settle(bodies);
        for (int i = 0; i < restedChunks.Count; i++) Mark(restedChunks[i]);
        restedChunks.Clear();
    }
    // Room for count more particles in one allocation; the cell table is sized for the fewer cells they share.
    internal void Reserve(int more)
    {
        EnsurePoints(checked(pointCount + more)); cellIndex.EnsureCapacity((pointCount + more) / 3);
    }
    private void EnsurePoints(int count)
    {
        if (count <= points.Length) return;
        int capacity = Math.Max(count, checked(points.Length * 2));
        Array.Resize(ref points, capacity); Array.Resize(ref status, capacity); Array.Resize(ref active, capacity); Array.Resize(ref participants, capacity);
        Array.Resize(ref links, checked(capacity * 4)); Array.Resize(ref particleNodes, checked(capacity * 9)); Array.Resize(ref nodeWeights, particleNodes.Length);
    }
    private static long Key(int x, int y) => ((long)x << 32) | (uint)y;
    private static int Coordinate(double value) => checked((int)Math.Floor(value * InverseCell - .5));
    private static void Coordinates(SnowPosition position, out int x, out int y, out Vec2 fraction)
    {
        double px = position.PreciseX * InverseCell - .5, py = position.PreciseY * InverseCell - .5;
        x = checked((int)Math.Floor(px)); y = checked((int)Math.Floor(py)); fraction = new((float)(px - x), (float)(py - y));
    }
    private int CellAt(int x, int y)
    {
        long key = Key(x, y);
        if (cellIndex.TryGetValue(key, out int c)) return c;
        if (freeCellCount > 0) c = freeCells[--freeCellCount];
        else
        {
            if (cellCount == cells.Capacity) { cells.Grow(); freeCells.Grow(); }
            c = cellCount++;
        }
        cells[c] = new Cell { X = x, Y = y, Head = -1 }; cellIndex.Add(key, c); return c;
    }
    private void Insert(int p, bool stressCurrent = false)
    {
        ref readonly SnowPoint point = ref points[p];
        Coordinates(point.Position, out int bx, out int by, out Vec2 f);
        SnowMatrix stress = stressCurrent ? status[p].TransferStress : SnowPlasticity.Stress(point);
        status[p].TransferStress = stress;
        for (int y = 0; y < 2; y++) for (int x = 0; x < 2; x++)
        {
            int id = p * 4 + y * 2 + x, c = CellAt(bx + x, by + y); ref Cell cell = ref cells[c];
            float weight = (x == 0 ? 1 - f.X : f.X) * (y == 0 ? 1 - f.Y : f.Y);
            links[id] = new Link { Cell = c, Weight = weight, Next = cell.Head, Previous = -1 };
            if (cell.Head >= 0) links[cell.Head].Previous = id;
            cell.Head = id;
            Accumulate(ref cell, point, stress, weight, 1);
        }
    }
    private static void Accumulate(ref Cell cell, in SnowPoint point, SnowMatrix stress, float weight, int sign)
    {
        Vec2 distance = new SnowPosition((cell.X + .5) * CellSize, (cell.Y + .5) * CellSize) - point.Position;
        float m = (float)(point.Mass * weight); Vec2 momentum = (point.Velocity + point.Affine * distance) * m;
        SnowMatrix gradient = point.Affine * m, force = stress * (point.ReferenceVolume * weight);
        cell.Mass += sign * (double)m; cell.Px += sign * (double)momentum.X; cell.Py += sign * (double)momentum.Y;
        cell.Ga += sign * (double)gradient.A; cell.Gb += sign * (double)gradient.B; cell.Gc += sign * (double)gradient.C; cell.Gd += sign * (double)gradient.D;
        cell.Ta += sign * (double)force.A; cell.Tb += sign * (double)force.B; cell.Tc += sign * (double)force.C; cell.Td += sign * (double)force.D;
    }
    private void Unlink(int p)
    {
        ref readonly SnowPoint point = ref points[p]; SnowMatrix stress = status[p].TransferStress;
        for (int j = 0; j < 4; j++)
        {
            int id = p * 4 + j; ref Link link = ref links[id]; ref Cell cell = ref cells[link.Cell];
            Accumulate(ref cell, point, stress, link.Weight, -1);
            if (link.Next >= 0) links[link.Next].Previous = link.Previous;
            if (link.Previous >= 0) links[link.Previous].Next = link.Next; else cell.Head = link.Next;
            if (cell.Head < 0) { cellIndex.Remove(Key(cell.X, cell.Y)); freeCells[freeCellCount++] = link.Cell; }
        }
    }
    private void Wake(int p)
    {
        if (status[p].ActiveSlot >= 0 || status[p].Frozen) return;
        status[p].ActiveSlot = activeCount; status[p].Still = 0; status[p].Awake = 0; active[activeCount++] = p;
    }
    private void Sleep(int p)
    {
        int slot = status[p].ActiveSlot;
        if (slot < 0) return;
        restedChunks.Add(ChunkOf(points[p].Position.PreciseX));
        int moved = active[--activeCount]; active[slot] = moved; status[moved].ActiveSlot = slot; status[p].ActiveSlot = -1;
    }
    private int NodeAt(int x, int y)
    {
        long key = Key(x, y); if (nodeIndex.TryGetValue(key, out int n)) return n;
        if (nodeCount == nodes.Capacity) nodes.Grow();
        n = nodeCount++; nodes[n] = new Node { X = x, Y = y }; nodeIndex.Add(key, n); return n;
    }
    // The 3x3 node stencil is only needed by particles that touch a wall or a body, so it is resolved on first use in a step.
    private void Stencil(int p)
    {
        if (status[p].Stencil == stamp) return;
        status[p].Stencil = stamp; int known = nodeCount; Coordinates(points[p].Position, out int bx, out int by, out Vec2 f);
        for (int y = 0; y < 3; y++) for (int x = 0; x < 3; x++)
        {
            int id = p * 9 + y * 3 + x; particleNodes[id] = NodeAt(bx + x, by + y);
            float wx = x == 0 ? .5f * (1 - f.X) : x == 1 ? .5f : .5f * f.X;
            float wy = y == 0 ? .5f * (1 - f.Y) : y == 1 ? .5f : .5f * f.Y;
            nodeWeights[id] = wx * wy;
        }
        for (int n = known; n < nodeCount; n++) Gather(n);
    }
    private void Participant(int p)
    {
        if (status[p].Stamp == stamp) return;
        status[p].Stamp = stamp; participants[participantCount++] = p;
    }
    // The four corner nodes of a cell, resolved once per substep instead of once per particle linked to it. Nodes that the active stencils did not create are gathered on the spot unless the whole set is gathered later.
    private void CellNodes(ref Cell cell, bool gather)
    {
        if (cell.NodeStamp != stamp)
        {
            cell.NodeStamp = stamp;
            if (gather) { cell.N0 = ResolvedNode(cell.X, cell.Y); cell.N1 = ResolvedNode(cell.X + 1, cell.Y); cell.N2 = ResolvedNode(cell.X, cell.Y + 1); cell.N3 = ResolvedNode(cell.X + 1, cell.Y + 1); }
            else { cell.N0 = NodeAt(cell.X, cell.Y); cell.N1 = NodeAt(cell.X + 1, cell.Y); cell.N2 = NodeAt(cell.X, cell.Y + 1); cell.N3 = NodeAt(cell.X + 1, cell.Y + 1); }
        }
        if (!gather || cell.CentreStamp == stamp) return;
        // Contact has finished with the nodes: every particle linked to the cell reads the same centre values, so they are formed once per cell instead of once per link.
        cell.CentreStamp = stamp;
        Vec2 v0 = nodes[cell.N0].Velocity, v1 = nodes[cell.N1].Velocity, v2 = nodes[cell.N2].Velocity, v3 = nodes[cell.N3].Velocity;
        cell.CentreVelocity = (v0 + v1 + v2 + v3) * .25f;
        SnowMatrix gradient = default;
        gradient += SnowMatrix.Outer(v0, new Vec2(-.5f, -.5f) * InverseCell);
        gradient += SnowMatrix.Outer(v1, new Vec2(.5f, -.5f) * InverseCell);
        gradient += SnowMatrix.Outer(v2, new Vec2(-.5f, .5f) * InverseCell);
        gradient += SnowMatrix.Outer(v3, new Vec2(.5f, .5f) * InverseCell);
        cell.CentreGradient = gradient;
    }
    private int ResolvedNode(int x, int y)
    {
        if (nodeIndex.TryGetValue(Key(x, y), out int n)) return n;
        n = NodeAt(x, y); Gather(n); return n;
    }
    private SnowFork fork = new(SnowFork.DefaultHelpers);
    private readonly SnowFork.Range settleFrontier, setupBoundaries, gatherNodes, gatherParticles, advanceParticles;
    private float stepSeconds;
    internal int Helpers { get => fork.Helpers; set => fork = new SnowFork(value); }
    // Sleeping neighbours the grid would set moving; each range writes only its own entries and the PinStamp of its own particles.
    private void SettleFrontier(int begin, int end)
    {
        Span<float> lows = stackalloc float[PinRows], highs = stackalloc float[PinRows];
        for (int i = begin; i < end; i++) if (moving[i] && status[participants[i]].ActiveSlot < 0 && Pinned(participants[i], lows, highs)) moving[i] = false;
    }
    private void GatherNodes(int begin, int end) { for (int n = begin; n < end; n++) Gather(n); }
    // Reads the grid and writes only the entries of its own particles; the cell nodes it needs were resolved before the range started.
    private void GatherParticles(int begin, int end)
    {
        float dt = stepSeconds; Span<float> lows = stackalloc float[RestRows], highs = stackalloc float[RestRows];
        for (int i = begin; i < end; i++)
        {
            int p = participants[i];
            // A sleeping neighbour already known to be buried under unchanged snow is a fixed base: its grid velocity is never read.
            if (status[p].ActiveSlot < 0 && status[p].Pinned && PinValid(p)) { moving[i] = false; continue; }
            Vec2 velocity = default; SnowMatrix gradient = default;
            for (int j = 0; j < 4; j++)
            {
                ref readonly Link link = ref links[p * 4 + j]; ref readonly Cell cell = ref cells[link.Cell];
                velocity += cell.CentreVelocity * link.Weight; gradient += cell.CentreGradient * link.Weight;
            }
            bool awake = status[p].ActiveSlot >= 0, quiet = velocity.LengthSquared() < QuietSpeed * QuietSpeed && (!awake || (velocity - points[p].Velocity).LengthSquared() < dt * dt * QuietAcceleration * QuietAcceleration) && gradient.NormSquared < QuietStrain * QuietStrain;
            gatheredVelocity[i] = velocity; gatheredGradient[i] = gradient;
            moving[i] = awake || !quiet;
            if (awake && quiet && status[p].Still + dt >= RestSeconds && !Rests(p, lows, highs)) quiet = false;
            if (awake) status[p].Still = quiet ? status[p].Still + dt : 0;
        }
    }
    // Moves the particles that the grid moved and computes the stress they will deposit back; waking and sleeping change the active list and stay outside the range.
    private void AdvanceParticles(int begin, int end)
    {
        float dt = stepSeconds; int limited = 0;
        for (int i = begin; i < end; i++)
        {
            if (!moving[i]) continue;
            int p = participants[i]; ref SnowPoint point = ref points[p]; Vec2 velocity = gatheredVelocity[i]; SnowMatrix gradient = gatheredGradient[i];
            limited += Limit(dt, ref velocity, ref gradient);
            point.Previous = point.Position; point.Position += velocity * dt;
            point.Elastic = SnowPlasticity.Project((SnowMatrix.Identity + gradient * dt) * point.Elastic, ref point, dt);
            point.Velocity = velocity; point.Affine = gradient;
            status[p].TransferStress = SnowPlasticity.Stress(point);
        }
        if (limited > 0) System.Threading.Interlocked.Add(ref limitedSubsteps, limited);
    }
    private int limitedSubsteps;
    // Snow dissipates what a pile or a hole wall gains in motion: grains lose their motion to one another instead of ringing as an elastic body. Only grid nodes that carry packed snow feel it, so a thrown clump still falls freely.
    internal const float Damping = 10;
    private const float FullNodeMass = SnowConstitutive.FreshDensity * CellSize * CellSize;
    private float prepareStep;
    private void Gather(int n)
    {
        float dt = prepareStep; ref Node node = ref nodes[n]; double mass = 0, px = 0, py = 0;
        for (int y = -1; y <= 0; y++) for (int x = -1; x <= 0; x++)
        {
            if (!cellIndex.TryGetValue(Key(node.X + x, node.Y + y), out int c)) continue;
            ref readonly Cell cell = ref cells[c]; float dx = (-x - .5f) * CellSize, dy = (-y - .5f) * CellSize;
            float gx = dx * InverseCell * InverseCell, gy = dy * InverseCell * InverseCell;
            mass += .25 * cell.Mass;
            px += .25 * (cell.Px + cell.Ga * dx + cell.Gb * dy) - dt * (cell.Ta * gx + cell.Tb * gy);
            py += .25 * (cell.Py + cell.Gc * dx + cell.Gd * dy) - dt * (cell.Tc * gx + cell.Td * gy);
        }
        node.Mass = (float)mass;
        node.Velocity = mass > 1e-12 ? (new Vec2((float)(px / mass), (float)(py / mass)) + gravity * dt) * (1 / (1 + dt * Damping * Math.Clamp((float)mass / FullNodeMass - .3f, 0, .4f) * 2.5f)) : default;
    }
    private void Prepare(float dt)
    {
        long topo0 = System.Diagnostics.Stopwatch.GetTimestamp(); nodeIndex.Clear(); nodeCount = 0; participantCount = 0;
        if (++stamp == 0) { Array.Clear(status); for (int i = 0; i < pointCount; i++) status[i].ActiveSlot = -1; for (int i = 0; i < activeCount; i++) status[active[i]].ActiveSlot = i; stamp = 1; }
        for (int i = 0; i < activeCount; i++)
        {
            int p = active[i]; Participant(p);
            for (int j = 0; j < 4; j++) CellNodes(ref cells[links[p * 4 + j].Cell], false);
        }
        int interior = nodeCount;
        for (int n = 0; n < interior; n++)
        {
            int nx = nodes[n].X, ny = nodes[n].Y;
            for (int y = -1; y <= 0; y++) for (int x = -1; x <= 0; x++)
                if (cellIndex.TryGetValue(Key(nx + x, ny + y), out int c) && cells[c].ScanStamp != stamp)
                {
                    cells[c].ScanStamp = stamp;
                    for (int id = cells[c].Head; id >= 0; id = links[id].Next) Participant(id / 4);
                }
        }
        long g0 = System.Diagnostics.Stopwatch.GetTimestamp(); Profile[5] += g0 - topo0;
        prepareStep = dt; fork.Run(nodeCount, 256, gatherNodes); Profile[6] += System.Diagnostics.Stopwatch.GetTimestamp() - g0;
        LastParticipants = Math.Max(LastParticipants, participantCount); LastNodes = Math.Max(LastNodes, nodeCount);
    }
    // Courant limits of the explicit update: a particle may not cross more than half a cell in one substep, and the strain it takes in one substep (|G|dt, Frobenius) stays below one half,
    // which keeps det(I + G dt) >= 1/4 for any grid velocity field. A contact that asks for more (a body snapping into the pack) is spread over the following substeps instead of inverting the material.
    internal const float MaximumTravel = .5f * CellSize, MaximumStrainStep = .5f;
    internal int Limited => limitedSubsteps;
    private static int Limit(float dt, ref Vec2 velocity, ref SnowMatrix gradient)
    {
        float speed = velocity.LengthSquared(), strain = gradient.NormSquared * dt * dt; int limited = 0;
        if (!(speed < float.MaxValue) || !(strain < float.MaxValue)) { velocity = default; gradient = default; return 1; }
        float travel = MaximumTravel / dt;
        if (speed > travel * travel) { velocity *= travel / MathF.Sqrt(speed); limited++; }
        if (strain > MaximumStrainStep * MaximumStrainStep) { gradient *= MaximumStrainStep / MathF.Sqrt(strain); limited++; }
        return limited;
    }
    // Seeding doubles the particle and cell storage as it goes, so a pack can finish with almost half of it unused (the Vostok pack holds 285k particles in arrays for 524k).
    // Once the pack is complete the slack is returned; the storage grows again the normal way if snow is ever added.
    internal void Trim()
    {
        int count = Math.Max(1, pointCount), cellsUsed = Math.Max(1, cellCount);
        Array.Resize(ref points, count); Array.Resize(ref status, count); Array.Resize(ref active, count); Array.Resize(ref participants, count);
        Array.Resize(ref links, checked(count * 4)); Array.Resize(ref particleNodes, checked(count * 9)); Array.Resize(ref nodeWeights, particleNodes.Length);
        cells.Trim(cellsUsed); freeCells.Trim(cellsUsed);
    }
    // The scratch arrays of a substep are sized to the pack, not to the snow that happens to be awake: they are allocated when the pack is seeded, so the first character to step into it does not pay for them.
    internal void Reserve()
    {
        if (gatheredVelocity.Length < participants.Length) { gatheredVelocity = new Vec2[participants.Length]; gatheredGradient = new SnowMatrix[participants.Length]; moving = new bool[participants.Length]; }
        if (boundaries.Length < participants.Length) { Array.Resize(ref boundaries, participants.Length); Array.Resize(ref terrainImpulses, participants.Length); Array.Resize(ref touching, participants.Length); nearBody = new bool[participants.Length]; }
    }
    private void Substep(float dt, Span<SnowRigid> bodies)
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        Prepare(dt); long t1 = System.Diagnostics.Stopwatch.GetTimestamp(); Contact(dt, bodies); long t2 = System.Diagnostics.Stopwatch.GetTimestamp(); Profile[0] += t1 - t0; Profile[1] += t2 - t1;
        Reserve();
        // Gather first: a sleeping neighbour that the grid leaves quiet keeps its state and its entry in the cells, so only particles that actually move are unlinked, advanced and inserted again.
        for (int i = 0; i < participantCount; i++) { int p = participants[i]; for (int j = 0; j < 4; j++) CellNodes(ref cells[links[p * 4 + j].Cell], true); }
        stepSeconds = dt; fork.Run(participantCount, 128, gatherParticles);
        // The frontier: sleeping neighbours the grid would set moving. Buried or unseen snow stays where it is and acts as a fixed base.
        fork.Run(participantCount, 128, settleFrontier);
        long t3 = System.Diagnostics.Stopwatch.GetTimestamp(); Profile[2] += t3 - t2;
        int movers = 0;
        for (int i = 0; i < participantCount; i++) if (moving[i]) { movers++; Unlink(participants[i]); }
        fork.Run(participantCount, 128, advanceParticles);
        for (int i = 0; i < participantCount; i++)
        {
            if (!moving[i]) continue;
            int p = participants[i];
            if (status[p].ActiveSlot < 0) Wake(p);
            if (SleepingEnabled && status[p].Still >= RestSeconds) Sleep(p);
        }
        long t4 = System.Diagnostics.Stopwatch.GetTimestamp(); Profile[3] += t4 - t3;
        for (int i = 0; i < participantCount; i++) if (moving[i]) Insert(participants[i], true);
        Profile[4] += System.Diagnostics.Stopwatch.GetTimestamp() - t4; LastMovers = Math.Max(LastMovers, movers);
        Revision++;
    }
}
