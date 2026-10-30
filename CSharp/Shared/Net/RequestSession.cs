using System.Runtime.CompilerServices;

namespace ScpItdMod.Net {
    public struct RequestSession {
        private object connection;
        private Replay replay, updates;
        public uint Nonce { get; private set; }
        public bool Accept(object owner, uint nonce, uint sequence, out bool changed) =>
            Accept(owner, nonce, sequence, false, out changed);
        public bool Accept(object owner, uint nonce, uint sequence, bool update, out bool changed) {
            changed = false;
            if (owner == null) { return false; }
            if (!ReferenceEquals(connection, owner)) {
                connection = owner; Nonce = nonce; replay.Reset(); updates.Reset(); changed = true;
            } else if (Nonce != nonce) { return false; }
            return update ? updates.Accept(sequence) : replay.Accept(sequence);
        }
    }

    public sealed class RequestSessions {
        private sealed class Connection {
            internal RequestSession Requests;
        }
        private readonly ConditionalWeakTable<object, Connection> connections = new ConditionalWeakTable<object, Connection>();
        public uint Nonce { get; private set; }

        public bool Accept(object owner, uint nonce, uint sequence, out bool changed) =>
            Accept(owner, nonce, sequence, false, out changed);
        public bool Accept(object owner, uint nonce, uint sequence, bool update, out bool changed) {
            changed = false;
            if (owner == null) return false;
            Connection connection = connections.GetValue(owner, static _ => new Connection());
            if (!connection.Requests.Accept(owner, nonce, sequence, update, out changed)) return false;
            Nonce = connection.Requests.Nonce;
            return true;
        }

        public void Clear() { connections.Clear(); Nonce = 0; }
    }
}
