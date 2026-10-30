using System.Collections.Generic;
using Barotrauma;

namespace ScpItdMod.Menu {
    internal sealed class MenuShelf {
        private readonly struct Retired {
            public readonly GUIComponent Component;
            public readonly bool Visible;
            public readonly bool IgnoreLayout;

            public Retired(GUIComponent component) {
                Component = component;
                Visible = component.Visible;
                IgnoreLayout = component.IgnoreLayoutGroups;
            }
        }

        private readonly List<Retired> retired = new List<Retired>();
        private readonly List<GUIComponent> raised = new List<GUIComponent>();

        public void Retire(GUIComponent component) {
            if (component == null) { return; }
            for (int i = 0; i < retired.Count; i++) {
                if (retired[i].Component == component) { return; }
            }
            retired.Add(new Retired(component));
            component.Visible = false;
            component.IgnoreLayoutGroups = true;
        }

        public T Raise<T>(T component) where T : GUIComponent {
            raised.Add(component);
            return component;
        }

        public void Hide() {
            for (int i = 0; i < retired.Count; i++) { retired[i].Component.Visible = false; }
        }

        public void Restore() {
            for (int i = raised.Count - 1; i >= 0; i--) { raised[i].Parent?.RemoveChild(raised[i]); }
            raised.Clear();
            for (int i = retired.Count - 1; i >= 0; i--) {
                Retired saved = retired[i];
                saved.Component.Visible = saved.Visible;
                saved.Component.IgnoreLayoutGroups = saved.IgnoreLayout;
            }
            retired.Clear();
        }
    }
}
