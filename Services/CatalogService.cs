using System.Globalization;
using System.Text;
using BirvennApi.Data;
using BirvennApi.Dtos;
using BirvennApi.Models;
using Microsoft.EntityFrameworkCore;

namespace BirvennApi.Services;

public record ProductQuery(
    string? Category,
    string[] Sizes,
    string[] Colors,
    string[] Tags,
    decimal? PriceMax,
    string? Query,
    string Sort,
    int Limit,
    int Offset);

/// <summary>
/// El catalogo vive en la base. El catalogo es chico (decenas de productos), asi que se carga completo y se
/// filtra en memoria con las mismas reglas que `filterProducts` del front; evita ordenar decimales en SQL
/// (Sqlite no lo soporta) y mantiene identico el orden y la busqueda sin tildes.
/// </summary>
public class CatalogService(AppDbContext db)
{
    private IQueryable<Product> Products => db.Products
        .AsNoTracking()
        .AsSplitQuery()
        .Include(p => p.ColorLinks).ThenInclude(l => l.Color)
        .Include(p => p.Stock)
        .Where(p => p.Active);

    public async Task<ProductListResponse> ListAsync(ProductQuery q, CancellationToken ct)
    {
        var all = await Products.ToListAsync(ct);
        var needle = string.IsNullOrWhiteSpace(q.Query) ? "" : Normalize(q.Query);

        IEnumerable<Product> result = all.Where(p =>
        {
            if (q.Category is not null && p.CategorySlug != q.Category) return false;
            if (q.Sizes.Length > 0 && !q.Sizes.Any(s => StockOf(p, s) > 0)) return false;
            if (q.Colors.Length > 0 && !p.ColorLinks.Any(l => q.Colors.Contains(l.ColorId))) return false;
            if (q.Tags.Length > 0 && !q.Tags.Any(t => p.Tags.Contains(t))) return false;
            if (q.PriceMax is { } max && p.Price > max) return false;
            if (needle.Length > 0 && !Normalize($"{p.Name} {p.ShortDescription} {p.Collection}").Contains(needle)) return false;
            return true;
        });

        result = q.Sort switch
        {
            "nuevos" => result.OrderByDescending(p => p.CreatedAt),
            "precio-asc" => result.OrderBy(p => p.Price),
            "precio-desc" => result.OrderByDescending(p => p.Price),
            "mas-vendidos" => result.OrderByDescending(p => p.ReviewsCount),
            _ => result,
        };

        var list = result.ToList();
        var items = list.Skip(q.Offset).Take(q.Limit).Select(p => p.ToDto()).ToList();
        var ceiling = all.Count == 0 ? 0 : all.Max(p => p.Price);
        return new ProductListResponse(items, list.Count, ceiling);
    }

    public async Task<ProductDto?> GetAsync(string idOrSlug, CancellationToken ct)
    {
        var p = await Products.FirstOrDefaultAsync(x => x.Id == idOrSlug || x.Slug == idOrSlug, ct);
        return p?.ToDto();
    }

    public async Task<List<ProductDto>> SearchAsync(string query, int limit, CancellationToken ct)
    {
        var needle = Normalize(query);
        if (needle.Length == 0) return [];

        var all = await Products.ToListAsync(ct);
        return all
            .Where(p => Normalize($"{p.Name} {p.CategorySlug} {p.Collection} {p.ShortDescription}").Contains(needle))
            .Take(limit)
            .Select(p => p.ToDto())
            .ToList();
    }

    public async Task<List<CategoryDto>> CategoriesAsync(CancellationToken ct) =>
        (await db.Categories.AsNoTracking().OrderBy(c => c.SortOrder).ToListAsync(ct)).Select(c => c.ToDto()).ToList();

    private static int StockOf(Product p, string size) => p.Stock.FirstOrDefault(s => s.Size == size)?.Quantity ?? 0;

    // minusculas y sin tildes: "Camperá" encuentra "campera"
    private static string Normalize(string value)
    {
        var decomposed = value.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(ch);
        return sb.ToString().Trim();
    }
}
