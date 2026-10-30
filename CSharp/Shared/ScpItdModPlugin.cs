using Barotrauma.LuaCs;
using ScpItdMod.Auth;
using ScpItdMod.Blink;
using ScpItdMod.Core;
using ScpItdMod.Docs;
using ScpItdMod.Inv;
using ScpItdMod.Life;
using ScpItdMod.Med;
using ScpItdMod.Paper;
using ScpItdMod.People;
using ScpItdMod.Rec;
using ScpItdMod.Rp;
using ScpItdMod.Stamina;
using ScpItdMod.Pc;
#if CLIENT
using ScpItdMod.Menu;
using ScpItdMod.Ambient;
using ScpItdMod.Footsteps;
using ScpItdMod.Hud;
#endif

namespace ScpItdMod {
    public sealed class ScpItdModPlugin : IAssemblyPlugin {
        private const string HarmonyId = "scpitd";

        public IPluginManagementService PluginManagementService { get; set; }

        public IConsoleCommandsService ConsoleCommandsService { get; set; }

        public ILoggerService LoggerService { get; set; }

        public void Initialize() {
            ModLog.Mount(LoggerService);
            ModAssets.Mount<ScpItdModPlugin>(PluginManagementService);
#if CLIENT
            MenuRuntime.Start();
#endif
            ModCommands.Mount(ConsoleCommandsService);
            ModNet.Mount();
            ModuleHost.Attach(HarmonyId, new Maps.MapModule(), new VanillaHudModule(),
#if CLIENT
                new Ui.UiModule(), new InvModule(),
#endif
                new InvCheck(),
#if CLIENT
                new HudModule(),
#endif
                new PeopleModule(), new EmoteModule(), new RpModule(), new AuthModule(), new MedModule(), new LifeModule(),
#if CLIENT
                new VanillaAudioModule(),
#endif
                new CharacterControlModule(), new StaminaModule(), new BlinkModule(),
#if CLIENT
                new FootstepsModule(), new AmbientModule(), new Fire.BarrelFireModule(),
#endif
                new PaperModule(), new PcModule(), new Pool.PoolModule(), new Bath.BathModule(), new RecModule(), new DocNet(), new Diagnostics.EngineDiagnostics(), new Weather.WeatherModule(), new Thermal.ThermalModule(), new Meteo.MeteoModule(), new Cinematics.CinematicModule());
            ModCommands.Register("scpitd_status", "reports loaded modules and missing features", _ => {
                ModLog.Message("SCPITD attached modules: " + ModuleHost.AttachedCount);
#if CLIENT
                ModLog.Message("SCPITD menu hook: " + (MenuRuntime.Attached ? "attached" : "MISSING"));
#endif
                foreach (string failure in ModuleHost.Failures) { ModLog.Error("MISSING: " + failure); }
            });
            if (ModuleHost.Failures.Count > 0) { ModLog.Error("SCPITD is partially loaded: " + string.Join("; ", ModuleHost.Failures)); }
        }

        public void OnLoadCompleted() {
#if CLIENT
            // LuaCs instantiates assembly plugins before GameMain creates the main
            // menu and calls this hook once the menu screen exists. Re-verifying
            // here is what turns a successfully loaded bridge into a visible menu
            // without depending on a DLL patch or on joining a server.
            MenuRuntime.OnGameLoaded();
#endif
        }

        public void PreInitPatching() {}

        public void Dispose() {
#if CLIENT
            MenuRuntime.Stop();
#endif
            ModNet.Release();
            ModuleHost.Release();
            Watchers.Clear();
            ModCommands.Release();
            ModAssets.Release();
#if CLIENT
            HudDock.Clear();
            TextureStore.Release();
            SoundStore.Release();
            FontStore.Release();
#endif
            ModLog.Release();
        }
    }
}
