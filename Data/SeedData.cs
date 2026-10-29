using System.Reflection;
using System.Text.Json;
using BirvennApi.Models;
using BirvennApi.Options;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BirvennApi.Data;

/// <summary>Datos iniciales idempotentes: cupones publicos, catalogo (una sola vez) y el usuario admin.</summary>
public static class SeedData
{
    private sealed class SeedFile
    {
        public List<SeedCategory> Categories { get; set; } = [];
        public List<Color> Colors { get; set; } = [];
        public List<SeedProduct> Products { get; set; } = [];
    }

    private sealed class SeedCategory
    {
        public string Slug { get; set; } = "";
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public string Kind { get; set; } = "";
        public string Tone { get; set; } = "";
        public int SortOrder { get; set; }
    }

    private sealed class SeedProduct
    {
        public string Id { get; set; } = "";
        public string Slug { get; set; } = "";
        public string Name { get; set; } = "";
        public string Category { get; set; } = "";
        public string Kind { get; set; } = "";
        public string Graphic { get; set; } = "none";
        public decimal Price { get; set; }
        public decimal? CompareAtPrice { get; set; }
        public List<string> Colors { get; set; } = [];
        public List<string> Sizes { get; set; } = [];
        public Dictionary<string, int> Stock { get; set; } = [];
        public List<string> Tags { get; set; } = [];
        public string Fit { get; set; } = "";
        public string Collection { get; set; } = "";
        public string ShortDescription { get; set; } = "";
        public string Description { get; set; } = "";
        public List<string> Details { get; set; } = [];
        public string Materials { get; set; } = "";
        public List<string> Care { get; set; } = [];
        public decimal Rating { get; set; }
        public int ReviewsCount { get; set; }
        public List<string>? Images { get; set; }
        public string CreatedAt { get; set; } = "";
    }

    public static async Task RunAsync(IServiceProvider services, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<AppDbContext>();
        var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("Seed");

        await SeedPublicCouponsAsync(db, ct);

        if (sp.GetRequiredService<IOptions<DatabaseOptions>>().Value.SeedCatalog) await SeedCatalogAsync(db, logger, ct);

        await SeedAdminAsync(db, sp.GetRequiredService<IPasswordHasher<User>>(), sp.GetRequiredService<IOptions<AdminOptions>>().Value, logger, ct);
    }

    // Los mismos cupones publicos que tenia `lib/data/coupons.ts`.
    private static async Task SeedPublicCouponsAsync(AppDbContext db, CancellationToken ct)
    {
        if (!await db.Coupons.AnyAsync(c => c.Code == "STREET10", ct))
            db.Coupons.Add(new Coupon { Code = "STREET10", Type = CouponType.Percent, Value = 10, Description = "10% off en toda la tienda", MinSubtotal = 60_000 });

        if (!await db.Coupons.AnyAsync(c => c.Code == "ENVIOFREE", ct))
            db.Coupons.Add(new Coupon { Code = "ENVIOFREE", Type = CouponType.FreeShipping, Value = 0, Description = "Envío estándar gratis", MinSubtotal = 80_000 });

        await db.SaveChangesAsync(ct);
    }

    private static async Task SeedCatalogAsync(AppDbContext db, ILogger logger, CancellationToken ct)
    {
        if (await db.Products.AnyAsync(ct)) return;

        await using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("catalog.json")
                                 ?? throw new InvalidOperationException("Falta el recurso embebido catalog.json.");
        var seed = await JsonSerializer.DeserializeAsync<SeedFile>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web), ct)
                   ?? throw new InvalidOperationException("catalog.json invalido.");

        db.Categories.AddRange(seed.Categories.Select(c => new Category
        {
            Slug = c.Slug, Name = c.Name, Description = c.Description, Kind = c.Kind, Tone = c.Tone, SortOrder = c.SortOrder,
        }));
        db.Colors.AddRange(seed.Colors);

        foreach (var p in seed.Products)
        {
            var product = new Product
            {
                Id = p.Id, Slug = p.Slug, Name = p.Name, CategorySlug = p.Category, Kind = p.Kind, Graphic = p.Graphic,
                Price = p.Price, CompareAtPrice = p.CompareAtPrice, Fit = p.Fit, Collection = p.Collection,
                ShortDescription = p.ShortDescription, Description = p.Description, Materials = p.Materials,
                Rating = p.Rating, ReviewsCount = p.ReviewsCount, CreatedAt = DateOnly.Parse(p.CreatedAt),
                Sizes = p.Sizes, Tags = p.Tags, Details = p.Details, Care = p.Care, Images = p.Images ?? [],
                ColorLinks = [.. p.Colors.Select((colorId, index) => new ProductColorLink { ProductId = p.Id, ColorId = colorId, Position = index })],
                // Una fila por talle ofrecido; si el talle no figuraba en el stock original, queda en 0.
                Stock = [.. p.Sizes.Select(size => new ProductStock { ProductId = p.Id, Size = size, Quantity = p.Stock.GetValueOrDefault(size) })],
            };
            db.Products.Add(product);
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Catálogo inicial cargado: {Products} productos", seed.Products.Count);
    }

    private static async Task SeedAdminAsync(AppDbContext db, IPasswordHasher<User> hasher, AdminOptions admin, ILogger logger, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(admin.Email) || string.IsNullOrWhiteSpace(admin.Password)) return;

        var email = admin.Email.Trim().ToLowerInvariant();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);
        if (user is null)
        {
            user = new User { Email = email, FirstName = "Admin", LastName = "BIRVENN", Role = Roles.Admin };
            user.PasswordHash = hasher.HashPassword(user, admin.Password);
            db.Users.Add(user);
            logger.LogInformation("Usuario admin creado: {Email}", email);
        }
        else if (user.Role != Roles.Admin)
        {
            // Se promueve una cuenta existente; la clave NO se toca.
            user.Role = Roles.Admin;
            logger.LogInformation("Cuenta {Email} promovida a admin", email);
        }

        await db.SaveChangesAsync(ct);
    }
}
