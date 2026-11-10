namespace EfCore.FileSeeding;

/// <summary>
/// Ошибка в данных для заполнения. В сообщении указаны файл и номер записи.
/// </summary>
public sealed class FileSeedingException : Exception
{
    /// <summary>Создаёт ошибку с сообщением.</summary>
    public FileSeedingException(string message)
        : base(message)
    {
    }

    /// <summary>Создаёт ошибку с сообщением и исходным исключением.</summary>
    public FileSeedingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
