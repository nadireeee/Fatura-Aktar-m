using DiaErpIntegration.API.Models;
using DiaErpIntegration.API.Models.Api;
using DiaErpIntegration.API.Models.DiaV3Json;
using System.Text.Json;

namespace DiaErpIntegration.API.Services
{
    public interface IDiaWsClient
    {
        Task<string> LoginAsync();
        Task LogoutAsync();

        // Lookup: sis_yetkili_firma_donem_sube_depo (result: firma/dönem/şube/depo)
        Task<List<DiaAuthorizedCompanyPeriodBranchItem>> GetAuthorizedCompanyPeriodBranchAsync();
        Task<List<(int FirmaKodu, string FirmaAdi)>> GetAllCompaniesAsync();
        Task<List<DiaAuthorizedPeriodItem>> GetPeriodsByFirmaAsync(int firmaKodu);
        Task<List<DiaAuthorizedBranchItem>> GetSubelerDepolarForFirmaAsync(int firmaKodu, int donemKodu);
        Task<string?> ResolveDynamicBranchColumnAsync(int firmaKodu, int donemKodu);

        // Üst grid: scf_fatura_listele
        Task<List<DiaInvoiceListItem>> GetInvoicesAsync(int firmaKodu, int donemKodu, string filters, int limit, int offset);
        Task<HashSet<long>?> GetDistributableInvoiceKeysAsync(int firmaKodu, int donemKodu, string filters);
        Task<HashSet<long>?> GetInvoiceKeysByUstIslemTuruAsync(int firmaKodu, int donemKodu, string filters, long ustIslemTuruKey);
        Task<Dinamik2ScanResult> ScanInvoiceKeysWithSubelerDinamik2Async(int firmaKodu, int donemKodu, string filters, CancellationToken cancellationToken = default);

        // Alt grid: scf_fatura_getir (result.m_kalemler)
        Task<DiaInvoiceDetail> GetInvoiceAsync(int firmaKodu, int donemKodu, long key);
        Task<DiaInvoiceDetail> GetInvoiceAsyncWithDonemFallback(int firmaKodu, int preferredDonemKodu, long key);
        /// <summary>scf_fatura_getir — en fazla N dönem dene; satır doluysa dön, aksi halde boş.</summary>
        Task<DiaInvoiceDetail> GetInvoiceAsyncWithLimitedDonemFallback(int firmaKodu, int preferredDonemKodu, long key, int maxPeriodAttempts = 3);
        
        // Alt grid fallback: scf_fatura_kalemi_liste_view (some tenants: scf_fatura_getir returns null)
        Task<List<JsonElement>> GetInvoiceLinesViewAsync(int firmaKodu, int donemKodu, long invoiceKey);
        
        // scf_fatura_listele üzerinden (header) cari bilgisi fallback
        Task<(string? cariKodu, string? cariUnvan, long? cariKey)> GetInvoiceCariFromListAsync(int firmaKodu, int donemKodu, long invoiceKey);

        // Aktarım: scf_fatura_ekle
        Task<DiaInvoiceAddResponse> CreateInvoiceAsync(int firmaKodu, int donemKodu, DiaInvoiceAddCardInput card);

        // Aktarım (Virman): scf_carihesap_fisi_ekle
        Task<DiaCariHesapFisiAddResponse> CreateVirmanAsync(int firmaKodu, int donemKodu, DiaCariHesapFisiCardInput card);

        // Doğrulama (Virman): scf_carihesap_fisi_getir
        Task<System.Text.Json.JsonElement> GetVirmanAsync(int firmaKodu, int donemKodu, long key);

        // Transfer mapping lookups (target firma context)
        Task<long?> FindCariKeyByCodeAsync(int firmaKodu, int donemKodu, string cariKartKodu);
        Task<long?> FindCariKeyByUnvanAsync(int firmaKodu, int donemKodu, string cariUnvan);
        Task<long?> FindCariAddressKeyAsync(int firmaKodu, int donemKodu, long cariKey);
        Task<(string? kodu, string? unvan)> GetCariInfoByKeyAsync(int firmaKodu, int donemKodu, long cariKey);
        Task<DiaTargetStockResolveResult> ResolveTargetStockAsync(int firmaKodu, int donemKodu, string stokKod, string? sourceAciklama = null, bool preferHizmet = false);
        Task<long?> FindKalemBirimKeyAsync(int firmaKodu, int donemKodu, long? targetKalemTuruKey, long? targetStokKartKey, string? sourceBirimText, bool isHizmetKart = false);
        Task<long?> FindOdemePlaniKeyByCodeAsync(int firmaKodu, int donemKodu, string odemePlaniKodu);
        Task<(string? kodu, string? aciklama, string? ilksatirOdemeTipi, string? ikkKodu, string? ikkAciklama)> GetOdemePlaniInfoByKeyAsync(int firmaKodu, int donemKodu, long odemePlaniKey);
        Task<long?> FindBankaOdemePlaniKeyByCodeAsync(int firmaKodu, int donemKodu, string bankaOdemePlaniKodu);
        Task<(string? kodu, string? bankahesapKodu, long? keyBcsBankahesabi)> GetBankaOdemePlaniInfoByKeyAsync(int firmaKodu, int donemKodu, long bankaOdemePlaniKey);
        Task<long?> FindBankaHesabiKeyByHesapKoduAsync(int firmaKodu, int donemKodu, string hesapKodu);
        Task<long?> FindCariYetkiliKeyByCodeAsync(int firmaKodu, int donemKodu, string cariKartKodu, string yetkiliKodu);
        Task<long?> FindProjeKeyByCodeAsync(int firmaKodu, int donemKodu, string projeKodu);
        Task<long?> FindDovizKeyByCodeAsync(int firmaKodu, int donemKodu, string dovizKodu);
        /// <summary>scf_kalemturu_listele — RAW <c>targetKeyKalemTuru</c> için toplu kod listesi.</summary>
        Task<IReadOnlyList<LookupKeyCodeItem>> GetKalemTuruLookupListAsync(int firmaKodu, int donemKodu);
        /// <summary>sis/scf birim listeleri — RAW <c>targetKeyKalemBirim</c> için toplu eşleme (tenant’a göre servis seçilir).</summary>
        Task<IReadOnlyList<LookupKeyCodeItem>> GetBirimLookupListAsync(int firmaKodu, int donemKodu);
        Task<List<DiaErpIntegration.API.Models.DiaV3Json.DiaAuthorizedCurrencyItem>> GetCurrenciesAsync(int firmaKodu, int donemKodu);
        Task<string?> FindDovizKuruByDateAsync(int firmaKodu, int donemKodu, long sisDovizKey, string tarih);
        Task<long?> FindInvoiceOdemePlaniKeyFromDetailAsync(int firmaKodu, int donemKodu, long invoiceKey);

        Task<long?> FindSatisElemaniKeyByCodeAsync(int firmaKodu, int donemKodu, string satisElemaniKodu);

        Task<Dictionary<long, string>> ResolveSubeNamesByKeysAsync(int firmaKodu, int donemKodu, IEnumerable<long> keys);
        Task<Dictionary<long, string>> ResolveDepoNamesByKeysAsync(int firmaKodu, int donemKodu, IEnumerable<long> keys);

        Task<Dictionary<long, (string kodu, string aciklama)>> ResolveStokHizmetByFiyatKartKeysAsync(int firmaKodu, int donemKodu, IEnumerable<long> fiyatKartKeys);
        Task<Dictionary<long, (string kodu, string adi)>> ResolveUnitByKeysAsync(int firmaKodu, int donemKodu, IEnumerable<long> unitKeys);

        // RPR özel rapor sonucu (base64 -> json -> __rows)
        Task<List<JsonElement>> GetRprReportRowsAsync(
            int firmaKodu,
            int donemKodu,
            string reportCode,
            Dictionary<string, object?> param,
            CancellationToken cancellationToken = default);
    }
}

