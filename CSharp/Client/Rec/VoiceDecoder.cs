using System;
using System.Reflection;
using Barotrauma.Networking;

namespace ScpItdMod.Rec {
    internal static class VoiceDecoder {
        internal delegate int DecodePacket(byte[] data, int offset, int length, short[] pcm, int pcmOffset, int frameSize, bool decodeFec = false);

        public static DecodePacket Create() {
            MethodInfo factory = typeof(VoipConfig).GetMethod("CreateDecoder", BindingFlags.Public | BindingFlags.Static,
                null, Type.EmptyTypes, null)
                ?? throw new MissingMethodException(typeof(VoipConfig).FullName, "CreateDecoder");
            object decoder = factory.CreateDelegate<Func<object>>()();
            Type decoderType = factory.ReturnType;
            MethodInfo decode = decoderType.GetMethod("Decode", BindingFlags.Public | BindingFlags.Instance,
                null, new[] { typeof(byte[]), typeof(int), typeof(int), typeof(short[]), typeof(int), typeof(int), typeof(bool) }, null)
                ?? throw new MissingMethodException(decoderType.FullName, "Decode");
            return decode.CreateDelegate<DecodePacket>(decoder);
        }
    }
}
