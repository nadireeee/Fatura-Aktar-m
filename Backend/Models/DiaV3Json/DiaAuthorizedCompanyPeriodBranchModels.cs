using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiaErpIntegration.API.Models.DiaV3Json;

public sealed class DiaAuthorizedCompanyPeriodBranchItem
{
    [JsonPropertyName("firmakodu")]
    public int FirmaKodu { get; set; }

    [JsonPropertyName("firmaadi")]
    public string FirmaAdi { get; set; } = string.Empty;

    [JsonPropertyName("donemler")]
    public List<DiaAuthorizedPeriodItem> Donemler { get; set; } = new();

    // Tenant farkı: bazı sistemlerde aynı liste farklı property adıyla dönebiliyor.
    // (Örn: "donem" / "donem_list" gibi.) LookupsController bu alanı da dikkate alır.
    [JsonPropertyName("donem")]
    public List<DiaAuthorizedPeriodItem> DonemFallback { get; set; } = new();

    [JsonPropertyName("donem_list")]
    public List<DiaAuthorizedPeriodItem> DonemListFallback { get; set; } = new();

    [JsonPropertyName("subeler")]
    public List<DiaAuthorizedBranchItem> Subeler { get; set; } = new();

    [JsonPropertyName("dovizler")]
    public List<DiaAuthorizedCurrencyItem> Dovizler { get; set; } = new();
}

public sealed class DiaAuthorizedPeriodItem
{
    [JsonPropertyName("_key")]
    public long Key { get; set; }

    [JsonPropertyName("donemkodu")]
    public int DonemKodu { get; set; }

    [JsonPropertyName("gorunendonemkodu")]
    public string GorunenDonemKodu { get; set; } = string.Empty;

    [JsonPropertyName("baslangictarihi")]
    public string? BaslangicTarihi { get; set; }

    [JsonPropertyName("bitistarihi")]
    public string? BitisTarihi { get; set; }

    [JsonPropertyName("ontanimli")]
    public string? Ontanimli { get; set; }
}

public sealed class DiaAuthorizedBranchItem
{
    [JsonPropertyName("_key")]
    public long Key { get; set; }

    [JsonPropertyName("subeadi")]
    public string SubeAdi { get; set; } = string.Empty;

    [JsonPropertyName("depolar")]
    public List<DiaAuthorizedDepotItem> Depolar { get; set; } = new();
}

public sealed class DiaAuthorizedDepotItem
{
    [JsonPropertyName("_key")]
    public long Key { get; set; }

    [JsonPropertyName("depoadi")]
    public string DepoAdi { get; set; } = string.Empty;
}

public sealed class DiaAuthorizedCurrencyItem
{
    [JsonPropertyName("_key")]
    public long Key { get; set; }

    [JsonPropertyName("kodu")]
    public string? Kodu { get; set; }

    [JsonPropertyName("adi")]
    public string? Adi { get; set; }

    [JsonPropertyName("uzunadi")]
    public string? UzunAdi { get; set; }

    [JsonPropertyName("anadovizmi")]
    public JsonElement AnaDovizMiRaw { get; set; }

    [JsonPropertyName("raporlamadovizmi")]
    public JsonElement RaporlamaDovizMiRaw { get; set; }
}

