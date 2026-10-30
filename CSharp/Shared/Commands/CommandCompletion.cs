using System;
using System.Collections.Generic;
using System.Text;

namespace ScpItdMod.Commands {
    public sealed class CompletionEntry {
        public readonly string[] Names;
        public readonly string Help;
        public readonly string Usage;
        public readonly CommandNode Root;

        public CompletionEntry(string[] names, string help, string usage, CommandNode root) {
            Names = names ?? Array.Empty<string>();
            Help = help ?? string.Empty;
            Usage = usage ?? string.Empty;
            Root = root;
        }
    }

    public readonly struct Suggestion {
        public readonly string Value;
        public readonly string Detail;

        public Suggestion(string value, string detail) {
            Value = value;
            Detail = detail ?? string.Empty;
        }
    }

    public static class CommandCompletion {
        public const int MaxDetail = 90;

        public static bool CompletesArgument(string command) {
            string text = command ?? string.Empty;
            List<string> words = Words(text);
            return words.Count > 1 || (words.Count == 1 && EndsWithSpace(text));
        }

        public static Suggestion[] Suggest(string command, IReadOnlyList<CompletionEntry> entries) {
            string text = command ?? string.Empty;
            if (entries == null) { return Array.Empty<Suggestion>(); }
            List<string> words = Words(text);
            bool trailingSpace = EndsWithSpace(text);
            if (words.Count == 0) { return NamesStartingWith(string.Empty, entries); }
            if (words.Count == 1 && !trailingSpace) { return NamesStartingWith(text, entries); }
            CompletionEntry entry = Find(entries, words[0]);
            if (entry == null) { return Array.Empty<Suggestion>(); }
            string partial = trailingSpace ? string.Empty : words[words.Count - 1];
            int argIndex = trailingSpace ? words.Count - 1 : words.Count - 2;
            CommandNode node = Walk(entry.Root, words, argIndex);
            return node == null ? Array.Empty<Suggestion>() : OptionsStartingWith(node.Options, partial.Trim());
        }

        public static string Header(string command, IReadOnlyList<CompletionEntry> entries) {
            List<string> words = Words(command ?? string.Empty);
            if (words.Count == 0 || entries == null) { return null; }
            CompletionEntry entry = Find(entries, words[0]);
            return entry == null || entry.Usage.Length == 0 ? null : entry.Usage;
        }

        public static string Description(string command, IReadOnlyList<CompletionEntry> entries) {
            List<string> words = Words(command ?? string.Empty);
            if (words.Count == 0 || entries == null) { return null; }
            CompletionEntry entry = Find(entries, words[0]);
            return entry == null || entry.Help.Length == 0 ? null : entry.Help;
        }

        public static string Label(string command, IReadOnlyList<CompletionEntry> entries) {
            string text = command ?? string.Empty;
            List<string> words = Words(text);
            if (words.Count == 0 || entries == null || !CompletesArgument(text)) { return null; }
            CompletionEntry entry = Find(entries, words[0]);
            if (entry == null) { return null; }
            int argIndex = EndsWithSpace(text) ? words.Count - 1 : words.Count - 2;
            CommandNode node = Walk(entry.Root, words, argIndex);
            return node == null || node.Label.Length == 0 ? null : node.Label;
        }

        public static string ApplyArgument(string command, string value) {
            string text = command ?? string.Empty;
            string quoted = value.IndexOf(' ') >= 0 ? "\"" + value + "\"" : value;
            if (text.Length == 0 || EndsWithSpace(text)) { return text + quoted; }
            int cut = text.Length;
            while (cut > 0 && text[cut - 1] != ' ') { cut--; }
            return text.Substring(0, cut) + quoted;
        }

        private static bool EndsWithSpace(string text) => text.Length > 0 && text[text.Length - 1] == ' ';

        private static CommandNode Walk(CommandNode root, List<string> words, int count) {
            CommandNode node = root;
            for (int i = 0; i < count && node != null; i++) { node = node.Next(words[i + 1]); }
            return node;
        }

        private static CompletionEntry Find(IReadOnlyList<CompletionEntry> entries, string head) {
            for (int i = 0; i < entries.Count; i++) {
                string[] names = entries[i].Names;
                for (int j = 0; j < names.Length; j++) {
                    if (string.Equals(names[j], head, StringComparison.OrdinalIgnoreCase)) { return entries[i]; }
                }
            }
            return null;
        }

        private static Suggestion[] NamesStartingWith(string prefix, IReadOnlyList<CompletionEntry> entries) {
            var result = new List<Suggestion>();
            for (int i = 0; i < entries.Count; i++) {
                CompletionEntry entry = entries[i];
                string detail = entry.Usage.Length > 0 ? entry.Usage : entry.Help;
                for (int j = 0; j < entry.Names.Length; j++) {
                    string name = entry.Names[j];
                    if (name == null || name.Length < prefix.Length) { continue; }
                    if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { result.Add(new Suggestion(name, Shorten(detail))); }
                }
            }
            return result.ToArray();
        }

        private static Suggestion[] OptionsStartingWith(string[] options, string key) {
            var result = new List<Suggestion>(options.Length);
            for (int i = 0; i < options.Length; i++) {
                string value = options[i];
                if (value != null && value.StartsWith(key, StringComparison.OrdinalIgnoreCase)) { result.Add(new Suggestion(value, string.Empty)); }
            }
            for (int i = 0; i < options.Length; i++) {
                string value = options[i];
                if (value == null || value.IndexOf(key, StringComparison.OrdinalIgnoreCase) < 0 || Contains(result, value)) { continue; }
                result.Add(new Suggestion(value, string.Empty));
            }
            return result.ToArray();
        }

        private static string Shorten(string text) => text.Length > MaxDetail ? text.Substring(0, MaxDetail) + "..." : text;

        private static bool Contains(List<Suggestion> list, string value) {
            for (int i = 0; i < list.Count; i++) {
                if (string.Equals(list[i].Value, value, StringComparison.OrdinalIgnoreCase)) { return true; }
            }
            return false;
        }

        private static List<string> Words(string text) {
            var words = new List<string>();
            var current = new StringBuilder();
            bool quoted = false, inWord = false;
            for (int i = 0; i < text.Length; i++) {
                char c = text[i];
                if (c == '"') {
                    quoted = !quoted;
                    inWord = true;
                    continue;
                }
                if (c == ' ' && !quoted) {
                    if (inWord) {
                        words.Add(current.ToString());
                        current.Clear();
                        inWord = false;
                    }
                    continue;
                }
                current.Append(c);
                inWord = true;
            }
            if (inWord) { words.Add(current.ToString()); }
            return words;
        }
    }
}
