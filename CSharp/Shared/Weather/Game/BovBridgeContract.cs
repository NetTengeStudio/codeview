using System;
using System.Collections.Generic;
using System.Reflection;

namespace ScpItdMod.Weather {
    internal static class BovBridgeContract {
        internal const int Version = 3;
        internal const string TypeName = "BlackOctoberVehicles.WeatherBridge";
        internal static readonly string[] Required = { "Publish", "Release", "PublishSnow", "PublishSnowDisturb", "PublishSnowSupport", "PublishSurface", "PublishWind", "RotorAirflowAt", "EnvironmentGain" };

        internal static Dictionary<string, MethodInfo> Resolve(Type bridge) {
            if (bridge == null) throw new ArgumentNullException(nameof(bridge));
            object found = bridge.GetField("Version")?.GetRawConstantValue();
            if (found is not int version || version != Version)
                throw new InvalidOperationException($"BOV weather bridge version {(found ?? "unknown")} does not match the version {Version} this mod was built for; enable the BOV copy that carries version {Version}");
            var members = new Dictionary<string, MethodInfo>(Required.Length);
            for (int i = 0; i < Required.Length; i++)
                members.Add(Required[i], bridge.GetMethod(Required[i], BindingFlags.Public | BindingFlags.Static) ?? throw new InvalidOperationException($"BOV weather bridge lacks {Required[i]}"));
            return members;
        }
    }
}
