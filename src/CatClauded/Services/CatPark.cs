using Avalonia;
using Avalonia.Threading;
using CatClauded.Core;
using CatClauded.Core.Platform;
using CatClauded.Views;

namespace CatClauded.Services;

/// <summary>
/// Котики сессий Claude Code. Лежанка с котами всех открытых сессий; кот, которому нужно разрешение или ответ,
/// прыгает на монитор с активным окном — если прыжки включены и окно не во весь экран. Несколько просящих —
/// очередь каскадом: облачко у переднего, колесо мыши меняет порядок, переднего можно перетащить вместе с очередью,
/// заднего — вытащить отдельно.
/// </summary>
public sealed class CatPark(ClaudeSessions sessions, SettingsStore settings, IProcessProbe? probe)
{
    private const int Gap = 20;
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly Dictionary<long, CatAskWindow> _out = [];
    private readonly List<long> _queue = [];
    private readonly HashSet<long> _detached = [];
    private readonly HashSet<string> _chimed = [];
    private readonly Dictionary<string, DateTimeOffset> _snoozed = [];
    private IReadOnlyList<CatInfo> _roster = [];
    private CatBedWindow? _bed;
    private (int?, int?, double) _bedPlace;
    private PixelPoint? _anchor;
    private int _ticks;

    private CatSettings Cats => settings.Current;

    public void Start()
    {
        _debounce.Tick += (_, _) => { _debounce.Stop(); Refresh(); };
        _tick.Tick += (_, _) => OnTick();
        sessions.Changed += () => Dispatcher.UIThread.Post(Later);
        settings.Changed += _ => Dispatcher.UIThread.Post(Later);
        Refresh();
        _tick.Start();
    }

    /// <summary>Вернуть лежанку на место по умолчанию.</summary>
    public void ResetBed() => settings.Update(s => { s.BedX = null; s.BedY = null; });

    private void Later()
    {
        _debounce.Stop();
        _debounce.Start();
    }

    private void OnTick()
    {
        Refresh();
        if (++_ticks % 5 != 0) return;
        if (_bed is { IsVisible: true } bed) Screens.BringTop(bed);
        foreach (var w in _out.Values) Screens.BringTop(w);
    }

    private void Refresh()
    {
        var c = Cats;
        _roster = c.Enabled ? sessions.Cats(c.Shows, c.CallWhenIdle) : [];
        Update();
    }

    private void Update()
    {
        if (!Cats.Enabled)
        {
            foreach (var w in _out.Values.ToList()) w.Close();
            _out.Clear();
            _queue.Clear();
            _detached.Clear();
            _bed?.Hide();
            return;
        }

        var jump = Cats.Jump && !Screens.ForegroundIsFullScreen();
        var now = DateTimeOffset.UtcNow;
        foreach (var key in _snoozed.Where(p => p.Value <= now).Select(p => p.Key).ToList()) _snoozed.Remove(key);
        var outCats = jump ? _roster.Where(c => c.State == CatState.Asking && !_snoozed.ContainsKey(AskKey(c))).ToList() : [];
        var outIds = outCats.Select(c => c.Id).ToHashSet();

        ShowBed(_roster.Where(c => !outIds.Contains(c.Id)).ToList());

        foreach (var gone in _out.Keys.Where(id => !outIds.Contains(id)).ToList())
        {
            _out[gone].Close();
            _out.Remove(gone);
            _queue.Remove(gone);
            _detached.Remove(gone);
        }
        foreach (var cat in outCats)
        {
            if (_out.TryGetValue(cat.Id, out var w))
            {
                w.Update(cat);
                continue;
            }
            // Первый в пустой очереди — встаёт в угол монитора с активным окном.
            if (_queue.Count == 0 && _detached.Count == 0) _anchor = null;
            _anchor ??= Corner();
            w = NewWindow(cat);
            _out[cat.Id] = w;
            _queue.Add(cat.Id);
            Chime(cat);
        }
        Arrange();
    }

    // ───────────── Лежанка ─────────────

    private void ShowBed(IReadOnlyList<CatInfo> cats)
    {
        if (_bed is null)
        {
            _bed = new CatBedWindow();
            _bed.Moved += p => settings.Update(s => { s.BedX = p.X; s.BedY = p.Y; });
            _bed.CatClicked += Open;
            _bed.SizeChanged += (_, _) => { if (Cats.BedX is null) PlaceBed(); };
        }
        _bed.SetCats(cats, Scale);
        if (!_bed.IsVisible) _bed.Show();
        else if (_bedPlace == (Cats.BedX, Cats.BedY, Scale)) return;
        // Первый показ или место поменялось в настройках («Вернуть лежанку на место»).
        _bedPlace = (Cats.BedX, Cats.BedY, Scale);
        PlaceBed();
    }

    private void PlaceBed()
    {
        var bed = _bed!;
        var size = Screens.SizeOf(bed);
        int x, y;
        if (Cats.BedX is { } bx && Cats.BedY is { } by)
        {
            var area = Screens.WorkAt(bed, new PixelPoint(bx + size.Width / 2, by + size.Height / 2));
            x = Math.Clamp(bx, area.X, Math.Max(area.X, area.Right - size.Width));
            y = Math.Clamp(by, area.Y, Math.Max(area.Y, area.Bottom - size.Height));
        }
        else
        {
            // По умолчанию — над панелью задач справа, левее очереди просящих.
            var area = bed.Screens.Primary?.WorkingArea ?? Screens.ActiveWork(bed);
            x = Math.Max(area.X, area.Right - size.Width - (int)(420 * bed.DesktopScaling));
            y = area.Bottom - size.Height;
        }
        bed.Position = new PixelPoint(x, y);
    }

    // ───────────── Просящие ─────────────

    private CatAskWindow NewWindow(CatInfo cat)
    {
        var w = new CatAskWindow(cat);
        w.SetScale(Scale);
        w.Decided += (win, id, allow) =>
        {
            Log.Write($"Кот: {(allow ? "разрешить" : "заблокировать")} #{id} → {(sessions.Decide(id, allow) ? "ушло в Claude" : "просьба уже снята")}");
            Refresh();
        };
        w.Always += (win, id) =>
        {
            Log.Write($"Кот: всегда #{id} → {(sessions.DecideAlways(id) ? "ушло в Claude" : "просьба уже снята")}");
            Refresh();
        };
        w.OpenRequested += win => Open(win.Cat);
        w.Dragged += OnDragged;
        w.Clicked += win => { if (_queue.Remove(win.Cat.Id)) _queue.Insert(0, win.Cat.Id); Arrange(); };
        w.Wheel += (_, delta) => Rotate(delta);
        w.Snoozed += (win, after) => Snooze(win.Cat, after);
        w.SizeChanged += (_, _) => Dispatcher.UIThread.Post(Arrange);
        if (_anchor is { } a) w.Position = new PixelPoint(a.X - 300, a.Y - 300);
        w.Show();
        w.PlayJump();
        return w;
    }

    /// <summary>
    /// «Напомнить позже»: кот сидит на лежанке со значком «!» и прыгает снова через выбранное время.
    /// Хук ждёт не дольше 55 минут — такой кот вернётся за минуту до этого.
    /// </summary>
    private void Snooze(CatInfo cat, TimeSpan after)
    {
        var until = DateTimeOffset.UtcNow + after;
        if (cat.Ask is { IsQuestion: false } ask && ask.AtUtc + Hook.MaxWait - TimeSpan.FromMinutes(1) is var deadline && deadline < until)
            until = deadline;
        _snoozed[AskKey(cat)] = until;
        _chimed.Remove(AskKey(cat)); // вернётся — позовёт звуком снова
        Update();
    }

    /// <summary>Щелчок по коту или «Открыть окно»: поднять окно терминала или десктопа Claude.</summary>
    private void Open(CatInfo cat)
    {
        if (cat.Ask is { IsQuestion: true }) sessions.Seen(cat.Id);
        var ok = probe is not null && sessions.OwnerOf(cat.Id) is { } owner && probe.Focus(owner);
        if (!ok) Log.Write($"Окно сессии «{cat.Title}» не поднять" + (OperatingSystem.IsLinux() ? " (нужен xdotool или wmctrl, X11)" : ""));
    }

    private static string AskKey(CatInfo c) => $"{c.Id}:{c.Ask?.AskId}:{c.Ask?.Title}";

    private void OnDragged(CatAskWindow w)
    {
        var id = w.Cat.Id;
        if (_detached.Contains(id)) return; // отдельный кот — где бросили, там и стоит
        if (_queue.Count > 0 && _queue[0] == id)
        {
            var size = Screens.SizeOf(w);
            _anchor = new PixelPoint(w.Position.X + size.Width, w.Position.Y + size.Height); // передний ведёт за собой очередь
        }
        else
        {
            _queue.Remove(id);
            _detached.Add(id); // задний — вытащили из очереди
        }
        Arrange();
    }

    private void Rotate(int delta)
    {
        if (_queue.Count < 2) return;
        if (delta < 0)
        {
            var first = _queue[0];
            _queue.RemoveAt(0);
            _queue.Add(first);
        }
        else
        {
            var last = _queue[^1];
            _queue.RemoveAt(_queue.Count - 1);
            _queue.Insert(0, last);
        }
        Arrange();
    }

    /// <summary>Очередь каскадом от угла: передний с облачком, остальные — левее и чуть выше, позади.</summary>
    private void Arrange()
    {
        foreach (var id in _detached)
            if (_out.TryGetValue(id, out var d)) { d.ShowsBubble = true; d.SetScale(Scale); }
        if (_queue.Count == 0 || _bed is null) return;

        var corner = _anchor ??= Corner();
        var inside = new PixelPoint(corner.X - 1, corner.Y - 1);
        var area = Screens.WorkAt(_bed, inside);
        var step = (int)(52 * Scale * Screens.ScaleAt(_bed, inside));
        for (var i = 0; i < _queue.Count; i++)
        {
            var w = _out[_queue[i]];
            w.ShowsBubble = i == 0;
            w.SetScale(Scale);
            var size = Screens.SizeOf(w);
            if (size.Width == 0) continue;
            var x = Math.Clamp(corner.X - i * step - size.Width, area.X, Math.Max(area.X, area.Right - size.Width));
            var y = Math.Clamp(corner.Y - i * step / 4 - size.Height, area.Y, Math.Max(area.Y, area.Bottom - size.Height));
            var p = new PixelPoint(x, y);
            if (w.Position != p) w.Position = p;
        }
        // Задние — под передним.
        for (var i = _queue.Count - 1; i >= 0; i--) Screens.BringTop(_out[_queue[i]]);
        foreach (var id in _detached) if (_out.TryGetValue(id, out var d)) Screens.BringTop(d);
    }

    private PixelPoint Corner()
    {
        var work = Screens.ActiveWork(_bed!);
        return new PixelPoint(work.Right - Gap, work.Bottom - Gap);
    }

    /// <summary>Звук при прыжке (по разу на просьбу).</summary>
    private void Chime(CatInfo cat)
    {
        if (cat.Ask is null || !_chimed.Add(AskKey(cat)) || !Cats.Sound) return;
        Sound.Chime();
    }

    private double Scale => Math.Clamp(Cats.Scale, 0.75, 1.5);
}
