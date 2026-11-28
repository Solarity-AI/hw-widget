// HW Widget for Windows - drawing (GDI+). Everything is drawn in device-independent pixels, then scaled.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;

namespace HwWidget
{
    public enum Fourth { Auto, Gpu, Disk }

    public sealed class Cell
    {
        public string Label = "", Value = "—";
        public double Frac = double.NaN;      // 0..1 for the bar, NaN = empty
        public Color Bar1, Bar2, LabelColor, ValueColor;
        public int Level;                     // 0 normal, 1 warm, 2 critical
        public string Spoken;                 // what a screen reader says instead of Value, when set
    }

    public static class Fmt
    {
        public static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        public const string Dash = "—";

        public static string Temp(double c, bool f)
        {
            if (double.IsNaN(c)) return Dash;
            double v = f ? c * 9 / 5 + 32 : c;
            return R(v).ToString("0", Inv) + "°";
        }
        public static string TempLong(double c, bool f)
        {
            if (double.IsNaN(c)) return Dash;
            return Temp(c, f) + (f ? "F" : "C");
        }
        public static string Pct(double p)
        {
            if (double.IsNaN(p)) return Dash;
            return R(Math.Max(0, Math.Min(100, p))).ToString("0", Inv) + "%";
        }
        public static string Rate(double bps)
        {
            if (double.IsNaN(bps)) return Dash;
            string[] u = { "B/s", "KB/s", "MB/s", "GB/s" };
            double v = Math.Max(0, bps); int i = 0;
            // Roll over on the value shown, as RateShort does: 999.6 B/s is "1.0 KB/s", never "1000 B/s" or "10.0 KB/s".
            while (v >= 999.5 && i < u.Length - 1) { v /= 1024; i++; }
            return (v < 9.95 && i > 0 ? v.ToString("0.0", Inv) : R(v).ToString("0", Inv)) + " " + u[i];
        }
        // The pill's 4-character rate: 0K, 999K, 1.0M, 9.9M, 10M, 999M, 1.0G (1024-based, per second).
        public static string RateShort(double bps)
        {
            if (double.IsNaN(bps)) return Dash;
            string[] u = { "K", "M", "G" };
            double v = Math.Max(0, bps) / 1024; int i = 0;
            while (v >= 999.5 && i < u.Length - 1) { v /= 1024; i++; }
            if (i > 0 && v < 9.95) return v.ToString("0.0", Inv) + u[i];
            return R(v).ToString("0", Inv) + u[i];
        }
        public static string GB(double g)
        {
            if (double.IsNaN(g)) return Dash;
            return (g < 9.95 ? g.ToString("0.0", Inv) : R(g).ToString("0", Inv)) + " GB";
        }
        // A process's memory: "640 MB" under 1 GB, else "1.2 GB" (1024-based).
        public static string Bytes(double b)
        {
            if (double.IsNaN(b)) return Dash;
            double mb = Math.Max(0, b) / (1024.0 * 1024);
            if (R(mb) < 1000) return R(mb).ToString("0", Inv) + " MB";
            return GB(mb / 1024);
        }
        // Half away from zero, as GNOME and macOS show it (Math.Round alone rounds 68.5 to 68).
        static double R(double v) { return Math.Round(v, MidpointRounding.AwayFromZero); }

        // Judged on the whole number shown, so 74.6 % reads "75%" and is amber, never "75%" in white.
        public static int Level(double v, double warn, double crit)
        {
            if (double.IsNaN(v)) return 0;
            v = R(v);
            return v >= crit ? 2 : v >= warn ? 1 : 0;
        }

        // Temperature: amber at 80 °C, red at 90 °C, judged on the number shown in the chosen unit (176 / 194 °F).
        public static int TempLevel(double c, bool f)
        {
            if (double.IsNaN(c)) return 0;
            return f ? Level(c * 9 / 5 + 32, 176, 194) : Level(c, 80, 90);
        }
    }

    public sealed class Palette
    {
        public bool Dark;
        public Color Bg1, Bg2, Text, SubText, Track, Sep, Warn, Crit, CardBg1, CardBg2, Shadow, Edge;
        public int BorderAlpha;

        public static Palette For(bool dark)
        {
            Palette p = new Palette();
            p.Dark = dark;
            if (dark)
            {
                p.Bg1 = Hex("#2A2A40"); p.Bg2 = Hex("#18182A");
                p.Text = Hex("#F8FAFC"); p.SubText = Hex("#A5ABC3");
                p.Track = Color.FromArgb(36, 255, 255, 255); p.Sep = Color.FromArgb(30, 255, 255, 255);
                p.Warn = Hex("#FCD34D"); p.Crit = Hex("#FF8FA3");
                p.CardBg1 = Hex("#26263B"); p.CardBg2 = Hex("#15151F");
                p.Shadow = Color.Black; p.BorderAlpha = 175; p.Edge = Color.FromArgb(70, 0, 0, 0);
            }
            else
            {
                p.Bg1 = Hex("#FFFFFF"); p.Bg2 = Hex("#EEF1F8");
                p.Text = Hex("#111827"); p.SubText = Hex("#5B6478");
                p.Track = Color.FromArgb(46, 17, 24, 39); p.Sep = Color.FromArgb(30, 17, 24, 39);
                p.Warn = Hex("#92400E"); p.Crit = Hex("#BE123C");   // 4.5:1 or more on every light background
                p.CardBg1 = Hex("#FFFFFF"); p.CardBg2 = Hex("#F3F5FB");
                p.Shadow = Hex("#1E293B"); p.BorderAlpha = 235; p.Edge = Color.FromArgb(34, 15, 23, 42);
            }
            return p;
        }

        public static Color Hex(string h)
        {
            h = h.TrimStart('#');
            return Color.FromArgb(int.Parse(h.Substring(0, 2), NumberStyles.HexNumber),
                                  int.Parse(h.Substring(2, 2), NumberStyles.HexNumber),
                                  int.Parse(h.Substring(4, 2), NumberStyles.HexNumber));
        }
    }

    public static class Painter
    {
        public const float PillH = 34, PillPad = 8, CellW = 44;
        public const float CardW = 300, CardMargin = 14;

        // Metric colours: bar gradient + label tint for dark and light taskbars.
        internal static readonly Color CpuA = Palette.Hex("#38BDF8"), CpuB = Palette.Hex("#818CF8");
        internal static readonly Color RamA = Palette.Hex("#C084FC"), RamB = Palette.Hex("#F472B6");
        internal static readonly Color GpuA = Palette.Hex("#4ADE80"), GpuB = Palette.Hex("#2DD4BF");
        internal static readonly Color DskA = Palette.Hex("#FBBF24"), DskB = Palette.Hex("#F97316");
        internal static readonly Color NetA = Palette.Hex("#60A5FA"), NetB = Palette.Hex("#A78BFA");

        static readonly double[] RampT = { 35, 50, 65, 75, 85, 95 };
        static readonly Color[] RampC = { Palette.Hex("#22D3EE"), Palette.Hex("#34D399"), Palette.Hex("#A3E635"),
                                          Palette.Hex("#FACC15"), Palette.Hex("#FB923C"), Palette.Hex("#F43F5E") };

        public static Color TempColor(double c)
        {
            if (double.IsNaN(c)) return Palette.Hex("#94A3B8");
            if (c <= RampT[0]) return RampC[0];
            for (int i = 1; i < RampT.Length; i++)
                if (c <= RampT[i]) return Lerp(RampC[i - 1], RampC[i], (c - RampT[i - 1]) / (RampT[i] - RampT[i - 1]));
            return RampC[RampC.Length - 1];
        }

        static Color Lerp(Color a, Color b, double t)
        {
            t = Math.Max(0, Math.Min(1, t));
            return Color.FromArgb((int)(a.A + (b.A - a.A) * t), (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }
        static Color Darken(Color c, double t) { return Lerp(c, Color.FromArgb(c.A, 0, 0, 0), t); }
        static Color Lighten(Color c, double t) { return Lerp(c, Color.FromArgb(c.A, 255, 255, 255), t); }
        static Color Alpha(Color c, int a) { return Color.FromArgb(Math.Max(0, Math.Min(255, a)), c.R, c.G, c.B); }

        // Light-mode text colours are darkened by half, so every one reaches 4.5:1 on the light backgrounds.
        internal static Color LabelTint(Color accent, Palette p) { return p.Dark ? Lighten(accent, 0.2) : Darken(accent, 0.5); }
        internal static Color PinnedColor(Palette p) { return p.Dark ? Palette.Hex("#A78BFA") : Palette.Hex("#6D28D9"); }
        static Color BarTone(Color c, Palette p) { return p.Dark ? c : Darken(c, 0.1); }

        static Color ValueColor(int level, Palette p) { return level == 2 ? p.Crit : level == 1 ? p.Warn : p.Text; }

        public static bool ShowsGpu(HwSnapshot s, Fourth f)
        {
            if (f == Fourth.Gpu) return true;
            if (f == Fourth.Disk) return false;
            return s.Gpus.Length > 0 && !double.IsNaN(s.GpuPercent);
        }

        // What a screen reader announces for the pill: "TEMP 61°, CPU 23%, RAM 58%, DISK unknown".
        public static string AccessibleText(Cell[] cells)
        {
            List<string> parts = new List<string>();
            foreach (Cell c in cells) parts.Add(c.Label + " " + (c.Value == Fmt.Dash ? "unknown" : c.Spoken ?? c.Value));
            return string.Join(", ", parts.ToArray());
        }

        public static Cell[] Cells(HwSnapshot s, bool fahrenheit, Fourth fourth, bool download, Palette p)
        {
            List<Cell> cells = new List<Cell>();

            // 1. temperature: CPU, else GPU (labelled so) when the machine exposes no CPU sensor.
            Cell t = new Cell();
            double tc = s.CpuTempC; t.Label = "TEMP";
            if (double.IsNaN(tc) && !double.IsNaN(s.GpuTempC)) { tc = s.GpuTempC; t.Label = "GPU°"; }
            Color tcol = TempColor(tc);
            t.Value = Fmt.Temp(tc, fahrenheit);
            t.Frac = double.IsNaN(tc) ? double.NaN : (tc - 30) / 70.0;
            t.Bar1 = BarTone(TempColor(35), p); t.Bar2 = BarTone(tcol, p);
            t.Level = Fmt.TempLevel(tc, fahrenheit);
            t.LabelColor = LabelTint(tcol, p);
            t.ValueColor = t.Level > 0 ? ValueColor(t.Level, p) : (p.Dark ? Lighten(tcol, 0.45) : Darken(tcol, 0.5));
            cells.Add(t);

            cells.Add(Metric("CPU", s.CpuPercent, 75, 90, CpuA, CpuB, p));
            cells.Add(Metric("RAM", s.RamPercent, 80, 90, RamA, RamB, p));
            if (ShowsGpu(s, fourth)) cells.Add(Metric("GPU", s.GpuPercent, 85, 95, GpuA, GpuB, p));
            else cells.Add(Metric("DISK", s.DiskPercent, 80, 95, DskA, DskB, p));
            if (download) cells.Add(Down(s.NetDownBps, p));
            return cells.ToArray();
        }

        // Live download rate on a log scale up to 1 Gbit/s; never amber or red, since a fast download is good news.
        public static double NetFrac(double bps)
        {
            return double.IsNaN(bps) ? double.NaN : Math.Log10(1 + Math.Max(0, bps)) / Math.Log10(1 + 125e6);
        }

        static Cell Down(double bps, Palette p)
        {
            Cell c = new Cell();
            c.Label = "DOWN"; c.Value = Fmt.RateShort(bps); c.Spoken = Fmt.Rate(bps);
            c.Frac = NetFrac(bps);
            c.Bar1 = BarTone(NetA, p); c.Bar2 = BarTone(NetB, p);
            c.LabelColor = LabelTint(Lerp(NetA, NetB, 0.4), p);
            c.ValueColor = p.Text;
            return c;
        }

        static Cell Metric(string label, double v, double warn, double crit, Color a, Color b, Palette p)
        {
            Cell c = new Cell();
            c.Label = label; c.Value = Fmt.Pct(v);
            c.Frac = double.IsNaN(v) ? double.NaN : v / 100.0;
            c.Level = Fmt.Level(v, warn, crit); c.Bar1 = BarTone(c.Level == 2 ? Palette.Hex("#F59E0B") : a, p); c.Bar2 = BarTone(c.Level == 2 ? Palette.Hex("#F43F5E") : c.Level == 1 ? Palette.Hex("#F59E0B") : b, p);
            c.LabelColor = LabelTint(Lerp(a, b, 0.4), p);
            c.ValueColor = ValueColor(c.Level, p);
            return c;
        }

        // ------------------------------------------------------------------ shapes and text
        static GraphicsPath Round(RectangleF r, float rad)
        {
            GraphicsPath gp = new GraphicsPath();
            rad = Math.Max(0.01f, Math.Min(rad, Math.Min(r.Width, r.Height) / 2));
            float d = rad * 2;
            gp.AddArc(r.X, r.Y, d, d, 180, 90);
            gp.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            gp.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            gp.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            gp.CloseFigure();
            return gp;
        }

        static string fontName;
        static string FontName
        {
            get
            {
                if (fontName != null) return fontName;
                fontName = "Segoe UI";
                try
                {
                    using (InstalledFontCollection fc = new InstalledFontCollection())
                        foreach (FontFamily ff in fc.Families)
                            if (ff.Name == "Segoe UI Variable Display") { fontName = ff.Name; break; }
                }
                catch { }
                return fontName;
            }
        }

        // Fonts and string formats are made once and kept: fonts are sized in DIP and scaled by the Graphics
        // transform, so a DPI or theme change needs no new ones. Drawing happens on the UI thread only.
        internal static int FontsMade, FormatsMade;
        static readonly Dictionary<string, Font> fonts = new Dictionary<string, Font>();
        static Font F(float px, FontStyle st)
        {
            string key = px.ToString("R", CultureInfo.InvariantCulture) + "|" + (int)st;
            Font f;
            if (!fonts.TryGetValue(key, out f)) { f = new Font(FontName, px, st, GraphicsUnit.Pixel); fonts[key] = f; FontsMade++; }
            return f;
        }

        // Everything the pill's image depends on: the same key draws the same pixels, so the repaint can be skipped.
        internal static string PillKey(Cell[] cells, Palette p, float scale, bool hover)
        {
            System.Text.StringBuilder k = new System.Text.StringBuilder();
            k.Append(p.Dark ? 'D' : 'L').Append('|').Append(scale.ToString("R", CultureInfo.InvariantCulture)).Append('|').Append(hover ? 'H' : '-');
            foreach (Cell c in cells)
                k.Append('|').Append(c.Label).Append('/').Append(c.Value).Append('/').Append(double.IsNaN(c.Frac) ? "-" : c.Frac.ToString("0.000", CultureInfo.InvariantCulture))
                 .Append('/').Append(c.Level).Append('/').Append(c.Bar1.ToArgb()).Append('/').Append(c.Bar2.ToArgb())
                 .Append('/').Append(c.LabelColor.ToArgb()).Append('/').Append(c.ValueColor.ToArgb());
            return k.ToString();
        }

        static readonly StringFormat[] formats = new StringFormat[3];   // by StringAlignment: Near, Center, Far
        static void Text(Graphics g, string s, Font f, Color c, RectangleF r, StringAlignment h)
        {
            StringFormat sf = formats[(int)h];
            if (sf == null)
            {
                sf = new StringFormat(StringFormat.GenericTypographic);
                sf.Alignment = h; sf.LineAlignment = StringAlignment.Center;
                sf.Trimming = StringTrimming.EllipsisCharacter;
                sf.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces;
                formats[(int)h] = sf; FormatsMade++;
            }
            using (SolidBrush b = new SolidBrush(c)) g.DrawString(s, f, b, r, sf);
        }

        static void Bar(Graphics g, RectangleF r, double frac, Color a, Color b, Palette p)
        {
            using (GraphicsPath track = Round(r, r.Height / 2))
            using (SolidBrush tb = new SolidBrush(double.IsNaN(frac) ? Alpha(p.Track, p.Track.A / 2) : p.Track)) g.FillPath(tb, track);
            if (double.IsNaN(frac)) return;
            float w = (float)(r.Width * Math.Max(0, Math.Min(1, frac)));
            if (w < r.Height) w = Math.Min(r.Width, r.Height);   // a visible dot even at 0-2%
            RectangleF fr = new RectangleF(r.X, r.Y, w, r.Height);
            using (GraphicsPath fill = Round(fr, r.Height / 2))
            using (LinearGradientBrush lb = new LinearGradientBrush(new RectangleF(r.X - 0.5f, r.Y, r.Width + 1, r.Height), a, b, 0f))
                g.FillPath(lb, fill);
        }

        static Graphics Prep(Bitmap bmp, float scale)
        {
            Graphics g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.ScaleTransform(scale, scale);
            return g;
        }

        static LinearGradientBrush Rainbow(RectangleF r, int alpha)
        {
            LinearGradientBrush bb = new LinearGradientBrush(new RectangleF(r.X - 1, r.Y - 1, r.Width + 2, r.Height + 2), Color.Black, Color.White, 0f);
            ColorBlend cb = new ColorBlend(7);
            cb.Colors = new Color[] { Alpha(Palette.Hex("#22D3EE"), alpha), Alpha(Palette.Hex("#60A5FA"), alpha), Alpha(Palette.Hex("#A78BFA"), alpha), Alpha(Palette.Hex("#F472B6"), alpha),
                                      Alpha(Palette.Hex("#FB923C"), alpha), Alpha(Palette.Hex("#FACC15"), alpha), Alpha(Palette.Hex("#34D399"), alpha) };
            cb.Positions = new float[] { 0f, 0.17f, 0.34f, 0.5f, 0.67f, 0.84f, 1f };
            bb.InterpolationColors = cb;
            return bb;
        }

        public static Size PillSize(int cells, float scale)
        {
            return new Size((int)Math.Ceiling((PillPad * 2 + CellW * cells) * scale), (int)Math.Ceiling(PillH * scale));
        }

        // ------------------------------------------------------------------ the taskbar pill
        public static Bitmap Pill(Cell[] cells, Palette p, float scale, bool hover)
        {
            float W = PillPad * 2 + CellW * cells.Length, H = PillH;
            Size sz = PillSize(cells.Length, scale);
            Bitmap bmp = new Bitmap(sz.Width, sz.Height, PixelFormat.Format32bppArgb);
            using (Graphics g = Prep(bmp, scale))
            {
                RectangleF r = new RectangleF(0.75f, 0.75f, W - 1.5f, H - 1.5f);
                using (GraphicsPath path = Round(r, r.Height / 2))
                {
                    using (LinearGradientBrush bg = new LinearGradientBrush(new RectangleF(0, 0, W, H), p.Bg1, p.Bg2, 90f)) g.FillPath(bg, path);
                    if (hover) using (SolidBrush hb = new SolidBrush(p.Dark ? Color.FromArgb(18, 255, 255, 255) : Color.FromArgb(10, 30, 41, 59))) g.FillPath(hb, path);

                    // soft inner tint of each cell's accent colour, stronger when warm/critical
                    // A fresh Graphics has no clip, so ResetClip restores it; reading g.Clip would allocate a
                    // GDI+ Region on every paint that only the finalizer frees.
                    g.SetClip(path);
                    for (int i = 0; i < cells.Length; i++)
                    {
                        Cell c = cells[i];
                        int a = c.Level == 2 ? (p.Dark ? 34 : 30) : c.Level == 1 ? (p.Dark ? 22 : 18) : (p.Dark ? 10 : 6);
                        Color tint = c.Level == 2 ? p.Crit : c.Level == 1 ? p.Warn : Lerp(c.Bar1, c.Bar2, 0.5);
                        RectangleF cr = new RectangleF(PillPad + i * CellW - (i == 0 ? PillPad : 0), 0, CellW + (i == 0 || i == cells.Length - 1 ? PillPad : 0), H);
                        using (LinearGradientBrush tb = new LinearGradientBrush(new RectangleF(cr.X, -1, cr.Width, H + 2), Alpha(tint, 0), Alpha(tint, a), 90f))
                            g.FillRectangle(tb, cr);
                    }
                    g.ResetClip();

                    // glossy top highlight
                    using (Pen hl = new Pen(p.Dark ? Color.FromArgb(28, 255, 255, 255) : Color.FromArgb(180, 255, 255, 255), 1f))
                        g.DrawArc(hl, new RectangleF(r.X + 1.2f, r.Y + 1.2f, r.Height - 2.4f, r.Height - 2.4f), 200, 70);
                    using (Pen hl = new Pen(p.Dark ? Color.FromArgb(28, 255, 255, 255) : Color.FromArgb(180, 255, 255, 255), 1f))
                        g.DrawLine(hl, r.X + r.Height / 2, r.Y + 1.2f, r.Right - r.Height / 2, r.Y + 1.2f);

                    using (Pen edge = new Pen(p.Edge, 2.2f)) g.DrawPath(edge, path);
                    using (LinearGradientBrush bb = Rainbow(r, hover ? 255 : p.BorderAlpha))
                    using (Pen pen = new Pen(bb, 1.3f)) g.DrawPath(pen, path);
                }

                Font lf = F(8.6f, FontStyle.Bold), vf = F(13f, FontStyle.Bold);   // kept by F, never disposed here
                using (Pen sep = new Pen(p.Sep, 1f))
                {
                    for (int i = 0; i < cells.Length; i++)
                    {
                        Cell c = cells[i];
                        float x = PillPad + i * CellW;
                        if (i > 0) g.DrawLine(sep, x, 9.5f, x, H - 9.5f);
                        Text(g, c.Label, lf, c.LabelColor, new RectangleF(x, 3.7f, CellW, 9.4f), StringAlignment.Center);
                        bool unknown = c.Value == Fmt.Dash;
                        Text(g, unknown ? "\u2013" : c.Value, vf, unknown ? Alpha(p.SubText, 150) : c.ValueColor, new RectangleF(x - 2, unknown ? 12.6f : 11.6f, CellW + 4, 15f), StringAlignment.Center);
                        Bar(g, new RectangleF(x + 8, 27f, CellW - 16, 2.6f), c.Frac, c.Bar1, c.Bar2, p);
                    }
                }
            }
            return bmp;
        }

        // ------------------------------------------------------------------ the hover card
        internal sealed class Row
        {
            public string Title, Sub, Value; public double Frac; public Color A, B, ValueColor, Dot;
            public List<string[]> Top = new List<string[]>();   // busiest processes: { name, value }
        }

        public const float RowH = 46, TopLineH = 15, TopPad = 3;
        internal static float RowHeight(Row r) { return RowH + (r.Top.Count > 0 ? r.Top.Count * TopLineH + TopPad : 0); }

        static void AddTop(Row r, TopProc[] top, Func<double, string> fmt)
        {
            if (top == null) return;
            foreach (TopProc t in top) r.Top.Add(new string[] { t.Name, fmt(t.Value) });
        }

        // Busy rows turn their bar amber (warm) or amber-to-red (critical), matching the pill.
        static void Heat(Row r, int level)
        {
            if (level == 2) { r.A = Palette.Hex("#F59E0B"); r.B = Palette.Hex("#F43F5E"); }
            else if (level == 1) r.B = Palette.Hex("#F59E0B");
        }

        // What a screen reader announces for the card, row by row in the pill's wording style:
        // "Temperature 61°C (CPU, ACPI thermal zone CPUZ), CPU 23% (...), ..., Network 178 KB/s download (21 KB/s upload)".
        internal static string CardAccessibleText(HwSnapshot s, bool f)
        {
            List<string> parts = new List<string>();
            foreach (Row r in CardRows(s, f, Palette.For(true)))
            {
                string value = r.Value.StartsWith("↓ ") ? r.Value.Substring(2) + " download" : r.Value;
                string sub = (r.Sub ?? "").Replace("↑ ", "").Replace(" · ", ", ");
                string said = r.Title + " " + value + (sub.Length > 0 ? " (" + sub + ")" : "");
                if (r.Top.Count > 0)
                {
                    List<string> top = new List<string>();
                    foreach (string[] t in r.Top) top.Add(t[0] + " " + t[1]);
                    said += ", top: " + string.Join(", ", top.ToArray());
                }
                parts.Add(said.Replace(Fmt.Dash, "unknown"));
            }
            return string.Join(", ", parts.ToArray());
        }

        internal static List<Row> CardRows(HwSnapshot s, bool f, Palette p)
        {
            List<Row> rows = new List<Row>();
            Row r;

            r = new Row(); r.Title = "Temperature";
            r.Sub = double.IsNaN(s.CpuTempC) ? (string.IsNullOrEmpty(s.CpuTempHint) ? "No CPU sensor readable without admin" : s.CpuTempHint) : (string.IsNullOrEmpty(s.CpuTempSource) ? "CPU" : "CPU · " + s.CpuTempSource);
            r.Value = Fmt.TempLong(s.CpuTempC, f);
            r.Frac = double.IsNaN(s.CpuTempC) ? double.NaN : (s.CpuTempC - 30) / 70.0;
            r.A = TempColor(35); r.B = TempColor(s.CpuTempC); r.Dot = r.B;
            int tl = Fmt.TempLevel(s.CpuTempC, f);
            r.ValueColor = tl > 0 ? ValueColor(tl, p) : (p.Dark ? Lighten(r.B, 0.35) : Darken(r.B, 0.5));
            rows.Add(r);

            r = new Row(); r.Title = "CPU"; r.Sub = s.CpuName; r.Value = Fmt.Pct(s.CpuPercent);
            r.Frac = s.CpuPercent / 100; r.A = CpuA; r.B = CpuB; r.ValueColor = ValueColor(Fmt.Level(s.CpuPercent, 75, 90), p); Heat(r, Fmt.Level(s.CpuPercent, 75, 90));
            AddTop(r, s.TopCpu, Fmt.Pct);
            rows.Add(r);

            r = new Row(); r.Title = "Memory";
            r.Sub = double.IsNaN(s.RamUsedGB) ? "Usage unavailable" : Fmt.GB(s.RamUsedGB).Replace(" GB", "") + " of " + Fmt.GB(s.RamTotalGB) + " in use";
            r.Value = Fmt.Pct(s.RamPercent); r.Frac = s.RamPercent / 100; r.A = RamA; r.B = RamB;
            r.ValueColor = ValueColor(Fmt.Level(s.RamPercent, 80, 90), p); Heat(r, Fmt.Level(s.RamPercent, 80, 90));
            AddTop(r, s.TopRam, Fmt.Bytes);
            rows.Add(r);

            foreach (GpuInfo gi in s.Gpus)
            {
                r = new Row(); r.Title = s.Gpus.Length > 1 ? (gi.Integrated ? "GPU (integrated)" : "GPU") : "GPU";
                List<string> parts = new List<string>();
                
                if (!double.IsNaN(gi.TempC)) parts.Add(Fmt.TempLong(gi.TempC, f));
                if (!double.IsNaN(gi.MemUsedGB) && !gi.Integrated) parts.Add(Fmt.GB(gi.MemUsedGB) + " VRAM");
                parts.Add(gi.Name.Replace("NVIDIA ", "").Replace("(R)", "").Replace("(TM)", "").Replace(" with Max-Q Design", " Max-Q"));
                r.Sub = string.Join(" · ", parts.ToArray());
                r.Value = Fmt.Pct(gi.Percent); r.Frac = gi.Percent / 100; r.A = GpuA; r.B = GpuB;
                r.ValueColor = ValueColor(Fmt.Level(gi.Percent, 85, 95), p); Heat(r, Fmt.Level(gi.Percent, 85, 95));
                AddTop(r, gi.Top, Fmt.Pct);
                rows.Add(r);
            }

            // Windows has no per-process network counter without admin rights: the list is each app's data read,
            // which counts disk reads and network downloads together, so it sits under Disk and says so.
            bool reads = s.TopRead != null && s.TopRead.Length > 0;
            r = new Row(); r.Title = "Disk"; r.Sub = reads ? "Busiest drive · apps by data read, incl. network" : "Busiest drive, active time"; r.Value = Fmt.Pct(s.DiskPercent);
            AddTop(r, s.TopRead, Fmt.Rate);
            r.Frac = s.DiskPercent / 100; r.A = DskA; r.B = DskB; r.ValueColor = ValueColor(Fmt.Level(s.DiskPercent, 80, 95), p); Heat(r, Fmt.Level(s.DiskPercent, 80, 95));
            rows.Add(r);

            r = new Row(); r.Title = "Network"; r.Sub = "↑ " + Fmt.Rate(s.NetUpBps) + " upload";
            r.Value = "↓ " + Fmt.Rate(s.NetDownBps);
            r.Frac = NetFrac(s.NetDownBps);   // log scale to 1 Gbit/s
            r.A = NetA; r.B = NetB; r.ValueColor = p.Text;
            rows.Add(r);
            return rows;
        }

        public static Bitmap Card(HwSnapshot s, bool fahrenheit, Palette p, float scale, bool pinned)
        {
            List<Row> rows = CardRows(s, fahrenheit, p);
            const float HeadH = 40, FootH = 28, M = CardMargin;
            float rowsH = 0;
            foreach (Row r0 in rows) rowsH += RowHeight(r0);
            float W = CardW + M * 2, H = HeadH + rowsH + FootH + M * 2;
            Bitmap bmp = new Bitmap((int)Math.Ceiling(W * scale), (int)Math.Ceiling(H * scale), PixelFormat.Format32bppArgb);
            using (Graphics g = Prep(bmp, scale))
            {
                RectangleF card = new RectangleF(M, M, CardW, H - M * 2);
                // shadow: stacked translucent rounded rects
                for (int i = 10; i >= 1; i--)
                {
                    RectangleF sr = new RectangleF(card.X - i + 0.5f, card.Y - i + 3, card.Width + i * 2 - 1, card.Height + i * 2 - 3);
                    using (GraphicsPath sp = Round(sr, 14 + i))
                    using (SolidBrush sb = new SolidBrush(Alpha(p.Shadow, (p.Dark ? 14 : 22) - (int)(i * (p.Dark ? 0.9 : 1.6)))))
                        g.FillPath(sb, sp);
                }
                using (GraphicsPath cp = Round(card, 14))
                {
                    using (LinearGradientBrush bg = new LinearGradientBrush(card, p.CardBg1, p.CardBg2, 90f)) g.FillPath(bg, cp);
                    using (Pen edge = new Pen(p.Edge, 2.2f)) g.DrawPath(edge, cp);
                    using (LinearGradientBrush bb = Rainbow(card, p.Dark ? 160 : 200))
                    using (Pen pen = new Pen(bb, 1.3f)) g.DrawPath(pen, cp);
                }

                float x = card.X + 16, w = card.Width - 32, y = card.Y + 10;
                Font hf = F(14f, FontStyle.Bold), tf = F(12.5f, FontStyle.Bold), sf = F(11f, FontStyle.Regular),
                     vf = F(15f, FontStyle.Bold), ff = F(10.5f, FontStyle.Regular);   // kept by F, never disposed here
                {
                    // header: rainbow dot + title + pin state
                    using (LinearGradientBrush db = new LinearGradientBrush(new RectangleF(x, y + 9, 12, 12), Palette.Hex("#22D3EE"), Palette.Hex("#F472B6"), 45f))
                        g.FillEllipse(db, x, y + 9, 12, 12);
                    Text(g, "Hardware", hf, p.Text, new RectangleF(x + 20, y + 3, 150, 24), StringAlignment.Near);
                    Text(g, pinned ? "Pinned" : "Live", ff, pinned ? PinnedColor(p) : p.SubText, new RectangleF(x + w - 120, y + 3, 120, 24), StringAlignment.Far);
                    y = card.Y + HeadH;

                    foreach (Row r in rows)
                    {
                        if (r.Dot.A > 0) using (SolidBrush sd = new SolidBrush(r.Dot)) g.FillEllipse(sd, x, y + 8, 9, 9);
                        else using (LinearGradientBrush ib = new LinearGradientBrush(new RectangleF(x, y + 7, 10, 10), r.A, r.B, 45f))
                            g.FillEllipse(ib, x, y + 8, 9, 9);
                        Text(g, r.Value, vf, r.Value == Fmt.Dash ? p.SubText : r.ValueColor, new RectangleF(x + w - 110, y + 1, 110, 22), StringAlignment.Far);
                        float valueW = g.MeasureString(r.Value, vf).Width;
                        Text(g, r.Title, tf, p.Text, new RectangleF(x + 16, y + 3, w - 16 - valueW - 6, 18), StringAlignment.Near);
                        Text(g, r.Sub ?? "", sf, p.SubText, new RectangleF(x + 16, y + 21, w - 16, 15), StringAlignment.Near);
                        Bar(g, new RectangleF(x + 16, y + 38, w - 16, 3.5f), r.Frac, r.A, r.B, p);
                        float ty = y + RowH - 2;
                        foreach (string[] t in r.Top)
                        {
                            float tvW = g.MeasureString(t[1], ff).Width;
                            Text(g, t[1], ff, p.Text, new RectangleF(x + w - 90, ty, 90, TopLineH), StringAlignment.Far);
                            Text(g, "• " + t[0], ff, p.SubText, new RectangleF(x + 22, ty, w - 22 - tvW - 8, TopLineH), StringAlignment.Near);
                            ty += TopLineH;
                        }
                        y += RowHeight(r);
                    }
                    Text(g, pinned ? "Click to unpin · right-click for options" : "Click to pin · right-click for options",
                         ff, p.SubText, new RectangleF(x, y + 2, w, FootH - 6), StringAlignment.Near);
                }
            }
            return bmp;
        }
    }
}
