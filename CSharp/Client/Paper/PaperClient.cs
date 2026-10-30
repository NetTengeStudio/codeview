using System;
using Barotrauma;
using Barotrauma.Networking;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ScpItdMod.Core;
using ScpItdMod.Net;

namespace ScpItdMod.Paper {
    public static class PaperClient {
        private static readonly WireWriter request = new WireWriter(PaperRules.MaxRequest);

        public static void Attach(Harmony harmony) {
            ModNet.ReceiveBytes(PaperModule.Channel, 1 << 16, OnMessage);
            SheetView.Attach();
            Faces.Attach(harmony);
            Faces.Stamping = SheetView.Stamping;
            harmony.Patch(AccessTools.Method(typeof(Character), nameof(Character.ControlLocalPlayer), new[] { typeof(float), typeof(Camera), typeof(bool) }),
                prefix: ModuleHost.Bind(typeof(SheetView), nameof(SheetView.ControlPrefix)),
                postfix: ModuleHost.Bind(typeof(PaperClient), nameof(ControlPostfix)));
            harmony.Patch(AccessTools.Method(typeof(GUI), nameof(GUI.TogglePauseMenu), Type.EmptyTypes),
                prefix: ModuleHost.Bind(typeof(SheetView), nameof(SheetView.EscapePrefix)));
            harmony.Patch(AccessTools.Method(typeof(GameMain), "Draw", new[] { typeof(GameTime) }),
                prefix: ModuleHost.Bind(typeof(PaperClient), nameof(DrawPrefix)));
            HudFrame.Register(HudFrame.Meters + 5, SheetView.Paint);
            harmony.Patch(AccessTools.Method(typeof(GUI), "DrawCursor", new[] { typeof(SpriteBatch) }), prefix: ModuleHost.Bind(typeof(PaperClient), nameof(CursorPrefix)));
        }

        public static void Reset() {
            SheetView.Reset();
            StampHand.Reset();
            Faces.Reset();
        }

        public static void Release() {
            Reset();
            Faces.Release();
            Script.Hand.Release(); Script.Printed.Release(); Script.Heading.Release();
            SheetView.ReleaseMotion();
        }

        public static bool CursorPrefix(SpriteBatch spriteBatch) {
            if (!GameMain.WindowActive || GUI.HideCursor) { StampHand.Reset(); return true; }
            return !StampHand.DrawCursor(spriteBatch);
        }

        public static void ControlPostfix(Character __instance) {
            if (ReferenceEquals(__instance, Character.Controlled)) { SheetView.Control(__instance); }
        }

        public static void DrawPrefix() {
            try {
                SheetView.SweepDrafts();
                Faces.Frame();
            } catch (Exception error) {
                ModLog.Once($"paper faces cannot update: {error.Message}");
            }
        }

        public static void Open(ushort item) {
            PaperRules.Head(request, PaperOp.Open, item);
            Send();
        }

        public static void Save(ushort item, uint rev, bool final, string body, uint token) {
            PaperRules.Save(request, item, rev, final, body, token);
            Send();
        }

        public static void Stamp(ushort item, in Mark mark) {
            PaperRules.Stamp(request, item, mark);
            Send();
        }

        public static void Show(ushort item, ushort target) {
            PaperRules.Show(request, item, target);
            Send();
        }

        public static void AskFaces(ReadOnlySpan<FaceAsk> asks) {
            PaperRules.Faces(request, asks);
            Send();
        }

        private static void Send() => ModNet.SendBytes(PaperModule.Channel, request.Span, DeliveryMethod.Reliable);

        private static void OnMessage(byte[] payload) {
            var r = new WireReader(payload);
            if (!PaperRules.Head(ref r, out PaperOp op, out ushort item)) { return; }
            switch (op) {
                case PaperOp.View:
                    if (PaperRules.ReadView(ref r, item, out PaperView view)) { SheetView.OnView(view); }
                    break;
                case PaperOp.Saved:
                    if (PaperRules.ReadSaved(ref r, out PaperError saved, out uint rev, out uint token)) { SheetView.OnSaved(item, saved, rev, token); }
                    break;
                case PaperOp.Face:
                    if (PaperRules.ReadFace(ref r, item, out PaperFace face)) { Faces.OnFace(face); }
                    break;
                case PaperOp.Note:
                    if (PaperRules.ReadNote(ref r, out PaperError note)) { SheetView.OnNote(item, note); }
                    break;
            }
        }
    }
}
