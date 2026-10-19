using System.Text.Json;
using System.Text.Json.Serialization;

namespace TerminalV.Data;

/// <summary>
/// Maps JSON null onto default(T) for value types.
/// System.Text.Json throws "requires an element of type 'Number' … 'Null'" otherwise.
/// </summary>
internal sealed class NullToDefaultConverter<T> : JsonConverter<T> where T : struct
{
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return default;
        if (typeof(T) == typeof(int)) return (T)(object)ReadInt(ref reader);
        if (typeof(T) == typeof(long)) return (T)(object)ReadLong(ref reader);
        if (typeof(T) == typeof(double)) return (T)(object)ReadDouble(ref reader);
        if (typeof(T) == typeof(bool)) return (T)(object)ReadBool(ref reader);
        throw new JsonException($"Unsupported NullToDefault type: {typeof(T).Name}.");
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        // Write the primitive directly; JsonSerializer.Serialize would re-enter this converter.
        if (typeof(T) == typeof(int)) writer.WriteNumberValue((int)(object)value);
        else if (typeof(T) == typeof(long)) writer.WriteNumberValue((long)(object)value);
        else if (typeof(T) == typeof(double)) writer.WriteNumberValue((double)(object)value);
        else if (typeof(T) == typeof(bool)) writer.WriteBooleanValue((bool)(object)value);
        else throw new JsonException($"Unsupported NullToDefault type: {typeof(T).Name}.");
    }

    private static int ReadInt(ref Utf8JsonReader reader)
        => reader.TokenType == JsonTokenType.Number ? reader.GetInt32()
            : reader.TokenType == JsonTokenType.String && int.TryParse(reader.GetString(), out var s) ? s : 0;

    private static long ReadLong(ref Utf8JsonReader reader)
        => reader.TokenType == JsonTokenType.Number ? reader.GetInt64()
            : reader.TokenType == JsonTokenType.String && long.TryParse(reader.GetString(), out var s) ? s : 0;

    private static double ReadDouble(ref Utf8JsonReader reader)
        => reader.TokenType == JsonTokenType.Number ? reader.GetDouble()
            : reader.TokenType == JsonTokenType.String && double.TryParse(reader.GetString(), out var s) ? s : 0;

    private static bool ReadBool(ref Utf8JsonReader reader)
        => reader.TokenType switch
        {
            JsonTokenType.True => true,
            JsonTokenType.False => false,
            JsonTokenType.String => bool.TryParse(reader.GetString(), out var b) && b,
            _ => false
        };
}

internal static class JsonDefaults
{
    public static readonly NullToDefaultConverter<int> Int = new();
    public static readonly NullToDefaultConverter<long> Long = new();
    public static readonly NullToDefaultConverter<double> Double = new();
    public static readonly NullToDefaultConverter<bool> Bool = new();

    public static void AddNullToDefault(JsonSerializerOptions options)
    {
        options.Converters.Add(Int);
        options.Converters.Add(Long);
        options.Converters.Add(Double);
        options.Converters.Add(Bool);
    }
}
