using System;
using System.Collections.Generic;
using Barotrauma;
using Microsoft.Xna.Framework;
using ScpItdMod.Core;
#if SERVER
using Barotrauma.Networking;
#endif

namespace ScpItdMod.Cinematics {
    internal sealed class CinematicActorLease : IDisposable {
        internal readonly Character Character;
        internal readonly ulong Owner;
        private readonly bool aiEnabled;
        private readonly AIController originalAi;
        private CinematicCue movement;
        private Vector2 destination;
        private double remaining;
        private uint held;
        private float radiusDisplay;
        private string pose = "Stand";
        private bool faceSet; private Direction facing;
        private CinematicLocomotion body;
        // Above this scripted speed the actor runs; at or below it the actor walks at the animation's own walk speed, the only speed at which the feet do not slide.
        internal const float RunSpeed = 2f;
        private const float DisplayPerMetre = 100f;
        internal bool Done { get; private set; } = true;
        internal bool Failed { get; private set; }
        internal string Failure { get; private set; } = "";
        private bool disposed;
#if SERVER
        internal Client Player { get; private set; }
        internal bool PlayerDisconnected;
#endif
        internal CinematicActorLease(Character character, ulong owner) {
            Character = character; Owner = owner;
            originalAi = character.AIController; aiEnabled = originalAi?.Enabled == true;
        }
        internal void TakeControl() {
            if (originalAi != null) originalAi.Enabled = false;
            Character.ClearInputs();
#if SERVER
            if (CinematicActors.TakeDetached(Character, out Client held)) { Player = held; ModLog.Message("Cinematic control kept from " + held.Name + " over " + Character.Name + " #" + Character.ID); }
            else if (GameMain.Server != null) foreach (Client client in GameMain.Server.ConnectedClients) if (ReferenceEquals(client.Character, Character)) {
                Player = client;
                GameMain.Server.SetClientCharacter(client, null);
                Character.ResetNetState();
                ModLog.Message("Cinematic control taken from " + client.Name + " over " + Character.Name + " #" + Character.ID);
                break;
            }
#endif
        }
        internal void Move(CinematicCue cue, Vector2 target, float unitsPerMetre) { movement = cue; destination = target; radiusDisplay = cue.Radius * unitsPerMetre; remaining = cue.Duration; Done = false; body = default; }
        internal bool Pose(string value) {
            if (value != "Stand" && value != "Crouch" && value != "Aim") return false;
            pose = value; return true;
        }
        internal void Face(Vector2 point) {
            Character.CursorPosition = point - (Character.Submarine?.Position ?? Vector2.Zero);
            facing = point.X >= Character.WorldPosition.X ? Direction.Right : Direction.Left; faceSet = true;
            if (Character.AnimController != null) Character.AnimController.TargetDir = facing;
        }
        internal void AfterControl() {
            if (disposed || Character.Removed || Character.AnimController == null) return;
            if (faceSet) Character.AnimController.TargetDir = facing;
        }
        internal void Step(float dt) {
            if (disposed) return;
            if (Character.Removed || Character.IsDead || Character.IsIncapacitated)
            { Fail("Actor is unavailable (removed=" + Character.Removed + " dead=" + Character.IsDead + " incapacitated=" + Character.IsIncapacitated + " vitality=" + Character.Vitality.ToString("F0") + " pressureTimer=" + Character.PressureTimer.ToString("F0") + ")"); return; }
#if SERVER
            if (PlayerDisconnected || Player?.Character != null) { Fail("Actor player ownership changed"); return; }
#endif
            if (!ReferenceEquals(Character.AIController, originalAi) || originalAi?.Enabled == true) { Fail("Actor AI ownership changed"); return; }
            held = pose == "Crouch" ? Bit(InputType.Crouch) : pose == "Aim" ? Bit(InputType.Aim) : 0;
            if (movement == null || Done) return;
            remaining -= dt;
            Vector2 delta = destination - Character.WorldPosition;
            float radius = radiusDisplay;
            if (Character.InWater) {
                if (delta.LengthSquared() <= radius * radius) { Done = true; movement = null; return; }
                if (remaining <= 0) { Fail("Actor movement timed out"); return; }
                if (MathF.Abs(delta.X) > radius) held |= Bit(delta.X < 0 ? InputType.Left : InputType.Right);
                if (MathF.Abs(delta.Y) > radius) held |= Bit(delta.Y < 0 ? InputType.Down : InputType.Up);
                Face(destination);
                return;
            }
            // On the ground only the line matters: the height of the target above the floor is not a distance the person can walk.
            bool run = movement.Speed > RunSpeed;
            float cruise = Character.AnimController.GetCurrentSpeed(run);
            body.Step(dt, delta.X / DisplayPerMetre, cruise, radius / DisplayPerMetre);
            if (body.Settled) { Done = true; movement = null; return; }
            if (remaining <= 0) { Fail("Actor movement timed out"); return; }
            if (MathF.Abs(body.Speed) > 0) held |= Bit(body.Speed < 0 ? InputType.Left : InputType.Right);
            if (run) held |= Bit(InputType.Run);
            // Near the target the person keeps the way they face: a few units of overshoot must not turn them round.
            if (MathF.Abs(delta.X) > radius * 3f) Face(destination);
        }
        internal bool Held(InputType input) => (held & Bit(input)) != 0;
        internal void BeforeControl() {
            if (disposed || Failed || Character.Removed) return;
            Character.SetInput(InputType.Aim, false, Held(InputType.Aim));
            Character.SetInput(InputType.Crouch, false, Held(InputType.Crouch));
            if (Character is AICharacter) {
                Character.AnimController.TargetMovement = Character.GetTargetMovement();
                Character.AnimController.IgnorePlatforms = Character.AnimController.TargetMovement.Y < -.1f;
            }
        }
        private void Fail(string reason) { Failure = reason; Failed = true; Done = true; held = 0; }
        private static uint Bit(InputType input) => (uint)input < 32 ? 1u << (int)input : 0;
        public void Dispose() {
            if (disposed) return;
            disposed = true; held = 0; movement = null;
            CinematicActors.Remove(this);
            if (!Character.Removed) {
                if (ReferenceEquals(Character.AIController, originalAi) && originalAi?.Enabled != true) {
                    Character.ClearInputs();
                    if (originalAi != null) originalAi.Enabled = aiEnabled;
                }
#if SERVER
                if (Player != null && !PlayerDisconnected) CinematicActors.Returning(Player, Character);
#endif
            }
        }
    }

    internal static class CinematicActors {
        private static readonly Dictionary<Character, CinematicActorLease> owners = new Dictionary<Character, CinematicActorLease>();
        internal static int Count => owners.Count;
        internal static bool Available(Character character) => character != null && !character.Removed && !character.IsDead && !owners.ContainsKey(character);
#if SERVER
        // A player's character is detached from the player BEFORE it is seated in the helicopter. The vehicle remembers who controlled a rider at
        // the moment of seating and drops a rider whose player has stopped controlling it (a safeguard against abandoned characters); seating first and
        // detaching afterwards threw the player out of the seat, in mid-air at the start point, a second after the scene began.
        private static readonly Dictionary<Character, Client> detached = new Dictionary<Character, Client>();
        internal static void Detach(Character character) {
            if (character == null || detached.ContainsKey(character) || owners.ContainsKey(character) || GameMain.Server == null) return;
            foreach (Client client in GameMain.Server.ConnectedClients) if (ReferenceEquals(client.Character, character)) {
                detached[character] = client;
                GameMain.Server.SetClientCharacter(client, null);
                character.ResetNetState();
                ModLog.Message("Cinematic control detached from " + client.Name + " over " + character.Name + " #" + character.ID + " before seating");
                return;
            }
        }
        internal static bool TakeDetached(Character character, out Client client) => detached.Remove(character, out client);
        internal static void ReturnDetached(Character only = null) {
            if (only != null) { if (detached.Remove(only, out Client one)) Returning(one, only); return; }
            var rest = new List<KeyValuePair<Character, Client>>(detached);
            detached.Clear();
            foreach (var pair in rest) Returning(pair.Value, pair.Key);
        }
        private sealed class Retry { internal Client Client; internal Character Character; internal int Attempt; internal double Due; }
        private static readonly List<Retry> retries = new List<Retry>();
        internal static void Returning(Client client, Character character) => TryReturn(client, character, 0);
        private static void TryReturn(Client client, Character character, int attempt) {
            try { ReturnNow(client, character); }
            catch (Exception error) {
                float delay = CinematicControlRetry.Delay(attempt);
                if (delay < 0) { ModLog.Error("Cinematic control could not be returned to " + client?.Name + " after " + attempt + " retries: " + error.Message); return; }
                retries.Add(new Retry { Client = client, Character = character, Attempt = attempt + 1, Due = Timing.TotalTime + delay });
            }
        }
        internal static void TickRetries() {
            if (retries.Count == 0) return;
            double now = Timing.TotalTime;
            for (int i = retries.Count - 1; i >= 0; i--) {
                Retry retry = retries[i];
                if (retry.Due > now) continue;
                retries.RemoveAt(i);
                TryReturn(retry.Client, retry.Character, retry.Attempt);
            }
        }
        private static void ReturnNow(Client client, Character character) {
            bool connected = false, taken = false;
            if (GameMain.Server != null) foreach (Client other in GameMain.Server.ConnectedClients) {
                if (ReferenceEquals(other, client)) connected = true;
                else if (ReferenceEquals(other.Character, character)) taken = true;
            }
            var seen = new CinematicControlObservation {
                ClientConnected = connected, CharacterUsable = character is { Removed: false, IsDead: false },
                ClientOwnsCharacter = connected && ReferenceEquals(client.Character, character),
                ClientHasOther = connected && client.Character != null && !ReferenceEquals(client.Character, character), CharacterHasOther = taken
            };
            CinematicControlVerdict verdict = CinematicControlReturn.Evaluate(seen, out CinematicControlGiveUp reason);
            if (verdict == CinematicControlVerdict.GiveUp) { ModLog.Message("Cinematic control recovery: " + client?.Name + " " + reason); return; }
            if (verdict == CinematicControlVerdict.Restore) GameMain.Server.SetClientCharacter(client, character);
            if (!ReferenceEquals(client.Character, character)) throw new InvalidOperationException("Native character control assignment failed");
            ModLog.Message("Cinematic control returned to " + client.Name + " via native control event");
        }
        internal static void Reset() { ReturnDetached(); }
#endif
        internal static void AfterControl(Character character) { if (owners.TryGetValue(character, out CinematicActorLease lease)) lease.AfterControl(); }
        internal static void BeforeControl(Character character) { if (owners.TryGetValue(character, out CinematicActorLease lease)) lease.BeforeControl(); }
#if SERVER
        internal static Character Disconnected(Client client) {
            foreach (CinematicActorLease lease in owners.Values) if (ReferenceEquals(lease.Player, client)) { lease.PlayerDisconnected = true; return lease.Character; }
            return client.Character;
        }
#endif
        internal static CinematicActorLease Acquire(Character character, ulong owner) {
            if (owners.ContainsKey(character)) throw new InvalidOperationException("Character already belongs to a cinematic");
            var lease = new CinematicActorLease(character, owner);
            owners.Add(character, lease);
            try { lease.TakeControl(); }
            catch { lease.Dispose(); throw; }
            return lease;
        }
        internal static void Remove(CinematicActorLease lease) { if (owners.TryGetValue(lease.Character, out var current) && ReferenceEquals(current, lease)) owners.Remove(lease.Character); }
        internal static bool Input(Character character, InputType input, bool hit, out bool result) {
            if (owners.TryGetValue(character, out CinematicActorLease lease)) { result = !hit && lease.Held(input); return true; }
#if CLIENT
            if (ReferenceEquals(character, Character.Controlled) && CinematicClient.BlocksInput) { result = false; return true; }
#endif
            result = false; return false;
        }
    }
}
