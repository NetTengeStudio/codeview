using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using Barotrauma.Networking;
using Barotrauma;
using HarmonyLib;
using ScpItdMod.Core;

namespace ScpItdMod.Diagnostics {
    public sealed class EngineDiagnostics : IModule, IRoundEnd {
        private const string HarmonyId = "scpitd.optional.performance";
        private static readonly Dictionary<MethodBase, int> zones = new Dictionary<MethodBase, int>();
        private static readonly List<string> names = new List<string>(), missing = new List<string>();
        private static Harmony hooks;
#if CLIENT
        private static Harmony frameHooks;
#endif
        private static volatile ProfileBuffer buffer;
        private static int session, mainThread;
        private static long start;
        private static TimeSpan cpuStart;
        private static string scenario;
        private static long sentBytes, receivedBytes, sentMessages, receivedMessages;
        private static readonly int[] gcStart = new int[3];
        private static readonly List<object> marks = new List<object>();
        private static string Folder => Path.Combine(Environment.CurrentDirectory, "Docs", "SCPITD", "Performance", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N"));
        public void Attach(Harmony harmony) { mainThread = Environment.CurrentManagedThreadId; ModCommands.Register("scpitd_profile", "start [scenario] [seconds<=120]|stop|dump|frame|mark [event]|status", Request); }
        public void RoundStart() { }
        public void RoundEnd() { CancelFrame(); if (buffer != null) Stop(); }
        public void Release() { CancelFrame(); if (buffer != null) Stop(); }
        private static void Request(string[] args) {
            try {
                if (Environment.CurrentManagedThreadId != mainThread) throw new InvalidOperationException("Diagnostics commands require the game thread");
                string command = args.Length == 0 ? "status" : args[0];
                switch (command) {
                    case "start": Start(args.Length > 1 ? args[1] : "manual", args.Length > 2 ? double.Parse(args[2], CultureInfo.InvariantCulture) : 30); break;
                    case "stop": Stop(); break;
                    case "mark": if (buffer?.Capturing != true || marks.Count >= 256) throw new InvalidOperationException("Markers require an active capture and are limited to 256"); marks.Add(new { at = Stopwatch.GetTimestamp(), name = args.Length > 1 ? args[1] : "event" }); break;
                    case "dump": Dump(); break;
                    case "frame":
#if CLIENT
                        if (buffer?.Capturing == true || frameHooks != null) throw new InvalidOperationException("Finish the active profile or pending frame capture first");
                        if (Screen.Selected != GameMain.GameScreen) throw new InvalidOperationException("Frame capture requires the game screen");
                        frameHooks = new Harmony("scpitd.optional.frame");
                        try { frameHooks.Patch(AccessTools.Method(typeof(GameMain), "Draw"), postfix: new HarmonyMethod(typeof(EngineDiagnostics), nameof(CompletedFrame)) { priority = Priority.Last }); }
                        catch { CancelFrame(); throw; }
                        break;
#else
                        throw new InvalidOperationException("Frame capture is client-only");
#endif
                    case "status": ModLog.Message($"Profile capture={buffer?.Capturing == true}; zones={names.Count}; dropped={buffer?.Dropped ?? 0}; off-thread calls={buffer?.OtherThreads ?? 0}"); break;
                    default: throw new ArgumentException("start|stop|dump|mark|status");
                }
            } catch (Exception error) { ModLog.Error("Diagnostics: " + error); }
        }
        private static void Start(string label, double seconds) {
            if (!double.IsFinite(seconds) || seconds < 1 || seconds > 120) throw new ArgumentOutOfRangeException(nameof(seconds));
            if (buffer != null) Stop();
            names.Clear(); missing.Clear(); zones.Clear(); marks.Clear(); scenario = label;
            sentBytes = receivedBytes = sentMessages = receivedMessages = 0;
            hooks = new Harmony(HarmonyId);
            try {
                Target("Barotrauma.GameScreen", "Update");
                Target("Barotrauma.MapEntity", "UpdateAll");
                Target("Barotrauma.Items.Components.Powered", "UpdatePower");
                Target("FarseerPhysics.Dynamics.World", "Step");
#if SERVER
                Target("Barotrauma.Networking.GameServer", "Update");
#endif
#if CLIENT
                Target("Barotrauma.Networking.GameClient", "Update");
#endif
                Target("Barotrauma.EventManager", "Update");
                Target("ScpItdMod.Weather.WeatherService", "Step");
                Target("ScpItdMod.Weather.WeatherNetwork", "Tick");
                Target("ScpItdMod.Weather.WeatherMap", "Rebuild");
                Target("ScpItdMod.Weather.Material.SnowMpm", "Step");
                Target("ScpItdMod.Weather.SnowSupportBodies", "Update");
#if CLIENT
                Target("Barotrauma.GameMain", "Draw");
                Target("Barotrauma.GameScreen", "DrawMap");
                Target("Barotrauma.Lights.LightManager", "RenderLightMap");
                Target("Barotrauma.Lights.LightManager", "UpdateObstructVision");
                Target("Barotrauma.Lights.LightManager", "Update");
                Target("Barotrauma.GUI", "Draw");
                Target("ScpItdMod.Weather.WeatherPainter", "Prepare");
                Target("ScpItdMod.Weather.WeatherPainter", "DrawAtmosphere");
                Target("ScpItdMod.Weather.SnowCoverRenderer", "Draw");
                Target("ScpItdMod.Weather.WeatherParticles", "Step");
                Target("ScpItdMod.Weather.WeatherAirflow", "Step");
                Target("ScpItdMod.Weather.SkyLightRenderer", "Prepare");
                Target("ScpItdMod.Weather.SkyLightRenderer", "Composite");
#endif
                Traffic("Barotrauma.Networking.LidgrenServerPeer", "Send", nameof(Sent));
                Traffic("Barotrauma.Networking.P2PServerPeer", "Send", nameof(Sent));
                Traffic("Barotrauma.Networking.LidgrenClientPeer", "Send", nameof(Sent));
                Traffic("Barotrauma.Networking.P2PClientPeer", "Send", nameof(Sent));
                Traffic("Barotrauma.Networking.P2POwnerPeer", "Send", nameof(Sent));
#if CLIENT
                Target("Barotrauma.Networking.GameClient", "ReadDataMessage");
                Traffic("Barotrauma.Networking.GameClient", "ReadDataMessage", nameof(ReceivedClient));
#endif
#if SERVER
                Target("Barotrauma.Networking.GameServer", "ReadDataMessage");
                Traffic("Barotrauma.Networking.GameServer", "ReadDataMessage", nameof(ReceivedServer));
#endif
                start = Stopwatch.GetTimestamp(); using var process = Process.GetCurrentProcess(); cpuStart = process.TotalProcessorTime;
                buffer = new ProfileBuffer(262144, seconds, ++session);
                for (int i = 0; i < 3; i++) gcStart[i] = GC.CollectionCount(i);
                ModLog.Message($"Profile armed: {names.Count} methods, {seconds:F0}s; run scpitd_profile stop to write files. Missing methods are recorded.");
            } catch { hooks.UnpatchAll(HarmonyId); hooks = null; buffer = null; throw; }
        }
        private static void Target(string typeName, string methodName) {
            Type type = AccessTools.TypeByName(typeName); bool found = false;
            if (type != null) foreach (MethodInfo method in AccessTools.GetDeclaredMethods(type)) {
                if (method.Name != methodName || method.ContainsGenericParameters || method.IsAbstract || method.GetMethodBody() == null) continue;
                found = true; zones.Add(method, names.Count); names.Add(typeName + "." + method);
                hooks.Patch(method, prefix: new HarmonyMethod(typeof(EngineDiagnostics), nameof(Before)), finalizer: new HarmonyMethod(typeof(EngineDiagnostics), nameof(After)));
            }
            if (!found) missing.Add(typeName + "." + methodName);
        }
        private static void Traffic(string typeName, string methodName, string prefix) {
            Type type = AccessTools.TypeByName(typeName);
            if (type == null) return;
            foreach (MethodInfo method in AccessTools.GetDeclaredMethods(type)) if (method.Name == methodName && !method.IsAbstract && method.GetMethodBody() != null)
                hooks.Patch(method, prefix: new HarmonyMethod(typeof(EngineDiagnostics), prefix));
        }
        private static void Sent(IWriteMessage __0) { if (buffer?.Capturing == true) { Interlocked.Add(ref sentBytes, __0.LengthBytes); Interlocked.Increment(ref sentMessages); } }
        private static void ReceivedClient(IReadMessage __0) => Received(__0);
        private static void ReceivedServer(IReadMessage __1) => Received(__1);
        private static void Received(IReadMessage message) { if (buffer?.Capturing == true) { Interlocked.Add(ref receivedBytes, message.LengthBytes); Interlocked.Increment(ref receivedMessages); } }
        private static void Before(MethodBase __originalMethod, out ProfileScope __state) => __state = buffer != null && zones.TryGetValue(__originalMethod, out int zone) ? buffer.Begin(zone) : default;
        private static Exception After(ProfileScope __state, Exception __exception) { buffer?.End(__state); return __exception; }
        private static JsonSerializerOptions Options => new JsonSerializerOptions { IncludeFields = true, WriteIndented = true };
        private static void Stop() {
            ProfileBuffer capture = buffer;
            if (capture == null) return;
            capture.Stop();
            hooks?.UnpatchAll(HarmonyId); hooks = null;
            string folder = Folder; Directory.CreateDirectory(folder);
            using var process = Process.GetCurrentProcess();
            File.WriteAllText(Path.Combine(folder, "capture.json"), JsonSerializer.Serialize(new { scenario, start, end = Stopwatch.GetTimestamp(), frequency = Stopwatch.Frequency, processCpuMilliseconds = (process.TotalProcessorTime - cpuStart).TotalMilliseconds, gcCollections = new[] { GC.CollectionCount(0) - gcStart[0], GC.CollectionCount(1) - gcStart[1], GC.CollectionCount(2) - gcStart[2] }, applicationTraffic = new { attemptedSendBytes = Interlocked.Read(ref sentBytes), receivedBytes = Interlocked.Read(ref receivedBytes), attemptedMessages = Interlocked.Read(ref sentMessages), receivedMessages = Interlocked.Read(ref receivedMessages), limits = "Logical precompression messages; excludes transport headers, retransmission and handshake traffic" }, workingSet = process.WorkingSet64, managedBytes = GC.GetTotalMemory(false), dropped = capture.Dropped, offThreadCallsExcluded = capture.OtherThreads, missing, zones = names, marks, records = capture.Records.ToArray(), limits = "Main-thread inclusive CPU spans; elapsed call intervals are not GPU execution time; nested allocation counts must not be summed" }, Options));
            buffer = null;
            ModLog.Message("Profile saved: " + folder);
            Snapshot(folder);
        }
        private static void CancelFrame() {
#if CLIENT
            frameHooks?.UnpatchAll("scpitd.optional.frame"); frameHooks = null;
#endif
        }
#if CLIENT
        private static void CompletedFrame() {
            CancelFrame();
            try {
                string folder = Folder; Directory.CreateDirectory(folder);
                LightDiagnostics.Capture(folder);
                Snapshot(folder, true);
                ModLog.Message("Game frame and matching state saved: " + folder);
            } catch (Exception error) { ModLog.Error("Frame capture: " + error); }
        }
#endif
        private static void Dump() { string folder = Folder; Directory.CreateDirectory(folder); Snapshot(folder); ModLog.Message("Diagnostics saved: " + folder); }
        private static void Snapshot(string folder, bool gameFrame = false) {
            var assemblies = new List<object>();
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies()) if (!assembly.IsDynamic) assemblies.Add(new { name = assembly.GetName().Name, version = assembly.GetName().Version?.ToString(), mvid = assembly.ManifestModule.ModuleVersionId });
            var patches = new List<object>();
            foreach (MethodBase method in Harmony.GetAllPatchedMethods()) {
                Patches info = Harmony.GetPatchInfo(method);
                if (info != null) patches.Add(new { method = method.DeclaringType?.FullName + "." + method, owners = info.Owners });
            }
            File.WriteAllText(Path.Combine(folder, "environment.json"), JsonSerializer.Serialize(new { kind = gameFrame ? "game-backbuffer" : "diagnostic-snapshot", runtime = Environment.Version.ToString(), processors = Environment.ProcessorCount, round = GameMain.GameSession?.RoundDuration, weather = Weather.WeatherService.Active, epoch = Weather.WeatherService.Epoch, time = Weather.WeatherService.Clock.Seconds, frozen = Weather.WeatherService.Clock.Frozen, state = Weather.WeatherService.Current, seed = Weather.WeatherService.Director?.Seed, fingerprint = Weather.WeatherMap.Fingerprint, assemblies, patches }, Options));
            GeometryDiagnostics.Write(folder);
#if CLIENT
            LightDiagnostics.Write(folder);
#endif
        }
    }
}
