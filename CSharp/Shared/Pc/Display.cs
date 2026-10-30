using System;
using ScpItdMod.Net;

namespace ScpItdMod.Pc {
    public enum PcApp : byte { Desktop, Files, Notes, Dossiers, Printers, Cameras, Durak, Chess, Meteo }

    public sealed class Station {
        public const double LeaseSeconds = 5;
        public ulong Owner { get; private set; }
        public PcApp App { get; private set; }
        public uint Room { get; private set; }
        public uint Revision { get; private set; } = 1;
        public ushort Camera { get; private set; }
        private double cameraTime, cameraAt;
        private byte speed;
        private bool cameraLive, cameraPlaying;
        private double activeUntil;

        public bool Use(ulong actor, double now = 0) {
            if (actor == 0 || !double.IsFinite(now)) { return false; }
            if (Expired(now)) { Release(); }
            if (Owner != 0 && Owner != actor) { return false; }
            if (Owner == 0) { Owner = actor; Changed(); }
            activeUntil = now + LeaseSeconds;
            return true;
        }

        public bool Expired(double now) => Owner != 0 && now >= activeUntil;

        public void Release() => Clear(false);
        public void Stop() => Clear(true);
        private void Changed() { if (++Revision == 0) { Revision++; } }
        private void Clear(bool stop) {
            bool keepPublic = !stop && (Room != 0 && (App == PcApp.Durak || App == PcApp.Chess) || App == PcApp.Cameras && Camera != 0);
            PcApp next = keepPublic ? App : PcApp.Desktop;
            uint nextRoom = keepPublic ? Room : 0;
            bool changed = Owner != 0 || App != next || Room != nextRoom || !keepPublic && Camera != 0;
            Owner = 0; App = next; Room = nextRoom; activeUntil = 0;
            if (!keepPublic) { Camera = 0; cameraTime = cameraAt = 0; speed = 0; cameraLive = cameraPlaying = false; }
            if (changed) { Changed(); }
        }

        public bool Present(ulong actor, PcApp app, uint room = 0) {
            if (actor == 0 || Owner != actor || app > PcApp.Meteo || room != 0 && app != PcApp.Chess && app != PcApp.Durak) { return false; }
            if (App != app || Room != room) {
                App = app; Room = room;
                if (app != PcApp.Cameras) { Camera = 0; cameraTime = cameraAt = 0; speed = 0; cameraLive = cameraPlaying = false; }
                Changed();
            }
            return true;
        }
        public bool ViewCamera(ulong actor, ushort camera, double time, byte playbackSpeed, bool live, bool playing, double now) {
            if (Owner != actor || actor == 0 || App != PcApp.Cameras || !double.IsFinite(time) || time < 0 || !double.IsFinite(now) || playbackSpeed >= Rec.Playback.Speeds.Length) { return false; }
            if (Camera != camera) { Changed(); }
            Camera = camera; cameraTime = time; cameraAt = now; speed = playbackSpeed; cameraLive = live; cameraPlaying = playing;
            return true;
        }
        public double CameraTime(double now) => cameraLive ? double.MaxValue : cameraTime + (cameraPlaying ? Math.Max(0, now - cameraAt) * Rec.Playback.Speeds[speed] : 0);
    }

    public enum PublicDisplayMode : byte { Starting, Desktop, Private, Shared, Camera, Off }

    public static class DisplaySource {
        public static bool CanPublish(bool powered, bool hasFrame, bool surfaceLive) => powered && hasFrame && surfaceLive;
        public static PublicDisplayMode Mode(in DisplayState state, bool ready) => !ready ? PublicDisplayMode.Starting : !state.Powered ? PublicDisplayMode.Off :
            state.App == PcApp.Desktop ? PublicDisplayMode.Desktop : state.App == PcApp.Cameras ? PublicDisplayMode.Camera :
            state.App == PcApp.Durak || state.App == PcApp.Chess || state.App == PcApp.Printers ? PublicDisplayMode.Shared : PublicDisplayMode.Private;
        public static bool Repaint(uint revision, uint paintedRevision, uint epoch, uint paintedEpoch, uint cameraVersion, uint paintedCameraVersion) =>
            revision != paintedRevision || epoch != paintedEpoch || cameraVersion != paintedCameraVersion;
    }

    // Periodic snapshots repair an initial display packet received before Item creation.
    public static class DisplayCadence {
        public const double ResyncSeconds = 8.0;
        public static bool Due(double now, double next) => double.IsFinite(now) && now >= next;
        public static double Next(double now) => now + ResyncSeconds;
        public static bool ShouldSend(bool changed, bool newViewer, bool resync) => changed || newViewer || resync;
    }

    public readonly struct DisplayState {
        public readonly ushort Item;
        public readonly uint Epoch, Revision;
        public readonly PcApp App;
        public readonly bool Powered, Busy;
        public DisplayState(ushort item, uint epoch, uint revision, PcApp app, bool powered, bool busy) {
            Item = item; Epoch = epoch; Revision = revision; App = app; Powered = powered; Busy = busy;
        }
    }

    public static class DisplayWire {
        public const string Channel = "scpitd.pc.display";
        public const int MaxReply = 16384;
        public const uint Session = 1;

        public static void Write(WireWriter writer, in DisplayState state, ReadOnlySpan<byte> game) {
            writer.Clear(); writer.U8(1); writer.U16(state.Item); writer.U32(state.Epoch); writer.U32(state.Revision);
            writer.U8((byte)state.App); writer.Bool(state.Powered); writer.Bool(state.Busy); writer.Bytes(game);
        }

        public static bool Read(ReadOnlySpan<byte> data, out DisplayState state, out ReadOnlySpan<byte> game) {
            state = default; game = default;
            if (data.Length > MaxReply) { return false; }
            var reader = new WireReader(data);
            if (reader.U8() != 1) { return false; }
            ushort item = reader.U16(); uint epoch = reader.U32(), revision = reader.U32(); PcApp app = (PcApp)reader.U8();
            bool powered = reader.Bool(), busy = reader.Bool(); game = reader.Bytes(MaxReply - 32);
            if (reader.Failed || !reader.End || item == 0 || epoch == 0 || revision == 0 || app > PcApp.Meteo ||
                !game.IsEmpty && app != PcApp.Printers && app != PcApp.Chess && (app != PcApp.Durak || !powered)) { return false; }
            state = new DisplayState(item, epoch, revision, app, powered, busy);
            return true;
        }
    }

}
