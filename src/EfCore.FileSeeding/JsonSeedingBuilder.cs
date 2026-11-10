using System.Reflection;
using System.Text.Json;

namespace EfCore.JsonSeeding;

/// <summary>
/// Настройка заполнения: откуда брать файлы и как обрабатывать особые поля.
/// </summary>
public sealed class JsonSeedingBuilder
{
    private readonly List<ISeedSource> _sources = [];
    private readonly List<TransformRule> _transforms = [];
    private bool _updateExisting;
    private bool _strict = true;
    private bool _resetSequences = true;
    private Action<string>? _log;

    internal JsonSeedingBuilder()
    {
    }

    /// <summary>
    /// Файлы, встроенные в сборку как EmbeddedResource, например из папки <c>Data/Seed/Demo</c>.
    /// Источники обрабатываются в порядке добавления.
    /// </summary>
    /// <param name="assembly">Сборка, в которую встроены файлы (обычно <c>typeof(Program).Assembly</c>).</param>
    /// <param name="folder">Папка внутри проекта, например <c>Data/Seed/Demo</c>.</param>
    /// <param name="enabled">Удобно для условий: <c>enabled: builder.Environment.IsDevelopment()</c>.</param>
    public JsonSeedingBuilder AddEmbeddedResources(Assembly assembly, string folder, bool enabled = true)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        if (enabled)
            _sources.Add(new EmbeddedResourceSource(assembly, folder));
        return this;
    }

    /// <summary>
    /// Файлы <c>*.json</c> из папки на диске. Относительный путь считается от папки приложения.
    /// </summary>
    public JsonSeedingBuilder AddDirectory(string path, bool enabled = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (enabled)
            _sources.Add(new DirectorySource(path));
        return this;
    }

    /// <summary>
    /// Особая обработка поля из JSON. Вызывается, если в записи есть поле <paramref name="jsonProperty"/>.
    /// Такое поле может отсутствовать в сущности: например, <c>password</c>, который превращается в хеш.
    /// </summary>
    /// <example>
    /// <code>
    /// .Transform&lt;User&gt;("password", (user, value) =&gt;
    /// {
    ///     var (hash, salt) = Encryptor.HashPassword(value.GetString()!);
    ///     user.PasswordHash = hash;
    ///     user.PasswordSalt = salt;
    /// })
    /// </code>
    /// </example>
    public JsonSeedingBuilder Transform<TEntity>(string jsonProperty, Action<TEntity, JsonElement> apply)
        where TEntity : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jsonProperty);
        ArgumentNullException.ThrowIfNull(apply);
        _transforms.Add(new TransformRule(typeof(TEntity), jsonProperty, (entity, value) => apply((TEntity)entity, value)));
        return this;
    }

    /// <summary>
    /// Обновлять уже существующие записи значениями из JSON. По умолчанию существующие записи не трогаются.
    /// Обновляются только поля, явно указанные в JSON.
    /// </summary>
    public JsonSeedingBuilder UpdateExisting(bool enabled = true)
    {
        _updateExisting = enabled;
        return this;
    }

    /// <summary>
    /// Не считать ошибкой поля в JSON, которых нет в сущности. По умолчанию такие поля — ошибка:
    /// так сразу видны опечатки вроде <c>"pirce"</c>.
    /// </summary>
    public JsonSeedingBuilder AllowUnknownProperties(bool allow = true)
    {
        _strict = !allow;
        return this;
    }

    /// <summary>
    /// Для PostgreSQL после загрузки сдвигать счётчики id за максимальное значение. Включено по умолчанию:
    /// без этого первая обычная вставка получила бы уже занятый id.
    /// </summary>
    public JsonSeedingBuilder ResetPostgresSequences(bool enabled = true)
    {
        _resetSequences = enabled;
        return this;
    }

    /// <summary>
    /// Куда писать краткий отчёт: сколько записей добавлено и обновлено в каждой таблице.
    /// </summary>
    public JsonSeedingBuilder LogTo(Action<string> log)
    {
        _log = log;
        return this;
    }

    internal JsonSeedingOptions Build() =>
        new(_sources.ToArray(), _transforms.ToArray(), _updateExisting, _strict, _resetSequences, _log);
}

internal sealed record TransformRule(Type EntityType, string JsonProperty, Action<object, JsonElement> Apply);

internal sealed record JsonSeedingOptions(
    IReadOnlyList<ISeedSource> Sources,
    IReadOnlyList<TransformRule> Transforms,
    bool UpdateExisting,
    bool Strict,
    bool ResetSequences,
    Action<string>? Log);
