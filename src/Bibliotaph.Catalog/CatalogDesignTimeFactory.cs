using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Bibliotaph.Catalog;

/// <summary>
/// Lets <c>dotnet ef migrations add</c> build the model without the app. From the repo root:
/// <c>dotnet ef migrations add Name --project src/Bibliotaph.Catalog --output-dir Migrations</c>
/// </summary>
public sealed class CatalogDesignTimeFactory : IDesignTimeDbContextFactory<CatalogDbContext>
{
    public CatalogDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<CatalogDbContext>().UseSqlite("Data Source=design-time.db").Options);
}
