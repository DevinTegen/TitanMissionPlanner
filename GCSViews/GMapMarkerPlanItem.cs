using GMap.NET;
using GMap.NET.WindowsForms;
using MissionPlanner.Utilities;
using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace MissionPlanner.GCSViews
{
    /// <summary>
    /// QGC-style mission marker: small orange numbered circle (green with a halo when selected), loiter
    /// circle to scale with direction arrows, and a label with the altitude (and loiter radius).
    /// </summary>
    public class GMapMarkerPlanItem : GMapMarker
    {
        public int ItemIndex; // -1 = home
        public string Label;
        public string AltText;
        public ushort Command;
        public bool Selected;
        public double LoiterRadiusMetres;
        public bool LoiterCounterClockwise;

        static Font numberFont;
        static Font altFont;
        const int R = 11;

        public static readonly Color Orange = Color.FromArgb(255, 160, 20);
        public static readonly Color SelectedGreen = Color.FromArgb(20, 150, 40);

        public GMapMarkerPlanItem(PointLatLng p) : base(p)
        {
            Size = new Size(R * 2, R * 2);
            Offset = new Point(-R, -R);
        }

        public override void OnRender(IGraphics g)
        {
            if (numberFont == null)
            {
                numberFont = new Font("Arial", 8.5f, FontStyle.Bold);
                altFont = new Font("Arial", 8, FontStyle.Bold);
            }

            float cx = LocalPosition.X + R;
            float cy = LocalPosition.Y + R;

            // loiter circle to scale, with direction arrows top and bottom
            if (LoiterRadiusMetres > 0 && Overlay?.Control != null)
            {
                var edge = Overlay.Control.FromLatLngToLocal(
                    new PointLatLngAlt(Position.Lat, Position.Lng).newpos(90, LoiterRadiusMetres));
                var centre = Overlay.Control.FromLatLngToLocal(Position);
                float pr = Math.Abs(edge.X - centre.X);
                if (pr > 6)
                {
                    using (var pen = new Pen(Orange, 3))
                        g.DrawEllipse(pen, cx - pr, cy - pr, pr * 2, pr * 2);
                    using (var b = new SolidBrush(Orange))
                    {
                        // clockwise: moving right along the top, left along the bottom
                        int dir = LoiterCounterClockwise ? -1 : 1;
                        g.FillPolygon(b, Arrow(cx, cy - pr, dir));
                        g.FillPolygon(b, Arrow(cx, cy + pr, -dir));
                    }
                }
            }

            float r = Selected ? R + 4 : R;
            if (Selected)
            {
                using (var halo = new SolidBrush(Color.FromArgb(90, 255, 255, 255)))
                    g.FillEllipse(halo, cx - r - 9, cy - r - 9, (r + 9) * 2, (r + 9) * 2);
                using (var ring = new Pen(Color.FromArgb(200, 255, 255, 255), 1.5f))
                    g.DrawEllipse(ring, cx - r - 9, cy - r - 9, (r + 9) * 2, (r + 9) * 2);
            }

            using (var b = new SolidBrush(Selected ? SelectedGreen :
                       ItemIndex < 0 ? Color.FromArgb(230, 140, 10) : Orange))
                g.FillEllipse(b, cx - r, cy - r, r * 2, r * 2);
            using (var pen = new Pen(Color.FromArgb(220, 255, 255, 255), 1))
                g.DrawEllipse(pen, cx - r, cy - r, r * 2, r * 2);

            var label = Label ?? "";
            var ls = g.MeasureString(label, numberFont);
            g.DrawString(label, numberFont, Brushes.White, cx - ls.Width / 2, cy - ls.Height / 2);

            if (!string.IsNullOrEmpty(AltText))
            {
                var s = g.MeasureString(AltText, altFont);
                float h = s.Height + 2, w = s.Width + 6;
                float x = cx - w / 2, y = cy + r + 3;
                using (var bg = new SolidBrush(Color.FromArgb(225, 25, 105, 215)))
                {
                    g.FillEllipse(bg, x - h / 2, y, h, h);
                    g.FillEllipse(bg, x + w - h / 2, y, h, h);
                    g.FillRectangle(bg, x, y, w, h);
                }

                g.DrawString(AltText, altFont, Brushes.White, x + 3, y + 1);
            }
        }

        static PointF[] Arrow(float x, float y, int dir)
        {
            const float s = 9;
            return new[]
            {
                new PointF(x + dir * s, y), new PointF(x - dir * s * 0.6f, y - s * 0.8f),
                new PointF(x - dir * s * 0.6f, y + s * 0.8f)
            };
        }
    }

    /// <summary>white chevron in the middle of a mission leg showing the direction of flight</summary>
    public class GMapMarkerLegArrow : GMapMarker
    {
        readonly PointLatLng from;
        readonly PointLatLng to;

        public GMapMarkerLegArrow(PointLatLng from, PointLatLng to)
            : base(new PointLatLng((from.Lat + to.Lat) / 2, (from.Lng + to.Lng) / 2))
        {
            this.from = from;
            this.to = to;
            IsHitTestVisible = false;
            Size = new Size(1, 1);
        }

        public override void OnRender(IGraphics g)
        {
            if (Overlay?.Control == null)
                return;
            var a = Overlay.Control.FromLatLngToLocal(from);
            var b = Overlay.Control.FromLatLngToLocal(to);
            float dx = b.X - a.X, dy = b.Y - a.Y;
            var len = (float) Math.Sqrt(dx * dx + dy * dy);
            if (len < 60)
                return;
            dx /= len;
            dy /= len;
            float mx = (a.X + b.X) / 2f, my = (a.Y + b.Y) / 2f;
            // a little "<" pointing along the leg
            const float s = 14;
            var p1 = new PointF(mx - dx * s - dy * s * 0.7f, my - dy * s + dx * s * 0.7f);
            var p2 = new PointF(mx - dx * s + dy * s * 0.7f, my - dy * s - dx * s * 0.7f);
            var tip = new PointF(mx, my);
            var old = g.Transform;
            g.ResetTransform();
            using (var pen = new Pen(Color.White, 2.5f))
            {
                g.DrawLine(pen, p1, tip);
                g.DrawLine(pen, p2, tip);
            }

            g.Transform = old;
        }
    }

    /// <summary>QGC's "+" handle in the middle of the leg into the selected item: click to insert a waypoint</summary>
    public class GMapMarkerInsertHandle : GMapMarker
    {
        public int InsertAt;
        const int R = 12;

        public GMapMarkerInsertHandle(PointLatLng p, int insertAt) : base(p)
        {
            InsertAt = insertAt;
            Size = new Size(R * 2, R * 2);
            Offset = new Point(-R, -R);
        }

        public override void OnRender(IGraphics g)
        {
            float cx = LocalPosition.X + R, cy = LocalPosition.Y + R;
            using (var bg = new SolidBrush(Color.FromArgb(120, 255, 255, 255)))
                g.FillEllipse(bg, cx - R, cy - R, R * 2, R * 2);
            using (var pen = new Pen(Color.White, 1.5f))
            {
                g.DrawEllipse(pen, cx - R, cy - R, R * 2, R * 2);
                g.DrawLine(pen, cx - 7, cy, cx + 7, cy);
                g.DrawLine(pen, cx, cy - 7, cx, cy + 7);
            }
        }
    }
}
