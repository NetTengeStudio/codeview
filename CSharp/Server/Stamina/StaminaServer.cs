using System;
using System.Runtime.CompilerServices;
using Barotrauma;
using Barotrauma.Networking;
using ScpItdMod.Core;

namespace ScpItdMod.Stamina {
    public static class StaminaServer {
        private const int Capacity = CharacterSlots.Capacity;

        private static readonly float[] values = new float[Capacity];
        private static readonly float[] delays = new float[Capacity];
        private static readonly StaminaFlags[] flags = new StaminaFlags[Capacity];
        private static readonly float[] sendTimers = new float[Capacity];
        private static readonly uint[] sequences = new uint[Capacity];
        private static readonly ushort[] lastSemantic = new ushort[Capacity];
        private static readonly uint[] generations = new uint[Capacity];

        public static void Reset() => Array.Clear(generations, 0, Capacity);

        public static bool Ensure(int slot, uint generation) {
            if ((uint)slot >= Capacity || generation == 0u) { return false; }
            if (generations[slot] == generation) { return true; }
            generations[slot] = generation;
            values[slot] = 1.0f;
            delays[slot] = 0.0f;
            flags[slot] = StaminaFlags.None;
            sendTimers[slot] = StaminaConfig.SendInterval;
            sequences[slot] = 0u;
            lastSemantic[slot] = ushort.MaxValue;
            return true;
        }

        public static bool TryGet(Character character, out StaminaState state) {
            int slot = CharacterRuntime.Slots.IndexOf(character);
            if (slot < 0) {
                state = StaminaState.Full;
                return false;
            }
            return TryGet(slot, CharacterRuntime.Slots.Generation(slot), out state);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryGet(int slot, uint generation, out StaminaState state) {
            if ((uint)slot >= Capacity || generation == 0u || generations[slot] != generation) {
                state = StaminaState.Full;
                return false;
            }
            state = new StaminaState {
                Value = values[slot],
                Delay = delays[slot],
                Flags = flags[slot]
            };
            return true;
        }

        public static void Advance(Character character, StaminaActivity activity, bool exerting, float dt) {
            int slot = CharacterRuntime.Slots.SlotFor(character, out _);
            if (slot < 0) { return; }
            uint generation = CharacterRuntime.Slots.Generation(slot);
            Advance(character, slot, generation, null, activity, exerting, dt);
        }

        public static void Advance(Character character, int slot, uint generation, NetworkConnection connection,
            StaminaActivity activity, bool exerting, float dt) {
            if (!Ensure(slot, generation)) { return; }
            StaminaSim.Step(ref values[slot], ref delays[slot], ref flags[slot], activity, exerting, dt);

            float elapsed = sendTimers[slot] + dt;
            sendTimers[slot] = elapsed;
            ushort semantic = StaminaNet.Semantic(values[slot], flags[slot]);
            bool dirty = semantic != lastSemantic[slot];
            bool heartbeat = elapsed >= StaminaConfig.HeartbeatInterval;
            if (!heartbeat && (!dirty || elapsed < StaminaConfig.SendInterval)) { return; }

            connection ??= OwnerConnection.Of(character, slot, generation);
            if (connection == null) { return; }
            sequences[slot] = StaminaNet.SequenceField.Next(sequences[slot]);
            ushort word = StaminaNet.Pack(values[slot], flags[slot], sequences[slot]);
            ModNet.Send(StaminaNet.StateId, connection, word, StaminaNet.Width, DeliveryMethod.Unreliable);
            sendTimers[slot] = 0.0f;
            lastSemantic[slot] = semantic;
        }
    }
}
