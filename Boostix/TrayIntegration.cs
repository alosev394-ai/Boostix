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
        private readonly System.Windows.Forms.NotifyIcon icon;
        private readonly System.Windows.Forms.ToolStripMenuItem boostItem;
        private readonly System.Drawing.Icon applicationIcon;

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

            var menu = new System.Windows.Forms.ContextMenuStrip();
            var openItem = new System.Windows.Forms.ToolStripMenuItem("Открыть Boostix");
            openItem.Font = new System.Drawing.Font(openItem.Font, System.Drawing.FontStyle.Bold);
            openItem.Click += delegate { Raise(OpenRequested); };
            menu.Items.Add(openItem);
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            boostItem = new System.Windows.Forms.ToolStripMenuItem("Запустить Boost");
            boostItem.ShortcutKeyDisplayString = "Ctrl+Alt+B";
            boostItem.Click += delegate { Raise(BoostToggleRequested); };
            menu.Items.Add(boostItem);
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            var exitItem = new System.Windows.Forms.ToolStripMenuItem("Выйти");
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
            boostItem.Text = active ? "Остановить Boost" : "Запустить Boost";
            icon.Text = ProductBrand.ProductName + (active ? ": Boost включён" : ": Boost выключен");
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
