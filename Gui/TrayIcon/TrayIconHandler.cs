using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Avalonia;
using Avalonia.Platform;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Controls;
using System.Globalization;
using System.Reflection;

namespace PtzJoystickControl.Gui.TrayIcon
{
    internal class TrayIconHandler
    {
        private readonly string _assemblyName;
        private readonly Avalonia.Controls.TrayIcon _trayIcon;

        public TrayIconHandler()
        {
            _assemblyName = Assembly.GetExecutingAssembly().GetName().Name!;

            // Initialize Tray Icon
            _trayIcon = new Avalonia.Controls.TrayIcon()
            {
                IsVisible = true,
                Menu = buildNativeMenu(),
                ToolTipText = "PTZ Joystick Control"
            };

            _trayIcon.Clicked += (object? s , EventArgs e) => OnShowClicked?.Invoke(s, e);

            UpdateIcon(0);
        }

        public void UpdateIcon(int number)
        {
            // Rendering is inside the try too: this runs on the UI thread when the selected camera
            // changes, and a failure must only leave the old icon, never take the app down.
            try
            {
                _trayIcon.Icon = IconFactory.PngsToMultiSizeIcon(RenderIcons(number));
            }
            catch (Exception e)
            {
                Trace.TraceWarning($"Tray icon: failed to set icon: {e}");
            }
        }

        // The tray icon background with the camera number on it, at 16, 24 and 32 px.
        private List<Bitmap> RenderIcons(int number)
        {
            int[] sizes = { 16, 24, 32 };
            string text = number > 0 ? number < 100 ? number.ToString() : "##" : "-";
            List<Bitmap> bitmaps = new(sizes.Length);

            for (int i = 0; i < sizes.Length; i++)
            {
                var size = sizes[i];
                using Bitmap background = new(AssetLoader.Open(new Uri($"avares://{_assemblyName}/Assets/bg_{size}.png")));

                // 96 DPI: one bitmap pixel per device-independent pixel, as Avalonia 0.10 rendered it.
                using var rtb = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
                using (var ctx = rtb.CreateDrawingContext())
                {
                    var formattedText = new FormattedText(
                        text,
                        CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight,
                        new Typeface("Microsoft Sans Serif", FontStyle.Normal, FontWeight.SemiBold),
                        size - 4,
                        Brushes.White)
                    {
                        TextAlignment = TextAlignment.Center,
                        MaxTextWidth = size,
                        MaxTextHeight = size,
                    };

                    ctx.DrawImage(background, new Rect(0, 0, size, size));
                    ctx.DrawText(formattedText, new Point(0, 0));
                }

                using var ms = new MemoryStream();
                rtb.Save(ms, new PngBitmapEncoderOptions());
                ms.Position = 0;

                bitmaps.Add(new Bitmap(ms));
            }

            return bitmaps;
        }

        public void Exit(object? s, EventArgs e)
        {
            _trayIcon.Dispose();
        }

        private NativeMenu buildNativeMenu() {
            var headerItem = new NativeMenuItem("PTZ Joystick Control");
            headerItem.Click += (object? s , EventArgs e) => OnShowClicked?.Invoke(s, e);

            var upadteCheckItem = new NativeMenuItem("Check for updates");
            upadteCheckItem.Click += (object? s, EventArgs e) => OnUpdateCheckClicked?.Invoke(s, e);

            var showLogItem = new NativeMenuItem("Show log");
            showLogItem.Click += (object? s, EventArgs e) => OnShowLogClicked?.Invoke(s, e);

            var quitItem = new NativeMenuItem("Quit");
            quitItem.Click += (object? s , EventArgs e) => OnQuitClicked?.Invoke(s, e);

            return new NativeMenu{
                headerItem,
                upadteCheckItem,
                showLogItem,
                new NativeMenuItemSeparator(),
                quitItem
            };
        }

        public event EventHandler? OnShowClicked;
        public event EventHandler? OnUpdateCheckClicked;
        public event EventHandler? OnShowLogClicked;
        public event EventHandler? OnQuitClicked;
    }
}
