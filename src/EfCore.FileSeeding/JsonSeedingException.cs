namespace EfCore.JsonSeeding;

/// <summary>
/// Ошибка в данных для заполнения. В сообщении указаны файл и номер записи.
/// </summary>
public sealed class JsonSeedingException : Exception
{
    public JsonSeedingException(string message)
        : base(message)
    {
    }

    public JsonSeedingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
