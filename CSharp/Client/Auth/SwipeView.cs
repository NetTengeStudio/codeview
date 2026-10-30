using System;
using System.Collections.Generic;
using Barotrauma;
using FarseerPhysics;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ScpItdMod.Core;
using ScpItdMod.Hud;
using ScpItdMod.Rp;

namespace ScpItdMod.Auth {
    public static class SwipeView {
        private const float Correction = 0.15f;
        private const float CorrectionRate = 0.35f;
        private const float LedSeconds = 0.8f;
        private const float CardPixels = 28.0f;
        private const float PredictWindow = 0.6f;
        private const int MaxViews = 64;

        private sealed class View {
            public uint Id;
            public Character Actor;
            public ushort Reader;
            public ushort Card;
            public byte Hand;
            public double Start;
            public float Offset;
            public float OffsetTarget;
            public ReachMotion Motion;
            public bool Planned;
            public float Approach;
            public bool Predicted;
        }

        private struct Led {
            public ushort Reader;
            public bool Granted;
            public bool Dark;
            public double At;
        }

        private static readonly Dictionary<Character, View> views = new Dictionary<Character, View>();
        private static readonly List<Led> leds = new List<Led>();
        private static readonly List<Character> finished = new List<Character>();

        public static int Active => views.Count;

        public static bool Busy(Character c) => c != null && views.ContainsKey(c);

        public static void Attach(Harmony harmony) {
            ModNet.ReceiveBytes(SwipeWire.Channel, SwipeWire.MaxMessage, OnMessage);
            harmony.Patch(AccessTools.Method(typeof(Item), nameof(Item.TryInteract)),
                postfix: ModuleHost.Bind(typeof(SwipeView), nameof(InteractPostfix)));
            harmony.Patch(AccessTools.Method(typeof(AnimController), nameof(AnimController.UpdateAnimations)),
                prefix: ModuleHost.Bind(typeof(SwipeView), nameof(AnimPrefix)),
                postfix: ModuleHost.Bind(typeof(SwipeView), nameof(AnimPostfix)));
            harmony.Patch(AccessTools.Method(typeof(Character), nameof(Character.DrawFront)),
                postfix: ModuleHost.Bind(typeof(SwipeView), nameof(DrawFrontPostfix)));
            HudFrame.Register(HudFrame.Meters - 4, PaintLeds);
        }

        public static void Reset() {
            views.Clear();
            leds.Clear();
        }

        public static void InteractPostfix(Item __instance, Character user, bool __result) {
            if (!__result || user == null || !ReferenceEquals(user, Character.Controlled) || !SwipeWorld.IsReader(__instance)) { return; }
            if (views.TryGetValue(user, out View current) && current.Reader == __instance.ID) { return; }
            Item card = SwipeWorld.Card(user);
            SwipeFacts f = SwipeWorld.Facts(user, __instance, card, false, RpWorld.IsHidden(user));
            if (SwipeRules.Check(f) != SwipeDeny.None) { return; }
            Begin(user, 0, __instance.ID, card.ID, (byte)SwipeWorld.FreeHand(user, card), SwipeWorld.Approach(user, __instance), 0.0f, true);
        }

        private static void OnMessage(byte[] payload) {
            if (!SwipeEvent.Read(payload, out SwipeEvent e)) { return; }
            if (!(Entity.FindEntityByID(e.Actor) is Character actor)) { return; }
            double now = Timing.TotalTime;
            switch (e.Kind) {
                case SwipeMsg.Start: {
                    float latency = (GameMain.Client?.ConnectedClients?.Find(c => ReferenceEquals(c.Character, Character.Controlled))?.Ping ?? 0) * 0.0005f;
                    float age = e.Age + latency;
                    if (views.TryGetValue(actor, out View v) && v.Predicted && v.Id == 0 && v.Reader == e.Reader && now - v.Start < PredictWindow) {
                        v.Id = e.Id;
                        v.Predicted = false;
                        v.Hand = e.Hand;
                        v.Card = e.Card;
                        v.OffsetTarget = Math.Clamp(age - (float)(now - v.Start), -Correction, Correction);
                        if (MathF.Abs(e.Approach - v.Approach) > 0.02f) { v.Planned = false; v.Approach = e.Approach; }
                        return;
                    }
                    Begin(actor, e.Id, e.Reader, e.Card, e.Hand, e.Approach, age, false);
                    break;
                }
                case SwipeMsg.Result:
                    leds.Add(new Led { Reader = e.Reader, Granted = e.Granted, Dark = e.Deny == SwipeDeny.NoPower, At = now });
                    if (leds.Count > 16) { leds.RemoveAt(0); }
                    if (ReferenceEquals(actor, Character.Controlled) && !e.Granted) { RpClient.Say(SwipeRules.Explain(e.Deny), GUIStyle.Orange); }
                    break;
                case SwipeMsg.Cancel:
                    if (views.TryGetValue(actor, out View c2) && (c2.Id == e.Id || c2.Id == 0)) { Stop(c2, now); }
                    if (ReferenceEquals(actor, Character.Controlled)) { RpClient.Say(SwipeRules.Explain(e.Deny), GUIStyle.Orange); }
                    break;
                case SwipeMsg.Denied:
                    if (views.TryGetValue(actor, out View c3) && c3.Predicted) { Stop(c3, now); }
                    if (ReferenceEquals(actor, Character.Controlled)) { RpClient.Say(SwipeRules.Explain(e.Deny), GUIStyle.Orange); }
                    break;
            }
        }

        private static void Begin(Character actor, uint id, ushort reader, ushort card, byte hand, float approach, float age, bool predicted) {
            if (views.Count >= MaxViews && !views.ContainsKey(actor)) { return; }
            var v = new View {
                Id = id, Actor = actor, Reader = reader, Card = card, Hand = hand, Approach = approach,
                Start = Timing.TotalTime - age, Predicted = predicted
            };
            views[actor] = v;
        }

        private static float Elapsed(View v, double now) => (float)(now - v.Start) + v.Offset;

        private static void Stop(View v, double now) {
            if (!v.Planned) {
                views.Remove(v.Actor);
                return;
            }
            v.Motion.Cancel(Elapsed(v, now));
        }

        public static void AnimPrefix(AnimController __instance) {
            if (views.Count == 0 || !(__instance.Character is Character c) || !views.TryGetValue(c, out View v)) { return; }
            if (Entity.FindEntityByID(v.Reader) is Item reader && !v.Motion.Cancelled) {
                __instance.TargetDir = reader.WorldPosition.X >= c.WorldPosition.X ? Direction.Right : Direction.Left;
            }
        }

        public static void AnimPostfix(AnimController __instance, float deltaTime) {
            if (views.Count == 0 || !(__instance.Character is Character c) || !views.TryGetValue(c, out View v)) { return; }
            double now = Timing.TotalTime;
            v.Offset = Scalar.Approach(v.Offset, v.OffsetTarget, CorrectionRate * 10.0f, deltaTime);
            Limb hand = __instance.GetLimb(v.Hand == 1 ? LimbType.RightHand : LimbType.LeftHand);
            LimbJoint shoulder = v.Hand == 1 ? __instance.rightShoulder : __instance.leftShoulder;
            if (hand?.body == null || shoulder == null || c.IsDead || c.IsIncapacitated || __instance.IsHangingWithRope) {
                views.Remove(c);
                return;
            }
            Vector2 sub = c.Submarine?.SimPosition ?? Vector2.Zero;
            Vector2 shoulderSim = shoulder.WorldAnchorA;
            float dir = __instance.Dir;
            float arm = __instance.ArmLength;
            float rotation = __instance.GetLimb(LimbType.Torso)?.Rotation ?? 0;
            Item reader = Entity.FindEntityByID(v.Reader) as Item;
            Vector2 readerSim = reader != null ? ConvertUnits.ToSimUnits(reader.WorldPosition) - sub : shoulderSim + new Vector2(dir * arm * 0.6f, 0.0f);
            if (!v.Planned) {
                Vector2 handLocal = hand.SimPosition - shoulderSim;
                var start = IkBlend.Local(new Vec2(handLocal.X, handLocal.Y), rotation, dir);
                Vector2 contactLocal = readerSim - shoulderSim;
                var contact = IkBlend.Local(new Vec2(contactLocal.X, contactLocal.Y), rotation, dir);
                v.Motion = ReachMotion.Plan(start, contact, arm, new ReachTiming { Approach = v.Approach });
                v.Planned = true;
            }
            float t = Elapsed(v, now);
            if (t >= v.Motion.End) {
                views.Remove(c);
                return;
            }
            Vec2 local = v.Motion.Sample(t);
            Vec2 anchor = new Vec2(shoulderSim.X, shoulderSim.Y);
            Vec2 targetCore = IkBlend.Target(Vec2.Zero, local, anchor, rotation, dir, MathF.Max(0.01f, arm - 0.01f), 1);
            Vector2 target = new Vector2(targetCore.X, targetCore.Y);
            float lockTo = v.Motion.CardTilt(t);
            if (lockTo > 0.0f && reader != null) {
                Vec2 plannedCore = IkBlend.Target(Vec2.Zero, v.Motion.Contact, anchor, rotation, dir, MathF.Max(0.01f, arm - 0.01f), 1);
                Vector2 planned = new Vector2(plannedCore.X, plannedCore.Y);
                target += (readerSim - planned) * lockTo;
            }
            float weight = v.Motion.Weight(t);
            if (weight > 0.002f) {
                Vector2 natural = v.Hand == 1 ? __instance.RightHandIKPos : __instance.LeftHandIKPos;
                if (!MathUtils.IsValid(natural) || natural == Vector2.Zero) { natural = hand.SimPosition; }
                float strength = __instance.CurrentAnimationParams.HandIKStrength;
                __instance.HandIK(hand, Vector2.Lerp(natural, target, weight), strength, strength);
            }
        }

        public static void DrawFrontPostfix(Character __instance, SpriteBatch spriteBatch, Camera cam) {
            if (views.Count == 0 || cam == null || !views.TryGetValue(__instance, out View v) || !v.Planned) { return; }
            if (!(Entity.FindEntityByID(v.Card) is Item card)) { return; }
            foreach (Item held in __instance.HeldItems) {
                if (ReferenceEquals(held, card)) { return; }
            }
            Limb hand = __instance.AnimController.GetLimb(v.Hand == 1 ? LimbType.RightHand : LimbType.LeftHand);
            Sprite sprite = card.Prefab.InventoryIcon ?? card.Sprite;
            if (hand?.body == null || sprite == null) { return; }
            float t = Elapsed(v, Timing.TotalTime);
            float tilt = v.Motion.CardTilt(t);
            Vector2 at = hand.body.DrawPosition;
            float dir = __instance.AnimController.Dir;
            float rotation = MathHelper.Lerp(-hand.body.DrawRotation + MathHelper.PiOver2 * 0.35f * dir, 0.0f, tilt);
            float scale = CardPixels / MathF.Max(sprite.size.X, sprite.size.Y) * MathHelper.Lerp(1.0f, 0.92f, tilt);
            sprite.Draw(spriteBatch, new Vector2(at.X, -at.Y), card.GetInventoryIconColor(), rotation, scale, dir < 0 ? SpriteEffects.FlipHorizontally : SpriteEffects.None);
        }

        private static void PaintLeds(SpriteBatch spriteBatch) {
            if (leds.Count == 0) { return; }
            Camera cam = GameMain.GameScreen?.Cam;
            if (cam == null) { return; }
            double now = Timing.TotalTime;
            HudPaint.BeginScreen(spriteBatch);
            try {
                for (int i = leds.Count - 1; i >= 0; i--) {
                    Led led = leds[i];
                    float age = (float)(now - led.At);
                    if (age > LedSeconds || !(Entity.FindEntityByID(led.Reader) is Item reader)) {
                        leds.RemoveAt(i);
                        continue;
                    }
                    if (led.Dark) { continue; }
                    float k = 1.0f - Scalar.Smooth(age / LedSeconds);
                    Vector2 p = cam.WorldToScreen(reader.WorldPosition);
                    float side = MathF.Max(5.0f, 8.0f * GUI.Scale * Math.Clamp(cam.Zoom, 0.5f, 1.5f));
                    Color color = (led.Granted ? GUIStyle.Green : GUIStyle.Red) * k;
                    GUI.DrawFilledRectangle(spriteBatch, new RectangleF(p.X - side * 0.5f, p.Y - side * 0.5f, side, side), color);
                }
            } finally {
                HudPaint.End(spriteBatch);
            }
        }
    }
}
