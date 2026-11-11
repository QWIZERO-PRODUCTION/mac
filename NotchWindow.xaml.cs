using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using Windows.Media.Control;

namespace WinNotch
{
    public partial class NotchWindow : Window
    {
        enum Mode { Idle, Media, Info, Notif }

        static readonly CultureInfo Ru = new CultureInfo("ru-RU");
        static readonly Color[] Palette =
        {
            Color.FromRgb(0xFF, 0x45, 0x3A), Color.FromRgb(0xFF, 0x9F, 0x0A), Color.FromRgb(0x30, 0xD1, 0x58),
            Color.FromRgb(0x40, 0xC8, 0xE0), Color.FromRgb(0x0A, 0x84, 0xFF), Color.FromRgb(0x5E, 0x5C, 0xE6),
            Color.FromRgb(0xBF, 0x5A, 0xF2), Color.FromRgb(0xFF, 0x37, 0x5F)
        };

        // Нижний радиус корпуса анимируется (маленький у свёрнутой челки, большой у раскрытой)
        public static readonly DependencyProperty BottomRadiusProperty = DependencyProperty.Register(
            "BottomRadius", typeof(double), typeof(NotchWindow),
            new PropertyMetadata(14.0, (d, e) =>
            {
                var w = (NotchWindow)d;
                double r = (double)e.NewValue;
                if (w.Shell != null) w.Shell.CornerRadius = new CornerRadius(0, 0, r, r);
            }));

        GlobalSystemMediaTransportControlsSessionManager _mgr;
        GlobalSystemMediaTransportControlsSession _session;
        readonly NotificationWatcher _watcher = new NotificationWatcher();
        readonly DropShadowEffect _shadow = new DropShadowEffect
        {
            BlurRadius = 24, ShadowDepth = 6, Direction = 270, Opacity = 0, Color = Colors.Black
        };

        readonly DispatcherTimer _tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        readonly DispatcherTimer _expand = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        readonly DispatcherTimer _collapse = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        readonly DispatcherTimer _notifTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };

        bool _hover, _playing, _hasMedia, _notifActive, _fullscreen, _eqOn, _hideIdle, _scrubbing;
        bool? _hasBattery;
        Mode _mode = (Mode)(-1);
        double _w = -1, _h = -1;
        double _total, _startSec;
        int _gen;
        string _trackKey;
        BitmapImage _art;
        DateTime _progressHold = DateTime.MinValue;

        public bool ShowNotifications { get; set; }

        public bool HideIdle
        {
            get { return _hideIdle; }
            set { _hideIdle = value; Refresh(); }
        }

        public NotchWindow()
        {
            InitializeComponent();
            ShowNotifications = true;
            Reposition();
            _hideIdle = Settings.Get("HideIdle", false);

            _tick.Tick += (s, e) => OnTick();
            _expand.Tick += (s, e) => { _expand.Stop(); _hover = true; Refresh(); };
            _collapse.Tick += (s, e) =>
            {
                if (_scrubbing) return;
                _collapse.Stop();
                _hover = false;
                Refresh();
            };
            _notifTimer.Tick += (s, e) =>
            {
                if (_hover) return; // пока курсор на челке — уведомление не исчезает
                _notifTimer.Stop();
                _notifActive = false;
                Refresh();
            };

            SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
            SourceInitialized += (s, e) => MakeToolWindow();
            Loaded += async (s, e) =>
            {
                _tick.Start();
                _watcher.Received += OnNotification;
                _watcher.Start();
                Refresh();

                // На старте системы медиа-служба может быть ещё не готова — пробуем несколько раз.
                for (int i = 0; i < 8 && _mgr == null; i++)
                {
                    try { await InitMediaAsync(); }
                    catch (Exception ex) { App.Log(ex); await Task.Delay(5000); }
                }
            };
            Closed += (s, e) =>
            {
                SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
                _watcher.Dispose();
            };
        }

        // ───────────── Окно: не берёт фокус, нет в Alt+Tab ─────────────

        void MakeToolWindow()
        {
            IntPtr h = new WindowInteropHelper(this).Handle;
            int ex = GetWindowLong(h, -20);
            SetWindowLong(h, -20, ex | 0x00000080 /*TOOLWINDOW*/ | 0x08000000 /*NOACTIVATE*/);
        }

        void Reposition()
        {
            Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
            Top = 0;
        }

        void OnDisplayChanged(object sender, EventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(Reposition));
        }

        // ───────────── Состояния и анимация ─────────────

        bool HasBattery()
        {
            if (_hasBattery == null)
            {
                try
                {
                    var ps = System.Windows.Forms.SystemInformation.PowerStatus;
                    _hasBattery = (ps.BatteryChargeStatus & System.Windows.Forms.BatteryChargeStatus.NoSystemBattery) == 0;
                }
                catch { _hasBattery = false; }
            }
            return _hasBattery.Value;
        }

        void Refresh()
        {
            Mode m = _notifActive ? Mode.Notif : _hover ? (_hasMedia ? Mode.Media : Mode.Info) : Mode.Idle;

            bool visible = !_fullscreen && !(m == Mode.Idle && _hideIdle && !_hasMedia);
            Notch.Visibility = visible ? Visibility.Visible : Visibility.Hidden;

            UpdateMini(m);

            double w, h;
            switch (m)
            {
                case Mode.Media: w = 400; h = 194; break;
                case Mode.Info: w = 300; h = HasBattery() ? 154 : 128; break;
                case Mode.Notif: w = 380; h = 130; break;
                default: w = _hasMedia ? 230 : 180; h = 32; break;
            }
            if (m == _mode && w == _w && h == _h) return;

            bool wasOpen = (int)_mode >= 0 && _mode != Mode.Idle;
            bool open = m != Mode.Idle;
            _mode = m; _w = w; _h = h;

            MediaPanel.Visibility = m == Mode.Media ? Visibility.Visible : Visibility.Collapsed;
            InfoPanel.Visibility = m == Mode.Info ? Visibility.Visible : Visibility.Collapsed;
            NotifPanel.Visibility = m == Mode.Notif ? Visibility.Visible : Visibility.Collapsed;
            if (m == Mode.Info) UpdateInfo();
            if (m == Mode.Media) UpdateProgress();

            // Раскрытие — с лёгкой «пружинкой», сворачивание — плавно и без отскока
            IEasingFunction ease = open && !wasOpen
                ? (IEasingFunction)new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.28 }
                : new CubicEase { EasingMode = EasingMode.EaseOut };
            var dur = TimeSpan.FromMilliseconds(open ? 380 : 300);
            Notch.BeginAnimation(FrameworkElement.WidthProperty, new DoubleAnimation(w, dur) { EasingFunction = ease });
            Notch.BeginAnimation(FrameworkElement.HeightProperty, new DoubleAnimation(h, dur) { EasingFunction = ease });
            BeginAnimation(BottomRadiusProperty, new DoubleAnimation(open ? 26 : 14, dur)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });

            // Тень только у раскрытой челки: в свёрнутом виде она не нужна и «съедала» бы клики под собой
            if (open)
            {
                Notch.Effect = _shadow;
                _shadow.BeginAnimation(DropShadowEffect.OpacityProperty, new DoubleAnimation(0.5, TimeSpan.FromMilliseconds(260)));
            }
            else
            {
                var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(220));
                fade.Completed += (s, e) => { if (_mode == Mode.Idle) Notch.Effect = null; };
                _shadow.BeginAnimation(DropShadowEffect.OpacityProperty, fade);
            }

            Body.IsHitTestVisible = open;
            var bodyAnim = new DoubleAnimation(open ? 1 : 0, TimeSpan.FromMilliseconds(open ? 220 : 90));
            if (open) { if (wasOpen) bodyAnim.From = 0; else bodyAnim.BeginTime = TimeSpan.FromMilliseconds(130); }
            Body.BeginAnimation(UIElement.OpacityProperty, bodyAnim);
        }

        void UpdateMini(Mode m)
        {
            bool idle = m == Mode.Idle;
            MiniArt.Visibility = idle && _hasMedia ? Visibility.Visible : Visibility.Collapsed;

            bool eq = idle && _hasMedia && _playing;
            if (eq == _eqOn) return;
            _eqOn = eq;
            Eq.Visibility = eq ? Visibility.Visible : Visibility.Collapsed;
            var bars = new[] { Sc1, Sc2, Sc3 };
            int[] ms = { 420, 300, 520 };
            for (int i = 0; i < bars.Length; i++)
            {
                if (eq)
                {
                    var a = new DoubleAnimation(0.25, 1.0, TimeSpan.FromMilliseconds(ms[i]))
                    {
                        AutoReverse = true,
                        RepeatBehavior = RepeatBehavior.Forever,
                        EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                    };
                    Timeline.SetDesiredFrameRate(a, 20); // эквалайзеру хватает 20 к/с — экономим процессор
                    bars[i].BeginAnimation(ScaleTransform.ScaleYProperty, a);
                }
                else bars[i].BeginAnimation(ScaleTransform.ScaleYProperty, null);
            }
        }

        // Раскрытие — не мгновенно, а с небольшой задержкой: курсор, пролетающий по верху экрана
        // к вкладкам браузера или кнопкам окна, не вызывает лишних раскрытий.
        void Notch_MouseEnter(object sender, MouseEventArgs e)
        {
            _collapse.Stop();
            if (_hover || _notifActive) { _hover = true; Refresh(); return; }
            _expand.Stop();
            _expand.Start();
        }

        void Notch_MouseLeave(object sender, MouseEventArgs e)
        {
            _expand.Stop();
            _collapse.Stop();
            _collapse.Start();
        }

        // Колесо мыши над РАСКРЫТОЙ челкой = громкость (штатные медиа-клавиши Windows).
        // В свёрнутом виде колесо не трогаем, чтобы не мешать прокрутке вкладок под ней.
        void Notch_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (_mode != Mode.Media && _mode != Mode.Info) return;
            byte vk = e.Delta > 0 ? (byte)0xAF : (byte)0xAE;
            for (int i = 0; i < 2; i++)
            {
                keybd_event(vk, 0, 0, UIntPtr.Zero);
                keybd_event(vk, 0, 2, UIntPtr.Zero);
            }
            e.Handled = true;
        }

        void OnTick()
        {
            bool fs = IsFullscreenAppActive();
            if (fs != _fullscreen) { _fullscreen = fs; Refresh(); }
            if (_mode == Mode.Info) UpdateInfo();
            if (_mode == Mode.Media) UpdateProgress();
        }

        void UpdateInfo()
        {
            var now = DateTime.Now;
            ClockText.Text = now.ToString("HH:mm");
            string date = now.ToString("dddd, d MMMM", Ru);
            DateText.Text = date.Length > 0 ? char.ToUpper(date[0], Ru) + date.Substring(1) : date;

            if (!HasBattery()) { BatteryPanel.Visibility = Visibility.Collapsed; return; }
            try
            {
                var ps = System.Windows.Forms.SystemInformation.PowerStatus;
                float p = ps.BatteryLifePercent;
                if (p < 0 || p > 1) { BatteryPanel.Visibility = Visibility.Collapsed; return; }
                BatteryPanel.Visibility = Visibility.Visible;
                int pct = (int)Math.Round(p * 100);
                bool plugged = ps.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online;
                BatteryText.Text = pct + "%" + (plugged ? " · заряжается" : "");
                BatFill.Width = Math.Max(2, 20.0 * pct / 100.0);
                BatFill.Background = new SolidColorBrush(plugged ? Color.FromRgb(0x30, 0xD1, 0x58)
                                                         : pct <= 20 ? Color.FromRgb(0xFF, 0x45, 0x3A) : Colors.White);
            }
            catch { BatteryPanel.Visibility = Visibility.Collapsed; }
        }

        // ───────────── Музыка (системные медиа-сессии Windows) ─────────────

        async Task InitMediaAsync()
        {
            var mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            mgr.CurrentSessionChanged += (s, a) => Dispatcher.BeginInvoke(new Action(AttachSession));
            _mgr = mgr;
            AttachSession();
        }

        void AttachSession()
        {
            if (_session != null)
            {
                _session.MediaPropertiesChanged -= OnMediaChanged;
                _session.PlaybackInfoChanged -= OnMediaChanged;
            }
            _session = _mgr.GetCurrentSession();
            _trackKey = null;
            if (_session != null)
            {
                _session.MediaPropertiesChanged += OnMediaChanged;
                _session.PlaybackInfoChanged += OnMediaChanged;
            }
            var ignore = RefreshMediaAsync();
        }

        void OnMediaChanged(GlobalSystemMediaTransportControlsSession s, object a)
        {
            Dispatcher.BeginInvoke(new Action(() => { var ignore = RefreshMediaAsync(); }));
        }

        async Task RefreshMediaAsync()
        {
            int gen = ++_gen; // защита от «гонки»: устаревший ответ не перезапишет свежий
            var s = _session;
            try
            {
                if (s == null)
                {
                    _hasMedia = false; _playing = false; _trackKey = null;
                    Refresh();
                    return;
                }

                var status = s.GetPlaybackInfo().PlaybackStatus;
                _playing = status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                var props = await s.TryGetMediaPropertiesAsync();
                if (gen != _gen) return;

                _hasMedia = !string.IsNullOrEmpty(props.Title)
                            && status != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed;
                TitleText.Text = props.Title ?? "";
                ArtistText.Text = props.Artist ?? "";
                PlayGlyph.Text = _playing ? "\uE769" : "\uE768";

                // Обложку перезагружаем только при смене трека (или если она ещё не успела прийти)
                string key = props.Title + "\n" + props.Artist + "\n" + props.AlbumTitle;
                if (key != _trackKey || _art == null)
                {
                    _trackKey = key;
                    BitmapImage art = null;
                    if (props.Thumbnail != null)
                    {
                        try
                        {
                            using (var st = await props.Thumbnail.OpenReadAsync())
                            using (var ms = new MemoryStream())
                            {
                                await st.AsStreamForRead().CopyToAsync(ms);
                                ms.Position = 0;
                                art = new BitmapImage();
                                art.BeginInit();
                                art.CacheOption = BitmapCacheOption.OnLoad;
                                art.StreamSource = ms;
                                art.EndInit();
                                art.Freeze();
                            }
                        }
                        catch { art = null; }
                    }
                    if (gen != _gen) return;
                    _art = art;
                    ArtBrush.ImageSource = art;
                    MiniArtBrush.ImageSource = art;
                    ApplyAccent(art);
                }

                Refresh();
                UpdateProgress();
            }
            catch (Exception ex) { App.Log(ex); }
        }

        // Цвет эквалайзера берём из обложки — маленький живой акцент в монохромном интерфейсе
        void ApplyAccent(BitmapSource art)
        {
            Color c = Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF);
            var a = art == null ? null : AccentFrom(art);
            if (a.HasValue) c = a.Value;
            var brush = new SolidColorBrush(c);
            foreach (var r in Eq.Children.OfType<Rectangle>()) r.Fill = brush;
        }

        static Color? AccentFrom(BitmapSource src)
        {
            try
            {
                if (src.PixelWidth < 1 || src.PixelHeight < 1) return null;
                var small = new TransformedBitmap(src, new ScaleTransform(16.0 / src.PixelWidth, 16.0 / src.PixelHeight));
                var conv = new FormatConvertedBitmap(small, PixelFormats.Bgra32, null, 0);
                int w = conv.PixelWidth, h = conv.PixelHeight;
                var px = new byte[w * h * 4];
                conv.CopyPixels(px, w * 4, 0);

                double r = 0, g = 0, b = 0, wt = 0;
                for (int i = 0; i + 3 < px.Length; i += 4)
                {
                    if (px[i + 3] < 128) continue;
                    double B = px[i], G = px[i + 1], R = px[i + 2];
                    double mx = Math.Max(R, Math.Max(G, B)), mn = Math.Min(R, Math.Min(G, B));
                    double sat = mx <= 0 ? 0 : (mx - mn) / mx;
                    double weight = 0.1 + sat; // насыщенные пиксели важнее серых
                    r += R * weight; g += G * weight; b += B * weight; wt += weight;
                }
                if (wt <= 0) return null;
                r /= wt; g /= wt; b /= wt;
                double max = Math.Max(r, Math.Max(g, b));
                if (max < 190) // на чёрном фоне цвет должен быть заметным
                {
                    double k = 210.0 / Math.Max(max, 1);
                    r = Math.Min(255, r * k); g = Math.Min(255, g * k); b = Math.Min(255, b * k);
                }
                return Color.FromRgb((byte)r, (byte)g, (byte)b);
            }
            catch { return null; }
        }

        void UpdateProgress()
        {
            var s = _session;
            if (s == null || _scrubbing || DateTime.UtcNow < _progressHold) return;
            try
            {
                var tl = s.GetTimelineProperties();
                double start = tl.StartTime.TotalSeconds;
                double total = (tl.EndTime - tl.StartTime).TotalSeconds;
                double pos = tl.Position.TotalSeconds - start;
                if (_playing) pos += (DateTimeOffset.Now - tl.LastUpdatedTime).TotalSeconds;
                _total = total; _startSec = start;

                if (total <= 0) { Fill.Width = 0; TimeCur.Text = ""; TimeTotal.Text = ""; return; }
                pos = Math.Max(0, Math.Min(pos, total));
                TimeCur.Text = Fmt(pos);
                TimeTotal.Text = Fmt(total);
                Fill.Width = Math.Max(0, TrackLine.ActualWidth * pos / total);
            }
            catch { }
        }

        static string Fmt(double sec)
        {
            var t = TimeSpan.FromSeconds(sec);
            return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
        }

        // Полоска прогресса: клик или перетаскивание = перемотка. При наведении становится толще.
        double Ratio(MouseEventArgs e)
        {
            if (Track.ActualWidth <= 0) return 0;
            return Math.Max(0, Math.Min(1, e.GetPosition(Track).X / Track.ActualWidth));
        }

        void AnimTrack(double height)
        {
            TrackLine.BeginAnimation(FrameworkElement.HeightProperty, new DoubleAnimation(height, TimeSpan.FromMilliseconds(120)));
        }

        void Track_Enter(object sender, MouseEventArgs e) { AnimTrack(7); }
        void Track_Leave(object sender, MouseEventArgs e) { if (!_scrubbing) AnimTrack(4); }

        void Track_Down(object sender, MouseButtonEventArgs e)
        {
            if (_session == null || _total <= 0) return;
            _scrubbing = true;
            Track.CaptureMouse();
            Scrub(e);
            e.Handled = true;
        }

        void Track_Move(object sender, MouseEventArgs e) { if (_scrubbing) Scrub(e); }

        void Scrub(MouseEventArgs e)
        {
            double r = Ratio(e);
            Fill.Width = TrackLine.ActualWidth * r;
            TimeCur.Text = Fmt(r * _total);
        }

        async void Track_Up(object sender, MouseButtonEventArgs e)
        {
            if (!_scrubbing) return;
            double r = Ratio(e);
            _scrubbing = false;
            Track.ReleaseMouseCapture();
            AnimTrack(Track.IsMouseOver ? 7 : 4);
            if (!Notch.IsMouseOver) { _collapse.Stop(); _collapse.Start(); }

            var s = _session;
            if (s == null || _total <= 0) return;
            _progressHold = DateTime.UtcNow.AddMilliseconds(900); // пока плеер «догонит» новую позицию
            try { await s.TryChangePlaybackPositionAsync((long)((_startSec + r * _total) * TimeSpan.TicksPerSecond)); }
            catch { }
        }

        void Track_Lost(object sender, MouseEventArgs e) { _scrubbing = false; }

        async void Prev_Click(object sender, RoutedEventArgs e)
        {
            var s = _session; if (s == null) return;
            try { await s.TrySkipPreviousAsync(); } catch { }
        }

        async void Play_Click(object sender, RoutedEventArgs e)
        {
            var s = _session; if (s == null) return;
            try { await s.TryTogglePlayPauseAsync(); } catch { }
        }

        async void Next_Click(object sender, RoutedEventArgs e)
        {
            var s = _session; if (s == null) return;
            try { await s.TrySkipNextAsync(); } catch { }
        }

        // ───────────── Уведомления ─────────────

        void OnNotification(NotificationItem n)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!ShowNotifications) return;
                string name = n.AppName ?? "";
                NotifApp.Text = name;
                NotifTitle.Text = n.Title ?? "";
                NotifBody.Text = n.Body ?? "";
                NotifIcon.Source = n.Icon;
                NotifInitial.Text = n.Icon == null && name.Length > 0 ? name.Substring(0, 1).ToUpperInvariant() : "";
                NotifBadge.Background = new SolidColorBrush(n.Icon != null ? Color.FromRgb(0x2C, 0x2C, 0x2E) : BadgeColor(name));

                _notifActive = true;
                _notifTimer.Stop();
                _notifTimer.Start();
                Refresh();
            }));
        }

        void Notif_Click(object sender, MouseButtonEventArgs e)
        {
            _notifTimer.Stop();
            _notifActive = false;
            Refresh();
        }

        static Color BadgeColor(string name)
        {
            int h = 0;
            foreach (char c in name) h = (h * 31 + c) & 0x7FFFFFFF;
            return Palette[h % Palette.Length];
        }

        // ───────────── Полноэкранные игры/видео: челка прячется ─────────────

        static bool IsFullscreenAppActive()
        {
            try
            {
                IntPtr h = GetForegroundWindow();
                if (h == IntPtr.Zero) return false;

                var cls = new StringBuilder(64);
                GetClassName(h, cls, 64);
                string c = cls.ToString();
                if (c == "Progman" || c == "WorkerW" || c == "Shell_TrayWnd") return false;

                // нас интересует только основной монитор — там висит челка
                IntPtr mon = MonitorFromWindow(h, 2 /*NEAREST*/);
                IntPtr primary = MonitorFromPoint(new POINT { X = 0, Y = 0 }, 1 /*PRIMARY*/);
                if (mon != primary) return false;

                RECT r;
                if (!GetWindowRect(h, out r)) return false;
                var mi = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
                if (!GetMonitorInfo(mon, ref mi)) return false;

                bool covers = r.Left <= mi.rcMonitor.Left && r.Top <= mi.rcMonitor.Top &&
                              r.Right >= mi.rcMonitor.Right && r.Bottom >= mi.rcMonitor.Bottom;
                if (!covers) return false;

                // обычное окно с заголовком (например, при автоскрытии панели задач) — не полноэкранное
                const int WS_CAPTION = 0x00C00000;
                return (GetWindowLong(h, -16) & WS_CAPTION) != WS_CAPTION;
            }
            catch { return false; }
        }

        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", CharSet = CharSet.Auto)] static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int max);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
        [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr hMon, ref MONITORINFO mi);
        [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    }
}
