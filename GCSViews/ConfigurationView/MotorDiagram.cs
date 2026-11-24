using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace MissionPlanner.GCSViews.ConfigurationView
{
    /// <summary>
    /// Top-down drawing of a multirotor frame: each motor with its test letter, motor number, position and
    /// spin direction. A motor can be highlighted (spinning), and in pick mode the user clicks a motor.
    /// </summary>
    public class MotorDiagram : Control
    {
        List<MotorInfo> motors = new List<MotorInfo>();
        readonly Dictionary<MotorInfo, PointF> centres = new Dictionary<MotorInfo, PointF>();
        readonly Timer highlightTimer = new Timer();
        float radius = 22;

        public MotorDiagram()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw, true);
            BackColor = Color.FromArgb(38, 39, 40);
            ForeColor = Color.WhiteSmoke;
            highlightTimer.Tick += (s, e) =>
            {
                highlightTimer.Stop();
                Highlighted = null;
            };
        }

        public List<MotorInfo> Motors
        {
            get => motors;
            set
            {
                motors = value ?? new List<MotorInfo>();
                Assigned.Clear();
                Highlighted = null;
                Invalidate();
            }
        }

        MotorInfo highlighted;

        /// <summary>the motor drawn as spinning</summary>
        public MotorInfo Highlighted
        {
            get => highlighted;
            set
            {
                highlighted = value;
                Invalidate();
            }
        }

        /// <summary>motors already identified (drawn green with a tick)</summary>
        public HashSet<MotorInfo> Assigned { get; } = new HashSet<MotorInfo>();

        /// <summary>clicking a motor raises MotorClicked</summary>
        public bool PickMode { get; set; }

        public event Action<MotorInfo> MotorClicked;

        public string Caption { get; set; } = "";

        /// <summary>show the motor as spinning for a while</summary>
        public void HighlightFor(int testOrder, int seconds)
        {
            Highlighted = motors.FirstOrDefault(a => a.TestOrder == testOrder);
            highlightTimer.Stop();
            highlightTimer.Interval = Math.Max(500, seconds * 1000);
            highlightTimer.Start();
        }

        void ComputeLayout()
        {
            centres.Clear();
            if (motors.Count == 0)
                return;
            float size = Math.Min(Width, Height - 40);
            radius = Math.Max(14, Math.Min(26, size / 11));
            var cx = Width / 2f;
            var cy = 26 + (Height - 26 - 14) / 2f;
            double max = motors.Max(a => Math.Sqrt(a.X * a.X + a.Y * a.Y));
            if (max < 1e-6)
                max = 1;
            float scale = (float) ((size / 2 - radius - 26) / max);

            foreach (var m in motors)
            {
                float x = cx + (float) (m.X * scale);
                float y = cy - (float) (m.Y * scale);
                centres[m] = new PointF(x, y);
            }

            // stacked motors: spread them along the arm so both can be seen and clicked
            foreach (var g in motors.GroupBy(a => (Math.Round(a.X, 3), Math.Round(a.Y, 3))).Where(g => g.Count() > 1))
            {
                var list = g.ToList();
                var p = centres[list[0]];
                var dx = p.X - cx;
                var dy = p.Y - cy;
                var len = (float) Math.Sqrt(dx * dx + dy * dy);
                if (len < 1)
                {
                    dx = 1;
                    dy = 0;
                    len = 1;
                }

                for (int i = 0; i < list.Count; i++)
                {
                    float off = (i - (list.Count - 1) / 2f) * radius * 1.15f;
                    // perpendicular to the arm
                    centres[list[i]] = new PointF(p.X - dy / len * off, p.Y + dx / len * off);
                }
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            ComputeLayout();

            using (var text = new SolidBrush(ForeColor))
            using (var small = new Font("Arial", 7.5f))
            using (var bold = new Font("Arial", 11f, FontStyle.Bold))
            using (var title = new Font("Arial", 9f, FontStyle.Bold))
            {
                // front marker
                var front = "FRONT";
                var fs = g.MeasureString(front, title);
                g.DrawString(front, title, text, Width / 2f - fs.Width / 2, 2);
                var tri = new[]
                {
                    new PointF(Width / 2f, 18), new PointF(Width / 2f - 7, 26), new PointF(Width / 2f + 7, 26)
                };
                using (var b = new SolidBrush(Color.FromArgb(255, 200, 60)))
                    g.FillPolygon(b, tri);

                if (motors.Count == 0)
                {
                    var msg = "Frame layout not known";
                    var ms = g.MeasureString(msg, small);
                    g.DrawString(msg, small, text, Width / 2f - ms.Width / 2, Height / 2f);
                    return;
                }

                var cx = Width / 2f;
                var cy = 26 + (Height - 26 - 14) / 2f;

                // arms and body
                using (var arm = new Pen(Color.FromArgb(110, 110, 110), 5))
                    foreach (var p in centres.Values)
                        g.DrawLine(arm, cx, cy, p.X, p.Y);
                using (var body = new SolidBrush(Color.FromArgb(80, 80, 85)))
                    g.FillRectangle(body, cx - radius * 0.9f, cy - radius * 1.1f, radius * 1.8f, radius * 2.2f);
                using (var arrow = new Pen(Color.FromArgb(255, 200, 60), 2))
                {
                    g.DrawLine(arrow, cx, cy + radius * 0.6f, cx, cy - radius * 0.7f);
                    g.DrawLine(arrow, cx, cy - radius * 0.7f, cx - 5, cy - radius * 0.7f + 6);
                    g.DrawLine(arrow, cx, cy - radius * 0.7f, cx + 5, cy - radius * 0.7f + 6);
                }

                foreach (var m in motors)
                {
                    var p = centres[m];
                    bool spinning = m == highlighted;
                    bool done = Assigned.Contains(m);
                    var fill = spinning ? Color.FromArgb(255, 140, 0) :
                        done ? Color.FromArgb(40, 150, 70) : Color.FromArgb(70, 90, 120);
                    using (var b = new SolidBrush(fill))
                        g.FillEllipse(b, p.X - radius, p.Y - radius, radius * 2, radius * 2);
                    using (var pen = new Pen(spinning ? Color.White : Color.FromArgb(160, 170, 180), spinning ? 3 : 1.5f))
                        g.DrawEllipse(pen, p.X - radius, p.Y - radius, radius * 2, radius * 2);

                    DrawRotation(g, p, m.Rotation, spinning);

                    var letter = m.Letter.ToString();
                    var ls = g.MeasureString(letter, bold);
                    g.DrawString(letter, bold, text, p.X - ls.Width / 2, p.Y - ls.Height / 2 - 4);
                    var num = "M" + m.Number;
                    var ns = g.MeasureString(num, small);
                    g.DrawString(num, small, text, p.X - ns.Width / 2, p.Y + 3);

                    // position + rotation under (or above) the motor
                    var label = m.Position + "  " + m.Rotation;
                    var lsz = g.MeasureString(label, small);
                    float ly = p.Y > cy ? p.Y + radius + 6 : p.Y - radius - 6 - lsz.Height;
                    float lx = Math.Max(1, Math.Min(Width - lsz.Width - 1, p.X - lsz.Width / 2));
                    using (var bg = new SolidBrush(Color.FromArgb(150, 0, 0, 0)))
                        g.FillRectangle(bg, lx - 1, ly, lsz.Width + 2, lsz.Height);
                    g.DrawString(label, small, text, lx, ly);
                }

                if (!string.IsNullOrEmpty(Caption))
                {
                    var cs = g.MeasureString(Caption, small);
                    g.DrawString(Caption, small, text, Width / 2f - cs.Width / 2, Height - cs.Height - 1);
                }
            }
        }

        void DrawRotation(Graphics g, PointF p, string rotation, bool spinning)
        {
            if (rotation != "CW" && rotation != "CCW")
                return;
            float r = radius + 5;
            var rect = new RectangleF(p.X - r, p.Y - r, r * 2, r * 2);
            using (var pen = new Pen(spinning ? Color.White : Color.FromArgb(200, 255, 200, 60), 2))
            {
                // arc over the top of the motor, arrow head at the end in the spin direction
                g.DrawArc(pen, rect, 200, 140);
                double endDeg = rotation == "CW" ? 340 : 200;
                double end = endDeg * Math.PI / 180;
                var tip = new PointF(p.X + r * (float) Math.Cos(end), p.Y + r * (float) Math.Sin(end));
                // tangent direction (clockwise on screen = increasing angle)
                double dir = rotation == "CW" ? end + Math.PI / 2 : end - Math.PI / 2;
                var a1 = new PointF(tip.X - 7 * (float) Math.Cos(dir - 0.5), tip.Y - 7 * (float) Math.Sin(dir - 0.5));
                var a2 = new PointF(tip.X - 7 * (float) Math.Cos(dir + 0.5), tip.Y - 7 * (float) Math.Sin(dir + 0.5));
                g.DrawLine(pen, tip, a1);
                g.DrawLine(pen, tip, a2);
            }
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (!PickMode)
                return;
            ComputeLayout();
            MotorInfo best = null;
            double bestDist = double.MaxValue;
            foreach (var kv in centres)
            {
                var d = Math.Sqrt(Math.Pow(kv.Value.X - e.X, 2) + Math.Pow(kv.Value.Y - e.Y, 2));
                if (d < bestDist)
                {
                    bestDist = d;
                    best = kv.Key;
                }
            }

            if (best != null && bestDist <= radius * 1.6)
                MotorClicked?.Invoke(best);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                highlightTimer.Dispose();
            base.Dispose(disposing);
        }
    }
}
