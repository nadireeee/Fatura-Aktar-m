using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiaErpIntegration.API.Models.DiaV3Json;

// DİA WS v3 JSON gateway çağrıları: /sis/json ve /scf/json
// Her request body tek bir root property taşır: { "<servis_adi>": { ... } }

public sealed class DiaSisYetkiliFirmaDonemSubeDepoRequest
{
    [JsonPropertyName("sis_yetkili_firma_donem_sube_depo")]
    public DiaWsRequestBase Payload { get; set; } = new();
}

public sealed class DiaScfFaturaListeleRequest
{
    [JsonPropertyName("scf_fatura_listele")]
    public DiaInvoiceListInput Payload { get; set; } = new();
}

public sealed class DiaSisFirmaGetirRequest
{
    [JsonPropertyName("sis_firma_getir")]
    public DiaWsRequestBase Payload { get; set; } = new();
}

public sealed class DiaInvoiceListInput : DiaWsRequestBase
{
    [JsonPropertyName("filters")]
    public string Filters { get; set; } = string.Empty;

    [JsonPropertyName("sorts")]
    public string Sorts { get; set; } = string.Empty;

    [JsonPropertyName("params")]
    public string Params { get; set; } = string.Empty;

    [JsonPropertyName("limit")]
    public int Limit { get; set; } = 10;

    [JsonPropertyName("offset")]
    public int Offset { get; set; } = 0;
}

public sealed class DiaScfFaturaGetirRequest
{
    [JsonPropertyName("scf_fatura_getir")]
    public DiaInvoiceGetInput Payload { get; set; } = new();
}

public sealed class DiaInvoiceGetInput : DiaWsRequestBase
{
    [JsonPropertyName("key")]
    public long Key { get; set; }
}

public sealed class DiaScfFaturaEkleRequest
{
    [JsonPropertyName("scf_fatura_ekle")]
    public DiaInvoiceAddInput Payload { get; set; } = new();
}

public sealed class DiaInvoiceAddInput : DiaWsRequestBase
{
    [JsonPropertyName("kart")]
    public DiaInvoiceAddCardInput Kart { get; set; } = new();
}

public sealed class DiaInvoiceAddCardInput
{
    [JsonPropertyName("_key_prj_proje")]
    public long? KeyPrjProje { get; set; }

    [JsonPropertyName("_key_scf_carikart")]
    public long? KeyScfCariKart { get; set; }

    [JsonPropertyName("_key_scf_carikart_adresleri")]
    public long? KeyScfCariKartAdresleri { get; set; }

    [JsonPropertyName("_key_scf_carikart_yetkili")]
    public long? KeyScfCariKartYetkili { get; set; }

    [JsonPropertyName("_key_scf_odeme_plani")]
    public long? KeyScfOdemePlani { get; set; }

    [JsonPropertyName("_key_scf_satiselemani")]
    public long? KeyScfSatisElemani { get; set; }

    // DIA bazı tenantlarda satış elemanını bu liste üzerinden okuyor.
    // Kaynak scf_fatura_getir: _key_satiselemanlari bir dizi obje döndürüyor.
    // Aktarımda en güvenlisi aynı shape ile göndermek.
    [JsonPropertyName("_key_satiselemanlari")]
    public List<DiaKeyRef> KeySatisElemanlari { get; set; } = new();

    [JsonPropertyName("_key_sis_sube_source")]
    public long KeySisSubeSource { get; set; }

    [JsonPropertyName("_key_sis_depo_source")]
    public long KeySisDepoSource { get; set; }

    [JsonPropertyName("_key_sis_doviz")]
    public long? KeySisDoviz { get; set; }

    [JsonPropertyName("_key_sis_doviz_raporlama")]
    public long? KeySisDovizRaporlama { get; set; }

    [JsonPropertyName("aciklama1")]
    public string? Aciklama1 { get; set; }

    [JsonPropertyName("aciklama2")]
    public string? Aciklama2 { get; set; }

    [JsonPropertyName("aciklama3")]
    public string? Aciklama3 { get; set; }

    [JsonPropertyName("belgeno2")]
    public string? BelgeNo2 { get; set; }

    [JsonPropertyName("belgeno")]
    public string? BelgeNo { get; set; }

    // Not: scf_fatura_ekle bazı tenantlarda fisno'yu otomatik üretir.
    // Ancak payload'ta boş gitmemesi için opsiyonel map ediyoruz.
    [JsonPropertyName("fisno")]
    public string? Fisno { get; set; }

    [JsonPropertyName("dovizkuru")]
    public string? DovizKuru { get; set; }

    [JsonPropertyName("raporlamadovizkuru")]
    public string? RaporlamaDovizKuru { get; set; }

    [JsonPropertyName("tarih")]
    public string? Tarih { get; set; }

    [JsonPropertyName("saat")]
    public string? Saat { get; set; }

    [JsonPropertyName("turu")]
    public int? Turu { get; set; }

    [JsonPropertyName("sevkadresi1")]
    public string? SevkAdresi1 { get; set; }

    [JsonPropertyName("sevkadresi2")]
    public string? SevkAdresi2 { get; set; }

    [JsonPropertyName("sevkadresi3")]
    public string? SevkAdresi3 { get; set; }

    [JsonPropertyName("m_altlar")]
    public List<DiaInvoiceAddAltInput> Altlar { get; set; } = new();

    [JsonPropertyName("m_kalemler")]
    public List<DiaInvoiceAddLineInput> Lines { get; set; } = new();

    // Kaynak scf_fatura_getir response'unda bulunan ekstra alanları (Ek Alanlar / Detay / Kümülatif vb.)
    // hedefte de aynen göndermek için extension data olarak taşırız.
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtraFields { get; set; }
}

public sealed class DiaKeyRef
{
    [JsonPropertyName("_key")]
    public long? Key { get; set; }
}

public sealed class DiaInvoiceAddAltInput
{
    [JsonPropertyName("_key")]
    public long? Key { get; set; }

    [JsonPropertyName("_key_kalemturu")]
    public long? KeyKalemTuru { get; set; }

    [JsonPropertyName("_key_scf_hediyeceki")]
    public long? KeyScfHediyeCeki { get; set; }

    [JsonPropertyName("_key_scf_sif")]
    public long? KeyScfSif { get; set; }

    [JsonPropertyName("_key_sis_doviz")]
    public long? KeySisDoviz { get; set; }

    [JsonPropertyName("deger")]
    public string? Deger { get; set; }

    [JsonPropertyName("dovizkuru")]
    public string? DovizKuru { get; set; }

    [JsonPropertyName("etkin")]
    public string? Etkin { get; set; }

    [JsonPropertyName("kalemturu")]
    public string? KalemTuru { get; set; }

    [JsonPropertyName("turu")]
    public string? Turu { get; set; }

    [JsonPropertyName("tutar")]
    public string? Tutar { get; set; }
}

public sealed class DiaInvoiceAddLineInput
{
    [JsonPropertyName("_key_bcs_bankahesabi")]
    public long? KeyBcsBankahesabi { get; set; }

    [JsonPropertyName("_key_kalemturu")]
    public long? KeyKalemTuru { get; set; }

    [JsonPropertyName("_key_prj_proje")]
    public long? KeyPrjProje { get; set; }

    [JsonPropertyName("_key_scf_banka_odeme_plani")]
    public long? KeyScfBankaOdemePlani { get; set; }

    // DİA kalem grid'inde "Ödeme Planı" kolonu satırdan da okunabiliyor.
    // Header'da set edilse bile bazı tenantlarda satırda boş görünebiliyor.
    [JsonPropertyName("_key_scf_odeme_plani")]
    public long? KeyScfOdemePlani { get; set; }

    [JsonPropertyName("_key_scf_kalem_birimleri")]
    public long? KeyKalemBirim { get; set; }

    [JsonPropertyName("_key_sis_depo_source")]
    public long? KeyDepoSource { get; set; }

    [JsonPropertyName("_key_sis_doviz")]
    public long? KeyDoviz { get; set; }

    [JsonPropertyName("kalemturu")]
    public string? KalemTuru { get; set; }

    [JsonPropertyName("anamiktar")]
    public string? AnaMiktar { get; set; }

    [JsonPropertyName("miktar")]
    public string? Miktar { get; set; }

    [JsonPropertyName("birimfiyati")]
    public string? BirimFiyati { get; set; }

    [JsonPropertyName("sonbirimfiyati")]
    public string? SonBirimFiyati { get; set; }

    [JsonPropertyName("yerelbirimfiyati")]
    public string? YerelBirimFiyati { get; set; }

    [JsonPropertyName("tutari")]
    public string? Tutari { get; set; }

    [JsonPropertyName("kdv")]
    public string? Kdv { get; set; }

    [JsonPropertyName("kdvtutari")]
    public string? KdvTutari { get; set; }

    [JsonPropertyName("kdvdurumu")]
    public string? KdvDurumu { get; set; }

    [JsonPropertyName("dovizkuru")]
    public string? DovizKuru { get; set; }

    [JsonPropertyName("sirano")]
    public int? SiraNo { get; set; }

    [JsonPropertyName("note")]
    public string? Note { get; set; }

    [JsonPropertyName("note2")]
    public string? Note2 { get; set; }

    [JsonPropertyName("m_varyantlar")]
    public List<object> Variants { get; set; } = new();

    // Kaynaktaki satır "Diğer/Detay" primitive alanlarını güvenli şekilde taşımak için.
    // (Object/Array göndermek tenant'a göre tip hatası yaratabildiği için sanitize edilir.)
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtraFields { get; set; }
}

public sealed class DiaInvoiceAddResponse : DiaWsResponseBase
{
    [JsonPropertyName("key")]
    public string? Key { get; set; }

    [JsonPropertyName("extra")]
    public DiaInvoiceAddResponseExtra? Extra { get; set; }
}

public sealed class DiaInvoiceAddResponseExtra
{
    [JsonPropertyName("kalemlerKeys")]
    public List<long> KalemlerKeys { get; set; } = new();
}

// ─────────────────────────────────────────────────────────────────────────────
// scf_carihesap_fisi_ekle / scf_carihesap_fisi_getir (Virman Fişi)
// Not: Payload şekli scf/json gateway üzerinden aynıdır.
// ─────────────────────────────────────────────────────────────────────────────

public sealed class DiaScfCariHesapFisiEkleRequest
{
    [JsonPropertyName("scf_carihesap_fisi_ekle")]
    public DiaCariHesapFisiAddInput Payload { get; set; } = new();
}

public sealed class DiaCariHesapFisiAddInput : DiaWsRequestBase
{
    [JsonPropertyName("kart")]
    public DiaCariHesapFisiCardInput Kart { get; set; } = new();
}

public sealed class DiaCariHesapFisiCardInput
{
    [JsonPropertyName("_key_scf_malzeme_baglantisi")]
    public long? KeyScfMalzemeBaglantisi { get; set; }

    [JsonPropertyName("_key_scf_odeme_plani")]
    public long? KeyScfOdemePlani { get; set; }

    [JsonPropertyName("_key_sis_ozelkod")]
    public long? KeySisOzelKod { get; set; }

    [JsonPropertyName("_key_sis_seviyekodu")]
    public long? KeySisSeviyeKod { get; set; }

    [JsonPropertyName("_key_sis_sube")]
    public long? KeySisSube { get; set; }

    [JsonPropertyName("aciklama1")]
    public string? Aciklama1 { get; set; }

    [JsonPropertyName("aciklama2")]
    public string? Aciklama2 { get; set; }

    [JsonPropertyName("aciklama3")]
    public string? Aciklama3 { get; set; }

    [JsonPropertyName("belgeno")]
    public string? Belgeno { get; set; }

    [JsonPropertyName("fisno")]
    public string? Fisno { get; set; }

    [JsonPropertyName("saat")]
    public string? Saat { get; set; }

    [JsonPropertyName("tarih")]
    public string? Tarih { get; set; }

    [JsonPropertyName("turu")]
    public string? Turu { get; set; } // "VF"

    [JsonPropertyName("m_kalemler")]
    public List<DiaCariHesapFisiAddLineInput> Lines { get; set; } = new();
}

public sealed class DiaCariHesapFisiAddLineInput
{
    [JsonPropertyName("_key_bcs_bankahesabi")]
    public long? KeyBcsBankahesabi { get; set; }

    [JsonPropertyName("_key_muh_masrafmerkezi")]
    public long? KeyMuhMasrafMerkezi { get; set; }

    [JsonPropertyName("_key_ote_rezervasyonkarti")]
    public long? KeyOteRezervasyonKarti { get; set; }

    [JsonPropertyName("_key_prj_proje")]
    public long? KeyPrjProje { get; set; }

    [JsonPropertyName("_key_scf_banka_odeme_plani")]
    public long? KeyScfBankaOdemePlani { get; set; }

    [JsonPropertyName("_key_scf_odeme_plani")]
    public long? KeyScfOdemePlani { get; set; }

    [JsonPropertyName("_key_scf_satiselemani")]
    public long? KeyScfSatisElemani { get; set; }

    [JsonPropertyName("_key_shy_servisformu")]
    public long? KeyShyServisFormu { get; set; }

    [JsonPropertyName("_key_sis_doviz")]
    public long? KeySisDoviz { get; set; }

    [JsonPropertyName("_key_sis_doviz_raporlama")]
    public long? KeySisDovizRaporlama { get; set; }

    [JsonPropertyName("_key_sis_ozelkod")]
    public long? KeySisOzelKod { get; set; }

    [JsonPropertyName("aciklama")]
    public string? Aciklama { get; set; }

    [JsonPropertyName("borc")]
    public string? Borc { get; set; }

    [JsonPropertyName("alacak")]
    public string? Alacak { get; set; }

    [JsonPropertyName("dovizkuru")]
    public string? DovizKuru { get; set; }

    [JsonPropertyName("kurfarkialacak")]
    public string? KurFarkiAlacak { get; set; }

    [JsonPropertyName("kurfarkiborc")]
    public string? KurFarkiBorc { get; set; }

    [JsonPropertyName("raporlamadovizkuru")]
    public string? RaporlamaDovizKuru { get; set; }

    [JsonPropertyName("vade")]
    public string? Vade { get; set; }

    [JsonPropertyName("__dinamik__1")]
    public string? DinamikSubeler1 { get; set; }
}

public sealed class DiaCariHesapFisiAddResponse : DiaWsResponseBase
{
    [JsonPropertyName("key")]
    public string? Key { get; set; }
}

public sealed class DiaScfCariHesapFisiGetirRequest
{
    [JsonPropertyName("scf_carihesap_fisi_getir")]
    public DiaCariHesapFisiGetInput Payload { get; set; } = new();
}

public sealed class DiaCariHesapFisiGetInput : DiaWsRequestBase
{
    [JsonPropertyName("key")]
    public long Key { get; set; }
}

