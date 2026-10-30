using System;
using System.Collections.Generic;
using ScpItdMod.People;
using ScpItdMod.Rec;
using ScpItdMod.Ui;
using ScpItdMod.Xp;

namespace ScpItdMod.Pc {
    public sealed class CamFeed : ICamFeed {
        private readonly ushort terminal;

        public CamFeed(ushort terminal) => this.terminal = terminal;

        public IReadOnlyList<CamInfo> Cameras => CamClient.Cameras;

        public CamInfo Selected => CamClient.Selected;

        public Playback View => CamClient.Archive.View;

        public bool Listed => CamClient.Listed;

        public CamDeny Deny => CamClient.Deny;

        public bool InGap => CamClient.InGap;
        public bool VoiceAllowed => CamClient.RecordVoice;
        public void AllowVoice(bool allowed) => CamClient.AllowVoice(allowed);

        public double ServerNow => CamClient.ServerNow;

        public void Begin() => CamClient.Begin(terminal);

        public void End() { CamClient.Close(); ScreenCameras.Expect(terminal, 0); }

        public void Select(ushort id) {
            CamClient.Select(id);
            ScreenCameras.Expect(terminal, id);
        }

        public void Control(ushort id, bool record) => CamClient.Control(id, record);

        public void Picture(ICanvas c, in Box viewport, CamInfo cam) {
            ScreenCameras.Picture(c, viewport, terminal);
            XpFaces faces = PcScreen.Faces();
            if (faces != null) { Paint.Text(c, faces.Header, ScreenCameras.Status(terminal), viewport, XpLook.White, Align.Center); }
        }

        public string Caption(double position) {
            Segment s = CamClient.SegmentAt(position);
            if (s == null) { return null; }
            TapeNote best = default;
            bool found = false;
            for (int i = 0; i < s.Notes.Count; i++) {
                TapeNote n = s.Notes[i];
                if (n.Kind != TapeEvent.Speech || n.Time > position || n.Time < position - 4.0) { continue; }
                best = n;
                found = true;
            }
            if (!found) { return null; }
            return Who(best.Subject) + ": " + best.Text;
        }

        private static string Who(Subject subject) {
            IReadOnlyList<FigurePose> frame = CamClient.Frame;
            for (int i = 0; i < frame.Count; i++) {
                if (!frame[i].Subject.Equals(subject)) { continue; }
                LookDesc d = CamClient.Archive.Looks.Get(frame[i].Look);
                if (d == null) { break; }
                return d.Masked ? Identity.MaskedLabel : d.Label;
            }
            return Identity.UnseenLabel;
        }
    }
}
