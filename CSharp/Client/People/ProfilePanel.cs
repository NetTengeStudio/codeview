using System;
using System.Reflection;
using Barotrauma;
using Barotrauma.Networking;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using ScpItdMod.Core;

namespace ScpItdMod.People {
    // The SCPITD character selector and editor inside the lobby. It owns its own
    // three-tab strip (professions / appearance / SCPITD profile) instead of
    // appending to the native tab row: the native row only exists when the
    // server allows appearance editing, so the third tab disappeared exactly in
    // the lobbies this feature must support. Character data still goes through
    // PeopleClient and the server remains authoritative.
    public static class ProfilePanel {
        private const string BarTag = "scpitd.profile.bar";
        private const string TabTag = "scpitd.profile.tab";
        private const string FrameTag = "scpitd.profile.frame";

        private static GUIComponent bar;
        private static GUITextBox replacedName;
        private static Vector2 replacedNameSize;
        private static bool replacedNameVisible, replacedNameIgnore;
        private static GUILayoutGroup vanillaTabs;
        private static bool vanillaTabsVisible, vanillaTabsIgnore;
        private static GUIButton vanillaJobsButton, vanillaLooksButton;
        private static bool vanillaJobsButtonFocusable, vanillaLooksButtonFocusable;
        private static GUILayoutGroup ownTabs;
        private static GUIButton tabJobs, tabLooks, tabProfile;
        private static GUITextBlock barName;
        private static GUILayoutGroup fallbackRow;
        private static GUITextBlock fallbackName;
        private static GUIButton prev;
        private static GUIButton next;
        private static GUIButton add;
        private static GUIButton edit;
        private static GUIFrame frame;
        private static GUIFrame ownedHost;
        private static GUIFrame ownedJobs;
        private static GUIFrame ownedLooks;
        private static CharacterInfo.AppearanceCustomizationMenu ownedMenu;
        private static CharacterInfo ownedMenuFor;
        private static GUITextBlock title;
        private static GUITextBlock state;
        private static GUITextBox nameBox;
        private static GUITextBox lookBox;
        private static GUITextBox notesBox;
        private static GUITextBlock lookCount;
        private static GUITextBlock notesCount;
        private static GUITextBlock status;
        private static GUIButton save;
        private static GUIButton revert;
        private static GUIButton delete;
        private static GUIButton keepMine;
        private static GUIButton takeTheirs;
        private static GUIComponent icon;
        private static CharacterInfo iconFor;
        private static uint shown;
        private static bool filling;
        private static bool librarySeeded;
        private static int seenBook = -1;
        private static int seenRevision = -1;
        private static CharacterInfo appliedLooksInfo;
        private static uint appliedLooksFor;
        private static uint appliedLooksVersion;
        private static uint sentLooksFor, sentLooksVersion;
        private static double nextRetry;
        private static readonly DraftAutosave autosave = new DraftAutosave();
        private static int lobbyNamesShown = -1;

        public static uint Shown => shown;

        // The GUI hit-tests only components on its update list. That list gets an element only through
        // AddToGUIUpdateList, and it drops every hidden element each frame. Elements that are created or
        // shown later must register themselves, or their clicks are ignored until the screen is rebuilt.
        private static void Register(GUIComponent component) {
            if (component != null && component.Visible) { component.AddToGUIUpdateList(); }
        }

        public static void Attach(Harmony harmony) {
            harmony.Patch(AccessTools.Method(typeof(NetLobbyScreen), "UpdatePlayerFrame",
                    new[] { typeof(CharacterInfo), typeof(bool), typeof(GUIComponent), typeof(bool) }),
                postfix: ModuleHost.Bind(typeof(ProfilePanel), nameof(UpdatePlayerFramePostfix)));
            harmony.Patch(AccessTools.Method(typeof(NetLobbyScreen), nameof(NetLobbyScreen.Select)),
                postfix: ModuleHost.Bind(typeof(ProfilePanel), nameof(LobbySelectPostfix)));
            // LuaCs can instantiate the package after NetLobbyScreen was already
            // selected and its player frame built. Without this the vanilla two
            // tabs stay untouched for the whole session: exactly the state the
            // live screenshot showed. Apply now, and keep a slow retry for the
            // case where the frame is rebuilt by the game.
            try {
                MethodInfo update = AccessTools.Method(typeof(NetLobbyScreen), nameof(NetLobbyScreen.Update));
                if (update != null) {
                    harmony.Patch(update, postfix: ModuleHost.Bind(typeof(ProfilePanel), nameof(LobbyTickPostfix)));
                }
            } catch (Exception error) {
                ModuleHost.ReportFailure("character panel lobby watchdog", error);
            }
            try {
                harmony.Patch(AccessTools.Method(typeof(NetLobbyScreen), nameof(NetLobbyScreen.Deselect)),
                    prefix: ModuleHost.Bind(typeof(ProfilePanel), nameof(LobbyDeselectPrefix)));
            } catch (Exception error) {
                ModuleHost.ReportFailure("character draft autosave on leaving the lobby", error);
            }
            // The player list row shows the vanilla client nickname; replace it
            // with the active profile name wherever the vanilla code sets it.
            try {
                harmony.Patch(AccessTools.Method(typeof(NetLobbyScreen), nameof(NetLobbyScreen.AddPlayer)),
                    postfix: ModuleHost.Bind(typeof(ProfilePanel), nameof(LobbyPlayerPostfix)));
                harmony.Patch(AccessTools.Method(typeof(NetLobbyScreen), nameof(NetLobbyScreen.SetPlayerNameAndJobPreference)),
                    postfix: ModuleHost.Bind(typeof(ProfilePanel), nameof(LobbyPlayerPostfix)));
            } catch (Exception error) {
                ModuleHost.ReportFailure("lobby player names", error);
            }
            LobbySelectPostfix();
        }

        public static void LobbyDeselectPrefix() => AutoSave(true);

        private static void AutoSave(bool flush) {
            try {
                uint id = PeopleClient.Active;
                Profile saved = id == 0 ? null : PeopleClient.Find(id);
                Draft d = saved == null ? null : PeopleClient.DraftOf(id);
                bool dirty = d != null && !d.Data.Same(saved.Data);
                bool blocked = d == null || !PeopleClient.Loaded || PeopleClient.Waiting || d.Conflict || d.Base != saved.Version;
                int signature = dirty ? HashCode.Combine(d.Data.Name, d.Data.Look, d.Data.Notes) : 0;
                if (!autosave.Step(Timing.TotalTime, id, dirty, blocked, signature, flush)) { return; }
                if (GameMain.Client?.CharacterInfo?.Head != null && (shown == 0 || shown == id)) {
                    d.Data.Looks = LooksCodec.Capture(GameMain.Client.CharacterInfo);
                }
                PeopleClient.Commit(d);
            } catch (Exception error) {
                ModLog.Once("character draft autosave failed: " + error.Message);
            }
        }

        public static void LobbyPlayerPostfix(NetLobbyScreen __instance, Client client) {
            if (__instance?.PlayerList == null || client == null) { return; }
            if (__instance.PlayerList.Content.FindChild(client) is GUITextBlock row) { row.Text = LobbyNameFor(client); }
        }

        private static void RefreshLobbyNames(NetLobbyScreen screen) {
            if (screen.PlayerList == null) { return; }
            foreach (GUIComponent child in screen.PlayerList.Content.Children) {
                if (child is GUITextBlock row && row.UserData is Client client) { row.Text = LobbyNameFor(client); }
            }
        }

        private static string LobbyNameFor(Client client) {
            // Our own row follows the selected profile at once; other players
            // arrive from the server, which knows their active profile.
            if (GameMain.Client != null && client.SessionId == GameMain.Client.SessionId) {
                Profile mine = PeopleClient.Find(PeopleClient.Active);
                if (mine != null && !string.IsNullOrEmpty(mine.Name)) { return mine.Name; }
            }
            return PeopleClient.LobbyName(client.SessionId, out string name) ? name : client.Name;
        }

        public static void LobbyTickPostfix(NetLobbyScreen __instance) {
            if (__instance == null || GameMain.Client == null) { return; }
            if (!ReferenceEquals(Screen.Selected, __instance)) { return; }
            if (lobbyNamesShown != PeopleClient.LobbyRevision) {
                lobbyNamesShown = PeopleClient.LobbyRevision;
                RefreshLobbyNames(__instance);
            }
            GUIComponent content = __instance.playerInfoContent;
            if (content == null) { return; }
            if (ReferenceEquals(bar?.Parent, content) && frame != null) { DropFallback(); return; }
            EnsureFallback(__instance);
            double now = Timing.TotalTime;
            if (now < nextRetry) { return; }
            nextRetry = now + 1.0;
            UpdatePlayerFramePostfix(__instance, GameMain.Client.CharacterInfo, __instance.jobPreferencesButton != null, content);
        }

        private static void DropFallback() {
            fallbackRow?.Parent?.RemoveChild(fallbackRow);
            fallbackRow = null;
            fallbackName = null;
        }

        private static void EnsureFallback(NetLobbyScreen screen) {
            if (screen.Frame == null) { return; }
            if (fallbackRow == null || !ReferenceEquals(fallbackRow.Parent, screen.Frame)) {
                DropFallback();
                fallbackRow = new GUILayoutGroup(new RectTransform(new Vector2(0.32f, 0.045f), screen.Frame.RectTransform, Anchor.TopCenter) { RelativeOffset = new Vector2(0.0f, 0.012f) },
                    isHorizontal: true, childAnchor: Anchor.CenterLeft) { Stretch = true, RelativeSpacing = 0.01f };
                Register(fallbackRow);
                new GUIButton(new RectTransform(new Vector2(0.1f, 1.0f), fallbackRow.RectTransform), "<", style: "GUIButtonFreeScale") {
                    ToolTip = "Предыдущий персонаж",
                    OnClicked = (_, _) => Step(-1)
                };
                fallbackName = new GUITextBlock(new RectTransform(new Vector2(0.56f, 1.0f), fallbackRow.RectTransform), "", textAlignment: Alignment.Center, font: GUIStyle.SubHeadingFont) { CanBeFocused = false };
                new GUIButton(new RectTransform(new Vector2(0.1f, 1.0f), fallbackRow.RectTransform), ">", style: "GUIButtonFreeScale") {
                    ToolTip = "Следующий персонаж",
                    OnClicked = (_, _) => Step(1)
                };
                new GUIButton(new RectTransform(new Vector2(0.2f, 1.0f), fallbackRow.RectTransform), "Библиотека", style: "GUIButtonSmall") {
                    ToolTip = "Персонажи, сохранённые на этом компьютере",
                    OnClicked = (_, _) => { ProfileLibraryWindow.Open(ImportLocal); return true; }
                };
            }
            Profile active = PeopleClient.Find(PeopleClient.Active);
            string text = !PeopleClient.Loaded ? "Загрузка персонажей..." : active?.Name ?? "Нет персонажа";
            if (PeopleClient.Last.Result == PeopleError.Denied) { text = "Смена закрыта админом"; }
            fallbackName.Text = ToolBox.LimitString(text, GUIStyle.SubHeadingFont, (int)Math.Max(40, fallbackName.Rect.Width - 12));
        }

        public static void Release() {
            DropFallback();
            RestoreVanilla();
            bar?.Parent?.RemoveChild(bar);
            ownTabs?.Parent?.RemoveChild(ownTabs);
            frame?.Parent?.RemoveChild(frame);
            ownedHost?.Parent?.RemoveChild(ownedHost);
            ownedJobs?.Parent?.RemoveChild(ownedJobs);
            ownedLooks?.Parent?.RemoveChild(ownedLooks);
            ownedMenu?.Dispose();
            bar = null;
            frame = null;
            ownTabs = null;
            edit = null;
            ownedHost = null;
            ownedJobs = null;
            ownedLooks = null;
            ownedMenu = null;
            ownedMenuFor = null;
            icon = null;
            seenBook = -1;
            seenRevision = -1;
            librarySeeded = false;
            appliedLooksInfo = null; appliedLooksFor = appliedLooksVersion = 0;
        }

        public static void LobbySelectPostfix() {
            NetLobbyScreen screen = GameMain.NetLobbyScreen;
            if (GameMain.Client == null || screen == null) { return; }
            PeopleClient.RequestBook();
            GUIComponent content = screen.playerInfoContent;
            if (content != null && !ReferenceEquals(bar?.Parent, content)) {
                UpdatePlayerFramePostfix(screen, GameMain.Client.CharacterInfo, screen.jobPreferencesButton != null, content);
            }
        }

        public static void UpdatePlayerFramePostfix(NetLobbyScreen __instance, CharacterInfo characterInfo, bool allowEditing, GUIComponent parent) {
            if (GameMain.Client == null || parent == null || !ReferenceEquals(parent, __instance.playerInfoContent)) { return; }
            GUITextBox vanillaName = __instance.CharacterNameBox;
            bool hasName = vanillaName != null && ReferenceEquals(vanillaName.Parent, parent);
            // The profile selector and editor are an independent subsystem. They
            // must not disappear when the server forbids appearance changes or
            // when the vanilla tabs are laid out differently. allowEditing only
            // controls the vanilla appearance editor; PeopleWire performs the
            // authoritative permission check for profile mutations.
            bool canBuildEditor = true;
            if (ReferenceEquals(bar?.Parent, parent) && canBuildEditor == (frame != null)) { return; }
            Release();
            int at = hasName ? parent.GetChildIndex(vanillaName) : 0;
            try {
                BuildBar(parent, at);
            } catch (Exception error) {
                bar?.Parent?.RemoveChild(bar);
                bar = null;
                ModuleHost.ReportFailure("character selector GUI", error);
                return;
            }
            if (canBuildEditor) {
                try {
                    BuildTabs(__instance, parent);
                    GUIFrame host = __instance.characterInfoFrame;
                    if (host == null) {
                        host = OwnedHost(__instance, parent);
                    }
                    BuildFrame(host, characterInfo);
                    // Open on the native job page, as the vanilla lobby does. The
                    // third tab is present and one click away; forcing the profile
                    // page would hide the familiar job/appearance controls.
                    SelectTab(0, false);
                } catch (Exception error) {
                    ownTabs?.Parent?.RemoveChild(ownTabs);
                    frame?.Parent?.RemoveChild(frame);
                    ownedHost?.Parent?.RemoveChild(ownedHost);
                    ownTabs = null; frame = null; ownedHost = null;
                    RestoreVanilla();
                    ModuleHost.ReportFailure("character editor GUI", error);
                }
            }
            if (frame != null && hasName) {
                replacedName = vanillaName;
                replacedNameVisible = vanillaName.Visible;
                replacedNameIgnore = vanillaName.IgnoreLayoutGroups;
                replacedNameSize = vanillaName.RectTransform.RelativeSize;
                vanillaName.Visible = false;
                vanillaName.IgnoreLayoutGroups = true;
                vanillaName.RectTransform.RelativeSize = Vector2.Zero;
            }
            if (!PeopleClient.Loaded) { PeopleClient.RequestBook(); }
            seenBook = -1;
            seenRevision = -1;
            if (shown == 0 && PeopleClient.Active != 0 && PeopleClient.DraftOf(0) == null) { shown = PeopleClient.Active; }
            Refresh(true);
        }

        private static void RestoreVanilla() {
            if (replacedName != null) {
                replacedName.Visible = replacedNameVisible;
                replacedName.IgnoreLayoutGroups = replacedNameIgnore;
                replacedName.RectTransform.RelativeSize = replacedNameSize;
                replacedName = null;
            }
            if (vanillaTabs != null) {
                vanillaTabs.Visible = vanillaTabsVisible;
                vanillaTabs.IgnoreLayoutGroups = vanillaTabsIgnore;
                vanillaTabs = null;
            }
            if (vanillaJobsButton != null) {
                vanillaJobsButton.CanBeFocused = vanillaJobsButtonFocusable;
                vanillaJobsButton = null;
            }
            if (vanillaLooksButton != null) {
                vanillaLooksButton.CanBeFocused = vanillaLooksButtonFocusable;
                vanillaLooksButton = null;
            }
            ownedMenu?.Dispose();
            ownedMenu = null;
            ownedMenuFor = null;
        }

        private static void BuildBar(GUIComponent parent, int at) {
            var row = new GUILayoutGroup(new RectTransform(new Vector2(1.0f, 0.065f), parent.RectTransform), isHorizontal: true, childAnchor: Anchor.CenterLeft) {
                Stretch = true,
                RelativeSpacing = 0.01f,
                UserData = BarTag
            };
            if (!ReferenceEquals(row.Parent, parent) || at < 0 || at >= parent.RectTransform.CountChildren) {
                throw new InvalidOperationException("Character selector insertion index is outside its parent hierarchy");
            }
            row.RectTransform.RepositionChildInHierarchy(at);
            prev = new GUIButton(new RectTransform(new Vector2(0.1f, 1.0f), row.RectTransform), "<", style: "GUIButtonFreeScale") {
                ToolTip = "Предыдущий персонаж",
                OnClicked = (_, _) => Step(-1)
            };
            barName = new GUITextBlock(new RectTransform(new Vector2(0.56f, 1.0f), row.RectTransform), "", textAlignment: Alignment.Center, font: GUIStyle.SubHeadingFont) {
                CanBeFocused = false
            };
            next = new GUIButton(new RectTransform(new Vector2(0.1f, 1.0f), row.RectTransform), ">", style: "GUIButtonFreeScale") {
                ToolTip = "Следующий персонаж",
                OnClicked = (_, _) => Step(1)
            };
            edit = new GUIButton(new RectTransform(new Vector2(0.1f, 1.0f), row.RectTransform), "Профиль", style: "GUIButtonSmall") {
                ToolTip = "Открыть профиль и описание",
                OnClicked = (_, _) => { SelectTab(2); return true; }
            };
            add = new GUIButton(new RectTransform(new Vector2(0.1f, 1.0f), row.RectTransform), "+", style: "GUIButtonFreeScale") {
                ToolTip = $"Новый персонаж (до {ProfileRules.PerAccount})",
                OnClicked = (_, _) => {
                    PeopleClient.Open(0);
                    Show(0);
                    SelectTab(2);
                    nameBox?.Select();
                    return true;
                }
            };
            new GUICustomComponent(new RectTransform(Vector2.Zero, row.RectTransform), onUpdate: (_, _) => Poll()) { IgnoreLayoutGroups = true, CanBeFocused = false };
            bar = row;
            Register(bar);
        }

        // The tab strip is ours, not the native one. When the native row exists
        // it is hidden (not removed) so release restores it exactly; its two
        // buttons are still driven through the native handlers when available.
        private static void BuildTabs(NetLobbyScreen screen, GUIComponent parent) {
            int at;
            if (screen.jobPreferencesButton?.Parent is GUILayoutGroup native &&
                screen.appearanceButton != null && ReferenceEquals(native, screen.appearanceButton.Parent) &&
                ReferenceEquals(native.Parent, parent)) {
                vanillaTabs = native;
                vanillaTabsVisible = native.Visible;
                vanillaTabsIgnore = native.IgnoreLayoutGroups;
                native.Visible = false;
                native.IgnoreLayoutGroups = true;
                // The native row can remain in the current GUI mouse-hit list
                // until Barotrauma rebuilds that list. Its hidden buttons would
                // then sit later in hierarchy order and swallow clicks intended
                // for our replacement row at the same position.
                vanillaJobsButton = screen.jobPreferencesButton;
                vanillaLooksButton = screen.appearanceButton;
                vanillaJobsButtonFocusable = vanillaJobsButton.CanBeFocused;
                vanillaLooksButtonFocusable = vanillaLooksButton.CanBeFocused;
                vanillaJobsButton.CanBeFocused = false;
                vanillaLooksButton.CanBeFocused = false;
                at = parent.GetChildIndex(native);
            } else {
                if (!ReferenceEquals(bar?.Parent, parent)) {
                    throw new InvalidOperationException("Character profile tabs have no direct child anchor");
                }
                int barIndex = parent.GetChildIndex(bar);
                if (barIndex < 0) {
                    throw new InvalidOperationException("Character selector is missing from its parent hierarchy");
                }
                at = barIndex + 1;
            }
            ownTabs = new GUILayoutGroup(new RectTransform(new Vector2(1.0f, 0.07f), parent.RectTransform), isHorizontal: true) {
                Stretch = true, RelativeSpacing = 0.015f, UserData = TabTag
            };
            if (!ReferenceEquals(ownTabs.Parent, parent) || at < 0 || at >= parent.RectTransform.CountChildren) {
                throw new InvalidOperationException("Character profile tab insertion index is outside its parent hierarchy");
            }
            ownTabs.RectTransform.RepositionChildInHierarchy(at);
            tabJobs = Tab(ownTabs, "Профессии", 0);
            tabLooks = Tab(ownTabs, "Внешность", 1);
            tabProfile = Tab(ownTabs, "Профиль", 2);
            GUITextBlock.AutoScaleAndNormalize(tabJobs.TextBlock, tabLooks.TextBlock, tabProfile.TextBlock);
            ownTabs.Recalculate();
            Register(ownTabs);
        }

        private static GUIButton Tab(GUILayoutGroup row, string text, int index) =>
            new GUIButton(new RectTransform(new Vector2(0.333f, 1.0f), row.RectTransform), text, style: "GUITabButton") {
                Selected = index == 2,
                OnClicked = (_, _) => { SelectTab(index); return true; }
            };

        private static GUIFrame OwnedHost(NetLobbyScreen screen, GUIComponent parent) {
            ownedHost = new GUIFrame(new RectTransform(Vector2.One, parent.RectTransform), "GUIFrameListBox") {
                IgnoreLayoutGroups = true, CanBeFocused = false, Visible = false
            };
            // Full-size panels must not take the hit test: they are added after the
            // tab strip, so a focusable one would swallow clicks meant for the tabs.
            ownedJobs = new GUIFrame(new RectTransform(Vector2.One, ownedHost.RectTransform), style: null) { Visible = false, CanBeFocused = false };
            ownedLooks = new GUIFrame(new RectTransform(Vector2.One, ownedHost.RectTransform), style: null) { Visible = false, CanBeFocused = false };
            return ownedHost;
        }

        private static void SelectTab(int index, bool focus = true) {
            NetLobbyScreen screen = GameMain.NetLobbyScreen;
            if (tabJobs != null) { tabJobs.Selected = index == 0; }
            if (tabLooks != null) { tabLooks.Selected = index == 1; }
            if (tabProfile != null) { tabProfile.Selected = index == 2; }
            if (frame != null) { frame.Visible = index == 2; }
            if (ownedHost != null) { ownedHost.Visible = index != 0; }
            if (ownedJobs != null) { ownedJobs.Visible = index == 0; }
            if (ownedLooks != null) { ownedLooks.Visible = index == 1; }
            if (index == 0) {
                if (screen?.JobPreferenceContainer == null || screen.appearanceFrame == null || !TryVanillaTab(() => screen.SelectJobPreferencesTab(null, null), "job tab")) {
                    HideVanillaTabs(screen);
                    BuildOwnJobs();
                }
            } else if (index == 1) {
                // The appearance editor is owned here. Driving the vanilla tab
                // throws inside the lobby (NullReferenceException in
                // AppearanceCustomizationMenu.RecreateFrameContents), so the owned
                // editor is the only path and the vanilla panels stay hidden.
                HideVanillaTabs(screen);
                BuildOwnLooks();
            } else {
                if (screen?.JobPreferenceContainer != null) { screen.JobPreferenceContainer.Visible = false; }
                if (screen?.appearanceFrame != null) { screen.appearanceFrame.Visible = false; }
                if (screen?.characterInfoFrame != null) { screen.characterInfoFrame.Visible = true; }
                if (focus) { Refresh(true); }
            }
            // Registered last, so the children that the branch above just built are included.
            // The vanilla characterInfoFrame is not registered: it is the vanilla lobby's own panel and
            // its subtree (appearance preview, job list) is heavy; our frame is hit-tested without it.
            Register(frame);
            Register(ownedHost);
            Register(ownedJobs);
            Register(ownedLooks);
        }

        // The vanilla tab switch can throw inside the lobby (observed as a
        // NullReferenceException in AppearanceCustomizationMenu.RecreateFrameContents).
        // An escaping exception aborts the click, so the tab would look dead.
        private static bool TryVanillaTab(Action select, string name) {
            try {
                select();
                return true;
            } catch (Exception error) {
                ModuleHost.ReportFailure(name, error);
                return false;
            }
        }

        private static void HideVanillaTabs(NetLobbyScreen screen) {
            if (screen == null) { return; }
            if (screen.JobPreferenceContainer != null) { screen.JobPreferenceContainer.Visible = false; }
            if (screen.appearanceFrame != null) { screen.appearanceFrame.Visible = false; }
            if (screen.characterInfoFrame != null) { screen.characterInfoFrame.Visible = false; }
        }

        private static void BuildOwnJobs() {
            if (ownedJobs == null || ownedJobs.CountChildren > 0) { return; }
            CharacterInfo info = GameMain.Client?.CharacterInfo;
            var body = new GUILayoutGroup(new RectTransform(new Vector2(0.94f, 0.94f), ownedJobs.RectTransform, Anchor.Center)) { Stretch = true, RelativeSpacing = 0.02f };
            new GUITextBlock(new RectTransform(new Vector2(1, 0.12f), body.RectTransform), "Профессия", font: GUIStyle.SubHeadingFont) { CanBeFocused = false };
            new GUITextBlock(new RectTransform(new Vector2(1, 0.12f), body.RectTransform), info?.Job?.Name ?? "—", font: GUIStyle.SubHeadingFont, textColor: GUIStyle.Green) { CanBeFocused = false };
            new GUITextBlock(new RectTransform(new Vector2(1, 0.3f), body.RectTransform),
                "Смена профессии доступна, когда сервер разрешает выбор и открывает штатную вкладку. Профиль персонажа, имя, описание и внешность редактируются на соседних вкладках.",
                font: GUIStyle.SmallFont, wrap: true) { CanBeFocused = false, TextColor = GUIStyle.TextColorNormal };
        }

        private static void BuildOwnLooks() {
            CharacterInfo info = GameMain.Client?.CharacterInfo ?? Character.Controlled?.Info;
            if (info == null || ownedLooks == null) { return; }
            if (ownedMenu != null && ReferenceEquals(ownedMenuFor, info)) { return; }
            ownedMenu?.Dispose();
            ownedMenu = null;
            ownedMenuFor = info;
            ownedLooks.ClearChildren();
            try {
                var host = new GUIFrame(new RectTransform(new Vector2(0.96f, 0.9f), ownedLooks.RectTransform, Anchor.Center), style: null);
                ownedMenu = new CharacterInfo.AppearanceCustomizationMenu(info, host);
                new GUIButton(new RectTransform(new Vector2(0.4f, 0.07f), ownedLooks.RectTransform, Anchor.BottomCenter),
                    "Применить внешность") {
                    OnClicked = (_, _) => {
                        GameMain.NetLobbyScreen?.SaveAppearance();
                        GameMain.Client?.SendCharacterInfo(GameMain.Client.PendingName);
                        return true;
                    }
                };
            } catch (Exception error) {
                ownedMenu?.Dispose();
                ownedMenu = null;
                ModuleHost.ReportFailure("character appearance editor", error);
                new GUITextBlock(new RectTransform(new Vector2(1, 0.2f), ownedLooks.RectTransform, Anchor.TopCenter),
                    "Редактор внешности недоступен: " + error.Message, wrap: true);
            }
        }

        private static void BuildFrame(GUIFrame host, CharacterInfo info) {
            frame = new GUIFrame(new RectTransform(Vector2.One, host.RectTransform), "GUIFrameListBox") { Visible = false, CanBeFocused = false, UserData = FrameTag };
            new GUIButton(new RectTransform(new Vector2(0.08f, 0.06f), frame.RectTransform, Anchor.TopRight) {
                RelativeOffset = new Vector2(-0.01f, 0.01f)
            }, "×", style: "GUIButtonSmall") {
                ToolTip = "Закрыть профиль",
                OnClicked = (_, _) => {
                    frame.Visible = false;
                    if (tabProfile != null) { tabProfile.Selected = false; }
                    if (ownedHost != null) { ownedHost.Visible = false; }
                    return true;
                }
            };
            var scroll = new GUIListBox(new RectTransform(new Vector2(0.94f, 0.94f), frame.RectTransform, Anchor.Center)) { Spacing = GUI.IntScale(6) };
            var body = new GUILayoutGroup(new RectTransform(new Vector2(1.0f, 1.0f), scroll.Content.RectTransform) {
                MinSize = new Point(0, GUI.IntScale(760))
            }) {
                Stretch = true,
                RelativeSpacing = 0.006f
            };
            var head = new GUILayoutGroup(new RectTransform(new Vector2(1.0f, 0.11f), body.RectTransform), isHorizontal: true) { Stretch = true, RelativeSpacing = 0.02f };
            var iconHost = new GUIFrame(new RectTransform(new Vector2(0.22f, 1.0f), head.RectTransform), style: null) { CanBeFocused = false };
            iconFor = info;
            info?.CreateIcon(new RectTransform(Vector2.One, iconHost.RectTransform, Anchor.Center));
            icon = iconHost;
            var heading = new GUILayoutGroup(new RectTransform(new Vector2(0.78f, 1.0f), head.RectTransform)) { Stretch = true };
            title = new GUITextBlock(new RectTransform(new Vector2(1.0f, 0.5f), heading.RectTransform), "", font: GUIStyle.SubHeadingFont) { CanBeFocused = false };
            state = new GUITextBlock(new RectTransform(new Vector2(1.0f, 0.5f), heading.RectTransform), "", font: GUIStyle.SmallFont, wrap: true) {
                CanBeFocused = false,
                TextColor = GUIStyle.TextColorNormal
            };

            Label(body, "Имя персонажа");
            nameBox = new GUITextBox(new RectTransform(new Vector2(1.0f, 0.055f), body.RectTransform)) { MaxTextLength = ProfileRules.NameMax, OverflowClip = true };
            nameBox.OnTextChanged += (_, text) => {
                Edited(d => d.Data.Name = text);
                return true;
            };
            Label(body, "Как выглядит со стороны — видно всем при наведении и осмотре");
            lookBox = Area(body, 0.20f, ProfileRules.LookMax * 2);
            lookBox.OnTextChanged += (_, text) => {
                Edited(d => d.Data.Look = text);
                return true;
            };
            lookCount = Counter(body);
            Label(body, "Личные заметки — видите только вы и ГМ");
            notesBox = Area(body, 0.18f, ProfileRules.NotesMax * 2);
            notesBox.OnTextChanged += (_, text) => {
                Edited(d => d.Data.Notes = text);
                return true;
            };
            notesCount = Counter(body);
            status = new GUITextBlock(new RectTransform(new Vector2(1.0f, 0.055f), body.RectTransform), "", font: GUIStyle.SmallFont, wrap: true) { CanBeFocused = false };

            var buttons = new GUILayoutGroup(new RectTransform(new Vector2(1.0f, 0.055f), body.RectTransform), isHorizontal: true) { Stretch = true, RelativeSpacing = 0.015f };
            save = new GUIButton(new RectTransform(new Vector2(0.25f, 1.0f), buttons.RectTransform), "Сохранить", style: "GUIButtonSmall") { OnClicked = (_, _) => Save() };
            revert = new GUIButton(new RectTransform(new Vector2(0.25f, 1.0f), buttons.RectTransform), "Отменить", style: "GUIButtonSmall") {
                ToolTip = "Вернуть сохранённую версию",
                OnClicked = (_, _) => {
                    PeopleClient.Discard(shown);
                    if (shown == 0) { Show(PeopleClient.Active); }
                    Refresh(true);
                    return true;
                }
            };
            keepMine = new GUIButton(new RectTransform(new Vector2(0.25f, 1.0f), buttons.RectTransform), "Мои правки", style: "GUIButtonSmall") {
                ToolTip = "Сохранить ваш черновик поверх изменённой версии",
                Visible = false,
                OnClicked = (_, _) => {
                    Draft d = PeopleClient.DraftOf(shown);
                    if (d != null) {
                        PeopleClient.Rebase(d);
                        PeopleClient.Commit(d);
                    }
                    Refresh(false);
                    return true;
                }
            };
            takeTheirs = new GUIButton(new RectTransform(new Vector2(0.25f, 1.0f), buttons.RectTransform), "Их версию", style: "GUIButtonSmall") {
                ToolTip = "Выбросить черновик и загрузить сохранённую на сервере версию",
                Visible = false,
                OnClicked = (_, _) => {
                    PeopleClient.Discard(shown);
                    Refresh(true);
                    return true;
                }
            };
            delete = new GUIButton(new RectTransform(new Vector2(0.25f, 1.0f), buttons.RectTransform), "Удалить", style: "GUIButtonSmall") {
                OnClicked = (_, _) => {
                    Profile p = PeopleClient.Find(shown);
                    if (p == null) { return true; }
                    var confirm = new GUIMessageBox("Удалить персонажа?", $"{p.Name} будет удалён без возможности восстановления.", new LocalizedString[] { "Удалить", "Отмена" });
                    uint id = p.Id;
                    confirm.Buttons[0].OnClicked = (_, _) => {
                        PeopleClient.Delete(id);
                        confirm.Close();
                        return true;
                    };
                    confirm.Buttons[1].OnClicked = confirm.Close;
                    return true;
                }
            };
            var libraryRow = new GUILayoutGroup(new RectTransform(new Vector2(1, 0.055f), body.RectTransform), isHorizontal: true);
            new GUIButton(new RectTransform(Vector2.One, libraryRow.RectTransform), "Из локальной библиотеки") {
                OnClicked = static (_, _) => {
                    ProfileLibraryWindow.Open(ImportLocal);
                    return true;
                }
            };
            Chain(nameBox, lookBox, notesBox);
        }

        private static void ImportLocal(ProfileData data) {
            if (!PeopleClient.Loaded || PeopleClient.Book.Count >= ProfileRules.PerAccount) {
                new GUIMessageBox("Перенос персонажа", "Дождитесь загрузки списка или освободите место в серверных профилях."); return;
            }
            void Apply() {
                Draft draft = PeopleClient.Open(0); draft.Data = data.Clone();
                Show(0); SelectTab(2);
            }
            if (!PeopleClient.Dirty(0)) { Apply(); return; }
            var confirm = new GUIMessageBox("Заменить черновик?", "Несохранённый новый персонаж в лобби будет заменён профилем из библиотеки.", new LocalizedString[] { "Заменить", "Отмена" });
            confirm.Buttons[0].OnClicked = (_, _) => { Apply(); confirm.Close(); return true; };
            confirm.Buttons[1].OnClicked = confirm.Close;
        }

        private static void Label(GUILayoutGroup body, string text) =>
            new GUITextBlock(new RectTransform(new Vector2(1.0f, 0.035f), body.RectTransform), text, font: GUIStyle.SmallFont) { CanBeFocused = false, TextColor = GUIStyle.TextColorNormal };

        private static GUITextBlock Counter(GUILayoutGroup body) =>
            new GUITextBlock(new RectTransform(new Vector2(1.0f, 0.03f), body.RectTransform), "", font: GUIStyle.SmallFont, textAlignment: Alignment.Right) { CanBeFocused = false };

        private static GUITextBox Area(GUILayoutGroup body, float height, int max) =>
            ProfileTextArea.Create(new RectTransform(new Vector2(1, height), body.RectTransform), max);

        private static void Chain(params GUITextBox[] boxes) {
            for (int i = 0; i < boxes.Length; i++) {
                int index = i;
                boxes[i].OnKeyHit += (_, key) => {
                    if (key != Keys.Tab) { return; }
                    int step = PlayerInput.IsShiftDown() ? boxes.Length - 1 : 1;
                    boxes[(index + step) % boxes.Length].Select();
                };
            }
        }

        private static bool Step(int dir) {
            if (PeopleClient.Book.Count == 0) { return true; }
            int index = Math.Max(0, PeopleClient.Book.FindIndex(p => p.Id == (shown == 0 ? PeopleClient.Active : shown)));
            index = (index + dir + PeopleClient.Book.Count) % PeopleClient.Book.Count;
            uint id = PeopleClient.Book[index].Id;
            Show(id);
            if (id != PeopleClient.Active) { PeopleClient.Select(id); }
            return true;
        }

        private static void Show(uint id) {
            shown = id;
            Refresh(true);
        }

        private static bool Save() {
            Draft d = PeopleClient.Open(shown);
            if (d == null) { return true; }
            if (GameMain.Client?.CharacterInfo?.Head != null && (shown == 0 || shown == PeopleClient.Active)) {
                d.Data.Looks = LooksCodec.Capture(GameMain.Client.CharacterInfo);
            }
            PeopleClient.Commit(d);
            Refresh(false);
            return true;
        }

        private static void Edited(Action<Draft> change) {
            if (filling) { return; }
            Draft d = PeopleClient.Open(shown);
            if (d == null) { return; }
            change(d);
            d.Error = PeopleError.Ok;
            Refresh(false);
        }

        private static void Poll() {
            PeopleClient.Tick();
            AutoSave(false);
            SeedFromLocalLibrary();
            ApplyActiveLooks();
            if (seenBook == PeopleClient.BookRevision && seenRevision == PeopleClient.Revision) { return; }
            bool book = seenBook != PeopleClient.BookRevision;
            seenBook = PeopleClient.BookRevision;
            seenRevision = PeopleClient.Revision;
            if (book) {
                shown = ProfileUiPolicy.ResolveAfterBook(shown, PeopleClient.Active, PeopleClient.LastProfile,
                    shown == 0 || PeopleClient.Find(shown) != null,
                    PeopleClient.Last.Result == PeopleError.Ok,
                    PeopleClient.DraftOf(0) != null,
                    PeopleClient.Find(PeopleClient.LastProfile) != null);
                ApplyActiveLooks();
            }
            Refresh(book);
        }

        private static void SeedFromLocalLibrary() {
            if (librarySeeded || !PeopleClient.Loaded || PeopleClient.Book.Count != 0 ||
                PeopleClient.DraftOf(0) != null) { return; }
            librarySeeded = true;
            try {
                string path = ProfileLibrary.DefaultPath;
                if (!System.IO.File.Exists(path)) { return; }
                var library = new ProfileLibrary(path);
                Profile source = library.Book?.Current;
                if (source == null || string.IsNullOrWhiteSpace(source.Data.Name)) { return; }
                // A local library is a template, not an authoritative server
                // profile. Seed a draft only when the account has no server
                // profiles yet; the player still has to save and the server still
                // validates permissions and limits.
                Draft draft = PeopleClient.Open(0);
                if (draft == null) { return; }
                draft.Data = source.Data.Clone();
                shown = 0;
                if (frame != null) { SelectTab(2); }
            } catch (Exception error) {
                ModLog.Once("character library cannot be opened: " + error.Message);
            }
        }

        private static void ApplyActiveLooks() {
            Profile active = PeopleClient.Find(PeopleClient.Active);
            CharacterInfo info = GameMain.Client?.CharacterInfo;
            if (active == null || info == null || !active.Data.Looks.Set) { return; }
            if (ReferenceEquals(appliedLooksInfo, info) && appliedLooksFor == active.Id && appliedLooksVersion == active.Version) { return; }
            // A round trip through the game's head model does not always reproduce the stored look
            // (tag order, colours outside the palette), so "changed" can stay true. The packet is sent once
            // per profile version; nothing here rebuilds the lobby, which previously made it run every frame.
            bool changed = !LooksCodec.Capture(info).Equals(active.Data.Looks);
            if (changed) { LooksCodec.Apply(info, active.Data.Looks); }
            appliedLooksInfo = info; appliedLooksFor = active.Id; appliedLooksVersion = active.Version;
            if (!changed || sentLooksFor == active.Id && sentLooksVersion == active.Version) { return; }
            sentLooksFor = active.Id; sentLooksVersion = active.Version;
            GameMain.NetLobbyScreen?.SaveAppearance();
            GameMain.Client?.SendCharacterInfo(GameMain.Client.PendingName);
        }

        private static void Refresh(bool reload) {
            if (bar == null) { return; }
            int count = PeopleClient.Book.Count;
            Profile p = PeopleClient.Find(shown);
            Draft d = PeopleClient.DraftOf(shown);
            bool creating = shown == 0 && d != null;
            bool dirty = PeopleClient.Dirty(shown);
            string name = creating ? (string.IsNullOrWhiteSpace(d.Data.Name) ? "Новый персонаж" : d.Data.Name) : p?.Name ?? (PeopleClient.Loaded ? "Нет персонажа" : "Загрузка…");
            int position = Math.Max(0, PeopleClient.Book.FindIndex(x => x.Id == shown)) + 1;
            barName.Text = ToolBox.LimitString(name, GUIStyle.SubHeadingFont, (int)Math.Max(40, barName.Rect.Width - 60)) +
                           (creating ? " *" : count > 0 ? $"  {position}/{count}" : "") + (dirty && !creating ? " *" : "");
            barName.ToolTip = name + (shown == PeopleClient.Active && !creating ? " — играете им" : "");
            prev.Enabled = next.Enabled = count > 1 || creating;
            if (edit != null) { edit.Enabled = frame != null; }
            add.Enabled = frame != null && PeopleClient.Loaded && !PeopleClient.Waiting && count < ProfileRules.PerAccount && !creating;
            if (frame == null) { return; }

            ProfileData data = d?.Data ?? p?.Data;
            if (reload || d == null) {
                filling = true;
                nameBox.Text = data?.Name ?? "";
                lookBox.Text = data?.Look ?? "";
                notesBox.Text = data?.Notes ?? "";
                filling = false;
            }
            title.Text = creating ? "Новый персонаж" : p == null ? "Персонажей пока нет" : p.Name;
            state.Text = creating ? $"Черновик. Сохраните, чтобы создать ({count}/{ProfileRules.PerAccount})."
                : p == null ? "Нажмите «+» над вкладками, чтобы создать персонажа."
                : (shown == PeopleClient.Active ? "Выбран для игры. " : "Не выбран. ") + $"Версия {p.Version}. Изменения вступят в силу при следующем появлении.";
            bool editable = creating || p != null;
            nameBox.Enabled = lookBox.Enabled = notesBox.Enabled = editable;
            int lookUnits = ProfileRules.Units(lookBox.Text);
            int lookLines = Lines(lookBox.Text);
            lookCount.Text = $"{lookUnits}/{ProfileRules.LookMax} знаков, {lookLines}/{ProfileRules.LookLines} строк";
            lookCount.TextColor = lookUnits > ProfileRules.LookMax || lookLines > ProfileRules.LookLines ? GUIStyle.Red : GUIStyle.TextColorDim;
            int notesUnits = ProfileRules.Units(notesBox.Text);
            notesCount.Text = $"{notesUnits}/{ProfileRules.NotesMax} знаков";
            notesCount.TextColor = notesUnits > ProfileRules.NotesMax ? GUIStyle.Red : GUIStyle.TextColorDim;

            bool conflict = d != null && d.Conflict;
            save.Enabled = editable && (dirty || creating) && !PeopleClient.Waiting && !conflict;
            revert.Enabled = dirty || creating;
            delete.Enabled = p != null && !creating && !PeopleClient.Waiting;
            keepMine.Visible = takeTheirs.Visible = conflict;
            save.Visible = revert.Visible = !conflict;
            (status.Text, status.TextColor) = Status(d, p, dirty, creating);
        }

        private static (string, Color) Status(Draft d, Profile p, bool dirty, bool creating) {
            if (PeopleClient.Last.Result == PeopleError.Timeout) { return (Explain(PeopleError.Timeout, ProfileField.None), GUIStyle.Orange); }
            if (PeopleClient.Waiting) { return ("Сохранение…", GUIStyle.TextColorNormal); }
            if (!PeopleClient.Durable) {
                return ("Сервер не смог записать профили на диск. Изменения действуют в текущей сессии, но после перезапуска могут пропасть.", GUIStyle.Red);
            }
            if (d != null && d.Conflict) {
                return ($"Профиль изменён в другом месте (теперь версия {p?.Version}). Ваш черновик сохранён: выберите, какую версию оставить.", GUIStyle.Orange);
            }
            if (d != null && d.Error != PeopleError.Ok) { return (Explain(d.Error, d.Field), GUIStyle.Red); }
            PeopleReply last = PeopleClient.Last;
            if (last.Result != PeopleError.Ok && PeopleClient.LastProfile == shown && !dirty) { return (Explain(last.Result, last.Field), GUIStyle.Red); }
            if (creating || dirty) { return ("Есть несохранённые изменения.", GUIStyle.Yellow); }
            return (p == null ? "" : "Сохранено.", GUIStyle.Green);
        }

        private static int Lines(string text) {
            int n = 1;
            for (int i = 0; i < text.Length; i++) {
                if (text[i] == '\n') { n++; }
            }
            return text.Length == 0 ? 0 : n;
        }

        public static string Explain(PeopleError error, ProfileField field) {
            string where = field switch {
                ProfileField.Name => "Имя: ",
                ProfileField.Look => "Описание: ",
                ProfileField.Notes => "Заметки: ",
                ProfileField.Looks => "Внешность: ",
                _ => ""
            };
            return error switch {
                PeopleError.Invalid when field == ProfileField.Name => where + "только буквы, пробел, дефис, апостроф и точка; без пробелов по краям и двойных пробелов.",
                PeopleError.Invalid => where + "недопустимые символы (управляющие, разметка ‖ или смена направления текста).",
                PeopleError.TooLarge when field == ProfileField.Name => where + $"от {ProfileRules.NameMin} до {ProfileRules.NameMax} букв.",
                PeopleError.TooLarge when field == ProfileField.Look => where + $"не больше {ProfileRules.LookMax} знаков и {ProfileRules.LookLines} строк.",
                PeopleError.TooLarge when field == ProfileField.Notes => where + $"не больше {ProfileRules.NotesMax} знаков и {ProfileRules.NotesLines} строк.",
                PeopleError.Full => $"Не больше {ProfileRules.PerAccount} персонажей. Удалите одного, чтобы создать нового.",
                PeopleError.Denied => "Редактирование персонажей сейчас закрыто администратором. Черновик сохранён.",
                PeopleError.NotFound => "Персонаж не найден: список обновлён.",
                PeopleError.Busy => "Этим персонажем сейчас играют — удалить его можно после смерти или конца раунда.",
                PeopleError.Conflict => "Версия устарела.",
                PeopleError.Timeout => "Ответ сервера не получен. Черновик сохранён; список запрашивается повторно.",
                PeopleError.RateLimited => "Слишком много запросов подряд. Повторим автоматически.",
                _ => "Сервер не принял изменения. Черновик сохранён."
            };
        }
    }
}
