using System;
using Vector2 = ScpItdMod.Core.Vec2;
using ScpItdMod.Net;

namespace ScpItdMod.Pool {
    public enum PoolOp : byte { Poll, Join, Leave, EightBall, Free, Strike, Place, Choose, Stalemate, Aim }
    public enum PoolError : byte { Ok, Invalid, Stale, Reach, Busy }
    public struct PoolCommand {
        public ushort Table;
        public uint Epoch, Revision, Token;
        public PoolOp Op;
        public Vector2 Aim, Tip;
        public float Speed;
        public int Ball, Pocket;
        public bool Safety;
        public PoolAction Action;
    }
    public struct PoolMeta {
        public ushort Table;
        public uint Epoch, Revision, Tick, Ack, Wins0, Wins1;
        public PoolError Error;
        public PoolMode Mode;
        public PoolPhase Phase;
        public PoolChoice Choice;
        public PoolFoul Foul;
        public int Me, Turn, Winner, Breaker, Ball, Pocket, Stalemate;
        public byte Group0, Group1, Online, Occupied;
        public bool Break, InHand, Kitchen, Safety, Fault, Full, Moving;
        public string Name0, Name1;
    }
    public struct CueState {
        public ushort Table;
        public uint Epoch, Revision, Sequence;
        public Vector2 Direction, Tip;
        public float Power;
    }
    public static class PoolWire {
        public const string Channel = "scpitd.pool";
        public const int MaxRequest = 64, MaxReply = 1024;
        public static void Request(WireWriter w, in PoolCommand c) {
            w.Clear(); w.U8(1); w.U16(c.Table); w.U32(c.Epoch); w.U32(c.Revision); w.U32(c.Token); w.U8((byte)c.Op);
            w.F32(c.Aim.X); w.F32(c.Aim.Y); w.F32(c.Tip.X); w.F32(c.Tip.Y); w.F32(c.Speed); w.U8((byte)(c.Ball + 1)); w.U8((byte)(c.Pocket + 1)); w.Bool(c.Safety); w.U8((byte)c.Action);
        }
        public static bool ReadRequest(ReadOnlySpan<byte> bytes, out PoolCommand c) {
            c = default; if (bytes.Length > MaxRequest) { return false; } var r = new WireReader(bytes); if (r.U8() != 1) { return false; }
            c.Table = r.U16(); c.Epoch = r.U32(); c.Revision = r.U32(); c.Token = r.U32(); c.Op = (PoolOp)r.U8();
            c.Aim = new Vector2(r.F32(), r.F32()); c.Tip = new Vector2(r.F32(), r.F32()); c.Speed = r.F32(); c.Ball = r.U8() - 1; c.Pocket = r.U8() - 1; c.Safety = r.Bool(); c.Action = (PoolAction)r.U8();
            return !r.Failed && r.End && c.Table != 0 && c.Op <= PoolOp.Aim && (c.Op == PoolOp.Poll || c.Op == PoolOp.Aim || c.Token != 0) &&
                Finite(c.Aim) && Finite(c.Tip) && float.IsFinite(c.Speed) && c.Speed >= 0 && c.Speed <= Physics.MaxSpeed && c.Tip.LengthSquared() <= 0.64001f && c.Ball < 16 && c.Pocket < 6 && c.Action <= PoolAction.BreakOther;
        }
        private static bool Finite(Vector2 v) => float.IsFinite(v.X) && float.IsFinite(v.Y);

        public static bool CanAim(Match match, ulong actor, in PoolCommand command, uint epoch) => command.Op == PoolOp.Aim && command.Epoch == epoch &&
            command.Revision == match.Revision && match.Seat(actor) >= 0 && (match.Mode == PoolMode.Free || match.Seat(actor) == match.Turn) &&
            !match.Physics.Moving && match.Phase is PoolPhase.Aiming or PoolPhase.Lag && !match.Physics.Balls[0].Down &&
            Finite(command.Aim) && MathF.Abs(command.Aim.LengthSquared() - 1) < 0.001f && Finite(command.Tip) && command.Tip.LengthSquared() <= 0.64001f &&
            float.IsFinite(command.Speed) && command.Speed >= 0.25f && command.Speed <= 10;

        public static void AimSnapshot(WireWriter w, in CueState cue) {
            w.Clear(); w.U8(2); w.U16(cue.Table); w.U32(cue.Epoch); w.U32(cue.Revision); w.U32(cue.Sequence);
            w.U16(Quantize(MathF.Atan2(cue.Direction.Y, cue.Direction.X), -MathF.PI, MathF.PI));
            w.U8((byte)Math.Clamp((int)MathF.Round(cue.Power * 255), 0, 255));
            w.U8((byte)Math.Clamp((int)MathF.Round((cue.Tip.X + 0.8f) * 159.375f), 0, 255));
            w.U8((byte)Math.Clamp((int)MathF.Round((cue.Tip.Y + 0.8f) * 159.375f), 0, 255));
        }
        public static bool ReadAim(ReadOnlySpan<byte> data, out CueState cue) {
            cue = default; if (data.Length != 20) { return false; }
            var reader = new WireReader(data); if (reader.U8() != 2) { return false; }
            cue.Table = reader.U16(); cue.Epoch = reader.U32(); cue.Revision = reader.U32(); cue.Sequence = reader.U32();
            float angle = Expand(reader.U16(), -MathF.PI, MathF.PI); cue.Direction = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
            cue.Power = reader.U8() / 255f; cue.Tip = new Vector2(reader.U8() / 159.375f - 0.8f, reader.U8() / 159.375f - 0.8f);
            return !reader.Failed && reader.End && cue.Table != 0 && cue.Epoch != 0 && cue.Revision != 0;
        }

        public static PoolError Apply(Match match, ulong actor, in PoolCommand c, uint epoch, uint random) {
            if (c.Op == PoolOp.Poll) { return PoolError.Ok; }
            if (c.Epoch != epoch || c.Revision != match.Revision) { return PoolError.Stale; }
            bool ok = c.Op switch {
                PoolOp.Join => match.Join(actor),
                PoolOp.Leave => Leave(match, actor),
                PoolOp.EightBall => match.Start(actor, PoolMode.EightBall, random),
                PoolOp.Free => match.Start(actor, PoolMode.Free, random),
                PoolOp.Strike => match.Strike(actor, c.Aim, c.Speed, c.Tip, c.Ball, c.Pocket, c.Safety),
                PoolOp.Place => match.Place(actor, c.Aim),
                PoolOp.Choose => match.Choose(actor, c.Action),
                PoolOp.Stalemate => match.Stalemate(actor),
                _ => false
            };
            return ok ? PoolError.Ok : match.Physics.Moving ? PoolError.Busy : PoolError.Invalid;
        }
        private static bool Leave(Match match, ulong actor) { if (match.Seat(actor) < 0) { return false; } match.Leave(actor); return true; }

        public static void Snapshot(WireWriter w, ushort id, uint epoch, Match match, int me, byte online, string name0, string name1, ushort mask, bool full, uint ack = 0, PoolError error = PoolError.Ok) {
            w.Clear(); w.U8(4); w.U16(id); w.U32(epoch); w.U32(match.Revision); w.U32(match.Physics.Tick); w.U32(ack); w.U8((byte)error); w.Bool(full); w.U16(full ? ushort.MaxValue : mask);
            w.U8((byte)(me + 1)); w.U8((byte)match.Mode); w.U8((byte)match.Phase); w.U8((byte)match.Choice); w.U8((byte)match.Foul); w.U8((byte)match.Turn); w.U8((byte)(match.Winner + 1)); w.U8((byte)match.Breaker);
            w.U8((byte)((match.Break ? 1 : 0) | (match.InHand ? 2 : 0) | (match.Kitchen ? 4 : 0) | (match.Safety ? 8 : 0) | (match.Physics.Fault ? 16 : 0) | (match.Physics.Moving ? 32 : 0)));
            w.U8((byte)(match.CalledBall + 1)); w.U8((byte)(match.CalledPocket + 1)); w.U8(match.Groups[0]); w.U8(match.Groups[1]); w.U8(online); w.U8((byte)((match.Players[0] != 0 ? 1 : 0) | (match.Players[1] != 0 ? 2 : 0))); w.U8((byte)(match.StalemateBy + 1)); w.U32(match.Wins(0)); w.U32(match.Wins(1));
            if (full) { w.Text(name0 ?? ""); w.Text(name1 ?? ""); }
            Table table = match.Physics.Table;
            for (int i = 0; i < 16; i++) {
                if (!full && (mask & 1 << i) == 0) { continue; }
                Ball b = match.Physics.Balls[i]; float margin = table.Radius * 4;
                w.U16(Quantize(b.Position.X, -margin, table.Length + margin)); w.U16(Quantize(b.Position.Y, -margin, table.Width + margin));
                w.U16(Quantize(b.Velocity.X, -32, 32)); w.U16(Quantize(b.Velocity.Y, -32, 32));
                float spinLimit = 3 * Physics.MaxSpeed / table.Radius;
                w.U16(Quantize(b.Spin.X, -spinLimit, spinLimit)); w.U16(Quantize(b.Spin.Y, -spinLimit, spinLimit)); w.U16(Quantize(b.Spin.Z, -spinLimit, spinLimit));
                ScpItdMod.Motion.Rotation3 rotation = BallRotation.Unit(b.Orientation);
                w.U16(Quantize(rotation.X, -1, 1)); w.U16(Quantize(rotation.Y, -1, 1)); w.U16(Quantize(rotation.Z, -1, 1)); w.U16(Quantize(rotation.W, -1, 1)); w.U8(b.Pocket);
            }
        }
        public static bool ReadSnapshot(ReadOnlySpan<byte> data, Table table, Span<Ball> balls, out PoolMeta m, out ushort mask) {
            m = default; mask = 0; if (data.Length > MaxReply || balls.Length < 16) { return false; } var r = new WireReader(data);
            byte version = r.U8(); if (version != 1 && version != 3 && version != 4) { return false; }
            m.Table = r.U16(); m.Epoch = r.U32(); m.Revision = r.U32(); m.Tick = r.U32(); m.Ack = r.U32(); m.Error = (PoolError)r.U8(); m.Full = r.Bool(); mask = r.U16();
            m.Me = r.U8() - 1; m.Mode = (PoolMode)r.U8(); m.Phase = (PoolPhase)r.U8(); m.Choice = (PoolChoice)r.U8(); m.Foul = (PoolFoul)r.U8(); m.Turn = r.U8(); m.Winner = r.U8() - 1; m.Breaker = r.U8();
            byte flags = r.U8(); m.Break = (flags & 1) != 0; m.InHand = (flags & 2) != 0; m.Kitchen = (flags & 4) != 0; m.Safety = (flags & 8) != 0; m.Fault = (flags & 16) != 0; m.Moving = (flags & 32) != 0;
            m.Ball = r.U8() - 1; m.Pocket = r.U8() - 1; m.Group0 = r.U8(); m.Group1 = r.U8(); m.Online = r.U8(); m.Occupied = r.U8(); m.Stalemate = r.U8() - 1;
            if (version == 4) { m.Wins0 = r.U32(); m.Wins1 = r.U32(); }
            if (m.Table == 0 || m.Epoch == 0 || m.Revision == 0 || m.Me < -1 || m.Me > 1 || m.Mode > PoolMode.Free || m.Phase > PoolPhase.Finished || m.Choice > PoolChoice.FoulOnBreak || m.Foul > PoolFoul.Break || m.Turn > 1 || m.Winner > 1 || m.Breaker > 1 || flags > 63 || m.Ball > 15 || m.Pocket > 5 || m.Group0 > 2 || m.Group1 > 2 || m.Online > 3 || m.Occupied > 3 || m.Stalemate > 1 || m.Error > PoolError.Busy || m.Full && mask != ushort.MaxValue) { return false; }
            if (m.Full) { m.Name0 = r.Text(192); m.Name1 = r.Text(192); }
            float margin = table.Radius * 4;
            for (int i = 0; i < 16; i++) {
                if ((mask & 1 << i) == 0) { continue; }
                Ball ball = new Ball { Position = new Vector2(Expand(r.U16(), -margin, table.Length + margin), Expand(r.U16(), -margin, table.Width + margin)), Velocity = new Vector2(Expand(r.U16(), -32, 32), Expand(r.U16(), -32, 32)) };
                if (version >= 3) {
                    float spinLimit = 3 * Physics.MaxSpeed / table.Radius;
                    ball.Spin = new AngularVelocity(Expand(r.U16(), -spinLimit, spinLimit), Expand(r.U16(), -spinLimit, spinLimit), Expand(r.U16(), -spinLimit, spinLimit));
                    var rotation = new ScpItdMod.Motion.Rotation3(Expand(r.U16(), -1, 1), Expand(r.U16(), -1, 1), Expand(r.U16(), -1, 1), Expand(r.U16(), -1, 1));
                    if (MathF.Abs(rotation.LengthSquared() - 1) > 0.001f) { return false; }
                    ball.Orientation = ScpItdMod.Motion.Rotation3.Normalize(rotation);
                } else { ball.Orientation = ScpItdMod.Motion.Rotation3.CreateFromAxisAngle(ScpItdMod.Motion.Vec3.UnitZ, Expand(r.U16(), -MathF.PI, MathF.PI)); }
                ball.Pocket = r.U8(); balls[i] = ball;
                if (balls[i].Pocket > 7) { return false; }
                if (balls[i].Down) { balls[i].Velocity = default; }
            }
            return !r.Failed && r.End;
        }
        private static ushort Quantize(float v, float low, float high) => (ushort)Math.Clamp((int)MathF.Round((v - low) * (65535 / (high - low))), 0, 65535);
        private static float Expand(ushort v, float low, float high) => low + v * ((high - low) / 65535);
        public static string Explain(PoolError error) => error switch { PoolError.Stale => "Состояние стола изменилось", PoolError.Reach => "Подойдите к столу", PoolError.Busy => "Шары ещё движутся", PoolError.Invalid => "Действие недоступно", _ => "" };
    }

}
