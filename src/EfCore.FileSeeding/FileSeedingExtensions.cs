using Microsoft.EntityFrameworkCore;

namespace EfCore.FileSeeding;

/// <summary>
/// Подключение заполнения базы из файлов JSON, CSV и Excel.
/// </summary>
public static class FileSeedingExtensions
{
    /// <summary>
    /// Включает заполнение базы из файлов JSON, CSV и Excel. Оно выполняется автоматически после
    /// <c>Database.Migrate()</c> / <c>MigrateAsync()</c>, <c>EnsureCreated()</c> и <c>dotnet ef database update</c>.
    /// </summary>
    /// <remarks>
    /// Использует <c>UseSeeding</c> / <c>UseAsyncSeeding</c> из EF Core, поэтому вместе с ними
    /// в одном контексте не применяется: последняя настройка заменяет предыдущую.
    /// </remarks>
    public static DbContextOptionsBuilder UseFileSeeding(
        this DbContextOptionsBuilder optionsBuilder,
        Action<FileSeedingBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        var seeder = CreateSeeder(configure);

        return optionsBuilder
            .UseSeeding((context, _) => seeder.SeedAsync(context, CancellationToken.None).GetAwaiter().GetResult())
            .UseAsyncSeeding((context, _, ct) => seeder.SeedAsync(context, ct));
    }

    /// <inheritdoc cref="UseFileSeeding(DbContextOptionsBuilder, Action{FileSeedingBuilder})"/>
    public static DbContextOptionsBuilder<TContext> UseFileSeeding<TContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder,
        Action<FileSeedingBuilder> configure)
        where TContext : DbContext
    {
        ((DbContextOptionsBuilder)optionsBuilder).UseFileSeeding(configure);
        return optionsBuilder;
    }

    /// <summary>
    /// Запускает заполнение вручную, без привязки к миграциям.
    /// </summary>
    public static Task SeedFromFilesAsync(
        this DbContext context,
        Action<FileSeedingBuilder> configure,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        return CreateSeeder(configure).SeedAsync(context, cancellationToken);
    }

    private static FileSeeder CreateSeeder(Action<FileSeedingBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new FileSeedingBuilder();
        configure(builder);
        return new FileSeeder(builder.Build());
    }
}
