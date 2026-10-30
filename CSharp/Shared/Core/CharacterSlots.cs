using System;
using System.Runtime.CompilerServices;
using Barotrauma;

namespace ScpItdMod.Core {
    public sealed class CharacterSlots {
        public const int Capacity = 64;
        private const int IdCapacity = 1 << 16;

        private readonly Character[] owners = new Character[Capacity];
        private readonly uint[] generations = new uint[Capacity];
        private readonly byte[] slotById = new byte[IdCapacity];
        private readonly ushort[] epochById = new ushort[IdCapacity];

        private ulong occupiedMask;
        private int occupied;
        private ushort epoch = 1;

        public void Reset() {
            if (occupied > 0) { Array.Clear(owners, 0, occupied); }
            occupiedMask = 0UL;
            occupied = 0;
            epoch++;
            if (epoch != 0) { return; }
            Array.Clear(epochById, 0, epochById.Length);
            epoch = 1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int IndexOf(Character character) {
            if (character == null) { return -1; }
            ushort id = character.ID;
            if (epochById[id] != epoch) { return -1; }
            int slot = slotById[id] - 1;
            if ((uint)slot >= Capacity || !ReferenceEquals(owners[slot], character)) { return -1; }
            return slot;
        }

        public int SlotFor(Character character, out bool claimed) {
            claimed = false;
            if (character == null) { return -1; }
            int slot = IndexOf(character);
            if (slot >= 0) { return slot; }

            ulong scan = occupiedMask;
            while (scan != 0UL) {
                slot = System.Numerics.BitOperations.TrailingZeroCount(scan);
                scan &= scan - 1UL;
                if (IsAlive(owners[slot])) { continue; }
                Unmap(slot);
                return Assign(slot, character, out claimed);
            }

            if (occupied >= Capacity) {
                ModLog.Once("character slots are full");
                return -1;
            }
            slot = occupied++;
            return Assign(slot, character, out claimed);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint Generation(int slot) =>
            (uint)slot < Capacity && (occupiedMask & (1UL << slot)) != 0UL ? generations[slot] : 0u;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Character Owner(int slot) =>
            (uint)slot < Capacity && (occupiedMask & (1UL << slot)) != 0UL ? owners[slot] : null;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Current(int slot, uint generation, Character character) =>
            generation != 0u && Generation(slot) == generation && ReferenceEquals(Owner(slot), character);

        private int Assign(int slot, Character character, out bool claimed) {
            uint generation = generations[slot] + 1u;
            if (generation == 0u) { generation = 1u; }
            generations[slot] = generation;
            owners[slot] = character;
            occupiedMask |= 1UL << slot;
            ushort id = character.ID;
            epochById[id] = epoch;
            slotById[id] = (byte)(slot + 1);
            claimed = true;
            return slot;
        }

        private void Unmap(int slot) {
            Character previous = owners[slot];
            if (previous == null) { return; }
            ushort id = previous.ID;
            if (epochById[id] == epoch && slotById[id] == slot + 1) {
                epochById[id] = 0;
                slotById[id] = 0;
            }
            owners[slot] = null;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsAlive(Character character) =>
            character != null && !character.Removed && !character.IsDead;
    }

    public static class CharacterRuntime {
        public static readonly CharacterSlots Slots = new CharacterSlots();

        public static void Reset() {
            Slots.Reset();
#if SERVER
            OwnerConnection.Reset();
#endif
        }
    }
}
