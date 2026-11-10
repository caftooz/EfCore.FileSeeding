using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EfCore.JsonSeeding.Tests;

public sealed class JsonSeedingTests : IDisposable
{
    private static readonly Assembly TestAssembly = typeof(JsonSeedingTests).Assembly;

    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    public JsonSeedingTests() => _connection.Open();

    public void Dispose() => _connection.Dispose();

    private static void BasicSeed(JsonSeedingBuilder seed) => seed
        .AddEmbeddedResources(TestAssembly, "Seed/Basic")
        .Transform<Account>("password", (account, value) => account.PasswordHash = "hash:" + value.GetString());

    private TestDbContext CreateContext(Action<JsonSeedingBuilder>? seeding = null)
    {
        var builder = new DbContextOptionsBuilder<TestDbContext>().UseSqlite(_connection);
        if (seeding is not null)
            builder.UseJsonSeeding(seeding);
        return new TestDbContext(builder.Options);
    }

    private async Task<TestDbContext> CreateEmptyDatabaseAsync()
    {
        var db = CreateContext();
        await db.Database.EnsureCreatedAsync();
        return db;
    }

    [Fact]
    public async Task EnsureCreatedAsync_seeds_in_foreign_key_order_and_applies_transform()
    {
        await using var db = CreateContext(BasicSeed);

        await db.Database.EnsureCreatedAsync();

        Assert.Equal(2, await db.Categories.CountAsync());

        var products = await db.Products.Include(p => p.Category).OrderBy(p => p.Id).ToListAsync();
        Assert.Equal(3, products.Count);
        Assert.Equal("Продукты", products[0].Category.Name);
        Assert.Equal(ProductStatus.Hidden, products[2].Status);

        var account = await db.Accounts.SingleAsync();
        Assert.Equal("hash:secret", account.PasswordHash);
    }

    [Fact]
    public void EnsureCreated_runs_synchronous_seeding()
    {
        using var db = CreateContext(BasicSeed);

        db.Database.EnsureCreated();

        Assert.Equal(3, db.Products.Count());
    }

    [Fact]
    public async Task Running_twice_does_not_duplicate_rows()
    {
        await using var db = await CreateEmptyDatabaseAsync();

        await db.SeedFromJsonAsync(BasicSeed);
        await db.SeedFromJsonAsync(BasicSeed);

        Assert.Equal(2, await db.Categories.CountAsync());
        Assert.Equal(3, await db.Products.CountAsync());
        Assert.Equal(1, await db.Accounts.CountAsync());
    }

    [Fact]
    public async Task Existing_rows_are_kept_by_default()
    {
        await using var db = await CreateEmptyDatabaseAsync();
        await db.SeedFromJsonAsync(BasicSeed);

        await db.SeedFromJsonAsync(seed => seed.AddEmbeddedResources(TestAssembly, "Seed/Update"));

        Assert.Equal("Продукты", (await db.Categories.SingleAsync(c => c.Id == 1)).Name);
    }

    [Fact]
    public async Task UpdateExisting_changes_only_fields_from_json()
    {
        await using var db = await CreateEmptyDatabaseAsync();
        await db.SeedFromJsonAsync(BasicSeed);

        await db.SeedFromJsonAsync(seed => seed.AddEmbeddedResources(TestAssembly, "Seed/Update").UpdateExisting());

        db.ChangeTracker.Clear();
        Assert.Equal("Еда", (await db.Categories.SingleAsync(c => c.Id == 1)).Name);
        Assert.Equal("Канцелярия", (await db.Categories.SingleAsync(c => c.Id == 2)).Name);
    }

    [Fact]
    public async Task Unknown_property_is_reported_as_typo()
    {
        await using var db = await CreateEmptyDatabaseAsync();

        var error = await Assert.ThrowsAsync<JsonSeedingException>(
            () => db.SeedFromJsonAsync(seed => seed.AddEmbeddedResources(TestAssembly, "Seed/Typo")));

        Assert.Contains("nmae", error.Message);
        Assert.Contains("запись №1", error.Message);
    }

    [Fact]
    public async Task Unknown_property_is_ignored_when_allowed()
    {
        await using var db = await CreateEmptyDatabaseAsync();

        await db.SeedFromJsonAsync(seed => seed
            .AddEmbeddedResources(TestAssembly, "Seed/Typo")
            .AllowUnknownProperties());

        Assert.Equal(1, await db.Categories.CountAsync());
    }

    [Fact]
    public async Task Missing_primary_key_is_an_error()
    {
        await using var db = await CreateEmptyDatabaseAsync();

        var error = await Assert.ThrowsAsync<JsonSeedingException>(
            () => db.SeedFromJsonAsync(seed => seed.AddEmbeddedResources(TestAssembly, "Seed/NoKey")));

        Assert.Contains("первичный ключ", error.Message);
    }

    [Fact]
    public async Task File_without_matching_table_is_an_error()
    {
        await using var db = await CreateEmptyDatabaseAsync();

        var error = await Assert.ThrowsAsync<JsonSeedingException>(
            () => db.SeedFromJsonAsync(seed => seed.AddEmbeddedResources(TestAssembly, "Seed/UnknownTable")));

        Assert.Contains("Prodcts", error.Message);
    }

    [Fact]
    public async Task Disabled_source_is_skipped()
    {
        await using var db = await CreateEmptyDatabaseAsync();

        await db.SeedFromJsonAsync(seed => seed.AddEmbeddedResources(TestAssembly, "Seed/Basic", enabled: false));

        Assert.Equal(0, await db.Categories.CountAsync());
    }

    [Fact]
    public async Task LogTo_reports_counts_per_file()
    {
        await using var db = await CreateEmptyDatabaseAsync();
        var log = new List<string>();

        await db.SeedFromJsonAsync(seed =>
        {
            BasicSeed(seed);
            seed.LogTo(log.Add);
        });

        Assert.Contains("Products: добавлено 3, обновлено 0", log);
    }

    [Fact]
    public async Task Csv_is_seeded_like_json()
    {
        await using var db = await CreateEmptyDatabaseAsync();

        await db.SeedFromJsonAsync(seed => seed
            .AddEmbeddedResources(TestAssembly, "Seed/Csv")
            .Transform<Account>("password", (account, value) => account.PasswordHash = "hash:" + value.GetString()));

        var categories = await db.Categories.OrderBy(c => c.Id).ToListAsync();
        Assert.Equal(new[] { "Продукты", "Канцелярия; бумага" }, categories.Select(c => c.Name));

        var products = await db.Products.OrderBy(p => p.Id).ToListAsync();
        Assert.Equal("Сок \"Яблочный\"", products[0].Name);
        Assert.Equal(25.50m, products[0].Price);
        Assert.Equal(ProductStatus.Active, products[0].Status);
        Assert.Equal("Ручка\nсиняя", products[1].Name);
        Assert.Equal(1234.5m, products[1].Price);
        Assert.Equal(ProductStatus.Hidden, products[1].Status);
        Assert.Equal(2, products[1].CategoryId);

        var account = await db.Accounts.SingleAsync();
        Assert.Equal("admin", account.Login);
        Assert.Equal("hash:secret", account.PasswordHash);
    }

    [Fact]
    public async Task Csv_in_windows_1251_is_read()
    {
        await using var db = await CreateEmptyDatabaseAsync();

        await db.SeedFromJsonAsync(seed => seed.AddEmbeddedResources(TestAssembly, "Seed/Csv1251"));

        Assert.Equal("Овощи", (await db.Categories.SingleAsync()).Name);
    }

    [Fact]
    public async Task Csv_unknown_column_is_reported_with_line()
    {
        await using var db = await CreateEmptyDatabaseAsync();

        var error = await Assert.ThrowsAsync<JsonSeedingException>(
            () => db.SeedFromJsonAsync(seed => seed.AddEmbeddedResources(TestAssembly, "Seed/CsvTypo")));

        Assert.Contains("Nmae", error.Message);
        Assert.Contains("строка 2", error.Message);
    }

    [Fact]
    public async Task Csv_invalid_number_is_reported_with_column()
    {
        await using var db = await CreateEmptyDatabaseAsync();

        var error = await Assert.ThrowsAsync<JsonSeedingException>(
            () => db.SeedFromJsonAsync(seed => seed.AddEmbeddedResources(TestAssembly, "Seed/CsvBadNumber")));

        Assert.Contains("«Id»", error.Message);
        Assert.Contains("«один»", error.Message);
        Assert.Contains("строка 2", error.Message);
    }

    [Fact]
    public async Task Xlsx_sheets_are_seeded_as_tables()
    {
        await using var db = await CreateEmptyDatabaseAsync();
        var log = new List<string>();

        await db.SeedFromJsonAsync(seed => seed
            .AddEmbeddedResources(TestAssembly, "Seed/Xlsx")
            .Transform<Account>("password", (account, value) => account.PasswordHash = "hash:" + value.GetString())
            .LogTo(log.Add));

        Assert.Equal(2, await db.Categories.CountAsync());

        var products = await db.Products.OrderBy(p => p.Id).ToListAsync();
        Assert.Equal(3, products.Count);
        Assert.Equal("Сок \"Яблочный\"", products[0].Name);
        Assert.Equal(25.5m, products[0].Price);
        Assert.Equal(ProductStatus.Active, products[0].Status);
        Assert.Equal(new DateOnly(2026, 11, 10), products[0].AddedOn);
        Assert.Equal(1234.5m, products[1].Price);
        Assert.Equal(ProductStatus.Hidden, products[1].Status);
        Assert.Null(products[1].AddedOn);
        Assert.Equal(new DateOnly(2026, 1, 15), products[2].AddedOn);

        Assert.Equal("hash:secret", (await db.Accounts.SingleAsync()).PasswordHash);

        // служебный «_Справка» и пустой «Лист1» пропущены, Categories загружены раньше Products
        var tables = log.Select(line => line.Split(':')[0]).ToList();
        Assert.Equal(new[] { "Accounts", "Categories", "Products" }, tables.Order());
        Assert.True(tables.IndexOf("Categories") < tables.IndexOf("Products"));
    }

    [Fact]
    public async Task Xlsx_unknown_column_is_reported_with_sheet_and_line()
    {
        await using var db = await CreateEmptyDatabaseAsync();

        var error = await Assert.ThrowsAsync<JsonSeedingException>(
            () => db.SeedFromJsonAsync(seed => seed.AddEmbeddedResources(TestAssembly, "Seed/XlsxTypo")));

        Assert.Contains("Nmae", error.Message);
        Assert.Contains("лист «Categories»", error.Message);
        Assert.Contains("строка 2", error.Message);
    }
}
