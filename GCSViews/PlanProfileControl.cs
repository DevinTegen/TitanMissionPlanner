using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace MissionPlanner.GCSViews
{
    /// <summary>
    /// QGC-style terrain profile: terrain filled in brown, the planned flight path above it in blue
    /// (red where it is closer to the ground than the minimum clearance), numbered waypoints, and a
    /// hover read-out. Clicking selects the nearest waypoint. Heights in feet, distance in miles.
    /// </summary>
    public class PlanProfileControl : Control
    {
        const double FtPerM = 3.28084;
        const double MPerMile = 1609.344;

        MissionProfile profile;
        int hoverX = -1;

        public MissionProfile Profile
        {
            get => profile;
            set
            {
                profile = value;
                Invalidate();
            }
        }

        /// <summary>minimum clearance above terrain, metres; closer is drawn red</summary>
        public double ClearanceMetres { get; set; } = 30;

        /// <summary>selected mission item index (-1 home, -2 none)</summary>
        public int SelectedItem { get; set; } = -2;

        public event Action<int> ItemClicked;

        public PlanProfileControl()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw, true);
            BackColor = Color.FromArgb(30, 32, 36);
            ForeColor = Color.Gainsboro;
        }

        const int Left_ = 58, Right_ = 14, Top_ = 10, Bottom_ = 22;

        bool TryScale(out double minFt, out double maxFt, out double totalM)
        {
            minFt = maxFt = totalM = 0;
            if (profile == null || profile.Points.Count < 2 || profile.TotalDistance <= 0)
                return false;
            var hs = profile.Points.SelectMany(p => new[] {p.Terrain, p.Flight}).Where(v => !double.IsNaN(v))
                .Concat(profile.Waypoints.Select(w => w.Flight).Where(v => !double.IsNaN(v))).ToList();
            if (hs.Count == 0)
                return false;
            minFt = hs.Min() * FtPerM;
            maxFt = hs.Max() * FtPerM;
            double pad = Math.Max(30, (maxFt - minFt) * 0.12);
            minFt -= pad;
            maxFt += pad;
            totalM = profile.TotalDistance;
            return true;
        }

        float X(double distM, double totalM) =>
            (float) (Left_ + distM / totalM * Math.Max(1, Width - Left_ - Right_));

        float Y(double ft, double minFt, double maxFt) =>
            (float) (Top_ + (maxFt - ft) / (maxFt - minFt) * Math.Max(1, Height - Top_ - Bottom_));

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);

            using (var font = new Font("Arial", 8))
            using (var text = new SolidBrush(ForeColor))
            {
                if (!TryScale(out var minFt, out var maxFt, out var totalM))
                {
                    g.DrawString("Add waypoints to see the terrain and altitude profile", font, text, Left_, Height / 2f - 6);
                    return;
                }

                // grid + axis labels
                using (var grid = new Pen(Color.FromArgb(55, 58, 64)))
                {
                    double stepFt = NiceStep((maxFt - minFt) / 4);
                    for (double v = Math.Ceiling(minFt / stepFt) * stepFt; v <= maxFt; v += stepFt)
                    {
                        var y = Y(v, minFt, maxFt);
                        g.DrawLine(grid, Left_, y, Width - Right_, y);
                        g.DrawString(v.ToString("0") + " ft", font, text, 2, y - 7);
                    }

                    double totalMi = totalM / MPerMile;
                    double stepMi = NiceStep(totalMi / 6);
                    for (double v = 0; v <= totalMi + 1e-9; v += stepMi)
                    {
                        var x = X(v * MPerMile, totalM);
                        g.DrawLine(grid, x, Top_, x, Height - Bottom_);
                        g.DrawString(v.ToString(stepMi < 1 ? "0.0#" : "0.#") + " mi", font, text, x - 10, Height - Bottom_ + 4);
                    }
                }

                var pts = profile.Points;

                // terrain (filled to the bottom), in runs where it is known
                using (var fill = new SolidBrush(Color.FromArgb(150, 139, 105, 70)))
                using (var edge = new Pen(Color.FromArgb(200, 170, 130, 90), 1.5f))
                {
                    var run = new List<PointF>();

                    void Flush()
                    {
                        if (run.Count >= 2)
                        {
                            var poly = new List<PointF>(run)
                            {
                                new PointF(run[run.Count - 1].X, Height - Bottom_),
                                new PointF(run[0].X, Height - Bottom_)
                            };
                            g.FillPolygon(fill, poly.ToArray());
                            g.DrawLines(edge, run.ToArray());
                        }

                        run.Clear();
                    }

                    foreach (var p in pts)
                    {
                        if (double.IsNaN(p.Terrain))
                        {
                            Flush();
                            continue;
                        }

                        run.Add(new PointF(X(p.Distance, totalM), Y(p.Terrain * FtPerM, minFt, maxFt)));
                    }

                    Flush();
                }

                // flight path, red where clearance is below the minimum
                using (var ok = new Pen(Color.FromArgb(40, 150, 255), 2.5f))
                using (var low = new Pen(Color.FromArgb(255, 60, 40), 3f))
                {
                    for (int i = 1; i < pts.Count; i++)
                    {
                        var a = pts[i - 1];
                        var b = pts[i];
                        if (double.IsNaN(a.Flight) || double.IsNaN(b.Flight))
                            continue;
                        bool isLow = (!double.IsNaN(a.Terrain) && a.Clearance < ClearanceMetres) ||
                                     (!double.IsNaN(b.Terrain) && b.Clearance < ClearanceMetres);
                        g.DrawLine(isLow ? low : ok, X(a.Distance, totalM), Y(a.Flight * FtPerM, minFt, maxFt),
                            X(b.Distance, totalM), Y(b.Flight * FtPerM, minFt, maxFt));
                    }
                }

                // waypoints
                using (var bold = new Font("Arial", 7.5f, FontStyle.Bold))
                {
                    foreach (var w in profile.Waypoints)
                    {
                        if (double.IsNaN(w.Flight))
                            continue;
                        var x = X(w.Distance, totalM);
                        var y = Y(w.Flight * FtPerM, minFt, maxFt);
                        bool sel = w.ItemIndex == SelectedItem;
                        float r = sel ? 9 : 7;
                        using (var b = new SolidBrush(sel ? Color.FromArgb(255, 200, 0) :
                                   w.ItemIndex < 0 ? Color.FromArgb(90, 90, 90) : Color.FromArgb(25, 105, 215)))
                            g.FillEllipse(b, x - r, y - r, r * 2, r * 2);
                        g.DrawEllipse(Pens.White, x - r, y - r, r * 2, r * 2);
                        var sz = g.MeasureString(w.Label, bold);
                        g.DrawString(w.Label, bold, sel ? Brushes.Black : Brushes.White, x - sz.Width / 2, y - sz.Height / 2);
                    }
                }

                // hover read-out
                if (hoverX >= Left_ && hoverX <= Width - Right_)
                {
                    double d = (hoverX - Left_) / (double) Math.Max(1, Width - Left_ - Right_) * totalM;
                    var p = pts.OrderBy(a => Math.Abs(a.Distance - d)).FirstOrDefault();
                    if (p != null)
                    {
                        using (var pen = new Pen(Color.FromArgb(120, 255, 255, 255)) {DashStyle = DashStyle.Dash})
                            g.DrawLine(pen, hoverX, Top_, hoverX, Height - Bottom_);
                        var s = (p.Distance / MPerMile).ToString("0.00") + " mi   alt " +
                                (double.IsNaN(p.Flight) ? "?" : (p.Flight * FtPerM).ToString("0")) + " ft   terrain " +
                                (double.IsNaN(p.Terrain) ? "loading" : (p.Terrain * FtPerM).ToString("0")) + " ft" +
                                (double.IsNaN(p.Terrain) || double.IsNaN(p.Flight)
                                    ? ""
                                    : "   clearance " + (p.Clearance * FtPerM).ToString("0") + " ft");
                        var sz = g.MeasureString(s, font);
                        float tx = Math.Min(Width - sz.Width - 4, hoverX + 6);
                        using (var bg = new SolidBrush(Color.FromArgb(200, 0, 0, 0)))
                            g.FillRectangle(bg, tx - 2, Top_, sz.Width + 4, sz.Height + 2);
                        g.DrawString(s, font, Brushes.White, tx, Top_ + 1);
                    }
                }

                if (profile.TerrainMissing)
                    g.DrawString("terrain loading...", font, text, Width - Right_ - 100, Height - Bottom_ - 14);
            }
        }

        static double NiceStep(double raw)
        {
            if (raw <= 0)
                return 1;
            double mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
            double f = raw / mag;
            return (f < 1.5 ? 1 : f < 3 ? 2 : f < 7 ? 5 : 10) * mag;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            hoverX = e.X;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hoverX = -1;
            Invalidate();
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (!TryScale(out _, out _, out var totalM))
                return;
            var w = profile.Waypoints.OrderBy(a => Math.Abs(X(a.Distance, totalM) - e.X)).FirstOrDefault();
            if (w != null && Math.Abs(X(w.Distance, totalM) - e.X) < 25)
                ItemClicked?.Invoke(w.ItemIndex);
        }
    }
}
