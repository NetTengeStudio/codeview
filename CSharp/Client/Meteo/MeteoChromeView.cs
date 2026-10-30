using System;
using ScpItdMod.Motion;
using ScpItdMod.Ui;
using ScpItdMod.Xp;
using Draw = ScpItdMod.Ui.Paint;

namespace ScpItdMod.Meteo {
    public sealed partial class MeteoScreen {
        private static readonly string[] MenuNames = { "Сеанс", "Архив", "Оповещение", "Журнал", "Сервис" };
        private readonly OpacityCanvas selectorCanvas = new OpacityCanvas();
        private readonly struct ControlMotion {
            public readonly MotionHandle<float> Hover, Press, Selection;
            public ControlMotion(MotionScope owner) { Hover = owner.Scalar(); Press = owner.Scalar(); Selection = owner.Scalar(); }
        }

        private bool Button(ICanvas c, XpFaces f, int id, in Box box, string label, bool enabled = true, bool isDefault = false) => Control(c, f, id, box, label, enabled, false, isDefault);
        private bool Toggle(ICanvas c, XpFaces f, int id, in Box box, string label, bool selected) => Control(c, f, id, box, label, true, selected, false);
        private bool Control(ICanvas c, XpFaces f, int id, in Box box, string label, bool enabled, bool selected, bool isDefault) {
            if (!controls.TryGetValue(id, out ControlMotion motion)) { motion = new ControlMotion(scope); controls.Add(id, motion); }
            Box drawn = box.Snap();
            var pose = new VisualPose(drawn);
            bool hover = enabled && pose.Contains(ui.In.X, ui.In.Y);
            bool clicked = enabled && ui.Click(id, pose);
            bool held = hover && ui.Active == id && ui.In.Down;
            motion.Hover.To(hover ? 1 : 0, MotionPresets.Hover);
            motion.Press.To(held ? 1 : 0, MotionPresets.Press);
            motion.Selection.To(selected ? 1 : 0, MotionPresets.Hover);
            float pressAmount = Math.Clamp(motion.Press.Value, 0, 1), selection = Math.Clamp(motion.Selection.Value, 0, 1);
            Rgba face = Rgba.Mix(MeteoLook.Surface, MeteoLook.ControlHot, Math.Clamp(motion.Hover.Value, 0, 1) * .5f);
            face = Rgba.Mix(face, MeteoLook.Pressure, selection);
            c.Fill(drawn, face);
            Draw.Frame(c, drawn, 1, enabled ? MeteoLook.Axis : MeteoLook.Rule);
            Rgba top = pressAmount > .5f ? MeteoLook.Axis : MeteoLook.Highlight;
            c.Fill(new Box(drawn.X + 1, drawn.Y + 1, drawn.W - 2, 1), top);
            c.Fill(new Box(drawn.X + 1, drawn.Y + 1, 1, drawn.H - 2), top);
            if (isDefault) { Draw.Frame(c, drawn.Inset(2), 1, MeteoLook.Ink); }
            Rgba ink = !enabled ? MeteoLook.Muted : selection > .5f ? MeteoLook.Highlight : MeteoLook.Ink;
            Draw.Clipped(c, selected ? f.Bold : f.Body, label, drawn.Inset(4).Offset(0, pressAmount), ink, Align.Center);
            if (clicked) { MeteoClient.Click(); }
            return clicked;
        }

        private static Box Panel(ICanvas c, XpFaces f, in Box box, string title, bool alarm = false) {
            c.Fill(box, MeteoLook.Surface);
            Draw.Frame(c, box, 1, MeteoLook.Axis);
            c.Fill(new Box(box.X + 1, box.Y + 1, box.W - 2, 1), MeteoLook.Highlight);
            c.Fill(new Box(box.X + 2, box.Y + 2, box.W - 4, 19), alarm ? MeteoLook.Alarm : MeteoLook.Chrome);
            Draw.Clipped(c, f.Bold, title, new Box(box.X + 6, box.Y + 2, box.W - 12, 19), MeteoLook.Highlight, Align.Left);
            return box.Inset(5, 24, 5, 5);
        }

        private void Chrome(ICanvas c, XpFaces f, double now) {
            c.Fill(new Box(0, 0, MeteoLook.W, 25), MeteoLook.Chrome);
            Draw.Text(c, f.Bold, "МЕТЕОКОНТРОЛЬ / АМС-М", new Box(12, 0, 500, 25), MeteoLook.Highlight, Align.Left);
            Draw.Text(c, f.Body, clockText, new Box(MeteoLook.W - 175, 0, 125, 25), MeteoLook.Highlight, Align.Right);
            if (Button(c, f, 9000, new Box(MeteoLook.W - 27, 3, 23, 19), "×")) { done = true; }
            c.Fill(new Box(0, 25, MeteoLook.W, 24), MeteoLook.Surface);
            c.Fill(new Box(0, 48, MeteoLook.W, 1), MeteoLook.Axis);
            for (int i = 0; i < MenuNames.Length; i++) {
                if (Toggle(c, f, 100 + i, new Box(7 + i * 94, 28, 90, 18), MenuNames[i], (int)tab == i)) { SwitchTab((MeteoTab)i); }
            }
            Draw.Text(c, f.Body, "Станции", new Box(11, 54, 70, 25), MeteoLook.Ink, Align.Left);
            if (Button(c, f, 300, new Box(78, 54, 252, 25), stationCaption, session.HasOverview)) { SetSelector(!selectorOpen); }
            Scope.Line(c, 316, 64, 320, 68, MeteoLook.Ink); Scope.Line(c, 320, 68, 324, 64, MeteoLook.Ink);
            if (Toggle(c, f, 301, new Box(340, 54, 138, 25), Historical ? "АРХИВНЫЙ СРЕЗ" : "ОПЕРАТИВНЫЙ", !Historical)) { SelectTime(double.NaN, false); }
            Draw.Text(c, f.Body, "История:", new Box(489, 54, 57, 25), MeteoLook.Ink, Align.Left);
            for (int i = 0; i < Spans.Length; i++) {
                if (Toggle(c, f, 160 + i, new Box(550 + i * 65, 54, 61, 25), SpanNames[i], spanIndex == i)) {
                    spanIndex = i; SelectTime(double.NaN, false); session.Refresh();
                }
            }
            Draw.Clipped(c, f.Body, ageText, new Box(824, 54, MeteoLook.W - 842, 25), SourceStale ? MeteoLook.Warn : MeteoLook.Ink, Align.Left);
            c.Fill(new Box(MeteoLook.W - 17, 63, 7, 7), !SourceStale ? MeteoLook.Good : MeteoLook.Alarm);
            c.Fill(new Box(0, 83, MeteoLook.W, 1), MeteoLook.Axis);
        }

        private void SetSelector(bool open) {
            if (selectorOpen == open || stationMenu == null) { return; }
            selectorOpen = open;
            stationMenu.Play(MotionPatterns.Visibility(stationMenu, open, MotionStyle.Menu));
        }
        private void StationSelector(ICanvas canvas, XpFaces f) {
            int rows = Math.Min(MeteoWire.MaxStations, O.Stations.Count);
            stationMenu.Place(new Box(78, 82, 350, rows * 23 + 29), MotionPresets.Layout, true);
            VisualPose pose = stationMenu.Pose;
            selectorCanvas.Inner = canvas; selectorCanvas.Alpha = pose.Opacity;
            ICanvas c = selectorCanvas;
            Box inner = Panel(c, f, pose.Bounds, "СТАНЦИИ И ПРИБОРЫ НА ЛИНИИ");
            for (int i = 0; i < rows; i++) {
                MeteoStationInfo info = O.Stations[i];
                Box row = new Box(inner.X, inner.Y + i * 23, inner.W, 22);
                if (Button(c, f, 320 + i, row, stationCaptions[i] ?? info.Name, selectorOpen && info.Kind == MeteoKind.Station)) { SelectStation(info.Id); }
            }
            if (selectorOpen && ui.In.Pressed && !pose.Contains(ui.In.X, ui.In.Y) && ui.In.Y > 82) { SetSelector(false); }
        }

        private void StatusBar(ICanvas c, XpFaces f, in Box bar, double now) {
            c.Fill(bar, MeteoLook.Surface); Draw.Frame(c, bar, 1, MeteoLook.Axis);
            string left = status.Length > 0 && now - statusAt < 8 ? status : !Live ? "Архив недоступен" : SourceStale ? "Данные устарели / проверьте линию" : O.RackName;
            Draw.Clipped(c, f.Body, left, new Box(8, bar.Y, 402, bar.H), status.Length > 0 && now - statusAt < 8 ? statusTone : MeteoLook.Ink, Align.Left);
            c.Fill(new Box(416, bar.Y + 2, 1, bar.H - 4), MeteoLook.Axis);
            Draw.Text(c, f.Body, "Запись: 10 с", new Box(427, bar.Y, 130, bar.H), MeteoLook.Ink, Align.Left);
            c.Fill(new Box(565, bar.Y + 2, 1, bar.H - 4), MeteoLook.Axis);
            Draw.Text(c, f.Body, serverClockCaption, new Box(577, bar.Y, 235, bar.H), MeteoLook.Ink, Align.Left);
            Draw.Text(c, f.Body, shell.Speaking ? "Микрофон: передача" : "Микрофон: молчит", new Box(823, bar.Y, 190, bar.H), MeteoLook.Ink, Align.Right);
        }
    }
}
