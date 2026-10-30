using System;
using ScpItdMod.Core;
using ScpItdMod.Motion;

namespace ScpItdMod.Thermal {
    internal enum FeltState : byte { Unavailable, Severe, VeryCold, Cold, Cool, Warm }
    internal enum FeltTrend : byte { Unknown, Steady, Cooling, Warming }
    internal enum FeltMoisture : byte { Dry, Damp, Wet }

    internal sealed class FeltPresentation : IDisposable {
        private static readonly MotionTransition Column = MotionTransition.Spring(28, 1);
        private static readonly MotionTransition Memory = MotionTransition.Spring(12, 1);
        private static readonly MotionTransition Ink = MotionTransition.Spring(25, 1);
        private static readonly MotionTransition Settle = MotionTransition.Spring(30, 1);
        private readonly MotionScope scope;
        private readonly MotionPlayback entrance;
        private readonly MotionHandle<float> marker, memory, deployment, details, focus, availability, moisture, damp, trend, stamp;
        private readonly MotionHandle<Vec4> ink;
        private readonly MotionHandle<Vec2> column, status, context, wet;
        private bool initialized, sampling, placed;
        private uint sequence;
        private double sampledAt;
        private float sampledSkin, rate;
        private FeltMoisture visibleMoisture;
        internal FeltState State { get; private set; }
        internal FeltTrend Trend { get; private set; }
        internal FeltMoisture Moisture { get; private set; }
        internal bool Alive => scope.Alive;
        internal bool Available => State != FeltState.Unavailable;
        internal float Marker => Unit(marker.Value);
        internal float MemoryMarker => Unit(memory.Value);
        internal float Deployment => Unit(deployment.Value);
        internal float Details => Unit(details.Value);
        internal float Focus => Unit(focus.Value);
        internal float Availability => Unit(availability.Value);
        internal float Wetness => Unit(moisture.Value);
        internal float Damp => Unit(damp.Value);
        internal float Direction => Trend == FeltTrend.Cooling ? -MathF.Abs(Math.Clamp(trend.Value, -1, 1)) :
            Trend == FeltTrend.Warming ? MathF.Abs(Math.Clamp(trend.Value, -1, 1)) : 0;
        internal float Stamp => Math.Clamp(stamp.Value, -3, 3);
        internal Vec4 InkColor => ink.Value;
        internal Vec2 ColumnSpan => column.Value;
        internal Vec2 StatusPosition => status.Value;
        internal Vec2 ContextPosition => context.Value;
        internal Vec2 MoisturePosition => wet.Value;
        internal FeltPresentation(MotionRuntime runtime) {
            scope = runtime.Scope();
            marker = scope.Scalar(); memory = scope.Scalar(); focus = scope.Scalar();
            availability = scope.Scalar(); moisture = scope.Scalar(); damp = scope.Scalar(); trend = scope.Scalar(); stamp = scope.Scalar();
            deployment = scope.Scalar(runtime.ReducedMotion ? 1 : .4f);
            details = scope.Scalar(runtime.ReducedMotion ? 1 : .7f);
            ink = scope.Color(Tint(FeltState.Unavailable));
            column = scope.Vector(); status = scope.Vector(); context = scope.Vector(); wet = scope.Vector();
            entrance = scope.Play(new MotionSequenceBuilder()
                .To(deployment, 1f, MotionPresets.Window)
                .To(details, 1f, MotionPresets.Fade, .045).Compile());
        }
        internal void Place(in FeltLayout layout) {
            Vec2 span = new Vec2(layout.GaugeTop, layout.GaugeBottom);
            Vec2 heading = new Vec2(layout.TextX, layout.StateY);
            Vec2 detail = new Vec2(layout.TextX, layout.DetailY);
            Vec2 humidity = new Vec2(layout.MoistureX, layout.MoistureY);
            if (!placed) {
                placed = true; column.Snap(span); status.Snap(heading); context.Snap(detail); wet.Snap(humidity);
            } else {
                column.To(span, MotionPresets.Layout); status.To(heading, MotionPresets.Layout);
                context.To(detail, MotionPresets.Layout); wet.To(humidity, MotionPresets.Layout);
            }
        }
        internal void Hover(bool hovered) => focus.To(hovered ? 1 : 0, MotionPresets.Hover);
        internal void Update(bool available, float skin, float wetness, uint revision, double time) {
            available &= float.IsFinite(skin) && skin >= -90 && skin <= 60 && float.IsFinite(wetness) && wetness >= 0 && wetness <= 1 && double.IsFinite(time);
            FeltState previous = State;
            State = Classify(available, skin);
            if (scope.Runtime.ReducedMotion) { entrance.Cancel(); deployment.Snap(1); details.Snap(1); stamp.Snap(0); }
            if (!initialized || State != previous) {
                Vec4 tint = Tint(State);
                bool urgent = initialized && Available && (previous == FeltState.Unavailable || (int)State < (int)previous);
                if (!initialized || urgent || !Available) ink.Snap(tint); else ink.To(tint, Ink);
                if (initialized && !scope.Runtime.ReducedMotion && State != FeltState.Unavailable) {
                    stamp.To(0, Settle); stamp.Impulse(urgent ? -100 : 65);
                }
            }
            availability.To(Available ? 1 : 0, MotionPresets.Fade);
            if (!available) {
                sampling = false; rate = 0; Trend = FeltTrend.Unknown;
                trend.To(0, MotionPresets.Notification); damp.To(0, MotionPresets.Fade);
                initialized = true;
                return;
            }
            Moisture = wetness < .1f ? FeltMoisture.Dry : wetness < .5f ? FeltMoisture.Damp : FeltMoisture.Wet;
            if (Moisture != FeltMoisture.Dry) visibleMoisture = Moisture;
            moisture.To(wetness, Memory); damp.To(Moisture == FeltMoisture.Dry ? 0 : 1, Ink);
            float position = Position(skin);
            if (!initialized || previous == FeltState.Unavailable) { marker.Snap(position); memory.Snap(position); }
            else { marker.To(position, Column); memory.To(position, Memory); }
            initialized = true;
            if (!sampling || time < sampledAt || time - sampledAt > 3) {
                sampling = true; sequence = revision; sampledAt = time; sampledSkin = skin;
                rate = 0; Trend = FeltTrend.Unknown; trend.To(0, Ink);
                return;
            }
            if (sequence == revision || time <= sampledAt) return;
            double elapsed = time - sampledAt;
            float blend = (float)(1 - Math.Exp(-elapsed / 2));
            rate += ((skin - sampledSkin) / (float)elapsed - rate) * blend;
            sampledAt = time; sampledSkin = skin; sequence = revision;
            float threshold = Trend == FeltTrend.Cooling || Trend == FeltTrend.Warming ? .005f : .01f;
            Trend = rate < -threshold ? FeltTrend.Cooling : rate > threshold ? FeltTrend.Warming : FeltTrend.Steady;
            trend.To(Trend == FeltTrend.Cooling ? -1 : Trend == FeltTrend.Warming ? 1 : 0, Ink);
        }
        private static float Unit(float value) => Math.Clamp(value, 0, 1);
        internal static FeltState Classify(bool available, float skin) => !available || !float.IsFinite(skin) ? FeltState.Unavailable :
            skin < Boundary(1) ? FeltState.Severe : skin < Boundary(2) ? FeltState.VeryCold : skin < Boundary(3) ? FeltState.Cold : skin < Boundary(4) ? FeltState.Cool : FeltState.Warm;
        internal static float Boundary(int index) => index switch { 0 => 0, 1 => 5, 2 => 10, 3 => 20, 4 => 26, 5 => 34, _ => throw new ArgumentOutOfRangeException(nameof(index)) };
        internal static float Position(float skin) => float.IsFinite(skin) ? Math.Clamp(skin / Boundary(5), 0, 1) : 0;
        internal static Vec4 Tint(FeltState state) => state switch {
            FeltState.Severe => new Vec4(.953f, .427f, .427f, 1),
            FeltState.VeryCold => new Vec4(.910f, .690f, .282f, 1),
            FeltState.Cold => new Vec4(.690f, .815f, .847f, 1),
            FeltState.Cool => new Vec4(.784f, .820f, .820f, 1),
            FeltState.Warm => new Vec4(.839f, .824f, .745f, 1),
            _ => new Vec4(.66f, .66f, .66f, 1)
        };
        internal static string Text(FeltState state) => state switch {
            FeltState.Severe => "СИЛЬНЫЙ ХОЛОД", FeltState.VeryCold => "ОЧЕНЬ ХОЛОДНО", FeltState.Cold => "ХОЛОДНО",
            FeltState.Cool => "ПРОХЛАДНО", FeltState.Warm => "ТЕПЛО", _ => "НЕТ ДАННЫХ"
        };
        internal string ChangeText => !Available ? "Нет свежих показаний" : Trend switch {
            FeltTrend.Cooling => "Остываете", FeltTrend.Warming => "Согреваетесь", FeltTrend.Steady => "Без изменений", _ => ""
        };
        internal string MoistureText => !Available || Damp < .001f ? "" : visibleMoisture == FeltMoisture.Wet ? "Вы промокли" : "Влажно";
        internal string Help => !Available ? "Теплоощущение недоступно: нет свежих данных о вашем состоянии." :
            "Состояние определяется температурой самого холодного участка кожи, а не воздуха или ядра тела. Подпись и стрелка показывают изменение этой температуры: можно ещё мёрзнуть и уже согреваться. Столбик — текущее состояние, тонкий след — недавнее. Намокание усиливает потерю тепла.";
        public void Dispose() { entrance.Cancel(); scope.Dispose(); }
    }

    internal readonly struct FeltLayout {
        internal readonly float Width, Height, GaugeX, GaugeTop, GaugeBottom, TextX, StateY, DetailY, MoistureX, MoistureY, TextScale, DetailScale, Scale;
        internal readonly bool Stacked;
        internal FeltLayout(float width, float scale, float inset, float normalHeight, float smallHeight, float stateWidth, float changeWidth, float wetWidth) {
            Width = MathF.Max(0, width); Scale = MathF.Max(.1f, scale);
            float pad = MathF.Ceiling(5 * Scale);
            TextX = Math.Clamp(inset, MathF.Min(Width, 16 * Scale), MathF.Max(MathF.Min(Width, 16 * Scale), Width - pad));
            float space = MathF.Max(0, Width - TextX - pad);
            TextScale = MathF.Min(1, space / MathF.Max(1, stateWidth));
            DetailScale = MathF.Min(1, MathF.Max(0, space - 12 * Scale) / MathF.Max(1, MathF.Max(changeWidth, wetWidth)));
            Stacked = changeWidth + wetWidth + 22 * Scale > space;
            StateY = pad + 2 * Scale;
            DetailY = StateY + normalHeight * TextScale + MathF.Ceiling(4 * Scale);
            MoistureX = Stacked ? TextX : TextX + changeWidth * DetailScale + 20 * Scale;
            MoistureY = Stacked ? DetailY + smallHeight * DetailScale + MathF.Ceiling(3 * Scale) : DetailY;
            Height = MathF.Max(52 * Scale, MoistureY + smallHeight * DetailScale + pad);
            GaugeX = MathF.Max(5 * Scale, TextX * .5f);
            GaugeTop = pad + 2 * Scale; GaugeBottom = MathF.Max(GaugeTop, Height - 11 * Scale);
        }
        internal float MarkerY(float position) => GaugeBottom - Math.Clamp(position, 0, 1) * (GaugeBottom - GaugeTop);
    }
}
