using System.Globalization;
using System.Security.Claims;
using System.Text;
using BirvennApi.Data;
using BirvennApi.Dtos;
using BirvennApi.Infrastructure;
using BirvennApi.Models;
using BirvennApi.Options;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace BirvennApi.Services;

public class TokenService(IOptions<JwtOptions> options)
{
    private readonly JwtOptions _jwt = options.Value;

    public (string Token, DateTime ExpiresUtc) Issue(User user, bool remember)
    {
        var expires = DateTime.UtcNow.Add(remember ? TimeSpan.FromDays(_jwt.RememberDays) : TimeSpan.FromHours(_jwt.SessionHours));

        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(
            [
                new Claim("sub", user.Id.ToString()),
                new Claim("email", user.Email),
                new Claim("role", user.Role),
            ]),
            Issuer = _jwt.Issuer,
            Audience = _jwt.Audience,
            Expires = expires,
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwt.Key)), SecurityAlgorithms.HmacSha256),
        };

        return (new JsonWebTokenHandler().CreateToken(descriptor), expires);
    }
}

/// <summary>Historial de actividad de la cuenta (maximo 50 entradas, como en el front).</summary>
public static class ActivityLog
{
    private const int Max = 50;

    public static async Task LogAsync(AppDbContext db, Guid userId, ActivityType type, string message, CancellationToken ct = default)
    {
        db.Activity.Add(new ActivityEntry { UserId = userId, Type = type, Message = message });
        await db.SaveChangesAsync(ct);

        var stale = await db.Activity
            .Where(a => a.UserId == userId)
            .OrderByDescending(a => a.CreatedAt)
            .Skip(Max)
            .Select(a => a.Id)
            .ToListAsync(ct);

        if (stale.Count > 0) await db.Activity.Where(a => stale.Contains(a.Id)).ExecuteDeleteAsync(ct);
    }
}

public class AuthService(
    AppDbContext db,
    IPasswordHasher<User> hasher,
    CouponService coupons,
    OrderLifecycle orders,
    IOptions<CommerceOptions> commerce,
    ILogger<AuthService> logger)
{
    private const int MaxFailedLogins = 5;
    private static readonly TimeSpan LockoutTime = TimeSpan.FromMinutes(15);

    // Hash de relleno para que el login de un email inexistente tarde lo mismo que uno real.
    private static readonly User DummyUser = new();
    private static readonly string DummyHash = new PasswordHasher<User>().HashPassword(DummyUser, "birvenn-dummy-password-1");

    public async Task<User> RegisterAsync(RegisterRequest request, CancellationToken ct)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        if (await db.Users.AnyAsync(u => u.Email == email, ct))
            throw AppException.Conflict("Ya existe una cuenta con ese email. Probá ingresar.", "email_taken");

        var user = new User
        {
            Email = email,
            FirstName = request.FirstName.Trim(),
            LastName = request.LastName.Trim(),
            Newsletter = request.Newsletter,
        };
        user.PasswordHash = hasher.HashPassword(user, request.Password);

        var welcome = await coupons.CreateWelcomeAsync(user.Id, ct);
        user.Coupons.Add(welcome);
        user.Activity.Add(new ActivityEntry { UserId = user.Id, Type = ActivityType.Registered, Message = "Creaste tu cuenta BIRVENN" });
        user.Activity.Add(new ActivityEntry { UserId = user.Id, Type = ActivityType.CouponReceived, Message = $"Recibiste el cupón {welcome.Code}" });

        db.Users.Add(user);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Dos registros simultaneos con el mismo email: gana el que llego primero.
            throw AppException.Conflict("Ya existe una cuenta con ese email. Probá ingresar.", "email_taken");
        }

        if (commerce.Value.ClaimGuestOrdersByEmail) await orders.ClaimGuestOrdersAsync(user.Email, user.Id, ct);

        logger.LogInformation("Usuario registrado {UserId}", user.Id);
        return user;
    }

    public async Task<User> LoginAsync(LoginRequest request, CancellationToken ct)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        var invalid = AppException.Unauthorized("Email o contraseña incorrectos.", "invalid_credentials");

        var user = await db.Users.Include(u => u.Coupons).Include(u => u.Activity).FirstOrDefaultAsync(u => u.Email == email, ct);
        if (user is null)
        {
            hasher.VerifyHashedPassword(DummyUser, DummyHash, request.Password);
            throw invalid;
        }

        if (user.LockoutEnd is { } until && until > DateTime.UtcNow)
            throw new AppException(429, "locked_out", "Demasiados intentos. Probá de nuevo en unos minutos.");

        var result = hasher.VerifyHashedPassword(user, user.PasswordHash, request.Password);
        if (result == PasswordVerificationResult.Failed)
        {
            user.FailedLogins++;
            if (user.FailedLogins >= MaxFailedLogins)
            {
                user.LockoutEnd = DateTime.UtcNow.Add(LockoutTime);
                user.FailedLogins = 0;
                logger.LogWarning("Cuenta {UserId} bloqueada temporalmente por intentos fallidos", user.Id);
            }
            await db.SaveChangesAsync(ct);
            throw invalid;
        }

        user.FailedLogins = 0;
        user.LockoutEnd = null;
        if (result == PasswordVerificationResult.SuccessRehashNeeded) user.PasswordHash = hasher.HashPassword(user, request.Password);
        await db.SaveChangesAsync(ct);

        await ActivityLog.LogAsync(db, user.Id, ActivityType.SignedIn, "Iniciaste sesión", ct);

        if (commerce.Value.ClaimGuestOrdersByEmail) await orders.ClaimGuestOrdersAsync(user.Email, user.Id, ct);

        return await GetAsync(user.Id, ct);
    }

    public async Task<User> GetAsync(Guid id, CancellationToken ct)
    {
        return await db.Users.AsNoTracking().Include(u => u.Coupons).Include(u => u.Activity).AsSplitQuery()
                   .FirstOrDefaultAsync(u => u.Id == id, ct)
               ?? throw AppException.Unauthorized("Tu sesión ya no es válida.", "session_invalid");
    }

    public async Task<User> UpdateProfileAsync(Guid id, UpdateProfileRequest request, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct)
                   ?? throw AppException.Unauthorized("Tu sesión ya no es válida.", "session_invalid");

        DateOnly? birthday = null;
        if (!string.IsNullOrWhiteSpace(request.Birthday))
        {
            if (!DateOnly.TryParseExact(request.Birthday, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
                || parsed > DateOnly.FromDateTime(DateTime.UtcNow) || parsed.Year < 1900)
                throw AppException.Unprocessable("Fecha de nacimiento inválida.");
            birthday = parsed;
        }

        user.FirstName = request.FirstName.Trim();
        user.LastName = request.LastName.Trim();
        user.Phone = string.IsNullOrWhiteSpace(request.Phone) ? null : request.Phone.Trim();
        user.Birthday = birthday;
        user.Newsletter = request.Newsletter;
        await db.SaveChangesAsync(ct);

        await ActivityLog.LogAsync(db, user.Id, ActivityType.ProfileUpdated, "Actualizaste tus datos", ct);
        return await GetAsync(user.Id, ct);
    }

    public Task LogoutAsync(Guid id, CancellationToken ct) =>
        ActivityLog.LogAsync(db, id, ActivityType.SignedOut, "Cerraste sesión", ct);
}
