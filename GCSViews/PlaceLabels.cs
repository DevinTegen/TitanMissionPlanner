using GMap.NET;
using GMap.NET.WindowsForms;
using log4net;
using MissionPlanner.Utilities;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MissionPlanner.GCSViews
{
    /// <summary>
    /// City / town names (and borders) drawn over the map, like a "hybrid" map. Satellite imagery has no
    /// labels, so this lays Esri's transparent "World Boundaries and Places" reference tiles on top.
    /// Tiles are cached in memory and on disk. Turned on/off with the "MapPlaceLabels" setting (default on).
    /// </summary>
    public class PlaceLabels
    {
        static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        static readonly HttpClient http = MakeClient();

        const string UrlFormat =
            "https://server.arcgisonline.com/ArcGIS/rest/services/Reference/World_Boundaries_and_Places/MapServer/tile/{0}/{1}/{2}";

        // z/y/x -> image (null = failed or empty)
        static readonly ConcurrentDictionary<string, Image> tiles = new ConcurrentDictionary<string, Image>();
        static readonly ConcurrentDictionary<string, bool> pending = new ConcurrentDictionary<string, bool>();
        static readonly ConcurrentDictionary<string, DateTime> failedAt = new ConcurrentDictionary<string, DateTime>();
        static readonly SemaphoreSlim downloads = new SemaphoreSlim(4);
        static readonly List<PlaceLabels> instances = new List<PlaceLabels>();
        static string cacheDir;

        public static bool Enabled
        {
            get => Settings.Instance.GetBoolean("MapPlaceLabels", true);
            set
            {
                Settings.Instance["MapPlaceLabels"] = value.ToString();
                foreach (var i in instances.ToArray())
                    i.map.Invalidate();
            }
        }

        readonly GMapControl map;

        static HttpClient MakeClient()
        {
            var c = new HttpClient {Timeout = TimeSpan.FromSeconds(20)};
            c.DefaultRequestHeaders.Add("User-Agent", "MissionPlanner-Mac/1.0");
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
            map.Paint += Map_Paint;
        }

        static string CacheDir
        {
            get
            {
                if (cacheDir == null)
                {
                    try
                    {
                        cacheDir = Path.Combine(Settings.GetDataDirectory(), "placelabels");
                        Directory.CreateDirectory(cacheDir);
                    }
                    catch
                    {
                        cacheDir = "";
                    }
                }

                return cacheDir;
            }
        }

        // web mercator tile corner -> lat/lng
        static PointLatLng TileToLatLng(int x, int y, int z)
        {
            double n = Math.Pow(2, z);
            double lng = x / n * 360.0 - 180.0;
            double lat = Math.Atan(Math.Sinh(Math.PI * (1 - 2 * y / n))) * 180.0 / Math.PI;
            return new PointLatLng(lat, lng);
        }

        static void LatLngToTile(double lat, double lng, int z, out int x, out int y)
        {
            double n = Math.Pow(2, z);
            lat = Math.Max(-85.05, Math.Min(85.05, lat));
            x = (int) Math.Floor((lng + 180.0) / 360.0 * n);
            double r = lat * Math.PI / 180;
            y = (int) Math.Floor((1 - Math.Log(Math.Tan(r) + 1 / Math.Cos(r)) / Math.PI) / 2 * n);
            x = Math.Max(0, Math.Min((int) n - 1, x));
            y = Math.Max(0, Math.Min((int) n - 1, y));
        }

        void Map_Paint(object sender, PaintEventArgs e)
        {
            if (!Enabled)
                return;
            try
            {
                var view = map.ViewArea;
                if (view.IsEmpty)
                    return;
                int z = (int) Math.Max(2, Math.Min(19, Math.Round(map.Zoom)));
                LatLngToTile(view.Top, view.Left, z, out int x0, out int y0);
                LatLngToTile(view.Bottom, view.Right, z, out int x1, out int y1);
                if ((x1 - x0 + 1) * (y1 - y0 + 1) > 64)
                    return;

                for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    var key = z + "/" + y + "/" + x;
                    if (!tiles.TryGetValue(key, out var img))
                    {
                        Request(key);
                        continue;
                    }

                    if (img == null)
                        continue;
                    var tl = map.FromLatLngToLocal(TileToLatLng(x, y, z));
                    var br = map.FromLatLngToLocal(TileToLatLng(x + 1, y + 1, z));
                    e.Graphics.DrawImage(img, new Rectangle((int) tl.X, (int) tl.Y, (int) (br.X - tl.X),
                        (int) (br.Y - tl.Y)));
                }
            }
            catch (Exception ex)
            {
                log.Debug(ex);
            }
        }

        void Request(string key)
        {
            if (failedAt.TryGetValue(key, out var when) && (DateTime.Now - when).TotalSeconds < 30)
                return;
            if (!pending.TryAdd(key, true))
                return;
            Task.Run(async () =>
            {
                try
                {
                    byte[] data = null;
                    var file = CacheDir == "" ? null : Path.Combine(CacheDir, key.Replace('/', '_') + ".png");
                    if (file != null && File.Exists(file) && (DateTime.Now - File.GetLastWriteTime(file)).TotalDays < 60)
                        data = File.ReadAllBytes(file);

                    if (data == null)
                    {
                        await downloads.WaitAsync().ConfigureAwait(false);
                        try
                        {
                            var p = key.Split('/');
                            data = await http.GetByteArrayAsync(string.Format(UrlFormat, p[0], p[1], p[2]))
                                .ConfigureAwait(false);
                        }
                        finally
                        {
                            downloads.Release();
                        }

                        if (file != null && data != null && data.Length > 0)
                            try
                            {
                                File.WriteAllBytes(file, data);
                            }
                            catch
                            {
                            }
                    }

                    Image img = null;
                    if (data != null && data.Length > 0)
                        img = Image.FromStream(new MemoryStream(data));
                    tiles[key] = img;
                }
                catch (Exception ex)
                {
                    log.Debug("place label tile " + key + ": " + ex.Message);
                    // leave it out of the cache so it is retried later (not straight away)
                    failedAt[key] = DateTime.Now;
                    pending.TryRemove(key, out _);
                    return;
                }

                pending.TryRemove(key, out _);
                foreach (var i in instances.ToArray())
                    try
                    {
                        if (i.map.IsHandleCreated && !i.map.IsDisposed)
                            i.map.BeginInvoke((Action) (() => i.map.Invalidate()));
                    }
                    catch
                    {
                    }
            });
        }
    }
}
