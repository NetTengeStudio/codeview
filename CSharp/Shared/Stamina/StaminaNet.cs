using System.Runtime.CompilerServices;
using ScpItdMod.Core;

namespace ScpItdMod.Stamina {
    public static class StaminaNet {
        public const string StateId = "scpitd.stamina";
        public const int Width = ModNet.ShortWord;

        public static readonly BitField ValueField = new BitField(0, StaminaConfig.ValueBits);

        public static readonly BitField FlagsField =
            new BitField(StaminaConfig.ValueBits, StaminaConfig.FlagBits);

        public static readonly BitField SequenceField =
            new BitField(StaminaConfig.ValueBits + StaminaConfig.FlagBits, StaminaConfig.SequenceBits);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ushort Semantic(float value, StaminaFlags flags) {
            uint word = ValueField.Write(0u, ValueField.Quantize(value));
            return (ushort)FlagsField.Write(word, (uint)flags);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ushort Pack(float value, StaminaFlags flags, uint sequence) =>
            (ushort)SequenceField.Write(Semantic(value, flags), sequence);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Unpack(ushort word, out float value, out StaminaFlags flags, out uint sequence) {
            value = ValueField.Dequantize(ValueField.Read(word));
            flags = (StaminaFlags)FlagsField.Read(word);
            sequence = SequenceField.Read(word);
        }
    }
}
