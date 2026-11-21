// HW Widget for Windows - colours for the right-click menu in dark mode.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace HwWidget
{
    internal sealed class DarkMenuColors : ProfessionalColorTable
    {
        static readonly Color Bg = Palette.Hex("#1E1E2E"), Hi = Palette.Hex("#34345A"), Line = Palette.Hex("#3A3A55");
        public override Color ToolStripDropDownBackground { get { return Bg; } }
        public override Color ImageMarginGradientBegin { get { return Bg; } }
        public override Color ImageMarginGradientMiddle { get { return Bg; } }
        public override Color ImageMarginGradientEnd { get { return Bg; } }
        public override Color MenuBorder { get { return Line; } }
        public override Color MenuItemBorder { get { return Hi; } }
        public override Color MenuItemSelected { get { return Hi; } }
        public override Color MenuItemSelectedGradientBegin { get { return Hi; } }
        public override Color MenuItemSelectedGradientEnd { get { return Hi; } }
        public override Color MenuItemPressedGradientBegin { get { return Hi; } }
        public override Color MenuItemPressedGradientEnd { get { return Hi; } }
        public override Color SeparatorDark { get { return Line; } }
        public override Color SeparatorLight { get { return Line; } }
        public override Color CheckBackground { get { return Palette.Hex("#4C4C80"); } }
        public override Color CheckSelectedBackground { get { return Palette.Hex("#5B5B99"); } }
        public override Color CheckPressedBackground { get { return Palette.Hex("#5B5B99"); } }
    }

    internal sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer() : base(new DarkMenuColors()) { RoundedEdges = false; }
        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e) { e.ArrowColor = Color.White; base.OnRenderArrow(e); }
        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            Rectangle r = e.ImageRectangle;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (SolidBrush b = new SolidBrush(Palette.Hex("#4C4C80"))) e.Graphics.FillRectangle(b, r);
            using (Pen p = new Pen(Color.White, Math.Max(1.5f, r.Height / 9f)))
                e.Graphics.DrawLines(p, new PointF[] { new PointF(r.Left + r.Width * 0.25f, r.Top + r.Height * 0.52f), new PointF(r.Left + r.Width * 0.43f, r.Top + r.Height * 0.70f), new PointF(r.Left + r.Width * 0.76f, r.Top + r.Height * 0.32f) });
        }
    }
}
