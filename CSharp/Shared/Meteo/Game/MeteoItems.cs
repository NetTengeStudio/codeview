using Barotrauma;
using Barotrauma.Items.Components;
using Microsoft.Xna.Framework;

namespace ScpItdMod.Meteo {
    public sealed class ScpMeteoInstrument : ItemComponent {
        public ScpMeteoInstrument(Item item, ContentXElement element) : base(item, element) { IsActive = false; }

        public override bool Select(Character character) {
#if SERVER
            MeteoHost.ReadInstrument(item, character);
#endif
            return false;
        }
    }

    public sealed class ScpMeteoDeicer : ItemComponent {
        public ScpMeteoDeicer(Item item, ContentXElement element) : base(item, element) { IsActive = false; }

        public override bool Use(float deltaTime, Character character = null) {
            if (character == null) { return false; }
#if SERVER
            MeteoHost.Deice(item, character, character.CursorWorldPosition, deltaTime);
#endif
            return true;
        }
    }
}
