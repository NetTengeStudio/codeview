using System;
using System.IO;
using Barotrauma;
using HarmonyLib;
using Microsoft.Xna.Framework;
using ScpItdMod.Core;

namespace ScpItdMod.Pool {
    public sealed class PoolModule : IModule, IRoundEnd {
        public static Table Layout { get; private set; }
        public static readonly Identifier Tag = "scpitd_pool".ToIdentifier();
        public static int Registry { get; private set; }
        public void Attach(Harmony harmony) {
            Layout = Table.Parse(File.ReadAllText(ModAssets.Resolve("Content/Data/pool.xml")));
            Registry = Tagged.Watch(Tag);
#if SERVER
            PoolHost.Attach(harmony);
#else
            PoolClient.Attach(harmony);
#endif
        }
        public void RoundStart() => Reset();
        public void RoundEnd() => Reset();
        public void Release() {
            Reset();
#if CLIENT
            PoolClient.Release();
#endif
        }
        private static void Reset() {
#if SERVER
            PoolHost.Clear();
#else
            PoolClient.Clear();
#endif
        }
        public static bool Near(Character c, Item table, float margin) {
            if (c == null || c.Removed || table == null || table.Removed || !table.HasTag(Tag) || !float.IsFinite(table.Scale) || table.Scale <= 0) { return false; }
            Vector2 d = c.WorldPosition - table.WorldPosition;
            float angle = Core.ItemPose.Angle(table), cosine = MathF.Cos(angle), sine = MathF.Sin(angle);
            float x = d.X * cosine - d.Y * sine, y = -d.X * sine - d.Y * cosine;
            x += Layout.WorldOrigin.X * table.Scale; y += Layout.WorldOrigin.Y * table.Scale;
            float dx = MathF.Max(0, MathF.Max(-x, x - Layout.WorldImage.X * table.Scale)), dy = MathF.Max(0, MathF.Max(-y, y - Layout.WorldImage.Y * table.Scale));
            return dx * dx + dy * dy <= margin * margin;
        }
        public static bool CanUse(Character c, Item table) => Rp.RpWorld.Conscious(c) && !c.LockHands && !Rp.RpWorld.IsSurrendered(c) && !Rp.RpWorld.IsHidden(c) && ReferenceEquals(c.SelectedItem, table) && Near(c, table, 160);
    }
}
