using System;
using System.Collections.Generic;
using System.Reflection;
using Barotrauma;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using ScpItdMod.Commands;
using ScpItdMod.Core;
using ScpItdMod.Motion;
using ScpItdMod.Ui;

namespace ScpItdMod.Hints {
    public static class ConsoleHints {
        private const string HintFeature = "console hints";
        private const string CompletionFeature = "console completion";
        private const int Rows = 8, HeaderLimit = 120;
        private const double Linger = 0.25;
        private static readonly MotionTextCard card = new MotionTextCard(HintFeature, new SurfaceBudget(2L << 20), new MotionShutter(1d / 240, 4));
        private static readonly FieldInfo textBoxField = AccessTools.Field(typeof(DebugConsole), "textBox");
        private static readonly string[] noLines = Array.Empty<string>();
        private static string anchor, applied, shownText;
        private static Suggestion[] cycle = Array.Empty<Suggestion>();
        private static int index, shownIndex = -2;
        private static string[] shownLines = noLines;

        public static void Attach(Harmony harmony) {
            harmony.Patch(AccessTools.Method(typeof(DebugConsole), nameof(DebugConsole.AutoComplete), new[] { typeof(string), typeof(int) }),
                prefix: ModuleHost.Bind(typeof(ConsoleHints), nameof(AutoCompletePrefix)));
            harmony.Patch(AccessTools.Method(typeof(DebugConsole), nameof(DebugConsole.ResetAutoComplete)),
                postfix: ModuleHost.Bind(typeof(ConsoleHints), nameof(ResetPostfix)));
            harmony.Patch(AccessTools.Method(typeof(GUI), nameof(GUI.Draw), new[] { typeof(Camera), typeof(SpriteBatch) }),
                postfix: ModuleHost.Bind(typeof(ConsoleHints), nameof(DrawPostfix)));
        }

        public static bool AutoCompletePrefix(string command, int increment, ref string __result) {
            if (Contained.IsDisabled(CompletionFeature)) { return true; }
            try {
                __result = Complete(command ?? string.Empty);
                return false;
            }
            catch (Exception error) {
                Contained.Fail(CompletionFeature, error);
                return true;
            }
        }

        public static void ResetPostfix() {
            applied = null;
            anchor = null;
            cycle = Array.Empty<Suggestion>();
            index = 0;
        }

        public static void DrawPostfix(SpriteBatch spriteBatch) {
            if (Contained.IsDisabled(HintFeature)) { return; }
            try { DrawHints(spriteBatch); }
            catch (Exception error) { Contained.Fail(HintFeature, error); }
        }

        public static void Reset() {
            ResetPostfix();
            shownText = null;
            shownIndex = -2;
            shownLines = noLines;
            card.Reset();
        }

        private static string Complete(string command) {
            IReadOnlyList<CompletionEntry> entries = Entries();
            if (anchor == null || !string.Equals(command, applied, StringComparison.Ordinal)) {
                anchor = command;
                cycle = CommandCompletion.Suggest(anchor, entries);
                index = 0;
            } else if (cycle.Length > 0) {
                int step = PlayerInput.KeyDown(Keys.LeftShift) ? -1 : 1;
                index = MathUtils.PositiveModulo(index + step, cycle.Length);
            }
            if (cycle.Length == 0) {
                applied = command;
                return command;
            }
            string result = CommandCompletion.CompletesArgument(anchor)
                ? CommandCompletion.ApplyArgument(anchor, cycle[index].Value)
                : cycle[index].Value;
            applied = result;
            return result;
        }

        private static void DrawHints(SpriteBatch spriteBatch) {
            GUITextBox box = TextBox();
            if (!DebugConsole.IsOpen || box == null || string.IsNullOrEmpty(box.Text)) {
                card.Hide();
                shownText = null;
                card.Update();
                card.Draw(spriteBatch);
                return;
            }
            string text = box.Text;
            bool cycling = anchor != null && cycle.Length > 0 && string.Equals(text, applied, StringComparison.Ordinal);
            int highlight = cycling ? index : -1;
            if (!string.Equals(text, shownText, StringComparison.Ordinal) || highlight != shownIndex) {
                shownText = text;
                shownIndex = highlight;
                shownLines = BuildLines(text, cycling ? cycle : null, highlight);
            }
            if (shownLines.Length == 0) {
                card.Hide();
            } else {
                card.Show(shownLines, CardPlacement.AboveRect, box.Rect);
                card.Hold(Linger);
            }
            card.Update();
            card.Draw(spriteBatch);
        }

        private static string[] BuildLines(string text, Suggestion[] list, int highlight) {
            IReadOnlyList<CompletionEntry> entries = Entries();
            Suggestion[] values = list ?? CommandCompletion.Suggest(text, entries);
            string header = CommandCompletion.CompletesArgument(text) ? CommandCompletion.Header(text, entries) : null;
            string description = header != null ? CommandCompletion.Description(text, entries) : null;
            string label = CommandCompletion.Label(text, entries);
            int headerRows = (header != null ? 1 : 0) + (description != null ? 1 : 0) + (label != null ? 1 : 0);
            if (values.Length == 0 && headerRows == 0) { return noLines; }
            int count = Math.Min(values.Length, Rows);
            var lines = new string[headerRows + count];
            int at = 0;
            if (header != null) { lines[at++] = "  " + Clip(header, HeaderLimit); }
            if (description != null) { lines[at++] = "  " + Clip(description, HeaderLimit); }
            if (label != null) { lines[at++] = "  > " + label; }
            if (count == 0) { return lines; }
            int first = highlight < 0 ? 0 : Math.Clamp(highlight - count / 2, 0, values.Length - count);
            for (int i = 0; i < count; i++) {
                Suggestion item = values[first + i];
                string marker = first + i == highlight ? "> " : "  ";
                lines[at++] = item.Detail.Length > 0 ? marker + item.Value + "   " + item.Detail : marker + item.Value;
            }
            return lines;
        }

        private static string Clip(string text, int limit) => text.Length > limit ? text.Substring(0, limit) + "..." : text;

        private static IReadOnlyList<CompletionEntry> Entries() {
            List<DebugConsole.Command> commands = DebugConsole.Commands;
            var entries = new List<CompletionEntry>(commands.Count);
            for (int i = 0; i < commands.Count; i++) {
                DebugConsole.Command command = commands[i];
                var names = new string[command.Names.Length];
                for (int j = 0; j < names.Length; j++) { names[j] = command.Names[j].Value; }
                if (CommandCatalog.TryGet(names[0], out CommandSpec spec)) {
                    entries.Add(new CompletionEntry(names, command.Help, spec.Usage, spec.Root));
                } else {
                    string usage = CommandUsage.Split(command.Help, names, out string description);
                    entries.Add(new CompletionEntry(names, description, usage, CommandUsage.Build(ValidArgs(command), CommandUsage.Labels(usage))));
                }
            }
            return entries;
        }

        private static string[][] ValidArgs(DebugConsole.Command command) {
            if (command.GetValidArgs == null) { return null; }
            try { return command.GetValidArgs(); }
            catch (Exception) { return null; }
        }

        private static GUITextBox TextBox() => textBoxField?.GetValue(null) as GUITextBox;
    }
}
