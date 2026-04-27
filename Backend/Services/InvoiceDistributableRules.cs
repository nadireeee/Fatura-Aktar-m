using DiaErpIntegration.API.Models.DiaV3Json;
using System.Text.Json;

namespace DiaErpIntegration.API.Services;

/// <summary>
/// Havuz listeleri: kalemlerde dinamik şube seçimi var mı — tek tanım (backend list + UI ile uyumlu kavram).
/// </summary>
public static class InvoiceDistributableRules
{
    public static bool LineHasDynamicBranchSelection(DiaInvoiceLine line, string? dynamicColumn) =>
        !string.IsNullOrWhiteSpace(line.ExtractDinamikSubelerRaw(dynamicColumn));

    public static bool InvoiceHasAnyDistributableLine(
        IReadOnlyList<DiaInvoiceLine>? lines,
        string? dynamicColumn) =>
        (lines ?? Array.Empty<DiaInvoiceLine>())
            .Any(l => LineHasDynamicBranchSelection(l, dynamicColumn));

    public static bool InvoiceHasAnyDistributableLineFromView(
        IReadOnlyList<JsonElement>? rows,
        string? dynamicColumn)
    {
        if (rows == null || rows.Count == 0) return false;

        foreach (var r in rows)
        {
            if (TryGetString(r, dynamicColumn) is { Length: > 0 }) return true;

            // tenant varyantları
            if (TryGetString(r, "__dinamik__2") is { Length: > 0 }) return true;
            if (TryGetString(r, "__dinamik__1") is { Length: > 0 }) return true;
            if (TryGetString(r, "__dinamik__00002") is { Length: > 0 }) return true;
            if (TryGetString(r, "__dinamik__00001") is { Length: > 0 }) return true;
        }

        return false;
    }

    private static string? TryGetString(JsonElement r, string? prop)
    {
        if (string.IsNullOrWhiteSpace(prop)) return null;
        if (r.ValueKind != JsonValueKind.Object) return null;
        if (!r.TryGetProperty(prop, out var v)) return null;

        try
        {
            return v.ValueKind switch
            {
                JsonValueKind.String => (v.GetString() ?? string.Empty).Trim(),
                JsonValueKind.Number => v.GetRawText().Trim(),
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }
}
