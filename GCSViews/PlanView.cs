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
    /// QGroundControl-style mission planning screen: map with simple numbered markers (click to add,
    /// drag to move), mission item cards on the right, terrain/altitude profile along the bottom and a
    /// summary bar on top. Altitudes are shown in feet. It edits the same mission as the classic Flight
    /// Planner, which still does Upload/Download, files and the advanced tools.
    /// </summary>
    public class PlanView : MyUserControl, IActivate, IDeactivate
    {
        static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        const double FtPerM = 3.28084;
        const double MPerMile = 1609.344;

        readonly myGMAP map = new myGMAP();
        readonly FlowLayoutPanel topBar = new FlowLayoutPanel();
        readonly Panel editor = new Panel();
        readonly PlanProfileControl profileView = new PlanProfileControl();
        readonly Label summary = new Label();
        readonly Button addButton = new Button();
        readonly GMapOverlay routeOverlay = new GMapOverlay("planroute");
        readonly GMapOverlay markerOverlay = new GMapOverlay("planmarkers");
        readonly Timer commitTimer = new Timer {Interval = 350};
        readonly Timer profileTimer = new Timer {Interval = 300};

        PointLatLngAlt home = new PointLatLngAlt(0, 0, 0, "H");
        List<Locationwp> items = new List<Locationwp>();
        int selected = -2; // -1 home, -2 nothing
        bool addMode;
        bool suppressEvents;
        int profileGeneration;
        int profileRetries;
        MissionProfile lastProfile;

        // mouse
        GMapMarkerPlanItem hoverMarker;
        int dragIndex = -3; // -3 = not dragging a marker
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
            ("Return to launch", MAVLink.MAV_CMD.RETURN_TO_LAUNCH)
        };

        static readonly (string name, MAVLink.MAV_FRAME frame)[] frameChoices =
        {
            ("Above home", MAVLink.MAV_FRAME.GLOBAL_RELATIVE_ALT),
            ("Above terrain", MAVLink.MAV_FRAME.GLOBAL_TERRAIN_ALT),
            ("Above sea level", MAVLink.MAV_FRAME.GLOBAL)
        };

        public PlanView()
        {
            BackColor = Color.FromArgb(38, 39, 40);
            Dock = DockStyle.Fill;

            // map first so it fills what the bars leave
            map.Dock = DockStyle.Fill;
            map.MinZoom = 0;
            map.MaxZoom = 24;
            map.MarkersEnabled = true;
            map.RoutesEnabled = true;
            map.PolygonsEnabled = true;
            map.Overlays.Add(routeOverlay);
            map.Overlays.Add(markerOverlay);
            map.OnMarkerEnter += m => hoverMarker = m as GMapMarkerPlanItem;
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
            profileView.Height = 160;
            profileView.ItemClicked += i => SelectItem(i);
            Controls.Add(profileView);

            editor.Dock = DockStyle.Right;
            editor.Width = 340;
            editor.AutoScroll = true;
            editor.BackColor = Color.FromArgb(45, 47, 50);
            Controls.Add(editor);

            topBar.Dock = DockStyle.Top;
            topBar.Height = 46;
            topBar.WrapContents = false;
            topBar.Padding = new Padding(4, 6, 4, 4);
            topBar.BackColor = Color.FromArgb(28, 30, 33);
            Controls.Add(topBar);

            BarButton(addButton, "+ Waypoint", (s, e) => SetAddMode(!addMode), 92);
            BarButton(new Button(), "Takeoff", (s, e) => AddTakeoff(), 64);
            BarButton(new Button(), "Land", (s, e) => AddLand(), 52);
            BarButton(new Button(), "RTL", (s, e) => AddRtl(), 46);
            BarButton(new Button(), "Clear", (s, e) => ClearMission(), 52);

            summary.AutoSize = false;
            summary.Width = 560;
            summary.Height = 34;
            summary.ForeColor = Color.Gainsboro;
            summary.Font = new Font("Arial", 8.5f);
            summary.TextAlign = ContentAlignment.MiddleLeft;
            summary.Margin = new Padding(10, 0, 10, 0);
            topBar.Controls.Add(summary);

            BarButton(new Button(), "Download", (s, e) => Download(), 76);
            BarButton(new Button(), "Upload", (s, e) => Upload(), 64);
            BarButton(new Button(), "Classic planner", (s, e) => MainV2.View.ShowScreen("FlightPlanner"), 104);

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

        void BarButton(Button b, string text, EventHandler click, int width)
        {
            b.Text = text;
            b.Width = width;
            b.Height = 32;
            b.Margin = new Padding(3, 0, 3, 0);
            b.FlatStyle = FlatStyle.Flat;
            b.ForeColor = Color.White;
            b.BackColor = Color.FromArgb(60, 64, 70);
            b.Click += click;
            topBar.Controls.Add(b);
        }

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

                if (home.Lat != 0 || home.Lng != 0)
                {
                    if (map.Zoom < 5)
                    {
                        map.Position = home;
                        map.Zoom = 15;
                    }
                }
                else if (MainV2.comPort?.MAV?.cs?.Location != null && MainV2.comPort.MAV.cs.Location.Lat != 0)
                {
                    map.Position = MainV2.comPort.MAV.cs.Location;
                    map.Zoom = 15;
                }
            }
            catch (Exception ex)
            {
                log.Error(ex);
            }
        }

        public void Deactivate()
        {
            if (commitTimer.Enabled)
            {
                commitTimer.Stop();
                CommitNow();
            }

            SetAddMode(false);
        }

        void LoadFromPlanner()
        {
            var fp = MainV2.instance?.FlightPlanner;
            if (fp == null || !fp.TryGetMissionForPlan(out var h, out var list))
                return;
            home = h;
            items = list;
            if (selected >= items.Count)
                selected = -2;
            RefreshAll();
        }

        void CommitSoon()
        {
            commitTimer.Stop();
            commitTimer.Start();
            RedrawMap();
            UpdateSummary();
            ScheduleProfile();
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

        void Changed(bool structural)
        {
            if (structural)
            {
                commitTimer.Stop();
                // rebuild after the current event, which may come from a control the rebuild disposes
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
                CommitSoon();
            }
        }

        void RefreshAll()
        {
            RedrawMap();
            RebuildEditor();
            UpdateSummary();
            ScheduleProfile();
        }

        #endregion

        #region top bar actions

        void SetAddMode(bool on)
        {
            addMode = on;
            addButton.BackColor = on ? Color.FromArgb(30, 140, 70) : Color.FromArgb(60, 64, 70);
            addButton.Text = on ? "Adding: click map" : "+ Waypoint";
            addButton.Width = on ? 128 : 92;
            map.Cursor = on ? Cursors.Cross : Cursors.Default;
        }

        double LastAltitude()
        {
            for (int i = items.Count - 1; i >= 0; i--)
                if (MissionProfile.IsNavWithLocation(items[i].id) && items[i].id != (ushort) MAVLink.MAV_CMD.LAND &&
                    items[i].id != (ushort) MAVLink.MAV_CMD.VTOL_LAND)
                    return items[i].alt;
            return MainV2.instance?.FlightPlanner?.DefaultAltitudeMetres ?? 100;
        }

        void InsertItem(Locationwp wp)
        {
            int at = selected >= 0 && selected < items.Count - 1 ? selected + 1 : items.Count;
            items.Insert(at, wp);
            selected = at;
            Changed(true);
        }

        void AddWaypointAt(PointLatLng p)
        {
            if (home.Lat == 0 && home.Lng == 0)
                SetHome(p);

            InsertItem(new Locationwp
            {
                id = (ushort) MAVLink.MAV_CMD.WAYPOINT, lat = p.Lat, lng = p.Lng, alt = (float) LastAltitude(),
                frame = (byte) MAVLink.MAV_FRAME.GLOBAL_RELATIVE_ALT
            });
        }

        void AddTakeoff()
        {
            if (items.Any(a => a.id == (ushort) MAVLink.MAV_CMD.TAKEOFF || a.id == (ushort) MAVLink.MAV_CMD.VTOL_TAKEOFF))
            {
                CustomMessageBox.Show("The mission already has a takeoff.", "Plan");
                return;
            }

            items.Insert(0, new Locationwp
            {
                id = (ushort) MAVLink.MAV_CMD.TAKEOFF, p1 = 15, lat = 0, lng = 0,
                alt = (float) (MainV2.instance?.FlightPlanner?.DefaultAltitudeMetres ?? 100),
                frame = (byte) MAVLink.MAV_FRAME.GLOBAL_RELATIVE_ALT
            });
            selected = 0;
            Changed(true);
        }

        void AddLand()
        {
            if (home.Lat == 0 && home.Lng == 0)
            {
                CustomMessageBox.Show("Set a home position first (right-click the map).", "Plan");
                return;
            }

            items.Add(new Locationwp
            {
                id = (ushort) MAVLink.MAV_CMD.LAND, lat = home.Lat, lng = home.Lng, alt = 0,
                frame = (byte) MAVLink.MAV_FRAME.GLOBAL_RELATIVE_ALT
            });
            selected = items.Count - 1;
            Changed(true);
        }

        void AddRtl()
        {
            items.Add(new Locationwp
            {
                id = (ushort) MAVLink.MAV_CMD.RETURN_TO_LAUNCH,
                frame = (byte) MAVLink.MAV_FRAME.GLOBAL_RELATIVE_ALT
            });
            selected = items.Count - 1;
            Changed(true);
        }

        void ClearMission()
        {
            if (items.Count == 0)
                return;
            if ((int) DialogResult.Yes !=
                CustomMessageBox.Show("Remove all mission items?", "Plan", MessageBoxButtons.YesNo))
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

        void SetHome(PointLatLng p)
        {
            var t = MissionProfile.TerrainAt(p.Lat, p.Lng);
            home = new PointLatLngAlt(p.Lat, p.Lng, double.IsNaN(t) ? home.Alt : t, "H");
        }

        #endregion

        #region map

        static bool IsLoiter(ushort id) =>
            id == (ushort) MAVLink.MAV_CMD.LOITER_TURNS || id == (ushort) MAVLink.MAV_CMD.LOITER_TIME ||
            id == (ushort) MAVLink.MAV_CMD.LOITER_UNLIM || id == (ushort) MAVLink.MAV_CMD.LOITER_TO_ALT;

        double LoiterRadius(Locationwp wp)
        {
            double r = wp.id == (ushort) MAVLink.MAV_CMD.LOITER_TO_ALT ? wp.p2 : wp.p3;
            r = Math.Abs(r);
            if (r < 1)
            {
                var p = MainV2.comPort?.MAV?.param;
                if (p != null && p.ContainsKey("WP_LOITER_RAD"))
                    r = Math.Abs(p["WP_LOITER_RAD"].Value);
            }

            return r;
        }

        static string AltLabel(Locationwp wp)
        {
            var s = (wp.alt * FtPerM).ToString("0") + " ft";
            if (MissionProfile.IsTerrainFrame(wp.frame))
                s += " AGL";
            else if (MissionProfile.IsAbsoluteFrame(wp.frame))
                s += " MSL";
            if (IsLoiter(wp.id))
                s = "Loiter " + s;
            return s;
        }

        void RedrawMap()
        {
            routeOverlay.Routes.Clear();
            markerOverlay.Markers.Clear();

            var path = new List<PointLatLng>();
            if (home.Lat != 0 || home.Lng != 0)
            {
                path.Add(home);
                markerOverlay.Markers.Add(new GMapMarkerPlanItem(home)
                {
                    ItemIndex = -1, Label = "H", Selected = selected == -1,
                    AltText = (home.Alt * FtPerM).ToString("0") + " ft MSL"
                });
            }

            bool endsWithRtl = false;
            for (int i = 0; i < items.Count; i++)
            {
                var wp = items[i];
                if (wp.id == (ushort) MAVLink.MAV_CMD.RETURN_TO_LAUNCH)
                {
                    endsWithRtl = true;
                    continue;
                }

                if (!MissionProfile.IsNavWithLocation(wp.id))
                    continue;
                endsWithRtl = false;
                if (wp.lat == 0 && wp.lng == 0)
                    continue; // takeoff/land in place

                var p = new PointLatLng(wp.lat, wp.lng);
                path.Add(p);
                markerOverlay.Markers.Add(new GMapMarkerPlanItem(p)
                {
                    ItemIndex = i,
                    Label = wp.id == (ushort) MAVLink.MAV_CMD.TAKEOFF ? "T" :
                        wp.id == (ushort) MAVLink.MAV_CMD.LAND ? "L" : (i + 1).ToString(),
                    Command = wp.id,
                    Selected = selected == i,
                    AltText = AltLabel(wp),
                    LoiterRadiusMetres = IsLoiter(wp.id) ? LoiterRadius(wp) : 0
                });
            }

            if (path.Count >= 2)
                routeOverlay.Routes.Add(new GMapRoute(path, "plan")
                    {Stroke = new Pen(Color.FromArgb(230, 255, 160, 30), 3)});

            if (endsWithRtl && path.Count >= 2)
                routeOverlay.Routes.Add(new GMapRoute(new List<PointLatLng> {path[path.Count - 1], path[0]}, "rtl")
                {
                    Stroke = new Pen(Color.FromArgb(200, 200, 200, 200), 2)
                        {DashStyle = System.Drawing.Drawing2D.DashStyle.Dash}
                });

            map.Invalidate();
        }

        void Map_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left)
            {
                if (e.Button == MouseButtons.Right)
                    ShowMapMenu(e.Location);
                return;
            }

            mouseDown = true;
            mouseMoved = false;
            mouseDownStart = e.Location;
            lastMouse = e.Location;
            dragIndex = hoverMarker != null ? hoverMarker.ItemIndex : -3;
            if (dragIndex != -3)
                map.CanDragMap = false;
        }

        void Map_MouseMove(object sender, MouseEventArgs e)
        {
            if (!mouseDown || e.Button != MouseButtons.Left)
                return;
            if (!mouseMoved && Math.Abs(e.X - mouseDownStart.X) + Math.Abs(e.Y - mouseDownStart.Y) < 4)
                return;
            mouseMoved = true;

            if (dragIndex != -3)
            {
                // drag a marker
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

            // pan with the left button, like the classic planner
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
            map.CanDragMap = true;
            var index = dragIndex;
            dragIndex = -3;

            if (index != -3)
            {
                if (mouseMoved)
                {
                    if (index == -1)
                        SetHome(home);
                    SelectItem(index, false);
                    Changed(false);
                }
                else
                {
                    SelectItem(index);
                }

                return;
            }

            if (!mouseMoved && addMode)
                AddWaypointAt(map.FromLocalToLatLng(e.X, e.Y));
        }

        void ShowMapMenu(Point at)
        {
            var p = map.FromLocalToLatLng(at.X, at.Y);
            var menu = new ContextMenuStrip();
            menu.Items.Add("Add waypoint here", null, (s, e) => AddWaypointAt(p));
            menu.Items.Add("Set home here", null, (s, e) =>
            {
                SetHome(p);
                Changed(true);
            });
            if (hoverMarker != null && hoverMarker.ItemIndex >= 0)
            {
                var idx = hoverMarker.ItemIndex;
                menu.Items.Add("Delete item " + (idx + 1), null, (s, e) => DeleteItem(idx));
            }

            menu.Show(map, at);
        }

        #endregion

        #region editor cards

        void SelectItem(int index, bool rebuild = true)
        {
            selected = index;
            profileView.SelectedItem = index;
            profileView.Invalidate();
            RedrawMap();
            UpdateSummary();
            if (rebuild)
                RebuildEditor();

            // keep the selected marker on screen
            if (index >= 0 && index < items.Count && (items[index].lat != 0 || items[index].lng != 0))
            {
                var p = new PointLatLng(items[index].lat, items[index].lng);
                if (!map.ViewArea.Contains(p))
                    map.Position = p;
            }
        }

        void DeleteItem(int index)
        {
            if (index < 0 || index >= items.Count)
                return;
            items.RemoveAt(index);
            selected = Math.Min(index, items.Count - 1);
            if (items.Count == 0)
                selected = -2;
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

        static string CommandName(ushort id)
        {
            foreach (var c in commandChoices)
                if ((ushort) c.cmd == id)
                    return c.name;
            return Enum.IsDefined(typeof(MAVLink.MAV_CMD), (int) id) ? ((MAVLink.MAV_CMD) id).ToString() : "CMD " + id;
        }

        void RebuildEditor()
        {
            editor.SuspendLayout();
            var scroll = editor.VerticalScroll.Value;
            foreach (Control c in editor.Controls.Cast<Control>().ToList())
            {
                editor.Controls.Remove(c);
                c.Dispose();
            }

            int y = 6;
            int w = editor.ClientSize.Width - 12;
            if (w < 200)
                w = 300;

            y = AddHeader(-1, "Mission start (home)", (home.Alt * FtPerM).ToString("0") + " ft MSL", y, w);
            if (selected == -1)
                y = AddHomeDetails(y, w);

            for (int i = 0; i < items.Count; i++)
            {
                var wp = items[i];
                string right = MissionProfile.IsNavWithLocation(wp.id) ? AltLabel(wp) : "";
                y = AddHeader(i, (i + 1) + "  " + CommandName(wp.id), right, y, w);
                if (selected == i)
                    y = AddItemDetails(i, y, w);
            }

            var hint = new Label
            {
                Text = items.Count == 0
                    ? "Press '+ Waypoint' and click the map to add waypoints. Right-click the map to set home."
                    : "Click a card or a marker to edit it. Drag markers to move them.",
                ForeColor = Color.Silver, Location = new Point(8, y + 6), Size = new Size(w - 4, 40)
            };
            editor.Controls.Add(hint);

            editor.ResumeLayout();
            try
            {
                editor.VerticalScroll.Value = Math.Min(scroll, editor.VerticalScroll.Maximum);
            }
            catch
            {
            }
        }

        int AddHeader(int index, string text, string right, int y, int w)
        {
            bool sel = selected == index;
            var header = new Panel
            {
                Location = new Point(6, y), Size = new Size(w, 30),
                BackColor = sel ? Color.FromArgb(25, 105, 215) : Color.FromArgb(62, 65, 70),
                Cursor = Cursors.Hand
            };
            var left = new Label
            {
                Text = text, ForeColor = Color.White, Font = new Font("Arial", 9.5f, FontStyle.Bold),
                Location = new Point(8, 7), AutoSize = true
            };
            var r = new Label
            {
                Text = right, ForeColor = Color.White, Font = new Font("Arial", 8.5f),
                AutoSize = false, Size = new Size(140, 18), Location = new Point(w - 146, 8),
                TextAlign = ContentAlignment.MiddleRight
            };
            header.Controls.Add(left);
            header.Controls.Add(r);
            EventHandler click = (s, e) => SelectItem(selected == index ? -2 : index);
            header.Click += click;
            left.Click += click;
            r.Click += click;
            editor.Controls.Add(header);
            return y + 33;
        }

        Panel DetailPanel(int y, int w, int h)
        {
            var p = new Panel
            {
                Location = new Point(6, y), Size = new Size(w, h), BackColor = Color.FromArgb(52, 55, 60)
            };
            editor.Controls.Add(p);
            return p;
        }

        static Label FieldLabel(Panel p, string text, int y)
        {
            var l = new Label {Text = text, ForeColor = Color.Gainsboro, Location = new Point(8, y + 3), AutoSize = true};
            p.Controls.Add(l);
            return l;
        }

        NumericUpDown Number(Panel p, int y, double value, double min, double max, int decimals,
            Action<double> changed)
        {
            var n = new NumericUpDown
            {
                Location = new Point(140, y), Width = p.Width - 150, DecimalPlaces = decimals,
                Minimum = (decimal) min, Maximum = (decimal) max,
                Increment = decimals > 0 ? 1 : 10
            };
            suppressEvents = true;
            n.Value = (decimal) Math.Max(min, Math.Min(max, value));
            suppressEvents = false;
            n.ValueChanged += (s, e) =>
            {
                if (!suppressEvents)
                    changed((double) n.Value);
            };
            p.Controls.Add(n);
            return n;
        }

        int AddHomeDetails(int y, int w)
        {
            var p = DetailPanel(y, w, 96);
            FieldLabel(p, "Latitude", 6);
            FieldLabel(p, "Longitude", 34);
            FieldLabel(p, "Altitude (ft MSL)", 62);
            Number(p, 4, home.Lat, -90, 90, 7, v =>
            {
                home = new PointLatLngAlt(v, home.Lng, home.Alt, "H");
                Changed(false);
            });
            Number(p, 32, home.Lng, -180, 180, 7, v =>
            {
                home = new PointLatLngAlt(home.Lat, v, home.Alt, "H");
                Changed(false);
            });
            Number(p, 60, home.Alt * FtPerM, -2000, 30000, 0, v =>
            {
                home = new PointLatLngAlt(home.Lat, home.Lng, v / FtPerM, "H");
                Changed(false);
            });
            return y + p.Height + 6;
        }

        int AddItemDetails(int index, int y, int w)
        {
            var wp = items[index];
            var cmd = (MAVLink.MAV_CMD) wp.id;
            bool known = commandChoices.Any(c => c.cmd == cmd);
            bool hasLocation = MissionProfile.IsNavWithLocation(wp.id);
            var p = DetailPanel(y, w, 10);
            int row = 6;

            // command
            FieldLabel(p, "Command", row);
            var cmdBox = new ComboBox
            {
                Location = new Point(140, row), Width = w - 150, DropDownStyle = ComboBoxStyle.DropDownList
            };
            foreach (var c in commandChoices)
                cmdBox.Items.Add(c.name);
            if (!known)
                cmdBox.Items.Add(CommandName(wp.id));
            cmdBox.SelectedItem = CommandName(wp.id);
            cmdBox.SelectedIndexChanged += (s, e) =>
            {
                var choice = commandChoices.FirstOrDefault(c => c.name == (string) cmdBox.SelectedItem);
                if (choice.name == null)
                    return;
                var item = items[index];
                ConvertCommand(ref item, choice.cmd);
                items[index] = item;
                Changed(true);
            };
            p.Controls.Add(cmdBox);
            row += 30;

            if (!known)
            {
                var note = new Label
                {
                    Text = "Edit this command's parameters in the classic planner.", ForeColor = Color.Silver,
                    Location = new Point(8, row), Size = new Size(w - 16, 30)
                };
                p.Controls.Add(note);
                row += 32;
            }

            if (hasLocation && known)
            {
                FieldLabel(p, "Altitude (ft)", row);
                Number(p, row, wp.alt * FtPerM, -2000, 30000, 0, v =>
                {
                    var item = items[index];
                    item.alt = (float) (v / FtPerM);
                    items[index] = item;
                    Changed(false);
                });
                row += 28;

                FieldLabel(p, "Altitude is", row);
                var frameBox = new ComboBox
                {
                    Location = new Point(140, row), Width = w - 150, DropDownStyle = ComboBoxStyle.DropDownList
                };
                foreach (var f in frameChoices)
                    frameBox.Items.Add(f.name);
                var current = frameChoices.FirstOrDefault(f => (byte) f.frame == wp.frame ||
                                                               (MissionProfile.IsTerrainFrame(wp.frame) &&
                                                                f.frame == MAVLink.MAV_FRAME.GLOBAL_TERRAIN_ALT) ||
                                                               (MissionProfile.IsAbsoluteFrame(wp.frame) &&
                                                                f.frame == MAVLink.MAV_FRAME.GLOBAL));
                frameBox.SelectedItem = current.name ?? frameChoices[0].name;
                frameBox.SelectedIndexChanged += (s, e) =>
                {
                    var f = frameChoices.First(a => a.name == (string) frameBox.SelectedItem);
                    var item = items[index];
                    item.frame = (byte) f.frame;
                    items[index] = item;
                    Changed(true);
                };
                p.Controls.Add(frameBox);
                row += 30;

                // command specific
                switch (cmd)
                {
                    case MAVLink.MAV_CMD.WAYPOINT:
                    case MAVLink.MAV_CMD.SPLINE_WAYPOINT:
                        FieldLabel(p, "Hold (s)", row);
                        Number(p, row, wp.p1, 0, 3600, 0, v => SetParam(index, 1, v));
                        row += 28;
                        break;
                    case MAVLink.MAV_CMD.LOITER_TURNS:
                        FieldLabel(p, "Turns", row);
                        Number(p, row, wp.p1, 0, 255, 0, v => SetParam(index, 1, v));
                        row += 28;
                        row = LoiterRadiusFields(p, index, wp.p3, 3, row, w);
                        break;
                    case MAVLink.MAV_CMD.LOITER_TIME:
                        FieldLabel(p, "Time (s)", row);
                        Number(p, row, wp.p1, 0, 36000, 0, v => SetParam(index, 1, v));
                        row += 28;
                        row = LoiterRadiusFields(p, index, wp.p3, 3, row, w);
                        break;
                    case MAVLink.MAV_CMD.LOITER_UNLIM:
                        row = LoiterRadiusFields(p, index, wp.p3, 3, row, w);
                        break;
                    case MAVLink.MAV_CMD.LOITER_TO_ALT:
                        row = LoiterRadiusFields(p, index, wp.p2, 2, row, w);
                        break;
                    case MAVLink.MAV_CMD.TAKEOFF:
                        FieldLabel(p, "Climb pitch (deg)", row);
                        Number(p, row, wp.p1, 0, 45, 0, v => SetParam(index, 1, v));
                        row += 28;
                        break;
                }

                if (wp.lat != 0 || wp.lng != 0)
                {
                    FieldLabel(p, "Latitude", row);
                    Number(p, row, wp.lat, -90, 90, 7, v =>
                    {
                        var item = items[index];
                        item.lat = v;
                        items[index] = item;
                        Changed(false);
                    });
                    row += 28;
                    FieldLabel(p, "Longitude", row);
                    Number(p, row, wp.lng, -180, 180, 7, v =>
                    {
                        var item = items[index];
                        item.lng = v;
                        items[index] = item;
                        Changed(false);
                    });
                    row += 28;
                }
            }

            // move / delete
            var up = new Button {Text = "Move up", Location = new Point(8, row + 2), Width = 80, Height = 26};
            up.Click += (s, e) => MoveItem(index, -1);
            var down = new Button {Text = "Move down", Location = new Point(94, row + 2), Width = 84, Height = 26};
            down.Click += (s, e) => MoveItem(index, 1);
            var del = new Button
            {
                Text = "Delete", Location = new Point(w - 88, row + 2), Width = 80, Height = 26,
                BackColor = Color.FromArgb(150, 40, 35), ForeColor = Color.White
            };
            del.Click += (s, e) => DeleteItem(index);
            p.Controls.Add(up);
            p.Controls.Add(down);
            p.Controls.Add(del);
            row += 34;

            p.Height = row;
            return y + p.Height + 6;
        }

        int LoiterRadiusFields(Panel p, int index, float value, int paramNo, int row, int w)
        {
            FieldLabel(p, "Radius (ft, 0 = default)", row);
            Number(p, row, Math.Abs(value) * FtPerM, 0, 30000, 0, v =>
            {
                var sign = GetParam(items[index], paramNo) < 0 ? -1 : 1;
                SetParam(index, paramNo, sign * v / FtPerM);
            });
            row += 28;
            var ccw = new CheckBox
            {
                Text = "Counter-clockwise", ForeColor = Color.Gainsboro, Location = new Point(140, row),
                AutoSize = true, Checked = value < 0
            };
            ccw.CheckedChanged += (s, e) =>
            {
                var r = Math.Abs(GetParam(items[index], paramNo));
                if (r < 1)
                {
                    // direction needs a radius; use the default loiter radius
                    r = LoiterRadius(items[index]);
                    if (r < 1)
                        r = 60;
                }

                SetParam(index, paramNo, ccw.Checked ? -r : r);
            };
            p.Controls.Add(ccw);
            return row + 26;
        }

        static float GetParam(Locationwp wp, int n) => n == 1 ? wp.p1 : n == 2 ? wp.p2 : n == 3 ? wp.p3 : wp.p4;

        void SetParam(int index, int n, double v)
        {
            var item = items[index];
            switch (n)
            {
                case 1: item.p1 = (float) v; break;
                case 2: item.p2 = (float) v; break;
                case 3: item.p3 = (float) v; break;
                default: item.p4 = (float) v; break;
            }

            items[index] = item;
            Changed(false);
        }

        void ConvertCommand(ref Locationwp wp, MAVLink.MAV_CMD to)
        {
            bool hadLocation = MissionProfile.IsNavWithLocation(wp.id) && (wp.lat != 0 || wp.lng != 0);
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
                    wp.alt = 0;
                    break;
                case MAVLink.MAV_CMD.RETURN_TO_LAUNCH:
                    wp.lat = wp.lng = 0;
                    wp.alt = 0;
                    break;
            }

            // give a location to items that need one
            if (MissionProfile.IsNavWithLocation(wp.id) && to != MAVLink.MAV_CMD.TAKEOFF && !hadLocation &&
                wp.lat == 0 && wp.lng == 0)
            {
                var c = map.Position;
                wp.lat = c.Lat;
                wp.lng = c.Lng;
                if (wp.alt <= 0 && to != MAVLink.MAV_CMD.LAND)
                    wp.alt = (float) LastAltitude();
            }
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
                parts.Add("Distance " + (prof.TotalDistance / MPerMile).ToString("0.00") + " mi");
                parts.Add("Time ~" + Math.Ceiling(prof.TotalDistance / CruiseSpeed() / 60) + " min");
                var maxAboveHome = prof.Waypoints.Where(a => !double.IsNaN(a.Flight)).Select(a => a.Flight - home.Alt)
                    .DefaultIfEmpty(0).Max();
                parts.Add("Max " + (maxAboveHome * FtPerM).ToString("0") + " ft above home");
                var clear = prof.Points.Where(a => !double.IsNaN(a.Terrain) && !double.IsNaN(a.Flight) &&
                                                   a.Leg > 1).Select(a => a.Clearance).DefaultIfEmpty(double.NaN).Min();
                if (!double.IsNaN(clear))
                    parts.Add((clear < FlightPlanner.TerrainClearanceMetres ? "LOW " : "") + "lowest clearance " +
                              (clear * FtPerM).ToString("0") + " ft");
            }
            else
            {
                parts.Add(items.Count + " items");
            }

            var line1 = string.Join("   |   ", parts);
            string line2 = "";
            if (selected >= 0 && selected < items.Count && prof != null)
            {
                var wpIdx = prof.Waypoints.FindIndex(a => a.ItemIndex == selected);
                if (wpIdx > 0)
                {
                    var a = prof.Waypoints[wpIdx - 1];
                    var b = prof.Waypoints[wpIdx];
                    var pa = new PointLatLngAlt(a.Lat, a.Lng);
                    var pb = new PointLatLngAlt(b.Lat, b.Lng);
                    var d = pa.GetDistance(pb);
                    var dAlt = double.IsNaN(a.Flight) || double.IsNaN(b.Flight) ? double.NaN : b.Flight - a.Flight;
                    line2 = "Item " + (selected + 1) + ": " + (d / MPerMile).ToString("0.00") + " mi from " +
                            (a.ItemIndex < 0 ? "home" : "item " + (a.ItemIndex + 1)) + ", heading " +
                            pa.GetBearing(pb).ToString("000") + "°" +
                            (double.IsNaN(dAlt)
                                ? ""
                                : ", " + (dAlt >= 0 ? "+" : "") + (dAlt * FtPerM).ToString("0") + " ft" +
                                  (d > 1 ? " (" + (Math.Atan2(dAlt, d) * 180 / Math.PI).ToString("0.0") + "° slope)" : ""));
                }
            }

            summary.Text = line1 + (line2 != "" ? "\n" + line2 : "");
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
            var list = new List<Locationwp>(items);
            Task.Run(() =>
            {
                MissionProfile prof;
                try
                {
                    prof = MissionProfile.Compute(h, list);
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
                        // terrain tiles still downloading: try again shortly
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
