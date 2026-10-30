using System;
using Barotrauma;
using EventInput;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace ScpItdMod.Paper {
    public sealed class Pen : IKeyboardSubscriber {
        private InkEdit edit;

        public Pen(InkEdit edit) => this.edit = edit;

        public InkEdit Target {
            get => edit;
            set => edit = value;
        }

        public Func<char, bool> Command;

        public Func<bool> Suppress;

        public bool Selected { get; set; }

        public string Composing { get; private set; } = "";

        public int Refused { get; private set; }

        public bool Focused => ReferenceEquals(GUI.KeyboardDispatcher.Subscriber, this);

        public void Focus(Rectangle area) {
            if (Focused) { return; }
            GUI.KeyboardDispatcher.Subscriber = this;
            TextInput.SetTextInputRect(area);
            TextInput.StartTextInput();
        }

        public void Blur() {
            Composing = "";
            if (!Focused) { return; }
            GUI.KeyboardDispatcher.Subscriber = null;
            TextInput.StopTextInput();
        }

        public void ReceiveTextInput(char inputChar) {
            if (char.IsControl(inputChar) || edit == null || Suppress?.Invoke() == true) { return; }
            Span<char> one = stackalloc char[1];
            one[0] = inputChar;
            Take(edit.Type(one, Timing.TotalTime));
        }

        public void ReceiveTextInput(string text) {
            if (string.IsNullOrEmpty(text) || edit == null || Suppress?.Invoke() == true) { return; }
            Composing = "";
            Take(edit.Type(text, Timing.TotalTime));
        }

        public void ReceiveCommandInput(char command) {
            if (Composing.Length > 0 || PlayerInput.IsAltDown()) { return; }
            if (Command != null && Command(command)) { return; }
            if (edit == null) { return; }
            bool ctrl = PlayerInput.IsCtrlDown();
            double now = Timing.TotalTime;
            switch (command) {
                case '\b': edit.Back(ctrl, now); break;
                case '\r':
                case '\n': Take(edit.Type("\n", now)); break;
                case '\u0001': edit.SelectAll(); break;
                case '\u0003': Copy(); break;
                case '\u0018':
                    Copy();
                    edit.Back(false, now);
                    break;
                case '\u0016': {
                    string pasted = Clipboard.GetText();
                    if (!string.IsNullOrEmpty(pasted)) { Take(edit.Type(pasted, now)); }
                    break;
                }
                case '\u001a': edit.Undo(); break;
                case '\u0019':
                case '\u0012': edit.Redo(); break;
            }
        }

        public void ReceiveSpecialInput(Keys key) {
            if (Composing.Length > 0 || edit == null) { return; }
            bool shift = PlayerInput.IsShiftDown();
            bool ctrl = PlayerInput.IsCtrlDown();
            switch (key) {
                case Keys.Left: edit.Move(ctrl ? InkMove.WordLeft : InkMove.Left, shift); break;
                case Keys.Right: edit.Move(ctrl ? InkMove.WordRight : InkMove.Right, shift); break;
                case Keys.Up: edit.Move(InkMove.Up, shift); break;
                case Keys.Down: edit.Move(InkMove.Down, shift); break;
                case Keys.Home: edit.Move(ctrl ? InkMove.First : InkMove.Home, shift); break;
                case Keys.End: edit.Move(ctrl ? InkMove.Last : InkMove.End, shift); break;
                case Keys.Delete: edit.Delete(ctrl, Timing.TotalTime); break;
            }
        }

        public void ReceiveEditingInput(string text, int start, int length) => Composing = text ?? "";

        private void Copy() {
            string selected = edit.Selected();
            if (selected.Length > 0) { Clipboard.SetText(selected); }
        }

        private void Take(bool accepted) {
            if (!accepted) { Refused++; }
        }
    }
}
