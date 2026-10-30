using System;
using System.Collections.Generic;
using ScpItdMod.Xp;

namespace ScpItdMod.Pc {
    // Logical desktops outlive the local connection and its render/input resources.
    // Keys are issued by the server only after checking the current terminal and actor.
    public sealed class DesktopStore {
        private readonly Dictionary<DesktopIdentity, XpShell> desktops = new Dictionary<DesktopIdentity, XpShell>();
        public int Count => desktops.Count;
        public XpShell Open(in DesktopIdentity identity, Func<XpShell> create) {
            if (!identity.Valid) { throw new ArgumentException("An authenticated desktop identity is required.", nameof(identity)); }
            if (desktops.TryGetValue(identity, out XpShell retained)) { retained.Resume(); return retained; }
            XpShell shell = create() ?? throw new InvalidOperationException("Desktop factory returned no shell.");
            desktops.Add(identity, shell); return shell;
        }
        public void Clear() {
            foreach (XpShell desktop in desktops.Values) { desktop.Shutdown(); }
            desktops.Clear();
        }
    }
}
