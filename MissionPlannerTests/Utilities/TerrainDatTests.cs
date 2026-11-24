using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using MissionPlanner.Utilities;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MissionPlanner.Utilities.Tests
{
    /// <summary>
    /// TerrainDat must produce exactly what ArduPilot's reference generator (terraingen/terrain_gen.py)
    /// produces, or the autopilot rejects the blocks. Expected values below were generated with
    /// terrain_gen.py using the same synthetic height function.
    /// </summary>
    [TestClass]
    public class TerrainDatTests
    {
        static double SyntheticHeight(double lat, double lon) =>
            500 + 300 * Math.Sin(lat * 37) + 200 * Math.Cos(lon * 53) + (lat - lon) * 0.123;

        [DataTestMethod]
        [DataRow(100, 45.67, -118.79, 31, 5, 456683504, -1188210952, 30, 1914880L, "2baec83a63887e03")]
        [DataRow(100, 47.6, -122.3, 27, 18, 475821116, -1223324922, 29, 1640448L, "d428b25a2defb1dc")]
        [DataRow(100, -33.9, 151.2, 4, 6, -339137613, 1511819474, 35, 299008L, "41d9dff35338b7fa")]
        [DataRow(100, -0.5, -78.5, 23, 19, -5041272, -785220524, 42, 2017280L, "6ff76756edac0b67")]
        [DataRow(100, 10.999, -0.001, 46, 39, 109917457, -23413, 41, 3942400L, "cd44246165aa1028")]
        [DataRow(100, 64.1, -21.9, 4, 1, 640862387, -219425331, 19, 157696L, "f103a69aeb0190ae")]
        [DataRow(100, 45.0001, -118.9999, 0, 0, 450000000, -1190000000, 30, 0L, "0a495b531fe07c26")]
        [DataRow(100, 45.9999, -118.0001, 46, 27, 459917457, -1180311435, 30, 2881536L, "5c0bbb889e11ef62")]
        [DataRow(30, 45.67, -118.79, 103, 19, 456661944, -1187960524, 95, 20078592L, "53c93b5640c9c8d1")]
        [DataRow(30, 47.6, -122.3, 92, 62, 475950474, -1223101575, 92, 17461248L, "edea46089f113060")]
        [DataRow(30, -33.9, 151.2, 15, 21, -339029814, 1511910327, 112, 3483648L, "1c2b5ef7b743ad55")]
        [DataRow(30, -0.5, -78.5, 77, 66, -5019712, -785019284, 134, 21266432L, "3af122966feee29c")]
        [DataRow(30, 10.999, -0.001, 154, 130, 109960577, -23343, 132, 41897984L, "75238251a590d2e2")]
        [DataRow(30, 64.1, -21.9, 15, 5, 640970186, -219137830, 60, 1853440L, "c7aa1938f2b7d506")]
        [DataRow(30, 45.0001, -118.9999, 0, 0, 450000000, -1190000000, 95, 0L, "5bcfd4899de8664b")]
        [DataRow(30, 45.9999, -118.0001, 154, 92, 459960577, -1180095755, 95, 30150656L, "c0354f22ef728644")]
        public void MatchesArduPilotReferenceGenerator(int spacing, double lat, double lon, int gridX, int gridY,
            long cornerLat, long cornerLon, int eastBlocks, long fileOffset, string sha256Prefix)
        {
            var b = TerrainDat.BlockFor(lat, lon, spacing);
            Assert.AreEqual(gridX, b.GridIdxX);
            Assert.AreEqual(gridY, b.GridIdxY);

            TerrainDat.BlockCorner(b, spacing, out var la, out var lo);
            Assert.AreEqual(cornerLat, la);
            Assert.AreEqual(cornerLon, lo);

            Assert.AreEqual(eastBlocks, TerrainDat.EastBlocks(b.LatDegrees, b.LonDegrees, spacing));
            Assert.AreEqual(fileOffset, TerrainDat.FileOffset(b, spacing));

            var block = TerrainDat.BuildBlock(b, spacing, (a, c) => SyntheticHeight(a, c));
            Assert.AreEqual(TerrainDat.IO_BLOCK_SIZE, block.Length);
            using (var sha = SHA256.Create())
            {
                var hash = BitConverter.ToString(sha.ComputeHash(block)).Replace("-", "").ToLowerInvariant();
                Assert.AreEqual(sha256Prefix, hash.Substring(0, 16));
            }
        }

        [TestMethod]
        public void FileNames()
        {
            Assert.AreEqual("N45W119.DAT", TerrainDat.FileName(45, -119));
            Assert.AreEqual("S34E151.DAT", TerrainDat.FileName(-34, 151));
            Assert.AreEqual("S01W079.DAT", TerrainDat.FileName(-1, -79));
        }

        [TestMethod]
        public void MissingTerrainGivesNoBlock()
        {
            var b = TerrainDat.BlockFor(45.67, -118.79, 100);
            Assert.IsNull(TerrainDat.BuildBlock(b, 100, (a, c) => null));
        }

        [TestMethod]
        public void RouteBufferIsCovered()
        {
            var pts = new List<(double lat, double lng)>
                {(45.6720, -118.7900), (45.7100, -118.7500), (45.7300, -118.8200), (45.6900, -118.8600)};
            foreach (var spacing in new[] {100, 30})
            {
                var set = new HashSet<TerrainDat.BlockId>(TerrainDat.BlocksCovering(pts, 1609.344, spacing));
                var r = new Random(1);
                for (int i = 0; i < 5000; i++)
                {
                    var a = pts[r.Next(pts.Count)];
                    var c = pts[r.Next(pts.Count)];
                    double t = r.NextDouble();
                    double lat = a.lat + (c.lat - a.lat) * t, lng = a.lng + (c.lng - a.lng) * t;
                    double ang = r.NextDouble() * 2 * Math.PI, d = r.NextDouble() * 1600;
                    lat += d * Math.Cos(ang) / 111319;
                    lng += d * Math.Sin(ang) / (111319 * Math.Cos(lat * Math.PI / 180));
                    Assert.IsTrue(set.Contains(TerrainDat.BlockFor(lat, lng, spacing)),
                        "point " + lat + "," + lng + " not covered at spacing " + spacing);
                }
            }
        }
    }
}
