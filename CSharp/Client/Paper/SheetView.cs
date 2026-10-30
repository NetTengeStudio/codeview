using System;
using System.Collections.Generic;
using Barotrauma;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using ScpItdMod.Core;
using ScpItdMod.Inv;
using ScpItdMod.Rp;
using ScpItdMod.Motion;

namespace ScpItdMod.Paper {
    public static class SheetView {
        private enum State : byte { Closed, Waiting, Open }

        private const double SaveIdle = 0.6;
        private const double SaveMax = 2.5;
        private const double DoubleClick = 0.35;
        private const float Fill = 0.86f;
        private static readonly Color Selection = new Color(70, 110, 220, 70);
        private static readonly Color Hint = new Color(225, 225, 225);

        private static State state;
        private static ushort itemId;
        private static Item item;
        private static PaperMode mode;
        private static ushort presenter;
        private static double shownUntil;
        private static readonly Dictionary<Item, PaperDraft> drafts = new Dictionary<Item, PaperDraft>();
        private static PaperDraft draft;
        private static readonly List<Item> removedDrafts = new List<Item>();
        private static uint rev => draft?.Revision ?? PaperRules.Unknown;

        private static string marksText = "";
        private static readonly Mark[] marks = new Mark[Sheet.MaxMarks + 1];
        private static int markCount;
        private static bool predicted;
        private static double predictedAt;

        private static InkEdit edit => draft?.Edit;
        private static readonly Pen pen = new Pen(null);
        private static uint[] grains = new uint[256];
        private static float[] born = new float[256];
        private static Glyph[] glyphs = new Glyph[256];
        private static int glyphCount;
        private static int glyphVersion = -1;
        private static uint glyphAtlas;

        private static double lastEdit;
        private static double firstDirty;
        private static bool parked;
        private static bool wasFocused;
        private static double escaped = double.NegativeInfinity;
        private static int refused;

        private static MotionScope motion;
        private static MotionHandle<float> reveal, tilt, bump, shake, ink;
        private static readonly MotionTransition PaperTilt = MotionTransition.Spring(7, 0.55f);
        private static readonly MotionTransition PaperBump = MotionTransition.Spring(34, 0.42f);
        private static readonly MotionTransition PaperShake = MotionTransition.Spring(48, 0.22f);
        private static readonly MotionTransition PaperInk = MotionTransition.Spring(20, 1);
        private static void EnsureMotion() {
            if (motion?.Alive == true) { return; }
            motion?.Dispose(); motion = MotionClient.Runtime.Scope();
            reveal = motion.Scalar(); tilt = motion.Angle(); bump = motion.Scalar(); shake = motion.Scalar(); ink = motion.Scalar();
        }
        public static void ReleaseMotion() { motion?.Dispose(); motion = null; }
        private static string status = "";
        private static Color statusColor = Hint;
        private static double statusUntil;
        private static bool dragging;
        private static double lastClick = double.NegativeInfinity;
        private static int lastClickCaret = -1;
        private static Vector2 centre;
        private static float scale = 1.0f;
        private static float turn;
        private static Rectangle showButton;
        private static readonly Random random = new Random();

        public static bool Active => state != State.Closed;

        public static bool Typing => pen.Focused;

        public static void Attach() { EnsureMotion(); }

        public static void Reset() {
            EnsureMotion();
            pen.Blur();
            state = State.Closed;
            item = null;
            itemId = 0;
            reveal.Snap(0.0f);
            drafts.Clear(); removedDrafts.Clear(); draft = null; pen.Target = null;
            predicted = false;
            markCount = 0;
        }

        public static bool Stamping(ushort id) => predicted && id == itemId;

        public static bool Covers(Vector2 mouse, out float pixels) {
            pixels = scale;
            if (state != State.Open || reveal.Value < 0.6f) { return false; }
            Vector2 tex = ToSheet(mouse);
            return Sheet.Inside(tex.X, tex.Y);
        }

        public static void Open(Item paper) {
            EnsureMotion();
            if (paper == null) { return; }
            if (state != State.Closed && ReferenceEquals(item, paper) && mode == PaperMode.Own) { return; }
            Close(true);
            item = paper;
            itemId = paper.ID;
            mode = PaperMode.Own;
            state = State.Waiting;
            if (!drafts.TryGetValue(paper, out draft)) { draft = NewDraft(); drafts.Add(paper, draft); }
            pen.Target = edit; SetMarks(draft.Marks); ResetInk(float.NegativeInfinity); glyphVersion = -1;
            ink.Snap(0.0f);
            tilt.Snap((float)(random.NextDouble() - 0.5) * 0.12f);
            parked = false;
            InvUi.Close();
            ActPanel.Hide();
            PaperClient.Open(itemId);
        }

        public static void Close(bool save) {
            if (state == State.Closed) { return; }
            if (save && mode == PaperMode.Own && state == State.Open) { SaveDraft(item, draft); }
            if (mode == PaperMode.Own && draft != null && !draft.Dirty && !draft.Pending) { drafts.Remove(item); }
            pen.Blur();
            wasFocused = false;
            state = State.Closed;
            dragging = false;
            predicted = false;
        }

        public static void OnView(in PaperView view) {
            EnsureMotion();
            Item paper = Entity.FindEntityByID(view.Item) as Item;
            if (!PaperModule.IsPaper(paper)) { return; }
            if (view.Mode == PaperMode.Shown) {
                Close(true);
                item = paper;
                itemId = view.Item;
                mode = PaperMode.Shown;
                presenter = view.Presenter;
                shownUntil = Timing.TotalTime + PaperRules.ShowSeconds;
                draft = NewDraft(); pen.Target = edit;
                Load(view.Body, view.Marks, view.Rev);
                ink.Snap(0.0f);
                tilt.Snap((float)(random.NextDouble() - 0.5) * 0.12f);
                state = State.Open;
                Character who = Entity.FindEntityByID(presenter) as Character;
                Say("Вам показывают лист" + (who?.Info != null ? ": " + who.Info.DisplayName : ""), Hint);
                return;
            }
            if (!drafts.TryGetValue(paper, out PaperDraft received)) { Faces.Local(paper, view.Body, view.Marks, view.Rev); return; }
            if (!received.Receive(view.Body, view.Marks, view.Rev, view.Request)) { return; }
            if (state == State.Closed || view.Item != itemId || mode != PaperMode.Own) { return; }
            state = State.Open; SetMarks(draft.Marks); ResetInk(float.NegativeInfinity); glyphVersion = -1;
            predicted = false;
            if (draft.Conflict) {
                PaperDraft conflicted = draft;
                var dialog = new GUIMessageBox("Конфликт версий", "Лист изменён другим игроком. Ваш текст остаётся в редакторе.\nЗаменить — записать ваш текст. Открыть — удалить черновик и загрузить серверный текст.", buttons: new LocalizedString[] { "Заменить", "Открыть", "Отмена" });
                dialog.Buttons[0].OnClicked = (_, _) => { conflicted.Resolve(true); SaveDraft(paper, conflicted); dialog.Close(); return true; };
                dialog.Buttons[1].OnClicked = (_, _) => { conflicted.Resolve(false); if (ReferenceEquals(conflicted, draft)) { ResetInk(float.NegativeInfinity); glyphVersion = -1; } dialog.Close(); return true; };
                dialog.Buttons[2].OnClicked = (_, _) => { dialog.Close(); return true; };
            }
            if (view.Result != PaperError.Ok) { Say(PaperRules.Explain(view.Result), GUIStyle.Orange); }
            Faces.Local(paper, edit.Text, marksText, rev);
        }

        private static PaperDraft NewDraft() {
            var created = new PaperDraft(new InkEdit(Script.Hand, Sheet.BodyWidth, Sheet.Lines, Sheet.BodyMax));
            created.Edit.Edited = OnEdited; return created;
        }

        private static void SaveDraft(Item paper, PaperDraft value) {
            if (paper == null || paper.Removed || value == null || !value.BeginSave(out string body, out uint revision)) { return; }
            PaperClient.Save(paper.ID, revision, true, body, value.Request);
        }

        public static void SweepDrafts() {
            removedDrafts.Clear();
            foreach (Item paper in drafts.Keys) { if (paper.Removed) { removedDrafts.Add(paper); } }
            for (int i = 0; i < removedDrafts.Count; i++) { drafts.Remove(removedDrafts[i]); }
        }

        public static void OnSaved(ushort id, PaperError result, uint next, uint request) {
            if (Entity.FindEntityByID(id) is not Item paper || !drafts.TryGetValue(paper, out PaperDraft saved)) { return; }
            if (!saved.Pending || saved.Request != request) { return; }
            bool success = saved.Complete(result, next, request);
            if (success) {
                Faces.Local(paper, saved.Edit.Text, saved.Marks, saved.Revision);
                if (state == State.Closed || !ReferenceEquals(saved, draft)) {
                    SaveDraft(paper, saved);
                    if (!saved.Dirty && !saved.Pending) { drafts.Remove(paper); }
                }
            } else {
                if (state != State.Closed && ReferenceEquals(saved, draft)) { Say(PaperRules.Explain(result), GUIStyle.Orange); }
                else { RpClient.Say(PaperRules.Explain(result), GUIStyle.Orange); }
            }
        }

        public static void OnNote(ushort id, PaperError result) {
            if (id != itemId || state == State.Closed) {
                if (result != PaperError.Ok) { RpClient.Say(PaperRules.Explain(result), GUIStyle.Orange); }
                return;
            }
            if (result == PaperError.Ok) {
                Say("Лист показан.", GUIStyle.Green);
                return;
            }
            if (state == State.Waiting) {
                Close(false);
                RpClient.Say(PaperRules.Explain(result), GUIStyle.Orange);
                return;
            }
            predicted = false;
            Say(PaperRules.Explain(result), GUIStyle.Orange);
        }

        public static void ControlPrefix(Character __instance) {
            if (ReferenceEquals(__instance, Character.Controlled) && pen.Focused) { Character.DisableControls = true; }
        }

        public static void Control(Character me) {
            EnsureMotion();
            if (state == State.Closed) {
                TryOpen(me);
                return;
            }
            me.ClearInput(InputType.Use);
            me.ClearInput(InputType.Aim);
            me.ClearInput(InputType.Shoot);
            double now = Timing.TotalTime;
            if (item == null || item.Removed) {
                Close(false);
                return;
            }
            if (!PlayView.Active(me) || me.IsIncapacitated || InvUi.Active || ActPanel.Shown) {
                Close(true);
                return;
            }
            if (mode == PaperMode.Own && !PaperModule.Reaches(me, item)) {
                Close(true);
                return;
            }
            if (mode == PaperMode.Shown && (now > shownUntil ||
                Vector2.Distance(me.WorldPosition, item.WorldPosition) > PaperRules.ShowRange * 1.6f)) {
                Close(false);
                return;
            }
            bool writable = state == State.Open && mode == PaperMode.Own && PaperModule.Writes(me, item);
            string kind = mode == PaperMode.Own ? PaperModule.StampKind(PaperModule.HeldStamp(me)) : null;
            if (PlayerInput.KeyHit(Keys.Escape)) {
                escaped = now;
                Close(true);
                return;
            }
            if (wasFocused && !pen.Focused) { parked = true; }
            if (writable && !parked) {
                pen.Focus(new Rectangle((int)centre.X, (int)centre.Y, 1, 1));
            } else {
                pen.Blur();
            }
            if (pen.Refused != refused) {
                refused = pen.Refused;
                shake.Impulse(900.0f);
                Say(PaperRules.Explain(PaperError.Long), GUIStyle.Orange);
            }

            Vector2 mouse = PlayerInput.MousePosition;
            Vector2 tex = ToSheet(mouse);
            bool over = Sheet.Inside(tex.X, tex.Y) && reveal.Value > 0.5f;
            bool onGui = GUI.MouseOn != null;
            if (PlayerInput.SecondaryMouseButtonClicked() && !onGui) {
                Close(true);
                return;
            }
            if (PlayerInput.PrimaryMouseButtonDown() && !onGui) {
                if (mode == PaperMode.Own && showButton.Contains(mouse)) {
                    ShowNearest(me);
                } else if (over && kind != null && state == State.Open) {
                    StampHand.Press(Contact);
                } else if (over && writable) {
                    parked = false;
                    int row = Math.Clamp((int)MathF.Floor((tex.Y - Sheet.BodyTop) / Sheet.Pitch), 0, Sheet.Lines - 1);
                    float x = tex.X - Sheet.BodyLeft;
                    edit.Point(row, x, PlayerInput.IsShiftDown());
                    if (now - lastClick < DoubleClick && edit.Caret == lastClickCaret) {
                        edit.SelectWord(edit.Caret);
                        dragging = false;
                    } else {
                        dragging = true;
                    }
                    lastClick = now;
                    lastClickCaret = edit.Caret;
                } else if (!over && !StampHand.Busy) {
                    Close(true);
                    return;
                }
            }
            if (dragging) {
                if (PlayerInput.PrimaryMouseButtonHeld() && writable) {
                    int row = Math.Clamp((int)MathF.Floor((tex.Y - Sheet.BodyTop) / Sheet.Pitch), 0, Sheet.Lines - 1);
                    edit.Point(row, tex.X - Sheet.BodyLeft, true);
                } else {
                    dragging = false;
                }
            }
            wasFocused = pen.Focused;
            Autosave(now, writable);
        }

        public static bool EscapePrefix() {
            if (state == State.Closed) { return escaped != Timing.TotalTime; }
            Close(true);
            return false;
        }

        private static void TryOpen(Character me) {
            if (!PlayView.Active(me) || PlayView.Busy || PlayView.Typing || InvUi.Active || ActPanel.Shown) { return; }
            if (!me.IsKeyHit(InputType.Use) || me.IsKeyDown(InputType.Aim) || GUI.MouseOn != null || Inventory.IsMouseOnInventory) { return; }
            Item stamp = PaperModule.HeldStamp(me);
            Item paper = PaperModule.HeldPaper(me);
            if (paper == null && stamp != null && PaperModule.IsPaper(me.FocusedItem) && PaperModule.Reaches(me, me.FocusedItem)) {
                paper = me.FocusedItem;
            }
            if (paper == null) {
                if (stamp != null) { RpClient.Say("Возьмите лист в другую руку или наведите печать на лист рядом.", Color.LightGray); }
                return;
            }
            me.ClearInput(InputType.Use);
            if (GameMain.Client == null) {
                RpClient.Say("Листы работают только в сетевой игре.", Color.LightGray);
                return;
            }
            Open(paper);
        }

        private static void Autosave(double now, bool writable) {
            if (!writable || draft == null || draft.Pending || draft.Conflict || !draft.Dirty) { return; }
            if (now - lastEdit < SaveIdle && now - firstDirty < SaveMax) { return; }
            SaveDraft(item, draft);
        }

        private static bool Contact(Vector2 mouse, float sway) {
            Character me = Character.Controlled;
            string kind = PaperModule.StampKind(PaperModule.HeldStamp(me));
            if (state != State.Open || mode != PaperMode.Own || kind == null) { return false; }
            Vector2 tex = ToSheet(mouse);
            if (!Sheet.Inside(tex.X, tex.Y)) {
                Say(PaperRules.Explain(PaperError.Outside), GUIStyle.Orange);
                return false;
            }
            float angle = Math.Clamp(sway * 0.6f - turn + (float)(random.NextDouble() - 0.5) * 0.08f, -Sheet.MaxTurn, Sheet.MaxTurn);
            Mark mark = Mark.At(kind, tex.X, tex.Y, angle);
            MarkError error = Marks.Check(new ReadOnlySpan<Mark>(marks, 0, markCount + (predicted ? 1 : 0)), mark);
            if (error != MarkError.Ok) {
                Say(PaperRules.Explain(PaperRules.Of(error)), GUIStyle.Orange);
                return false;
            }
            marks[markCount] = mark;
            predicted = true;
            predictedAt = Timing.TotalTime;
            bump.Impulse(220.0f * scale / 2.6f);
            PaperClient.Stamp(itemId, mark);
            return true;
        }

        private static void ShowNearest(Character me) {
            Character target = me.FocusedCharacter;
            if (!Viewer(me, target)) {
                target = null;
                float best = PaperRules.ShowRange;
                foreach (Character c in Character.CharacterList) {
                    if (!Viewer(me, c)) { continue; }
                    float d = Vector2.Distance(me.WorldPosition, c.WorldPosition);
                    if (d < best) {
                        best = d;
                        target = c;
                    }
                }
            }
            if (target == null) {
                Say(PaperRules.Explain(PaperError.Target), GUIStyle.Orange);
                return;
            }
            PaperClient.Show(itemId, target.ID);
            Say("Показываю лист: " + (target.Info?.DisplayName ?? target.Name), Hint);
        }

        private static bool Viewer(Character me, Character c) =>
            c != null && !ReferenceEquals(c, me) && !c.Removed && !c.IsDead && c.IsHuman && c.IsRemotePlayer &&
            Vector2.Distance(me.WorldPosition, c.WorldPosition) <= PaperRules.ShowRange;

        private static void Load(string body, string marksValue, uint revision) {
            draft.Receive(body, marksValue, revision);
            ResetInk(float.NegativeInfinity);
            SetMarks(marksValue);
            glyphVersion = -1;
        }

        private static void SetMarks(string value) {
            marksText = value ?? "";
            markCount = Math.Max(0, Marks.Parse(marksText, new Span<Mark>(marks, 0, Sheet.MaxMarks)));
        }

        private static void ResetInk(float time) {
            int n = edit.Length;
            Grow(n);
            for (int i = 0; i < n; i++) {
                grains[i] = Script.Hash((ulong)itemId, (uint)i, edit.Span[i]);
                born[i] = time;
            }
        }

        private static void OnEdited(int at, int removed, int inserted) {
            if (removed == int.MaxValue) { return; }
            int length = edit.Length;
            int before = length - inserted + removed;
            Grow(Math.Max(length, before));
            int tail = before - at - removed;
            if (tail > 0) {
                Array.Copy(grains, at + removed, grains, at + inserted, tail);
                Array.Copy(born, at + removed, born, at + inserted, tail);
            }
            float now = (float)Timing.TotalTime;
            for (int i = at; i < at + inserted; i++) {
                grains[i] = (uint)random.Next() ^ ((uint)random.Next() << 16);
                born[i] = now;
            }
            double t = Timing.TotalTime;
            if (edit.Version - 1 == draft.SavedVersion || t - lastEdit > SaveMax) { firstDirty = t; }
            lastEdit = t;
        }

        private static void Grow(int n) {
            if (grains.Length >= n + 1) { return; }
            int size = Math.Max(n + 1, grains.Length * 2);
            Array.Resize(ref grains, size);
            Array.Resize(ref born, size);
        }

        private static void Say(string text, Color color) {
            if (string.IsNullOrEmpty(text)) { return; }
            status = text;
            statusColor = color;
            statusUntil = Timing.TotalTime + 3.5;
        }

        private static Vector2 ToSheet(Vector2 screen) {
            if (scale <= 0.0f) { return new Vector2(-1.0f, -1.0f); }
            Vector2 d = screen - centre;
            float cos = MathF.Cos(-turn);
            float sin = MathF.Sin(-turn);
            return new Vector2((d.X * cos - d.Y * sin) / scale + Sheet.Width * 0.5f, (d.X * sin + d.Y * cos) / scale + Sheet.Height * 0.5f);
        }

        private static Vector2 ToScreen(float x, float y) {
            float lx = (x - Sheet.Width * 0.5f) * scale;
            float ly = (y - Sheet.Height * 0.5f) * scale;
            float cos = MathF.Cos(turn);
            float sin = MathF.Sin(turn);
            return new Vector2(centre.X + lx * cos - ly * sin, centre.Y + lx * sin + ly * cos);
        }

        public static void Paint(SpriteBatch spriteBatch) {
            EnsureMotion();
            reveal.To(state != State.Closed ? 1 : 0, state != State.Closed ? MotionPresets.Paper : MotionPresets.Window);
            tilt.To(0, PaperTilt); bump.To(0, PaperBump); shake.To(0, PaperShake);
            ink.To(state == State.Open ? 1 : 0, PaperInk);
            float shown = Scalar.Clamp01(reveal.Value);
            if (state == State.Closed && reveal.Value < 0.004f) {
                showButton = Rectangle.Empty;
                return;
            }
            Texture2D paper = TextureStore.File(Sheet.Art);
            if (paper == null) { return; }

            float height = MathF.Min(GameMain.GraphicsHeight * Fill, GameMain.GraphicsWidth * 0.62f * Sheet.Height / Sheet.Width);
            scale = height / Sheet.Height;
            float hide = 1.0f - reveal.Value;
            centre = new Vector2(GameMain.GraphicsWidth * 0.5f + shake.Value, GameMain.GraphicsHeight * 0.5f + hide * GameMain.GraphicsHeight * 0.75f + bump.Value);
            turn = tilt.Value + hide * 0.22f;
            Vector2 middle = new Vector2(Sheet.Width * 0.5f, Sheet.Height * 0.5f);
            Vector2 origin = ToScreen(0.0f, 0.0f);
            float now = (float)Timing.TotalTime;

            HudPaint.BeginScreen(spriteBatch);
            HudPaint.Fill(spriteBatch, new Rectangle(0, 0, GameMain.GraphicsWidth, GameMain.GraphicsHeight), Script.Fade(Color.Black, 0.38f * shown));
            Vector2 drop = new Vector2(7.0f, 11.0f) * (scale / 2.6f);
            spriteBatch.Draw(paper, centre + drop, null, Script.Fade(Color.Black, 0.32f * shown), turn, middle, scale, SpriteEffects.None, 0.0f);
            spriteBatch.Draw(paper, centre, null, Color.White, turn, middle, scale, SpriteEffects.None, 0.0f);

            Script.DrawHeader(spriteBatch, origin, scale, turn, shown);

            if (pen.Focused && edit.HasSelection) { DrawSelection(spriteBatch); }

            uint atlas = Script.AtlasFor(scale);
            if (glyphVersion != edit.Version || glyphAtlas != atlas) {
                glyphCount = Script.Hand.Build(edit.Span, edit.Layout, itemId, new ReadOnlySpan<uint>(grains, 0, edit.Length), atlas, ref glyphs);
                glyphVersion = edit.Version;
                glyphAtlas = atlas;
            }
            Script.Draw(spriteBatch, new ReadOnlySpan<Glyph>(glyphs, 0, glyphCount), origin, scale, turn,
                Script.Fade(Script.InkColor, ink.Value), new ReadOnlySpan<float>(born, 0, edit.Length), now);

            if (pen.Focused) { DrawCaret(spriteBatch, now); }

            for (int i = 0; i < markCount; i++) { Script.DrawMark(spriteBatch, marks[i], origin, scale, turn, 1.0f, itemId); }
            if (predicted) {
                float age = (float)(Timing.TotalTime - predictedAt);
                Script.DrawMark(spriteBatch, marks[markCount], origin, scale, turn, Scalar.Clamp01(age / 0.08f), itemId);
            }

            DrawFooter(spriteBatch, shown);
            HudPaint.End(spriteBatch);
        }

        private static void DrawSelection(SpriteBatch spriteBatch) {
            InkLayout layout = edit.Layout;
            int from = edit.SelectionStart;
            int to = edit.SelectionEnd;
            int firstRow = layout.Line(from);
            int lastRow = layout.Line(to);
            for (int row = firstRow; row <= lastRow; row++) {
                float x0 = row == firstRow ? layout.X(from) : 0.0f;
                float x1 = row == lastRow ? layout.X(to) : layout.X(layout.End(row)) + Sheet.Em * 0.3f;
                if (x1 <= x0) { continue; }
                Vector2 at = ToScreen(Sheet.BodyLeft + x0, Sheet.BodyTop + row * Sheet.Pitch + 1.0f);
                spriteBatch.Draw(GUI.WhiteTexture, at, null, Selection, turn, Vector2.Zero,
                    new Vector2((x1 - x0) * scale / GUI.WhiteTexture.Width, Sheet.Pitch * scale / GUI.WhiteTexture.Height), SpriteEffects.None, 0.0f);
            }
        }

        private static void DrawCaret(SpriteBatch spriteBatch, float now) {
            InkLayout layout = edit.Layout;
            int caret = edit.Caret;
            float x = Sheet.BodyLeft + layout.X(caret);
            float top = Sheet.BodyTop + layout.Line(caret) * Sheet.Pitch + Sheet.Pitch * 0.12f;
            float blink = 0.5f + 0.5f * MathF.Cos((now - (float)lastEdit) * MathF.PI * 2.0f * 0.9f);
            Vector2 a = ToScreen(x, top);
            Vector2 b = ToScreen(x, top + Sheet.Pitch * 0.82f);
            GUI.DrawLine(spriteBatch, a, b, Script.Fade(Script.InkColor, 0.25f + 0.75f * blink), 0.0f, MathF.Max(1.5f, scale * 0.6f));
            if (pen.Composing.Length > 0) {
                GUIFont font = GUIStyle.Font;
                Vector2 size = font.MeasureString(pen.Composing);
                Vector2 at = ToScreen(x, top);
                GUI.DrawRectangle(spriteBatch, new Rectangle((int)at.X, (int)at.Y, (int)size.X + 4, (int)size.Y), Script.Fade(Color.White, 0.9f), true);
                font.DrawString(spriteBatch, pen.Composing, at + new Vector2(2.0f, 0.0f), Script.InkColor);
                GUI.DrawLine(spriteBatch, at + new Vector2(2.0f, size.Y - 2.0f), at + new Vector2(size.X + 2.0f, size.Y - 2.0f), Script.InkColor);
            }
        }

        private static void DrawFooter(SpriteBatch spriteBatch, float shown) {
            GUIFont font = GUIStyle.SmallFont;
            Character me = Character.Controlled;
            bool writable = state == State.Open && mode == PaperMode.Own && PaperModule.Writes(me, item);
            bool stamping = mode == PaperMode.Own && PaperModule.HeldStamp(me) != null;
            string hint = state == State.Waiting ? "…"
                : mode == PaperMode.Shown ? "ПКМ — вернуть"
                : stamping ? "ЛКМ: поставить печать    ПКМ: закрыть"
                : writable ? "Пишите прямо на листе. ПКМ: закрыть"
                : "ПКМ — убрать";
            float bottom = centre.Y + Sheet.Height * 0.5f * scale + 10.0f * GUI.Scale;
            Vector2 hintSize = font.MeasureString(hint);
            var hintAt = new Vector2(MathF.Round(centre.X - hintSize.X * 0.5f), MathF.Round(bottom));
            if (mode == PaperMode.Own && state == State.Open) {
                const string label = "Показать рядом стоящему";
                Vector2 size = font.MeasureString(label);
                var at = new Vector2(MathF.Round(centre.X - size.X * 0.5f), MathF.Round(bottom + hintSize.Y + 4.0f * GUI.Scale));
                showButton = new Rectangle((int)at.X - 6, (int)at.Y - 2, (int)size.X + 12, (int)size.Y + 4);
                bool hover = showButton.Contains(PlayerInput.MousePosition);
                font.DrawString(spriteBatch, label, at + Vector2.One, Script.Fade(Color.Black, 0.6f * shown));
                font.DrawString(spriteBatch, label, at, Script.Fade(hover ? Color.White : Hint, (hover ? 1.0f : 0.85f) * shown));
                if (hover) { GUI.DrawLine(spriteBatch, at + new Vector2(0.0f, size.Y), at + new Vector2(size.X, size.Y), Script.Fade(Color.White, shown)); }
            } else {
                showButton = Rectangle.Empty;
            }
            font.DrawString(spriteBatch, hint, hintAt + Vector2.One, Script.Fade(Color.Black, 0.6f * shown));
            font.DrawString(spriteBatch, hint, hintAt, Script.Fade(Hint, 0.85f * shown));
            if (Timing.TotalTime < statusUntil && status.Length > 0) {
                float fade = Scalar.Clamp01((float)(statusUntil - Timing.TotalTime) / 0.5f) * shown;
                Vector2 size = font.MeasureString(status);
                float top = centre.Y - Sheet.Height * 0.5f * scale - size.Y - 10.0f * GUI.Scale;
                var at = new Vector2(MathF.Round(centre.X - size.X * 0.5f), MathF.Round(top));
                font.DrawString(spriteBatch, status, at + Vector2.One, Script.Fade(Color.Black, 0.7f * fade));
                font.DrawString(spriteBatch, status, at, Script.Fade(statusColor, fade));
            }
        }
    }
}
