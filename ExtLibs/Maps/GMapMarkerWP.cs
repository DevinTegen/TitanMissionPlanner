using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using GMap.NET;
using GMap.NET.WindowsForms;
using GMap.NET.WindowsForms.Markers;

namespace MissionPlanner.Maps
{
    [Serializable]
    public class GMapMarkerWP : GMarkerGoogle
    {
        string wpno = "";
        public bool selected = false;

        /// <summary>always-visible label under the marker, e.g. "Loiter 400 ft"</summary>
        public string AltLabel;
        static Font labelFont;
        SizeF txtsize = SizeF.Empty;
        static Dictionary<string, Bitmap> fontBitmaps = new Dictionary<string, Bitmap>();
        static Font font;

        public GMapMarkerWP(PointLatLng p, string wpno)
            : base(p, GMarkerGoogleType.green)
        {
            this.wpno = wpno;
            if (font == null)
                font = SystemFonts.DefaultFont;

            if (!fontBitmaps.ContainsKey(wpno))
            {
                Bitmap temp = new Bitmap(100,40, PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(temp))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                    txtsize = g.MeasureString(wpno, font);

                    g.DrawString(wpno, font, Brushes.Black, new PointF(0, 0));
                }
                fontBitmaps[wpno] = temp;
            }
        }

        public override void OnRender(IGraphics g)
        {
            if (selected)
            {
                g.FillEllipse(Brushes.Red, new Rectangle(this.LocalPosition, this.Size));
                g.DrawArc(Pens.Red, new Rectangle(this.LocalPosition, this.Size), 0, 360);
            }
            
            base.OnRender(g);

            var midw = LocalPosition.X + 10;
            var midh = LocalPosition.Y + 3;

            if (txtsize.Width > 15)
                midw -= 4;

            if (Overlay.Control.Zoom> 16 || IsMouseOver)
                g.DrawImageUnscaled(fontBitmaps[wpno], midw,midh);

            if (!string.IsNullOrEmpty(AltLabel) && (Overlay.Control.Zoom >= 12 || IsMouseOver))
            {
                if (labelFont == null)
                    labelFont = new Font("Arial", 8, FontStyle.Bold);
                // small blue "bubble" (pill) under the marker
                var size = g.MeasureString(AltLabel, labelFont);
                float h = size.Height + 2;
                float w = size.Width + 6;
                float x = LocalPosition.X + Size.Width / 2f - w / 2f;
                float y = LocalPosition.Y + Size.Height + 2;
                using (var bg = new SolidBrush(Color.FromArgb(225, 25, 105, 215)))
                {
                    g.FillEllipse(bg, x - h / 2, y, h, h);
                    g.FillEllipse(bg, x + w - h / 2, y, h, h);
                    g.FillRectangle(bg, x, y, w, h);
                }
                g.DrawString(AltLabel, labelFont, Brushes.White, x + 3, y + 1);
            }
        }
    }
}