using System;
using System.Collections.Generic;
using System.Globalization;
using Barotrauma;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ScpItdMod.Core;
using ScpItdMod.Motion;

namespace ScpItdMod.Cinematics {
    internal static class CinematicEditor {
        private const double CompileInterval = .25, SimulationBudgetMilliseconds = 6;
        private static readonly Color RouteColor = new Color(120, 200, 255), PathColor = new Color(255, 196, 64), CameraColor = new Color(170, 255, 140),
            ActiveCameraColor = new Color(255, 255, 255), EffectColor = new Color(255, 120, 200), ErrorColor = new Color(255, 70, 70), CrewColor = new Color(255, 230, 160);
        private static List<CinematicAuthoredScene> scenes = new List<CinematicAuthoredScene>();
        private static CinematicAuthoredScene active;
        private static CinematicPreview preview;
        private static string previewError = "";
        private static ulong previewHash;
        private static string previewSignature = "";
        private static double nextCompile, playTime, lastFrameTime;
        private static bool playing, cameraView, cameraSaved;
        private static Vector2 savedCameraPosition;
        private static float savedCameraZoom;
        private static GUIFrame panel;
        private static GUITextBlock title, status, notes;
        private static GUIScrollBar timeline;
        private static GUIButton playButton, cameraButton;
        private static bool timelineDragging;
        // The editor is not GameScreen, so the shared runtime stands paused here: the panel runs its own Motion instance.
        private static readonly MotionRuntime panelMotion = new MotionRuntime(8);
        private static MotionScope panelScope;
        private static MotionHandle<float> reveal;
        private static GUIButton showButton;
        private static bool panelShown = true;

        internal static void Attach(Harmony harmony) {
            harmony.Patch(AccessTools.Method(typeof(SubEditorScreen), nameof(SubEditorScreen.Update), new[] { typeof(double) }), postfix: ModuleHost.Bind(typeof(CinematicEditor), nameof(Update)));
            harmony.Patch(AccessTools.Method(typeof(SubEditorScreen), nameof(SubEditorScreen.Draw), new[] { typeof(double), typeof(GraphicsDevice), typeof(SpriteBatch) }), prefix: ModuleHost.Bind(typeof(CinematicEditor), nameof(Prepare)));
            harmony.Patch(AccessTools.Method(typeof(SubEditorScreen), "DrawGrid", new[] { typeof(SpriteBatch) }), postfix: ModuleHost.Bind(typeof(CinematicEditor), nameof(DrawWorld)));
            harmony.Patch(AccessTools.Method(typeof(SubEditorScreen), nameof(SubEditorScreen.AddToGUIUpdateList)), postfix: ModuleHost.Bind(typeof(CinematicEditor), nameof(AddToGUIUpdateList)));
            harmony.Patch(AccessTools.Method(typeof(SubEditorScreen), "DeselectEditorSpecific"), postfix: ModuleHost.Bind(typeof(CinematicEditor), nameof(Close)));
        }

        internal static void Close() {
            RestoreCamera();
            preview?.Dispose(); preview = null; active = null; scenes.Clear(); playing = false; playTime = 0; previewHash = 0; previewSignature = ""; previewError = "";
            if (panel != null) panel.RectTransform.Parent = null;
            if (showButton != null) showButton.RectTransform.Parent = null;
            panelScope?.Dispose(); panelScope = null; panel = null; showButton = null; panelShown = true;
        }

        private static Submarine Sub => Submarine.MainSub;

        private static void Update(double __0) {
            if (!SubEditorScreen.IsSubEditor() || Sub == null) return;
            try {
                double now = Timing.TotalTime;
                if (now >= nextCompile) { nextCompile = now + CompileInterval; Recompile(); }
                preview?.Advance(SimulationBudgetMilliseconds);
                double dt = Math.Clamp(now - lastFrameTime, 0, .1); lastFrameTime = now;
                if (preview != null && playing && !timelineDragging) {
                    playTime = Math.Min(playTime + dt, preview.Duration);
                    if (preview.Finished && playTime >= preview.Duration) playing = false;
                }
                RefreshPanel();
                ApplyCameraView();
            } catch (Exception error) { ModLog.Once("Cinematic editor: " + error.Message); }
        }

        private static void Recompile() {
            bool anyMarker = false;
            foreach (ScpCineMarker marker in ScpCineMarker.All) if (ReferenceEquals(marker.Owner.Submarine, Sub)) { anyMarker = true; break; }
            if (!anyMarker) { if (active != null || panel != null) Close(); return; }
            scenes = CinematicSubScenes.Compile(Sub, true);
            CinematicAuthoredScene chosen = null;
            foreach (MapEntity selected in MapEntity.SelectedList) {
                if (selected is not Item item || item.GetComponent<ScpCineMarker>() == null) continue;
                foreach (CinematicAuthoredScene scene in scenes) if (scene.SceneMarker == item.ID || scene.Used != null && scene.Used.Contains(item.ID)) { chosen = scene; break; }
                if (chosen != null) break;
            }
            if (chosen == null && active != null) foreach (CinematicAuthoredScene scene in scenes) if (scene.SceneMarker == active.SceneMarker) chosen = scene;
            if (chosen == null && scenes.Count > 0) chosen = scenes[0];
            active = chosen;
            EnsurePanel();
            string signature = Signature();
            if (active?.Plan == null) { DisposePreview(); previewError = ""; return; }
            if (preview != null && active.Plan.Hash == previewHash && signature == previewSignature) return;
            DisposePreview();
            previewHash = active.Plan.Hash; previewSignature = signature;
            try {
                if (Entity.FindEntityByID(active.SceneMarker) is not Item sceneItem) throw new InvalidOperationException("Маркер сцены не найден");
                Camera cam = GameMain.SubEditorScreen.Cam;
                preview = new CinematicPreview(active, sceneItem, new Vec2(cam.Resolution.X, cam.Resolution.Y));
                previewError = "";
                playTime = 0;
            } catch (Exception error) { previewError = error.Message; preview = null; }
        }

        private static string Signature() {
            if (active == null) return "";
            var text = new System.Text.StringBuilder(64);
            foreach (KeyValuePair<string, ushort> binding in active.Bindings)
                if (Entity.FindEntityByID(binding.Value) is Item item) text.Append(binding.Key).Append(':').Append(item.WorldPosition.X.ToString("R", CultureInfo.InvariantCulture)).Append(',').Append(item.WorldPosition.Y.ToString("R", CultureInfo.InvariantCulture)).Append(',').Append(item.Scale.ToString("R", CultureInfo.InvariantCulture)).Append(';');
            return text.ToString();
        }

        private static void DisposePreview() {
            RestoreCamera(); cameraView = false;
            preview?.Dispose(); preview = null; previewHash = 0; previewSignature = ""; playing = false;
        }

        private static int Frame => preview == null || preview.Frames.Count == 0 ? -1 : Math.Clamp((int)(playTime / CinematicPreview.StepSeconds), 0, preview.Frames.Count - 1);

        private static void EnsurePanel() {
            if (panel != null) return;
            panel = new GUIFrame(new RectTransform(new Vector2(.34f, .2f), GUI.Canvas, Anchor.BottomRight) { RelativeOffset = new Vector2(.01f, .015f), MinSize = new Point(560, 190) }, "GUIFrameListBox");
            panelScope = panelMotion.Scope(); reveal = panelScope.Scalar(1); panelShown = true;
            var column = new GUILayoutGroup(new RectTransform(new Vector2(.96f, .9f), panel.RectTransform, Anchor.Center)) { RelativeSpacing = .02f, Stretch = true };
            title = new GUITextBlock(new RectTransform(new Vector2(1, .16f), column.RectTransform), "", font: GUIStyle.SubHeadingFont, textAlignment: Alignment.CenterLeft) { ToolTip = "Перетащите заголовок, чтобы сдвинуть панель" };
            new GUIDragHandle(new RectTransform(Vector2.One, title.RectTransform, Anchor.Center), panel.RectTransform, null);
            var buttons = new GUILayoutGroup(new RectTransform(new Vector2(1, .2f), column.RectTransform), isHorizontal: true) { RelativeSpacing = .02f, Stretch = true };
            playButton = new GUIButton(new RectTransform(new Vector2(.22f, 1), buttons.RectTransform), "Пуск") { OnClicked = (_, _) => { if (preview != null) { if (playTime >= preview.Duration && preview.Finished) playTime = 0; playing = !playing; } return true; } };
            new GUIButton(new RectTransform(new Vector2(.22f, 1), buttons.RectTransform), "Сначала") { OnClicked = (_, _) => { playTime = 0; playing = true; return true; } };
            cameraButton = new GUIButton(new RectTransform(new Vector2(.26f, 1), buttons.RectTransform), "Вид из камеры") { OnClicked = (_, _) => { cameraView = !cameraView; if (!cameraView) RestoreCamera(); return true; } };
            new GUIButton(new RectTransform(new Vector2(.26f, 1), buttons.RectTransform), "Пересчитать") { OnClicked = (_, _) => { DisposePreview(); nextCompile = 0; return true; } };
            timeline = new GUIScrollBar(new RectTransform(new Vector2(1, .12f), column.RectTransform), barSize: .03f, isHorizontal: true) {
                OnMoved = (bar, scroll) => { if (preview != null && GUIScrollBar.DraggingBar == bar) playTime = Math.Min(scroll * TimelineSpan(), preview.Duration); return true; }
            };
            status = new GUITextBlock(new RectTransform(new Vector2(1, .16f), column.RectTransform), "", font: GUIStyle.SmallFont);
            notes = new GUITextBlock(new RectTransform(new Vector2(1, .36f), column.RectTransform), "", font: GUIStyle.SmallFont, wrap: true) { TextColor = Color.LightGray };
            new GUIButton(new RectTransform(new Point(86, 24), panel.RectTransform, Anchor.TopRight) { AbsoluteOffset = new Point(8, 8) }, "Скрыть") { OnClicked = (_, _) => { SetShown(false); return true; } };
            showButton = new GUIButton(new RectTransform(new Point(150, 30), GUI.Canvas, Anchor.BottomRight) { AbsoluteOffset = new Point(12, 12) }, "Кинематограф") { OnClicked = (_, _) => { SetShown(true); return true; } };
        }

        private static void SetShown(bool value) {
            panelShown = value;
            reveal.To(value ? 1f : 0f, MotionTransition.Spring(value ? 12f : 14f, 1f));
        }

        private static void StepPanelMotion() {
            panelMotion.ReducedMotion = MotionClient.Runtime.ReducedMotion;
            panelMotion.Advance(System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency);
            float t = Math.Clamp(reveal.Value, 0f, 1f);
            // Slide through ScreenSpaceOffset so the drag handle's own offset stays the panel's resting place.
            Point slide = new Point(0, (int)((1f - t) * (panel.Rect.Height + 40)));
            panel.RectTransform.ScreenSpaceOffset = slide;
            panel.Visible = panelShown || t > .01f;
        }

        private static void RefreshPanel() {
            if (panel == null) return;
            StepPanelMotion();
            title.Text = active == null ? "Кинематограф: в подлодке нет сцены" : "Кинематограф: " + active.Id;
            playButton.Text = playing ? "Пауза" : "Пуск";
            cameraButton.Selected = cameraView;
            var errors = new System.Text.StringBuilder();
            if (active != null) foreach (CinematicDiagnostic d in active.Diagnostics) if (d.Severity != CinematicSeverity.Info && errors.Length < 600)
                errors.Append(d.Severity == CinematicSeverity.Error ? "Ошибка" : "Внимание").Append(" #").Append(d.Marker).Append(": ").Append(d.Message).Append('\n');
            if (preview == null) {
                status.Text = previewError.Length > 0 ? "Предпросмотр недоступен: " + previewError : active?.Plan == null ? "Сцена не собрана: исправьте ошибки маркеров" : "";
                notes.Text = errors.ToString();
                return;
            }
            int frame = Frame;
            timelineDragging = GUIScrollBar.DraggingBar == timeline;
            if (!timelineDragging) timeline.BarScroll = (float)Math.Clamp(playTime / TimelineSpan(), 0, 1);
            string stage = frame >= 0 ? preview.Plan.StageAt(preview.Frames[frame].Stage).Id : "-";
            status.Text = "t = " + playTime.ToString("0.0") + " с из " + preview.Duration.ToString("0.0") + (preview.Finished ? " · " + preview.Outcome : " · считается…") +
                " · кадр «" + stage + "» · шаг физики " + preview.MicrosecondsPerStep.ToString("0") + " мкс";
            notes.Text = errors + string.Join("\n", preview.Notes);
        }

        private static void ApplyCameraView() {
            int frame = Frame;
            if (!cameraView || frame < 0 || !preview.Frames[frame].HasCamera) return;
            Camera cam = GameMain.SubEditorScreen.Cam;
            if (!cameraSaved) { savedCameraPosition = cam.Position; savedCameraZoom = cam.Zoom; cameraSaved = true; }
            CinematicPreviewFrame f = preview.Frames[frame];
            cam.Position = new Vector2(f.Camera.X, f.Camera.Y); cam.Zoom = f.Zoom;
        }

        private static void RestoreCamera() {
            if (!cameraSaved) return;
            cameraSaved = false;
            if (GameMain.SubEditorScreen?.Cam is Camera cam) { cam.Position = savedCameraPosition; cam.Zoom = savedCameraZoom; }
        }

        private static void AddToGUIUpdateList() { if (SubEditorScreen.IsSubEditor() && panel != null && !GUI.DisableHUD) { if (panel.Visible) panel.AddToGUIUpdateList(); if (!panelShown) showButton.AddToGUIUpdateList(); } }

        private static void Prepare(GraphicsDevice __1) {
            int frame = Frame;
            if (preview == null || frame < 0 || !BovCinematicAdapter.TryCreate(out BovCinematicAdapter bov, out _)) return;
            float zoom = GameMain.SubEditorScreen.Cam.Zoom, dt = (float)Math.Clamp(Timing.Step, 0, .1);
            for (int v = 0; v < preview.Vehicles.Length; v++) {
                object ghost = preview.Ghosts[preview.Vehicles[v]];
                if (ghost != null && (preview.Frames[frame].Removed & (1u << preview.Vehicles[v])) == 0) bov.GhostPrepare(ghost, __1, preview.Pose(frame, v), playing ? dt : 0f, zoom);
            }
        }

        private static Vector2 Screen(Vec2 world) => new Vector2(world.X, -world.Y);

        private static void DrawWorld(SpriteBatch __0) {
            if (!SubEditorScreen.IsSubEditor() || active == null) return;
            try {
                float zoom = GameMain.SubEditorScreen.Cam.Zoom, width = 2f / zoom;
                Vec2 origin = Entity.FindEntityByID(active.SceneMarker) is Item sceneItem ? new Vec2(sceneItem.WorldPosition.X, sceneItem.WorldPosition.Y) : Vec2.Zero;
                DrawDiagnostics(__0, width);
                if (active.Plan == null) return;
                DrawPlan(__0, origin, width);
                if (preview == null) return;
                int frame = Frame;
                DrawPath(__0, width);
                if (frame < 0) return;
                if (BovCinematicAdapter.TryCreate(out BovCinematicAdapter bov, out _))
                    foreach (int binding in preview.Vehicles) { object ghost = preview.Ghosts[binding]; if (ghost != null && (preview.Frames[frame].Removed & (1u << binding)) == 0) bov.GhostDraw(ghost, __0, Color.White * .6f, .05f); }
                DrawCrew(__0, frame, width);
                DrawWash(__0, frame, width);
                CinematicPreviewFrame f = preview.Frames[frame];
                if (f.HasCamera) DrawFrame(__0, f.Camera, f.Zoom, ActiveCameraColor, width * 2);
            } catch (Exception error) { ModLog.Once("Cinematic editor drawing: " + error.Message); }
        }

        private static void DrawDiagnostics(SpriteBatch batch, float width) {
            foreach (CinematicAuthoredScene scene in scenes)
                foreach (CinematicDiagnostic d in scene.Diagnostics) {
                    if (d.Severity == CinematicSeverity.Info || Entity.FindEntityByID(d.Marker) is not Item item) continue;
                    Color color = d.Severity == CinematicSeverity.Error ? ErrorColor : Color.Orange;
                    batch.DrawCircle(new Vector2(item.WorldPosition.X, -item.WorldPosition.Y), 60f, 24, color, width * 2);
                    GUI.DrawString(batch, new Vector2(item.WorldPosition.X + 70f, -item.WorldPosition.Y - 20f), d.Message, color, Color.Black * .6f, 4, GUIStyle.SmallFont);
                }
        }

        private static void DrawPlan(SpriteBatch batch, Vec2 origin, float width) {
            CinematicPlan plan = active.Plan;
            for (int s = 0; s < plan.StageCount; s++) {
                CinematicStage stage = plan.StageAt(s);
                for (int c = 0; c < stage.CueCount; c++) {
                    CinematicCue cue = stage.CueAt(c);
                    switch (cue.Action) {
                        case CinematicAction.Flight:
                            Vec2 previous = default;
                            for (int w = 0; w < cue.Route.Length; w++) {
                                CinematicWaypoint point = cue.Route[w];
                                Vec2 at = origin + point.Position;
                                float units = UnitsPerMetre(cue.Target);
                                batch.DrawCircle(Screen(at), point.Radius * units, 32, RouteColor * .8f, width);
                                if (point.Mode == CinematicFlightMode.Land) GUI.DrawLine(batch, Screen(at + new Vec2(-point.Radius * units, 0)), Screen(at + new Vec2(point.Radius * units, 0)), RouteColor, 0, width * 3);
                                if (w > 0) DrawArrow(batch, previous, at, RouteColor * .7f, width);
                                GUI.DrawString(batch, Screen(at) + new Vector2(8, 8), point.Id + " · " + point.Mode + " · " + point.Speed.ToString("0") + " м/с", RouteColor, Color.Black * .5f, 3, GUIStyle.SmallFont);
                                previous = at;
                            }
                            break;
                        case CinematicAction.Camera:
                            if (cue.CameraMode == CinematicCameraMode.Follow) break;
                            Vec2 start = origin + cue.Position, end = origin + cue.End;
                            DrawFrame(batch, start, cue.Zoom, CameraColor * .7f, width);
                            if (end != start) { DrawFrame(batch, end, cue.Zoom, CameraColor * .45f, width); DrawArrow(batch, start, end, CameraColor * .7f, width); }
                            GUI.DrawString(batch, Screen(start) + new Vector2(8, -24), plan.StageAt(s).Id + " · " + cue.CameraMode, CameraColor, Color.Black * .5f, 3, GUIStyle.SmallFont);
                            break;
                        case CinematicAction.Sound:
                        case CinematicAction.Particle:
                            if (plan.BindingAt(cue.Target).Kind != CinematicBindingKind.Anchor) break;
                            batch.DrawCircle(Screen(origin + cue.Position), cue.Radius * (preview?.Units ?? 100f), 40, EffectColor * .5f, width);
                            break;
                        case CinematicAction.Light:
                            if (active.Bindings.TryGetValue(plan.BindingAt(cue.Target).Id, out ushort lightId) && Entity.FindEntityByID(lightId) is Item lamp) {
                                Vec2 lampAt = new Vec2(lamp.WorldPosition.X, lamp.WorldPosition.Y);
                                batch.DrawCircle(Screen(lampAt), 80f, 24, Color.Yellow * .7f, width);
                                GUI.DrawString(batch, Screen(lampAt) + new Vector2(8, 8), plan.StageAt(s).Id + " · свет ×" + cue.Intensity.ToString("0.0") + (cue.Enabled ? "" : " выкл"), Color.Yellow, Color.Black * .5f, 3, GUIStyle.SmallFont);
                            }
                            break;
                        case CinematicAction.Wash:
                            Vec2 zone = origin + cue.Position;
                            float radius = cue.Radius * UnitsPerMetre(cue.Target);
                            GUI.DrawLine(batch, Screen(zone - new Vec2(radius, 0)), Screen(zone + new Vec2(radius, 0)), EffectColor * .8f, 0, width * 2);
                            break;
                    }
                }
            }
        }

        private static float UnitsPerMetre(int binding) => preview?.UnitsPerMetre(binding) ?? 100f;
        private static double TimelineSpan() => preview == null ? 1 : preview.Finished ? Math.Max(preview.Duration, .001) : preview.Plan.MaximumSeconds;

        private static void DrawPath(SpriteBatch batch, float width) {
            for (int v = 0; v < preview.Vehicles.Length; v++) {
                int count = preview.Frames.Count;
                Vec2 previous = preview.PoseDisplay(0, v);
                for (int i = 15; i < count; i += 15) {
                    Vec2 next = preview.PoseDisplay(i, v);
                    GUI.DrawLine(batch, Screen(previous), Screen(next), PathColor * .8f, 0, width);
                    previous = next;
                }
            }
        }

        private static void DrawCrew(SpriteBatch batch, int frame, float width) {
            for (int i = 0; i < preview.Crew.Members.Length; i++) {
                if (preview.Plan.BindingAt(i).Kind != CinematicBindingKind.Actor) continue;
                CinematicCrewPhase phase = preview.CrewPhase(frame, i);
                if (phase == CinematicCrewPhase.Seated) continue;
                Vec2 at = preview.CrewPosition(frame, i);
                Color color = phase == CinematicCrewPhase.Climbing ? CrewColor * .4f : CrewColor * .8f;
                GUI.DrawRectangle(batch, Screen(at + new Vec2(-20, 170)), new Vector2(40, 170), color, false, 0, width);
                batch.DrawCircle(Screen(at + new Vec2(0, 190)), 18, 12, color, width);
            }
        }

        private static void DrawWash(SpriteBatch batch, int frame, float width) {
            CinematicPreviewFrame f = preview.Frames[frame];
            for (int i = 0; i < f.WashCount; i++) {
                CinematicWashSample sample = preview.Wash(f.WashStart + i);
                Vec2 velocity = CinematicWashMath.Launch(new CinematicOutwash { Speed = sample.Speed, Ratio = sample.Ratio }, .5f, .5f);
                Color color = Color.Lerp(EffectColor * .3f, Color.White, CinematicWashMath.Strength(new CinematicOutwash { Ratio = sample.Ratio }));
                DrawArrow(batch, sample.Ground, sample.Ground + velocity * .25f, color, width);
            }
        }

        private static void DrawFrame(SpriteBatch batch, Vec2 centre, float zoom, Color color, float width) {
            Camera cam = GameMain.SubEditorScreen.Cam;
            Vector2 size = new Vector2(cam.Resolution.X, cam.Resolution.Y) / MathF.Max(.1f, zoom);
            GUI.DrawRectangle(batch, Screen(centre) - size * .5f, size, color, false, 0, width);
        }

        private static void DrawArrow(SpriteBatch batch, Vec2 from, Vec2 to, Color color, float width) {
            Vector2 a = Screen(from), b = Screen(to);
            GUI.DrawLine(batch, a, b, color, 0, width);
            Vector2 direction = b - a;
            float length = direction.Length();
            if (length < 1f) return;
            direction /= length;
            Vector2 side = new Vector2(-direction.Y, direction.X);
            float head = MathF.Min(length * .3f, 40f * width);
            GUI.DrawLine(batch, b, b - direction * head + side * head * .5f, color, 0, width);
            GUI.DrawLine(batch, b, b - direction * head - side * head * .5f, color, 0, width);
        }
    }
}
