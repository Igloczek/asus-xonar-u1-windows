// XonarU1: the tray flyout. Layout and styles live in Flyout.xaml.
// Copyright (C) 2026 XonarU1 contributors. Licensed under GPL-3.0-or-later.
using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shell;
using Microsoft.Win32;

static partial class XonarU1
{
    class Flyout
    {
        const double PanelWidth = 340, EdgeMargin = 12;

        // A click on the tray icon first closes the flyout through
        // deactivation; this stops the same click from reopening it.
        const int ReopenGuardMs = 300;

        const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20, DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWA_SYSTEMBACKDROP_TYPE = 38;
        const int CornerRound = 2, BackdropAcrylic = 3;
        const int GWL_STYLE = -16, WS_SYSMENU = 0x00080000;
        const string CheckLabel = "Check for updates";

        readonly Tray tray;
        readonly Window window;
        readonly Border panel, ledCard, knobCard;
        readonly TranslateTransform slide;
        readonly TextBlock status;
        readonly CheckBox ledSwitch;
        readonly TextBlock ledState, updateStatus;
        readonly Button updateAction;
        string updateTag;
        readonly StackPanel ledOptions;
        readonly UniformGrid ledColors, effects, sensitivity;
        readonly IntPtr hwnd;
        int hiddenAt = Environment.TickCount - ReopenGuardMs;

        public Flyout(Tray tray)
        {
            this.tray = tray;
            using (var xaml = Assembly.GetExecutingAssembly().GetManifestResourceStream("Flyout.xaml"))
                panel = (Border)XamlReader.Load(xaml);
            slide = (TranslateTransform)panel.FindName("Slide");
            status = (TextBlock)panel.FindName("Status");
            ledCard = (Border)panel.FindName("LedCard");
            knobCard = (Border)panel.FindName("KnobCard");
            ledSwitch = (CheckBox)panel.FindName("LedSwitch");
            ledState = (TextBlock)panel.FindName("LedState");
            ledOptions = (StackPanel)panel.FindName("LedOptions");
            ledColors = (UniformGrid)panel.FindName("LedColors");
            effects = (UniformGrid)panel.FindName("Effects");
            sensitivity = (UniformGrid)panel.FindName("Sensitivity");

            ledSwitch.Click += (s, e) =>
            {
                config.LedOn = ledSwitch.IsChecked == true;
                LedChanged();
            };

            foreach (var led in LedColors)
            {
                string id = led.Id;
                AddSegment(ledColors, led.Label, () =>
                {
                    config.Color = id;
                    if (id == "blue" && config.Effect == "activity") config.Effect = "solid";
                    LedChanged();
                });
            }

            for (int i = 0; i < Effects.Length; i++)
            {
                string effect = Effects[i];
                AddSegment(effects, EffectLabels[i], () =>
                {
                    config.Effect = effect;
                    LedChanged();
                });
            }

            for (int i = 0; i < Dividers.Length; i++)
            {
                int divider = Dividers[i];
                AddSegment(sensitivity, DividerLabels[i], () => tray.SelectDivider(divider));
            }

            ((Button)panel.FindName("Exit")).Click += (s, e) => Application.Current.Shutdown();

            updateStatus = (TextBlock)panel.FindName("UpdateStatus");
            updateAction = (Button)panel.FindName("UpdateAction");
            updateAction.Click += (s, e) => { if (updateTag == null) CheckForUpdate(); else InstallUpdate(); };
            ShowUpdateState("Version " + Version, CheckLabel, true);

            window = new Window
            {
                Width = PanelWidth,
                SizeToContent = SizeToContent.Height,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Topmost = true,
                Background = Brushes.Transparent,
                Content = panel,
                Left = -32000,
                Top = -32000,
            };
            WindowChrome.SetWindowChrome(window, new WindowChrome
            {
                CaptionHeight = 0,
                GlassFrameThickness = new Thickness(-1),
                ResizeBorderThickness = new Thickness(0),
                UseAeroCaptionButtons = false,
            });
            window.Deactivated += (s, e) => Close();
            window.PreviewKeyDown += (s, e) => { if (e.Key == Key.Escape) Close(); };
            window.Closing += (s, e) => { e.Cancel = true; Close(); };
            hwnd = new WindowInteropHelper(window).EnsureHandle();
            HwndSource.FromHwnd(hwnd).CompositionTarget.BackgroundColor = Colors.Transparent;

            // Without the system menu the frame has no close button.
            SetWindowLong(hwnd, GWL_STYLE, GetWindowLong(hwnd, GWL_STYLE) & ~WS_SYSMENU);
        }

        // Radio buttons with the same parent form a group by themselves.
        void AddSegment(UniformGrid group, string label, Action onClick)
        {
            var segment = new RadioButton { Content = label, Style = (Style)panel.FindResource("Segment") };
            segment.Click += (s, e) => onClick();
            group.Children.Add(segment);
        }

        public void Toggle()
        {
            if (window.IsVisible) Close();
            else if (Environment.TickCount - hiddenAt > ReopenGuardMs) Open();
        }

        public void Open()
        {
            Sync();
            ApplyTheme();
            window.Left = -32000;
            window.Show();
            window.UpdateLayout();
            Rect area = SystemParameters.WorkArea;
            window.Left = area.Right - window.ActualWidth - EdgeMargin;
            window.Top = area.Bottom - window.ActualHeight - EdgeMargin;
            window.Activate();
            SetForegroundWindow(hwnd);

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            slide.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(24, 0, TimeSpan.FromMilliseconds(160)) { EasingFunction = ease });
        }

        void Close()
        {
            if (!window.IsVisible) return;
            window.Hide();
            hiddenAt = Environment.TickCount;
        }

        public void UpdateStatus(bool connected)
        {
            status.Text = StatusText(connected);
            ledCard.IsEnabled = connected;
            knobCard.IsEnabled = connected;
        }

        void ShowUpdateState(string text, string action, bool enabled)
        {
            updateStatus.Text = text;
            updateAction.Content = action;
            updateAction.IsEnabled = enabled;
        }

        // Runs slow network work off the UI thread, then shows the outcome.
        void RunUpdateStep(Func<Action> work, string failure)
        {
            new Thread(() =>
            {
                Action done;
                try { done = work(); }
                catch (Exception) { done = () => ShowUpdateState(failure, CheckLabel, true); }
                panel.Dispatcher.BeginInvoke(done);
            }) { IsBackground = true }.Start();
        }

        void CheckForUpdate()
        {
            ShowUpdateState("Checking for updates...", CheckLabel, false);
            RunUpdateStep(() =>
            {
                string tag = LatestTag();
                if (!IsNewer(tag))
                    return () => ShowUpdateState("Version " + Version + " is up to date", CheckLabel, true);
                return () =>
                {
                    updateTag = tag;
                    ShowUpdateState("Version " + tag.TrimStart('v') + " is available", "Update", true);
                };
            }, "Couldn't check for updates");
        }

        void InstallUpdate()
        {
            string tag = updateTag;
            updateTag = null;
            ShowUpdateState("Downloading update...", "Update", false);
            RunUpdateStep(() =>
            {
                DownloadAndRunSetup(tag);
                return () => ShowUpdateState("Installing update...", "Update", false);
            }, "Couldn't download the update");
        }

        void LedChanged()
        {
            Sync();
            tray.LedChanged();
        }

        void Sync()
        {
            ledSwitch.IsChecked = config.LedOn;
            ledState.Text = config.LedOn ? "On" : "Off";
            ledOptions.IsEnabled = config.LedOn;
            for (int i = 0; i < LedColors.Length; i++)
                ((RadioButton)ledColors.Children[i]).IsChecked = LedColors[i].Id == config.Color;
            for (int i = 0; i < Effects.Length; i++)
            {
                var segment = (RadioButton)effects.Children[i];
                segment.IsChecked = Effects[i] == config.Effect;
                segment.IsEnabled = Effects[i] != "activity" || config.Color != "blue";
            }
            for (int i = 0; i < Dividers.Length; i++)
                ((RadioButton)sensitivity.Children[i]).IsChecked = Dividers[i] == config.Divider;
        }

        void ApplyTheme()
        {
            bool light = UsesLightTheme();

            int dark = light ? 0 : 1, corner = CornerRound, backdrop = BackdropAcrylic;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
            // Windows 10 and early 11 have no system backdrop, so the panel
            // falls back to a solid colour there.
            bool hasBackdrop = DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int)) == 0;

            Set("PanelBrush", hasBackdrop ? "#00000000" : (light ? "#F3F3F3" : "#2B2B2B"));
            Set("TextBrush", light ? "#E4000000" : "#FFFFFFFF");
            Set("SubtleBrush", light ? "#9E000000" : "#C8FFFFFF");
            Set("CardBrush", light ? "#B3FFFFFF" : "#0DFFFFFF");
            Set("HoverBrush", light ? "#0A000000" : "#0FFFFFFF");
            Set("WellBrush", light ? "#0F000000" : "#33000000");
            Set("StrokeBrush", light ? "#0F000000" : "#14FFFFFF");
            Set("AccentBrush", light ? "#005FB8" : "#60CDFF");
            Set("OnAccentBrush", light ? "#FFFFFF" : "#000000");
        }

        void Set(string key, string color)
        {
            panel.Resources[key] = new BrushConverter().ConvertFromString(color);
        }

        static bool UsesLightTheme()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                return key != null && Equals(key.GetValue("AppsUseLightTheme"), 1);
        }

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
        [DllImport("user32.dll")]
        static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")]
        static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll")]
        static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    }
}
