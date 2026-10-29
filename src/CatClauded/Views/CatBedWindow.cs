using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using CatClauded.Core;
using CatClauded.Services;

namespace CatClauded.Views;

/// <summary>
/// Лежанка поверх всех окон: здесь спят, работают и отдыхают коты всех открытых сессий Claude Code.
/// Перетаскивается мышью; щелчок по коту поднимает окно его сессии.
/// </summary>
public sealed class CatBedWindow : Overlay
{
    private readonly StackPanel _cats = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(10, 0, 18, 10) };
    private readonly ScaleTransform _scale = new(1, 1);
    private string _shown = "";

    /// <summary>Лежанку перетащили — новое место (левый верхний угол, пиксели экрана).</summary>
    public event Action<PixelPoint>? Moved;

    /// <summary>Щелчок по коту.</summary>
    public event Action<CatInfo>? CatClicked;

    public CatBedWindow()
    {
        Title = "CatClauded — лежанка";
        var cushion = new Border
        {
            Height = 30, CornerRadius = new CornerRadius(15), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(6, 0, 6, 6),
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.FromRgb(0x9B, 0x7B, 0xC4), 0), new GradientStop(Color.FromRgb(0x6E, 0x55, 0x96), 1) },
            },
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x57, 0x42, 0x7A)), BorderThickness = new Thickness(1.2),
            BoxShadow = BoxShadows.Parse("0 2 8 0 #50000000"),
            Cursor = new Cursor(StandardCursorType.SizeAll),
        };
        ToolTip.SetTip(cushion, "Лежанка котов Claude Code — можно перетащить");
        var root = new Grid { MinWidth = 140 };
        root.Children.Add(cushion);
        root.Children.Add(_cats);
        Content = new LayoutTransformControl { LayoutTransform = _scale, Child = root };
        Dragged += () => Moved?.Invoke(Position);
        Clicked += v =>
        {
            if (TagOf<CatInfo>(v) is { Id: > 0 } cat) CatClicked?.Invoke(cat);
        };
    }

    public void SetCats(IReadOnlyList<CatInfo> cats, double scale)
    {
        _scale.ScaleX = _scale.ScaleY = scale;
        // Перерисовываем, только если коты поменялись: иначе анимации начинались бы заново на каждом обновлении.
        var key = scale + "|" + string.Join(";", cats.Select(c => $"{c.Id}:{c.State}:{c.Coat}:{c.Title}:{c.Origin.Badge}"));
        if (key == _shown) return;
        _shown = key;
        _cats.Children.Clear();
        if (cats.Count == 0)
        {
            var empty = new Grid { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(-2, 0) };
            empty.Children.Add(new CatView(1, CatState.Sleeping));
            ToolTip.SetTip(empty, "Сессий Claude Code нет — коты спят");
            _cats.Children.Add(empty);
            return;
        }
        foreach (var c in cats) _cats.Children.Add(Cat(c));
    }

    private static Control Cat(CatInfo c)
    {
        var grid = new Grid { Tag = c, Cursor = new Cursor(StandardCursorType.Hand), Margin = new Thickness(-2, 0), VerticalAlignment = VerticalAlignment.Bottom, Background = Brushes.Transparent };
        ToolTip.SetTip(grid, Tip(c));
        grid.Children.Add(new CatView(c.Coat, c.State));
        // Значок, откуда кот: «▣» десктоп, «›_» терминал, 🐧 Linux/WSL.
        grid.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(6), Padding = new Thickness(4, 0, 4, 1), Margin = new Thickness(2, 0, 0, 4),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom,
            Background = new SolidColorBrush(Color.FromArgb(0xD0, 0x30, 0x30, 0x36)),
            Child = new TextBlock { Text = c.Origin.Badge, FontSize = 10, Foreground = Brushes.White },
        });
        if (c.State == CatState.Asking)
        {
            // Просит, но прыгать не стал (прыжки выключены, «напомнить позже», полноэкранное окно) — значок «!».
            grid.Children.Add(new Border
            {
                Width = 20, Height = 20, CornerRadius = new CornerRadius(10), Background = new SolidColorBrush(Color.FromRgb(0xF0, 0x8A, 0x24)),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 2, 0),
                Child = new TextBlock { Text = "!", Foreground = Brushes.White, FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            });
        }
        return grid;
    }

    private static string Tip(CatInfo c) => c.State switch
    {
        CatState.Asking => $"{c.Title}\n{c.Origin.Label} — ждёт вас, щелчок откроет окно",
        CatState.Working => $"{c.Title}\n{c.Origin.Label} — работает",
        CatState.Finished => $"{c.Title}\n{c.Origin.Label} — закончил",
        _ => $"{c.Title}\n{c.Origin.Label} — ждёт задачу",
    };
}
