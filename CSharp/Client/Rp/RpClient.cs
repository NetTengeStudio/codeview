using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Networking;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ScpItdMod.Core;
using ScpItdMod.Hud;
using ScpItdMod.Net;
using ScpItdMod.People;
using ScpItdMod.Ui;

namespace ScpItdMod.Rp {
    public static class RpClient {
        public const string Channel = "scpitd.rp";
        private const float FlagPixels = 28.0f;

        public sealed class Shown {
            public RpAct Act;
            public ushort Actor;
            public ushort Target;
            public double Start;
            public float Duration;
            public bool Mine;
        }

        private static readonly WireWriter request = new WireWriter(64);
        private static readonly List<ushort> surrenderedIds = new List<ushort>();
        private static readonly List<ushort> hiddenIds = new List<ushort>();
        private static readonly InputType[] Movement = { InputType.Left, InputType.Right, InputType.Up, InputType.Down, InputType.Run, InputType.Crouch };
        private static readonly BatchCanvas canvas = new BatchCanvas();
        private sealed class PendingEmote {
            public byte Index, Variant;
            public double Start;
        }
        private static readonly Dictionary<ushort, PendingEmote> pendingEmotes = new Dictionary<ushort, PendingEmote>();
        private static readonly Dictionary<ushort, ulong> emoteRevisions = new Dictionary<ushort, ulong>();
        private static readonly List<ushort> resolvedEmotes = new List<ushort>();
        private static object connection;
        private static double nextSync;
        private static bool synchronized;
        private static uint stateRevision;

        public static Shown Current { get; private set; }

        public static void Attach(Harmony harmony) {
            ModNet.ReceiveBytes(Channel, 4096, OnMessage);
            harmony.Patch(AccessTools.Method(typeof(GameScreen), nameof(GameScreen.Update)),
                postfix: ModuleHost.Bind(typeof(RpClient), nameof(Tick)));
            harmony.Patch(AccessTools.Method(typeof(Character), nameof(Character.Draw)),
                prefix: ModuleHost.Bind(typeof(RpClient), nameof(DrawPrefix)));
            harmony.Patch(AccessTools.Method(typeof(Character), nameof(Character.DrawFront)),
                prefix: ModuleHost.Bind(typeof(RpClient), nameof(DrawPrefix)),
                postfix: ModuleHost.Bind(typeof(RpClient), nameof(DrawFrontPostfix)));
            harmony.Patch(AccessTools.Method(typeof(Character), nameof(Character.ControlLocalPlayer)),
                postfix: ModuleHost.Bind(typeof(RpClient), nameof(ControlPostfix)));
            HudFrame.Register(HudFrame.Meters + 1, PaintProgress);
        }

        public static void Reset() {
            Current = null;
            pendingEmotes.Clear(); emoteRevisions.Clear(); resolvedEmotes.Clear();
            synchronized = false; stateRevision = 0; nextSync = 0;
            OfferWindow.Dismiss();
            SearchPanel.Shut();
            RpWorld.Clear();
            EmoteView.Reset();
        }

        public static void Act(RpAct act, Entity target, Item item) {
            RpWire.Act(request, act, target?.ID ?? 0, item?.ID ?? 0);
            ModNet.SendBytes(Channel, request.Span, DeliveryMethod.Reliable);
        }

        public static void Emote(EmoteDef def) {
            if (def == null) { return; }
            EmoteTrace.Log("client send", def.Id + " #" + def.Index + " of " + EmoteView.Catalog.Count);
            RpWire.Emote(request, def.Index, EmoteView.Catalog.Fingerprint);
            ModNet.SendBytes(Channel, request.Span, DeliveryMethod.Reliable);
        }

        public static void Tick() {
            object current = GameMain.Client;
            if (!ReferenceEquals(connection, current)) { Reset(); connection = current; }
            if (current == null) { return; }
            double now = Timing.TotalTime;
            if (!synchronized && now >= nextSync) {
                nextSync = now + 2;
                RpWire.Synchronize(request);
                ModNet.SendBytes(Channel, request.Span, DeliveryMethod.Reliable);
            }
            resolvedEmotes.Clear();
            foreach (var pair in pendingEmotes) {
                PendingEmote p = pair.Value;
                EmoteDef def = EmoteView.Catalog[p.Index];
                float age = (float)(now - p.Start);
                if (def == null || age >= Poses.Total(def.Pose, def.Duration(p.Variant))) { resolvedEmotes.Add(pair.Key); continue; }
                if (Entity.FindEntityByID(pair.Key) is Character c && !c.Removed) {
                    EmoteTrace.Log("client play", def.Id + " variant " + p.Variant + " age " + age.ToString("F2") + " on " + c.Name);
                    EmoteView.Start(c, p.Index, p.Variant, age);
                    resolvedEmotes.Add(pair.Key);
                }
            }
            for (int i = 0; i < resolvedEmotes.Count; i++) { pendingEmotes.Remove(resolvedEmotes[i]); }
        }

        public static void Cancel() {
            RpWire.Cancel(request);
            ModNet.SendBytes(Channel, request.Span, DeliveryMethod.Reliable);
        }

        public static void Answer(uint offer, bool accept) {
            RpWire.Answer(request, offer, accept);
            ModNet.SendBytes(Channel, request.Span, DeliveryMethod.Reliable);
        }

        public static bool DrawPrefix(Character __instance) => !Concealed(__instance);

        public static bool Concealed(Character c) => RpWorld.IsHidden(c) && !ReferenceEquals(c, Character.Controlled);

        public static void ControlPostfix(Character __instance) {
            if (!ReferenceEquals(__instance, Character.Controlled) || !RpWorld.IsHidden(__instance)) { return; }
            for (int i = 0; i < Movement.Length; i++) { __instance.ClearInput(Movement[i]); }
        }

        private static void OnMessage(byte[] payload) {
            var r = new WireReader(payload);
            if (r.U8() != RpWire.Proto) { return; }
            var msg = (RpMsg)r.U8();
            switch (msg) {
                case RpMsg.Started: {
                    var act = (RpAct)r.U8();
                    if (!RpWire.ReadEntity(ref r, out ushort actor) || !RpWire.ReadEntity(ref r, out ushort target)) { return; }
                    float duration = BitConverter.Int32BitsToSingle((int)r.U32());
                    if (r.Failed || !float.IsFinite(duration) || duration < 0.0f || duration > 60.0f) { return; }
                    Current = new Shown {
                        Act = act, Actor = actor, Target = target, Start = HudClock.Now, Duration = duration,
                        Mine = Character.Controlled != null && Character.Controlled.ID == actor
                    };
                    break;
                }
                case RpMsg.Ended: {
                    var act = (RpAct)r.U8();
                    var end = (RpEnd)r.U8();
                    var deny = (RpDeny)r.U8();
                    if (r.Failed) { return; }
                    Shown was = Current;
                    if (was != null && was.Act == act) { Current = null; }
                    Finish(act, end, deny, was);
                    break;
                }
                case RpMsg.Offer: {
                    uint id = (uint)r.Count(int.MaxValue);
                    if (!RpWire.ReadEntity(ref r, out ushort from)) { return; }
                    string item = r.Text(256);
                    if (r.Failed) { return; }
                    OfferWindow.Show(id, from, item);
                    break;
                }
                case RpMsg.State: {
                    uint revision = r.U32();
                    if (RpWire.ReadIds(ref r, surrenderedIds) && RpWire.ReadIds(ref r, hiddenIds) && r.End) {
                        if (synchronized && unchecked((int)(revision - stateRevision)) <= 0) { return; }
                        RpWorld.ReplaceState(surrenderedIds, hiddenIds);
                        stateRevision = revision;
                        synchronized = true;
                    }
                    break;
                }
                case RpMsg.Note: {
                    string text = r.Text(1024);
                    if (!r.Failed) { Say(text, Color.LightGray); ActPanel.EmoteRefused(); }
                    break;
                }
                case RpMsg.EmoteStart: {
                    if (!RpWire.ReadEntity(ref r, out ushort actor)) { return; }
                    ulong run = r.U64();
                    byte index = r.U8();
                    byte variant = r.U8();
                    float age = BitConverter.Int32BitsToSingle((int)r.U32());
                    ReadOnlySpan<byte> catalog = r.Bytes(32);
                    if (r.Failed || !r.End || actor == 0 || run == 0 || !float.IsFinite(age) || age < 0) { EmoteTrace.Log("client drop", "malformed EmoteStart"); return; }
                    if (!catalog.SequenceEqual(EmoteView.Catalog.Fingerprint)) {
                        EmoteTrace.Log("client drop", "catalog fingerprint differs from the server's");
                        ModLog.Once("Client and server emote catalogs differ; reload the same mod on both peers."); return;
                    }
                    EmoteDef def = EmoteView.Catalog[index];
                    if (def == null || variant >= Math.Max(1, def.Sounds.Length) || age >= Poses.Total(def.Pose, def.Duration(variant))) { EmoteTrace.Log("client drop", "unknown emote #" + index + ", variant " + variant + " or already over (age " + age.ToString("F2") + ")"); return; }
                    if (emoteRevisions.TryGetValue(actor, out ulong last) && run <= last) { EmoteTrace.Log("client drop", "stale run " + run + " <= " + last + " for actor " + actor); return; }
                    EmoteTrace.Log("client recv", def.Id + " run " + run + " actor " + actor);
                    emoteRevisions[actor] = run;
                    pendingEmotes[actor] = new PendingEmote { Index = index, Variant = variant, Start = Timing.TotalTime - age };
                    break;
                }
                case RpMsg.EmoteStop: {
                    if (!RpWire.ReadEntity(ref r, out ushort actor)) { return; }
                    ulong run = r.U64();
                    var why = (EmoteDeny)r.U8();
                    if (r.Failed || !r.End || actor == 0 || run == 0) { return; }
                    bool active = emoteRevisions.TryGetValue(actor, out ulong last) && run == last;
                    if (run < last) { return; }
                    emoteRevisions[actor] = run;
                    pendingEmotes.Remove(actor);
                    if (!active) { return; }
                    if (Entity.FindEntityByID(actor) is Character c) {
                        EmoteView.Stop(c);
                        if (ReferenceEquals(c, Character.Controlled) && why != EmoteDeny.None) { Say(EmoteRules.Explain(why), GUIStyle.Orange); }
                    }
                    break;
                }
                case RpMsg.Permit: {
                    if (!RpWire.ReadEntity(ref r, out ushort target)) { return; }
                    float seconds = BitConverter.Int32BitsToSingle((int)r.U32());
                    if (r.Failed || !float.IsFinite(seconds)) { return; }
                    Character me = Character.Controlled;
                    if (me == null) { return; }
                    if (target == 0 || !(Entity.FindEntityByID(target) is Character searched)) {
                        RpWorld.Revoke(me);
                        SearchPanel.Shut();
                        break;
                    }
                    RpWorld.Grant(me, searched, Timing.TotalTime + Math.Clamp(seconds, 0.0f, SearchRules.PermitSeconds));
                    SearchPanel.Open(searched);
                    break;
                }
                case RpMsg.Shown: {
                    if (!RpWire.ReadEntity(ref r, out ushort from) || !RpWire.ReadEntity(ref r, out ushort item)) { return; }
                    if (r.Failed || !r.End) { return; }
                    if (Entity.FindEntityByID(from) is Character presenter && Entity.FindEntityByID(item) is Item shown) { ShownWindow.Show(presenter, shown); }
                    break;
                }
            }
        }

        private static void Finish(RpAct act, RpEnd end, RpDeny deny, Shown was) {
            bool mine = was == null || was.Mine;
            switch (end) {
                case RpEnd.Done:
                    if (act == RpAct.Give) { Say("Предмет передан.", GUIStyle.Green); }
                    break;
                case RpEnd.Denied:
                    Say(RpRules.Title(act) + ": " + RpRules.Explain(deny), GUIStyle.Orange);
                    break;
                case RpEnd.Interrupted:
                    Say(RpRules.Title(act) + ": прервано" + (deny != RpDeny.None ? " — " + RpRules.Explain(deny) : ""), GUIStyle.Orange);
                    break;
                case RpEnd.Cancelled:
                    if (!mine) { Say(RpRules.Title(act) + ": отменено", Color.LightGray); }
                    break;
                case RpEnd.Declined:
                    Say("От предмета отказались.", Color.LightGray);
                    break;
                case RpEnd.Expired:
                    Say("Ответа не было, предложение снято.", Color.LightGray);
                    break;
            }
        }

        public static void Say(string text, Color color) {
            if (string.IsNullOrEmpty(text)) { return; }
            GUI.AddMessage(text, color, playSound: false);
        }

        private static void PaintProgress(SpriteBatch spriteBatch) {
            Shown run = Current;
            Character me = Character.Controlled;
            if (run == null || run.Duration <= 0.0f || me == null) { return; }
            float progress = Scalar.Clamp01((float)((HudClock.Now - run.Start) / run.Duration));
            float scale = GUI.Scale;
            RetroFaces f = Faces.Retro(scale);
            if (f.Body == null || f.Small == null) { return; }
            string label = run.Mine
                ? RpRules.Title(run.Act)
                : $"{Actor(run)}: {RpRules.Title(run.Act).ToLowerInvariant()} вас";
            string hint = run.Mine ? "отмена" : "отойдите, чтобы помешать";
            canvas.Begin(spriteBatch, scale);
            try {
                float w = MathF.Round(360.0f * scale);
                float h = MathF.Round(48.0f * scale);
                var box = new Box(MathF.Round(GameMain.GraphicsWidth * 0.5f - w * 0.5f), MathF.Round(GameMain.GraphicsHeight * 0.7f), w, h);
                Retro.Panel(canvas, box, 1.0f);
                Box inner = box.Inset(Retro.Px(canvas, Retro.EdgePx) + Retro.Px(canvas, 4.0f));
                Ui.Paint.Clipped(canvas, f.Body, label, inner.TakeTop(f.Body.LineHeight), run.Mine ? Retro.Bright : Retro.Warn, Align.Left);
                Retro.Meter(canvas, inner.TakeBottom(Retro.Px(canvas, 10.0f)), progress, run.Mine ? Retro.Ink : Retro.Warn, 1.0f);
                if (run.Mine) {
                    float hw = Retro.HintWidth(canvas, f, ActPanel.KeyName, hint);
                    Retro.Hint(canvas, f, inner.Right - hw + Retro.Px(canvas, 10.0f), inner.Y, ActPanel.KeyName, hint, 1.0f);
                } else {
                    Ui.Paint.Text(canvas, f.Small, hint, inner.TakeTop(f.Body.LineHeight), Retro.Muted, Align.Right);
                }
            } finally {
                canvas.End();
            }
        }

        private static string Actor(Shown run) =>
            Entity.FindEntityByID(run.Actor) is Character c ? Names.Sighted(c) : "Кто-то";

        public static void DrawFrontPostfix(Character __instance, SpriteBatch spriteBatch, Camera cam) {
            if (cam == null || GUI.DisableHUD || !RpWorld.IsSurrendered(__instance) || Concealed(__instance)) { return; }
            if (!ReferenceEquals(__instance, Character.Controlled) && !__instance.hudInfoVisible) { return; }
            Texture2D flag = TextureStore.File("Content/HUD/Rp/WhiteFlag.png");
            if (flag == null) { return; }
            Vector2 head = __instance.AnimController.GetLimb(LimbType.Head)?.body?.DrawPosition ?? __instance.DrawPosition + Vector2.UnitY * 100.0f;
            float scale = FlagPixels * GUI.Scale / cam.Zoom / Math.Max(flag.Width, flag.Height);
            spriteBatch.Draw(flag, new Vector2(head.X, -head.Y - 60.0f / cam.Zoom), null, Color.White * 0.9f, 0.0f,
                new Vector2(flag.Width, flag.Height) * 0.5f, scale, SpriteEffects.None, 0.0f);
        }
    }
}
