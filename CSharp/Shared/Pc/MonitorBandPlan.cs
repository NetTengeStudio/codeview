using System;
using System.Collections.Generic;

namespace ScpItdMod.Pc {
    // Precomputed draw strips. Adjacent source rows may be merged only if their
    // pixel-to-texel mapping is identical: same horizontal crop and constant
    // integer source height per destination row.
    public readonly struct MonitorBand {
        public readonly int X, Y, Width, Height, SourceX, SourceY, SourceWidth, SourceHeight;
        public MonitorBand(int x, int y, int width, int height, int sourceX, int sourceY, int sourceWidth, int sourceHeight) {
            X = x; Y = y; Width = width; Height = height;
            SourceX = sourceX; SourceY = sourceY; SourceWidth = sourceWidth; SourceHeight = sourceHeight;
        }
    }

    public static class MonitorBandPlan {
        public static MonitorBand[] Build(in MonitorGlass glass, int sourceWidth, int sourceHeight) {
            if (glass.Width <= 0 || glass.Height <= 0 || sourceWidth <= 0 || sourceHeight <= 0) {
                throw new ArgumentOutOfRangeException(nameof(glass));
            }
            var strips = new List<MonitorBand>(Math.Min(glass.Height, 32));
            int lastStep = 0;
            for (int y = 0; y < glass.Height; y++) {
                int inset = glass.Inset(y);
                int sx = (int)((long)inset * sourceWidth / glass.Width);
                int sy = (int)((long)y * sourceHeight / glass.Height);
                int nextSy = (int)((long)(y + 1) * sourceHeight / glass.Height);
                int step = Math.Max(1, nextSy - sy);
                int width = glass.Width - inset * 2;
                int sw = sourceWidth - sx * 2;
                if (width <= 0 || sw <= 0) { continue; }
                int x = glass.X + inset;
                int py = glass.Y + y;
                int at = strips.Count - 1;
                if (at >= 0) {
                    MonitorBand previous = strips[at];
                    if (lastStep == step && previous.X == x && previous.Width == width &&
                        previous.SourceX == sx && previous.SourceWidth == sw &&
                        previous.Y + previous.Height == py &&
                        previous.SourceY + previous.SourceHeight == sy) {
                        strips[at] = new MonitorBand(x, previous.Y, width, previous.Height + 1,
                            sx, previous.SourceY, sw, previous.SourceHeight + step);
                        continue;
                    }
                }
                strips.Add(new MonitorBand(x, py, width, 1, sx, sy, sw, step));
                lastStep = step;
            }
            return strips.ToArray();
        }
    }
}
