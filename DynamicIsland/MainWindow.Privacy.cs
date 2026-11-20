using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using DynamicIsland.Services;

namespace DynamicIsland;

// Camera and microphone in use. When an app starts using one, the island shows a 3-second pop-up with the app name
// ("Camera in use / Zoom / LIVE"). The pop-up then gives way to a small colored dot on the pill's top-right corner
// (green = camera, orange = microphone) that stays while the device is in use. Under an open card a short line says
// which app uses what. Detection reads Windows' own privacy records (PrivacyService): no audio or video is touched.
public partial class MainWindow
{
    private static readonly Brush CamGreen = Freeze(new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58)));
    private static readonly Brush MicOrange = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0x9F, 0x0A)));
    private static readonly Brush PrivacyDim = Freeze(new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0xA3)));

    private static readonly TimeSpan PrivacyPollTime = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PrivacyPopupTime = TimeSpan.FromSeconds(3);

    // 24 x 24 outlines, the same shapes as in the design preview.
    private static readonly Geometry CameraIcon = Freeze(Geometry.Parse(
        "M5,6.5 H13 A2.5,2.5 0 0 1 15.5,9 V15 A2.5,2.5 0 0 1 13,17.5 H5 A2.5,2.5 0 0 1 2.5,15 V9 A2.5,2.5 0 0 1 5,6.5 Z M15.5,10.5 L21.5,7 V17 L15.5,13.5 Z"));
    private static readonly Geometry MicIcon = Freeze(Geometry.Parse(
        "M12,2.5 A3,3 0 0 1 15,5.5 V11.5 A3,3 0 0 1 9,11.5 V5.5 A3,3 0 0 1 12,2.5 Z M5.5,11.5 A6.5,6.5 0 0 0 18.5,11.5 M12,18 V21.5"));

    private readonly DispatcherTimer _privacyTimer = new() { Interval = PrivacyPollTime };
    private readonly DispatcherTimer _privacyPopupTimer = new() { Interval = PrivacyPopupTime };
    private PrivacyUsage _usage = PrivacyUsage.None;
    private bool _usageKnown;                       // false until the first reading: what is already in use at start shows a dot, no pop-up
    private bool _privacyPolling;
    private bool _privacyPopup;                     // a pop-up is on screen (or waiting for its turn)
    private (bool Cam, bool Mic) _privacyShowing;   // what the pop-up on screen announces
    private bool _camPending, _micPending;          // announced by a pop-up that has not ended yet: its dot waits
    private bool _camDotOn, _micDotOn, _privacyStripOn, _dotsVisibleState = true;
    // One entry per pop-up waiting for its turn: microphone / camera in use, or a Bluetooth / USB device event.
    // Cam / Mic mark the privacy ones: they hold back their dot until the pop-up ends.
    private sealed record PopupItem(Geometry Icon, Brush Accent, string Title, string Sub, string Trail, bool Live, bool Cam = false, bool Mic = false, bool FillIcon = false);
    private readonly Queue<PopupItem> _privacyQueue = new();

    private void InitPrivacy()
    {
        _privacyTimer.Tick += async (_, _) => await PollPrivacyAsync();
        _privacyPopupTimer.Tick += (_, _) => EndPrivacyPopup();
    }

    // Starts or stops watching, following the setting.
    private void ApplyPrivacySetting()
    {
        if (SettingsService.Current.ShowPrivacyIndicator)
        {
            if (_privacyTimer.IsEnabled) return;
            _privacyTimer.Start();
            _ = PollPrivacyAsync();
        }
        else if (_privacyTimer.IsEnabled || _usage != PrivacyUsage.None || _privacyPopup)
        {
            _privacyTimer.Stop();
            _privacyPopupTimer.Stop();
            _privacyQueue.Clear();
            _usage = PrivacyUsage.None;
            _usageKnown = false;
            _privacyPopup = false;
            _privacyShowing = (false, false);
            _camPending = _micPending = false;
            ApplyState();
        }
    }

    private async Task PollPrivacyAsync()
    {
        if (_privacyPolling) return;
        _privacyPolling = true;
        try
        {
            // The registry read is tiny, but keep it off the UI thread anyway.
            var usage = await Task.Run(PrivacyService.Read);
            OnPrivacyUsage(usage);
        }
        finally
        {
            _privacyPolling = false;
        }
    }

    private static bool Same(PrivacyUsage a, PrivacyUsage b) =>
        a.CameraApps.SequenceEqual(b.CameraApps) && a.MicApps.SequenceEqual(b.MicApps);

    private void OnPrivacyUsage(PrivacyUsage usage)
    {
        if (!SettingsService.Current.ShowPrivacyIndicator) return;

        bool newCam = usage.CameraActive && !_usage.CameraActive;
        bool newMic = usage.MicActive && !_usage.MicActive;
        bool first = !_usageKnown;
        bool changed = !Same(usage, _usage);
        _usageKnown = true;
        _usage = usage;

        if (!usage.CameraActive) _camPending = false;
        if (!usage.MicActive) _micPending = false;

        // Something just started: announce it. (Already running when SkyLand started: just the dot.)
        if ((newCam || newMic) && !first)
        {
            var apps = new List<string>();
            if (newCam) apps.AddRange(usage.CameraApps);
            if (newMic) apps.AddRange(usage.MicApps);
            string who = string.Join(", ", apps.Distinct(StringComparer.OrdinalIgnoreCase).Take(2));

            _camPending |= newCam;
            _micPending |= newMic;
            bool micOnly = newMic && !newCam;
            _privacyQueue.Enqueue(new PopupItem(micOnly ? MicIcon : CameraIcon, micOnly ? MicOrange : CamGreen,
                "", who.Length > 0 ? who : "An app", "LIVE", Live: true, Cam: newCam, Mic: newMic));
            if (!_privacyPopup) ShowNextPrivacyPopup();
            else ApplyState();   // the dots must wait for their pop-up
            return;
        }

        if (changed) ApplyState();
    }

    private void ShowNextPrivacyPopup()
    {
        while (_privacyQueue.Count > 0)
        {
            var item = _privacyQueue.Dequeue();
            string title = item.Title;
            Geometry icon = item.Icon;
            Brush accent = item.Accent;

            if (item.Cam || item.Mic)
            {
                bool cam = item.Cam && _usage.CameraActive;   // it may have stopped while waiting
                bool mic = item.Mic && _usage.MicActive;
                if (!cam && !mic) continue;

                _privacyShowing = (cam, mic);
                bool micOnly = mic && !cam;
                icon = micOnly ? MicIcon : CameraIcon;
                accent = micOnly ? MicOrange : CamGreen;
                title = cam && mic ? "Camera and microphone" : cam ? "Camera in use" : "Microphone in use";
            }
            else
            {
                _privacyShowing = (false, false);
            }

            PrivacyIcon.Data = icon;
            PrivacyIcon.Stroke = accent;
            PrivacyIcon.Fill = item.FillIcon ? accent : null;   // the charging bolt is solid, the other icons are outlines
            PrivacyTitle.Text = title;
            PrivacySub.Text = item.Sub;

            // "LIVE" with a pulsing dot for microphone / camera; a plain value (battery level, drive size) for devices.
            PrivacyLiveDot.Visibility = item.Live ? Visibility.Visible : Visibility.Collapsed;
            PrivacyLiveDot.Fill = accent;
            PrivacyLive.Text = item.Trail;
            PrivacyLive.Foreground = item.Live ? accent : NormalBrush;
            PrivacyLive.Margin = new Thickness(item.Live ? 6 : 0, 0, 0, 0);

            _privacyPopup = true;
            _privacyPopupTimer.Stop();
            _privacyPopupTimer.Start();
            ApplyState();
            return;
        }

        _privacyPopup = false;
        ApplyState();
    }

    // The pop-up's time is up: its dots may land, and the next waiting pop-up (if any) takes its turn.
    private void EndPrivacyPopup()
    {
        _privacyPopupTimer.Stop();
        if (_privacyShowing.Cam) _camPending = false;
        if (_privacyShowing.Mic) _micPending = false;
        _privacyShowing = (false, false);
        _privacyPopup = false;
        ShowNextPrivacyPopup();
    }

    // ----- Dots (inside the pill, top-right) ------------------------------

    // The collapsed island grows a little to give the dots their own spot on the right: its content keeps its place.
    private double CollapsedDotExtraWidth()
    {
        int dots = (_usage.CameraActive && !_camPending ? 1 : 0) + (_usage.MicActive && !_micPending ? 1 : 0);
        return dots == 0 ? 0 : 24 + (dots - 1) * 16;
    }

    private void UpdatePrivacyDots()
    {
        // Vertically centred on the right edge, in their own spot beside the content. Only while the island is
        // collapsed (idle island or capsule): an open card, a pop-up or the intro hides them (the line under the card
        // still says what is in use).
        bool collapsed = _state is IslandState.Idle or IslandState.Capsule;
        if (collapsed != _dotsVisibleState)
        {
            _dotsVisibleState = collapsed;
            FadeView(PrivacyDots, collapsed);
        }

        SetDot(CamDot, CamDotScale, CamDotPing, CamPingScale, _usage.CameraActive && !_camPending, ref _camDotOn);
        SetDot(MicDot, MicDotScale, MicDotPing, MicPingScale, _usage.MicActive && !_micPending, ref _micDotOn);
    }

    // A dot springs in when it appears and shrinks away when the device is released. The slow pulse ring
    // is a looping animation, so it exists only while the dot does.
    private static void SetDot(Grid dot, ScaleTransform scale, Ellipse ping, ScaleTransform pingScale, bool on, ref bool state)
    {
        if (on == state) return;
        state = on;

        if (on)
        {
            dot.Visibility = Visibility.Visible;
            var spring = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(420))
            {
                EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.8 }
            };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, spring);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, spring);

            var period = TimeSpan.FromMilliseconds(2400);
            var grow = new DoubleAnimation(0.7, 1.7, period) { RepeatBehavior = RepeatBehavior.Forever };
            var fade = new DoubleAnimation(0.7, 0, period) { RepeatBehavior = RepeatBehavior.Forever };
            Timeline.SetDesiredFrameRate(grow, LoopFrameRate);
            Timeline.SetDesiredFrameRate(fade, LoopFrameRate);
            pingScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            pingScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            ping.BeginAnimation(OpacityProperty, fade);
            return;
        }

        var shrink = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(180)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        shrink.Completed += (_, _) =>
        {
            // It may have come back in the meantime (a new spring started): only collapse if still shrunk.
            if (scale.ScaleX > 0.05) return;
            dot.Visibility = Visibility.Collapsed;
            ping.BeginAnimation(OpacityProperty, null);
            pingScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            pingScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
    }

    // ----- Strip under an open card --------------------------------------

    private void UpdatePrivacyStrip()
    {
        bool want = _state == IslandState.Card && (_usage.CameraActive || _usage.MicActive);

        if (want)
        {
            var text = PrivacyStripText.Inlines;
            text.Clear();
            void Add(Brush color, string device, IReadOnlyList<string> apps)
            {
                if (apps.Count == 0) return;
                if (text.Count > 0) text.Add(new Run("     "));
                text.Add(new Run("● ") { Foreground = color });
                text.Add(new Run(device));
                text.Add(new Run("  ·  " + string.Join(", ", apps.Take(3))) { Foreground = PrivacyDim, FontWeight = FontWeights.Normal });
            }
            Add(CamGreen, "Camera", _usage.CameraApps);
            Add(MicOrange, "Microphone", _usage.MicApps);

            if (_privacyStripOn) return;
            _privacyStripOn = true;
            PrivacyStrip.Visibility = Visibility.Visible;
            PrivacyStrip.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(220))
            {
                BeginTime = TimeSpan.FromMilliseconds(250)   // after the card has opened
            });
        }
        else if (_privacyStripOn)
        {
            _privacyStripOn = false;
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(120));
            fade.Completed += (_, _) =>
            {
                if (!_privacyStripOn) PrivacyStrip.Visibility = Visibility.Collapsed;
            };
            PrivacyStrip.BeginAnimation(OpacityProperty, fade);
        }
    }
}
