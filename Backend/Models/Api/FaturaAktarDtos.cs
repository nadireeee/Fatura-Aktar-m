using System.Text.Json.Serialization;

namespace DiaErpIntegration.API.Models.Api;

public sealed class FaturaAktarRequestDto
{
    [JsonPropertyName("sourceFirmaKodu")]
    public int SourceFirmaKodu { get; set; }

    [JsonPropertyName("sourceDonemKodu")]
    public int SourceDonemKodu { get; set; }

    [JsonPropertyName("sourceSubeKey")]
    public long? SourceSubeKey { get; set; }

    [JsonPropertyName("sourceDepoKey")]
    public long? SourceDepoKey { get; set; }

    [JsonPropertyName("targetFirmaKodu")]
    public int TargetFirmaKodu { get; set; }

    [JsonPropertyName("targetDonemKodu")]
    public int TargetDonemKodu { get; set; }

    [JsonPropertyName("targetSubeKey")]
    public long TargetSubeKey { get; set; }

    [JsonPropertyName("targetDepoKey")]
    public long TargetDepoKey { get; set; }

    /// <summary>
    /// Toplu aktarım: her fatura için kaynak fatura key + (opsiyonel) seçili kalem key listesi.
    /// Kalem listesi boşsa InvoiceTransferService "tüm kalemleri aktar" kuralını uygular.
    /// </summary>
    [JsonPropertyName("invoices")]
    public List<FaturaAktarInvoiceItemDto> Invoices { get; set; } = new();
}

public sealed class FaturaAktarInvoiceItemDto
{
    [JsonPropertyName("sourceInvoiceKey")]
    public long SourceInvoiceKey { get; set; }

    [JsonPropertyName("selectedKalemKeys")]
    public List<long> SelectedKalemKeys { get; set; } = new();

    // UI payload doğrulama / key mismatch fallback için
    [JsonPropertyName("selectedLineSnapshots")]
    public List<InvoiceTransferLineSnapshotDto> SelectedLineSnapshots { get; set; } = new();

    [JsonPropertyName("headerSnapshot")]
    public InvoiceTransferHeaderSnapshotDto? HeaderSnapshot { get; set; }

    /// <summary>UI modu: Dağıtılacak (Kalem) ise true, Tüm Faturalar ise false.</summary>
    [JsonPropertyName("useDynamicBranch")]
    public bool? UseDynamicBranch { get; set; }
}

public sealed class FaturaAktarResponseDto
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("total")]
    public int Total { get; set; }

    [JsonPropertyName("successCount")]
    public int SuccessCount { get; set; }

    [JsonPropertyName("failedCount")]
    public int FailedCount { get; set; }

    [JsonPropertyName("durationMs")]
    public long DurationMs { get; set; }

    [JsonPropertyName("results")]
    public List<FaturaAktarResultItemDto> Results { get; set; } = new();
}

public sealed class FaturaAktarResultItemDto
{
    [JsonPropertyName("sourceInvoiceKey")]
    public long SourceInvoiceKey { get; set; }

    [JsonPropertyName("result")]
    public InvoiceTransferResultDto Result { get; set; } = new();
}

