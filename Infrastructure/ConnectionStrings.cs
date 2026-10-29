using Npgsql;

namespace BirvennApi.Infrastructure;

public static class ConnectionStrings
{
    /// <summary>
    /// Supabase, Neon y Render entregan la conexion como URL (postgresql://usuario:clave@host:5432/base) y Npgsql
    /// espera "Host=...;Username=...". Acepta las dos formas.
    /// </summary>
    public static string NormalizePostgres(string value)
    {
        value = value.Trim();
        if (!value.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase)
            && !value.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
            return value;

        var uri = new Uri(value);
        var credentials = uri.UserInfo.Split(':', 2);
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);

        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.Port > 0 ? uri.Port : 5432,
            Username = Uri.UnescapeDataString(credentials[0]),
            Password = credentials.Length > 1 ? Uri.UnescapeDataString(credentials[1]) : null,
            Database = uri.AbsolutePath.Trim('/'),
            // Los proveedores gestionados exigen TLS; sslmode=disable en la URL lo apaga (util para Postgres local).
            // "Require" cifra sin validar la cadena (Supabase firma con su propia CA, que el contenedor no conoce).
            SslMode = string.Equals(query["sslmode"], "disable", StringComparison.OrdinalIgnoreCase) ? SslMode.Disable : SslMode.Require,
        };

        return builder.ConnectionString;
    }
}
