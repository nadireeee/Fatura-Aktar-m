using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace DiaErpIntegration.API.Models
{
    // ══════════════════════════════════════════════════════════════════════════
    // ENUM'LAR — sadece uygulama katmanında yaşar, DİA'da standart değil
    // ══════════════════════════════════════════════════════════════════════════

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum TransferStatus { Bekliyor = 0, Kismi = 1, Aktarildi = 2, Hata = 3 }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum DuplicateRiskLevel { Yok = 0, Dusuk = 1, Yuksek = 2, Kesin = 3 }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum MappingStatus { Eslenmedi = 0, Kismi = 1, Tamam = 2 }

    // ══════════════════════════════════════════════════════════════════════════
    // ÜST GRİD — scf_fatura_liste_view KAYNAĞI
    // Sadece view'daki gerçek kolon adları kullanılır
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Üst grid satırı — kaynak: scf_fatura_liste_view
    /// Standart DİA view alanları + uygulama özel alanlar ayrılmış
    /// </summary>
    public class InvoiceListRowDto
    {
        // ── Sistem (standart DİA meta) ────────────────────────────────────────
        [JsonPropertyName("key")]
        public string Key { get; set; } = string.Empty;


        [JsonPropertyName("_cdate")]
        public DateTime? CDate { get; set; }

        [JsonPropertyName("_user")]
        public string? User { get; set; }

        // ── Fatura kimlik (standart scf_fatura_liste_view) ────────────────────
        [JsonPropertyName("fisno")]
        public string FisNo { get; set; } = string.Empty;

        [JsonPropertyName("belgeno")]
        public string BelgeNo { get; set; } = string.Empty;

        [JsonPropertyName("belgeno2")]
        public string? BelgeNo2 { get; set; }

        [JsonPropertyName("tarih")]
        public DateTime Tarih { get; set; }

        [JsonPropertyName("saat")]
        public string? Saat { get; set; }

        [JsonPropertyName("turu_txt")]
        public string TuruTxt { get; set; } = string.Empty;

        [JsonPropertyName("kategori")]
        public string? Kategori { get; set; }

        // ── Cari bilgileri (standart) ─────────────────────────────────────────
        [JsonPropertyName("carikartkodu")]
        public string CariKartKodu { get; set; } = string.Empty;

        [JsonPropertyName("cariunvan")]
        public string CariUnvan { get; set; } = string.Empty;

        [JsonPropertyName("carivergitcno")]
        public string? CariVergiTcNo { get; set; }

        // ── Firma / Şube / Depo (standart view alanları) ─────────────────────
        [JsonPropertyName("firmaadi")]
        public string FirmaAdi { get; set; } = string.Empty;

        [JsonPropertyName("sourcesubeadi")]
        public string SourceSubeAdi { get; set; } = string.Empty;

        [JsonPropertyName("subekodu")]
        public string SubeKodu { get; set; } = string.Empty;

        [JsonPropertyName("sourcedepoadi")]
        public string? SourceDepoAdi { get; set; }


        [JsonPropertyName("destsubeadi")]
        public string? DestSubeAdi { get; set; }

        [JsonPropertyName("destdepoadi")]
        public string? DestDepoAdi { get; set; }

        // ── Döviz (standart) ──────────────────────────────────────────────────
        [JsonPropertyName("dovizadi")]
        public string DovizAdi { get; set; } = "TRY";

        [JsonPropertyName("dovizkuru")]
        public decimal DovizKuru { get; set; } = 1m;

        // ── Finansal toplamlar (standart scf_fatura_liste_view) ───────────────
        [JsonPropertyName("toplam")]
        public decimal Toplam { get; set; }

        [JsonPropertyName("toplamdvz")]
        public decimal ToplamDvz { get; set; }

        [JsonPropertyName("toplamkdv")]
        public decimal ToplamKdv { get; set; }

        [JsonPropertyName("toplamkdvdvz")]
        public decimal ToplamKdvDvz { get; set; }

        [JsonPropertyName("toplamkdvtevkifati")]
        public decimal ToplamKdvTevkifati { get; set; }

        [JsonPropertyName("toplamindirim")]
        public decimal ToplamIndirim { get; set; }

        [JsonPropertyName("toplammasraf")]
        public decimal ToplamMasraf { get; set; }

        [JsonPropertyName("toplamov")]
        public decimal ToplamOv { get; set; }

        [JsonPropertyName("net")]
        public decimal Net { get; set; }

        [JsonPropertyName("netdvz")]
        public decimal NetDvz { get; set; }

        // ── Durum flag'leri (standart) ────────────────────────────────────────
        [JsonPropertyName("iptal")]
        public bool Iptal { get; set; }

        [JsonPropertyName("kilitli")]
        public bool Kilitli { get; set; }

        [JsonPropertyName("muhasebelesme")]
        public bool Muhasebelesme { get; set; }

        [JsonPropertyName("kapanmadurumu")]
        public int KapanmaDurumu { get; set; }

        [JsonPropertyName("kasadurum")]
        public int KasaDurum { get; set; }

        // ── e-Fatura (standart) ───────────────────────────────────────────────
        [JsonPropertyName("efatura_durum_txt")]
        public string? EFaturaDurumTxt { get; set; }

        [JsonPropertyName("efaturasenaryosu")]
        public string? EFaturaSenaryosu { get; set; }

        [JsonPropertyName("earsiv_durum")]
        public string? EArsivDurum { get; set; }

        // ── Kullanıcı & diğer (standart) ─────────────────────────────────────
        [JsonPropertyName("kullaniciadi")]
        public string? KullaniciAdi { get; set; }

        [JsonPropertyName("satiselemani")]
        public string? SatisElemani { get; set; }

        [JsonPropertyName("vadegun")]
        public int VadeGun { get; set; }

        [JsonPropertyName("aciklama1")]
        public string? Aciklama1 { get; set; }

        [JsonPropertyName("aciklama2")]
        public string? Aciklama2 { get; set; }

        // ─────────────────────────────────────────────────────────────────────
        // UYGULAMA ÖZEL ALANLAR — standart DİA kolonu değildir
        // Sadece uygulama DTO katmanında yaşar
        // ─────────────────────────────────────────────────────────────────────

        [NotMapped]
        [JsonPropertyName("transfer_status")]
        public TransferStatus TransferStatus { get; set; } = TransferStatus.Bekliyor;

        [NotMapped]
        [JsonPropertyName("duplicate_risk")]
        public DuplicateRiskLevel DuplicateRisk { get; set; } = DuplicateRiskLevel.Yok;

        [NotMapped]
        [JsonPropertyName("transfer_log_id")]
        public string? TransferLogId { get; set; }

        // Bekleyen kalem sayısı — UI için hesaplanan, standart değil
        [NotMapped]
        [JsonPropertyName("bekleyen_kalem_sayisi")]
        public int BekleyenKalemSayisi { get; set; }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // FATURA BAŞLIK DETAY — scf_fatura TABLOSU (backend kayıt işlemleri için)
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Kayıt oluşturma/aktarım için gerçek tablo yapısı.
    /// Kaynak: scf_fatura (tablo, view değil)
    /// </summary>
    public class InvoiceHeaderDto
    {
        // Sistem (standart)
        public string Key { get; set; } = string.Empty;
        public string? KeySisSuBeSource { get; set; }   // _key_sis_sube_source
        public string? KeySisSuBeDest { get; set; }     // _key_sis_sube_dest
        public string? KeySisFirmaDest { get; set; }    // _key_sis_firma_dest
        public string? KeySisDepoSource { get; set; }   // _key_sis_depo_source
        public string? KeySisDepoDest { get; set; }     // _key_sis_depo_dest
        public string? KeySisDoviz { get; set; }        // _key_sis_doviz
        public string? KeyScfCariKart { get; set; }     // _key_scf_carikart
        public string? KeyScfOdemePlani { get; set; }   // _key_scf_odeme_plani
        public string? KeyMuhMasrafMerkezi { get; set; } // _key_muh_masrafmerkezi
        public string? KeyPrjProje { get; set; }        // _key_prj_proje
        public string? KeySisOzelKod1 { get; set; }     // _key_sis_ozelkod1
        public string? KeySisOzelKod2 { get; set; }     // _key_sis_ozelkod2
        public string? KeySisSeviyeKodu { get; set; }   // _key_sis_seviyekodu
        public string? KeySisUstIslemTuru { get; set; } // _key_sis_ust_islem_turu

        // Kimlik (standart scf_fatura)
        public string FisNo { get; set; } = string.Empty;
        public string? BelgeNo { get; set; }
        public string? BelgeNo2 { get; set; }
        public string? BelgeTuru { get; set; }
        public int Turu { get; set; }
        public DateTime Tarih { get; set; }
        public string? Saat { get; set; }

        // Durumlar (standart)
        public bool Iptal { get; set; }
        public string? IptalNedeni { get; set; }
        public bool Kilitli { get; set; }

        // Finansal (standart)
        public decimal Toplam { get; set; }
        public decimal ToplamDvz { get; set; }
        public decimal ToplamKdv { get; set; }
        public decimal ToplamKdvTevkifati { get; set; }
        public decimal ToplamIndirim { get; set; }
        public decimal ToplamMasraf { get; set; }
        public decimal ToplamOv { get; set; }
        public decimal Net { get; set; }
        public decimal DovizKuru { get; set; }
        public decimal RaporlamaDovizKuru { get; set; }

        // Açıklama (standart)
        public string? Aciklama1 { get; set; }
        public string? Aciklama2 { get; set; }
        public string? Aciklama3 { get; set; }

        // Sevk (standart)
        public string? SevkAdresi1 { get; set; }
        public string? SevkAdresi2 { get; set; }
        public string? SevkAdresi3 { get; set; }

        // Ek alanlar (standart scf_fatura.ekalan1-6)
        public string? EkAlan1 { get; set; }
        public string? EkAlan2 { get; set; }
        public string? EkAlan3 { get; set; }
        public string? EkAlan4 { get; set; }
        public string? EkAlan5 { get; set; }
        public string? EkAlan6 { get; set; }

        // Gider paylaştırıcı (standart)
        public decimal Navlun { get; set; }
        public decimal NavlunDvz { get; set; }
        public decimal NavlunKdv { get; set; }
        public decimal Komisyon { get; set; }
        public decimal KomisyonDvz { get; set; }
        public decimal Stopaj { get; set; }
        public decimal StopajDvz { get; set; }
        public decimal StopajYuzde { get; set; }
        public decimal BagKur { get; set; }
        public decimal BagKurDvz { get; set; }
        public decimal Borsa { get; set; }
        public decimal BorsaDvz { get; set; }
        public decimal Ssdf { get; set; }
        public decimal SsdfDvz { get; set; }

        // Vade (standart)
        public int OrtalamaVade { get; set; }
        public string? KarsiFirema { get; set; }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ALT GRİD — scf_fatura_kalemi_liste_view KAYNAĞI
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Alt grid satırı — kaynak: scf_fatura_kalemi_liste_view
    /// Standart DİA view alanları + uygulama özel alanlar ayrılmış
    /// </summary>
    public class InvoiceLineListRowDto
    {
        // ── Sistem (standart) ─────────────────────────────────────────────────
        // Frontend tek bir alan adı bekliyor: "key"
        // (Mock + UI tutarlılığı için "_key" yerine "key" kullanıyoruz.)
        [JsonPropertyName("key")]
        public string Key { get; set; } = string.Empty;

        // Frontend alan adı: "faturakey"
        [JsonPropertyName("faturakey")]
        public string FaturaKey { get; set; } = string.Empty;

        // ── Sıra & Tür (standart) ─────────────────────────────────────────────
        [JsonPropertyName("sirano")]
        public int SiraNo { get; set; }

        [JsonPropertyName("kalemturu")]
        public int KalemTuru { get; set; }

        // ── Stok / Hizmet (standart kalemi_liste_view) ────────────────────────
        [JsonPropertyName("stokhizmetkodu")]
        public string StokHizmetKodu { get; set; } = string.Empty;

        [JsonPropertyName("stokhizmetaciklama")]
        public string StokHizmetAciklama { get; set; } = string.Empty;

        [JsonPropertyName("stokkartmarka")]
        public string? StokKartMarka { get; set; }

        [JsonPropertyName("kalemozelkodu")]
        public string? KalemOzelKodu { get; set; }

        // ── Birim & Miktar (standart) ─────────────────────────────────────────
        [JsonPropertyName("birimkodu")]
        public string BirimKodu { get; set; } = string.Empty;

        [JsonPropertyName("anabirimkodu")]
        public string? AnabirimKodu { get; set; }

        [JsonPropertyName("miktar")]
        public decimal Miktar { get; set; }

        [JsonPropertyName("anamiktar")]
        public decimal AnaMiktar { get; set; }

        // ── Fiyat (standart) ──────────────────────────────────────────────────
        [JsonPropertyName("birimfiyati")]
        public decimal BirimFiyati { get; set; }

        [JsonPropertyName("sonbirimfiyati")]
        public decimal SonBirimFiyati { get; set; }

        [JsonPropertyName("yerelbirimfiyati")]
        public decimal YerelBirimFiyati { get; set; }

        [JsonPropertyName("tutari")]
        public decimal Tutari { get; set; }

        [JsonPropertyName("tutarisatirdovizi")]
        public decimal TutariSatirDovizi { get; set; }

        [JsonPropertyName("dovizkuru")]
        public decimal DovizKuru { get; set; }

        [JsonPropertyName("dovizadi")]
        public string DovizAdi { get; set; } = "TRY";

        // ── İndirim (standart) ────────────────────────────────────────────────
        [JsonPropertyName("indirim1")]
        public decimal Indirim1 { get; set; }

        [JsonPropertyName("indirim2")]
        public decimal Indirim2 { get; set; }

        [JsonPropertyName("indirim3")]
        public decimal Indirim3 { get; set; }

        [JsonPropertyName("indirim4")]
        public decimal Indirim4 { get; set; }

        [JsonPropertyName("indirim5")]
        public decimal Indirim5 { get; set; }

        [JsonPropertyName("indirimtoplam")]
        public decimal IndirimToplam { get; set; }

        [JsonPropertyName("indirimtutari")]
        public decimal IndirimTutari { get; set; }

        [JsonPropertyName("kdvdahilindirimtoplamtutar")]
        public decimal KdvDahilIndirimToplamTutar { get; set; }

        // ── KDV (standart) ────────────────────────────────────────────────────
        [JsonPropertyName("kdv")]
        public decimal Kdv { get; set; }

        [JsonPropertyName("kdvtutari")]
        public decimal KdvTutari { get; set; }

        [JsonPropertyName("kdvdurumu")]
        public int KdvDurumu { get; set; }

        [JsonPropertyName("kdvtevkifatorani")]
        public decimal KdvTevkifatOrani { get; set; }

        [JsonPropertyName("kdvtevkifattutari")]
        public decimal KdvTevkifatTutari { get; set; }

        // ── ÖTV (standart) ────────────────────────────────────────────────────
        [JsonPropertyName("ovtutartutari")]
        public decimal OvTutarTutari { get; set; }

        [JsonPropertyName("ovtutartutari2")]
        public decimal OvTutarTutari2 { get; set; }

        [JsonPropertyName("ovkdvoran")]
        public decimal OvKdvOran { get; set; }

        [JsonPropertyName("ovkdvtutari")]
        public decimal OvKdvTutari { get; set; }

        [JsonPropertyName("ovorantutari")]
        public decimal OvOranTutari { get; set; }

        [JsonPropertyName("ovtoplamtutari")]
        public decimal OvToplamTutari { get; set; }

        // ── Konum (standart) ──────────────────────────────────────────────────
        [JsonPropertyName("depoadi")]
        public string? DepoAdi { get; set; }

        [JsonPropertyName("karsidepoadi")]
        public string? KarsiDepoAdi { get; set; }

        [JsonPropertyName("masrafmerkezikodu")]
        public string? MasrafMerkeziKodu { get; set; }

        [JsonPropertyName("masrafmerkeziaciklama")]
        public string? MasrafMerkeziAciklama { get; set; }

        [JsonPropertyName("projekodu")]
        public string? ProjeKodu { get; set; }

        [JsonPropertyName("projeaciklama")]
        public string? ProjeAciklama { get; set; }

        // ── İrsaliye (standart) ───────────────────────────────────────────────
        [JsonPropertyName("irsaliyeno")]
        public string? IrsaliyeNo { get; set; }

        [JsonPropertyName("irsaliyetarih")]
        public DateTime? IrsaliyeTarih { get; set; }

        // ── Sipariş (standart) ────────────────────────────────────────────────
        [JsonPropertyName("siparisno")]
        public string? SiparisNo { get; set; }

        [JsonPropertyName("siparistarih")]
        public DateTime? SiparisTarih { get; set; }

        // ── Ödeme planı (standart) ────────────────────────────────────────────
        [JsonPropertyName("odemeplanikodu")]
        public string? OdemePlaniKodu { get; set; }

        [JsonPropertyName("odemeplaniaciklama")]
        public string? OdemePlaniAciklama { get; set; }

        // ── Notlar (standart) ─────────────────────────────────────────────────
        [JsonPropertyName("note")]
        public string? Note { get; set; }

        [JsonPropertyName("note2")]
        public string? Note2 { get; set; }

        // ── Özel alanlar (standart scf_fatura_kalemi_liste_view) ─────────────
        [JsonPropertyName("ozelalan1")]
        public string? OzelAlan1 { get; set; }

        [JsonPropertyName("ozelalan2")]
        public string? OzelAlan2 { get; set; }

        [JsonPropertyName("ozelalan3")]
        public string? OzelAlan3 { get; set; }

        [JsonPropertyName("ozelalan4")]
        public string? OzelAlan4 { get; set; }

        [JsonPropertyName("ozelalan5")]
        public string? OzelAlan5 { get; set; }

        // ── Müstahsil (standart) ──────────────────────────────────────────────
        [JsonPropertyName("cari_mustahsil_kodu")]
        public string? CariMustahsilKodu { get; set; }

        [JsonPropertyName("cari_mustahsil_unvan")]
        public string? CariMustahsilUnvan { get; set; }

        // ── Maliyet (standart — sadece backend) ───────────────────────────────
        [JsonPropertyName("maliyetfaturano")]
        public string? MaliyetFaturaNo { get; set; }

        [JsonPropertyName("maliyetstokkodu")]
        public string? MaliyetStokKodu { get; set; }

        // ── İade bilgileri (standart) ─────────────────────────────────────────
        [JsonPropertyName("iadeanamiktar")]
        public decimal IadeAnaMiktar { get; set; }

        [JsonPropertyName("iadekalanmiktar")]
        public decimal IadeKalanMiktar { get; set; }

        // ── Ağırlık / Hacim (standart) ────────────────────────────────────────
        [JsonPropertyName("toplambrutagirlik")]
        public decimal ToplamBrutaGirlik { get; set; }

        [JsonPropertyName("toplamnetagirlik")]
        public decimal ToplamNetAGirlik { get; set; }

        [JsonPropertyName("toplambruthacim")]
        public decimal ToplamBrutHacim { get; set; }

        [JsonPropertyName("toplamnethacim")]
        public decimal ToplamNetHacim { get; set; }

        // ── Satış elemanı (standart) ──────────────────────────────────────────
        [JsonPropertyName("satiselemaniaciklama")]
        public string? SatisElemaniAciklama { get; set; }

        [JsonPropertyName("kullaniciadi")]
        public string? KullaniciAdi { get; set; }

        // ─────────────────────────────────────────────────────────────────────
        // UYGULAMA ÖZEL ALANLAR — standart DİA kolonu değildir
        // scf_fatura_kalemi_liste_view içinde bu alanlar YOKTUR
        // ─────────────────────────────────────────────────────────────────────

        [NotMapped]
        [JsonPropertyName("is_selected")]
        public bool IsSelected { get; set; }

        [NotMapped]
        [JsonPropertyName("transfer_status")]
        public TransferStatus TransferStatus { get; set; } = TransferStatus.Bekliyor;

        [NotMapped]
        [JsonPropertyName("mapping_status")]
        public MappingStatus MappingStatus { get; set; } = MappingStatus.Eslenmedi;

        [NotMapped]
        [JsonPropertyName("duplicate_risk")]
        public DuplicateRiskLevel DuplicateRisk { get; set; } = DuplicateRiskLevel.Yok;

        // Aktarım sonrası dolan hedef bilgileri — UYGULAMA ÖZEL
        [NotMapped]
        [JsonPropertyName("target_firma_key")]
        public string? TargetFirmaKey { get; set; }

        [NotMapped]
        [JsonPropertyName("target_firma_kodu")]
        public string? TargetFirmaKodu { get; set; }

        [NotMapped]
        [JsonPropertyName("target_sube_key")]
        public string? TargetSubeKey { get; set; }

        [NotMapped]
        [JsonPropertyName("target_sube_kodu")]
        public string? TargetSubeKodu { get; set; }

        [NotMapped]
        [JsonPropertyName("target_donem_key")]
        public string? TargetDonemKey { get; set; }

        [NotMapped]
        [JsonPropertyName("target_donem_kodu")]
        public string? TargetDonemKodu { get; set; }

        [NotMapped]
        [JsonPropertyName("is_manual_override")]
        public bool IsManualOverride { get; set; }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // KALEM YAZMA DTO — scf_fatura_kalemi TABLOSU (yeni kayıt için)
    // Backend aktarım sırasında kullanılır, UI'a gönderilmez
    // ══════════════════════════════════════════════════════════════════════════

    public class InvoiceLineDto
    {
        // Standart _key FK'lar
        public string? KeyScfFatura { get; set; }        // _key_scf_fatura (hedef)
        public string? KeySisDepoSource { get; set; }    // _key_sis_depo_source
        public string? KeySisDepoDest { get; set; }      // _key_sis_depo_dest
        public string? KeySisDoviz { get; set; }         // _key_sis_doviz
        public string? KeySisOzelKod { get; set; }       // _key_sis_ozelkod
        public string? KeyMuhMasrafMerkezi { get; set; } // _key_muh_masrafmerkezi
        public string? KeyPrjProje { get; set; }         // _key_prj_proje
        public string? KeyScfOdemePlani { get; set; }    // _key_scf_odeme_plani
        public string? KeyScfKalemBirimleri { get; set; } // _key_scf_kalem_birimleri
        public string? KeyKalemTuru { get; set; }        // _key_kalemturu

        // Standart kalem alanları
        public int SiraNo { get; set; }
        public int KalemTuru { get; set; }
        public decimal Miktar { get; set; }
        public decimal BirimFiyati { get; set; }
        public decimal SonBirimFiyati { get; set; }
        public decimal YerelBirimFiyati { get; set; }
        public decimal Tutari { get; set; }
        public decimal DovizKuru { get; set; }
        public decimal Kdv { get; set; }
        public decimal KdvTutari { get; set; }
        public int KdvDurumu { get; set; }
        public string? KdvTevkifatKodu { get; set; }    // kdvtevkifatkodu
        public decimal KdvTevkifatOrani { get; set; }
        public decimal KdvTevkifatTutari { get; set; }
        public decimal Indirim1 { get; set; }
        public decimal Indirim2 { get; set; }
        public decimal Indirim3 { get; set; }
        public decimal Indirim4 { get; set; }
        public decimal Indirim5 { get; set; }
        public decimal IndirimTutari { get; set; }
        public decimal IndirimToplam { get; set; }
        public decimal OvTutarTutari { get; set; }
        public decimal OvKdvOran { get; set; }
        public decimal OvKdvTutari { get; set; }
        public bool OvManuel { get; set; }               // ovmanuel
        public decimal StopajYuzde { get; set; }
        public string? Note { get; set; }
        public string? Note2 { get; set; }
        public string? OzelAlan1 { get; set; }
        public string? OzelAlan2 { get; set; }
        public string? OzelAlan3 { get; set; }
        public string? OzelAlan4 { get; set; }
        public string? OzelAlan5 { get; set; }
        public string? EFaturaTipKodu { get; set; }              // efaturatipkodu
        public string? EFaturaVergiMuafiyetKodu { get; set; }   // efaturavergimuafiyetkodu
        public string? EFaturaVergiMuafiyetSebebi { get; set; } // efaturavergimuafiyetsebebi
    }

    // ══════════════════════════════════════════════════════════════════════════
    // DÖNEM DTO — sis_donem KAYNAĞI
    // ══════════════════════════════════════════════════════════════════════════

    public class TargetPeriodDto
    {
        [JsonPropertyName("key")]
        public string Key { get; set; } = string.Empty;        // sis_donem._key

        [JsonPropertyName("donemkodu")]

        public string DonemKodu { get; set; } = string.Empty;  // sis_donem.donemkodu

        [JsonPropertyName("gorunenkod")]
        public string GorunenKod { get; set; } = string.Empty; // sis_donem.gorunenkod

        [JsonPropertyName("baslangic")]
        public DateTime Baslangic { get; set; }                // sis_donem.baslangic

        [JsonPropertyName("bitis")]
        public DateTime Bitis { get; set; }                    // sis_donem.bitis

        [JsonPropertyName("aktif")]
        public bool Aktif { get; set; }                        // sis_donem.aktif

        [JsonPropertyName("arsiv")]
        public bool Arsiv { get; set; }                        // sis_donem.arsiv

        [JsonPropertyName("ontanimli")]
        public bool Ontanimli { get; set; }                    // sis_donem.ontanimli

        [JsonPropertyName("firma_key")]
        public string? FirmaKey { get; set; }                  // sis_donem._key_sis_firma
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ŞUBE DTO — sis_sube KAYNAĞI
    // ══════════════════════════════════════════════════════════════════════════

    public class TargetBranchDto
    {
        [JsonPropertyName("key")]
        public string Key { get; set; } = string.Empty;       // sis_sube._key

        [JsonPropertyName("subekodu")]

        public string SubeKodu { get; set; } = string.Empty;  // sis_sube.subekodu

        [JsonPropertyName("subeadi")]
        public string SubeAdi { get; set; } = string.Empty;   // sis_sube.subeadi

        [JsonPropertyName("aktif")]
        public bool Aktif { get; set; }                       // sis_sube.aktif

        [JsonPropertyName("merkezmi")]
        public bool MerkezMi { get; set; }                    // sis_sube.merkezmi

        [JsonPropertyName("firma_key")]
        public string? FirmaKey { get; set; }                 // sis_sube._key_sis_firma
    }

    // ══════════════════════════════════════════════════════════════════════════
    // FİRMA DTO — sis_kullanici_firma_parametreleri KAYNAĞI
    // ══════════════════════════════════════════════════════════════════════════

    public class FirmaDto
    {
        [JsonPropertyName("firma_key")]
        public string FirmaKey { get; set; } = string.Empty;  // _key_sis_kullanici veya firma kodu

        [JsonPropertyName("firma_kodu")]
        public string FirmaKodu { get; set; } = string.Empty; // sis_kullanici_firma_parametreleri.firma

        [JsonPropertyName("firma_adi")]
        public string FirmaAdi { get; set; } = string.Empty;
    }

    // ══════════════════════════════════════════════════════════════════════════
    // TRANSFER PLAN — UYGULAMA ÖZEL, DİA STANDART DEĞIL
    // Kalem bazlı hedef firma/şube/dönem bilgisi bu nesneyle taşınır
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Kalem bazlı aktarım planı satırı.
    /// TAMAMIYLA UYGULAMA ÖZEL — scf_fatura_kalemi'nde bu yapı yoktur.
    /// </summary>
    public class TransferPlanLine
    {
        // ── Standart DİA kaynakları ────────────────────────────────────────────
        public string SourceFaturaKey { get; set; } = string.Empty;     // scf_fatura._key
        public string SourceKalemKey { get; set; } = string.Empty;      // scf_fatura_kalemi._key
        public string SourceSubeKey { get; set; } = string.Empty;       // sis_sube._key
        public string SourceFirmaKey { get; set; } = string.Empty;      // kaynak firma key

        // Kalem özet verileri (scf_fatura_kalemi'den alınan standart alanlar)
        public int SiraNo { get; set; }
        public string StokHizmetKodu { get; set; } = string.Empty;      // stokhizmetkodu (view)
        public string StokHizmetAciklama { get; set; } = string.Empty;  // stokhizmetaciklama (view)
        public decimal Miktar { get; set; }                              // miktar
        public decimal BirimFiyati { get; set; }                         // birimfiyati
        public decimal Tutari { get; set; }                              // tutari
        public decimal Kdv { get; set; }                                 // kdv
        public decimal KdvTutari { get; set; }                           // kdvtutari
        public decimal KdvTevkifatOrani { get; set; }                    // kdvtevkifatorani

        // ── UYGULAMA ÖZEL — Hedefleme (standart DİA kolonu değil) ─────────────
        public string TargetFirmaKey { get; set; } = string.Empty;      // ÖZEL
        public string TargetFirmaKodu { get; set; } = string.Empty;     // ÖZEL (görüntü amaçlı)
        public string TargetSubeKey { get; set; } = string.Empty;       // ÖZEL
        public string TargetSubeKodu { get; set; } = string.Empty;      // ÖZEL (görüntü amaçlı)
        public string TargetDonemKey { get; set; } = string.Empty;      // ÖZEL
        public string TargetDonemKodu { get; set; } = string.Empty;     // ÖZEL (görüntü amaçlı)

        // ── UYGULAMA ÖZEL — Durum ─────────────────────────────────────────────
        public TransferStatus TransferStatus { get; set; } = TransferStatus.Bekliyor;
        public MappingStatus MappingStatus { get; set; } = MappingStatus.Eslenmedi;
        public bool IsSelected { get; set; }
        public bool IsManualOverride { get; set; }
        public DuplicateRiskLevel DuplicateRisk { get; set; } = DuplicateRiskLevel.Yok;
    }

    // ══════════════════════════════════════════════════════════════════════════
    // TRANSFER İSTEĞİ — UYGULAMA ÖZEL
    // ══════════════════════════════════════════════════════════════════════════

    public class TransferRequestDto
    {
        // Kaynak — standart DİA referansı
        [JsonPropertyName("source_fatura_key")]
        public string SourceFaturaKey { get; set; } = string.Empty;  // scf_fatura._key

        [JsonPropertyName("source_firma_key")]
        public string SourceFirmaKey { get; set; } = string.Empty;

        [JsonPropertyName("source_sube_key")]
        public string SourceSubeKey { get; set; } = string.Empty;   // sis_sube._key

        // Seçili kalem key'leri — standart scf_fatura_kalemi._key değerleri
        [JsonPropertyName("selected_kalem_keys")]
        public List<string> SelectedKalemKeys { get; set; } = new();

        // Hedef — UYGULAMA ÖZEL (standart DİA kolonu değil)
        [JsonPropertyName("target_firma_key")]
        public string TargetFirmaKey { get; set; } = string.Empty;  // ÖZEL

        [JsonPropertyName("target_firma_kodu")]
        public string TargetFirmaKodu { get; set; } = string.Empty; // ÖZEL

        [JsonPropertyName("target_sube_key")]
        public string TargetSubeKey { get; set; } = string.Empty;   // ÖZEL

        [JsonPropertyName("target_sube_kodu")]
        public string TargetSubeKodu { get; set; } = string.Empty;  // ÖZEL

        [JsonPropertyName("target_donem_key")]
        public string TargetDonemKey { get; set; } = string.Empty;  // ÖZEL

        [JsonPropertyName("target_donem_kodu")]
        public string TargetDonemKodu { get; set; } = string.Empty; // ÖZEL

        // Kontrol — UYGULAMA ÖZEL
        [JsonPropertyName("allow_duplicate_override")]
        public bool AllowDuplicateOverride { get; set; }

        [JsonPropertyName("transferred_by")]
        public string TransferredBy { get; set; } = string.Empty;
    }

    // ══════════════════════════════════════════════════════════════════════════
    // TRANSFER SONUCU — UYGULAMA ÖZEL
    // ══════════════════════════════════════════════════════════════════════════

    public class TransferResultDto
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        // Hedefde oluşturulan — standart DİA referansları
        [JsonPropertyName("new_fatura_key")]
        public string? NewFaturaKey { get; set; }           // yeni scf_fatura._key

        [JsonPropertyName("new_fatura_no")]
        public string? NewFaturaNo { get; set; }            // yeni fisno

        // Sayaçlar
        [JsonPropertyName("total_success")]
        public int TotalSuccess { get; set; }

        [JsonPropertyName("total_duplicate")]
        public int TotalDuplicate { get; set; }

        [JsonPropertyName("total_error")]
        public int TotalError { get; set; }

        // Log detayları
        [JsonPropertyName("process_logs")]
        public List<TransferLogEntryDto> ProcessLogs { get; set; } = new();

        // UYGULAMA ÖZEL — işlem log ID'si
        [JsonPropertyName("transfer_log_id")]
        public string? TransferLogId { get; set; }

        [JsonPropertyName("transferred_at")]
        public DateTime TransferredAt { get; set; } = DateTime.Now;
    }

    // ══════════════════════════════════════════════════════════════════════════
    // TRANSFER LOG SATIRI — UYGULAMA ÖZEL
    // ══════════════════════════════════════════════════════════════════════════

    public class TransferLogEntryDto
    {
        // Standart DİA kalem referansı
        [JsonPropertyName("source_kalem_key")]
        public string SourceKalemKey { get; set; } = string.Empty;

        [JsonPropertyName("stok_hizmet_kodu")]
        public string? StokHizmetKodu { get; set; }

        [JsonPropertyName("stok_hizmet_aciklama")]
        public string? StokHizmetAciklama { get; set; }

        // Hedefde oluşturulan kalem — standart ref
        [JsonPropertyName("new_kalem_key")]
        public string? NewKalemKey { get; set; }

        // UYGULAMA ÖZEL
        [JsonPropertyName("status")]
        public string Status { get; set; } = string.Empty; // "success" | "duplicate" | "error"

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        [JsonPropertyName("was_duplicate_override")]
        public bool WasDuplicateOverride { get; set; }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // DUPLICATE KONTROL — UYGULAMA ÖZEL
    // ══════════════════════════════════════════════════════════════════════════

    public class DuplicateCheckRequest
    {
        // Standart DİA referansları
        public string SourceFaturaKey { get; set; } = string.Empty;
        public string SourceKalemKey { get; set; } = string.Empty;
        public string BelgeNo { get; set; } = string.Empty;         // scf_fatura.belgeno
        public string StokHizmetKodu { get; set; } = string.Empty;  // stokhizmetkodu
        public DateTime FaturaTarih { get; set; }                    // scf_fatura.tarih
        public decimal Miktar { get; set; }                          // scf_fatura_kalemi.miktar
        public decimal Tutari { get; set; }                          // scf_fatura_kalemi.tutari

        // Hedef — UYGULAMA ÖZEL
        public string TargetFirmaKey { get; set; } = string.Empty;
        public string TargetSubeKodu { get; set; } = string.Empty;
        public string TargetDonemKey { get; set; } = string.Empty;
        public string TargetDonemKodu { get; set; } = string.Empty;

    }


    public class DuplicateCheckResult
    {
        public string SourceKalemKey { get; set; } = string.Empty;
        public bool IsDuplicate { get; set; }
        public DuplicateRiskLevel RiskLevel { get; set; }
        public string? ExistingTargetFaturaKey { get; set; }        // varsa hedef scf_fatura._key
        public string? ExistingTargetFaturaNo { get; set; }         // varsa fisno
        public DateTime? PreviousTransferDate { get; set; }
        public string? Reason { get; set; }
        public bool CanOverride { get; set; }
    }
}
