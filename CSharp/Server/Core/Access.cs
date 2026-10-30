using System;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading;
using Barotrauma.Networking;

namespace ScpItdMod.Core {
    public static class Access {
        private sealed class Identity {
            public AccountId Account;
            public ulong Value;
            public bool Ready;
            // The connection object, not the recyclable SessionId, owns a guest identity.
            public readonly ulong Guest = 0x8000000000000000UL |
                ((guestEpoch + (ulong)Interlocked.Increment(ref nextGuest)) & 0x7FFFFFFFFFFFFFFFUL);
        }

        private static readonly ConditionalWeakTable<Client, Identity> identities = new ConditionalWeakTable<Client, Identity>();
        private static readonly ulong guestEpoch = NewGuestEpoch();
        private static long nextGuest;

        private static ulong NewGuestEpoch() {
            byte[] bytes = RandomNumberGenerator.GetBytes(sizeof(ulong));
            return BitConverter.ToUInt64(bytes, 0) & 0x7FFFFFFFFFFFFFFFUL;
        }
        public static Func<Client, ulong> Roles = _ => 0;

        public static bool Gm(Client client) => client != null && client.HasPermission(ClientPermissions.ConsoleCommands);

        public static ulong Id(Client client) {
            if (client == null) { return 0; }
            client.AccountId.TryUnwrap(out AccountId account);
            Identity identity = identities.GetValue(client, static _ => new Identity());
            if (account == null) { return identity.Guest; }
            if (identity.Ready && identity.Account == account) { return identity.Value; }
            // Preserve existing authenticated-account IDs for persisted document ownership.
            string key = account.StringRepresentation ?? "";
            ulong h = 14695981039346656037UL;
            for (int i = 0; i < key.Length; i++) {
                h ^= key[i];
                h *= 1099511628211UL;
            }
            identity.Account = account;
            identity.Value = h == 0 ? 1 : h;
            identity.Ready = true;
            return identity.Value;
        }
    }
}
