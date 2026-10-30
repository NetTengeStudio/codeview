using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Barotrauma;
using Barotrauma.Networking;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ScpItdMod.Core;
using ScpItdMod.Motion;
using ScpItdMod.Ui;

namespace ScpItdMod.Hud {
    public static class ChatType {
        public const int MaxReveals = 16;
        public const int MaxPopups = 8;
        private const float PopupSeconds = 5.0f;
        private const float GrowOmega = 22.0f;
        private const float SlideOmega = 18.0f;

        private sealed class Reveal {
            public GUITextBlock Block;
            public GUIComponent Holder;
            public GUICustomComponent Overlay;
            public TypeText Text;
            public readonly TypeLayout Layout = new TypeLayout();
            public GameFace Face;
            public string Wrapped;
            public double Received;
            public TypePresentation Presentation;
            public TypeClickCursor Clicks;
            public Color Saved;
            public bool SavedOverride;
            public MotionScope Motion;
            public MotionHandle<float> Shown;
        }

        private sealed class Popup {
            public GUIComponent Native;
            public bool Rendered;
            public string Sender;
            public Color SenderColor;
            public TypeText Text;
            public readonly TypeLayout Layout = new TypeLayout();
            public GameFace Face;
            public float MaxWidth;
            public float Width;
            public Color Ink;
            public TypePresentation Presentation;
            public TypeClickCursor Clicks;
            public float Front;
            public MotionScope Motion;
            public MotionHandle<float> Y;
            public bool Placed;
            public bool Leaving;
            public double LeaveAt;
        }

        private static readonly AccessTools.FieldRef<ChatBox, List<GUIComponent>> nativePopups =
            AccessTools.FieldRefAccess<ChatBox, List<GUIComponent>>("popupMessages");
        private static readonly List<Reveal> reveals = new List<Reveal>(MaxReveals);
        private static readonly List<Popup> popups = new List<Popup>(MaxPopups);
        private static readonly BatchCanvas canvas = new BatchCanvas();
        private static readonly BatchCanvas popupCanvas = new BatchCanvas();
        private static ChatBox owner;
        private static GUICustomComponent popupOverlay;
        private static double lastPaint;

        public static void Attach(Harmony harmony) {
            harmony.Patch(AccessTools.Method(typeof(ChatBox), nameof(ChatBox.AddMessage)),
                prefix: ModuleHost.Bind(typeof(ChatType), nameof(AddMessagePrefix)),
                postfix: ModuleHost.Bind(typeof(ChatType), nameof(AddMessagePostfix)));
            harmony.Patch(AccessTools.Method(typeof(ChatBox), nameof(ChatBox.Update)),
                postfix: ModuleHost.Bind(typeof(ChatType), nameof(UpdatePostfix)));
        }

        public static void Release() {
            for (int i = reveals.Count - 1; i >= 0; i--) { Finish(reveals[i]); }
            reveals.Clear();
            for (int i = 0; i < popups.Count; i++) { popups[i].Motion?.Dispose(); }
            popups.Clear();
            popupOverlay?.Parent?.RemoveChild(popupOverlay);
            popupOverlay = null;
            canvas.Release();
            popupCanvas.Release();
            owner = null;
            lastPaint = 0.0;
        }

        private static GUIComponent LastMessage(ChatBox chat) {
            GUIComponent content = chat?.chatBox?.Content;
            return content == null || content.CountChildren == 0 ? null : content.GetChild(content.CountChildren - 1);
        }

        public static void AddMessagePrefix(ChatBox __instance, out GUIComponent __state) => __state = LastMessage(__instance);

        public static void AddMessagePostfix(ChatBox __instance, ChatMessage message, GUIComponent __state) {
            if (__instance == null || message == null) { return; }
            GUIComponent holder = LastMessage(__instance);
            if (holder == null || ReferenceEquals(holder, __state)) { return; }
            if (owner != null && !ReferenceEquals(owner, __instance)) { Release(); }
            owner = __instance;
            GUITextBlock block = TextOf(holder);
            if (__instance.ToggleOpen) {
                if (block != null) { Track(holder, block); }
            } else {
                TakePopup(__instance, message, block);
            }
        }

        private static GUITextBlock TextOf(GUIComponent holder) {
            foreach (GUIComponent child in holder.Children) {
                if (child is GUITextBlock text && text.Wrap) { return text; }
            }
            return null;
        }

        private static TypeText Parse(GUITextBlock block, ChatMessage message = null) => block != null ?
            TypeText.Parse(block.Text.SanitizedValue, TypeSettings.Pace, false, Runs(block.RichTextData)) :
            TypeText.Parse(message?.TranslatedText, TypeSettings.Pace, true);

        private static void Track(GUIComponent holder, GUITextBlock block) {
            ScalableFont font = block.Font?.Value;
            string wrapped = block.WrappedText.Value;
            if (font == null || string.IsNullOrEmpty(wrapped) || TypeSettings.Pace.Instant) { return; }
            if (reveals.Count == MaxReveals) {
                Finish(reveals[0]);
                reveals.RemoveAt(0);
            }
            var r = new Reveal {
                Block = block,
                Holder = holder,
                Text = Parse(block),
                Face = Faces.Of(font, font.LineHeight),
                Wrapped = wrapped,
                Received = HudClock.Now,
                Saved = block.TextColor,
                SavedOverride = block.OverrideRichTextDataAlpha
            };
            r.Layout.Follow(r.Text, wrapped, r.Face, font.LineHeight);
            r.Motion = MotionClient.Runtime.Scope();
            r.Shown = r.Motion.Scalar();
            r.Shown.Snap(font.LineHeight);
            r.Overlay = new GUICustomComponent(new RectTransform(Point.Zero, holder.RectTransform), (sb, _) => Draw(sb, r)) { CanBeFocused = false };
            reveals.Add(r);
        }

        private static List<TypeRun> Runs(ImmutableArray<RichTextData>? data) {
            if (data == null || data.Value.Length == 0) { return null; }
            var runs = new List<TypeRun>(data.Value.Length);
            foreach (RichTextData d in data.Value) {
                if (d.Color is Color c) { runs.Add(new TypeRun(d.StartIndex, d.EndIndex, new Rgba(c.R, c.G, c.B, c.A).Packed)); }
            }
            return runs;
        }

        private static void Draw(SpriteBatch sb, Reveal r) {
            GUITextBlock b = r.Block;
            if (!b.Visible || Rectangle.Intersect(b.Rect, sb.GraphicsDevice.ScissorRectangle).IsEmpty) { return; }
            ScalableFont font = b.Font?.Value;
            if (font == null) { b.TextColor = r.Saved; b.OverrideRichTextDataAlpha = r.SavedOverride; return; }
            string wrapped = b.WrappedText.Value;
            GameFace face = Faces.Of(font, font.LineHeight);
            if (!ReferenceEquals(face, r.Face) || !string.Equals(wrapped, r.Wrapped, StringComparison.Ordinal)) {
                r.Face = face;
                r.Wrapped = wrapped;
                r.Layout.Follow(r.Text, wrapped, face, font.LineHeight);
            }
            double now = HudClock.Now;
            r.Presentation.Present(now);
            Vector2 pos = b.Rect.Location.ToVector2() + b.TextPos + b.TextOffset;
            if (b.RoundToNearestPixel) { pos = new Vector2((int)pos.X, (int)pos.Y); }
            Color ink = r.Saved;
            bool submitted = false;
            try {
                canvas.Join(sb, GUI.Scale, smooth: true, typewriter: true);
                try {
                    TypeDraw.Draw(canvas, r.Text, r.Layout, r.Face, pos.X, pos.Y, r.Presentation.Age(now),
                        new Rgba(ink.R, ink.G, ink.B, ink.A), default, 1.0f, 0.0f);
                    TypeSound.Present(ref r.Clicks, r.Text, r.Layout.Count, r.Presentation.Age(now), 1.0f, now);
                } finally { canvas.End(); }
                submitted = true;
            } finally {
                b.TextColor = submitted ? new Color(r.Saved.R, r.Saved.G, r.Saved.B, (byte)0) : r.Saved;
                b.OverrideRichTextDataAlpha = submitted || r.SavedOverride;
            }
        }

        public static void UpdatePostfix(ChatBox __instance, float deltaTime) {
            if (__instance == null || !ReferenceEquals(owner, __instance)) { return; }
            double now = HudClock.Now;
            Reveal newest = null;
            for (int i = reveals.Count - 1; i >= 0; i--) {
                Reveal r = reveals[i];
                bool gone = r.Holder.Parent == null || r.Block.Parent == null || !__instance.ToggleOpen;
                bool done = r.Presentation.Started ? r.Presentation.Age(now) >= r.Text.Duration : now - r.Received >= TypeMotion.PendingSeconds;
                if (gone || done) {
                    Finish(r);
                    reveals.RemoveAt(i);
                } else { newest ??= r; }
            }
            Follow(__instance, newest, now, deltaTime);
            if (__instance.ToggleOpen) { for (int i = 0; i < popups.Count; i++) { popups[i].Motion?.Dispose(); } popups.Clear(); }
        }

        private static void Follow(ChatBox chat, Reveal r, double now, float dt) {
            if (r == null || !chat.ToggleOpen || !ReferenceEquals(LastMessage(chat), r.Holder)) { return; }
            GUIListBox list = chat.chatBox;
            float range = list.TotalSize - list.Content.Rect.Height;
            if (range <= 1.0f) { return; }
            r.Shown.To(r.Layout.HeightAt(r.Text, r.Presentation.Age(now)), MotionTransition.Spring(GrowOmega, 1.0f));
            float hidden = MathF.Max(0.0f, r.Layout.Height - r.Shown.Value);
            float target = Scalar.Clamp01((range - hidden) / range);
            if (list.BarScroll < target - 0.0005f && list.BarScroll < 0.97f - hidden / range) { return; }
            list.BarScroll = target;
        }

        private static void Finish(Reveal r) {
            r.Block.TextColor = r.Saved;
            r.Block.OverrideRichTextDataAlpha = r.SavedOverride;
            r.Overlay?.Parent?.RemoveChild(r.Overlay);
            r.Overlay = null;
            r.Motion?.Dispose();
            r.Motion = null;
        }

        private static void TakePopup(ChatBox chat, ChatMessage message, GUITextBlock block) {
            GUIComponent frame = chat.GUIFrame;
            List<GUIComponent> pending = nativePopups(chat);
            if (frame == null || pending.Count == 0) { return; }
            GUIComponent native = pending[pending.Count - 1];
            if (GUIStyle.SmallFont.Value == null) { return; }
            TypeText text = Parse(block, message);
            if (text.Count == 0) { return; }
            if (popupOverlay == null) {
                popupOverlay = new GUICustomComponent(new RectTransform(Point.Zero, frame.RectTransform), PaintPopups) { CanBeFocused = false };
            }
            string sender = string.IsNullOrWhiteSpace(message.SenderName) ? "" :
                (message.Type == ChatMessageType.Private ? "[PM] " : "") + message.SenderName;
            Color senderColor = message.SenderCharacter?.Info?.Job is { } job ? Color.Lerp(job.Prefab.UIColor, Color.White, 0.25f) : Color.White;
            if (popups.Count == MaxPopups) { popups[0].Motion?.Dispose(); popups.RemoveAt(0); }
            var popup = new Popup { Native = native, Sender = sender, SenderColor = senderColor, Text = text, Ink = message.Color };
            popup.Motion = MotionClient.Runtime.Scope();
            popup.Y = popup.Motion.Scalar();
            popups.Add(popup);
        }

        private static void PaintPopups(SpriteBatch spriteBatch, GUICustomComponent component) {
            ChatBox chat = owner;
            if (popups.Count == 0 || chat == null || chat.ToggleOpen) { lastPaint = 0.0; return; }
            ScalableFont font = GUIStyle.SmallFont.Value;
            GUIComponent frame = chat.GUIFrame;
            if (font == null || frame == null) { return; }
            float scale = GUI.Scale;
            GameFace face = Faces.Of(font, font.LineHeight);
            double now = HudClock.Now;
            float dt = lastPaint > 0.0 ? (float)Math.Max(0.0, now - lastPaint) : 0.0f;
            lastPaint = now;
            float pad = MathF.Round(6.0f * scale);
            float gap = MathF.Round(10.0f * scale);
            float maxWidth = MathF.Max(face.Advance('W'), frame.Rect.Width * 0.95f - pad * 2.0f);
            float reports = GameMain.GameSession?.CrewManager?.ReportButtonFrame?.Rect.Width ?? 0;
            float shownX = frame.Rect.X + reports + frame.Rect.Width + MathF.Round(35.0f * scale);
            popupCanvas.Join(spriteBatch, scale, smooth: true, typewriter: true);
            try {
                float y = frame.Rect.Y;
                for (int i = 0; i < popups.Count; i++) {
                    Popup p = popups[i];
                    if (!ReferenceEquals(p.Face, face) || p.MaxWidth != maxWidth) {
                        p.Layout.Wrap(p.Text, face, maxWidth, face.LineHeight, 12);
                        p.Face = face;
                        p.MaxWidth = maxWidth;
                        p.Width = MathF.Max(p.Layout.Width, Ui.Paint.Measure(face, p.Sender));
                    }
                    p.Presentation.Present(now);
                    float age = p.Presentation.Age(now);
                    float textAge = age - TypeMotion.RevealLead;
                    if (i == 0 && textAge >= p.Text.Duration && !p.Leaving) {
                        p.Front += dt * Math.Max(popups.Count / 2, 1);
                        if (p.Front >= PopupSeconds) { p.Leaving = true; p.LeaveAt = now; }
                    }
                    float enter = TypeMotion.Ease(age / TypeMotion.EnterSeconds);
                    float leave = p.Leaving ? TypeMotion.Ease((float)(now - p.LeaveAt) / TypeMotion.LeaveSeconds) : 0.0f;
                    float a = enter * (1.0f - leave);
                    float h = MathF.Min(GameMain.GraphicsHeight, p.Layout.HeightAt(p.Text, textAge) + (p.Sender.Length > 0 ? face.LineHeight : 0.0f) + pad * 2.0f);
                    if (!p.Placed) { p.Y.Snap(y); p.Placed = true; }
                    p.Y.To(y, MotionTransition.Spring(SlideOmega, 1.0f));
                    float x = shownX - scale * (12.0f * (1.0f - enter) + 16.0f * leave);
                    float width = MathF.Min(GameMain.GraphicsWidth, p.Width + pad * 2.0f);
                    var box = new Box(Math.Clamp(x, 0, MathF.Max(0, GameMain.GraphicsWidth - width)), Math.Clamp(p.Y.Value, 0, MathF.Max(0, GameMain.GraphicsHeight - h)), width, h);
                    popupCanvas.PushClip(box);
                    popupCanvas.Fill(box, Retro.Plate.Fade(0.9f * a));
                    Ui.Paint.Frame(popupCanvas, box, 1.0f, Retro.Edge.Fade(a));
                    float nameH = p.Sender.Length > 0 ? face.LineHeight : 0.0f;
                    if (nameH > 0.0f) {
                        Ui.Paint.Text(popupCanvas, face, p.Sender, box.X + pad, box.Y + pad,
                            new Rgba(p.SenderColor.R, p.SenderColor.G, p.SenderColor.B, p.SenderColor.A).Fade(a));
                    }
                    TypeDraw.Draw(popupCanvas, p.Text, p.Layout, face, box.X + pad, box.Y + pad + nameH, textAge,
                        new Rgba(p.Ink.R, p.Ink.G, p.Ink.B, p.Ink.A), default, a, 0.0f);
                    if (a > 0) { p.Rendered = true; }
                    TypeSound.Present(ref p.Clicks, p.Text, p.Layout.Count, textAge, a, now);
                    popupCanvas.PopClip();
                    y += (h + gap) * (1.0f - leave);
                }
            } finally {
                popupCanvas.End();
            }
            for (int i = popups.Count - 1; i >= 0; i--) {
                Popup rendered = popups[i];
                if (rendered.Rendered && rendered.Native != null) {
                    nativePopups(chat).Remove(rendered.Native);
                    rendered.Native.Parent?.RemoveChild(rendered.Native); rendered.Native = null;
                }
                if (popups[i].Leaving && now - popups[i].LeaveAt >= TypeMotion.LeaveSeconds) { popups[i].Motion?.Dispose(); popups.RemoveAt(i); }
            }
        }
    }
}
