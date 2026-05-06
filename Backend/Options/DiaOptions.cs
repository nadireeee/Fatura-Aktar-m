using System.ComponentModel.DataAnnotations;

namespace DiaErpIntegration.API.Options;

public sealed class DiaOptions
{
    /// <summary>Mock | Real</summary>
    public string Mode { get; set; } = "Mock";

    /// <summary>Örn: https://api.dia.com.tr/api/v3/</summary>
    public string BaseUrl { get; set; } = string.Empty;

    // Real modda zorunlu (Program.cs içinde kontrol ediliyor)
    public string Username { get; set; } = string.Empty;

    // Real modda zorunlu (Program.cs içinde kontrol ediliyor)
    public string Password { get; set; } = string.Empty;

    // Real modda zorunlu (Program.cs içinde kontrol ediliyor)
    public string ApiKey { get; set; } = string.Empty;

    // Optional defaults (0 => unset). These should be treated as fallbacks only.
    public int DefaultSourceFirmaKodu { get; set; } = 0;
    public int DefaultSourceDonemKodu { get; set; } = 0;
    public long DefaultSourceSubeKey { get; set; } = 0;

    // Pool (Havuz) firm is fixed and must be set in Real mode.
    public int PoolFirmaKodu { get; set; } = 0;
    public string PoolFirmaAdi { get; set; } = string.Empty;
    // Opsiyonel: dinamik şube kolonu için manual override (örn: __dinamik__2)
    public string BranchDynamicColumnOverride { get; set; } = string.Empty;

    /// <summary>Login sonrası session cache süresi (dakika).</summary>
    public int SessionTtlMinutes { get; set; } = 30;

    /// <summary>
    /// Tek fatura aktarımı için timeout (saniye). 0/negatif ise default 45s uygulanır.
    /// </summary>
    public int TransferInvoiceTimeoutSeconds { get; set; } = 45;

    /// <summary>
    /// Toplu aktarım paralellik sınırı. 0/negatif ise min(8, CPU) uygulanır.
    /// </summary>
    public int TransferConcurrency { get; set; } = 0;

    /// <summary>
    /// SPA toplu aktarımda tek <c>POST /fatura-aktar</c> gövdesine konan azami fatura sayısı ipucu (0/negatif → 12).
    /// Sunucu tarafında faturalar <see cref="TransferConcurrency"/> ile paralel işlenir.
    /// </summary>
    public int TransferBatchSize { get; set; } = 12;

    /// <summary>
    /// true: geçersiz/eksik snapshot’ta <c>snapshot_invalid</c>; legacy yol kapalı.
    /// false (varsayılan): snapshot hatalı olsa bile <c>TransferRequireSnapshot</c> kapalıyken legacy (scf_fatura_getir / cache) denenir.
    /// </summary>
    public bool TransferRequireSnapshot { get; set; } = false;

    /// <summary>
    /// true: <c>scf_fatura_ekle</c> sonrası hedefte <c>scf_fatura_getir</c> ile doğrulama yapılmaz (1 WS, daha hızlı).
    /// </summary>
    public bool TransferDisableVerify { get; set; } = false;

    /// <summary>
    /// true: mükerrer aktarım bellek + kalıcı state/dedup atlanır; transfer state diske yazılmaz.
    /// </summary>
    public bool TransferDisableDedup { get; set; } = false;

    /// <summary>
    /// true: legacy yolda boş kalem sonrası çoklu dönem taraması ve satır <c>view</c> toparlama atlanır.
    /// </summary>
    public bool TransferDisableLegacyFallback { get; set; } = false;

    /// <summary>
    /// true: satır <c>dovizkuru</c> / <c>raporlamadovizkuru</c> fatura başlığındaki stringlerle aynı (6 hane); TL ise 1.000000. Satır normalizasyonu yok.
    /// </summary>
    public bool TransferMirrorHeaderKurOnLines { get; set; } = false;

    /// <summary>
    /// true: snapshot ve istemcinin taşıdığı hedef <c>_key</c> ile doğrudan <c>scf_fatura_ekle</c>.
    /// Kaynakta tekrarlı <c>scf_fatura_getir</c> döngüsü olmadığı için aktarım başına kontör genelde düşük kalır;
    /// küçük lookup çağrıları (ör. hedef cari/döviz anahtarı çözümü) kalır.
    /// </summary>
    public bool TransferRawMode { get; set; } = false;

    /// <summary>
    /// Invoice bazlı retry sayısı (yeniden deneme). 0 = deneme yok. Negatif ise controller varsayılanı (2) kullanılabilir.
    /// </summary>
    public int TransferMaxRetry { get; set; } = 2;

    /// <summary>
    /// DİA HttpClient timeout (saniye). 0/negatif ise framework default'u kullanılır.
    /// </summary>
    public int DiaHttpTimeoutSeconds { get; set; } = 0;
}

