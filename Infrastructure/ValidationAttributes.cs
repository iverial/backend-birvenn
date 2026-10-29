using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;

namespace BirvennApi.Infrastructure;

/// <summary>Mismas reglas que el esquema Zod del front: 8 a 72 caracteres, al menos una letra y un numero.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class PasswordRulesAttribute : ValidationAttribute
{
    protected override ValidationResult? IsValid(object? value, ValidationContext context)
    {
        if (value is not string s) return ValidationResult.Success; // [Required] se encarga del vacio
        if (s.Length < 8) return new ValidationResult("Mínimo 8 caracteres");
        if (s.Length > 72) return new ValidationResult("Máximo 72 caracteres");
        if (!Regex.IsMatch(s, "[A-Za-z]")) return new ValidationResult("Tiene que incluir al menos una letra");
        if (!Regex.IsMatch(s, @"\d")) return new ValidationResult("Tiene que incluir al menos un número");
        return ValidationResult.Success;
    }
}

[AttributeUsage(AttributeTargets.Property)]
public sealed class MustBeTrueAttribute : ValidationAttribute
{
    public MustBeTrueAttribute() : base("Tenés que aceptar los términos") { }

    public override bool IsValid(object? value) => value is true;
}

/// <summary>El valor tiene que ser uno de la lista (talles, provincias...). Vacio/nulo se valida con [Required].</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter)]
public sealed class OneOfAttribute(params string[] allowed) : ValidationAttribute
{
    public override bool IsValid(object? value) => value is null or "" || allowed.Contains(value.ToString(), StringComparer.Ordinal);
}

public static class Rules
{
    public static readonly string[] Sizes = ["XS", "S", "M", "L", "XL", "XXL", "U"];

    public static readonly string[] Provinces =
    [
        "CABA", "Buenos Aires", "Catamarca", "Chaco", "Chubut", "Córdoba", "Corrientes", "Entre Ríos", "Formosa",
        "Jujuy", "La Pampa", "La Rioja", "Mendoza", "Misiones", "Neuquén", "Río Negro", "Salta", "San Juan",
        "San Luis", "Santa Cruz", "Santa Fe", "Santiago del Estero", "Tierra del Fuego", "Tucumán",
    ];

    public const string PersonName = @"^[\p{L}\s'-]+$";
    public const string Phone = @"^[\d\s+()-]{8,20}$";
    public const string PhoneOptional = @"^[\d\s+()-]{0,20}$";
    public const string Zip = @"^[A-Za-z]?\d{4}[A-Za-z]{0,3}$";
}
