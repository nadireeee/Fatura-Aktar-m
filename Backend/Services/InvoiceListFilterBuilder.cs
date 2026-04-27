namespace DiaErpIntegration.API.Services;

/// <summary>
/// scf_fatura_listele filtreleri — bazı tenantlarda _key_sis_sube / _key_sis_depo,
/// bazılarında *_source soneki kullanılır; ikisini OR ile birleştiririz.
/// </summary>
public static class InvoiceListFilterBuilder
{
    public static void AppendSubeDepoFilters(ICollection<string> filterParts, long? subeKey, long? depoKey)
    {
        if (subeKey.HasValue && subeKey.Value > 0)
        {
            var k = subeKey.Value;
            filterParts.Add($"([_key_sis_sube] = {k} OR [_key_sis_sube_source] = {k})");
        }

        if (depoKey.HasValue && depoKey.Value > 0)
        {
            var k = depoKey.Value;
            filterParts.Add($"([_key_sis_depo] = {k} OR [_key_sis_depo_source] = {k})");
        }
    }
}
