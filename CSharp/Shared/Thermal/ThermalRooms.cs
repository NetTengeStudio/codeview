using System;

namespace ScpItdMod.Thermal {
    public struct ThermalRoom {
        public double Temperature, Capacity, OutsideConductance, Heat, SurfaceHeat;
        public double SurfaceTemperature, FabricCapacity, FabricConductance, FabricOutsideConductance;
    }
    public struct ThermalLink {
        public int A, B;
        public bool Fabric;
        public double Conductance;
    }
    public sealed class ThermalRooms {
        public readonly ThermalRoom[] Rooms;
        public readonly ThermalLink[] Links;
        public double SourceJoules { get; private set; }
        public double BoundaryJoules { get; private set; }
        private double externalJoules;
        public ThermalRooms(int rooms, int links) { Rooms = new ThermalRoom[rooms]; Links = new ThermalLink[links]; }
        public double Energy { get { double sum = 0; for (int i = 0; i < Rooms.Length; i++) sum += Rooms[i].Capacity * Rooms[i].Temperature + Rooms[i].FabricCapacity * Rooms[i].SurfaceTemperature; return sum; } }
        public void Step(double outside, float dt) {
            if (!double.IsFinite(outside) || !float.IsFinite(dt) || dt <= 0 || dt > 1) throw new ArgumentOutOfRangeException(nameof(dt));
            SourceJoules = externalJoules = 0;
            for (int i = 0; i < Rooms.Length; i++) {
                ref ThermalRoom room = ref Rooms[i];
                if (!double.IsFinite(room.Capacity + room.Temperature + room.OutsideConductance + room.Heat + room.SurfaceHeat + room.SurfaceTemperature + room.FabricCapacity + room.FabricConductance + room.FabricOutsideConductance) || room.Capacity <= 0 || room.OutsideConductance < 0 || room.FabricCapacity < 0 || room.FabricConductance < 0 || room.FabricOutsideConductance < 0) throw new InvalidOperationException("Invalid thermal room");
                SourceJoules += (room.Heat + room.SurfaceHeat) * dt;
            }
            for (int i = 0; i < Links.Length; i++) {
                ThermalLink link = Links[i];
                if ((uint)link.A >= Rooms.Length || (uint)link.B >= Rooms.Length || link.A == link.B || !double.IsFinite(link.Conductance) || link.Conductance < 0 || link.Fabric && (Rooms[link.A].FabricCapacity <= 0 || Rooms[link.B].FabricCapacity <= 0)) throw new InvalidOperationException("Invalid thermal link");
            }
            Sources(outside, dt * .5);
            for (int i = 0; i < Links.Length; i++) Exchange(Links[i], dt * .5);
            for (int i = Links.Length - 1; i >= 0; i--) Exchange(Links[i], dt * .5);
            Sources(outside, dt * .5);
            BoundaryJoules = externalJoules - SourceJoules;
        }
        private void Sources(double outside, double dt) {
            for (int i = 0; i < Rooms.Length; i++) {
                ref ThermalRoom r = ref Rooms[i];
                double before = r.Capacity * r.Temperature + r.FabricCapacity * r.SurfaceTemperature;
                if (r.FabricCapacity == 0) { Source(ref r.Temperature, r.Capacity, r.OutsideConductance, outside, r.Heat + r.SurfaceHeat, dt); r.SurfaceTemperature = r.Temperature; }
                else {
                    Source(ref r.Temperature, r.Capacity, r.OutsideConductance, outside, r.Heat, dt * .5);
                    Source(ref r.SurfaceTemperature, r.FabricCapacity, r.FabricOutsideConductance, outside, r.SurfaceHeat, dt * .5);
                    Exchange(ref r.Temperature, ref r.SurfaceTemperature, r.Capacity, r.FabricCapacity, r.FabricConductance, dt);
                    Source(ref r.SurfaceTemperature, r.FabricCapacity, r.FabricOutsideConductance, outside, r.SurfaceHeat, dt * .5);
                    Source(ref r.Temperature, r.Capacity, r.OutsideConductance, outside, r.Heat, dt * .5);
                }
                externalJoules += r.Capacity * r.Temperature + r.FabricCapacity * r.SurfaceTemperature - before;
            }
        }
        private static void Source(ref double temperature, double capacity, double conductance, double outside, double watts, double dt) {
            if (conductance == 0) temperature += watts * dt / capacity;
            else {
                double equilibrium = outside + watts / conductance;
                temperature = equilibrium + (temperature - equilibrium) * Math.Exp(-conductance * dt / capacity);
            }
        }
        private static void Exchange(ref double a, ref double b, double ca, double cb, double conductance, double dt) {
            double inverse = 1 / ca + 1 / cb;
            double transfer = (a - b) / inverse * (1 - Math.Exp(-conductance * inverse * dt));
            a -= transfer / ca; b += transfer / cb;
        }
        private void Exchange(in ThermalLink link, double dt) {
            ref ThermalRoom a = ref Rooms[link.A]; ref ThermalRoom b = ref Rooms[link.B];
            if (link.Fabric) Exchange(ref a.SurfaceTemperature, ref b.SurfaceTemperature, a.FabricCapacity, b.FabricCapacity, link.Conductance, dt);
            else Exchange(ref a.Temperature, ref b.Temperature, a.Capacity, b.Capacity, link.Conductance, dt);
        }
        public static double Ventilation(float area, float open, float wind, float deltaT, float height) {
            double speed = Math.Sqrt(.04 * wind * wind + 2 * 9.81 * Math.Max(.1, height) * Math.Abs(deltaT) / 273.15);
            return 1.2 * 1005 * .62 * Math.Max(0, area) * Math.Clamp(open, 0, 1) * speed;
        }
    }
}
