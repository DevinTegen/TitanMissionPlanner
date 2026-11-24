using GMap.NET;
using GMap.NET.WindowsForms;
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace MissionPlanner.Maps
{
    /// <summary>
    /// Terrain altitude shown as a semi-transparent colour heat map over the map area it was sampled
    /// for, with a colour scale legend in the bottom-left corner.
    /// </summary>
    public class GMapMarkerTerrainHeat : GMapMarker
    {
        readonly Bitmap image;
        readonly RectLatLng rect;
        readonly double minMetres;
        readonly double maxMetres;
        readonly double unitMultiplier;
        readonly string unit;

        /// <summary>opacity of the heat map, 0..255</summary>
        public const int Alpha = 120;

        /// <param name="heights">heights in metres AMSL, [column, row] with row 0 at the top (north); NaN = unknown</param>
        /// <param name="rect">the area the samples cover</param>
        /// <param name="unitMultiplier">metres to display units (CurrentState.multiplieralt)</param>
        /// <param name="unit">display unit name, e.g. "m" or "ft"</param>
        public GMapMarkerTerrainHeat(double[,] heights, RectLatLng rect, double unitMultiplier, string unit)
            : base(rect.LocationMiddle)
        {
            IsHitTestVisible = false;
            this.rect = rect;
            this.unitMultiplier = unitMultiplier <= 0 ? 1 : unitMultiplier;
            this.unit = string.IsNullOrEmpty(unit) ? "m" : unit;

            int w = heights.GetLength(0), h = heights.GetLength(1);

            minMetres = double.MaxValue;
            maxMetres = double.MinValue;
            foreach (var v in heights)
            {
                if (double.IsNaN(v))
                    continue;
                minMetres = Math.Min(minMetres, v);
                maxMetres = Math.Max(maxMetres, v);
            }

            if (minMetres > maxMetres)
            {
                minMetres = maxMetres = 0;
            }

            // a flat area still gets a visible colour range
            double range = Math.Max(maxMetres - minMetres, 1);

            image = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            var data = image.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            int stride = data.Stride / 4;
            var pixels = new int[stride * h];
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var v = heights[x, y];
                pixels[y * stride + x] = double.IsNaN(v)
                    ? Color.Transparent.ToArgb()
                    : ColourFor((v - minMetres) / range, Alpha).ToArgb();
            }

            Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
            image.UnlockBits(data);
        }

        public double MinMetres => minMetres;
        public double MaxMetres => maxMetres;

        /// <summary>
        /// "Turbo"-like ramp: low = blue, then cyan, green, yellow, orange, high = red.
        /// </summary>
        public static Color ColourFor(double frac, int alpha = 255)
        {
            frac = Math.Max(0, Math.Min(1, frac));
            // control points of the ramp
            double[] stops = {0.0, 0.2, 0.4, 0.6, 0.8, 1.0};
            int[,] rgb =
            {
                {48, 18, 160}, // deep blue
                {30, 140, 255}, // blue/cyan
                {30, 210, 140}, // green
                {200, 230, 40}, // yellow
                {250, 140, 20}, // orange
                {200, 20, 20} // red
            };
            int i = 0;
            while (i < stops.Length - 2 && frac > stops[i + 1])
                i++;
            double t = (frac - stops[i]) / (stops[i + 1] - stops[i]);
            int r = (int)(rgb[i, 0] + (rgb[i + 1, 0] - rgb[i, 0]) * t);
            int g = (int)(rgb[i, 1] + (rgb[i + 1, 1] - rgb[i, 1]) * t);
            int b = (int)(rgb[i, 2] + (rgb[i + 1, 2] - rgb[i, 2]) * t);
            return Color.FromArgb(alpha, r, g, b);
        }

        public override void OnRender(IGraphics g)
        {
            base.OnRender(g);

            var tl = Overlay.Control.FromLatLngToLocal(rect.LocationTopLeft);
            var br = Overlay.Control.FromLatLngToLocal(rect.LocationRightBottom);

            var old = g.Transform;
            g.ResetTransform();
            g.CompositingMode = CompositingMode.SourceOver;
            g.InterpolationMode = InterpolationMode.Bilinear;

            g.DrawImage(image, tl.X, tl.Y, br.X - tl.X, br.Y - tl.Y);

            DrawLegend(g);

            g.Transform = old;
        }

        void DrawLegend(IGraphics g)
        {
            const int barWidth = 220, barHeight = 14, pad = 6;
            int left = 10;
            int top = Overlay.Control.Height - 70;
            if (top < 10)
                top = 10;

            using (var bg = new SolidBrush(Color.FromArgb(170, 0, 0, 0)))
                g.FillRectangle(bg, left - pad, top - 18 - pad, barWidth + 2 * pad, barHeight + 18 + 2 * pad + 4);

            for (int i = 0; i < barWidth; i++)
            {
                using (var p = new Pen(ColourFor(i / (double)(barWidth - 1)), 1))
                    g.DrawLine(p, left + i, top, left + i, top + barHeight);
            }

            using (var pen = new Pen(Color.White, 1))
                g.DrawRectangle(pen, left, top, barWidth, barHeight);

            using (var font = new Font("Arial", 8))
            using (var white = new SolidBrush(Color.White))
            {
                g.DrawString("Terrain altitude (AMSL)", font, white, left, top - 17);
                var lo = (minMetres * unitMultiplier).ToString("0") + " " + unit;
                var hi = (maxMetres * unitMultiplier).ToString("0") + " " + unit;
                g.DrawString(lo, font, white, left, top + barHeight + 2);
                var hiSize = g.MeasureString(hi, font);
                g.DrawString(hi, font, white, left + barWidth - hiSize.Width, top + barHeight + 2);
            }
        }

        public override void Dispose()
        {
            image?.Dispose();
            base.Dispose();
        }
    }
}
