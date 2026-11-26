using GMap.NET;
using GMap.NET.WindowsForms;
using log4net;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MissionPlanner.GCSViews
{
    /// <summary>
    /// "Search places" box: type a town, area or address (e.g. "Pendleton Oregon"), press Enter, and the map
    /// jumps to fit that place. Uses OpenStreetMap's Nominatim geocoder. The ▾ button lists other matches.
    /// Also accepts "lat, lng" coordinates.
    /// </summary>
    public class MapSearchBox : Panel
    {
        static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        static readonly HttpClient http = MakeClient();

        readonly GMapControl map;
        readonly TextBox box = new TextBox();
        readonly Button go = new Button();
        readonly Button more = new Button();
        List<Place> lastResults = new List<Place>();
        bool busy;

        class Place
        {
            public string Name;
            public double Lat, Lng;
            public double South, North, West, East;
            public bool HasBox;
        }

        static HttpClient MakeClient()
        {
            var c = new HttpClient {Timeout = TimeSpan.FromSeconds(15)};
            // Nominatim's usage policy asks for an identifying user agent
            c.DefaultRequestHeaders.Add("User-Agent", "MissionPlanner-Mac/1.0 (github.com/DevinTegen/TitanMissionPlanner)");
            return c;
        }

        public MapSearchBox(GMapControl map, int width = 300)
        {
            this.map = map;
            Size = new Size(width, 30);
            BackColor = Color.FromArgb(40, 43, 48);

            box.Location = new Point(4, 4);
            box.Width = width - 4 - 34 - 26 - 6;
            box.Font = new Font("Arial", 10f);
            box.BorderStyle = BorderStyle.FixedSingle;
            SetHint();
            box.GotFocus += (s, e) =>
            {
                if (box.ForeColor == Color.Gray)
                {
                    box.Text = "";
                    box.ForeColor = Color.Black;
                }
            };
            box.LostFocus += (s, e) =>
            {
                if (string.IsNullOrWhiteSpace(box.Text))
                    SetHint();
            };
            box.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Return)
                {
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                    Search();
                }
                else if (e.KeyCode == Keys.Escape)
                {
                    map.Focus();
                }
            };

            go.Text = "Go";
            go.Location = new Point(box.Right + 3, 2);
            go.Size = new Size(34, 26);
            Style(go);
            go.Click += (s, e) => Search();

            more.Text = "▾";
            more.Location = new Point(go.Right + 2, 2);
            more.Size = new Size(24, 26);
            Style(more);
            more.Click += (s, e) => ShowMatches();

            Controls.Add(box);
            Controls.Add(go);
            Controls.Add(more);
        }

        static void Style(Button b)
        {
            b.FlatStyle = FlatStyle.Flat;
            b.BackColor = Color.FromArgb(40, 140, 230);
            b.ForeColor = Color.White;
            b.Font = new Font("Arial", 9f, FontStyle.Bold);
            b.FlatAppearance.BorderSize = 0;
        }

        void SetHint()
        {
            box.Text = "Search places (e.g. Pendleton, OR)";
            box.ForeColor = Color.Gray;
        }

        async void Search()
        {
            var q = box.ForeColor == Color.Gray ? "" : box.Text.Trim();
            if (q == "" || busy)
                return;

            // plain coordinates: "45.67, -118.79"
            var parts = q.Split(new[] {',', ' '}, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 &&
                double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var la) &&
                double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var lo) &&
                Math.Abs(la) <= 90 && Math.Abs(lo) <= 180)
            {
                map.Position = new PointLatLng(la, lo);
                map.Zoom = Math.Max(map.Zoom, 15);
                return;
            }

            busy = true;
            go.Text = "…";
            try
            {
                var results = await Task.Run(() => Lookup(q));
                lastResults = results;
                if (results.Count == 0)
                {
                    CustomMessageBox.Show("No place found for \"" + q + "\".", "Search");
                    return;
                }

                GoTo(results[0]);
            }
            catch (Exception ex)
            {
                log.Error(ex);
                CustomMessageBox.Show("Search failed (no internet?): " + ex.Message, "Search");
            }
            finally
            {
                busy = false;
                go.Text = "Go";
            }
        }

        static List<Place> Lookup(string q)
        {
            var url = "https://nominatim.openstreetmap.org/search?format=jsonv2&limit=8&addressdetails=0&q=" +
                      Uri.EscapeDataString(q);
            var json = http.GetStringAsync(url).GetAwaiter().GetResult();
            var list = new List<Place>();
            foreach (var t in JArray.Parse(json))
            {
                var p = new Place
                {
                    Name = (string) t["display_name"],
                    Lat = double.Parse((string) t["lat"], CultureInfo.InvariantCulture),
                    Lng = double.Parse((string) t["lon"], CultureInfo.InvariantCulture)
                };
                var bb = t["boundingbox"] as JArray;
                if (bb != null && bb.Count == 4)
                {
                    p.South = double.Parse((string) bb[0], CultureInfo.InvariantCulture);
                    p.North = double.Parse((string) bb[1], CultureInfo.InvariantCulture);
                    p.West = double.Parse((string) bb[2], CultureInfo.InvariantCulture);
                    p.East = double.Parse((string) bb[3], CultureInfo.InvariantCulture);
                    p.HasBox = p.North > p.South && p.East > p.West;
                }

                list.Add(p);
            }

            return list;
        }

        void GoTo(Place p)
        {
            if (p.HasBox)
            {
                // fit the whole town/area on screen
                var rect = RectLatLng.FromLTRB(p.West, p.North, p.East, p.South);
                map.SetZoomToFitRect(rect);
                // a single address has a tiny box: don't zoom in absurdly far
                if (map.Zoom > 17)
                    map.Zoom = 17;
                // a huge area (state/country) still centres on the place
                if (map.Zoom < 4)
                    map.Zoom = 4;
                map.Position = new PointLatLng(p.Lat, p.Lng);
            }
            else
            {
                map.Position = new PointLatLng(p.Lat, p.Lng);
                map.Zoom = 13;
            }

            map.Invalidate();
        }

        void ShowMatches()
        {
            if (lastResults.Count == 0)
            {
                Search();
                return;
            }

            var menu = new ContextMenuStrip();
            foreach (var p in lastResults)
            {
                var place = p;
                var name = place.Name.Length > 90 ? place.Name.Substring(0, 90) + "…" : place.Name;
                menu.Items.Add(name, null, (s, e) => GoTo(place));
            }

            menu.Show(this, new Point(0, Height));
        }
    }
}
