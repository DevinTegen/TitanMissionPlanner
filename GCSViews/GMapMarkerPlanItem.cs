using GMap.NET;
using GMap.NET.WindowsForms;
using System;
using System.Drawing;

namespace MissionPlanner.GCSViews
{
    /// <summary>QGC-style mission marker: numbered circle, blue altitude bubble, loiter circle to scale</summary>
    public class GMapMarkerPlanItem : GMapMarker
    {
        public int ItemIndex; // -1 = home
        public string Label;
        public string AltText;
        public ushort Command;
        public bool Selected;
        public double LoiterRadiusMetres;

        static Font numberFont;
        static Font altFont;
        const int R = 12;

        public GMapMarkerPlanItem(PointLatLng p) : base(p)
        {
            Size = new Size(R * 2, R * 2);
            Offset = new Point(-R, -R);
        }

        Color FillColour
        {
            get
            {
                if (ItemIndex < 0)
                    return Color.FromArgb(60, 60, 60);
                switch ((MAVLink.MAV_CMD) Command)
                {
                    case MAVLink.MAV_CMD.TAKEOFF:
                    case MAVLink.MAV_CMD.VTOL_TAKEOFF:
                        return Color.FromArgb(30, 160, 70);
                    case MAVLink.MAV_CMD.LAND:
                    case MAVLink.MAV_CMD.VTOL_LAND:
                        return Color.FromArgb(200, 50, 40);
                    case MAVLink.MAV_CMD.LOITER_TURNS:
                    case MAVLink.MAV_CMD.LOITER_TIME:
                    case MAVLink.MAV_CMD.LOITER_UNLIM:
                    case MAVLink.MAV_CMD.LOITER_TO_ALT:
                        return Color.FromArgb(130, 70, 200);
                    default:
                        return Color.FromArgb(235, 130, 20);
                }
            }
        }

        public override void OnRender(IGraphics g)
        {
            if (numberFont == null)
            {
                numberFont = new Font("Arial", 9, FontStyle.Bold);
                altFont = new Font("Arial", 8, FontStyle.Bold);
            }

            float cx = LocalPosition.X + R;
            float cy = LocalPosition.Y + R;

            // loiter circle to scale
            if (LoiterRadiusMetres > 0 && Overlay?.Control != null)
            {
                var edge = Overlay.Control.FromLatLngToLocal(
                    new PointLatLngAlt(Position.Lat, Position.Lng).newpos(90, LoiterRadiusMetres));
                var centre = Overlay.Control.FromLatLngToLocal(Position);
                float pr = Math.Abs(edge.X - centre.X);
                if (pr > 4)
                    using (var pen = new Pen(Color.FromArgb(200, 170, 120, 255), 2) {DashStyle = System.Drawing.Drawing2D.DashStyle.Dash})
                        g.DrawEllipse(pen, cx - pr, cy - pr, pr * 2, pr * 2);
            }

            float r = Selected ? R + 3 : R;
            using (var b = new SolidBrush(FillColour))
                g.FillEllipse(b, cx - r, cy - r, r * 2, r * 2);
            using (var pen = new Pen(Selected ? Color.FromArgb(255, 220, 0) : Color.White, Selected ? 3 : 1.5f))
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
    }
}
