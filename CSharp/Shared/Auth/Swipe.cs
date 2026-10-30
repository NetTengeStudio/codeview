using System;
using System.Collections.Generic;
using ScpItdMod.Net;

namespace ScpItdMod.Auth {
    public enum SwipeDeny : byte { None, NoCard, NotOwner, TooFar, NoPower, Busy, Bound, Unconscious, Hidden, HandsFull, NoReader, Refused, Moved }

    public struct SwipeFacts {
        public bool ReaderExists;
        public bool ReaderPowered;
        public bool HasCard;
        public bool CardOwned;
        public bool ActorConscious;
        public bool ActorBound;
        public bool ActorHidden;
        public bool HandFree;
        public bool Busy;
        public float Distance;
        public float Reach;
    }

    public static class SwipeRules {
        public const float Slack = 1.2f;
        public const double Cooldown = 0.6;

        public static SwipeDeny Check(in SwipeFacts f) {
            if (!f.ReaderExists) { return SwipeDeny.NoReader; }
            if (!f.ActorConscious) { return SwipeDeny.Unconscious; }
            if (f.ActorBound) { return SwipeDeny.Bound; }
            if (f.ActorHidden) { return SwipeDeny.Hidden; }
            if (f.Busy) { return SwipeDeny.Busy; }
            if (!f.HasCard) { return SwipeDeny.NoCard; }
            if (!f.CardOwned) { return SwipeDeny.NotOwner; }
            if (!f.HandFree) { return SwipeDeny.HandsFull; }
            if (f.Distance > f.Reach) { return SwipeDeny.TooFar; }
            return SwipeDeny.None;
        }

        public static SwipeDeny Interrupted(in SwipeFacts f) {
            var relaxed = f;
            relaxed.Busy = false;
            relaxed.HandFree = true;
            relaxed.Reach = f.Reach * Slack;
            SwipeDeny d = Check(relaxed);
            return d == SwipeDeny.TooFar ? SwipeDeny.Moved : d;
        }

        public static string Explain(SwipeDeny d) => d switch {
            SwipeDeny.NoCard => "Нет карты доступа: наденьте её или возьмите в руку",
            SwipeDeny.NotOwner => "Карта не у вас",
            SwipeDeny.TooFar => "Подойдите к считывателю ближе",
            SwipeDeny.NoPower => "Считыватель без питания",
            SwipeDeny.Busy => "Вы уже прикладываете карту",
            SwipeDeny.Bound => "Руки связаны",
            SwipeDeny.Unconscious => "Вы не в состоянии",
            SwipeDeny.Hidden => "Сначала выйдите из укрытия",
            SwipeDeny.HandsFull => "Освободите руку",
            SwipeDeny.NoReader => "Считыватель пропал",
            SwipeDeny.Refused => "Доступ запрещён",
            SwipeDeny.Moved => "Вы отошли от считывателя",
            _ => ""
        };
    }

    public static class CardCredentials {
        public const string NamePrefix = "name:";
        public const string JobPrefix = "jobid:";

        public static void Fill(Credentials c, IEnumerable<string> cardTags, IEnumerable<string> grants) {
            c.Clear();
            if (cardTags != null) {
                foreach (string tag in cardTags) { Take(c, tag); }
            }
            if (grants != null) {
                foreach (string tag in grants) { Take(c, tag); }
            }
        }

        private static void Take(Credentials c, string tag) {
            if (string.IsNullOrEmpty(tag) || tag.StartsWith(NamePrefix, StringComparison.OrdinalIgnoreCase)) { return; }
            if (tag.StartsWith(JobPrefix, StringComparison.OrdinalIgnoreCase)) {
                string job = tag.Substring(JobPrefix.Length).Trim();
                if (job.Length > 0) { c.Jobs.Add(job); }
                return;
            }
            c.AddTag(tag);
        }
    }

    public enum SwipeMsg : byte { Start, Result, Cancel, Denied }

    public struct SwipeEvent {
        public SwipeMsg Kind;
        public uint Id;
        public ushort Actor;
        public ushort Reader;
        public ushort Card;
        public byte Hand;
        public float Approach;
        public float Age;
        public bool Granted;
        public SwipeDeny Deny;

        public void Write(WireWriter w) {
            w.Clear();
            w.U8(SwipeWire.Proto);
            w.U8((byte)Kind);
            w.Var(Id);
            w.U32(Actor);
            w.U32(Reader);
            switch (Kind) {
                case SwipeMsg.Start:
                    w.U32(Card);
                    w.U8(Hand);
                    w.U32((uint)BitConverter.SingleToInt32Bits(Approach));
                    w.U32((uint)BitConverter.SingleToInt32Bits(Age));
                    break;
                case SwipeMsg.Result:
                    w.Bool(Granted);
                    w.U8((byte)Deny);
                    break;
                case SwipeMsg.Cancel:
                    w.U32((uint)BitConverter.SingleToInt32Bits(Age));
                    w.U8((byte)Deny);
                    break;
                case SwipeMsg.Denied:
                    w.U8((byte)Deny);
                    break;
            }
        }

        public static bool Read(ReadOnlySpan<byte> data, out SwipeEvent e) {
            e = default;
            var r = new WireReader(data);
            if (r.U8() != SwipeWire.Proto) { return false; }
            e.Kind = (SwipeMsg)r.U8();
            e.Id = (uint)r.Count(int.MaxValue);
            uint actor = r.U32();
            uint reader = r.U32();
            switch (e.Kind) {
                case SwipeMsg.Start: {
                    uint card = r.U32();
                    e.Hand = r.U8();
                    e.Approach = BitConverter.Int32BitsToSingle((int)r.U32());
                    e.Age = BitConverter.Int32BitsToSingle((int)r.U32());
                    if (card > ushort.MaxValue || e.Hand > 1 || !float.IsFinite(e.Approach) || !float.IsFinite(e.Age) ||
                        e.Approach < 0.0f || e.Approach > 2.0f || e.Age < 0.0f || e.Age > 10.0f) {
                        return false;
                    }
                    e.Card = (ushort)card;
                    break;
                }
                case SwipeMsg.Result:
                    e.Granted = r.Bool();
                    e.Deny = (SwipeDeny)r.U8();
                    break;
                case SwipeMsg.Cancel:
                    e.Age = BitConverter.Int32BitsToSingle((int)r.U32());
                    e.Deny = (SwipeDeny)r.U8();
                    if (!float.IsFinite(e.Age) || e.Age < 0.0f || e.Age > 10.0f) { return false; }
                    break;
                case SwipeMsg.Denied:
                    e.Deny = (SwipeDeny)r.U8();
                    break;
                default:
                    return false;
            }
            if (r.Failed || !r.End || actor > ushort.MaxValue || reader > ushort.MaxValue) { return false; }
            e.Actor = (ushort)actor;
            e.Reader = (ushort)reader;
            return true;
        }
    }

    public static class SwipeWire {
        public const string Channel = "scpitd.swipe";
        public const byte Proto = 1;
        public const int MaxMessage = 64;
    }
}
