using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Networking;
using Barotrauma.Sounds;
using HarmonyLib;
using Microsoft.Xna.Framework;
using ScpItdMod.Core;
using ScpItdMod.Net;
using ScpItdMod.Weather;

namespace ScpItdMod.Meteo {
    public static class MeteoClient {
        public static readonly MeteoSession Session = new MeteoSession();
        private static SoundCue alert;
        private static SoundCue relay;

        public static void Attach(Harmony harmony) {
            alert = new SoundCue("Content/Sound/Meteo/alert_pips.ogg");
            relay = new SoundCue("Content/Sound/Meteo/relay.ogg");
            MeteoWorld.Config = () => WeatherService.Config; MeteoWorld.Active = () => WeatherService.Active && WeatherService.Director != null;
            MeteoWorld.Day = t => WeatherService.Director.Day(t); MeteoWorld.Now = () => WeatherService.Clock.Seconds;
            Session.Send = bytes => ModNet.SendBytes(MeteoWire.Channel, bytes.Span, DeliveryMethod.Reliable);
            ModNet.ReceiveBytes(MeteoWire.Channel, MeteoWire.MaxReply, OnPayload);
            MeteoAmbient.Attach(harmony);
        }

        public static void Reset() { Session.End(); MeteoAmbient.Reset(); alert?.Release(); relay?.Release(); }
        public static void Release() { Reset(); alert = relay = null; Session.Send = _ => { }; }

        public static void Click() => relay?.Play(.35f);

        private static void OnPayload(byte[] payload) {
            var r = new WireReader(payload);
            if (!MeteoWire.ReadHeader(ref r, out MeteoReply kind, out uint token, out MeteoError error)) { return; }
            if (kind == MeteoReply.Reading) {
                string text = r.Text(1024); if (r.Failed || !r.End) { return; }
                GUI.AddMessage(text, new Color(160, 235, 170), 6f, playSound: false);
                return;
            }
            if (kind == MeteoReply.Alert) {
                byte level = r.U8(); string from = r.Text(160); string text = r.Text(MeteoWire.AlertMax * 4);
                if (r.Failed || !r.End || level < 1 || level > 3) { return; }
                Color color = level == 3 ? new Color(255, 90, 80) : level == 2 ? new Color(255, 176, 0) : new Color(160, 235, 170);
                string head = level == 3 ? "ШТОРМОВОЕ ПРЕДУПРЕЖДЕНИЕ" : level == 2 ? "ПРЕДУПРЕЖДЕНИЕ МЕТЕОСЛУЖБЫ" : "СООБЩЕНИЕ МЕТЕОСЛУЖБЫ";
                GUI.AddMessage(head + ": " + text + " - " + from, color, 12f, playSound: false);
                alert?.Play(level == 3 ? 1f : .7f);
                return;
            }
            Session.Apply(kind, token, error, ref r, Timing.TotalTime);
        }
    }
}
