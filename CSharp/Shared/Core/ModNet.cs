using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Barotrauma;
using Barotrauma.LuaCs;
using Barotrauma.Networking;
using ScpItdMod.Net;

namespace ScpItdMod.Core {
    public static class ModNet {
        public const int ByteWord = 8;
        public const int ShortWord = 16;
        public const int LongWord = 32;

        private static bool live;
        private static readonly HashSet<string> channels = new HashSet<string>(StringComparer.Ordinal);

        public static void Mount() {
            live = true;
            ModCommands.Register("scpitd_net_trace", "on/off/dump: bounded local transport and state diagnostics", args => {
                if (args.Length != 1) { ModLog.Message("scpitd_net_trace on/off/dump"); return; }
                if (args[0] == "on") NetTrace.Enable(true);
                else if (args[0] == "off") NetTrace.Enable(false);
                else if (args[0] == "dump") NetTrace.Dump(ModLog.Message);
                else ModLog.Message("scpitd_net_trace on/off/dump");
            });
        }

        public static void Release() {
            live = false;
            INetworkingService networking = LuaCsSetup.Instance?.Networking;
            if (networking != null) {
                foreach (string channel in channels) {
                    // LuaCs NetworkingService.Receive replaces an existing channel callback.
                    // Replacing it with a capture-free delegate releases all plugin closures.
                    try { networking.Receive(channel, (NetMessageReceived)Ignore); }
                    catch (Exception error) { ModLog.Once($"{channel} cannot be released: {error.Message}"); }
                }
            }
            channels.Clear();
            NetTrace.Reset();
        }

#if CLIENT
        private static void Ignore(IReadMessage message) { }
#else
        private static void Ignore(IReadMessage message, Client sender) { }
#endif

        private static void Register(INetworkingService networking, string id, NetMessageReceived callback) {
            if (!live || string.IsNullOrEmpty(id)) { return; }
            networking.Receive(id, callback);
            channels.Add(id);
        }

#if CLIENT
        public delegate void SpanReceived(ReadOnlySpan<byte> payload);

        public static void ReceiveSpan(string id, int maxBytes, SpanReceived onPayload) {
            INetworkingService networking = Service();
            if (networking == null || onPayload == null || maxBytes <= 0) { return; }
            var scratch = new byte[maxBytes];
            Register(networking, id, (NetMessageReceived)(message => {
                if (!live || message == null) { return; }
                try {
                    if (Payload(message, scratch, out ReadOnlySpan<byte> payload)) {
                        var measured = NetTrace.Start();
                        try { onPayload(payload); }
                        finally { NetTrace.Finish(measured, id, "receive", payload.Length); }
                    }
                } catch (Exception error) { ModLog.Once($"{id} cannot be received: {error.Message}"); }
            }));
        }

        public static void Receive(string id, int bits, Action<uint> onWord) {
            if (!Valid(bits) || onWord == null) {
                ModLog.Once($"{id} receiver has an invalid word contract");
                return;
            }
            INetworkingService networking = Service();
            if (networking == null) { return; }
            Register(networking, id, (NetMessageReceived)(message => {
                if (!live || message == null) { return; }
                try {
                    var measured = NetTrace.Start();
                    try { onWord(Read(message, bits)); }
                    finally { NetTrace.Finish(measured, id, "receive", bits / 8); }
                } catch (Exception error) {
                    ModLog.Once($"{id} cannot be received: {error.Message}");
                }
            }));
        }

        public static void Send(string id, uint word, int bits, DeliveryMethod delivery) {
            if (!Valid(bits)) {
                ModLog.Once($"{id} cannot carry {bits} bits");
                return;
            }
            INetworkingService networking = Sender();
            if (networking == null) { return; }
            try {
                var measured = NetTrace.Start();
                IWriteMessage message = networking.Start(id);
                Write(message, word, bits);
                networking.SendToServer(message, delivery);
                NetTrace.Finish(measured, id, "send", bits / 8);
            } catch (Exception error) {
                ModLog.Once($"{id} cannot be sent: {error.Message}");
            }
        }
#else
        public delegate void SpanReceived(Client sender, uint nonce, uint seq, ReadOnlySpan<byte> payload);

        public static void ReceiveSpan(string id, int maxBytes, SpanReceived onPayload) {
            INetworkingService networking = Service();
            if (networking == null || onPayload == null || maxBytes <= 0) { return; }
            var scratch = new byte[maxBytes];
            Register(networking, id, (NetMessageReceived)((message, sender) => {
                if (!live || message == null || sender == null) { return; }
                try {
                    uint nonce = message.ReadUInt32(), seq = message.ReadUInt32();
                    if (Payload(message, scratch, out ReadOnlySpan<byte> payload)) {
                        var measured = NetTrace.Start();
                        try { onPayload(sender, nonce, seq, payload); }
                        finally { NetTrace.Finish(measured, id, "receive", payload.Length, NetTrace.Enabled ? Access.Id(sender) : 0, seq); }
                    }
                } catch (Exception error) { ModLog.Once($"{id} cannot be received: {error.Message}"); }
            }));
        }

        public static void Receive(string id, int bits, Action<Client, uint> onWord) {
            if (!Valid(bits) || onWord == null) {
                ModLog.Once($"{id} receiver has an invalid word contract");
                return;
            }
            INetworkingService networking = Service();
            if (networking == null) { return; }
            Register(networking, id, (NetMessageReceived)((message, sender) => {
                if (!live || message == null || sender == null) { return; }
                try {
                    var measured = NetTrace.Start();
                    try { onWord(sender, Read(message, bits)); }
                    finally { NetTrace.Finish(measured, id, "receive", bits / 8, NetTrace.Enabled ? Access.Id(sender) : 0); }
                } catch (Exception error) {
                    ModLog.Once($"{id} cannot be received: {error.Message}");
                }
            }));
        }

        public static void Send(string id, NetworkConnection connection, uint word, int bits,
            DeliveryMethod delivery) {
            if (connection == null) { return; }
            if (!Valid(bits)) {
                ModLog.Once($"{id} cannot carry {bits} bits");
                return;
            }
            INetworkingService networking = Sender();
            if (networking == null) { return; }
            try {
                var measured = NetTrace.Start();
                IWriteMessage message = networking.Start(id);
                Write(message, word, bits);
                networking.SendToClient(message, connection, delivery);
                NetTrace.Finish(measured, id, "send", bits / 8);
            } catch (Exception error) {
                ModLog.Once($"{id} cannot be sent: {error.Message}");
            }
        }
#endif

#if CLIENT
        private static readonly uint nonce = (uint)Environment.TickCount ^ (uint)Guid.NewGuid().GetHashCode();
        private static readonly Dictionary<string, uint> sequences = new Dictionary<string, uint>(StringComparer.Ordinal);

        private static uint NextSequence(string id) {
            sequences.TryGetValue(id, out uint sequence);
            sequence = unchecked(sequence + 1);
            sequences[id] = sequence;
            return sequence;
        }

        public static void ReceiveBytes(string id, int maxBytes, Action<byte[]> onPayload) {
            INetworkingService networking = Service();
            if (networking == null || onPayload == null || maxBytes < 0) { return; }
            Register(networking, id, (NetMessageReceived)(message => {
                if (!live || message == null) { return; }
                try {
                    uint length = message.ReadVariableUInt32();
                    int remaining = message.LengthBytes - message.BytePosition;
                    if (remaining < 0 || length > (uint)maxBytes || length > (uint)remaining) { return; }
                    var measured = NetTrace.Start();
                    try { onPayload(message.ReadBytes((int)length)); }
                    finally { NetTrace.Finish(measured, id, "receive", (int)length); }
                } catch (Exception error) {
                    ModLog.Once($"{id} cannot be received: {error.Message}");
                }
            }));
        }

        public static void SendBytes(string id, ReadOnlySpan<byte> payload, DeliveryMethod delivery) =>
            TrySendBytes(id, payload, delivery, out _, out _);

        public static bool TrySendBytes(string id, ReadOnlySpan<byte> payload, DeliveryMethod delivery, out uint session, out uint sequence) {
            session = nonce;
            sequence = 0;
            INetworkingService networking = Sender();
            if (networking == null) { return false; }
            try {
                var measured = NetTrace.Start();
                IWriteMessage message = networking.Start(id);
                message.WriteUInt32(session);
                sequence = NextSequence(id);
                message.WriteUInt32(sequence);
                WritePayload(message, payload);
                networking.SendToServer(message, delivery);
                NetTrace.Finish(measured, id, "send", payload.Length, sequence: sequence);
                return true;
            } catch (Exception error) {
                ModLog.Once($"{id} cannot be sent: {error.Message}");
                return false;
            }
        }
#else
        public delegate void BytesReceived(Client sender, uint nonce, uint seq, byte[] payload);

        public static void ReceiveBytes(string id, int maxBytes, BytesReceived onPayload) {
            INetworkingService networking = Service();
            if (networking == null || onPayload == null || maxBytes < 0) { return; }
            Register(networking, id, (NetMessageReceived)((message, sender) => {
                if (!live || message == null || sender == null) { return; }
                try {
                    uint nonce = message.ReadUInt32();
                    uint seq = message.ReadUInt32();
                    uint length = message.ReadVariableUInt32();
                    int remaining = message.LengthBytes - message.BytePosition;
                    if (remaining < 0 || length > (uint)maxBytes || length > (uint)remaining) { return; }
                    var measured = NetTrace.Start();
                    try { onPayload(sender, nonce, seq, message.ReadBytes((int)length)); }
                    finally { NetTrace.Finish(measured, id, "receive", (int)length, NetTrace.Enabled ? Access.Id(sender) : 0, seq); }
                } catch (Exception error) {
                    ModLog.Once($"{id} cannot be received: {error.Message}");
                }
            }));
        }

        public static void SendBytes(string id, NetworkConnection connection, ReadOnlySpan<byte> payload, DeliveryMethod delivery) {
            if (connection == null) { return; }
            INetworkingService networking = Sender();
            if (networking == null) { return; }
            try {
                var measured = NetTrace.Start();
                IWriteMessage message = networking.Start(id);
                WritePayload(message, payload);
                networking.SendToClient(message, connection, delivery);
                NetTrace.Finish(measured, id, "send", payload.Length);
            } catch (Exception error) {
                ModLog.Once($"{id} cannot be sent: {error.Message}");
            }
        }
#endif

        private static bool Payload(IReadMessage message, byte[] scratch, out ReadOnlySpan<byte> payload) {
            payload = default; uint length = message.ReadVariableUInt32();
            if (length > (uint)scratch.Length || (long)length * 8 > message.LengthBits - message.BitPosition) { return false; }
            int count = (int)length;
            if ((message.BitPosition & 7) == 0) {
                int start = message.BytePosition;
                if (start < 0 || count > message.Buffer.Length - start) { return false; }
                message.BitPosition += count * 8; payload = message.Buffer.AsSpan(start, count);
            } else {
                for (int i = 0; i < count; i++) { scratch[i] = message.ReadByte(); }
                payload = scratch.AsSpan(0, count);
            }
            return true;
        }

        private static void WritePayload(IWriteMessage message, ReadOnlySpan<byte> payload) {
            message.WriteVariableUInt32((uint)payload.Length);
            byte[] rented = System.Buffers.ArrayPool<byte>.Shared.Rent(Math.Max(1, payload.Length));
            try {
                payload.CopyTo(rented);
                message.WriteBytes(rented, 0, payload.Length);
            } finally {
                System.Buffers.ArrayPool<byte>.Shared.Return(rented);
            }
        }

        private static INetworkingService Sender() => live ? Service() : null;

        private static INetworkingService Service() {
            INetworkingService networking = LuaCsSetup.Instance?.Networking;
            if (networking == null) { ModLog.Once("networking service is missing"); }
            return networking;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool Valid(int bits) => bits == ByteWord || bits == ShortWord || bits == LongWord;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint Read(IReadMessage message, int bits) {
            return bits switch {
                ByteWord => message.ReadByte(),
                ShortWord => message.ReadUInt16(),
                LongWord => message.ReadUInt32(),
                _ => 0u
            };
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Write(IWriteMessage message, uint word, int bits) {
            switch (bits) {
                case ByteWord: message.WriteByte((byte)word); break;
                case ShortWord: message.WriteUInt16((ushort)word); break;
                case LongWord: message.WriteUInt32(word); break;
            }
        }
    }
}
