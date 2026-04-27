using DiaErpIntegration.API.Models.DiaV3Json;

namespace DiaErpIntegration.API.Models;

/// <summary><c>scf_fatura_listele</c> yanıtı — HTTP ve DİA gövde kodu birlikte.</summary>
public sealed class DiaScfFaturaListeleResult
{
    public List<DiaInvoiceListItem> Items { get; set; } = new();

    /// <summary>DİA JSON <c>code</c>; parse edilemediyse -1.</summary>
    public int DiaCode { get; set; }

    public string? DiaMessage { get; set; }

    public int HttpStatus { get; set; }

    public bool HttpSuccess { get; set; } = true;

    public bool IsDiaOk => DiaCode == 200 && HttpSuccess;
}

/// <summary><c>scf_fatura_listele_ayrintili</c> ile ŞUBELER/__dinamik__2 anahtar taraması özeti.</summary>
public sealed class Dinamik2ScanResult
{
    public HashSet<long> Keys { get; set; } = new();

    public int BatchesFetched { get; set; }

    public int RowsScannedApprox { get; set; }

    public bool CapReached { get; set; }

    public double ElapsedMs { get; set; }
}
