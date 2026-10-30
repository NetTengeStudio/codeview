using Barotrauma;
using FarseerPhysics.Dynamics;

namespace ScpItdMod.Footsteps {
    public static class FootstepFloor {
        public static string Tag(Character character) =>
            character?.AnimController?.floorFixture?.Body?.UserData is Structure structure ? structure.SpecialTag ?? string.Empty : string.Empty;

        public static Identifier Under(Character character) {
            Fixture floor = character?.AnimController?.floorFixture;
            if (floor?.Body == null) { return Identifier.Empty; }
            return floor.Body.UserData is Structure structure && structure.Prefab != null
                ? structure.Prefab.Identifier
                : Identifier.Empty;
        }
    }
}
