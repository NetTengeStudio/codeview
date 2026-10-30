using System;
using System.Globalization;
using System.Text;
using ScpItdMod.Weather;

namespace ScpItdMod.Meteo {
    public static class MeteoText {
        private static readonly string[] Regimes = { "ясно", "снегопад", "позёмок", "метель", "буран", "прояснение" };
        private static readonly string[] Trends = { "быстро растёт", "растёт", "без изменений", "падает", "быстро падает" };
        private static readonly string[] Alerts = { "без предупреждений", "наблюдение", "предупреждение", "штормовое предупреждение" };
        // ru-RU недоступна при InvariantGlobalization (сервер Barotrauma), поэтому строим формат вручную.
        private static readonly CultureInfo Ru = BuildRu();

        private static CultureInfo BuildRu() {
            var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
            var numbers = (NumberFormatInfo)culture.NumberFormat.Clone();
            numbers.NumberDecimalSeparator = ",";
            culture.NumberFormat = numbers;
            return culture;
        }

        public static string Regime(WeatherRegime r) => Regimes[(int)r];

        public static string Trend(MeteoTrend t) => Trends[(int)t];

        public static string Alert(MeteoAlert a) => Alerts[(int)a];

        public static string Clock(double day) {
            double f = day - Math.Floor(day);
            int minutes = (int)(f * 1440) % 1440;
            return (minutes / 60).ToString("00", Ru) + ":" + (minutes % 60).ToString("00", Ru);
        }

        public static string Duration(float seconds) {
            if (float.IsInfinity(seconds) || seconds < 0) return "-";
            int s = (int)MathF.Round(seconds);
            return s < 90 ? s + " с" : s < 5400 ? (s / 60) + " мин " + (s % 60).ToString("00", Ru) + " с" : (s / 3600) + " ч " + (s % 3600 / 60) + " мин";
        }

        public static string Number(float v, string format = "0.0") => v.ToString(format, Ru);

        public static string Signed(float v, string format = "0.0") => (v > 0 ? "+" : v < 0 ? "-" : "") + MathF.Abs(v).ToString(format, Ru);

        public static string Summary(in MeteoForecast f) {
            if (!f.Valid) return "Данных для прогноза недостаточно: нужен непрерывный ряд наблюдений не менее 1-2 минут.";
            var b = new StringBuilder();
            if (f.Precursor) {
                b.Append("Давление падает вне суточного хода - признак скорой смены погоды. Ожидается: ").Append(Regime(f.Likely)).Append(" через ")
                 .Append(Duration(f.EtaLow)).Append("-").Append(Duration(f.EtaHigh)).Append('.');
            } else {
                b.Append("Сейчас: ").Append(Regime(f.Observed)).Append(", держится ").Append(Duration(f.Age)).Append(f.AgeExact ? "" : " (не меньше - запись неполная)").Append(". ");
                b.Append("Смена режима вероятна через ").Append(Duration(f.EtaLow)).Append("-").Append(Duration(f.EtaHigh)).Append("; вероятнее всего - ").Append(Regime(f.Likely))
                 .Append(" (").Append(Number(f.Probability[(int)f.Likely] * 100, "0")).Append("%).");
            }
            return b.ToString();
        }

        public static string Bulletin(int number, double day, string station, in MeteoSample s, in MeteoForecast f, MeteoOverview o) {
            var b = new StringBuilder(1400);
            b.Append("МЕТЕОРОЛОГИЧЕСКАЯ СЛУЖБА СТАНЦИИ «ВОСТОК»\n");
            b.Append("Синоптический бюллетень № ").Append(number).Append('\n');
            b.Append("Срок: ").Append(Clock(day)).Append(" станционного времени\n");
            b.Append("Источник: ").Append(station).Append("\n\n");
            b.Append("1. ФАКТИЧЕСКАЯ ПОГОДА\n");
            b.Append("  Температура воздуха   ").Append(Signed(s.Temperature)).Append(" °C\n");
            float chill = s.WindValid ? s.WindChill : s.Temperature;
            b.Append("  Ощущаемая             ").Append(Signed(chill, "0")).Append(" °C");
            float minutes = MeteoPhysics.FrostbiteMinutes(chill);
            if (!float.IsInfinity(minutes)) b.Append(", обморожение кожи за ").Append(Number(MathF.Round(minutes), "0")).Append(" мин");
            b.Append('\n');
            if (s.WindValid) b.Append("  Ветер                 ").Append(MeteoPhysics.Compass(s.Direction)).Append(' ').Append(Number(s.Wind)).Append(" м/с, ").Append(MeteoPhysics.BeaufortName(s.Wind)).Append('\n');
            else b.Append("  Ветер                 нет данных (датчик обледенел)\n");
            b.Append("  Давление              ").Append(Number(s.Pressure)).Append(" гПа");
            if (f.Valid) b.Append(", ").Append(Trend(f.Trend)).Append(" (").Append(Signed(f.PressureSlope)).Append(" гПа/мин)");
            b.Append('\n');
            b.Append("  Видимость             ").Append(s.Visibility >= 1000 ? Number(s.Visibility / 1000, "0.#") + " км" : Number(s.Visibility, "0") + " м").Append('\n');
            b.Append("  Точка инея            ").Append(s.HumidityValid ? Signed(s.DewPoint) + " °C" : "гигрометр не работает при t ниже -40 °C").Append('\n');
            b.Append("  Снежный покров        ").Append(Number(s.Snow, "0.00")).Append(" м\n");
            b.Append("  Обледенение датчиков  ").Append(Number(s.Ice * 100, "0")).Append(" %\n\n");
            b.Append("2. ПРОГНОЗ\n  ").Append(Summary(f).Replace("\n", " ")).Append('\n');
            if (f.Valid) b.Append("  Уверенность ").Append(Number(f.Confidence * 100, "0")).Append(" %, оповещение: ").Append(Alert(f.Alert)).Append("\n");
            b.Append("\n3. ПРИМЕЧАНИЯ\n");
            b.Append("  Архив сервера: ").Append(o.Used).Append('/').Append(o.Capacity).Append(" блоков");
            if (o.Overwritten > 0) b.Append(", перезаписано ").Append(o.Overwritten);
            b.Append('\n');
            return b.ToString();
        }
    }
}
