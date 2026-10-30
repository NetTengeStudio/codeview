using System;

namespace ScpItdMod.Paper {
    // Owns one paper editor through visibility changes and asynchronous server replies.
    public sealed class PaperDraft {
        public PaperDraft(InkEdit edit) { Edit = edit ?? throw new ArgumentNullException(nameof(edit)); SavedVersion = edit.Version; }
        public InkEdit Edit { get; }
        public uint Revision { get; private set; } = PaperRules.Unknown;
        public int SavedVersion { get; private set; }
        public string Marks { get; private set; } = "";
        public bool Dirty => Edit.Version != SavedVersion;
        public bool Pending { get; private set; }
        public bool Conflict { get; private set; }
        private string savedText = "", sentText, remoteText = "";
        private int sentVersion;
        private static uint sequence;
        public uint Request { get; private set; }
        private uint remoteRevision;

        public bool Receive(string body, string marks, uint revision, uint request = 0) {
            if (request != 0 && request != Request || Revision != PaperRules.Unknown && revision < Revision || Conflict && revision < remoteRevision) { return false; }
            body ??= ""; Marks = marks ?? "";
            if (Pending && string.Equals(body, sentText, StringComparison.Ordinal)) {
                Complete(PaperError.Ok, revision, Request);
            }
            if (!Dirty) {
                if (!string.Equals(Edit.Text, body, StringComparison.Ordinal)) { Edit.Load(body); }
                SavedVersion = Edit.Version; savedText = body;
                Revision = revision; Conflict = false;
            } else if (string.Equals(body, savedText, StringComparison.Ordinal)) {
                Revision = revision; Conflict = false;
                if (request == 0) { Pending = false; }
            } else {
                remoteText = body; remoteRevision = revision; Conflict = true; Pending = false;
            }
            return true;
        }
        public bool BeginSave(out string body, out uint revision) {
            body = null; revision = Revision;
            if (!Dirty || Pending || Conflict || Revision == PaperRules.Unknown) { return false; }
            if (++sequence == 0) { sequence++; }
            Request = sequence;
            sentText = body = Edit.Text; sentVersion = Edit.Version; Pending = true;
            return true;
        }
        public bool Complete(PaperError result, uint revision, uint request) {
            if (!Pending || request != Request) { return false; }
            Pending = false;
            if (result == PaperError.Ok) { Revision = Revision == PaperRules.Unknown ? revision : Math.Max(Revision, revision); SavedVersion = sentVersion; savedText = sentText; }
            sentText = null;
            return result == PaperError.Ok;
        }
        public void Resolve(bool keepLocal) {
            if (!Conflict) { return; }
            Revision = remoteRevision; Conflict = false;
            savedText = remoteText;
            if (!keepLocal) { Edit.Load(remoteText); SavedVersion = Edit.Version; }
        }
    }
}
