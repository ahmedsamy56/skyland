using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DynamicIsland.Services;

namespace DynamicIsland;

// Start-up animation: a dot at the top springs open into a pill, the logo pops in, then "SkyLand" rises in
// letter by letter. About 3 seconds, then the island settles into whatever it normally shows.
public partial class MainWindow
{
    private const string IntroName = "SkyLand";
    private static readonly (double W, double H, double R) DotSize = (12, 12, 6);

    private bool _intro;                         // the intro is waiting to play or playing
    private readonly List<TextBlock> _introLetters = new();

    // True when Windows animations are on. When they are off (accessibility / performance setting) the intro
    // still shows the logo and name, but static: no spring, no pop, no rising letters.
    private bool _introAnimated;

    // Called before the window is first shown: the intro always shows. With animations, it starts from the dot.
    private void PrepareIntro()
    {
        // Starting minimized: no intro, the island stays tucked away.
        if (SettingsService.Current.Minimized) return;

        _introAnimated = SystemParameters.ClientAreaAnimation;

        foreach (char c in IntroName)
        {
            var letter = new TextBlock
            {
                Text = c.ToString(),
                Foreground = NormalBrush,
                FontSize = 21,
                FontWeight = FontWeights.ExtraBold,
                FontStyle = FontStyles.Italic,
                Opacity = _introAnimated ? 0 : 1,
                RenderTransform = new TranslateTransform(0, _introAnimated ? 8 : 0)
            };
            _introLetters.Add(letter);
            IntroWord.Children.Add(letter);
        }

        _intro = true;
        if (_introAnimated)
        {
            Pill.Width = DotSize.W;
            Pill.Height = DotSize.H;
            PillRadius = DotSize.R;
            _size = DotSize;
        }
        else
        {
            // Straight to the standard pill with the finished logo and name.
            IntroLogo.Opacity = 1;
            IntroLogoScale.ScaleX = 1;
            IntroLogoScale.ScaleY = 1;
            Pill.Width = StandardSize.W;
            Pill.Height = StandardSize.H;
            PillRadius = StandardSize.R;
            _size = StandardSize;
        }
    }

    // Loaded: let the dot show for a moment, open the pill, hold, then hand over to the normal states.
    private async Task StartIntroAsync()
    {
        if (!_intro) return;

        if (_introAnimated) await Task.Delay(300);
        ApplyState();                                  // target is Intro: the pill opens and PlayIntro() runs
        await Task.Delay(3000);

        _intro = false;
        ApplyState();                                  // shrink into stats / media / agents as usual
    }

    // Called by ApplyState the moment the island enters the Intro state.
    private void PlayIntro()
    {
        if (!_introAnimated) return;   // already showing the finished logo and name

        // Logo: pops in with a little overshoot.
        IntroLogo.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300)) { BeginTime = TimeSpan.FromMilliseconds(250) });

        var pop = new DoubleAnimation(0.4, 1, TimeSpan.FromMilliseconds(550))
        {
            BeginTime = TimeSpan.FromMilliseconds(250),
            EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.9 }
        };
        IntroLogoScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        IntroLogoScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);

        // Letters: each rises and fades in a little after the previous one.
        for (int i = 0; i < _introLetters.Count; i++)
        {
            var begin = TimeSpan.FromMilliseconds(450 + i * 45);
            var letter = _introLetters[i];

            letter.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300)) { BeginTime = begin });

            ((TranslateTransform)letter.RenderTransform).BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(400))
                {
                    BeginTime = begin,
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                });
        }
    }
}
