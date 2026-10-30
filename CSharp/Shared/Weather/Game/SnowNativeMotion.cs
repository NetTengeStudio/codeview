using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using FarseerPhysics;
using FarseerPhysics.Dynamics;
using FarseerPhysics.Dynamics.Contacts;
using HarmonyLib;
using X = Microsoft.Xna.Framework;

namespace ScpItdMod.Weather;

internal static class SnowNativeMotion
{
    private struct Entry { internal Body Body; internal X.Vector2 Displacement; internal float Angle; }
    private sealed class Frame
    {
        internal Entry[] Entries = new Entry[16];
        internal readonly Dictionary<Body, int> Index = new Dictionary<Body, int>(16);
        internal int Count;
        internal float Seconds;
        internal void Remove(int index)
        {
            Index.Remove(Entries[index].Body);
            int last = --Count;
            if (index != last) { Entries[index] = Entries[last]; Index[Entries[index].Body] = index; }
            Entries[last] = default;
        }
        internal void Clear() { Array.Clear(Entries, 0, Count); Count = 0; Index.Clear(); Seconds = 0; }
    }
    private static readonly ConditionalWeakTable<World, Frame> frames = new ConditionalWeakTable<World, Frame>();
    private static readonly ConditionalWeakTable<World, Frame>.CreateValueCallback createFrame = CreateFrame;
    private static readonly MethodInfo solve = AccessTools.Method(typeof(Island), nameof(Island.Solve), new[] { typeof(TimeStep).MakeByRefType(), typeof(X.Vector2).MakeByRefType() });
    private static readonly MethodInfo step = AccessTools.Method(typeof(World), nameof(World.Step), new[] { typeof(float) });
    private static Harmony owner;
    private static Frame CreateFrame(World world) => new Frame();
    internal static void Attach(Harmony harmony)
    {
        if (harmony == null) throw new ArgumentNullException(nameof(harmony));
        if (owner != null) { if (owner.Id == harmony.Id) return; throw new InvalidOperationException("Native snow motion already has a patch owner"); }
        try
        {
            harmony.Patch(solve, transpiler: new HarmonyMethod(typeof(SnowNativeMotion), nameof(Transpile)));
            harmony.Patch(step, prefix: new HarmonyMethod(typeof(SnowNativeMotion), nameof(Validate)) { priority = Priority.Last },
                finalizer: new HarmonyMethod(typeof(SnowNativeMotion), nameof(Finish)));
            owner = harmony;
        }
        catch
        {
            harmony.Unpatch(solve, AccessTools.Method(typeof(SnowNativeMotion), nameof(Transpile)));
            harmony.Unpatch(step, AccessTools.Method(typeof(SnowNativeMotion), nameof(Finish)));
            harmony.Unpatch(step, AccessTools.Method(typeof(SnowNativeMotion), nameof(Validate)));
            throw;
        }
    }
    internal static void Release()
    {
        if (owner == null) return;
        owner.Unpatch(solve, AccessTools.Method(typeof(SnowNativeMotion), nameof(Transpile)));
        owner.Unpatch(step, AccessTools.Method(typeof(SnowNativeMotion), nameof(Finish)));
        owner.Unpatch(step, AccessTools.Method(typeof(SnowNativeMotion), nameof(Validate)));
        frames.Clear(); owner = null;
    }
    internal static void Queue(Body body, X.Vector2 displacement, float angle, float seconds)
    {
        if (owner == null) throw new InvalidOperationException("Native snow motion must be attached before committing a material frame");
        Frame frame = frames.GetValue(body.World, createFrame);
        if (frame.Count > 0 && MathF.Abs(frame.Seconds - seconds) > seconds * .00001f) throw new InvalidOperationException("Snow bodies disagree on the native frame duration");
        if (frame.Count == frame.Entries.Length) Array.Resize(ref frame.Entries, checked(frame.Entries.Length * 2));
        if (!frame.Index.TryAdd(body, frame.Count)) throw new InvalidOperationException("Snow body already committed to this native frame");
        frame.Seconds = seconds;
        frame.Entries[frame.Count++] = new Entry { Body = body, Displacement = displacement, Angle = angle };
    }
    private static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> input)
    {
        var code = new List<CodeInstruction>(input);
        MethodInfo anchor = AccessTools.Method(typeof(ContactSolver), nameof(ContactSolver.StoreImpulses));
        int found = -1, count = 0;
        for (int i = 0; i < code.Count; i++) if (code[i].Calls(anchor)) { found = i; count++; }
        if (count != 1) throw new NotSupportedException("Installed Farseer island has no unique pre-integration snow seam");
        code.InsertRange(found + 1, new[] { new CodeInstruction(OpCodes.Ldarg_0), new CodeInstruction(OpCodes.Ldarg_1),
            new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(SnowNativeMotion), nameof(Apply))) });
        return code;
    }
    private static void Validate(World __instance, float __0)
    {
        if (frames.TryGetValue(__instance, out Frame frame) && frame.Count > 0 &&
            (!float.IsFinite(__0) || __0 <= 0 || MathF.Abs(frame.Seconds - __0) > __0 * .00001f))
            throw new InvalidOperationException("Snow motion cannot be consumed by a different native timestep");
    }
    private static void Apply(Island island, ref TimeStep step)
    {
        if (island.BodyCount == 0 || !frames.TryGetValue(island.Bodies[0].World, out Frame frame) || frame.Count == 0) return;
        if (MathF.Abs(frame.Seconds - step.dt) > step.dt * .00001f) throw new InvalidOperationException("Snow motion cannot be consumed by a different native timestep");
        if (frame.Count > island.BodyCount)
        {
            for (int index = 0; index < island.BodyCount; index++)
                if (frame.Index.TryGetValue(island.Bodies[index], out int entry)) ApplyEntry(island, frame, entry, index);
            return;
        }
        for (int i = 0; i < frame.Count;)
        {
            Entry entry = frame.Entries[i]; int index = entry.Body.IslandIndex;
            if ((uint)index >= island.BodyCount || !ReferenceEquals(island.Bodies[index], entry.Body)) { i++; continue; }
            ApplyEntry(island, frame, i, index);
        }
    }
    private static void ApplyEntry(Island island, Frame frame, int entry, int index)
    {
        island._positions[index].c += frame.Entries[entry].Displacement; island._positions[index].a += frame.Entries[entry].Angle;
        frame.Remove(entry);
    }
    private static Exception Finish(World __instance, Exception __exception)
    {
        if (!frames.TryGetValue(__instance, out Frame frame)) return __exception;
        int missing = 0;
        if (__exception == null) for (int i = 0; i < frame.Count; i++)
        {
            Body body = frame.Entries[i].Body;
            if (body.World == __instance && body.Enabled && body.BodyType == BodyType.Dynamic) missing++;
        }
        frame.Clear();
        return __exception ?? (missing == 0 ? null : new InvalidOperationException("Native world did not consume " + missing + " snow motion corrections"));
    }
}
