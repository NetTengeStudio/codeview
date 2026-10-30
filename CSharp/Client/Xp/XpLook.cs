using ScpItdMod.Ui;

namespace ScpItdMod.Xp {
    public sealed class XpFaces {
        public IFace Body;
        public IFace Bold;
        public IFace Title;
        public IFace Start;
        public IFace Header;
    }

    public static class XpLook {
        public const int ScreenW = 1024;
        public const int ScreenH = 768;

        public const int Border = 3;
        public const int TitleH = 29;
        public const int CornerR = 8;
        public const int CaptionBtn = 21;
        public const int CaptionGap = 2;
        public const int CaptionRight = 5;
        public const int CaptionTop = 4;
        public const int MenuH = 19;
        public const int StatusH = 22;
        public const int ButtonW = 75;
        public const int ButtonH = 23;
        public const int FieldH = 21;
        public const int RowH = 17;
        public const int ScrollW = 17;
        public const int TaskbarH = 30;
        public const int StartW = 99;
        public const int TaskBtnMaxW = 160;
        public const int TaskBtnH = 22;
        public const int TaskBtnTop = 4;
        public const int TrayPad = 10;
        public const int IconCell = 75;
        public const int IconCellH = 70;
        public const int StartMenuW = 380;
        public const int StartHeaderH = 54;
        public const int StartFooterH = 36;
        public const int StartLeftRow = 34;
        public const int StartRightRow = 26;

        public static readonly Rgba Desktop = Rgba.Hex(0x004E98);
        public static readonly Rgba Surface = Rgba.Hex(0xECE9D8);
        public static readonly Rgba White = Rgba.Hex(0xFFFFFF);
        public static readonly Rgba Black = Rgba.Hex(0x000000);
        public static readonly Rgba Text = Rgba.Hex(0x000000);
        public static readonly Rgba Disabled = Rgba.Hex(0xACA899);
        public static readonly Rgba Select = Rgba.Hex(0x316AC5);
        public static readonly Rgba SelectText = Rgba.Hex(0xFFFFFF);
        public static readonly Rgba InputBorder = Rgba.Hex(0x7F9DB9);
        public static readonly Rgba ButtonBorder = Rgba.Hex(0x003C74);
        public static readonly Rgba ButtonBorderOff = Rgba.Hex(0xC9C7BA);
        public static readonly Rgba Shadow = Rgba.Hex(0x808080);
        public static readonly Rgba Separator = Rgba.Hex(0xACA899);
        public static readonly Rgba TitleShadow = Rgba.Hex(0x0F1089);
        public static readonly Rgba TitleInactiveText = Rgba.Hex(0xD8E4F8);
        public static readonly Rgba Link = Rgba.Hex(0x0000FF);
        public static readonly Rgba GroupHeader = Rgba.Hex(0x215DC6);

        public static readonly Rgba FrameOuterLight = Rgba.Hex(0x0831D9);
        public static readonly Rgba FrameOuterDark = Rgba.Hex(0x00138C);
        public static readonly Rgba FrameMidLight = Rgba.Hex(0x166AEE);
        public static readonly Rgba FrameMidDark = Rgba.Hex(0x001EA0);
        public static readonly Rgba FrameInnerLight = Rgba.Hex(0x0855DD);
        public static readonly Rgba FrameInnerDark = Rgba.Hex(0x003BDA);
        public static readonly Rgba FrameInactiveOuter = Rgba.Hex(0x7B96DD);
        public static readonly Rgba FrameInactiveMid = Rgba.Hex(0x9BB0EA);
        public static readonly Rgba FrameInactiveInner = Rgba.Hex(0xA9BDF1);

        public static readonly Ramp TitleActive = new Ramp(
            (0.00f, 0x0997FF), (0.08f, 0x0053EE), (0.40f, 0x0050EE), (0.88f, 0x0066FF),
            (0.93f, 0x0066FF), (0.95f, 0x005BFF), (0.96f, 0x003DD7), (1.00f, 0x003DD7));

        public static readonly Ramp TitleInactive = new Ramp(
            (0.00f, 0xA6BBF2), (0.08f, 0x7A96DF), (0.40f, 0x7B98E1), (0.88f, 0x91ACEB),
            (0.93f, 0x91ACEB), (0.96f, 0x7B95DD), (1.00f, 0x7B95DD));

        public static readonly Ramp ButtonFace = new Ramp((0.00f, 0xFFFFFF), (0.86f, 0xECEBE5), (1.00f, 0xD8D0C4));

        public static readonly Ramp ButtonDown = new Ramp((0.00f, 0xCDCAC3), (0.08f, 0xE3E3DB), (0.94f, 0xE5E5DE), (1.00f, 0xF2F2F1));

        public static readonly Rgba HoverA = Rgba.Hex(0xFFF0CF);
        public static readonly Rgba HoverB = Rgba.Hex(0xFDD889);
        public static readonly Rgba HoverC = Rgba.Hex(0xFBC761);
        public static readonly Rgba HoverD = Rgba.Hex(0xE5A01A);
        public static readonly Rgba FocusA = Rgba.Hex(0xCEE7FF);
        public static readonly Rgba FocusB = Rgba.Hex(0x98B8EA);
        public static readonly Rgba FocusC = Rgba.Hex(0xBCD4F6);
        public static readonly Rgba FocusD = Rgba.Hex(0x89ADE4);

        public static readonly Ramp Taskbar = new Ramp(
            (0.00f, 0x1F2F86), (0.03f, 0x3165C4), (0.06f, 0x3682E5), (0.10f, 0x4490E6), (0.12f, 0x3883E5),
            (0.15f, 0x2B71E0), (0.18f, 0x2663DA), (0.20f, 0x235BD6), (0.23f, 0x2258D5), (0.38f, 0x2157D6),
            (0.54f, 0x245DDB), (0.86f, 0x2562DF), (0.89f, 0x245FDC), (0.92f, 0x2158D4), (0.95f, 0x1D4EC0), (0.98f, 0x1941A5), (1.00f, 0x1941A5));

        public static readonly Ramp Tray = new Ramp(
            (0.00f, 0x0C59B9), (0.06f, 0x139EE9), (0.10f, 0x18B5F2), (0.14f, 0x139BEB), (0.19f, 0x1290E8),
            (0.63f, 0x0D8DEA), (0.81f, 0x0D9FF1), (0.88f, 0x0F9EED), (0.91f, 0x119BE9), (0.94f, 0x1392E2), (0.97f, 0x137ED7), (1.00f, 0x095BC9));

        public static readonly Rgba TrayEdge = Rgba.Hex(0x1042AF);
        public static readonly Rgba TrayGlow = Rgba.Hex(0x18BBFF);

        public static readonly Ramp StartButton = new Ramp(
            (0.00f, 0x5EAC56), (0.06f, 0x46A13D), (0.15f, 0x3C9A35), (0.50f, 0x2F8B2A), (0.85f, 0x34932F), (0.94f, 0x2E8429), (1.00f, 0x1D6A1A));

        public static readonly Ramp StartButtonHot = new Ramp(
            (0.00f, 0x6CBA63), (0.06f, 0x53AE49), (0.15f, 0x48A73F), (0.50f, 0x389831), (0.85f, 0x3DA136), (0.94f, 0x349030), (1.00f, 0x237622));

        public static readonly Rgba TaskBtn = Rgba.Hex(0x3C81F3);
        public static readonly Rgba TaskBtnHot = Rgba.Hex(0x53A3FF);
        public static readonly Rgba TaskBtnFocus = Rgba.Hex(0x1E52B7);
        public static readonly Rgba TaskBtnFocusHot = Rgba.Hex(0x3576F3);

        public static readonly Ramp StartHeader = new Ramp(
            (0.00f, 0x1868CE), (0.12f, 0x0E60CB), (0.20f, 0x0E60CB), (0.32f, 0x1164CF), (0.33f, 0x1667CF),
            (0.47f, 0x1B6CD3), (0.54f, 0x1E70D9), (0.60f, 0x2476DC), (0.65f, 0x297AE0), (0.77f, 0x3482E3),
            (0.79f, 0x3786E5), (0.90f, 0x428EE9), (1.00f, 0x4791EB));

        public static readonly Ramp StartFooter = new Ramp(
            (0.00f, 0x4282D6), (0.03f, 0x3B85E0), (0.05f, 0x418AE3), (0.17f, 0x418AE3), (0.21f, 0x3C87E2),
            (0.26f, 0x3786E4), (0.29f, 0x3482E3), (0.39f, 0x2E7EE1), (0.49f, 0x2374DF), (0.57f, 0x2072DB),
            (0.62f, 0x196EDB), (0.72f, 0x176BD8), (0.75f, 0x1468D5), (0.83f, 0x1165D2), (0.88f, 0x0F61CB), (1.00f, 0x0F61CB));

        public static readonly Rgba StartRight = Rgba.Hex(0xCBE3FF);
        public static readonly Rgba StartRightText = Rgba.Hex(0x00136B);
        public static readonly Rgba StartRightEdge = Rgba.Hex(0x95BDEE);
        public static readonly Rgba StartMenuHot = Rgba.Hex(0x2F71CD);
        public static readonly Rgba StartOrange = Rgba.Hex(0xDA884A);
        public static readonly Rgba StartMenuEdge = Rgba.Hex(0x385DE7);

        public static readonly Ramp ScrollTrack = new Ramp((0.00f, 0xEEEDE5), (0.06f, 0xF3F1EC), (0.50f, 0xF9F8F4), (1.00f, 0xFEFEFB));
        public static readonly Rgba ScrollThumb = Rgba.Hex(0xC8D6FB);
        public static readonly Rgba ScrollThumbEdge = Rgba.Hex(0xBAD1FC);

        public static readonly Rgba TabBorder = Rgba.Hex(0x919B9C);
        public static readonly Rgba TabHot = Rgba.Hex(0xE68B2C);
        public static readonly Rgba TabHotLight = Rgba.Hex(0xFFC73C);
        public static readonly Ramp TabFace = new Ramp((0.00f, 0xFFFFFF), (0.80f, 0xF1EFE2), (1.00f, 0xECE9D8));
        public static readonly Rgba TabPage = Rgba.Hex(0xFCFCFE);

        public static readonly Rgba Progress = Rgba.Hex(0x2FB52F);
        public static readonly Rgba ProgressLight = Rgba.Hex(0x8CDC8C);

        public const string Caption = "Content/UI/Xp/";

        private static readonly System.Collections.Generic.Dictionary<string, string> images = new System.Collections.Generic.Dictionary<string, string>(System.StringComparer.Ordinal);

        public static string Image(string name) {
            if (images.TryGetValue(name, out string path)) { return path; }
            path = Caption + name + ".png";
            images[name] = path;
            return path;
        }
    }
}
