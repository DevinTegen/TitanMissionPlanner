using System;
using System.Collections.Generic;

namespace MissionPlanner.Utilities
{
    /// <summary>
    /// Builds ArduPilot terrain database blocks (the APM/TERRAIN/NxxWyyy.DAT files on the autopilot's
    /// microSD card) so terrain can be pre-loaded before a flight instead of being requested from the
    /// GCS in flight.
    ///
    /// This is a line-for-line port of ArduPilot's reference generator (github.com/ArduPilot/terraingen,
    /// terrain_gen.py, "4.1+" format) which in turn mirrors libraries/AP_Terrain and AP_Common/Location.
    /// The float32 rounding of the scaling constants is deliberate: the autopilot compares block corner
    /// positions with a tolerance of a few 1e-7 degrees (TERRAIN_MARGIN), so the arithmetic has to match.
    /// </summary>
    public static class TerrainDat
    {
        // MAVLink sends 4x4 grids
        public const int GRID_MAVLINK_SIZE = 4;

        // a 2k grid_block on disk contains 8x7 of the mavlink grids, overlapping their neighbours by one
        public const int GRID_BLOCK_MUL_X = 7;
        public const int GRID_BLOCK_MUL_Y = 8;

        // spacing between 32x28 grid blocks, in grid_spacing units
        public const int GRID_BLOCK_SPACING_X = (GRID_BLOCK_MUL_X - 1) * GRID_MAVLINK_SIZE; // 24 (north)
        public const int GRID_BLOCK_SPACING_Y = (GRID_BLOCK_MUL_Y - 1) * GRID_MAVLINK_SIZE; // 28 (east)

        // total size of a disk grid_block: 28 (north) x 32 (east) heights
        public const int GRID_BLOCK_SIZE_X = GRID_MAVLINK_SIZE * GRID_BLOCK_MUL_X; // 28
        public const int GRID_BLOCK_SIZE_Y = GRID_MAVLINK_SIZE * GRID_BLOCK_MUL_Y; // 32

        public const int GRID_FORMAT_VERSION = 1;

        // terrain data generated after the Feb 2026 lat/lon scaling fixes is tagged version_minor 1;
        // the autopilot warns ("terrain data expired") about blocks below this
        public const int VERSION_MINOR = 1;

        public const int IO_BLOCK_SIZE = 2048;
        public const int IO_BLOCK_DATA_SIZE = 1821; // CRC covers bytes [0, 1821)

        public const ulong FULL_BITMAP = (1UL << 56) - 1;

        public const string TERRAIN_DIRECTORY = "/APM/TERRAIN";

        static double ToFloat32(double v) => (double)(float)v;

        static readonly double LOCATION_SCALING_FACTOR = ToFloat32(0.011131884502145034);
        static readonly double LOCATION_SCALING_FACTOR_INV = ToFloat32(89.83204953368922);

        static double DegToRad(double d) => d * Math.PI / 180.0;

        static double LongitudeScale(double latDeg)
        {
            var scale = ToFloat32(Math.Cos(ToFloat32(DegToRad(latDeg))));
            return Math.Max(scale, 0.01);
        }

        static long DiffLongitudeE7(long lon1, long lon2)
        {
            if (lon1 * lon2 >= 0)
                return lon1 - lon2;
            var dlon = lon1 - lon2;
            if (dlon > 1800000000L)
                dlon -= 3600000000L;
            else if (dlon < -1800000000L)
                dlon += 3600000000L;
            return dlon;
        }

        /// <summary>north/east distance in metres from 1 to 2 (positions in 1e-7 degrees)</summary>
        public static void GetDistanceNE(long lat1, long lon1, long lat2, long lon2, out double north, out double east)
        {
            double dlat = lat2 - lat1;
            double dlng = DiffLongitudeE7(lon2, lon1) * LongitudeScale((lat1 + lat2) * 0.5 * 1.0e-7);
            north = dlat * LOCATION_SCALING_FACTOR;
            east = dlng * LOCATION_SCALING_FACTOR;
        }

        /// <summary>move a 1e-7 degree position by metres north and east, as ArduPilot's Location::offset</summary>
        public static void AddOffset(long latE7, long lonE7, double ofsNorth, double ofsEast, out long outLat,
            out long outLon)
        {
            // python int() truncates toward zero, as does a C# cast
            long dlat = (long)(ofsNorth * LOCATION_SCALING_FACTOR_INV);
            long dlng = (long)((ofsEast * LOCATION_SCALING_FACTOR_INV) /
                                LongitudeScale((latE7 + dlat * 0.5) * 1.0e-7));
            outLat = (long)(latE7 + dlat);
            outLon = (long)(lonE7 + dlng);
        }

        /// <summary>number of grid blocks per east-west row in a one degree file</summary>
        public static int EastBlocks(int latDegrees, int lonDegrees, int gridSpacing)
        {
            long latE7 = latDegrees * 10000000L;
            long lonE7 = lonDegrees * 10000000L;
            long lat2 = latE7;
            long lon2 = lonE7 + 10000000L;
            // shift another two blocks east to ensure room is available
            AddOffset(lat2, lon2, 0, 2 * gridSpacing * GRID_BLOCK_SIZE_Y, out lat2, out lon2);
            GetDistanceNE(latE7, lonE7, lat2, lon2, out _, out var east);
            return (int)(east / (gridSpacing * GRID_BLOCK_SPACING_Y));
        }

        /// <summary>a 2048 byte terrain block inside a one degree file</summary>
        public struct BlockId : IEquatable<BlockId>
        {
            public int LatDegrees;
            public int LonDegrees;
            public int GridIdxX; // north
            public int GridIdxY; // east

            public bool Equals(BlockId o) => LatDegrees == o.LatDegrees && LonDegrees == o.LonDegrees &&
                                             GridIdxX == o.GridIdxX && GridIdxY == o.GridIdxY;

            public override bool Equals(object obj) => obj is BlockId o && Equals(o);

            public override int GetHashCode() =>
                ((LatDegrees * 397 ^ LonDegrees) * 397 ^ GridIdxX) * 397 ^ GridIdxY;

            public override string ToString() => FileName(LatDegrees, LonDegrees) + " " + GridIdxX + "," + GridIdxY;
        }

        /// <summary>which block a position falls in</summary>
        public static BlockId BlockFor(double lat, double lon, int gridSpacing)
        {
            var b = new BlockId
            {
                LatDegrees = (int)Math.Floor(lat),
                LonDegrees = (int)Math.Floor(lon)
            };
            long latE7 = (long)(lat * 1.0e7);
            long lonE7 = (long)(lon * 1.0e7);
            long refLat = b.LatDegrees * 10000000L;
            long refLon = b.LonDegrees * 10000000L;
            GetDistanceNE(refLat, refLon, latE7, lonE7, out var north, out var east);
            // python round() is banker's rounding
            north = Math.Round(north, MidpointRounding.ToEven);
            east = Math.Round(east, MidpointRounding.ToEven);
            int idxX = (int)(north / gridSpacing);
            int idxY = (int)(east / gridSpacing);
            b.GridIdxX = FloorDiv(idxX, GRID_BLOCK_SPACING_X);
            b.GridIdxY = FloorDiv(idxY, GRID_BLOCK_SPACING_Y);
            return b;
        }

        static int FloorDiv(int a, int b) => (int)Math.Floor((double)a / b);

        /// <summary>south west corner of a block in 1e-7 degrees</summary>
        public static void BlockCorner(BlockId b, int gridSpacing, out long latE7, out long lonE7)
        {
            AddOffset(b.LatDegrees * 10000000L, b.LonDegrees * 10000000L,
                b.GridIdxX * GRID_BLOCK_SPACING_X * (double)gridSpacing,
                b.GridIdxY * GRID_BLOCK_SPACING_Y * (double)gridSpacing, out latE7, out lonE7);
        }

        /// <summary>position of height sample [gx,gy] (gx north 0..27, gy east 0..31) in 1e-7 degrees</summary>
        public static void SamplePosition(BlockId b, int gridSpacing, int gx, int gy, out long latE7, out long lonE7)
        {
            AddOffset(b.LatDegrees * 10000000L, b.LonDegrees * 10000000L,
                (b.GridIdxX * GRID_BLOCK_SPACING_X + gx) * (double)gridSpacing,
                (b.GridIdxY * GRID_BLOCK_SPACING_Y + gy) * (double)gridSpacing, out latE7, out lonE7);
        }

        public static long FileOffset(BlockId b, int gridSpacing)
        {
            long stride = EastBlocks(b.LatDegrees, b.LonDegrees, gridSpacing);
            return (stride * b.GridIdxX + b.GridIdxY) * IO_BLOCK_SIZE;
        }

        /// <summary>file name, e.g. N45W119.DAT</summary>
        public static string FileName(int latDegrees, int lonDegrees)
        {
            return string.Format("{0}{1:00}{2}{3:000}.DAT",
                latDegrees < 0 ? 'S' : 'N', Math.Min(Math.Abs(latDegrees), 99),
                lonDegrees < 0 ? 'W' : 'E', Math.Min(Math.Abs(lonDegrees), 999));
        }

        public static string FilePath(BlockId b) => TERRAIN_DIRECTORY + "/" + FileName(b.LatDegrees, b.LonDegrees);

        /// <summary>
        /// Build the 2048 byte block. heightAt(lat, lon) returns the terrain height in metres AMSL, or
        /// null when it is not known; then no block is produced (a partial block would be worse than none,
        /// the autopilot fetches missing blocks from the GCS instead).
        /// </summary>
        public static byte[] BuildBlock(BlockId b, int gridSpacing, Func<double, double, double?> heightAt)
        {
            var heights = new short[GRID_BLOCK_SIZE_X, GRID_BLOCK_SIZE_Y];
            for (int gx = 0; gx < GRID_BLOCK_SIZE_X; gx++)
            for (int gy = 0; gy < GRID_BLOCK_SIZE_Y; gy++)
            {
                SamplePosition(b, gridSpacing, gx, gy, out var latE7, out var lonE7);
                var h = heightAt(latE7 * 1.0e-7, lonE7 * 1.0e-7);
                if (h == null || double.IsNaN(h.Value))
                    return null;
                // python int() truncation, clamped to int16
                heights[gx, gy] = (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, (long)h.Value));
            }

            BlockCorner(b, gridSpacing, out var cornerLat, out var cornerLon);
            return Pack(b, gridSpacing, (int)cornerLat, (int)cornerLon, heights);
        }

        public static byte[] Pack(BlockId b, int gridSpacing, int cornerLat, int cornerLon, short[,] heights)
        {
            var buf = new byte[IO_BLOCK_SIZE];
            int o = 0;
            o = PutU64(buf, o, FULL_BITMAP);
            o = PutI32(buf, o, cornerLat);
            o = PutI32(buf, o, cornerLon);
            int crcOffset = o;
            o = PutU16(buf, o, 0); // crc, filled below
            o = PutU16(buf, o, GRID_FORMAT_VERSION);
            o = PutU16(buf, o, gridSpacing);
            for (int gx = 0; gx < GRID_BLOCK_SIZE_X; gx++)
            for (int gy = 0; gy < GRID_BLOCK_SIZE_Y; gy++)
                o = PutU16(buf, o, heights[gx, gy]);
            o = PutU16(buf, o, b.GridIdxX);
            o = PutU16(buf, o, b.GridIdxY);
            o = PutU16(buf, o, b.LonDegrees); // int16
            buf[o++] = (byte)(sbyte)b.LatDegrees; // int8
            if (o != IO_BLOCK_DATA_SIZE)
                throw new InvalidOperationException("terrain block layout error " + o);
            buf[o] = VERSION_MINOR; // excluded from the CRC

            var crc = Crc16Xmodem(buf, IO_BLOCK_DATA_SIZE);
            buf[crcOffset] = (byte)crc;
            buf[crcOffset + 1] = (byte)(crc >> 8);
            return buf;
        }

        static int PutU64(byte[] buf, int o, ulong v)
        {
            for (int i = 0; i < 8; i++) buf[o++] = (byte)(v >> (8 * i));
            return o;
        }

        static int PutI32(byte[] buf, int o, int v)
        {
            for (int i = 0; i < 4; i++) buf[o++] = (byte)(v >> (8 * i));
            return o;
        }

        static int PutU16(byte[] buf, int o, int v)
        {
            buf[o++] = (byte)v;
            buf[o++] = (byte)(v >> 8);
            return o;
        }

        static readonly ushort[] crcTable = MakeCrcTable();

        static ushort[] MakeCrcTable()
        {
            var t = new ushort[256];
            for (int i = 0; i < 256; i++)
            {
                int c = i << 8;
                for (int j = 0; j < 8; j++)
                    c = (c & 0x8000) != 0 ? (c << 1) ^ 0x1021 : c << 1;
                t[i] = (ushort)c;
            }

            return t;
        }

        /// <summary>CRC-16/XMODEM (ArduPilot crc16_ccitt with initial value 0)</summary>
        public static ushort Crc16Xmodem(byte[] data, int length)
        {
            ushort crc = 0;
            for (int i = 0; i < length; i++)
                crc = (ushort)((crc << 8) ^ crcTable[((crc >> 8) ^ data[i]) & 0xff]);
            return crc;
        }

        /// <summary>
        /// All blocks within bufferMetres of any point inside the polygon formed by the given points
        /// (their convex hull), so a straight-line return home from anywhere on the route is covered too.
        /// </summary>
        public static List<BlockId> BlocksCovering(IList<(double lat, double lng)> points, double bufferMetres,
            int gridSpacing)
        {
            var result = new HashSet<BlockId>();
            if (points == null || points.Count == 0)
                return new List<BlockId>();

            // work in a local flat projection around the first point (fine for missions of tens of km)
            double lat0 = points[0].lat, lng0 = points[0].lng;
            double mPerDegLat = 111319.49, mPerDegLng = 111319.49 * Math.Cos(DegToRad(lat0));
            var local = new List<(double x, double y)>();
            foreach (var p in points)
                local.Add(((p.lng - lng0) * mPerDegLng, (p.lat - lat0) * mPerDegLat));
            var hull = ConvexHull(local);

            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (var p in hull)
            {
                minX = Math.Min(minX, p.x); maxX = Math.Max(maxX, p.x);
                minY = Math.Min(minY, p.y); maxY = Math.Max(maxY, p.y);
            }

            minX -= bufferMetres; minY -= bufferMetres; maxX += bufferMetres; maxY += bufferMetres;

            // sample at a quarter of the smaller block dimension, so no block can be skipped over
            double step = Math.Max(10, GRID_BLOCK_SPACING_X * gridSpacing / 4.0);
            for (double y = minY; y <= maxY + step; y += step)
            for (double x = minX; x <= maxX + step; x += step)
            {
                var px = Math.Min(x, maxX);
                var py = Math.Min(y, maxY);
                // allow one sample step of slack so blocks straddling the buffer edge are included
                if (DistanceToPolygon(hull, px, py) > bufferMetres + step)
                    continue;
                result.Add(BlockFor(lat0 + py / mPerDegLat, lng0 + px / mPerDegLng, gridSpacing));
            }

            return new List<BlockId>(result);
        }

        static List<(double x, double y)> ConvexHull(List<(double x, double y)> pts)
        {
            var p = new List<(double x, double y)>(pts);
            p.Sort((a, b) => a.x != b.x ? a.x.CompareTo(b.x) : a.y.CompareTo(b.y));
            if (p.Count < 3)
                return p;
            var h = new List<(double x, double y)>();
            foreach (var pt in p)
            {
                while (h.Count >= 2 && Cross(h[h.Count - 2], h[h.Count - 1], pt) <= 0) h.RemoveAt(h.Count - 1);
                h.Add(pt);
            }

            int lower = h.Count + 1;
            for (int i = p.Count - 2; i >= 0; i--)
            {
                while (h.Count >= lower && Cross(h[h.Count - 2], h[h.Count - 1], p[i]) <= 0) h.RemoveAt(h.Count - 1);
                h.Add(p[i]);
            }

            h.RemoveAt(h.Count - 1);
            return h;
        }

        static double Cross((double x, double y) o, (double x, double y) a, (double x, double y) b)
        {
            return (a.x - o.x) * (b.y - o.y) - (a.y - o.y) * (b.x - o.x);
        }

        /// <summary>0 inside the polygon, otherwise the distance to its nearest edge</summary>
        static double DistanceToPolygon(List<(double x, double y)> poly, double x, double y)
        {
            if (poly.Count == 1)
                return Math.Sqrt(Math.Pow(poly[0].x - x, 2) + Math.Pow(poly[0].y - y, 2));
            bool inside = false;
            double best = double.MaxValue;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            {
                var a = poly[j];
                var b = poly[i];
                if (poly.Count >= 3 && ((b.y > y) != (a.y > y)) &&
                    (x < (a.x - b.x) * (y - b.y) / (a.y - b.y) + b.x))
                    inside = !inside;
                double dx = b.x - a.x, dy = b.y - a.y;
                double len2 = dx * dx + dy * dy;
                double t = len2 > 0 ? Math.Max(0, Math.Min(1, ((x - a.x) * dx + (y - a.y) * dy) / len2)) : 0;
                double ex = a.x + t * dx - x, ey = a.y + t * dy - y;
                best = Math.Min(best, Math.Sqrt(ex * ex + ey * ey));
            }

            return inside ? 0 : best;
        }
    }
}
