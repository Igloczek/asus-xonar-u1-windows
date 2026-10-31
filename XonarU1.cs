// XonarU1: tray app for the ASUS Xonar U1 (USB 0B05:1743) volume knob and LEDs.
// Copyright (C) 2026 XonarU1 contributors. Licensed under GPL-3.0-or-later.
//
// Runs on the stock Microsoft USB audio driver: everything goes through the
// card's HID interface. Protocol from xonard by Alessandro Pignotti
// (github.com/alexp-sssup/asus-xonar-u1-utils).
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Windows.Threading;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

static partial class XonarU1
{
    const string DeviceFilter = "vid_0b05&pid_1743";
    const int ReportSize = 17;

    // The card reports itself as "USB Advanced Audio Device".
    const string CardName = "ASUS Xonar U1";
    const string AudioFunctionFilter = "VID_0B05&PID_1743&MI_00";
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\XonarU1";
    const int SPDRP_FRIENDLYNAME = 0x0C;

    // Windows prepends the report ID, so xonard's buf[6] is index 7 here.
    const int ControlByte = 7;

    // Global config byte: bit 7 enables knob reports, bits 4-3 are the blue
    // LED (11 blink, otherwise solid), bits 2-1 the red LED (11 blink,
    // 10 off, 00/01 flash on audio).
    const byte KnobReports = 0x80;
    const byte BlueBlink = 3 << 3, BlueSolid = 1 << 3;
    const byte RedBlink = 3 << 1, RedAudio = 1 << 1, RedOff = 2 << 1;

    const byte BlueLed = 0x13, RedLed = 0x11;

    // Time on and period for the blinking modes.
    const int BlinkOn = 100, BlinkPeriod = 200;

    // The knob's push switch bounces on release.
    const int PressDebounceMs = 250;

    // Physical knob states in rotation order.
    static readonly int[] WheelMap = { 0, 1, 3, 2 };

    class LedColor
    {
        public string Id, Label;
        public Color Color;
        public LedColor(string id, string label, Color color) { Id = id; Label = label; Color = color; }
    }

    static readonly LedColor[] LedColors = {
        new LedColor("blue", "Blue", Color.FromArgb(0, 120, 215)),
        new LedColor("red", "Red", Color.FromArgb(220, 50, 50)),
        new LedColor("purple", "Purple", Color.FromArgb(150, 70, 220)),
    };

    // Only the red LED can follow audio, so blue has no "activity" effect.
    static readonly string[] Effects = { "solid", "blink", "activity" };
    static readonly string[] EffectLabels = { "Solid", "Blinking", "On activity" };

    // Knob positions per 2% volume step.
    static readonly int[] Dividers = { 6, 3, 1 };
    static readonly string[] DividerLabels = { "Low", "Medium", "High" };

    class Config
    {
        public bool LedOn = true;
        public string Color = "blue", Effect = "solid";
        public int Divider = 3;
        public bool RenameDeclined;
    }

    static Config config;
    static Tray tray;

    // Open write handle while the card is connected; guards LED writes from
    // the menu and the watchdog.
    static readonly object deviceLock = new object();
    static FileStream device;

    static string ConfigPath
    {
        get
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XonarU1", "XonarU1.ini");
        }
    }

    static Config LoadConfig()
    {
        var c = new Config();
        if (!File.Exists(ConfigPath)) return c;
        foreach (var line in File.ReadAllLines(ConfigPath))
        {
            var kv = line.Split(new[] { '=' }, 2);
            if (kv.Length != 2) continue;
            string k = kv[0].Trim().ToLowerInvariant(), v = kv[1].Trim();
            int n;
            if (k == "led") c.LedOn = v.ToLowerInvariant() != "off";
            else if (k == "color") c.Color = v.ToLowerInvariant();
            else if (k == "effect") c.Effect = v.ToLowerInvariant();
            else if (k == "divider" && int.TryParse(v, out n) && n > 0) c.Divider = n;
            else if (k == "rename") c.RenameDeclined = v == "declined";
        }
        return c;
    }

    static void SaveConfig()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath));
        File.WriteAllLines(ConfigPath, new[] {
            "led=" + (config.LedOn ? "on" : "off"), "color=" + config.Color, "effect=" + config.Effect, "divider=" + config.Divider,
            "rename=" + (config.RenameDeclined ? "declined" : "auto")
        });
    }

    static void SendGlobal(FileStream dev, byte conf)
    {
        var buf = new byte[ReportSize];
        buf[1] = 0x02;
        buf[2] = 0x01;
        buf[3] = conf;
        dev.Write(buf, 0, ReportSize);
    }

    static void SendBlink(FileStream dev, byte led, int on, int period)
    {
        var buf = new byte[ReportSize];
        buf[1] = led;
        buf[2] = 0x02;
        buf[3] = (byte)on;
        buf[4] = (byte)period;
        dev.Write(buf, 0, ReportSize);
    }

    static void Apply(FileStream dev, Config c)
    {
        bool blue = c.Color == "blue", red = c.Color == "red";

        // Blue is "off" by blinking with a zero duty cycle. Purple keeps it
        // solid underneath whatever the red LED does.
        byte blueBase = (byte)(c.Color == "purple" ? BlueSolid : BlueBlink);

        if (!c.LedOn)
        {
            SendGlobal(dev, KnobReports | BlueBlink | RedOff);
            SendBlink(dev, BlueLed, 0, 0);
        }
        else if (blue)
        {
            SendGlobal(dev, (byte)(KnobReports | (c.Effect == "blink" ? BlueBlink : BlueSolid) | RedOff));
            if (c.Effect == "blink") SendBlink(dev, BlueLed, BlinkOn, BlinkPeriod);
        }
        else
        {
            SendGlobal(dev, (byte)(KnobReports | blueBase | (c.Effect == "activity" ? RedAudio : RedBlink)));
            if (c.Effect == "blink") SendBlink(dev, RedLed, BlinkOn, BlinkPeriod);
            // Time on past the period keeps the LED lit without flicker.
            else if (c.Effect == "solid") SendBlink(dev, RedLed, 201, 200);
            if (red) SendBlink(dev, BlueLed, 0, 0);
        }
        dev.Flush();
    }

    static void ApplyToDevice()
    {
        lock (deviceLock)
        {
            if (device == null) return;
            // A failed write means the reader is failing too and will reconnect.
            try { Apply(device, config); } catch (Exception) { }
        }
    }

    [STAThread]
    static int Main(string[] args)
    {
        if (Array.IndexOf(args, "--uninstall") >= 0)
        {
            Uninstall();
            return 0;
        }
        if (Array.IndexOf(args, "--rename") >= 0) return RenameCard() ? 0 : 1;
        if (!IsInstalled())
        {
            Install();
            return 0;
        }
        bool first;
        using (new Mutex(true, "Local\\XonarU1", out first))
        {
            if (!first) return 0;
            Tray.RegisterStartup();
            var app = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
            config = LoadConfig();
            tray = new Tray();
            new Thread(ServeForever) { IsBackground = true }.Start();
            if (Array.IndexOf(args, "--show") >= 0) tray.ShowFlyout();
            app.Run();
            tray.Dispose();
            return 0;
        }
    }

    // The download is its own installer: run from anywhere else, it copies
    // itself here and starts that copy. No admin rights needed.
    static string InstallDir
    {
        get
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "XonarU1");
        }
    }

    static string InstalledExe
    {
        get { return Path.Combine(InstallDir, "XonarU1.exe"); }
    }

    static bool IsInstalled()
    {
        return string.Equals(Path.GetFullPath(Application.ExecutablePath), InstalledExe, StringComparison.OrdinalIgnoreCase);
    }

    static void StopOtherCopies()
    {
        int self = System.Diagnostics.Process.GetCurrentProcess().Id;
        foreach (var other in System.Diagnostics.Process.GetProcessesByName("XonarU1"))
        {
            if (other.Id == self) continue;
            other.Kill();
            other.WaitForExit(3000);
        }
    }

    static void Install()
    {
        StopOtherCopies();
        Directory.CreateDirectory(InstallDir);
        File.Copy(Application.ExecutablePath, InstalledExe, true);
        using (var key = Registry.CurrentUser.CreateSubKey(UninstallKey))
        {
            key.SetValue("DisplayName", "XonarU1 for ASUS Xonar U1");
            key.SetValue("DisplayVersion", Version);
            key.SetValue("Publisher", "XonarU1 contributors");
            key.SetValue("DisplayIcon", InstalledExe);
            key.SetValue("InstallLocation", InstallDir);
            key.SetValue("UninstallString", "\"" + InstalledExe + "\" --uninstall");
            key.SetValue("URLInfoAbout", "https://github.com/Igloczek/asus-xonar-u1-windows");
            key.SetValue("EstimatedSize", (int)(new FileInfo(InstalledExe).Length / 1024), RegistryValueKind.DWord);
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }
        System.Diagnostics.Process.Start(InstalledExe, "--show");
    }

    static void Uninstall()
    {
        StopOtherCopies();
        Tray.UnregisterStartup();
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
        string settings = Path.GetDirectoryName(ConfigPath);
        if (Directory.Exists(settings)) Directory.Delete(settings, true);

        // A running exe can't delete itself, so leave that to cmd after exiting.
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe",
            "/c ping -n 3 127.0.0.1 >nul & rd /s /q \"" + InstallDir + "\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetTempPath(),
        });
    }

    static void ServeForever()
    {
        while (true)
        {
            try
            {
                using (var dev = OpenDevice(FileAccess.Write))
                using (var input = OpenDevice(FileAccess.Read))
                    if (dev != null && input != null) Serve(dev, input);
            }
            // Unplugging surfaces as several exception types; just reconnect.
            catch (Exception) { }
            Thread.Sleep(2000);
        }
    }

    static void Serve(FileStream dev, FileStream input)
    {
        lock (deviceLock)
        {
            Apply(dev, config);
            device = dev;
        }
        tray.SetConnected(true);
        new Thread(RenameInBackground) { IsBackground = true }.Start();

        // The card forgets its setup across sleep, so reapply after any gap
        // in this timer.
        var stop = new ManualResetEvent(false);
        var watchdog = new Thread(() =>
        {
            DateTime last = DateTime.UtcNow;
            while (!stop.WaitOne(5000))
            {
                DateTime now = DateTime.UtcNow;
                if ((now - last).TotalSeconds > 20) ApplyToDevice();
                last = now;
            }
        }) { IsBackground = true };
        watchdog.Start();

        try
        {
            var buf = new byte[ReportSize];
            int pos = -1, travel = 0;
            int lastPress = Environment.TickCount - PressDebounceMs;
            bool pressed = false;
            while (input.Read(buf, 0, ReportSize) > 0)
            {
                int b = buf[ControlByte];
                int next = WheelMap[b & 3];
                if (pos != -1 && next != pos)
                {
                    if ((pos + 1) % 4 == next) travel++;
                    else if ((pos + 3) % 4 == next) travel--;
                    if (Math.Abs(travel) >= config.Divider)
                    {
                        Tap(travel > 0 ? VK_VOLUME_UP : VK_VOLUME_DOWN);
                        travel = 0;
                    }
                }
                pos = next;
                bool down = (b & 4) != 0;
                if (down && !pressed && Environment.TickCount - lastPress > PressDebounceMs)
                {
                    Tap(VK_VOLUME_MUTE);
                    lastPress = Environment.TickCount;
                }
                pressed = down;
            }
        }
        finally
        {
            stop.Set();
            watchdog.Join();
            lock (deviceLock) device = null;
            tray.SetConnected(false);
        }
    }

    const byte VK_VOLUME_MUTE = 0xAD, VK_VOLUME_DOWN = 0xAE, VK_VOLUME_UP = 0xAF;

    // Real media keys, so the Windows volume flyout appears as usual.
    static void Tap(byte vk)
    {
        keybd_event(vk, 0, 1, UIntPtr.Zero);
        keybd_event(vk, 0, 1 | 2, UIntPtr.Zero);
    }

    class Tray : IDisposable
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunValue = "XonarU1";

        readonly NotifyIcon icon = new NotifyIcon();
        readonly Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        readonly Flyout flyout;
        bool connected;

        public Tray()
        {
            flyout = new Flyout(this);
            icon.MouseUp += (s, e) => flyout.Toggle();
            Refresh();
            icon.Visible = true;
        }

        public void ShowFlyout()
        {
            flyout.Open();
        }

        public void SetConnected(bool value)
        {
            dispatcher.BeginInvoke((Action)(() => { connected = value; Refresh(); }));
        }

        public void LedChanged()
        {
            SaveConfig();
            ApplyToDevice();
            Refresh();
        }

        public void SelectDivider(int divider)
        {
            config.Divider = divider;
            SaveConfig();
        }

        void Refresh()
        {
            string state = connected ? "Connected" : "Card not found";
            flyout.UpdateStatus(connected);
            icon.Text = "Xonar U1: " + state;
            var old = icon.Icon;
            icon.Icon = DrawIcon(connected ? CurrentColor() : Color.DimGray, connected);
            if (old != null) old.Dispose();
        }

        static Color CurrentColor()
        {
            if (config.LedOn)
                foreach (var led in LedColors)
                    if (led.Id == config.Color) return led.Color;
            return Color.Gray;
        }

        // The card's knob: a ring in the LED's colour, with a pointer when connected.
        static Icon DrawIcon(Color color, bool pointer)
        {
            int size = SystemInformation.SmallIconSize.Width;
            using (var bitmap = new Bitmap(size, size))
            using (var g = Graphics.FromImage(bitmap))
            using (var pen = new Pen(color, Math.Max(2f, size / 7f)))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                float inset = pen.Width / 2 + 0.5f;
                g.DrawEllipse(pen, inset, inset, size - inset * 2 - 1, size - inset * 2 - 1);
                if (pointer) g.DrawLine(pen, size / 2f, size / 2f, size / 2f, size / 5f);
                IntPtr handle = bitmap.GetHicon();
                try
                {
                    using (var temp = Icon.FromHandle(handle)) return (Icon)temp.Clone();
                }
                finally { DestroyIcon(handle); }
            }
        }

        // The knob needs this app running, so it always starts with Windows.
        // Rewritten at each launch so moving the exe keeps working.
        public static void RegisterStartup()
        {
            using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
                key.SetValue(RunValue, "\"" + Application.ExecutablePath + "\"");
        }

        public static void UnregisterStartup()
        {
            using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
                key.DeleteValue(RunValue, false);
        }

        public void Dispose()
        {
            icon.Visible = false;
            icon.Dispose();
        }
    }

    [DllImport("user32.dll")]
    static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr icon);

    static FileStream OpenDevice(FileAccess access)
    {
        string path = FindDevicePath();
        if (path == null) return null;
        uint desired = access == FileAccess.Read ? 0x80000000u : 0x40000000u;
        var h = CreateFile(path, desired, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (h.IsInvalid) return null;
        return new FileStream(h, access, ReportSize, false);
    }

    static string FindDevicePath()
    {
        Guid g;
        HidD_GetHidGuid(out g);
        IntPtr set = SetupDiGetClassDevs(ref g, IntPtr.Zero, IntPtr.Zero, 0x12);
        try
        {
            for (int i = 0; ; i++)
            {
                var d = new InterfaceData();
                d.Size = Marshal.SizeOf(d);
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref g, i, ref d)) return null;
                int need;
                SetupDiGetDeviceInterfaceDetail(set, ref d, IntPtr.Zero, 0, out need, IntPtr.Zero);
                IntPtr b = Marshal.AllocHGlobal(need);
                try
                {
                    Marshal.WriteInt32(b, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetail(set, ref d, b, need, out need, IntPtr.Zero)) continue;
                    string path = Marshal.PtrToStringUni(b + 4);
                    if (path.ToLowerInvariant().Contains(DeviceFilter)) return path;
                }
                finally { Marshal.FreeHGlobal(b); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    // Finds the card's audio function, which is what names the sound devices.
    static bool FindAudioFunction(IntPtr set, ref DeviceData d)
    {
        for (int i = 0; ; i++)
        {
            d = new DeviceData();
            d.Size = Marshal.SizeOf(d);
            if (!SetupDiEnumDeviceInfo(set, i, ref d)) return false;
            var id = new StringBuilder(512);
            int need;
            if (SetupDiGetDeviceInstanceId(set, ref d, id, id.Capacity, out need) && id.ToString().ToUpperInvariant().Contains(AudioFunctionFilter))
                return true;
        }
    }

    static bool CardNeedsRename()
    {
        IntPtr set = SetupDiGetClassDevsAll(IntPtr.Zero, "USB", IntPtr.Zero, 0x6);
        try
        {
            var d = new DeviceData();
            if (!FindAudioFunction(set, ref d)) return false;
            var name = new byte[512];
            int type, need;
            if (!SetupDiGetDeviceRegistryProperty(set, ref d, SPDRP_FRIENDLYNAME, out type, name, name.Length, out need)) return true;
            return Encoding.Unicode.GetString(name, 0, need).TrimEnd('\0') != CardName;
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    // Renaming needs admin rights, which this app doesn't otherwise use, so it
    // happens in an elevated copy of itself. The card isn't restarted to apply
    // the name, because that would cut audio in whatever is playing.
    static void RenameInBackground()
    {
        if (config.RenameDeclined || Interlocked.Exchange(ref renaming, 1) == 1) return;
        try
        {
            if (!CardNeedsRename()) return;
            var start = new System.Diagnostics.ProcessStartInfo(Application.ExecutablePath, "--rename")
            {
                UseShellExecute = true,
                Verb = "runas",
            };
            using (var rename = System.Diagnostics.Process.Start(start)) rename.WaitForExit();
        }
        // The user declined the admin prompt, so don't keep asking.
        catch (System.ComponentModel.Win32Exception)
        {
            config.RenameDeclined = true;
            try { SaveConfig(); } catch (Exception) { }
        }
        finally { renaming = 0; }
    }

    static int renaming;

    // Windows picks the new name up when the card reconnects.
    static bool RenameCard()
    {
        IntPtr set = SetupDiGetClassDevsAll(IntPtr.Zero, "USB", IntPtr.Zero, 0x6);
        try
        {
            var d = new DeviceData();
            if (!FindAudioFunction(set, ref d)) return false;
            var name = Encoding.Unicode.GetBytes(CardName + "\0");
            return SetupDiSetDeviceRegistryProperty(set, ref d, SPDRP_FRIENDLYNAME, name, name.Length);
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct InterfaceData { public int Size; public Guid Class; public int Flags; public IntPtr Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    struct DeviceData { public int Size; public Guid Class; public int Instance; public IntPtr Reserved; }

    [DllImport("hid.dll")]
    static extern void HidD_GetHidGuid(out Guid g);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint creation, uint flags, IntPtr template);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr enumerator, IntPtr parent, int flags);
    [DllImport("setupapi.dll")]
    static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid g, int index, ref InterfaceData data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, EntryPoint = "SetupDiGetDeviceInterfaceDetailW")]
    static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref InterfaceData data, IntPtr detail, int size, out int need, IntPtr info);
    [DllImport("setupapi.dll")]
    static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, EntryPoint = "SetupDiGetClassDevsW")]
    static extern IntPtr SetupDiGetClassDevsAll(IntPtr g, string enumerator, IntPtr parent, int flags);
    [DllImport("setupapi.dll")]
    static extern bool SetupDiEnumDeviceInfo(IntPtr set, int index, ref DeviceData data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, EntryPoint = "SetupDiGetDeviceInstanceIdW")]
    static extern bool SetupDiGetDeviceInstanceId(IntPtr set, ref DeviceData data, StringBuilder id, int size, out int need);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, EntryPoint = "SetupDiGetDeviceRegistryPropertyW")]
    static extern bool SetupDiGetDeviceRegistryProperty(IntPtr set, ref DeviceData data, int property, out int type, byte[] buffer, int size, out int need);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, EntryPoint = "SetupDiSetDeviceRegistryPropertyW")]
    static extern bool SetupDiSetDeviceRegistryProperty(IntPtr set, ref DeviceData data, int property, byte[] buffer, int size);
}
