using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Networking;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ScpItdMod.Core;
using ScpItdMod.Net;
using NVector2 = ScpItdMod.Core.Vec2;

namespace ScpItdMod.Pool {
    public static class PoolClient {
        private sealed class View {
            public Item Item;
            public PoolReplica Replica;
            public Match Local;
            public double Next;
            public uint Sent;
            public CueState Cue;
            public double CueAt, Seen;
            public readonly PoolSphereRenderer Spheres = new PoolSphereRenderer();
        }
        private static readonly Dictionary<ushort, View> views = new Dictionary<ushort, View>();
        private static readonly List<ushort> removed = new List<ushort>();
        private static readonly WireWriter writer = new WireWriter(PoolWire.MaxReply), retry = new WireWriter(PoolWire.MaxRequest);
        private static readonly string[] pocketLabels = { "1", "2", "3", "4", "5", "6" };
        private static View current;
        private static Motion.MotionScope statusScope;
        private static Motion.MotionHandle<float> statusOpacity;
        private static int shownTurn = -1;
        private static PoolPhase shownPhase;
        private static GUIFrame frame;
        private static GUIMessageBox confirmation;
        private static GUICustomComponent surface, spin;
        private static GUITextBlock message, players, call, strength;
        private static GUIButton join, leave, free, eight, strike, safety, place, stalemate;
        private static readonly GUIButton[] choices = new GUIButton[3];
        private static GUIScrollBar power;
        private static NVector2 direction = NVector2.UnitX, tip;
        private static int ball = -1, pocket = -1, shownPower = -1;
        private static bool placing, safe;
        private static bool pulling;
        private static NVector2 pullAt;
        private static float pullStart;
        private static double nextAim;
        private static uint token, pending;
        private static PoolError error;
        private static double retryAt, deadline, pollAt;
        private static uint shownRevision;
        private static byte shownOnline;

        public static void Attach(Harmony harmony) {
            ModNet.ReceiveSpan(PoolWire.Channel, PoolWire.MaxReply, Receive);
            harmony.Patch(AccessTools.Method(typeof(GameMain), "Draw", new[] { typeof(GameTime) }), prefix: new HarmonyMethod(typeof(PoolClient), nameof(Prepare)) { priority = Priority.High });
            harmony.Patch(AccessTools.Method(typeof(Item), nameof(Item.Draw), new[] { typeof(SpriteBatch), typeof(bool), typeof(bool), typeof(Color?), typeof(float?) }), postfix: ModuleHost.Bind(typeof(PoolClient), nameof(DrawItemPostfix)));
            harmony.Patch(AccessTools.Method(typeof(GameScreen), nameof(GameScreen.Update), new[] { typeof(double) }), postfix: ModuleHost.Bind(typeof(PoolClient), nameof(Tick)));
            harmony.Patch(AccessTools.Method(typeof(GameScreen), nameof(GameScreen.AddToGUIUpdateList), Type.EmptyTypes), postfix: ModuleHost.Bind(typeof(PoolClient), nameof(GuiPostfix)));
            harmony.Patch(AccessTools.Method(typeof(Character), nameof(Character.ControlLocalPlayer), new[] { typeof(float), typeof(Camera), typeof(bool) }), prefix: ModuleHost.Bind(typeof(PoolClient), nameof(ControlPrefix)), postfix: ModuleHost.Bind(typeof(PoolClient), nameof(ControlPostfix)));
            harmony.Patch(AccessTools.Method(typeof(GUI), nameof(GUI.TogglePauseMenu), Type.EmptyTypes), prefix: ModuleHost.Bind(typeof(PoolClient), nameof(EscapePrefix)));
        }
        public static void Clear() { Close(false); foreach (View view in views.Values) { view.Spheres.Dispose(); } views.Clear(); removed.Clear(); pending = 0; pollAt = 0; }
        public static void Release() { Clear(); PoolDraw.Clear(); PoolSphereRenderer.ReleaseShared(); }
        private static View Find(Item item) {
            if (views.TryGetValue(item.ID, out View view) && ReferenceEquals(view.Item, item)) { return view; }
            view?.Spheres.Dispose();
            view = new View { Item = item, Replica = new PoolReplica(PoolModule.Layout) };
            if (GameMain.Client == null) { view.Local = new Match(PoolModule.Layout); LocalSnapshot(view); }
            views[item.ID] = view; return view;
        }
        private static void LocalSnapshot(View view) {
            PoolWire.Snapshot(writer, view.Item.ID, 1, view.Local, view.Local.Seat(1), 1, Character.Controlled?.Name ?? "Игрок", "", ushort.MaxValue, true);
            view.Replica.Apply(writer.Span, Timing.TotalTime, out _); view.Sent = view.Local.Revision;
        }
        private static void Receive(ReadOnlySpan<byte> bytes) {
            if (bytes.Length > 0 && bytes[0] == 2) {
                if (PoolWire.ReadAim(bytes, out CueState cue) && views.TryGetValue(cue.Table, out View shown) && shown.Replica.Ready &&
                    cue.Epoch == shown.Replica.Meta.Epoch && cue.Revision == shown.Replica.Meta.Revision &&
                    (shown.Cue.Revision != cue.Revision || (int)(cue.Sequence - shown.Cue.Sequence) > 0)) { shown.Cue = cue; shown.CueAt = Timing.TotalTime; }
                return;
            }
            if (bytes.Length < 3) { return; } ushort id = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(1));
            if (!(Entity.FindEntityByID(id) is Item item) || item.Removed || !item.HasTag(PoolModule.Tag)) { return; }
            View view = Find(item); PoolMeta previous = view.Replica.Meta; bool applied = view.Replica.Apply(bytes, Timing.TotalTime, out PoolMeta meta);
            if (applied && ReferenceEquals(view, current) && (previous.Name0 != meta.Name0 || previous.Name1 != meta.Name1)) { shownRevision = 0; }
            if (applied && meta.Ack != 0 && meta.Ack == pending) { pending = 0; error = meta.Error; shownRevision = 0; }
        }
        public static void ControlPrefix(Character __instance) { if (current != null && ReferenceEquals(__instance, Character.Controlled)) { Character.DisableControls = true; } }
        public static void ControlPostfix(Character __instance) {
            if (!ReferenceEquals(__instance, Character.Controlled)) { return; }
            Item item = __instance.SelectedItem;
            bool wants = item != null && item.HasTag(PoolModule.Tag) && PoolModule.CanUse(__instance, item);
            if (wants && !ReferenceEquals(current?.Item, item)) { Open(Find(item)); }
            else if (!wants && current != null) { Close(false); }
            if (current != null) { __instance.ClearInput(InputType.Use); __instance.ClearInput(InputType.Shoot); __instance.ClearInput(InputType.Aim); }
        }
        public static bool EscapePrefix() { if (current == null) { return true; } Close(true); return false; }
        public static void GuiPostfix() { frame?.AddToGUIUpdateList(order: 3); }
        private static void Close(bool deselect) {
            Item was = current?.Item; current = null;
            statusScope?.Dispose(); statusScope = null;
            confirmation?.Close(); confirmation = null;
            pending = 0; error = PoolError.Ok;
            pulling = false;
            frame?.RectTransform.Parent?.GUIComponent?.RemoveChild(frame); frame = null; surface = spin = null;
            message = players = call = strength = null; join = leave = free = eight = strike = safety = place = stalemate = null; power = null; Array.Clear(choices);
            if (deselect && Character.Controlled != null && ReferenceEquals(Character.Controlled.SelectedItem, was)) { Character.Controlled.EmulateInput(InputType.Deselect); Character.Controlled.SelectedItem = null; }
        }
        private static RectTransform Rect(GUIComponent parent, int x, int y, int w, int h) => new RectTransform(new Point(w, h), parent.RectTransform) { AbsoluteOffset = new Point(x, y) };
        private static GUIButton Button(GUIComponent parent, int x, int y, int w, string text, Action action) => new GUIButton(Rect(parent, x, y, w, 28), text) { OnClicked = (_, _) => { action(); return true; } };

        private static void Open(View view) {
            Close(false); current = view; ball = pocket = -1; placing = safe = false; tip = NVector2.Zero; direction = NVector2.UnitX; error = PoolError.Ok; shownRevision = uint.MaxValue; shownPower = -1;
            int w = Math.Min(1440, GameMain.GraphicsWidth - 32), h = Math.Min(930, GameMain.GraphicsHeight - 32);
            frame = new GUIFrame(new RectTransform(new Point(w, h), GUI.Canvas, Anchor.Center));
            new GUITextBlock(Rect(frame, 20, 10, 140, 28), "Бильярд", font: GUIStyle.SubHeadingFont);
            Button(frame, w - 52, 10, 32, "×", () => Close(true));
            players = new GUITextBlock(Rect(frame, 20, 43, w - 40, 28), "", font: GUIStyle.SmallFont);
            surface = new GUICustomComponent(Rect(frame, 20, 78, w - 40, h - 272), DrawSurface) { CanBeFocused = true, HoverCursor = CursorState.Hand };
            call = new GUITextBlock(Rect(frame, 20, h - 190, w - 40, 24), "", font: GUIStyle.SmallFont);
            int row = h - 157, lower = h - 98;
            join = Button(frame, 20, row, 172, "Играть", () => Command(PoolOp.Join));
            leave = Button(frame, 20, row, 172, "Покинуть стол", ConfirmLeave);
            eight = Button(frame, 210, row, 172, "Восьмёрка", () => Command(PoolOp.EightBall));
            free = Button(frame, 398, row, 172, "Свободная игра", () => Command(PoolOp.Free));
            for (int i = 0; i < 3; i++) { int choice = i; choices[i] = Button(frame, 210 + i * 188, row, 176, "", () => Choose(choice)); choices[i].Visible = false; }
            new GUITextBlock(Rect(frame, 20, lower, 130, 22), "Сила удара", font: GUIStyle.SmallFont);
            power = new GUIScrollBar(Rect(frame, 20, lower + 26, 190, 18), barSize: 0.1f, style: "GUISlider") { BarScroll = 0.45f };
            strength = new GUITextBlock(Rect(frame, 158, lower, 52, 22), "", font: GUIStyle.SmallFont);
            spin = new GUICustomComponent(Rect(frame, 232, lower - 6, 64, 64), (sb, component) => PoolDraw.Spin(sb, component.Rect, tip)) { CanBeFocused = true, HoverCursor = CursorState.Hand };
            new GUITextBlock(Rect(frame, 310, lower, 116, 48), "Точка удара", font: GUIStyle.SmallFont, wrap: true);
            int actionWidth = Math.Max(80, (w - 466) / 4);
            strike = Button(frame, 438, lower + 10, actionWidth, "Удар", () => Command(PoolOp.Strike));
            safety = Button(frame, 446 + actionWidth, lower + 10, actionWidth, "Отыгрыш", () => { safe = !safe; safety.Selected = safe; });
            place = Button(frame, 454 + actionWidth * 2, lower + 10, actionWidth, "Биток с руки", () => { placing = !placing; place.Selected = placing; });
            stalemate = Button(frame, 462 + actionWidth * 3, lower + 10, actionWidth, "Переиграть", ConfirmStalemate);
            message = new GUITextBlock(Rect(frame, 20, h - 30, w - 40, 24), "", font: GUIStyle.SmallFont);
            statusScope = Motion.MotionClient.Runtime.Scope(); statusOpacity = statusScope.Scalar(1); shownTurn = -1;
            Refresh();
        }
        private static void ConfirmLeave() {
            PoolMeta m = current.Replica.Meta;
            if (m.Mode == PoolMode.EightBall && m.Phase != PoolPhase.Waiting && m.Phase != PoolPhase.Finished) { Confirm("Сдаться и покинуть стол?", () => Command(PoolOp.Leave)); }
            else { Command(PoolOp.Leave); }
        }
        private static void ConfirmStalemate() {
            PoolMeta m = current.Replica.Meta;
            Confirm(m.Stalemate < 0 ? "Предложить переиграть эту партию?" : m.Stalemate == m.Me ? "Отозвать предложение?" : "Переиграть эту партию?", () => Command(PoolOp.Stalemate));
        }
        private static void Confirm(string text, Action yes) {
            confirmation?.Close(); View owner = current;
            var box = confirmation = new GUIMessageBox("Бильярд", text, buttons: new LocalizedString[] { "Да", "Нет" });
            box.Buttons[0].OnClicked = (_, _) => { confirmation = null; box.Close(); if (ReferenceEquals(current, owner)) { yes(); } return true; };
            box.Buttons[1].OnClicked = (_, _) => { confirmation = null; box.Close(); return true; };
        }
        private static void Choose(int index) {
            PoolMeta m = current.Replica.Meta; PoolAction action;
            if (m.Phase == PoolPhase.BreakChoice) { action = index == 0 ? PoolAction.BreakSelf : PoolAction.BreakOther; }
            else if (m.Choice == PoolChoice.EightOnBreak) { action = index == 0 ? PoolAction.Spot : PoolAction.RackSelf; }
            else if (m.Choice == PoolChoice.IllegalBreak) { action = index == 0 ? PoolAction.Accept : index == 1 ? PoolAction.RackSelf : PoolAction.RackOther; }
            else { action = index == 0 ? PoolAction.Accept : PoolAction.Kitchen; }
            Command(PoolOp.Choose, action: action);
        }
        private static void Command(PoolOp op, NVector2 position = default, PoolAction action = PoolAction.Accept) {
            if (current == null || !current.Replica.Ready || pending != 0) { return; }
            PoolMeta m = current.Replica.Meta; if (++token == 0) { token++; }
            var command = new PoolCommand { Table = current.Item.ID, Epoch = m.Epoch, Revision = m.Revision, Token = token, Op = op, Aim = op == PoolOp.Place ? position : direction,
                Tip = m.Phase == PoolPhase.Lag ? NVector2.Zero : tip, Speed = 0.25f + power.BarScroll * 9.75f, Ball = ball, Pocket = pocket, Safety = safe, Action = action };
            if (current.Local != null) { error = PoolWire.Apply(current.Local, 1, command, 1, (uint)Environment.TickCount); LocalSnapshot(current); shownRevision = 0; return; }
            pending = token; PoolWire.Request(retry, command); retryAt = Timing.TotalTime + 1; deadline = Timing.TotalTime + 10;
            ModNet.SendBytes(PoolWire.Channel, retry.Span, DeliveryMethod.Reliable); shownRevision = 0;
        }
        private static Surface Fit(Rectangle area) {
            Table table = PoolModule.Layout; float scale = MathF.Min(area.Width / table.UiImage.X, area.Height / table.UiImage.Y);
            return new Surface(new NVector2(area.Center.X, area.Center.Y), scale);
        }
        private static void DrawSurface(SpriteBatch batch, GUICustomComponent component) {
            if (current == null || !current.Replica.Ready) { return; }
            if (statusOpacity.Alive) { message.TextColor = Color.White * statusOpacity.Value; }
            Surface fitted = Fit(component.Rect); PoolDraw.Table(batch, fitted); PoolDraw.Balls(batch, current.Replica, fitted, 0, current.Spheres.Texture);
            PoolMeta m = current.Replica.Meta;
            if (!m.Moving && m.Me >= 0 && (m.Mode == PoolMode.Free || m.Me == m.Turn) && m.Phase is PoolPhase.Aiming or PoolPhase.Lag && !placing) { PoolDraw.Aim(batch, current.Replica, fitted, direction, power.BarScroll); }
            if (!m.Break && m.Phase == PoolPhase.Aiming && m.Mode == PoolMode.EightBall) {
                for (int i = 0; i < 6; i++) { Vector2 p = PoolDraw.Xna(fitted.Ball(PoolModule.Layout, PoolModule.Layout.Pockets[i])); GUI.DrawString(batch, p - new Vector2(3, 7), pocketLabels[i], pocket == i ? Color.Yellow : Color.White, font: GUIStyle.SmallFont); }
            }
            if (ball > 0 && !current.Replica.Current[ball].Down) { Vector2 p = PoolDraw.Xna(fitted.Ball(PoolModule.Layout, current.Replica.Current[ball].Position)); GUI.DrawRectangle(batch, new Rectangle((int)p.X - 11, (int)p.Y - 11, 22, 22), Color.Yellow); }
            if (placing && component.Rect.Contains(PlayerInput.MousePosition)) { Vector2 p = PlayerInput.MousePosition; GUI.DrawLine(batch, p - Vector2.UnitX * 8, p + Vector2.UnitX * 8, Color.White); GUI.DrawLine(batch, p - Vector2.UnitY * 8, p + Vector2.UnitY * 8, Color.White); }
        }
        public static void Prepare() {
            if (Screen.Selected != GameMain.GameScreen || GameMain.GameSession == null) { return; }
            double now = Timing.TotalTime + Timing.Alpha * Timing.Step;
            Rectangle world = GameMain.GameScreen.Cam.WorldView;
            foreach (View view in views.Values) {
                Item item = view.Item;
                bool visible = !item.Removed && !item.HiddenInGame && item.ParentInventory == null &&
                    item.WorldPosition.X >= world.Left - 1000 && item.WorldPosition.X <= world.Right + 1000 &&
                    item.WorldPosition.Y <= world.Top + 1000 && item.WorldPosition.Y >= world.Top - world.Height - 1000;
                if (ReferenceEquals(view, current) || visible || Rec.ScreenCameras.Observes(item)) { view.Seen = now; view.Spheres.Update(view.Replica, now); }
                else if (now - view.Seen > 15) { view.Spheres.Dispose(); }
            }
        }

        public static void DrawItemPostfix(Item __instance, SpriteBatch spriteBatch, bool editing, float? overrideDepth) {
            if (__instance.Removed || !__instance.Visible || !__instance.HasTag(PoolModule.Tag) || editing && !Item.ShowItems || !editing && __instance.IsHidden || !SubEditorScreen.IsLayerVisible(__instance)) { return; }
            View view = Find(__instance); if (!view.Replica.Ready) { return; }
            Vector2 anchor = ItemPose.DrawPosition(__instance);
            var fitted = Surface.World(PoolModule.Layout, new NVector2(anchor.X, -anchor.Y), __instance.Scale, ItemPose.DrawAngle(__instance));
            float depth = MathF.Max(0, (overrideDepth ?? __instance.GetDrawDepth()) - 0.0001f);
            PoolDraw.Balls(spriteBatch, view.Replica, fitted, depth, view.Spheres.Texture);
            PoolMeta meta = view.Replica.Meta;
            if (ReferenceEquals(view, current) && !meta.Moving && meta.Phase is PoolPhase.Aiming or PoolPhase.Lag && meta.Me >= 0 && (meta.Mode == PoolMode.Free || meta.Me == meta.Turn) && !placing) { PoolDraw.Aim(spriteBatch, view.Replica, fitted, direction, power.BarScroll, depth); }
            else if (!view.Replica.Meta.Moving && view.Cue.Revision == view.Replica.Meta.Revision && Timing.TotalTime - view.CueAt < 1) { PoolDraw.Aim(spriteBatch, view.Replica, fitted, view.Cue.Direction, view.Cue.Power, depth); }
        }
        public static void Tick(double deltaTime) {
            double now = Timing.TotalTime; IReadOnlyList<Item> list = Tagged.Of(PoolModule.Registry);
            for (int i = 0; i < list.Count; i++) { Find(list[i]); }
            removed.Clear();
            foreach (View view in views.Values) {
                if (view.Item.Removed || !view.Item.HasTag(PoolModule.Tag)) { removed.Add(view.Item.ID); continue; }
                if (view.Local != null && double.IsFinite(deltaTime) && deltaTime >= 0) {
                    view.Local.AdvanceTime(deltaTime);
                    if (view.Sent != view.Local.Revision || view.Local.Physics.Moving && now >= view.Next) { LocalSnapshot(view); view.Next = now + 0.05; }
                } else if (!view.Replica.Ready && now >= pollAt && PoolModule.Near(Character.Controlled, view.Item, 1800)) {
                    pollAt = now + 0.25; PoolWire.Request(writer, new PoolCommand { Table = view.Item.ID, Op = PoolOp.Poll }); ModNet.SendBytes(PoolWire.Channel, writer.Span, DeliveryMethod.Reliable);
                }
            }
            for (int i = 0; i < removed.Count; i++) { if (views.Remove(removed[i], out View gone)) { gone.Spheres.Dispose(); } }
            if (pending != 0 && now >= deadline) { pending = 0; error = PoolError.Reach; shownRevision = 0; }
            if (pending != 0 && now >= retryAt) { retryAt = now + 1; ModNet.SendBytes(PoolWire.Channel, retry.Span, DeliveryMethod.Reliable); }
            if (current == null) { return; }
            if (!PoolModule.CanUse(Character.Controlled, current.Item)) { Close(true); return; }
            Input(); Refresh();
            if (GameMain.Client != null && now >= nextAim && power.Enabled && !placing && !GUI.InputBlockingMenuOpen) {
                nextAim = now + 0.25;
                PoolMeta meta = current.Replica.Meta;
                PoolWire.Request(writer, new PoolCommand { Table = current.Item.ID, Epoch = meta.Epoch, Revision = meta.Revision, Op = PoolOp.Aim,
                    Aim = direction, Tip = tip, Speed = 0.25f + power.BarScroll * 9.75f });
                ModNet.SendBytes(PoolWire.Channel, writer.Span, DeliveryMethod.Unreliable);
            }
        }
        private static void Input() {
            if (GUI.InputBlockingMenuOpen || !current.Replica.Ready || pending != 0) { pulling = false; return; }
            PoolMeta m = current.Replica.Meta; bool mine = !m.Moving && m.Me >= 0 && (m.Mode == PoolMode.Free || m.Me == m.Turn);
            if (!mine || m.Phase is not (PoolPhase.Aiming or PoolPhase.Lag)) { pulling = false; return; }
            Vector2 mouse = PlayerInput.MousePosition;
            if (pulling) {
                NVector2 point = Fit(surface.Rect).Position(PoolModule.Layout, new NVector2(mouse.X, mouse.Y));
                float pull = NVector2.Dot(pullAt - point, direction);
                power.BarScroll = Math.Clamp(pullStart + pull / 0.35f, 0, 1);
                if (PlayerInput.PrimaryMouseButtonReleased()) { pulling = false; if (pull > 0.015f && strike.Enabled) { Command(PoolOp.Strike); } }
                return;
            }
            if (spin.Rect.Contains(mouse) && PlayerInput.PrimaryMouseButtonHeld() && m.Phase != PoolPhase.Lag) {
                tip = new NVector2((mouse.X - spin.Rect.Center.X) * 2 / spin.Rect.Width, -(mouse.Y - spin.Rect.Center.Y) * 2 / spin.Rect.Height);
                if (tip.LengthSquared() > 0.64f) { tip = NVector2.Normalize(tip) * 0.8f; }
            }
            if (!surface.Rect.Contains(mouse) || GUI.MouseOn != surface) { return; }
            Surface fitted = Fit(surface.Rect); NVector2 at = fitted.Position(PoolModule.Layout, new NVector2(mouse.X, mouse.Y));
            if (PlayerInput.ScrollWheelSpeed != 0) { power.BarScroll = Math.Clamp(power.BarScroll + Math.Sign(PlayerInput.ScrollWheelSpeed) * 0.025f, 0, 1); }
            if (!placing) { NVector2 aim = at - current.Replica.Current[0].Position; if (aim.LengthSquared() > 0.0001f) { direction = NVector2.Normalize(aim); } }
            if (PlayerInput.PrimaryMouseButtonDown() && !placing && strike.Enabled) {
                bool control = false; float near = PoolModule.Layout.Radius * 2;
                for (int i = 1; i < 16; i++) { control |= !current.Replica.Current[i].Down && NVector2.DistanceSquared(at, current.Replica.Current[i].Position) < near * near; }
                for (int i = 0; i < 6; i++) { control |= NVector2.DistanceSquared(at, PoolModule.Layout.Pockets[i]) < near * near * 4; }
                if (!control) { pulling = true; pullAt = at; pullStart = power.BarScroll; }
            }
            if (!PlayerInput.PrimaryMouseButtonClicked()) { return; }
            if (placing && m.InHand) { Command(PoolOp.Place, at); placing = false; place.Selected = false; shownRevision = 0; return; }
            if (m.Mode == PoolMode.EightBall && !m.Break && m.Phase == PoolPhase.Aiming) {
                float radius = PoolModule.Layout.Radius * 2;
                ushort live = LiveMask(); byte group = m.Me == 0 ? m.Group0 : m.Group1;
                for (int i = 1; i < 16; i++) { if (Match.CallAllowed(live, group, i) && NVector2.DistanceSquared(at, current.Replica.Current[i].Position) < radius * radius) { ball = i; shownRevision = 0; return; } }
                for (int i = 0; i < 6; i++) { if (NVector2.DistanceSquared(at, PoolModule.Layout.Pockets[i]) < radius * radius * 4) { pocket = i; shownRevision = 0; return; } }
            }
        }
        private static void Refresh() {
            if (current == null || frame == null) { return; }
            PoolMeta m = current.Replica.Meta; bool ready = current.Replica.Ready, member = ready && m.Me >= 0, available = ready && pending == 0 && !GUI.InputBlockingMenuOpen;
            bool mine = !m.Moving && member && (m.Mode == PoolMode.Free || m.Me == m.Turn), waiting = !m.Moving && (m.Phase is PoolPhase.Waiting or PoolPhase.Finished || m.Mode == PoolMode.Free && m.Phase == PoolPhase.Aiming);
            join.Visible = !member; join.Enabled = available && m.Occupied != 3; leave.Visible = member; leave.Enabled = available;
            eight.Visible = free.Visible = waiting; eight.Enabled = available && member && m.Occupied == 3; free.Enabled = available && member;
            bool choice = m.Phase is PoolPhase.Choice or PoolPhase.BreakChoice;
            for (int i = 0; i < choices.Length; i++) { choices[i].Visible = choice && (i < 2 || m.Choice == PoolChoice.IllegalBreak); choices[i].Enabled = available && mine; }
            strike.Enabled = available && mine && m.Phase is PoolPhase.Aiming or PoolPhase.Lag && !current.Replica.Current[0].Down && (m.Mode == PoolMode.Free || m.Break || m.Phase == PoolPhase.Lag || safe || ball > 0 && pocket >= 0);
            safety.Enabled = available && mine && m.Mode == PoolMode.EightBall && !m.Break && m.Phase == PoolPhase.Aiming;
            place.Enabled = available && mine && m.InHand; stalemate.Enabled = available && member && m.Mode == PoolMode.EightBall && m.Phase == PoolPhase.Aiming;
            power.Enabled = available && mine && m.Phase is PoolPhase.Aiming or PoolPhase.Lag; spin.Enabled = power.Enabled && m.Phase != PoolPhase.Lag;
            int percent = (int)MathF.Round(power.BarScroll * 100);
            if (shownPower != percent) { strength.Text = percent + "%"; shownPower = percent; }
            if (shownRevision == m.Revision && shownOnline == m.Online) { return; }
            shownRevision = m.Revision; shownOnline = m.Online;
            if (ball > 0 && !Match.CallAllowed(LiveMask(), m.Me == 0 ? m.Group0 : m.Group1, ball)) { ball = -1; }
            if (m.Turn != shownTurn || m.Phase != shownPhase) {
                shownTurn = m.Turn; shownPhase = m.Phase;
                if (statusOpacity.Alive) { statusOpacity.Snap(0.45f); statusOpacity.To(1, Motion.MotionPresets.Fade); }
            }
            players.Text = PlayerLabel(m.Name0, m.Group0) + "  •  " + m.Wins0 + " побед" +
                ((m.Occupied & 2) != 0 ? "       " + PlayerLabel(m.Name1, m.Group1) + "  •  " + m.Wins1 + " побед" : "");
            call.Text = placing ? m.Kitchen ? "Биток ставится за передней линией" : "Выберите место для битка" : m.Phase == PoolPhase.Lag ? "Ударьте в дальний борт. Ближе к переднему борту выигрывает." : m.Mode == PoolMode.Free || m.Break ? "" : safe ? "Отыгрыш" : ball > 0 && pocket >= 0 ? "Шар " + ball + ", луза " + (pocket + 1) : "Выберите шар и лузу";
            message.Text = !ready ? "Получение состояния стола" : pending != 0 ? "Ожидание ответа" : error != PoolError.Ok ? PoolWire.Explain(error) : m.Fault ? "Ошибка симуляции" : m.Phase == PoolPhase.Waiting ? "Займите место и выберите игру" : m.Phase == PoolPhase.Finished ? m.Winner == m.Me ? "Вы выиграли" : "Партия окончена" : m.Stalemate >= 0 ? m.Stalemate == m.Me ? "Предложена переигровка" : "Соперник предлагает переиграть" : m.Phase == PoolPhase.Choice || m.Phase == PoolPhase.BreakChoice ? mine ? "Выберите продолжение" : "Соперник выбирает продолжение" : m.Phase == PoolPhase.Moving ? "" : m.Foul != PoolFoul.None ? Foul(m.Foul) : mine ? m.InHand && current.Replica.Current[0].Down ? "Поставьте биток" : "Ваш ход" : "Ход соперника";
            if (m.Phase == PoolPhase.BreakChoice) { choices[0].Text = "Разбиваю"; choices[1].Text = "Передать разбой"; }
            else if (m.Choice == PoolChoice.EightOnBreak) { choices[0].Text = "Выставить восьмёрку"; choices[1].Text = "Разбить заново"; }
            else if (m.Choice == PoolChoice.IllegalBreak) { choices[0].Text = "Принять позицию"; choices[1].Text = "Разбиваю заново"; choices[2].Text = "Повторить сопернику"; }
            else { choices[0].Text = "Принять позицию"; choices[1].Text = "Биток с руки"; }
        }
        private static ushort LiveMask() { ushort live = 0; for (int i = 0; i < 16; i++) { if (!current.Replica.Current[i].Down) { live |= (ushort)(1 << i); } } return live; }
        private static string PlayerLabel(string name, byte group) => string.IsNullOrEmpty(name) ? "" : name + (group == 0 ? "" : " (" + (group == 1 ? "сплошные: " : "полосатые: ") + System.Numerics.BitOperations.PopCount((uint)(LiveMask() & Match.GroupMask(group))) + ")");
        private static string Foul(PoolFoul foul) => foul switch { PoolFoul.Scratch => "Фол: биток в лузе", PoolFoul.WrongBall => "Фол: первый контакт", PoolFoul.NoRail => "Фол: нет борта после контакта", PoolFoul.OffTable => "Фол: шар за столом", PoolFoul.Kitchen => "Фол: удар из-за передней линии", PoolFoul.Break => "Неправильный разбой", _ => "" };
    }
}
