using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Path = Avalonia.Controls.Shapes.Path;
using Avalonia.Media;
using Avalonia.Threading;
using CatClauded.Core;

namespace CatClauded.Views;

/// <summary>
/// Кот сессии, нарисованный векторами: лежит (спит, работает, потягивается) или сидит (просит разрешения).
/// Окрас — по номеру сессии, чтобы одна и та же сессия всегда была узнаваема. Анимации — от общего таймера (Ticker).
/// </summary>
public sealed class CatView : Canvas
{
    private sealed record Coat(Color Fur, Color Dark, Color Light, Color Eyes, Color? Patch = null, bool Stripes = false, bool Points = false);

    private static readonly Coat[] Coats =
    [
        new(C("#F0A35E"), C("#C46D2C"), C("#FBDDBE"), C("#7CB66B"), Stripes: true),   // рыжий
        new(C("#A9B1BA"), C("#6C7580"), C("#E2E6EA"), C("#E2C24F"), Stripes: true),   // серый полосатый
        new(C("#34353B"), C("#1C1D21"), C("#5A5B63"), C("#E7C14A")),                  // чёрный
        new(C("#F5ECDF"), C("#C9B9A2"), C("#FFFFFF"), C("#6FA8DC")),                  // белый
        new(C("#F7F2EA"), C("#3A3A3F"), C("#FFFFFF"), C("#7CB66B"), Patch: C("#E9964F")), // трёхцветный
        new(C("#EBDFCB"), C("#6B4E3D"), C("#F7F0E4"), C("#5B9BD5"), Points: true),    // сиамский
    ];

    public const double LyingWidth = 110, LyingHeight = 72, SittingWidth = 84, SittingHeight = 104;

    private readonly List<Action<double>> _animations = [];
    private readonly double _born = Ticker.Now;

    public CatView(int coat, CatState state, bool animate = true)
    {
        var c = Coats[((coat % Coats.Length) + Coats.Length) % Coats.Length];
        if (state == CatState.Asking) DrawSitting(c);
        else DrawLying(c, state);
        if (!animate) _animations.Clear();
        AttachedToVisualTree += (_, _) => { if (_animations.Count > 0) Ticker.Add(Animate); };
        DetachedFromVisualTree += (_, _) => Ticker.Remove(Animate);
    }

    private void Animate(double now)
    {
        var t = now - _born;
        foreach (var a in _animations) a(t);
    }

    // ───────────── Лежит ─────────────

    private void DrawLying(Coat c, CatState state)
    {
        Width = LyingWidth;
        Height = LyingHeight;
        var sleeping = state == CatState.Sleeping;

        // Хвост обёрнут вокруг тела; у работающего кота кончик ходит.
        var tail = Stroke("M 26,58 C 6,60 2,44 12,36", c.Points ? c.Dark : c.Fur, 9);
        Add(tail);
        if (state == CatState.Working) Swing(tail, 12, 6, 58, 0.6);

        var body = new Ellipse { Width = 74, Height = 40, Fill = new SolidColorBrush(c.Fur), Stroke = new SolidColorBrush(c.Dark), StrokeThickness = 1.2 };
        Place(body, 14, 26);
        if (sleeping) Breathe(body, 51 - 14, 66 - 26);
        if (state == CatState.Finished) Stretch(body, 51 - 14, 66 - 26);
        if (c.Stripes)
            foreach (var x in new[] { 34.0, 46, 58 })
                Add(Stroke($"M {x},28 Q {x - 4},36 {x},44", c.Dark, 3, 0.55));
        if (c.Patch is { } patch)
        {
            Place(new Ellipse { Width = 22, Height = 16, Fill = new SolidColorBrush(patch) }, 26, 30);
            Place(new Ellipse { Width = 16, Height = 12, Fill = new SolidColorBrush(c.Dark) }, 48, 44);
        }
        Place(new Ellipse { Width = 34, Height = 14, Fill = new SolidColorBrush(c.Light), Opacity = 0.9 }, 44, 50);

        // Лапы спереди.
        foreach (var x in new[] { 62.0, 76 })
            Place(new Ellipse { Width = 14, Height = 9, Fill = new SolidColorBrush(c.Points ? c.Dark : c.Light), Stroke = new SolidColorBrush(c.Dark), StrokeThickness = 0.8 }, x, 59);

        Head(c, 58, 10, eyesOpen: !sleeping, ears: state == CatState.Working);

        if (sleeping) Zzz(98, 8);
        if (state == CatState.Working) Dots(96, 6, c.Dark);
    }

    // ───────────── Сидит и просит ─────────────

    private void DrawSitting(Coat c)
    {
        Width = SittingWidth;
        Height = SittingHeight;
        var tail = Stroke("M 60,96 C 82,94 84,74 74,62", c.Points ? c.Dark : c.Fur, 9);
        Add(tail);
        Swing(tail, 8, 60, 96, 1.4);

        var body = new Ellipse { Width = 54, Height = 60, Fill = new SolidColorBrush(c.Fur), Stroke = new SolidColorBrush(c.Dark), StrokeThickness = 1.2 };
        Place(body, 14, 40);
        if (c.Stripes)
            foreach (var y in new[] { 56.0, 68, 80 })
                Add(Stroke($"M 18,{y} Q 24,{y - 3} 28,{y}", c.Dark, 3, 0.55));
        if (c.Patch is { } patch) Place(new Ellipse { Width = 18, Height = 22, Fill = new SolidColorBrush(patch) }, 46, 56);
        Place(new Ellipse { Width = 26, Height = 34, Fill = new SolidColorBrush(c.Light), Opacity = 0.9 }, 28, 58);
        foreach (var x in new[] { 24.0, 44 })
            Place(new Ellipse { Width = 15, Height = 10, Fill = new SolidColorBrush(c.Points ? c.Dark : c.Light), Stroke = new SolidColorBrush(c.Dark), StrokeThickness = 0.8 }, x, 92);

        Head(c, 12, 0, eyesOpen: true, ears: true);
    }

    // ───────────── Голова ─────────────

    private void Head(Coat c, double x, double y, bool eyesOpen, bool ears)
    {
        var fur = new SolidColorBrush(c.Fur);
        var dark = new SolidColorBrush(c.Dark);
        var earFill = c.Points ? dark : fur;
        var left = Shape($"M {x + 6},{y + 22} L {x + 10},{y + 2} L {x + 24},{y + 14} Z", earFill, dark);
        var right = Shape($"M {x + 36},{y + 14} L {x + 50},{y + 2} L {x + 54},{y + 22} Z", earFill, dark);
        Add(left);
        Add(right);
        Add(Shape($"M {x + 10},{y + 18} L {x + 12},{y + 7} L {x + 19},{y + 14} Z", new SolidColorBrush(C("#F2A7B0")), null, 0.8));
        Add(Shape($"M {x + 41},{y + 14} L {x + 48},{y + 7} L {x + 50},{y + 18} Z", new SolidColorBrush(C("#F2A7B0")), null, 0.8));
        if (ears) Twitch(right, x + 43, y + 16);

        Place(new Ellipse { Width = 60, Height = 46, Fill = fur, Stroke = dark, StrokeThickness = 1.2 }, x, y + 8);
        if (c.Points) Place(new Ellipse { Width = 30, Height = 24, Fill = dark, Opacity = 0.85 }, x + 15, y + 26);
        if (c.Patch is { } patch) Place(new Ellipse { Width = 20, Height = 18, Fill = new SolidColorBrush(patch) }, x + 6, y + 12);
        Place(new Ellipse { Width = 26, Height = 16, Fill = new SolidColorBrush(c.Light), Opacity = 0.9 }, x + 17, y + 34);

        if (eyesOpen)
            foreach (var ex in new[] { x + 14, x + 34 })
            {
                Place(new Ellipse { Width = 12, Height = 13, Fill = new SolidColorBrush(c.Eyes), Stroke = dark, StrokeThickness = 0.8 }, ex, y + 22);
                var pupil = new Ellipse { Width = 4, Height = 10, Fill = Brushes.Black };
                Place(pupil, ex + 4, y + 23.5);
                Place(new Ellipse { Width = 3, Height = 3, Fill = Brushes.White }, ex + 6.5, y + 24.5);
                Blink(pupil);
            }
        else
            foreach (var ex in new[] { x + 14, x + 34 })
                Add(Stroke($"M {ex},{y + 28} Q {ex + 6},{y + 33} {ex + 12},{y + 28}", c.Dark, 1.8));

        Add(Shape($"M {x + 27},{y + 36} L {x + 33},{y + 36} L {x + 30},{y + 40} Z", new SolidColorBrush(C("#E57F8D")), null));
        Add(Stroke($"M {x + 30},{y + 40} Q {x + 27},{y + 44} {x + 24},{y + 42} M {x + 30},{y + 40} Q {x + 33},{y + 44} {x + 36},{y + 42}", c.Dark, 1.2));
        foreach (var dy in new[] { 37.0, 41 })
        {
            Add(Stroke($"M {x + 20},{y + dy} L {x - 2},{y + dy - 2}", c.Dark, 0.8, 0.6));
            Add(Stroke($"M {x + 40},{y + dy} L {x + 62},{y + dy - 2}", c.Dark, 0.8, 0.6));
        }
    }

    // ───────────── Анимации (t — секунды с рождения кота) ─────────────

    /// <summary>Туда и обратно по синусу: from → to за seconds, и назад.</summary>
    private static double Loop(double t, double from, double to, double seconds) => from + (to - from) * (1 - Math.Cos(Math.PI * t / seconds)) / 2;

    /// <summary>Ключевые кадры (время, значение) с линейными переходами; вне — крайние значения.</summary>
    private static double Keys(double t, params (double At, double Value)[] keys)
    {
        if (t <= keys[0].At) return keys[0].Value;
        for (var i = 1; i < keys.Length; i++)
            if (t <= keys[i].At)
                return keys[i - 1].Value + (keys[i].Value - keys[i - 1].Value) * (t - keys[i - 1].At) / (keys[i].At - keys[i - 1].At);
        return keys[^1].Value;
    }

    /// <summary>Точка (x, y) в координатах самого элемента — центр его поворота или масштаба.</summary>
    private static void Origin(Control e, double x, double y) => e.RenderTransformOrigin = new RelativePoint(x, y, RelativeUnit.Absolute);

    private void Breathe(Control e, double cx, double cy)
    {
        var s = new ScaleTransform(1, 1);
        e.RenderTransform = s;
        Origin(e, cx, cy);
        _animations.Add(t => s.ScaleY = Loop(t, 1, 1.06, 1.8));
    }

    private void Stretch(Control e, double cx, double cy)
    {
        var s = new ScaleTransform(1, 1);
        e.RenderTransform = s;
        Origin(e, cx, cy);
        _animations.Add(t => s.ScaleX = t < 2.8 ? Loop(t, 1, 1.12, 0.7) : 1);
    }

    private void Swing(Control e, double angle, double cx, double cy, double seconds)
    {
        var r = new RotateTransform(0);
        e.RenderTransform = r;
        Origin(e, cx, cy);
        _animations.Add(t => r.Angle = Loop(t, -angle, angle, seconds));
    }

    private void Twitch(Control e, double cx, double cy)
    {
        var r = new RotateTransform(0);
        e.RenderTransform = r;
        Origin(e, cx, cy);
        _animations.Add(t => r.Angle = Keys(t % 4, (3.2, 0), (3.35, 14), (3.5, 0)));
    }

    private void Blink(Control pupil)
    {
        var s = new ScaleTransform(1, 1);
        pupil.RenderTransform = s;
        Origin(pupil, 2, 5);
        _animations.Add(t => s.ScaleY = Keys(t % 5, (4.7, 1), (4.8, 0.1), (4.95, 1)));
    }

    private void Zzz(double x, double y)
    {
        for (var i = 0; i < 3; i++)
        {
            var z = new TextBlock { Text = "z", FontSize = 9 + i * 3, FontWeight = FontWeight.Bold, Foreground = new SolidColorBrush(C("#8A94A6")), Opacity = 0 };
            var move = new TranslateTransform();
            z.RenderTransform = move;
            Place(z, x - i * 4, y + 14);
            var begin = i * 0.9;
            _animations.Add(t =>
            {
                if (t < begin) return;
                var p = (t - begin) % 2.7 / 2.7;
                move.Y = -16 * p;
                z.Opacity = p < 0.3 ? p / 0.3 : (1 - p) / 0.7;
            });
        }
    }

    /// <summary>«Печатает»: три точки по очереди.</summary>
    private void Dots(double x, double y, Color color)
    {
        for (var i = 0; i < 3; i++)
        {
            var dot = new Ellipse { Width = 4, Height = 4, Fill = new SolidColorBrush(color), Opacity = 0.2 };
            Place(dot, x + i * 6, y);
            var begin = i * 0.2;
            _animations.Add(t => dot.Opacity = t < begin ? 0.2 : Loop(t - begin, 0.2, 1, 0.45));
        }
    }

    // ───────────── Фигуры ─────────────

    private void Add(Control e) => Children.Add(e);

    private void Place(Control e, double x, double y)
    {
        SetLeft(e, x);
        SetTop(e, y);
        Children.Add(e);
    }

    private static Path Shape(string data, IBrush fill, IBrush? stroke, double opacity = 1) =>
        new() { Data = Geometry.Parse(data), Fill = fill, Stroke = stroke, StrokeThickness = stroke is null ? 0 : 1.1, Opacity = opacity, StrokeJoin = PenLineJoin.Round };

    private static Path Stroke(string data, Color color, double thickness, double opacity = 1) =>
        new() { Data = Geometry.Parse(data), Stroke = new SolidColorBrush(color), StrokeThickness = thickness, Opacity = opacity, StrokeLineCap = PenLineCap.Round };

    private static Color C(string hex) => Color.Parse(hex);
}

/// <summary>Общий таймер анимаций котов (~30 кадров в секунду): работает, только пока есть кого анимировать.</summary>
public static class Ticker
{
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly List<Action<double>> Subscribers = [];
    private static readonly DispatcherTimer Timer = new(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) => Tick());

    public static double Now => Clock.Elapsed.TotalSeconds;

    public static void Add(Action<double> a)
    {
        Subscribers.Add(a);
        if (!Timer.IsEnabled) Timer.Start();
        a(Now);
    }

    public static void Remove(Action<double> a)
    {
        Subscribers.Remove(a);
        if (Subscribers.Count == 0) Timer.Stop();
    }

    private static void Tick()
    {
        var now = Now;
        foreach (var a in Subscribers.ToList()) a(now);
    }
}
