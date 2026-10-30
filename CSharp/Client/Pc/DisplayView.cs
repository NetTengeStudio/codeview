using System;
using ScpItdMod.Net;

namespace ScpItdMod.Pc {
    public sealed class DisplayView {
        public DisplayState State { get; private set; }
        public bool Ready { get; private set; }
        public Durak.DurakView Cards { get; private set; }
        public Chess.ChessView Chess { get; private set; }
        public PrinterView Printer { get; private set; }

        public bool Apply(ReadOnlySpan<byte> data) {
            if (!DisplayWire.Read(data, out DisplayState state, out ReadOnlySpan<byte> game) || Ready &&
                (State.Item != state.Item || state.Epoch != State.Epoch && (int)(state.Epoch - State.Epoch) <= 0 ||
                state.Epoch == State.Epoch && (int)(state.Revision - State.Revision) <= 0)) { return false; }
            Durak.DurakView cards = null; Chess.ChessView chess = null; PrinterView printer = default;
            if (!game.IsEmpty) {
                if (state.App == PcApp.Durak) {
                    if (!Durak.DurakWire.ReadReply(game, DisplayWire.Session, out _, out _, out _, out _, out _, out bool hasState, out cards) ||
                        !hasState || cards != null && (cards.Me != -1 || cards.Hand != 0 || cards.CanAttack != 0 || cards.CanTransfer != 0)) { return false; }
                } else if (state.App == PcApp.Chess) {
                    if (!ScpItdMod.Chess.ChessWire.ReadReply(game, DisplayWire.Session, out _, out _, out _, out _, out _, out bool hasState, out chess) ||
                        !hasState || chess != null && (chess.Me != -1 || chess.Owner)) { return false; }
                } else if (state.App == PcApp.Printers) { if (!PrinterView.Read(game, out printer)) { return false; } }
                else { return false; }
            }
            State = state; Cards = cards; Chess = chess; Printer = printer; Ready = true;
            return true;
        }
    }
}
