using Microsoft.EntityFrameworkCore;

namespace EfCore.FileSeeding.Tests;

public enum ProductStatus
{
    Active,
    Hidden
}

public class Category
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

public class Product
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public decimal Price { get; set; }
    public ProductStatus Status { get; set; }
    public int CategoryId { get; set; }
    public DateOnly? AddedOn { get; set; }

    public Category Category { get; set; } = null!;
}

public class Account
{
    public int Id { get; set; }
    public string Login { get; set; } = "";
    public string PasswordHash { get; set; } = "";
}

public sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
{
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Account> Accounts => Set<Account>();
}
