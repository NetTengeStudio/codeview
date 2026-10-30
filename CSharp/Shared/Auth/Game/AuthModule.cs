using Barotrauma.Items.Components;
using HarmonyLib;
using ScpItdMod.Core;

namespace ScpItdMod.Auth {
    public sealed class AuthModule : IModule {
        public void Attach(Harmony harmony) {
            AuthWorld.Load();
            harmony.Patch(AccessTools.Method(typeof(WifiComponent), nameof(WifiComponent.CanReceive)),
                postfix: ModuleHost.Bind(typeof(AuthModule), nameof(CanReceivePostfix)));
#if SERVER
            SwipeHost.Attach(harmony);
#else
            RadioUi.Attach(harmony);
            SwipeView.Attach(harmony);
#endif
        }

        public void RoundStart() {
            AuthWorld.Clear();
#if SERVER
            SwipeHost.RoundStart();
#else
            RadioUi.Reset();
            SwipeView.Reset();
#endif
        }

        public void Release() {
            AuthWorld.Clear();
#if SERVER
            SwipeHost.Release();
#else
            RadioUi.Reset();
            SwipeView.Reset();
#endif
        }

        public static void CanReceivePostfix(WifiComponent __instance, WifiComponent sender, ref bool __result) {
            if (!__result || AuthWorld.Plan.Count == 0) { return; }
            if (!AuthWorld.RadioAllowed(sender, sender.Channel) || !AuthWorld.RadioAllowed(__instance, __instance.Channel)) { __result = false; }
        }
    }
}
