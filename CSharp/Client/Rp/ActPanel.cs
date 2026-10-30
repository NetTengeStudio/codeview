using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Items.Components;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using ScpItdMod.Core;
using ScpItdMod.Inv;
using ScpItdMod.Paper;
using ScpItdMod.People;
using ScpItdMod.Ui;

namespace ScpItdMod.Rp {
    public sealed class ActPanel : RetroWindow {
        public const Keys Key = Keys.N;
        public const string KeyName = "N";
        private const double RefreshSeconds = 0.15;

        private static ActPanel instance;
        private static readonly Dictionary<byte, double> lastEmote = new Dictionary<byte, double>();

        private readonly ActMenu menu = new ActMenu();
        private Entity target;
        private ushort presetItem;
        private RpAct presetAct;
        private double nextRefresh;
        private bool awaitingEmote;
        private byte requestedEmote;

        public override string Title => "";

        public static bool Shown => instance != null && instance.IsOpen;

        public static void Hide() => instance?.Close();

        public static void Attach(Harmony harmony) {
            harmony.Patch(AccessTools.Method(typeof(Character), nameof(Character.ControlLocalPlayer)),
                postfix: ModuleHost.Bind(typeof(ActPanel), nameof(ControlPostfix)));
            EmoteView.Heard += ConfirmEmote;
        }

        public static void Release() { Reset(); EmoteView.Heard -= ConfirmEmote; }

        private static void ConfirmEmote(Character actor, EmoteDef def, int variant) {
            if (!ReferenceEquals(actor, Character.Controlled)) { return; }
            lastEmote[def.Index] = EmoteView.StartTime(actor);
            if (instance != null && instance.awaitingEmote && instance.requestedEmote == def.Index) { instance.Close(); }
        }

        protected override void Closed() => awaitingEmote = false;

        // The server answers a refused emote with a note only. The panel stops waiting and rebuilds its list on the next refresh,
        // so the reason shows on the entry instead of the panel sitting in a pending state.
        public static void EmoteRefused() {
            if (instance == null || !instance.awaitingEmote) { return; }
            instance.awaitingEmote = false;
            instance.nextRefresh = 0;
        }

        public static void Reset() {
            instance?.Close();
            lastEmote.Clear();
        }

        public static void ControlPostfix(Character __instance) {
            if (!ReferenceEquals(__instance, Character.Controlled)) { return; }
            if (!PlayView.Typing && !GUI.InputBlockingMenuOpen && PlayerInput.KeyHit(Key) && !Targeting.Active) {
                if (RpClient.Current != null && RpClient.Current.Mine) {
                    RpClient.Cancel();
                } else if (EmoteView.Busy(__instance) && !Shown) {
                    RpClient.Cancel();
                } else if (Shown) {
                    instance.Close();
                } else {
                    OpenFor(__instance, Pick(__instance), 0, RpAct.None);
                }
            }
            if (Shown) {
                __instance.ClearInput(InputType.Aim);
                __instance.ClearInput(InputType.Use);
                __instance.ClearInput(InputType.Shoot);
                if (__instance.IsDead || __instance.IsIncapacitated) { instance.Close(); }
            }
        }

        public static void OpenFor(Character me, Entity target, ushort item, RpAct act) {
            instance ??= new ActPanel();
            InvUi.Close();
            instance.target = target;
            instance.presetItem = item;
            instance.presetAct = act;
            instance.menu.Tab = target is Character ? ActTab.Person : target is Item ? ActTab.Place : ActTab.Hands;
            instance.menu.Selected = 0;
            instance.menu.Scroll = 0.0f;
            instance.nextRefresh = 0;
            instance.Rebuild(me);
            instance.menu.FirstNonEmpty();
            if (act != RpAct.None) {
                List<ActEntry> list = instance.menu.Of(ActTab.Person);
                for (int i = 0; i < list.Count; i++) {
                    if (list[i].Act == act) {
                        instance.menu.Tab = ActTab.Person;
                        instance.menu.Selected = i;
                    }
                }
            }
            float scale = GUI.Scale;
            int rows = 6;
            for (int t = 0; t < ActMenu.Tabs; t++) { rows = Math.Max(rows, instance.menu.Of((ActTab)t).Count); }
            float w = MathF.Round(440.0f * scale);
            float h = MathF.Round((Math.Min(rows, 14) * Retro.RowPx + 170.0f) * scale);
            instance.Rect = new Box(GameMain.GraphicsWidth - w - MathF.Round(40.0f * scale), MathF.Round(GameMain.GraphicsHeight * 0.5f - h * 0.5f), w, h);
            instance.Open();
        }

        public static Entity Pick(Character me) {
            if (me.FocusedCharacter != null && !me.FocusedCharacter.Removed && !RpClient.Concealed(me.FocusedCharacter)) { return me.FocusedCharacter; }
            if (me.FocusedItem != null && RpWorld.IsHideout(me.FocusedItem)) { return me.FocusedItem; }
            return Under(me);
        }

        public static Character Under(Character me) {
            Camera cam = GameMain.GameScreen?.Cam;
            if (cam == null) { return null; }
            Vector2 cursor = cam.ScreenToWorld(PlayerInput.MousePosition);
            Character best = null;
            float bestDistance = float.MaxValue;
            var humans = Humans.All;
            for (int i = 0; i < humans.Count; i++) {
                Character c = humans[i];
                if (ReferenceEquals(c, me) || c.Removed || !c.Enabled || RpClient.Concealed(c)) { continue; }
                if (!CharacterPicking.Contains(c, cursor)) { continue; }
                float d = Vector2.DistanceSquared(cursor, c.WorldPosition);
                if (d < bestDistance) {
                    bestDistance = d;
                    best = c;
                }
            }
            return best;
        }

        public override void Tick(float dt) {
            Character me = Character.Controlled;
            if (me == null) {
                Close();
                return;
            }
            if (Timing.TotalTime >= nextRefresh) {
                nextRefresh = Timing.TotalTime + RefreshSeconds;
                Rebuild(me);
            }
            if (PlayView.Typing) { return; }
            if (PlayerInput.KeyHit(Keys.Down)) { menu.Step(1); }
            if (PlayerInput.KeyHit(Keys.Up)) { menu.Step(-1); }
            if (PlayerInput.KeyHit(Keys.Right)) { menu.TabStep(1); }
            if (PlayerInput.KeyHit(Keys.Left)) { menu.TabStep(-1); }
            if (PlayerInput.KeyHit(Keys.Back)) { menu.Back(); }
            if (PlayerInput.KeyHit(Keys.Enter)) { Run(menu.Activate(menu.Selected)); }
            for (int i = 0; i < 9; i++) {
                if (PlayerInput.KeyHit(Keys.D1 + i)) { Run(menu.Activate(i)); }
            }
            if (PlayerInput.SecondaryMouseButtonClicked() && RetroWindows.MouseOver) {
                if (menu.Stage == ActStage.PickItem) { menu.Back(); } else { Close(); }
            }
        }

        public override void Body(ICanvas c, UiState ui, RetroFaces f, Box body) { }

        protected override bool Custom => true;

        protected override void Full(ICanvas c, UiState ui, RetroFaces f) => Run(menu.Draw(c, ui, f, Rect, 1.0f, KeyName));

        private void Run(in ActIntent intent) {
            if (!intent.Valid) { return; }
            Character me = Character.Controlled;
            if (me == null) { return; }
            if (intent.Kind == ActKind.Emote) {
                EmoteDef def = EmoteView.Catalog[intent.Emote];
                if (def == null) { return; }
                awaitingEmote = true; requestedEmote = def.Index;
                RpClient.Emote(def);
                return;
            }
            Item item = intent.Item == 0 ? null : Entity.FindEntityByID(intent.Item) as Item;
            switch (intent.Act) {
                case RpAct.Examine:
                    if (target is Character who) { ExamineWindow.Show(who); }
                    break;
                case RpAct.Present when item != null && PaperModule.IsPaper(item) && target is Character viewer:
                    PaperClient.Show(item.ID, viewer.ID);
                    break;
                default:
                    RpClient.Act(intent.Act, RpRules.OnSelf(intent.Act) ? null : target, item);
                    break;
            }
            Close();
        }

        private void Rebuild(Character me) {
            int keepTab = (int)menu.Tab;
            int keepSel = menu.Selected;
            int keepArmed = menu.Armed;
            ActStage stage = menu.Stage;
            ActEntry pending = menu.PendingEntry;
            menu.Clear();
            menu.Stage = stage;
            menu.Tab = (ActTab)keepTab;
            menu.Armed = keepArmed;
            Item preset = presetItem == 0 ? null : Entity.FindEntityByID(presetItem) as Item;
            if (preset != null && !RpWorld.Owns(me, preset)) {
                preset = null;
                presetItem = 0;
            }
            menu.PresetItem = presetItem;
            menu.ItemLabel = preset?.Name ?? "";
            bool busy = RpClient.Current != null && RpClient.Current.Mine;
            Character person = target as Character;
            if (target != null && target.Removed) {
                menu.TargetLabel = "цель потеряна";
                menu.TargetNote = "";
            } else if (person != null) {
                menu.TargetLabel = Names.Sighted(person);
                float m = Vector2.Distance(me.WorldPosition, person.WorldPosition) / 100.0f;
                menu.TargetNote = m.ToString("0.0") + " м";
            } else if (target is Item place) {
                menu.TargetLabel = place.Name;
                menu.TargetNote = "";
            } else {
                menu.TargetLabel = "нет — наведите курсор на человека";
                menu.TargetNote = "";
            }

            if (person != null && !person.Removed) {
                AddRp(me, ActTab.Person, RpAct.Examine, preset, busy, false, false);
                AddRp(me, ActTab.Person, RpAct.Introduce, preset, busy, false, false);
                AddRp(me, ActTab.Person, RpAct.Give, preset, busy, true, false);
                AddRp(me, ActTab.Person, RpAct.Present, preset, busy, true, false);
                AddRp(me, ActTab.Person, RpAct.Search, preset, busy, false, true);
                if (person.LockHands) {
                    AddRp(me, ActTab.Person, RpAct.Free, preset, busy, false, true);
                } else {
                    AddRp(me, ActTab.Person, RpAct.Detain, preset != null && RpWorld.IsCuffs(preset) ? preset : null, busy, true, true);
                }
                if (Med.MedWorld.CanDiagnose(me)) { AddRp(me, ActTab.Person, RpAct.Diagnose, preset, busy, false, false); }
            }
            if (target is Item hideout && !hideout.Removed && RpWorld.IsHideout(hideout)) { AddRp(me, ActTab.Place, RpAct.Hide, null, busy, false, false); }
            if (RpWorld.IsHidden(me)) { AddRp(me, ActTab.Self, RpAct.Leave, null, busy, false, false); }
            var surrender = Entry(me, RpAct.Surrender, null, busy, false, false);
            surrender.Title = RpWorld.IsSurrendered(me) ? "Опустить руки" : "Сдаться";
            menu.Of(ActTab.Self).Add(surrender);
            AddEmotes(me);
            if (stage == ActStage.PickItem) { Choices(me, pending); }
            if (keepSel < menu.Current.Count) { menu.Selected = keepSel; }
            if (menu.Current.Count == 0 && stage == ActStage.Browse) { menu.FirstNonEmpty(); }
        }

        private void AddRp(Character me, ActTab tab, RpAct act, Item item, bool busy, bool needsItem, bool confirm) =>
            menu.Of(tab).Add(Entry(me, act, item, busy, needsItem, confirm));

        private ActEntry Entry(Character me, RpAct act, Item item, bool busy, bool needsItem, bool confirm) {
            Entity t = RpRules.OnSelf(act) ? null : target;
            Item probe = item;
            if (needsItem && probe == null) { probe = act == RpAct.Detain ? FirstCuffs(me) : FirstGivable(me); }
            RpFacts facts = RpWorld.Facts(me, t, probe, busy);
            if (RpRules.OnSelf(act)) { facts.HasTarget = true; }
            RpDeny deny = RpRules.Check(act, facts);
            string right = act == RpAct.Search && target is Character p ? SearchHint(p) : "";
            return new ActEntry {
                Kind = ActKind.Rp, Act = act, Title = RpRules.Title(act), Reason = deny == RpDeny.None ? null : RpRules.Explain(deny),
                NeedsItem = needsItem, Confirm = confirm, Right = right
            };
        }

        private static string SearchHint(Character p) => RpWorld.StateOf(p) switch {
            TargetState.Cooperating => "сдался",
            TargetState.Bound => "связан",
            TargetState.Unconscious => "без сознания",
            TargetState.Dead => "тело",
            _ => "сопротивляется"
        };

        private void AddEmotes(Character me) {
            EmoteCatalog catalog = EmoteView.Catalog;
            bool right = me.Inventory?.GetItemInLimbSlot(InvSlotType.RightHand) != null;
            bool left = me.Inventory?.GetItemInLimbSlot(InvSlotType.LeftHand) != null;
            for (int i = 0; i < catalog.Count; i++) {
                EmoteDef d = catalog[i];
                double since = lastEmote.TryGetValue(d.Index, out double last) ? Timing.TotalTime - last : double.MaxValue;
                var facts = new EmoteFacts {
                    Alive = !me.IsDead,
                    Conscious = RpWorld.Conscious(me),
                    Bound = me.LockHands,
                    HandBusy = RpClient.Current != null && RpClient.Current.Mine && RpRules.Timed(RpClient.Current.Act) || Auth.SwipeView.Busy(me),
                    BothHandsBusy = right && left,
                    AnyHandBusy = right || left,
                    Muted = me.SpeechImpediment >= 100.0f,
                    Hidden = RpWorld.IsHidden(me),
                    Emoting = EmoteView.Busy(me),
                    Vitality = me.MaxVitality > 0 ? me.Vitality / me.MaxVitality : 1.0f,
                    SinceLast = since
                };
                EmoteDeny deny = EmoteRules.Check(d, facts);
                ActTab tab = d.Kind == EmoteKind.Hands ? ActTab.Hands : d.Kind == EmoteKind.Voice ? ActTab.Voice : ActTab.Body;
                menu.Of(tab).Add(new ActEntry {
                    Kind = ActKind.Emote, Emote = d.Index, Title = d.Title, Reason = deny == EmoteDeny.None ? null : EmoteRules.Explain(deny),
                    Right = d.Sounds.Length == 0 ? "жест" : d.Sounds.Length > 1 ? $"{d.Sounds.Length} вар." : ""
                });
            }
        }

        private void Choices(Character me, ActEntry pending) {
            menu.Items.Clear();
            if (me.Inventory == null) { return; }
            Character receiver = target as Character;
            for (int i = 0; i < me.Inventory.Capacity; i++) {
                InvSlotType slot = me.Inventory.SlotTypes[i];
                if (slot != InvSlotType.Any && slot != InvSlotType.RightHand && slot != InvSlotType.LeftHand) { continue; }
                Item it = me.Inventory.GetItemAt(i);
                if (it == null || ContainsItem(it)) { continue; }
                bool cuffs = RpWorld.IsCuffs(it);
                if (pending.Act == RpAct.Detain && !cuffs) { continue; }
                if (pending.Act != RpAct.Detain && cuffs) { continue; }
                string reason = null;
                if (pending.Act == RpAct.Give && receiver != null && !RpWorld.CanReceive(receiver, it)) { reason = RpRules.Explain(RpDeny.NoRoom); }
                menu.Items.Add(new ItemChoice {
                    Item = it.ID, Title = it.Name, Where = slot == InvSlotType.Any ? "карман" : "в руке", Reason = reason
                });
            }
        }

        private bool ContainsItem(Item it) {
            for (int i = 0; i < menu.Items.Count; i++) {
                if (menu.Items[i].Item == it.ID) { return true; }
            }
            return false;
        }

        private static Item FirstCuffs(Character me) {
            if (me?.Inventory == null) { return null; }
            foreach (Item it in me.Inventory.AllItems) {
                if (RpWorld.IsCuffs(it) && ReferenceEquals(it.ParentInventory, me.Inventory)) { return it; }
            }
            return null;
        }

        private static Item FirstGivable(Character me) {
            if (me?.Inventory == null) { return null; }
            for (int i = 0; i < me.Inventory.Capacity; i++) {
                InvSlotType slot = me.Inventory.SlotTypes[i];
                if (slot != InvSlotType.Any && slot != InvSlotType.RightHand && slot != InvSlotType.LeftHand) { continue; }
                Item it = me.Inventory.GetItemAt(i);
                if (it != null && !RpWorld.IsCuffs(it)) { return it; }
            }
            return null;
        }
    }
}
