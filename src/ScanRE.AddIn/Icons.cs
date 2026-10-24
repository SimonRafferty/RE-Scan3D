// Toolbar icons, drawn at start-up (no image files to ship or lose). SolidWorks wants one PNG strip per
// size (20, 32, 40, 64, 96, 128 px high) with the icons side by side, in command order.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;

namespace ScanRE.AddIn
{
    public static class Icons
    {
        public static readonly int[] Sizes = { 20, 32, 40, 64, 96, 128 };

        public sealed class Glyph
        {
            public string Text;
            public Color Colour;
            public Glyph(string t, Color c) { Text = t; Colour = c; }
        }

        /// <summary>Write the strips for `glyphs`; returns their paths, smallest first.</summary>
        public static string[] Strips(string tag, Glyph[] glyphs)
        {
            var dir = Path.Combine(Log.Folder, "icons");
            Directory.CreateDirectory(dir);
            var paths = new string[Sizes.Length];
            for (int k = 0; k < Sizes.Length; k++)
            {
                int s = Sizes[k];
                paths[k] = Path.Combine(dir, tag + "_" + s + ".png");
                using (var bmp = new Bitmap(s * glyphs.Length, s, PixelFormat.Format32bppArgb))
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    g.Clear(Color.Transparent);
                    for (int i = 0; i < glyphs.Length; i++) Draw(g, i * s, s, glyphs[i]);
                    try { bmp.Save(paths[k], ImageFormat.Png); }
                    catch (Exception) { /* in use by SolidWorks from an earlier session: keep the old file */ }
                }
            }
            return paths;
        }

        private static void Draw(Graphics g, int x0, int s, Glyph glyph)
        {
            float pad = s * 0.08f, r = s * 0.22f;
            var rect = new RectangleF(x0 + pad, pad, s - 2 * pad, s - 2 * pad);
            using (var path = Rounded(rect, r))
            using (var fill = new LinearGradientBrush(rect, Lighter(glyph.Colour), glyph.Colour, 90f))
            using (var pen = new Pen(Darker(glyph.Colour), Math.Max(1f, s / 24f)))
            {
                g.FillPath(fill, path);
                g.DrawPath(pen, path);
            }
            float fs = s * (glyph.Text.Length > 2 ? 0.30f : 0.40f);
            using (var font = new Font("Segoe UI", fs, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString(glyph.Text, font, Brushes.White, rect, sf);
        }

        private static GraphicsPath Rounded(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            float d = 2 * rad;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        private static Color Lighter(Color c) { return Color.FromArgb(c.A, Math.Min(255, c.R + 50), Math.Min(255, c.G + 50), Math.Min(255, c.B + 50)); }
        private static Color Darker(Color c) { return Color.FromArgb(c.A, c.R * 2 / 3, c.G * 2 / 3, c.B * 2 / 3); }
    }
}
