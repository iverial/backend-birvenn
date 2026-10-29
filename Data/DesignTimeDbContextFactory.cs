using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace BirvennApi.Data;

/// <summary>
/// Solo lo usa `dotnet ef migrations add`. Las migraciones se generan para Postgres
/// (no hace falta una base real para crearlas).
/// </summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=birvenn;Username=birvenn;Password=design-time-only")
            .Options;
        return new AppDbContext(options);
    }
}
