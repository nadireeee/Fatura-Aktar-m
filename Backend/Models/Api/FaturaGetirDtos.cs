namespace DiaErpIntegration.API.Models.Api;

public sealed class FaturaGetirRequestDto
{
    // DİA WS context: rapor çalıştırılacak firma (firmakodu). Boşsa backend varsayılan havuzu kullanır.
    public int? firma_kodu { get; set; }
    // DİA WS context: rapor çalıştırılacak dönem (donemkodu)
    public int? donem_kodu { get; set; }
    // Rapor kodu (RPRxxxx). Boşsa backend default kullanır.
    public string? report_code { get; set; }
    public string? baslangic { get; set; }
    public string? bitis { get; set; }
    public string? fatura_tipi { get; set; }
    public int? kaynak_sube { get; set; }
    public int? kaynak_depo { get; set; }
    public string? ust_islem { get; set; }
    public string? cari_adi { get; set; }
    public string? fatura_no { get; set; }
    public string? fatura_turu { get; set; }
    public string? kalem_sube { get; set; }
    /// <summary>
    /// True ise backend RPR memory cache'ini bypass eder (yeni kayıtlar hemen gelsin diye).
    /// </summary>
    public bool? force_refresh { get; set; }
}

