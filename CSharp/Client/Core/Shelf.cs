using System.Collections.Generic;
using Barotrauma;
using Microsoft.Xna.Framework;

namespace ScpItdMod.Core {
    public sealed class Shelf {
        private readonly List<GUIComponent> retired = new List<GUIComponent>();
        private readonly List<GUIComponent> raised = new List<GUIComponent>();
        private readonly List<RectTransform> resized = new List<RectTransform>();
        private readonly List<Vector2> sizes = new List<Vector2>();
        private readonly List<GUIComponent> reanchored = new List<GUIComponent>();
        private readonly List<Anchor> anchors = new List<Anchor>();

        public void Retire(GUIComponent component) {
            if (component == null || retired.Contains(component)) { return; }
            component.Visible = false;
            component.IgnoreLayoutGroups = true;
            retired.Add(component);
        }

        public void Hide() {
            for (int i = 0; i < retired.Count; i++) { retired[i].Visible = false; }
        }

        public T Raise<T>(T component) where T : GUIComponent {
            if (component != null) { raised.Add(component); }
            return component;
        }

        public void Resize(RectTransform transform, float width, float height) {
            if (transform == null) { return; }
            if (!resized.Contains(transform)) {
                resized.Add(transform);
                sizes.Add(transform.RelativeSize);
            }
            transform.RelativeSize = new Vector2(width, height);
        }

        public void Reanchor(GUIComponent component, Anchor anchor) {
            if (component == null) { return; }
            if (!reanchored.Contains(component)) {
                reanchored.Add(component);
                anchors.Add(component.RectTransform.Anchor);
            }
            component.RectTransform.SetPosition(anchor);
        }

        public void Restore() {
            for (int i = reanchored.Count - 1; i >= 0; i--) {
                reanchored[i].RectTransform.SetPosition(anchors[i]);
            }
            reanchored.Clear();
            anchors.Clear();

            for (int i = resized.Count - 1; i >= 0; i--) { resized[i].RelativeSize = sizes[i]; }
            resized.Clear();
            sizes.Clear();

            for (int i = raised.Count - 1; i >= 0; i--) { raised[i].Parent?.RemoveChild(raised[i]); }
            raised.Clear();

            for (int i = retired.Count - 1; i >= 0; i--) {
                retired[i].Visible = true;
                retired[i].IgnoreLayoutGroups = false;
            }
            retired.Clear();
        }
    }
}
