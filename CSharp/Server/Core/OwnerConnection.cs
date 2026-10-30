using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Barotrauma;
using Barotrauma.Networking;

namespace ScpItdMod.Core {
    public static class OwnerConnection {
        private const double RetryDelay = 0.25;
        private const int Capacity = CharacterSlots.Capacity;

        private static readonly Client[] clients = new Client[Capacity];
        private static readonly NetworkConnection[] connections = new NetworkConnection[Capacity];
        private static readonly uint[] generations = new uint[Capacity];
        private static readonly double[] retryAt = new double[Capacity];

        public static void Reset() {
            Array.Clear(clients, 0, Capacity);
            Array.Clear(connections, 0, Capacity);
            Array.Clear(generations, 0, Capacity);
            Array.Clear(retryAt, 0, Capacity);
        }

        public static NetworkConnection Of(Character character) {
            if (character == null) { return null; }
            int slot = CharacterRuntime.Slots.IndexOf(character);
            if (slot >= 0) {
                return Of(character, slot, CharacterRuntime.Slots.Generation(slot));
            }
            return Probe(character, -1, 0u);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static NetworkConnection Of(Character character, int slot, uint generation) {
            if (character == null) { return null; }
            if ((uint)slot >= Capacity || generation == 0u) { return Probe(character, -1, 0u); }

            if (generations[slot] == generation) {
                Client cached = clients[slot];
                if (cached != null && ReferenceEquals(cached.Character, character)) {
                    return connections[slot];
                }
                if (cached == null && Timing.TotalTime < retryAt[slot]) { return null; }
            }
            return Probe(character, slot, generation);
        }

        private static NetworkConnection Probe(Character character, int slot, uint generation) {
            IReadOnlyList<Client> connected = GameMain.Server?.ConnectedClients;
            if (connected != null) {
                for (int i = 0; i < connected.Count; i++) {
                    Client client = connected[i];
                    if (!ReferenceEquals(client.Character, character)) { continue; }
                    NetworkConnection connection = client.Connection;
                    if ((uint)slot < Capacity && generation != 0u) {
                        clients[slot] = client;
                        connections[slot] = connection;
                        generations[slot] = generation;
                        retryAt[slot] = 0.0;
                    }
                    return connection;
                }
            }

            if ((uint)slot < Capacity && generation != 0u) {
                clients[slot] = null;
                connections[slot] = null;
                generations[slot] = generation;
                retryAt[slot] = Timing.TotalTime + RetryDelay;
            }
            return null;
        }
    }
}
