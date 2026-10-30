using System;
using System.IO;
using Microsoft.Xna.Framework.Graphics;
using ScpItdMod.Core;

namespace ScpItdMod.Rendering {
    // Owns the window fade effect: one instance per device, loaded from the compiled shader for the running platform.
    // A failed load is remembered, so the windows fall back to an unfaded draw instead of retrying every frame.
    public static class WindowFade {
        private static Effect effect;
        private static EffectParameter opacity;
        private static bool unavailable;

        public static Effect Get(GraphicsDevice device) {
            if (effect != null && ReferenceEquals(effect.GraphicsDevice, device) && !effect.IsDisposed) { return effect; }
            if (unavailable) { return null; }
            string backend = OperatingSystem.IsWindows() ? "dx11" : "opengl";
            string path = ModAssets.Resolve("Shaders/windowfade_" + backend + ".mgfxo");
            try {
                effect?.Dispose();
                effect = new Effect(device, File.ReadAllBytes(path));
                opacity = effect.Parameters["Opacity"] ?? throw new InvalidDataException("Window fade parameter absent: Opacity");
                return effect;
            } catch (Exception error) when (error is IOException || error is InvalidDataException || error is InvalidOperationException || error is ArgumentException || error is NotSupportedException) {
                unavailable = true; effect = null;
                ModLog.Error("Window fade shader unavailable: " + path + ": " + error.Message);
                return null;
            }
        }

        public static void SetOpacity(float value) => opacity.SetValue(Math.Clamp(value, 0f, 1f));

        public static void Release() { effect?.Dispose(); effect = null; opacity = null; unavailable = false; }
    }
}
