using System.Runtime.CompilerServices;
using ScpItdMod.Core;

namespace ScpItdMod.Blink {
    public enum BlinkFlags : byte {
        None = 0,
        Irritated = 1,
        Engaged = 2
    }

    public static class BlinkNet {
        public const string StateId = "scpitd.blink.state";
        public const string ForceId = "scpitd.blink.force";

        public const int StateWidth = ModNet.LongWord;
        public const int ForceWidth = ModNet.ByteWord;

        public const byte HitBit = 1;
        public const byte HeldBit = 2;

        public static readonly BitField TimerField = new BitField(0, BlinkConfig.TimerBits);

        public static readonly BitField FrequencyField =
            new BitField(BlinkConfig.TimerBits, BlinkConfig.FrequencyBits);

        public static readonly BitField StuckField =
            new BitField(BlinkConfig.TimerBits + BlinkConfig.FrequencyBits, BlinkConfig.StuckBits);

        public static readonly BitField FlagsField =
            new BitField(BlinkConfig.TimerBits + BlinkConfig.FrequencyBits + BlinkConfig.StuckBits,
                BlinkConfig.FlagBits);

        public static readonly BitField SequenceField =
            new BitField(BlinkConfig.TimerBits + BlinkConfig.FrequencyBits + BlinkConfig.StuckBits
                + BlinkConfig.FlagBits, BlinkConfig.SequenceBits);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint Semantic(float timer, float frequency, float stuck, BlinkFlags flags) =>
            Pack(timer, frequency, stuck, flags, 0u);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint Pack(in BlinkState state, BlinkFlags flags, uint sequence) =>
            Pack(state.Timer, state.Frequency, state.Stuck, flags, sequence);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint Pack(float timer, float frequency, float stuck, BlinkFlags flags, uint sequence) {
            uint word = TimerField.Write(0u, TimerField.Quantize(
                Fraction(timer, BlinkConfig.TimerFloor, BlinkConfig.TimerCeiling)));
            word = FrequencyField.Write(word, FrequencyField.Quantize(
                Fraction(frequency, BlinkConfig.FrequencyFloor, BlinkConfig.FrequencyCeiling)));
            word = StuckField.Write(word, StuckField.Quantize(
                Fraction(stuck, 0.0f, BlinkConfig.StuckSeconds)));
            word = FlagsField.Write(word, (uint)flags);
            return SequenceField.Write(word, sequence);
        }

        public static void Unpack(uint word, ref BlinkState state, out BlinkFlags flags, out uint sequence) {
            state.Timer = Value(TimerField.Dequantize(TimerField.Read(word)),
                BlinkConfig.TimerFloor, BlinkConfig.TimerCeiling);
            state.Frequency = Value(FrequencyField.Dequantize(FrequencyField.Read(word)),
                BlinkConfig.FrequencyFloor, BlinkConfig.FrequencyCeiling);
            state.Stuck = Value(StuckField.Dequantize(StuckField.Read(word)),
                0.0f, BlinkConfig.StuckSeconds);
            flags = (BlinkFlags)FlagsField.Read(word);
            state.Irritation = (flags & BlinkFlags.Irritated) != 0 ? BlinkConfig.IrritationPerSecond : 0.0f;
            sequence = SequenceField.Read(word);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float Fraction(float value, float floor, float ceiling) =>
            Scalar.Clamp01((value - floor) / (ceiling - floor));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float Value(float fraction, float floor, float ceiling) =>
            floor + fraction * (ceiling - floor);
    }
}
