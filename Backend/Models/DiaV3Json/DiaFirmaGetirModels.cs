using System.Text.Json.Serialization;

namespace DiaErpIntegration.API.Models.DiaV3Json;

public sealed class DiaSisFirmaGetirCompany
{
    [JsonPropertyName("firmakodu")]
    public int FirmaKodu { get; set; }

    [JsonPropertyName("firmaadi")]
    public string? FirmaAdi { get; set; }

    // Bazı tenantlarda dönem/şube listeleri m_* altında gelir.
    [JsonPropertyName("m_donemler")]
    public List<DiaFirmaGetirDonemRow> MDonemler { get; set; } = new();

    [JsonPropertyName("m_subeler")]
    public List<DiaFirmaGetirSubeRow> MSubeler { get; set; } = new();

    // Tenant farkı: dönem/şube listeleri farklı property adlarıyla gelebilir.
    [JsonPropertyName("donemler")]
    public List<DiaAuthorizedPeriodItem> Donemler { get; set; } = new();

    [JsonPropertyName("donem")]
    public List<DiaAuthorizedPeriodItem> DonemFallback { get; set; } = new();

    [JsonPropertyName("donem_list")]
    public List<DiaAuthorizedPeriodItem> DonemListFallback { get; set; } = new();

    [JsonPropertyName("subeler")]
    public List<DiaAuthorizedBranchItem> Subeler { get; set; } = new();
}

public sealed class DiaFirmaGetirDonemRow
{
    [JsonPropertyName("_key")]
    public long Key { get; set; }

    [JsonPropertyName("donemkodu")]
    public int DonemKodu { get; set; }

    [JsonPropertyName("gorunenkod")]
    public string? GorunenKod { get; set; }

    [JsonPropertyName("baslangic")]
    public string? Baslangic { get; set; }

    [JsonPropertyName("bitis")]
    public string? Bitis { get; set; }

    [JsonPropertyName("ontanimli")]
    public string? Ontanimli { get; set; }
}

public sealed class DiaFirmaGetirSubeRow
{
    [JsonPropertyName("_key")]
    public long Key { get; set; }

    [JsonPropertyName("subeadi")]
    public string? SubeAdi { get; set; }
}

