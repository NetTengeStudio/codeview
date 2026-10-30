using System;

namespace ScpItdMod.Paper {
    public interface IGlyphs {
        float Advance(char c);
    }

    public sealed class InkLayout {
        private float[] x = new float[64];
        private short[] line = new short[64];
        private int[] starts = new int[16];

        public int Count { get; private set; }

        public int Lines { get; private set; } = 1;

        public float X(int caret) => x[caret];

        public int Line(int caret) => line[caret];

        public int Start(int row) => starts[row];

        public int End(int row) => row + 1 < Lines ? starts[row + 1] - 1 : Count;

        public void Build(ReadOnlySpan<char> text, float width, IGlyphs glyphs) {
            int n = text.Length;
            Count = n;
            if (x.Length < n + 1) {
                int size = Math.Max(n + 1, x.Length * 2);
                x = new float[size];
                line = new short[size];
            }
            Lines = 1;
            starts[0] = 0;
            int row = 0;
            int rowStart = 0;
            int lastBreak = -1;
            float pen = 0.0f;
            for (int i = 0; i < n; i++) {
                char c = text[i];
                x[i] = pen;
                line[i] = (short)row;
                if (c == '\n') {
                    row = NewRow(i + 1);
                    rowStart = i + 1;
                    lastBreak = -1;
                    pen = 0.0f;
                    continue;
                }
                float advance = glyphs.Advance(c);
                if (c != ' ' && pen + advance > width && i > rowStart) {
                    int from = lastBreak > rowStart ? lastBreak : i;
                    float shift = x[from];
                    row = NewRow(from);
                    rowStart = from;
                    lastBreak = -1;
                    for (int j = from; j <= i; j++) {
                        x[j] -= shift;
                        line[j] = (short)row;
                    }
                    pen = x[i];
                }
                pen += advance;
                if (c == ' ') { lastBreak = i + 1; }
            }
            x[n] = pen;
            line[n] = (short)row;
        }

        private int NewRow(int start) {
            if (Lines == starts.Length) { Array.Resize(ref starts, starts.Length * 2); }
            starts[Lines] = start;
            return Lines++;
        }

        public int Hit(int row, float at) {
            if (row < 0) { return 0; }
            if (row >= Lines) { return Count; }
            int from = Start(row);
            int to = End(row);
            int best = from;
            float bestDistance = float.MaxValue;
            for (int i = from; i <= to; i++) {
                float d = MathF.Abs(x[i] - at);
                if (d < bestDistance) {
                    bestDistance = d;
                    best = i;
                }
            }
            return best;
        }
    }

    public enum InkMove : byte { Left, Right, WordLeft, WordRight, Home, End, Up, Down, First, Last }

    public sealed class InkEdit {
        private const int UndoDepth = 48;
        private const double GroupSeconds = 1.2;

        private enum Kind : byte { None, Typing, Erasing, Other }

        private struct Snapshot {
            public string Text;
            public int Caret;
        }

        private char[] text = new char[256];
        private char[] scratch = new char[256];
        private int length;
        private string cached = "";
        private int cachedVersion = -1;
        private readonly Snapshot[] undo = new Snapshot[UndoDepth];
        private readonly Snapshot[] redo = new Snapshot[UndoDepth];
        private int undoCount;
        private int redoCount;
        private Kind lastKind;
        private double lastTime = double.NegativeInfinity;
        private float goal = -1.0f;

        public readonly InkLayout Layout = new InkLayout();
        public IGlyphs Glyphs { get; private set; }
        public Action<int, int, int> Edited;
        public float Width { get; private set; }
        public readonly int MaxLines;
        public readonly int MaxChars;

        public InkEdit(IGlyphs glyphs, float width, int maxLines, int maxChars) {
            Glyphs = glyphs ?? throw new ArgumentNullException(nameof(glyphs));
            Width = width;
            MaxLines = maxLines;
            MaxChars = maxChars;
            Layout.Build(ReadOnlySpan<char>.Empty, Width, Glyphs);
        }

        // Reflow is presentation-only: text, revision, selection and undo ownership are unchanged.
        public void Reflow(IGlyphs glyphs, float width) {
            if (glyphs == null) { throw new ArgumentNullException(nameof(glyphs)); }
            if (!float.IsFinite(width) || width <= 0) { throw new ArgumentOutOfRangeException(nameof(width)); }
            Glyphs = glyphs; Width = width; goal = -1;
            Layout.Build(Span, Width, Glyphs);
        }

        public int Caret { get; private set; }

        public int Anchor { get; private set; } = -1;

        public int Version { get; private set; }

        public int Length => length;

        public int EditAt { get; private set; }

        public int EditRemoved { get; private set; }

        public int EditInserted { get; private set; }

        public bool HasSelection => Anchor >= 0 && Anchor != Caret;

        public int SelectionStart => HasSelection ? Math.Min(Anchor, Caret) : Caret;

        public int SelectionEnd => HasSelection ? Math.Max(Anchor, Caret) : Caret;

        public ReadOnlySpan<char> Span => new ReadOnlySpan<char>(text, 0, length);

        public string Text {
            get {
                if (cachedVersion != Version) {
                    cached = new string(text, 0, length);
                    cachedVersion = Version;
                }
                return cached;
            }
        }

        public void Load(string value) {
            value ??= "";
            int n = Clean(value.AsSpan(), Math.Min(value.Length, MaxChars), ref text);
            length = n;
            Version++;
            Note(0, int.MaxValue, n);
            Layout.Build(Span, Width, Glyphs);
            while (Layout.Lines > MaxLines && length > 0) {
                length = Layout.Start(MaxLines);
                while (length > 0 && (text[length - 1] == '\n' || text[length - 1] == ' ')) { length--; }
                Layout.Build(Span, Width, Glyphs);
            }
            Caret = length;
            Anchor = -1;
            undoCount = 0;
            redoCount = 0;
            lastKind = Kind.None;
            goal = -1.0f;
        }

        public bool Type(ReadOnlySpan<char> input, double now) {
            Span<char> clean = input.Length <= 512 ? stackalloc char[input.Length] : new char[input.Length];
            int n = 0;
            for (int i = 0; i < input.Length; i++) {
                char c = Filter(input[i]);
                if (c != '\0') { clean[n++] = c; }
            }
            if (n == 0) { return false; }
            Group(n == 1 && clean[0] != '\n' ? Kind.Typing : Kind.Other, now, n == 1 && (clean[0] == ' ' || clean[0] == '\n'));
            return Replace(SelectionStart, SelectionEnd, clean.Slice(0, n));
        }

        public bool Back(bool word, double now) {
            if (HasSelection) {
                Group(Kind.Erasing, now, false);
                return Replace(SelectionStart, SelectionEnd, ReadOnlySpan<char>.Empty);
            }
            if (Caret == 0) { return false; }
            int from = word ? WordStart(Caret) : Caret - 1;
            Group(Kind.Erasing, now, false);
            return Replace(from, Caret, ReadOnlySpan<char>.Empty);
        }

        public bool Delete(bool word, double now) {
            if (HasSelection) {
                Group(Kind.Erasing, now, false);
                return Replace(SelectionStart, SelectionEnd, ReadOnlySpan<char>.Empty);
            }
            if (Caret >= length) { return false; }
            int to = word ? WordEnd(Caret) : Caret + 1;
            Group(Kind.Erasing, now, false);
            return Replace(Caret, to, ReadOnlySpan<char>.Empty);
        }

        public string Selected() => HasSelection ? new string(text, SelectionStart, SelectionEnd - SelectionStart) : "";

        public void SelectAll() {
            Anchor = 0;
            Caret = length;
            goal = -1.0f;
        }

        public void Move(InkMove move, bool extend) {
            int target = Caret;
            if (!extend && HasSelection && (move == InkMove.Left || move == InkMove.Right)) {
                target = move == InkMove.Left ? SelectionStart : SelectionEnd;
                Anchor = -1;
                Caret = target;
                goal = -1.0f;
                lastKind = Kind.Other;
                return;
            }
            switch (move) {
                case InkMove.Left: target = Math.Max(0, Caret - 1); break;
                case InkMove.Right: target = Math.Min(length, Caret + 1); break;
                case InkMove.WordLeft: target = WordStart(Caret); break;
                case InkMove.WordRight: target = WordEnd(Caret); break;
                case InkMove.Home: target = Layout.Start(Layout.Line(Caret)); break;
                case InkMove.End: target = Layout.End(Layout.Line(Caret)); break;
                case InkMove.First: target = 0; break;
                case InkMove.Last: target = length; break;
                case InkMove.Up:
                case InkMove.Down: {
                    if (goal < 0.0f) { goal = Layout.X(Caret); }
                    int row = Layout.Line(Caret) + (move == InkMove.Up ? -1 : 1);
                    target = row < 0 ? 0 : row >= Layout.Lines ? length : Layout.Hit(row, goal);
                    Place(target, extend);
                    return;
                }
            }
            Place(target, extend);
            goal = -1.0f;
        }

        public void Jump(int caret) {
            Place(Math.Clamp(caret, 0, length), false);
            goal = -1.0f;
        }

        public void Point(int row, float at, bool extend) {
            Place(Layout.Hit(row, at), extend);
            goal = -1.0f;
            lastKind = Kind.Other;
        }

        public void SelectWord(int caret) {
            caret = Math.Clamp(caret, 0, length);
            Anchor = WordStart(caret < length && IsWord(text[caret]) ? caret + 1 : caret);
            Caret = WordEnd(Anchor);
            goal = -1.0f;
        }

        public bool Undo() {
            if (undoCount == 0) { return false; }
            Push(redo, ref redoCount);
            Snapshot s = undo[--undoCount];
            Restore(s);
            return true;
        }

        public bool Redo() {
            if (redoCount == 0) { return false; }
            Push(undo, ref undoCount);
            Snapshot s = redo[--redoCount];
            Restore(s);
            return true;
        }

        private void Restore(Snapshot s) {
            length = Clean(s.Text.AsSpan(), s.Text.Length, ref text);
            Version++;
            Note(0, int.MaxValue, length);
            Layout.Build(Span, Width, Glyphs);
            Caret = Math.Clamp(s.Caret, 0, length);
            Anchor = -1;
            lastKind = Kind.None;
            goal = -1.0f;
        }

        private void Place(int target, bool extend) {
            target = Math.Clamp(target, 0, length);
            if (extend) {
                if (Anchor < 0) { Anchor = Caret; }
            } else {
                Anchor = -1;
            }
            Caret = target;
            if (Anchor == Caret) { Anchor = -1; }
            lastKind = Kind.Other;
        }

        private bool Replace(int from, int to, ReadOnlySpan<char> insert) {
            int next = length - (to - from) + insert.Length;
            if (next > MaxChars) { return false; }
            if (scratch.Length < next) { scratch = new char[Math.Max(next, scratch.Length * 2)]; }
            Array.Copy(text, 0, scratch, 0, from);
            insert.CopyTo(new Span<char>(scratch, from, insert.Length));
            Array.Copy(text, to, scratch, from + insert.Length, length - to);
            Layout.Build(new ReadOnlySpan<char>(scratch, 0, next), Width, Glyphs);
            if (Layout.Lines > MaxLines) {
                Layout.Build(Span, Width, Glyphs);
                return false;
            }
            (text, scratch) = (scratch, text);
            length = next;
            Version++;
            Note(from, to - from, insert.Length);
            Caret = from + insert.Length;
            Anchor = -1;
            goal = -1.0f;
            redoCount = 0;
            return true;
        }

        private void Note(int at, int removed, int inserted) {
            EditAt = at;
            EditRemoved = removed;
            EditInserted = inserted;
            Edited?.Invoke(at, removed, inserted);
        }

        private void Group(Kind kind, double now, bool boundary) {
            bool fresh = kind != lastKind || kind == Kind.Other || now - lastTime > GroupSeconds;
            if (fresh) { Push(undo, ref undoCount); }
            lastKind = boundary ? Kind.Other : kind;
            lastTime = now;
        }

        private void Push(Snapshot[] stack, ref int count) {
            if (count == UndoDepth) {
                Array.Copy(stack, 1, stack, 0, UndoDepth - 1);
                count--;
            }
            stack[count++] = new Snapshot { Text = Text, Caret = Caret };
        }

        private int WordStart(int at) {
            int i = at;
            while (i > 0 && !IsWord(text[i - 1])) { i--; }
            while (i > 0 && IsWord(text[i - 1])) { i--; }
            return i;
        }

        private int WordEnd(int at) {
            int i = at;
            while (i < length && !IsWord(text[i])) { i++; }
            while (i < length && IsWord(text[i])) { i++; }
            return i;
        }

        private static bool IsWord(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '-';

        public static char Filter(char c) {
            if (c == '\n' || c == '\r') { return c == '\n' ? '\n' : '\0'; }
            if (c == '\t') { return ' '; }
            if (char.IsControl(c) || char.IsSurrogate(c) || c == '﻿') { return '\0'; }
            return c;
        }

        private static int Clean(ReadOnlySpan<char> source, int max, ref char[] into) {
            if (into.Length < source.Length) { into = new char[Math.Max(source.Length, into.Length * 2)]; }
            int n = 0;
            for (int i = 0; i < source.Length && n < max; i++) {
                char c = Filter(source[i]);
                if (c != '\0') { into[n++] = c; }
            }
            return n;
        }
    }
}
