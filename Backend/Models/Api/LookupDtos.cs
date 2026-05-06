using System.Text.Json.Serialization;

namespace DiaErpIntegration.API.Models.Api;

public sealed class CompanyDto
{
    [JsonPropertyName("firma_kodu")]
    public int FirmaKodu { get; set; }

    [JsonPropertyName("firma_adi")]
    public string FirmaAdi { get; set; } = string.Empty;
}

public sealed class PeriodDto
{
    [JsonPropertyName("key")]
    public long Key { get; set; }

    [JsonPropertyName("donemkodu")]
    public int DonemKodu { get; set; }

    [JsonPropertyName("gorunenkod")]
    public string GorunenKod { get; set; } = string.Empty;

    [JsonPropertyName("ontanimli")]
    public bool Ontanimli { get; set; }

    [JsonPropertyName("baslangic_tarihi")]
    public string? BaslangicTarihi { get; set; }

    [JsonPropertyName("bitis_tarihi")]
    public string? BitisTarihi { get; set; }
}

public sealed class BranchDto
{
    [JsonPropertyName("key")]
    public long Key { get; set; }

    [JsonPropertyName("subeadi")]
    public string SubeAdi { get; set; } = string.Empty;
}

public sealed class DepotDto
{
    [JsonPropertyName("key")]
    public long Key { get; set; }

    [JsonPropertyName("depoadi")]
    public string DepoAdi { get; set; } = string.Empty;
}

public sealed class ResolveNamesRequestDto
{
    [JsonPropertyName("firmaKodu")]
    public int FirmaKodu { get; set; }

    [JsonPropertyName("donemKodu")]
    public int DonemKodu { get; set; }

    [JsonPropertyName("subeKeys")]
    public List<long> SubeKeys { get; set; } = new();

    [JsonPropertyName("depoKeys")]
    public List<long> DepoKeys { get; set; } = new();
}

public sealed class ResolveStokHizmetRequestDto
{
    [JsonPropertyName("firmaKodu")]
    public int FirmaKodu { get; set; }

    [JsonPropertyName("donemKodu")]
    public int DonemKodu { get; set; }

    [JsonPropertyName("fiyatKartKeys")]
    public List<long> FiyatKartKeys { get; set; } = new();
}

/// <summary>RAW satır zenginleştirme: DİA listelerinden key ↔ kod eşlemesi (toplu cache).</summary>
public sealed class LookupKeyCodeItem
{
    [JsonPropertyName("key")]
    public long Key { get; set; }

    [JsonPropertyName("kod")]
    public string Kod { get; set; } = string.Empty;
}

public sealed class ResolveUnitsRequestDto
{
    [JsonPropertyName("firmaKodu")]
    public int FirmaKodu { get; set; }

    [JsonPropertyName("donemKodu")]
    public int DonemKodu { get; set; }

    [JsonPropertyName("unitKeys")]
    public List<long> UnitKeys { get; set; } = new();
}

public sealed class CurrencyDto
{
    [JsonPropertyName("key")]
    public long Key { get; set; }

    [JsonPropertyName("kodu")]
    public string Kodu { get; set; } = string.Empty;

    [JsonPropertyName("adi")]
    public string Adi { get; set; } = string.Empty;
}

public sealed class DefaultSourceContextDto
{
    [JsonPropertyName("defaultSourceFirmaKodu")]
    public int DefaultSourceFirmaKodu { get; set; }

    [JsonPropertyName("defaultSourceDonemKodu")]
    public int DefaultSourceDonemKodu { get; set; }

    [JsonPropertyName("defaultSourceSubeKey")]
    public long DefaultSourceSubeKey { get; set; }
}

public sealed class PoolContextDto
{
    [JsonPropertyName("poolFirmaKodu")]
    public int PoolFirmaKodu { get; set; }

    [JsonPropertyName("poolFirmaAdi")]
    public string PoolFirmaAdi { get; set; } = string.Empty;
}

public sealed class TargetResolveRequestDto
{
    [JsonPropertyName("targetFirmaKodu")]
    public int TargetFirmaKodu { get; set; }

    [JsonPropertyName("sourceDonemKodu")]
    public int? SourceDonemKodu { get; set; }

    [JsonPropertyName("sourceInvoiceDate")]
    public string? SourceInvoiceDate { get; set; }
}

public sealed class TargetResolveResultDto
{
    [JsonPropertyName("targetFirmaKodu")]
    public int TargetFirmaKodu { get; set; }

    [JsonPropertyName("targetFirmaAdi")]
    public string TargetFirmaAdi { get; set; } = string.Empty;

    [JsonPropertyName("targetSubeKey")]
    public long TargetSubeKey { get; set; }

    [JsonPropertyName("targetSubeAdi")]
    public string TargetSubeAdi { get; set; } = string.Empty;

    [JsonPropertyName("targetDepoKey")]
    public long TargetDepoKey { get; set; }

    [JsonPropertyName("targetDepoAdi")]
    public string TargetDepoAdi { get; set; } = string.Empty;

    [JsonPropertyName("targetDonemKodu")]
    public int TargetDonemKodu { get; set; }

    [JsonPropertyName("targetDonemKey")]
    public long TargetDonemKey { get; set; }

    [JsonPropertyName("targetDonemLabel")]
    public string TargetDonemLabel { get; set; } = string.Empty;

    [JsonPropertyName("autoSelected")]
    public bool AutoSelected { get; set; } = true;

    [JsonPropertyName("fallbackUsed")]
    public bool FallbackUsed { get; set; }

    [JsonPropertyName("fallbackReason")]
    public string? FallbackReason { get; set; }
}

/// <summary>Yetkili firma ağacından normalize edilmiş tek firma görünümü (LookupNormalizer çıktısı).</summary>
public sealed class CompanyLookupNormalizedDto
{
    public int FirmaKodu { get; set; }
    public string FirmaAdi { get; set; } = string.Empty;
    public List<NormalizedDonemDto> Donemler { get; set; } = new();
    public List<NormalizedSubeDto> Subeler { get; set; } = new();
}

public sealed class NormalizedDonemDto
{
    public long Key { get; set; }
    public int DonemKodu { get; set; }
    public string GorunenKod { get; set; } = string.Empty;
    public bool Ontanimli { get; set; }
    public string? BaslangicTarihi { get; set; }
    public string? BitisTarihi { get; set; }
}

public sealed class NormalizedSubeDto
{
    public long Key { get; set; }
    public string SubeAdi { get; set; } = string.Empty;
    public List<NormalizedDepoDto> Depolar { get; set; } = new();
}

public sealed class NormalizedDepoDto
{
    public long Key { get; set; }
    public string DepoAdi { get; set; } = string.Empty;
}

