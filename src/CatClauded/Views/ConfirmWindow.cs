using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace CatClauded.Views;

/// <summary>Подтверждение перед правкой ~/.claude/settings.json: что именно изменится, и кнопки «Да» / «Отмена».</summary>
public sealed class ConfirmWindow : Window
{
    private bool _ok;

    private ConfirmWindow(string title, string text, string yes)
    {
        Title = title;
        Width = 520;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(0, 16, 0, 0) };
        var ok = new Button { Content = yes, IsDefault = true };
        ok.Classes.Add("accent");
        ok.Click += (_, _) => { _ok = true; Close(); };
        var cancel = new Button { Content = "Отмена", IsCancel = true };
        cancel.Click += (_, _) => Close();
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Children =
            {
                new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 10) },
                new SelectableTextBlock { Text = text, TextWrapping = TextWrapping.Wrap },
                buttons,
            },
        };
    }

    /// <summary>Спросить; true — пользователь нажал «Да».</summary>
    public static async Task<bool> AskAsync(string title, string text, string yes)
    {
        var w = new ConfirmWindow(title, text, yes);
        var done = new TaskCompletionSource();
        w.Closed += (_, _) => done.TrySetResult();
        w.Show();
        w.Activate();
        await done.Task;
        return w._ok;
    }

    /// <summary>Сообщение с одной кнопкой.</summary>
    public static async Task InformAsync(string title, string text)
    {
        var w = new ConfirmWindow(title, text, "Понятно");
        if (w.Content is StackPanel { Children: [.., StackPanel buttons] }) buttons.Children.RemoveAt(1);
        var done = new TaskCompletionSource();
        w.Closed += (_, _) => done.TrySetResult();
        w.Show();
        w.Activate();
        await done.Task;
    }
}
