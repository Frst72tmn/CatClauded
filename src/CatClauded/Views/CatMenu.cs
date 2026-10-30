using Avalonia.Controls;

namespace CatClauded.Views;

/// <summary>Меню кота по правой кнопке: открыть сессию или скрыть кота до новой сессии.</summary>
public static class CatMenu
{
    public static ContextMenu For(Action open, Action hide)
    {
        var openItem = new MenuItem { Header = "Открыть сессию" };
        openItem.Click += (_, _) => open();
        var hideItem = new MenuItem { Header = "Скрыть до новой сессии" };
        ToolTip.SetTip(hideItem, "Кот уйдёт с экрана и с лежанки, пока эта сессия Claude не запустится заново. Разрешения она будет спрашивать сама.");
        hideItem.Click += (_, _) => hide();
        return new ContextMenu { Items = { openItem, new Separator(), hideItem } };
    }
}
