using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace BirvennApi.Infrastructure;

/// <summary>Error de negocio con un mensaje apto para mostrar al cliente (es-AR).</summary>
public class AppException(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;

    public static AppException BadRequest(string message, string code = "bad_request") => new(400, code, message);
    public static AppException Unauthorized(string message = "Tenés que iniciar sesión.", string code = "unauthorized") => new(401, code, message);
    public static AppException Forbidden(string message = "No tenés permiso para hacer esto.", string code = "forbidden") => new(403, code, message);
    public static AppException NotFound(string message, string code = "not_found") => new(404, code, message);
    public static AppException Conflict(string message, string code = "conflict") => new(409, code, message);
    public static AppException Unprocessable(string message, string code = "invalid") => new(422, code, message);
}

/// <summary>
/// Convierte cualquier excepcion en un ProblemDetails (RFC 9457). `detail` es el mensaje para el usuario
/// y `code` un identificador estable para que el front decida que hacer sin parsear texto.
/// </summary>
public class ApiExceptionHandler(ILogger<ApiExceptionHandler> logger, IProblemDetailsService problems) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        int status;
        string code;
        string detail;

        switch (exception)
        {
            case AppException app:
                status = app.Status;
                code = app.Code;
                detail = app.Message;
                if (status >= 500) logger.LogError(exception, "Error de negocio {Code}", code);
                else logger.LogInformation("{Code}: {Message}", code, app.Message);
                break;

            case BadHttpRequestException:
                status = StatusCodes.Status400BadRequest;
                code = "bad_request";
                detail = "Solicitud inválida.";
                break;

            default:
                status = StatusCodes.Status500InternalServerError;
                code = "internal_error";
                detail = "Algo salió mal de nuestro lado. Probá de nuevo en unos minutos.";
                logger.LogError(exception, "Error no controlado en {Method} {Path}", context.Request.Method, context.Request.Path);
                break;
        }

        context.Response.StatusCode = status;
        return await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            Exception = exception,
            ProblemDetails = new ProblemDetails
            {
                Status = status,
                Title = Title(status),
                Detail = detail,
                Extensions = { ["code"] = code },
            },
        });
    }

    public static string Title(int status) => status switch
    {
        400 => "Solicitud inválida",
        401 => "No autenticado",
        403 => "Sin permiso",
        404 => "No encontrado",
        409 => "Conflicto",
        422 => "Datos inválidos",
        429 => "Demasiadas solicitudes",
        502 => "Error con la pasarela de pago",
        _ => "Error",
    };
}
