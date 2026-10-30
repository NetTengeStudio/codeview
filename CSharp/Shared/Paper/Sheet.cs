using System;
using System.Globalization;
using System.Text;

namespace ScpItdMod.Paper {
    public static class Sheet {
        public const string Art = "Content/Items/Paper/paper_base.png";
        public const int Width = 258;
        public const int Height = 348;
        public const float TitleLeft = 84, TitleRight = 230, TitleTop = 30;
        public const float BodyLeft = 30.0f;
        public const float BodyTop = 70.0f;
        public const float BodyRight = 230.0f;
        public const float BodyBottom = 300.0f;
        public const float BodyWidth = BodyRight - BodyLeft;
        public const float Em = 13.0f;
        public const float Pitch = 16.0f;
        public const int Lines = (int)((BodyBottom - BodyTop) / Pitch);
        public const int BodyMax = 1200;

        public const string MarkArt = "Content/Items/Paper/paper_stamp-ok.png";
        public const int MarkArtWidth = 121;
        public const int MarkArtHeight = 54;
        public const float MarkWidth = 64.0f;
        public const float MarkHeight = MarkWidth * MarkArtHeight / MarkArtWidth;
        public const int MaxMarks = 5;
        public const float MaxTurn = 0.35f;
        public const int Sub = 4;

        public static int LinesOf(ReadOnlySpan<char> text) {
            int lines = 1;
            for (int i = 0; i < text.Length; i++) {
                if (text[i] == '\n') { lines++; }
            }
            return lines;
        }

        public static bool Inside(float x, float y) => x >= 0.0f && y >= 0.0f && x <= Width && y <= Height;
    }

    public readonly struct Mark {
        public readonly string Kind;
        public readonly ushort X;
        public readonly ushort Y;
        public readonly short Turn;

        public Mark(string kind, ushort x, ushort y, short turn) {
            Kind = kind;
            X = x;
            Y = y;
            Turn = turn;
        }

        public static Mark At(string kind, float x, float y, float turn) => new Mark(kind,
            (ushort)Math.Clamp((int)MathF.Round(x * Sheet.Sub), 0, ushort.MaxValue),
            (ushort)Math.Clamp((int)MathF.Round(y * Sheet.Sub), 0, ushort.MaxValue),
            (short)Math.Clamp((int)MathF.Round(turn * 1000.0f), short.MinValue, short.MaxValue));

        public float Left => X / (float)Sheet.Sub;

        public float Top => Y / (float)Sheet.Sub;

        public float Angle => Turn / 1000.0f;
    }

    public enum MarkError : byte { Ok, Full, Repeat, Outside, Bad }

    public static class Marks {
        public const int KindMax = 16;
        public const int TextMax = Sheet.MaxMarks * (KindMax + 20);

        public static bool ValidKind(string kind) {
            if (string.IsNullOrEmpty(kind) || kind.Length > KindMax) { return false; }
            for (int i = 0; i < kind.Length; i++) {
                char c = kind[i];
                if (!(c >= 'a' && c <= 'z') && !(c >= '0' && c <= '9') && c != '_') { return false; }
            }
            return true;
        }

        public static int Parse(string text, Span<Mark> into) {
            if (string.IsNullOrEmpty(text)) { return 0; }
            int count = 0;
            int at = 0;
            while (at <= text.Length) {
                int end = text.IndexOf(';', at);
                if (end < 0) { end = text.Length; }
                if (end > at) {
                    if (count >= into.Length || !ParseOne(text.AsSpan(at, end - at), out Mark mark)) { return -1; }
                    into[count++] = mark;
                }
                at = end + 1;
            }
            return count;
        }

        private static bool ParseOne(ReadOnlySpan<char> part, out Mark mark) {
            mark = default;
            int a = part.IndexOf(',');
            if (a <= 0) { return false; }
            ReadOnlySpan<char> rest = part.Slice(a + 1);
            int b = rest.IndexOf(',');
            if (b <= 0) { return false; }
            ReadOnlySpan<char> tail = rest.Slice(b + 1);
            int c = tail.IndexOf(',');
            if (c <= 0) { return false; }
            string kind = part.Slice(0, a).ToString();
            if (!ValidKind(kind)
                || !ushort.TryParse(rest.Slice(0, b), NumberStyles.None, CultureInfo.InvariantCulture, out ushort x)
                || !ushort.TryParse(tail.Slice(0, c), NumberStyles.None, CultureInfo.InvariantCulture, out ushort y)
                || !short.TryParse(tail.Slice(c + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out short turn)) {
                return false;
            }
            mark = new Mark(kind, x, y, turn);
            return true;
        }

        public static string Format(ReadOnlySpan<Mark> marks) {
            if (marks.Length == 0) { return ""; }
            var text = new StringBuilder(marks.Length * 20);
            for (int i = 0; i < marks.Length; i++) {
                if (i > 0) { text.Append(';'); }
                text.Append(marks[i].Kind).Append(',')
                    .Append(marks[i].X.ToString(CultureInfo.InvariantCulture)).Append(',')
                    .Append(marks[i].Y.ToString(CultureInfo.InvariantCulture)).Append(',')
                    .Append(marks[i].Turn.ToString(CultureInfo.InvariantCulture));
            }
            return text.ToString();
        }

        public static MarkError Check(ReadOnlySpan<Mark> marks, in Mark mark) {
            if (!ValidKind(mark.Kind)) { return MarkError.Bad; }
            if (!Sheet.Inside(mark.Left, mark.Top) || MathF.Abs(mark.Angle) > Sheet.MaxTurn) { return MarkError.Outside; }
            for (int i = 0; i < marks.Length; i++) {
                if (string.Equals(marks[i].Kind, mark.Kind, StringComparison.Ordinal)) { return MarkError.Repeat; }
            }
            return marks.Length >= Sheet.MaxMarks ? MarkError.Full : MarkError.Ok;
        }

        public static MarkError Add(string text, in Mark mark, out string next) {
            next = text ?? "";
            Span<Mark> marks = new Mark[Sheet.MaxMarks];
            int count = Parse(text, marks);
            if (count < 0) { count = 0; }
            MarkError error = Check(marks.Slice(0, count), mark);
            if (error != MarkError.Ok) { return error; }
            marks[count++] = mark;
            next = Format(marks.Slice(0, count));
            return MarkError.Ok;
        }
    }

    public static class Scramble {
        private const string LowerRu = "абвгдежзийклмнопрстуфхцчшщъыьэюя";
        private const string UpperRu = "АБВГДЕЖЗИЙКЛМНОПРСТУФХЦЧШЩЪЫЬЭЮЯ";

        public static string Of(string text, ulong seed) {
            if (string.IsNullOrEmpty(text)) { return ""; }
            return string.Create(text.Length, (text, seed), static (span, state) => {
                ulong s = state.seed * 0x9E3779B97F4A7C15UL + 0x632BE59BD9B4E019UL;
                string source = state.text;
                for (int i = 0; i < span.Length; i++) {
                    s ^= s << 13;
                    s ^= s >> 7;
                    s ^= s << 17;
                    span[i] = Swap(source[i], (uint)(s >> 32));
                }
            });
        }

        private static char Swap(char c, uint roll) {
            if (c >= 'а' && c <= 'я') { return LowerRu[(int)(roll % (uint)LowerRu.Length)]; }
            if (c >= 'А' && c <= 'Я') { return UpperRu[(int)(roll % (uint)UpperRu.Length)]; }
            if (c == 'ё') { return LowerRu[(int)(roll % (uint)LowerRu.Length)]; }
            if (c == 'Ё') { return UpperRu[(int)(roll % (uint)UpperRu.Length)]; }
            if (c >= 'a' && c <= 'z') { return (char)('a' + roll % 26u); }
            if (c >= 'A' && c <= 'Z') { return (char)('A' + roll % 26u); }
            if (c >= '0' && c <= '9') { return (char)('0' + roll % 10u); }
            return c;
        }
    }
}
