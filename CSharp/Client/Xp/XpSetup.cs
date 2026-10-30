using ScpItdMod.Pc;

namespace ScpItdMod.Xp {
    public static class XpSetup {
        public static XpShell Create(PcSession pc, object cams, Durak.DurakSession cards = null, Chess.ChessSession chess = null) {
            var shell = new XpShell { Pc = pc, Cams = cams, Cards = cards, Chess = chess };
            shell.Register("explorer", (s, arg) => new Explorer(arg is ulong id ? id : Explorer.Computer));
            shell.Register("bin", (s, arg) => new Explorer(Explorer.RecycleBin), singleInstance: true);
            shell.Register("notepad", (s, arg) => arg is ulong id ? new Notepad(pc.Computer, id) : null);
            shell.Register("dossiers", (s, arg) => new Dossiers(), singleInstance: true);
            shell.Register("cameras", (s, arg) => new Cameras(), singleInstance: true);
            shell.Register("printers", (s, arg) => new Printers(), singleInstance: true);
            shell.Register("documents", (s, arg) => new Explorer(DocumentsRoot(pc)));
            shell.Register("durak", (s, arg) => new DurakApp(s), singleInstance: true);
            shell.Register("chess", (s, arg) => new ChessApp(s), singleInstance: true);
            shell.Register("meteo", (s, arg) => new Meteo.MeteoScreen(), singleInstance: true);

            shell.Icons.Add(new XpShortcut { Name = "Мой компьютер", Icon = "computer16", Large = "computer32", App = "explorer", Arg = Explorer.Computer });
            shell.Icons.Add(new XpShortcut { Name = "Мои документы", Icon = "folder16", Large = "folder32", App = "documents" });
            shell.Icons.Add(new XpShortcut { Name = "Личные дела", Icon = "dossier16", Large = "dossier32", App = "dossiers" });
            shell.Icons.Add(new XpShortcut { Name = "Камеры", Icon = "camera16", Large = "camera32", App = "cameras" });
            shell.Icons.Add(new XpShortcut { Name = "Принтеры", Icon = "printer16", Large = "printer32", App = "printers" });
            shell.Icons.Add(new XpShortcut { Name = "Корзина", Icon = "recycle16", Large = "recycle32", App = "bin" });
            shell.Icons.Add(new XpShortcut { Name = "Дурак", Icon = "durak16", Large = "durak32", App = "durak" });
            shell.Icons.Add(new XpShortcut { Name = "Шахматы", Icon = "chess16", Large = "chess32", App = "chess" });
            shell.Icons.Add(new XpShortcut { Name = "Метеорология", Icon = "meteo16", Large = "meteo32", App = "meteo" });

            shell.StartLeft.Add(new XpShortcut { Name = "Проводник", Icon = "folder16", Large = "folder32", App = "explorer", Arg = Explorer.Computer, Bold = true });
            shell.StartLeft.Add(new XpShortcut { Name = "Личные дела", Icon = "dossier16", Large = "dossier32", App = "dossiers", Bold = true });
            shell.StartLeft.Add(new XpShortcut { Name = "Метеослужба", Icon = "meteo16", Large = "meteo32", App = "meteo", Bold = true });
            shell.StartLeft.Add(new XpShortcut { Name = "Камеры наблюдения", Icon = "camera16", Large = "camera32", App = "cameras" });
            shell.StartLeft.Add(new XpShortcut { Name = "Принтеры", Icon = "printer16", Large = "printer32", App = "printers" });
            shell.StartLeft.Add(new XpShortcut { Name = "Дурак", Icon = "durak16", Large = "durak32", App = "durak" });
            shell.StartLeft.Add(new XpShortcut { Name = "Шахматы", Icon = "chess16", Large = "chess32", App = "chess" });

            shell.StartRight.Add(new XpShortcut { Name = "Мои документы", Icon = "folder16", App = "documents", Bold = true });
            shell.StartRight.Add(new XpShortcut { Name = "Мой компьютер", Icon = "computer16", App = "explorer", Arg = Explorer.Computer, Bold = true });
            shell.StartRight.Add(new XpShortcut { Name = "Принтеры", Icon = "printer16", App = "printers" });
            shell.StartRight.Add(new XpShortcut { Name = "Корзина", Icon = "recycle16", App = "bin" });
            return shell;
        }

        public static ulong DocumentsRoot(PcSession pc) {
            for (int i = 0; i < pc.Roots.Count; i++) {
                if (pc.Roots[i].Name == "Мои документы") { return pc.Roots[i].Id; }
            }
            return pc.Roots.Count > 0 ? pc.Roots[0].Id : Explorer.Computer;
        }
    }
}
