using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.Networking;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ScpItdMod.Core;
using ScpItdMod.Hud;
using ScpItdMod.Net;

namespace ScpItdMod.Auth {
    public static class RadioUi {
        private const double ChannelCheckSeconds = 0.4;

        private static double nextChannelCheck;
        private static int lastChannel = -1;
        private static bool lastAllowed = true;
        private static Color? channelColor;

        public static void Attach(Harmony harmony) =>
            harmony.Patch(AccessTools.Method(typeof(ChatBox), nameof(ChatBox.Update)),
                postfix: ModuleHost.Bind(typeof(RadioUi), nameof(ChatUpdatePostfix)));

        public static void Reset() => lastChannel = -1;

        public static void ChatUpdatePostfix(ChatBox __instance) {
            double now = HudClock.Now;
            if (now < nextChannelCheck || AuthWorld.Plan.Count == 0) { return; }
            nextChannelCheck = now + ChannelCheckSeconds;
            GUITextBox box = __instance.channelText;
            Character me = Character.Controlled;
            if (box == null || me == null || !ChatMessage.CanUseRadio(me, out WifiComponent radio)) { return; }
            int channel = radio.Channel;
            bool allowed = AuthWorld.RadioAllowed(radio, channel);
            if (channel == lastChannel && allowed == lastAllowed) { return; }
            lastChannel = channel;
            lastAllowed = allowed;
            channelColor ??= box.TextBlock.TextColor;
            if (AuthWorld.Plan.Find(channel, out ChannelPlan.Band band)) {
                box.TextBlock.ToolTip = allowed ? $"Канал {channel}: {band.Name}" : $"Канал {channel}: {band.Name} — нет допуска, вас не услышат";
                box.TextBlock.TextColor = allowed ? GUIStyle.Green : GUIStyle.Red;
            } else {
                box.TextBlock.ToolTip = $"Канал {channel}: общий";
                box.TextBlock.TextColor = channelColor.Value;
            }
        }
    }
}
