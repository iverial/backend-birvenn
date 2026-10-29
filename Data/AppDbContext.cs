using BirvennApi.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace BirvennApi.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<ActivityEntry> Activity => Set<ActivityEntry>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Color> Colors => Set<Color>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<ProductColorLink> ProductColors => Set<ProductColorLink>();
    public DbSet<ProductStock> ProductStocks => Set<ProductStock>();
    public DbSet<Coupon> Coupons => Set<Coupon>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<ContactMessage> ContactMessages => Set<ContactMessage>();
    public DbSet<NewsletterSubscriber> NewsletterSubscribers => Set<NewsletterSubscriber>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Todo se guarda y se lee en UTC (Npgsql lo exige; Sqlite devuelve Kind sin especificar).
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
        configurationBuilder.Properties<decimal>().HavePrecision(18, 2);
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        // Los Guid los asigna la aplicacion (Guid.NewGuid() en cada entidad). Sin esto EF los toma por "generados por la base"
        // y, al agregar una entidad con id ya cargado a la coleccion de un padre rastreado, la trata como existente (UPDATE en vez de INSERT).
        foreach (var entity in new[] { typeof(User), typeof(ActivityEntry), typeof(Coupon), typeof(Order), typeof(OrderItem), typeof(Payment), typeof(ContactMessage), typeof(NewsletterSubscriber) })
            b.Entity(entity).Property(nameof(User.Id)).ValueGeneratedNever();

        b.Entity<User>(e =>
        {
            e.HasIndex(x => x.Email).IsUnique();
            e.Property(x => x.Email).HasMaxLength(120);
            e.Property(x => x.Role).HasMaxLength(20);
            e.HasMany(x => x.Coupons).WithOne().HasForeignKey(c => c.OwnerId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Activity).WithOne().HasForeignKey(a => a.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ActivityEntry>(e =>
        {
            e.Property(x => x.Type).HasConversion<string>().HasMaxLength(30);
            e.HasIndex(x => new { x.UserId, x.CreatedAt });
        });

        b.Entity<Category>(e =>
        {
            e.HasKey(x => x.Slug);
            e.Property(x => x.Slug).HasMaxLength(40);
        });

        b.Entity<Color>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(32);
        });

        b.Entity<Product>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(32);
            e.Property(x => x.Slug).HasMaxLength(120);
            e.Property(x => x.CategorySlug).HasMaxLength(40);
            e.HasIndex(x => x.Slug).IsUnique();
            e.HasIndex(x => x.CategorySlug);
            e.Property(x => x.Rating).HasPrecision(3, 1);
            e.HasMany(x => x.ColorLinks).WithOne().HasForeignKey(l => l.ProductId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Stock).WithOne().HasForeignKey(s => s.ProductId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ProductColorLink>(e =>
        {
            e.HasKey(x => new { x.ProductId, x.ColorId });
            e.Property(x => x.ProductId).HasMaxLength(32);
            e.Property(x => x.ColorId).HasMaxLength(32);
            e.HasOne(x => x.Color).WithMany().HasForeignKey(x => x.ColorId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<ProductStock>(e =>
        {
            e.HasKey(x => new { x.ProductId, x.Size });
            e.Property(x => x.ProductId).HasMaxLength(32);
            e.Property(x => x.Size).HasMaxLength(4);
        });

        b.Entity<Coupon>(e =>
        {
            e.HasIndex(x => x.Code).IsUnique();
            e.Property(x => x.Code).HasMaxLength(40);
            e.Property(x => x.Type).HasConversion<string>().HasMaxLength(20);
        });

        b.Entity<Order>(e =>
        {
            e.HasIndex(x => x.PublicId).IsUnique();
            e.HasIndex(x => x.Number).IsUnique();
            e.HasIndex(x => new { x.Email, x.ClientOrderId }).IsUnique();
            e.HasIndex(x => x.UserId);
            e.HasIndex(x => new { x.Status, x.ExpiresAt });
            e.Property(x => x.PublicId).HasMaxLength(40);
            e.Property(x => x.Number).HasMaxLength(20);
            e.Property(x => x.ClientOrderId).HasMaxLength(64);
            e.Property(x => x.Email).HasMaxLength(120);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.ShippingMethod).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.PaymentMethod).HasConversion<string>().HasMaxLength(20);
            e.OwnsOne(x => x.Address);
            e.HasMany(x => x.Items).WithOne().HasForeignKey(i => i.OrderId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Payments).WithOne().HasForeignKey(p => p.OrderId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Payment>(e =>
        {
            e.HasIndex(x => new { x.Provider, x.ExternalId }).IsUnique();
            e.Property(x => x.Provider).HasMaxLength(20);
            e.Property(x => x.ExternalId).HasMaxLength(64);
            e.Property(x => x.Status).HasMaxLength(30);
            e.Property(x => x.Currency).HasMaxLength(3);
        });

        b.Entity<NewsletterSubscriber>(e =>
        {
            e.HasIndex(x => x.Email).IsUnique();
            e.Property(x => x.Email).HasMaxLength(120);
        });
    }
}

public class UtcDateTimeConverter : ValueConverter<DateTime, DateTime>
{
    public UtcDateTimeConverter()
        : base(v => v.Kind == DateTimeKind.Utc ? v : v.ToUniversalTime(), v => DateTime.SpecifyKind(v, DateTimeKind.Utc))
    {
    }
}
