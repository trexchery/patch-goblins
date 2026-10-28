using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PatchGoblins;

/// The one goblin green: the tray's dots, buttons, switches and meters, and the agents terminal's theme all read it.
/// `Hex` is what a theme file wants; the goblin's own pixels are not here and never change.
public static class Brand
{
    public const string DarkHex = "#5BD25B", LightHex = "#2E7D32";
    public static readonly Color Dark = ColorTranslator.FromHtml(DarkHex), Light = ColorTranslator.FromHtml(LightHex);
}

public sealed record Palette(
    bool IsDark, Color Back, Color Card, Color Text, Color Muted, Color Faint, Color Track,
    Color Live, Color Border, Color Hover, Color Pressed)
{
    public static Palette Current => (ForceDark is { } d ? !d : IsLight()) ? Light : Dark;

    /// Tests and screenshots pin a theme instead of reading Windows'.
    internal static bool? ForceDark;

    // Neutral greys plus the one goblin green (Brand): green for what is live or healthy, amber and red
    // for meters that are close to a limit.
    static readonly Palette Light = new(false,
        Color.FromArgb(249, 249, 249), Color.White, Color.FromArgb(24, 24, 24), Color.FromArgb(112, 112, 112),
        Color.FromArgb(160, 160, 160), Color.FromArgb(0, 0, 0, 0x14), Brand.Light,
        Color.FromArgb(0, 0, 0, 0x14), Color.FromArgb(0, 0, 0, 0x08), Color.FromArgb(0, 0, 0, 0x10));

    static readonly Palette Dark = new(true,
        Color.FromArgb(32, 32, 32), Color.FromArgb(40, 40, 40), Color.FromArgb(236, 236, 236), Color.FromArgb(150, 150, 150),
        Color.FromArgb(100, 100, 100), Color.FromArgb(0x1A, 255, 255, 255), Brand.Dark,
        Color.FromArgb(0x16, 255, 255, 255), Color.FromArgb(0x0C, 255, 255, 255), Color.FromArgb(0x16, 255, 255, 255));

    static bool IsLight()
    {
        using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return k?.GetValue("AppsUseLightTheme") is not int v || v != 0;
    }

    public static readonly Color Warn = Color.FromArgb(214, 162, 67), Critical = Color.FromArgb(218, 106, 94);

    /// Where meters turn amber; quota notifications use the same line.
    public const double WarnPct = 80;

    /// Meter fill: goblin green until a window is nearly spent.
    public Color Meter(double pct) =>
        pct >= 95 ? Critical : pct >= WarnPct ? Warn : Live;

    /// Tray dot (needs to read on any taskbar).
    public static Color ForPct(double pct) =>
        pct >= 95 ? Critical : pct >= WarnPct ? Warn : Brand.Dark;
}

/// Test pins for what Ui reads from the display; always null in the app. The tests set them before Ui is first touched
/// (its statics run once) to prove layouts hold on other scales and when Segoe UI is missing.
internal static class UiPins
{
    internal static float? Scale;
    internal static string? Family;
}

/// Fonts, DPI scaling, glyphs and Win32 cosmetics shared by every surface.
public static class Ui
{
    // ponytail: one scale from the primary monitor at start-up; per-monitor
    // re-layout on WM_DPICHANGED if people drag the flyout across monitors.
    public static readonly float Scale = UiPins.Scale ?? Real();
    static readonly float FontPin = Scale / Real(); // 1 in the app; a pinned scale grows the fonts with the layout, as a real display does

    static float Real()
    {
        using var g = Graphics.FromHwnd(IntPtr.Zero);
        return g.DpiX / 96f;
    }

    public static int S(int px) => (int)Math.Round(px * Scale);

    static readonly string TextFamily = UiPins.Family ?? Installed("Segoe UI Variable Text") ?? "Segoe UI";
    static readonly string DisplayFamily = Installed("Segoe UI Variable Display") ?? "Segoe UI";
    static readonly string IconFamily = Installed("Segoe Fluent Icons") ?? "Segoe MDL2 Assets";

    static string? Installed(string name) =>
        FontFamily.Families.Any(f => f.Name == name) ? name : null;

    public static Font Font(float pt, bool semibold = false) =>
        new(semibold ? (TextFamily == "Segoe UI" ? "Segoe UI Semibold" : TextFamily) : TextFamily, pt * FontPin,
            semibold && TextFamily != "Segoe UI" ? FontStyle.Bold : FontStyle.Regular);

    public static Font Display(float pt) => new(DisplayFamily, pt, FontStyle.Bold);
    public static Font Icons(float pt) => new(IconFamily, pt);

    public const string Refresh = "\uE72C", Settings = "\uE713", More = "\uE712", Terminal = "\uE756",
        Add = "\uE710", Check = "\uE73E", Pin = "\uE718", Unpin = "\uE77A", Close = "\uE711", Agents = "\uE99A", Chat = "\uE8BD";

    public static GraphicsPath Round(RectangleF r, float radius)
    {
        var d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    /// Dark scrollbars and title bars where Windows supports them; no-ops elsewhere.
    public static void ThemeWindow(Control c, Palette pal)
    {
        _ = SetWindowTheme(c.Handle, pal.IsDark ? "DarkMode_Explorer" : "Explorer", null);
        int dark = pal.IsDark ? 1 : 0;
        _ = DwmSetWindowAttribute(c.Handle, 20, ref dark, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
    }

    public static void RoundCorners(IntPtr hwnd)
    {
        int round = 2; // DWMWCP_ROUND
        _ = DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));
    }

    /// Recolours a plain dialog's standard controls to the palette.
    public static void ThemeDialog(Control root, Palette pal)
    {
        root.BackColor = pal.Back;
        root.ForeColor = pal.Text;
        foreach (Control c in root.Controls)
        {
            switch (c)
            {
                case TextBox or NumericUpDown or ComboBox:
                    c.BackColor = pal.Card;
                    c.ForeColor = pal.Text;
                    if (c is TextBox t) t.BorderStyle = BorderStyle.FixedSingle;
                    if (c is NumericUpDown n) n.BorderStyle = BorderStyle.FixedSingle;
                    if (c is ComboBox cb) cb.FlatStyle = FlatStyle.Flat;
                    break;
                case Label l when l.Tag as string == "muted":
                    l.ForeColor = pal.Muted;
                    break;
                case Pill or Toggle:
                    break;
                default:
                    ThemeDialog(c, pal);
                    break;
            }
        }
    }

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    static extern int SetWindowTheme(IntPtr hwnd, string? app, string? idList);

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}

public enum PillStyle { Primary, Subtle, Icon }

/// Rounded flat button with hover/press states; text, a Fluent glyph, or both.
public sealed class Pill : Control, IButtonControl
{
    public PillStyle Style { get; set; }
    public string Glyph { get; set; } = "";
    /// A small state dot at the pill's top right (null: none).
    public Color? Badge { get; set; }
    /// A highlight sweeping across the icon and label, 0 to 1 left to right (below 0: none).
    public float Shine { get; set; } = -1;
    public DialogResult DialogResult { get; set; }
    bool _hover, _down;

    public Pill(string text, PillStyle style = PillStyle.Subtle, string glyph = "")
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor | ControlStyles.Selectable, true);
        Text = text;
        Style = style;
        Glyph = glyph;
        Cursor = Cursors.Hand;
        Font = Ui.Font(9f, semibold: style == PillStyle.Primary);
        Height = Ui.S(style == PillStyle.Icon ? 28 : 30);
        Width = style == PillStyle.Icon ? Ui.S(28) : MeasureWidth();
        BackColor = Color.Transparent;
    }

    int MeasureWidth()
    {
        var w = TextRenderer.MeasureText(Text, Font).Width + Ui.S(20);
        return Glyph.Length > 0 ? w + Ui.S(20) : w;
    }

    public void NotifyDefault(bool value) { }
    public void PerformClick() => OnClick(EventArgs.Empty);

    protected override void OnClick(EventArgs e)
    {
        if (DialogResult != DialogResult.None && FindForm() is { } f) f.DialogResult = DialogResult;
        base.OnClick(e);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Space or Keys.Enter) PerformClick();
        base.OnKeyUp(e);
    }

    /// White band over the glyph (and the label, when there is one), drawn as paths so the gradient can fade, same spot as the text.
    void PaintShine(Graphics g, Palette pal, bool label = false, int glyphX = 0, int textX = 0)
    {
        using var icon = Ui.Icons(9f);
        using var path = new GraphicsPath();
        // Typographic for the label: the default format pads the left edge, which would sit the glint's text a pixel or two off the drawn text.
        using var fmt = new StringFormat(label ? StringFormat.GenericTypographic : StringFormat.GenericDefault) { Alignment = label ? StringAlignment.Near : StringAlignment.Center, LineAlignment = StringAlignment.Center };
        var glyphBox = label ? new RectangleF(glyphX, 0, Ui.S(14), Height) : new RectangleF(0, 0, Width, Height);
        path.AddString(Glyph, icon.FontFamily, (int)icon.Style, icon.SizeInPoints * g.DpiY / 72f, glyphBox, fmt);
        if (label) path.AddString(Text, Font.FontFamily, (int)Font.Style, Font.SizeInPoints * g.DpiY / 72f, new RectangleF(textX, 0, Width - textX, Height), fmt);
        float w = Width * 0.7f, x = -w + Shine * (Width + w);
        using var br = new LinearGradientBrush(new RectangleF(x, 0, w, Height), Color.Transparent, Color.Transparent, 20f)
        {
            InterpolationColors = new ColorBlend
            {
                // Light: white alone vanishes on the pale card, so the glint is flanked by the text colour.
                Positions = [0f, 0.3f, 0.5f, 0.7f, 1f],
                Colors = pal.IsDark
                    ? [Color.Transparent, Color.FromArgb(120, 255, 255, 255), Color.White, Color.FromArgb(120, 255, 255, 255), Color.Transparent]
                    : [Color.Transparent, Color.FromArgb(200, pal.Text), Color.White, Color.FromArgb(200, pal.Text), Color.Transparent],
            },
        };
        g.FillPath(br, path);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var pal = Palette.Current;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        using var path = Ui.Round(r, Ui.S(5));

        Color fg;
        if (Style == PillStyle.Primary)
        {
            var bg = Enabled ? pal.Live : pal.Track;
            if (_down || _hover) bg = Color.FromArgb(_down ? 200 : 225, bg);
            using var b = new SolidBrush(bg);
            g.FillPath(b, path);
            fg = Enabled ? pal.Back : pal.Muted;
        }
        else
        {
            if (Style == PillStyle.Subtle)
            {
                using var b = new SolidBrush(pal.Hover);
                g.FillPath(b, path);
            }
            if (_hover || _down)
            {
                using var b = new SolidBrush(_down ? pal.Pressed : pal.Hover);
                g.FillPath(b, path);
            }
            fg = Enabled ? pal.Text : pal.Muted;
        }
        if (Focused && ShowFocusCues)
        {
            using var pen = new Pen(pal.Text, Ui.S(1));
            g.DrawPath(pen, path);
        }

        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;
        if (Style == PillStyle.Icon || Text.Length == 0)
        {
            using var icon = Ui.Icons(9f);
            TextRenderer.DrawText(g, Glyph, icon, ClientRectangle, _hover ? pal.Text : pal.Muted, flags | TextFormatFlags.HorizontalCenter);
            if (Shine >= 0) PaintShine(g, pal);
            if (Badge is { } dot)
            {
                using var b = new SolidBrush(dot);
                float d = Ui.S(6);
                g.FillEllipse(b, Width - d - Ui.S(4), Ui.S(4), d, d);
            }
            return;
        }
        if (Glyph.Length > 0)
        {
            using var icon = Ui.Icons(9f);
            var textW = TextRenderer.MeasureText(g, Text, Font, Size.Empty, TextFormatFlags.NoPadding).Width;
            var x = (Width - textW - Ui.S(18)) / 2;
            TextRenderer.DrawText(g, Glyph, icon, new Rectangle(x, 0, Ui.S(14), Height), fg, flags);
            TextRenderer.DrawText(g, Text, Font, new Rectangle(x + Ui.S(18), 0, textW + Ui.S(2), Height), fg, flags);
            if (Shine >= 0) PaintShine(g, pal, label: true, glyphX: x, textX: x + Ui.S(18));
            if (Badge is { } dot)
            {
                using var b = new SolidBrush(dot);
                float d = Ui.S(6);
                g.FillEllipse(b, Width - d - Ui.S(6), Ui.S(6), d, d);
            }
            return;
        }
        TextRenderer.DrawText(g, Text, Font, ClientRectangle, fg, flags | TextFormatFlags.HorizontalCenter);
    }
}

/// Windows 11–style on/off switch with an optional caption to its right.
public sealed class Toggle : Control
{
    bool _checked;
    public event EventHandler? CheckedChanged;

    public bool Checked
    {
        get => _checked;
        set { if (_checked == value) return; _checked = value; Invalidate(); CheckedChanged?.Invoke(this, EventArgs.Empty); }
    }

    public Toggle(string text)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor | ControlStyles.Selectable, true);
        Text = text;
        Font = Ui.Font(8.5f);
        Cursor = Cursors.Hand;
        BackColor = Color.Transparent;
        Height = Ui.S(22);
        Width = Ui.S(42) + TextRenderer.MeasureText(text, Font).Width;
    }

    protected override void OnClick(EventArgs e) { Focus(); Checked = !Checked; base.OnClick(e); }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Space) Checked = !Checked;
        base.OnKeyUp(e);
    }

    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var pal = Palette.Current;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float w = Ui.S(32), h = Ui.S(16), y = (Height - h) / 2f;
        using var track = Ui.Round(new RectangleF(1, y, w, h), h / 2);
        if (Checked)
        {
            using var b = new SolidBrush(pal.Live);
            g.FillPath(b, track);
        }
        else
        {
            using var pen = new Pen(pal.Muted, Math.Max(1, Ui.S(1)));
            g.DrawPath(pen, track);
        }
        if (Focused && ShowFocusCues)
        {
            using var focus = new Pen(pal.Text, Math.Max(1, Ui.S(1))) { DashStyle = DashStyle.Dot };
            g.DrawPath(focus, track);
        }
        float k = h - Ui.S(7);
        var kx = Checked ? 1 + w - k - Ui.S(4) : 1 + Ui.S(4);
        using (var knob = new SolidBrush(Checked ? pal.Back : pal.Muted))
            g.FillEllipse(knob, kx, y + (h - k) / 2, k, k);
        TextRenderer.DrawText(g, Text, Font, new Point((int)w + Ui.S(8), (Height - Font.Height) / 2), pal.Muted, TextFormatFlags.NoPadding);
    }
}

/// Palette-aware context menus (the default renderer is always light).
public sealed class MenuRenderer(Palette pal) : ToolStripProfessionalRenderer(new MenuColors(pal))
{
    public static void Apply(ToolStrip menu)
    {
        var pal = Palette.Current;
        menu.Renderer = new MenuRenderer(pal);
        menu.Font = Ui.Font(9f);
        menu.ForeColor = pal.Text;
        menu.Padding = new Padding(Ui.S(4));
        foreach (ToolStripItem item in menu.Items)
        {
            item.Padding = new Padding(Ui.S(4), Ui.S(4), Ui.S(4), Ui.S(4));
            if (item is ToolStripMenuItem { HasDropDownItems: true } m) Apply(m.DropDown);
        }
    }

    /// One-shot menu: disposed after it closes and the clicked item's handler has run. Queued on
    /// the menu itself (it has a handle once shown): the control that opened it may be gone by
    /// then, since a dashboard refresh disposes every row.
    public static void DisposeOnClose(ContextMenuStrip menu) => menu.Closed += (_, _) => menu.BeginInvoke(menu.Dispose);

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? pal.Text : pal.Muted;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = pal.Muted;
        base.OnRenderArrow(e);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        using var f = Ui.Icons(9f);
        TextRenderer.DrawText(e.Graphics, Ui.Check, f, e.ImageRectangle, pal.Text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    sealed class MenuColors(Palette p) : ProfessionalColorTable
    {
        Color Popup => p.IsDark ? Color.FromArgb(44, 44, 44) : Color.FromArgb(249, 249, 249);
        Color Sel => p.IsDark ? Color.FromArgb(60, 60, 60) : Color.FromArgb(234, 234, 234);
        public override Color ToolStripDropDownBackground => Popup;
        public override Color ImageMarginGradientBegin => Popup;
        public override Color ImageMarginGradientMiddle => Popup;
        public override Color ImageMarginGradientEnd => Popup;
        public override Color MenuBorder => p.Border;
        public override Color MenuItemBorder => Sel;
        public override Color MenuItemSelected => Sel;
        public override Color MenuItemSelectedGradientBegin => Sel;
        public override Color MenuItemSelectedGradientEnd => Sel;
        public override Color MenuItemPressedGradientBegin => Sel;
        public override Color MenuItemPressedGradientEnd => Sel;
        public override Color CheckBackground => Popup;
        public override Color CheckSelectedBackground => Sel;
        public override Color CheckPressedBackground => Sel;
        public override Color SeparatorDark => p.Border;
        public override Color SeparatorLight => p.Border;
    }
}
