using System;
using Vector2 = ScpItdMod.Core.Vec2;
using ScpItdMod.Motion;
using ScpItdMod.Ui;

namespace ScpItdMod.Durak {
    public enum CardZone : byte { Hidden, Hand, Attack, Defense, Departing }
    public sealed class DurakPresentation : IDisposable {
        private sealed class CardView : IDisposable {
            internal readonly int Id;
            internal readonly MotionVisual Motion;
            internal CardZone Zone;
            internal int Pair, Order;
            internal Box Target;
            internal float Angle;
            internal bool Queued;
            private MotionPlayback deal;
            private MotionScope dealScope;
            internal CardView(int id, MotionRuntime runtime, in Box initial) { Id = id; Motion = new MotionVisual(runtime, initial, true); }
            internal void Deal(int index, MotionRuntime runtime) {
                Queued = true;
                dealScope = runtime.Scope();
                deal = dealScope.Play(new MotionSequenceBuilder().Marker(MotionPresets.Stagger(index), Start).Wait(0.001).Compile()).OnCompleted(FinishDeal);
            }
            private void FinishDeal() { dealScope?.Dispose(); dealScope = null; }
            internal void StopDeal() { deal?.Cancel(); deal = null; FinishDeal(); Queued = false; }
            private void Start() { Queued = false; Move(); Motion.Opacity.To(1, MotionPresets.Fade); }
            internal void Move() { if (Queued) { return; } Motion.Place(Target, MotionPresets.Card); Motion.Rotation.To(Angle, MotionPresets.Card); }
            public void Dispose() { StopDeal(); Motion.Dispose(); }
        }
        private readonly MotionRuntime runtime;
        private readonly CardView[] cards = new CardView[DurakCards.Count];
        private readonly CardView[] opponents = new CardView[DurakCards.Seats * DurakCards.Count];
        private readonly Box[] opponentOrigin = new Box[DurakCards.Seats];
        private byte discardCount;
        private readonly int[] order = new int[DurakCards.Count];
        private readonly CardZone[] zones = new CardZone[DurakCards.Count];
        private uint room, revision, bout, runtimeEpoch;
        private int viewer = -2, priorDefender = -1, count;
        private bool priorTaking;
        private ulong hand;
        private Box table;
        private Vector2 dragOrigin, dragOffset, lastPointer;
        private double pointerAt;
        public int DragCard { get; private set; } = -1;
        public bool Dragged { get; private set; }
        public int SemanticChanges { get; private set; }
        public DurakPresentation(MotionRuntime runtime) { this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime)); runtimeEpoch = runtime.Epoch; }
        public VisualPose Pose(int card) => cards[card].Motion.Pose;
        public CardZone Zone(int card) => cards[card]?.Zone ?? CardZone.Hidden;
        public void Reconcile(DurakView view, ReadOnlySpan<byte> sortedHand, in Box mine, in Box center, in Box deck, in Box discard) {
            if (view == null) { Dispose(); room = 0; viewer = -2; return; }
            if (room != view.Id || viewer != view.Me || runtimeEpoch != runtime.Epoch) { Dispose(); runtimeEpoch = runtime.Epoch; room = view.Id; viewer = view.Me; revision = 0; bout = view.Bout; }
            table = center; Array.Clear(zones);
            int seats = Math.Max(1, view.Capacity - (view.Me >= 0 ? 1 : 0)), ordinal = 0;
            for (int seat = 0; seat < DurakCards.Seats; seat++) {
                int visible = seat < view.Capacity && seat != view.Me && seat < view.Players.Length && view.Players[seat].Present ? view.Players[seat].Cards : 0;
                float width = center.W / seats;
                float origin = center.X + (ordinal + 0.5f) * width;
                if (seat < view.Capacity && seat != view.Me) { ordinal++; }
                opponentOrigin[seat] = new Box(origin - 14, center.Y - 40, 28, 38);
                for (int slot = 0; slot < DurakCards.Count; slot++) {
                    int token = seat * DurakCards.Count + slot; CardView back = opponents[token];
                    if (slot < visible) {
                        if (back != null && !back.Motion.Alive) { back.Dispose(); back = opponents[token] = null; }
                        bool fresh = back == null;
                        if (fresh) { back = opponents[token] = new CardView(-1, runtime, deck); }
                        float spread = MathF.Min(7, (width - 36) / Math.Max(1, visible - 1));
                        back.Target = opponentOrigin[seat].Offset((slot - (visible - 1) * 0.5f) * spread, 0);
                        back.Angle = (slot - (visible - 1) * 0.5f) * 0.012f;
                        back.Zone = CardZone.Hand;
                        if (fresh) { back.Deal(slot, runtime); }
                        back.Move(); back.Motion.Shadow.To(1, MotionPresets.Hover);
                    } else if (back != null) {
                        if (back.Zone != CardZone.Departing) { back.StopDeal(); back.Zone = CardZone.Departing; back.Motion.Opacity.To(0, MotionPresets.Fade); }
                        if (!back.Motion.Opacity.Running && back.Motion.Opacity.Value <= 0) { back.Dispose(); opponents[token] = null; }
                    }
                }
            }
            for (int i = 0; i < sortedHand.Length; i++) { zones[sortedHand[i]] = CardZone.Hand; }
            for (int i = 0; i < view.TableCount; i++) {
                zones[view.Attacks[i]] = CardZone.Attack;
                if (view.Defenses[i] != DurakCards.None) { zones[view.Defenses[i]] = CardZone.Defense; }
            }
            if (revision != view.GameRevision || hand != view.Hand || bout != view.Bout) {
                for (int i = 0; i < cards.Length; i++) { if ((cards[i]?.Zone ?? CardZone.Hidden) != zones[i]) { SemanticChanges++; } }
                revision = view.GameRevision; hand = view.Hand;
            }
            float stride = sortedHand.Length <= 1 ? 0 : MathF.Min(76, (mine.W - 85) / (sortedHand.Length - 1));
            float left = mine.CenterX - (71 + stride * (sortedHand.Length - 1)) * 0.5f;
            int dealt = 0;
            for (int i = 0; i < sortedHand.Length; i++) {
                int id = sortedHand[i]; bool fresh = cards[id] == null;
                CardView card = Ensure(id, deck); card.Zone = CardZone.Hand; card.Order = i;
                card.Target = new Box(left + i * stride, mine.Y + 15, 71, 96);
                card.Angle = sortedHand.Length <= 1 ? 0 : (i / (float)(sortedHand.Length - 1) - 0.5f) * 0.16f;
                if (fresh) { card.Deal(dealt++, runtime); }
                if (DragCard != id) { card.Move(); }
                if (!card.Queued) { card.Motion.Opacity.To(1, MotionPresets.Fade); }
            }
            float step = MathF.Min(96, (center.W - 14) / DurakCards.Pairs);
            float x = center.CenterX - (step * view.TableCount - (step - 87)) * 0.5f;
            for (int i = 0; i < view.TableCount; i++) {
                Box target = new Box(x + i * step, center.CenterY - 58, 71, 96);
                PlaceTable(view.Attacks[i], CardZone.Attack, i, target, view.Attacker >= 0 && view.Attacker != viewer ? opponentOrigin[view.Attacker] : deck, -0.025f + i * 0.008f);
                if (view.Defenses[i] != DurakCards.None) { PlaceTable(view.Defenses[i], CardZone.Defense, i, target.Offset(14, 20), view.Defender >= 0 && view.Defender != viewer ? opponentOrigin[view.Defender] : deck, 0.05f); }
            }
            count = 0;
            for (int i = 0; i < cards.Length; i++) {
                CardView card = cards[i]; if (card == null) { continue; }
                if (zones[i] == CardZone.Hidden && card.Zone != CardZone.Departing) {
                    card.StopDeal(); card.Zone = CardZone.Departing;
                    card.Target = priorTaking || view.Bout != bout && view.DiscardCount == discardCount ? new Box(priorDefender == viewer ? mine.CenterX - 35 : center.Right - 71, priorDefender == viewer ? mine.Y : center.Y, 71, 96) : discard;
                    card.Angle = 0; card.Motion.Interact(false, false, false);
                    card.Motion.Place(card.Target, MotionTransition.Tween(0.55f));
                    card.Motion.Rotation.To(0, MotionTransition.Tween(0.55f));
                }
                if (card.Zone == CardZone.Departing && !card.Motion.Position.Running && !card.Motion.Size.Running) { card.Motion.Opacity.To(0, MotionPresets.Fade); }
                if (card.Zone == CardZone.Departing && !card.Motion.Opacity.Running && card.Motion.Opacity.Value <= 0) { card.Dispose(); cards[i] = null; continue; }
                int slot = count;
                while (slot > 0 && Depth(cards[order[slot - 1]]) > Depth(card)) { order[slot] = order[slot - 1]; slot--; }
                order[slot] = i; count++;
            }
            bout = view.Bout; discardCount = view.DiscardCount; priorTaking = view.Taking; priorDefender = view.Defender;
            if (DragCard >= 0 && cards[DragCard]?.Zone != CardZone.Hand) { DragCard = -1; }
        }
        private CardView Ensure(int id, in Box from) {
            if (cards[id] == null || !cards[id].Motion.Alive) { cards[id]?.Dispose(); cards[id] = new CardView(id, runtime, from); }
            return cards[id];
        }
        private void PlaceTable(int id, CardZone zone, int pair, in Box target, in Box deck, float angle) {
            CardView card = Ensure(id, deck); card.Zone = zone; card.Pair = pair; card.Target = target; card.Angle = angle;
            card.StopDeal(); card.Move(); card.Motion.Opacity.To(1, MotionPresets.Fade); card.Motion.Interact(false, false, false);
        }
        private float Depth(CardView card) => card.Zone == CardZone.Departing ? 400 + card.Id * 0.001f : card.Zone == CardZone.Hand ? 100 + card.Motion.Depth.Value * 100 + card.Order * 0.001f : card.Pair * 2 + (card.Zone == CardZone.Defense ? 1 : 0);
        public int HitHand(float x, float y) {
            for (int i = count - 1; i >= 0; i--) { CardView card = cards[order[i]]; if (card.Zone == CardZone.Hand && card.Motion.Pose.Contains(x, y)) { return card.Id; } }
            return -1;
        }
        public bool OverTable(float x, float y) => table.Contains(x, y);
        public int AttackAt(float x, float y) {
            for (int i = count - 1; i >= 0; i--) { CardView card = cards[order[i]]; if (card.Zone == CardZone.Attack && card.Motion.Pose.Contains(x, y)) { return card.Pair; } }
            return -1;
        }
        public void Interact(int selected, float x, float y, bool down) {
            int hot = HitHand(x, y);
            for (int i = 0; i < cards.Length; i++) { CardView card = cards[i]; if (card?.Zone == CardZone.Hand) { card.Motion.Interact(card.Id == hot, card.Id == selected, down && card.Id == hot); } }
        }
        public void BeginDrag(int card, float x, float y) {
            if ((uint)card >= cards.Length || cards[card]?.Zone != CardZone.Hand) { return; }
            DragCard = card; Dragged = false; dragOrigin = lastPointer = new Vector2(x, y);
            VisualPose pose = cards[card].Motion.Pose;
            dragOffset = new Vector2(x - pose.Bounds.CenterX, y - pose.Bounds.CenterY); pointerAt = runtime.Time;
        }
        public void Drag(float x, float y) {
            if (DragCard < 0) { return; }
            var pointer = new Vector2(x, y); Dragged |= Vector2.DistanceSquared(pointer, dragOrigin) > 9;
            if (!Dragged) { return; }
            CardView card = cards[DragCard]; double elapsed = runtime.Time - pointerAt;
            Vector2 velocity = elapsed > 0 ? (pointer - lastPointer) / (float)elapsed : Vector2.Zero;
            float length = velocity.Length(); if (length > 3000) { velocity *= 3000 / length; }
            card.Motion.Lift.Snap(0); card.Motion.Position.Snap(pointer - dragOffset); card.Motion.Position.Impulse(velocity); card.Motion.Rotation.To(0, MotionPresets.Hover);
            lastPointer = pointer; pointerAt = runtime.Time;
        }
        public void EndDrag() { if (DragCard >= 0) { cards[DragCard]?.Move(); } DragCard = -1; Dragged = false; }
        public void Paint(ICanvas canvas, Func<int, string> path) {
            for (int i = 0; i < opponents.Length; i++) { CardView back = opponents[i]; if (back != null) { MotionPaint.Image(canvas, "Content/UI/Durak/back.png", back.Motion.Pose, new Rgba(255, 255, 255)); } }
            for (int i = 0; i < count; i++) { CardView card = cards[order[i]]; MotionPaint.Image(canvas, path(card.Id), card.Motion.Pose, new Rgba(255, 255, 255)); }
        }
        public void Dispose() {
            for (int i = 0; i < cards.Length; i++) { cards[i]?.Dispose(); cards[i] = null; }
            for (int i = 0; i < opponents.Length; i++) { opponents[i]?.Dispose(); opponents[i] = null; }
            count = 0; DragCard = -1; Dragged = false;
        }
    }
}
