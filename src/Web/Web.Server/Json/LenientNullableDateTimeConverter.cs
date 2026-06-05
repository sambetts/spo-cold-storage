using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Web.Server.Json;

/// <summary>
/// Lenient deserializer for nullable <see cref="DateTime"/> properties on API
/// request bodies. Any of:
///   - missing / explicit null
///   - empty string / whitespace
///   - a string that cannot be parsed by <see cref="DateTime.TryParse(string, IFormatProvider, DateTimeStyles, out DateTime)"/>
/// produces <c>null</c> instead of failing the whole model-binding pass with a
/// 400 ProblemDetails response. This matters because callers like the SPFx
/// command set ship SharePoint's locale-formatted <c>Modified</c> display
/// string (e.g. "12/5/2025 10:29 AM") rather than ISO 8601 - rejecting the
/// entire request because one optional metadata field is unparseable is far
/// worse than just dropping the field.
///
/// On the write path the value is serialized as ISO 8601 round-trip
/// ("o" format) for unambiguous interop with non-.NET clients.
/// </summary>
public sealed class LenientNullableDateTimeConverter : JsonConverter<DateTime?>
{
    private const DateTimeStyles ParseStyles =
        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;

    public override DateTime? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;
            case JsonTokenType.String:
                var s = reader.GetString();
                if (string.IsNullOrWhiteSpace(s))
                {
                    return null;
                }
                return DateTime.TryParse(s, CultureInfo.InvariantCulture, ParseStyles, out var dt)
                    ? dt
                    : null;
            default:
                // Anything else (number, object, …): drop it rather than throw,
                // matching the "optional metadata" contract.
                reader.Skip();
                return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, DateTime? value, JsonSerializerOptions options)
    {
        if (value.HasValue)
        {
            writer.WriteStringValue(value.Value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}
