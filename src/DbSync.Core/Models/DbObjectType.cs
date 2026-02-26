namespace DbSync.Core.Models;

public enum DbObjectType
{
    StoredProcedure,
    View,
    ScalarFunction,
    TableValuedFunction,
    InlineFunction
}

public static class DbObjectTypeExtensions
{
    public static string ToSqlType(this DbObjectType type) => type switch
    {
        DbObjectType.StoredProcedure => "P",
        DbObjectType.View => "V",
        DbObjectType.ScalarFunction => "FN",
        DbObjectType.TableValuedFunction => "TF",
        DbObjectType.InlineFunction => "IF",
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    public static DbObjectType FromSqlType(string sqlType) => sqlType.Trim() switch
    {
        "P" => DbObjectType.StoredProcedure,
        "V" => DbObjectType.View,
        "FN" => DbObjectType.ScalarFunction,
        "TF" => DbObjectType.TableValuedFunction,
        "IF" => DbObjectType.InlineFunction,
        _ => throw new ArgumentOutOfRangeException(nameof(sqlType), $"Tipo SQL desconocido: {sqlType}")
    };

    public static string ToDisplayName(this DbObjectType type) => type switch
    {
        DbObjectType.StoredProcedure => "Stored Procedure",
        DbObjectType.View => "Vista",
        DbObjectType.ScalarFunction => "Función Escalar",
        DbObjectType.TableValuedFunction => "Función Table-Valued",
        DbObjectType.InlineFunction => "Función Inline",
        _ => type.ToString()
    };

    public static string ToShortCode(this DbObjectType type) => type switch
    {
        DbObjectType.StoredProcedure => "SP",
        DbObjectType.View => "VIEW",
        DbObjectType.ScalarFunction or DbObjectType.TableValuedFunction or DbObjectType.InlineFunction => "FN",
        _ => type.ToString()
    };
}
