using System;
using System.Linq;
using Barotrauma;
using Barotrauma.Networking;
using HarmonyLib;
using Microsoft.Xna.Framework;
using ScpItdMod.Core;
using ScpItdMod.Hud;
using ScpItdMod.Net;
using ScpItdMod.Rp;

namespace ScpItdMod.Life {
    public static class LifeUi {
        private const string ButtonTag = "scpitd.life.die";
        private static readonly WireWriter request = new WireWriter(8);
        private static GUIMessageBox dialog;

        public static void Attach(Harmony harmony) {
            ModNet.ReceiveBytes(LifeModule.Channel, 16, OnReply);
            harmony.Patch(AccessTools.Method(typeof(GUI), nameof(GUI.TogglePauseMenu), new[] { typeof(GUIButton), typeof(object) }),
                postfix: ModuleHost.Bind(typeof(LifeUi), nameof(PauseMenuPostfix)));
        }

        public static void Release() {
            dialog?.Close();
            dialog = null;
        }

        public static void PauseMenuPostfix() {
            if (!GUI.PauseMenuOpen || GUI.PauseMenu == null || GameMain.Client == null) { return; }
            Character me = Character.Controlled;
            if (me == null || me.IsDead) { return; }
            GUILayoutGroup buttons = GUI.PauseMenu.GetAllChildren().OfType<GUILayoutGroup>().FirstOrDefault();
            if (buttons == null || buttons.GetChildByUserData(ButtonTag) != null) { return; }
            new GUIButton(new RectTransform(new Vector2(1.0f, 0.1f), buttons.RectTransform), "Завершить жизнь персонажа") {
                UserData = ButtonTag,
                TextColor = GUIStyle.Red,
                ToolTip = "Персонаж умрёт. Дальше — по правилам возрождения сервера.",
                OnClicked = (_, _) => {
                    if (GUI.PauseMenuOpen) { GUI.TogglePauseMenu(); }
                    Confirm();
                    return true;
                }
            };
        }

        private static void Confirm() {
            dialog?.Close();
            dialog = new GUIMessageBox("Завершить жизнь персонажа?",
                "Персонаж умрёт. Это нельзя отменить. Удерживайте кнопку, чтобы подтвердить.",
                new LocalizedString[] { "Удерживайте", "Отмена" });
            GUIMessageBox box = dialog;
            GUIButton hold = box.Buttons[0];
            hold.TextColor = GUIStyle.Red;
            box.Buttons[1].OnClicked = (_, _) => {
                box.Close();
                return true;
            };
            double pressedAt = -1.0;
            bool sent = false;
            new GUICustomComponent(new RectTransform(Vector2.Zero, box.Content.RectTransform), onUpdate: (_, _) => {
                if (sent) { return; }
                bool down = hold.Rect.Contains(PlayerInput.MousePosition) && PlayerInput.PrimaryMouseButtonHeld();
                if (!down) {
                    pressedAt = -1.0;
                    hold.Text = "Удерживайте";
                    return;
                }
                double now = HudClock.Now;
                if (pressedAt < 0.0) { pressedAt = now; }
                double left = DeathRules.HoldSeconds - (now - pressedAt);
                hold.Text = left > 0.0 ? $"Удерживайте… {left:0.0}" : "Отправлено";
                if (left > 0.0) { return; }
                sent = true;
                Send();
                box.Close();
            });
        }

        private static void Send() {
            request.Clear();
            request.U8(LifeModule.Proto);
            request.U8(LifeModule.Die);
            ModNet.SendBytes(LifeModule.Channel, request.Span, DeliveryMethod.Reliable);
        }

        private static void OnReply(byte[] payload) {
            var r = new WireReader(payload);
            if (r.U8() != LifeModule.Proto) { return; }
            var deny = (DeathDeny)r.U8();
            if (r.Failed) { return; }
            if (deny != DeathDeny.None) { RpClient.Say(DeathRules.Explain(deny), GUIStyle.Orange); }
        }
    }
}
