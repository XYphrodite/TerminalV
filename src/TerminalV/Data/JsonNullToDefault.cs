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
        if (typeof(T) == typeof(int)) return (T)(object)reader.GetInt32();
        if (typeof(T) == typeof(long)) return (T)(object)reader.GetInt64();
        if (typeof(T) == typeof(double)) return (T)(object)reader.GetDouble();
        if (typeof(T) == typeof(bool)) return (T)(object)reader.GetBoolean();
        return JsonSerializer.Deserialize<T>(ref reader, options);
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        => JsonSerializer.Serialize(writer, value, options);
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
