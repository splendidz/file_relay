using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;
using System.Windows.Forms;

namespace FileRelay.UI
{
    /// <summary>
    /// 테마 색상(라이트/다크), 글꼴, DPI 배율과 그리기 도우미.
    /// 색은 Init 에서 테마에 맞춰 채운다 (바꾸면 다시 시작해야 적용).
    /// </summary>
    public static class Theme
    {
        public static float Scale = 1f;
        public static int S(int v) => (int)Math.Round(v * Scale);
        public static float Sf(float v) => v * Scale;

        public static bool IsDark { get; private set; }

        // ── 색상 ──
        public static Color Bg;             // 목록/본문
        public static Color Panel;          // 사이드바, 창 머리글
        public static Color Chrome;         // 메뉴, 툴바, 상태 표시줄
        public static Color MenuBg;         // 펼친 메뉴
        public static Color Raised;         // 보조 버튼, 진행 막대 바탕
        public static Color Input;          // 입력 칸
        public static Color Hover;
        public static Color Pressed;
        public static Color Border;
        public static Color BorderSoft;
        public static Color Splitter;       // 창 나누기 막대
        public static Color Text;
        public static Color TextDim;
        public static Color TextFaint;
        public static Color Accent;
        public static Color AccentHover;
        public static Color AccentPressed;
        public static Color Selection;
        public static Color SelectionInactive;
        public static Color Online;
        public static Color Away;
        public static Color Busy;
        public static Color Offline;
        public static Color Danger;
        public static Color DangerHover;
        public static Color Warn;
        public static Color Success;
        public static Color Info;
        public static Color Download;
        public static Color Upload;
        public static Color AvatarDim;      // 오프라인 아바타를 흐리게 섞을 색
        public static Color AvatarDimText;

        static Theme() { ApplyPalette(false); }

        static void ApplyPalette(bool dark)
        {
            IsDark = dark;
            if (dark)
            {
                Bg = Hex("1E1F22"); Panel = Hex("232428"); Chrome = Hex("2B2D31"); MenuBg = Chrome;
                Raised = Hex("34363C"); Input = Hex("17181B"); Hover = Hex("2C2F34"); Pressed = Hex("3A3D44");
                Border = Hex("3A3C42"); BorderSoft = Hex("2C2E33"); Splitter = Hex("141517");
                Text = Hex("E6E8EB"); TextDim = Hex("A4A8AF"); TextFaint = Hex("6E737B");
                Accent = Hex("8B6CF7"); AccentHover = Hex("A089F9"); AccentPressed = Hex("7352E8");
                Selection = Hex("3A2F6B"); SelectionInactive = Hex("34373E");
                Online = Hex("3FB950"); Away = Hex("E3B341"); Busy = Hex("F0544F"); Offline = Hex("6E7681");
                Danger = Hex("E5534B"); DangerHover = Hex("F06A62"); Warn = Hex("D9A43B"); Success = Hex("3FB950");
                Info = Hex("58A6FF"); Download = Hex("4CC38A"); Upload = Hex("6AA7FF");
                AvatarDim = Hex("4A4D53"); AvatarDimText = Hex("C9CCD1");
            }
            else
            {
                Bg = Hex("FFFFFF"); Panel = Hex("F7F7F9"); Chrome = Hex("F2F2F5"); MenuBg = Hex("FFFFFF");
                Raised = Hex("EEEEF2"); Input = Hex("FFFFFF"); Hover = Hex("EDEDF2"); Pressed = Hex("E1E1E8");
                Border = Hex("D5D6DC"); BorderSoft = Hex("E6E7EB"); Splitter = Hex("E3E4E9");
                Text = Hex("1F2024"); TextDim = Hex("5B5E66"); TextFaint = Hex("8D9098");
                Accent = Hex("6E44E8"); AccentHover = Hex("7E58EE"); AccentPressed = Hex("5A33D0");
                Selection = Hex("E9E2FF"); SelectionInactive = Hex("ECECF1");
                Online = Hex("2DA44E"); Away = Hex("D4A017"); Busy = Hex("E5484D"); Offline = Hex("9AA0A6");
                Danger = Hex("CF222E"); DangerHover = Hex("DD3A45"); Warn = Hex("B7791F"); Success = Hex("1A7F37");
                Info = Hex("0969DA"); Download = Hex("1A7F37"); Upload = Hex("0969DA");
                AvatarDim = Hex("C3C6CD"); AvatarDimText = Hex("FFFFFF");
            }
        }

        /// <summary>호버·강조용으로 살짝 띄운 색 — 다크는 밝게, 라이트는 어둡게.</summary>
        public static Color Lift(Color c, float t) => Blend(c, IsDark ? Color.White : Color.Black, IsDark ? t : t * 0.6f);

        // ── 글꼴 ──
        public static Font Ui, UiBold, Small, SmallBold, Title, Big, Mono;
        public static string FontName = "Malgun Gothic";
        public static string IconFont;   // Segoe Fluent Icons / Segoe MDL2 Assets / null

        public static void Init(string fontName, float size, string theme = "light")
        {
            ApplyPalette(string.Equals(theme, "dark", StringComparison.OrdinalIgnoreCase));
            using (var g = Graphics.FromHwnd(IntPtr.Zero)) Scale = Math.Max(1f, g.DpiX / 96f);
            using (var fonts = new InstalledFontCollection())
            {
                var names = fonts.Families.Select(f => f.Name).ToList();
                FontName = names.Contains(fontName) ? fontName :
                           names.Contains("Malgun Gothic") ? "Malgun Gothic" :
                           names.Contains("맑은 고딕") ? "맑은 고딕" : "Segoe UI";
                IconFont = names.Contains("Segoe Fluent Icons") ? "Segoe Fluent Icons" :
                           names.Contains("Segoe MDL2 Assets") ? "Segoe MDL2 Assets" : null;
            }
            Ui = new Font(FontName, size);
            UiBold = new Font(FontName, size, FontStyle.Bold);
            Small = new Font(FontName, Math.Max(7f, size - 1f));
            SmallBold = new Font(FontName, Math.Max(7f, size - 1f), FontStyle.Bold);
            Title = new Font(FontName, size + 2.5f, FontStyle.Bold);
            Big = new Font(FontName, size + 5f, FontStyle.Bold);
            Mono = new Font("Consolas", size);
        }

        public static Color Hex(string h)
        {
            int v = Convert.ToInt32(h, 16);
            return Color.FromArgb(255, (v >> 16) & 255, (v >> 8) & 255, v & 255);
        }

        public static Color Blend(Color a, Color b, float t)
        {
            t = Math.Max(0, Math.Min(1, t));
            return Color.FromArgb(255,
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
        }

        public static Color Alpha(Color c, int a) => Color.FromArgb(a, c);

        public static Color PresenceColor(Presence p)
        {
            switch (p)
            {
                case Presence.Online: return Online;
                case Presence.Away: return Away;
                case Presence.Busy: return Busy;
                default: return Offline;
            }
        }

        // ── 창 장식 ──
        /// <summary>제목 표시줄을 테마에 맞춘다 (다크 모드 여부, Win11 이면 제목 표시줄 색).</summary>
        public static void StyleTitleBar(IntPtr hwnd, Color? caption = null)
        {
            try
            {
                int on = IsDark ? 1 : 0;
                if (Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, 4) != 0)
                    Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref on, 4);
                if (Native.IsWin11)
                {
                    int c = Native.ColorRef(caption ?? Chrome);
                    Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_CAPTION_COLOR, ref c, 4);
                    int b = Native.ColorRef(Border);
                    Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_BORDER_COLOR, ref b, 4);
                }
            }
            catch { }
        }

        /// <summary>Windows 11 둥근 모서리 (테두리 없는 창/팝업).</summary>
        public static void RoundCorners(IntPtr hwnd, bool small, Color? border = null)
        {
            if (!Native.IsWin11) return;
            try
            {
                int pref = small ? 3 : 2;
                Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, 4);
                if (border.HasValue)
                {
                    int b = Native.ColorRef(border.Value);
                    Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_BORDER_COLOR, ref b, 4);
                }
            }
            catch { }
        }

        /// <summary>스크롤바 등 기본 컨트롤을 테마에 맞춘다 (dark 를 주면 강제 — 다크 쪽지 등).</summary>
        public static void ApplyControlTheme(Control c, bool? dark = null)
        {
            string name = (dark ?? IsDark) ? "DarkMode_Explorer" : "Explorer";
            if (c.IsHandleCreated) Native.SetWindowTheme(c.Handle, name, null);
            else c.HandleCreated += (s, e) => Native.SetWindowTheme(((Control)s).Handle, name, null);
        }

        // ── 그리기 ──
        public static GraphicsPath Round(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            if (d <= 0.5f) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void FillRound(Graphics g, Color c, RectangleF r, float radius)
        {
            using (var p = Round(r, radius))
            using (var b = new SolidBrush(c))
                g.FillPath(b, p);
        }

        public static void DrawRound(Graphics g, Color c, RectangleF r, float radius, float width = 1f)
        {
            using (var p = Round(r, radius))
            using (var pen = new Pen(c, width))
                g.DrawPath(pen, p);
        }

        public static void HQ(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        }

        public const TextFormatFlags TextLeft = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
        public const TextFormatFlags TextRight = TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
        public const TextFormatFlags TextCenter = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;

        public static void DrawText(Graphics g, string s, Font f, Color c, Rectangle r, TextFormatFlags flags = TextLeft) =>
            TextRenderer.DrawText(g, s ?? "", f, r, c, flags);

        public static Size Measure(string s, Font f) =>
            TextRenderer.MeasureText(s ?? "", f, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
    }
}
