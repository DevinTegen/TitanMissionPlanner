using GMap.NET;
using GMap.NET.WindowsForms;
using log4net;
using MissionPlanner.Utilities;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MissionPlanner.GCSViews
{
    /// <summary>
    /// City / town / village names drawn over the map (satellite imagery has none). Names come from
    /// OpenStreetMap (Overpass API) for the area in view and are cached. Bigger places show from further out.
    /// Turned on/off with the "MapPlaceLabels" setting (default on).
    /// </summary>
    public class PlaceLabels
    {
        static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        static readonly HttpClient http = MakeClient();

        // shared across maps: id -> place
        static readonly Dictionary<long, Place> cache = new Dictionary<long, Place>();
        static readonly HashSet<string> fetchedCells = new HashSet<string>();
        static readonly object sync = new object();

        static readonly List<PlaceLabels> instances = new List<PlaceLabels>();

        public static bool Enabled
        {
            get => Settings.Instance.GetBoolean("MapPlaceLabels", true);
            set
            {
                Settings.Instance["MapPlaceLabels"] = value.ToString();
                foreach (var i in instances.ToArray())
                    i.Refresh();
            }
        }

        readonly GMapControl map;
        readonly GMapOverlay overlay = new GMapOverlay("placelabels");
        readonly Timer timer = new Timer {Interval = 700};
        bool fetching;

        class Place
        {
            public long Id;
            public string Name;
            public double Lat, Lng;
            public int Rank; // 0 city, 1 town, 2 village, 3 hamlet/suburb
        }

        static HttpClient MakeClient()
        {
            var c = new HttpClient {Timeout = TimeSpan.FromSeconds(25)};
            c.DefaultRequestHeaders.Add("User-Agent", "MissionPlanner-Mac/1.0 (github.com/DevinTegen/TitanMissionPlanner)");
            return c;
        }

        public static void Attach(GMapControl map)
        {
            try
            {
                instances.Add(new PlaceLabels(map));
            }
            catch (Exception ex)
            {
                log.Error(ex);
            }
        }

        PlaceLabels(GMapControl map)
        {
            this.map = map;
            map.Overlays.Insert(0, overlay);
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                Refresh();
            };
            map.OnPositionChanged += p => Kick();
            map.OnMapZoomChanged += Kick;
            map.SizeChanged += (s, e) => Kick();
            Kick();
        }

        void Kick()
        {
            timer.Stop();
            timer.Start();
        }

        /// <summary>smallest place rank shown at a zoom level</summary>
        static int MaxRank(double zoom) => zoom < 8 ? 0 : zoom < 10 ? 1 : zoom < 13 ? 2 : 3;

        // fetch in 0.5 degree cells so panning reuses earlier downloads
        const double Cell = 0.5;

        void Refresh()
        {
            try
            {
                if (!map.Overlays.Contains(overlay))
                    map.Overlays.Insert(0, overlay);

                if (!Enabled || map.Zoom < 6)
                {
                    Show();
                    return;
                }

                var view = map.ViewArea;
                if (view.IsEmpty)
                    return;

                var need = new List<string>();
                int maxRank = MaxRank(map.Zoom);
                for (double lat = Math.Floor(view.Bottom / Cell) * Cell; lat < view.Top; lat += Cell)
                for (double lng = Math.Floor(view.Left / Cell) * Cell; lng < view.Right; lng += Cell)
                {
                    var key = CellKey(lat, lng, maxRank);
                    lock (sync)
                        if (!fetchedCells.Contains(key))
                            need.Add(key);
                }

                Show();
                if (need.Count == 0 || fetching || need.Count > 40)
                    return;

                fetching = true;
                Task.Run(() =>
                {
                    try
                    {
                        foreach (var key in need)
                            Fetch(key);
                    }
                    catch (Exception ex)
                    {
                        log.Debug(ex);
                    }
                    finally
                    {
                        fetching = false;
                    }

                    try
                    {
                        if (map.IsHandleCreated && !map.IsDisposed)
                            map.BeginInvoke((Action) Show);
                    }
                    catch
                    {
                    }
                });
            }
            catch (Exception ex)
            {
                log.Debug(ex);
            }
        }

        static string CellKey(double lat, double lng, int maxRank) =>
            Math.Round(lat / Cell).ToString(CultureInfo.InvariantCulture) + "_" +
            Math.Round(lng / Cell).ToString(CultureInfo.InvariantCulture) + "_" +
            (maxRank >= 2 ? 2 : maxRank) + (maxRank >= 3 ? "h" : "");

        static readonly string[] rankNames = {"city", "town", "village", "hamlet|suburb|neighbourhood"};

        static void Fetch(string key)
        {
            var parts = key.Split('_');
            double s = double.Parse(parts[0], CultureInfo.InvariantCulture) * Cell;
            double w = double.Parse(parts[1], CultureInfo.InvariantCulture) * Cell;
            int maxRank = parts[2].StartsWith("2") ? (parts[2].EndsWith("h") ? 3 : 2) : int.Parse(parts[2]);
            var types = string.Join("|", rankNames.Take(maxRank + 1));
            var bbox = string.Format(CultureInfo.InvariantCulture, "{0},{1},{2},{3}", s, w, s + Cell, w + Cell);
            var query = "[out:json][timeout:20];node[\"place\"~\"^(" + types + ")$\"][\"name\"](" + bbox + ");out 400;";

            var resp = http.PostAsync("https://overpass-api.de/api/interpreter",
                new FormUrlEncodedContent(new[] {new KeyValuePair<string, string>("data", query)})).GetAwaiter().GetResult();
            if (!resp.IsSuccessStatusCode)
                return; // busy server: try again next time
            var json = JObject.Parse(resp.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            lock (sync)
            {
                foreach (var el in (JArray) json["elements"])
                {
                    var tags = el["tags"];
                    var type = (string) tags?["place"];
                    var name = (string) tags?["name"];
                    if (type == null || string.IsNullOrEmpty(name))
                        continue;
                    int rank = type == "city" ? 0 : type == "town" ? 1 : type == "village" ? 2 : 3;
                    long id = (long) el["id"];
                    cache[id] = new Place
                    {
                        Id = id, Name = name, Lat = (double) el["lat"], Lng = (double) el["lon"], Rank = rank
                    };
                }

                fetchedCells.Add(key);
                // a fuller fetch also covers the smaller ones
                for (int r = 0; r < maxRank && r < 2; r++)
                    fetchedCells.Add(key.Substring(0, key.LastIndexOf('_') + 1) + r);
            }
        }

        void Show()
        {
            foreach (var m in overlay.Markers.ToList())
                m.Dispose();
            overlay.Markers.Clear();
            if (!Enabled)
            {
                map.Invalidate();
                return;
            }

            var view = map.ViewArea;
            int maxRank = MaxRank(map.Zoom);
            List<Place> places;
            lock (sync)
                places = cache.Values.Where(p => p.Rank <= maxRank && view.Contains(p.Lat, p.Lng))
                    .OrderBy(p => p.Rank).Take(250).ToList();

            // drop names that would sit on top of a bigger one
            var placed = new List<Rectangle>();
            foreach (var p in places)
            {
                var lp = map.FromLatLngToLocal(new PointLatLng(p.Lat, p.Lng));
                var w = p.Name.Length * (p.Rank == 0 ? 9 : 7) + 10;
                var rect = new Rectangle((int) lp.X - w / 2, (int) lp.Y - 10, w, 20);
                if (placed.Any(r => r.IntersectsWith(rect)))
                    continue;
                placed.Add(rect);
                overlay.Markers.Add(new PlaceLabelMarker(new PointLatLng(p.Lat, p.Lng), p.Name, p.Rank));
            }

            map.Invalidate();
        }
    }

    public class PlaceLabelMarker : GMapMarker
    {
        readonly string name;
        readonly int rank;
        static readonly Font[] fonts =
        {
            new Font("Arial", 13f, FontStyle.Bold), new Font("Arial", 11f, FontStyle.Bold),
            new Font("Arial", 9.5f, FontStyle.Bold), new Font("Arial", 8.5f)
        };

        public PlaceLabelMarker(PointLatLng p, string name, int rank) : base(p)
        {
            this.name = name;
            this.rank = Math.Max(0, Math.Min(3, rank));
            IsHitTestVisible = false;
            Size = new Size(1, 1);
        }

        public override void OnRender(IGraphics g)
        {
            var f = fonts[rank];
            var sz = g.MeasureString(name, f);
            float x = LocalPosition.X - sz.Width / 2, y = LocalPosition.Y - sz.Height / 2;
            // dark halo so the name reads on any imagery
            using (var halo = new SolidBrush(Color.FromArgb(200, 0, 0, 0)))
                for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                    if (dx != 0 || dy != 0)
                        g.DrawString(name, f, halo, x + dx, y + dy);
            using (var b = new SolidBrush(rank <= 1 ? Color.White : Color.FromArgb(235, 235, 235)))
                g.DrawString(name, f, b, x, y);
        }
    }
}
