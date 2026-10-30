using System;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using Barotrauma;
using HarmonyLib;
using ScpItdMod.Core;

namespace ScpItdMod.Menu {
    internal static class MenuRuntime {
        private const string HarmonyId = "scpitd.menu";
        private const double FallbackDelay = 1.5;

        private static Harmony harmony;
        private static Harmony watchdog;
        private static DebugConsole.Command host;
        private static bool persistent;
        private static bool local;
        private static bool installOffered;
        private static bool installCommand;
        private static Type bridgeType;
        private static MethodInfo bridgeRefresh;
        private static MethodInfo bridgeApplied;
        private static MethodInfo bridgeShutdown;
        private static double nextVerify;

        internal static bool Attached => local || persistent && BridgeApplied();

        public static void Start() {
            if (persistent || local) { return; }
            if (string.IsNullOrWhiteSpace(ModAssets.Root)) {
                ModLog.Error("main menu hook skipped because LuaCs did not resolve the enabled mod package");
                return;
            }

            string runtime = Path.Combine(ModAssets.Root, "Runtime", "Menu");
            try {
                // Prefer the bridge in the game directory: the injected bootstrap
                // loads that exact path. Loading a second copy from the mod folder
                // would create two independent Bridge statics in one process.
                string installedBridge = Path.Combine(AppContext.BaseDirectory, "ScpItd.MenuBridge.dll");
                string bridgePath = File.Exists(installedBridge)
                    ? installedBridge
                    : Path.Combine(runtime, "ScpItd.MenuBridge.dll");
                Assembly bridge = LoadDefault(bridgePath);
                bridgeType = bridge.GetType("ScpItdMod.MenuBridge.Bridge", true);
                bridgeRefresh = bridgeType.GetMethod("Refresh", BindingFlags.Public | BindingFlags.Static);
                bridgeApplied = bridgeType.GetProperty("Applied", BindingFlags.Public | BindingFlags.Static)?.GetMethod;
                bridgeShutdown = bridgeType.GetMethod("Shutdown", BindingFlags.Public | BindingFlags.Static);
                if (bridgeApplied == null || bridgeShutdown == null) {
                    throw new InvalidDataException("The installed menu bridge is obsolete; run Install-Menu.ps1 -Action Install with the game closed.");
                }
                MethodInfo activate = bridgeType.GetMethod("Activate", BindingFlags.Public | BindingFlags.Static);
                if (activate != null) {
                    activate.Invoke(null, new object[] { ModAssets.Root });
                } else if (bridgeRefresh != null) {
                    bridgeRefresh.Invoke(null, null);
                }
                persistent = true;
                // The bridge is persistent and owns the menu. The C# plugin only
                // verifies it; it never installs game files behind the player's back.
                AttachWatchdog();
                return;
            } catch (Exception error) {
                ModLog.Error("Persistent menu runtime unavailable: " + (error.InnerException ?? error));
                bridgeType = null; bridgeRefresh = null; bridgeApplied = null;
            }
            RegisterInstallCommand();
            OfferPersistentInstall();
            AttachLocal();
        }

        private static void RegisterInstallCommand() {
            if (installCommand) { return; }
            installCommand = true;
            try {
                ModCommands.Register("scpitd_menuinstall",
                    "client: install the persistent SCPITD main menu loader (writes Barotrauma.dll once, keeps a backup)",
                    _ => InstallNow());
            } catch (Exception error) { ModLog.Error("menu install command unavailable: " + error.Message); }
        }

        // The main menu exists before LuaCs loads C# plugins, so a permanently
        // visible custom menu needs the one-time bootstrap in Barotrauma.dll.
        // That file is only modified after an explicit confirmation; the
        // installer keeps a verified backup and supports a full uninstall.
        private static void OfferPersistentInstall() {
            if (installOffered || Prefs.Flag("menu.install.declined", false)) { return; }
            if (GameMain.NetLobbyScreen == null && GameMain.GameScreen == null) { return; }
            installOffered = true;
            try {
                var box = new GUIMessageBox("SCPITD: главное меню",
                    "Чтобы меню SCPITD появлялось при каждом запуске, один раз нужно добавить загрузчик в игровой файл Barotrauma.dll. " +
                    "Изменяется только клиент, сервер не затрагивается. Установщик сохранит проверенную резервную копию и контрольные суммы; " +
                    "удаление: Install-Menu.ps1 -Action Uninstall. Установить сейчас?",
                    new LocalizedString[] { "Установить", "Не сейчас" });
                box.Buttons[0].OnClicked = (_, _) => {
                    box.Close();
                    InstallNow();
                    return true;
                };
                box.Buttons[1].OnClicked = (_, _) => {
                    Prefs.Set("menu.install.declined", "1");
                    box.Close();
                    return true;
                };
            } catch (Exception error) {
                ModLog.Error("menu install prompt unavailable: " + error.Message);
            }
        }

        private static void InstallNow() {
            if (string.IsNullOrWhiteSpace(ModAssets.Root)) { ModLog.Error("menu package path is unavailable"); return; }
            try {
                // Mono.Cecil is not part of the game's dependency list. Load it
                // explicitly from the installation so the installer assembly can
                // resolve its IL dependency inside the running client.
                string cecil = Path.Combine(AppContext.BaseDirectory, "Mono.Cecil.dll");
                if (File.Exists(cecil)) { LoadDefault(cecil); }
                string installer = Path.Combine(ModAssets.Root, "Runtime", "Menu", "ScpItd.MenuInstaller.dll");
                Assembly assembly = LoadDefault(installer);
                Type type = assembly.GetType("ScpItdMod.MenuInstaller.PatchInstaller", true);
                MethodInfo install = type.GetMethod("InstallLoadedClient", BindingFlags.Public | BindingFlags.Static)
                    ?? throw new MissingMethodException(type.FullName, "InstallLoadedClient");
                string result = install.Invoke(null, new object[] { ModAssets.Root }) as string ?? "Menu installed.";
                ModLog.Message(result);
                // The in-mod fallback and the persistent bridge must never paint
                // two menus. Release the fallback, then let the freshly installed
                // bridge own the presentation for the rest of this session too.
                Stop();
                persistent = false;
                Start();
                if (!Attached) { AttachLocal(); }
            } catch (Exception error) {
                Exception failure = error is TargetInvocationException invocation && invocation.InnerException != null
                    ? invocation.InnerException : error;
                ModLog.Error("persistent menu installation failed: " + failure.Message);
                new GUIMessageBox("SCPITD: главное меню", "Не удалось установить постоянный загрузчик меню: " + failure.Message,
                    new LocalizedString[] { "Закрыть" });
            }
        }

        // Called by LuaCs after the main menu screen has been constructed. This is
        // the earliest reliable point where a bridge that was loaded during package
        // initialization can actually attach its presentation.
        public static void OnGameLoaded() {
            if (local) { return; }
            if (persistent) {
                RefreshBridge();
                if (BridgeApplied()) { return; }
                ModLog.Error("Persistent menu bridge did not report an applied menu after load; using the in-mod runtime.");
                AbandonBridge();
            }
            AttachLocal();
        }

        private static void AttachLocal() {
            if (local) { return; }
            Harmony candidate = new Harmony(HarmonyId + ".fallback");
            try {
                MainMenuView.Attach(candidate);
                harmony = candidate;
                local = true;
            } catch (Exception error) {
                candidate.UnpatchSelf();
                try { MainMenuView.Release(); }
                catch (Exception cleanup) { ModLog.Error("main menu rollback failed: " + cleanup); }
                ModLog.Error("main menu hook could not start: " + error);
            }
        }

        private static void AttachWatchdog() {
            if (watchdog != null) { return; }
            Harmony candidate = new Harmony(HarmonyId + ".watchdog");
            try {
                candidate.Patch(AccessTools.Method(typeof(MainMenuScreen), nameof(MainMenuScreen.Update)),
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(MenuRuntime), nameof(VerifyPostfix))));
                watchdog = candidate;
            } catch (Exception error) {
                candidate.UnpatchSelf();
                ModLog.Error("main menu verification hook is unavailable: " + error);
            }
        }

        private static void VerifyPostfix() {
            if (local || Screen.Selected is not MainMenuScreen) { return; }
            if (BridgeApplied()) { return; }
            double now = Timing.TotalTime;
            if (now < nextVerify) { return; }
            nextVerify = now + FallbackDelay;
            RefreshBridge();
            if (!BridgeApplied()) {
                ModLog.Error("Persistent menu bridge is loaded but has not attached a menu; falling back to the in-mod runtime.");
                watchdog?.UnpatchSelf();
                watchdog = null;
                AbandonBridge();
                AttachLocal();
            }
        }

        private static void AbandonBridge() {
            try { bridgeShutdown?.Invoke(null, null); }
            catch (Exception error) { ModLog.Error("Persistent menu shutdown failed: " + (error.InnerException ?? error)); }
            persistent = false;
        }

        private static void RefreshBridge() {
            try { bridgeRefresh?.Invoke(null, null); }
            catch (Exception error) { ModLog.Error("Persistent menu refresh failed: " + (error.InnerException ?? error)); }
        }

        private static bool BridgeApplied() {
            if (bridgeApplied == null) { return false; }
            try { return bridgeApplied.Invoke(null, null) is bool applied && applied; }
            catch { return false; }
        }

        private static Assembly LoadDefault(string path) {
            string name = Path.GetFileNameWithoutExtension(path);
            foreach (Assembly assembly in AssemblyLoadContext.Default.Assemblies) {
                if (assembly.GetName().Name == name) { return assembly; }
            }
            return AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(path));
        }

        public static void Stop() {
            // The installed bridge belongs to the process, not to a server plugin.
            // Only the in-mod fallback is owned by this plugin instance.
            if (!local) { return; }
            local = false;
            Exception failure = null;
            try { MainMenuView.Release(); }
            catch (Exception error) { failure = error; }
            try { harmony?.UnpatchSelf(); }
            catch (Exception error) { failure = failure == null ? error : new AggregateException(failure, error); }
            finally { harmony = null; }
            try { if (host != null) { DebugConsole.Commands.Remove(host); } }
            catch (Exception error) { failure = failure == null ? error : new AggregateException(failure, error); }
            finally { host = null; }
            if (failure != null) { ModLog.Error("main menu cleanup failed: " + failure); }
        }

        internal static void Fail(Exception error) {
            Stop();
            ModLog.Error("main menu initialization failed: " + error);
        }

        internal static HarmonyMethod Bind(Type type, string method) =>
            new HarmonyMethod(AccessTools.DeclaredMethod(type, method)
                ?? throw new MissingMethodException(type.FullName, method));

        internal static void RegisterHost(Action<string[]> action) {
            host = new DebugConsole.Command("createlobby", "opens the host server tab", action);
            DebugConsole.Commands.Add(host);
        }
    }
}
