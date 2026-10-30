using System;
using System.Collections.Generic;
using Barotrauma;
using ScpItdMod.Core;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace ScpItdMod.Menu {
    // Historical SCPITD main menu: logo, four actions, community link and the
    // version footer. The four actions are the original localisation tags; the
    // rest of the native menu (new/load/host/tutorials/editors) stays retired,
    // exactly as it was before the unfinished interface overhaul. Motion is the
    // only animation system used here: fade, item glow and icon glow are springs
    // owned by one scope, so a tab switch cannot leave a half-finished tween.
    internal static class MainMenuView {
        public const string LogoPath = "Content/UI/logo.png";
        public const string IconPath = "Content/UI/discord.png";
        public const string FontPath = "Content/Font/Exo2-Bold.ttf";
        public const string CommunityUrl = "https://discord.gg/UWzn79J4UT";
        public const string BannerText = "Lua active";
        public const string FooterText = "SCP: Into The Darkness";

        private const float LogoTop = 0.0444f;
        private const float LogoWidth = 0.6484f;
        private const float LogoHeight = 0.2519f;
        private const float ColumnTop = 0.398f;
        private const float ColumnWidth = 0.44f;
        private const float ItemHeight = 0.062f;
        private const float ItemStride = 0.070f;
        private const float ItemFont = 0.0425f;
        private const float IconLeft = 0.0122f;
        private const float IconBottom = 0.0287f;
        private const float IconWidth = 0.0330f;
        private const float IconHeight = 0.02405f;
        private const float BarHeight = 0.0173f;
        private const float BarOverhang = 0.0222f;
        private const float ShadowDrop = 0.0028f;
        private const float ShadowAlpha = 0.72f;
        private const float TextFloor = 0.60f;
        private const float AlarmFloor = 0.40f;
        private const float GlyphRise = 0.4f;
        private const int GradientBands = 48;
        private const float Dim = 0.70f;
        private const float Rest = 0.72f;
        private const float Lit = 1.0f;
        private const float Grow = 0.08f;
        private const float PaintCutoff = 0.004f;

        private static readonly string[] Tags = {
            "JoinServerButton", "SettingsButton", "settingstab.mods", "QuitButton"
        };

        private static readonly Motion.MotionRuntime motion = new Motion.MotionRuntime(16);
        private static Motion.MotionScope motionScope;
        private static Motion.MotionHandle<float> opacityMotion, iconMotion;
        private static Motion.MotionHandle<float>[] hoverMotion;
        private static readonly MenuShelf shelf = new MenuShelf();

        private static MainMenuScreen screen;
        private static GUIButton[] lines;
        private static GUIButton[] sources;
        private static string[] labels;
        private static Color[] tints;
        private static float[] floors;
        private static float[] glows;
        private static GUIComponent logo;
        private static GUIComponent column;
        private static GUIComponent link;
        private static GUIImage icon;
        private static GUITextBlock banner;
        private static Action<GUIComponent> bannerUpdate;
        private static Color bannerColorWas;
        private static LocalizedString bannerTextWas;
        private static GUITextBlock footer;
        private static LocalizedString footerWas;
        private static LanguageIdentifier language;

        private static float iconGlow;
        private static float opacity;

        public static void Attach(Harmony harmony) {
            harmony.Patch(
                AccessTools.Method(typeof(MainMenuScreen), nameof(MainMenuScreen.Select)),
                postfix: MenuRuntime.Bind(typeof(MainMenuView), nameof(SelectPostfix)));
            harmony.Patch(
                AccessTools.Method(typeof(MainMenuScreen), nameof(MainMenuScreen.DrawBackground)),
                postfix: MenuRuntime.Bind(typeof(MainMenuView), nameof(DimPostfix)));
            harmony.Patch(AccessTools.Method(typeof(MainMenuScreen), nameof(MainMenuScreen.Deselect)),
                postfix: MenuRuntime.Bind(typeof(MainMenuView), nameof(DeselectPostfix)));
            harmony.Patch(AccessTools.Method(typeof(MainMenuScreen), nameof(MainMenuScreen.Update)),
                postfix: MenuRuntime.Bind(typeof(MainMenuView), nameof(UpdatePostfix)));
            MenuRuntime.RegisterHost(Host);
            // The plugin may attach after the screen already exists (LuaCs
            // constructs the screen before assembly plugins in some launch
            // paths). Applying here as well as from the postfix means the menu
            // is never left showing the native buttons until the next Select.
            if (Screen.Selected is MainMenuScreen open) { Apply(open); }
        }

        public static void Release() {
            People.ProfileLibraryWindow.Close();
            motionScope?.Dispose(); motionScope = null; hoverMotion = null; motion.Reset();
            if (banner != null) {
                banner.OnAddedToGUIUpdateList = bannerUpdate;
                banner.TextColor = bannerColorWas;
                banner.Text = bannerTextWas;
                banner = null;
                bannerUpdate = null;
                bannerTextWas = null;
            }
            if (footer != null) {
                footer.Text = footerWas;
                footer = null;
                footerWas = null;
            }
            shelf.Restore();
            screen = null;
            language = default;
            lines = null;
            sources = null;
            labels = null;
            tints = null;
            floors = null;
            glows = null;
            logo = null;
            column = null;
            link = null;
            icon = null;
            iconGlow = 0.0f;
            opacity = 0.0f;
        }

        public static void DeselectPostfix() {
            People.ProfileLibraryWindow.Close();
            motionScope?.Dispose();
            motionScope = null;
            hoverMotion = null;
            motion.Reset();
        }

        public static void SelectPostfix(MainMenuScreen __instance) {
            Prefs.Reload();
            try { Apply(__instance); }
            catch (Exception error) { MenuRuntime.Fail(error); }
        }

        public static void UpdatePostfix(MainMenuScreen __instance) {
            if (!ReferenceEquals(Screen.Selected, __instance) || ReferenceEquals(screen, __instance) && lines != null) { return; }
            try { Apply(__instance); }
            catch (Exception error) { MenuRuntime.Fail(error); }
        }

        private static bool MotionReduced() => Prefs.Flag("motion.reduced", false);

        public static void Advance(float deltaTime, GUICustomComponent component) {
            double now = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
            motion.ReducedMotion = MotionReduced();
            motion.Advance(now, !GameMain.WindowActive || Screen.Selected != screen);
            if (motionScope?.Alive != true) {
                motionScope = motion.Scope(); opacityMotion = motionScope.Scalar(); iconMotion = motionScope.Scalar();
                hoverMotion = new Motion.MotionHandle<float>[lines?.Length ?? 0];
                for (int i = 0; i < hoverMotion.Length; i++) { hoverMotion[i] = motionScope.Scalar(); }
            }

            shelf.Hide();
            AdoptBanner();
            if (language != GameSettings.CurrentConfig.Language) { Localise(); }

            bool engaged = screen != null && !Occupied(screen);
            if (screen != null && !engaged && GUI.KeyboardDispatcher.Subscriber == null && PlayerInput.KeyHit(Keys.Escape)) {
                screen.ReturnToMainMenu(null, null);
            }
            opacityMotion.To(engaged ? 1 : 0, Motion.MotionTransition.Tween(engaged ? 0.22f : 0.12f));
            opacity = opacityMotion.Value;
            bool painted = opacity > PaintCutoff;
            if (logo != null) { logo.Visible = painted; }
            if (column != null) { column.Visible = painted; }
            if (link != null) { link.Visible = painted; }

            if (lines == null) { return; }

            for (int i = 0; i < lines.Length; i++) {
                GUIButton line = lines[i];
                if (line == null) { continue; }
                if (sources != null && i < sources.Length) { line.Enabled = sources[i]?.Enabled ?? true; }
                hoverMotion[i].To(Hovered(line) ? 1 : 0, Motion.MotionPresets.Hover);
                glows[i] = hoverMotion[i].Value;
            }
            if (icon == null) { return; }
            iconMotion.To(Hovered(link) ? 1 : 0, Motion.MotionPresets.Hover);
            iconGlow = iconMotion.Value;
            icon.Color = HudPaint.Alpha(HudPaint.Ink, opacity * Scalar.Lerp(Rest, Lit, iconGlow));
        }

        public static void DrawLogo(SpriteBatch spriteBatch, GUICustomComponent component) {
            Texture2D art = TextureStore.File(LogoPath);
            if (art == null) { return; }
            HudPaint.BeginScreen(spriteBatch);
            HudPaint.Blit(spriteBatch, art,
                HudPaint.Contain(component.Rect, art.Width, art.Height),
                HudPaint.Alpha(HudPaint.Ink, opacity));
            HudPaint.End(spriteBatch);
        }

        public static void DrawItems(SpriteBatch spriteBatch, GUICustomComponent component) {
            if (opacity <= PaintCutoff || lines == null) { return; }
            ScalableFont font = FontStore.File(FontPath);
            if (font == null) { return; }
            int frame = component.Rect.Height;
            int drop = Scalar.Round(frame * ShadowDrop);
            int rail = Scalar.Round(frame * BarHeight);
            int overhang = Scalar.Round(frame * BarOverhang);
            for (int i = 0; i < lines.Length; i++) {
                GUIButton line = lines[i];
                if (line == null) { continue; }
                float glow = glows[i];
                float scale = line.Rect.Height * (ItemFont / ItemHeight) / FontStore.Atlas
                    * (1.0f + Grow * glow);
                Vector2 extent = font.MeasureString(labels[i]) * scale;
                Rectangle box = new Rectangle(
                    Scalar.Round(line.Rect.Center.X - extent.X * 0.5f),
                    Scalar.Round(line.Rect.Center.Y - extent.Y * 0.5f),
                    Scalar.Round(extent.X), Scalar.Round(extent.Y));

                HudPaint.Fill(spriteBatch,
                    new Rectangle(box.X - overhang, box.Center.Y - rail / 2,
                        box.Width + overhang * 2, rail),
                    HudPaint.Premultiplied(HudPaint.Alpha(HudPaint.Rail, opacity)));
                Stamp(spriteBatch, font, labels[i], new Vector2(box.X, box.Y + drop), scale,
                    HudPaint.Alpha(HudPaint.Plate, opacity * ShadowAlpha));
                Gradient(spriteBatch, font, i, box, scale, overhang, glow);
            }
        }

        public static void DimPostfix(SpriteBatch spriteBatch) {
            if (Screen.Selected is not MainMenuScreen) { return; }
            HudPaint.Fill(spriteBatch,
                new Rectangle(0, 0, GameMain.GraphicsWidth, GameMain.GraphicsHeight),
                HudPaint.Premultiplied(HudPaint.Alpha(HudPaint.Plate, Dim)));
        }

        private static void Gradient(SpriteBatch spriteBatch, ScalableFont font, int index,
            Rectangle box, float scale, int overhang, float glow) {
            float floor = Scalar.Lerp(floors[index], 1.0f, glow);
            int ramp = Math.Max(1, Scalar.Round(
                font.MeasureString(labels[index], removeExtraSpacing: true).Y * scale));
            int rise = box.Y + Scalar.Round(ramp * GlyphRise);
            int bands = ramp < GradientBands ? ramp : GradientBands;
            try {
                for (int band = 0; band < bands; band++) {
                    int top = rise + ramp * band / bands;
                    int bottom = rise + ramp * (band + 1) / bands;
                    HudPaint.Clip(spriteBatch, new Rectangle(
                        box.X - overhang,
                        band == 0 ? box.Y - box.Height : top,
                        box.Width + overhang * 2,
                        (band == bands - 1 ? box.Bottom + box.Height : bottom)
                            - (band == 0 ? box.Y - box.Height : top)));
                    float shade = Scalar.Lerp(1.0f, floor, (band + 0.5f) / bands);
                    Stamp(spriteBatch, font, labels[index], new Vector2(box.X, box.Y), scale,
                        HudPaint.Alpha(HudPaint.Shade(tints[index], shade), opacity));
                }
            } finally {
                HudPaint.Unclip(spriteBatch);
            }
        }

        private static void Stamp(SpriteBatch spriteBatch, ScalableFont font, string text,
            Vector2 origin, float scale, Color ink) =>
            font.DrawString(spriteBatch, text, origin, HudPaint.Premultiplied(ink), 0.0f,
                Vector2.Zero, scale, SpriteEffects.None, 0.0f);

        private static void Host(string[] args) {
            if (Screen.Selected is not MainMenuScreen open || open.buttonsParent == null) {
                ModLog.Error("createlobby needs the main menu");
                return;
            }
            GUIButton host = Vanilla(open.buttonsParent, MainMenuScreen.Tab.HostServer);
            if (host == null) { return; }
            if (!host.Enabled) {
                ModLog.Error("hosting is unavailable");
                return;
            }
            Trigger(host);
        }

        private static void Localise() {
            language = GameSettings.CurrentConfig.Language;
            labels = new string[Tags.Length];
            for (int i = 0; i < Tags.Length; i++) { labels[i] = TextManager.Get(Tags[i]).Value; }
        }

        private static void Apply(MainMenuScreen open) {
            if (screen != null && screen != open) { Release(); }
            opacity = 0.0f;
            screen = open;

            if (labels == null || language != GameSettings.CurrentConfig.Language) { Localise(); }
            if (lines != null) { return; }
            GUIComponent buttons = open.buttonsParent;
            if (buttons == null) { return; }
            if (TextureStore.File(LogoPath) == null || TextureStore.Art(IconPath) == null || FontStore.File(FontPath) == null) {
                throw new InvalidOperationException("Menu assets are unavailable. The original menu will be restored.");
            }

            shelf.Retire(buttons);
            RetireTitle(open);
            if (open.remoteContentContainer != null) { shelf.Retire(open.remoteContentContainer); }
            AdoptFooter(open);

            logo = shelf.Raise(new GUICustomComponent(
                new RectTransform(new Vector2(LogoWidth, LogoHeight), open.Frame.RectTransform,
                    Anchor.TopCenter) { RelativeOffset = new Vector2(0.0f, LogoTop) },
                DrawLogo) { CanBeFocused = false });

            BuildColumn(open, buttons);
            BuildIcon(open);

            shelf.Raise(new GUICustomComponent(
                new RectTransform(Vector2.One, open.Frame.RectTransform), DrawItems, Advance) {
                CanBeFocused = false
            });
        }

        private static void BuildColumn(MainMenuScreen open, GUIComponent buttons) {
            float gap = ItemStride - ItemHeight;
            float height = ItemHeight * 4.0f + gap * 3.0f;

            column = shelf.Raise(new GUILayoutGroup(
                new RectTransform(new Vector2(ColumnWidth, height), open.Frame.RectTransform,
                    Anchor.TopCenter) { RelativeOffset = new Vector2(0.0f, ColumnTop) }) {
                Stretch = false,
                RelativeSpacing = gap / height,
                ChildAnchor = Anchor.TopCenter
            });

            GUIButton join = Vanilla(buttons, MainMenuScreen.Tab.JoinServer);
            GUIButton settings = Vanilla(buttons, MainMenuScreen.Tab.Settings);
            GUIButton mods = Vanilla(buttons, MainMenuScreen.Tab.Mods);

            sources = new[] { join, settings, mods, null };
            lines = new GUIButton[4];
            tints = new[] { HudPaint.Ink, HudPaint.Ink, HudPaint.Ink, HudPaint.Alarm };
            floors = new[] { TextFloor, TextFloor, TextFloor, AlarmFloor };
            glows = new float[4];
            lines[0] = Line(ItemHeight / height, (_, _) => Trigger(join));
            lines[1] = Line(ItemHeight / height, (_, _) => Trigger(settings));
            lines[2] = Line(ItemHeight / height, (_, _) => Trigger(mods));
            lines[3] = Line(ItemHeight / height, (_, _) => {
                GameMain.Instance.Exit();
                return true;
            });
        }

        private static void BuildIcon(MainMenuScreen open) {
            Sprite art = TextureStore.Art(IconPath);
            if (art == null) { return; }
            link = shelf.Raise(new GUIButton(
                new RectTransform(new Vector2(IconWidth, IconHeight), open.Frame.RectTransform,
                    Anchor.BottomLeft, scaleBasis: ScaleBasis.BothHeight) {
                    RelativeOffset = new Vector2(IconLeft, IconBottom)
                }, style: null) {
                OnClicked = (_, _) => {
                    GameMain.ShowOpenUriPrompt(CommunityUrl);
                    return true;
                }
            });
            icon = new GUIImage(new RectTransform(Vector2.One, link.RectTransform), art,
                scaleToFit: true) {
                CanBeFocused = false
            };
        }

        private static GUIButton Line(float height, GUIButton.OnClickedHandler onClicked) =>
            new GUIButton(new RectTransform(new Vector2(1.0f, height), column.RectTransform),
                style: null) {
                OnClicked = onClicked
            };

        private static bool Trigger(GUIButton source) {
            if (source?.OnClicked == null || !source.Enabled) { return false; }
            return source.OnClicked(source, source.UserData);
        }

        private static GUIButton Vanilla(GUIComponent buttons, MainMenuScreen.Tab tab) {
            foreach (GUIComponent child in buttons.GetAllChildren()) {
                if (child is GUIButton button && button.UserData is MainMenuScreen.Tab found
                    && found == tab) {
                    return button;
                }
            }
            throw new InvalidOperationException($"Main menu {tab} button is missing.");
        }

        private static bool Occupied(MainMenuScreen open) {
            if (People.ProfileLibraryWindow.IsOpen) { return true; }
            foreach (KeyValuePair<MainMenuScreen.Tab, GUIFrame> tab in open.menuTabs) {
                if (tab.Value.Visible && tab.Value.CountChildren > 0) { return true; }
            }
            return false;
        }

        private static bool Hovered(GUIComponent component) =>
            component != null && (component.State == GUIComponent.ComponentState.Hover
                || component.State == GUIComponent.ComponentState.HoverSelected
                || component.State == GUIComponent.ComponentState.Pressed);

        private static void AdoptBanner() {
            if (banner != null || screen == null) { return; }
            GUITextBlock found = null;
            foreach (RectTransform child in screen.Frame.RectTransform.Children) {
                if (child.GUIComponent is not GUITextBlock text
                    || text.OnAddedToGUIUpdateList == null) {
                    continue;
                }
                if (found != null) {
                    ModLog.Once("main menu banner is ambiguous");
                    return;
                }
                found = text;
            }
            if (found == null) { return; }
            banner = found;
            bannerUpdate = found.OnAddedToGUIUpdateList;
            bannerColorWas = found.TextColor;
            bannerTextWas = found.Text;
            found.OnAddedToGUIUpdateList = null;
            found.Text = BannerText;
            found.TextColor = HudPaint.Ink;
        }

        private static void AdoptFooter(MainMenuScreen open) {
            GUIComponent group = open.leftTextFooterLayout;
            if (group == null) { return; }
            if (open.gameAnalyticsStatusText != null) { shelf.Retire(open.gameAnalyticsStatusText); }
            foreach (RectTransform child in group.RectTransform.Children) {
                if (child.GUIComponent is GUITextBlock text
                    && text != open.gameAnalyticsStatusText) {
                    footer = text;
                    footerWas = text.Text;
                    text.Text = $"Barotrauma v{GameMain.Version}. {FooterText}";
                    return;
                }
            }
        }

        private static void RetireTitle(MainMenuScreen open) {
            GUIComponentStyle title = open.titleText?.Style;
            if (title == null) { return; }
            foreach (RectTransform child in open.Frame.RectTransform.Children) {
                if (child.GUIComponent is GUIImage image && image.Style == title) { shelf.Retire(image); }
            }
        }

    }
}

