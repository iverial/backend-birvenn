using BirvennApi.Dtos;
using BirvennApi.Infrastructure;
using BirvennApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace BirvennApi.Controllers;

[ApiController]
[Route("api/auth")]
[EnableRateLimiting("auth")]
public class AuthController(AuthService auth, TokenService tokens, SessionCookie cookie) : ControllerBase
{
    /// <summary>Crea la cuenta, genera el cupon de bienvenida personal e inicia sesion (cookie httpOnly).</summary>
    [HttpPost("register")]
    [ProducesResponseType<AuthResponse>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Register(RegisterRequest request, CancellationToken ct)
    {
        var created = await auth.RegisterAsync(request, ct);
        var user = await auth.GetAsync(created.Id, ct);
        StartSession(user, remember: true);
        return StatusCode(StatusCodes.Status201Created, new AuthResponse(user.ToDto(), IsNew: true));
    }

    [HttpPost("login")]
    public async Task<ActionResult<AuthResponse>> Login(LoginRequest request, CancellationToken ct)
    {
        var user = await auth.LoginAsync(request, ct);
        StartSession(user, request.Remember);
        return new AuthResponse(user.ToDto(), IsNew: false);
    }

    [HttpPost("logout")]
    [AllowAnonymous]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        if (User.GetUserId() is { } id) await auth.LogoutAsync(id, ct);
        cookie.Clear(HttpContext);
        return NoContent();
    }

    /// <summary>Usuario de la sesion actual, con cupones y actividad al dia (reemplaza `refresh` del store).</summary>
    [HttpGet("me")]
    [Authorize]
    public async Task<ActionResult<UserDto>> Me(CancellationToken ct) =>
        (await auth.GetAsync(User.GetUserId()!.Value, ct)).ToDto();

    private void StartSession(Models.User user, bool remember)
    {
        var (token, expires) = tokens.Issue(user, remember);
        cookie.Append(HttpContext, token, remember, expires);
    }
}

[ApiController]
[Route("api/account")]
[Authorize]
public class AccountController(AuthService auth) : ControllerBase
{
    [HttpPatch("profile")]
    [HttpPut("profile")]
    public async Task<ActionResult<UserDto>> UpdateProfile(UpdateProfileRequest request, CancellationToken ct) =>
        (await auth.UpdateProfileAsync(User.GetUserId()!.Value, request, ct)).ToDto();

    [HttpGet("coupons")]
    public async Task<ActionResult<List<CouponDto>>> Coupons(CancellationToken ct) =>
        (await auth.GetAsync(User.GetUserId()!.Value, ct)).ToDto().Coupons;
}
