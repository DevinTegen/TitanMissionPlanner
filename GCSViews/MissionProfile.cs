using MissionPlanner.Utilities;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MissionPlanner.GCSViews
{
    /// <summary>
    /// Terrain and planned flight altitude along a mission, for the Plan screen's profile strip.
    /// All heights in metres AMSL, distances in metres along the route.
    /// </summary>
    public class MissionProfile
    {
        public class Point
        {
            public double Distance;
            public double Lat, Lng;
            public double Terrain = double.NaN; // AMSL, NaN while the terrain tile is loading
            public double Flight = double.NaN; // AMSL
            public int Leg; // index into Waypoints of the leg's end point
            public double Clearance => Flight - Terrain;
        }

        public class Waypoint
        {
            public int ItemIndex; // index into the mission items, -1 = home
            public string Label;
            public double Distance;
            public double Lat, Lng;
            public double Flight = double.NaN; // AMSL
            public double Terrain = double.NaN;
            public ushort Command;
        }

        public List<Point> Points = new List<Point>();
        public List<Waypoint> Waypoints = new List<Waypoint>();
        public double TotalDistance;
        public bool TerrainMissing;

        static readonly HashSet<ushort> navWithLocation = new HashSet<ushort>
        {
            (ushort) MAVLink.MAV_CMD.WAYPOINT, (ushort) MAVLink.MAV_CMD.LOITER_UNLIM,
            (ushort) MAVLink.MAV_CMD.LOITER_TURNS, (ushort) MAVLink.MAV_CMD.LOITER_TIME,
            (ushort) MAVLink.MAV_CMD.LAND, (ushort) MAVLink.MAV_CMD.TAKEOFF,
            (ushort) MAVLink.MAV_CMD.LOITER_TO_ALT, (ushort) MAVLink.MAV_CMD.SPLINE_WAYPOINT,
            (ushort) MAVLink.MAV_CMD.VTOL_TAKEOFF, (ushort) MAVLink.MAV_CMD.VTOL_LAND
        };

        public static bool IsNavWithLocation(ushort id) => navWithLocation.Contains(id);

        public static bool IsTerrainFrame(byte f) =>
            f == (byte) MAVLink.MAV_FRAME.GLOBAL_TERRAIN_ALT || f == (byte) MAVLink.MAV_FRAME.GLOBAL_TERRAIN_ALT_INT;

        public static bool IsAbsoluteFrame(byte f) =>
            f == (byte) MAVLink.MAV_FRAME.GLOBAL || f == (byte) MAVLink.MAV_FRAME.GLOBAL_INT;

        public static double TerrainAt(double lat, double lng)
        {
            var r = srtm.getAltitude(lat, lng);
            if (r == null || r.currenttype == srtm.tiletype.invalid)
                return double.NaN;
            return r.alt;
        }

        /// <summary>altitude AMSL of a mission item, NaN if it needs terrain that is not loaded</summary>
        public static double AmslOf(Locationwp wp, double homeAlt)
        {
            if (IsAbsoluteFrame(wp.frame))
                return wp.alt;
            if (IsTerrainFrame(wp.frame))
                return TerrainAt(wp.lat, wp.lng) + wp.alt;
            return homeAlt + wp.alt;
        }

        public static MissionProfile Compute(PointLatLngAlt home, IList<Locationwp> items, int maxSamples = 600)
        {
            var p = new MissionProfile();
            if (home == null)
                return p;

            var homeTerrain = TerrainAt(home.Lat, home.Lng);
            p.Waypoints.Add(new Waypoint
            {
                ItemIndex = -1, Label = "H", Lat = home.Lat, Lng = home.Lng, Distance = 0,
                Flight = home.Alt, Terrain = homeTerrain
            });

            // navigation points in order; takeoff/land without a position happen where the vehicle is
            var route = new List<(Locationwp wp, int index)>();
            double lastLat = home.Lat, lastLng = home.Lng;
            for (int i = 0; i < items.Count; i++)
            {
                var wp = items[i];
                if (!IsNavWithLocation(wp.id))
                    continue;
                if (wp.lat == 0 && wp.lng == 0)
                {
                    if (wp.id == (ushort) MAVLink.MAV_CMD.LAND || wp.id == (ushort) MAVLink.MAV_CMD.VTOL_LAND)
                        continue;
                    wp.lat = lastLat;
                    wp.lng = lastLng;
                }

                route.Add((wp, i));
                lastLat = wp.lat;
                lastLng = wp.lng;
            }

            // total length first, to pick a sample spacing
            double total = 0;
            {
                double la = home.Lat, ln = home.Lng;
                foreach (var r in route)
                {
                    total += new PointLatLngAlt(la, ln).GetDistance(new PointLatLngAlt(r.wp.lat, r.wp.lng));
                    la = r.wp.lat;
                    ln = r.wp.lng;
                }
            }

            p.TotalDistance = total;
            double step = Math.Max(5, total / maxSamples);

            var prev = new Locationwp
            {
                lat = home.Lat, lng = home.Lng, alt = 0, frame = (byte) MAVLink.MAV_FRAME.GLOBAL_RELATIVE_ALT
            };
            double dist = 0;
            for (int k = 0; k < route.Count; k++)
            {
                var a = prev;
                var b = route[k].wp;
                var legLen = new PointLatLngAlt(a.lat, a.lng).GetDistance(new PointLatLngAlt(b.lat, b.lng));

                bool bothTerrain = IsTerrainFrame(a.frame) && IsTerrainFrame(b.frame);
                double amslA = AmslOf(a, home.Alt);
                double amslB = AmslOf(b, home.Alt);

                // takeoff climbs in place: the leg into the takeoff point starts on the ground
                int n = Math.Max(1, (int) Math.Ceiling(legLen / step));
                for (int s = (k == 0 ? 0 : 1); s <= n; s++)
                {
                    double t = s / (double) n;
                    var pt = new Point
                    {
                        Distance = dist + legLen * t,
                        Lat = a.lat + (b.lat - a.lat) * t,
                        Lng = a.lng + (b.lng - a.lng) * t,
                        Leg = k + 1
                    };
                    pt.Terrain = TerrainAt(pt.Lat, pt.Lng);
                    if (double.IsNaN(pt.Terrain))
                        p.TerrainMissing = true;
                    pt.Flight = bothTerrain
                        ? pt.Terrain + a.alt + (b.alt - a.alt) * t
                        : amslA + (amslB - amslA) * t;
                    p.Points.Add(pt);
                }

                dist += legLen;
                p.Waypoints.Add(new Waypoint
                {
                    ItemIndex = route[k].index, Label = (route[k].index + 1).ToString(), Lat = b.lat, Lng = b.lng,
                    Distance = dist, Flight = amslB, Terrain = TerrainAt(b.lat, b.lng), Command = b.id
                });
                prev = b;
            }

            return p;
        }
    }
}
