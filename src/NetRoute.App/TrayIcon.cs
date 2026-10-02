using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using NetRoute.Core;
using Forms = System.Windows.Forms;

namespace NetRoute.App;

/// Tray icon: globe coloured by mode (green Phone, blue LAN, gray Auto), amber badge on fallback.
sealed class TrayIcon : IDisposable
{
    readonly Forms.NotifyIcon _icon = new();
    readonly Dictionary<(TrayColor, bool), Icon> _icons = new();
    readonly Forms.ToolStripMenuItem _phone = new("Phone");
    readonly Forms.ToolStripMenuItem _lan = new("LAN");
    readonly Forms.ToolStripMenuItem _auto = new("Auto");
    readonly Forms.ToolStripMenuItem _show = new("Show card");
    readonly Forms.ToolStripMenuItem _startup = new("Start with Windows");
    readonly Forms.ToolStripMenuItem _quit = new("Quit");

    public TrayIcon()
    {
        _phone.Click += (_, _) => ModeRequested?.Invoke(RoutingMode.Phone);
        _lan.Click += (_, _) => ModeRequested?.Invoke(RoutingMode.Lan);
        _auto.Click += (_, _) => ModeRequested?.Invoke(RoutingMode.Auto);
        _show.Click += (_, _) => ShowCardRequested?.Invoke();
        _startup.Click += (_, _) => StartWithWindowsToggled?.Invoke();
        _quit.Click += (_, _) => QuitRequested?.Invoke();

        var menu = new Forms.ContextMenuStrip();
        menu.Items.AddRange(new Forms.ToolStripItem[]
        {
            _phone, _lan, _auto, new Forms.ToolStripSeparator(), _show, _startup, new Forms.ToolStripSeparator(), _quit,
        });
        _icon.ContextMenuStrip = menu;
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) ToggleCardRequested?.Invoke();
        };
        _icon.Icon = IconFor(TrayColor.Gray, badge: false);
        _icon.Text = "NetRoute Widget";
        _icon.Visible = true;
    }

    public event Action<RoutingMode>? ModeRequested;
    public event Action? ToggleCardRequested;
    public event Action? ShowCardRequested;
    public event Action? StartWithWindowsToggled;
    public event Action? QuitRequested;

    public void Update(CardView view, RoutingMode mode, bool canModify, bool startWithWindows)
    {
        _icon.Icon = IconFor(view.TrayColor, view.TrayBadge);
        _icon.Text = view.Tooltip.Length > 127 ? view.Tooltip[..127] : view.Tooltip;
        _phone.Checked = mode == RoutingMode.Phone;
        _lan.Checked = mode == RoutingMode.Lan;
        _auto.Checked = mode == RoutingMode.Auto;
        _phone.Enabled = _lan.Enabled = _auto.Enabled = canModify;
        _startup.Checked = startWithWindows;
        _startup.Enabled = canModify;
    }

    public void Notify(string message) =>
        _icon.ShowBalloonTip(5000, "NetRoute Widget", message, Forms.ToolTipIcon.Info);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        foreach (var icon in _icons.Values) icon.Dispose();
    }

    Icon IconFor(TrayColor color, bool badge)
    {
        if (!_icons.TryGetValue((color, badge), out var icon))
            _icons[(color, badge)] = icon = Draw(color, badge);
        return icon;
    }

    static Icon Draw(TrayColor color, bool badge)
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var fill = color switch
            {
                TrayColor.Green => Color.FromArgb(0x2E, 0xA0, 0x43),
                TrayColor.Blue => Color.FromArgb(0x25, 0x63, 0xEB),
                _ => Color.FromArgb(0x8B, 0x94, 0x9E),
            };
            using var globe = new SolidBrush(fill);
            using var lines = new Pen(Color.White, 2);
            g.FillEllipse(globe, 2, 2, 28, 28);
            g.DrawEllipse(lines, 10, 3, 12, 26);
            g.DrawLine(lines, 3, 16, 29, 16);
            if (badge)
            {
                using var amber = new SolidBrush(Color.FromArgb(0xF5, 0xA6, 0x23));
                using var ring = new Pen(Color.White, 1.5f);
                g.FillEllipse(amber, 18, 18, 13, 13);
                g.DrawEllipse(ring, 18, 18, 13, 13);
            }
        }

        var handle = bitmap.GetHicon();
        try
        {
            return (Icon)Icon.FromHandle(handle).Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr handle);
}
