using System;
using System.Globalization;
using Barotrauma;
using ScpItdMod.Core;

namespace ScpItdMod.Weather {
    internal static class WeatherCommands {
        private static readonly CultureInfo culture = CultureInfo.InvariantCulture;
        internal static void Request(string[] args) {
            try {
#if CLIENT
                if (!WeatherService.Authority) { WeatherNetwork.Command(args); return; }
#endif
                ModLog.Message(Execute(args));
            } catch (Exception error) { ModLog.Error("Weather: " + error.Message); }
        }
        internal static string Execute(string[] args) {
            if (args == null || args.Length == 0) throw new ArgumentException("weather set|wind|temp|time|season|timescale|snowdepth|freeze|forecast|dump|quality|dig|hulls");
            if (!WeatherService.Active) throw new InvalidOperationException("Weather is enabled by the mode configuration only on " + WeatherService.Config.Map);
            float seconds = WeatherService.Config.BlendSeconds;
            switch (args[0].ToLowerInvariant()) {
                case "set":
                    Arity(args, 2, 3); if (!TryRegime(args[1], out WeatherRegime regime)) throw new ArgumentException("Clear|Snowfall|Drifting|Blizzard|Storm|Recovery");
                    WeatherService.SetRegime(regime, args.Length == 3 ? Number(args[2], 0, 3600) : seconds); break;
                case "wind": Arity(args, 3, 3); WeatherService.Set(WeatherControlKind.Wind, Number(args[1], 0, 60), Number(args[2], 0, 360) * MathF.PI / 180, seconds); break;
                case "temp":
                    Arity(args, 1, 2); if (args.Length == 1) return WeatherService.Current.Temperature.ToString("F1", culture) + " C";
                    WeatherService.Set(WeatherControlKind.Temperature, Number(args[1], -80, -15), seconds: seconds); break;
                case "time":
                    Arity(args, 2, 3); string[] hm = args[1].Split(':');
                    float hour = hm.Length == 2 ? Number(hm[0], 0, 23) + Number(hm[1], 0, 59) / 60 : Number(args[1], 0, 23.999f);
                    WeatherService.Set(WeatherControlKind.Time, hour, seconds: args.Length == 3 ? Number(args[2], 0, 3600) : seconds); break;
                case "season":
                    Arity(args, 2, 2); PolarSeason season = args[1] switch { "polar_day" => PolarSeason.PolarDay, "polar_night" => PolarSeason.PolarNight, "equinox" => PolarSeason.Equinox, _ => throw new ArgumentException("polar_day|polar_night|equinox") };
                    WeatherService.Set(WeatherControlKind.Season, (float)season, seconds: seconds); break;
                case "timescale": Arity(args, 2, 2); WeatherService.Set(WeatherControlKind.TimeScale, Number(args[1], 1, 10080), seconds: 0); break;
                case "snowdepth": Arity(args, 2, 2); SnowRuntime.Climate.SetDepth(Number(args[1], 0, WeatherService.Config.MaximumDepth)); WeatherNetwork.SnowChanged(); break;
                case "cold":
                    if (args.Length == 2 && string.Equals(args[1], "status", StringComparison.OrdinalIgnoreCase)) return Thermal.ThermalRuntime.Diagnostics();
                    if (args.Length == 3 && string.Equals(args[1], "trace", StringComparison.OrdinalIgnoreCase)) return args[2].ToLowerInvariant() switch {
                        "start" => Thermal.ThermalRuntime.CaptureStart(),
                        "stop" => Thermal.ThermalRuntime.CaptureStop(),
                        _ => throw new ArgumentException("cold trace start|stop")
                    };
                    Arity(args, 2, 2);
                    bool cold = args[1].ToLowerInvariant() switch {
                        "on" or "true" or "1" => true,
                        "off" or "false" or "0" => false,
                        "toggle" => !Thermal.ThermalRuntime.Enabled,
                        _ => throw new ArgumentException("cold on|off|toggle")
                    };
                    Thermal.ThermalRuntime.SetEnabled(cold);
                    return cold ? "Холод включён." : "Холод выключен. Накопленные холодовые эффекты сняты.";
                case "freeze": Arity(args, 1, 2); WeatherService.Freeze(args.Length == 1 ? !WeatherService.Clock.Frozen : Boolean(args[1])); break;
                case "forecast": {
                    Arity(args, 1, 2); int hours = args.Length == 1 ? 6 : (int)Number(args[1], 1, 48);
                    var forecast = new System.Text.StringBuilder(); double now = WeatherService.Clock.Seconds, day = WeatherService.Director.Day(now), rate = WeatherService.Director.DayRate(now);
                    for (int i = 1; i <= hours; i++) {
                        double t = now + i / (24 * rate); WeatherState s = WeatherService.Director.Evaluate(t);
                        forecast.AppendFormat(culture, "+{0}h {1} {2:F0} C, {3:F1} m/s, {4:F0} m\n", i, s.Regime, s.Temperature, s.WindSpeed, s.Visibility);
                    }
                    return forecast.ToString();
                }
                case "dump": Arity(args, 1, 1); return Dump();
                case "quality":
#if CLIENT
                    Arity(args, 2, 2); WeatherClient.Quality = Number(args[1], .1f, 1); return "Weather local quality=" + WeatherClient.Quality.ToString("F2", culture);
#else
                    throw new ArgumentException("quality is a client setting");
#endif
                case "dig":
#if CLIENT
                    Arity(args, 1, 1); if (Character.Controlled != null) WeatherService.DisturbSnow(Character.Controlled.WorldPosition, 2, .2f, true); break;
#else
                    throw new ArgumentException("Use a weather shovel, or the public DisturbSnow API at a world position");
#endif
                case "hulls":
#if CLIENT
                    Arity(args, 1, 1); return Hulls();
#else
                    throw new ArgumentException("hulls is a client diagnostic");
#endif
                default: throw new ArgumentException("Unknown weather command");
            }
            return Dump();
        }
        internal static string Dump() {
            WeatherState s = WeatherService.Current;
            return string.Format(culture, "{0} seed={1} epoch={2} revision={3} t={4:F2}s day={5:F4} frozen={6}\nT={7:F2} C wind={8:F2} m/s bearing={9:F1} gust={10:F2} snowfall={11:F3} drifting={12:F3}\nvisibility={13:F1}m cloud={14:F3} sky={15:F2}lux humidity={16:F3} frost={17:F3} pressure={18:F1}hPa warning={19:F2} flight={20} work={21} cells={22}",
                s.Regime, WeatherService.Director.Seed, WeatherService.Epoch, WeatherService.Director.Revision, WeatherService.Clock.Seconds, WeatherService.Director.Day(WeatherService.Clock.Seconds), WeatherService.Clock.Frozen,
                s.Temperature, s.WindSpeed, s.WindDirection * 180 / MathF.PI, s.Gustiness, s.Snowfall, s.Drifting, s.Visibility, s.Cloud, s.SkyIlluminance, s.Humidity, s.Frost, s.Pressure, s.Warning, WeatherService.FlightAllowed, WeatherService.OutsideWorkAllowed, WeatherService.Grid?.Cells ?? 0);
        }
#if CLIENT
        private static string Hulls() {
            var text = new System.Text.StringBuilder("Authored hull ambient is unchanged. Weather adds spatial sky light after silhouettes and lamps; the centre is only one sample, not the whole hull. Global ambient=" + GameMain.LightManager.AmbientLight + "\n");
            Microsoft.Xna.Framework.Rectangle seen = GameMain.GameScreen.Cam.WorldView;
            ScpItdMod.Weather.Material.SnowPack snow = SnowRuntime.Pack; WeatherGrid grid = WeatherService.Grid;
            foreach (Hull hull in Hull.HullList) {
                if (hull.Removed) continue;
                Microsoft.Xna.Framework.Rectangle r = hull.WorldRect; bool visible = r.Left < seen.Right && seen.Left < r.Right && r.Bottom < seen.Y && seen.Y - seen.Height < r.Y;
                float left = r.X / 100f, right = (r.X + r.Width) / 100f, top = r.Y / 100f, bottom = (r.Y - r.Height) / 100f;
                int slots = 0, snowy = 0, open = 0; float deepest = 0;
                if (snow != null) for (int i = 0; i < grid.SurfaceCount; i++) {
                    Vec2 at = grid.Position(grid.SurfaceCell(i));
                    if (at.X < left || at.X > right || at.Y < bottom || at.Y > top) continue;
                    slots++; if (grid.OpenSky(grid.SurfaceCell(i))) open++;
                    float depth = snow.Depth(i); if (depth > .03f) { snowy++; deepest = MathF.Max(deepest, depth); }
                }
                float sky = grid?.LightExposureAt((r.X + r.Width * .5f) / 100, (r.Y - r.Height * .5f) / 100) ?? 0;
                LightColor light = WeatherLighting.Skylight(WeatherService.OutdoorLight, sky);
                text.AppendFormat(culture, "{0} hull {1} rect {2},{3},{4},{5} authored-ambient={6} sky(centre)={7:F3} floor cells {8} open-sky {9} snowy {10} deepest {11:F2} m weather-rgb(centre)={12:F3},{13:F3},{14:F3}\n", visible ? "VISIBLE" : "hidden ", hull.ID, r.X, r.Y, r.Width, r.Height, hull.AmbientLight,
                    sky, slots, open, snowy, deepest, light.R, light.G, light.B);
            }
            return text.ToString();
        }
#endif
        internal static bool TryRegime(string text, out WeatherRegime regime) => Enum.TryParse(text, true, out regime) && (byte)regime < 6;
        private static bool Boolean(string value) => value switch { "true" or "1" => true, "false" or "0" => false, _ => throw new ArgumentException("true|false") };
        private static float Number(string text, float min, float max) {
            if (!float.TryParse(text, NumberStyles.Float, culture, out float x) || !float.IsFinite(x) || x < min || x > max) throw new ArgumentException("Value must be in [" + min + "," + max + "]"); return x;
        }
        private static void Arity(string[] args, int min, int max) { if (args.Length < min || args.Length > max) throw new ArgumentException("Wrong argument count for weather " + args[0]); }
    }
}
