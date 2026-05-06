using DiaErpIntegration.API.Models;
using DiaErpIntegration.API.Models.Api;
using System.Text.Json;
using DiaErpIntegration.API.Models.DiaV3Json;

namespace DiaErpIntegration.API.Services
{
public class MockDiaWsClient : IDiaWsClient
    {
        private readonly MockDataRepository _repo;
        private readonly Dictionary<long, string> _invoiceKeyToRepoKey = new();
        private readonly Dictionary<string, long> _repoKeyToInvoiceKey = new();
        private readonly Dictionary<long, (long invoiceKey, string repoInvoiceKey)> _lineKeyToRepo = new();

        public MockDiaWsClient(MockDataRepository repo)
        {
            _repo = repo;

            // Stable-ish numeric keys for UI flows that expect DIA numeric _key.
            long nextInv = 410000;
            foreach (var inv in _repo.Invoices)
            {
                if (string.IsNullOrWhiteSpace(inv.Key)) continue;
                var k = nextInv++;
                _repoKeyToInvoiceKey[inv.Key] = k;
                _invoiceKeyToRepoKey[k] = inv.Key;
            }

            long nextLine = 510000;
            foreach (var kv in _repo.InvoiceLines)
            {
                var repoInvKey = kv.Key;
                if (!_repoKeyToInvoiceKey.TryGetValue(repoInvKey, out var invKey))
                    continue;
                foreach (var line in kv.Value)
                {
                    if (string.IsNullOrWhiteSpace(line.Key)) continue;
                    var lk = nextLine++;
                    _lineKeyToRepo[lk] = (invKey, repoInvKey);
                }
            }
        }

        public Task<string> LoginAsync() => Task.FromResult("mock_session_id");

        public Task LogoutAsync() => Task.CompletedTask;

        public Task<Dictionary<long, string>> ResolveSubeNamesByKeysAsync(int firmaKodu, int donemKodu, IEnumerable<long> keys)
            => Task.FromResult(new Dictionary<long, string>());

        public Task<Dictionary<long, string>> ResolveDepoNamesByKeysAsync(int firmaKodu, int donemKodu, IEnumerable<long> keys)
            => Task.FromResult(new Dictionary<long, string>());

        public Task<Dictionary<long, (string kodu, string aciklama)>> ResolveStokHizmetByFiyatKartKeysAsync(int firmaKodu, int donemKodu, IEnumerable<long> fiyatKartKeys)
            => Task.FromResult(new Dictionary<long, (string kodu, string aciklama)>());

        public Task<Dictionary<long, (string kodu, string adi)>> ResolveUnitByKeysAsync(int firmaKodu, int donemKodu, IEnumerable<long> unitKeys)
            => Task.FromResult(new Dictionary<long, (string kodu, string adi)>());

        public Task<List<DiaErpIntegration.API.Models.DiaV3Json.DiaAuthorizedCompanyPeriodBranchItem>> GetAuthorizedCompanyPeriodBranchAsync()
        {
            // Map repo firmalar (001/002/003) -> int firmakodu (1/2/3)
            int MapFirma(string? kodu)
            {
                kodu = (kodu ?? "").Trim();
                return kodu switch
                {
                    "001" => 1,
                    "002" => 2,
                    "003" => 3,
                    _ => int.TryParse(kodu, out var n) ? n : 0
                };
            }

            long MapSubeKey(string key) => Math.Abs(key.GetHashCode()) + 1000;
            long MapDepoKey(string key) => Math.Abs(key.GetHashCode()) + 5000;

            var companies = new List<DiaAuthorizedCompanyPeriodBranchItem>();
            foreach (var f in _repo.Firmalar)
            {
                var firmaKodu = MapFirma(f.FirmaKodu);
                if (firmaKodu <= 0) continue;

                var periods = _repo.Donemler
                    .Where(d => string.Equals(d.FirmaKey, f.FirmaKey, StringComparison.OrdinalIgnoreCase))
                    .Select(d => new DiaAuthorizedPeriodItem
                    {
                        Key = Math.Abs((d.Key ?? "").GetHashCode()) + 2000,
                        DonemKodu = int.TryParse(d.DonemKodu, out var dk) ? dk : 0,
                        GorunenDonemKodu = d.GorunenKod ?? "",
                        BaslangicTarihi = d.Baslangic.ToString("yyyy-MM-dd"),
                        BitisTarihi = d.Bitis.ToString("yyyy-MM-dd"),
                        Ontanimli = d.Ontanimli ? "t" : "f"
                    })
                    .Where(p => p.DonemKodu > 0)
                    .OrderByDescending(p => p.DonemKodu)
                    .ToList();

                // Pool firması için dönem yoksa en azından 2025 ver.
                if (firmaKodu == 1 && periods.Count == 0)
                {
                    periods.Add(new DiaAuthorizedPeriodItem
                    {
                        Key = 2335,
                        DonemKodu = 2025,
                        GorunenDonemKodu = "2025",
                        BaslangicTarihi = "2025-01-01",
                        BitisTarihi = "2025-12-31",
                        Ontanimli = "t"
                    });
                }

                var branches = _repo.Subeler
                    .Where(s => string.Equals(s.FirmaKey, f.FirmaKey, StringComparison.OrdinalIgnoreCase))
                    .Select(s => new DiaAuthorizedBranchItem
                    {
                        Key = MapSubeKey(s.Key ?? $"{firmaKodu}-{s.SubeKodu}"),
                        SubeAdi = s.SubeAdi ?? "",
                        Depolar = new List<DiaAuthorizedDepotItem>
                        {
                            new() { Key = MapDepoKey($"{s.Key}-depo-1"), DepoAdi = "MERKEZ" }
                        }
                    })
                    .ToList();

                companies.Add(new DiaAuthorizedCompanyPeriodBranchItem
                {
                    FirmaKodu = firmaKodu,
                    FirmaAdi = f.FirmaAdi ?? $"Firma {firmaKodu}",
                    Donemler = periods,
                    Subeler = branches,
                    Dovizler = new List<DiaAuthorizedCurrencyItem>()
                });
            }

            return Task.FromResult(companies);
        }

        public async Task<List<DiaAuthorizedPeriodItem>> GetPeriodsByFirmaAsync(int firmaKodu)
        {
            var ctx = await GetAuthorizedCompanyPeriodBranchAsync();
            return ctx.FirstOrDefault(c => c.FirmaKodu == firmaKodu)?.Donemler ?? new List<DiaAuthorizedPeriodItem>();
        }

        public async Task<List<(int FirmaKodu, string FirmaAdi)>> GetAllCompaniesAsync()
        {
            var ctx = await GetAuthorizedCompanyPeriodBranchAsync();
            return ctx
                .Select(c => (c.FirmaKodu, c.FirmaAdi))
                .OrderBy(x => x.FirmaKodu)
                .ToList();
        }

        public Task<string?> ResolveDynamicBranchColumnAsync(int firmaKodu, int donemKodu)
        {
            // Mock'ta sabit fallback
            return Task.FromResult<string?>("__dinamik__1");
        }

        public Task<List<DiaErpIntegration.API.Models.DiaV3Json.DiaInvoiceListItem>> GetInvoicesAsync(int firmaKodu, int donemKodu, string filters, int limit, int offset)
        {
            // Mock'ta filters parsing yapmıyoruz; sadece slice dönüyoruz.
            var slice = _repo.Invoices
                .OrderByDescending(i => i.Tarih)
                .Skip(Math.Max(0, offset))
                .Take(Math.Max(0, limit))
                .ToList();

            var list = new List<DiaInvoiceListItem>();
            foreach (var inv in slice)
            {
                if (string.IsNullOrWhiteSpace(inv.Key)) continue;
                if (!_repoKeyToInvoiceKey.TryGetValue(inv.Key, out var key))
                    continue;

                list.Add(new DiaInvoiceListItem
                {
                    Key = key,
                    FisNo = inv.FisNo,
                    BelgeNo = inv.BelgeNo,
                    BelgeNo2 = null,
                    Tarih = inv.Tarih.ToString("yyyy-MM-dd"),
                    Turu = null,
                    TuruAck = inv.TuruTxt,
                    TuruKisa = null,
                    CariKartKodu = inv.CariKartKodu,
                    CariUnvan = inv.CariUnvan,
                    SourceSubeAdi = inv.SourceSubeAdi,
                    SourceDepoAdi = inv.SourceDepoAdi,
                    DestSubeAdi = null,
                    DestDepoAdi = null,
                    FirmaAdi = inv.FirmaAdi,
                    DovizTuru = inv.DovizAdi,
                    Toplam = inv.Toplam,
                    ToplamKdv = inv.ToplamKdv,
                    Net = inv.Net,
                    IptalRaw = JsonDocument.Parse(inv.Iptal ? "true" : "false").RootElement,
                    OdemePlani = null,
                    OdemePlaniAck = null,
                    ProjeKodu = null,
                    ProjeAciklama = null
                });
            }

            return Task.FromResult(list);
        }

        public Task<HashSet<long>?> GetDistributableInvoiceKeysAsync(int firmaKodu, int donemKodu, string filters)
        {
            // Mock tarafta dağıtılabilir anahtarları hızlı hesaplayan ayrı kaynak yok.
            // Controller, null döndüğünde mevcut detail-scan fallback'ine düşer.
            return Task.FromResult<HashSet<long>?>(null);
        }

        public Task<HashSet<long>?> GetInvoiceKeysByUstIslemTuruAsync(int firmaKodu, int donemKodu, string filters, long ustIslemTuruKey)
            => Task.FromResult<HashSet<long>?>(null);

        public Task<Dinamik2ScanResult> ScanInvoiceKeysWithSubelerDinamik2Async(int firmaKodu, int donemKodu, string filters, CancellationToken cancellationToken = default)
            => Task.FromResult(new Dinamik2ScanResult());

        public Task<DiaErpIntegration.API.Models.DiaV3Json.DiaInvoiceDetail> GetInvoiceAsync(int firmaKodu, int donemKodu, long key)
        {
            if (!_invoiceKeyToRepoKey.TryGetValue(key, out var repoKey))
                return Task.FromResult(new DiaInvoiceDetail { Key = key, Lines = new List<DiaInvoiceLine>() });

            var inv = _repo.Invoices.FirstOrDefault(i => i.Key == repoKey);
            var lines = new List<DiaInvoiceLine>();
            if (_repo.InvoiceLines.TryGetValue(repoKey, out var repoLines))
            {
                long nextLineKey = key * 10;
                foreach (var rl in repoLines)
                {
                    var lineKey = nextLineKey++;
                    lines.Add(new DiaInvoiceLine
                    {
                        Key = lineKey,
                        SiraNo = rl.SiraNo,
                        KalemTuruRaw = "MLZM",
                        Miktar = rl.Miktar,
                        BirimFiyati = rl.BirimFiyati,
                        SonBirimFiyati = rl.SonBirimFiyati,
                        Tutari = rl.Tutari,
                        Kdv = rl.Kdv,
                        KdvTutari = rl.KdvTutari,
                        IndirimToplam = rl.IndirimToplam,
                        Note = rl.Note,
                        Note2 = rl.Note2,
                        KalemRef = new DiaLineStokHizmetRef { Key = 1, StokKartKodu = rl.StokHizmetKodu, Aciklama = rl.StokHizmetAciklama },
                        BirimRaw = JsonDocument.Parse($"\"{(rl.BirimKodu ?? "ADET")}\"").RootElement,
                        DepoSource = new DiaDepotRef { DepoAdi = rl.DepoAdi ?? "MERKEZ" },
                        ProjeRaw = default,
                        Dinamik1Raw = default,
                        KeyScfIrsaliyeRaw = default,
                        KeyScfIrsaliyeKalemiRaw = default
                    });
                }
            }

            return Task.FromResult(new DiaInvoiceDetail
            {
                Key = key,
                FisNo = inv?.FisNo,
                Tarih = inv?.Tarih.ToString("yyyy-MM-dd"),
                Saat = inv?.Saat,
                Turu = null,
                Aciklama1 = inv?.TuruTxt,
                BelgeNo = inv?.BelgeNo,
                BelgeNo2 = null,
                CariKartKodu = inv?.CariKartKodu,
                CariUnvan = inv?.CariUnvan,
                DovizKuru = (inv?.DovizKuru ?? 1m).ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture),
                RaporlamaDovizKuru = "1.000000",
                Lines = lines
            });
        }

        public Task<DiaErpIntegration.API.Models.DiaV3Json.DiaInvoiceAddResponse> CreateInvoiceAsync(
            int firmaKodu,
            int donemKodu,
            DiaErpIntegration.API.Models.DiaV3Json.DiaInvoiceAddCardInput card)
            => Task.FromResult(new DiaErpIntegration.API.Models.DiaV3Json.DiaInvoiceAddResponse
            {
                Code = 200,
                Message = "Mock transfer created",
                Key = "0"
            });

        public Task<DiaErpIntegration.API.Models.DiaV3Json.DiaCariHesapFisiAddResponse> CreateVirmanAsync(
            int firmaKodu,
            int donemKodu,
            DiaErpIntegration.API.Models.DiaV3Json.DiaCariHesapFisiCardInput card)
            => Task.FromResult(new DiaErpIntegration.API.Models.DiaV3Json.DiaCariHesapFisiAddResponse
            {
                Code = 200,
                Message = "Mock virman created",
                Key = "0"
            });

        public Task<JsonElement> GetVirmanAsync(int firmaKodu, int donemKodu, long key)
            => Task.FromResult(default(JsonElement));

        public Task<long?> FindCariKeyByCodeAsync(int firmaKodu, int donemKodu, string cariKartKodu) => Task.FromResult<long?>(1);
        public Task<long?> FindCariKeyByUnvanAsync(int firmaKodu, int donemKodu, string cariUnvan) => Task.FromResult<long?>(1);
        public Task<long?> FindCariAddressKeyAsync(int firmaKodu, int donemKodu, long cariKey) => Task.FromResult<long?>(1);
        public Task<(string? kodu, string? unvan)> GetCariInfoByKeyAsync(int firmaKodu, int donemKodu, long cariKey)
            => Task.FromResult<(string? kodu, string? unvan)>(("MOCK", "MOCK UNVAN"));
        public Task<DiaTargetStockResolveResult> ResolveTargetStockAsync(int firmaKodu, int donemKodu, string stokKod, string? sourceAciklama = null, bool preferHizmet = false)
            => Task.FromResult(new DiaTargetStockResolveResult
            {
                StokKodu = stokKod,
                TargetKalemTuruKey = 1,
                TargetStokKartKey = 1,
                IsHizmetKart = preferHizmet,
                ServiceUsed = "mock",
                EndpointUsed = "mock",
                RowCount = 1
            });

        public Task<long?> FindKalemBirimKeyAsync(int firmaKodu, int donemKodu, long? targetKalemTuruKey, long? targetStokKartKey, string? sourceBirimText, bool isHizmetKart = false)
            => Task.FromResult<long?>(1);
        public Task<long?> FindOdemePlaniKeyByCodeAsync(int firmaKodu, int donemKodu, string odemePlaniKodu) => Task.FromResult<long?>(1);
        public Task<(string? kodu, string? aciklama, string? ilksatirOdemeTipi, string? ikkKodu, string? ikkAciklama)> GetOdemePlaniInfoByKeyAsync(int firmaKodu, int donemKodu, long odemePlaniKey)
            => Task.FromResult<(string?, string?, string?, string?, string?)>((odemePlaniKey.ToString(), null, null, null, null));
        public Task<long?> FindBankaOdemePlaniKeyByCodeAsync(int firmaKodu, int donemKodu, string bankaOdemePlaniKodu) => Task.FromResult<long?>(1);
        public Task<(string? kodu, string? bankahesapKodu, long? keyBcsBankahesabi)> GetBankaOdemePlaniInfoByKeyAsync(int firmaKodu, int donemKodu, long bankaOdemePlaniKey)
            => Task.FromResult<(string?, string?, long?)>((bankaOdemePlaniKey.ToString(), "00000001", 1));
        public Task<long?> FindBankaHesabiKeyByHesapKoduAsync(int firmaKodu, int donemKodu, string hesapKodu) => Task.FromResult<long?>(1);
        public Task<long?> FindProjeKeyByCodeAsync(int firmaKodu, int donemKodu, string projeKodu) => Task.FromResult<long?>(1);
        public Task<long?> FindDovizKeyByCodeAsync(int firmaKodu, int donemKodu, string dovizKodu) => Task.FromResult<long?>(1);

        public Task<IReadOnlyList<LookupKeyCodeItem>> GetKalemTuruLookupListAsync(int firmaKodu, int donemKodu)
            => Task.FromResult<IReadOnlyList<LookupKeyCodeItem>>(new List<LookupKeyCodeItem>
            {
                new() { Key = 1, Kod = "MLZM" },
                new() { Key = 2, Kod = "HZMT" },
                new() { Key = 1, Kod = "Malzeme" },
                new() { Key = 2, Kod = "Hizmet" },
            });

        public Task<IReadOnlyList<LookupKeyCodeItem>> GetBirimLookupListAsync(int firmaKodu, int donemKodu)
            => Task.FromResult<IReadOnlyList<LookupKeyCodeItem>>(new List<LookupKeyCodeItem>
            {
                new() { Key = 100, Kod = "ADET" },
                new() { Key = 101, Kod = "KG" },
                new() { Key = 102, Kod = "MT" },
            });

        public Task<DiaInvoiceDetail> GetInvoiceAsyncWithDonemFallback(int firmaKodu, int preferredDonemKodu, long key)
            => GetInvoiceAsync(firmaKodu, preferredDonemKodu, key);

        public Task<DiaInvoiceDetail> GetInvoiceAsyncWithLimitedDonemFallback(int firmaKodu, int preferredDonemKodu, long key, int maxPeriodAttempts = 3)
            => GetInvoiceAsync(firmaKodu, preferredDonemKodu, key);

        public Task<List<JsonElement>> GetInvoiceLinesViewAsync(int firmaKodu, int donemKodu, long invoiceKey)
        {
            if (!_invoiceKeyToRepoKey.TryGetValue(invoiceKey, out var repoInvKey))
                return Task.FromResult(new List<JsonElement>());

            if (!_repo.InvoiceLines.TryGetValue(repoInvKey, out var lines))
                return Task.FromResult(new List<JsonElement>());

            var rows = new List<JsonElement>();
            foreach (var l in lines)
            {
                var obj = new Dictionary<string, object?>
                {
                    ["_key"] = long.TryParse(l.Key, out var lk) ? lk : Math.Abs((l.Key ?? "").GetHashCode()) + 500000,
                    ["sirano"] = l.SiraNo,
                    ["stokhizmetkodu"] = l.StokHizmetKodu,
                    ["stokhizmetaciklama"] = l.StokHizmetAciklama,
                    ["birimkodu"] = l.BirimKodu,
                    ["miktar"] = l.Miktar,
                    ["birimfiyati"] = l.BirimFiyati,
                    ["tutari"] = l.Tutari,
                    ["depoadi"] = l.DepoAdi,
                    ["note"] = l.Note,
                    ["note2"] = l.Note2,
                };
                rows.Add(JsonSerializer.SerializeToElement(obj));
            }

            return Task.FromResult(rows);
        }

        public Task<(string? cariKodu, string? cariUnvan, long? cariKey)> GetInvoiceCariFromListAsync(int firmaKodu, int donemKodu, long invoiceKey)
            => Task.FromResult<(string? cariKodu, string? cariUnvan, long? cariKey)>((null, null, null));

        public async Task<List<DiaAuthorizedBranchItem>> GetSubelerDepolarForFirmaAsync(int firmaKodu, int donemKodu)
        {
            var ctx = await GetAuthorizedCompanyPeriodBranchAsync();
            return ctx.FirstOrDefault(c => c.FirmaKodu == firmaKodu)?.Subeler ?? new List<DiaAuthorizedBranchItem>();
        }

        public Task<List<DiaErpIntegration.API.Models.DiaV3Json.DiaAuthorizedCurrencyItem>> GetCurrenciesAsync(int firmaKodu, int donemKodu)
            => Task.FromResult(new List<DiaErpIntegration.API.Models.DiaV3Json.DiaAuthorizedCurrencyItem>
            {
                new() { Key = 2326, Kodu = "", Adi = "TL", UzunAdi = "Türk Lirası", AnaDovizMiRaw = JsonSerializer.SerializeToElement("t"), RaporlamaDovizMiRaw = JsonSerializer.SerializeToElement("t") },
                new() { Key = 2328, Kodu = "", Adi = "USD", UzunAdi = "ABD Doları", AnaDovizMiRaw = JsonSerializer.SerializeToElement("f"), RaporlamaDovizMiRaw = JsonSerializer.SerializeToElement("f") },
                new() { Key = 2330, Kodu = "", Adi = "EUR", UzunAdi = "Euro", AnaDovizMiRaw = JsonSerializer.SerializeToElement("f"), RaporlamaDovizMiRaw = JsonSerializer.SerializeToElement("f") },
            });

        public Task<string?> FindDovizKuruByDateAsync(int firmaKodu, int donemKodu, long sisDovizKey, string tarih)
            => Task.FromResult<string?>("1.0000000000");

        public Task<long?> FindInvoiceOdemePlaniKeyFromDetailAsync(int firmaKodu, int donemKodu, long invoiceKey)
            => Task.FromResult<long?>(null);

        public Task<long?> FindSatisElemaniKeyByCodeAsync(int firmaKodu, int donemKodu, string satisElemaniKodu)
            => Task.FromResult<long?>(null);

        public Task<long?> FindCariYetkiliKeyByCodeAsync(int firmaKodu, int donemKodu, string cariKartKodu, string yetkiliKodu)
            => Task.FromResult<long?>(null);

        public Task<List<JsonElement>> GetRprReportRowsAsync(
            int firmaKodu,
            int donemKodu,
            string reportCode,
            Dictionary<string, object?> param,
            CancellationToken cancellationToken = default)
        {
            // Mock: filtreleri sadece echo amaçlı taşır, UI'ı geliştirme sırasında bloklamaz.
            var now = DateTimeOffset.UtcNow.ToString("O");
            var rows = new List<JsonElement>
            {
                JsonSerializer.SerializeToElement(new Dictionary<string, object?>
                {
                    ["fatura_key"] = 192122,
                    ["cari_adi"] = "MOCK CARI A",
                    ["tutar"] = 122.00m,
                    ["kdv"] = 0.00m,
                    ["indirimtoplam"] = 0.00m,
                    ["tarih"] = "2025-08-01",
                    ["report_code"] = reportCode,
                    ["generated_at_utc"] = now,
                }),
                JsonSerializer.SerializeToElement(new Dictionary<string, object?>
                {
                    ["fatura_key"] = 241897,
                    ["cari_adi"] = "MOCK CARI B",
                    ["tutar"] = 24000.00m,
                    ["kdv"] = 114.91m,
                    ["indirimtoplam"] = 0.00m,
                    ["tarih"] = "2025-10-17",
                    ["report_code"] = reportCode,
                    ["generated_at_utc"] = now,
                }),
                JsonSerializer.SerializeToElement(new Dictionary<string, object?>
                {
                    ["_debug"] = true,
                    ["firma_kodu"] = firmaKodu,
                    ["donem_kodu"] = donemKodu,
                    ["param"] = param,
                }),
            };

            return Task.FromResult(rows);
        }
    }
}
