using System;
using Barotrauma;
using Microsoft.Xna.Framework;

namespace ScpItdMod.People {
    // GUITextBox's OverflowClip handles horizontal overflow only. A listbox owns
    // vertical clipping and scrolling; the editor always fits its complete text.
    public static class ProfileTextArea {
        public static GUITextBox Create(RectTransform bounds, int maxLength) {
            var viewport = new GUIListBox(bounds) { CanBeFocused = false, CanTakeKeyBoardFocus = false };
            bounds.MinSize = new Point(0, GUI.IntScale(100));
            var box = new GUITextBox(new RectTransform(new Vector2(1, 0), viewport.Content.RectTransform), "",
                font: GUIStyle.SmallFont, textAlignment: Alignment.TopLeft, wrap: true, createPenIcon: false) {
                MaxTextLength = maxLength, OverflowClip = true
            };
            box.RectTransform.MaxSize = new Point(int.MaxValue, int.MaxValue);
            foreach (GUIComponent child in box.Children) { child.RectTransform.MaxSize = new Point(int.MaxValue, int.MaxValue); }
            bool dirty = true;
            int lastCaret = -1, lastWidth = -1, lastViewportHeight = -1;
            box.OnTextChanged += (_, _) => { dirty = true; return true; };
            box.OnEnterPressed = (editor, text) => {
                if (text.Length < editor.MaxTextLength) {
                    int caret = Math.Clamp(editor.CaretIndex, 0, text.Length);
                    editor.Text = text.Insert(caret, "\n"); editor.CaretIndex = caret + 1;
                }
                return true;
            };
            new GUICustomComponent(new RectTransform(Vector2.Zero, viewport.Content.RectTransform), onUpdate: (_, _) => {
                bool changed = dirty || lastWidth != box.Rect.Width || lastViewportHeight != viewport.Content.Rect.Height;
                if (changed) {
                    dirty = false; lastWidth = box.Rect.Width; lastViewportHeight = viewport.Content.Rect.Height;
                    box.TextBlock.SetTextPos();
                    int height = Math.Max(viewport.Content.Rect.Height, (int)Math.Ceiling(box.TextBlock.TextSize.Y * box.TextBlock.TextScale) + GUI.IntScale(16));
                    if (box.Rect.Height != height) {
                        box.RectTransform.MaxSize = new Point(int.MaxValue, int.MaxValue);
                        box.RectTransform.MinSize = new Point(0, height);
                        box.RectTransform.MaxSize = new Point(int.MaxValue, height);
                    }
                }
                if (box.Selected && (changed || lastCaret != box.CaretIndex)) {
                    float overflow = box.Rect.Height - viewport.Content.Rect.Height;
                    if (overflow > 0) {
                        float top = viewport.Content.Rect.Top + GUI.IntScale(6);
                        float bottom = viewport.Content.Rect.Bottom - box.Font.LineHeight - GUI.IntScale(6);
                        float caret = box.CaretScreenPos.Y;
                        float shift = caret < top ? caret - top : caret > bottom ? caret - bottom : 0;
                        if (shift != 0) { viewport.BarScroll = Math.Clamp(viewport.BarScroll + shift / overflow, 0, 1); }
                    }
                }
                lastCaret = box.CaretIndex;
            }) { IgnoreLayoutGroups = true, CanBeFocused = false };
            return box;
        }
    }
}
