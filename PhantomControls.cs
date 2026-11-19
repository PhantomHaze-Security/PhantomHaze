using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Text;

namespace PhantomHaze;

// Owner-drawn, anti-aliased controls for the v1.3.6 top bar and tab strip: rounded buttons (pills, circles and
// segments), rounded panels (the address box, the segmented view switcher, the zoom group and the compact protection
// card) and Chrome-style tab headers. Every corner outside the shape shows the parent's background, so gradient
// themes keep painting one continuous gradient behind them.

/// <summary>Which corners of a rounded shape are rounded (the others stay square).</summary>
[Flags]
internal enum RoundedCorners
{
    None = 0,
    TopLeft = 1,
    TopRight = 2,
    BottomLeft = 4,
    BottomRight = 8,
    Left = TopLeft | BottomLeft,
    Right = TopRight | BottomRight,
    Top = TopLeft | TopRight,
    All = TopLeft | TopRight | BottomLeft | BottomRight,
}

internal static class RoundedGeometry
{
    /// <summary>
    /// A rectangle with the given corners rounded. A negative <paramref name="radius"/> rounds them fully (half the
    /// shorter side), which makes a pill - or a circle when the rectangle is square.
    /// </summary>
    public static GraphicsPath Create(RectangleF bounds, float radius, RoundedCorners corners)
    {
        var path = new GraphicsPath();
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return path;
        }

        float maximum = Math.Min(bounds.Width, bounds.Height) / 2f;
        float r = radius < 0 ? maximum : Math.Min(radius, maximum);
        if (r <= 0.5f || corners == RoundedCorners.None)
        {
            path.AddRectangle(bounds);
            return path;
        }

        float d = r * 2f;
        if ((corners & RoundedCorners.TopLeft) != 0)
        {
            path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
        }
        else
        {
            path.AddLine(bounds.Left, bounds.Top, bounds.Left, bounds.Top);
        }

        if ((corners & RoundedCorners.TopRight) != 0)
        {
            path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
        }
        else
        {
            path.AddLine(bounds.Right, bounds.Top, bounds.Right, bounds.Top);
        }

        if ((corners & RoundedCorners.BottomRight) != 0)
        {
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        }
        else
        {
            path.AddLine(bounds.Right, bounds.Bottom, bounds.Right, bounds.Bottom);
        }

        if ((corners & RoundedCorners.BottomLeft) != 0)
        {
            path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
        }
        else
        {
            path.AddLine(bounds.Left, bounds.Bottom, bounds.Left, bounds.Bottom);
        }

        path.CloseFigure();
        return path;
    }

    public static Color Blend(Color from, Color to, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        int Mix(int a, int b) => (int)Math.Round(a + ((b - a) * amount));
        return Color.FromArgb(Mix(from.A, to.A), Mix(from.R, to.R), Mix(from.G, to.G), Mix(from.B, to.B));
    }
}

/// <summary>
/// A button drawn as a pill, a circle (when square) or one segment of a segmented control. BackColor is the fill
/// (Color.Transparent for none); FlatAppearance.BorderSize/BorderColor draw the outline. Hover and press darken or
/// lighten the fill towards the text colour.
/// </summary>
internal sealed class PillButton : Button
{
    private RoundedCorners _corners = RoundedCorners.All;
    private float _cornerRadius = -1f;
    private bool _suppressOwnShape;
    private bool _hovered;
    private bool _pressed;

    public PillButton()
    {
        SetStyle(
            ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor,
            true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        UseMnemonic = false;
        Cursor = Cursors.Hand;
    }

    /// <summary>The corners that are rounded (segments of a segmented control round only their outer corners).</summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public RoundedCorners Corners
    {
        get => _corners;
        set
        {
            if (_corners != value)
            {
                _corners = value;
                Invalidate();
            }
        }
    }

    /// <summary>The corner radius in pixels; negative for fully rounded (pill or circle).</summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public float CornerRadius
    {
        get => _cornerRadius;
        set
        {
            if (_cornerRadius != value)
            {
                _cornerRadius = value;
                Invalidate();
            }
        }
    }

    /// <summary>
    /// Set while an extension gives this button its own outline (ui.shape): the control is then clipped to that shape,
    /// so the fill is drawn as a plain rectangle and the extension's outline is the only one visible.
    /// </summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool SuppressOwnShape
    {
        get => _suppressOwnShape;
        set
        {
            if (_suppressOwnShape != value)
            {
                _suppressOwnShape = value;
                Invalidate();
            }
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
        if (!Enabled)
        {
            _hovered = false;
            _pressed = false;
        }

        Invalidate();
        base.OnEnabledChanged(e);
    }

    protected override void OnPaintBackground(PaintEventArgs pevent)
    {
        PaintParentBackground(pevent);
    }

    protected override void OnPaint(PaintEventArgs pevent)
    {
        Graphics graphics = pevent.Graphics;
        if (Width <= 0 || Height <= 0)
        {
            return;
        }

        Color text = Enabled ? ForeColor : RoundedGeometry.Blend(ForeColor, Color.FromArgb(ForeColor.A, 128, 128, 128), 0.55);
        Color fill = BackColor;
        if (Enabled && (_pressed || _hovered))
        {
            double amount = _pressed ? 0.22 : 0.12;
            fill = fill.A == 0
                ? Color.FromArgb((int)Math.Round(255 * amount * 1.6), ForeColor)
                : RoundedGeometry.Blend(fill, Color.FromArgb(fill.A, ForeColor), amount);
        }

        int borderSize = FlatAppearance.BorderSize;
        Color border = FlatAppearance.BorderColor;
        bool drawBorder = borderSize > 0 && !border.IsEmpty && border.A > 0 && !_suppressOwnShape;

        SmoothingMode previousSmoothing = graphics.SmoothingMode;
        PixelOffsetMode previousOffset = graphics.PixelOffsetMode;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        try
        {
            float inset = drawBorder ? borderSize / 2f : 0f;
            var bounds = new RectangleF(inset, inset, Width - (inset * 2f), Height - (inset * 2f));
            using GraphicsPath path = _suppressOwnShape
                ? RoundedGeometry.Create(new RectangleF(0, 0, Width, Height), 0, RoundedCorners.None)
                : RoundedGeometry.Create(bounds, _cornerRadius, _corners);

            if (fill.A > 0)
            {
                using var brush = new SolidBrush(fill);
                graphics.FillPath(brush, path);
            }

            if (drawBorder)
            {
                using var pen = new Pen(Enabled ? border : RoundedGeometry.Blend(border, fill, 0.4), borderSize);
                graphics.DrawPath(pen, path);
            }

            if (Focused && ShowFocusCues && !_suppressOwnShape)
            {
                var focusBounds = RectangleF.Inflate(bounds, -2f, -2f);
                using GraphicsPath focusPath = RoundedGeometry.Create(focusBounds, _cornerRadius < 0 ? -1 : Math.Max(0, _cornerRadius - 2), _corners);
                using var focusPen = new Pen(Color.FromArgb(150, text), 1f) { DashStyle = DashStyle.Dot };
                graphics.DrawPath(focusPen, focusPath);
            }
        }
        finally
        {
            graphics.SmoothingMode = previousSmoothing;
            graphics.PixelOffsetMode = previousOffset;
        }

        DrawContent(graphics, text);
    }

    private void DrawContent(Graphics graphics, Color text)
    {
        Rectangle content = new(
            Padding.Left,
            Padding.Top,
            Math.Max(0, Width - Padding.Horizontal),
            Math.Max(0, Height - Padding.Vertical));
        Image? image = Image;
        string caption = Text ?? string.Empty;
        TextFormatFlags flags = TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
                                TextFormatFlags.NoPadding;
        if (!UseMnemonic)
        {
            flags |= TextFormatFlags.NoPrefix;
        }

        if (image is null)
        {
            if (caption.Length > 0)
            {
                TextRenderer.DrawText(graphics, caption, Font, content, text, flags | TextFormatFlags.HorizontalCenter);
            }

            return;
        }

        Size imageSize = image.Size;
        if (caption.Length == 0)
        {
            var imageBounds = new Rectangle(
                content.Left + ((content.Width - imageSize.Width) / 2),
                content.Top + ((content.Height - imageSize.Height) / 2),
                imageSize.Width,
                imageSize.Height);
            DrawImage(graphics, image, imageBounds);
            return;
        }

        // Image before the text, centred together.
        int gap = Math.Max(3, (int)Math.Round(Font.Height * 0.25));
        Size textSize = TextRenderer.MeasureText(graphics, caption, Font, new Size(int.MaxValue, content.Height), flags);
        int totalWidth = Math.Min(content.Width, imageSize.Width + gap + textSize.Width);
        int left = content.Left + Math.Max(0, (content.Width - totalWidth) / 2);
        DrawImage(graphics, image, new Rectangle(left, content.Top + ((content.Height - imageSize.Height) / 2), imageSize.Width, imageSize.Height));
        var textBounds = new Rectangle(left + imageSize.Width + gap, content.Top, Math.Max(0, content.Right - (left + imageSize.Width + gap)), content.Height);
        TextRenderer.DrawText(graphics, caption, Font, textBounds, text, flags | TextFormatFlags.Left);
    }

    private void DrawImage(Graphics graphics, Image image, Rectangle bounds)
    {
        if (Enabled)
        {
            graphics.DrawImage(image, bounds);
            return;
        }

        using var attributes = new System.Drawing.Imaging.ImageAttributes();
        var matrix = new System.Drawing.Imaging.ColorMatrix { Matrix33 = 0.45f };
        attributes.SetColorMatrix(matrix);
        graphics.DrawImage(image, bounds, 0, 0, image.Width, image.Height, GraphicsUnit.Pixel, attributes);
    }

    /// <summary>
    /// Paints what is behind the button (its parent's background, through any see-through parents) so the area outside
    /// the shape blends in. This is how WinForms itself paints a transparent control (Control.PaintTransparentBackground
    /// for parents other than TabPage). ButtonRenderer.DrawParentBackground must not be used: it relies on the theme API
    /// (WM_ERASEBKGND / WM_PRINTCLIENT), which WinForms panels do not answer, so the button showed leftover pixels from
    /// the shared double buffer - squares of another control's colour and fragments of other text.
    /// </summary>
    private void PaintParentBackground(PaintEventArgs e)
    {
        // Safety net first: the nearest solid colour behind the button, so nothing stale can ever show through.
        using (var fallback = new SolidBrush(GetNearestOpaqueBackColor()))
        {
            e.Graphics.FillRectangle(fallback, ClientRectangle);
        }

        Control? parent = Parent;
        if (parent is null)
        {
            return;
        }

        GraphicsState state = e.Graphics.Save();
        try
        {
            e.Graphics.TranslateTransform(-Left, -Top);
            var parentClip = new Rectangle(Left, Top, Width, Height);
            using var parentArgs = new PaintEventArgs(e.Graphics, parentClip);
            InvokePaintBackground(parent, parentArgs);
            InvokePaint(parent, parentArgs);
        }
        finally
        {
            e.Graphics.Restore(state);
        }
    }

    private Color GetNearestOpaqueBackColor()
    {
        for (Control? control = Parent; control is not null; control = control.Parent)
        {
            Color color = control.BackColor;
            if (color.A == 255)
            {
                return color;
            }
        }

        return SystemColors.Control;
    }
}

/// <summary>
/// A panel with a rounded fill and outline; outside the shape it shows its parent's background. Its own BackColor
/// stays transparent - FillColor is the colour inside the shape. With a header colour, the top part of the shape
/// (down to the bottom of HeaderControl, or HeaderFraction of the height) is filled with it instead (the compact
/// protection card).
/// </summary>
internal class RoundedPanel : Panel
{
    private Color _fillColor = Color.Transparent;
    private Color _borderColor = Color.Empty;
    private Color _headerFillColor = Color.Empty;
    private float _headerFraction = 0.5f;
    private float _cornerRadius = -1f;
    private RoundedCorners _corners = RoundedCorners.All;

    public RoundedPanel()
    {
        SetStyle(
            ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor,
            true);
        BackColor = Color.Transparent;
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color FillColor
    {
        get => _fillColor;
        set => SetAndInvalidate(ref _fillColor, value);
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color BorderColor
    {
        get => _borderColor;
        set => SetAndInvalidate(ref _borderColor, value);
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color HeaderFillColor
    {
        get => _headerFillColor;
        set => SetAndInvalidate(ref _headerFillColor, value);
    }

    /// <summary>
    /// A control inside the panel whose bottom edge ends the header colour, so the colour matches that control (and
    /// text centred in it is centred in the coloured area). Without one, HeaderFraction is used.
    /// </summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Control? HeaderControl { get; set; }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public float HeaderFraction
    {
        get => _headerFraction;
        set
        {
            _headerFraction = Math.Clamp(value, 0f, 1f);
            Invalidate();
        }
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public float CornerRadius
    {
        get => _cornerRadius;
        set
        {
            _cornerRadius = value;
            Invalidate();
        }
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public RoundedCorners Corners
    {
        get => _corners;
        set
        {
            _corners = value;
            Invalidate();
        }
    }

    /// <summary>The height of the header colour: down to HeaderControl's bottom edge, or HeaderFraction of the height.</summary>
    private float GetHeaderHeight()
    {
        if (HeaderControl is { IsDisposed: false } header && header.Visible)
        {
            int bottom = header.Bottom;
            for (Control? parent = header.Parent; parent is not null && !ReferenceEquals(parent, this); parent = parent.Parent)
            {
                bottom += parent.Top;
                if (parent.Parent is null)
                {
                    // Not inside this panel after all.
                    return Height * _headerFraction;
                }
            }

            return Math.Clamp(bottom, 0, Height);
        }

        return Height * _headerFraction;
    }

    private void SetAndInvalidate(ref Color field, Color value)
    {
        if (field != value)
        {
            field = value;
            Invalidate(true);
        }
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // BackColor is transparent, so the base class paints the parent's background first.
        base.OnPaintBackground(e);
        if (Width <= 0 || Height <= 0)
        {
            return;
        }

        Graphics graphics = e.Graphics;
        SmoothingMode previousSmoothing = graphics.SmoothingMode;
        PixelOffsetMode previousOffset = graphics.PixelOffsetMode;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        try
        {
            bool drawBorder = !_borderColor.IsEmpty && _borderColor.A > 0;
            var bounds = drawBorder ? new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f) : new RectangleF(0, 0, Width, Height);
            using GraphicsPath path = RoundedGeometry.Create(bounds, _cornerRadius, _corners);
            if (_fillColor.A > 0)
            {
                using var brush = new SolidBrush(_fillColor);
                graphics.FillPath(brush, path);
            }

            float headerHeight = GetHeaderHeight();
            if (!_headerFillColor.IsEmpty && _headerFillColor.A > 0 && headerHeight > 0)
            {
                Region previousClip = graphics.Clip;
                try
                {
                    graphics.SetClip(path, CombineMode.Intersect);
                    using var headerBrush = new SolidBrush(_headerFillColor);
                    graphics.FillRectangle(headerBrush, new RectangleF(0, 0, Width, headerHeight));
                }
                finally
                {
                    graphics.Clip = previousClip;
                    previousClip.Dispose();
                }
            }

            if (drawBorder)
            {
                using var pen = new Pen(_borderColor, 1f);
                graphics.DrawPath(pen, path);
                if (!_headerFillColor.IsEmpty && headerHeight > 0 && headerHeight < Height)
                {
                    float y = (float)Math.Round(headerHeight) - 0.5f;
                    graphics.SmoothingMode = SmoothingMode.None;
                    graphics.DrawLine(pen, 1f, y, Width - 2f, y);
                }
            }
        }
        finally
        {
            graphics.SmoothingMode = previousSmoothing;
            graphics.PixelOffsetMode = previousOffset;
        }
    }
}

/// <summary>
/// A left-to-right group of buttons drawn on one rounded track: the Log | Web | extension pages view switcher and the
/// zoom group. The children are PillButtons; UpdateSegmentCorners rounds the outer corners of the first and last
/// visible ones so a selected end segment follows the track's outline.
/// </summary>
internal sealed class SegmentedPanel : FlowLayoutPanel
{
    private Color _trackColor = Color.Transparent;
    private Color _borderColor = Color.Empty;

    public SegmentedPanel()
    {
        SetStyle(
            ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor,
            true);
        BackColor = Color.Transparent;
        FlowDirection = FlowDirection.LeftToRight;
        WrapContents = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Margin = new Padding(0);
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color TrackColor
    {
        get => _trackColor;
        set
        {
            if (_trackColor != value)
            {
                _trackColor = value;
                Invalidate(true);
            }
        }
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color BorderColor
    {
        get => _borderColor;
        set
        {
            if (_borderColor != value)
            {
                _borderColor = value;
                Invalidate();
            }
        }
    }

    /// <summary>
    /// Decides which segments count as shown when the corners are rounded. Control.Visible cannot be used: it reports
    /// false for every child until the window itself is visible. Without it, every segment counts.
    /// </summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Func<Control, bool>? IsSegmentShown { get; set; }

    /// <summary>Rounds only the outer corners of the first and last shown segments.</summary>
    public void UpdateSegmentCorners()
    {
        Func<Control, bool> isShown = IsSegmentShown ?? (_ => true);
        List<PillButton> visible = Controls.OfType<PillButton>().Where(segment => isShown(segment)).ToList();
        for (int i = 0; i < visible.Count; i++)
        {
            RoundedCorners corners = RoundedCorners.None;
            if (i == 0)
            {
                corners |= RoundedCorners.Left;
            }

            if (i == visible.Count - 1)
            {
                corners |= RoundedCorners.Right;
            }

            visible[i].Corners = corners;
        }
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        base.OnPaintBackground(e);
        if (Width <= 0 || Height <= 0)
        {
            return;
        }

        Graphics graphics = e.Graphics;
        SmoothingMode previousSmoothing = graphics.SmoothingMode;
        PixelOffsetMode previousOffset = graphics.PixelOffsetMode;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        try
        {
            bool drawBorder = !_borderColor.IsEmpty && _borderColor.A > 0;
            var bounds = drawBorder ? new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f) : new RectangleF(0, 0, Width, Height);
            using GraphicsPath path = RoundedGeometry.Create(bounds, -1f, RoundedCorners.All);
            if (_trackColor.A > 0)
            {
                using var brush = new SolidBrush(_trackColor);
                graphics.FillPath(brush, path);
            }

            if (drawBorder)
            {
                using var pen = new Pen(_borderColor, 1f);
                graphics.DrawPath(pen, path);
            }
        }
        finally
        {
            graphics.SmoothingMode = previousSmoothing;
            graphics.PixelOffsetMode = previousOffset;
        }
    }

    protected override void OnControlAdded(ControlEventArgs e)
    {
        base.OnControlAdded(e);
        UpdateSegmentCorners();
    }

    protected override void OnControlRemoved(ControlEventArgs e)
    {
        base.OnControlRemoved(e);
        UpdateSegmentCorners();
    }
}

/// <summary>
/// A Chrome-style tab header: the active tab is filled with rounded top corners, inactive tabs are flat (with a
/// faint fill while hovered) and separated by a thin line.
/// </summary>
internal sealed class TabHeaderPanel : Panel
{
    private bool _active;
    private bool _hovered;
    private Color _fillColor = Color.Transparent;
    private Color _separatorColor = Color.Empty;

    public TabHeaderPanel()
    {
        SetStyle(
            ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor,
            true);
        BackColor = Color.Transparent;
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool IsActive
    {
        get => _active;
        set
        {
            if (_active != value)
            {
                _active = value;
                Invalidate(true);
            }
        }
    }

    /// <summary>The tab's colour: the whole tab while active, a light wash of it while an inactive tab is hovered.</summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color FillColor
    {
        get => _fillColor;
        set
        {
            if (_fillColor != value)
            {
                _fillColor = value;
                Invalidate(true);
            }
        }
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color SeparatorColor
    {
        get => _separatorColor;
        set
        {
            if (_separatorColor != value)
            {
                _separatorColor = value;
                Invalidate();
            }
        }
    }

    /// <summary>Called by the header and its children so hovering anywhere on the tab highlights it.</summary>
    public void SetHovered(bool hovered)
    {
        if (_hovered != hovered)
        {
            _hovered = hovered;
            Invalidate(true);
        }
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        SetHovered(true);
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        // Moving onto a child (title, icon, close button) also leaves the panel; only a real exit clears the hover.
        if (!ClientRectangle.Contains(PointToClient(MousePosition)))
        {
            SetHovered(false);
        }

        base.OnMouseLeave(e);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        base.OnPaintBackground(e);
        if (Width <= 0 || Height <= 0)
        {
            return;
        }

        Graphics graphics = e.Graphics;
        SmoothingMode previousSmoothing = graphics.SmoothingMode;
        PixelOffsetMode previousOffset = graphics.PixelOffsetMode;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        try
        {
            float radius = Math.Min(Height / 3.5f, 9f * (DeviceDpi > 0 ? DeviceDpi : 96) / 96f);
            if (_active && _fillColor.A > 0)
            {
                using GraphicsPath path = RoundedGeometry.Create(new RectangleF(0, 0, Width, Height + radius), radius, RoundedCorners.Top);
                using var brush = new SolidBrush(_fillColor);
                graphics.FillPath(brush, path);
            }
            else if (_hovered && _fillColor.A > 0)
            {
                float inset = Math.Max(1f, Height * 0.08f);
                using GraphicsPath path = RoundedGeometry.Create(new RectangleF(1f, inset, Width - 2f, Height - (inset * 2f)), radius, RoundedCorners.All);
                using var brush = new SolidBrush(Color.FromArgb(Math.Min(255, (int)(_fillColor.A * 0.55)), _fillColor));
                graphics.FillPath(brush, path);
            }
            else if (!_separatorColor.IsEmpty && _separatorColor.A > 0)
            {
                graphics.SmoothingMode = SmoothingMode.None;
                using var pen = new Pen(_separatorColor, 1f);
                float top = Height * 0.28f;
                graphics.DrawLine(pen, Width - 1f, top, Width - 1f, Height - top);
            }
        }
        finally
        {
            graphics.SmoothingMode = previousSmoothing;
            graphics.PixelOffsetMode = previousOffset;
        }
    }
}

/// <summary>The built-in icons of the top bar, drawn by PhantomSvgRenderer in the current text colour.</summary>
internal static class PhantomIcons
{
    private const string Open = "<svg viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"2\" stroke-linecap=\"round\" stroke-linejoin=\"round\">";

    public static PhantomSvgDocument Settings { get; } = PhantomSvgDocument.Parse(Open + BuildGearPolygon() + "<circle cx=\"12\" cy=\"12\" r=\"3\"/></svg>");

    public static PhantomSvgDocument Back { get; } = PhantomSvgDocument.Parse(Open + "<path d=\"M19 12H5\"/><path d=\"M11 6l-6 6 6 6\"/></svg>");

    public static PhantomSvgDocument Forward { get; } = PhantomSvgDocument.Parse(Open + "<path d=\"M5 12h14\"/><path d=\"M13 6l6 6-6 6\"/></svg>");

    public static PhantomSvgDocument Refresh { get; } = PhantomSvgDocument.Parse(Open + "<path d=\"M20 12a8 8 0 1 1-2.34-5.66L20 8.5\"/><path d=\"M20 3.5v5h-5\"/></svg>");

    public static PhantomSvgDocument Plus { get; } = PhantomSvgDocument.Parse(Open + "<path d=\"M12 5v14\"/><path d=\"M5 12h14\"/></svg>");

    public static PhantomSvgDocument Minus { get; } = PhantomSvgDocument.Parse(Open + "<path d=\"M5 12h14\"/></svg>");

    public static PhantomSvgDocument StarOutline { get; } = PhantomSvgDocument.Parse(Open + BuildStarPolygon("none", "currentColor") + "</svg>");

    /// <summary>The star of a page that is in the favourites: filled gold.</summary>
    public static PhantomSvgDocument StarFilled { get; } = PhantomSvgDocument.Parse(Open + BuildStarPolygon("#F5B301", "#D99A00") + "</svg>");

    private static string BuildGearPolygon()
    {
        // Eight teeth around a ring: each tooth rises from the inner radius to the outer radius and back.
        const int teeth = 8;
        const double inner = 7.4;
        const double outer = 10.4;
        var points = new StringBuilder();
        for (int i = 0; i < teeth; i++)
        {
            double centre = i * (360.0 / teeth);
            foreach ((double offset, double radius) in new[] { (-15.0, inner), (-8.5, outer), (8.5, outer), (15.0, inner) })
            {
                double angle = (centre + offset) * Math.PI / 180.0;
                points.Append(FormattableString.Invariant($"{12 + (radius * Math.Cos(angle)):0.##},{12 + (radius * Math.Sin(angle)):0.##} "));
            }
        }

        return $"<polygon points=\"{points.ToString().TrimEnd()}\"/>";
    }

    private static string BuildStarPolygon(string fill, string stroke)
    {
        var points = new StringBuilder();
        for (int i = 0; i < 10; i++)
        {
            double angle = (-90 + (i * 36)) * Math.PI / 180.0;
            double radius = i % 2 == 0 ? 9.6 : 4.3;
            points.Append(FormattableString.Invariant($"{12 + (radius * Math.Cos(angle)):0.##},{12.6 + (radius * Math.Sin(angle)):0.##} "));
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"<polygon points=\"{points.ToString().TrimEnd()}\" fill=\"{fill}\" stroke=\"{stroke}\" stroke-width=\"1.8\"/>");
    }
}
