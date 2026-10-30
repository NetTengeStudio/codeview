using System;
using System.Collections.Generic;

namespace ScpItdMod.Commands {
    public static class CommandUsage {
        private const string Separator = ": ";

        public static string Split(string help, string[] names, out string description) {
            description = help ?? string.Empty;
            if (string.IsNullOrWhiteSpace(help) || names == null) { return string.Empty; }
            int cut = help.IndexOf(Separator, StringComparison.Ordinal);
            if (cut <= 0) { return string.Empty; }
            string head = help.Substring(0, cut);
            int space = head.IndexOf(' ');
            string first = space < 0 ? head : head.Substring(0, space);
            if (!NamesContain(first.Split('|'), names)) { return string.Empty; }
            description = help.Substring(cut + Separator.Length).Trim();
            return head.Trim();
        }

        public static string[] Labels(string usage) {
            if (string.IsNullOrEmpty(usage)) { return Array.Empty<string>(); }
            var labels = new List<string>();
            int depth = 0, start = -1;
            for (int i = 0; i < usage.Length; i++) {
                char c = usage[i];
                if (c == '[' || c == '<') {
                    if (depth++ == 0) { start = i + 1; }
                } else if ((c == ']' || c == '>') && depth > 0 && --depth == 0 && i > start) {
                    labels.Add(usage.Substring(start, i - start).Trim());
                }
            }
            return labels.ToArray();
        }

        public static CommandNode Build(string[][] valid, string[] labels) {
            int count = Math.Max(valid?.Length ?? 0, labels.Length);
            CommandNode node = null;
            for (int i = count - 1; i >= 0; i--) {
                string[] options = valid != null && i < valid.Length && valid[i] != null ? valid[i] : Array.Empty<string>();
                string label = i < labels.Length ? labels.Length > 1 ? $"{labels[i]} ({i + 1}/{labels.Length})" : labels[i] : string.Empty;
                node = new CommandNode(label, options, null, node);
            }
            return node;
        }

        private static bool NamesContain(string[] spelled, string[] names) {
            for (int i = 0; i < spelled.Length; i++) {
                for (int j = 0; j < names.Length; j++) {
                    if (string.Equals(spelled[i], names[j], StringComparison.OrdinalIgnoreCase)) { return true; }
                }
            }
            return false;
        }
    }
}
