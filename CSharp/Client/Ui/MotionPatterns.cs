using System;
using Vector2 = ScpItdMod.Core.Vec2;
using ScpItdMod.Motion;

namespace ScpItdMod.Ui {
    public enum MotionStyle : byte { Control, Card, Window, Document, Menu, Notification, Modal }
    public static class MotionPatterns {
        public static MotionTransition Profile(MotionStyle style) => style switch {
            MotionStyle.Card => MotionPresets.Card, MotionStyle.Document => MotionPresets.Paper,
            MotionStyle.Window or MotionStyle.Modal or MotionStyle.Menu => MotionPresets.Window,
            MotionStyle.Notification => MotionPresets.Notification, _ => MotionPresets.Hover
        };
        public static MotionSequence Visibility(MotionVisual visual, bool shown, MotionStyle style = MotionStyle.Control) {
            if (visual == null) { throw new ArgumentNullException(nameof(visual)); }
            float hiddenScale = style == MotionStyle.Document ? 1 : style == MotionStyle.Menu ? 0.99f : 0.97f;
            return new MotionSequenceBuilder().To(visual.Opacity, shown ? 1f : 0f, MotionPresets.Fade)
                .To(visual.Scale, shown ? 1f : hiddenScale, Profile(style)).Compile();
        }
        public static MotionSequence Emphasis(MotionVisual visual, bool selected, float strength = 1) {
            if (!float.IsFinite(strength) || strength < 0) { throw new ArgumentOutOfRangeException(nameof(strength)); }
            return new MotionSequenceBuilder().To(visual.Glow, selected ? strength : 0, MotionPresets.Hover)
                .To(visual.Shadow, selected ? 7f : 2f, MotionPresets.Hover).To(visual.Depth, selected ? 2f : 0f, MotionPresets.Hover).Compile();
        }
        public static MotionSequence Drop(MotionVisual visual, in Box destination) => new MotionSequenceBuilder()
            .To(visual.Position, new Vector2(destination.CenterX, destination.CenterY), MotionPresets.Card)
            .To(visual.Size, new Vector2(destination.W, destination.H), MotionPresets.Layout)
            .To(visual.Rotation, 0f, MotionPresets.Card).To(visual.Press, 1f, MotionPresets.Press).Compile();
        public static MotionSequence Feedback(MotionVisual visual, float distance = 4) => new MotionSequenceBuilder()
            .To(visual.Offset, new Vector2(-distance, 0), MotionTransition.Tween(0.05f))
            .To(visual.Offset, new Vector2(distance, 0), MotionTransition.Tween(0.07f), 0.05)
            .To(visual.Offset, Vector2.Zero, MotionTransition.Tween(0.1f), 0.12).Compile();
        public static MotionSequence Notification(MotionVisual visual, double hold = 3) {
            if (!double.IsFinite(hold) || hold < 0) { throw new ArgumentOutOfRangeException(nameof(hold)); }
            return new MotionSequenceBuilder().Insert(Visibility(visual, true, MotionStyle.Notification), 0).Wait(hold)
                .Append(Visibility(visual, false, MotionStyle.Notification)).Compile();
        }
    }
}
