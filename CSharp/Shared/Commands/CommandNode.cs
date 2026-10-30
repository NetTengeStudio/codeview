using System;
using System.Collections.Generic;

namespace ScpItdMod.Commands {
    public sealed class CommandNode {
        public readonly string Label;
        public readonly string[] Options;
        public readonly CommandNode Rest;
        private readonly Dictionary<string, CommandNode> then;

        public CommandNode(string label, string[] options, Dictionary<string, CommandNode> then, CommandNode rest) {
            Label = label ?? string.Empty;
            Options = options ?? Array.Empty<string>();
            this.then = then;
            Rest = rest;
        }

        public CommandNode Next(string word) {
            if (then != null && then.TryGetValue(word, out CommandNode child)) { return child; }
            return Rest;
        }

        public static CommandNode Leaf(string label, params string[] options) => new CommandNode(label, options, null, null);

        public static CommandNode Branch(string label, string[] options, CommandNode rest, params KeyValuePair<string, CommandNode>[] then) {
            var table = new Dictionary<string, CommandNode>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < then.Length; i++) { table[then[i].Key] = then[i].Value; }
            return new CommandNode(label, options, table, rest);
        }

        public static KeyValuePair<string, CommandNode> Word(string word, CommandNode node) => new KeyValuePair<string, CommandNode>(word, node);

        public static CommandNode Positional(string[][] valid, int index) {
            if (valid == null || index >= valid.Length) { return null; }
            string[] options = valid[index] ?? Array.Empty<string>();
            return new CommandNode(string.Empty, options, null, Positional(valid, index + 1));
        }
    }
}
