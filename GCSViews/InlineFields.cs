using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace MissionPlanner.GCSViews
{
    /// <summary>
    /// Self-drawn single-line text field. The Mac build's native TextBox/NumericUpDown can pop up as a
    /// full-window white editor, so QPlan draws its own: click it, type, Enter to accept, Esc to cancel.
    /// </summary>
    public class InlineTextBox : Control
    {
        public string Placeholder = "";
        public event EventHandler EnterPressed;
        protected string editText;
        protected bool selectAll;
        readonly Timer blink = new Timer {Interval = 500};
        bool caretOn;

        public static readonly Color Back = Color.FromArgb(18, 19, 22);
        public static readonly Color Border = Color.FromArgb(80, 84, 92);
        public static readonly Color Focus = Color.FromArgb(40, 140, 230);

        public InlineTextBox()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable | ControlStyles.ResizeRedraw,
                true);
            TabStop = true;
            BackColor = Back;
            ForeColor = Color.White;
            Font = new Font("Arial", 10f);
            Height = 26;
            Cursor = Cursors.IBeam;
            blink.Tick += (s, e) =>
            {
                caretOn = !caretOn;
                Invalidate();
            };
        }

        public bool Editing => editText != null || typing != null;

        TextBox typing;

        /// <summary>
        /// Open a real TextBox over this field for typing. On the Mac build keyboard text only reaches TextBoxes
        /// (through the app's text-entry field), so self-drawn fields hand typing to one of these.
        /// </summary>
        protected void StartTyping(string initial)
        {
            if (typing != null || Parent == null)
                return;
            var tb = new TextBox
            {
                Bounds = Bounds, Text = initial, Font = Font, BorderStyle = BorderStyle.FixedSingle,
                BackColor = Color.White, ForeColor = Color.Black
            };
            typing = tb;
            bool done = false;
            Action finish = () =>
            {
                if (done)
                    return;
                done = true;
                var text = tb.Text;
                typing = null;
                try
                {
                    BeginInvoke((Action) (() =>
                    {
                        tb.Parent?.Controls.Remove(tb);
                        tb.Dispose();
                    }));
                }
                catch
                {
                }

                TypedText(text);
            };
            tb.TextChanged += (s, e) => finish();
            tb.LostFocus += (s, e) => finish();
            tb.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Return)
                {
                    e.Handled = e.SuppressKeyPress = true;
                    finish();
                }
                else if (e.KeyCode == Keys.Escape)
                {
                    done = true;
                    typing = null;
                    tb.Parent?.Controls.Remove(tb);
                    tb.Dispose();
                }
            };
            Parent.Controls.Add(tb);
            tb.BringToFront();
            tb.Focus();
            tb.SelectAll();
        }

        /// <summary>text accepted from the typing box</summary>
        protected virtual void TypedText(string text)
        {
            Text = text;
            Invalidate();
            EnterPressed?.Invoke(this, EventArgs.Empty);
        }

        protected virtual string DisplayText => Text;

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left)
                StartTyping(Text);
        }

        protected void BeginEdit()
        {
            if (editText == null)
            {
                editText = Text;
                selectAll = true;
            }

            blink.Start();
            caretOn = true;
            Invalidate();
        }

        protected override void OnLostFocus(EventArgs e)
        {
            base.OnLostFocus(e);
            if (editText != null)
                Commit();
        }

        protected virtual void Commit()
        {
            if (editText != null)
                Text = editText;
            editText = null;
            blink.Stop();
            Invalidate();
        }

        protected void Cancel()
        {
            editText = null;
            blink.Stop();
            Invalidate();
        }

        protected override bool IsInputKey(Keys keyData)
        {
            var k = keyData & Keys.KeyCode;
            if (k == Keys.Up || k == Keys.Down || k == Keys.Left || k == Keys.Right || k == Keys.Enter ||
                k == Keys.Return || k == Keys.Escape || k == Keys.Back)
                return true;
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Handled)
                return;
            if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Return)
            {
                if (editText == null)
                    BeginEdit();
                Commit();
                EnterPressed?.Invoke(this, EventArgs.Empty);
                e.Handled = e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Escape)
            {
                Cancel();
                e.Handled = e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Back)
            {
                if (editText == null)
                    BeginEdit();
                if (selectAll)
                    editText = "";
                else if (editText.Length > 0)
                    editText = editText.Substring(0, editText.Length - 1);
                selectAll = false;
                Invalidate();
                e.Handled = e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.V && (e.Control || e.Modifiers == Keys.Alt))
            {
                try
                {
                    var t = Clipboard.GetText();
                    if (!string.IsNullOrEmpty(t))
                    {
                        if (editText == null)
                            BeginEdit();
                        editText = (selectAll ? "" : editText) + t.Replace("\r", "").Replace("\n", " ");
                        selectAll = false;
                        Invalidate();
                    }
                }
                catch
                {
                }

                e.Handled = e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.A && e.Control)
            {
                if (editText == null)
                    BeginEdit();
                selectAll = true;
                Invalidate();
                e.Handled = e.SuppressKeyPress = true;
            }
        }

        protected virtual bool Accept(char c) => !char.IsControl(c);

        protected override void OnKeyPress(KeyPressEventArgs e)
        {
            base.OnKeyPress(e);
            if (e.Handled || !Accept(e.KeyChar))
                return;
            if (editText == null)
                BeginEdit();
            editText = (selectAll ? "" : editText) + e.KeyChar;
            selectAll = false;
            caretOn = true;
            Invalidate();
            e.Handled = true;
        }

        protected virtual string Suffix => "";

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            var text = editText ?? DisplayText;
            var sz = g.MeasureString(string.IsNullOrEmpty(text) ? "0" : text, Font);
            float y = (Height - sz.Height) / 2;
            float x = 6;

            if (editText == null && string.IsNullOrEmpty(text) && Placeholder != "")
            {
                using (var b = new SolidBrush(Color.Gray))
                    g.DrawString(Placeholder, Font, b, x, y);
            }
            else
            {
                var w = string.IsNullOrEmpty(text) ? 0 : g.MeasureString(text, Font).Width;
                if (editText != null && selectAll && text.Length > 0)
                    using (var sel = new SolidBrush(Color.FromArgb(40, 100, 180)))
                        g.FillRectangle(sel, x, y, w, sz.Height);
                using (var b = new SolidBrush(ForeColor))
                    g.DrawString(text, Font, b, x, y);
                if (editText == null && Suffix != "")
                    using (var b = new SolidBrush(Color.Silver))
                        g.DrawString(Suffix, Font, b, x + w - 2, y);
                if (editText != null && caretOn && !selectAll)
                    using (var p = new Pen(Color.White, 1.5f))
                        g.DrawLine(p, x + w, y + 2, x + w, y + sz.Height - 2);
            }

            using (var p = new Pen(Focused ? Focus : Border, Focused ? 2 : 1))
                g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                blink.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// Number field: click to type a value, or click-and-drag (left/right or up/down) to dial it,
    /// mouse wheel and arrow keys step it. Same members as NumericUpDown where QPlan uses them.
    /// </summary>
    public class InlineNumber : InlineTextBox
    {
        decimal value;
        public decimal Minimum = 0;
        public decimal Maximum = 100;
        public int DecimalPlaces;
        public decimal Increment = 1;
        public string Unit = "";
        public event EventHandler ValueChanged;

        bool down;
        bool dragged;
        Point downAt;
        decimal downValue;

        public InlineNumber()
        {
            Cursor = Cursors.SizeWE;
        }

        public decimal Value
        {
            get => value;
            set
            {
                var v = Math.Max(Minimum, Math.Min(Maximum, Math.Round(value, DecimalPlaces)));
                if (v == this.value)
                {
                    Invalidate();
                    return;
                }

                this.value = v;
                Invalidate();
                ValueChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        protected override string DisplayText =>
            value.ToString("F" + DecimalPlaces, CultureInfo.InvariantCulture);

        protected override string Suffix => Unit == "" ? "" : " " + Unit;

        protected override bool Accept(char c) => char.IsDigit(c) || c == '.' || c == '-';

        protected override void TypedText(string text)
        {
            var t = (text ?? "").Replace(Unit, "").Trim();
            if (decimal.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                Value = v;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left)
                return;
            down = true;
            dragged = false;
            downAt = e.Location;
            downValue = value;
            Capture = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!down)
                return;
            int d = (e.X - downAt.X) - (e.Y - downAt.Y);
            if (!dragged && Math.Abs(d) < 4)
                return;
            if (!dragged)
            {
                dragged = true;
                if (Editing)
                    Cancel();
            }

            // one step per 2 pixels
            Value = downValue + Increment * (d / 2);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            // not base: the text field's mouse-up would open a second typing box
            if (!down)
                return;
            down = false;
            Capture = false;
            if (!dragged)
                StartTyping(DisplayText);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (Editing)
                Commit();
            Value += e.Delta > 0 ? Increment : -Increment;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Up || e.KeyCode == Keys.Down)
            {
                if (Editing)
                    Commit();
                var step = e.Shift ? Increment * 10 : Increment;
                Value += e.KeyCode == Keys.Up ? step : -step;
                e.Handled = e.SuppressKeyPress = true;
                return;
            }

            base.OnKeyDown(e);
        }

        protected override void Commit()
        {
            if (editText != null)
            {
                var t = editText.Trim();
                editText = null;
                if (decimal.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                    Value = v;
            }

            base.Commit();
        }
    }
}
