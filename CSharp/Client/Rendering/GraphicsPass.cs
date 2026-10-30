using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ScpItdMod.Rendering {
    public readonly struct GraphicsPass : IDisposable {
        private sealed class State {
            internal GraphicsDevice Device;
            internal readonly RenderTargetBinding[][] ByCount = new RenderTargetBinding[9][];
            internal RenderTargetBinding[] Targets = Array.Empty<RenderTargetBinding>();
            internal Viewport Viewport;
            internal Rectangle Scissor;
            internal BlendState Blend;
            internal DepthStencilState Depth;
            internal RasterizerState Rasterizer;
            internal SamplerState Sampler;
            internal Texture Texture;
            internal long Generation;
        }
        private static readonly State[] stack = MakeStack();
        private static int depth;
        private readonly int slot;
        private readonly long generation;
        private static State[] MakeStack() { var states = new State[16]; for (int i = 0; i < states.Length; i++) { states[i] = new State(); } return states; }
        private GraphicsPass(int slot, long generation) { this.slot = slot; this.generation = generation; }
        public static GraphicsPass Capture(GraphicsDevice device) {
            if (device == null) { throw new ArgumentNullException(nameof(device)); }
            if (depth == stack.Length) { throw new InvalidOperationException("GPU pass nesting limit exceeded."); }
            State state = stack[depth]; int count = device.RenderTargetCount;
            if (count >= state.ByCount.Length) { throw new InvalidOperationException("Too many render targets to capture."); }
            state.Targets = state.ByCount[count] ??= new RenderTargetBinding[count];
            device.GetRenderTargets(state.Targets); state.Device = device; state.Viewport = device.Viewport; state.Scissor = device.ScissorRectangle;
            state.Blend = device.BlendState; state.Depth = device.DepthStencilState; state.Rasterizer = device.RasterizerState;
            state.Sampler = device.SamplerStates[0]; state.Texture = device.Textures[0]; state.Generation++;
            return new GraphicsPass(depth++, state.Generation);
        }
        public void Dispose() {
            if (depth != slot + 1 || stack[slot].Generation != generation) { throw new InvalidOperationException("GPU passes must be restored exactly once, in nesting order."); }
            State state = stack[slot]; GraphicsDevice device = state.Device;
            try {
                device.Textures[0] = null; device.SetRenderTargets(state.Targets); device.Viewport = state.Viewport; device.ScissorRectangle = state.Scissor;
                device.BlendState = state.Blend; device.DepthStencilState = state.Depth; device.RasterizerState = state.Rasterizer;
                device.SamplerStates[0] = state.Sampler; device.Textures[0] = state.Texture;
            } finally { Array.Clear(state.Targets); state.Targets = Array.Empty<RenderTargetBinding>(); state.Device = null; state.Texture = null; state.Blend = null; state.Depth = null; state.Rasterizer = null; state.Sampler = null; depth--; }
        }
    }
}
