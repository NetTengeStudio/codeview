using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using System.Xml.Linq;
using Barotrauma;
using Barotrauma.Networking;
using HarmonyLib;
using Microsoft.Xna.Framework;
using ScpItdMod.Core;
using ScpItdMod.Docs;
using ScpItdMod.Net;

namespace ScpItdMod.People {
    public static class PeopleHost {
        public const string Channel = "scpitd.people";
        private const string ProfilesFile = "profiles.bin";
        private const string KnowledgeFile = "knowledge.bin";
        private const string RolesAsset = "Content/Data/privacy.xml";
        private const string FallbackName = "Без Имени";
        private const double TickSeconds = 0.5;

        public sealed class Record {
            public Character Character;
            public ushort Entity;
            public uint Profile;
            public ulong Account;
            public string Real;
            public string Alias;
            public string Job;
            public bool Hidden;
            public string Look;
            public uint Version;
        }

        private static ProfileStore store = new ProfileStore();
        private static Knowledge knowledge = new Knowledge();
        private static readonly Roles roles = new Roles();
        private static PeopleServer server;
        private static bool profilesFrozen;
        private static bool knowledgeFrozen;
        private static bool dirtyStore;
        private static readonly WriteBehind profilesOut = new WriteBehind(1.0, 30.0);
        private static readonly WriteBehind knowledgeOut = new WriteBehind(1.0, 30.0);
        private static readonly Stopwatch clock = Stopwatch.StartNew();
        private static DiskWriter diskWriter;

        private static readonly Dictionary<CharacterInfo, Record> pending = new Dictionary<CharacterInfo, Record>(ReferenceEqualityComparer.Instance);
        private static readonly Dictionary<ushort, Record> live = new Dictionary<ushort, Record>();
        private static readonly HashSet<ulong> dirty = new HashSet<ulong>();
        private static readonly Dictionary<ulong, bool> gmSeen = new Dictionary<ulong, bool>();
        private static readonly List<NameEntry> entries = new List<NameEntry>();
        private static readonly WireWriter reply = new WireWriter(1024);
        private static readonly WireWriter push = new WireWriter(4096);
        private static readonly WireWriter disk = new WireWriter(4096);
        private static bool looksDirty;
        private static bool lobbyDirty;
        private static double nextTick;
        private static int lastClients = -1;

        private static readonly AccessTools.FieldRef<CharacterInfo, string> originalName = OriginalNameRef();

        public static IReadOnlyDictionary<ushort, Record> Live => live;

        public static void Attach(Harmony harmony) {
            store = new ProfileStore();
            knowledge = new Knowledge();
            profilesFrozen = !Load(ProfilesFile, data => store.Load(data) == PeopleError.Ok);
            knowledgeFrozen = !Load(KnowledgeFile, data => knowledge.Load(data) == PeopleError.Ok);
            LoadRoles();
            diskWriter = new DiskWriter(SaveFile);
            server = new PeopleServer(store) {
                InUse = InUse,
                IsGm = GmAccount,
                IsDurable = StoreDurable,
                FillRoster = FillRoster
            };
            ModNet.ReceiveBytes(Channel, PeopleServer.MaxRequest, OnRequest);

            harmony.Patch(
                AccessTools.Method(typeof(Character), nameof(Character.Create), new[] {
                    typeof(CharacterPrefab), typeof(Vector2), typeof(string), typeof(CharacterInfo), typeof(ushort),
                    typeof(bool), typeof(bool), typeof(bool), typeof(RagdollParams), typeof(bool)
                }),
                prefix: ModuleHost.Bind(typeof(PeopleHost), nameof(CreatePrefix)),
                postfix: ModuleHost.Bind(typeof(PeopleHost), nameof(CreatePostfix)));
            harmony.Patch(AccessTools.Method(typeof(GameServer), nameof(GameServer.Update)),
                postfix: ModuleHost.Bind(typeof(PeopleHost), nameof(ServerUpdatePostfix)));
            harmony.Patch(AccessTools.Method(typeof(GameSession), nameof(GameSession.EndRound)),
                postfix: ModuleHost.Bind(typeof(PeopleHost), nameof(Save)));

            ModCommands.Register("scpitd_people", "lists characters of this round with real names and accounts", _ => ModLog.Message(Report()));
            ModCommands.Register("scpitd_profilelock", "on/off: forbid or allow players to edit character profiles", Lock);
        }

        public static void RoundStart() {
            gmSeen.Clear();
            pending.Clear();
            live.Clear();
            server?.Clear();
            looksDirty = true;
            lastClients = -1;
        }

        public static void Release() {
            Save();
            diskWriter?.Dispose();
            diskWriter = null;
            pending.Clear();
            live.Clear();
            dirty.Clear();
            server = null;
        }

        public static Record Of(Character character) =>
            character != null && live.TryGetValue(character.ID, out Record record) && ReferenceEquals(record.Character, character) ? record : null;

        public static bool Introduce(Character speaker, Character listener) => Grant(listener, speaker, Acquired.Introduced);

        public static bool Grant(Character viewer, Character target, Acquired how) {
            Record from = Of(target);
            Record to = Of(viewer);
            if (from == null || to == null || from.Profile == to.Profile) { return false; }
            if (!knowledge.Learn(to.Profile, from.Profile, how)) { return false; }
            dirty.Add(to.Account);
            dirtyStore = true;
            return true;
        }

        public static bool Revoke(Character viewer, Character target) {
            Record from = Of(target);
            Record to = Of(viewer);
            if (from == null || to == null || !knowledge.Forget(to.Profile, from.Profile)) { return false; }
            dirty.Add(to.Account);
            dirtyStore = true;
            return true;
        }

        private static bool Taken(string alias) {
            foreach (Record r in live.Values) {
                if (r.Character != null && !r.Character.Removed && r.Alias == alias) { return true; }
            }
            foreach (Record r in pending.Values) {
                if (r.Alias == alias) { return true; }
            }
            return false;
        }

        public static void CreatePrefix(CharacterInfo characterInfo) {
            try { Book(characterInfo); }
            catch (Exception error) {
                if (characterInfo != null) { pending.Remove(characterInfo); }
                ModLog.Error("People: identity setup failed, the character spawns with its stored name: " + error);
            }
        }

        private static void Book(CharacterInfo characterInfo) {
            if (characterInfo == null || GameMain.Server == null) { return; }
            Client client = null;
            foreach (Client c in GameMain.Server.ConnectedClients) {
                if (ReferenceEquals(c.CharacterInfo, characterInfo)) {
                    client = c;
                    break;
                }
            }
            if (client == null) { return; }
            ulong account = Access.Id(client);
            Profile profile = store.Book(account)?.Current ?? Implicit(account, characterInfo);
            if (profile == null) { return; }
            if (profile.Data.Looks.Set) {
                LooksCodec.Apply(characterInfo, profile.Data.Looks);
            } else if (characterInfo.Head != null) {
                ProfileData filled = profile.Data.Clone();
                filled.Looks = LooksCodec.Capture(characterInfo);
                if (store.Edit(account, profile.Id, profile.Version, filled, Timing.TotalTime, out _) == PeopleError.Ok) { SaveProfiles(); }
            }
            string job = characterInfo.Job?.Prefab?.Identifier.Value;
            bool hidden = roles.Hidden(job);
            string alias = hidden ? roles.Alias(job, profile.Id, Taken) ?? profile.Name : profile.Name;
            characterInfo.Name = alias;
            if (originalName != null) { originalName(characterInfo) = alias; }
            pending[characterInfo] = new Record {
                Profile = profile.Id, Account = account, Real = profile.Name, Alias = alias, Job = job, Hidden = hidden, Look = profile.Description,
                Version = profile.Version
            };
        }

        public static void CreatePostfix(Character __result, CharacterInfo characterInfo) {
            try { Register(__result, characterInfo); }
            catch (Exception error) {
                if (characterInfo != null) { pending.Remove(characterInfo); }
                ModLog.Error("People: registering a spawned character failed: " + error);
            }
        }

        private static void Register(Character __result, CharacterInfo characterInfo) {
            if (characterInfo == null || !pending.Remove(characterInfo, out Record record) || __result == null) { return; }
            record.Character = __result;
            record.Entity = __result.ID;
            foreach (Record other in live.Values) {
                if (other.Profile == record.Profile) { continue; }
                if (roles.Teammates(record.Job, other.Job) && knowledge.Learn(record.Profile, other.Profile, Acquired.Team)) { dirtyStore = true; }
                if (roles.Teammates(other.Job, record.Job) && knowledge.Learn(other.Profile, record.Profile, Acquired.Team)) { dirtyStore = true; }
            }
            live[record.Entity] = record;
            looksDirty = true;
            MarkAll();
        }

        public static void ServerUpdatePostfix() {
            if (GameMain.Server == null) { return; }
            double now = Timing.TotalTime;
            if (now < nextTick) { return; }
            nextTick = now + TickSeconds;
            Prune();
            var clients = GameMain.Server.ConnectedClients;
            for (int i = 0; i < clients.Count; i++) {
                ulong id = Access.Id(clients[i]);
                bool gm = Access.Gm(clients[i]);
                if (!gmSeen.TryGetValue(id, out bool was) || was != gm) {
                    gmSeen[id] = gm;
                    dirty.Add(id);
                }
            }
            if (clients.Count != lastClients) {
                lastClients = clients.Count;
                MarkAll();
                looksDirty = true;
                lobbyDirty = true;
            }
            if (dirty.Count > 0) {
                for (int i = 0; i < clients.Count; i++) {
                    Client client = clients[i];
                    ulong account = Access.Id(client);
                    if (!dirty.Remove(account)) { continue; }
                    SendNames(client, account);
                }
                dirty.Clear();
            }
            if (looksDirty) {
                looksDirty = false;
                SendLooks(clients);
            }
            if (lobbyDirty) {
                lobbyDirty = false;
                SendLobbyNames(clients);
            }
            if (dirtyStore && now > 0) {
                dirtyStore = false;
                SaveKnowledge();
            }
            Flush(false);
        }

        private static void SendNames(Client client, ulong account) {
            entries.Clear();
            bool gm = Access.Gm(client);
            uint viewer = ViewerProfile(account);
            foreach (Record record in live.Values) {
                if (!record.Hidden) { continue; }
                if (gm || record.Account == account || knowledge.Knows(viewer, record.Profile)) {
                    entries.Add(new NameEntry(record.Entity, record.Real));
                }
            }
            PeopleServer.WriteNames(push, PeopleOp.Names, entries);
            ModNet.SendBytes(Channel, client.Connection, push.Span, DeliveryMethod.Reliable);
        }

        private static void SendLooks(IReadOnlyList<Client> clients) {
            entries.Clear();
            foreach (Record record in live.Values) {
                if (!string.IsNullOrEmpty(record.Look)) { entries.Add(new NameEntry(record.Entity, record.Look)); }
            }
            PeopleServer.WriteNames(push, PeopleOp.Looks, entries);
            for (int i = 0; i < clients.Count; i++) {
                ModNet.SendBytes(Channel, clients[i].Connection, push.Span, DeliveryMethod.Reliable);
            }
        }

        // The lobby list shows each player's active profile name, not the
        // client's own nickname. Keyed by SessionId, which is what the lobby
        // client already has for every player row.
        private static void SendLobbyNames(IReadOnlyList<Client> clients) {
            entries.Clear();
            for (int i = 0; i < clients.Count; i++) {
                ulong account = Access.Id(clients[i]);
                Profile active = store.Get(store.Book(account)?.Active ?? 0);
                if (active == null || string.IsNullOrEmpty(active.Name)) { continue; }
                entries.Add(new NameEntry(clients[i].SessionId, active.Name));
            }
            PeopleServer.WriteNames(push, PeopleOp.Lobby, entries);
            for (int i = 0; i < clients.Count; i++) {
                ModNet.SendBytes(Channel, clients[i].Connection, push.Span, DeliveryMethod.Reliable);
            }
        }

        private static uint ViewerProfile(ulong account) {
            foreach (Record record in live.Values) {
                if (record.Account == account && record.Character != null && !record.Character.Removed) { return record.Profile; }
            }
            return store.Book(account)?.Active ?? 0;
        }

        private static void OnRequest(Client sender, uint nonce, uint seq, byte[] payload) {
            if (server == null) { return; }
            ulong account = Access.Id(sender);
            int revision = store.Revision;
            if (!server.Handle(account, sender, nonce, seq, payload, Timing.TotalTime, reply, out PeopleOp op)) { return; }
            ModNet.SendBytes(Channel, sender.Connection, reply.Span, DeliveryMethod.Reliable);
            lobbyDirty = true;
            if (store.Revision != revision) {
                SaveProfiles();
                dirty.Add(account);
            }
        }

        private static bool InUse(ulong account, uint profile) {
            foreach (Record r in live.Values) {
                if (r.Account == account && r.Profile == profile && r.Character != null && !r.Character.Removed && !r.Character.IsDead) { return true; }
            }
            return false;
        }

        private static bool GmAccount(ulong account) {
            if (GameMain.Server == null) { return false; }
            foreach (Client c in GameMain.Server.ConnectedClients) {
                if (Access.Id(c) == account) { return Access.Gm(c); }
            }
            return false;
        }

        private static void FillRoster(List<RosterEntry> roster) {
            foreach (Record r in live.Values) {
                if (roster.Count >= PeopleServer.MaxRoster) { break; }
                Profile current = store.Get(r.Profile);
                roster.Add(new RosterEntry {
                    Entity = r.Entity,
                    Callsign = r.Alias ?? "",
                    Real = r.Real ?? "",
                    Job = r.Job ?? "",
                    Account = AccountLabel(r.Account),
                    Profile = r.Profile,
                    Version = r.Version,
                    Look = r.Look ?? "",
                    Notes = current?.Data.Notes ?? "",
                    Alive = r.Character != null && !r.Character.Removed && !r.Character.IsDead
                });
            }
            roster.Sort((a, b) => string.Compare(a.Callsign, b.Callsign, StringComparison.CurrentCultureIgnoreCase));
        }

        private static string AccountLabel(ulong account) {
            if (GameMain.Server != null) {
                foreach (Client c in GameMain.Server.ConnectedClients) {
                    if (Access.Id(c) == account) { return c.Name; }
                }
            }
            return account.ToString("X16");
        }

        private static Profile Implicit(ulong account, CharacterInfo info) {
            string vanillaName = info.Name;
            var data = new ProfileData {
                Name = ProfileRules.CheckName(vanillaName) == PeopleError.Ok ? vanillaName : FallbackName,
                Looks = info.Head != null ? LooksCodec.Capture(info) : default
            };
            if (store.Create(account, data, Timing.TotalTime, out uint id, out _) != PeopleError.Ok) { return null; }
            SaveProfiles();
            return store.Get(id);
        }

        private static void Prune() {
            List<ushort> gone = null;
            foreach (KeyValuePair<ushort, Record> pair in live) {
                if (pair.Value.Character == null || pair.Value.Character.Removed) { (gone ??= new List<ushort>()).Add(pair.Key); }
            }
            if (gone == null) { return; }
            for (int i = 0; i < gone.Count; i++) { live.Remove(gone[i]); }
            looksDirty = true;
            MarkAll();
        }

        private static void MarkAll() {
            if (GameMain.Server == null) { return; }
            foreach (Client client in GameMain.Server.ConnectedClients) { dirty.Add(Access.Id(client)); }
        }

        private static void Lock(string[] args) {
            if (server == null) { return; }
            server.Locked = args.Length > 0 && args[0].Equals("on", StringComparison.OrdinalIgnoreCase);
            ModLog.Message(server.Locked ? "profile editing is locked" : "profile editing is open");
        }

        private static string Report() {
            var text = new StringBuilder();
            text.Append($"{live.Count} characters, {store.Accounts} accounts with profiles");
            foreach (Record record in live.Values) {
                text.Append($"\n#{record.Entity} {record.Alias} = {record.Real} [{record.Job ?? "no job"}] profile {record.Profile} v{record.Version} account {AccountLabel(record.Account)}{(record.Hidden ? " hidden" : "")}");
            }
            return text.ToString();
        }

        private static void LoadRoles() {
            string path = ModAssets.Resolve(RolesAsset);
            if (path.Length == 0 || !System.IO.File.Exists(path)) { return; }
            try {
                XElement root = XDocument.Load(path).Root;
                foreach (XElement hide in root?.Elements("Hide") ?? Array.Empty<XElement>()) {
                    roles.Hide(hide.Attribute("job")?.Value, hide.Attribute("alias")?.Value);
                }
                foreach (XElement team in root?.Elements("Acquainted") ?? Array.Empty<XElement>()) {
                    roles.Acquaint(team.Attribute("job")?.Value, team.Attribute("with")?.Value);
                }
            } catch (Exception error) {
                ModLog.Error($"{RolesAsset} cannot be read: {error.Message}");
            }
        }

        public static void Save() {
            SaveProfiles();
            SaveKnowledge();
            Flush(true);
        }

        private static void SaveProfiles() {
            if (!profilesFrozen) { profilesOut.Mark(Timing.TotalTime); }
        }

        private static void SaveKnowledge() {
            if (!knowledgeFrozen) { knowledgeOut.Mark(Timing.TotalTime); }
        }

        // Runs on the game thread. Serializes the due files, hands the snapshots to the disk thread and
        // applies the outcomes of earlier writes. Only a forced flush (round end, shutdown) waits for the disk.
        private static void Flush(bool force) {
            if (diskWriter == null) { return; }
            double now = Timing.TotalTime;
            if (force ? profilesOut.Dirty : profilesOut.Due(now)) {
                Submit(profilesOut, ProfilesFile, writer => store.Save(writer));
            }
            if (force ? knowledgeOut.Dirty : knowledgeOut.Due(now)) {
                Submit(knowledgeOut, KnowledgeFile, writer => knowledge.Save(writer));
            }
            if (force) { diskWriter.WaitIdle(); }
            Collect(now);
        }

        private static void Submit(WriteBehind output, string file, Action<WireWriter> serialize) {
            serialize(disk);
            diskWriter.Post(file, disk.Span.ToArray(), output.Start());
        }

        private static void Collect(double now) {
            while (diskWriter.TryTake(out DiskWriter.Outcome outcome)) {
                WriteBehind output = outcome.File == ProfilesFile ? profilesOut : knowledgeOut;
                if (outcome.Error == null) {
                    output.Done(now, outcome.Generation);
                    if (ModStorage.Moved(outcome.Root)) { ModLog.Error($"{outcome.File}: the usual folder refuses writes, saving to {outcome.Root}"); }
                } else if (output.Failed(now)) {
                    ModLog.Error($"{outcome.File} cannot be saved ({output.Failures} failed attempts, retrying in the background; changes stay in memory): {outcome.Error.Message}");
                }
            }
        }

        // Called on the disk thread. The clock is read through Stopwatch, not Timing, because Timing is game-thread state.
        private static string SaveFile(string file, byte[] data) =>
            DocFile.WriteStored(ModStorage.For(SaveUtil.DefaultSaveFolder), file, data, () => clock.Elapsed.TotalSeconds);

        private static bool StoreDurable() => profilesOut.Failures == 0 && knowledgeOut.Failures == 0;

        private static string PathOf(string file) => ModStorage.ReadPath(SaveUtil.DefaultSaveFolder, file);

        private static bool Load(string file, Func<byte[], bool> accept) {
            string path = PathOf(file);
            string used;
            try { used = DocFile.Read(path, data => accept(data) ? DocError.Ok : DocError.Corrupt, out _); }
            catch (Exception error) {
                ModLog.Error($"{path} cannot be read ({error.Message}); saving it is disabled to keep the file");
                return false;
            }
            if (used != null || !System.IO.File.Exists(path) && !System.IO.File.Exists(path + ".bak")) { return true; }
            ModLog.Error($"{path} cannot be loaded; saving it is disabled to keep the file");
            return false;
        }

        private static AccessTools.FieldRef<CharacterInfo, string> OriginalNameRef() {
            try {
                return AccessTools.FieldRefAccess<CharacterInfo, string>("<OriginalName>k__BackingField");
            } catch (Exception) {
                return null;
            }
        }
    }
}
