using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Particles;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ScpItdMod.Core;

namespace ScpItdMod.Bath {
    // Вода и пар купели: рисуется по снимкам сервера, геометрия из префаба.
    public static class BathDraw {
        private static readonly Color Body = new Color(40, 110, 170, 150);
        private static readonly Color Line = new Color(150, 205, 235, 210);
        private const float PuffInterval = .35f;
        private const float LineThickness = 2f;
        private static readonly Dictionary<Item, BathBasin> geometry = new Dictionary<Item, BathBasin>();
        private static readonly List<Item> stale = new List<Item>();
        private static ParticlePrefab steam;
        private static float puffTimer;

        public static void Tick(float dt) {
            if (GameMain.ParticleManager == null || BathModule.Views.Count == 0) { return; }
            puffTimer -= dt;
            if (puffTimer > 0) { return; }
            puffTimer = PuffInterval;
            Prune();
            steam ??= ParticleManager.FindPrefab("steam");
            if (steam == null) { return; }
            foreach (KeyValuePair<ushort, BathSnapshot> pair in BathModule.Views) {
                if (!BathMath.Steaming(new BathWater(pair.Value.Litres, pair.Value.Celsius))) { continue; }
                if (Entity.FindEntityByID(pair.Key) is not Item bath || bath.Removed) { continue; }
                BathBasin basin = Geometry(bath);
                float s = bath.Scale;
                BathMath.ToWorld(0, basin.SurfaceLocal(pair.Value.Litres, s), ItemPose.Angle(bath), out float dx, out float dy);
                GameMain.ParticleManager.CreateParticle(steam, bath.WorldPosition + new Vector2(dx, dy), new Vector2(0, 30));
            }
        }

        public static void Front(SpriteBatch batch) {
            if (BathModule.Views.Count == 0) { return; }
            Camera cam = GameMain.GameScreen.Cam;
            batch.End();
            try {
                batch.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied, null, DepthStencilState.None, null, null, cam.Transform);
                foreach (KeyValuePair<ushort, BathSnapshot> pair in BathModule.Views) {
                    if (Entity.FindEntityByID(pair.Key) is not Item bath || bath.Removed) { continue; }
                    Water(batch, bath, Geometry(bath), pair.Value.Litres);
                }
                batch.End();
            } finally { batch.Begin(SpriteSortMode.BackToFront, BlendState.NonPremultiplied, null, DepthStencilState.None, null, null, cam.Transform); }
        }

        // Рисуется в системе предмета и переводится в экранные координаты (ось Y экрана вниз).
        private static void Water(SpriteBatch batch, Item bath, BathBasin basin, float litres) {
            float s = bath.Scale, angle = ItemPose.DrawAngle(bath);
            Vector2 center = ItemPose.DrawPosition(bath);
            float bottom = basin.Bottom * s, surface = basin.SurfaceLocal(litres, s), fill = surface - bottom;
            if (fill <= 0) { return; }
            float width = basin.Width * s;
            BathMath.ToWorld((basin.Left + basin.Width / 2) * s, bottom + fill / 2, angle, out float bx, out float by);
            batch.Draw(GUI.WhiteTexture, new Vector2(center.X + bx, -(center.Y + by)), null, Body, -angle, new Vector2(.5f, .5f), new Vector2(width, fill), SpriteEffects.None, 0);
            BathMath.ToWorld((basin.Left + basin.Width / 2) * s, surface, angle, out float lx, out float ly);
            batch.Draw(GUI.WhiteTexture, new Vector2(center.X + lx, -(center.Y + ly)), null, Line, -angle, new Vector2(.5f, .5f), new Vector2(width, LineThickness), SpriteEffects.None, 0);
        }

        private static BathBasin Geometry(Item bath) {
            if (!geometry.TryGetValue(bath, out BathBasin basin)) { basin = new BathBasin(bath); geometry[bath] = basin; }
            return basin;
        }

        public static void Prune() {
            stale.Clear();
            foreach (Item known in geometry.Keys) { if (known.Removed) { stale.Add(known); } }
            foreach (Item gone in stale) { geometry.Remove(gone); }
        }
    }
}
