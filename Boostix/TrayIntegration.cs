using System;
using System.Runtime.InteropServices;
using Boostix.Branding;

namespace Boostix
{
    /// <summary>
    /// Notification-area icon: open Boostix, switch Boost on or off, exit.
    /// WinForms types are fully qualified to keep them apart from WPF ones.
    /// </summary>
    internal sealed class BoostixTrayIcon : IDisposable
    {
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyIcon(IntPtr icon);

        private readonly System.Windows.Forms.NotifyIcon icon;
        private readonly System.Windows.Forms.ToolStripMenuItem boostItem;
        private readonly System.Drawing.Icon applicationIcon;
        private readonly System.Drawing.Icon idleIcon;
        private readonly System.Drawing.Icon activeIcon;

        public BoostixTrayIcon(string executablePath)
        {
            try
            {
                applicationIcon = System.Drawing.Icon.ExtractAssociatedIcon(executablePath);
            }
            catch (Exception)
            {
                applicationIcon = null;
            }
            idleIcon = CreateGaugeIcon(false);
            activeIcon = CreateGaugeIcon(true);

            var menu = new System.Windows.Forms.ContextMenuStrip();
            var openItem = new System.Windows.Forms.ToolStripMenuItem(L.T("Открыть Boostix"));
            openItem.Font = new System.Drawing.Font(openItem.Font, System.Drawing.FontStyle.Bold);
            openItem.Click += delegate { Raise(OpenRequested); };
            menu.Items.Add(openItem);
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            boostItem = new System.Windows.Forms.ToolStripMenuItem(L.T("Запустить Boost"));
            boostItem.ShortcutKeyDisplayString = "Ctrl+Alt+B";
            boostItem.Click += delegate { Raise(BoostToggleRequested); };
            menu.Items.Add(boostItem);
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            var exitItem = new System.Windows.Forms.ToolStripMenuItem(L.T("Выйти"));
            exitItem.Click += delegate { Raise(ExitRequested); };
            menu.Items.Add(exitItem);

            icon = new System.Windows.Forms.NotifyIcon
            {
                Icon = applicationIcon ?? System.Drawing.SystemIcons.Application,
                ContextMenuStrip = menu,
                Visible = true
            };
            icon.DoubleClick += delegate { Raise(OpenRequested); };
            icon.MouseClick += delegate(object sender, System.Windows.Forms.MouseEventArgs e)
            {
                if (e.Button == System.Windows.Forms.MouseButtons.Left)
                {
                    Raise(OpenRequested);
                }
            };
            SetBoostState(false);
        }

        public event EventHandler OpenRequested;
        public event EventHandler BoostToggleRequested;
        public event EventHandler ExitRequested;

        public void SetBoostState(bool active)
        {
            boostItem.Text = active ? L.T("Остановить Boost") : L.T("Запустить Boost");
            icon.Text = ProductBrand.ProductName + (active ? L.T(": Boost включён") : L.T(": Boost выключен"));
            System.Drawing.Icon stateIcon = active ? activeIcon : idleIcon;
            if (stateIcon != null)
            {
                icon.Icon = stateIcon;
            }
        }

        /// <summary>
        /// The tray gauge: needle at rest and a dim zone while Boost is off,
        /// needle in the lit lemon zone while it runs.
        /// </summary>
        internal static System.Drawing.Bitmap CreateGaugeBitmap(bool active)
        {
            const int size = 32;
            var bitmap = new System.Drawing.Bitmap(
                size,
                size,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (System.Drawing.Graphics graphics = System.Drawing.Graphics.FromImage(bitmap))
            {
                graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                graphics.Clear(System.Drawing.Color.Transparent);
                using (var tile = RoundedTile(new System.Drawing.RectangleF(1, 1, 30, 30), 7))
                using (var fill = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(15, 76, 92)))
                {
                    graphics.FillPath(fill, tile);
                }

                var lemon = System.Drawing.Color.FromArgb(255, 244, 79);
                var haze = System.Drawing.Color.FromArgb(159, 192, 198);
                const float centerX = 16F;
                const float centerY = 18.5F;
                const float radius = 9.75F;
                var arc = new System.Drawing.RectangleF(centerX - radius, centerY - radius, radius * 2, radius * 2);
                using (var scalePen = new System.Drawing.Pen(haze, 2.2F))
                using (var zonePen = new System.Drawing.Pen(
                    active ? lemon : System.Drawing.Color.FromArgb(70, lemon),
                    active ? 3.2F : 2.6F))
                using (var needlePen = new System.Drawing.Pen(active ? lemon : haze, 2.4F))
                using (var hub = new System.Drawing.SolidBrush(active ? lemon : haze))
                {
                    scalePen.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                    scalePen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                    zonePen.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                    zonePen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                    needlePen.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                    needlePen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                    // GDI+ measures arcs clockwise from +X; the gauge measures from up.
                    graphics.DrawArc(scalePen, arc, -120F - 90F, 175F);
                    graphics.DrawArc(zonePen, arc, 62F - 90F, 58F);
                    double needle = (active ? 88 : -112) * Math.PI / 180;
                    graphics.DrawLine(
                        needlePen,
                        centerX,
                        centerY,
                        centerX + (float)(radius * 0.9 * Math.Sin(needle)),
                        centerY - (float)(radius * 0.9 * Math.Cos(needle)));
                    graphics.FillEllipse(hub, centerX - 2.6F, centerY - 2.6F, 5.2F, 5.2F);
                }
            }
            return bitmap;
        }

        private static System.Drawing.Icon CreateGaugeIcon(bool active)
        {
            try
            {
                using (System.Drawing.Bitmap bitmap = CreateGaugeBitmap(active))
                {
                    IntPtr handle = bitmap.GetHicon();
                    try
                    {
                        // Clone owns its own handle, so the GDI handle can go.
                        using (System.Drawing.Icon borrowed = System.Drawing.Icon.FromHandle(handle))
                        {
                            return (System.Drawing.Icon)borrowed.Clone();
                        }
                    }
                    finally
                    {
                        DestroyIcon(handle);
                    }
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static System.Drawing.Drawing2D.GraphicsPath RoundedTile(
            System.Drawing.RectangleF rectangle,
            float radius)
        {
            float diameter = radius * 2;
            var path = new System.Drawing.Drawing2D.GraphicsPath();
            path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
            path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
            path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        public void Notify(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }
            icon.ShowBalloonTip(2500, ProductBrand.ProductName, text, System.Windows.Forms.ToolTipIcon.None);
        }

        public void Dispose()
        {
            icon.Visible = false;
            icon.Dispose();
            if (applicationIcon != null)
            {
                applicationIcon.Dispose();
            }
            if (idleIcon != null)
            {
                idleIcon.Dispose();
            }
            if (activeIcon != null)
            {
                activeIcon.Dispose();
            }
        }

        private void Raise(EventHandler handler)
        {
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>
    /// Ctrl+Alt+B, registered for the main window so it works while a game
    /// has focus. Registration fails when another program owns the shortcut.
    /// </summary>
    internal static class BoostHotkey
    {
        public const int Id = 0xB005;
        public const int WmHotkey = 0x0312;
        public const string DisplayText = "Ctrl+Alt+B";
        private const uint ModAlt = 0x0001;
        private const uint ModControl = 0x0002;
        private const uint ModNoRepeat = 0x4000;
        private const uint VirtualKeyB = 0x42;

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint virtualKey);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnregisterHotKey(IntPtr window, int id);

        public static bool Register(IntPtr window)
        {
            return window != IntPtr.Zero &&
                RegisterHotKey(window, Id, ModControl | ModAlt | ModNoRepeat, VirtualKeyB);
        }

        public static void Unregister(IntPtr window)
        {
            if (window != IntPtr.Zero)
            {
                UnregisterHotKey(window, Id);
            }
        }
    }
}
