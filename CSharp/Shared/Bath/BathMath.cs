using System;

namespace ScpItdMod.Bath {
    // Вода в купели: объём и температура. Чистая физика без типов игры.
    // Константы — стандартные значения воды (теплоёмкость, теплота испарения) и потери стенки купели.
    public readonly struct BathWater {
        public readonly float Litres, Celsius;
        public BathWater(float litres, float celsius) { Litres = litres; Celsius = celsius; }
    }

    public static class BathMath {
        public const float SpecificHeat = 4186f;      // Дж/(кг·К), вода
        public const float LatentHeat = 2.26e6f;      // Дж/кг, испарение при кипении
        public const float KgPerLitre = 1f;
        public const float Boil = 100f;
        public const float LossWattsPerKelvin = 12f;  // деревянная стенка и открытая поверхность
        public const float FusionHeat = 3.34e5f;      // Дж/кг, плавление льда и снега
        public const float MeltLitresPerKg = KgPerLitre; // масса сохраняется при таянии: 1 кг снега дает 1 кг воды (1 л)

        // Нагрев мощностью watts и потери в окружающий воздух ambient за dt секунд.
        // Перегрев сверх кипения уходит в испарение: масса уменьшается, температура стоит на 100 °C.
        public static BathWater Heat(BathWater w, float dt, float watts, float ambient) {
            if (w.Litres <= 0) { return new BathWater(0, ambient); }
            float capacity = w.Litres * KgPerLitre * SpecificHeat;
            float loss = LossWattsPerKelvin * (w.Celsius - ambient);
            float celsius = w.Celsius + (watts - loss) * dt / capacity;
            if (celsius < ambient) { celsius = ambient; }
            float litres = w.Litres;
            if (celsius > Boil) {
                float evaporated = (celsius - Boil) * capacity / LatentHeat;
                litres = MathF.Max(0, litres - evaporated / KgPerLitre);
                celsius = Boil;
            }
            return new BathWater(litres, celsius);
        }

        // Термостат: нагреватель работает, пока вода ниже уставки.
        public static bool Heating(BathWater w, float setpoint) => w.Celsius < setpoint;

        // Долив: принимает не больше свободного места; температура смешивается по массе.
        public static BathWater Pour(BathWater w, float litres, float sourceCelsius, float capacity, out float accepted) {
            accepted = Math.Clamp(litres, 0, MathF.Max(0, capacity - w.Litres));
            if (accepted <= 0) { return w; }
            float total = w.Litres + accepted;
            return new BathWater(total, (w.Litres * w.Celsius + accepted * sourceCelsius) / total);
        }

        // Слив через открытое отверстие: постоянный расход в литрах в секунду.
        public static BathWater Drain(BathWater w, float dt, float rate) => new BathWater(MathF.Max(0, w.Litres - rate * dt), w.Celsius);

        // Сколько снега растает на энергию energy (Дж) и не больше available (кг).
        public static float MeltKg(float energy, float available) => MathF.Max(0, MathF.Min(energy / FusionHeat, available));

        // Литры, которые насос переносит за шаг: не больше производительности и не больше того, что есть в источнике.
        public static float Pumped(float dt, float rate, float available) => MathF.Max(0, MathF.Min(rate * dt, available));

        // Высота уровня воды внутри чаши (мировая координата Y).
        public static float SurfaceY(float bottom, float height, float litres, float capacity) =>
            bottom + height * Math.Clamp(litres / capacity, 0, 1);

        // Доля тела под водой. Та же мерка, что у погружения в гидросистемах: высота 180 единиц
        // от подошв вверх, начало отсчёта на 90 единиц ниже центра персонажа.
        public static float Submersion(float worldY, float surfaceY) => Math.Clamp((surfaceY - (worldY - 90)) / 180, 0, 1);

        // Поворот предмета: из мировых смещений в локальную систему купели и обратно (ось Y вверх).
        public static void ToLocal(float dx, float dy, float angle, out float x, out float y) {
            float c = MathF.Cos(angle), s = MathF.Sin(angle);
            x = dx * c + dy * s; y = -dx * s + dy * c;
        }

        public static void ToWorld(float x, float y, float angle, out float dx, out float dy) {
            float c = MathF.Cos(angle), s = MathF.Sin(angle);
            dx = x * c - y * s; dy = x * s + y * c;
        }

        // Пар виден, когда вода близка к кипению.
        public static bool Steaming(BathWater w) => w.Litres > 0 && w.Celsius >= 60;
    }
}
