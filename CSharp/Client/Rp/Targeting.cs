using System;
using Barotrauma;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using ScpItdMod.Core;
using ScpItdMod.People;
using ScpItdMod.Ui;

namespace ScpItdMod.Rp {
    public static class Targeting {
        public const Keys Key = Keys.F;
        public const string KeyName = "F";

        private static ushort item;
        private static bool cuffs;
        private static string itemName = "";
        private static Character candidate;
        private static string candidateLabel = "";
        private static Character labelFor;
        private static readonly BatchCanvas canvas = new BatchCanvas();

        public static bool Active => item != 0;

        public static void Attach(Harmony harmony) {
            harmony.Patch(AccessTools.Method(typeof(Character), nameof(Character.ControlLocalPlayer)),
                postfix: ModuleHost.Bind(typeof(Targeting), nameof(ControlPostfix)));
            harmony.Patch(AccessTools.Method(typeof(GUI), nameof(GUI.TogglePauseMenu)),
                prefix: ModuleHost.Bind(typeof(Targeting), nameof(PausePrefix)));
            HudFrame.Register(HudFrame.Meters + 2, Paint);
        }

        public static void Begin(Item chosen) {
            Character me = Character.Controlled;
            if (chosen == null || me == null || !RpWorld.Owns(me, chosen)) { return; }
            item = chosen.ID;
            cuffs = RpWorld.IsCuffs(chosen);
            itemName = chosen.Name;
            candidate = null;
            Inv.InvUi.Close();
        }

        public static void Cancel() {
            item = 0;
            candidate = null;
        }

        public static bool PausePrefix() {
            if (!Active) { return true; }
            Cancel();
            return false;
        }

        public static void ControlPostfix(Character __instance) {
            if (!Active || !ReferenceEquals(__instance, Character.Controlled)) { return; }
            if (!(Entity.FindEntityByID(item) is Item chosen) || !RpWorld.Owns(__instance, chosen) || __instance.IsIncapacitated || __instance.IsDead) {
                Cancel();
                return;
            }
            __instance.ClearInput(InputType.Use);
            __instance.ClearInput(InputType.Shoot);
            __instance.ClearInput(InputType.Aim);
            candidate = ActPanel.Under(__instance);
            if (PlayerInput.SecondaryMouseButtonClicked() || PlayerInput.KeyHit(Key) && !PlayView.Typing) {
                Cancel();
                return;
            }
            if (PlayerInput.PrimaryMouseButtonClicked() && GUI.MouseOn == null && candidate != null) {
                Character chosenTarget = candidate;
                ushort chosenItem = item;
                RpAct act = cuffs ? RpAct.Detain : RpAct.Give;
                Cancel();
                ActPanel.OpenFor(__instance, chosenTarget, chosenItem, act);
            }
        }

        private static void Paint(SpriteBatch spriteBatch) {
            if (!Active) { return; }
            Camera cam = GameMain.GameScreen?.Cam;
            if (cam == null) { return; }
            float scale = GUI.Scale;
            RetroFaces f = Faces.Retro(scale);
            if (f.Body == null || f.Small == null) { return; }
            canvas.Begin(spriteBatch, scale);
            try {
                Vector2 mouse = PlayerInput.MousePosition;
                string head = cuffs ? "Сковать: выберите человека" : "Предмет: выберите человека";
                float row = Retro.Px(canvas, Retro.RowPx);
                float w = MathF.Max(Ui.Paint.Measure(f.Body, head), Ui.Paint.Measure(f.Body, itemName)) + Retro.Px(canvas, 20.0f);
                w = MathF.Max(w, Retro.HintWidth(canvas, f, "ЛКМ", "выбрать") + Retro.HintWidth(canvas, f, "ПКМ", "отмена") + Retro.Px(canvas, 12.0f));
                var box = new Box(MathF.Round(mouse.X + 24 * scale), MathF.Round(mouse.Y + 20 * scale), w, row * 3.0f + Retro.Px(canvas, 12.0f));
                Retro.Panel(canvas, box, 0.95f);
                Box inner = box.Inset(Retro.Px(canvas, 7.0f));
                Ui.Paint.Clipped(canvas, f.Body, head, inner.TakeTop(row), Retro.Bright, Align.Left);
                Ui.Paint.Clipped(canvas, f.Body, itemName, inner.CutTop(row).TakeTop(row), Retro.Warn, Align.Left);
                Box hints = inner.TakeBottom(row);
                float x = hints.X + Retro.Hint(canvas, f, hints.X, hints.Y, "ЛКМ", "выбрать", 1.0f);
                Retro.Hint(canvas, f, x, hints.Y, "ПКМ", "отмена", 1.0f);
                if (candidate != null && !candidate.Removed) {
                    if (!ReferenceEquals(labelFor, candidate)) {
                        labelFor = candidate;
                        candidateLabel = Names.Sighted(candidate);
                    }
                    Vector2 a = cam.WorldToScreen(candidate.WorldPosition + new Vector2(-45, 95));
                    Vector2 b = cam.WorldToScreen(candidate.WorldPosition + new Vector2(45, -95));
                    var bracket = new Box(MathF.Min(a.X, b.X), MathF.Min(a.Y, b.Y), MathF.Abs(b.X - a.X), MathF.Abs(b.Y - a.Y));
                    Corners(canvas, bracket, Retro.Px(canvas, 10.0f), Retro.Px(canvas, 2.0f), Retro.Warn);
                    var tag = new Box(bracket.X, bracket.Y - row - Retro.Px(canvas, 4.0f), MathF.Max(bracket.W, Ui.Paint.Measure(f.Body, candidateLabel) + Retro.Px(canvas, 12.0f)), row);
                    canvas.Fill(tag, Retro.Plate);
                    Ui.Paint.Text(canvas, f.Body, candidateLabel, tag.Inset(Retro.Px(canvas, 6.0f), 0, 0, 0), Retro.Bright, Align.Left);
                }
            } finally {
                canvas.End();
            }
        }

        private static void Corners(ICanvas c, Box b, float len, float t, Rgba ink) {
            c.Fill(new Box(b.X, b.Y, len, t), ink);
            c.Fill(new Box(b.X, b.Y, t, len), ink);
            c.Fill(new Box(b.Right - len, b.Y, len, t), ink);
            c.Fill(new Box(b.Right - t, b.Y, t, len), ink);
            c.Fill(new Box(b.X, b.Bottom - t, len, t), ink);
            c.Fill(new Box(b.X, b.Bottom - len, t, len), ink);
            c.Fill(new Box(b.Right - len, b.Bottom - t, len, t), ink);
            c.Fill(new Box(b.Right - t, b.Bottom - len, t, len), ink);
        }
    }
}
