using Microsoft.EntityFrameworkCore;

namespace EfCore.JsonSeeding;

/// <summary>
/// Подключение заполнения базы из JSON-файлов.
/// </summary>
public static class JsonSeedingExtensions
{
    /// <summary>
    /// Включает заполнение базы из JSON-файлов. Оно выполняется автоматически после
    /// <c>Database.Migrate()</c> / <c>MigrateAsync()</c>, <c>EnsureCreated()</c> и <c>dotnet ef database update</c>.
    /// </summary>
    /// <remarks>
    /// Использует <c>UseSeeding</c> / <c>UseAsyncSeeding</c> из EF Core, поэтому вместе с ними
    /// в одном контексте не применяется: последняя настройка заменяет предыдущую.
    /// </remarks>
    public static DbContextOptionsBuilder UseJsonSeeding(
        this DbContextOptionsBuilder optionsBuilder,
        Action<JsonSeedingBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        var seeder = CreateSeeder(configure);

        return optionsBuilder
            .UseSeeding((context, _) => seeder.SeedAsync(context, CancellationToken.None).GetAwaiter().GetResult())
            .UseAsyncSeeding((context, _, ct) => seeder.SeedAsync(context, ct));
    }

    /// <inheritdoc cref="UseJsonSeeding(DbContextOptionsBuilder, Action{JsonSeedingBuilder})"/>
    public static DbContextOptionsBuilder<TContext> UseJsonSeeding<TContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder,
        Action<JsonSeedingBuilder> configure)
        where TContext : DbContext
    {
        ((DbContextOptionsBuilder)optionsBuilder).UseJsonSeeding(configure);
        return optionsBuilder;
    }

    /// <summary>
    /// Запускает заполнение вручную, без привязки к миграциям.
    /// </summary>
    public static Task SeedFromJsonAsync(
        this DbContext context,
        Action<JsonSeedingBuilder> configure,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        return CreateSeeder(configure).SeedAsync(context, cancellationToken);
    }

    private static JsonSeeder CreateSeeder(Action<JsonSeedingBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new JsonSeedingBuilder();
        configure(builder);
        return new JsonSeeder(builder.Build());
    }
}
