using System.Text.Json;

namespace ILens;

/// <summary>
/// The names the tool schema gives enum values. Program.cs serializes every enum with
/// <see cref="Policy"/>, so a message naming a value the caller can pass writes it
/// through <see cref="ToSchemaName"/> and agrees with the schema.
/// </summary>
public static class SchemaNames
{
    public static readonly JsonNamingPolicy Policy = JsonNamingPolicy.SnakeCaseLower;

    public static string ToSchemaName(this Enum value) => Policy.ConvertName(value.ToString());
}
