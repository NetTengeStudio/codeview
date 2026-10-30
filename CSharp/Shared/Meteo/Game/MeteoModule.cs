using HarmonyLib;
using ScpItdMod.Core;
using ScpItdMod.Weather;

namespace ScpItdMod.Meteo {
    public sealed class MeteoModule : IModule, IRoundEnd {
        public void Attach(Harmony harmony) {
#if SERVER
            MeteoHost.Attach();
            WeatherService.Updated += MeteoHost.Step;
#else
            MeteoClient.Attach(harmony);
#endif
        }

        public void RoundStart() {
#if SERVER
            MeteoHost.RoundStart();
#else
            MeteoClient.Reset();
#endif
        }

        public void RoundEnd() => RoundStart();

        public void Release() {
#if SERVER
            WeatherService.Updated -= MeteoHost.Step;
            MeteoHost.Clear(); ServerRacks.Clear();
#else
            MeteoClient.Release();
#endif
        }
    }
}
