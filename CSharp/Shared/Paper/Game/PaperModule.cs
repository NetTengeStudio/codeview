using System;
using System.Xml.Linq;
using Barotrauma;
using HarmonyLib;
using ScpItdMod.Core;
using ScpItdMod.Docs;

namespace ScpItdMod.Paper {
    public sealed class PaperModule : IModule {
        public const string Channel = "scpitd.paper";
        public static readonly Identifier PaperTag = "scpitd_paper".ToIdentifier();
        public static readonly Identifier StampTag = "scpitd_stamp".ToIdentifier();

        public void Attach(Harmony harmony) {
            DocNet.Register(PaperRules.Type());
            LoadStamps();
#if SERVER
            PaperHost.Attach();
#else
            PaperClient.Attach(harmony);
#endif
        }

        public void RoundStart() {
#if SERVER
            PaperHost.RoundStart();
#else
            PaperClient.Reset();
#endif
        }

        public void Release() {
#if CLIENT
            PaperClient.Release();
#endif
            Stamps.Current = new Stamps();
        }

        public static bool IsPaper(Item item) => item != null && !item.Removed && item.HasTag(PaperTag);

        public static string StampKind(Item item) =>
            item != null && !item.Removed && item.HasTag(StampTag) ? Stamps.Current.KindOf(item.Prefab.Identifier.Value) : null;

        public static Item HeldStamp(Character c) {
            if (c == null) { return null; }
            foreach (Item held in c.HeldItems) {
                if (StampKind(held) != null) { return held; }
            }
            return null;
        }

        public static bool InHands(Character c, Item item) {
            if (c == null || item == null) { return false; }
            foreach (Item held in c.HeldItems) {
                if (ReferenceEquals(held, item)) { return true; }
            }
            return false;
        }

        public static Item HeldPaper(Character c) {
            if (c == null) { return null; }
            foreach (Item held in c.HeldItems) {
                if (IsPaper(held)) { return held; }
            }
            return null;
        }

        public static bool Reaches(Character c, Item paper) {
            if (c == null || c.Removed || c.IsDead || c.IsIncapacitated || !IsPaper(paper)) { return false; }
            if (InHands(c, paper)) { return true; }
            if (paper.ParentInventory != null) { return ReferenceEquals(paper.GetRootInventoryOwner(), c); }
            return c.CanInteractWith(paper);
        }

        public static bool Writes(Character c, Item paper) => InHands(c, paper) && HeldStamp(c) == null;

        private static void LoadStamps() {
            string path = ModAssets.Resolve(Stamps.Asset);
            if (path.Length == 0 || !System.IO.File.Exists(path)) {
                ModLog.Error($"{Stamps.Asset} is missing");
                return;
            }
            try {
                Stamps.Current = Stamps.Parse(XDocument.Load(path).Root, error => ModLog.Error($"{Stamps.Asset}: {error}"));
            } catch (Exception error) {
                ModLog.Error($"{Stamps.Asset} cannot be read: {error.Message}");
            }
        }
    }
}
