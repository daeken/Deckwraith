using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Deckwraith.Core.Serialization;

/// <summary>Produces stable JSON bytes by sorting every object's properties ordinally.</summary>
public static class CanonicalJson
{
    private static readonly JsonSerializerOptions SerializerOptions = CreateOptions();

    public static JsonElement ToElement<T>(T value) =>
        JsonSerializer.SerializeToElement(value, SerializerOptions);

    public static byte[] Serialize<T>(T value)
    {
        var element = ToElement(value);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteElement(writer, element);
        }

        return stream.ToArray();
    }

    public static string Hash<T>(T value) =>
        $"sha256:{Convert.ToHexStringLower(SHA256.HashData(Serialize(value)))}";

    private static void WriteElement(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(
                    property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteElement(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteElement(writer, item);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(element.GetRawText(), skipInputValidation: true);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                writer.WriteNullValue();
                break;
            default:
                throw new JsonException($"Unsupported JSON value kind {element.ValueKind}.");
        }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
