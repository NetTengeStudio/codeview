using System;
using System.Collections.Generic;
using System.Globalization;
using Barotrauma;
using Barotrauma.Items.Components;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ScpItdMod.Core;
using ScpItdMod.Motion;

namespace ScpItdMod.Inv {
    // The container deck: tabs (big boxes only), item cells and pager unfolding from the ring wedge that
    // holds the opened container. Geometry is OrbitDeck, ordering rules are OrbitFlow, and every
    // moving value (reveal, bloom, hover, press, slide, FLIP shift, pill, dots) is a
    // Motion handle written once per tick in ApplyContainer and only read in Draw.
    public static class SubPanel {
        private const double WaveInterval = 0.028;
        private const float BloomStartScale = 0.9f, EmptyAlpha = 0.42f, FilledAlpha = 0.64f, AccentShare = 0.16f;
        private const float HoverGrow = 0.05f, HoverIcon = 0.16f, SwipeDistance = 22f, PillKick = 3.5f, HoverKick = 3.2f;
        private const int BackChip = ItemCategoryChips.Back;
        // Inner padding of a tab label, in pixels at scale 1.
        private const float TabPad = 14f;

        private static readonly List<ItemInventory> path = new List<ItemInventory>(8);
        private static readonly List<int> visible = new List<int>(128);
        private static readonly List<int> chipKinds = new List<int>(OrbitDeck.MaxChips + 1);
        private static readonly string[] chipLabels = new string[OrbitDeck.MaxChips], chipShown = new string[OrbitDeck.MaxChips];
        private static readonly float[] tabNatural = new float[OrbitDeck.MaxChips];
        private static readonly int[] counts = new int[OrbitDeck.MaxChips];
        private static readonly string[] stackLabels = BuildStackLabels();

        private static readonly Dictionary<VisualSlot, int> hitCells = new Dictionary<VisualSlot, int>(128);
        private static VisualSlot[] hitSlots;
        private static readonly int[] cellKey = new int[DeckMotion.Cells], nextKey = new int[DeckMotion.Cells], cellSource = new int[DeckMotion.Cells];
        private static readonly bool[] tabUsed = new bool[DeckMotion.Tabs];
        private static readonly Item[] cellItem = new Item[DeckMotion.Cells];
        private static readonly int[] cellSlot = new int[DeckMotion.Cells], cellCount = new int[DeckMotion.Cells];
        private static readonly Item[] ghostItem = new Item[DeckMotion.Cells], previousItem = new Item[DeckMotion.Cells];

        private static bool open;
        private static Character owner;
        private static Inventory current;
        private static Item[] slotItems = Array.Empty<Item>();
        private static ItemCategory[] slotCategories = Array.Empty<ItemCategory>();
        private static OrbitDeck layout;
        private static Point resolution;
        private static int labelsVersion, layoutLabelsVersion = -1;
        private static int layoutCount = -1, layoutChips = -1, page, pages = 1, anchor = InvRing.None, pageDirection, hoverCell = -1, shownCells, shownTabs;
        private static ItemCategory category = ItemCategory.All;
        private static Item focusItem;
        private static DeckNavigation dwell;
        private static InventorySlotLease lease;
        private static Item observedWorld;
        private static Inventory pressInventory;
        private static VisualSlot pressSlot;
        private static int pressIndex = -1;
        private static DeckPhase phase;
        private static bool shownLocked;
        private static Vector2 pressPoint;
        private static Item pressed;
        private static bool filterDirty = true, keysValid;
        private static Inventory shown;
        private static DeckMotion deck;
        private static readonly HarmonyLib.AccessTools.FieldRef<Sprite, Vector2> SpriteOffset = HarmonyLib.AccessTools.FieldRefAccess<Sprite, Vector2>("offset");
        private static int activeTab = -1, pillKind = int.MinValue;
        private static string pagerText = "";
        private static int pagerKey = -1;

        public static bool IsOpen => open;
        public static bool Active =>
            open && owner != null && ReferenceEquals(owner, Character.Controlled) &&
            !owner.Removed && !owner.IsDead && !owner.IsIncapacitated && owner.Stun <= 0 && owner.Inventory != null;

        internal static bool InputAllowed => Active && !PlayView.Busy && !PlayView.Typing && GUI.MouseOn == null;

        // The item under the pointer, for the ring hub caption.
        public static Item FocusItem => Active ? focusItem : null;
        internal static Item ViewedItem => Active ? current?.Owner as Item : null;

        private static string[] BuildStackLabels() {
            var labels = new string[Inventory.MaxPossibleStackSize + 1];
            for (int i = 0; i < labels.Length; i++) { labels[i] = "x" + i.ToString(CultureInfo.InvariantCulture); }
            return labels;
        }

        private const int Detached = -2;

        public static void Open(Character character) {
            Clear();
            owner = character; open = true;
            category = ItemCategory.All; page = 0; filterDirty = true; layoutCount = -1;
            Inventory.highlightedSubInventorySlots.RemoveWhere(ReleaseNativeHighlight);
            EnsureMotion();
        }

        private static bool ReleaseNativeHighlight(Inventory.SlotReference reference) {
            if (!Manages(reference.Item) || !ContainerAccess.TryRoot(reference.Inventory, out Entity root) || !ReferenceEquals(root, owner)) { return false; }
            reference.Inventory.OpenState = reference.Inventory.HideTimer = 0;
            return true;
        }

        public static Item WorldContainer(Character character) {
            Item selected = character?.SelectedItem;
            if (selected == null || selected.Removed || selected.ParentInventory != null || selected.OwnInventory is not ItemInventory inventory) { return null; }
            ItemContainer container = inventory.Container;
            if (container == null || !container.DrawInventory || inventory.RectTransform != null || !selected.IsInteractable(character)) { return null; }
            int frames = 0;
            foreach (ItemComponent component in selected.Components) { if (component.GuiFrame != null) { frames++; } }
            return frames <= 1 && character.CanInteractWith(selected) ? selected : null;
        }

        public static void OpenWorld(Item item, int wedge) {
            if (OpenItem(item)) { anchor = wedge; }
        }

        internal static void Prepare() {
            if (!Active) { return; }
            Item selected = owner.SelectedItem;
            if (!ReferenceEquals(observedWorld, selected)) {
                observedWorld = selected;
                Item world = WorldContainer(owner);
                if (world != null) { OpenItem(world); }
            }
            ValidatePath();
            BeforeNative();
        }

        internal static void BeforeNative() {
            Inventory.SlotReference selected = Inventory.SelectedSlot;
            if (selected != null && (Owns(selected.ParentInventory) || ReferenceEquals(selected.ParentInventory, owner?.Inventory)) &&
                (!InputAllowed || !selected.Slot.MouseOn() || selected.Item != null && !ReferenceEquals(selected.Item.ParentInventory, selected.ParentInventory))) { Inventory.selectedSlot = null; }
        }

        internal static bool Owns(Inventory inventory) => inventory != null && ReferenceEquals(inventory, current);

        public static void Retire() {
            if (!DeckPresence.Draws(phase)) { Clear(); return; }
            Inventory.DraggingItems.Clear(); Inventory.DraggingSlot = null;
            ReleaseCurrent();
            path.Clear(); current = null; open = false; focusItem = null; pressed = null; hoverCell = -1;
            dwell.Reset();
            deck?.Reveal.To(0, MotionPresets.Fade);
        }

        public static void Tick() {
            if (open && !Active) { Clear(); }
            if (open || !DeckPresence.Draws(phase)) { return; }
            phase = DeckPresence.Next(phase, false, deck?.Alive == true ? deck.Reveal.Value : 0f);
            if (phase == DeckPhase.Hidden) { Clear(); }
        }

        public static void Clear() {
            if (open) { Inventory.DraggingItems.Clear(); Inventory.DraggingSlot = null; }
            ReleaseCurrent();
            observedWorld = null; CancelPress();
            path.Clear(); current = null; owner = null; open = false; focusItem = null; pressed = null; anchor = InvRing.None;
            dwell.Reset(); phase = DeckPhase.Hidden; shownLocked = false; pages = 1; page = 0; shownCells = 0; shownTabs = 0;
            Array.Clear(cellCount, 0, cellCount.Length);
            filterDirty = true; layoutCount = -1; shown = null; keysValid = false; hoverCell = -1; activeTab = -1; pillKind = int.MinValue;
            slotItems = Array.Empty<Item>(); slotCategories = Array.Empty<ItemCategory>();
            DisposeMotion();
            visible.Clear(); chipKinds.Clear();
            Array.Clear(cellItem, 0, cellItem.Length);
            Array.Clear(ghostItem, 0, ghostItem.Length);
        }

        private static void DisposeMotion() { deck?.Dispose(); deck = null; }

        private static void EnsureMotion() {
            if (deck?.Alive == true) { return; }
            DisposeMotion();
            deck = new DeckMotion(MotionClient.Runtime);
            keysValid = false;
        }

        public static void Update(float deltaTime, Camera cam, InvRing ring) {
            if (!Active) { if (open) { Retire(); } return; }
            if (deck?.Alive != true) { EnsureMotion(); }
            Prepare();
            RefreshFilter();
            RefreshLayout(ring);
            ApplyContainer(deltaTime, cam);
            phase = DeckPresence.Next(phase, current != null && layout.Capacity > 0 && shown != null, deck.Reveal.Value);
            if (!DeckPresence.Draws(phase)) { Settle(); }
            HandleInput();
        }

        private static void Settle() {
            InventoryTrace.Record("settle", null, current);
            shown = null; layout = default; layoutCount = -1; shownCells = 0; shownTabs = 0;
            Array.Clear(cellItem, 0, cellItem.Length);
            Array.Clear(ghostItem, 0, ghostItem.Length);
        }

        private static void ValidatePath() {
            for (int i = 0; i < path.Count; i++) {
                ItemInventory inventory = path[i];
                bool linked = i == 0 || ReferenceEquals(((Item)inventory.Owner).ParentInventory, path[i - 1]);
                if (linked && ContainerAccess.CanOpen(owner, inventory) && (i != 0 || !EquipmentStorage((Item)inventory.Owner))) { continue; }
                InventoryTrace.Record("access-lost", inventory.Owner as Item, inventory);
                CancelInvalidDrag(i);
                path.RemoveRange(i, path.Count - i);
                break;
            }
            if (path.Count > 0) {
                Item root = (Item)path[0].Owner;
                anchor = Detached;
                if (root.ParentInventory is CharacterInventory holder) {
                    for (int i = 0; i < holder.Capacity; i++) {
                        int wedge = InvUi.RingIndexOf(holder, i);
                        if (wedge >= 0 && holder.slots[i].Contains(root)) { anchor = wedge; break; }
                    }
                }
            }
            SwitchCurrent(path.Count > 0 ? path[path.Count - 1] : null);
        }

        private static void CancelInvalidDrag(int first) {
            for (int i = Inventory.DraggingItems.Count - 1; i >= 0; i--) {
                Inventory source = Inventory.DraggingItems[i].ParentInventory;
                for (int j = first; j < path.Count; j++) {
                    if (!ReferenceEquals(source, path[j])) { continue; }
                    Inventory.DraggingItems.RemoveAt(i); break;
                }
            }
            if (Inventory.DraggingItems.Count == 0) { Inventory.DraggingSlot = null; }
        }

        private static void SwitchCurrent(ItemInventory wanted) {
            if (ReferenceEquals(wanted, current)) { return; }
            ReleaseCurrent(); current = wanted;
            shown = null; focusItem = null; hoverCell = -1;
            if (wanted != null) { lease = new InventorySlotLease(wanted); }
            page = 0; category = ItemCategory.All; filterDirty = true; layoutCount = -1; pageDirection = 0;
            slotItems = Array.Empty<Item>(); slotCategories = Array.Empty<ItemCategory>();
        }

        private static void RefreshFilter() {
            if (current == null || current.slots == null) {
                Array.Clear(counts, 0, counts.Length);
                if (!DeckPresence.Draws(phase)) { visible.Clear(); chipKinds.Clear(); pages = 1; }
                return;
            }
            int capacity = current.slots.Length;
            if (slotItems.Length != capacity) {
                slotItems = new Item[capacity]; slotCategories = new ItemCategory[capacity]; filterDirty = true;
            }
            bool changed = filterDirty;
            Array.Clear(counts, 0, counts.Length);
            for (int i = 0; i < capacity; i++) {
                Item item = current.GetItemAt(i);
                counts[0] += item != null ? 1 : 0;
                if (!ReferenceEquals(item, slotItems[i])) {
                    slotItems[i] = item; slotCategories[i] = ItemCategories.Of(item); changed = true;
                }
                if (item != null) { counts[(int)slotCategories[i]]++; }
            }
            if (category != ItemCategory.All && counts[(int)category] == 0) { category = ItemCategory.All; changed = true; }
            if (!changed) { return; }
            filterDirty = false;
            ItemCategoryChips.Fill(chipKinds, counts, path.Count > 1);
            if (category != ItemCategory.All && !chipKinds.Contains((int)category)) { category = ItemCategory.All; }
            visible.Clear();
            for (int i = 0; i < capacity; i++) {
                if (category == ItemCategory.All) { visible.Add(i); continue; }
                if (slotItems[i] != null && slotCategories[i] == category) { visible.Add(i); }
            }
            activeTab = -1;
            for (int i = 0; i < chipKinds.Count && i < chipLabels.Length; i++) {
                int kind = chipKinds[i];
                if (kind == BackChip) {
                    SetLabel(i, "‹ " + ToolBox.LimitString(((Item)path[path.Count - 2].Owner).Name, GUIStyle.SmallFont, 90).Value);
                    continue;
                }
                if (kind == (int)category) { activeTab = i; }
                string text = ItemCategoryChips.Label((ItemCategory)kind);
                SetLabel(i, counts[kind] > 0 ? text + " " + counts[kind].ToString(CultureInfo.InvariantCulture) : text);
            }
        }

        private static void SetLabel(int index, string text) {
            if (chipLabels[index] == text) { return; }
            chipLabels[index] = text; labelsVersion++;
        }

        private static void RefreshLayout(InvRing ring) {
            if (current == null || anchor == InvRing.None) {
                if (!DeckPresence.Draws(phase)) { layout = default; layoutCount = -1; }
                return;
            }
            Point screen = new Point(GameMain.GraphicsWidth, GameMain.GraphicsHeight);
            int chips = chipKinds.Count;
            float angle = anchor >= 0 ? InvRing.Angle(anchor) : -MathF.PI * .5f;
            if (screen == resolution && layoutCount == visible.Count && layoutChips == chips &&
                layoutLabelsVersion == labelsVersion && layout.RingX == ring.X && layout.RingY == ring.Y && layout.Angle == angle) {
                return;
            }
            OrbitDeck previous = layout;
            resolution = screen; layoutCount = visible.Count; layoutChips = chips;
            layoutLabelsVersion = labelsVersion;
            float pad = TabPad * GUI.Scale;
            for (int i = 0; i < chips; i++) { tabNatural[i] = GUIStyle.SmallFont.MeasureString(chipLabels[i]).X + pad; }
            layout = new OrbitDeck(screen.X, screen.Y, ring.X, ring.Y, ring.Outer, angle, chips, visible.Count, tabNatural);
            // A label wider than its tab is shortened to the width the tab was given, so it never runs into the next tab.
            for (int i = 0; i < chips; i++) {
                chipShown[i] = ToolBox.LimitString(chipLabels[i], GUIStyle.SmallFont, Math.Max(1, (int)(layout.TabPixels[i] - pad))).Value;
            }
            if (previous.RingX != layout.RingX || previous.RingY != layout.RingY) { deck.ResetPlacement(); }
            if (previous.Capacity != layout.Capacity || previous.Columns != layout.Columns) { keysValid = false; }
            pages = layout.Pages;
            page = Math.Clamp(page, 0, pages - 1);
        }

        private static Vector2 CellCentre(int local, float angle, float scale, Vec2 shift) {
            BandSlot cell = layout.Cells[local];
            return SampleCellCentre(cell, new Vector2(layout.RingX, layout.RingY), angle, scale, shift);
        }
        private static Vector2 SampleCellCentre(in BandSlot cell, Vector2 centre, float angle, float scale, Vec2 shift) =>
            new Vector2(centre.X + MathF.Cos(angle) * cell.Mid * scale + shift.X, centre.Y + MathF.Sin(angle) * cell.Mid * scale + shift.Y);

        private static float CellScale(int local) => SampleCellScale(deck, local, 0);
        private static float SampleCellScale(DeckMotion motion, int local, double ago) {
            float bloom = Scalar.Clamp01(motion.Bloom[local].Sample(ago)), hover = motion.Hover[local].Sample(ago), press = motion.Press[local].Sample(ago);
            return (BloomStartScale + (1f - BloomStartScale) * bloom) * (1f + HoverGrow * hover) * (1f - (1f - press) * 0.5f);
        }
        private static float SpriteFit(float thickness, float bloom, float hover, float press) => thickness * .72f * (.7f + .3f * bloom) * (1 + HoverIcon * hover) * press;
        private readonly struct CellSpritePath : IMotionPath2D {
            private readonly DeckMotion motion;
            private readonly BandSlot band;
            private readonly int local;
            private readonly Vector2 centre, offset;
            private readonly Vec2 size;
            private readonly float angle, alpha;
            public bool Alive => motion.Alive;
            internal CellSpritePath(DeckMotion motion, in BandSlot band, int local, Vector2 centre, Sprite sprite, float alpha) {
                this.motion = motion; this.band = band; this.local = local; this.centre = centre; this.alpha = alpha;
                offset = SpriteOffset(sprite); angle = sprite.rotation;
                float maximum = MathF.Max(sprite.size.X, sprite.size.Y); Rectangle source = sprite.SourceRect;
                size = new Vec2(source.Width / maximum, source.Height / maximum);
            }
            public MotionPose2D Sample(double ago) {
                float scale = SampleCellScale(motion, local, ago), bloom = Scalar.Clamp01(motion.Bloom[local].Sample(ago));
                Vector2 at = SampleCellCentre(band, centre, motion.CellAngle[local].Sample(ago), scale, motion.Shift[local].Sample(ago)) + offset;
                float fit = SpriteFit(band.Thickness * scale, bloom, motion.Hover[local].Sample(ago), motion.Press[local].Sample(ago));
                return new MotionPose2D(new Vec2(at.X, at.Y), size * fit, angle, alpha * motion.Reveal.Sample(ago) * bloom);
            }
        }

        private static void ApplyContainer(float deltaTime, Camera cam) {
            if (current == null || current.slots == null) { Leave(); return; }
            lease.Bind();
            VisualSlot[] slots = current.visualSlots;
            if (slots == null || layout.Capacity <= 0) { return; }
            if (!ReferenceEquals(hitSlots, slots)) {
                hitCells.Clear();
                for (int i = 0; i < slots.Length; i++) if (slots[i] != null) hitCells.Add(slots[i], -1);
                hitSlots = slots;
            }
            if (!ReferenceEquals(shown, current)) { BeginContainer(); }
            shownLocked = current.Locked;
            for (int i = 0; i < slots.Length; i++) {
                if (slots[i] != null) { slots[i].Rect = slots[i].InteractRect = Rectangle.Empty; slots[i].DrawOffset = Vector2.Zero; hitCells[slots[i]] = -1; }
            }

            deck.Reveal.To(1, MotionPresets.Fade);
            Vector2 mouse = PlayerInput.MousePosition;
            float mx = mouse.X - layout.RingX, my = mouse.Y - layout.RingY;
            bool mouseDown = PlayerInput.PrimaryMouseButtonHeld();

            ApplyTabs(mx, my);
            ApplyPager();
            ApplyCellKeys();

            int over = -1;
            for (int local = 0; local < layout.Cells.Length && local < DeckMotion.Cells; local++) {
                BandSlot cell = layout.Cells[local];
                Place(deck.CellAngle[local], ref deck.CellPlaced[local], cell.Angle);
                bool inside = CellContains(local, mx, my, 0);
                bool filled = cellItem[local] != null;
                if (inside) { over = local; }
                deck.Hover[local].To(inside ? 1 : 0, MotionPresets.Hover);
                deck.Press[local].To(inside && filled && mouseDown ? 0.93f : 1f, MotionPresets.Press);
                if (ghostItem[local] != null && deck.Bloom[local].Value < 0.02f) { ghostItem[local] = null; }
                int index = cellSlot[local];
                cellCount[local] = (uint)index < (uint)current.slots.Length ? current.slots[index].Items.Count : 0;
                if ((uint)index >= (uint)slots.Length || slots[index] == null) { continue; }
                hitCells[slots[index]] = local;
                {
                    float angle = deck.CellAngle[local].Value, scale = CellScale(local);
                    Vector2 c = CellCentre(local, angle, scale, deck.Shift[local].Value);
                    int side = Math.Max(8, (int)(cell.Thickness * scale));
                    slots[index].Rect = new Rectangle((int)c.X - side / 2, (int)c.Y - side / 2, side, side);
                    slots[index].InteractRect = inside ? Rectangle.Union(slots[index].Rect,
                        new Rectangle((int)mouse.X - 1, (int)mouse.Y - 1, 3, 3)) : Rectangle.Empty;
                }
                slots[index].IsMoving = false;
            }
            if (over != hoverCell) {
                if (over >= 0 && cellItem[over] != null) { deck.Hover[over].Impulse(HoverKick); }
                hoverCell = over;
            }
            focusItem = hoverCell >= 0 && Inventory.DraggingItems.Count == 0 ? cellItem[hoverCell] : null;
            if (InputAllowed) {
                InvUi.RefreshInputSurface();
                for (int local = 0; local < shownCells; local++) {
                    int index = cellSlot[local];
                    if ((uint)index < (uint)slots.Length) { current.UpdateSlot(slots[index], index, current.GetItemAt(index), true); }
                }
            }
        }

        private static void Leave() {
            deck.Reveal.To(0, MotionPresets.Fade);
            hoverCell = -1; focusItem = null;
            for (int i = 0; i < DeckMotion.Cells; i++) { deck.Hover[i].To(0, MotionPresets.Hover); deck.Press[i].To(1f, MotionPresets.Press); }
            for (int i = 0; i < DeckMotion.Tabs; i++) { deck.TabHover[i].To(0, MotionPresets.Hover); }
        }

        private static void BeginContainer() {
            InventoryTrace.Record("begin-container", current?.Owner as Item, current);
            shown = current;
            deck.TabWave.Cancel(); deck.ItemWave.Cancel();
            deck.ResetPlacement();
            deck.Reveal.Snap(0);
            deck.PillOn.Snap(0);
            for (int i = 0; i < DeckMotion.Cells; i++) {
                deck.Hover[i].Snap(0); deck.Press[i].Snap(1); deck.Bloom[i].Snap(0); deck.Shift[i].Snap(default);
            }
            Array.Clear(cellKey, 0, cellKey.Length);
            Array.Clear(tabUsed, 0, tabUsed.Length);
            Array.Clear(cellItem, 0, cellItem.Length);
            Array.Clear(ghostItem, 0, ghostItem.Length);
            keysValid = true;
            hoverCell = -1; pageDirection = 0; shownCells = 0; shownTabs = 0;
        }

        private static void Place(MotionHandle<float> handle, ref bool placed, float angle) {
            if (!placed) { handle.Snap(angle); placed = true; }
            else { handle.To(angle, MotionPresets.Layout); }
        }

        // Tabs: new categories bloom in on a ripple, the others only slide to their new angle; one
        // pill follows the active category and gets a small kick whenever it lands somewhere new.
        private static void ApplyTabs(float mx, float my) {
            int tabs = Math.Min(layout.Chips.Length, Math.Min(chipKinds.Count, DeckMotion.Tabs));
            int fresh = 0;
            for (int i = 0; i < tabs; i++) {
                Place(deck.TabAngle[i], ref deck.TabPlaced[i], layout.Chips[i].Angle);
                // A tab blooms once when it appears; later frames never reset a bloom that is already running.
                if (!tabUsed[i]) {
                    deck.TabBloom[i].Snap(0);
                    deck.TabWave.Queue(deck.TabBloom[i], WaveInterval * 1.5 * fresh++);
                }
                tabUsed[i] = true;
                bool inside = TabContains(i, mx, my, 0);
                deck.TabHover[i].To(inside ? 1 : 0, MotionPresets.Hover);
            }
            for (int i = tabs; i < DeckMotion.Tabs; i++) {
                if (tabUsed[i]) { deck.TabBloom[i].Snap(0); tabUsed[i] = false; }
            }
            shownTabs = tabs;

            if (activeTab >= 0 && activeTab < tabs) {
                int activeKind = chipKinds[activeTab];
                float target = layout.Chips[activeTab].Angle;
                if (!deck.PillPlaced) { deck.PillAngle.Snap(target); deck.PillPlaced = true; }
                else if (activeKind != pillKind) { deck.PillBump.Impulse(PillKick); }
                pillKind = activeKind;
                deck.PillAngle.To(target, MotionPresets.Card);
                deck.PillOn.To(1, MotionPresets.Hover);
            } else {
                deck.PillOn.To(0, MotionPresets.Hover);
            }
            deck.PillBump.To(0, MotionPresets.Pop);
            deck.TabWave.Play(MotionPresets.Card);
        }

        private static void ApplyPager() {
            if (layout.HasDots) {
                float target = layout.DotAngles[Math.Clamp(page, 0, layout.DotAngles.Length - 1)];
                if (!deck.DotPlaced) { deck.DotAngle.Snap(target); deck.DotPlaced = true; }
                deck.DotAngle.To(target, MotionPresets.Card);
            }
            deck.DotBump.To(0, MotionPresets.Pop);
        }

        // One event per change of what the cells show: moved items keep flying from their old cell
        // (FLIP), items that just appeared bloom on a ripple from the anchor column, gone items fade.
        private static void ApplyCellKeys() {
            int cells = Math.Min(layout.Cells.Length, DeckMotion.Cells);
            int first = page * layout.Capacity;
            bool changed = !keysValid || shownCells != cells;
            for (int local = 0; local < cells; local++) {
                int at = first + local;
                Item item = at < visible.Count ? slotItems[visible[at]] : null;
                int key = ItemKey(item);
                nextKey[local] = key;
                cellSlot[local] = at < visible.Count ? visible[at] : -1;
                previousItem[local] = cellItem[local];
                cellItem[local] = item;
                if (key != cellKey[local]) { changed = true; }
            }
            if (!changed) { return; }
            if (!keysValid) { Array.Clear(cellKey, 0, cellKey.Length); keysValid = true; }
            OrbitFlow.Match(cellKey, nextKey, cellSource);
            deck.ItemWave.Cancel();
            int direction = pageDirection;
            pageDirection = 0;
            for (int local = 0; local < cells; local++) {
                MotionHandle<float> bloom = deck.Bloom[local];
                if (nextKey[local] == 0) {
                    deck.Shift[local].Snap(default);
                    bloom.To(0, MotionPresets.Card);
                    ghostItem[local] = cellKey[local] != 0 && !HasKey(nextKey, cellKey[local]) ? previousItem[local] : null;
                    continue;
                }
                ghostItem[local] = null;
                int source = cellSource[local];
                // The item stayed in its cell: finish whatever bloom it is in, never restart it.
                if (source == local) {
                    if (bloom.Value < 0.98f) { bloom.To(1, MotionPresets.Card); }
                    continue;
                }
                if (source >= 0 && source != local) {
                    Vector2 from = CellCentre(source, layout.Cells[source].Angle, 1f, default), to = CellCentre(local, layout.Cells[local].Angle, 1f, default);
                    deck.Shift[local].Snap(new Vec2(from.X - to.X, from.Y - to.Y));
                    deck.Shift[local].To(default, MotionPresets.Layout);
                    bloom.To(1, MotionPresets.Hover);
                    continue;
                }
                bloom.Snap(0);
                float a = layout.Cells[local].Angle;
                deck.Shift[local].Snap(direction == 0 ? default : new Vec2(-MathF.Sin(a) * SwipeDistance * direction, MathF.Cos(a) * SwipeDistance * direction));
                deck.Shift[local].To(default, MotionPresets.Layout);
                deck.ItemWave.Queue(bloom, OrbitFlow.WaveDelay(layout.RowOf(local), layout.ColumnOf(local), layout.Columns, WaveInterval));
            }
            deck.ItemWave.Play(MotionPresets.Card);
            Array.Copy(nextKey, cellKey, cells);
            for (int local = cells; local < cellKey.Length; local++) { cellKey[local] = 0; }
            shownCells = cells;
        }

        private static int ItemKey(Item item) =>
            item == null ? 0 : (item.ID != 0 ? item.ID : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(item) | 1);

        private static bool HasKey(int[] keys, int wanted) {
            for (int i = 0; i < keys.Length; i++) { if (keys[i] == wanted) { return true; } }
            return false;
        }

        private static void HandleInput() {
            if (!InputAllowed) { dwell.Reset(); CancelPress(); return; }
            Vector2 mouse = PlayerInput.MousePosition;
            float dx = mouse.X - layout.RingX, dy = mouse.Y - layout.RingY;
            if (current != null && DeckPresence.Interactive(phase)) {
                if (PlayerInput.PrimaryMouseButtonClicked() && Inventory.DraggingItems.Count == 0) {
                    for (int i = 0; i < layout.Chips.Length && i < chipKinds.Count; i++) {
                        if (!TabContains(i, dx, dy, 0)) { continue; }
                        int kind = chipKinds[i];
                        if (kind == BackChip) {
                            InventoryTrace.Record("back", current.Owner as Item, current);
                            if (path.Count > 1) { path.RemoveAt(path.Count - 1); ValidatePath(); }
                            dwell.Reset(); filterDirty = true;
                        } else if (category != (ItemCategory)kind) { category = (ItemCategory)kind; page = 0; filterDirty = true; }
                        return;
                    }
                    if (pages > 1 && layout.Previous.Contains(dx, dy, 0)) { ChangePage(-1); return; }
                    if (pages > 1 && layout.Next.Contains(dx, dy, 0)) { ChangePage(1); return; }
                }
                if (pages > 1 && Hit((int)mouse.X, (int)mouse.Y, 6) && PlayerInput.ScrollWheelSpeed != 0) {
                    ChangePage(-Math.Sign(PlayerInput.ScrollWheelSpeed)); return;
                }
            }
            Inventory.SlotReference selected = Inventory.SelectedSlot;
            Item hovered = selected?.Slot != null && selected.Slot.MouseOn() ? selected.Item : null;
            if (!Manages(hovered)) { dwell.Reset(); return; }
            bool nested = ReferenceEquals(selected.ParentInventory, current);
            bool selectKey = GameSettings.CurrentConfig.KeyMap.Bindings[InputType.Select].MouseButton != MouseButton.PrimaryMouse &&
                PlayerInput.KeyHit(InputType.Select);
            bool activate = PlayerInput.SecondaryMouseButtonDown() || selectKey;
            bool dragging = Inventory.DraggingItems.Count > 0;
            if (activate || dwell.Step(ItemKey(hovered), Timing.TotalTime, nested, false, !dragging && PlayerInput.PrimaryMouseButtonHeld())) {
                OpenAt(hovered, selected.ParentInventory, selected.SlotIndex, activate);
            }
            if (!PlayerInput.PrimaryMouseButtonHeld() && !PlayerInput.PrimaryMouseButtonClicked()) { CancelPress(); }
        }


        private static bool Hit(int x, int y, int pad) {
            if (current == null || !ReferenceEquals(shown, current) || layout.Capacity <= 0) { return false; }
            float dx = x - layout.RingX, dy = y - layout.RingY;
            for (int i = 0; i < layout.Chips.Length; i++) { if (TabContains(i, dx, dy, pad)) { return true; } }
            for (int i = 0; i < layout.Cells.Length; i++) { if (CellContains(i, dx, dy, pad)) { return true; } }
            return pages > 1 && (layout.Previous.Contains(dx, dy, pad) || layout.Next.Contains(dx, dy, pad));
        }

        // Hit testing uses the resting cell, not the animated scale: hover and press resize the drawn cell, so testing
        // against the drawn size flips the hover target at the cell edge every frame. Position still follows the motion.
        private const float RestingCellScale = 1f;

        private static bool CellContains(int local, float dx, float dy, float pad) => deck?.Alive == true &&
            (uint)local < (uint)layout.Cells.Length && (uint)local < DeckMotion.Cells &&
            layout.Cells[local].ContainsPose(dx - deck.Shift[local].Value.X, dy - deck.Shift[local].Value.Y, deck.CellAngle[local].Value, RestingCellScale, pad);

        private static bool TabContains(int local, float dx, float dy, float pad) {
            if (deck?.Alive != true || (uint)local >= (uint)layout.Chips.Length || (uint)local >= DeckMotion.Tabs) return false;
            float bloom = Scalar.Clamp01(deck.TabBloom[local].Value);
            return bloom > DeckPresence.Epsilon && layout.Chips[local].ContainsPose(dx, dy, deck.TabAngle[local].Value, BloomStartScale + (1f - BloomStartScale) * bloom, pad);
        }

        internal static bool MouseOnPrefix(VisualSlot slot, ref bool result) {
            if (!Active || slot == null || !hitCells.TryGetValue(slot, out int local)) return true;
            Vector2 mouse = PlayerInput.MousePosition;
            result = CellContains(local, mouse.X - layout.RingX, mouse.Y - layout.RingY, 0);
            return false;
        }

        internal static bool CanOpen(Item item) {
            if (item == null || item.Removed) { return false; }
            var inventories = item.OwnInventories;
            for (int i = 0; i < inventories.Length; i++) {
                if (ContainerAccess.CanOpen(Character.Controlled, inventories[i])) { return true; }
            }
            return false;
        }

        internal static bool Manages(Item item) => CanOpen(item) && !EquipmentStorage(item);

        private static bool EquipmentStorage(Item item) {
            Inventory parent = item.ParentInventory;
            while (parent is ItemInventory storage) { item = (Item)storage.Owner; parent = item.ParentInventory; }
            if (parent is not CharacterInventory holder) { return false; }
            for (int i = 0; i < holder.Capacity; i++) {
                if (holder.SlotTypes[i] != InvSlotType.Any && holder.slots[i].Contains(item)) { return true; }
            }
            return false;
        }

        internal static bool OpenItem(Item item) => OpenAt(item, null, -1, true);

        private static bool OpenAt(Item item, Inventory from, int index, bool cycle) {
            if (!Active || !Manages(item)) { return false; }
            if (from != null && (!ReferenceEquals(item.ParentInventory, from) || (uint)index >= (uint)from.Capacity || !from.slots[index].Contains(item))) { return false; }
            var inventories = item.OwnInventories;
            int start = ReferenceEquals(current?.Owner, item) ? inventories.IndexOf((ItemInventory)current) + (cycle ? 1 : 0) : 0;
            ItemInventory wanted = null;
            for (int i = 0; i < inventories.Length; i++) {
                ItemInventory candidate = inventories[(start + i) % inventories.Length];
                if (ContainerAccess.CanOpen(owner, candidate)) { wanted = candidate; break; }
            }
            if (wanted == null) { return false; }
            if (ReferenceEquals(wanted, current)) { return true; }
            path.Clear();
            Inventory at = wanted;
            while (at is ItemInventory storage) {
                path.Add(storage);
                at = ((Item)storage.Owner).ParentInventory;
            }
            path.Reverse();
            ValidatePath(); dwell.Reset();
            InventoryTrace.Record("open", item, item.ParentInventory);
            return true;
        }


        private static void ChangePage(int delta) {
            if (pages <= 1) { return; }
            int next = (page + delta + pages) % pages;
            pageDirection = delta;
            page = next;
            deck?.DotBump.Impulse(PillKick);
            if (ReferenceEquals(Inventory.SelectedSlot?.ParentInventory, current)) { Inventory.selectedSlot = null; }
        }

        private static void ReleaseCurrent() {
            hitCells.Clear(); hitSlots = null;
            CancelPress();
            lease?.Dispose(); lease = null;
        }

        public static bool Contains(Vector2 mouse) => Active && Hit((int)mouse.X, (int)mouse.Y, 4);
        public static int Anchor => anchor;

        private static void CancelPress() { pressed = null; pressInventory = null; pressSlot = null; pressIndex = -1; }

        internal static bool SlotUpdating(Inventory inventory, VisualSlot slot, int index) {
            if (!Active) { return true; }
            bool controlled = ReferenceEquals(inventory, owner.Inventory) || Owns(inventory);
            if (!controlled) { return true; }
            Item item = inventory.GetItemAt(index);
            if (item != null && !ReferenceEquals(item.ParentInventory, inventory)) { slot.State = GUIComponent.ComponentState.None; return false; }
            bool inside = slot.MouseOn();
            if (Owns(inventory)) {
                Vector2 mouse = PlayerInput.MousePosition;
                inside = hitCells.TryGetValue(slot, out int local) && CellContains(local, mouse.X - layout.RingX, mouse.Y - layout.RingY, 0);
            } else {
                int wedge = InvUi.RingIndexOf(owner.Inventory, index);
                if (wedge >= 0) { inside = InvUi.PickAt(PlayerInput.MousePosition) == wedge; }
                if (Contains(PlayerInput.MousePosition)) { inside = false; }
            }
            if (!InputAllowed || !inside) { slot.State = GUIComponent.ComponentState.None; return false; }
            return !(PlayerInput.PrimaryMouseButtonClicked() && ClickOnPressed() && ReferenceEquals(pressSlot, slot));
        }

        internal static void SlotUpdated(Inventory inventory, VisualSlot slot, int index) {
            if (!InputAllowed || !PlayerInput.PrimaryMouseButtonDown() || Inventory.SelectedSlot?.Slot != slot) { return; }
            Item item = inventory.GetItemAt(index);
            if (!Manages(item)) { return; }
            pressed = item; pressInventory = inventory; pressSlot = slot; pressPoint = PlayerInput.MousePosition;
            pressIndex = index;
            InventoryTrace.Record("press", item, inventory);
        }

        private static bool ClickOnPressed() => pressed != null && !pressed.Removed &&
            ReferenceEquals(pressed.ParentInventory, pressInventory) && pressSlot != null && pressSlot.MouseOn() &&
            Vector2.DistanceSquared(PlayerInput.MousePosition, pressPoint) < DeckGesture.ClickTravel * DeckGesture.ClickTravel && Manages(pressed);

        public static bool ReleasePrefix() {
            if (!Active || !PlayerInput.PrimaryMouseButtonClicked()) { return true; }
            if (!InputAllowed) {
                Inventory.DraggingItems.Clear(); Inventory.DraggingSlot = null; CancelPress(); return false;
            }
            bool click = ClickOnPressed(); Item item = pressed; Inventory from = pressInventory; int index = pressIndex; CancelPress();
            if (click) {
                Inventory.DraggingItems.Clear(); Inventory.DraggingSlot = null;
                OpenAt(item, from, index, true); return false;
            }
            if (Inventory.DraggingItems.Count > 0 && Inventory.SelectedSlot == null &&
                (Contains(PlayerInput.MousePosition) || InvUi.OverRing(PlayerInput.MousePosition))) {
                InventoryTrace.Record("cancel-nontarget-drop", Inventory.DraggingItems[0], Inventory.DraggingItems[0].ParentInventory);
                Inventory.DraggingItems.Clear(); Inventory.DraggingSlot = null; return false;
            }
            for (int i = Inventory.DraggingItems.Count - 1; i >= 0; i--) {
                Item dragged = Inventory.DraggingItems[i];
                if (dragged.Removed || dragged.ParentInventory != null && !ContainerAccess.TryRoot(dragged.ParentInventory, out _)) { Inventory.DraggingItems.RemoveAt(i); }
            }
            return true;
        }

        public static bool UpdatePrefix(Inventory inventory, bool subInventory) => !Active || !Owns(inventory);
        public static bool DrawPrefix(Inventory inventory, bool subInventory) => !Active || !Owns(inventory);
        public static bool SubInventoryPrefix(Inventory inventory, int slotIndex) => !ManagedSlot(inventory, slotIndex);
        public static bool DrawSubInventoryPrefix(Inventory inventory, int slotIndex) => !ManagedSlot(inventory, slotIndex);

        private static bool ManagedSlot(Inventory inventory, int index) => Active &&
            inventory?.slots != null && (uint)index < (uint)inventory.slots.Length && Manages(inventory.GetItemAt(index));

        internal static void Dump() {
            ModLog.Message($"inventory radial deck: active={Active} input={InputAllowed} depth={path.Count} category={category} drag={Inventory.DraggingItems.Count}");
            ModLog.Message($"ringWedge={anchor} pivot=({layout.RingX},{layout.RingY}) capacity={layout.Capacity}");
            for (int i = 0; i < path.Count; i++) {
                ItemInventory inventory = path[i];
                ContainerAccess.TryRoot(inventory, out Entity root);
                ModLog.Message($"depth={i} item={inventory.Owner.ID} root={root?.ID} capacity={inventory.Capacity} accessible={ContainerAccess.CanOpen(owner, inventory)}");
                for (int slot = 0; slot < inventory.Capacity; slot++) {
                    foreach (Item item in inventory.GetItemsAt(slot)) {
                        ModLog.Message($"  slot={slot} item={item.ID} parentCorrect={ReferenceEquals(item.ParentInventory, inventory)} removed={item.Removed}");
                    }
                }
            }
            InventoryTrace.Dump();
        }


        public static void Draw(SpriteBatch spriteBatch) {
            if (!DeckPresence.Draws(phase) || shown == null || layout.Capacity <= 0 || deck?.Alive != true) { return; }
            float alpha = Scalar.Clamp01(deck.Reveal.Value);
            if (alpha <= 0.01f) { return; }
            var centre = new Vector2(layout.RingX, layout.RingY);
            DrawTabs(spriteBatch, centre, alpha);
            int cells = Math.Min(layout.Cells.Length, DeckMotion.Cells);
            for (int local = 0; local < cells; local++) { DrawCellBase(spriteBatch, centre, local, alpha); }
            for (int local = 0; local < cells; local++) { DrawCellItem(spriteBatch, centre, local, alpha); }
            if (pages > 1) { DrawPager(spriteBatch, centre, alpha); }
        }

        private static Vector2 Polar(Vector2 centre, float angle, float radius) =>
            new Vector2(centre.X + MathF.Cos(angle) * radius, centre.Y + MathF.Sin(angle) * radius);

        private static BandSlot Accent(in BandSlot band) =>
            new BandSlot(band.Outer - (band.Outer - band.Inner) * AccentShare, band.Outer, band.Angle, band.Half, band.Gap);

        private static void DrawTabs(SpriteBatch spriteBatch, Vector2 centre, float alpha) {
            int tabs = Math.Min(shownTabs, layout.Chips.Length);
            for (int i = 0; i < tabs; i++) {
                float bloom = Scalar.Clamp01(deck.TabBloom[i].Value), a = alpha * bloom;
                if (a < 0.01f) { continue; }
                BandSlot band = layout.Chips[i];
                float angle = deck.TabAngle[i].Value, scale = BloomStartScale + (1f - BloomStartScale) * bloom, hover = Scalar.Clamp01(deck.TabHover[i].Value);
                RingPaint.Sector(spriteBatch, centre, band, angle, scale, Color.Black * (FilledAlpha * a));
                if (hover > 0.01f) { RingPaint.Sector(spriteBatch, centre, band, angle, scale, Color.White * (0.14f * hover * a)); }
            }
            float on = Scalar.Clamp01(deck.PillOn.Value);
            if (on > 0.01f && tabs > 0) {
                // The pill takes the width of the active tab, the same tab the label below is lit for.
                BandSlot reference = layout.Chips[activeTab >= 0 && activeTab < tabs ? activeTab : 0];
                float pillAngle = deck.PillAngle.Value, bump = 1f + deck.PillBump.Value;
                var pill = new BandSlot(reference.Inner, reference.Outer, pillAngle, reference.Half, reference.Gap);
                RingPaint.Sector(spriteBatch, centre, pill, pillAngle, bump, Color.White * (0.2f * on * alpha));
                RingPaint.Sector(spriteBatch, centre, Accent(pill), pillAngle, bump, Color.White * (0.85f * on * alpha));
            }
            for (int i = 0; i < tabs; i++) {
                float bloom = Scalar.Clamp01(deck.TabBloom[i].Value), a = alpha * bloom;
                if (a < 0.01f) { continue; }
                BandSlot band = layout.Chips[i];
                float angle = deck.TabAngle[i].Value, hover = Scalar.Clamp01(deck.TabHover[i].Value);
                float lit = i == activeTab ? 1f : 0.74f + 0.26f * hover;
                DrawLabel(spriteBatch, chipShown[i], Polar(centre, angle, band.Mid * (BloomStartScale + (1f - BloomStartScale) * bloom)), Color.White * (lit * a), GUIStyle.SmallFont);
            }
        }

        private static void DrawCellBase(SpriteBatch spriteBatch, Vector2 centre, int local, float alpha) {
            Vec2 shift = deck.Shift[local].Value;
            centre += new Vector2(shift.X, shift.Y);
            BandSlot band = layout.Cells[local];
            float angle = deck.CellAngle[local].Value, scale = CellScale(local);
            float bloom = Scalar.Clamp01(deck.Bloom[local].Value), hover = Scalar.Clamp01(deck.Hover[local].Value);
            bool filled = cellItem[local] != null || ghostItem[local] != null;
            RingPaint.Sector(spriteBatch, centre, band, angle, scale, Color.Black * (EmptyAlpha * alpha));
            if (!filled) {
                if (Inventory.DraggingItems.Count > 0 && hover > 0.01f) {
                    RingPaint.Sector(spriteBatch, centre, band, angle, scale, GUIStyle.Green * (0.24f * hover * alpha));
                    RingPaint.Rim(spriteBatch, centre, band, angle, scale, GUIStyle.Green * (0.8f * hover * alpha));
                }
                return;
            }
            float a = alpha * bloom;
            if (a < 0.01f) { return; }
            RingPaint.Sector(spriteBatch, centre, band, angle, scale, Color.Black * (FilledAlpha * a));
            if (hover > 0.01f) {
                RingPaint.Sector(spriteBatch, centre, band, angle, scale, Color.White * (0.16f * hover * a));
                RingPaint.Rim(spriteBatch, centre, band, angle, scale, Color.White * (0.55f * hover * a));
            }
        }

        private static void DrawCellItem(SpriteBatch spriteBatch, Vector2 centre, int local, float alpha) {
            Item item = cellItem[local] ?? ghostItem[local];
            if (item == null) { return; }
            float bloom = Scalar.Clamp01(deck.Bloom[local].Value), a = alpha * bloom;
            if (Inventory.DraggingItems.Count > 0 && Inventory.DraggingItems.Contains(item)) { a *= 0.3f; }
            if (a < 0.01f) { return; }
            BandSlot band = layout.Cells[local];
            float angle = deck.CellAngle[local].Value, scale = CellScale(local), hover = deck.Hover[local].Value, press = deck.Press[local].Value;
            Vector2 at = CellCentre(local, angle, scale, deck.Shift[local].Value);
            float thickness = band.Thickness * scale;
            Sprite sprite = item.OverrideInventorySprite ?? item.Prefab.InventoryIcon ?? item.Sprite;
            if (sprite != null && sprite.size.X > 0 && sprite.size.Y > 0) {
                float fit = SpriteFit(thickness, bloom, hover, press);
                float spriteScale = MathF.Min(fit / sprite.size.X, fit / sprite.size.Y);
                Color tint = sprite == item.Sprite ? item.GetSpriteColor() : item.GetInventoryIconColor();
                if (shownLocked || !item.IsInteractable(Character.Controlled)) { tint *= 0.5f; }
                // The reveal is applied per sample inside CellSpritePath, so the path itself carries no extra opacity.
                float parentAlpha = 1f;
                if (Inventory.DraggingItems.Count > 0 && Inventory.DraggingItems.Contains(item)) { parentAlpha *= .3f; }
                var path = new CellSpritePath(deck, band, local, new Vector2(layout.RingX, layout.RingY), sprite, parentAlpha);
                Rectangle source = sprite.SourceRect; Vector2 origin = sprite.Origin;
                bool blurred = sprite.Texture != null && source.Width > 0 && source.Height > 0 && Rendering.MotionBlur.Draw(spriteBatch, sprite.Texture, source, tint, path,
                    new Vec2(origin.X / source.Width, origin.Y / source.Height), new Rendering.MotionBlurShadow(new Vec2(2, 2), .5f));
                if (!blurred) {
                    sprite.Draw(spriteBatch, at + Vector2.One * 2, Color.Black * (0.5f * a), 0, spriteScale);
                    sprite.Draw(spriteBatch, at, tint * a, 0, spriteScale);
                }
            }
            int bar = Math.Max(2, (int)(thickness * 0.05f)), barWidth = (int)(thickness * 0.6f);
            int barX = (int)at.X - barWidth / 2, barY = (int)(at.Y + thickness * 0.34f);
            if (!item.IsFullCondition && !item.Prefab.HideConditionBar && item.MaxCondition > 0.0f) {
                float state = Scalar.Clamp01(item.Condition / item.MaxCondition);
                GUI.DrawRectangle(spriteBatch, new Rectangle(barX, barY, barWidth, bar), Color.Black * (0.6f * a), true);
                GUI.DrawRectangle(spriteBatch, new Rectangle(barX, barY, (int)(barWidth * state), bar), ToolBox.GradientLerp(state, GUIStyle.ColorInventoryEmpty, GUIStyle.ColorInventoryHalf, GUIStyle.ColorInventoryFull) * (0.9f * a), true);
            }
            int count = cellCount[local];
            if (count > 1) {
                string text = count < stackLabels.Length ? stackLabels[count] : "x" + count.ToString(CultureInfo.InvariantCulture);
                DrawLabel(spriteBatch, text, at + new Vector2(thickness * 0.3f, -thickness * 0.3f), Color.White * a, GUIStyle.SmallFont);
            }
            if (item.OwnInventory?.Container is { DrawInventory: true }) {
                DrawLabel(spriteBatch, "›", at + new Vector2(-thickness * 0.34f, -thickness * 0.28f), Color.White * (0.7f * a), GUIStyle.SubHeadingFont);
            }
        }

        private static void DrawPager(SpriteBatch spriteBatch, Vector2 centre, float alpha) {
            Vector2 mouse = PlayerInput.MousePosition;
            DrawControl(spriteBatch, centre, layout.Previous, "‹", mouse, alpha);
            DrawControl(spriteBatch, centre, layout.Next, "›", mouse, alpha);
            if (layout.HasDots) {
                for (int i = 0; i < layout.DotAngles.Length; i++) {
                    RingPaint.Dot(spriteBatch, Polar(centre, layout.DotAngles[i], layout.DotRadius), layout.DotSize * 0.45f, Color.White * (0.35f * alpha));
                }
                float grow = 1f + deck.DotBump.Value;
                RingPaint.Dot(spriteBatch, Polar(centre, deck.DotAngle.Value, layout.DotRadius), layout.DotSize * 0.85f * grow, Color.White * (0.95f * alpha));
                return;
            }
            DrawLabel(spriteBatch, PagerText(), Polar(centre, layout.Next.Angle, layout.Next.Mid) + new Vector2(0, layout.Next.Thickness), Color.White * (0.8f * alpha), GUIStyle.SmallFont);
        }

        // "page/pages" is rebuilt only when either number changes, not every frame.
        private static string PagerText() {
            int key = page * 1024 + pages;
            if (key != pagerKey) {
                pagerKey = key;
                pagerText = (page + 1).ToString(CultureInfo.InvariantCulture) + "/" + pages.ToString(CultureInfo.InvariantCulture);
            }
            return pagerText;
        }

        private static void DrawControl(SpriteBatch spriteBatch, Vector2 centre, BandSlot band, string glyph, Vector2 mouse, float alpha) {
            bool over = band.Contains(mouse.X - layout.RingX, mouse.Y - layout.RingY, 0);
            RingPaint.Sector(spriteBatch, centre, band, band.Angle, 1f, Color.Black * (FilledAlpha * alpha));
            if (over) {
                RingPaint.Sector(spriteBatch, centre, band, band.Angle, 1f, Color.White * (0.16f * alpha));
                RingPaint.Rim(spriteBatch, centre, band, band.Angle, 1f, Color.White * (0.55f * alpha));
            }
            DrawLabel(spriteBatch, glyph, Polar(centre, band.Angle, band.Mid), Color.White * alpha, GUIStyle.SubHeadingFont);
        }

        private static void DrawLabel(SpriteBatch spriteBatch, string text, Vector2 at, Color color, GUIFont font) {
            Vector2 size = font.MeasureString(text);
            var corner = new Vector2(MathF.Round(at.X - size.X * 0.5f), MathF.Round(at.Y - size.Y * 0.5f));
            GUI.DrawString(spriteBatch, corner + Vector2.One, text, Color.Black * (0.7f * color.A / 255f), font: font);
            GUI.DrawString(spriteBatch, corner, text, color, font: font);
        }
    }
}
