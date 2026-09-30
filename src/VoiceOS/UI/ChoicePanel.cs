using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using VoiceOS.Core.Interaction;

namespace VoiceOS.UI;

/// <summary>
/// The clickable surface of a <see cref="PendingChoice"/>: one short question and the real options, in the site's own words.
/// It shows no confidence, reference or model wording. It never takes focus from the page it is asking about, and it goes away
/// by itself when the choice expires.
/// </summary>
internal sealed class ChoicePanel : IDisposable
{
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();

    private readonly System.Windows.Forms.Timer _expiry = new();
    private ChoiceForm? _form;

    /// <summary>The user clicked an option; the argument is its choice id.</summary>
    public event Action<string>? Selected;
    /// <summary>The user closed the question without answering.</summary>
    public event Action? Dismissed;

    public ChoicePanel() => _expiry.Tick += (_, _) => Hide();

    public bool IsShowing => _form is not null;

    public void Show(PendingChoice choice)
    {
        Hide();
        IntPtr foreground = GetForegroundWindow();
        Screen screen = foreground != IntPtr.Zero ? Screen.FromHandle(foreground) : Screen.FromPoint(Cursor.Position);
        var form = new ChoiceForm(choice, screen);
        form.OptionClicked += id => { Hide(); Selected?.Invoke(id); };
        form.CloseClicked += () => { Hide(); Dismissed?.Invoke(); };
        _form = form;
        form.Show();
        _expiry.Interval = (int)Math.Clamp((choice.ExpiresAt - DateTimeOffset.UtcNow).TotalMilliseconds, 1000, int.MaxValue);
        _expiry.Start();
    }

    public void Hide()
    {
        _expiry.Stop();
        var form = _form;
        _form = null;
        if (form is null) return;
        form.Close();
        form.Dispose();
    }

    public void Dispose()
    {
        Hide();
        _expiry.Dispose();
    }

    private sealed class ChoiceForm : Form
    {
        private const int WsExToolWindow = 0x80;
        private const int WsExNoActivate = 0x08000000;
        private static readonly Color Surface = Color.FromArgb(30, 30, 33);
        private static readonly Color Hover = Color.FromArgb(58, 58, 64);
        private static readonly Color Ink = Color.FromArgb(240, 238, 232);
        private static readonly Color Muted = Color.FromArgb(160, 158, 152);

        public event Action<string>? OptionClicked;
        public event Action? CloseClicked;

        public ChoiceForm(PendingChoice choice, Screen screen)
        {
            double scale = GlowStripWindow.DpiScaleAt(screen.Bounds);
            int Px(double v) => (int)Math.Round(v * scale);
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Surface;
            Opacity = 0.97;
            var questionFont = new Font("Segoe UI Variable Display", (float)(14 * scale), FontStyle.Regular, GraphicsUnit.Pixel);
            var optionFont = new Font("Segoe UI Variable Display", (float)(15 * scale), FontStyle.Regular, GraphicsUnit.Pixel);
            var secondaryFont = new Font("Segoe UI Variable Display", (float)(12 * scale), FontStyle.Regular, GraphicsUnit.Pixel);
            Font = optionFont;

            int pad = Px(14), gap = Px(6), rowHeight = Px(40), rowHeightTwoLines = Px(56);
            int maxWidth = Math.Min(Px(420), screen.WorkingArea.Width - Px(36));
            int width = Px(280);
            foreach (var option in choice.Options)
            {
                width = Math.Max(width, TextRenderer.MeasureText(option.DisplayText, optionFont).Width + pad * 4);
                if (option.SecondaryText is { } secondary)
                    width = Math.Max(width, TextRenderer.MeasureText(secondary, secondaryFont).Width + pad * 4);
            }
            width = Math.Min(width, maxWidth);

            var question = new Label
            {
                Text = choice.Reason, Font = questionFont, ForeColor = Muted, BackColor = Surface, AutoSize = false,
                Left = pad, Top = Px(12), Width = width - pad * 2 - Px(24), Height = Px(22), AutoEllipsis = true
            };
            var close = new Label
            {
                Text = "✕", Font = questionFont, ForeColor = Muted, BackColor = Surface, AutoSize = false,
                TextAlign = ContentAlignment.MiddleCenter, Left = width - pad - Px(22), Top = Px(10), Width = Px(24), Height = Px(24),
                Cursor = Cursors.Hand
            };
            close.Click += (_, _) => CloseClicked?.Invoke();
            Controls.Add(question);
            Controls.Add(close);

            int y = Px(42);
            foreach (var option in choice.Options)
            {
                var button = new OptionButton(option.DisplayText, option.SecondaryText, optionFont, secondaryFont)
                {
                    Left = pad, Top = y, Width = width - pad * 2,
                    Height = option.SecondaryText is null ? rowHeight : rowHeightTwoLines
                };
                var id = option.ChoiceId;
                button.Click += (_, _) => OptionClicked?.Invoke(id);
                Controls.Add(button);
                y += button.Height + gap;
            }
            ClientSize = new Size(width, y + pad - gap);
            Location = new Point(screen.Bounds.Left + (screen.Bounds.Width - Width) / 2, screen.WorkingArea.Top + Px(26 + 50 + 10));
            Region = new Region(Rounded(new Rectangle(0, 0, Width, Height), Px(16)));
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WsExToolWindow | WsExNoActivate;
                return cp;
            }
        }

        private static GraphicsPath Rounded(Rectangle bounds, int radius)
        {
            var path = new GraphicsPath();
            int d = radius * 2;
            path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        /// <summary>One option: the site's own words, and when it has them the words that tell it apart.</summary>
        private sealed class OptionButton : Control
        {
            private readonly string _text;
            private readonly string? _secondary;
            private readonly Font _secondaryFont;
            private bool _hot;

            public OptionButton(string text, string? secondary, Font font, Font secondaryFont)
            {
                _text = text;
                _secondary = secondary;
                _secondaryFont = secondaryFont;
                Font = font;
                Cursor = Cursors.Hand;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
                AccessibleName = secondary is null ? text : $"{text}, {secondary}";
                AccessibleRole = AccessibleRole.PushButton;
            }

            protected override void OnMouseEnter(EventArgs e) { _hot = true; Invalidate(); base.OnMouseEnter(e); }
            protected override void OnMouseLeave(EventArgs e) { _hot = false; Invalidate(); base.OnMouseLeave(e); }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                using var path = Rounded(new Rectangle(0, 0, Width - 1, Height - 1), (int)(Height * 0.28));
                using (var fill = new SolidBrush(_hot ? Hover : Color.FromArgb(44, 44, 48))) g.FillPath(fill, path);
                var format = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap, LineAlignment = StringAlignment.Center };
                int pad = (int)(Height * 0.3);
                if (_secondary is null)
                {
                    using var ink = new SolidBrush(Ink);
                    g.DrawString(_text, Font, ink, new RectangleF(pad, 0, Width - pad * 2, Height), format);
                    return;
                }
                using var primary = new SolidBrush(Ink);
                using var muted = new SolidBrush(Muted);
                g.DrawString(_text, Font, primary, new RectangleF(pad, 0, Width - pad * 2, Height * 0.56f), format);
                g.DrawString(_secondary, _secondaryFont, muted, new RectangleF(pad, Height * 0.5f, Width - pad * 2, Height * 0.45f), format);
            }
        }
    }
}
