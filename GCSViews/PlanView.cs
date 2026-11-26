using GMap.NET;
using GMap.NET.WindowsForms;
using log4net;
using MissionPlanner.Controls;
using MissionPlanner.Utilities;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MissionPlanner.GCSViews
{
    /// <summary>
    /// "QPlan": QGroundControl-style mission planning. Map with orange numbered markers (selected = green),
    /// left tool strip (Takeoff, Waypoint, ROI, Land, RTL), mission list + item editor on the right,
    /// terrain/altitude profile along the bottom. Altitudes in feet. Edits the same mission as the classic
    /// Flight Planner. Loiter radii are never allowed below the plane's minimum turn radius.
    /// </summary>
    public class PlanView : MyUserControl, IActivate, IDeactivate
    {
        static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        const double FtPerM = 3.28084;

        static readonly Color Dark = Color.FromArgb(22, 23, 26);
        static readonly Color Panel1 = Color.FromArgb(30, 32, 36);
        static readonly Color Panel2 = Color.FromArgb(40, 43, 48);
        static readonly Color Accent = Color.FromArgb(40, 140, 230);
        static readonly Color OrangeText = Color.FromArgb(255, 160, 20);

        readonly myGMAP map = new myGMAP();
        readonly Panel topBar = new Panel();
        readonly Panel toolStrip = new Panel();
        readonly Panel right = new Panel();
        readonly Panel rightHeader = new Panel();
        readonly Panel list = new Panel();
        readonly Panel detail = new Panel();
        readonly Label summary = new Label();
        readonly Label selectedAlt = new Label();
        readonly PlanProfileControl profileView = new PlanProfileControl();
        readonly GMapOverlay routeOverlay = new GMapOverlay("qplanroute");
        readonly GMapOverlay markerOverlay = new GMapOverlay("qplanmarkers");
        readonly Timer commitTimer = new Timer {Interval = 350};
        readonly Timer profileTimer = new Timer {Interval = 300};
        readonly Dictionary<string, Button> tools = new Dictionary<string, Button>();

        PointLatLngAlt home = new PointLatLngAlt(0, 0, 0, "H");
        List<Locationwp> items = new List<Locationwp>();
        int selected = -2; // -1 = mission start (home), -2 = nothing
        string addTool; // "Waypoint" / "ROI" while click-to-add is active
        bool suppressEvents;
        int profileGeneration;
        int profileRetries;
        MissionProfile lastProfile;
        string notice = "";

        // live radius editors, updated while dragging the map handle
        InlineNumber radiusBox;
        InlineNumber detailAltBox;
        TrackBar detailAltSlider;
        TrackBar radiusSlider;
        bool radiusDrag;

        // mouse
        GMapMarker hoverMarker;
        int dragIndex = -3;
        bool mouseDown;
        bool mouseMoved;
        Point mouseDownStart;
        Point lastMouse;

        static readonly (string name, MAVLink.MAV_CMD cmd)[] commandChoices =
        {
            ("Waypoint", MAVLink.MAV_CMD.WAYPOINT),
            ("Spline waypoint", MAVLink.MAV_CMD.SPLINE_WAYPOINT),
            ("Loiter (turns)", MAVLink.MAV_CMD.LOITER_TURNS),
            ("Loiter (time)", MAVLink.MAV_CMD.LOITER_TIME),
            ("Loiter (unlimited)", MAVLink.MAV_CMD.LOITER_UNLIM),
            ("Loiter to altitude", MAVLink.MAV_CMD.LOITER_TO_ALT),
            ("Takeoff", MAVLink.MAV_CMD.TAKEOFF),
            ("Land", MAVLink.MAV_CMD.LAND),
            ("Region of interest", MAVLink.MAV_CMD.DO_SET_ROI),
            ("Return to launch", MAVLink.MAV_CMD.RETURN_TO_LAUNCH)
        };

        static readonly (string name, MAVLink.MAV_FRAME frame)[] frameChoices =
        {
            ("Rel", MAVLink.MAV_FRAME.GLOBAL_RELATIVE_ALT),
            ("Terrain", MAVLink.MAV_FRAME.GLOBAL_TERRAIN_ALT),
            ("AMSL", MAVLink.MAV_FRAME.GLOBAL)
        };

        public PlanView()
        {
            BackColor = Dark;
            Dock = DockStyle.Fill;

            // the map is added first so it fills whatever the docked panels leave
            map.Dock = DockStyle.Fill;
            map.MinZoom = 0;
            map.MaxZoom = 24;
            map.MarkersEnabled = true;
            map.RoutesEnabled = true;
            map.CanDragMap = false;
            map.PolygonsEnabled = true;
            PlaceLabels.Attach(map);
            map.Overlays.Add(routeOverlay);
            map.Overlays.Add(markerOverlay);
            map.OnMarkerEnter += m => hoverMarker = m;
            map.OnMarkerLeave += m =>
            {
                if (hoverMarker == m)
                    hoverMarker = null;
            };
            map.MouseDown += Map_MouseDown;
            map.MouseMove += Map_MouseMove;
            map.MouseUp += Map_MouseUp;
            Controls.Add(map);

            profileView.Dock = DockStyle.Bottom;
            profileView.Height = 170;
            profileView.ItemClicked += i => SelectItem(i);
            Controls.Add(profileView);

            BuildToolStrip();
            Controls.Add(toolStrip);

            BuildRightPanel();
            Controls.Add(right);

            BuildTopBar();
            Controls.Add(topBar);

            commitTimer.Tick += (s, e) =>
            {
                commitTimer.Stop();
                CommitNow();
            };
            profileTimer.Tick += (s, e) =>
            {
                profileTimer.Stop();
                UpdateProfile();
            };
        }

        #region layout

        static Button FlatButton(string text, int w, int h)
        {
            var b = new Button
            {
                Text = text, Width = w, Height = h, FlatStyle = FlatStyle.Flat, ForeColor = Color.White,
                BackColor = Panel2, Font = new Font("Arial", 9.5f)
            };
            b.FlatAppearance.BorderColor = Color.FromArgb(70, 74, 80);
            return b;
        }

        int barX;

        void BarAdd(string text, int w, EventHandler click, Color? back = null)
        {
            var b = FlatButton(text, w, 34);
            b.Location = new Point(barX, 8);
            if (back.HasValue)
                b.BackColor = back.Value;
            b.Click += click;
            topBar.Controls.Add(b);
            barX += w + 8;
        }

        void BuildTopBar()
        {
            topBar.Dock = DockStyle.Top;
            topBar.Height = 50;
            topBar.BackColor = Dark;
            barX = 10;
            BarAdd("Open", 90, (s, e) => OpenFile());
            BarAdd("Save", 90, (s, e) => SaveFile(), Accent);
            BarAdd("Upload", 100, (s, e) => Upload());
            BarAdd("Download", 100, (s, e) => Download());
            BarAdd("Clear", 90, (s, e) => ClearMission());
            BarAdd("Classic planner", 130, (s, e) => MainV2.View.ShowScreen("FlightPlanner"));

            var labels = new CheckBox
            {
                Text = "Town names", ForeColor = Color.White, Checked = PlaceLabels.Enabled, AutoSize = true,
                Location = new Point(barX + 4, 15), Font = new Font("Arial", 9.5f)
            };
            labels.CheckedChanged += (s, e) => PlaceLabels.Enabled = labels.Checked;
            topBar.Controls.Add(labels);
            barX += 120;

            var search = new MapSearchBox(map, 320) {Location = new Point(barX + 12, 10)};
            topBar.Controls.Add(search);
        }

        int toolY;

        void Tool(string name, string glyph, EventHandler click)
        {
            var b = FlatButton(glyph + "\n" + name, 80, 64);
            b.Location = new Point(6, toolY);
            b.Font = new Font("Arial", 9f);
            b.Click += click;
            toolStrip.Controls.Add(b);
            tools[name] = b;
            toolY += 70;
        }

        void BuildToolStrip()
        {
            toolStrip.Dock = DockStyle.Left;
            toolStrip.Width = 92;
            toolStrip.BackColor = Panel1;
            toolY = 10;
            Tool("Takeoff", "⇧", (s, e) => AddTakeoff());
            Tool("Waypoint", "⌖", (s, e) => SetAddTool(addTool == "Waypoint" ? null : "Waypoint"));
            Tool("ROI", "◎", (s, e) => SetAddTool(addTool == "ROI" ? null : "ROI"));
            Tool("Land", "⇩", (s, e) => AddLand());
            Tool("RTL", "⌂", (s, e) => AddRtl());

            toolY += 6;
            var lbl = new Label
            {
                Text = "Default alt", ForeColor = Color.Gainsboro, Font = new Font("Arial", 8.5f),
                Location = new Point(6, toolY), AutoSize = true
            };
            toolStrip.Controls.Add(lbl);
            toolY += 18;
            var def = new InlineNumber
            {
                Location = new Point(6, toolY), Width = 80, Minimum = 0, Maximum = 30000, Increment = 5,
                Unit = "ft", Font = new Font("Arial", 9.5f)
            };
            def.Value = (decimal) Math.Round(DefaultAltMetres * FtPerM);
            def.ValueChanged += (s, e) =>
            {
                if (def.Value > 0)
                    DefaultAltMetres = (double) def.Value / FtPerM;
            };
            toolStrip.Controls.Add(def);
            toolY += 32;
            var all = FlatButton("Set all", 80, 28);
            all.Font = new Font("Arial", 8.5f);
            all.Location = new Point(6, toolY);
            all.Click += (s, e) => ApplyDefaultAltToAll();
            toolStrip.Controls.Add(all);
            toolY += 34;
        }

        void ApplyDefaultAltToAll()
        {
            var alt = (float) DefaultAltMetres;
            if ((int) DialogResult.Yes != CustomMessageBox.Show(
                    "Set every waypoint and loiter to " + FtText(alt) + "?", "QPlan", MessageBoxButtons.YesNo))
                return;
            for (int i = 0; i < items.Count; i++)
            {
                var wp = items[i];
                if (!MissionProfile.IsNavWithLocation(wp.id) || wp.id == (ushort) MAVLink.MAV_CMD.LAND ||
                    wp.id == (ushort) MAVLink.MAV_CMD.VTOL_LAND)
                    continue;
                wp.alt = alt;
                items[i] = wp;
            }

            Changed(true);
        }

        void BuildRightPanel()
        {
            right.Dock = DockStyle.Right;
            right.Width = 380;
            right.BackColor = Panel1;

            rightHeader.Dock = DockStyle.Top;
            rightHeader.Height = 44;
            rightHeader.BackColor = Dark;
            summary.Location = new Point(10, 6);
            summary.Size = new Size(250, 34);
            summary.ForeColor = OrangeText;
            summary.Font = new Font("Arial", 9f);
            rightHeader.Controls.Add(summary);
            selectedAlt.Location = new Point(266, 10);
            selectedAlt.Size = new Size(106, 24);
            selectedAlt.ForeColor = Color.White;
            selectedAlt.BackColor = Panel2;
            selectedAlt.TextAlign = ContentAlignment.MiddleCenter;
            rightHeader.Controls.Add(selectedAlt);

            detail.Dock = DockStyle.Bottom;
            detail.Height = 360;
            detail.AutoScroll = false;
            detail.BackColor = Panel1;

            list.Dock = DockStyle.Fill;
            list.AutoScroll = true;
            list.BackColor = Panel1;

            // fill first, then the docked edges
            right.Controls.Add(list);
            right.Controls.Add(detail);
            right.Controls.Add(rightHeader);
        }

        #endregion

        #region activation / sync with the classic planner

        public void Activate()
        {
            try
            {
                var fp = MainV2.instance?.FlightPlanner;
                if (fp != null)
                {
                    map.MapProvider = fp.MainMap.MapProvider;
                    if (map.Position.Lat == 0 && map.Position.Lng == 0)
                    {
                        map.Position = fp.MainMap.Position;
                        map.Zoom = Math.Max(3, fp.MainMap.Zoom);
                    }
                }

                LoadFromPlanner();

                // nothing to show yet: go back to where the map was last time
                if ((map.Position.Lat == 0 && map.Position.Lng == 0) || map.Zoom < 4)
                {
                    var lat = Settings.Instance.GetDouble("QPlan_MapLat", 0);
                    var lng = Settings.Instance.GetDouble("QPlan_MapLng", 0);
                    if (lat != 0 || lng != 0)
                    {
                        map.Position = new PointLatLng(lat, lng);
                        map.Zoom = Settings.Instance.GetDouble("QPlan_MapZoom", 12);
                    }
                }

                if (map.Zoom < 5)
                {
                    if (home.Lat != 0 || home.Lng != 0)
                    {
                        map.Position = home;
                        map.Zoom = 15;
                    }
                    else if (MainV2.comPort?.MAV?.cs?.Location != null && MainV2.comPort.MAV.cs.Location.Lat != 0)
                    {
                        map.Position = MainV2.comPort.MAV.cs.Location;
                        map.Zoom = 15;
                    }
                }
            }
            catch (Exception ex)
            {
                log.Error(ex);
            }
        }

        public void Deactivate()
        {
            SaveMapView();

            if (commitTimer.Enabled)
            {
                commitTimer.Stop();
                CommitNow();
            }

            SetAddTool(null);
        }

        void SaveMapView()
        {
            try
            {
                if (map.Position.Lat == 0 && map.Position.Lng == 0)
                    return;
                var ci = System.Globalization.CultureInfo.InvariantCulture;
                Settings.Instance["QPlan_MapLat"] = map.Position.Lat.ToString(ci);
                Settings.Instance["QPlan_MapLng"] = map.Position.Lng.ToString(ci);
                Settings.Instance["QPlan_MapZoom"] = map.Zoom.ToString(ci);
            }
            catch
            {
            }
        }

        void LoadFromPlanner()
        {
            var fp = MainV2.instance?.FlightPlanner;
            if (fp == null || !fp.TryGetMissionForPlan(out var h, out var l))
                return;
            home = h;
            items = l;
            if (selected >= items.Count)
                selected = -2;

            var raised = EnforceMinLoiterRadius();
            notice = raised > 0
                ? raised + " loiter" + (raised > 1 ? "s" : "") + " raised to the minimum radius (" +
                  FtText(MinLoiterRadiusMetres()) + ")"
                : "";
            if (raised > 0)
                CommitNow();
            RefreshAll();
        }

        void CommitNow()
        {
            try
            {
                MainV2.instance?.FlightPlanner?.SetMissionFromPlan(home, new List<Locationwp>(items));
            }
            catch (Exception ex)
            {
                log.Error(ex);
            }
        }

        /// <summary>structural = the list of items changed (rebuild everything); else just values</summary>
        void Changed(bool structural)
        {
            if (structural)
            {
                commitTimer.Stop();
                // after the current event: it may come from a control the rebuild disposes
                Action apply = () =>
                {
                    CommitNow();
                    RefreshAll();
                };
                if (IsHandleCreated)
                    BeginInvoke(apply);
                else
                    apply();
            }
            else
            {
                commitTimer.Stop();
                commitTimer.Start();
                RedrawMap();
                RebuildList();
                UpdateSummary();
                ScheduleProfile();
            }
        }

        void RefreshAll()
        {
            RedrawMap();
            RebuildList();
            RebuildDetail();
            UpdateSummary();
            ScheduleProfile();
        }

        #endregion

        #region minimum loiter radius

        /// <summary>user override of the plane's minimum loiter radius, metres (0 = work it out)</summary>
        static double MinLoiterOverride
        {
            get => Settings.Instance.GetDouble("QPlan_MinLoiterRadius", 0);
            set => Settings.Instance["QPlan_MinLoiterRadius"] =
                value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Tightest circle the plane can fly: r = v^2 / (g tan(bank)) at cruise airspeed and the roll limit,
        /// unless the user set their own value. 0 when unknown (not connected, not a plane).
        /// </summary>
        public static double MinLoiterRadiusMetres()
        {
            if (MinLoiterOverride > 0)
                return MinLoiterOverride;
            return AutoMinLoiterRadius(out _);
        }

        static double AutoMinLoiterRadius(out string how)
        {
            how = "";
            var p = MainV2.comPort?.MAV?.param;
            if (p == null)
                return 0;
            double speed = 0, roll = 0;
            if (p.ContainsKey("AIRSPEED_CRUISE"))
                speed = p["AIRSPEED_CRUISE"].Value;
            else if (p.ContainsKey("TRIM_ARSPD_CM"))
                speed = p["TRIM_ARSPD_CM"].Value / 100.0;
            if (p.ContainsKey("ROLL_LIMIT_DEG"))
                roll = p["ROLL_LIMIT_DEG"].Value;
            else if (p.ContainsKey("LIM_ROLL_CD"))
                roll = p["LIM_ROLL_CD"].Value / 100.0;
            if (speed <= 0 || roll <= 5 || roll >= 89)
                return 0;
            how = "cruise " + (speed * 2.23694).ToString("0") + " mph, bank " + roll.ToString("0") + "°";
            return speed * speed / (9.80665 * Math.Tan(roll * Math.PI / 180));
        }

        static bool IsLoiter(ushort id) =>
            id == (ushort) MAVLink.MAV_CMD.LOITER_TURNS || id == (ushort) MAVLink.MAV_CMD.LOITER_TIME ||
            id == (ushort) MAVLink.MAV_CMD.LOITER_UNLIM || id == (ushort) MAVLink.MAV_CMD.LOITER_TO_ALT;

        static int RadiusParam(ushort id) => id == (ushort) MAVLink.MAV_CMD.LOITER_TO_ALT ? 2 : 3;

        static float GetParam(Locationwp wp, int n) => n == 1 ? wp.p1 : n == 2 ? wp.p2 : n == 3 ? wp.p3 : wp.p4;

        static void PutParam(ref Locationwp wp, int n, float v)
        {
            switch (n)
            {
                case 1: wp.p1 = v; break;
                case 2: wp.p2 = v; break;
                case 3: wp.p3 = v; break;
                default: wp.p4 = v; break;
            }
        }

        static double DefaultLoiterRadius()
        {
            var p = MainV2.comPort?.MAV?.param;
            if (p != null && p.ContainsKey("WP_LOITER_RAD"))
                return Math.Abs(p["WP_LOITER_RAD"].Value);
            return 0;
        }

        /// <summary>radius the vehicle will actually fly, metres</summary>
        static double LoiterRadius(Locationwp wp)
        {
            var r = Math.Abs(GetParam(wp, RadiusParam(wp.id)));
            return r < 1 ? DefaultLoiterRadius() : r;
        }

        /// <summary>radius to draw: the real one, else the minimum, else 60 m so there is something to drag</summary>
        static double DisplayRadius(Locationwp wp)
        {
            var r = LoiterRadius(wp);
            if (r >= 1)
                return r;
            var min = MinLoiterRadiusMetres();
            return min > 0 ? min : 60;
        }

        /// <summary>set a loiter's radius (metres), keeping its direction and never below the minimum</summary>
        void SetLoiterRadius(int index, double metres)
        {
            var item = items[index];
            int n = RadiusParam(item.id);
            bool ccw = CounterClockwise(item);
            var r = Math.Max(metres, MinLoiterRadiusMetres());
            r = Math.Max(1, r);
            PutParam(ref item, n, (float) (ccw ? -r : r));
            items[index] = item;
        }

        void ShowRadiusInEditors(double metres)
        {
            suppressEvents = true;
            try
            {
                var ft = (decimal) Math.Round(metres * FtPerM);
                if (radiusBox != null && !radiusBox.IsDisposed)
                    radiusBox.Value = Math.Max(radiusBox.Minimum, Math.Min(radiusBox.Maximum, ft));
                if (radiusSlider != null && !radiusSlider.IsDisposed)
                {
                    if ((int) ft > radiusSlider.Maximum)
                        radiusSlider.Maximum = (int) (Math.Ceiling((double) ft / 500) * 500);
                    radiusSlider.Value = Math.Max(radiusSlider.Minimum, Math.Min(radiusSlider.Maximum, (int) ft));
                }
            }
            finally
            {
                suppressEvents = false;
            }
        }

        static bool CounterClockwise(Locationwp wp) =>
            GetParam(wp, RadiusParam(wp.id)) < 0 ||
            (Math.Abs(GetParam(wp, RadiusParam(wp.id))) < 1 && DefaultLoiterRadiusIsCcw());

        static bool DefaultLoiterRadiusIsCcw()
        {
            var p = MainV2.comPort?.MAV?.param;
            return p != null && p.ContainsKey("WP_LOITER_RAD") && p["WP_LOITER_RAD"].Value < 0;
        }

        /// <summary>raise every loiter below the minimum radius to it; returns how many changed</summary>
        int EnforceMinLoiterRadius()
        {
            var min = MinLoiterRadiusMetres();
            if (min <= 0)
                return 0;
            int changed = 0;
            for (int i = 0; i < items.Count; i++)
            {
                var wp = items[i];
                if (!IsLoiter(wp.id))
                    continue;
                if (LoiterRadius(wp) + 0.01 >= min)
                    continue;
                bool ccw = CounterClockwise(wp);
                PutParam(ref wp, RadiusParam(wp.id), (float) (ccw ? -Math.Ceiling(min) : Math.Ceiling(min)));
                items[i] = wp;
                changed++;
            }

            return changed;
        }

        #endregion

        #region tools

        void SetAddTool(string tool)
        {
            addTool = tool;
            foreach (var kv in tools)
                kv.Value.BackColor = kv.Key == tool ? Accent : Panel2;
            map.Cursor = tool != null ? Cursors.Cross : Cursors.Default;
        }

        /// <summary>altitude new waypoints get (metres, relative to home); set in the left tool strip</summary>
        static double DefaultAltMetres
        {
            get
            {
                var v = Settings.Instance.GetDouble("QPlan_DefaultAltM", 0);
                if (v > 0)
                    return v;
                return MainV2.instance?.FlightPlanner?.DefaultAltitudeMetres ?? 100;
            }
            set => Settings.Instance["QPlan_DefaultAltM"] =
                value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        double LastAltitude()
        {
            for (int i = items.Count - 1; i >= 0; i--)
                if (MissionProfile.IsNavWithLocation(items[i].id) && items[i].id != (ushort) MAVLink.MAV_CMD.LAND &&
                    items[i].id != (ushort) MAVLink.MAV_CMD.VTOL_LAND)
                    return items[i].alt;
            return DefaultAltMetres;
        }

        void InsertItem(int at, Locationwp wp)
        {
            at = Math.Max(0, Math.Min(items.Count, at));
            items.Insert(at, wp);
            selected = at;
            Changed(true);
        }

        int InsertPosition() => selected >= 0 && selected < items.Count - 1 ? selected + 1 : items.Count;

        void AddAt(PointLatLng p)
        {
            if (home.Lat == 0 && home.Lng == 0)
                SetHome(p);

            if (addTool == "ROI")
            {
                InsertItem(InsertPosition(), new Locationwp
                {
                    id = (ushort) MAVLink.MAV_CMD.DO_SET_ROI, lat = p.Lat, lng = p.Lng, alt = 0,
                    frame = (byte) MAVLink.MAV_FRAME.GLOBAL_RELATIVE_ALT
                });
                SetAddTool(null);
                return;
            }

            InsertItem(InsertPosition(), new Locationwp
            {
                id = (ushort) MAVLink.MAV_CMD.WAYPOINT, lat = p.Lat, lng = p.Lng, alt = (float) DefaultAltMetres,
                frame = (byte) MAVLink.MAV_FRAME.GLOBAL_RELATIVE_ALT
            });
        }

        void AddTakeoff()
        {
            if (items.Any(a => a.id == (ushort) MAVLink.MAV_CMD.TAKEOFF || a.id == (ushort) MAVLink.MAV_CMD.VTOL_TAKEOFF))
            {
                CustomMessageBox.Show("The mission already has a takeoff.", "QPlan");
                return;
            }

            InsertItem(0, new Locationwp
            {
                id = (ushort) MAVLink.MAV_CMD.TAKEOFF, p1 = 15, lat = 0, lng = 0,
                alt = (float) DefaultAltMetres,
                frame = (byte) MAVLink.MAV_FRAME.GLOBAL_RELATIVE_ALT
            });
        }

        void AddLand()
        {
            if (home.Lat == 0 && home.Lng == 0)
            {
                CustomMessageBox.Show("Set home first (right-click the map).", "QPlan");
                return;
            }

            InsertItem(items.Count, new Locationwp
            {
                id = (ushort) MAVLink.MAV_CMD.LAND, lat = home.Lat, lng = home.Lng, alt = 0,
                frame = (byte) MAVLink.MAV_FRAME.GLOBAL_RELATIVE_ALT
            });
        }

        void AddRtl()
        {
            InsertItem(items.Count, new Locationwp
            {
                id = (ushort) MAVLink.MAV_CMD.RETURN_TO_LAUNCH, frame = (byte) MAVLink.MAV_FRAME.GLOBAL_RELATIVE_ALT
            });
        }

        void ClearMission()
        {
            if (items.Count == 0)
                return;
            if ((int) DialogResult.Yes !=
                CustomMessageBox.Show("Remove all mission items?", "QPlan", MessageBoxButtons.YesNo))
                return;
            items.Clear();
            selected = -2;
            Changed(true);
        }

        void Upload()
        {
            commitTimer.Stop();
            CommitNow();
            try
            {
                MainV2.instance.FlightPlanner.BUT_write_Click(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                CustomMessageBox.Show("Upload failed: " + ex.Message, Strings.ERROR);
            }
        }

        void Download()
        {
            try
            {
                MainV2.instance.FlightPlanner.BUT_read_Click(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                CustomMessageBox.Show("Download failed: " + ex.Message, Strings.ERROR);
            }

            selected = -2;
            LoadFromPlanner();
        }

        void OpenFile()
        {
            try
            {
                MainV2.instance.FlightPlanner.BUT_loadwpfile_Click(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                CustomMessageBox.Show("Open failed: " + ex.Message, Strings.ERROR);
            }

            selected = -2;
            LoadFromPlanner();
        }

        void SaveFile()
        {
            commitTimer.Stop();
            CommitNow();
            try
            {
                MainV2.instance.FlightPlanner.BUT_saveWPFile_Click(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                CustomMessageBox.Show("Save failed: " + ex.Message, Strings.ERROR);
            }
        }

        void SetHome(PointLatLng p)
        {
            var t = MissionProfile.TerrainAt(p.Lat, p.Lng);
            home = new PointLatLngAlt(p.Lat, p.Lng, double.IsNaN(t) ? home.Alt : t, "H");
        }

        void DeleteItem(int index)
        {
            if (index < 0 || index >= items.Count)
                return;
            items.RemoveAt(index);
            selected = items.Count == 0 ? -2 : Math.Min(index, items.Count - 1);
            Changed(true);
        }

        void MoveItem(int index, int delta)
        {
            int to = index + delta;
            if (index < 0 || to < 0 || to >= items.Count)
                return;
            var wp = items[index];
            items.RemoveAt(index);
            items.Insert(to, wp);
            selected = to;
            Changed(true);
        }

        #endregion

        #region map

        static string FtText(double metres) => (metres * FtPerM).ToString("0") + " ft";

        static string MarkerText(Locationwp wp)
        {
            if (wp.id == (ushort) MAVLink.MAV_CMD.DO_SET_ROI)
                return "ROI";
            var s = FtText(wp.alt);
            if (MissionProfile.IsTerrainFrame(wp.frame))
                s += " AGL";
            else if (MissionProfile.IsAbsoluteFrame(wp.frame))
                s += " MSL";
            if (IsLoiter(wp.id))
            {
                var r = LoiterRadius(wp);
                s = "Loiter " + s + (r > 0 ? " · r " + FtText(r) : "");
            }

            return s;
        }

        static string MarkerLabel(int i, Locationwp wp)
        {
            switch ((MAVLink.MAV_CMD) wp.id)
            {
                case MAVLink.MAV_CMD.TAKEOFF:
                case MAVLink.MAV_CMD.VTOL_TAKEOFF:
                    return "T";
                case MAVLink.MAV_CMD.LAND:
                case MAVLink.MAV_CMD.VTOL_LAND:
                    return "L";
                case MAVLink.MAV_CMD.DO_SET_ROI:
                    return "R";
                default:
                    return (i + 1).ToString();
            }
        }

        void RedrawMap()
        {
            routeOverlay.Routes.Clear();
            foreach (var m in markerOverlay.Markers.ToList())
                m.Dispose();
            markerOverlay.Markers.Clear();

            var path = new List<(PointLatLng p, int index)>();
            bool hasHome = home.Lat != 0 || home.Lng != 0;
            if (hasHome)
                path.Add((home, -1));

            bool endsWithRtl = false;
            var itemMarkers = new List<GMapMarker>();
            for (int i = 0; i < items.Count; i++)
            {
                var wp = items[i];
                if (wp.id == (ushort) MAVLink.MAV_CMD.RETURN_TO_LAUNCH)
                {
                    endsWithRtl = true;
                    continue;
                }

                bool roi = wp.id == (ushort) MAVLink.MAV_CMD.DO_SET_ROI;
                if (!roi && !MissionProfile.IsNavWithLocation(wp.id))
                    continue;
                if (wp.lat == 0 && wp.lng == 0)
                    continue; // takeoff/land in place

                var p = new PointLatLng(wp.lat, wp.lng);
                if (!roi)
                {
                    path.Add((p, i));
                    endsWithRtl = false;
                }

                itemMarkers.Add(new GMapMarkerPlanItem(p)
                {
                    ItemIndex = i, Label = MarkerLabel(i, wp), Command = wp.id, Selected = selected == i,
                    AltText = MarkerText(wp),
                    LoiterRadiusMetres = IsLoiter(wp.id) ? DisplayRadius(wp) : 0,
                    LoiterCounterClockwise = IsLoiter(wp.id) && CounterClockwise(wp)
                });
            }

            if (path.Count >= 2)
            {
                routeOverlay.Routes.Add(new GMapRoute(path.Select(a => a.p).ToList(), "qplan")
                    {Stroke = new Pen(GMapMarkerPlanItem.Orange, 3)});
                for (int i = 1; i < path.Count; i++)
                    markerOverlay.Markers.Add(new GMapMarkerLegArrow(path[i - 1].p, path[i].p));

                // "+" handle on the leg into the selected item, to insert a waypoint before it
                var k = path.FindIndex(a => a.index == selected);
                if (k > 0)
                {
                    var a = path[k - 1].p;
                    var b = path[k].p;
                    markerOverlay.Markers.Add(new GMapMarkerInsertHandle(
                        new PointLatLng((a.Lat + b.Lat) / 2, (a.Lng + b.Lng) / 2), selected));
                }
            }

            if (endsWithRtl && path.Count >= 2)
                routeOverlay.Routes.Add(new GMapRoute(
                    new List<PointLatLng> {path[path.Count - 1].p, path[0].p}, "qplanrtl")
                {
                    Stroke = new Pen(Color.FromArgb(200, 255, 160, 20), 2)
                        {DashStyle = System.Drawing.Drawing2D.DashStyle.Dash}
                });

            foreach (var m in itemMarkers)
                markerOverlay.Markers.Add(m);

            // drag handle on the selected loiter's circle
            if (selected >= 0 && selected < items.Count && IsLoiter(items[selected].id) &&
                (items[selected].lat != 0 || items[selected].lng != 0))
            {
                var wp = items[selected];
                var r = DisplayRadius(wp);
                var c = new PointLatLngAlt(wp.lat, wp.lng);
                var edge = c.newpos(90, r);
                markerOverlay.Markers.Add(new GMapMarkerRadiusHandle(edge, c, selected,
                    "r " + FtText(r) + (LoiterRadius(wp) < 1 ? " (default)" : "")));
            }

            if (hasHome)
                markerOverlay.Markers.Add(new GMapMarkerPlanItem(home)
                {
                    ItemIndex = -1, Label = "H", Selected = selected == -1,
                    AltText = FtText(home.Alt) + " MSL"
                });

            map.Invalidate();
        }

        const int ClickSlopPixels = 6;
        const int ShortClickMs = 350;
        DateTime mouseDownTime;
        GMapMarker downMarker;

        /// <summary>what's under the pointer: radius handle, then + handle, then a mission marker</summary>
        GMapMarker HitTest(Point pt)
        {
            GMapMarker best = null;
            int bestRank = int.MaxValue;
            double bestDist = double.MaxValue;
            foreach (var m in markerOverlay.Markers)
            {
                int rank = m is GMapMarkerRadiusHandle ? 0 : m is GMapMarkerInsertHandle ? 1 :
                    m is GMapMarkerPlanItem ? 2 : -1;
                if (rank < 0)
                    continue;
                var lp = map.FromLatLngToLocal(m.Position);
                double d = Math.Sqrt(Math.Pow(lp.X - pt.X, 2) + Math.Pow(lp.Y - pt.Y, 2));
                if (d > 16)
                    continue;
                if (rank < bestRank || (rank == bestRank && d < bestDist))
                {
                    best = m;
                    bestRank = rank;
                    bestDist = d;
                }
            }

            return best ?? hoverMarker;
        }

        void Map_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right)
            {
                ShowMapMenu(e.Location);
                return;
            }

            if (e.Button != MouseButtons.Left)
                return;

            mouseDown = true;
            mouseMoved = false;
            mouseDownTime = DateTime.Now;
            mouseDownStart = e.Location;
            lastMouse = e.Location;
            downMarker = HitTest(e.Location);
            radiusDrag = downMarker is GMapMarkerRadiusHandle;
            dragIndex = downMarker is GMapMarkerPlanItem pi ? pi.ItemIndex :
                downMarker is GMapMarkerRadiusHandle rh ? rh.ItemIndex : -3;
            map.CanDragMap = false;
        }

        void Map_MouseMove(object sender, MouseEventArgs e)
        {
            if (!mouseDown)
                return;
            if (!mouseMoved && Math.Abs(e.X - mouseDownStart.X) + Math.Abs(e.Y - mouseDownStart.Y) < ClickSlopPixels)
                return;
            mouseMoved = true;

            if (radiusDrag && dragIndex >= 0 && dragIndex < items.Count)
            {
                var m = map.FromLocalToLatLng(e.X, e.Y);
                var wpc = items[dragIndex];
                var dist = new PointLatLngAlt(wpc.lat, wpc.lng).GetDistance(new PointLatLngAlt(m.Lat, m.Lng));
                // snap to 5 ft
                var ft = Math.Max(5, Math.Round(dist * FtPerM / 5) * 5);
                SetLoiterRadius(dragIndex, ft / FtPerM);
                ShowRadiusInEditors(LoiterRadius(items[dragIndex]));
                RedrawMap();
                return;
            }

            if (dragIndex != -3)
            {
                // move the grabbed waypoint / home
                var p = map.FromLocalToLatLng(e.X, e.Y);
                if (dragIndex == -1)
                {
                    home = new PointLatLngAlt(p.Lat, p.Lng, home.Alt, "H");
                }
                else if (dragIndex >= 0 && dragIndex < items.Count)
                {
                    var wp = items[dragIndex];
                    wp.lat = p.Lat;
                    wp.lng = p.Lng;
                    items[dragIndex] = wp;
                }

                RedrawMap();
                return;
            }

            // nothing grabbed: pan the map
            int dx = e.X - lastMouse.X, dy = e.Y - lastMouse.Y;
            if (dx == 0 && dy == 0)
                return;
            var centre = map.FromLocalToLatLng(map.Width / 2 - dx, map.Height / 2 - dy);
            if (!double.IsNaN(centre.Lat) && !double.IsNaN(centre.Lng))
                map.Position = centre;
            lastMouse = e.Location;
        }

        void Map_MouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || !mouseDown)
                return;
            mouseDown = false;
            var index = dragIndex;
            var grabbed = downMarker;
            dragIndex = -3;
            downMarker = null;

            // a quick click that didn't move; anything held or dragged is a drag, never an add
            bool moved = mouseMoved ||
                         Math.Abs(e.X - mouseDownStart.X) + Math.Abs(e.Y - mouseDownStart.Y) >= ClickSlopPixels;
            bool shortClick = !moved && (DateTime.Now - mouseDownTime).TotalMilliseconds < ShortClickMs;

            if (radiusDrag)
            {
                radiusDrag = false;
                if (moved)
                {
                    Changed(false);
                    UpdateSummary();
                }

                return;
            }

            if (index != -3)
            {
                if (moved)
                {
                    if (index == -1)
                        SetHome(home);
                    selected = index;
                    Changed(false);
                    RebuildDetail();
                }
                else
                {
                    SelectItem(index);
                }

                return;
            }

            if (moved || !shortClick)
                return;

            if (grabbed is GMapMarkerInsertHandle handle)
            {
                // insert a waypoint halfway along the leg into the selected item
                var prevAlt = DefaultAltMetres;
                InsertItem(handle.InsertAt, new Locationwp
                {
                    id = (ushort) MAVLink.MAV_CMD.WAYPOINT, lat = handle.Position.Lat, lng = handle.Position.Lng,
                    alt = (float) prevAlt, frame = (byte) MAVLink.MAV_FRAME.GLOBAL_RELATIVE_ALT
                });
                return;
            }

            if (addTool != null)
                AddAt(map.FromLocalToLatLng(e.X, e.Y));
        }

        double LastAltitudeBefore(int index)
        {
            for (int i = Math.Min(index, items.Count) - 1; i >= 0; i--)
                if (MissionProfile.IsNavWithLocation(items[i].id) && items[i].alt > 0)
                    return items[i].alt;
            return DefaultAltMetres;
        }

        void ShowMapMenu(Point at)
        {
            var p = map.FromLocalToLatLng(at.X, at.Y);
            var menu = new ContextMenuStrip();
            menu.Items.Add("Add waypoint here", null, (s, e) =>
            {
                var t = addTool;
                addTool = "Waypoint";
                AddAt(p);
                addTool = t;
            });
            menu.Items.Add("Set home here", null, (s, e) =>
            {
                SetHome(p);
                Changed(true);
            });
            if (hoverMarker is GMapMarkerPlanItem pi && pi.ItemIndex >= 0)
            {
                var idx = pi.ItemIndex;
                menu.Items.Add("Delete item " + (idx + 1), null, (s, e) => DeleteItem(idx));
            }

            menu.Show(map, at);
        }

        #endregion

        #region mission list (right panel)

        void SelectItem(int index)
        {
            selected = index;
            profileView.SelectedItem = index;
            profileView.Invalidate();
            RedrawMap();
            RebuildList();
            RebuildDetail();
            UpdateSummary();

            if (index >= 0 && index < items.Count && (items[index].lat != 0 || items[index].lng != 0))
            {
                var p = new PointLatLng(items[index].lat, items[index].lng);
                if (!map.ViewArea.Contains(p))
                    map.Position = p;
            }
        }

        static string CommandName(ushort id)
        {
            foreach (var c in commandChoices)
                if ((ushort) c.cmd == id)
                    return c.name;
            return Enum.IsDefined(typeof(MAVLink.MAV_CMD), (int) id) ? ((MAVLink.MAV_CMD) id).ToString() : "CMD " + id;
        }

        void RebuildList()
        {
            list.SuspendLayout();
            var scroll = list.VerticalScroll.Value;
            foreach (Control c in list.Controls.Cast<Control>().ToList())
            {
                list.Controls.Remove(c);
                c.Dispose();
            }

            int w = Math.Max(240, right.Width - SystemInformation.VerticalScrollBarWidth - 14);
            int y = 4;
            y = AddRow(-1, "H", "Mission Start", FtText(home.Alt) + " MSL", y, w, false, 0);
            for (int i = 0; i < items.Count; i++)
            {
                var wp = items[i];
                bool hasAlt = MissionProfile.IsNavWithLocation(wp.id) &&
                              wp.id != (ushort) MAVLink.MAV_CMD.RETURN_TO_LAUNCH;
                string rightText = "";
                if (hasAlt && IsLoiter(wp.id))
                    rightText = "r " + FtText(DisplayRadius(wp));
                y = AddRow(i, (i + 1).ToString(), CommandName(wp.id), rightText, y, w, hasAlt, wp.alt);
            }

            list.ResumeLayout();
            try
            {
                list.VerticalScroll.Value = Math.Min(scroll, list.VerticalScroll.Maximum);
            }
            catch
            {
            }
        }

        /// <summary>altitude typed or dragged in a mission-list row</summary>
        void AltChangedFromList(int index, double ft)
        {
            if (index < 0 || index >= items.Count)
                return;
            var item = items[index];
            item.alt = (float) (ft / FtPerM);
            items[index] = item;
            commitTimer.Stop();
            commitTimer.Start();
            RedrawMap();
            UpdateSummary();
            ScheduleProfile();
            if (index == selected)
            {
                suppressEvents = true;
                try
                {
                    if (detailAltBox != null && !detailAltBox.IsDisposed)
                        detailAltBox.Value = (decimal) Math.Round(ft, 1);
                    if (detailAltSlider != null && !detailAltSlider.IsDisposed && ft >= 0)
                    {
                        if (ft > detailAltSlider.Maximum)
                            detailAltSlider.Maximum = (int) (Math.Ceiling(ft / 100) * 100);
                        detailAltSlider.Value = (int) ft;
                    }
                }
                finally
                {
                    suppressEvents = false;
                }
            }
        }

        int AddRow(int index, string number, string name, string rightText, int y, int w, bool editAlt,
            double altMetres)
        {
            bool sel = selected == index;
            var row = new Panel
            {
                Location = new Point(4, y), Size = new Size(w, 40),
                BackColor = sel ? Color.FromArgb(26, 50, 80) : Panel1, Cursor = Cursors.Hand
            };
            var circle = new NumberCircle {Text = number, Selected = sel, Location = new Point(6, 5), Size = new Size(30, 30)};
            var nameLabel = new Label
            {
                Text = name, ForeColor = Color.White, Font = new Font("Arial", 11f), Location = new Point(44, 10),
                AutoSize = true
            };
            var r = new Label
            {
                Text = rightText, ForeColor = Color.Silver, Font = new Font("Arial", 8.5f), AutoSize = false,
                Size = new Size(editAlt ? 86 : 150, 20), Location = new Point(editAlt ? w - 90 : w - 156, 12),
                TextAlign = ContentAlignment.MiddleRight
            };
            row.Controls.Add(circle);
            row.Controls.Add(nameLabel);
            row.Controls.Add(r);
            if (editAlt)
            {
                // click to type the altitude, or click-and-drag to dial it
                var alt = new InlineNumber
                {
                    Location = new Point(w - 186, 7), Size = new Size(92, 26), Minimum = -2000, Maximum = 30000,
                    DecimalPlaces = 0, Increment = 5, Unit = "ft", Font = new Font("Arial", 9.5f)
                };
                alt.Value = (decimal) Math.Round(altMetres * FtPerM);
                alt.ValueChanged += (s, e) => AltChangedFromList(index, (double) alt.Value);
                row.Controls.Add(alt);
                nameLabel.MaximumSize = new Size(w - 186 - 48, 24);
            }
            EventHandler click = (s, e) => SelectItem(index);
            row.Click += click;
            circle.Click += click;
            nameLabel.Click += click;
            r.Click += click;
            if (sel)
                row.Paint += (s, e) =>
                {
                    using (var pen = new Pen(Accent, 2))
                        e.Graphics.DrawRectangle(pen, 1, 1, row.Width - 3, row.Height - 3);
                };
            list.Controls.Add(row);
            return y + 42;
        }

        /// <summary>numbered circle used in the mission list</summary>
        class NumberCircle : Control
        {
            public bool Selected;

            public NumberCircle()
            {
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer,
                    true);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(Parent?.BackColor ?? Panel1);
                var rect = new Rectangle(1, 1, Width - 3, Height - 3);
                if (Selected)
                    using (var b = new SolidBrush(Accent))
                        g.FillEllipse(b, rect);
                using (var pen = new Pen(Color.White, 1.5f))
                    g.DrawEllipse(pen, rect);
                using (var f = new Font("Arial", 8.5f, FontStyle.Bold))
                {
                    var sz = g.MeasureString(Text, f);
                    g.DrawString(Text, f, Brushes.White, Width / 2f - sz.Width / 2, Height / 2f - sz.Height / 2);
                }
            }
        }

        #endregion

        #region item editor (right panel, bottom)

        Label FieldLabel(string text, int y)
        {
            var l = new Label
            {
                Text = text, ForeColor = Color.Gainsboro, Font = new Font("Arial", 10f), Location = new Point(12, y + 4),
                AutoSize = true
            };
            detail.Controls.Add(l);
            return l;
        }

        InlineNumber Number(int y, double value, double min, double max, int decimals, Action<double> changed,
            int x = 190)
        {
            var n = new InlineNumber
            {
                Location = new Point(x, y), Width = detail.ClientSize.Width - x - 14, DecimalPlaces = decimals,
                Minimum = (decimal) min, Maximum = (decimal) max,
                Increment = decimals >= 5 ? 0.00001m : decimals > 0 ? 1 : 5, Font = new Font("Arial", 10f)
            };
            suppressEvents = true;
            n.Value = (decimal) Math.Max(min, Math.Min(max, value));
            suppressEvents = false;
            n.ValueChanged += (s, e) =>
            {
                if (!suppressEvents)
                    changed((double) n.Value);
            };
            detail.Controls.Add(n);
            return n;
        }

        void RebuildDetail()
        {
            radiusBox = null;
            radiusSlider = null;
            detailAltBox = null;
            detailAltSlider = null;
            detail.SuspendLayout();
            foreach (Control c in detail.Controls.Cast<Control>().ToList())
            {
                detail.Controls.Remove(c);
                c.Dispose();
            }

            if (selected == -1)
                BuildMissionStartDetail();
            else if (selected >= 0 && selected < items.Count)
                BuildItemDetail(selected);
            else
            {
                var hint = new Label
                {
                    Text = "Select a mission item to edit it.\n\nUse Waypoint on the left, then click the map to add " +
                           "waypoints. Drag markers to move them; click the + on a leg to insert one. Right-click " +
                           "the map to set home.",
                    ForeColor = Color.Silver, Font = new Font("Arial", 9.5f), Location = new Point(12, 12),
                    Size = new Size(detail.ClientSize.Width - 24, 140)
                };
                detail.Controls.Add(hint);
            }

            detail.ResumeLayout();

            // fit the editor to its contents (no scrolling): the mission list takes the rest
            int bottom = 0;
            foreach (Control c in detail.Controls)
                bottom = Math.Max(bottom, c.Bottom);
            int maxH = Math.Max(200, right.ClientSize.Height - rightHeader.Height - 130);
            detail.Height = Math.Min(maxH, bottom + 12);
        }

        int DetailHeader(string number, string title, int index)
        {
            var num = new Label
            {
                Text = number, ForeColor = Color.Silver, Font = new Font("Arial", 11f), Location = new Point(12, 12),
                AutoSize = true
            };
            detail.Controls.Add(num);
            if (index >= 0)
            {
                var del = FlatButton("Delete", 70, 28);
                del.BackColor = Color.FromArgb(120, 35, 30);
                del.Location = new Point(detail.ClientSize.Width - 84, 8);
                del.Click += (s, e) => DeleteItem(index);
                detail.Controls.Add(del);
                var up = FlatButton("▲", 32, 28);
                up.Location = new Point(detail.ClientSize.Width - 160, 8);
                up.Click += (s, e) => MoveItem(index, -1);
                detail.Controls.Add(up);
                var down = FlatButton("▼", 32, 28);
                down.Location = new Point(detail.ClientSize.Width - 124, 8);
                down.Click += (s, e) => MoveItem(index, 1);
                detail.Controls.Add(down);
            }

            var t = new Label
            {
                Text = title.ToUpperInvariant(), ForeColor = Color.White, Font = new Font("Arial", 12f, FontStyle.Bold),
                Location = new Point(12, 48), AutoSize = true
            };
            detail.Controls.Add(t);
            return 48;
        }

        void BuildMissionStartDetail()
        {
            int y = DetailHeader("Home", "Mission start", -1);
            y += 40;
            FieldLabel("Home altitude (ft MSL)", y);
            Number(y, home.Alt * FtPerM, -2000, 30000, 0, v =>
            {
                home = new PointLatLngAlt(home.Lat, home.Lng, v / FtPerM, "H");
                Changed(false);
            });
            y += 34;
            FieldLabel("Latitude", y);
            Number(y, home.Lat, -90, 90, 7, v =>
            {
                home = new PointLatLngAlt(v, home.Lng, home.Alt, "H");
                Changed(false);
            });
            y += 34;
            FieldLabel("Longitude", y);
            Number(y, home.Lng, -180, 180, 7, v =>
            {
                home = new PointLatLngAlt(home.Lat, v, home.Alt, "H");
                Changed(false);
            });
            y += 44;

            // plane minimum loiter radius
            var auto = AutoMinLoiterRadius(out var how);
            FieldLabel("Min loiter radius (ft)", y);
            Number(y, MinLoiterOverride * FtPerM, 0, 30000, 0, v =>
            {
                MinLoiterOverride = v / FtPerM;
                var raised = EnforceMinLoiterRadius();
                notice = raised > 0 ? raised + " loiter(s) raised to " + FtText(MinLoiterRadiusMetres()) : "";
                Changed(raised > 0);
            });
            y += 32;
            var note = new Label
            {
                Text = "0 = work it out from the plane: " +
                       (auto > 0 ? FtText(auto) + " (" + how + ")" : "connect to the plane to calculate it") +
                       ".\nLoiters are never planned tighter than this.",
                ForeColor = Color.Silver, Font = new Font("Arial", 8.5f), Location = new Point(12, y),
                Size = new Size(detail.ClientSize.Width - 24, 46)
            };
            detail.Controls.Add(note);
        }

        void BuildItemDetail(int index)
        {
            var wp = items[index];
            var cmd = (MAVLink.MAV_CMD) wp.id;
            bool known = commandChoices.Any(c => c.cmd == cmd);
            bool hasAlt = MissionProfile.IsNavWithLocation(wp.id);

            int y = DetailHeader("#" + (index + 1), CommandName(wp.id), index);

            // "Change..." command
            var cmdBox = new ComboBox
            {
                Location = new Point(detail.ClientSize.Width - 170, y - 2), Width = 156,
                DropDownStyle = ComboBoxStyle.DropDownList, Font = new Font("Arial", 9.5f)
            };
            foreach (var c in commandChoices)
                cmdBox.Items.Add(c.name);
            if (!known)
                cmdBox.Items.Add(CommandName(wp.id));
            cmdBox.SelectedItem = CommandName(wp.id);
            cmdBox.SelectedIndexChanged += (s, e) =>
            {
                var choice = commandChoices.FirstOrDefault(c => c.name == (string) cmdBox.SelectedItem);
                if (choice.name == null || choice.cmd == (MAVLink.MAV_CMD) items[index].id)
                    return;
                var item = items[index];
                ConvertCommand(ref item, choice.cmd);
                items[index] = item;
                EnforceMinLoiterRadius();
                Changed(true);
            };
            detail.Controls.Add(cmdBox);
            y += 40;

            if (!known)
            {
                var note = new Label
                {
                    Text = "Edit this command's parameters in the classic planner.", ForeColor = Color.Silver,
                    Location = new Point(12, y), Size = new Size(detail.ClientSize.Width - 24, 30)
                };
                detail.Controls.Add(note);
                return;
            }

            if (hasAlt)
            {
                // altitude with frame, number box and a 0-400 ft slider like QGC
                var frameBox = new ComboBox
                {
                    Location = new Point(12, y), Width = 96, DropDownStyle = ComboBoxStyle.DropDownList,
                    Font = new Font("Arial", 9.5f)
                };
                foreach (var f in frameChoices)
                    frameBox.Items.Add("Alt (" + f.name + ")");
                var cur = MissionProfile.IsTerrainFrame(wp.frame) ? 1 : MissionProfile.IsAbsoluteFrame(wp.frame) ? 2 : 0;
                frameBox.SelectedIndex = cur;
                frameBox.SelectedIndexChanged += (s, e) =>
                {
                    var item = items[index];
                    item.frame = (byte) frameChoices[frameBox.SelectedIndex].frame;
                    items[index] = item;
                    Changed(true);
                };
                detail.Controls.Add(frameBox);

                var altFt = wp.alt * FtPerM;
                var slider = new TrackBar
                {
                    Location = new Point(12, y + 32), Width = detail.ClientSize.Width - 24, Minimum = 0,
                    Maximum = (int) Math.Max(400, Math.Ceiling(altFt / 100) * 100), TickFrequency = 50,
                    SmallChange = 5, LargeChange = 50
                };
                var altBox = Number(y, altFt, -2000, 30000, 1, v =>
                {
                    var item = items[index];
                    item.alt = (float) (v / FtPerM);
                    items[index] = item;
                    suppressEvents = true;
                    if (v >= slider.Minimum && v <= slider.Maximum)
                        slider.Value = (int) v;
                    suppressEvents = false;
                    Changed(false);
                }, 116);
                slider.Value = (int) Math.Max(0, Math.Min(slider.Maximum, altFt));
                slider.ValueChanged += (s, e) =>
                {
                    if (suppressEvents)
                        return;
                    altBox.Value = slider.Value;
                };
                detail.Controls.Add(slider);
                detailAltBox = altBox;
                detailAltSlider = slider;
                y += 80;
            }

            switch (cmd)
            {
                case MAVLink.MAV_CMD.WAYPOINT:
                case MAVLink.MAV_CMD.SPLINE_WAYPOINT:
                    FieldLabel("Hold (s)", y);
                    Number(y, wp.p1, 0, 3600, 0, v => SetParam(index, 1, v));
                    y += 34;
                    break;
                case MAVLink.MAV_CMD.LOITER_TURNS:
                    FieldLabel("Turns", y);
                    Number(y, wp.p1, 0, 255, 0, v => SetParam(index, 1, v));
                    y += 34;
                    y = LoiterFields(index, y);
                    break;
                case MAVLink.MAV_CMD.LOITER_TIME:
                    FieldLabel("Time (s)", y);
                    Number(y, wp.p1, 0, 36000, 0, v => SetParam(index, 1, v));
                    y += 34;
                    y = LoiterFields(index, y);
                    break;
                case MAVLink.MAV_CMD.LOITER_UNLIM:
                case MAVLink.MAV_CMD.LOITER_TO_ALT:
                    y = LoiterFields(index, y);
                    break;
                case MAVLink.MAV_CMD.TAKEOFF:
                    FieldLabel("Climb pitch (deg)", y);
                    Number(y, wp.p1, 0, 45, 0, v => SetParam(index, 1, v));
                    y += 34;
                    break;
            }

            if (wp.lat != 0 || wp.lng != 0)
            {
                int half = (detail.ClientSize.Width - 24) / 2;
                FieldLabel("Lat / Lon (or drag on the map)", y);
                y += 26;
                var lat = Number(y, wp.lat, -90, 90, 7, v =>
                {
                    var item = items[index];
                    item.lat = v;
                    items[index] = item;
                    Changed(false);
                }, 12);
                lat.Width = half - 4;
                var lon = Number(y, wp.lng, -180, 180, 7, v =>
                {
                    var item = items[index];
                    item.lng = v;
                    items[index] = item;
                    Changed(false);
                }, 12 + half + 4);
                lon.Width = half - 4;
            }
        }

        int LoiterFields(int index, int y)
        {
            var wp = items[index];
            int n = RadiusParam(wp.id);
            var min = MinLoiterRadiusMetres();
            var minFt = Math.Ceiling(min * FtPerM);
            var curFt = Math.Round(Math.Max(LoiterRadius(wp), min) * FtPerM);

            FieldLabel("Radius (ft)", y);
            // never below the plane's minimum radius
            radiusBox = Number(y, curFt, Math.Max(0, minFt), 30000, 0, v =>
            {
                SetLoiterRadius(index, v / FtPerM);
                ShowRadiusInEditors(LoiterRadius(items[index]));
                Changed(false);
            }, 150);
            y += 32;

            radiusSlider = new TrackBar
            {
                Location = new Point(12, y), Width = detail.ClientSize.Width - 24,
                Minimum = (int) Math.Max(0, minFt),
                Maximum = (int) Math.Max(Math.Max(minFt + 500, 1500), Math.Ceiling(curFt / 500) * 500),
                TickFrequency = 100, SmallChange = 5, LargeChange = 50
            };
            radiusSlider.Value = (int) Math.Max(radiusSlider.Minimum, Math.Min(radiusSlider.Maximum, curFt));
            radiusSlider.ValueChanged += (s, e) =>
            {
                if (suppressEvents)
                    return;
                var ft = Math.Round(radiusSlider.Value / 5.0) * 5;
                SetLoiterRadius(index, ft / FtPerM);
                ShowRadiusInEditors(LoiterRadius(items[index]));
                Changed(false);
            };
            detail.Controls.Add(radiusSlider);
            y += 40;

            var info = new Label
            {
                Text = (min > 0
                           ? "Plane minimum " + FtText(min) + ". "
                           : "Plane minimum unknown (set it under Mission Start). ") +
                       "Or drag the white handle on the circle.",
                ForeColor = Color.Silver, Font = new Font("Arial", 8.5f), Location = new Point(12, y),
                Size = new Size(detail.ClientSize.Width - 24, 30)
            };
            detail.Controls.Add(info);
            y += 32;

            var ccw = new CheckBox
            {
                Text = "Counter-clockwise", ForeColor = Color.Gainsboro, Font = new Font("Arial", 10f),
                Location = new Point(12, y), AutoSize = true, Checked = CounterClockwise(wp)
            };
            ccw.CheckedChanged += (s, e) =>
            {
                var item = items[index];
                double r = Math.Max(DisplayRadius(item), min);
                PutParam(ref item, n, (float) (ccw.Checked ? -r : r));
                items[index] = item;
                Changed(false);
            };
            detail.Controls.Add(ccw);
            return y + 30;
        }

        void SetParam(int index, int n, double v)
        {
            var item = items[index];
            PutParam(ref item, n, (float) v);
            items[index] = item;
            Changed(false);
        }

        void ConvertCommand(ref Locationwp wp, MAVLink.MAV_CMD to)
        {
            bool hadLocation = wp.lat != 0 || wp.lng != 0;
            wp.id = (ushort) to;
            wp.p1 = wp.p2 = wp.p3 = wp.p4 = 0;
            switch (to)
            {
                case MAVLink.MAV_CMD.LOITER_TURNS:
                    wp.p1 = 1;
                    break;
                case MAVLink.MAV_CMD.LOITER_TIME:
                    wp.p1 = 30;
                    break;
                case MAVLink.MAV_CMD.TAKEOFF:
                    wp.p1 = 15;
                    wp.lat = wp.lng = 0;
                    break;
                case MAVLink.MAV_CMD.LAND:
                case MAVLink.MAV_CMD.DO_SET_ROI:
                    wp.alt = 0;
                    break;
                case MAVLink.MAV_CMD.RETURN_TO_LAUNCH:
                    wp.lat = wp.lng = 0;
                    wp.alt = 0;
                    break;
            }

            bool needsLocation = (MissionProfile.IsNavWithLocation(wp.id) && to != MAVLink.MAV_CMD.TAKEOFF) ||
                                 to == MAVLink.MAV_CMD.DO_SET_ROI;
            if (needsLocation && !hadLocation)
            {
                var c = map.Position;
                wp.lat = c.Lat;
                wp.lng = c.Lng;
            }

            if (MissionProfile.IsNavWithLocation(wp.id) && wp.alt <= 0 && to != MAVLink.MAV_CMD.LAND)
                wp.alt = (float) LastAltitude();
        }

        #endregion

        #region summary + profile

        double CruiseSpeed()
        {
            var p = MainV2.comPort?.MAV?.param;
            if (p != null)
            {
                if (p.ContainsKey("AIRSPEED_CRUISE") && p["AIRSPEED_CRUISE"].Value > 0)
                    return p["AIRSPEED_CRUISE"].Value;
                if (p.ContainsKey("TRIM_ARSPD_CM") && p["TRIM_ARSPD_CM"].Value > 0)
                    return p["TRIM_ARSPD_CM"].Value / 100.0;
                if (p.ContainsKey("WPNAV_SPEED") && p["WPNAV_SPEED"].Value > 0)
                    return p["WPNAV_SPEED"].Value / 100.0;
                if (p.ContainsKey("WP_SPD") && p["WP_SPD"].Value > 0)
                    return p["WP_SPD"].Value;
            }

            return 15;
        }

        void UpdateSummary()
        {
            var prof = lastProfile;
            var parts = new List<string>();
            if (prof != null && prof.TotalDistance > 0)
            {
                parts.Add(FtText(prof.TotalDistance));
                var t = TimeSpan.FromSeconds(prof.TotalDistance / CruiseSpeed());
                parts.Add(t.ToString(@"hh\:mm\:ss"));
                var clear = prof.Points
                    .Where(a => !double.IsNaN(a.Terrain) && !double.IsNaN(a.Flight) && a.Leg > 1)
                    .Select(a => a.Clearance).DefaultIfEmpty(double.NaN).Min();
                if (!double.IsNaN(clear))
                    parts.Add((clear < FlightPlanner.TerrainClearanceMetres ? "LOW " : "") + "min AGL " + FtText(clear));
            }
            else
            {
                parts.Add(items.Count + " items");
            }

            summary.Text = string.Join(" · ", parts) + (notice != "" ? "\n" + notice : "");

            if (selected >= 0 && selected < items.Count && MissionProfile.IsNavWithLocation(items[selected].id))
                selectedAlt.Text = "Alt " + FtText(items[selected].alt);
            else if (selected == -1)
                selectedAlt.Text = "Home " + FtText(home.Alt);
            else
                selectedAlt.Text = "";
        }

        void ScheduleProfile()
        {
            profileTimer.Stop();
            profileTimer.Start();
        }

        void UpdateProfile()
        {
            var gen = ++profileGeneration;
            var h = home;
            var copy = new List<Locationwp>(items);
            Task.Run(() =>
            {
                MissionProfile prof;
                try
                {
                    prof = MissionProfile.Compute(h, copy);
                }
                catch (Exception ex)
                {
                    log.Error(ex);
                    return;
                }

                try
                {
                    if (IsDisposed || !IsHandleCreated)
                        return;
                    BeginInvoke((Action) (() =>
                    {
                        if (gen != profileGeneration)
                            return;
                        lastProfile = prof;
                        profileView.ClearanceMetres = FlightPlanner.TerrainClearanceMetres;
                        profileView.SelectedItem = selected;
                        profileView.Profile = prof;
                        UpdateSummary();
                        if (prof.TerrainMissing && profileRetries++ < 10)
                        {
                            profileTimer.Interval = 2500;
                            ScheduleProfile();
                            profileTimer.Interval = 300;
                        }
                        else if (!prof.TerrainMissing)
                        {
                            profileRetries = 0;
                        }
                    }));
                }
                catch (Exception ex)
                {
                    log.Debug(ex);
                }
            });
        }

        #endregion

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                commitTimer.Dispose();
                profileTimer.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
