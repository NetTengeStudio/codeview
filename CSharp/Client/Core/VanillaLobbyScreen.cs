using System;
using System.IO;
using Barotrauma;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ScpItdMod.Core {
    public static class VanillaLobbyScreen {
        public const string LogoPath = "Content/UI/logo.png";
        public const string BackgroundPath = "Content/UI/background.png";
        public const string BoardImagePath = "Content/UI/board.png";
        public const string BoardTextPath = "Content/UI/board.txt";
        private const float LogoLeft = -0.01f, LogoTop = 0.25f, LogoWidth = 0.7f, LogoHeight = 0.9f;
        private const string BoardKey = "scpitd.board";

        // The briefing replaces the settings presentation; native controls retain
        // their hierarchy so releasing the module restores them exactly.
        private sealed class Layout {
            internal readonly NetLobbyScreen Owner;
            internal readonly GUILayoutGroup Panel, Horizontal;
            internal readonly GUIComponent Top, Bottom;
            internal readonly Shelf Changes = new Shelf();
            internal readonly bool TopVisible, TopIgnored, BottomVisible, BottomIgnored;
            internal GUIComponent Board;
            internal Layout(NetLobbyScreen owner, GUILayoutGroup panel, GUILayoutGroup horizontal, GUIComponent top, GUIComponent bottom) {
                Owner = owner; Panel = panel; Horizontal = horizontal; Top = top; Bottom = bottom;
                TopVisible = top.Visible; TopIgnored = top.IgnoreLayoutGroups;
                BottomVisible = bottom.Visible; BottomIgnored = bottom.IgnoreLayoutGroups;
            }
            internal void ShowBriefing() {
                Top.Visible = Bottom.Visible = false;
                Top.IgnoreLayoutGroups = Bottom.IgnoreLayoutGroups = true;
                Board.Visible = true; Board.IgnoreLayoutGroups = false;
                Panel.Recalculate();
            }
            internal void Restore() {
                Changes.Restore();
                Top.Visible = TopVisible; Top.IgnoreLayoutGroups = TopIgnored;
                Bottom.Visible = BottomVisible; Bottom.IgnoreLayoutGroups = BottomIgnored;
                Panel.Recalculate(); Horizontal.Recalculate();
            }
        }
        private static Layout layout;
        private static bool insideBanner, backgroundTaken;
        private static Sprite backgroundWas, backgroundOurs;
        private static NetLobbyScreen backgroundOwner;

        public static void Attach(Harmony harmony) {
            harmony.Patch(AccessTools.Constructor(typeof(NetLobbyScreen), Type.EmptyTypes),
                postfix: ModuleHost.Bind(typeof(VanillaLobbyScreen), nameof(Apply)));
            harmony.Patch(AccessTools.Method(typeof(NetLobbyScreen), nameof(NetLobbyScreen.Select)),
                postfix: ModuleHost.Bind(typeof(VanillaLobbyScreen), nameof(Apply)));
            harmony.Patch(AccessTools.Method(typeof(NetLobbyScreen), nameof(NetLobbyScreen.DrawServerBanner)),
                prefix: ModuleHost.Bind(typeof(VanillaLobbyScreen), nameof(EnterBannerPrefix)),
                postfix: ModuleHost.Bind(typeof(VanillaLobbyScreen), nameof(BannerPostfix)),
                finalizer: ModuleHost.Bind(typeof(VanillaLobbyScreen), nameof(LeaveBannerFinalizer)));
            harmony.Patch(AccessTools.Method(typeof(GUI), nameof(GUI.DrawBackgroundSprite)), prefix: ModuleHost.Bind(typeof(VanillaLobbyScreen), nameof(SkipBannerSpritePrefix)));
            harmony.Patch(AccessTools.Method(typeof(NetLobbyScreen), nameof(NetLobbyScreen.Draw)), prefix: ModuleHost.Bind(typeof(VanillaLobbyScreen), nameof(BackgroundPrefix)));
            Apply(GameMain.NetLobbyScreen);
        }

        public static void Apply(NetLobbyScreen __instance) {
            if (__instance == null) { return; }
            GUIComponent top = __instance.ModeList?.Parent?.Parent;
            GUIComponent bottom = __instance.gameModeSettingsLayout?.Parent;
            if (top?.Parent is not GUILayoutGroup panel || !ReferenceEquals(bottom?.Parent, panel) ||
                panel.Parent?.Parent is not GUILayoutGroup horizontal) {
                ModLog.Once("SCPITD lobby layout is incompatible; native controls remain available.");
                return;
            }
            if (layout != null && ReferenceEquals(layout.Owner, __instance) && ReferenceEquals(layout.Top, top) && ReferenceEquals(layout.Bottom, bottom)) { return; }
            Sprite boardArt = TextureStore.Art(BoardImagePath);
            if (boardArt == null) {
                ModLog.Once("SCPITD lobby briefing image is unavailable: " + BoardImagePath);
                return;
            }
            layout?.Restore(); layout = null;
            var next = new Layout(__instance, panel, horizontal, top, bottom);
            try {
                // The briefing is the page itself. The previous implementation left a
                // tab row and a separate header above it, which produced an empty strip
                // and an invisible navigation surface after the match-settings tab was
                // removed. There is now one board owning the whole former content area.
                next.Board = next.Changes.Raise(CreateBoard(panel, boardArt));
                int topIndex = panel.GetChildIndex(top);
                if (!ReferenceEquals(top.Parent, panel) || topIndex < 0 || topIndex >= panel.RectTransform.CountChildren ||
                    !ReferenceEquals(next.Board.Parent, panel)) {
                    throw new InvalidOperationException("SCPITD briefing board has no valid lobby hierarchy anchor");
                }
                next.Board.RectTransform.RepositionChildInHierarchy(topIndex);
                foreach (GUIComponent child in panel.Children) {
                    if (!ReferenceEquals(child, top) && !ReferenceEquals(child, bottom) &&
                        !ReferenceEquals(child, next.Board) && !child.IgnoreLayoutGroups) {
                        next.Changes.Resize(child.RectTransform, 1, 0.18f);
                    }
                }
                foreach (GUIComponent child in horizontal.Children) {
                    if (!child.IgnoreLayoutGroups) { next.Changes.Resize(child.RectTransform, ReferenceEquals(child, panel.Parent) ? 0.55f : 0.445f, 1); }
                }
                // The historical SCPITD header is the logo and the play-style
                // tags only. The native server name and the workshop-description
                // button used to be retired; the reconstruction lost that step,
                // so the large wordmark was painted underneath the live server
                // name (visible overlap in the game). Restore the original
                // composition instead of masking or shrinking the logo.
                GUIComponent header = __instance.SettingsButton?.Parent?.Parent;
                if (header != null) {
                    foreach (RectTransform child in header.RectTransform.Children) {
                        if (child.GUIComponent is GUITextBlock text && text.TextGetter != null && text.IgnoreLayoutGroups) {
                            next.Changes.Retire(text);
                        }
                    }
                }
                if (__instance.ServerMessageButton != null) { next.Changes.Retire(__instance.ServerMessageButton); }
                next.ShowBriefing(); horizontal.Recalculate(); layout = next;
            } catch (Exception error) {
                next.Restore();
                ModuleHost.ReportFailure("SCPITD lobby presentation", error);
            }
        }

        private static GUIComponent CreateBoard(GUILayoutGroup panel, Sprite art) {
            var frame = new GUIFrame(new RectTransform(new Vector2(1, 0.80f), panel.RectTransform)) { UserData = BoardKey, CanBeFocused = false };
            try {
                var content = new GUILayoutGroup(new RectTransform(new Vector2(0.94f, 0.96f), frame.RectTransform, Anchor.Center)) {
                    Stretch = true, RelativeSpacing = 0.015f
                };
                new GUITextBlock(new RectTransform(new Vector2(1, 0.075f), content.RectTransform), "Брифинг",
                    textAlignment: Alignment.Center, font: GUIStyle.SubHeadingFont) { CanBeFocused = false };
                new GUIImage(new RectTransform(new Vector2(1, 0.55f), content.RectTransform), art,
                    scaleToFit: GUIImage.ScalingMode.ScaleToFitSmallestExtent) { CanBeFocused = false };
                var textScroll = new GUIListBox(new RectTransform(new Vector2(1, 0.36f), content.RectTransform));
                var text = new GUITextBlock(new RectTransform(new Vector2(1, 0), textScroll.Content.RectTransform), BoardText(),
                    textAlignment: Alignment.TopCenter, wrap: true) { CanBeFocused = false };
                text.CalculateHeightFromText();
                int textHeight = Math.Max(GUI.IntScale(48), text.Rect.Height);
                text.RectTransform.MinSize = new Point(0, textHeight);
                text.RectTransform.MaxSize = new Point(int.MaxValue, textHeight);
                return frame;
            } catch { frame.Parent?.RemoveChild(frame); throw; }
        }
        private static string BoardText() {
            string path = ModAssets.Resolve(BoardTextPath);
            if (path.Length == 0) { return string.Empty; }
            try { return File.ReadAllText(path); }
            catch (SystemException error) { ModLog.Error($"{BoardTextPath} cannot be read: {error.Message}"); return string.Empty; }
        }
        public static void Release() {
            layout?.Restore(); layout = null; insideBanner = false;
            if (backgroundTaken && backgroundOwner != null && ReferenceEquals(backgroundOwner.backgroundSprite, backgroundOurs)) {
                backgroundOwner.backgroundSprite = backgroundWas;
            }
            backgroundTaken = false; backgroundWas = backgroundOurs = null; backgroundOwner = null;
        }
        public static void EnterBannerPrefix() => insideBanner = TextureStore.File(LogoPath) != null;
        public static void LeaveBannerFinalizer() => insideBanner = false;
        public static bool SkipBannerSpritePrefix() => !insideBanner;
        public static void BannerPostfix(SpriteBatch spriteBatch, GUICustomComponent component) {
            insideBanner = false;
            Texture2D logo = TextureStore.File(LogoPath);
            if (logo == null) { return; }
            HudPaint.BeginScreen(spriteBatch);
            try {
                HudPaint.Blit(spriteBatch, logo, HudPaint.Contain(HudPaint.Slice(component.Rect, LogoLeft, LogoTop, LogoWidth, LogoHeight), logo.Width, logo.Height), HudPaint.Ink);
            } finally { HudPaint.End(spriteBatch); }
        }
        public static void BackgroundPrefix(NetLobbyScreen __instance) {
            Sprite art = TextureStore.Art(BackgroundPath);
            if (art == null) { return; }
            if (backgroundTaken && !ReferenceEquals(backgroundOwner, __instance)) {
                if (ReferenceEquals(backgroundOwner.backgroundSprite, backgroundOurs)) { backgroundOwner.backgroundSprite = backgroundWas; }
                backgroundTaken = false;
            }
            if (!backgroundTaken) {
                backgroundOwner = __instance; backgroundWas = __instance.backgroundSprite; backgroundTaken = true;
            }
            __instance.backgroundSprite = backgroundOurs = art;
        }
    }
}
