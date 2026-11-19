using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using DynamicIsland.Services;

namespace DynamicIsland;

// Moving the island: press and drag the pill anywhere; the position is remembered.
public partial class MainWindow
{
    private const double DragThreshold = 5;     // DIPs of movement before a press becomes a drag
    private const double SnapDistance = 14;     // pulls to the default top-centre spot when this close

    private bool _dragCandidate;                // left button is down on the pill
    private bool _dragging;                     // moved past the threshold: this is a drag, not a click
    private Point _dragStartMouse;
    private double _dragStartLeft, _dragStartTop;

    private void InitPosition()
    {
        SettingsService.Changed += ApplyPosition;                 // "Reset position" in Settings
        SystemEvents.DisplaySettingsChanged += OnDisplayChanged;  // resolution or monitor change
    }

    private (double Left, double Top) DefaultPosition() =>
        ((SystemParameters.PrimaryScreenWidth - Width) / 2, 0);

    // Saved spot if there is one, otherwise top-centre; always kept on a visible screen.
    private void ApplyPosition()
    {
        var s = SettingsService.Current;
        var (left, top) = s.IslandLeft is double l && s.IslandTop is double t ? (l, t) : DefaultPosition();
        (Left, Top) = Clamp(left, top);
    }

    private void OnDisplayChanged(object? sender, EventArgs e) => Dispatcher.InvokeAsync(ApplyPosition);

    // The window must stay fully on the desktop, or the pill could end up where it cannot be grabbed.
    private (double Left, double Top) Clamp(double left, double top)
    {
        double minL = SystemParameters.VirtualScreenLeft;
        double minT = SystemParameters.VirtualScreenTop;
        double maxL = minL + SystemParameters.VirtualScreenWidth - Width;
        double maxT = minT + SystemParameters.VirtualScreenHeight - Height;
        return (Math.Clamp(left, minL, Math.Max(minL, maxL)), Math.Clamp(top, minT, Math.Max(minT, maxT)));
    }

    // ----- Mouse ---------------------------------------------------------

    private void Pill_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Buttons, toggles and the like keep working: they must never start a drag.
        if (IsInteractive(e.OriginalSource as DependencyObject)) return;

        _dragCandidate = true;
        _dragging = false;
        _dragStartMouse = MouseOnScreen(e);
        _dragStartLeft = Left;
        _dragStartTop = Top;
        // The mouse is only captured once a real drag starts, so plain clicks on dots and the like still arrive.
    }

    private void Pill_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragCandidate) return;

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndDrag(save: _dragging); // button released somewhere we did not see
            return;
        }

        var mouse = MouseOnScreen(e);
        double dx = mouse.X - _dragStartMouse.X, dy = mouse.Y - _dragStartMouse.Y;

        if (!_dragging)
        {
            if (Math.Abs(dx) + Math.Abs(dy) < DragThreshold) return;
            _dragging = true;
            Pill.CaptureMouse();
            Pill.Cursor = Cursors.SizeAll;
        }

        double left = _dragStartLeft + dx, top = _dragStartTop + dy;

        // Magnetic default spot, so it is easy to put it back where it started.
        var home = DefaultPosition();
        if (Math.Abs(left - home.Left) < SnapDistance) left = home.Left;
        if (Math.Abs(top - home.Top) < SnapDistance) top = home.Top;

        (Left, Top) = Clamp(left, top);
    }

    // Returns true when the press ended a drag, so the caller must not also treat it as a click.
    private bool FinishPressAsDrag()
    {
        if (!_dragCandidate) return false;

        bool wasDrag = _dragging;
        EndDrag(save: wasDrag);
        return wasDrag;
    }

    private void EndDrag(bool save)
    {
        _dragCandidate = false;
        _dragging = false;
        Pill.ReleaseMouseCapture();
        Pill.ClearValue(CursorProperty);

        if (!save) return;

        var s = SettingsService.Current;
        s.IslandLeft = Left;
        s.IslandTop = Top;
        SettingsService.Save();
    }

    // Mouse position in screen DIPs. Computed from the live window position, so it stays correct while the window moves.
    private Point MouseOnScreen(MouseEventArgs e)
    {
        var pixels = PointToScreen(e.GetPosition(this));
        var toDips = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        return toDips.Transform(pixels);
    }

    private static bool IsInteractive(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is ButtonBase) return true;

            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }
        return false;
    }
}
