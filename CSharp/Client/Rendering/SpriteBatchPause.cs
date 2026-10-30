using System;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ScpItdMod.Rendering {
    internal readonly struct SpriteBatchPause : IDisposable {
        private static readonly AccessTools.FieldRef<SpriteBatch, SpriteSortMode> Sort = AccessTools.FieldRefAccess<SpriteBatch, SpriteSortMode>("_sortMode");
        private static readonly AccessTools.FieldRef<SpriteBatch, BlendState> Blend = AccessTools.FieldRefAccess<SpriteBatch, BlendState>("_blendState");
        private static readonly AccessTools.FieldRef<SpriteBatch, SamplerState> Sampler = AccessTools.FieldRefAccess<SpriteBatch, SamplerState>("_samplerState");
        private static readonly AccessTools.FieldRef<SpriteBatch, DepthStencilState> Depth = AccessTools.FieldRefAccess<SpriteBatch, DepthStencilState>("_depthStencilState");
        private static readonly AccessTools.FieldRef<SpriteBatch, RasterizerState> Rasterizer = AccessTools.FieldRefAccess<SpriteBatch, RasterizerState>("_rasterizerState");
        private static readonly AccessTools.FieldRef<SpriteBatch, SpriteBatch.EffectWithParams> Shader = AccessTools.FieldRefAccess<SpriteBatch, SpriteBatch.EffectWithParams>("_effect");
        private static readonly AccessTools.FieldRef<SpriteBatch, Matrix?> Transform = AccessTools.FieldRefAccess<SpriteBatch, Matrix?>("_matrix");
        private readonly SpriteBatch batch;
        private readonly SpriteSortMode sort;
        private readonly BlendState blend;
        private readonly SamplerState sampler;
        private readonly DepthStencilState depth;
        private readonly RasterizerState rasterizer;
        private readonly SpriteBatch.EffectWithParams shader;
        private readonly Matrix? transform;
        internal static bool Supported(SpriteBatch batch) {
            if (batch == null) { return true; }
            SpriteSortMode sort = Sort(batch); DepthStencilState state = Depth(batch);
            return (sort == SpriteSortMode.Deferred || sort == SpriteSortMode.Immediate) && (state == null || !state.DepthBufferEnable && !state.StencilEnable);
        }
        internal static Matrix MatrixOf(SpriteBatch batch) => batch == null ? Matrix.Identity : Transform(batch) ?? Matrix.Identity;
        internal SpriteBatchPause(SpriteBatch batch) {
            this.batch = batch;
            if (batch == null) { sort = default; blend = null; sampler = null; depth = null; rasterizer = null; shader = default; transform = null; return; }
            sort = Sort(batch); blend = Blend(batch); sampler = Sampler(batch); depth = Depth(batch); rasterizer = Rasterizer(batch); shader = Shader(batch); transform = Transform(batch);
            batch.End();
        }
        public void Dispose() {
            if (batch == null) { return; }
            batch.Begin(sort, blend, sampler, depth, rasterizer, shader.Effect, transform); batch.SwapEffect(shader);
        }
    }
}
