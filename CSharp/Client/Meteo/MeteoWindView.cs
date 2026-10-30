using System;
using ScpItdMod.Ui;
using ScpItdMod.Xp;
using Draw = ScpItdMod.Ui.Paint;
using Vec = ScpItdMod.Core.Vec2;

namespace ScpItdMod.Meteo {
    public sealed partial class MeteoScreen {
        private static readonly string[] CompassLetters = { "С", "В", "Ю", "З" };
        private readonly string[,] windRows = new string[5, 3];
        private readonly string[] windTicks = { "10", "20", "30" };

        private void CacheWindHistory() {
            for (int ring = 0; ring < 3; ring++) { windTicks[ring] = MeteoText.Number(hodoMaximum * (ring + 1) / 3, "0"); }
            for (int row = 0; row < 5; row++) {
                double age = Spans[spanIndex] * (row == 0 ? 1 : row == 1 ? 2.0 / 3 : row == 2 ? 1.0 / 3 : row == 3 ? 1.0 / 6 : 0);
                windRows[row, 0] = row == 4 ? "Сейчас" : "−" + MeteoText.Number((float)(age / 60), "0") + " мин";
                int at = plotData.Nearest(plotData.To - age);
                if (at < 0 || !plotData.At(at).WindValid) { windRows[row, 1] = windRows[row, 2] = "—"; continue; }
                ref readonly MeteoSample sample = ref plotData.At(at);
                windRows[row, 1] = MeteoText.Number(sample.Wind);
                windRows[row, 2] = MeteoText.Number((sample.Direction * 180 / MathF.PI + 360) % 360, "000") + "°";
            }
        }

        private void WindAnalysis(ICanvas c, XpFaces f, in Box box) {
            Box inner = Panel(c, f, box, "ВЕКТОРНЫЙ АНАЛИЗ ВЕТРА");
            Draw.Text(c, f.Body, "Годограф скорости воздуха", inner.TakeTop(15), MeteoLook.Ink, Align.Left);
            float cx = inner.CenterX, cy = inner.Y + 77, radius = 53;
            for (int ring = 1; ring <= 3; ring++) { Scope.Ring(c, cx, cy, radius * ring / 3, MeteoLook.Axis); }
            Scope.Line(c, cx - radius - 4, cy, cx + radius + 4, cy, MeteoLook.Axis);
            Scope.Line(c, cx, cy - radius - 4, cx, cy + radius + 4, MeteoLook.Axis);
            for (int i = 0; i < 4; i++) {
                float angle = i * MathF.PI * .5f;
                Draw.Text(c, f.Bold, CompassLetters[i], new Box(cx + MathF.Sin(angle) * (radius + 12) - 8, cy - MathF.Cos(angle) * (radius + 12) - 7, 16, 15), MeteoLook.Ink, Align.Center);
            }
            for (int ring = 1; ring <= 3; ring++) { Draw.Text(c, f.Body, windTicks[ring - 1], new Box(cx + 3, cy - radius * ring / 3 - 6, 35, 13), MeteoLook.Muted, Align.Left); }
            Draw.Text(c, f.Body, "м/с", new Box(cx + 24, cy - radius - 13, 32, 14), MeteoLook.Muted, Align.Left);
            if (PlotReady) {
                bool previous = false; Vec prior = default;
                c.PushClip(new Box(cx - radius - 1, cy - radius - 1, radius * 2 + 2, radius * 2 + 2));
                for (int i = 0; i < plotData.Count; i++) {
                    ref readonly MeteoSample sample = ref plotData.At(i);
                    if (!sample.WindValid) { previous = false; continue; }
                    Vec point = MeteoPlotMath.Velocity(sample.Wind, sample.Direction) * (radius / hodoMaximum);
                    if (previous && plotData.Connects(i)) { Scope.Line(c, cx + prior.X, cy + prior.Y, cx + point.X, cy + point.Y, MeteoLook.Pressure); }
                    if (i % Math.Max(1, plotData.Count / 12) == 0 || i == plotData.Count - 1) { Scope.Cross(c, cx + point.X, cy + point.Y, 1, MeteoLook.Pressure); }
                    prior = point; previous = true;
                }
                c.PopClip();
            }
            if (HasReading && Reading.WindValid) {
                Vec current = windVector.Value * (radius / hodoMaximum);
                Scope.Arrow(c, cx, cy, cx + current.X, cy + current.Y, Stale ? MeteoLook.Muted : MeteoLook.Alarm, 7);
                c.Fill(new Box(MathF.Round(cx + current.X) - 2, MathF.Round(cy + current.Y) - 2, 5, 5), Stale ? MeteoLook.Muted : MeteoLook.Alarm);
            }
            Draw.Text(c, f.Body, "Направление в таблице: откуда дует", new Box(inner.X, inner.Y + 148, inner.W, 14), MeteoLook.Muted, Align.Left);
            float tableY = inner.Y + 166;
            c.Fill(new Box(inner.X, tableY, inner.W, 15), MeteoLook.Rule);
            Draw.Text(c, f.Body, "Время", new Box(inner.X + 2, tableY, 69, 15), MeteoLook.Ink, Align.Left);
            Draw.Text(c, f.Body, "м/с", new Box(inner.X + 75, tableY, 56, 15), MeteoLook.Ink, Align.Right);
            Draw.Text(c, f.Body, "Направл.", new Box(inner.Right - 85, tableY, 83, 15), MeteoLook.Ink, Align.Right);
            for (int row = 0; row < 5; row++) {
                float y = tableY + 16 + row * 13;
                c.Fill(new Box(inner.X, y + 12, inner.W, 1), MeteoLook.Rule);
                Draw.Text(c, f.Body, PlotReady ? windRows[row, 0] : "—", new Box(inner.X + 2, y, 76, 13), MeteoLook.Ink, Align.Left);
                Draw.Text(c, f.Body, PlotReady ? windRows[row, 1] : "—", new Box(inner.X + 79, y, 52, 13), MeteoLook.Ink, Align.Right);
                Draw.Text(c, f.Body, PlotReady ? windRows[row, 2] : "—", new Box(inner.Right - 81, y, 79, 13), MeteoLook.Ink, Align.Right);
            }
        }
    }
}
