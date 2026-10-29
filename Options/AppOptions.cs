namespace BirvennApi.Options;

public class DatabaseOptions
{
    public const string Section = "Database";

    /// <summary>"Postgres" (produccion, Supabase/Neon/Render) o "Sqlite" (desarrollo local sin instalar nada).</summary>
    public string Provider { get; set; } = "Postgres";

    /// <summary>Aplica las migraciones al arrancar (solo Postgres). En Sqlite se usa EnsureCreated.</summary>
    public bool AutoMigrate { get; set; } = true;

    /// <summary>Carga el catalogo inicial si no hay productos.</summary>
    public bool SeedCatalog { get; set; } = true;
}

public class JwtOptions
{
    public const string Section = "Jwt";

    public string Key { get; set; } = "";
    public string Issuer { get; set; } = "birvenn-api";
    public string Audience { get; set; } = "birvenn-web";
    public string CookieName { get; set; } = "birvenn_session";

    /// <summary>Duracion de la sesion sin "recordarme".</summary>
    public double SessionHours { get; set; } = 12;

    /// <summary>Duracion con "recordarme".</summary>
    public int RememberDays { get; set; } = 30;

    /// <summary>Lax (front y API en el mismo sitio, o proxy de Next) o None (dominios distintos, exige HTTPS).</summary>
    public string CookieSameSite { get; set; } = "Lax";

    public bool CookieSecure { get; set; } = true;
}

public class UrlsOptions
{
    public const string Section = "Urls";

    /// <summary>URL publica del front. Se usa para las back_urls de Mercado Pago.</summary>
    public string Frontend { get; set; } = "http://localhost:3000";

    /// <summary>URL publica de esta API. Debe ser https para que Mercado Pago pueda llamar al webhook.</summary>
    public string PublicApi { get; set; } = "http://localhost:5180";
}

public class RateLimitOptions
{
    public const string Section = "RateLimit";

    /// <summary>Login y registro, por IP y por minuto.</summary>
    public int AuthPerMinute { get; set; } = 10;

    /// <summary>Crear pedidos, por IP y por minuto.</summary>
    public int CheckoutPerMinute { get; set; } = 15;

    /// <summary>Endpoints publicos de escritura o consulta de pedidos/cupones, por IP y por minuto.</summary>
    public int PublicPerMinute { get; set; } = 60;
}

public class NetworkOptions
{
    public const string Section = "Network";

    /// <summary>
    /// Confiar en X-Forwarded-For / X-Forwarded-Proto. Activar SOLO detras de un proxy que los controla
    /// (Render, Cloud Run, Nginx). Sin esto el rate limiting veria la IP del proxy.
    /// </summary>
    public bool TrustProxy { get; set; }
}

public class AdminOptions
{
    public const string Section = "Admin";

    public string Email { get; set; } = "";
    public string Password { get; set; } = "";
}

public class PaymentsOptions
{
    public const string Section = "Payments";

    /// <summary>"Mock" (aprueba tarjetas/MP al instante) o "MercadoPago".</summary>
    public string Provider { get; set; } = "Mock";

    /// <summary>Habilita POST /api/dev/payments/simulate (solo con Provider=Mock).</summary>
    public bool AllowSimulation { get; set; }
}

public class MercadoPagoOptions
{
    public const string Section = "MercadoPago";

    public string AccessToken { get; set; } = "";

    /// <summary>Clave secreta de Webhooks (Tus integraciones > Webhooks > Configurar notificaciones).</summary>
    public string WebhookSecret { get; set; } = "";

    public string BaseUrl { get; set; } = "https://api.mercadopago.com/";
    public string StatementDescriptor { get; set; } = "BIRVENN";

    /// <summary>Usar sandbox_init_point en lugar de init_point (casi nunca hace falta con credenciales de prueba).</summary>
    public bool UseSandboxInitPoint { get; set; }
}

public class CommerceOptions
{
    public const string Section = "Commerce";

    public string Currency { get; set; } = "ARS";

    /// <summary>Subtotal a partir del cual el envio estandar es gratis.</summary>
    public decimal FreeShippingThreshold { get; set; } = 150_000;

    /// <summary>Cuotas maximas que se ofrecen con tarjeta. Las cuotas sin interes se configuran en la cuenta de Mercado Pago.</summary>
    public int Installments { get; set; } = 3;

    /// <summary>Descuento extra por transferencia (0 a 1).</summary>
    public decimal TransferDiscount { get; set; } = 0.10m;

    public int WelcomeCouponPercent { get; set; } = 15;
    public int WelcomeCouponDays { get; set; } = 30;
    public int MaxQuantityPerItem { get; set; } = 10;

    /// <summary>Horas que se reserva un pedido sin pagar antes de cancelarlo y devolver el stock.</summary>
    public double PendingExpirationHours { get; set; } = 48;

    /// <summary>Cada cuantos segundos corre el worker que vence pedidos.</summary>
    public int ExpirationIntervalSeconds { get; set; } = 300;

    /// <summary>
    /// Por seguridad NO se asocian pedidos de invitado a una cuenta nueva solo por coincidir el email
    /// (cualquiera podria registrarse con el email de otro). Activar cuando exista verificacion de email.
    /// </summary>
    public bool ClaimGuestOrdersByEmail { get; set; }

    /// <summary>Texto que se muestra al elegir transferencia (datos bancarios).</summary>
    public string TransferInstructions { get; set; } =
        "Te enviamos los datos bancarios por email. Tenes 48 h para transferir.";
}
