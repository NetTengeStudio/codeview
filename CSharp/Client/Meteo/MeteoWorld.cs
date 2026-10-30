using System;
using ScpItdMod.Weather;

namespace ScpItdMod.Meteo {
    public static class MeteoWorld {
        public static Func<WeatherConfig> Config = () => null;
        public static Func<double, double> Day = t => 0;
        public static Func<double> Now = () => 0;
        public static Func<bool> Active = () => false;
    }
}
