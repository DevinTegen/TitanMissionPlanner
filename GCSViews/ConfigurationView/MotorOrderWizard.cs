using MissionPlanner.Utilities;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace MissionPlanner.GCSViews.ConfigurationView
{
    /// <summary>
    /// Betaflight-style motor order wizard: each motor is spun in turn, the user clicks where on the frame it
    /// actually is (and optionally which way it turns), and the SERVOn_FUNCTION outputs are remapped (and
    /// DShot directions reversed with SERVO_BLH_RVMASK) so every motor ends up where ArduPilot expects it.
    /// </summary>
    public class MotorOrderWizard : Form
    {
        readonly List<MotorInfo> motors;
        readonly MotorDiagram diagram = new MotorDiagram();
        readonly Label instructions = new Label();
        readonly NumericUpDown throttle = new NumericUpDown();
        readonly CheckBox checkDirection = new CheckBox();
        readonly Button startButton = new Button();
        readonly Button spinAgainButton = new Button();
        readonly Button nothingButton = new Button();
        readonly Button cwButton = new Button();
        readonly Button ccwButton = new Button();
        readonly Button unsureButton = new Button();
        readonly Button applyButton = new Button();
        readonly Button rebootButton = new Button();
        readonly Button closeButton = new Button();

        int step = -1; // index into motors (test order)
        readonly Dictionary<MotorInfo, MotorInfo> observed = new Dictionary<MotorInfo, MotorInfo>(); // spun -> where
        readonly Dictionary<MotorInfo, string> observedRotation = new Dictionary<MotorInfo, string>();
        readonly List<MotorInfo> notSpinning = new List<MotorInfo>();
        MotorInfo pendingPosition; // clicked, waiting for the direction answer

        readonly Dictionary<string, double> pendingChanges = new Dictionary<string, double>();

        public MotorOrderWizard(List<MotorInfo> motors)
        {
            this.motors = motors.OrderBy(a => a.TestOrder).ToList();

            Text = "Motor order wizard";
            Size = new Size(760, 560);
            MinimumSize = new Size(700, 520);
            StartPosition = FormStartPosition.CenterParent;

            diagram.Location = new Point(10, 10);
            diagram.Size = new Size(420, 500);
            diagram.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left;
            diagram.Motors = this.motors;
            diagram.MotorClicked += OnMotorClicked;
            Controls.Add(diagram);

            int x = 445, w = 290;
            instructions.Location = new Point(x, 10);
            instructions.Size = new Size(w, 230);
            instructions.Font = new Font("Arial", 9.5f);
            Controls.Add(instructions);

            var thrLabel = new Label {Text = "Test throttle %", Location = new Point(x, 250), AutoSize = true};
            Controls.Add(thrLabel);
            throttle.Location = new Point(x + 110, 247);
            throttle.Size = new Size(60, 20);
            throttle.Minimum = 3;
            throttle.Maximum = 20;
            throttle.Value = 7;
            Controls.Add(throttle);

            checkDirection.Text = "Also check spin direction";
            checkDirection.Location = new Point(x, 275);
            checkDirection.AutoSize = true;
            checkDirection.Checked = this.motors.Any(a => a.Rotation == "CW" || a.Rotation == "CCW");
            checkDirection.Enabled = checkDirection.Checked;
            Controls.Add(checkDirection);

            Btn(startButton, "Start", x, 305, w, (s, e) => Start());
            Btn(spinAgainButton, "Spin it again", x, 345, 140, (s, e) => Spin());
            Btn(nothingButton, "Nothing spun", x + 150, 345, 140, (s, e) => NothingSpun());
            Btn(cwButton, "Clockwise", x, 385, 92, (s, e) => DirectionAnswer("CW"));
            Btn(ccwButton, "Counter-CW", x + 99, 385, 92, (s, e) => DirectionAnswer("CCW"));
            Btn(unsureButton, "Not sure", x + 198, 385, 92, (s, e) => DirectionAnswer(null));
            Btn(applyButton, "Apply changes", x, 425, w, (s, e) => Apply());
            Btn(rebootButton, "Reboot flight controller", x, 465, 140, (s, e) => Reboot());
            Btn(closeButton, "Close", x + 150, 465, 140, (s, e) => Close());

            SetButtons(start: true);
            ShowIntro();
            ThemeManager.ApplyThemeTo(this);
        }

        void Btn(Button b, string text, int x, int y, int w, EventHandler click)
        {
            b.Text = text;
            b.Location = new Point(x, y);
            b.Size = new Size(w, 32);
            b.Click += click;
            Controls.Add(b);
        }

        void SetButtons(bool start = false, bool spinning = false, bool direction = false, bool apply = false,
            bool reboot = false)
        {
            startButton.Enabled = start;
            spinAgainButton.Enabled = spinning;
            nothingButton.Enabled = spinning;
            cwButton.Enabled = ccwButton.Enabled = unsureButton.Enabled = direction;
            applyButton.Enabled = apply;
            rebootButton.Enabled = reboot;
            throttle.Enabled = start || spinning;
            checkDirection.Enabled = start && motors.Any(a => a.Rotation == "CW" || a.Rotation == "CCW");
            diagram.PickMode = spinning && !direction;
        }

        void ShowIntro()
        {
            instructions.Text =
                "This spins each motor in turn. Click the motor on the drawing that is actually spinning, " +
                "and the outputs are remapped so every motor is where ArduPilot expects it.\n\n" +
                "REMOVE ALL PROPELLERS FIRST.\n\n" +
                "The vehicle must be disarmed and powered from its flight battery.\n" +
                "Start with a low throttle; raise it if a motor does not start.\n\n" +
                "The drawing is seen from above, front at the top.";
        }

        void Start()
        {
            var mav = MainV2.comPort;
            if (mav?.BaseStream == null || !mav.BaseStream.IsOpen)
            {
                CustomMessageBox.Show("Connect to the vehicle first.", Text);
                return;
            }

            if (mav.MAV.cs.armed)
            {
                CustomMessageBox.Show("The vehicle is armed. Disarm first.", Text);
                return;
            }

            var outputs = MotorLayout.OutputsByMotor(mav.MAV.param);
            var missing = motors.Where(m => !outputs.ContainsKey(m.Number)).Select(m => "M" + m.Number).ToList();
            if (missing.Count > 0)
            {
                CustomMessageBox.Show("These motors are not assigned to any output (SERVOn_FUNCTION): " +
                                      string.Join(", ", missing) + ".\nAssign them first, then run the wizard.", Text);
                return;
            }

            if ((int) DialogResult.Yes != CustomMessageBox.Show(
                    "Are ALL propellers removed?\n\nThe motors will spin.", "Safety", MessageBoxButtons.YesNo))
                return;

            observed.Clear();
            observedRotation.Clear();
            notSpinning.Clear();
            pendingChanges.Clear();
            diagram.Assigned.Clear();
            step = 0;
            Spin();
        }

        MotorInfo Current => step >= 0 && step < motors.Count ? motors[step] : null;

        void Spin()
        {
            var m = Current;
            if (m == null)
                return;
            pendingPosition = null;
            diagram.Highlighted = null;
            diagram.Caption = "Step " + (step + 1) + " of " + motors.Count;
            instructions.Text = "Step " + (step + 1) + " of " + motors.Count + ": motor test " + m.Letter +
                                " is spinning.\n\nClick the motor on the drawing that is ACTUALLY spinning.\n\n" +
                                "If you missed it, press 'Spin it again'. If no motor moved, press 'Nothing spun'.";
            SetButtons(spinning: true);
            try
            {
                if (!MotorLayout.TestMotor(m.TestOrder, (int) throttle.Value, 3))
                    instructions.Text += "\n\nThe autopilot refused the motor test (is it armed, or the safety " +
                                         "switch on?).";
            }
            catch (Exception ex)
            {
                instructions.Text += "\n\n" + Strings.ErrorCommunicating + " " + ex.Message;
            }
        }

        void OnMotorClicked(MotorInfo clicked)
        {
            var m = Current;
            if (m == null)
                return;

            // coaxial motors share a position; the spin direction tells them apart
            var group = motors.Where(a => Math.Abs(a.X - clicked.X) < 1e-3 && Math.Abs(a.Y - clicked.Y) < 1e-3).ToList();
            if (group.Count == 1 && observed.Values.Contains(clicked))
            {
                CustomMessageBox.Show("That position was already picked for an earlier motor. Press 'Spin it " +
                                      "again' and watch closely.", Text);
                return;
            }

            if (group.Count > 1 && group.All(a => observed.Values.Contains(a)))
            {
                CustomMessageBox.Show("Both motors on that arm were already picked.", Text);
                return;
            }

            pendingPosition = clicked;
            diagram.Highlighted = clicked;

            if (checkDirection.Checked || group.Count > 1)
            {
                instructions.Text = "Which way is that motor turning, looking down from above?" +
                                    (group.Count > 1
                                        ? "\n\n(Two motors share this arm; the direction tells them apart.)"
                                        : "");
                SetButtons(direction: true);
                if (group.Count > 1)
                    unsureButton.Enabled = false;
                return;
            }

            Record(clicked, null);
        }

        void DirectionAnswer(string rotation)
        {
            var clicked = pendingPosition;
            if (clicked == null)
                return;
            var group = motors.Where(a => Math.Abs(a.X - clicked.X) < 1e-3 && Math.Abs(a.Y - clicked.Y) < 1e-3).ToList();
            if (group.Count > 1)
            {
                // coaxial: the motor on this arm that should turn this way
                var match = group.FirstOrDefault(a => a.Rotation == rotation && !observed.Values.Contains(a));
                if (match == null)
                {
                    CustomMessageBox.Show("The motor on that arm that turns " + rotation +
                                          " was already picked. Spin again and check the direction.", Text);
                    return;
                }

                // direction decided which motor it is, so it cannot also flag a reversal
                Record(match, null);
                return;
            }

            Record(clicked, rotation);
        }

        void Record(MotorInfo position, string rotation)
        {
            var m = Current;
            observed[m] = position;
            if (rotation != null)
                observedRotation[m] = rotation;
            diagram.Assigned.Add(position);
            Next();
        }

        void NothingSpun()
        {
            var m = Current;
            if (m == null)
                return;
            notSpinning.Add(m);
            Next();
        }

        void Next()
        {
            step++;
            diagram.Highlighted = null;
            if (step < motors.Count)
            {
                Spin();
                return;
            }

            Finish();
        }

        void Finish()
        {
            diagram.Caption = "";
            SetButtons(start: true);
            pendingChanges.Clear();
            var param = MainV2.comPort.MAV.param;
            var sb = new StringBuilder();

            if (notSpinning.Count > 0)
            {
                sb.AppendLine("These motor tests did not spin anything: " +
                              string.Join(", ", notSpinning.Select(a => a.Letter + " (M" + a.Number + ")")) + ".");
                sb.AppendLine("Check those ESCs, their wiring and MOT_SPIN_ARM, then run the wizard again.");
                instructions.Text = sb.ToString();
                return;
            }

            var outputs = MotorLayout.OutputsByMotor(param);

            // the output that drives spun motor m physically sits at position p, so give it p's function
            var newFunction = new Dictionary<int, int>();
            foreach (var kv in observed)
            {
                var spun = kv.Key;
                var at = kv.Value;
                if (!outputs.TryGetValue(spun.Number, out var output))
                    continue;
                newFunction[output] = MotorLayout.FunctionForMotor(at.Number);
            }

            int remapped = 0;
            foreach (var kv in newFunction.OrderBy(a => a.Key))
            {
                var name = "SERVO" + kv.Key + "_FUNCTION";
                var current = (int) param[name].Value;
                if (current != kv.Value)
                {
                    pendingChanges[name] = kv.Value;
                    sb.AppendLine("Output " + kv.Key + ": Motor" + MotorLayout.MotorForFunction(current) + " -> Motor" +
                                  MotorLayout.MotorForFunction(kv.Value));
                    remapped++;
                }
            }

            // spin direction, judged against the motor that will now be on that output
            var wrongDirection = new List<(int output, MotorInfo at)>();
            foreach (var kv in observedRotation)
            {
                var at = observed[kv.Key];
                if (at.Rotation != "CW" && at.Rotation != "CCW")
                    continue;
                if (kv.Value != at.Rotation && outputs.TryGetValue(kv.Key.Number, out var output))
                    wrongDirection.Add((output, at));
            }

            if (wrongDirection.Count > 0)
            {
                if (MotorLayout.CanReverseInSoftware(param))
                {
                    var mask = (int) param["SERVO_BLH_RVMASK"].Value;
                    var newMask = mask;
                    foreach (var w in wrongDirection)
                        newMask ^= 1 << (w.output - 1);
                    pendingChanges["SERVO_BLH_RVMASK"] = newMask;
                    sb.AppendLine("Reverse " + string.Join(", ", wrongDirection.Select(w => w.at.Position)) +
                                  " (SERVO_BLH_RVMASK " + mask + " -> " + newMask + ")");
                }
                else
                {
                    sb.AppendLine("Spinning the wrong way: " + string.Join(", ", wrongDirection.Select(w => w.at.Position)) +
                                  ". Swap any two of the three wires on those motors (or use DShot ESCs so this " +
                                  "can be fixed in software).");
                }
            }

            if (pendingChanges.Count == 0)
            {
                instructions.Text = (remapped == 0 && wrongDirection.Count == 0
                                        ? "Everything is correct: every motor is in the right place"
                                          + (observedRotation.Count > 0 ? " and spins the right way." : ".")
                                        : "No parameter changes possible.") + "\n\n" + sb;
                return;
            }

            instructions.Text = "Changes needed:\n\n" + sb + "\nPress 'Apply changes' to write them. Then reboot " +
                                "and run the wizard again to confirm.";
            SetButtons(start: true, apply: true);
        }

        void Apply()
        {
            if (pendingChanges.Count == 0)
                return;
            var mav = MainV2.comPort;
            if (mav.MAV.cs.armed)
            {
                CustomMessageBox.Show("The vehicle is armed. Disarm first.", Text);
                return;
            }

            var failed = new List<string>();
            foreach (var kv in pendingChanges)
            {
                try
                {
                    if (!mav.setParam(kv.Key, kv.Value))
                        failed.Add(kv.Key);
                }
                catch
                {
                    failed.Add(kv.Key);
                }
            }

            pendingChanges.Clear();
            if (failed.Count > 0)
            {
                instructions.Text = "Could not write: " + string.Join(", ", failed) + ".";
                SetButtons(start: true);
                return;
            }

            instructions.Text = "Saved.\n\nReboot the flight controller for the new motor order to take effect, " +
                                "then press Start to run the wizard again and confirm every motor is right.";
            SetButtons(start: true, reboot: true);
        }

        void Reboot()
        {
            if (MainV2.comPort.MAV.cs.armed)
                return;
            try
            {
                MainV2.comPort.doReboot(false, true);
                instructions.Text = "Rebooting. Wait for it to reconnect, then press Start to check the result.";
            }
            catch (Exception ex)
            {
                instructions.Text = "Reboot failed: " + ex.Message;
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // stop anything still spinning
            try
            {
                if (step >= 0 && MainV2.comPort?.BaseStream != null && MainV2.comPort.BaseStream.IsOpen)
                    foreach (var m in motors)
                        MotorLayout.TestMotor(m.TestOrder, 0, 0);
            }
            catch
            {
            }

            base.OnFormClosing(e);
        }
    }
}
