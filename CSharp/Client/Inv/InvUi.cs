using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Barotrauma;
using Barotrauma.Items.Components;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ScpItdMod.Core;

namespace ScpItdMod.Inv {
    public static class InvUi {
        private const int Slots = InvRing.Slots;
        private const float IconPopFrom = 0.55f, IconExitScale = 0.8f, IconGhostCutoff = 0.01f;
        private const float PressedScale = 0.94f;
        private const float ShownEpsilon = 0.02f;
        private const float DimOpacity = 0.35f;
        // The live backdrop paints black in the in-game screenshot the user sent, so it stays off until the
        // world target is verified in game. Turning it on is a one-line change here.
        private const bool LiveBlur = false;

        private enum RingPhase : byte { Hidden, Shown }

        private static readonly Motion.MotionHandle<float>[] hover = new Motion.MotionHandle<float>[Slots];
        private static readonly Motion.MotionHandle<float>[] bloom = new Motion.MotionHandle<float>[Slots];
        private static readonly Motion.MotionHandle<float>[] press = new Motion.MotionHandle<float>[Slots];
        private static readonly Motion.MotionHandle<float>[] iconPop = new Motion.MotionHandle<float>[Slots];
        private static readonly Motion.MotionHandle<float>[] iconAlpha = new Motion.MotionHandle<float>[Slots];
        private static readonly RingContent[] ghosts = new RingContent[Slots];
        private static readonly int[] iconId = new int[Slots];
        private static readonly RingWedge[] wedges = new RingWedge[Slots];
        private static readonly RingContent[] contents = new RingContent[Slots];
        private static Motion.MotionHandle<float> captionFade, captionFocus;
        private static Motion.MotionScope motion;
        private static Motion.MotionStateMachine<RingPhase> phase;
        private static readonly int[] slotOf = new int[Slots];
        private static readonly Rectangle[] cells = new Rectangle[Slots];
        private static readonly string[] stackLabels = StackLabels();
        private static string[] StackLabels() {
            var labels = new string[Inventory.MaxPossibleStackSize + 1];
            for (int i = 0; i < labels.Length; i++) { labels[i] = "x" + i.ToString(System.Globalization.CultureInfo.InvariantCulture); }
            return labels;
        }
        private static sbyte[] ringOf = Array.Empty<sbyte>();
        private static CharacterInventory mapped;
        private static InvSlotType[] mappedTypes;
        private static int mappedCount;

        private static bool open;
        private static Character owner;
        private static Motion.MotionHandle<float> reveal;
        private static Motion.MotionHandle<float> pop;
        private static InvRing ring;
        private static Point screen;
        private static int picked = InvRing.None;

        private static Item captionItem;
        private static bool captionEquipped, captionNested, captionContainer;
        private static string captionName = "";
        private static string captionHint = "";
        private static string captionExtra = "";
        private static int captionWidth;

        private static bool vanillaDrawing;
        private static Rendering.LiveBackdrop backdrop = new Rendering.LiveBackdrop();

        internal static InvRing Geometry => ring;

        public static bool Active =>
            open && owner != null && ReferenceEquals(owner, Character.Controlled) && !owner.Removed && !owner.IsDead;

        // Drawn while open, and while the ring is still fading or its closing wedges are still retracting.
        private static bool Shown => open || reveal.Alive && (reveal.Value > ShownEpsilon || phase != null && phase.Running);

        public static void Attach(Harmony harmony) {
            Postfix(harmony, typeof(GameScreen), nameof(GameScreen.Update), nameof(SessionPostfix));

            Prefix(harmony, typeof(GameSession), nameof(GameSession.ToggleTabMenu), nameof(ToggleTabMenuPrefix));
            Prefix(harmony, typeof(GUI), nameof(GUI.TogglePauseMenu), nameof(TogglePauseMenuPrefix));
            Prefix(harmony, typeof(CharacterInventory), nameof(CharacterInventory.Update), nameof(UpdatePrefix));
            Postfix(harmony, typeof(CharacterInventory), nameof(CharacterInventory.Update), nameof(UpdatePostfix));
            Prefix(harmony, typeof(Inventory), "UpdateSlot", nameof(UpdateSlotPrefix));
            Prefix(harmony, typeof(VisualSlot), nameof(VisualSlot.MouseOn), nameof(SlotMouseOnPrefix));
            Prefix(harmony, typeof(CharacterInventory), "HandleButtonEquipStates", nameof(EquipInputPrefix));
            Prefix(harmony, typeof(CharacterInventory), nameof(CharacterInventory.QuickUseItem), nameof(QuickUsePrefix));
            Postfix(harmony, typeof(Inventory), "UpdateSlot", nameof(UpdateSlotPostfix));
            Postfix(harmony, typeof(Inventory), "DetermineMouseOnInventory", nameof(DetermineMouseOnInventoryPostfix));
            Postfix(harmony, typeof(Character), nameof(Character.ControlLocalPlayer), nameof(ControlPostfix));
            Prefix(harmony, typeof(Character), nameof(Character.DoInteractionUpdate), nameof(WorldInteractionPrefix));
            Postfix(harmony, typeof(CharacterInventory), nameof(CharacterInventory.HideSlot), nameof(HideSlotPostfix));
            Postfix(harmony, typeof(CharacterInventory), "SetSlotPositions", nameof(SlotPositionsPostfix));
            Prefix(harmony, typeof(CharacterInventory), nameof(CharacterInventory.CreateSlots), nameof(SlotsCreatingPrefix));
            Postfix(harmony, typeof(CharacterInventory), nameof(CharacterInventory.CreateSlots), nameof(SlotsCreatedPostfix));
            Postfix(harmony, typeof(CharacterInventory), nameof(CharacterInventory.AssignQuickUseNumKeys), nameof(NumKeysPostfix));
            Prefix(harmony, typeof(Inventory), nameof(Inventory.UpdateDragging), nameof(ReleasePrefix));
            Prefix(harmony, typeof(Inventory), nameof(Inventory.UpdateSubInventory), nameof(SubInventoryPrefix));
            Prefix(harmony, typeof(Inventory), nameof(Inventory.Update), nameof(InventoryUpdatePrefix));
            Prefix(harmony, typeof(Inventory), nameof(Inventory.Draw), nameof(InventoryDrawPrefix));
            Prefix(harmony, typeof(ItemInventory), nameof(ItemInventory.Draw), nameof(InventoryDrawPrefix));
            Prefix(harmony, typeof(Inventory), nameof(Inventory.DrawSubInventory), nameof(DrawSubInventoryPrefix));
            Prefix(harmony, typeof(CharacterInventory), "ShowSubInventory", nameof(ShowSubInventoryPrefix));
            Postfix(harmony, typeof(ItemContainer), nameof(ItemContainer.KeepOpenWhenEquippedBy), nameof(KeepOpenPostfix));
            Prefix(harmony, typeof(CharacterInventory), nameof(CharacterInventory.DrawOwn), nameof(DrawOwnPrefix));
            MethodInfo drawOwn = AccessTools.Method(typeof(CharacterInventory), nameof(CharacterInventory.DrawOwn));
            if (drawOwn != null) { harmony.Patch(drawOwn, finalizer: ModuleHost.Bind(typeof(InvUi), nameof(DrawOwnFinalizer))); }
            InventoryTrace.Attach();
        }

        public static void Reset() {
            open = false;
            owner = null;
            DisposeMotion();
            SubPanel.Clear();
            picked = InvRing.None;
            mapped = null;
            mappedTypes = null;
            mappedCount = 0;
            captionItem = null;
        }

        public static void Release() {
            Reset();
            backdrop.Dispose();
            backdrop = new Rendering.LiveBackdrop();
        }

        public static bool ToggleTabMenuPrefix(ref bool __result) {
            if (PlayView.Typing) {
                __result = false;
                return false;
            }
            if (GameSession.IsTabMenuOpen || !PlayerInput.KeyHit(InputType.InfoTab)) { return true; }
            Character character = Character.Controlled;
            if (open) {
                Close();
            } else {
                if (!Eligible(character)) { return true; }
                Rp.ActPanel.Hide();
                open = true;
                owner = character;
                SubPanel.Open(character);
            }
            __result = true;
            return false;
        }

        public static bool TogglePauseMenuPrefix() {
            if (!open) { return true; }
            bool consumed = Active;
            Close();
            return !consumed;
        }

        public static void UpdatePrefix(CharacterInventory __instance, float deltaTime, Camera cam) {
            if (!Controls(__instance)) { return; }
            EquipmentLayout.Apply(__instance);
            Sync(__instance);
            SubPanel.BeforeNative();
            RefreshInputSurface();
            if (Active) {
                if (cam != null) { cam.Freeze = true; }
            }
        }

        public static void UpdatePostfix(CharacterInventory __instance, float deltaTime, Camera cam) {
            if (Active && ReferenceEquals(__instance, owner.Inventory)) { SubPanel.Update(deltaTime, cam, ring); }
        }

        public static bool UpdateSlotPrefix(Inventory __instance, VisualSlot slot, int slotIndex) => SubPanel.SlotUpdating(__instance, slot, slotIndex);
        public static bool SlotMouseOnPrefix(VisualSlot __instance, ref bool __result) => SubPanel.MouseOnPrefix(__instance, ref __result);
        public static bool EquipInputPrefix(CharacterInventory __instance) => !Active || !ReferenceEquals(__instance, owner.Inventory) || SubPanel.InputAllowed;
        public static bool QuickUsePrefix(CharacterInventory __instance, Item item) {
            if (!Active || !ReferenceEquals(__instance, owner.Inventory)) { return true; }
            return SubPanel.InputAllowed && item != null && !item.Removed &&
                (item.ParentInventory == null || ContainerAccess.TryRoot(item.ParentInventory, out _)) && owner.CanInteractWith(item);
        }

        public static void UpdateSlotPostfix(Inventory __instance, VisualSlot slot, int slotIndex) {
            SubPanel.SlotUpdated(__instance, slot, slotIndex);
            if (!Active || slot == null || slot.State != GUIComponent.ComponentState.Hover ||
                Inventory.DraggingItems.Count != 0 || PlayView.Typing) { return; }
            if (!PlayerInput.KeyHit(Rp.Targeting.Key)) { return; }
            Item chosen = __instance.GetItemAt(slotIndex);
            if (chosen != null) { Rp.Targeting.Begin(chosen); }
        }

        public static bool ReleasePrefix() => SubPanel.ReleasePrefix();

        internal static int RingIndexOf(CharacterInventory inventory, int slotIndex) =>
            ReferenceEquals(inventory, mapped) && (uint)slotIndex < (uint)ringOf.Length ? ringOf[slotIndex] : -1;

        internal static int PickAt(Vector2 mouse) => ring.Cell <= 0 ? InvRing.None : ring.Pick(ring.X + (mouse.X - ring.X) / Spread(), ring.Y + (mouse.Y - ring.Y) / Spread());

        // The inventory slot under the pointer on the ring, or -1.
        internal static int SlotAt(Vector2 mouse) {
            int wedge = PickAt(mouse);
            return (uint)wedge < (uint)mappedCount ? slotOf[wedge] : -1;
        }

        internal static bool OverRing(Vector2 mouse) => Active && ring.Cell > 0 && ring.Covers(mouse.X, mouse.Y);

        public static bool InventoryUpdatePrefix(Inventory __instance, bool subInventory) => SubPanel.UpdatePrefix(__instance, subInventory);
        public static bool InventoryDrawPrefix(Inventory __instance, bool subInventory) => SubPanel.DrawPrefix(__instance, subInventory);
        public static bool SubInventoryPrefix(Inventory __instance, int slotIndex) => SubPanel.SubInventoryPrefix(__instance, slotIndex);
        public static bool DrawSubInventoryPrefix(Inventory __instance, int slotIndex) => SubPanel.DrawSubInventoryPrefix(__instance, slotIndex);
        public static bool ShowSubInventoryPrefix(Inventory.SlotReference slotRef) => !Active || !SubPanel.Manages(slotRef.Item);
        public static void KeepOpenPostfix(ItemContainer __instance, ref bool __result) {
            if (Active && SubPanel.Manages(__instance.Item)) { __result = false; }
        }

        internal static void RefreshInputSurface() => Inventory.RefreshMouseOnInventory();

        public static void DetermineMouseOnInventoryPostfix(ref bool __result) {
            if (!__result && SubPanel.InputAllowed && (OverRing(PlayerInput.MousePosition) || SubPanel.Contains(PlayerInput.MousePosition))) { __result = true; }
        }

        public static void ControlPostfix(Character __instance) {
            if (!Active || !ReferenceEquals(__instance, owner)) { return; }
            __instance.ClearInput(InputType.Aim);
            __instance.ClearInput(InputType.Use);
            __instance.ClearInput(InputType.Shoot);
            __instance.ClearInput(InputType.Select);
        }

        public static bool WorldInteractionPrefix(Character __instance) => !Active || !ReferenceEquals(__instance, owner);

        public static void HideSlotPostfix(CharacterInventory __instance, int i, ref bool __result) {
            if (__instance.CurrentLayout != CharacterInventory.Layout.Default || !ReferenceEquals(__instance, mapped) || (uint)i >= (uint)ringOf.Length ||
                !ReferenceEquals(__instance, Character.Controlled?.Inventory)) {
                return;
            }
            if (ringOf[i] >= 0) {
                if (!Shown || vanillaDrawing) { __result = true; }
                return;
            }
            if (__instance.SlotTypes[i] == InvSlotType.Any && __instance.GetItemAt(i) == null) { __result = true; return; }
            if (!__result || Shown) { return; }
            InvSlotType type = __instance.SlotTypes[i];
            if (type != InvSlotType.LeftHand && type != InvSlotType.RightHand) { return; }
            Item item = __instance.GetItemAt(i);
            if (item == null || __instance.visualSlots[i].Disabled) { return; }
            if (type == InvSlotType.RightHand && __instance.IsInLimbSlot(item, InvSlotType.LeftHand)) { return; }
            __result = false;
        }

        public static void SlotPositionsPostfix(CharacterInventory __instance) => EquipmentLayout.Apply(__instance);

        public static void SlotsCreatingPrefix(CharacterInventory __instance, ref int __state) {
            __state = -1;
            if (!ReferenceEquals(__instance, Character.Controlled?.Inventory) || Inventory.DraggingSlot == null || __instance.visualSlots == null) { return; }
            VisualSlot[] slots = __instance.visualSlots;
            for (int i = 0; i < slots.Length; i++) {
                if (ReferenceEquals(slots[i], Inventory.DraggingSlot)) { __state = i; return; }
            }
        }

        public static void SlotsCreatedPostfix(CharacterInventory __instance, int __state) {
            EquipmentLayout.Apply(__instance);
            if (!ReferenceEquals(__instance, Character.Controlled?.Inventory) || __instance.visualSlots == null) { return; }
            VisualSlot[] slots = __instance.visualSlots;
            if ((uint)__state < (uint)slots.Length) { Inventory.DraggingSlot = slots[__state]; }
            foreach (Inventory.SlotReference reference in Inventory.highlightedSubInventorySlots) {
                if (ReferenceEquals(reference.ParentInventory, __instance) && (uint)reference.SlotIndex < (uint)slots.Length) {
                    reference.Slot = slots[reference.SlotIndex];
                }
            }
        }

        public static void NumKeysPostfix(CharacterInventory __instance) {
            if (!Controls(__instance) || __instance.visualSlots == null) { return; }
            for (int k = 0; k < mappedCount; k++) {
                VisualSlot slot = __instance.visualSlots[slotOf[k]];
                if (slot != null) { slot.InventoryKeyIndex = k; }
            }
        }

        public static void DrawOwnPrefix(CharacterInventory __instance, ref bool __state) {
            __state = vanillaDrawing;
            if (!Controls(__instance)) { return; }
            EquipmentLayout.Apply(__instance);
            Sync(__instance);
            vanillaDrawing = true;
        }

        public static void DrawOwnFinalizer(CharacterInventory __instance, SpriteBatch spriteBatch, bool __state, Exception __exception) {
            vanillaDrawing = __state;
            if (__exception != null || !Controls(__instance)) { return; }
            Sync(__instance);
            if (Shown) {
                float fade = Fade();
                if (LiveBlur && fade > 0.01f) {
                    backdrop.Build(spriteBatch);
                    backdrop.Draw(spriteBatch, fade);
                }
                DrawDim(spriteBatch);
                DrawRing(spriteBatch, __instance);
            }
            SubPanel.Draw(spriteBatch);
        }

        // The world behind the ring darkens with the same reveal timeline that opens the ring, so it never runs ahead of it.
        private static void DrawDim(SpriteBatch spriteBatch) {
            float a = Fade();
            if (a < 0.01f) { return; }
            GUI.DrawRectangle(spriteBatch, new Rectangle(0, 0, GameMain.GraphicsWidth, GameMain.GraphicsHeight), Color.Black * (DimOpacity * a), true);
        }

        private static bool Controls(CharacterInventory inventory) {
            Character controlled = Character.Controlled;
            if (controlled == null || !ReferenceEquals(controlled.Inventory, inventory) || !controlled.IsHuman ||
                inventory.CurrentLayout != CharacterInventory.Layout.Default) { return false; }
            if (!ReferenceEquals(inventory, mapped) || !ReferenceEquals(inventory.SlotTypes, mappedTypes)) { Map(inventory); }
            return mappedCount > 0;
        }

        private static bool Eligible(Character character) =>
            character != null && character.IsHuman && !character.IsDead && !character.Removed &&
            !character.IsIncapacitated && character.Stun <= 0 && character.Inventory != null && Controls(character.Inventory) &&
            !GUI.DisableHUD && !CrewManager.IsCommandInterfaceOpen &&
            CharacterHealth.OpenHealthWindow == null && character.SelectedCharacter == null;

        private static void Map(CharacterInventory inventory) {
            mapped = inventory;
            mappedTypes = inventory.SlotTypes;
            mappedCount = 0;
            int capacity = mappedTypes?.Length ?? 0;
            if (ringOf.Length != capacity) { ringOf = new sbyte[capacity]; }
            for (int i = 0; i < capacity; i++) {
                ringOf[i] = -1;
                if (mappedTypes[i] != InvSlotType.Any || mappedCount >= Slots) { continue; }
                ringOf[i] = (sbyte)mappedCount;
                slotOf[mappedCount++] = i;
            }
            if (inventory.visualSlots != null) {
                NumKeysPostfix(inventory);
            }
        }

        public static void SessionPostfix() {
            if (open && (!ReferenceEquals(owner, Character.Controlled) || !Eligible(owner) || GameSession.IsTabMenuOpen || Screen.Selected != GameMain.GameScreen)) { Close(); }
            SubPanel.Tick();
        }

        public static void Close() {
            open = false;
            SubPanel.Retire();
            picked = InvRing.None;
        }

        private static void Sync(CharacterInventory inventory) {
            if (open && (!ReferenceEquals(owner, Character.Controlled) || !Eligible(owner) || GameSession.IsTabMenuOpen)) {
                Close();
            }
            if (!Shown || inventory.visualSlots == null) { return; }

            Point size = new Point(GameMain.GraphicsWidth, GameMain.GraphicsHeight);
            DeviceSpan(out float left, out float right);
            float centreX = InvRing.PlaceX(size.X, size.Y, left, right);
            if (size != screen || centreX != ring.X) {
                screen = size;
                ring = new InvRing(centreX, size.Y * 0.5f, size.Y);
                int side = (int)ring.Cell;
                for (int k = 0; k < Slots; k++) {
                    cells[k] = new Rectangle((int)(ring.CellX(k) - side * 0.5f), (int)(ring.CellY(k) - side * 0.5f), side, side);
                }
            }

            Animate(inventory);
            Vector2 mouse = PlayerInput.MousePosition;
            picked = open && !OverSubInventory(mouse) ? ring.Pick(ring.X + (mouse.X - ring.X) / Spread(), ring.Y + (mouse.Y - ring.Y) / Spread()) : InvRing.None;
            Rectangle cursor = new Rectangle((int)mouse.X - 1, (int)mouse.Y - 1, 3, 3);
            VisualSlot[] visuals = inventory.visualSlots;
            for (int k = 0; k < mappedCount; k++) {
                VisualSlot slot = visuals[slotOf[k]];
                if (slot == null) { continue; }
                slot.DrawOffset = Vector2.Zero;
                slot.SubInventoryDir = Math.Sign(screen.Y / 2 - cells[k].Center.Y);
                slot.IsMoving = true;
                if (!open) {
                    slot.InteractRect = new Rectangle(cells[k].Center, Point.Zero);
                } else {
                    slot.InteractRect = k == picked ? Rectangle.Union(slot.Rect, cursor) : Rectangle.Empty;
                }
            }
        }

        private static void DisposeMotion() {
            phase?.Dispose(); phase = null;
            motion?.Dispose(); motion = null;
            backdrop.Reset();
        }

        // Open and close are two timelines of one state machine. The ring springs reveal and pop together; each wedge
        // blooms on its own spring, in order when opening and in reverse order when closing, so the retract is visible.
        private static void DefineRing() {
            var shownPlan = new Motion.MotionSequenceBuilder()
                .To(reveal, 1f, Motion.MotionPresets.Window)
                .To(pop, 1f, Motion.MotionPresets.Pop);
            var hiddenPlan = new Motion.MotionSequenceBuilder()
                .To(reveal, 0f, Motion.MotionPresets.Window)
                .To(pop, 0f, Motion.MotionPresets.Window);
            for (int k = 0; k < Slots; k++) {
                shownPlan.To(bloom[k], 1f, Motion.MotionPresets.Card, RingWedge.OpenDelay(k));
                hiddenPlan.To(bloom[k], 0f, Motion.MotionPresets.Card, RingWedge.CloseDelay(k));
            }
            phase.Define(RingPhase.Shown, shownPlan.Compile());
            phase.Define(RingPhase.Hidden, hiddenPlan.Compile());
        }

        private static void Animate(CharacterInventory inventory) {
            if (!open && motion?.Alive != true) { return; }
            if (motion?.Alive != true) {
                if (open && motion != null) { InventoryTrace.Record("ring-restart", null, null); }
                DisposeMotion();
                motion = Motion.MotionClient.Runtime.Scope();
                reveal = motion.Scalar(); pop = motion.Scalar();
                captionFade = motion.Scalar(1); captionFocus = motion.Scalar();
                for (int i = 0; i < Slots; i++) {
                    hover[i] = motion.Scalar(); bloom[i] = motion.Scalar(); press[i] = motion.Scalar(1); iconPop[i] = motion.Scalar(1); iconAlpha[i] = motion.Scalar(1);
                }
                Array.Clear(iconId, 0, iconId.Length);
                Array.Clear(ghosts, 0, ghosts.Length);
                phase = new Motion.MotionStateMachine<RingPhase>(motion);
                DefineRing();
            }
            phase.Enter(open ? RingPhase.Shown : RingPhase.Hidden);
            if (!open && !phase.Running && reveal.Value < ShownEpsilon) {
                DisposeMotion(); return;
            }
            VisualSlot[] visuals = inventory.visualSlots;
            if (visuals == null) { return; }
            float spread = Spread();
            bool held = Inventory.DraggingItems.Count == 0 && PlayerInput.PrimaryMouseButtonHeld();
            int anchor = SubPanel.IsOpen ? SubPanel.Anchor : InvRing.None;
            for (int k = 0; k < mappedCount; k++) {
                hover[k].To(k == picked || k == anchor ? 1 : 0, Motion.MotionPresets.Hover);
                press[k].To(k == picked && held ? PressedScale : 1f, Motion.MotionPresets.Press);
                wedges[k] = RingWedge.Place(ring, k, spread, bloom[k].Value, hover[k].Value, press[k].Value);
                VisualSlot slot = visuals[slotOf[k]];
                if (slot == null) { continue; }
                Item present = inventory.GetItemAt(slotOf[k]);
                int id = present == null ? 0 : (present.ID != 0 ? present.ID : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(present) | 1);
                if (id != iconId[k]) {
                    iconId[k] = id;
                    if (id != 0) {
                        iconPop[k].Snap(IconPopFrom); iconPop[k].To(1f, Motion.MotionPresets.Pop);
                        iconAlpha[k].Snap(0f); iconAlpha[k].To(1f, Motion.MotionPresets.Fade);
                    } else {
                        iconPop[k].To(IconExitScale, Motion.MotionPresets.Window);
                        iconAlpha[k].To(0f, Motion.MotionPresets.Fade);
                    }
                }
                slot.Rect = new Rectangle(wedges[k].X, wedges[k].Y, wedges[k].Side, wedges[k].Side);
            }
        }

        // Gathers what each wedge shows this frame. The renderer draws only these values.
        private static void DrawRing(SpriteBatch spriteBatch, CharacterInventory inventory) {
            Character character = Character.Controlled;
            int dragging = Inventory.DraggingItems.Count;
            for (int k = 0; k < mappedCount; k++) {
                int index = slotOf[k];
                Item item = inventory.GetItemAt(index);
                var content = new RingContent { Fill = -1f, Condition = -1f, IconPop = iconPop[k].Alive ? iconPop[k].Value : 1f, IconAlpha = iconAlpha[k].Alive ? iconAlpha[k].Value : 1f };
                if (item != null) {
                    IReadOnlyList<Item> items = inventory.slots[index].Items;
                    int count = items.Count, dragged = 0;
                    if (dragging != 0) { for (int i = 0; i < count; i++) { if (Inventory.DraggingItems.Contains(items[i])) { dragged++; } } }
                    bool mouseOn = k == picked;
                    content.Hidden = dragged == count && !mouseOn;
                    content.Stack = mouseOn ? count : count - dragged;
                    Sprite sprite = item.OverrideInventorySprite ?? item.Prefab.InventoryIcon ?? item.Sprite;
                    content.Sprite = sprite;
                    content.Tint = sprite == item.Sprite ? item.GetSpriteColor() : item.GetInventoryIconColor();
                    if (inventory.Locked || !item.IsInteractable(character)) { content.Tint *= 0.5f; }
                    var container = item.GetComponent<ItemContainer>();
                    if (container != null && container.ShowContainedStateIndicator && container.Capacity > 0) {
                        float state = container.GetContainedIndicatorState();
                        if (state >= 0f) { content.Fill = state; }
                    }
                    if (!item.IsFullCondition && !item.Prefab.HideConditionBar && item.MaxCondition > 0f) { content.Condition = item.Condition / item.MaxCondition; }
                    content.Held = character != null && character.HasEquippedItem(item);
                    content.DropTarget = mouseOn && dragging > 0 && Accepts(inventory, index, Inventory.DraggingItems[0]);
                }
                if (item != null) { ghosts[k] = content; }
                else if (ghosts[k].Sprite != null) {
                    if (content.IconAlpha <= IconGhostCutoff) { ghosts[k] = default; }
                    else {
                        content = ghosts[k];
                        content.IconPop = iconPop[k].Alive ? iconPop[k].Value : content.IconPop;
                        content.IconAlpha = iconAlpha[k].Alive ? iconAlpha[k].Value : 0f;
                        content.Hidden = false; content.Held = false; content.DropTarget = false;
                    }
                }
                contents[k] = content;
            }
            RingRenderer.Draw(spriteBatch, ring, wedges.AsSpan(0, mappedCount), contents.AsSpan(0, mappedCount),
                picked, Fade(), Spread(), BuildCaption(inventory, ring.HubRadius * Spread()));
        }

        private static bool Accepts(CharacterInventory inventory, int index, Item dragged) {
            if (dragged == null) { return false; }
            if (inventory.CanBePutInSlot(dragged, index)) { return true; }
            Item occupant = inventory.GetItemAt(index);
            return occupant?.OwnInventory != null && occupant.OwnInventory.CanBePut(dragged) &&
                   occupant.OwnInventory.Container.AllowDragAndDrop && occupant.OwnInventory.Container.DrawInventory;
        }

        internal static void DeviceSpan(out float left, out float right) {
            left = float.MaxValue;
            right = float.MinValue;
            Character character = Character.Controlled;
            if (character == null) { return; }
            Widen(character.SelectedItem, ref left, ref right);
            Widen(character.SelectedSecondaryItem, ref left, ref right);
        }

        private static void Widen(Item item, ref float left, ref float right) {
            if (item == null) { return; }
            List<ItemComponent> huds = item.activeHUDs;
            for (int i = 0; i < huds.Count; i++) {
                GUIFrame frame = huds[i]?.GuiFrame;
                if (frame == null || !frame.Visible) { continue; }
                Rectangle rect = frame.Rect;
                if (!frame.CanBeFocused && rect.Width >= GameMain.GraphicsWidth && rect.Height >= GameMain.GraphicsHeight) { continue; }
                if (rect.Width <= 0) { continue; }
                if (rect.Left < left) { left = rect.Left; }
                if (rect.Right > right) { right = rect.Right; }
            }
        }

        private static bool OverSubInventory(Vector2 mouse) {
            if (SubPanel.Contains(mouse)) { return true; }
            if (Inventory.SelectedSlot?.IsSubSlot == true && Inventory.SelectedSlot.Slot != null && Inventory.SelectedSlot.Slot.InteractRect.Contains(mouse)) { return true; }
            foreach (Inventory.SlotReference reference in Inventory.highlightedSubInventorySlots) {
                Inventory sub = reference.Inventory;
                if (sub != null && sub.BackgroundFrame.Contains(mouse)) { return true; }
            }
            return false;
        }

        // The hub caption's text and focus. Text changes only with the item; focus eases on its own handle.
        private static RingCaption BuildCaption(CharacterInventory inventory, float hub) {
            Character character = Character.Controlled;
            Item nested = SubPanel.FocusItem;
            Inventory.SlotReference selected = Inventory.SelectedSlot;
            Item equipment = SubPanel.InputAllowed && ReferenceEquals(selected?.ParentInventory, inventory) &&
                selected.Slot.MouseOn() ? selected.Item : null;
            Item item = nested ?? equipment ?? (picked >= 0 && picked < mappedCount ? inventory.GetItemAt(slotOf[picked]) : null) ?? SubPanel.ViewedItem;
            bool focused = item != null;
            if (item == null && character != null) {
                item = character.Inventory.GetItemInLimbSlot(InvSlotType.RightHand) ?? character.Inventory.GetItemInLimbSlot(InvSlotType.LeftHand);
            }
            if (item == null) { return default; }
            bool equipped = nested == null && character != null && character.HasEquippedItem(item);
            bool container = SubPanel.CanOpen(item);
            int width = (int)(hub * 1.7f);
            if (!ReferenceEquals(item, captionItem) || equipped != captionEquipped || (nested != null) != captionNested || container != captionContainer || width != captionWidth) {
                captionItem = item;
                captionEquipped = equipped;
                captionNested = nested != null;
                captionContainer = container;
                captionWidth = width;
                if (captionFade.Alive) { captionFade.Snap(0f); captionFade.To(1f, Motion.MotionPresets.Fade); }
                captionName = ToolBox.LimitString(item.Name, GUIStyle.SubHeadingFont, width).Value;
                string hint = container ? "ЛКМ · открыть" : nested != null ? "2× ЛКМ · применить" : Hint(inventory, item).Value;
                captionExtra = container || nested != null ? ToolBox.LimitString("Тянуть · перенести", GUIStyle.SmallFont, width).Value : "";
                captionHint = ToolBox.LimitString(hint, GUIStyle.SmallFont, width).Value;
            }
            captionFocus.To(focused ? 1f : 0f, Motion.MotionPresets.Hover);
            return new RingCaption {
                Name = captionName, Hint = captionHint, Extra = captionExtra,
                Focus = captionFocus.Alive ? Scalar.Clamp01(captionFocus.Value) : (focused ? 1f : 0f),
                Swap = captionFade.Alive ? Scalar.Clamp01(captionFade.Value) : 1f,
                Alpha = 1f
            };
        }

        private static LocalizedString Hint(CharacterInventory inventory, Item item) {
            var action = inventory.GetQuickUseAction(item, allowEquip: true, allowInventorySwap: false, allowApplyTreatment: true);
            LocalizedString drop = TextManager.Get("DropItem");
            LocalizedString person = Rp.RpWorld.IsCuffs(item) ? "[" + Rp.Targeting.KeyName + "] сковать человека" : "[" + Rp.Targeting.KeyName + "] человеку";
            if (action == CharacterInventory.QuickUseAction.None || action == CharacterInventory.QuickUseAction.Drop) {
                return drop + "    " + person;
            }
            Character character = Character.Controlled;
            LocalizedString use = TextManager.GetWithVariable("QuickUseAction." + action, "[equippeditem]",
                character?.HeldItems.FirstOrDefault()?.Name ?? item.Name);
            return use + "    " + drop + "    " + person;
        }


        private static float Fade() => reveal.Alive ? Scalar.Clamp01(reveal.Value) : 0;

        private static float Spread() => 0.8f + 0.2f * (pop.Alive ? pop.Value : 0);


        private static void Prefix(Harmony harmony, Type type, string name, string patch) => Patch(harmony, type, name, patch, true);

        private static void Postfix(Harmony harmony, Type type, string name, string patch) => Patch(harmony, type, name, patch, false);

        private static void Patch(Harmony harmony, Type type, string name, string patch, bool prefix) {
            MethodInfo target = AccessTools.Method(type, name);
            if (target == null) {
                ModLog.Error($"inventory hook is missing: {type.Name}.{name}");
                return;
            }
            HarmonyMethod method = ModuleHost.Bind(typeof(InvUi), patch);
            if (prefix) {
                harmony.Patch(target, prefix: method);
            } else {
                harmony.Patch(target, postfix: method);
            }
        }
    }
}
