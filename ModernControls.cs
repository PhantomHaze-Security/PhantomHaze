using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace PhantomHaze;

/// <summary>Painting shared by the rounded controls of the top bar (pill buttons, pill panels, the protection banner).</summary>
internal static class RoundedControlPainting
{
    /// <summary>
    /// Paints what is behind <paramref name="control"/>, so the corners outside its rounded shape show its parent. A solid
    /// parent colour is filled directly; otherwise (transparent parents in gradient themes, custom-painted surfaces) the
    /// parent is asked to paint itself into the control's area, which is what WinForms does for transparent controls.
    /// </summary>
    public static void PaintParentBackground(
        Control control,
        PaintEventArgs e,
        Action<Control, PaintEventArgs> invokePaintBackground,
        Action<Control, PaintEventArgs> invokePaint)
    {
        Control? parent = control.Parent;
        if (parent is null)
        {
            e.Graphics.Clear(SystemColors.Control);
            return;
        }

        Color parentBack = parent.BackColor;
        if (parentBack.A == 255 && parent is not ThemedPanel { BackgroundPainter: not null })
        {
            using var brush = new SolidBrush(parentBack);
            e.Graphics.FillRectangle(brush, control.ClientRectangle);
            return;
        }

        GraphicsState state = e.Graphics.Save();
        try
        {
            e.Graphics.TranslateTransform(-control.Left, -control.Top);
            Rectangle clip = e.ClipRectangle;
            clip.Offset(control.Left, control.Top);

            // Built on the caller's Graphics, which it does not own, so it is not disposed here.
            var parentArgs = new PaintEventArgs(e.Graphics, clip);
            invokePaintBackground(parent, parentArgs);
            invokePaint(parent, parentArgs);
        }
        finally
        {
            e.Graphics.Restore(state);
        }
    }

    /// <summary>A pill (radius 0 or less: half the shorter side) that fits inside <paramref name="bounds"/>.</summary>
    public static GraphicsPath CreatePillPath(RectangleF bounds, float cornerRadius)
    {
        var inner = new RectangleF(bounds.X + 0.5f, bounds.Y + 0.5f, Math.Max(1f, bounds.Width - 1f), Math.Max(1f, bounds.Height - 1f));
        float radius = cornerRadius > 0 ? cornerRadius : Math.Min(inner.Width, inner.Height) / 2f;
        return PhantomShapeGeometry.CreateRoundedRectangle(inner, radius);
    }

    public static Color Blend(Color from, Color to, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        int Mix(int a, int b) => (int)Math.Round(a + ((b - a) * amount));
        return Color.FromArgb(255, Mix(from.R, to.R), Mix(from.G, to.G), Mix(from.B, to.B));
    }

    /// <summary>True for colours on which white text reads better than black text.</summary>
    public static bool IsDark(Color color)
    {
        static double Channel(int value)
        {
            double c = value / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        double luminance = (0.2126 * Channel(color.R)) + (0.7152 * Channel(color.G)) + (0.0722 * Channel(color.B));
        return (1.05 / (luminance + 0.05)) >= ((luminance + 0.05) / 0.05);
    }

    /// <summary>The colour of a hovered (or, more strongly, pressed) surface.</summary>
    public static Color Highlight(Color color, bool pressed)
    {
        return Blend(color, IsDark(color) ? Color.White : Color.Black, pressed ? 0.16 : 0.08);
    }

    public static TextFormatFlags MnemonicFlags(bool useMnemonic, bool showKeyboardCues)
    {
        if (!useMnemonic)
        {
            return TextFormatFlags.NoPrefix;
        }

        return showKeyboardCues ? TextFormatFlags.Default : TextFormatFlags.HidePrefix;
    }
}

/// <summary>
/// A button drawn as a pill (a circle when it is as wide as it is tall): the button style of the top bar. Its colours
/// come from the usual Button properties, so the theme code and extension customisations keep working: BackColor is the
/// pill, ForeColor the text, FlatAppearance.BorderColor an optional ring (drawn when BorderSize is above 0) and Image an
/// icon (ui.svg). An extension's ui.shape gives the button a Region, which then replaces the pill outline.
/// </summary>
internal class PillButton : Button
{
    private bool _hovered;
    private bool _pressed;

    public PillButton()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        UseVisualStyleBackColor = false;

        // The pill is drawn by the first Paint handler, on top of the standard flat button, so Paint handlers added
        // later (such as the outline of an extension's ui.shape) still draw on top of the pill.
        Paint += OnPillPaint;
    }

    /// <summary>The corner radius in pixels; 0 or less draws a full pill.</summary>
    [DefaultValue(0)]
    public int CornerRadius { get; set; }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hovered = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hovered = false;
        _pressed = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs mevent)
    {
        if (mevent.Button == MouseButtons.Left)
        {
            _pressed = true;
            Invalidate();
        }

        base.OnMouseDown(mevent);
    }

    protected override void OnMouseUp(MouseEventArgs mevent)
    {
        _pressed = false;
        Invalidate();
        base.OnMouseUp(mevent);
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        _hovered = false;
        _pressed = false;
        base.OnEnabledChanged(e);
        Invalidate();
    }

    private void OnPillPaint(object? sender, PaintEventArgs e)
    {
        Rectangle bounds = ClientRectangle;
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        Graphics graphics = e.Graphics;
        // An extension's ui.shape sets a Region: the control is already cut to that outline, so it is filled whole.
        bool shapedByExtension = Region is not null;
        if (!shapedByExtension)
        {
            RoundedControlPainting.PaintParentBackground(this, e, InvokePaintBackground, InvokePaint);
        }

        Color fill = BackColor.A == 0 ? (Parent?.BackColor ?? SystemColors.Control) : BackColor;
        if (Enabled && _pressed && _hovered)
        {
            fill = FlatAppearance.MouseDownBackColor.IsEmpty ? RoundedControlPainting.Highlight(fill, pressed: true) : FlatAppearance.MouseDownBackColor;
        }
        else if (Enabled && _hovered)
        {
            fill = FlatAppearance.MouseOverBackColor.IsEmpty ? RoundedControlPainting.Highlight(fill, pressed: false) : FlatAppearance.MouseOverBackColor;
        }

        SmoothingMode previousSmoothing = graphics.SmoothingMode;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        try
        {
            if (shapedByExtension)
            {
                using var brush = new SolidBrush(fill);
                graphics.FillRectangle(brush, bounds);
            }
            else
            {
                using GraphicsPath path = RoundedControlPainting.CreatePillPath(bounds, CornerRadius);
                using (var brush = new SolidBrush(fill))
                {
                    graphics.FillPath(brush, path);
                }

                int borderSize = FlatAppearance.BorderSize;
                Color borderColor = FlatAppearance.BorderColor;
                if (borderSize > 0 && !borderColor.IsEmpty && borderColor.A > 0)
                {
                    float inset = borderSize / 2f;
                    var ringBounds = RectangleF.Inflate(bounds, -inset, -inset);
                    using GraphicsPath ring = RoundedControlPainting.CreatePillPath(ringBounds, CornerRadius > 0 ? Math.Max(1, CornerRadius - inset) : 0);
                    using var pen = new Pen(borderColor, borderSize);
                    graphics.DrawPath(pen, ring);
                }
            }

            if (Focused && ShowFocusCues)
            {
                var focusBounds = RectangleF.Inflate(bounds, -3, -3);
                if (focusBounds.Width > 4 && focusBounds.Height > 4)
                {
                    using GraphicsPath focusPath = RoundedControlPainting.CreatePillPath(focusBounds, CornerRadius > 0 ? Math.Max(1, CornerRadius - 3) : 0);
                    using var focusPen = new Pen(Color.FromArgb(160, ForeColor)) { DashStyle = DashStyle.Dot };
                    graphics.DrawPath(focusPen, focusPath);
                }
            }
        }
        finally
        {
            graphics.SmoothingMode = previousSmoothing;
        }

        DrawContent(graphics, bounds, fill);
    }

    private void DrawContent(Graphics graphics, Rectangle bounds, Color fill)
    {
        Color textColor = Enabled ? ForeColor : RoundedControlPainting.Blend(ForeColor, fill, 0.55);
        Rectangle content = new(
            bounds.X + Padding.Left,
            bounds.Y + Padding.Top,
            bounds.Width - Padding.Horizontal,
            bounds.Height - Padding.Vertical);
        if (content.Width <= 4 || content.Height <= 4)
        {
            content = bounds;
        }

        TextFormatFlags flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
                                TextFormatFlags.EndEllipsis | RoundedControlPainting.MnemonicFlags(UseMnemonic, ShowKeyboardCues);
        Image? image = Image;
        string text = Text;
        if (image is null)
        {
            if (text.Length > 0)
            {
                TextRenderer.DrawText(graphics, text, Font, content, textColor, flags);
            }

            return;
        }

        Size imageSize = image.Size;
        if (text.Length == 0)
        {
            DrawImage(graphics, image, new Rectangle(
                bounds.X + ((bounds.Width - imageSize.Width) / 2),
                bounds.Y + ((bounds.Height - imageSize.Height) / 2),
                imageSize.Width,
                imageSize.Height));
            return;
        }

        int gap = Math.Max(2, imageSize.Width / 4);
        Size textSize = TextRenderer.MeasureText(graphics, text, Font, new Size(int.MaxValue, content.Height), flags);
        int total = Math.Min(content.Width, imageSize.Width + gap + textSize.Width);
        int left = content.X + Math.Max(0, (content.Width - total) / 2);
        DrawImage(graphics, image, new Rectangle(left, bounds.Y + ((bounds.Height - imageSize.Height) / 2), imageSize.Width, imageSize.Height));
        var textBounds = new Rectangle(left + imageSize.Width + gap, content.Y, Math.Max(1, content.Right - (left + imageSize.Width + gap)), content.Height);
        TextRenderer.DrawText(graphics, text, Font, textBounds, textColor, (flags & ~TextFormatFlags.HorizontalCenter) | TextFormatFlags.Left);
    }

    private void DrawImage(Graphics graphics, Image image, Rectangle target)
    {
        if (Enabled)
        {
            graphics.DrawImage(image, target);
        }
        else
        {
            ControlPaint.DrawImageDisabled(graphics, image, target.X, target.Y, Color.Transparent);
        }
    }
}

/// <summary>A panel drawn as a rounded shape filled with its BackColor (tabs, the address box); child controls inherit that colour.</summary>
internal class PillPanel : Panel
{
    public PillPanel()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
    }

    /// <summary>The corner radius in pixels; 0 or less draws a full pill.</summary>
    [DefaultValue(0)]
    public int CornerRadius { get; set; }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        RoundedControlPainting.PaintParentBackground(this, e, InvokePaintBackground, InvokePaint);
        Color fill = BackColor;
        if (fill.A == 0 || ClientSize.Width <= 0 || ClientSize.Height <= 0)
        {
            return;
        }

        Graphics graphics = e.Graphics;
        SmoothingMode previous = graphics.SmoothingMode;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        try
        {
            using GraphicsPath path = RoundedControlPainting.CreatePillPath(ClientRectangle, CornerRadius);
            using var brush = new SolidBrush(fill);
            graphics.FillPath(brush, path);
        }
        finally
        {
            graphics.SmoothingMode = previous;
        }
    }
}

/// <summary>
/// The protection banner of Web View: a green or red card beside the address bar with the protection status and an
/// ON/OFF switch. (The Logs view and extension pages show a <see cref="ProtectionStatusBar"/> instead.) It is drawn here
/// instead of being built from standard controls, and it is deliberately not something extensions can customise (it is
/// not a ui.element target): an extension must never be able to make PhantomHaze look protected when it is not.
/// </summary>
internal sealed class ProtectionBanner : Control
{
    private static readonly Color LightEnabledCard = Color.FromArgb(0, 191, 99);
    private static readonly Color LightDisabledCard = Color.FromArgb(204, 0, 0);
    private static readonly Color LightDisabledTrack = Color.FromArgb(255, 51, 51);
    private static readonly Color DarkEnabledCard = Color.FromArgb(22, 134, 77);
    private static readonly Color DarkDisabledCard = Color.FromArgb(158, 30, 30);
    private static readonly Color DarkDisabledTrack = Color.FromArgb(214, 58, 58);
    private static readonly Color TrackBorder = Color.FromArgb(200, 208, 218);
    private static readonly Color Knob = Color.FromArgb(166, 166, 166);

    private readonly Dictionary<(float Size, FontStyle Style), Font> _fonts = new();
    private Font? _fontsFor;
    private string _title = string.Empty;
    private string _subtitle = string.Empty;
    private string? _countdown;
    private bool _protectionOn;
    private bool _darkStyle;
    private bool _hovered;
    private bool _pressed;
    private Rectangle _toggleBounds;

    public ProtectionBanner()
    {
        SetStyle(
            ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw | ControlStyles.Selectable,
            true);
        TabStop = true;
        Cursor = Cursors.Hand;
        AccessibleRole = AccessibleRole.PushButton;
    }

    /// <summary>Raised when the card is clicked or Space/Enter is pressed on it.</summary>
    public event EventHandler? ToggleRequested;

    /// <summary>Where the switch is drawn, in client coordinates (the protection menu opens below it).</summary>
    public Rectangle ToggleBounds
    {
        get
        {
            UpdateToggleBounds();
            return _toggleBounds;
        }
    }

    /// <summary>Darker greens and reds with white text, for dark themes.</summary>
    [DefaultValue(false)]
    public bool DarkStyle
    {
        get => _darkStyle;
        set
        {
            if (_darkStyle != value)
            {
                _darkStyle = value;
                Invalidate();
            }
        }
    }

    public bool ProtectionOn => _protectionOn;

    public void SetStatus(bool protectionOn, string title, string subtitle, string? countdown)
    {
        if (_protectionOn == protectionOn &&
            string.Equals(_title, title, StringComparison.Ordinal) &&
            string.Equals(_subtitle, subtitle, StringComparison.Ordinal) &&
            string.Equals(_countdown, countdown, StringComparison.Ordinal))
        {
            return;
        }

        _protectionOn = protectionOn;
        _title = title;
        _subtitle = subtitle;
        _countdown = countdown;
        AccessibleName = countdown is null ? $"{title}. {subtitle}" : $"{title}. {subtitle} {countdown}";
        AccessibleDescription = protectionOn
            ? "Protection switch: on. Press to choose how long to turn protection off."
            : "Protection switch: off. Press to turn protection back on.";
        Invalidate();
    }

    private int Scale(int logicalPixels)
    {
        int dpi = DeviceDpi > 0 ? DeviceDpi : 96;
        return Math.Max(1, (int)Math.Round(logicalPixels * (dpi / 96.0)));
    }

    private Font GetFont(float size, FontStyle style)
    {
        if (!ReferenceEquals(_fontsFor, Font))
        {
            ClearFonts();
            _fontsFor = Font;
        }

        if (!_fonts.TryGetValue((size, style), out Font? font))
        {
            FontFamily family = Font.FontFamily;
            FontStyle available = family.IsStyleAvailable(style) ? style : FontStyle.Regular;
            font = new Font(family, size, available, GraphicsUnit.Point);
            _fonts[(size, style)] = font;
        }

        return font;
    }

    private void ClearFonts()
    {
        foreach (Font font in _fonts.Values)
        {
            font.Dispose();
        }

        _fonts.Clear();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ClearFonts();
        }

        base.Dispose(disposing);
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        Invalidate();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        Invalidate();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        UpdateToggleBounds();
    }

    private void UpdateToggleBounds()
    {
        Rectangle client = ClientRectangle;
        if (client.Width <= 0 || client.Height <= 0)
        {
            _toggleBounds = Rectangle.Empty;
            return;
        }

        int padding = Scale(10);
        int toggleHeight = Math.Min(Scale(32), Math.Max(Scale(18), (client.Height - (padding * 2)) / 3));
        _toggleBounds = new Rectangle(client.X + padding, client.Bottom - padding - toggleHeight, Math.Max(1, client.Width - (padding * 2)), toggleHeight);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        RoundedControlPainting.PaintParentBackground(this, e, InvokePaintBackground, InvokePaint);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Rectangle client = ClientRectangle;
        if (client.Width <= 4 || client.Height <= 4)
        {
            return;
        }

        UpdateToggleBounds();
        Graphics graphics = e.Graphics;
        Color card = _protectionOn
            ? (_darkStyle ? DarkEnabledCard : LightEnabledCard)
            : (_darkStyle ? DarkDisabledCard : LightDisabledCard);
        Color text = _darkStyle ? Color.White : Color.Black;

        SmoothingMode previous = graphics.SmoothingMode;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        try
        {
            using (GraphicsPath cardPath = RoundedControlPainting.CreatePillPath(client, Scale(18)))
            using (var cardBrush = new SolidBrush(card))
            {
                graphics.FillPath(cardBrush, cardPath);
            }

            DrawToggle(graphics, card);
        }
        finally
        {
            graphics.SmoothingMode = previous;
        }

        DrawCompactText(graphics, client, text);
    }

    private void DrawCompactText(Graphics graphics, Rectangle client, Color text)
    {
        int padding = Scale(10);
        int gap = Scale(4);
        var area = new Rectangle(client.X + padding, client.Y + padding, client.Width - (padding * 2), _toggleBounds.Top - gap - (client.Y + padding));
        if (area.Width <= 0 || area.Height <= 0)
        {
            return;
        }

        const TextFormatFlags wrap = TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl;
        const TextFormatFlags line = TextFormatFlags.HorizontalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis;

        // The largest text that fits: the countdown first moves onto the subtitle line, then everything gets smaller.
        foreach (float scale in new[] { 1f, 0.9f, 0.8f, 0.7f })
        {
            foreach (bool countdownOnOwnLine in _countdown is null ? new[] { false } : new[] { true, false })
            {
                Font titleFont = GetFont(10.5f * scale, FontStyle.Bold);
                Font subtitleFont = GetFont(8.25f * scale, FontStyle.Regular);
                Font countdownFont = GetFont(12f * scale, FontStyle.Bold);
                string subtitle = _countdown is not null && !countdownOnOwnLine ? $"{_subtitle} {_countdown}" : _subtitle;

                Size titleSize = TextRenderer.MeasureText(graphics, _title, titleFont, new Size(area.Width, int.MaxValue), wrap);
                Size subtitleSize = subtitle.Length == 0 ? Size.Empty : TextRenderer.MeasureText(graphics, subtitle, subtitleFont, new Size(area.Width, int.MaxValue), wrap);
                Size countdownSize = countdownOnOwnLine ? TextRenderer.MeasureText(graphics, _countdown!, countdownFont, new Size(area.Width, int.MaxValue), line) : Size.Empty;
                int total = titleSize.Height + (subtitleSize.Height > 0 ? gap + subtitleSize.Height : 0) + (countdownOnOwnLine ? gap + countdownSize.Height : 0);
                bool last = scale <= 0.7f && !countdownOnOwnLine;
                if (total > area.Height && !last)
                {
                    continue;
                }

                int y = area.Y + Math.Max(0, (area.Height - total) / 2);
                TextRenderer.DrawText(graphics, _title, titleFont, new Rectangle(area.X, y, area.Width, titleSize.Height), text, wrap);
                y += titleSize.Height + gap;
                if (subtitleSize.Height > 0)
                {
                    int height = Math.Min(subtitleSize.Height, Math.Max(0, area.Bottom - y));
                    TextRenderer.DrawText(graphics, subtitle, subtitleFont, new Rectangle(area.X, y, area.Width, height), text, wrap | TextFormatFlags.EndEllipsis);
                    y += subtitleSize.Height + gap;
                }

                if (countdownOnOwnLine && y < area.Bottom)
                {
                    TextRenderer.DrawText(graphics, _countdown!, countdownFont, new Rectangle(area.X, y, area.Width, countdownSize.Height), text, line);
                }

                return;
            }
        }
    }

    private void DrawToggle(Graphics graphics, Color card)
    {
        Rectangle track = _toggleBounds;
        if (track.Width <= 8 || track.Height <= 8)
        {
            return;
        }

        int border = Math.Max(2, Scale(3));
        float halfBorder = border / 2f;
        var trackBounds = RectangleF.Inflate(track, -halfBorder, -halfBorder);
        Color trackFill = _protectionOn ? card : (_darkStyle ? DarkDisabledTrack : LightDisabledTrack);
        using (GraphicsPath trackPath = RoundedControlPainting.CreatePillPath(trackBounds, 0))
        {
            using (var fill = new SolidBrush(trackFill))
            {
                graphics.FillPath(fill, trackPath);
            }

            using var pen = new Pen(TrackBorder, border);
            graphics.DrawPath(pen, trackPath);
        }

        int inset = border + Scale(2);
        var inner = Rectangle.Inflate(track, -inset, -inset);
        if (inner.Width <= 4 || inner.Height <= 4)
        {
            return;
        }

        int knobWidth = Math.Max(inner.Height, (int)Math.Round(inner.Width * 0.6));
        var knob = new Rectangle(_protectionOn ? inner.Right - knobWidth : inner.X, inner.Y, knobWidth, inner.Height);
        Color knobColor = Enabled && (_hovered || Focused)
            ? RoundedControlPainting.Highlight(Knob, _pressed)
            : Knob;
        using (GraphicsPath knobPath = RoundedControlPainting.CreatePillPath(knob, 0))
        using (var knobBrush = new SolidBrush(knobColor))
        {
            graphics.FillPath(knobBrush, knobPath);
        }

        if (Focused && ShowFocusCues)
        {
            using GraphicsPath focusPath = RoundedControlPainting.CreatePillPath(RectangleF.Inflate(track, 2, 2), 0);
            using var focusPen = new Pen(_darkStyle ? Color.White : Color.Black) { DashStyle = DashStyle.Dot };
            graphics.DrawPath(focusPen, focusPath);
        }

        // "ON" with a small arrow (it opens the menu of how long to turn protection off for), or "OFF".
        Font knobFont = GetFont(10f, FontStyle.Bold);
        string label = _protectionOn ? "ON" : "OFF";
        const TextFormatFlags flags = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter | TextFormatFlags.Left;
        Size labelSize = TextRenderer.MeasureText(graphics, label, knobFont, new Size(int.MaxValue, knob.Height), flags);
        int arrowWidth = _protectionOn ? Math.Max(6, (int)Math.Round(labelSize.Height * 0.55)) : 0;
        int arrowGap = _protectionOn ? Math.Max(2, Scale(3)) : 0;
        int contentWidth = labelSize.Width + arrowGap + arrowWidth;
        int left = knob.X + Math.Max(0, (knob.Width - contentWidth) / 2);
        TextRenderer.DrawText(graphics, label, knobFont, new Rectangle(left, knob.Y, Math.Max(1, knob.Right - left), knob.Height), Color.Black, flags);
        if (_protectionOn)
        {
            float arrowLeft = left + labelSize.Width + arrowGap;
            float arrowHeight = arrowWidth * 0.75f;
            float arrowTop = knob.Y + ((knob.Height - arrowHeight) / 2f);
            using var arrowBrush = new SolidBrush(Color.Black);
            graphics.FillPolygon(arrowBrush, new[]
            {
                new PointF(arrowLeft, arrowTop),
                new PointF(arrowLeft + arrowWidth, arrowTop),
                new PointF(arrowLeft + (arrowWidth / 2f), arrowTop + arrowHeight),
            });
        }
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hovered = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hovered = false;
        _pressed = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            _pressed = true;
            if (CanFocus)
            {
                Focus();
            }

            Invalidate();
        }

        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        bool wasPressed = _pressed;
        _pressed = false;
        Invalidate();
        base.OnMouseUp(e);
        if (wasPressed && e.Button == MouseButtons.Left && ClientRectangle.Contains(e.Location))
        {
            ToggleRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override bool IsInputKey(Keys keyData)
    {
        return keyData is Keys.Space or Keys.Enter || base.IsInputKey(keyData);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.Handled && e.Modifiers == Keys.None && e.KeyCode is Keys.Space or Keys.Enter)
        {
            e.Handled = true;
            ToggleRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        Invalidate();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        Invalidate();
    }

    /// <summary>The height the banner asks for, in logical pixels (smaller in the top bar's tighter styles).</summary>
    [DefaultValue(132)]
    public int PreferredHeightLogical { get; set; } = 132;

    // The banner fills its cell of the top bar, so this only sets its smallest useful size. (Returning the proposed width
    // would be unsafe: layouts ask with int.MaxValue when the width is unconstrained.)
    public override Size GetPreferredSize(Size proposedSize) => new(Scale(Math.Min(204, PreferredHeightLogical * 2)), Scale(PreferredHeightLogical));
}

/// <summary>
/// The protection status of the Logs view and extension pages: a rounded green or red banner across the window, under
/// the top bar, with one line of text (the protection button is below it, as in earlier versions). Like
/// <see cref="ProtectionBanner"/> it is drawn here and is deliberately not something extensions can customise.
/// </summary>
internal sealed class ProtectionStatusBar : Control
{
    // The colours of the status banner of earlier versions.
    private static readonly Color LightEnabled = Color.LightGreen;
    private static readonly Color LightDisabled = Color.LightSalmon;
    private static readonly Color DarkEnabled = Color.FromArgb(58, 123, 85);
    private static readonly Color DarkDisabled = Color.FromArgb(133, 76, 73);

    private bool _protectionOn;
    private bool _darkStyle;

    public ProtectionStatusBar()
    {
        SetStyle(
            ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw,
            true);
        SetStyle(ControlStyles.Selectable, false);
        TabStop = false;
        AccessibleRole = AccessibleRole.StaticText;
    }

    /// <summary>Darker greens and reds with white text, for dark themes.</summary>
    [DefaultValue(false)]
    public bool DarkStyle
    {
        get => _darkStyle;
        set
        {
            if (_darkStyle != value)
            {
                _darkStyle = value;
                Invalidate();
            }
        }
    }

    public void SetStatus(bool protectionOn, string text)
    {
        if (_protectionOn == protectionOn && string.Equals(Text, text, StringComparison.Ordinal))
        {
            return;
        }

        _protectionOn = protectionOn;
        Text = text;
        AccessibleName = text;
        Invalidate();
    }

    private int Scale(int logicalPixels)
    {
        int dpi = DeviceDpi > 0 ? DeviceDpi : 96;
        return Math.Max(1, (int)Math.Round(logicalPixels * (dpi / 96.0)));
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        Invalidate();
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        Invalidate();
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        RoundedControlPainting.PaintParentBackground(this, e, InvokePaintBackground, InvokePaint);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Rectangle client = ClientRectangle;
        if (client.Width <= 4 || client.Height <= 4)
        {
            return;
        }

        Color fill = _protectionOn
            ? (_darkStyle ? DarkEnabled : LightEnabled)
            : (_darkStyle ? DarkDisabled : LightDisabled);
        Color text = _darkStyle ? Color.White : Color.Black;
        Graphics graphics = e.Graphics;
        SmoothingMode previous = graphics.SmoothingMode;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        try
        {
            using GraphicsPath path = RoundedControlPainting.CreatePillPath(client, Scale(12));
            using (var brush = new SolidBrush(fill))
            {
                graphics.FillPath(brush, path);
            }

            using var pen = new Pen(RoundedControlPainting.Blend(fill, Color.Black, 0.22));
            graphics.DrawPath(pen, path);
        }
        finally
        {
            graphics.SmoothingMode = previous;
        }

        int paddingX = Scale(14);
        var textBounds = new Rectangle(client.X + paddingX, client.Y, Math.Max(1, client.Width - (paddingX * 2)), client.Height);
        TextRenderer.DrawText(
            graphics,
            Text,
            Font,
            textBounds,
            text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }
}

/// <summary>
/// The protection switch of the Simple top bar: a small ON/OFF switch, green or red, next to the Settings button. While
/// protection is on, clicking it opens the menu of how long to turn protection off for (its arrow says so); while it is
/// off, clicking turns protection back on. Like <see cref="ProtectionBanner"/> it is drawn here and is deliberately not
/// something extensions can customise.
/// </summary>
internal sealed class ProtectionSwitch : Control
{
    private static readonly Color LightOn = Color.FromArgb(0, 191, 99);
    private static readonly Color LightOff = Color.FromArgb(204, 0, 0);
    private static readonly Color DarkOn = Color.FromArgb(22, 134, 77);
    private static readonly Color DarkOff = Color.FromArgb(158, 30, 30);
    private static readonly Color KnobColor = Color.FromArgb(245, 245, 245);

    private Font? _labelFont;
    private Font? _labelFontFor;
    private bool _protectionOn = true;
    private bool _darkStyle;
    private bool _hovered;
    private bool _pressed;

    public ProtectionSwitch()
    {
        SetStyle(
            ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.SupportsTransparentBackColor,
            true);
        TabStop = true;
        Cursor = Cursors.Hand;
        AccessibleRole = AccessibleRole.ButtonDropDown;
        AccessibleName = "Protection: on";
    }

    /// <summary>Raised when the switch is clicked or Space/Enter is pressed on it.</summary>
    public event EventHandler? ToggleRequested;

    /// <summary>Darker greens and reds, for dark themes.</summary>
    [DefaultValue(false)]
    public bool DarkStyle
    {
        get => _darkStyle;
        set
        {
            if (_darkStyle != value)
            {
                _darkStyle = value;
                Invalidate();
            }
        }
    }

    public void SetStatus(bool protectionOn, string accessibleName)
    {
        if (_protectionOn == protectionOn && string.Equals(AccessibleName, accessibleName, StringComparison.Ordinal))
        {
            return;
        }

        _protectionOn = protectionOn;
        AccessibleName = accessibleName;
        AccessibleRole = protectionOn ? AccessibleRole.ButtonDropDown : AccessibleRole.PushButton;
        AccessibleDescription = protectionOn
            ? "Protection switch: on. Press to choose how long to turn protection off."
            : "Protection switch: off. Press to turn protection back on.";
        Invalidate();
    }

    private int Scale(int logicalPixels)
    {
        int dpi = DeviceDpi > 0 ? DeviceDpi : 96;
        return Math.Max(1, (int)Math.Round(logicalPixels * (dpi / 96.0)));
    }

    private Font LabelFont
    {
        get
        {
            if (_labelFont is null || !ReferenceEquals(_labelFontFor, Font))
            {
                _labelFont?.Dispose();
                FontFamily family = Font.FontFamily;
                _labelFont = new Font(family, 8.5f, family.IsStyleAvailable(FontStyle.Bold) ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Point);
                _labelFontFor = Font;
            }

            return _labelFont;
        }
    }

    public override Size GetPreferredSize(Size proposedSize) => new(Scale(74), Scale(28));

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        RoundedControlPainting.PaintParentBackground(this, e, InvokePaintBackground, InvokePaint);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Rectangle client = ClientRectangle;
        if (client.Width <= 8 || client.Height <= 8)
        {
            return;
        }

        Graphics graphics = e.Graphics;
        Color track = _protectionOn ? (_darkStyle ? DarkOn : LightOn) : (_darkStyle ? DarkOff : LightOff);
        if (Enabled && _hovered)
        {
            track = RoundedControlPainting.Highlight(track, _pressed);
        }

        SmoothingMode previous = graphics.SmoothingMode;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        Rectangle knob;
        try
        {
            using (GraphicsPath trackPath = RoundedControlPainting.CreatePillPath(client, 0))
            using (var trackBrush = new SolidBrush(track))
            {
                graphics.FillPath(trackBrush, trackPath);
            }

            int inset = Math.Max(2, Scale(3));
            int knobSize = Math.Max(4, client.Height - (inset * 2));
            knob = new Rectangle(_protectionOn ? client.Right - inset - knobSize : client.X + inset, client.Y + inset, knobSize, knobSize);
            using (var knobBrush = new SolidBrush(KnobColor))
            {
                graphics.FillEllipse(knobBrush, knob);
            }

            if (Focused && ShowFocusCues)
            {
                using GraphicsPath focusPath = RoundedControlPainting.CreatePillPath(RectangleF.Inflate(client, -1.5f, -1.5f), 0);
                using var focusPen = new Pen(Color.White) { DashStyle = DashStyle.Dot };
                graphics.DrawPath(focusPen, focusPath);
            }
        }
        finally
        {
            graphics.SmoothingMode = previous;
        }

        // "ON" with a small arrow (it opens the menu of how long to turn protection off for) left of the knob, or "OFF"
        // right of it.
        string label = _protectionOn ? "ON" : "OFF";
        const TextFormatFlags flags = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter | TextFormatFlags.Left;
        Size labelSize = TextRenderer.MeasureText(graphics, label, LabelFont, new Size(int.MaxValue, client.Height), flags);
        int arrowWidth = _protectionOn ? Math.Max(5, (int)Math.Round(labelSize.Height * 0.5)) : 0;
        int arrowGap = _protectionOn ? Math.Max(2, Scale(3)) : 0;
        int contentWidth = labelSize.Width + arrowGap + arrowWidth;
        Rectangle area = _protectionOn
            ? new Rectangle(client.X + Scale(6), client.Y, Math.Max(1, knob.Left - client.X - Scale(8)), client.Height)
            : new Rectangle(knob.Right + Scale(2), client.Y, Math.Max(1, client.Right - knob.Right - Scale(8)), client.Height);
        int left = area.X + Math.Max(0, (area.Width - contentWidth) / 2);
        TextRenderer.DrawText(graphics, label, LabelFont, new Rectangle(left, area.Y, Math.Max(1, area.Right - left), area.Height), Color.White, flags);
        if (_protectionOn)
        {
            float arrowLeft = left + labelSize.Width + arrowGap;
            float arrowHeight = arrowWidth * 0.6f;
            float arrowTop = client.Y + ((client.Height - arrowHeight) / 2f);
            using var arrowBrush = new SolidBrush(Color.White);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.FillPolygon(arrowBrush, new[]
            {
                new PointF(arrowLeft, arrowTop),
                new PointF(arrowLeft + arrowWidth, arrowTop),
                new PointF(arrowLeft + (arrowWidth / 2f), arrowTop + arrowHeight),
            });
            graphics.SmoothingMode = previous;
        }
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hovered = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hovered = false;
        _pressed = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            _pressed = true;
            Invalidate();
        }

        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        bool wasPressed = _pressed;
        _pressed = false;
        Invalidate();
        base.OnMouseUp(e);
        if (wasPressed && e.Button == MouseButtons.Left && ClientRectangle.Contains(e.Location))
        {
            ToggleRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override bool IsInputKey(Keys keyData)
    {
        return keyData is Keys.Space or Keys.Enter || base.IsInputKey(keyData);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.Handled && e.Modifiers == Keys.None && e.KeyCode is Keys.Space or Keys.Enter)
        {
            e.Handled = true;
            ToggleRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        Invalidate();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        Invalidate();
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _labelFont?.Dispose();
            _labelFont = null;
        }

        base.Dispose(disposing);
    }
}

/// <summary>
/// The drop-down button of the Compact top bar: an arrow that shows or hides the rest of the top bar, with a dot that
/// always shows whether protection is on (green) or off (red), even while the protection controls are hidden.
/// </summary>
internal sealed class TopBarRevealButton : PillButton
{
    private Color _dotColor = Color.FromArgb(0, 191, 99);

    public TopBarRevealButton()
    {
        // Drawn after the pill and its arrow (PillButton paints in the first Paint handler).
        Paint += OnDotPaint;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color DotColor
    {
        get => _dotColor;
        set
        {
            if (_dotColor != value)
            {
                _dotColor = value;
                Invalidate();
            }
        }
    }

    private int Scale(int logicalPixels)
    {
        int dpi = DeviceDpi > 0 ? DeviceDpi : 96;
        return Math.Max(1, (int)Math.Round(logicalPixels * (dpi / 96.0)));
    }

    private void OnDotPaint(object? sender, PaintEventArgs e)
    {
        int size = Scale(9);
        int left = Math.Max(Scale(6), (Padding.Left - size) / 2);
        var dot = new Rectangle(left, (ClientSize.Height - size) / 2, size, size);
        SmoothingMode previous = e.Graphics.SmoothingMode;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        try
        {
            using var brush = new SolidBrush(_dotColor);
            e.Graphics.FillEllipse(brush, dot);
            using var ring = new Pen(Color.FromArgb(90, Color.Black), 1);
            e.Graphics.DrawEllipse(ring, dot);
        }
        finally
        {
            e.Graphics.SmoothingMode = previous;
        }
    }
}
