namespace DiaErpIntegration.API.Models.DiaV3Json;

/// <summary>
/// <c>sis_firma_getir</c> sonucundan üretilir; listele servisleri boş dönünce dönem/şube için kullanılır.
/// </summary>
public sealed class DiaFirmaGetirEnrichment
{
    public List<DiaAuthorizedPeriodItem> Periods { get; set; } = new();
    public List<DiaAuthorizedBranchItem> Subeler { get; set; } = new();
}
