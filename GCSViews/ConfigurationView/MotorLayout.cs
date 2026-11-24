using MissionPlanner.Utilities;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace MissionPlanner.GCSViews.ConfigurationView
{
    /// <summary>one motor of a multirotor frame, from ArduPilot's AP_Motors layout data</summary>
    public class MotorInfo
    {
        /// <summary>motor number = output function MotorN</summary>
        public int Number;

        /// <summary>order used by the motor test (A = 1), clockwise from the front</summary>
        public int TestOrder;

        /// <summary>"CW", "CCW" or "?" seen from above</summary>
        public string Rotation;

        /// <summary>position, +X right, +Y forward (from the roll/pitch factors)</summary>
        public double X, Y;

        /// <summary>e.g. "Front Right"</summary>
        public string Position;

        /// <summary>motors stacked on the same arm (coaxial frames)</summary>
        public bool Coaxial;

        public char Letter => (char) ('A' + TestOrder - 1);

        public double AngleDegrees => (Math.Atan2(X, Y) * 180 / Math.PI + 360) % 360;
    }

    public static class MotorLayout
    {
        static JObject cache;

        /// <summary>APMotorLayout.json sits next to the program on Windows and in Contents/Resources on a Mac</summary>
        public static string FindLayoutFile()
        {
            var dirs = new List<string>();
            try
            {
                dirs.Add(Settings.GetRunningDirectory());
            }
            catch
            {
            }

            try
            {
                dirs.Add(Path.GetDirectoryName(Path.GetFullPath(Assembly.GetExecutingAssembly().Location)));
            }
            catch
            {
            }

            dirs.Add(AppDomain.CurrentDomain.BaseDirectory);

            foreach (var d in dirs.Where(a => !string.IsNullOrEmpty(a)).ToList())
            {
                var candidates = new[]
                {
                    Path.Combine(d, "APMotorLayout.json"),
                    Path.Combine(d, "..", "Resources", "APMotorLayout.json"),
                    Path.Combine(d, "Resources", "APMotorLayout.json")
                };
                foreach (var c in candidates)
                    if (File.Exists(c))
                        return c;
            }

            return null;
        }

        /// <summary>the vehicle's frame class/type (Copter FRAME_*, QuadPlane Q_FRAME_*)</summary>
        public static bool TryGetFrame(MAVLink.MAVLinkParamList param, out int frameClass, out int frameType)
        {
            frameClass = frameType = -1;
            foreach (var prefix in new[] {"", "Q_"})
            {
                if (param.ContainsKey(prefix + "FRAME_CLASS") && param.ContainsKey(prefix + "FRAME_TYPE"))
                {
                    frameClass = (int) param[prefix + "FRAME_CLASS"].Value;
                    frameType = (int) param[prefix + "FRAME_TYPE"].Value;
                    return true;
                }
            }

            return false;
        }

        /// <summary>motors for a frame class/type, or null when the layout is unknown</summary>
        public static List<MotorInfo> Load(int frameClass, int frameType)
        {
            try
            {
                if (cache == null)
                {
                    var file = FindLayoutFile();
                    if (file == null)
                        return null;
                    cache = JObject.Parse(File.ReadAllText(file));
                }

                foreach (var layout in cache["layouts"])
                {
                    if ((int) layout["Class"] != frameClass || (int) layout["Type"] != frameType)
                        continue;

                    var motors = layout["motors"].Select(m => new MotorInfo
                    {
                        Number = (int) m["Number"],
                        TestOrder = (int) m["TestOrder"],
                        Rotation = (string) m["Rotation"] ?? "?",
                        // AP_MotorsMatrix: roll factor = cos(angle + 90) => -X, pitch factor = cos(angle) => +Y
                        X = -(double) m["Roll"],
                        Y = (double) m["Pitch"]
                    }).OrderBy(a => a.TestOrder).ToList();

                    Name(motors);
                    return motors;
                }
            }
            catch
            {
            }

            return null;
        }

        static readonly string[] sectorNames =
            {"Front", "Front Right", "Right", "Rear Right", "Rear", "Rear Left", "Left", "Front Left"};

        static void Name(List<MotorInfo> motors)
        {
            foreach (var m in motors)
            {
                if (Math.Abs(m.X) < 1e-6 && Math.Abs(m.Y) < 1e-6)
                {
                    m.Position = "Centre";
                    continue;
                }

                int sector = (int) Math.Floor(((m.AngleDegrees + 22.5) % 360) / 45.0);
                m.Position = sectorNames[sector];
            }

            // stacked (coaxial) motors share a position: tell them apart by spin direction
            foreach (var g in motors.GroupBy(a => (Math.Round(a.X, 3), Math.Round(a.Y, 3))).Where(g => g.Count() > 1))
            foreach (var m in g)
            {
                m.Coaxial = true;
                m.Position += " (" + m.Rotation + ")";
            }

            // different positions with the same name (hexa/octa): add the clock position
            foreach (var g in motors.Where(a => !a.Coaxial).GroupBy(a => a.Position).Where(g => g.Count() > 1))
            foreach (var m in g)
            {
                int hour = (int) Math.Round(m.AngleDegrees / 30.0) % 12;
                if (hour == 0)
                    hour = 12;
                m.Position += " (" + hour + " o'clock)";
            }
        }

        /// <summary>SERVOn_FUNCTION value for MotorN</summary>
        public static int FunctionForMotor(int motor)
        {
            if (motor >= 1 && motor <= 8)
                return 32 + motor; // 33..40
            if (motor >= 9 && motor <= 12)
                return 82 + motor - 9; // 82..85
            if (motor >= 13 && motor <= 32)
                return 160 + motor - 13; // 160..179
            return -1;
        }

        /// <summary>motor number for a SERVOn_FUNCTION value, or -1</summary>
        public static int MotorForFunction(int function)
        {
            if (function >= 33 && function <= 40)
                return function - 32;
            if (function >= 82 && function <= 85)
                return function - 82 + 9;
            if (function >= 160 && function <= 179)
                return function - 160 + 13;
            return -1;
        }

        /// <summary>output number (1..32) currently assigned to each motor number</summary>
        public static Dictionary<int, int> OutputsByMotor(MAVLink.MAVLinkParamList param)
        {
            var result = new Dictionary<int, int>();
            for (int n = 1; n <= 32; n++)
            {
                var name = "SERVO" + n + "_FUNCTION";
                if (!param.ContainsKey(name))
                    continue;
                var motor = MotorForFunction((int) param[name].Value);
                if (motor > 0 && !result.ContainsKey(motor))
                    result[motor] = n;
            }

            return result;
        }

        /// <summary>DShot (or BLHeli) ESCs can be reversed with SERVO_BLH_RVMASK</summary>
        public static bool CanReverseInSoftware(MAVLink.MAVLinkParamList param)
        {
            if (!param.ContainsKey("SERVO_BLH_RVMASK"))
                return false;
            foreach (var name in new[] {"MOT_PWM_TYPE", "Q_M_PWM_TYPE"})
            {
                if (param.ContainsKey(name))
                {
                    var type = (int) param[name].Value;
                    // 4..7 = DShot150/300/600/1200
                    return type >= 4 && type <= 7;
                }
            }

            return false;
        }

        /// <summary>spin one motor (by test order, A = 1) with MAV_CMD_DO_MOTOR_TEST</summary>
        public static bool TestMotor(int testOrder, int throttlePercent, int seconds)
        {
            return MainV2.comPort.doCommand(MainV2.comPort.sysidcurrent, (byte) MainV2.comPort.compidcurrent,
                MAVLink.MAV_CMD.DO_MOTOR_TEST, testOrder,
                (float) (byte) MAVLink.MOTOR_TEST_THROTTLE_TYPE.MOTOR_TEST_THROTTLE_PERCENT, throttlePercent,
                seconds, 0, 0, 0);
        }
    }
}
