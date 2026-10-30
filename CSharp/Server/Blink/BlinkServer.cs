using System;
using System.Runtime.CompilerServices;
using Barotrauma;
using Barotrauma.Networking;
using ScpItdMod.Core;

namespace ScpItdMod.Blink {
    public static class BlinkServer {
        private const int Capacity = CharacterSlots.Capacity;

        private static readonly float[] timers = new float[Capacity];
        private static readonly float[] frequencies = new float[Capacity];
        private static readonly float[] irritations = new float[Capacity];
        private static readonly float[] stuck = new float[Capacity];
        private static readonly float[] effects = new float[Capacity];
        private static readonly float[] effectTimers = new float[Capacity];
        private static readonly float[] sendTimers = new float[Capacity];
        private static readonly uint[] sequences = new uint[Capacity];
        private static readonly uint[] lastSemantic = new uint[Capacity];
        private static readonly uint[] generations = new uint[Capacity];
        private static readonly double[] lastForce = new double[Capacity];
        private static readonly double[] lastPacket = new double[Capacity];

        private static ulong engagedMask;
        private static ulong pendingHitMask;
        private static ulong heldMask;

        public static void Hook() =>
            ModNet.Receive(BlinkNet.ForceId, BlinkNet.ForceWidth, OnForceReceived);

        public static void Reset() {
            Array.Clear(generations, 0, Capacity);
            engagedMask = 0UL;
            pendingHitMask = 0UL;
            heldMask = 0UL;
        }

        public static bool EyesOpen(Character character) {
            int slot = CharacterRuntime.Slots.IndexOf(character);
            if (slot < 0) { return true; }
            uint generation = CharacterRuntime.Slots.Generation(slot);
            return generations[slot] != generation || BlinkSim.EyesOpen(timers[slot]);
        }

        public static bool Ensure(int slot, uint generation) {
            if ((uint)slot >= Capacity || generation == 0u) { return false; }
            if (generations[slot] == generation) { return true; }
            generations[slot] = generation;
            BlinkSim.Rest(ref timers[slot], ref frequencies[slot], ref irritations[slot], ref stuck[slot],
                ref effects[slot], ref effectTimers[slot], Difficulty());
            sendTimers[slot] = BlinkConfig.SendInterval;
            sequences[slot] = 0u;
            lastSemantic[slot] = uint.MaxValue;
            lastForce[slot] = double.NegativeInfinity;
            lastPacket[slot] = double.NegativeInfinity;
            ulong bit = 1UL << slot;
            engagedMask &= ~bit;
            pendingHitMask &= ~bit;
            heldMask &= ~bit;
            return true;
        }

        public static void Advance(Character character, float dt) {
            NetworkConnection connection = OwnerConnection.Of(character);
            if (connection == null) { return; }
            int slot = CharacterRuntime.Slots.SlotFor(character, out _);
            if (slot < 0) { return; }
            uint generation = CharacterRuntime.Slots.Generation(slot);
            Advance(character, slot, generation, connection, dt);
        }

        public static void Advance(Character character, int slot, uint generation, NetworkConnection connection, float dt) {
            if (connection == null || !Ensure(slot, generation)) { return; }
            ulong bit = 1UL << slot;
            bool engaged = BlinkGate.Active(character);
            bool wasEngaged = (engagedMask & bit) != 0UL;
            bool transition = engaged != wasEngaged;
            BlinkDifficulty difficulty = Difficulty();

            if (!engaged) {
                if (wasEngaged) {
                    engagedMask &= ~bit;
                    BlinkSim.Rest(ref timers[slot], ref frequencies[slot], ref irritations[slot], ref stuck[slot],
                        ref effects[slot], ref effectTimers[slot], difficulty);
                }
                pendingHitMask &= ~bit;
                heldMask &= ~bit;
            } else {
                if (!wasEngaged) {
                    engagedMask |= bit;
                    BlinkSim.Rest(ref timers[slot], ref frequencies[slot], ref irritations[slot], ref stuck[slot],
                        ref effects[slot], ref effectTimers[slot], difficulty);
                }
                bool hit = (pendingHitMask & bit) != 0UL;
                bool held = (heldMask & bit) != 0UL;
                BlinkSim.Force(ref timers[slot], hit, held);
                pendingHitMask &= ~bit;
                BlinkSim.Advance(ref timers[slot], ref frequencies[slot], ref irritations[slot], ref stuck[slot],
                    ref effects[slot], ref effectTimers[slot], difficulty, dt);
            }

            BlinkFlags flags = Flags(slot, engaged);
            float elapsed = sendTimers[slot] + dt;
            sendTimers[slot] = elapsed;
            uint semantic = BlinkNet.Semantic(timers[slot], frequencies[slot], stuck[slot], flags);
            bool dirty = semantic != lastSemantic[slot];
            bool heartbeat = elapsed >= BlinkConfig.HeartbeatInterval;
            if (!transition && !heartbeat && (!dirty || elapsed < BlinkConfig.SendInterval)) { return; }

            sequences[slot] = BlinkNet.SequenceField.Next(sequences[slot]);
            uint word = BlinkNet.Pack(timers[slot], frequencies[slot], stuck[slot], flags, sequences[slot]);
            ModNet.Send(BlinkNet.StateId, connection, word, BlinkNet.StateWidth, DeliveryMethod.Unreliable);
            sendTimers[slot] = 0.0f;
            lastSemantic[slot] = semantic;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static BlinkFlags Flags(int slot, bool engaged) {
            BlinkFlags flags = engaged ? BlinkFlags.Engaged : BlinkFlags.None;
            if (irritations[slot] > 0.0f) { flags |= BlinkFlags.Irritated; }
            return flags;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static BlinkDifficulty Difficulty() => BlinkSim.Grade(Level.Loaded?.Difficulty ?? 0.0f);

        private static void OnForceReceived(Client sender, uint bits) {
            const uint allowed = BlinkNet.HitBit | BlinkNet.HeldBit;
            if ((bits & ~allowed) != 0u) { return; }
            Character character = sender?.Character;
            if (!BlinkSim.Eligible(character)) { return; }
            int slot = CharacterRuntime.Slots.SlotFor(character, out _);
            if (slot < 0) { return; }
            uint generation = CharacterRuntime.Slots.Generation(slot);
            if (!Ensure(slot, generation)) { return; }

            double now = Timing.TotalTime;
            if (now - lastPacket[slot] < BlinkConfig.ForcePacketInterval) { return; }
            lastPacket[slot] = now;

            ulong bit = 1UL << slot;
            if ((bits & BlinkNet.HeldBit) != 0u) { heldMask |= bit; }
            else { heldMask &= ~bit; }
            if ((bits & BlinkNet.HitBit) == 0u) { return; }
            if (now - lastForce[slot] < BlinkConfig.ForceInterval) { return; }
            lastForce[slot] = now;
            pendingHitMask |= bit;
        }
    }
}
