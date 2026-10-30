using System;
using Barotrauma;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ScpItdMod.Thermal {
    internal sealed class FeltHealthLayout : IDisposable {
        private readonly GUILayoutGroup parent;
        private readonly CharacterHealth health;
        private readonly GUIProgressBar healthBar;
        private readonly RectTransform[] siblings;
        private readonly Vector2[] sizes;
        private bool disposed;
        private CharacterInventory synchronizedInventory;
        private Rectangle inventoryBounds;
        internal GUICustomComponent Row { get; }
        internal int Width => parent.Rect.Width;
        internal float TextInset => healthBar.Rect.Left - parent.Rect.Left;
        internal FeltHealthLayout(CharacterHealth health, GUIProgressBar healthBar, Action<SpriteBatch, GUICustomComponent> draw, Action<float, GUICustomComponent> update = null) {
            this.health = health ?? throw new ArgumentNullException(nameof(health));
            this.healthBar = healthBar ?? throw new ArgumentNullException(nameof(healthBar));
            RectTransform after = healthBar.RectTransform.Parent;
            if (after?.Parent?.GUIComponent is not GUILayoutGroup group || group.IsHorizontal) throw new ArgumentException("Expected a child of the vertical health layout", nameof(healthBar));
            parent = group;
            int count = parent.RectTransform.CountChildren;
            siblings = new RectTransform[count]; sizes = new Vector2[count];
            for (int i = 0; i < count; i++) {
                siblings[i] = parent.RectTransform.GetChild(i);
                sizes[i] = siblings[i].RelativeSize;
            }
            Row = new GUICustomComponent(new RectTransform(new Vector2(1, 0), parent.RectTransform), draw, update) { CanBeFocused = true };
            Row.RectTransform.RepositionChildInHierarchy(parent.RectTransform.GetChildIndex(after) + 1);
        }
        internal void Resize(float height) {
            if (disposed) throw new ObjectDisposedException(nameof(FeltHealthLayout));
            int pixels = Math.Max(0, (int)MathF.Ceiling(height));
            Point minimum = new Point(0, pixels), maximum = new Point(int.MaxValue, pixels);
            if (Row.RectTransform.MinSize != minimum || Row.RectTransform.MaxSize != maximum) {
                Row.RectTransform.MinSize = minimum; Row.RectTransform.MaxSize = maximum;
                parent.Recalculate();
            }
            SynchronizeInventory();
        }
        private void SynchronizeInventory() {
            Character character = health.Character;
            if (character == null || character.Removed) { synchronizedInventory = null; return; }
            CharacterInventory inventory = character.Inventory;
            GUIComponent container = health.InventorySlotContainer;
            if (inventory == null || container == null) { synchronizedInventory = null; return; }
            Rectangle bounds = container.Rect;
            if (ReferenceEquals(synchronizedInventory, inventory) && inventoryBounds == bounds) return;
            inventory.RefreshSlotPositions();
            synchronizedInventory = inventory; inventoryBounds = bounds;
        }
        public void Dispose() {
            if (disposed) return;
            disposed = true;
            Row.RectTransform.Parent = null;
            for (int i = 0; i < siblings.Length; i++) if (ReferenceEquals(siblings[i].Parent, parent.RectTransform)) siblings[i].RelativeSize = sizes[i];
            parent.Recalculate();
            SynchronizeInventory();
        }
    }
}
