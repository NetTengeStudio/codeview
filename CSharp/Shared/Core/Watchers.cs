using System.Runtime.CompilerServices;
using Barotrauma;

namespace ScpItdMod.Core {
    public interface IWatcher {
        bool Watching(Entity target);
    }

    public static class Watchers {
        private const int Capacity = 64;
        private static readonly IWatcher[] watchers = new IWatcher[Capacity];
        private static ulong liveMask;

        public static void Add(IWatcher watcher) {
            if (watcher == null) { return; }
            ulong scan = liveMask;
            while (scan != 0UL) {
                int slot = System.Numerics.BitOperations.TrailingZeroCount(scan);
                scan &= scan - 1UL;
                if (ReferenceEquals(watchers[slot], watcher)) { return; }
            }
            ulong free = ~liveMask;
            if (free == 0UL) {
                ModLog.Once("watcher slots are full");
                return;
            }
            int index = System.Numerics.BitOperations.TrailingZeroCount(free);
            watchers[index] = watcher;
            liveMask |= 1UL << index;
        }

        public static void Remove(IWatcher watcher) {
            if (watcher == null) { return; }
            ulong scan = liveMask;
            while (scan != 0UL) {
                int slot = System.Numerics.BitOperations.TrailingZeroCount(scan);
                scan &= scan - 1UL;
                if (!ReferenceEquals(watchers[slot], watcher)) { continue; }
                watchers[slot] = null;
                liveMask &= ~(1UL << slot);
                return;
            }
        }

        public static void Clear() {
            ulong scan = liveMask;
            liveMask = 0UL;
            while (scan != 0UL) {
                int slot = System.Numerics.BitOperations.TrailingZeroCount(scan);
                scan &= scan - 1UL;
                watchers[slot] = null;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Any(Entity target) {
            ulong scan = liveMask;
            while (scan != 0UL) {
                int slot = System.Numerics.BitOperations.TrailingZeroCount(scan);
                scan &= scan - 1UL;
                IWatcher watcher = watchers[slot];
                if (watcher != null && watcher.Watching(target)) { return true; }
            }
            return false;
        }
    }
}
