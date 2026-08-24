using System.Collections;
using System.Management.Automation;
using System.Text.Json;
using Deckwraith.Core.Serialization;
using Deckwraith.Core.State;

namespace Deckwraith.PowerShell.Serialization;

public static class PortablePowerShellValue
{
    public static JsonElement ToJsonElement(object? value) =>
        CanonicalJson.ToElement(Normalize(Unwrap(value)));

    public static object? FromJsonElement(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number when value.TryGetInt64(out var integer) => integer,
        JsonValueKind.Number when value.TryGetDecimal(out var number) => number,
        JsonValueKind.Number => value.GetDouble(),
        JsonValueKind.Array => value.EnumerateArray().Select(FromJsonElement).ToArray(),
        JsonValueKind.Object => ToPowerShellObject(value),
        _ => throw new DeckStateException($"JSON kind '{value.ValueKind}' is not portable."),
    };

    private static object? Normalize(object? value)
    {
        value = Unwrap(value);
        if (value is null || value is string || value is bool ||
            value is byte or sbyte or short or ushort or int or uint or long or ulong or
            float or double or decimal)
        {
            if (value is double doubleValue && !double.IsFinite(doubleValue) ||
                value is float floatValue && !float.IsFinite(floatValue))
            {
                throw new DeckStateException("Non-finite numbers are not portable durable values.");
            }

            return value;
        }

        if (value.GetType().IsEnum ||
            value is DateTimeOffset or DateTime or DateOnly or TimeOnly or TimeSpan or Guid or Uri)
        {
            return CanonicalJson.ToElement(value);
        }

        if (value is JsonElement element)
        {
            return element.Clone();
        }

        if (value is byte[] bytes)
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["type"] = "bytes",
                ["encoding"] = "base64",
                ["data"] = Convert.ToBase64String(bytes),
            };
        }

        if (value is IDictionary dictionary)
        {
            var result = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (DictionaryEntry entry in dictionary)
            {
                if (entry.Key is not string key)
                {
                    throw new DeckStateException("Durable maps require string keys.");
                }

                result.Add(key, Normalize(entry.Value));
            }

            return result;
        }

        if (value is PSCustomObject || value is PSObject)
        {
            var wrapper = PSObject.AsPSObject(value);
            var result = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var property in wrapper.Properties.Where(property => property.IsGettable))
            {
                result[property.Name] = Normalize(property.Value);
            }

            return result;
        }

        if (value is IEnumerable enumerable)
        {
            var result = new List<object?>();
            foreach (var item in enumerable)
            {
                result.Add(Normalize(item));
            }

            return result;
        }

        throw new DeckStateException(
            $"Values of type '{value.GetType().FullName}' are not portable durable values.");
    }

    private static PSObject ToPowerShellObject(JsonElement value)
    {
        var result = new PSObject();
        foreach (var property in value.EnumerateObject())
        {
            result.Properties.Add(new PSNoteProperty(
                property.Name, FromJsonElement(property.Value)));
        }

        return result;
    }

    private static object? Unwrap(object? value)
    {
        while (value is PSObject wrapper &&
               wrapper.BaseObject is { } baseObject &&
               !ReferenceEquals(baseObject, wrapper))
        {
            if (baseObject is PSCustomObject || !IsDirectlyPortable(baseObject))
            {
                break;
            }

            value = baseObject;
        }

        return value;
    }

    private static bool IsDirectlyPortable(object value) =>
        value is string or bool or
        byte or sbyte or short or ushort or int or uint or long or ulong or
        float or double or decimal or JsonElement or byte[] or
        IDictionary or IEnumerable;
}
