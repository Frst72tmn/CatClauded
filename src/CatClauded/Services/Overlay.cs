using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace CatClauded.Services;

/// <summary>
/// Окно котов: прозрачное, без рамки, поверх всех, без фокуса и панели задач. Перетаскивается мышью;
/// щелчок без сдвига — Clicked с тем, по чему щёлкнули.
/// На Windows окно без фокуса не получает движения мыши за своим краем (захват мыши у него неполный),
/// поэтому там курсор и кнопка опрашиваются напрямую, пока кнопка зажата. На Linux — обычный захват указателя.
/// </summary>
public class Overlay : Window
{
    private const double DragThreshold = 4;
    private readonly DispatcherTimer _poll = new(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(10) };
    private Point? _grab;
    private PixelPoint _pressedAt;
    private PixelPoint _cursorAt;
    private bool _moved;
    private Visual? _pressedOn;

    /// <summary>Окно перетащили — оно уже на новом месте.</summary>
    public event Action? Dragged;

    /// <summary>Щелчок без перетаскивания; аргумент — элемент под мышью.</summary>
    public event Action<Visual?>? Clicked;

    /// <summary>Кнопка мыши зажата на окне — его сейчас тащат (или вот-вот щёлкнут); двигать его со стороны нельзя.</summary>
    public bool IsDragging => _grab is not null || _poll.IsEnabled;

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
        Closed += (_, _) => _poll.Stop();
        _poll.Tick += (_, _) => Poll();

        PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || IsDragging) return;
            _pressedAt = Position;
            _moved = false;
            _pressedOn = e.Source as Visual;
            if (CatClauded.Services.Screens.Cursor() is { } cursor)
            {
                _cursorAt = cursor;
                _poll.Start();
                return;
            }
            _grab = e.GetPosition(this);
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
            Finish();
        };
        PointerCaptureLost += (_, _) => _grab = null;
    }

    /// <summary>Windows: курсор и кнопка напрямую — работает и за краем окна, и когда окно не в фокусе.</summary>
    private void Poll()
    {
        if (!CatClauded.Services.Screens.IsLeftButtonDown())
        {
            _poll.Stop();
            Finish();
            return;
        }
        if (CatClauded.Services.Screens.Cursor() is not { } cursor) return;
        var delta = cursor - _cursorAt;
        if (!_moved && Math.Abs(delta.X) < DragThreshold * DesktopScaling && Math.Abs(delta.Y) < DragThreshold * DesktopScaling) return;
        _moved = true;
        var to = _pressedAt + delta;
        if (Position != to) Position = to;
    }

    private void Finish()
    {
        if (_moved && Position != _pressedAt) Dragged?.Invoke();
        else Clicked?.Invoke(_pressedOn);
    }

    /// <summary>Первый предок с Tag нужного типа (по нему понятно, по какому коту щёлкнули).</summary>
    public static T? TagOf<T>(Visual? v) where T : class
    {
        for (; v is not null; v = v.GetVisualParent())
            if (v is Control { Tag: T tag }) return tag;
        return null;
    }
}
