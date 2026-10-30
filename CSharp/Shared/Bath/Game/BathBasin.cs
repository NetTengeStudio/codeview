using Barotrauma;
using Microsoft.Xna.Framework;
using ScpItdMod.Core;

namespace ScpItdMod.Bath {
    // Геометрия и параметры купели из префаба: одно место для сервера, тепловой модели и отрисовки.
    public readonly struct BathBasin {
        public readonly float Capacity, Setpoint, WaterShare, PumpRate, DrainRate, UnitsPerLitre;
        public readonly float Left, Bottom, Width, Height;
        public readonly float SnowCapacity, HopperLeft, HopperBottom, HopperWidth, HopperHeight;

        public BathBasin(Item item) {
            var config = item.Prefab.ConfigElement;
            Capacity = config.GetAttributeFloat("scpitdcapacity", 200f);
            Setpoint = config.GetAttributeFloat("scpitdsetpoint", 60f);
            WaterShare = config.GetAttributeFloat("scpitdwatershare", .6f);
            PumpRate = config.GetAttributeFloat("scpitdpump", 3f);
            DrainRate = config.GetAttributeFloat("scpitddrain", 4f);
            // Объём воды корпуса (единицы Hull.WaterVolume) на один литр купели.
            UnitsPerLitre = config.GetAttributeFloat("scpitdunits", 100f);
            // Чаша в единицах дисплея от центра предмета, ось Y вверх (неповёрнутый предмет, масштаб 1).
            Rectangle local = config.GetAttributeRect("scpitdbasin", Rectangle.Empty);
            Left = local.X; Bottom = local.Y; Width = local.Width; Height = local.Height;
            // Приёмник снега над водой (та же система координат) и его вместимость.
            Rectangle hopper = config.GetAttributeRect("scpitdhopper", Rectangle.Empty);
            HopperLeft = hopper.X; HopperBottom = hopper.Y; HopperWidth = hopper.Width; HopperHeight = hopper.Height;
            SnowCapacity = config.GetAttributeFloat("scpitdsnowkg", 60f);
        }

        public bool InsideHopper(float x, float y, float s) =>
            x >= HopperLeft * s && x <= (HopperLeft + HopperWidth) * s && y >= HopperBottom * s && y <= (HopperBottom + HopperHeight) * s;

        // Уровень воды в локальной системе предмета (ось Y вверх) при масштабе предмета s.
        public float SurfaceLocal(float litres, float s) => BathMath.SurfaceY(Bottom * s, Height * s, litres, Capacity);

        public bool InsideLocal(float x, float y, float s) =>
            x >= Left * s && x <= (Left + Width) * s && y >= Bottom * s && y <= (Bottom + Height) * s;
    }
}
