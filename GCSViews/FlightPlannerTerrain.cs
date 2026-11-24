using GMap.NET;
using GMap.NET.WindowsForms;
using GMap.NET.WindowsForms.Markers;
using MissionPlanner.ArduPilot;
using MissionPlanner.ArduPilot.Mavlink;
using MissionPlanner.Controls;
using MissionPlanner.Maps;
using MissionPlanner.Utilities;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MissionPlanner.GCSViews
{
    /// <summary>
    /// Terrain tools for the Flight Planner:
    ///  - terrain altitude heat map over the visible map
    ///  - mission terrain-clearance check (legs closer to the ground than a minimum are drawn red,
    ///    and Write WPs asks for confirmation)
    ///  - upload of terrain to the autopilot's SD card covering the route + a buffer (default 5 miles)
    ///  - one-click terrain-following RTL setup
    /// </summary>
    public partial class FlightPlanner
    {
        const double MileMetres = 1609.344;

        /// <summary>how far around the mission area terrain is uploaded to the vehicle, miles</summary>
        public static double TerrainUploadBufferMiles
        {
            get => Settings.Instance.GetDouble("Terrain_UploadBufferMiles", 5);
            set => Settings.Instance["Terrain_UploadBufferMiles"] = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        static string BufferText => TerrainUploadBufferMiles.ToString("0.#") + (TerrainUploadBufferMiles == 1 ? " mile" : " miles");

        GMapOverlay terrainHeatOverlay;
        GMapOverlay terrainClearanceOverlay;
        System.Windows.Forms.Timer terrainHeatTimer;
        System.Windows.Forms.Timer terrainClearanceTimer;
        int terrainHeatGeneration;
        int terrainHeatRetries;
        int terrainClearanceGeneration;
        int terrainClearanceRetries;
        ToolStripMenuItem terrainHeatMenuItem;
        ToolStripMenuItem terrainCheckMenuItem;
        ToolStripMenuItem terrainClearanceMenuItem;
        TerrainClearanceResult lastTerrainClearance;

        static bool TerrainHeatEnabled
        {
            get => Settings.Instance.GetBoolean("Terrain_HeatMap", false);
            set => Settings.Instance["Terrain_HeatMap"] = value.ToString();
        }

        static bool TerrainCheckEnabled
        {
            get => Settings.Instance.GetBoolean("Terrain_ClearanceCheck", true);
            set => Settings.Instance["Terrain_ClearanceCheck"] = value.ToString();
        }

        /// <summary>minimum height above terrain a mission leg may come to, metres</summary>
        public static double TerrainClearanceMetres
        {
            get => Settings.Instance.GetDouble("Terrain_ClearanceMetres", 30);
            set => Settings.Instance["Terrain_ClearanceMetres"] = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        static string AltText(double metres) =>
            (metres * CurrentState.multiplieralt).ToString("0") + " " +
            (string.IsNullOrEmpty(CurrentState.AltUnit) ? "m" : CurrentState.AltUnit);

        void InitTerrainTools()
        {
            try
            {
                terrainHeatOverlay = new GMapOverlay("terrainheat");
                MainMap.Overlays.Insert(0, terrainHeatOverlay);
                terrainClearanceOverlay = new GMapOverlay("terrainclearance");
                MainMap.Overlays.Add(terrainClearanceOverlay);

                terrainHeatTimer = new System.Windows.Forms.Timer {Interval = 400};
                terrainHeatTimer.Tick += (s, e) =>
                {
                    terrainHeatTimer.Stop();
                    UpdateTerrainHeatMap();
                };

                terrainClearanceTimer = new System.Windows.Forms.Timer {Interval = 500};
                terrainClearanceTimer.Tick += (s, e) =>
                {
                    terrainClearanceTimer.Stop();
                    UpdateTerrainClearance();
                };

                MainMap.OnMapZoomChanged += () => ScheduleTerrainHeatMap(true);
                MainMap.OnPositionChanged += p => ScheduleTerrainHeatMap(true);
                MainMap.SizeChanged += (s, e) => ScheduleTerrainHeatMap(true);

                BuildTerrainMenu();

                if (TerrainHeatEnabled)
                    ScheduleTerrainHeatMap(true);
            }
            catch (Exception ex)
            {
                log.Error("InitTerrainTools", ex);
            }
        }

        void BuildTerrainMenu()
        {
            var terrain = new ToolStripMenuItem("Terrain");

            terrainHeatMenuItem = new ToolStripMenuItem("Show terrain heat map")
                {CheckOnClick = true, Checked = TerrainHeatEnabled};
            terrainHeatMenuItem.CheckedChanged += (s, e) =>
            {
                TerrainHeatEnabled = terrainHeatMenuItem.Checked;
                ScheduleTerrainHeatMap(true);
            };

            terrainCheckMenuItem = new ToolStripMenuItem("Warn when the mission is close to terrain")
                {CheckOnClick = true, Checked = TerrainCheckEnabled};
            terrainCheckMenuItem.CheckedChanged += (s, e) =>
            {
                TerrainCheckEnabled = terrainCheckMenuItem.Checked;
                ScheduleTerrainClearance(true);
            };

            terrainClearanceMenuItem = new ToolStripMenuItem();
            UpdateClearanceMenuText();
            terrainClearanceMenuItem.Click += (s, e) =>
            {
                double value = Math.Round(TerrainClearanceMetres * CurrentState.multiplieralt);
                if (InputBox.Show("Terrain clearance",
                        "Minimum height above terrain for mission legs (" +
                        (string.IsNullOrEmpty(CurrentState.AltUnit) ? "m" : CurrentState.AltUnit) + ")",
                        ref value) != DialogResult.OK)
                    return;
                if (value < 0)
                    return;
                TerrainClearanceMetres = value / CurrentState.multiplieralt;
                UpdateClearanceMenuText();
                ScheduleTerrainClearance(true);
            };

            var report = new ToolStripMenuItem("Terrain clearance report...");
            report.Click += (s, e) => ShowTerrainClearanceReport();

            var upload = new ToolStripMenuItem();
            upload.Click += (s, e) => UploadTerrainToVehicle();
            terrain.DropDownOpening += (s, e) => upload.Text = "Upload terrain to vehicle (route + " + BufferText + ")...";
            upload.Text = "Upload terrain to vehicle (route + " + BufferText + ")...";

            var uploadArea = new ToolStripMenuItem("Terrain upload distance around the route...");
            uploadArea.Click += (s, e) =>
            {
                double miles = TerrainUploadBufferMiles;
                if (InputBox.Show("Terrain upload", "Upload terrain this many miles around the mission area", ref miles) !=
                    DialogResult.OK)
                    return;
                if (miles < 0.5 || miles > 25)
                {
                    CustomMessageBox.Show("Use between 0.5 and 25 miles.", "Terrain upload");
                    return;
                }

                TerrainUploadBufferMiles = miles;
            };

            var rtl = new ToolStripMenuItem("Set up terrain-following RTL...");
            rtl.Click += (s, e) => SetupTerrainFollowingRtl();

            terrain.DropDownItems.AddRange(new ToolStripItem[]
            {
                terrainHeatMenuItem, terrainCheckMenuItem, terrainClearanceMenuItem, report,
                new ToolStripSeparator(), upload, uploadArea, rtl
            });

            var index = contextMenuStrip1.Items.IndexOf(mapToolToolStripMenuItem);
            contextMenuStrip1.Items.Insert(index < 0 ? contextMenuStrip1.Items.Count : index + 1, terrain);
        }

        void UpdateClearanceMenuText()
        {
            if (terrainClearanceMenuItem != null)
                terrainClearanceMenuItem.Text = "Minimum terrain clearance: " + AltText(TerrainClearanceMetres) + "...";
        }

        /// <summary>terrain height AMSL in metres, or null while the tile is not loaded yet</summary>
        static double? TerrainAt(double lat, double lng)
        {
            var r = srtm.getAltitude(lat, lng);
            if (r == null || r.currenttype == srtm.tiletype.invalid)
                return null;
            return r.alt;
        }

        #region heat map

        void ScheduleTerrainHeatMap(bool resetRetries)
        {
            if (terrainHeatTimer == null)
                return;
            if (resetRetries)
                terrainHeatRetries = 0;
            terrainHeatTimer.Stop();
            terrainHeatTimer.Start();
        }

        void UpdateTerrainHeatMap()
        {
            var generation = ++terrainHeatGeneration;

            if (!TerrainHeatEnabled)
            {
                ClearOverlay(terrainHeatOverlay);
                return;
            }

            var area = MainMap.ViewArea;
            int width = MainMap.Width, height = MainMap.Height;
            if (area.IsEmpty || width < 10 || height < 10)
                return;

            // zoomed far out: too many terrain tiles to load
            if (area.WidthLng > 4 || area.HeightLat > 4)
            {
                ClearOverlay(terrainHeatOverlay);
                return;
            }

            int cols = Math.Max(20, Math.Min(240, width / 4));
            int rows = Math.Max(20, Math.Min(240, (int)Math.Round(cols * (double)height / width)));
            double multiplier = CurrentState.multiplieralt;
            string unit = CurrentState.AltUnit;

            Task.Run(() =>
            {
                var heights = new double[cols, rows];
                int missing = 0;
                for (int y = 0; y < rows; y++)
                {
                    if (generation != terrainHeatGeneration)
                        return;
                    double lat = area.Top - (y + 0.5) * area.HeightLat / rows;
                    for (int x = 0; x < cols; x++)
                    {
                        double lng = area.Left + (x + 0.5) * area.WidthLng / cols;
                        var h = TerrainAt(lat, lng);
                        if (h == null)
                            missing++;
                        heights[x, y] = h ?? double.NaN;
                    }
                }

                if (generation != terrainHeatGeneration)
                    return;

                var marker = new GMapMarkerTerrainHeat(heights, area, multiplier, unit);

                BeginInvokeIfAlive(() =>
                {
                    if (generation != terrainHeatGeneration || !TerrainHeatEnabled)
                    {
                        marker.Dispose();
                        return;
                    }

                    ClearOverlay(terrainHeatOverlay);
                    terrainHeatOverlay.Markers.Add(marker);
                    MainMap.Invalidate();

                    // some terrain tiles were still downloading: draw again shortly
                    if (missing > 0 && terrainHeatRetries++ < 10)
                    {
                        terrainHeatTimer.Interval = 3000;
                        ScheduleTerrainHeatMap(false);
                        terrainHeatTimer.Interval = 400;
                    }
                });
            });
        }

        #endregion

        #region clearance check

        public class TerrainLeg
        {
            public string From;
            public string To;
            public double MinClearance = double.MaxValue; // metres above terrain
            public PointLatLng Worst;
            public bool TerrainMissing;
            public bool Skipped;
            public string SkipReason;
            public List<List<PointLatLng>> LowRuns = new List<List<PointLatLng>>();
        }

        public class TerrainClearanceResult
        {
            public List<TerrainLeg> Legs = new List<TerrainLeg>();
            public double Threshold;
            public bool AnyMissing => Legs.Any(a => a.TerrainMissing);
            public IEnumerable<TerrainLeg> LowLegs => Legs.Where(a => !a.Skipped && a.MinClearance < Threshold);
        }

        class NavPoint
        {
            public string Label;
            public double Lat, Lng, Alt;
            public MAVLink.MAV_FRAME Frame;
            public ushort Cmd;
        }

        static bool IsTerrainFrame(MAVLink.MAV_FRAME f) =>
            f == MAVLink.MAV_FRAME.GLOBAL_TERRAIN_ALT || f == MAVLink.MAV_FRAME.GLOBAL_TERRAIN_ALT_INT;

        static bool IsAbsoluteFrame(MAVLink.MAV_FRAME f) =>
            f == MAVLink.MAV_FRAME.GLOBAL || f == MAVLink.MAV_FRAME.GLOBAL_INT;

        static readonly HashSet<ushort> navWithLocation = new HashSet<ushort>
        {
            (ushort) MAVLink.MAV_CMD.WAYPOINT, (ushort) MAVLink.MAV_CMD.LOITER_UNLIM,
            (ushort) MAVLink.MAV_CMD.LOITER_TURNS, (ushort) MAVLink.MAV_CMD.LOITER_TIME,
            (ushort) MAVLink.MAV_CMD.LAND, (ushort) MAVLink.MAV_CMD.TAKEOFF,
            (ushort) MAVLink.MAV_CMD.LOITER_TO_ALT, (ushort) MAVLink.MAV_CMD.SPLINE_WAYPOINT,
            (ushort) MAVLink.MAV_CMD.VTOL_TAKEOFF, (ushort) MAVLink.MAV_CMD.VTOL_LAND
        };

        /// <summary>home + the mission's navigation points, in flight order (DO_JUMPs are not followed)</summary>
        bool TryGetRoute(out PointLatLngAlt home, out List<NavPoint> route)
        {
            home = null;
            route = new List<NavPoint>();
            if (!double.TryParse(TXT_homelat.Text, out var hlat) || !double.TryParse(TXT_homelng.Text, out var hlng) ||
                !double.TryParse(TXT_homealt.Text, out var halt) || (hlat == 0 && hlng == 0))
                return false;
            home = new PointLatLngAlt(hlat, hlng, halt / CurrentState.multiplieralt);

            if ((MAVLink.MAV_MISSION_TYPE) cmb_missiontype.SelectedValue != MAVLink.MAV_MISSION_TYPE.MISSION)
                return false;

            List<Locationwp> commands;
            try
            {
                commands = GetCommandList();
            }
            catch
            {
                return false;
            }

            double lastLat = hlat, lastLng = hlng;
            for (int i = 0; i < commands.Count; i++)
            {
                var c = commands[i];
                if (!navWithLocation.Contains(c.id))
                    continue;
                double lat = c.lat, lng = c.lng;
                // takeoff (and others) without a position happen where the vehicle is
                if (lat == 0 && lng == 0)
                {
                    lat = lastLat;
                    lng = lastLng;
                }

                route.Add(new NavPoint
                {
                    Label = (i + 1).ToString(), Lat = lat, Lng = lng, Alt = c.alt,
                    Frame = (MAVLink.MAV_FRAME) c.frame, Cmd = c.id
                });
                lastLat = lat;
                lastLng = lng;
            }

            return true;
        }

        static double? AmslOf(NavPoint p, double homeAlt)
        {
            if (IsAbsoluteFrame(p.Frame))
                return p.Alt;
            if (IsTerrainFrame(p.Frame))
            {
                var t = TerrainAt(p.Lat, p.Lng);
                return t == null ? (double?) null : t.Value + p.Alt;
            }

            return homeAlt + p.Alt;
        }

        static TerrainClearanceResult ComputeTerrainClearance(PointLatLngAlt home, List<NavPoint> route,
            double threshold)
        {
            var result = new TerrainClearanceResult {Threshold = threshold};
            if (route.Count == 0)
                return result;

            var homePoint = new NavPoint
            {
                Label = "Home", Lat = home.Lat, Lng = home.Lng, Alt = home.Alt,
                Frame = MAVLink.MAV_FRAME.GLOBAL, Cmd = 0
            };
            var points = new List<NavPoint> {homePoint};
            points.AddRange(route);

            for (int i = 1; i < points.Count; i++)
            {
                var a = points[i - 1];
                var b = points[i];
                var leg = new TerrainLeg {From = a.Label, To = b.Label};
                result.Legs.Add(leg);

                if (b.Cmd == (ushort) MAVLink.MAV_CMD.LAND || b.Cmd == (ushort) MAVLink.MAV_CMD.VTOL_LAND)
                {
                    leg.Skipped = true;
                    leg.SkipReason = "landing approach";
                    continue;
                }

                if (a == homePoint && b.Cmd != (ushort) MAVLink.MAV_CMD.TAKEOFF &&
                    b.Cmd != (ushort) MAVLink.MAV_CMD.VTOL_TAKEOFF)
                {
                    leg.Skipped = true;
                    leg.SkipReason = "climb-out from home";
                    continue;
                }

                if (b.Cmd == (ushort) MAVLink.MAV_CMD.TAKEOFF || b.Cmd == (ushort) MAVLink.MAV_CMD.VTOL_TAKEOFF)
                {
                    leg.Skipped = true;
                    leg.SkipReason = "takeoff climb";
                    continue;
                }

                bool bothTerrain = IsTerrainFrame(a.Frame) && IsTerrainFrame(b.Frame);
                double? amslA = bothTerrain ? 0 : AmslOf(a, home.Alt);
                double? amslB = bothTerrain ? 0 : AmslOf(b, home.Alt);
                if (amslA == null || amslB == null)
                {
                    leg.TerrainMissing = true;
                    continue;
                }

                var pa = new PointLatLngAlt(a.Lat, a.Lng);
                var pb = new PointLatLngAlt(b.Lat, b.Lng);
                double dist = pa.GetDistance(pb);
                int n = Math.Max(1, Math.Min(400, (int)Math.Ceiling(dist / 20.0)));

                List<PointLatLng> run = null;
                for (int s = 0; s <= n; s++)
                {
                    double t = s / (double) n;
                    double lat = a.Lat + (b.Lat - a.Lat) * t;
                    double lng = a.Lng + (b.Lng - a.Lng) * t;
                    double clearance;
                    if (bothTerrain)
                    {
                        // terrain-frame legs hold an interpolated height above terrain
                        clearance = a.Alt + (b.Alt - a.Alt) * t;
                    }
                    else
                    {
                        var ground = TerrainAt(lat, lng);
                        if (ground == null)
                        {
                            leg.TerrainMissing = true;
                            run = null;
                            continue;
                        }

                        clearance = amslA.Value + (amslB.Value - amslA.Value) * t - ground.Value;
                    }

                    if (clearance < leg.MinClearance)
                    {
                        leg.MinClearance = clearance;
                        leg.Worst = new PointLatLng(lat, lng);
                    }

                    if (clearance < threshold)
                    {
                        if (run == null)
                        {
                            run = new List<PointLatLng>();
                            leg.LowRuns.Add(run);
                        }

                        run.Add(new PointLatLng(lat, lng));
                    }
                    else
                    {
                        run = null;
                    }
                }
            }

            return result;
        }

        void ScheduleTerrainClearance(bool resetRetries)
        {
            if (terrainClearanceTimer == null)
                return;
            if (resetRetries)
                terrainClearanceRetries = 0;
            terrainClearanceTimer.Stop();
            terrainClearanceTimer.Start();
        }

        void UpdateTerrainClearance()
        {
            var generation = ++terrainClearanceGeneration;
            if (!TerrainCheckEnabled || !TryGetRoute(out var home, out var route) || route.Count == 0)
            {
                lastTerrainClearance = null;
                ClearOverlay(terrainClearanceOverlay);
                return;
            }

            var threshold = TerrainClearanceMetres;
            Task.Run(() =>
            {
                var result = ComputeTerrainClearance(home, route, threshold);
                BeginInvokeIfAlive(() =>
                {
                    if (generation != terrainClearanceGeneration)
                        return;
                    lastTerrainClearance = result;
                    DrawTerrainClearance(result);
                    if (result.AnyMissing && terrainClearanceRetries++ < 10)
                    {
                        terrainClearanceTimer.Interval = 3000;
                        ScheduleTerrainClearance(false);
                        terrainClearanceTimer.Interval = 500;
                    }
                });
            });
        }

        void DrawTerrainClearance(TerrainClearanceResult result)
        {
            ClearOverlay(terrainClearanceOverlay);
            foreach (var leg in result.LowLegs)
            {
                bool belowGround = leg.MinClearance < 0;
                var colour = belowGround ? Color.FromArgb(230, 200, 0, 0) : Color.FromArgb(220, 255, 60, 0);
                foreach (var run in leg.LowRuns)
                {
                    var pts = run.Count == 1 ? new List<PointLatLng> {run[0], run[0]} : run;
                    var r = new GMapRoute(pts, "terrainlow") {Stroke = new Pen(colour, 7)};
                    terrainClearanceOverlay.Routes.Add(r);
                }

                var m = new GMarkerGoogle(leg.Worst, GMarkerGoogleType.red_small)
                {
                    ToolTipText = (belowGround ? "HITS TERRAIN" : "Low terrain clearance") + "\nLeg " + leg.From +
                                  " -> " + leg.To + ": " + AltText(leg.MinClearance) + " above ground (min " +
                                  AltText(result.Threshold) + ")",
                    ToolTipMode = MarkerTooltipMode.Always,
                    IsHitTestVisible = false
                };
                terrainClearanceOverlay.Markers.Add(m);
            }

            MainMap.Invalidate();
        }

        static string DescribeClearance(TerrainClearanceResult r)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Minimum terrain clearance: " + AltText(r.Threshold));
            sb.AppendLine("(SRTM terrain; trees, buildings and wires are not included)");
            sb.AppendLine();
            foreach (var leg in r.Legs)
            {
                sb.Append("Leg " + leg.From + " -> " + leg.To + ": ");
                if (leg.Skipped)
                    sb.AppendLine("not checked (" + leg.SkipReason + ")");
                else if (leg.MinClearance == double.MaxValue)
                    sb.AppendLine(leg.TerrainMissing ? "terrain data not loaded yet" : "-");
                else
                    sb.AppendLine((leg.MinClearance < 0 ? "HITS TERRAIN, " : leg.MinClearance < r.Threshold ? "LOW, " : "") +
                                  "lowest " + AltText(leg.MinClearance) + " above ground" +
                                  (leg.TerrainMissing ? " (some terrain not loaded)" : ""));
            }

            return sb.ToString();
        }

        void ShowTerrainClearanceReport()
        {
            if (!TryGetRoute(out var home, out var route) || route.Count == 0)
            {
                CustomMessageBox.Show("Set a home location and add mission waypoints first.", "Terrain");
                return;
            }

            var result = ComputeTerrainClearance(home, route, TerrainClearanceMetres);
            lastTerrainClearance = result;
            DrawTerrainClearance(result);
            CustomMessageBox.Show(DescribeClearance(result), "Terrain clearance");
        }

        /// <summary>called before writing the mission; false cancels the write</summary>
        bool ConfirmTerrainClearanceBeforeWrite()
        {
            try
            {
                if (!TerrainCheckEnabled || !TryGetRoute(out var home, out var route) || route.Count == 0)
                    return true;

                var result = ComputeTerrainClearance(home, route, TerrainClearanceMetres);
                lastTerrainClearance = result;
                DrawTerrainClearance(result);

                var low = result.LowLegs.ToList();
                if (low.Count == 0)
                    return true;

                var sb = new StringBuilder();
                sb.AppendLine("This mission comes closer to terrain than " + AltText(result.Threshold) + ":");
                sb.AppendLine();
                foreach (var leg in low)
                    sb.AppendLine("Leg " + leg.From + " -> " + leg.To + ": " +
                                  (leg.MinClearance < 0 ? "HITS TERRAIN (" : "") + AltText(leg.MinClearance) +
                                  " above ground" + (leg.MinClearance < 0 ? ")" : ""));
                sb.AppendLine();
                sb.AppendLine("The low parts are marked in red on the map.");
                sb.AppendLine("Write the mission anyway?");

                return (int) DialogResult.Yes ==
                       CustomMessageBox.Show(sb.ToString(), "Terrain warning", MessageBoxButtons.YesNo);
            }
            catch (Exception ex)
            {
                log.Error(ex);
                return true;
            }
        }

        #endregion

        #region upload terrain to the vehicle

        void UploadTerrainToVehicle()
        {
            var mav = MainV2.comPort;
            if (mav?.BaseStream == null || !mav.BaseStream.IsOpen)
            {
                CustomMessageBox.Show("Connect to the vehicle first.", "Upload terrain");
                return;
            }

            if (mav.MAV.cs.armed)
            {
                CustomMessageBox.Show("The vehicle is ARMED. Disarm before uploading terrain.", "Upload terrain");
                return;
            }

            if (!mav.MAV.param.ContainsKey("TERRAIN_ENABLE"))
            {
                CustomMessageBox.Show(
                    "This firmware has no terrain database (no TERRAIN_ENABLE parameter), or the parameters " +
                    "have not finished loading.", "Upload terrain");
                return;
            }

            if (!TryGetRoute(out var home, out var route))
            {
                CustomMessageBox.Show("Set a home location first.", "Upload terrain");
                return;
            }

            int spacing = mav.MAV.param.ContainsKey("TERRAIN_SPACING")
                ? (int) mav.MAV.param["TERRAIN_SPACING"].Value
                : 100;
            if (spacing <= 0)
                spacing = 100;

            var points = new List<(double lat, double lng)> {(home.Lat, home.Lng)};
            points.AddRange(route.Select(a => (a.Lat, a.Lng)));
            if (mav.MAV.cs.Location != null && mav.MAV.cs.Location.Lat != 0 && mav.MAV.cs.Location.Lng != 0)
                points.Add((mav.MAV.cs.Location.Lat, mav.MAV.cs.Location.Lng));

            var bufferMiles = TerrainUploadBufferMiles;
            var blocks = TerrainDat.BlocksCovering(points, bufferMiles * MileMetres, spacing);
            if (blocks.Count == 0)
                return;

            var files = blocks.Select(a => TerrainDat.FileName(a.LatDegrees, a.LonDegrees)).Distinct().ToList();
            bool terrainEnabled = mav.MAV.param["TERRAIN_ENABLE"].Value != 0;

            double kb = blocks.Count * 2;
            var sizeText = kb >= 1024 ? (kb / 1024).ToString("0.0") + " MB" : kb.ToString("0") + " KB";
            var question = "Upload terrain for the whole mission area plus " + BufferText + " around it to the " +
                           "vehicle's SD card?\n\n" +
                           blocks.Count + " terrain blocks (" + sizeText + ") at " + spacing +
                           " m spacing (TERRAIN_SPACING), into " + string.Join(", ", files) + ".\n" +
                           "The area covers every waypoint and the straight-line return home from anywhere " +
                           "inside the mission.\n\n" +
                           "Over USB this takes about " + Math.Max(1, (int)Math.Ceiling(kb / 600)) + " minute(s); over a " +
                           "telemetry radio it can take roughly " + Math.Max(1, (int)Math.Ceiling(kb / 40)) +
                           " minutes, so connect by USB for big areas." +
                           (terrainEnabled
                               ? "\nTerrain is paused (TERRAIN_ENABLE=0) during the upload and turned back on afterwards."
                               : "\n\nTERRAIN_ENABLE is 0, so the vehicle will not use terrain until it is set to 1 " +
                                 "(the 'Set up terrain-following RTL' option does this).");

            if ((int) DialogResult.Yes != CustomMessageBox.Show(question, "Upload terrain", MessageBoxButtons.YesNo))
                return;

            string summary = null;
            var prd = new ProgressReporterDialogue
            {
                StartPosition = FormStartPosition.CenterScreen,
                Text = "Upload terrain"
            };
            prd.DoWork += sender =>
            {
                summary = UploadTerrainBlocks(sender, blocks, spacing, terrainEnabled);
            };
            prd.UpdateProgressAndStatus(-1, "Preparing terrain");
            ThemeManager.ApplyThemeTo(prd);
            prd.RunBackgroundOperationAsync();
            prd.Dispose();

            if (summary != null)
                CustomMessageBox.Show(summary, "Upload terrain");
        }

        string UploadTerrainBlocks(IProgressReporterDialogue sender, List<TerrainDat.BlockId> blocks, int spacing,
            bool pauseTerrain)
        {
            var mav = MainV2.comPort;
            var cancel = new CancellationTokenSource();

            // 1. generate the blocks, waiting for any terrain tiles that are still downloading
            var built = new Dictionary<TerrainDat.BlockId, byte[]>();
            var pending = new List<TerrainDat.BlockId>(blocks);
            var deadline = DateTime.Now.AddSeconds(120);
            while (pending.Count > 0)
            {
                var still = new List<TerrainDat.BlockId>();
                foreach (var b in pending)
                {
                    if (sender.doWorkArgs.CancelRequested)
                    {
                        sender.doWorkArgs.CancelAcknowledged = true;
                        return null;
                    }

                    var data = TerrainDat.BuildBlock(b, spacing, TerrainAt);
                    if (data == null)
                        still.Add(b);
                    else
                        built[b] = data;
                }

                sender.UpdateProgressAndStatus(built.Count * 30 / blocks.Count,
                    "Preparing terrain " + built.Count + "/" + blocks.Count +
                    (still.Count > 0 ? " (downloading terrain tiles)" : ""));

                pending = still;
                if (pending.Count == 0)
                    break;
                if (DateTime.Now > deadline)
                    break;
                Thread.Sleep(1000);
            }

            int skipped = pending.Count;
            if (built.Count == 0)
            {
                sender.doWorkArgs.ErrorMessage =
                    "No terrain data available for this area. Check the internet connection (terrain is " +
                    "downloaded from terrain.ardupilot.org) and try again.";
                return null;
            }

            // 2. write them into the DAT files on the SD card
            var ftp = new MAVFtp(mav, mav.MAV.sysid, mav.MAV.compid);
            bool paused = false;
            int written = 0;
            try
            {
                if (pauseTerrain)
                {
                    // stop the autopilot's own terrain disk IO while the files are written
                    paused = mav.setParam("TERRAIN_ENABLE", 0);
                }

                ftp.kCmdResetSessions();
                try
                {
                    ftp.kCmdCreateDirectory(TerrainDat.TERRAIN_DIRECTORY, cancel);
                }
                catch
                {
                    // already exists
                }

                foreach (var file in built.Keys.GroupBy(TerrainDat.FilePath))
                {
                    int size = 0;
                    bool open = false;
                    try
                    {
                        open = ftp.kCmdOpenFileWO(file.Key, ref size, cancel);
                    }
                    catch
                    {
                        open = false;
                    }

                    if (!open)
                    {
                        // new file (create truncates, so only when it does not exist yet)
                        ftp.kCmdResetSessions();
                        size = 0;
                        if (!ftp.kCmdCreateFile(file.Key, ref size, cancel))
                            throw new Exception("could not create " + file.Key);
                    }

                    // write in file order, merging blocks that sit next to each other in the file
                    var ordered = file.OrderBy(a => TerrainDat.FileOffset(a, spacing)).ToList();
                    int i = 0;
                    while (i < ordered.Count)
                    {
                        long start = TerrainDat.FileOffset(ordered[i], spacing);
                        var chunk = new List<byte>(built[ordered[i]]);
                        int j = i + 1;
                        while (j < ordered.Count && j - i < 16 &&
                               TerrainDat.FileOffset(ordered[j], spacing) == start + chunk.Count)
                        {
                            chunk.AddRange(built[ordered[j]]);
                            j++;
                        }

                        if (sender.doWorkArgs.CancelRequested)
                        {
                            sender.doWorkArgs.CancelAcknowledged = true;
                            return null;
                        }

                        if (!ftp.kCmdWriteFile(chunk.ToArray(), (uint) start, file.Key, cancel))
                            throw new Exception("write failed in " + file.Key);

                        written += j - i;
                        i = j;
                        sender.UpdateProgressAndStatus(30 + written * 70 / built.Count,
                            "Writing terrain " + written + "/" + built.Count);
                    }

                    ftp.kCmdResetSessions();
                }
            }
            catch (Exception ex)
            {
                log.Error(ex);
                sender.doWorkArgs.ErrorMessage = "Terrain upload failed after " + written + " blocks: " + ex.Message;
                return null;
            }
            finally
            {
                try
                {
                    ftp.kCmdResetSessions();
                }
                catch
                {
                }

                if (paused)
                    mav.setParam("TERRAIN_ENABLE", 1);
            }

            return "Uploaded " + written + " terrain blocks to " + TerrainDat.TERRAIN_DIRECTORY + "." +
                   (skipped > 0
                       ? "\n" + skipped + " blocks had no terrain data yet and were left for the vehicle " +
                         "to request in flight."
                       : "") +
                   "\n\nThe vehicle now has terrain for the mission area even if the ground station " +
                   "connection is lost.";
        }

        #endregion

        #region terrain-following RTL

        void SetupTerrainFollowingRtl()
        {
            var mav = MainV2.comPort;
            if (mav?.BaseStream == null || !mav.BaseStream.IsOpen)
            {
                CustomMessageBox.Show("Connect to the vehicle first.", "Terrain-following RTL");
                return;
            }

            var p = mav.MAV.param;
            if (!p.ContainsKey("TERRAIN_ENABLE"))
            {
                CustomMessageBox.Show("This firmware has no terrain support, or parameters are still loading.",
                    "Terrain-following RTL");
                return;
            }

            double minAgl = Math.Round(30 * CurrentState.multiplieralt);
            if (InputBox.Show("Terrain-following RTL",
                    "Minimum height above terrain during RTL (" +
                    (string.IsNullOrEmpty(CurrentState.AltUnit) ? "m" : CurrentState.AltUnit) + ")",
                    ref minAgl) != DialogResult.OK)
                return;
            minAgl /= CurrentState.multiplieralt;
            if (minAgl < 10)
            {
                CustomMessageBox.Show("Use at least 10 m.", "Terrain-following RTL");
                return;
            }

            var changes = new List<(string name, double from, double to, string note)>();

            void Want(string name, double to, string note)
            {
                if (!p.ContainsKey(name))
                    return;
                var from = p[name].Value;
                if (Math.Abs(from - to) > 0.0001)
                    changes.Add((name, from, to, note));
            }

            if (p.ContainsKey("TERRAIN_FOLLOW") && mav.MAV.cs.firmware == Firmwares.ArduPlane)
            {
                // Plane: TERRAIN_FOLLOW bit 4 = RTL (bit 0 = all modes); RTL altitude becomes height above terrain
                Want("TERRAIN_ENABLE", 1, "use the terrain database");
                var follow = (int) p["TERRAIN_FOLLOW"].Value;
                if ((follow & 1) == 0)
                    Want("TERRAIN_FOLLOW", follow | 16, "terrain following in RTL");

                if (p.ContainsKey("RTL_ALTITUDE"))
                {
                    var cur = p["RTL_ALTITUDE"].Value;
                    if (cur < minAgl)
                        Want("RTL_ALTITUDE", minAgl, "RTL height above terrain (m)");
                }
                else if (p.ContainsKey("ALT_HOLD_RTL"))
                {
                    // older Plane firmware, centimetres
                    var cur = p["ALT_HOLD_RTL"].Value;
                    if (cur < minAgl * 100)
                        Want("ALT_HOLD_RTL", minAgl * 100, "RTL height above terrain (cm)");
                }
            }
            else if (p.ContainsKey("RTL_ALT_TYPE"))
            {
                // Copter: RTL_ALT_TYPE 1 = RTL_ALT is above terrain
                Want("TERRAIN_ENABLE", 1, "use the terrain database");
                Want("RTL_ALT_TYPE", 1, "RTL altitude is above terrain");
                if (p.ContainsKey("RTL_ALT") && p["RTL_ALT"].Value < minAgl * 100)
                    Want("RTL_ALT", minAgl * 100, "RTL height above terrain (cm)");
            }
            else
            {
                CustomMessageBox.Show("Terrain-following RTL is not supported by this vehicle type.",
                    "Terrain-following RTL");
                return;
            }

            if (changes.Count == 0)
            {
                CustomMessageBox.Show("Already set: RTL follows terrain at " + AltText(minAgl) +
                                      " or more above the ground.", "Terrain-following RTL");
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine("These parameters will be changed:");
            sb.AppendLine();
            foreach (var c in changes)
                sb.AppendLine(c.name + ":  " + c.from + "  ->  " + c.to + "   (" + c.note + ")");
            sb.AppendLine();
            sb.AppendLine("In RTL the vehicle will then hold at least " + AltText(minAgl) +
                          " above the terrain on its way home, using the terrain on its SD card or from this " +
                          "ground station. Upload terrain for the mission area first so it works even if the " +
                          "link is lost. Rally points keep their own altitudes.");
            sb.AppendLine();
            sb.AppendLine("Apply?");

            if ((int) DialogResult.Yes !=
                CustomMessageBox.Show(sb.ToString(), "Terrain-following RTL", MessageBoxButtons.YesNo))
                return;

            var failed = new List<string>();
            foreach (var c in changes)
            {
                try
                {
                    if (!mav.setParam(c.name, c.to))
                        failed.Add(c.name);
                }
                catch (Exception ex)
                {
                    log.Error(ex);
                    failed.Add(c.name);
                }
            }

            CustomMessageBox.Show(
                failed.Count == 0
                    ? "Done. RTL now follows the terrain at " + AltText(minAgl) + " or more above the ground."
                    : "Could not set: " + string.Join(", ", failed), "Terrain-following RTL");
        }

        #endregion

        void ClearOverlay(GMapOverlay overlay)
        {
            if (overlay == null)
                return;
            foreach (var m in overlay.Markers.ToList())
                m.Dispose();
            overlay.Markers.Clear();
            overlay.Routes.Clear();
            MainMap?.Invalidate();
        }

        void BeginInvokeIfAlive(Action action)
        {
            try
            {
                if (IsDisposed || Disposing || !IsHandleCreated)
                    return;
                BeginInvoke(action);
            }
            catch (Exception ex)
            {
                log.Debug(ex);
            }
        }
    }
}
