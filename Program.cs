using System.Text;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using BirvennApi.Data;
using BirvennApi.Infrastructure;
using BirvennApi.Models;
using BirvennApi.Options;
using BirvennApi.Payments;
using BirvennApi.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;

// .env (mismo criterio que el resto de los backends). En produccion alcanzan las variables de entorno.
DotNetEnv.Env.Load();

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

// Render y Cloud Run indican el puerto en PORT.
if (Environment.GetEnvironmentVariable("PORT") is { Length: > 0 } port)
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

// ---------- Opciones ----------
builder.Services.Configure<DatabaseOptions>(config.GetSection(DatabaseOptions.Section));
builder.Services.Configure<JwtOptions>(config.GetSection(JwtOptions.Section));
builder.Services.Configure<UrlsOptions>(config.GetSection(UrlsOptions.Section));
builder.Services.Configure<AdminOptions>(config.GetSection(AdminOptions.Section));
builder.Services.Configure<PaymentsOptions>(config.GetSection(PaymentsOptions.Section));
builder.Services.Configure<MercadoPagoOptions>(config.GetSection(MercadoPagoOptions.Section));
builder.Services.Configure<CommerceOptions>(config.GetSection(CommerceOptions.Section));
builder.Services.Configure<RateLimitOptions>(config.GetSection(RateLimitOptions.Section));
builder.Services.Configure<NetworkOptions>(config.GetSection(NetworkOptions.Section));

var dbOptions = config.GetSection(DatabaseOptions.Section).Get<DatabaseOptions>() ?? new();
var jwt = config.GetSection(JwtOptions.Section).Get<JwtOptions>() ?? new();
var urls = config.GetSection(UrlsOptions.Section).Get<UrlsOptions>() ?? new();
var paymentsOptions = config.GetSection(PaymentsOptions.Section).Get<PaymentsOptions>() ?? new();
var mercadoPago = config.GetSection(MercadoPagoOptions.Section).Get<MercadoPagoOptions>() ?? new();
var rateLimits = config.GetSection(RateLimitOptions.Section).Get<RateLimitOptions>() ?? new();
var network = config.GetSection(NetworkOptions.Section).Get<NetworkOptions>() ?? new();
var corsOrigins = config.GetCorsOrigins();

// Falla al arrancar (no a las 3 de la manana) si la configuracion no sirve.
if (jwt.Key.Length < 32)
    throw new InvalidOperationException("Jwt:Key tiene que tener al menos 32 caracteres (variable Jwt__Key).");

var useMercadoPago = paymentsOptions.Provider.Equals("MercadoPago", StringComparison.OrdinalIgnoreCase);
if (!useMercadoPago && !paymentsOptions.Provider.Equals("Mock", StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException($"Payments:Provider '{paymentsOptions.Provider}' no existe. Usá Mock o MercadoPago.");

// El Mock aprueba cualquier pago al instante: en produccion seria regalar la mercaderia.
if (builder.Environment.IsProduction() && !useMercadoPago)
    throw new InvalidOperationException(
        "Payments:Provider=Mock aprueba cualquier pago y no se permite en Production. Usá MercadoPago (con el Access Token de prueba TEST-... mientras probás).");

if (useMercadoPago)
{
    if (string.IsNullOrWhiteSpace(mercadoPago.AccessToken))
        throw new InvalidOperationException("Payments:Provider=MercadoPago requiere MercadoPago:AccessToken (MercadoPago__AccessToken).");
    if (string.IsNullOrWhiteSpace(mercadoPago.WebhookSecret))
        throw new InvalidOperationException("Payments:Provider=MercadoPago requiere MercadoPago:WebhookSecret (MercadoPago__WebhookSecret).");
}

if (corsOrigins.Length == 0)
    Console.WriteLine("ADVERTENCIA: Cors:Origins esta vacio; el navegador va a bloquear las llamadas del front.");

// ---------- Base de datos ----------
var useSqlite = dbOptions.Provider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase);
builder.Services.AddDbContext<AppDbContext>(options =>
{
    var connection = config.GetConnectionString("Default");
    if (useSqlite)
    {
        options.UseSqlite(string.IsNullOrWhiteSpace(connection) ? "Data Source=birvenn.db" : connection);
    }
    else
    {
        if (string.IsNullOrWhiteSpace(connection))
            throw new InvalidOperationException("Falta la cadena de conexion (ConnectionStrings__Default). Para probar sin Postgres usá Database__Provider=Sqlite.");
        options.UseNpgsql(ConnectionStrings.NormalizePostgres(connection));
    }
});

// ---------- Autenticacion: JWT en cookie httpOnly (o en header Authorization) ----------
builder.Services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    options.MapInboundClaims = false;
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidIssuer = jwt.Issuer,
        ValidateAudience = true,
        ValidAudience = jwt.Audience,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Key)),
        ClockSkew = TimeSpan.FromMinutes(1),
        NameClaimType = "sub",
        RoleClaimType = "role",
    };
    options.Events = new JwtBearerEvents
    {
        // Si no viene Authorization, el token esta en la cookie de sesion.
        OnMessageReceived = context =>
        {
            if (string.IsNullOrEmpty(context.Token) && context.Request.Cookies.TryGetValue(jwt.CookieName, out var token))
                context.Token = token;
            return Task.CompletedTask;
        },
    };
});
builder.Services.AddAuthorization();

// ---------- CORS ----------
builder.Services.AddCors(options => options.AddPolicy("front", policy =>
{
    if (corsOrigins.Length > 0) policy.WithOrigins(corsOrigins).AllowAnyHeader().AllowAnyMethod().AllowCredentials();
}));

// ---------- Rate limiting por IP ----------
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, token) =>
    {
        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        await context.HttpContext.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = 429,
            Title = ApiExceptionHandler.Title(429),
            Detail = "Hiciste demasiados intentos. Esperá un momento y probá de nuevo.",
            Extensions = { ["code"] = "rate_limited" },
        }, token);
    };

    static RateLimitPartition<string> PerIp(HttpContext http, int perMinute) =>
        RateLimitPartition.GetFixedWindowLimiter(
            http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = perMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 });

    options.AddPolicy("auth", http => PerIp(http, rateLimits.AuthPerMinute));
    options.AddPolicy("checkout", http => PerIp(http, rateLimits.CheckoutPerMinute));
    options.AddPolicy("public", http => PerIp(http, rateLimits.PublicPerMinute));
});

if (network.TrustProxy)
{
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
    });
}

// ---------- Servicios de negocio ----------
builder.Services.AddScoped<TokenService>();
builder.Services.AddScoped<SessionCookie>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<CouponService>();
builder.Services.AddScoped<CatalogService>();
builder.Services.AddScoped<OrderLifecycle>();
builder.Services.AddScoped<OrderService>();
builder.Services.AddScoped<CheckoutService>();
builder.Services.AddScoped<PaymentProcessor>();
builder.Services.AddSingleton<PricingService>();
builder.Services.AddHostedService<PendingOrderExpirationWorker>();

// Pasarela de pago activa. Para sumar otra: implementar IPaymentGateway y agregar un caso aca.
builder.Services.AddHttpClient<MercadoPagoClient>(client =>
{
    client.BaseAddress = new Uri(mercadoPago.BaseUrl.TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromSeconds(20);
    if (!string.IsNullOrWhiteSpace(mercadoPago.AccessToken))
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", mercadoPago.AccessToken);
});
if (useMercadoPago) builder.Services.AddScoped<IPaymentGateway, MercadoPagoGateway>();
else builder.Services.AddSingleton<IPaymentGateway, MockGateway>();

// ---------- MVC / API ----------
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services
    .AddControllers(options =>
    {
        // La validacion es explicita (con mensajes en espanol): no se agrega [Required] implicito a cada string.
        options.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes = true;
    })
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
        options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    })
    .ConfigureApiBehaviorOptions(options =>
    {
        options.InvalidModelStateResponseFactory = context =>
        {
            var errors = context.ModelState.Values.SelectMany(v => v.Errors).ToList();
            // Un error de parseo del JSON (enum invalido, tipo incorrecto) no se le muestra tal cual al cliente.
            var detail = errors.FirstOrDefault(e => e.Exception is null && !string.IsNullOrWhiteSpace(e.ErrorMessage))?.ErrorMessage
                         ?? "Solicitud inválida.";
            return new UnprocessableEntityObjectResult(new ValidationProblemDetails(context.ModelState)
            {
                Status = 422,
                Title = ApiExceptionHandler.Title(422),
                Detail = detail,
                Extensions = { ["code"] = "validation_failed" },
            });
        };
    });

var swaggerEnabled = builder.Environment.IsDevelopment() || config.GetValue<bool>("Swagger:Enabled");
if (swaggerEnabled)
{
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen(c =>
    {
        c.SwaggerDoc("v1", new OpenApiInfo { Title = "BIRVENN API", Version = "v1" });
        c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
        {
            Name = "Authorization",
            Type = SecuritySchemeType.ApiKey,
            Scheme = "Bearer",
            BearerFormat = "JWT",
            In = ParameterLocation.Header,
            Description = "Opcional: la sesion normal viaja en la cookie httpOnly. Para probar desde aca: 'Bearer ' + token.",
        });
        c.AddSecurityRequirement(document => new OpenApiSecurityRequirement
        {
            [new OpenApiSecuritySchemeReference("Bearer", document)] = [],
        });
    });
}

// ---------- Pipeline ----------
var app = builder.Build();

if (network.TrustProxy) app.UseForwardedHeaders();

app.UseExceptionHandler();
app.UseStatusCodePages();

app.Use(async (context, next) =>
{
    // OnStarting corre justo antes de enviar la respuesta: asi tambien llegan en las respuestas de error,
    // porque el manejador de excepciones limpia los headers que se hubieran puesto antes.
    context.Response.OnStarting(() =>
    {
        var headers = context.Response.Headers;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["X-Frame-Options"] = "DENY";
        headers["Referrer-Policy"] = "no-referrer";

        // Respuestas con datos de cuenta o pedidos: nunca en cache compartido.
        var path = context.Request.Path;
        if (path.StartsWithSegments("/api/auth") || path.StartsWithSegments("/api/account")
            || path.StartsWithSegments("/api/orders") || path.StartsWithSegments("/api/admin"))
            headers.CacheControl = "no-store";

        return Task.CompletedTask;
    });

    await next();
});

if (swaggerEnabled)
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseRouting();
app.UseCors("front");
// (object): sin el cast, el string[] se interpreta como la lista de argumentos y no como UN argumento.
app.UseMiddleware<OriginCheckMiddleware>((object)corsOrigins);
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapControllers();
app.MapGet("/", () => Results.Ok(new { name = "BIRVENN API", status = "ok" }));
app.MapGet("/health", async (AppDbContext db, CancellationToken ct) =>
    await db.Database.CanConnectAsync(ct) ? Results.Ok(new { status = "ok" }) : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));

// ---------- Base y datos iniciales ----------
await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    if (useSqlite) await db.Database.EnsureCreatedAsync();
    else if (dbOptions.AutoMigrate) await db.Database.MigrateAsync();
}

await SeedData.RunAsync(app.Services);

app.Run();
