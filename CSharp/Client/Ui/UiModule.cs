using HarmonyLib;
using ScpItdMod.Core;
using ScpItdMod.Cinematics;
using ScpItdMod.Hints;

namespace ScpItdMod.Ui {
    public sealed class UiModule : IModule, IRoundEnd {
        public void Attach(Harmony harmony) {
            RetroWindows.Attach(harmony);
            UiDiagnostics.Attach(harmony);
            FrameProfiler.Attach(harmony);
            Motion.MotionClient.Attach(harmony);
            Rendering.OffscreenPhase.Attach(harmony);
            NativeTooltipMotion.Attach(harmony);
            NativeWindowMotion.Attach(harmony);
            StatsPanel.Attach(harmony);
            ConsoleHints.Attach(harmony);
            Commands.ClientCommands.Attach(harmony);
            PatchGuard.Watch(harmony, HarmonyLib.AccessTools.Method(typeof(Barotrauma.GUIComponent), nameof(Barotrauma.GUIComponent.UpdateAuto)));
        }

        public void RoundStart() {
            UiDiagnostics.Stop();
            RetroWindows.Release();
            CinematicOverlay.Reset();
            NativeTooltipMotion.Reset();
            NativeWindowMotion.Reset();
            StatsPanel.Reset();
            ConsoleHints.Reset();
            Motion.MotionClient.Reset();
        }

        public void RoundEnd() => RoundStart();

        public void Release() {
            Motion.MotionBridge.Disconnect();
            UiDiagnostics.Stop();
            RetroWindows.Release();
            CinematicOverlay.Reset();
            NativeTooltipMotion.Reset();
            NativeWindowMotion.Reset();
            StatsPanel.Reset();
            ConsoleHints.Reset();
            Motion.MotionClient.Reset();
            Commands.ClientCommands.Reset();
            Faces.Release();
        }
    }
}
