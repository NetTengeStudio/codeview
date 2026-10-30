using System;
using HarmonyLib;
using ScpItdMod.Core;

namespace ScpItdMod.People {
    public sealed class PeopleModule : IModule {
        public void Attach(Harmony harmony) {
#if SERVER
            PeopleHost.Attach(harmony);
#else
            PeopleClient.Attach();
            TryUi(() => Names.Attach(harmony), "character names");
            TryUi(() => Looks.Attach(harmony), "appearance overlays");
            TryUi(() => ProfilePanel.Attach(harmony), "character editor");
            TryUi(GmRoster.Attach, "GM roster");
#endif
        }

#if CLIENT
        private static void TryUi(Action attach, string feature) {
            try { attach(); }
            catch (Exception error) { ModuleHost.ReportFailure("people UI " + feature, error); }
        }
#endif

        public void RoundStart() {
#if SERVER
            PeopleHost.RoundStart();
#else
            PeopleClient.Reset();
            Names.Reset();
            Looks.Reset();
#endif
        }

        public void Release() {
#if SERVER
            PeopleHost.Release();
#else
            PeopleClient.Reset();
            Looks.Reset();
            ProfileLibraryWindow.Close();
            ProfilePanel.Release();
#endif
        }
    }
}
