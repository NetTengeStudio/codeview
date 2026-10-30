using System;
using System.Collections.Generic;
using Barotrauma;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ScpItdMod.Core;

namespace ScpItdMod.Pc {
    public static class PrinterDraw {
        private sealed class View {
            public Item Item;
            public Rectangle Output;
            public readonly DisplayView State = new DisplayView();
        }
        private static readonly Dictionary<ushort, View> views = new Dictionary<ushort, View>();
        private static readonly List<ushort> removed = new List<ushort>();
        public static void Receive(Item item, ReadOnlySpan<byte> data) {
            if (!views.TryGetValue(item.ID, out View view) || !ReferenceEquals(view.Item, item)) {
                view = new View { Item = item, Output = item.Prefab.ConfigElement.GetAttributeRect("scpitdoutput", Rectangle.Empty) }; views[item.ID] = view;
            }
            view.State.Apply(data);
        }
        public static void Sweep() {
            removed.Clear();
            foreach (var pair in views) { if (pair.Value.Item.Removed || !pair.Value.Item.HasTag(PrinterWorld.Tag)) { removed.Add(pair.Key); } }
            for (int i = 0; i < removed.Count; i++) { views.Remove(removed[i]); }
        }
        public static void Clear() { views.Clear(); removed.Clear(); }
        public static void Draw(Item item, SpriteBatch batch, float? overrideDepth) {
            if (item.IsHidden || item.ParentInventory != null || !views.TryGetValue(item.ID, out View view) || !view.State.Ready) { return; }
            PrinterView state = view.State.Printer;
            if (state.Pages != 0 && state.Printed < state.Pages) {
                float progress = Math.Clamp(state.Progress / 255f * state.Pages - state.Printed, 0, 1);
                Texture2D paper = TextureStore.File("Content/Items/Paper/coalitiondoc.png");
                if (paper != null && progress > 0 && view.Output.Width > 0 && view.Output.Height > 0) {
                    Sprite sprite = item.Prefab.Sprite;
                    Vector2 local = (new Vector2(view.Output.Center.X, view.Output.Y) - sprite.Origin) * item.Scale;
                    Vector2 anchor = Core.ItemPose.DrawPosition(item);
                    float angle = Core.ItemPose.DrawAngle(item), cosine = MathF.Cos(angle), sine = MathF.Sin(angle);
                    Vector2 at = new Vector2(anchor.X, -anchor.Y) + new Vector2(local.X * cosine - local.Y * sine, local.X * sine + local.Y * cosine);
                    int height = Math.Max(1, (int)(paper.Height * progress));
                    batch.Draw(paper, at, new Rectangle(0, 0, paper.Width, height), Color.White, angle, new Vector2(paper.Width / 2f, 0),
                        new Vector2(view.Output.Width * item.Scale / paper.Width, view.Output.Height * item.Scale / paper.Height), SpriteEffects.None, MathF.Max(0, (overrideDepth ?? item.GetDrawDepth()) - 0.0001f));
                }
            }
            if (!Rec.CameraScene.Capturing && (ReferenceEquals(Character.Controlled?.FocusedItem, item) || ReferenceEquals(Character.Controlled?.SelectedItem, item))) {
                Vector2 at = new Vector2(item.DrawPosition.X, -item.DrawPosition.Y) - new Vector2(0, item.Rect.Height * item.Scale / 2 + 14);
                GUI.DrawString(batch, at, state.Text, state.State == PrinterState.Ready || state.State == PrinterState.Busy ? Color.LightGreen : Color.LightGray, font: GUIStyle.SmallFont);
            }
        }
    }
}
