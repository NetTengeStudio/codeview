using System;
using System.Collections.Generic;
using Barotrauma;
using Microsoft.Xna.Framework;
using ScpItdMod.Motion;

namespace ScpItdMod.Core {
    public enum HudCorner : byte {
        BottomRight = 0,
        BottomLeft = 1,
        TopRight = 2,
        TopLeft = 3
    }

    // A docked HUD block. Its position is a Motion vector handle, so corner
    // changes, stacking when another meter appears and resolution changes all
    // animate through the same engine as every other SCPITD surface instead of
    // a private per-axis exponential approach.
    public sealed class HudSlot {
        private readonly Func<bool> occupies;
        private readonly MotionScope motion;
        private readonly MotionHandle<Vec2> position;

        internal HudSlot(HudCorner corner, Vector2 designSize, int row, Func<bool> occupies) {
            Corner = corner;
            DesignSize = designSize;
            Row = row;
            this.occupies = occupies;
            Scale = 1.0f;
            motion = MotionClient.Runtime.Scope();
            position = motion.Vector();
        }

        public HudCorner Corner { get; }

        public int Row { get; }

        public Vector2 DesignSize { get; }

        public Vector2 Position => position.Alive ? new Vector2(position.Value.X, position.Value.Y) : Target;

        public Vector2 Target { get; private set; }

        public Vector2 Size { get; internal set; }

        public float Scale { get; internal set; }

        public bool Occupies => occupies == null || occupies();

        internal bool Placed { get; set; }

        internal void Aim(Vector2 target, bool snap) {
            Target = target;
            if (!position.Alive) { return; }
            var value = new Vec2(target.X, target.Y);
            if (snap) { position.Snap(value); }
            else { position.To(value, MotionPresets.Layout); }
        }

        internal void Dispose() => motion.Dispose();
    }

    public static class HudDock {
        private const int CornerCount = 4;
        private const int LeftBit = 1;
        private const int TopBit = 2;

        private static readonly List<HudSlot> slots = new List<HudSlot>();
        private static readonly Comparison<HudSlot> ByRow = (a, b) =>
            a.Corner != b.Corner ? ((int)a.Corner).CompareTo((int)b.Corner) : a.Row.CompareTo(b.Row);

        private static Point resolution;
        private static float scale;
        private static float usableBottom;
        private static bool dirty = true;

        public static float Padding => HUDLayoutSettings.Padding;

        public static HudSlot Register(HudCorner corner, Vector2 designSize, int row, Func<bool> occupies = null) {
            var slot = new HudSlot(corner, designSize, row, occupies);
            slots.Add(slot);
            slots.Sort(ByRow);
            dirty = true;
            return slot;
        }

        public static void Clear() {
            for (int i = 0; i < slots.Count; i++) { slots[i].Dispose(); }
            slots.Clear();
            dirty = true;
        }

        public static void Layout() => Aim();

        private static void Aim() {
            var current = new Point(GameMain.GraphicsWidth, GameMain.GraphicsHeight);
            float bottom = UsableBottom();
            bool restacked = Restack();
            bool resized = current != resolution || scale != GUI.Scale || usableBottom != bottom;
            if (!dirty && !restacked && !resized) { return; }
            resolution = current;
            scale = GUI.Scale;
            usableBottom = bottom;
            bool snap = dirty || resized;
            dirty = false;

            float padding = Padding;
            Span<float> stacked = stackalloc float[CornerCount];
            for (int i = 0; i < CornerCount; i++) { stacked[i] = padding; }

            for (int i = 0; i < slots.Count; i++) {
                HudSlot slot = slots[i];
                Vector2 size = slot.DesignSize * scale;
                int corner = (int)slot.Corner;
                float across = (corner & LeftBit) != 0 ? padding : resolution.X - padding - size.X;
                float along = (corner & TopBit) != 0 ? stacked[corner] : usableBottom - stacked[corner] - size.Y;
                if (slot.Placed) { stacked[corner] += size.Y + padding; }
                slot.Size = size;
                slot.Scale = scale;
                slot.Aim(new Vector2(Scalar.Round(across), Scalar.Round(along)), snap);
            }
        }

        private static bool Restack() {
            bool changed = false;
            for (int i = 0; i < slots.Count; i++) {
                HudSlot slot = slots[i];
                if (slot.Occupies == slot.Placed) { continue; }
                slot.Placed = slot.Occupies;
                changed = true;
            }
            return changed;
        }

        private static float UsableBottom() {
            int inventoryTop = HUDLayoutSettings.InventoryTopY;
            return inventoryTop > 0 && inventoryTop < GameMain.GraphicsHeight
                ? inventoryTop
                : GameMain.GraphicsHeight;
        }
    }
}
