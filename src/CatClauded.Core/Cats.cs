namespace CatClauded.Core;

public enum CatState { Sleeping, Working, Asking, Finished }

/// <summary>Чего кот просит: разрешения (AskId — ждёт кнопку) или вашего ответа в самом Claude (AskId нет).</summary>
public sealed record CatAsk(long? AskId, string Title, string Detail, bool CanAlways, DateTimeOffset AtUtc)
{
    public bool IsQuestion => AskId is null;
}

/// <summary>Откуда кот: подпись («Десктоп · Windows») и значок на коте.</summary>
public sealed record CatOrigin(string Label, string Badge);

/// <summary>Кот — одна сессия Claude Code. Coat — окрас, постоянный для сессии.</summary>
public sealed record CatInfo(long Id, string Title, int Coat, CatState State, CatAsk? Ask, CatOrigin Origin);
