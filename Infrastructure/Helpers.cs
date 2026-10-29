using System.Security.Claims;
using System.Security.Cryptography;
using BirvennApi.Models;
using BirvennApi.Options;
using Microsoft.Extensions.Options;

namespace BirvennApi.Infrastructure;

/// <summary>Codigos aleatorios con CSPRNG. Alfabeto sin caracteres ambiguos (0/O, 1/I), igual que el front.</summary>
public static class CodeGenerator
{
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public static string Code(int length)
    {
        var chars = new char[length];
        for (var i = 0; i < length; i++) chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return new string(chars);
    }

    public static string OrderNumber() => $"BRV-{Code(6)}";

    public static string WelcomeCoupon() => $"BIENVENIDA-{Code(6)}";

    /// <summary>128 bits aleatorios: imposible de adivinar, por eso alcanza como llave de un pedido de invitado.</summary>
    public static string PublicOrderId() => "ord_" + Base64Url(RandomNumberGenerator.GetBytes(16));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public static class ClaimsExtensions
{
    public static Guid? GetUserId(this ClaimsPrincipal user)
    {
        var raw = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub");
        return Guid.TryParse(raw, out var id) ? id : null;
    }
}

public static class ConfigExtensions
{
    /// <summary>Acepta "a,b" o Cors__Origins__0=a, Cors__Origins__1=b.</summary>
    public static string[] GetCorsOrigins(this IConfiguration configuration)
    {
        var section = configuration.GetSection("Cors:Origins");
        var values = section.GetChildren().Select(c => c.Value).ToList();
        if (values.Count == 0 && !string.IsNullOrWhiteSpace(section.Value)) values = [.. section.Value.Split(',', ';')];

        return values
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!.Trim().TrimEnd('/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}

/// <summary>Emite y borra la cookie httpOnly que lleva el JWT.</summary>
public class SessionCookie(IOptions<JwtOptions> options)
{
    private readonly JwtOptions _jwt = options.Value;

    public void Append(HttpContext http, string token, bool remember, DateTime expiresUtc)
    {
        http.Response.Cookies.Append(_jwt.CookieName, token, Build(remember ? expiresUtc : null));
    }

    public void Clear(HttpContext http)
    {
        http.Response.Cookies.Delete(_jwt.CookieName, Build(null));
    }

    private CookieOptions Build(DateTime? expiresUtc) => new()
    {
        HttpOnly = true,
        Secure = _jwt.CookieSecure,
        SameSite = _jwt.CookieSameSite.ToLowerInvariant() switch
        {
            "none" => SameSiteMode.None,
            "strict" => SameSiteMode.Strict,
            _ => SameSiteMode.Lax,
        },
        Path = "/",
        // Sin Expires es una cookie de sesion: se borra al cerrar el navegador (login sin "recordarme").
        Expires = expiresUtc is null ? null : new DateTimeOffset(expiresUtc.Value),
        IsEssential = true,
    };
}

/// <summary>
/// Proteccion CSRF para el flujo con cookie: si el navegador manda Origin en una peticion que modifica
/// datos, tiene que ser uno de los permitidos. El webhook no usa cookie, asi que no le afecta.
/// </summary>
public class OriginCheckMiddleware(RequestDelegate next, string[] allowedOrigins, IOptions<JwtOptions> jwt)
{
    private static readonly HashSet<string> Safe = ["GET", "HEAD", "OPTIONS", "TRACE"];

    public Task InvokeAsync(HttpContext context)
    {
        var unsafeMethod = !Safe.Contains(context.Request.Method);
        var usesCookie = context.Request.Cookies.ContainsKey(jwt.Value.CookieName);
        var origin = context.Request.Headers.Origin.ToString();

        if (unsafeMethod && usesCookie && !string.IsNullOrEmpty(origin)
            && !allowedOrigins.Contains(origin.TrimEnd('/'), StringComparer.OrdinalIgnoreCase))
        {
            throw AppException.Forbidden("Origen no permitido.", "origin_not_allowed");
        }

        return next(context);
    }
}
