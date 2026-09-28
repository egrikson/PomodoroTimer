// Помодоро — таймер для Windows 11, оформление по Fluent Design.
// Компилируется csc.exe из состава .NET Framework, см. build.ps1.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Pomodoro
{
    internal static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct MARGINS { public int L, R, T, B; }

        [StructLayout(LayoutKind.Sequential)]
        internal struct FLASHWINFO
        {
            public uint cbSize;
            public IntPtr hwnd;
            public uint dwFlags;
            public uint uCount;
            public uint dwTimeout;
        }

        [DllImport("dwmapi.dll")]
        internal static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        [DllImport("dwmapi.dll")]
        internal static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS m);

        [DllImport("user32.dll")]
        internal static extern bool FlashWindowEx(ref FLASHWINFO info);

        internal const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        internal const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        internal const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    }

    public static class Program
    {
        // Наборы «работа / перерыв» в минутах
        static readonly int[,] Presets = { { 25, 5 }, { 50, 10 }, { 60, 15 } };

        // Геометрия кольца на холсте 220 × 220
        const double Cx = 110, Cy = 110, R = 92;

        // Сколько звонит будильник, если его не выключить
        static readonly TimeSpan RingFor = TimeSpan.FromSeconds(30);

        static Window win;
        static Border root;
        static Grid bar;
        static TextBlock timeTxt, phaseTxt, doneTxt;
        static System.Windows.Shapes.Path prog;
        static ArcSegment arc;
        static Button mainBtn, resetBtn, skipBtn, soundBtn, pinBtn, minBtn, closeBtn, backdropBtn;
        static readonly Ellipse[] dots = new Ellipse[4];
        static readonly RadioButton[] segs = new RadioButton[3];

        static Brush accentBrush, restBrush, trackBrush, solidBrush, txt1Brush;
        static DispatcherTimer ringTimer;
        static IntPtr hwnd = IntPtr.Zero;

        static bool light;
        static int idx, done, backdrop;      // backdrop: 0 мика, 1 акрил, 2 сплошная
        static bool isWork = true, running, sound = true;
        static double total = 25 * 60, left = 25 * 60;
        static DateTime endAt = DateTime.Now;

        [STAThread]
        public static void Main()
        {
            var app = new Application();
            Build();
            app.Run(win);
        }

        // ───────────────────────── системная тема и акцент ─────────────────────────

        static bool ReadLightTheme()
        {
            try
            {
                object v = Registry.GetValue(
                    @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                    "AppsUseLightTheme", 0);
                return v != null && Convert.ToInt32(v) != 0;
            }
            catch { return false; }
        }

        static Color ReadAccent(bool isLight)
        {
            // Тёмная тема Windows показывает SystemAccentColorLight2, светлая — Dark1
            int i = isLight ? 4 : 1;
            try
            {
                var blob = Registry.GetValue(
                    @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Accent",
                    "AccentPalette", null) as byte[];
                if (blob != null && blob.Length >= i * 4 + 3)
                    return Color.FromRgb(blob[i * 4], blob[i * 4 + 1], blob[i * 4 + 2]);
            }
            catch { }
            return isLight ? Color.FromRgb(0x00, 0x5F, 0xB8) : Color.FromRgb(0x4C, 0xC2, 0xFF);
        }

        static string Hex(Color c)
        {
            return string.Format(CultureInfo.InvariantCulture, "#FF{0:X2}{1:X2}{2:X2}", c.R, c.G, c.B);
        }

        static SolidColorBrush Solid(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        // ───────────────────────────── сборка окна ─────────────────────────────

        static void Build()
        {
            light = ReadLightTheme();
            Color accent = ReadAccent(light);
            // SystemFillColorSuccess
            Color rest = light ? Color.FromRgb(0x0F, 0x7B, 0x0F) : Color.FromRgb(0x6C, 0xCB, 0x5F);
            double luma = 0.299 * accent.R + 0.587 * accent.G + 0.114 * accent.B;

            // Токены Fluent (WinUI Common Colors)
            var t = new Dictionary<string, string>();
            if (light)
            {
                t["TXT1"] = "#E4000000"; t["TXT2"] = "#9B000000";
                t["CTRL1"] = "#B3FFFFFF"; t["CTRL2"] = "#80F9F9F9"; t["CTRL3"] = "#4DF9F9F9";
                t["BR1"] = "#0F000000";  t["BR2"] = "#29000000";
                t["ABR1"] = "#14FFFFFF"; t["ABR2"] = "#66000000";
                t["SUB1"] = "#09000000"; t["SUB2"] = "#06000000";
                t["TRACK"] = "#1A000000";
                t["SOLID"] = "#FFF3F3F3";
            }
            else
            {
                t["TXT1"] = "#FFFFFFFF"; t["TXT2"] = "#C5FFFFFF";
                t["CTRL1"] = "#0FFFFFFF"; t["CTRL2"] = "#15FFFFFF"; t["CTRL3"] = "#08FFFFFF";
                t["BR1"] = "#17FFFFFF";  t["BR2"] = "#0FFFFFFF";
                t["ABR1"] = "#14FFFFFF"; t["ABR2"] = "#23000000";
                t["SUB1"] = "#0FFFFFFF"; t["SUB2"] = "#0AFFFFFF";
                t["TRACK"] = "#1AFFFFFF";
                t["SOLID"] = "#FF202020";
            }
            t["ACC"] = Hex(accent);
            t["ACCTXT"] = luma > 150 ? "#FF000000" : "#FFFFFFFF";

            string xaml;
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("ui.xaml"))
            using (var r = new StreamReader(s, System.Text.Encoding.UTF8, true))
                xaml = r.ReadToEnd();

            foreach (var kv in t) xaml = xaml.Replace("%" + kv.Key + "%", kv.Value);

            win = (Window)XamlReader.Parse(xaml);

            var chrome = new WindowChrome
            {
                CaptionHeight = 0,
                GlassFrameThickness = new Thickness(-1),
                ResizeBorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(0)
            };
            WindowChrome.SetWindowChrome(win, chrome);

            root = (Border)win.FindName("Root");
            bar = (Grid)win.FindName("Bar");
            timeTxt = (TextBlock)win.FindName("TimeTxt");
            phaseTxt = (TextBlock)win.FindName("PhaseTxt");
            doneTxt = (TextBlock)win.FindName("DoneTxt");
            prog = (System.Windows.Shapes.Path)win.FindName("Prog");
            mainBtn = (Button)win.FindName("MainBtn");
            resetBtn = (Button)win.FindName("ResetBtn");
            skipBtn = (Button)win.FindName("SkipBtn");
            soundBtn = (Button)win.FindName("SoundBtn");
            pinBtn = (Button)win.FindName("PinBtn");
            minBtn = (Button)win.FindName("MinBtn");
            closeBtn = (Button)win.FindName("CloseBtn");
            backdropBtn = (Button)win.FindName("BackdropBtn");
            for (int i = 0; i < 4; i++) dots[i] = (Ellipse)win.FindName("D" + i);
            for (int i = 0; i < 3; i++) segs[i] = (RadioButton)win.FindName("P" + i);

            arc = (ArcSegment)((PathGeometry)prog.Data).Figures[0].Segments[0];

            accentBrush = Solid(accent);
            restBrush = Solid(rest);
            var bc = new BrushConverter();
            trackBrush = (Brush)bc.ConvertFromString(t["TRACK"]);
            solidBrush = (Brush)bc.ConvertFromString(t["SOLID"]);
            txt1Brush = (Brush)bc.ConvertFromString(t["TXT1"]);

            Wire();
            Update();
        }

        static void Wire()
        {
            win.SourceInitialized += delegate
            {
                hwnd = new WindowInteropHelper(win).Handle;
                var src = HwndSource.FromHwnd(hwnd);
                if (src != null && src.CompositionTarget != null)
                    src.CompositionTarget.BackgroundColor = Colors.Transparent;

                var m = new Native.MARGINS { L = -1, R = -1, T = -1, B = -1 };
                Native.DwmExtendFrameIntoClientArea(hwnd, ref m);

                int dark = light ? 0 : 1;
                Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, 4);
                int corner = 2; // DWMWCP_ROUND — штатное скругление Windows 11
                Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, 4);

                SetBackdrop(0);
            };

            win.Closed += delegate { Alarm.Stop(); };

            // Любое действие в окне глушит звонок
            win.PreviewMouseDown += delegate { Silence(); };

            bar.MouseLeftButtonDown += delegate
            {
                try { win.DragMove(); } catch { }
            };

            mainBtn.Click += delegate { ToggleRun(); };
            resetBtn.Click += delegate { Reset(); };
            skipBtn.Click += delegate { SetPhase(!isWork); Update(); };

            for (int i = 0; i < 3; i++)
            {
                segs[i].Tag = i;
                segs[i].Checked += delegate (object sender, RoutedEventArgs e)
                {
                    UsePreset((int)((RadioButton)sender).Tag);
                };
            }

            soundBtn.Click += delegate
            {
                sound = !sound;
                if (!sound) Silence();
                soundBtn.Content = sound ? "" : "";
                soundBtn.ToolTip = sound ? "Будильник включён" : "Будильник выключен";
                soundBtn.Foreground = sound ? txt1Brush : accentBrush;
            };

            pinBtn.Click += delegate
            {
                win.Topmost = !win.Topmost;
                pinBtn.Content = win.Topmost ? "" : "";
                pinBtn.Foreground = win.Topmost ? accentBrush : txt1Brush;
            };
            backdropBtn.Click += delegate { SetBackdrop((backdrop + 1) % 3); };
            minBtn.Click += delegate { win.WindowState = WindowState.Minimized; };
            closeBtn.Click += delegate { win.Close(); };

            win.PreviewKeyDown += delegate (object sender, KeyEventArgs e)
            {
                Silence();
                if (e.Key == Key.Space) { ToggleRun(); e.Handled = true; }
                else if (e.Key == Key.R) { Reset(); e.Handled = true; }
                else if (e.Key == Key.Escape) { win.Close(); }
            };

            // Звонок сам замолкает, если к компьютеру не подошли
            ringTimer = new DispatcherTimer { Interval = RingFor };
            ringTimer.Tick += delegate { Silence(); };

            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            timer.Tick += delegate
            {
                if (!running) return;
                left = (endAt - DateTime.Now).TotalSeconds;
                if (left <= 0) { left = 0; Complete(); }
                else Update();
            };
            timer.Start();
        }

        // ───────────────────────────── поведение ─────────────────────────────

        static void SetBackdrop(int mode)
        {
            backdrop = mode;
            if (hwnd == IntPtr.Zero) return;

            int v = mode == 0 ? 2 : (mode == 1 ? 3 : 1);   // 2 Mica, 3 Acrylic, 1 Auto/None
            int hr = Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_SYSTEMBACKDROP_TYPE, ref v, 4);

            root.Background = (mode == 2 || hr != 0) ? solidBrush : Brushes.Transparent;
        }

        static string Fmt(double sec)
        {
            int s = (int)Math.Ceiling(sec);
            if (s < 0) s = 0;
            return (s / 60).ToString("00", CultureInfo.InvariantCulture) + ":" +
                   (s % 60).ToString("00", CultureInfo.InvariantCulture);
        }

        static void UpdateRing(double p)
        {
            if (p < 0) p = 0;
            if (p > 0.99999) p = 0.99999;
            double a = 2 * Math.PI * p;
            arc.Point = new Point(Cx + R * Math.Sin(a), Cy - R * Math.Cos(a));
            arc.IsLargeArc = p > 0.5;
        }

        static void Update()
        {
            string txt = Fmt(left);
            timeTxt.Text = txt;
            win.Title = txt + " — Помодоро";

            UpdateRing(total > 0 ? left / total : 0);

            phaseTxt.Text = isWork ? "Фокус" : "Перерыв";
            prog.Stroke = isWork ? accentBrush : restBrush;

            mainBtn.Content = running ? "Пауза" : "Старт";

            int k = done % 4;
            if (done > 0 && k == 0) k = 4;
            for (int i = 0; i < 4; i++) dots[i].Fill = i < k ? accentBrush : trackBrush;

            doneTxt.Text = "Завершено: " + done.ToString(CultureInfo.InvariantCulture);
        }

        static void SetPhase(bool work)
        {
            isWork = work;
            total = (work ? Presets[idx, 0] : Presets[idx, 1]) * 60;
            left = total;
            if (running) endAt = DateTime.Now.AddSeconds(left);
        }

        static void UsePreset(int i)
        {
            idx = i;
            running = false;
            isWork = true;
            total = Presets[i, 0] * 60;
            left = total;
            Update();
        }

        static void Reset()
        {
            running = false;
            left = total;
            Update();
        }

        static void ToggleRun()
        {
            if (running)
            {
                left = (endAt - DateTime.Now).TotalSeconds;
                if (left < 0) left = 0;
                running = false;
            }
            else
            {
                if (left <= 0) left = total;
                endAt = DateTime.Now.AddSeconds(left);
                running = true;
            }
            Update();
        }

        static void Complete()
        {
            if (isWork) { done++; SetPhase(false); }
            else SetPhase(true);
            Ring(isWork);          // isWork здесь — уже новая фаза
            Update();
        }

        static void Ring(bool toWork)
        {
            if (sound)
            {
                Alarm.Start(toWork);
                ringTimer.Stop();
                ringTimer.Start();
            }

            if (hwnd == IntPtr.Zero) return;
            var f = new Native.FLASHWINFO
            {
                hwnd = hwnd,
                dwFlags = 3 | 12,   // FLASHW_ALL | FLASHW_TIMERNOFG
                uCount = 12,
                dwTimeout = 0
            };
            f.cbSize = (uint)Marshal.SizeOf(f);
            Native.FlashWindowEx(ref f);
        }

        static void Silence()
        {
            if (ringTimer != null) ringTimer.Stop();
            Alarm.Stop();
        }
    }
}
