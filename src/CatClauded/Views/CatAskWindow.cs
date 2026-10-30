using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Shapes;
using Path = Avalonia.Controls.Shapes.Path;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using CatClauded.Core;
using CatClauded.Services;

namespace CatClauded.Views;

/// <summary>
/// Кот, который просит: сидит на экране с активным окном, над ним облачко — что разрешить и кнопки.
/// В очереди облачко только у переднего; колесо мыши меняет, кто впереди; кота можно утащить мышью.
/// </summary>
public sealed class CatAskWindow : Overlay
{
    private readonly StackPanel _root = new() { HorizontalAlignment = HorizontalAlignment.Right };
    private readonly Border _bubble;
    private readonly Path _tail;
    private readonly ScaleTransform _scale = new(1, 1);
    private readonly TranslateTransform _jump = new();
    private readonly Palette _p;
    private string _askKey = "";
    private double _jumpStart = -1;

    public CatInfo Cat { get; private set; }

    /// <summary>Разрешить (true) или заблокировать (false) просьбу AskId.</summary>
    public event Action<CatAskWindow, long, bool>? Decided;
    public event Action<CatAskWindow, long>? Always;
    public event Action<CatAskWindow>? OpenRequested;
    /// <summary>ПКМ → «Скрыть до новой сессии».</summary>
    public event Action<CatAskWindow>? HideRequested;
    public event Action<CatAskWindow, int>? Wheel;
    /// <summary>«Напомнить позже»: кот уходит на лежанку и вернётся через указанное время.</summary>
    public event Action<CatAskWindow, TimeSpan>? Snoozed;
    public new event Action<CatAskWindow>? Dragged;
    public new event Action<CatAskWindow>? Clicked;

    private sealed record Palette(IBrush Back, IBrush Stroke, IBrush Text, IBrush Secondary, IBrush Accent);

    public CatAskWindow(CatInfo cat)
    {
        Cat = cat;
        Title = "CatClauded — Claude просит";
        var dark = Application.Current?.ActualThemeVariant == ThemeVariant.Dark;
        RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        _p = dark
            ? new Palette(Brush("#2B2B30"), Brush("#48484F"), Brush("#F2F2F5"), Brush("#A8A8B3"), Brush("#7AB8FF"))
            : new Palette(Brush("#FBFBFD"), Brush("#D2D2DA"), Brush("#1B1B1F"), Brush("#5D5D68"), Brush("#0A64C8"));

        _bubble = new Border
        {
            MaxWidth = 380, MinWidth = 240, Padding = new Thickness(14, 10, 14, 12), CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1),
            Margin = new Thickness(10, 10, 10, 0), HorizontalAlignment = HorizontalAlignment.Right,
            Background = _p.Back, BorderBrush = _p.Stroke, BoxShadow = BoxShadows.Parse("0 2 12 0 #45000000"),
        };
        _bubble.SetValue(TextElement.ForegroundProperty, _p.Text);
        _tail = new Path
        {
            Data = Geometry.Parse("M 0,0 L 14,0 L 4,12 Z"), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, -1, 50, -2),
            Fill = _p.Back, Stroke = _p.Stroke, StrokeThickness = 1,
        };
        _root.Children.Add(_bubble);
        _root.Children.Add(_tail);
        _root.Children.Add(new CatView(cat.Coat, CatState.Asking)
        {
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 8, 0), Cursor = new Cursor(StandardCursorType.SizeAll),
            ContextMenu = CatMenu.For(() => OpenRequested?.Invoke(this), () => HideRequested?.Invoke(this)),
        });
        _root.Children.Add(NameTag($"{cat.Origin.Badge}  {cat.Title}", cat.Origin.Label));
        _root.RenderTransform = _jump;
        Content = new LayoutTransformControl { LayoutTransform = _scale, Child = _root };

        base.Dragged += () => Dragged?.Invoke(this);
        base.Clicked += _ => Clicked?.Invoke(this);
        PointerWheelChanged += (_, e) => { Wheel?.Invoke(this, e.Delta.Y > 0 ? 1 : -1); e.Handled = true; };
        Closed += (_, _) => Ticker.Remove(AnimateJump);
        Update(cat);
    }

    public bool ShowsBubble
    {
        get => _bubble.IsVisible;
        set => _bubble.IsVisible = _tail.IsVisible = value;
    }

    public void SetScale(double scale) => _scale.ScaleX = _scale.ScaleY = scale;

    /// <summary>Прыжок на экран: снизу с отскоком.</summary>
    public void PlayJump()
    {
        _jumpStart = Ticker.Now;
        Ticker.Add(AnimateJump);
    }

    private void AnimateJump(double now)
    {
        var t = (now - _jumpStart) / 0.6;
        if (t >= 1)
        {
            _jump.Y = 0;
            _root.Opacity = 1;
            Ticker.Remove(AnimateJump);
            return;
        }
        _jump.Y = 90 * (1 - Bounce(t));
        _root.Opacity = Math.Min(1, t * 3);
    }

    /// <summary>Отскок с двумя подпрыгиваниями (0 → 1).</summary>
    private static double Bounce(double t)
    {
        const double n = 7.5625, d = 2.75;
        if (t < 1 / d) return n * t * t;
        if (t < 2 / d) return n * (t -= 1.5 / d) * t + 0.75;
        if (t < 2.5 / d) return n * (t -= 2.25 / d) * t + 0.9375;
        return n * (t -= 2.625 / d) * t + 0.984375;
    }

    /// <summary>Тот же кот просит о новом — облачко перестраивается.</summary>
    public void Update(CatInfo cat)
    {
        Cat = cat;
        if (cat.Ask is not { } ask) return;
        var key = $"{ask.AskId}:{ask.Title}:{ask.Detail}";
        if (key == _askKey) return;
        _askKey = key;
        _bubble.Child = ask.IsQuestion ? Question(ask) : Approval(ask);
    }

    private Control Approval(CatAsk ask)
    {
        var panel = new StackPanel();
        panel.Children.Add(Caption("Claude Code просит разрешения · " + Cat.Origin.Label));
        panel.Children.Add(new TextBlock { Text = ask.Title, FontWeight = FontWeight.SemiBold, FontSize = 14, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
        if (ask.Detail.Length > 0)
        {
            var detail = new TextBlock
            {
                Text = Short(ask.Detail, 420), FontFamily = new FontFamily("Cascadia Mono, Consolas, DejaVu Sans Mono, monospace"), FontSize = 12, TextWrapping = TextWrapping.Wrap,
                MaxHeight = 110, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 6, 0, 0), Foreground = _p.Secondary,
            };
            ToolTip.SetTip(detail, ask.Detail);
            panel.Children.Add(detail);
        }
        var id = ask.AskId!.Value;
        var buttons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(Button("Разрешить", "accent", () => Decided?.Invoke(this, id, true)));
        buttons.Children.Add(Button("Заблокировать", null, () => Decided?.Invoke(this, id, false)));
        if (ask.CanAlways) buttons.Children.Add(Button("Всегда", null, () => Always?.Invoke(this, id), "Разрешить и больше не спрашивать о таком действии (правило предложил сам Claude)"));
        panel.Children.Add(buttons);
        panel.Children.Add(Footer());
        return panel;
    }

    private Control Question(CatAsk ask)
    {
        var panel = new StackPanel();
        panel.Children.Add(Caption("Claude Code · " + Cat.Origin.Label));
        var text = new TextBlock { Text = Short(ask.Title, 500), FontSize = 14, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
        ToolTip.SetTip(text, ask.Title);
        panel.Children.Add(text);
        panel.Children.Add(Footer());
        return panel;
    }

    /// <summary>Внизу облачка: «Открыть окно» и «Напомнить позже» (сейчас ничего не делать) с выбором срока.</summary>
    private Control Footer()
    {
        var panel = new StackPanel();
        var row = new DockPanel { LastChildFill = false };
        var later = Link("Напомнить позже ▾", "Сейчас ничего не делать — кот вернётся через выбранное время");
        DockPanel.SetDock(later, Dock.Right);
        var open = Link("Открыть сессию", "Открыть эту сессию: в десктопе Claude — её саму, у терминала — его окно");
        open.Margin = new Thickness(0, 8, 0, 0);
        open.PointerPressed += (_, e) => { e.Handled = true; OpenRequested?.Invoke(this); };
        row.Children.Add(open);
        row.Children.Add(later);
        panel.Children.Add(row);

        var choices = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, IsVisible = false, Margin = new Thickness(0, 2, 0, 0) };
        foreach (var (title, after) in new[] { ("5 мин", TimeSpan.FromMinutes(5)), ("15 мин", TimeSpan.FromMinutes(15)), ("1 ч", TimeSpan.FromHours(1)) })
        {
            var choice = Link(title, "Напомнить через " + title);
            choice.PointerPressed += (_, e) => { e.Handled = true; Snoozed?.Invoke(this, after); };
            choices.Children.Add(choice);
        }
        later.PointerPressed += (_, e) => { e.Handled = true; choices.IsVisible = !choices.IsVisible; };
        panel.Children.Add(choices);
        return panel;
    }

    private TextBlock Link(string text, string tip)
    {
        var link = new TextBlock { Text = text, FontSize = 12, Cursor = new Cursor(StandardCursorType.Hand), Margin = new Thickness(12, 8, 0, 0), TextDecorations = TextDecorations.Underline, Foreground = _p.Accent, Background = Brushes.Transparent };
        ToolTip.SetTip(link, tip);
        return link;
    }

    private TextBlock Caption(string text) => new() { Text = text, FontSize = 12, Foreground = _p.Secondary, TextWrapping = TextWrapping.Wrap };

    private static Button Button(string text, string? cls, Action click, string? tip = null)
    {
        var b = new Button { Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 300 }, Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(12, 5) };
        if (cls is not null) b.Classes.Add(cls);
        if (tip is not null) ToolTip.SetTip(b, tip);
        b.Click += (_, _) => click();
        return b;
    }

    private static Control NameTag(string title, string origin)
    {
        var tag = new Border
        {
            CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 2, 8, 3), Margin = new Thickness(0, 2, 0, 4), HorizontalAlignment = HorizontalAlignment.Right,
            MaxWidth = 220, Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x30, 0x30, 0x36)),
            Child = new TextBlock { Text = title, FontSize = 11, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis },
        };
        ToolTip.SetTip(tag, title + "\n" + origin);
        return tag;
    }

    private static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex));

    private static string Short(string s, int max)
    {
        s = s.Trim();
        return s.Length <= max ? s : s[..max] + "…";
    }
}
