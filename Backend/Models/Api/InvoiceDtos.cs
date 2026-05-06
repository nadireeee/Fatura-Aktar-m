using System.Text.Json.Serialization;
using TransferStatus = DiaErpIntegration.API.Models.TransferStatus;

namespace DiaErpIntegration.API.Models.Api;

public sealed class InvoiceListRequestDto
{
    [JsonPropertyName("firma_kodu")]
    public int FirmaKodu { get; set; }

    [JsonPropertyName("donem_kodu")]
    public int DonemKodu { get; set; }

    // sourceSubeKey filtrelemek için (opsiyonel)
    [JsonPropertyName("source_sube_key")]
    public long? SourceSubeKey { get; set; }

    // sourceDepoKey filtrelemek için (opsiyonel)
    [JsonPropertyName("source_depo_key")]
    public long? SourceDepoKey { get; set; }

    [JsonPropertyName("filters")]
    public string? Filters { get; set; }

    // Havuz özel: üst işlem türü filtresi (sis_ust_islem_turu: A/B vb.)
    // Örnek: 39715 => A, 39717 => B (TEST FIRMA)
    [JsonPropertyName("ust_islem_turu_key")]
    public long? UstIslemTuruKey { get; set; }

    // Havuz özel: sadece kalemlerinde ŞUBELER (__dinamik__1) dolu olan faturalar
    [JsonPropertyName("only_distributable")]
    public bool OnlyDistributable { get; set; } = false;

    // Havuz özel: sadece kalemlerinde ŞUBELER (__dinamik__1/__dinamik__00001) HİÇ DOLU olmayan faturalar
    // (Yani normal fatura adayları)
    [JsonPropertyName("only_non_distributable")]
    public bool OnlyNonDistributable { get; set; } = false;

    [JsonPropertyName("limit")]
    public int Limit { get; set; } = 10;

    [JsonPropertyName("offset")]
    public int Offset { get; set; } = 0;
}

public sealed class InvoiceListRowDto
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty; // DIA _key -> string

    [JsonPropertyName("fisno")]
    public string? FisNo { get; set; }

    [JsonPropertyName("belgeno")]
    public string? BelgeNo { get; set; }

    [JsonPropertyName("belgeno2")]
    public string? BelgeNo2 { get; set; }

    [JsonPropertyName("tarih")]
    public string? Tarih { get; set; }

    [JsonPropertyName("turu")]
    public int? Turu { get; set; }

    [JsonPropertyName("turuack")]
    public string? TuruAck { get; set; }

    [JsonPropertyName("turu_kisa")]
    public string? TuruKisa { get; set; }

    [JsonPropertyName("carikartkodu")]
    public string? CariKartKodu { get; set; }

    [JsonPropertyName("cariunvan")]
    public string? CariUnvan { get; set; }

    [JsonPropertyName("sourcesubeadi")]
    public string? SourceSubeAdi { get; set; }

    [JsonPropertyName("sourcedepoadi")]
    public string? SourceDepoAdi { get; set; }

    [JsonPropertyName("destsubeadi")]
    public string? DestSubeAdi { get; set; }

    [JsonPropertyName("destdepoadi")]
    public string? DestDepoAdi { get; set; }

    [JsonPropertyName("firmaadi")]
    public string? FirmaAdi { get; set; }

    [JsonPropertyName("dovizturu")]
    public string? DovizTuru { get; set; }

    [JsonPropertyName("toplam")]
    public decimal? Toplam { get; set; }

    [JsonPropertyName("toplamkdv")]
    public decimal? ToplamKdv { get; set; }

    [JsonPropertyName("net")]
    public decimal? Net { get; set; }

    [JsonPropertyName("iptal")]
    public bool? Iptal { get; set; }

    [JsonPropertyName("odemeplani")]
    public string? OdemePlani { get; set; }

    [JsonPropertyName("odemeplaniack")]
    public string? OdemePlaniAck { get; set; }

    [JsonPropertyName("projekodu")]
    public string? ProjeKodu { get; set; }

    [JsonPropertyName("projeaciklama")]
    public string? ProjeAciklama { get; set; }

    [JsonPropertyName("transfer_status")]
    public TransferStatus TransferStatus { get; set; } = TransferStatus.Bekliyor;

    [JsonPropertyName("bekleyen_kalem_sayisi")]
    public int BekleyenKalemSayisi { get; set; } = 0;

    [JsonPropertyName("hasLineBranchSelection")]
    public bool HasLineBranchSelection { get; set; }

    [JsonPropertyName("hasHeaderOnlyBranch")]
    public bool HasHeaderOnlyBranch { get; set; }

    [JsonPropertyName("effectiveTransferType")]
    public string EffectiveTransferType { get; set; } = "FATURA";
}

public sealed class InvoiceDetailDto
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("fisno")]
    public string? FisNo { get; set; }

    [JsonPropertyName("tarih")]
    public string? Tarih { get; set; }

    [JsonPropertyName("carikartkodu")]
    public string? CariKartKodu { get; set; }

    [JsonPropertyName("cariunvan")]
    public string? CariUnvan { get; set; }

    [JsonPropertyName("odemeplani")]
    public string? OdemePlani { get; set; }

    [JsonPropertyName("odemeplaniack")]
    public string? OdemePlaniAck { get; set; }

    [JsonPropertyName("satiselemani")]
    public string? SatisElemani { get; set; }

    [JsonPropertyName("kalemler")]
    public List<InvoiceLineDto> Kalemler { get; set; } = new();
}

public sealed class InvoiceLineDto
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("sirano")]
    public int? SiraNo { get; set; }

    [JsonPropertyName("kalemturu")]
    public int? KalemTuru { get; set; }

    [JsonPropertyName("stokhizmetkodu")]
    public string? StokHizmetKodu { get; set; }

    [JsonPropertyName("stokhizmetaciklama")]
    public string? StokHizmetAciklama { get; set; }

    [JsonPropertyName("birim")]
    public string? Birim { get; set; }

    [JsonPropertyName("miktar")]
    public decimal? Miktar { get; set; }

    [JsonPropertyName("birimfiyati")]
    public decimal? BirimFiyati { get; set; }

    [JsonPropertyName("sonbirimfiyati")]
    public decimal? SonBirimFiyati { get; set; }

    [JsonPropertyName("tutari")]
    public decimal? Tutari { get; set; }

    [JsonPropertyName("kdv")]
    public decimal? Kdv { get; set; }

    [JsonPropertyName("kdvtutari")]
    public decimal? KdvTutari { get; set; }

    [JsonPropertyName("indirimtoplam")]
    public decimal? IndirimToplam { get; set; }

    [JsonPropertyName("depoadi")]
    public string? DepoAdi { get; set; }

    [JsonPropertyName("note")]
    public string? Note { get; set; }

    [JsonPropertyName("note2")]
    public string? Note2 { get; set; }

    [JsonPropertyName("projekodu")]
    public string? ProjeKodu { get; set; }

    [JsonPropertyName("projeaciklama")]
    public string? ProjeAciklama { get; set; }

    // Dinamik alan: ŞUBELER (Fatura Kalemi) -> __dinamik__1
    [JsonPropertyName("dinamik_subeler_raw")]
    public string? DinamikSubelerRaw { get; set; }

    [JsonPropertyName("dinamik_subeler_normalized")]
    public string? DinamikSubelerNormalized { get; set; }

    [JsonPropertyName("transfer_status")]
    public TransferStatus TransferStatus { get; set; } = TransferStatus.Bekliyor;

    [JsonPropertyName("target_firma_kodu")]
    public string? TargetFirmaKodu { get; set; }

    [JsonPropertyName("target_sube_kodu")]
    public string? TargetSubeKodu { get; set; }

    [JsonPropertyName("target_donem_kodu")]
    public string? TargetDonemKodu { get; set; }
}

/// <summary>RPR / UI snapshot — kaynak scf_fatura_getir yerine kullanılır.</summary>
public sealed class InvoiceTransferHeaderSnapshotDto
{
    [JsonPropertyName("sourceInvoiceKey")]
    public long SourceInvoiceKey { get; set; }

    [JsonPropertyName("invoiceNo")]
    public string? InvoiceNo { get; set; }

    [JsonPropertyName("fisNo")]
    public string? FisNo { get; set; }

    [JsonPropertyName("date")]
    public string? Date { get; set; }

    [JsonPropertyName("time")]
    public string? Time { get; set; }

    /// <summary>Örn. Mal Alım / Toptan Satış (görünen etiket).</summary>
    [JsonPropertyName("invoiceType")]
    public string? InvoiceType { get; set; }

    /// <summary>DİA fatura türü numerik kodu (scf türü).</summary>
    [JsonPropertyName("invoiceTypeCode")]
    public int? InvoiceTypeCode { get; set; }

    [JsonPropertyName("upperProcessCode")]
    public string? UpperProcessCode { get; set; }

    [JsonPropertyName("cariCode")]
    public string? CariCode { get; set; }

    [JsonPropertyName("cariName")]
    public string? CariName { get; set; }

    [JsonPropertyName("currencyCode")]
    public string? CurrencyCode { get; set; }

    [JsonPropertyName("exchangeRate")]
    public decimal? ExchangeRate { get; set; }

    [JsonPropertyName("sourceBranchName")]
    public string? SourceBranchName { get; set; }

    [JsonPropertyName("sourceWarehouseName")]
    public string? SourceWarehouseName { get; set; }

    [JsonPropertyName("total")]
    public decimal? Total { get; set; }

    [JsonPropertyName("discount")]
    public decimal? Discount { get; set; }

    [JsonPropertyName("expense")]
    public decimal? Expense { get; set; }

    [JsonPropertyName("vat")]
    public decimal? Vat { get; set; }

    [JsonPropertyName("net")]
    public decimal? Net { get; set; }

    /// <summary>Kaynak cari kart yetkili kodu — RAW aktarımda hedef firmada aynı kodla _key çözülür.</summary>
    [JsonPropertyName("cariYetkiliKodu")]
    public string? CariYetkiliKodu { get; set; }

    // ─── RAW / zero-read mode: istemci hedef firmada çözülmüş DİA _key değerlerini gönderir ───

    [JsonPropertyName("targetCariYetkiliKey")]
    public long? TargetCariYetkiliKey { get; set; }

    [JsonPropertyName("targetCariKey")]
    public long? TargetCariKey { get; set; }

    [JsonPropertyName("targetCariAdresKey")]
    public long? TargetCariAdresKey { get; set; }

    [JsonPropertyName("targetSisDovizKey")]
    public long? TargetSisDovizKey { get; set; }

    [JsonPropertyName("targetSisDovizRaporlamaKey")]
    public long? TargetSisDovizRaporlamaKey { get; set; }

    [JsonPropertyName("targetOdemePlaniKey")]
    public long? TargetOdemePlaniKey { get; set; }

    [JsonPropertyName("targetProjeKey")]
    public long? TargetProjeKey { get; set; }

    /// <summary>RAW: başlık <c>dovizkuru</c> metni — sunucu formatlamaz.</summary>
    [JsonPropertyName("headerDovizKuru")]
    public string? HeaderDovizKuru { get; set; }

    /// <summary>RAW: başlık <c>raporlamadovizkuru</c> metni — sunucu formatlamaz.</summary>
    [JsonPropertyName("headerRaporlamaDovizKuru")]
    public string? HeaderRaporlamaDovizKuru { get; set; }
}

public sealed class InvoiceTransferRequestDto
{
    [JsonPropertyName("sourceFirmaKodu")]
    public int SourceFirmaKodu { get; set; }

    [JsonPropertyName("sourceDonemKodu")]
    public int SourceDonemKodu { get; set; }

    [JsonPropertyName("sourceSubeKey")]
    public long? SourceSubeKey { get; set; }

    [JsonPropertyName("sourceDepoKey")]
    public long? SourceDepoKey { get; set; }

    [JsonPropertyName("sourceInvoiceKey")]
    public long SourceInvoiceKey { get; set; }

    [JsonPropertyName("selectedKalemKeys")]
    public List<long> SelectedKalemKeys { get; set; } = new();

    // UI state/key mismatch durumunda fallback eşleştirme için satır snapshot'ı
    [JsonPropertyName("selectedLineSnapshots")]
    public List<InvoiceTransferLineSnapshotDto> SelectedLineSnapshots { get; set; } = new();

    [JsonPropertyName("headerSnapshot")]
    public InvoiceTransferHeaderSnapshotDto? HeaderSnapshot { get; set; }

    [JsonPropertyName("targetFirmaKodu")]
    public int TargetFirmaKodu { get; set; }

    [JsonPropertyName("targetDonemKodu")]
    public int TargetDonemKodu { get; set; }

    [JsonPropertyName("targetSubeKey")]
    public long TargetSubeKey { get; set; }

    [JsonPropertyName("targetDepoKey")]
    public long TargetDepoKey { get; set; }

    /// <summary>
    /// true: Kalem Şube (dinamik_fatsube) kuralını uygula (Dağıtılacak/Kalem modu).
    /// false: Kalem Şube'yi tamamen ignore et; hedef şube/depo sadece seçili hedefe göre belirlenir (Tüm Faturalar).
    /// </summary>
    [JsonPropertyName("useDynamicBranch")]
    public bool? UseDynamicBranch { get; set; }
}

public sealed class InvoiceTransferLineSnapshotDto
{
    [JsonPropertyName("sourceLineKey")]
    public long? SourceLineKey { get; set; }

    [JsonPropertyName("stokKartKodu")]
    public string? StokKartKodu { get; set; }

    [JsonPropertyName("aciklama")]
    public string? Aciklama { get; set; }

    [JsonPropertyName("miktar")]
    public decimal? Miktar { get; set; }

    [JsonPropertyName("birimFiyati")]
    public decimal? BirimFiyati { get; set; }

    [JsonPropertyName("tutar")]
    public decimal? Tutar { get; set; }

    // RPR payload doğrulama / debug için opsiyonel ek alanlar
    [JsonPropertyName("birimAdi")]
    public string? BirimAdi { get; set; }

    [JsonPropertyName("indirim")]
    public decimal? Indirim { get; set; }

    [JsonPropertyName("masraf")]
    public decimal? Masraf { get; set; }

    /// <summary>STOK / HIZMET — hedefte stok vs hizmet kart çözümü için.</summary>
    [JsonPropertyName("lineTypeLabel")]
    public string? LineTypeLabel { get; set; }

    /// <summary>RPR stok/hizmet kodu (stokKartKodu ile aynı; öncelik itemCode).</summary>
    [JsonPropertyName("itemCode")]
    public string? ItemCode { get; set; }

    /// <summary>Kalem dinamik şube (__dinamik__fatsube).</summary>
    [JsonPropertyName("dynamicBranch")]
    public string? DynamicBranch { get; set; }

    [JsonPropertyName("kdvYuzde")]
    public decimal? KdvYuzde { get; set; }

    /// <summary>Satır KDV tutarı — sunucu <see cref="InvoiceTransferService"/> ile oranı türetmek için (opsiyonel).</summary>
    [JsonPropertyName("kdvTutari")]
    public decimal? KdvTutari { get; set; }

    // ─── RAW / zero-read: hedef kalem anahtarları (zorunlu alanlar TryValidateRawSnapshot ile kontrol edilir) ───

    [JsonPropertyName("targetKeyKalemTuru")]
    public long? TargetKeyKalemTuru { get; set; }

    [JsonPropertyName("targetKeyKalemBirim")]
    public long? TargetKeyKalemBirim { get; set; }

    /// <summary>Boşsa başlıktaki <c>targetSisDovizKey</c> kullanılır.</summary>
    [JsonPropertyName("targetKeySisDoviz")]
    public long? TargetKeySisDoviz { get; set; }

    [JsonPropertyName("targetKeyScfOdemePlani")]
    public long? TargetKeyScfOdemePlani { get; set; }

    [JsonPropertyName("targetKeyScfBankaOdemePlani")]
    public long? TargetKeyScfBankaOdemePlani { get; set; }

    [JsonPropertyName("targetKeyBcsBankahesabi")]
    public long? TargetKeyBcsBankahesabi { get; set; }

    [JsonPropertyName("targetPrjProjeKey")]
    public long? TargetPrjProjeKey { get; set; }

    /// <summary>Kalem satırı para birimi (USD/TL); boşsa başlık <c>currencyCode</c>.</summary>
    [JsonPropertyName("lineCurrencyCode")]
    public string? LineCurrencyCode { get; set; }

    /// <summary>RPR yerel birim fiyatı (dövizli belgede TL satırı için).</summary>
    [JsonPropertyName("yerelBirimFiyati")]
    public decimal? YerelBirimFiyati { get; set; }

    [JsonPropertyName("sonBirimFiyati")]
    public decimal? SonBirimFiyati { get; set; }

    /// <summary>RAW: istemci gönderirse kullanılır; aksi veya güvenilmez (ör. çapraz dövizde 1) sunucu hesaplar.</summary>
    [JsonPropertyName("dovizKuru")]
    public string? DovizKuru { get; set; }

    /// <summary>RAW: satır <c>raporlamadovizkuru</c>.</summary>
    [JsonPropertyName("raporlamaDovizKuru")]
    public string? RaporlamaDovizKuru { get; set; }

    /// <summary>MLZM / HZMT; boşsa lineTypeLabel ile tahmin.</summary>
    [JsonPropertyName("kalemTuru")]
    public string? KalemTuru { get; set; }
}

public sealed class InvoiceTransferResultDto
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("createdInvoiceKey")]
    public long? CreatedInvoiceKey { get; set; }

    [JsonPropertyName("createdTargetType")]
    public string? CreatedTargetType { get; set; } // FATURA

    [JsonPropertyName("createdVerified")]
    public bool CreatedVerified { get; set; }

    [JsonPropertyName("createdVerifyMessage")]
    public string? CreatedVerifyMessage { get; set; }

    [JsonPropertyName("targetFirmaKodu")]
    public int? TargetFirmaKodu { get; set; }

    [JsonPropertyName("targetDonemKodu")]
    public int? TargetDonemKodu { get; set; }

    [JsonPropertyName("targetSubeKey")]
    public long? TargetSubeKey { get; set; }

    [JsonPropertyName("targetDepoKey")]
    public long? TargetDepoKey { get; set; }

    [JsonPropertyName("createdKalemKeys")]
    public List<long> CreatedKalemKeys { get; set; } = new();

    [JsonPropertyName("transferredLineCount")]
    public int TransferredLineCount { get; set; }

    [JsonPropertyName("duplicateSkippedCount")]
    public int DuplicateSkippedCount { get; set; }

    /// <summary>Kaynak kalem anahtarları: bu istekte gerçekten aktarılanlar.</summary>
    [JsonPropertyName("transferredSourceKalemKeys")]
    public List<long> TransferredSourceKalemKeys { get; set; } = new();

    /// <summary>Kaynak kalem anahtarları: duplicate registry nedeniyle atlananlar.</summary>
    [JsonPropertyName("duplicateSkippedSourceKalemKeys")]
    public List<long> DuplicateSkippedSourceKalemKeys { get; set; } = new();

    [JsonPropertyName("errors")]
    public List<string> Errors { get; set; } = new();

    /// <summary>
    /// UI uyarıları: DİA payload'ına eklenmek istenen bazı "extra" alanlar flatten edilemediği için
    /// (veya güvenlik/tenant uyumsuzluğu nedeniyle) atlandı.
    /// </summary>
    [JsonPropertyName("skippedExtraFields")]
    public List<InvoiceSkippedExtraFieldDto> SkippedExtraFields { get; set; } = new();

    // UI için: hatanın hangi aşamada / hangi kodla çıktığı
    [JsonPropertyName("failureStage")]
    public string? FailureStage { get; set; } // e.g. target_period_resolve, target_cari_resolve, target_stock_resolve, unit_resolve, create_document

    [JsonPropertyName("failureCode")]
    public string? FailureCode { get; set; } // e.g. target_period_unresolved, target_cari_not_found, stock_not_found_in_target, unit_unresolved, unexpected_transfer_error

    [JsonPropertyName("details")]
    public string? Details { get; set; }

    [JsonPropertyName("diaPayload")]
    public object? DiaPayload { get; set; }

    [JsonPropertyName("diaResponse")]
    public string? DiaResponse { get; set; }

    [JsonPropertyName("traceId")]
    public string? TraceId { get; set; }
}

public sealed class InvoiceSkippedExtraFieldDto
{
    [JsonPropertyName("scope")]
    public string Scope { get; set; } = "line"; // header | line

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty; // blocked_name | contains_key | not_primitive
}

