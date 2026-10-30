using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Networking;
using HarmonyLib;
using ScpItdMod.Core;

namespace ScpItdMod.People {
    public static class Names {
        private struct Cached {
            public string Real;
            public string Callsign;
            public bool Masked;
            public bool Gm;
            public bool Self;
            public int Revision;
            public Designation Sight;
        }

        private static readonly AccessTools.FieldRef<ChatMessage, string> senderName =
            AccessTools.FieldRefAccess<ChatMessage, string>(nameof(ChatMessage.SenderName));
        private static readonly AccessTools.FieldRef<ChatMessage, Client> senderClient =
            AccessTools.FieldRefAccess<ChatMessage, Client>(nameof(ChatMessage.SenderClient));
        private static readonly Dictionary<ushort, Cached> cache = new Dictionary<ushort, Cached>();
        private static bool resolving;

        public static void Attach(Harmony harmony) {
            harmony.Patch(AccessTools.PropertyGetter(typeof(CharacterInfo), nameof(CharacterInfo.DisplayName)),
                postfix: ModuleHost.Bind(typeof(Names), nameof(DisplayNamePostfix)));
            harmony.Patch(AccessTools.Method(typeof(ChatBox), nameof(ChatBox.AddMessage)),
                prefix: ModuleHost.Bind(typeof(Names), nameof(AddMessagePrefix)));
            harmony.Patch(AccessTools.Method(typeof(TabMenu), nameof(TabMenu.SelectInfoFrameTab)),
                prefix: ModuleHost.Bind(typeof(Names), nameof(SelectTabPrefix)));
            harmony.Patch(AccessTools.Method(typeof(TabMenu), "CreateInfoFrame"),
                postfix: ModuleHost.Bind(typeof(Names), nameof(CreateInfoFramePostfix)));
        }

        public static void Reset() => cache.Clear();

        public static bool Gm => GameMain.Client == null || GameMain.Client.HasPermission(ClientPermissions.ConsoleCommands);

        public static Designation Of(Character target, Sense sense) {
            if (target?.Info == null) { return new Designation(Shown.Callsign, target?.Name ?? ""); }
            if (sense == Sense.Sight) { return Sight(target); }
            return Identity.Label(sense, Facts(target, out string real), real, target.Info.Name);
        }

        public static string Sighted(Character target) => Of(target, Sense.Sight).Text;

        public static string Witnessed(Character target, Sense sense) {
            if (target?.Info == null) { return target?.Name ?? ""; }
            bool known = PeopleClient.RealName(target.ID, out string real);
            var facts = new IdentityFacts { Self = ReferenceEquals(target, Character.Controlled), Gm = false, Known = known, Masked = target.HideFace, Visible = sense == Sense.Sight };
            return Identity.Label(sense, facts, real, target.Info.Name).Text;
        }

        private static IdentityFacts Facts(Character target, out string real) {
            Character me = Character.Controlled;
            bool self = ReferenceEquals(target, me);
            bool known = PeopleClient.RealName(target.ID, out real);
            return new IdentityFacts {
                Self = self,
                Gm = Gm,
                Known = known,
                Masked = target.HideFace,
                Visible = self || me == null || target.hudInfoVisible
            };
        }

        private static Designation Sight(Character target) {
            bool known = PeopleClient.RealName(target.ID, out string real);
            string callsign = target.Info.Name;
            bool masked = target.HideFace;
            bool gm = Gm;
            bool self = ReferenceEquals(target, Character.Controlled);
            int revision = PeopleClient.Revision;
            if (cache.TryGetValue(target.ID, out Cached c) && c.Revision == revision && c.Masked == masked && c.Gm == gm && c.Self == self &&
                ReferenceEquals(c.Callsign, callsign) && ReferenceEquals(c.Real, real)) {
                return c.Sight;
            }
            var facts = new IdentityFacts { Self = self, Gm = gm, Known = known, Masked = masked, Visible = true };
            c = new Cached { Real = real, Callsign = callsign, Masked = masked, Gm = gm, Self = self, Revision = revision, Sight = Identity.Label(Sense.Sight, facts, real, callsign) };
            if (cache.Count > 512) { cache.Clear(); }
            cache[target.ID] = c;
            return c.Sight;
        }

        public static void DisplayNamePostfix(CharacterInfo __instance, ref string __result) {
            if (resolving) { return; }
            Character character = __instance.Character;
            if (character == null) { return; }
            resolving = true;
            try {
                __result = Sight(character).Text;
            } finally {
                resolving = false;
            }
        }

        public static void AddMessagePrefix(ChatMessage message) {
            Character sender = message?.SenderCharacter;
            if (sender?.Info == null || string.IsNullOrEmpty(message.SenderName)) { return; }
            Sense sense = message.Type switch {
                ChatMessageType.Radio => Sense.Radio,
                ChatMessageType.Order => Sense.Radio,
                ChatMessageType.Default => Sense.Voice,
                _ => Sense.Sight
            };
            if (message.Type != ChatMessageType.Default && message.Type != ChatMessageType.Radio && message.Type != ChatMessageType.Order) { return; }
            senderName(message) = Of(sender, sense).Text;
            if (!Gm && GameMain.GameSession != null) { senderClient(message) = null; }
        }

        public static void SelectTabPrefix(ref TabMenu.InfoFrameTab selectedTab) {
            if (selectedTab == TabMenu.InfoFrameTab.Crew && !Gm) { selectedTab = TabMenu.InfoFrameTab.Mission; }
        }

        public static void CreateInfoFramePostfix(TabMenu __instance) {
            if (Gm) { return; }
            foreach (GUIButton button in __instance.tabButtons) {
                if (button.UserData is TabMenu.InfoFrameTab tab && tab == TabMenu.InfoFrameTab.Crew) { button.Visible = false; }
            }
        }
    }
}
