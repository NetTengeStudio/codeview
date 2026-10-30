using System.Collections.Generic;
using Barotrauma;

namespace ScpItdMod.Ambient {
    public static class AmbientZoneClassifier {
        private static readonly Dictionary<Identifier, AmbientZone> Rooms =
            new Dictionary<Identifier, AmbientZone> {
                { "RoomName.ReactorRoom".ToIdentifier(), AmbientZone.Machinery },
                { "RoomName.EngineRoom".ToIdentifier(), AmbientZone.Machinery },
                { "RoomName.Ballast".ToIdentifier(), AmbientZone.Machinery }
            };

        public static AmbientZone? Supplied { get; set; }

        public static bool Forced => Supplied.HasValue;

        public static AmbientZone Classify(Hull hull) {
            if (Supplied.HasValue) { return Supplied.Value; }
            if (hull == null) { return AmbientZone.General; }
            if (Visited(hull.Submarine)) { return AmbientZone.Outpost; }
            return Rooms.TryGetValue(Room(hull), out AmbientZone zone) ? zone : AmbientZone.Interior;
        }

        private static Identifier Room(Hull hull) =>
            string.IsNullOrEmpty(hull.RoomName) ? Identifier.Empty : hull.RoomName.ToIdentifier();

        private static bool Visited(Submarine submarine) {
            SubmarineType type = submarine?.Info?.Type ?? SubmarineType.Player;
            return type != SubmarineType.Player && type != SubmarineType.EnemySubmarine;
        }
    }
}
