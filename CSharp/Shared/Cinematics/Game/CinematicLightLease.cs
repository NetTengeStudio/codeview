using System;
using System.Collections.Generic;
using Barotrauma.Items.Components;

namespace ScpItdMod.Cinematics {
    internal sealed class CinematicLightLease : IDisposable {
        private static readonly HashSet<LightComponent> owners = new HashSet<LightComponent>();
        private readonly LightComponent light;
        private readonly bool previous;
        private readonly float range;
        private float written;
        private bool enabled;
        internal double Remaining;
        private bool active = true;
        internal static int Count => owners.Count;
        internal CinematicLightLease(LightComponent light) {
            if (light == null) throw new ArgumentNullException(nameof(light));
            if (!owners.Add(light)) throw new InvalidOperationException("Light is already owned by another cinematic");
            this.light = light; previous = light.IsOn; range = light.Range;
        }
        internal void Apply(CinematicCue cue) {
            light.IsOn = enabled = cue.Enabled; light.Range = range * cue.Intensity; written = light.Range; Remaining = cue.Duration;
        }
        public void Dispose() {
            if (!active) return;
            active = false; owners.Remove(light);
            if (light.Item.Removed) return;
            if (light.IsOn == enabled) light.IsOn = previous;
            if (light.Range == written) light.Range = range;
        }
    }
}
