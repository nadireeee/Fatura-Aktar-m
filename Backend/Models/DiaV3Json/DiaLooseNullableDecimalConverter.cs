using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiaErpIntegration.API.Models.DiaV3Json;

/// <summary>
/// DİA WS bazı tenantlarda tutari/miktar vb. için hem sayı hem TR biçimli string döndürür;
/// <see cref="decimal"/> bekleyen model deserialize sırasında patlar. Bu converter ikisini de okur.
/// </summary>
public sealed class DiaLooseNullableDecimalConverter : JsonConverter<decimal?>
{
    private static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");

    public override decimal? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;

            case JsonTokenType.Number:
                if (reader.TryGetDecimal(out var d)) return d;
                if (reader.TryGetDouble(out var db) && !double.IsNaN(db) && !double.IsInfinity(db))
                    return (decimal)db;
                return null;

            case JsonTokenType.String:
                return ParseString(reader.GetString());

            case JsonTokenType.True:
            case JsonTokenType.False:
                return null;

            case JsonTokenType.StartObject:
            case JsonTokenType.StartArray:
                try
                {
                    var el = JsonElement.ParseValue(ref reader);
                    return ParseElement(el);
                }
                catch
                {
                    return null;
                }

            default:
                return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, decimal? value, JsonSerializerOptions options)
    {
        if (value == null) writer.WriteNullValue();
        else writer.WriteNumberValue(value.Value);
    }

    private static decimal? ParseElement(JsonElement el)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return null;
            case JsonValueKind.Number:
                if (el.TryGetDecimal(out var dec)) return dec;
                if (el.TryGetDouble(out var dbl)) return (decimal)dbl;
                return null;
            case JsonValueKind.String:
                return ParseString(el.GetString());
            case JsonValueKind.Object:
                foreach (var p in el.EnumerateObject())
                {
                    var v = p.Value;
                    if (v.ValueKind == JsonValueKind.Number || v.ValueKind == JsonValueKind.String)
                    {
                        var inner = ParseElement(v);
                        if (inner.HasValue) return inner;
                    }
                }
                return null;
            default:
                return null;
        }
    }

    private static decimal? ParseString(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        if (decimal.TryParse(s, NumberStyles.Any, Tr, out var d)) return d;
        if (decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out d)) return d;
        // Ayırıcı karışık örnekler: "1.234,56" veya boşluklu
        var compact = s.Replace("\u00a0", "").Replace(" ", "");
        if (decimal.TryParse(compact, NumberStyles.Any, Tr, out d)) return d;
        if (decimal.TryParse(compact, NumberStyles.Any, CultureInfo.InvariantCulture, out d)) return d;
        return null;
    }
}
