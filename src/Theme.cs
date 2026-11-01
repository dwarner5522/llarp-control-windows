// The look of LLARP Control (same palette as the GTK app): near-black, lime -> green -> teal.

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace LlarpControl
{
    static class Theme
    {
        public static readonly Color Bg = C(0x191919);
        public static readonly Color Surface = C(0x202120);
        public static readonly Color Raised = C(0x2a2c2a);
        public static readonly Color Hover = C(0x333733);
        public static readonly Color Border = C(0x393d39);
        public static readonly Color Well = C(0x111211);
        public static readonly Color Text = C(0xe4e8e2);
        public static readonly Color Dim = C(0x8e968c);
        public static readonly Color Lime = C(0x6fb34c);
        public static readonly Color Bright = C(0x86cf5f);
        public static readonly Color Green = C(0x128741);
        public static readonly Color Teal = C(0x006261);
        public static readonly Color Busy = C(0xd6b450);
        public static readonly Color Err = C(0xe5675c);
        public static readonly Color LogBg = C(0x0d0e0d);
        public static readonly Color LogFg = C(0xa9d991);
        public static readonly Color Banner = C(0x1c2a1c);

        public static readonly Font UI = new Font("Segoe UI", 9f);
        public static readonly Font Bold = new Font("Segoe UI", 9f, FontStyle.Bold);
        public static readonly Font Status = new Font("Segoe UI", 11f, FontStyle.Bold);
        public static readonly Font Brand = new Font("Segoe UI", 16f, FontStyle.Bold);
        public static readonly Font Title = new Font("Segoe UI", 13f, FontStyle.Bold);
        public static readonly Font Mono = new Font("Consolas", 9.5f);

        static Color C(int rgb)
        {
            return Color.FromArgb(255, (rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255);
        }

        public static Button Button(string text, bool primary = false)
        {
            var b = new Button
            {
                Text = text,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlatStyle = FlatStyle.Flat,
                BackColor = primary ? Green : Raised,
                ForeColor = primary ? Color.White : Text,
                Font = primary ? Bold : UI,
                Padding = new Padding(8, 2, 8, 2),
                Margin = new Padding(0, 0, 6, 0),
                Cursor = Cursors.Hand,
                UseVisualStyleBackColor = false,
            };
            b.FlatAppearance.BorderColor = primary ? Lime : Border;
            b.FlatAppearance.MouseOverBackColor = primary ? C(0x17a050) : Hover;
            b.FlatAppearance.MouseDownBackColor = Green;
            b.EnabledChanged += (s, e) => b.ForeColor = b.Enabled ? (primary ? Color.White : Text) : C(0x5c625b);
            return b;
        }

        public static Label Label(string text, Color? color = null, Font font = null)
        {
            return new Label
            {
                Text = text,
                AutoSize = true,
                ForeColor = color ?? Text,
                Font = font ?? UI,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(0, 3, 6, 3),
            };
        }

        /// A label that wraps to the width of its table cell.
        public static Label Wrap(string text, Color? color = null)
        {
            var l = Label(text, color);
            l.Dock = DockStyle.Fill;
            l.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            return l;
        }

        public static CheckBox Check(string text)
        {
            var c = new CheckBox
            {
                Text = text,
                AutoSize = true,
                FlatStyle = FlatStyle.Flat,
                ForeColor = Text,
                Font = UI,
                Margin = new Padding(0, 4, 6, 4),
                Cursor = Cursors.Hand,
            };
            c.FlatAppearance.BorderColor = Border;
            c.FlatAppearance.CheckedBackColor = Green;
            c.FlatAppearance.MouseOverBackColor = Hover;
            return c;
        }

        public static TextBox Entry(bool mono = false)
        {
            return new TextBox
            {
                BackColor = Well,
                ForeColor = Text,
                BorderStyle = BorderStyle.FixedSingle,
                Font = mono ? Mono : UI,
                Anchor = AnchorStyles.Left | AnchorStyles.Right,
            };
        }

        public static TextBox Output()
        {
            return new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                WordWrap = true,
                BackColor = LogBg,
                ForeColor = LogFg,
                BorderStyle = BorderStyle.FixedSingle,
                Font = Mono,
                Dock = DockStyle.Fill,
                HideSelection = false,
            };
        }

        public static TableLayoutPanel Table(int columns)
        {
            var t = new TableLayoutPanel
            {
                ColumnCount = columns,
                RowCount = 0,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Fill,
                Margin = new Padding(0),
                Padding = new Padding(0),
            };
            return t;
        }

        public static FlowLayoutPanel Flow()
        {
            return new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = true,
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 4, 0, 4),
            };
        }

        /// Append a control as a new auto-sized row (or a filling row when fill is set).
        public static void AddRow(TableLayoutPanel t, Control c, bool fill = false, int span = 1)
        {
            t.RowCount++;
            t.RowStyles.Add(fill ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.AutoSize));
            t.Controls.Add(c, 0, t.RowCount - 1);
            if (span > 1) t.SetColumnSpan(c, span);
        }

        public static GraphicsPath Rounded(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            float d = radius * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        /// The logo's motif: a diamond shaded lime -> green -> teal, optionally glowing.
        public static void DrawDiamond(Graphics g, RectangleF box, bool glow)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float cx = box.X + box.Width / 2, cy = box.Y + box.Height / 2;
            float size = Math.Min(box.Width, box.Height);
            float r = size * (glow ? 0.36f : 0.48f);
            if (glow)
            {
                using (var halo = new GraphicsPath())
                {
                    halo.AddEllipse(cx - size / 2, cy - size / 2, size, size);
                    using (var br = new PathGradientBrush(halo))
                    {
                        br.CenterColor = Color.FromArgb(130, Lime);
                        br.SurroundColors = new[] { Color.FromArgb(0, Lime) };
                        g.FillPath(br, halo);
                    }
                }
            }
            var pts = new[] { new PointF(cx, cy - r), new PointF(cx + r, cy), new PointF(cx, cy + r), new PointF(cx - r, cy) };
            using (var br = new LinearGradientBrush(new PointF(cx - r - 1, cy), new PointF(cx + r + 1, cy), Lime, Teal))
            {
                br.InterpolationColors = new ColorBlend
                {
                    Colors = new[] { Lime, Green, Teal },
                    Positions = new[] { 0f, 0.5f, 1f },
                };
                g.FillPolygon(br, pts);
            }
        }

        public static Bitmap LogoBitmap(int size, bool background)
        {
            var bmp = new Bitmap(size, size);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                if (background)
                {
                    using (var path = Rounded(new RectangleF(0, 0, size - 1, size - 1), size * 0.18f))
                    using (var br = new SolidBrush(Bg))
                        g.FillPath(br, path);
                }
                DrawDiamond(g, new RectangleF(0, 0, size, size), background);
            }
            return bmp;
        }

        public static Icon AppIcon()
        {
            try
            {
                var ico = Icon.ExtractAssociatedIcon(Paths.Exe);
                if (ico != null) return ico;
            }
            catch (Exception) { }
            return Icon.FromHandle(LogoBitmap(64, true).GetHicon());
        }

        /// Colour every child control for the dark theme (things not styled when created).
        public static void Apply(Control root)
        {
            foreach (Control c in root.Controls)
            {
                if (c is TableLayoutPanel || c is FlowLayoutPanel || (c.GetType() == typeof(Panel)))
                {
                    if (c.BackColor == SystemColors.Control) c.BackColor = Color.Transparent;
                }
                Apply(c);
            }
        }
    }

    /// An on/off switch. Clicking raises Toggled; the owner decides the new state.
    class ToggleSwitch : Control
    {
        bool on;
        bool hover;
        public event EventHandler Toggled;

        public ToggleSwitch()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint
                     | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor | ControlStyles.Selectable, true);
            Size = new Size(48, 24);
            Cursor = Cursors.Hand;
            TabStop = true;
            Anchor = AnchorStyles.Right;
            Margin = new Padding(6, 4, 6, 4);
        }

        public bool Checked
        {
            get { return on; }
            set { if (on != value) { on = value; Invalidate(); } }
        }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            Focus();
            if (Enabled && Toggled != null) Toggled(this, e);
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            base.OnKeyUp(e);
            if (e.KeyCode == Keys.Space) OnClick(e);
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Bg);
            var r = new RectangleF(1, 1, Width - 3, Height - 3);
            float radius = r.Height / 2;
            using (var path = Theme.Rounded(r, radius))
            {
                if (on)
                {
                    using (var br = new LinearGradientBrush(r, Theme.Lime, Theme.Teal, 0f))
                    {
                        br.InterpolationColors = new ColorBlend
                        {
                            Colors = new[] { Theme.Lime, Theme.Green, Theme.Teal },
                            Positions = new[] { 0f, 0.55f, 1f },
                        };
                        g.FillPath(br, path);
                    }
                }
                else
                {
                    using (var br = new SolidBrush(hover && Enabled ? Theme.Hover : Color.FromArgb(0x2d, 0x30, 0x2d)))
                        g.FillPath(br, path);
                }
                using (var pen = new Pen(Focused ? Theme.Lime : on ? Theme.Green : Theme.Border))
                    g.DrawPath(pen, path);
            }
            float d = r.Height - 6;
            float x = on ? r.Right - d - 3 : r.Left + 3;
            using (var br = new SolidBrush(on ? Color.FromArgb(0xf3, 0xf8, 0xf0) : Color.FromArgb(0xcf, 0xd5, 0xcd)))
                g.FillEllipse(br, x, r.Top + 3, d, d);
            if (!Enabled)
            {
                using (var br = new SolidBrush(Color.FromArgb(140, Parent != null ? Parent.BackColor : Theme.Bg)))
                    g.FillRectangle(br, ClientRectangle);
            }
        }
    }

    class LogoBox : Control
    {
        public LogoBox()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint
                     | ControlStyles.ResizeRedraw, true);
            Size = new Size(38, 38);
            Margin = new Padding(0, 0, 6, 0);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent != null ? Parent.BackColor : Theme.Bg);
            Theme.DrawDiamond(e.Graphics, new RectangleF(0, 0, Width, Height), true);
        }
    }

    /// A tab header: text with a lime underline when selected.
    class TabButton : Control
    {
        bool selected;
        bool hover;

        public TabButton(string text)
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            Text = text;
            Font = Theme.Bold;
            Cursor = Cursors.Hand;
            Margin = new Padding(0, 0, 2, 0);
            var size = TextRenderer.MeasureText(text, Theme.Bold);
            Size = new Size(size.Width + 32, size.Height + 16);
        }

        public bool Selected
        {
            get { return selected; }
            set { selected = value; Invalidate(); }
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Bg);
            var color = selected ? Theme.Lime : hover ? Theme.Text : Theme.Dim;
            TextRenderer.DrawText(g, Text, Font, new Rectangle(0, 0, Width, Height - 3), color,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            if (selected || hover)
            {
                using (var br = new SolidBrush(selected ? Theme.Lime : Color.FromArgb(90, Theme.Lime)))
                    g.FillRectangle(br, 0, Height - 3, Width, 3);
            }
        }
    }

    /// A panel with a thin rounded border (the exit-node panel, banners).
    class BorderPanel : Panel
    {
        public Color BorderColor = Theme.Border;

        public BorderPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint
                     | ControlStyles.ResizeRedraw, true);
            Padding = new Padding(10, 8, 10, 8);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Bg);
            using (var path = Theme.Rounded(new RectangleF(0.5f, 0.5f, Width - 2, Height - 2), 5))
            {
                using (var br = new SolidBrush(BackColor)) g.FillPath(br, path);
                using (var pen = new Pen(BorderColor)) g.DrawPath(pen, path);
            }
        }
    }

    /// A small one-line text prompt.
    class InputDialog : Form
    {
        readonly TextBox entry;

        InputDialog(string title, string prompt, string placeholder, bool secret)
        {
            Text = title;
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = Theme.UI;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(14);

            var t = Theme.Table(1);
            Theme.AddRow(t, Theme.Label(prompt));
            entry = Theme.Entry(true);
            entry.Width = 460;
            entry.Text = placeholder ?? "";
            entry.UseSystemPasswordChar = secret;
            Theme.AddRow(t, entry);
            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 10, 0, 0) };
            var ok = Theme.Button("OK", true);
            ok.DialogResult = DialogResult.OK;
            var cancel = Theme.Button("Cancel");
            cancel.DialogResult = DialogResult.Cancel;
            buttons.Controls.Add(ok);
            buttons.Controls.Add(cancel);
            Theme.AddRow(t, buttons);
            Controls.Add(t);
            AcceptButton = ok;
            CancelButton = cancel;
        }

        public static string Ask(IWin32Window owner, string title, string prompt, string initial = null, bool secret = false)
        {
            using (var d = new InputDialog(title, prompt, initial, secret))
                return d.ShowDialog(owner) == DialogResult.OK ? d.entry.Text.Trim() : null;
        }
    }
}
