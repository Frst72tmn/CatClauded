namespace CatClauded.Core;

/// <summary>Короткий журнал рядом с настройками (catclauded.log): что пришло от Claude и чем ответили.</summary>
public static class Log
{
    private static readonly object Gate = new();
    public static string? FilePath { get; set; }

    public static void Write(string message)
    {
        if (FilePath is null) return;
        lock (Gate)
            try
            {
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 1_000_000) File.Move(FilePath, FilePath + ".old", overwrite: true);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
            catch (IOException) { }
    }
}
