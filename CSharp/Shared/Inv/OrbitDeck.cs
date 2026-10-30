using System;

namespace ScpItdMod.Inv {
    // The container deck that unfolds from one wedge of the radial ring. Stack from the ring
    // outwards: an optional tab rail, item cells (one or two rows) and a pager line of dots. The
    // fan holds exactly as many cells as the container shows, centred on the wedge, so a box with
    // three items is three cells and not an empty grid. Every band shares the ring centre and the ring's angular rhythm, and
    // every size is a fraction of the screen height, so the deck scales exactly like the ring.
    public readonly struct OrbitDeck {
        public const int MaxChips = 9;
        public const int MaxCells = 48;
        public const int MaxDots = 12;
        public const float TabOfHeight = 0.052f;
        public const float TabLabelOfHeight = 0.078f;
        public const float CellOfHeight = 0.07f;
        public const float DotBandOfHeight = 0.014f;
        public const float MaxSpan = 2.0f;

        public readonly float RingX, RingY, Angle;
        public readonly BandSlot[] Chips, Cells;
        public readonly BandSlot Previous, Next;
        public readonly float[] DotAngles, TabPixels;
        public readonly float DotRadius, DotSize, CellStep;
        public readonly int Columns, Rows, Capacity, Pages;

        public bool HasDots => DotAngles.Length > 1;

        // Tabs take the width of their own label (measured by the caller, in pixels) and share the fan budget MaxSpan.
        // A label without a measurement gets the old fixed width, so the deck still lays out.
        public OrbitDeck(int screenWidth, int screenHeight, float ringX, float ringY, float ringOuter, float angle, int chipCount, int itemCount, float[] tabLabels = null, int maxBottom = 0) {
            int fitHeight = maxBottom > 0 ? Math.Min(maxBottom, screenHeight) : screenHeight;
            float h = screenHeight, gap = h * InvRing.GapOfHeight;
            float tabThick = h * TabOfHeight, cellThick = h * CellOfHeight, dotBand = h * DotBandOfHeight;
            RingX = ringX; RingY = ringY; Angle = angle;

            int chips = Math.Clamp(chipCount, 0, MaxChips);
            float tabInner = ringOuter + gap, tabOuter = chips > 0 ? tabInner + tabThick : ringOuter;
            float tabMid = tabInner + tabThick * 0.5f;
            float fallback = h * TabLabelOfHeight;
            var natural = new float[chips];
            for (int i = 0; i < chips; i++) { natural[i] = tabLabels != null && i < tabLabels.Length && tabLabels[i] > 0 ? tabLabels[i] : fallback; }
            TabPixels = AllotTabs(natural, MaxSpan * tabMid);
            Chips = new BandSlot[chips];
            float span = 0;
            for (int i = 0; i < chips; i++) { span += TabPixels[i]; }
            // The tab row is centred on the wedge, each tab takes its own arc length from the one before it.
            float cursor = angle - span * 0.5f / tabMid;
            for (int i = 0; i < chips; i++) {
                float step = TabPixels[i] / tabMid;
                Chips[i] = new BandSlot(tabInner, tabOuter, cursor + step * 0.5f, step * 0.5f, gap);
                cursor += step;
            }

            float row0Inner = tabOuter + gap, row0Outer = row0Inner + cellThick;
            float row1Inner = row0Outer + gap, row1Outer = row1Inner + cellThick;
            CellStep = (cellThick + gap) / (row0Inner + cellThick * 0.5f);
            int columns = Math.Clamp((int)MathF.Floor(MaxSpan / CellStep), 1, MaxCells);
            while (columns > 1 && !FitsFan(screenWidth, fitHeight, ringX, ringY, angle, CellStep, columns, row0Inner, row0Outer, gap)) { columns--; }
            int rows = FitsFan(screenWidth, fitHeight, ringX, ringY, angle, CellStep, columns, row1Inner, row1Outer, gap) ? 2 : 1;
            columns = Math.Min(columns, MaxCells / rows);

            int wanted = Math.Max(1, itemCount);
            if (wanted <= columns) { columns = wanted; rows = 1; }
            else if (rows == 2 && wanted <= columns * 2) { columns = (wanted + 1) / 2; }
            int pages = (wanted + columns * rows - 1) / (columns * rows);
            float dotInner = (rows == 2 ? row1Outer : row0Outer) + gap, dotOuter = dotInner + dotBand;
            float dotStep = MathF.Min(CellStep * 0.5f, MathF.Tau / 90f);
            bool dots = pages > 1 && pages <= MaxDots && DotsFit(screenWidth, fitHeight, ringX, ringY, angle, dotStep, pages, dotInner, dotOuter, gap);
            if (pages > 1 && pages <= MaxDots && !dots && rows == 2) {
                rows = 1;
                pages = (wanted + columns - 1) / columns;
                dotInner = row0Outer + gap; dotOuter = dotInner + dotBand;
                dots = pages <= MaxDots && DotsFit(screenWidth, fitHeight, ringX, ringY, angle, dotStep, pages, dotInner, dotOuter, gap);
            }
            Columns = columns; Rows = rows; Pages = pages;

            Cells = new BandSlot[columns * rows];
            for (int r = 0; r < rows; r++) {
                float inner = r == 0 ? row0Inner : row1Inner, outer = r == 0 ? row0Outer : row1Outer;
                for (int c = 0; c < columns; c++) {
                    float at = angle + (c - (columns - 1) * 0.5f) * CellStep;
                    Cells[r * columns + c] = new BandSlot(inner, outer, at, CellStep * 0.5f, gap);
                }
            }
            float edge = (columns - 1) * 0.5f + 1.0f;
            Previous = new BandSlot(row0Inner, row0Outer, angle - edge * CellStep, CellStep * 0.5f, gap);
            Next = new BandSlot(row0Inner, row0Outer, angle + edge * CellStep, CellStep * 0.5f, gap);
            Capacity = Cells.Length;

            DotRadius = 0.5f * (dotInner + dotOuter);
            DotSize = dotBand * 0.5f;
            DotAngles = dots ? new float[pages] : Array.Empty<float>();
            for (int i = 0; i < DotAngles.Length; i++) { DotAngles[i] = angle + (i - (DotAngles.Length - 1) * 0.5f) * dotStep; }
        }

        public int RowOf(int cell) => cell / Columns;

        public int ColumnOf(int cell) => cell % Columns;

        // Each tab keeps its measured width while the row fits the budget; a wider row shrinks all tabs in the same ratio,
        // and the caller then shortens the labels to the width they were given.
        public static float[] AllotTabs(float[] natural, float budget) {
            float total = 0;
            for (int i = 0; i < natural.Length; i++) { total += natural[i]; }
            var allotted = new float[natural.Length];
            float ratio = total > budget && total > 0 ? budget / total : 1f;
            for (int i = 0; i < natural.Length; i++) { allotted[i] = natural[i] * ratio; }
            return allotted;
        }

        public static bool Fits(in BandSlot slot, float ringX, float ringY, float width, float height) {
            const int Samples = 16;
            for (int r = 0; r < 2; r++) {
                float radius = r == 0 ? slot.Inner : slot.Outer;
                for (int s = 0; s <= Samples; s++) {
                    float at = slot.Angle + (s * 2f / Samples - 1f) * slot.Half;
                    float x = ringX + MathF.Cos(at) * radius, y = ringY + MathF.Sin(at) * radius;
                    if (x < 0 || y < 0 || x > width || y > height) { return false; }
                }
            }
            return true;
        }

        private static bool FitsFan(int width, int height, float ringX, float ringY, float angle, float step, int columns, float inner, float outer, float gap) {
            for (int c = 0; c < columns; c++) {
                float at = angle + (c - (columns - 1) * 0.5f) * step;
                if (!Fits(new BandSlot(inner, outer, at, step * 0.5f, gap), ringX, ringY, width, height)) { return false; }
            }
            float edge = (columns - 1) * 0.5f + 1.0f;
            return Fits(new BandSlot(inner, outer, angle - edge * step, step * 0.5f, gap), ringX, ringY, width, height) &&
                   Fits(new BandSlot(inner, outer, angle + edge * step, step * 0.5f, gap), ringX, ringY, width, height);
        }

        private static bool DotsFit(int width, int height, float ringX, float ringY, float angle, float step, int count, float inner, float outer, float gap) {
            for (int i = 0; i < count; i++) {
                float at = angle + (i - (count - 1) * 0.5f) * step;
                if (!Fits(new BandSlot(inner, outer, at, step * 0.5f, gap), ringX, ringY, width, height)) { return false; }
            }
            return true;
        }
    }
}
