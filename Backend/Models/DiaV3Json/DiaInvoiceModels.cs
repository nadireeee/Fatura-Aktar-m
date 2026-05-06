using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiaErpIntegration.API.Models.DiaV3Json;

// ─────────────────────────────────────────────────────────────────────────────
// scf_fatura_listele
// ─────────────────────────────────────────────────────────────────────────────

public sealed class DiaInvoiceListItem
{
    [JsonPropertyName("_key")]
    public long Key { get; set; }

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

    [JsonPropertyName("__carikartkodu")]
    public string? CariKartKodu { get; set; }

    [JsonPropertyName("__cariunvan")]
    public string? CariUnvan { get; set; }

    [JsonPropertyName("__sourcesubeadi")]
    public string? SourceSubeAdi { get; set; }

    [JsonPropertyName("__sourcedepoadi")]
    public string? SourceDepoAdi { get; set; }

    [JsonPropertyName("__destsubeadi")]
    public string? DestSubeAdi { get; set; }

    [JsonPropertyName("__destdepoadi")]
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
    public JsonElement IptalRaw { get; set; }

    [JsonPropertyName("odemeplani")]
    public string? OdemePlani { get; set; }

    [JsonPropertyName("odemeplanikodu")]
    public string? OdemePlaniKodu { get; set; }

    [JsonPropertyName("__odemeplani")]
    public string? OdemePlaniUnderscore { get; set; }

    [JsonPropertyName("odemeplaniack")]
    public string? OdemePlaniAck { get; set; }

    [JsonPropertyName("__odemeplaniack")]
    public string? OdemePlaniAckUnderscore { get; set; }

    [JsonPropertyName("projekodu")]
    public string? ProjeKodu { get; set; }

    [JsonPropertyName("projeaciklama")]
    public string? ProjeAciklama { get; set; }
}

// ─────────────────────────────────────────────────────────────────────────────
// scf_fatura_getir
// ─────────────────────────────────────────────────────────────────────────────

public sealed class DiaInvoiceDetail
{
    [JsonPropertyName("_key")]
    public long Key { get; set; }

    [JsonPropertyName("fisno")]
    public string? FisNo { get; set; }

    [JsonPropertyName("tarih")]
    public string? Tarih { get; set; }

    [JsonPropertyName("saat")]
    public string? Saat { get; set; }

    [JsonPropertyName("turu")]
    public int? Turu { get; set; }

    [JsonPropertyName("aciklama1")]
    public string? Aciklama1 { get; set; }

    [JsonPropertyName("aciklama2")]
    public string? Aciklama2 { get; set; }

    [JsonPropertyName("aciklama3")]
    public string? Aciklama3 { get; set; }

    [JsonPropertyName("belgeno")]
    public string? BelgeNo { get; set; }

    [JsonPropertyName("belgeno2")]
    public string? BelgeNo2 { get; set; }

    [JsonPropertyName("ortalamavade")]
    public string? Ortalamavade { get; set; }

    // Some tenants return these convenience fields on scf_fatura_getir too.
    [JsonPropertyName("__carikartkodu")]
    public string? CariKartKodu { get; set; }

    [JsonPropertyName("__cariunvan")]
    public string? CariUnvan { get; set; }

    // Other tenants may return plain field names instead of __prefixed.
    [JsonPropertyName("carikartkodu")]
    public string? CariKartKoduPlain { get; set; }

    // Bazı tenant / WS yanıtlarında kod bu alan adlarıyla gelir (__ önekli değil).
    [JsonPropertyName("cari_kodu")]
    public string? CariKoduSnake { get; set; }

    [JsonPropertyName("carikodu")]
    public string? CariKoduCompact { get; set; }

    [JsonPropertyName("cariunvan")]
    public string? CariUnvanPlain { get; set; }

    [JsonPropertyName("dovizkuru")]
    public string? DovizKuru { get; set; }

    [JsonPropertyName("raporlamadovizkuru")]
    public string? RaporlamaDovizKuru { get; set; }

    // Bazı tenantlarda döviz adı kodu olarak string gelir (örn "USD").
    [JsonPropertyName("dovizturu")]
    public string? DovizTuru { get; set; }

    // Sevk adresi alanları (UI'da görünen)
    [JsonPropertyName("sevkadresi1")]
    public string? SevkAdresi1 { get; set; }

    [JsonPropertyName("sevkadresi2")]
    public string? SevkAdresi2 { get; set; }

    [JsonPropertyName("sevkadresi3")]
    public string? SevkAdresi3 { get; set; }

    [JsonPropertyName("_key_prj_proje")]
    public JsonElement KeyPrjProjeRaw { get; set; }

    [JsonPropertyName("_key_scf_carikart")]
    public JsonElement KeyScfCariKartRaw { get; set; }

    // Some tenants expose without leading underscore.
    [JsonPropertyName("key_scf_carikart")]
    public JsonElement KeyScfCariKartRaw2 { get; set; }

    [JsonPropertyName("_key_scf_carikart_adresleri")]
    public JsonElement KeyScfCariKartAdresleriRaw { get; set; }

    [JsonPropertyName("_key_scf_odeme_plani")]
    public JsonElement KeyScfOdemePlaniRaw { get; set; }

    [JsonPropertyName("_key_sis_sube_source")]
    public JsonElement KeySisSubeSourceRaw { get; set; }

    [JsonPropertyName("_key_sis_depo_source")]
    public JsonElement KeySisDepoSourceRaw { get; set; }

    [JsonPropertyName("_key_sis_doviz")]
    public JsonElement KeySisDovizRaw { get; set; }

    [JsonPropertyName("_key_sis_doviz_raporlama")]
    public JsonElement KeySisDovizRaporlamaRaw { get; set; }

    [JsonPropertyName("m_kalemler")]
    public List<DiaInvoiceLine>? Lines { get; set; } = new();

    // Alt indirim/masraf/hediye çeki vb. (m_altlar)
    [JsonPropertyName("m_altlar")]
    public List<DiaInvoiceAlt>? Altlar { get; set; } = new();

    // Kaynak response'da bulunan ekstra/dinamik alanları kaybetmemek için (Ek Alanlar, Detay, Kümülatif vb.)
    // JSON'daki tanımlı olmayan tüm alanları burada tutarız.
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtraFields { get; set; }
}

public sealed class DiaInvoiceAlt
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
    public JsonElement KeySisDovizRaw { get; set; }

    [JsonPropertyName("deger")]
    public string? Deger { get; set; }

    [JsonPropertyName("dovizkuru")]
    public string? DovizKuru { get; set; }

    [JsonPropertyName("etkin")]
    public string? Etkin { get; set; }

    // INDR / MSRF ...
    [JsonPropertyName("kalemturu")]
    public string? KalemTuru { get; set; }

    // TDY / TEK ...
    [JsonPropertyName("turu")]
    public string? Turu { get; set; }

    [JsonPropertyName("tutar")]
    public string? Tutar { get; set; }
}

public sealed class DiaInvoiceLine
{
    [JsonPropertyName("_key")]
    public long Key { get; set; }

    [JsonPropertyName("sirano")]
    public int? SiraNo { get; set; }

    [JsonPropertyName("kalemturu")]
    public string? KalemTuruRaw { get; set; }

    [JsonPropertyName("miktar")]
    [JsonConverter(typeof(DiaLooseNullableDecimalConverter))]
    public decimal? Miktar { get; set; }

    [JsonPropertyName("birimfiyati")]
    [JsonConverter(typeof(DiaLooseNullableDecimalConverter))]
    public decimal? BirimFiyati { get; set; }

    [JsonPropertyName("sonbirimfiyati")]
    [JsonConverter(typeof(DiaLooseNullableDecimalConverter))]
    public decimal? SonBirimFiyati { get; set; }

    [JsonPropertyName("yerelbirimfiyati")]
    [JsonConverter(typeof(DiaLooseNullableDecimalConverter))]
    public decimal? YerelBirimFiyati { get; set; }

    [JsonPropertyName("tutari")]
    [JsonConverter(typeof(DiaLooseNullableDecimalConverter))]
    public decimal? Tutari { get; set; }

    [JsonPropertyName("kdv")]
    [JsonConverter(typeof(DiaLooseNullableDecimalConverter))]
    public decimal? Kdv { get; set; }

    // Bazı tenantlarda KDV yüzdesi ayrı alanda gelir; `kdv` başka anlam taşıyabiliyor.
    [JsonPropertyName("kdvyuzde")]
    [JsonConverter(typeof(DiaLooseNullableDecimalConverter))]
    public decimal? KdvYuzde { get; set; }

    [JsonPropertyName("kdvorani")]
    [JsonConverter(typeof(DiaLooseNullableDecimalConverter))]
    public decimal? KdvOrani { get; set; }

    [JsonPropertyName("kdvyuzdesi")]
    [JsonConverter(typeof(DiaLooseNullableDecimalConverter))]
    public decimal? KdvYuzdesi { get; set; }

    [JsonPropertyName("kdvtutari")]
    [JsonConverter(typeof(DiaLooseNullableDecimalConverter))]
    public decimal? KdvTutari { get; set; }

    [JsonPropertyName("kdvdurumu")]
    public string? KdvDurumuRaw { get; set; }

    [JsonPropertyName("dovizkuru")]
    [JsonConverter(typeof(DiaLooseNullableDecimalConverter))]
    public decimal? DovizKuru { get; set; }

    [JsonPropertyName("indirimtoplam")]
    [JsonConverter(typeof(DiaLooseNullableDecimalConverter))]
    public decimal? IndirimToplam { get; set; }

    [JsonPropertyName("note")]
    public string? Note { get; set; }

    [JsonPropertyName("note2")]
    public string? Note2 { get; set; }

    // Varyantlar (m_varyantlar) bazı kalemlerde dolu gelebiliyor; boş gönderilirse DIA hata verebiliyor.
    [JsonPropertyName("m_varyantlar")]
    public JsonElement VariantsRaw { get; set; }

    [JsonPropertyName("_key_kalemturu")]
    public DiaLineStokHizmetRef? KalemRef { get; set; }

    [JsonPropertyName("_key_sis_doviz")]
    public JsonElement KeySisDovizRaw { get; set; }

    // Birim bilgisi WS'de farklı tiplerde gelebiliyor; JsonElement ile yakalayıp map'liyoruz
    [JsonPropertyName("_key_scf_kalem_birimleri")]
    public JsonElement BirimRaw { get; set; }

    [JsonPropertyName("_key_sis_depo_source")]
    public DiaDepotRef? DepoSource { get; set; }

    [JsonPropertyName("_key_prj_proje")]
    public JsonElement ProjeRaw { get; set; }

    // Dinamik alanlar (tenant farkı):
    // ŞUBELER -> Fatura Kalemi -> kolon bazı firmalarda __dinamik__1, bazılarında __dinamik__2.
    // Not: DİA bazı tenantlarda değeri nested objeler içinde de döndürebiliyor.
    [JsonPropertyName("__dinamik__1")]
    public JsonElement Dinamik1Raw { get; set; }

    [JsonPropertyName("__dinamik__2")]
    public JsonElement Dinamik2Raw { get; set; }

    // Bazı tenantlarda dinamik kolon adı normalize edilerek gelebilir.
    // Örn: "__dinamik__00001" / "__dinamik__00002"
    [JsonPropertyName("__dinamik__00001")]
    public JsonElement Dinamik00001Raw { get; set; }

    [JsonPropertyName("__dinamik__00002")]
    public JsonElement Dinamik00002Raw { get; set; }

    // Fallback okuma için: _key_scf_irsaliye.__dinamik__1 / __dinamik__2
    [JsonPropertyName("_key_scf_irsaliye")]
    public JsonElement KeyScfIrsaliyeRaw { get; set; }

    // Bazı tenantlarda fallback değer _key_scf_irsaliye_kalemi içinde gelebiliyor.
    [JsonPropertyName("_key_scf_irsaliye_kalemi")]
    public JsonElement KeyScfIrsaliyeKalemiRaw { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement> ExtraFields { get; set; } = new();
}

public sealed class DiaLineStokHizmetRef
{
    [JsonPropertyName("_key")]
    public long? Key { get; set; }

    [JsonPropertyName("stokkartkodu")]
    public string? StokKartKodu { get; set; }

    [JsonPropertyName("hizmetkartkodu")]
    public string? HizmetKartKodu { get; set; }

    [JsonPropertyName("aciklama")]
    public string? Aciklama { get; set; }
}

public sealed class DiaDepotRef
{
    [JsonPropertyName("depoadi")]
    public string? DepoAdi { get; set; }
}

public sealed class DiaProjectRef
{
    [JsonPropertyName("kodu")]
    public string? Kodu { get; set; }

    [JsonPropertyName("aciklama")]
    public string? Aciklama { get; set; }
}

