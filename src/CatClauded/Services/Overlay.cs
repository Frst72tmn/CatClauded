using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace CatClauded.Services;

/// <summary>
/// Окно котов: прозрачное, без рамки, поверх всех, без фокуса и панели задач. Перетаскивается мышью
/// (своим кодом — одинаково на Windows и Linux); щелчок без сдвига — Clicked с тем, по чему щёлкнули.
/// </summary>
public class Overlay : Window
{
    private const double DragThreshold = 4;
    private Point? _grab;
    private PixelPoint _pressedAt;
    private bool _moved;
    private Visual? _pressedOn;

    /// <summary>Окно перетащили — оно уже на новом месте.</summary>
    public event Action? Dragged;

    /// <summary>Щелчок без перетаскивания; аргумент — элемент под мышью.</summary>
    public event Action<Visual?>? Clicked;

    public Overlay()
    {
        SystemDecorations = SystemDecorations.None;
        CanResize = false;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;
        Opened += (_, _) => CatClauded.Services.Screens.NoActivate(this);

        PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            _grab = e.GetPosition(this);
            _pressedAt = Position;
            _moved = false;
            _pressedOn = e.Source as Visual;
            e.Pointer.Capture(this);
        };
        PointerMoved += (_, e) =>
        {
            if (_grab is not { } grab) return;
            var delta = e.GetPosition(this) - grab;
            if (!_moved && Math.Abs(delta.X) < DragThreshold && Math.Abs(delta.Y) < DragThreshold) return;
            _moved = true;
            Position += new PixelPoint((int)Math.Round(delta.X * DesktopScaling), (int)Math.Round(delta.Y * DesktopScaling));
        };
        PointerReleased += (_, e) =>
        {
            if (_grab is null) return;
            _grab = null;
            e.Pointer.Capture(null);
            if (_moved && Position != _pressedAt) Dragged?.Invoke();
            else Clicked?.Invoke(_pressedOn);
        };
        PointerCaptureLost += (_, _) => _grab = null;
    }

    /// <summary>Первый предок с Tag нужного типа (по нему понятно, по какому коту щёлкнули).</summary>
    public static T? TagOf<T>(Visual? v) where T : class
    {
        for (; v is not null; v = v.GetVisualParent())
            if (v is Control { Tag: T tag }) return tag;
        return null;
    }
}
