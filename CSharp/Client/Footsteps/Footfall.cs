using Barotrauma;
using Microsoft.Xna.Framework;
using FarseerPhysics;
using ScpItdMod.Core;

namespace ScpItdMod.Footsteps {
    public static class Footfall {
        private const float FloodedMargin = 5.0f;

        public static bool Intercept(Limb limb) {
            if (limb == null || (limb.type != LimbType.LeftFoot && limb.type != LimbType.RightFoot)) {
                return false;
            }
            Character character = limb.character;
            if (character == null || !character.IsHumanoid || !PlayView.Active(character)) { return false; }
            if (!FootstepAudio.Audible(limb.WorldPosition)) { return false; }
            if (Submerged(character, limb)) { return false; }
            if (FootstepGear.Suppresses(limb)) { return false; }

            FootstepAudio.Play(
                FootstepMaterials.Of(FootstepFloor.Tag(character), FootstepFloor.Under(character), InteriorSnow(character, limb), Weather.WeatherService.IndoorAt(limb.WorldPosition + new Vector2(0, 30))),
                character.AnimController.IsMovingFast,
                limb.WorldPosition,
                character.CurrentHull);
            FootstepGear.PlayWorn(limb);
            limb.LastImpactSoundTime = (float)Timing.TotalTime;
            return true;
        }

        // The sound follows the snow that is really under the foot. A closed room has none (the weather grid keeps it out), so the floor sounds;
        // snow that blew in through an open door or hatch lies on the room's cells and is heard there.
        private static Weather.SnowContact InteriorSnow(Character character, Limb limb) => Weather.WeatherService.Footing(limb.WorldPosition).Contact;

        private static bool Submerged(Character character, Limb limb) {
            if (limb.InWater) { return true; }
            Hull hull = character.CurrentHull;
            if (hull == null) { return false; }
            float floor = hull.Rect.Y - hull.Rect.Height;
            return hull.Surface > floor + FloodedMargin &&
                limb.SimPosition.Y < ConvertUnits.ToSimUnits(floor) + limb.body.GetMaxExtent();
        }
    }
}
