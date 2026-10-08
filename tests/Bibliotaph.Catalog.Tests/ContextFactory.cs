using Microsoft.EntityFrameworkCore;

namespace Bibliotaph.Catalog.Tests;

sealed class Factory(CatalogDatabase database) : IDbContextFactory<CatalogDbContext>
{
    public CatalogDbContext CreateDbContext() => database.CreateContext();
}
