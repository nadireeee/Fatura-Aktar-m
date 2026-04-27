using System.Net.Http.Json;
using System.Text.Json;
using DiaErpIntegration.API.Models;
using DiaErpIntegration.API.Models.DiaV3Json;
using DiaErpIntegration.API.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DiaErpIntegration.API.Services
{
    public class DiaWsClient : IDiaWsClient
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<DiaWsClient> _logger;
        private readonly DiaSessionManager _session;
        private readonly DiaOptions _opt;
        private readonly SemaphoreSlim _authCacheGate = new(1, 1);
        private List<DiaAuthorizedCompanyPeriodBranchItem> _authorizedCache = new();
        private DateTimeOffset _authorizedCacheAt = DateTimeOffset.MinValue;
        private readonly SemaphoreSlim _dynamicColumnGate = new(1, 1);
        private readonly Dictionary<string, (string column, DateTimeOffset cachedAt)> _dynamicColumnCache = new();

        public DiaWsClient(HttpClient httpClient, IOptions<DiaOptions> opt, DiaSessionManager session, ILogger<DiaWsClient> logger)
        {
            _httpClient = httpClient;
            _logger = logger;
            _session = session;
            _opt = opt.Value;
            
            var baseUrl = _opt.BaseUrl;
            if (string.IsNullOrWhiteSpace(baseUrl))
                throw new InvalidOperationException("DiaSettings:BaseUrl must be set in Real mode.");

            if (!baseUrl.EndsWith("/")) baseUrl += "/";
            _httpClient.BaseAddress = new Uri(baseUrl);
        }

        public async Task<string> LoginAsync()
        {
            return await _session.GetSessionIdAsync();
        }

        public async Task LogoutAsync()
        {
            // Bu uygulama login ekranının yerine geçmediği için logout zorunlu değil.
            await _session.InvalidateAsync();
        }

        private async Task<TResponse> PostAsync<TRequest, TResponse>(string url, TRequest request) where TResponse : DiaWsResponseBase
        {
            var attempt = 0;
            const int maxAttempts = 4;
            while (attempt < maxAttempts)
            {
                attempt++;
                HttpResponseMessage? response = null;
                try
                {
                    response = await _httpClient.PostAsJsonAsync(url, request);
                if (!response.IsSuccessStatusCode)
                {
                        var code = (int)response.StatusCode;
                        var body = await response.Content.ReadAsStringAsync();

                        // DİA tarafında zaman zaman 500/502 dönebiliyor; kısa retry uygula.
                        var isTransient = code is 429 or 500 or 502 or 503 or 504;
                        if (isTransient && attempt < maxAttempts)
                        {
                            _logger.LogWarning("DIA HTTP transient error. attempt={Attempt}/{Max} status={Status} url={Url} bodyExcerpt={Body}",
                                attempt, maxAttempts, response.StatusCode, url, body.Length > 400 ? body[..400] + "…" : body);
                            await _session.InvalidateAsync();
                            var sid = await _session.GetSessionIdAsync();
                            UpdateSessionIdOnRequest(request, sid);
                            await Task.Delay(250 * attempt);
                            continue;
                        }

                        throw new Exception($"DIA HTTP Error: {response.StatusCode} at {url} body={(body.Length > 400 ? body[..400] + "…" : body)}");
                }

                var result = await response.Content.ReadFromJsonAsync<TResponse>();
                if (result != null)
                {
                    if (result.Message == "INVALID_SESSION" || result.Code == 401)
                    {
                            if (attempt < maxAttempts)
                            {
                                _logger.LogWarning("DIA Session Invalid. Retrying login... attempt={Attempt}/{Max}", attempt, maxAttempts);
                        await _session.InvalidateAsync();
                        var sid = await _session.GetSessionIdAsync();
                        UpdateSessionIdOnRequest(request, sid);
                                await Task.Delay(200 * attempt);
                        continue;
                            }
                    }
                    return result;
                }

                throw new Exception("DIA Response is null");
            }
                finally
                {
                    response?.Dispose();
                }
            }
            throw new Exception("DIA API call failed after retries.");
        }

        private static void UpdateSessionIdOnRequest<TRequest>(TRequest request, string sessionId)
        {
            if (request is DiaWsRequestBase reqBase)
            {
                reqBase.SessionId = sessionId;
                return;
            }

            var payloadProp = request?.GetType().GetProperty("Payload");
            if (payloadProp == null) return;
            var payload = payloadProp.GetValue(request);
            if (payload is DiaWsRequestBase payloadBase)
            {
                payloadBase.SessionId = sessionId;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Okuma tarafı — WS Tester’da doğrulanan servisler
        // - sis_yetkili_firma_donem_sube_depo  -> POST /sis/json
        // - scf_fatura_listele                 -> POST /scf/json
        // - scf_fatura_getir                   -> POST /scf/json
        // ─────────────────────────────────────────────────────────────────────

        public async Task<List<DiaAuthorizedCompanyPeriodBranchItem>> GetAuthorizedCompanyPeriodBranchAsync()
        {
            await _authCacheGate.WaitAsync();
            try
            {
                for (var attempt = 1; attempt <= 3; attempt++)
                {
                    try
                    {
                        var sid = await _session.GetSessionIdAsync();
                        var req = new DiaSisYetkiliFirmaDonemSubeDepoRequest
                        {
                            Payload = new DiaWsRequestBase { SessionId = sid }
                        };

                        var res = await PostAsync<DiaSisYetkiliFirmaDonemSubeDepoRequest, DiaWsResponse<List<DiaAuthorizedCompanyPeriodBranchItem>>>("sis/json", req);
                        var current = res.Result ?? new List<DiaAuthorizedCompanyPeriodBranchItem>();
                        if (current.Count > 0)
                        {
                            _authorizedCache = current;
                            _authorizedCacheAt = DateTimeOffset.UtcNow;
                        }
                        return current;
                    }
                    catch (Exception ex) when (attempt < 3)
                    {
                        _logger.LogWarning(ex, "Authorized context fetch attempt {Attempt}/3 failed.", attempt);
                        await _session.InvalidateAsync();
                        await Task.Delay(250 * attempt);
                    }
                }

                if (_authorizedCache.Count > 0)
                {
                    _logger.LogWarning("Using cached authorized context due to temporary DIA failure. CacheAgeSeconds={Age}",
                        (DateTimeOffset.UtcNow - _authorizedCacheAt).TotalSeconds);
                    return _authorizedCache;
                }

                return new List<DiaAuthorizedCompanyPeriodBranchItem>();
            }
            finally
            {
                _authCacheGate.Release();
            }
        }

        public async Task<List<DiaAuthorizedPeriodItem>> GetPeriodsByFirmaAsync(int firmaKodu)
        {
            // Dokümantasyona göre dönem/şube/depo bilgisi için ana kaynak:
            // - sis_yetkili_firma_donem_sube_depo (yetkili ağaç)
            // - sis_firma_getir (firmaya ait alt modeller)
            // Bazı tenantlarda sis_donem_* liste servisleri bulunmadığı için bunlara güvenmiyoruz.

            try
            {
                var ctx = await GetAuthorizedCompanyPeriodBranchAsync();
                var company = ctx.FirstOrDefault(c => c.FirmaKodu == firmaKodu);
                if (company != null)
                {
                    var periods = (company.Donemler.Count > 0
                            ? company.Donemler
                            : (company.DonemFallback.Count > 0 ? company.DonemFallback : company.DonemListFallback))
                        .Where(p => p.DonemKodu > 0)
                        .GroupBy(p => p.DonemKodu)
                        .Select(g => g.First())
                        .OrderByDescending(p => p.DonemKodu)
                        .ToList();
                    if (periods.Count > 0) return periods;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "GetAuthorizedCompanyPeriodBranchAsync failed in GetPeriodsByFirmaAsync. firma={Firma}", firmaKodu);
            }

            try
            {
                var enrich = await GetFirmaGetirEnrichmentAsync(firmaKodu);
                if (enrich.Periods.Count > 0)
                {
                    _logger.LogInformation("Periods from sis_firma_getir: firma={Firma} count={Count}", firmaKodu, enrich.Periods.Count);
                    return enrich.Periods;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "GetPeriodsByFirmaAsync via sis_firma_getir failed. firma={Firma}", firmaKodu);
            }

            return new List<DiaAuthorizedPeriodItem>();
        }

        private async Task<DiaFirmaGetirEnrichment> GetFirmaGetirEnrichmentAsync(int firmaKodu)
        {
            var sid = await _session.GetSessionIdAsync();
            var req = new DiaSisFirmaGetirRequest
            {
                Payload = new DiaWsRequestBase
                {
                    SessionId = sid,
                    FirmaKodu = firmaKodu,
                    DonemKodu = _opt.DefaultSourceDonemKodu > 0 ? _opt.DefaultSourceDonemKodu : 1
                }
            };

            // Tenant farkı:
            // - result: [{...}, {...}] (firma listesi)
            // - result: {...} (tek firma)
            // Ayrıca dönem/şube listeleri donemler/subeler veya m_donemler/m_subeler altında gelebilir.
            var raw = await PostAsync<DiaSisFirmaGetirRequest, DiaWsResponse<JsonElement>>("sis/json", req);
            DiaSisFirmaGetirCompany? r = null;
            if (raw.Result.ValueKind == JsonValueKind.Array)
            {
                var list = JsonSerializer.Deserialize<List<DiaSisFirmaGetirCompany>>(raw.Result.GetRawText()) ?? new List<DiaSisFirmaGetirCompany>();
                r = list.FirstOrDefault(x => x.FirmaKodu == firmaKodu);
            }
            else if (raw.Result.ValueKind == JsonValueKind.Object)
            {
                r = JsonSerializer.Deserialize<DiaSisFirmaGetirCompany>(raw.Result.GetRawText());
                if (r != null && r.FirmaKodu != firmaKodu) r = null;
            }

            if (r == null) return new DiaFirmaGetirEnrichment();

            var periods = new List<DiaAuthorizedPeriodItem>();
            if (r.Donemler.Count > 0 || r.DonemFallback.Count > 0 || r.DonemListFallback.Count > 0)
            {
                periods =
                    (r.Donemler.Count > 0 ? r.Donemler : (r.DonemFallback.Count > 0 ? r.DonemFallback : r.DonemListFallback))
                    .Where(p => p.DonemKodu > 0)
                    .GroupBy(p => p.DonemKodu)
                    .Select(g => g.First())
                    .OrderByDescending(p => p.DonemKodu)
                    .ToList();
            }
            else if (r.MDonemler.Count > 0)
            {
                periods = r.MDonemler
                    .Where(d => d.DonemKodu > 0)
                    .Select(d => new DiaAuthorizedPeriodItem
                    {
                        Key = d.Key > 0 ? d.Key : d.DonemKodu,
                        DonemKodu = d.DonemKodu,
                        GorunenDonemKodu = d.GorunenKod ?? d.DonemKodu.ToString(),
                        BaslangicTarihi = d.Baslangic,
                        BitisTarihi = d.Bitis,
                        Ontanimli = string.Equals(d.Ontanimli, "t", StringComparison.OrdinalIgnoreCase) ? "t" : "f"
                    })
                    .GroupBy(p => p.DonemKodu)
                    .Select(g => g.First())
                    .OrderByDescending(p => p.DonemKodu)
                    .ToList();
            }

            var subeler = new List<DiaAuthorizedBranchItem>();
            if (r.Subeler.Count > 0)
            {
                subeler = (r.Subeler ?? new List<DiaAuthorizedBranchItem>())
                    .Where(s => s.Key > 0 && !string.IsNullOrWhiteSpace(s.SubeAdi))
                    .Select(s =>
                    {
                        s.Depolar = (s.Depolar ?? new List<DiaAuthorizedDepotItem>())
                            .Where(d => d.Key > 0 && !string.IsNullOrWhiteSpace(d.DepoAdi))
                            .ToList();
                        return s;
                    })
                    .Where(s => s.Depolar.Count > 0)
                    .GroupBy(s => s.Key)
                    .Select(g => g.First())
                    .OrderBy(s => s.SubeAdi, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            else if (r.MSubeler.Count > 0)
            {
                // m_subeler sadece şube verir; depoları mevcut resolver ile tamamlarız.
                List<DiaAuthorizedCompanyPeriodBranchItem>? auth = null;
                try { auth = await GetAuthorizedCompanyPeriodBranchAsync(); } catch { /* ignore */ }
                var authCompany = auth?.FirstOrDefault(x => x.FirmaKodu == firmaKodu);

                foreach (var s in r.MSubeler.Where(x => x.Key > 0 && !string.IsNullOrWhiteSpace(x.SubeAdi)))
                {
                    // Tenant farkı: sis_depo_* bazı sistemlerde yok (404). Önce authorized tree'den dene.
                    var depolar = authCompany?.Subeler?.FirstOrDefault(x => x.Key == s.Key)?.Depolar
                        ?.Where(d => d.Key > 0 && !string.IsNullOrWhiteSpace(d.DepoAdi))
                        .ToList()
                        ?? new List<DiaAuthorizedDepotItem>();

                    // Eğer authorized'da da yoksa, şubeyi yine de listele (depolar boş olabilir).
                    subeler.Add(new DiaAuthorizedBranchItem { Key = s.Key, SubeAdi = s.SubeAdi ?? string.Empty, Depolar = depolar });
                }
                subeler = subeler
                    .Where(s => s.Key > 0 && !string.IsNullOrWhiteSpace(s.SubeAdi))
                    .GroupBy(x => x.Key)
                    .Select(g => g.First())
                    .OrderBy(x => x.SubeAdi, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            return new DiaFirmaGetirEnrichment
            {
                Periods = periods,
                Subeler = subeler
            };
        }

        public async Task<List<(int FirmaKodu, string FirmaAdi)>> GetAllCompaniesAsync()
        {
            try
            {
                var donem = _opt.DefaultSourceDonemKodu > 0 ? _opt.DefaultSourceDonemKodu : 1;
                var firmaCandidates = new HashSet<int>();
                if (_opt.PoolFirmaKodu > 0) firmaCandidates.Add(_opt.PoolFirmaKodu);
                if (_opt.DefaultSourceFirmaKodu > 0) firmaCandidates.Add(_opt.DefaultSourceFirmaKodu);
                var auth = await GetAuthorizedCompanyPeriodBranchAsync();
                foreach (var c in auth.Where(x => x.FirmaKodu > 0))
                    firmaCandidates.Add(c.FirmaKodu);
                if (firmaCandidates.Count == 0) firmaCandidates.Add(1);

                var mergedRows = new List<JsonElement>();
                foreach (var fk in firmaCandidates)
                {
                    var rows = await QueryListByCandidatesAsync(
                        new[]
                        {
                            ("sis/json", "sis_firma_listele"),
                            ("sis/json", "sis_firmalar_listele")
                        },
                        fk,
                        donem,
                        string.Empty,
                        1000,
                        throwOnAllFail: false);
                    if (rows.Count > 0) mergedRows.AddRange(rows);
                }

                var result = new List<(int FirmaKodu, string FirmaAdi)>();
                foreach (var r in mergedRows)
                {
                    var kodText = GetString(r, "firmakodu", "firma_kodu", "kodu", "gorunenkod");
                    var kodNum = GetLong(r, "firmakodu", "firma_kodu", "kodu");
                    int firmaKodu = 0;
                    if (kodNum.HasValue && kodNum.Value > 0)
                        firmaKodu = (int)kodNum.Value;
                    else if (!string.IsNullOrWhiteSpace(kodText) && int.TryParse(kodText.Trim(), out var parsed))
                        firmaKodu = parsed;
                    if (firmaKodu <= 0) continue;

                    var firmaAdi = GetString(r, "firmaadi", "firma_adi", "adi", "unvan")?.Trim() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(firmaAdi)) firmaAdi = $"Firma {firmaKodu}";
                    result.Add((firmaKodu, firmaAdi));
                }

                // Son fallback: en azından yetkili context'i mutlaka ekle.
                foreach (var c in auth.Where(x => x.FirmaKodu > 0))
                    result.Add((c.FirmaKodu, c.FirmaAdi));

                return result
                    .GroupBy(x => x.FirmaKodu)
                    .Select(g => g.First())
                    .OrderBy(x => x.FirmaKodu)
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "GetAllCompaniesAsync failed.");
                return new List<(int FirmaKodu, string FirmaAdi)>();
            }
        }

        public async Task<string?> ResolveDynamicBranchColumnAsync(int firmaKodu, int donemKodu)
        {
            if (!string.IsNullOrWhiteSpace(_opt.BranchDynamicColumnOverride))
            {
                var overrideColumn = _opt.BranchDynamicColumnOverride.Trim();
                _logger.LogInformation("Resolved dynamic branch column from config override: firma={Firma} donem={Donem} column={Column}",
                    firmaKodu, donemKodu, overrideColumn);
                return overrideColumn;
            }

            var cacheKey = $"{firmaKodu}:{donemKodu}";
            await _dynamicColumnGate.WaitAsync();
            try
            {
                if (_dynamicColumnCache.TryGetValue(cacheKey, out var hit)
                    && (DateTimeOffset.UtcNow - hit.cachedAt).TotalMinutes < 30)
                {
                    return hit.column;
                }
            }
            finally
            {
                _dynamicColumnGate.Release();
            }

            string? resolved = null;
            try
            {
                var candidates = new[]
                {
                    ("sis/json", "sis_dinamik_alan_listele"),
                };

                var rows = await QueryListByCandidatesAsync(candidates, firmaKodu, donemKodu, string.Empty, 500, throwOnAllFail: false);
                foreach (var r in rows)
                {
                    var adi = NormalizeText(GetString(r, "adi", "alanadi", "ad"));
                    var turu = NormalizeText(GetString(r, "turu", "tur", "tip", "tipi"));
                    var kolon = GetString(r, "kolonadi", "kolon_adi", "kolon", "column")?.Trim();
                    if (string.IsNullOrWhiteSpace(kolon)) continue;

                    var isSubeler = adi.Contains("SUBELER");
                    var isFaturaKalemi = turu.Contains("FATURA KALEMI") || turu.Contains("FATURA KALEM");
                    if (isSubeler && isFaturaKalemi)
                    {
                        resolved = kolon;
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Dynamic branch metadata resolve failed. firma={Firma} donem={Donem}", firmaKodu, donemKodu);
            }

            if (string.IsNullOrWhiteSpace(resolved))
            {
                // Son fallback: TEST FIRMA görüntüsüne göre en yaygın kolonlar.
                resolved = "__dinamik__2";
            }

            await _dynamicColumnGate.WaitAsync();
            try
            {
                _dynamicColumnCache[cacheKey] = (resolved, DateTimeOffset.UtcNow);
            }
            finally
            {
                _dynamicColumnGate.Release();
            }

            _logger.LogInformation("Resolved dynamic branch column: firma={Firma} donem={Donem} column={Column}", firmaKodu, donemKodu, resolved);
            return resolved;
        }

        private async Task<List<DiaInvoiceListItem>> GetInvoicesRawAsync(int firmaKodu, int donemKodu, string filters, int limit, int offset)
        {
            var sid = await _session.GetSessionIdAsync();
            var req = new DiaScfFaturaListeleRequest
            {
                Payload = new DiaInvoiceListInput
                {
                    SessionId = sid,
                    FirmaKodu = firmaKodu,
                    DonemKodu = donemKodu,
                    Filters = filters ?? string.Empty,
                    Sorts = "_key desc",
                    Params = string.Empty,
                    Limit = limit,
                    Offset = offset,
                }
            };

            _logger.LogInformation("DIA scf_fatura_listele payload: firma_kodu={Firma} donem_kodu={Donem} filters={Filters} limit={Limit} offset={Offset}",
                firmaKodu, donemKodu, req.Payload.Filters, limit, offset);

            var res = await PostAsync<DiaScfFaturaListeleRequest, DiaWsResponse<List<DiaInvoiceListItem>>>("scf/json", req);
            var list = res.Result ?? new List<DiaInvoiceListItem>();
            _logger.LogInformation("DIA scf_fatura_listele result count={Count}", list.Count);
            return list;
        }

        public async Task<List<DiaInvoiceListItem>> GetInvoicesAsync(int firmaKodu, int donemKodu, string filters, int limit, int offset)
        {
            var list = await GetInvoicesRawAsync(firmaKodu, donemKodu, filters, limit, offset);

            // Bazı tenantlarda `filters` parametresi göz ardı edilebiliyor.
            // Özellikle `[_key]=...` filtreleri ignored olursa yanlış fatura satırı okunup
            // ödeme planı / döviz gibi alanlar hatalı map edilebiliyor.
            // Bu yüzden `_key` hedeflenmiş isteklerde listedeki doğru satırı ararız;
            // yoksa sayfalı tarama yapıp doğru satırı bulana kadar devam ederiz.
            var requestedKey = TryExtractSingleKeyFilter(filters);
            if (requestedKey is > 0)
            {
                var hit = list.FirstOrDefault(x => x.Key == requestedKey.Value);
                if (hit != null) return new List<DiaInvoiceListItem> { hit };

                // Filter muhtemelen ignored: sayfalı tarayalım (uygun bir üst sınırla).
                var scanOffset = offset + list.Count;
                // Çok agresif tarama aktarımı kilitler; sadece kısa bir pencere tara.
                var maxScan = 200; // limit=1 iken max 200 istek
                while (scanOffset < maxScan && list.Count == limit)
                {
                    // CRITICAL: burada recursion kullanmayız; sadece raw sayfa okuruz.
                    var nextPage = await GetInvoicesRawAsync(firmaKodu, donemKodu, filters ?? string.Empty, limit, scanOffset);
                    var nextHit = nextPage.FirstOrDefault(x => x.Key == requestedKey.Value);
                    if (nextHit != null) return new List<DiaInvoiceListItem> { nextHit };
                    if (nextPage.Count < limit) break;
                    scanOffset += nextPage.Count;
                }

                // Bulamadıysak yine de ilk listeyi döndür (mevcut davranışla uyumlu fallback).
            }
            return list;
        }

        private static long? TryExtractSingleKeyFilter(string? filters)
        {
            if (string.IsNullOrWhiteSpace(filters)) return null;
            // Beklenen format örn:
            // - "[_key]=12345"
            // - "[_key] = 12345"
            // - "[_key]  =   12345  AND ..."
            // Basit ve güvenli parse; başka filtreleri de içerebilir.
            var s = filters;
            var idx = s.IndexOf("[_key]", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;
            idx += "[_key]".Length;

            // whitespace
            while (idx < s.Length && char.IsWhiteSpace(s[idx])) idx++;
            if (idx >= s.Length || s[idx] != '=') return null;
            idx++; // '='
            while (idx < s.Length && char.IsWhiteSpace(s[idx])) idx++;

            var end = idx;
            while (end < s.Length && char.IsDigit(s[end])) end++;
            if (end == idx) return null;
            if (long.TryParse(s.Substring(idx, end - idx), out var key) && key > 0) return key;
            return null;
        }

        public async Task<HashSet<long>?> GetDistributableInvoiceKeysAsync(int firmaKodu, int donemKodu, string filters)
        {
            try
            {
                var dynamicColumn = await ResolveDynamicBranchColumnAsync(firmaKodu, donemKodu);
                var fullFilter = filters ?? string.Empty;

                var result = new HashSet<long>();
                var rowsWithAnyDynamicProp = 0;
                var offset = 0;
                const int batch = 500;

                while (true)
                {
                    var rows = await QueryListWithOffsetByCandidatesAsync(
                        new[] { ("scf/json", "scf_fatura_listele_ayrintili") },
                        firmaKodu, donemKodu, fullFilter, batch, offset, throwOnAllFail: false);

                    if (rows.Count == 0) break;

                    foreach (var r in rows)
                    {
                        // "Kolon hiç yok" durumunu ayırt etmek için presence say.
                        if (RowHasAnyDynamicProp(r, dynamicColumn)) rowsWithAnyDynamicProp++;

                        var rawDyn = ExtractDynamicFromDetailRow(r, dynamicColumn);
                        if (string.IsNullOrWhiteSpace(rawDyn)) continue;

                        // Satır bazlı ayrıntı listelerinde `faturakalemkey` kalem anahtarıdır; fatura anahtarı sanılmasın.
                        var invoiceKey =
                            GetLong(r, "_key_scf_fatura", "_key_fatura", "faturakey", "fatura_key", "_key_scf_fatura_fisi")
                            ?? GetLong(r, "_key_scf_fatura_karti");
                        if (invoiceKey.HasValue && invoiceKey.Value > 0)
                            result.Add(invoiceKey.Value);
                    }

                    if (rows.Count < batch) break;
                    offset += rows.Count;
                    if (offset >= 20000) break;
                }

                _logger.LogInformation("Fast distributable key scan: firma={Firma} donem={Donem} filters={Filters} dynamicColumn={Column} keyCount={Count}",
                    firmaKodu, donemKodu, filters, dynamicColumn, result.Count);

                // Eğer taranan satırlarda dinamik kolonlar hiç yoksa, bu tenant bu listede dinamik alan taşımıyor demektir.
                // Bu durumda "empty set" güvenilir değil; fallback detail-scan daha doğru.
                if (rowsWithAnyDynamicProp == 0)
                {
                    _logger.LogWarning("Fast distributable key scan found NO dynamic props in detail rows. Falling back to detail-scan. firma={Firma} donem={Donem}", firmaKodu, donemKodu);
                    return null;
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Fast distributable key scan failed. Fallback to detail-scan will be used.");
                return null;
            }
        }

        public async Task<HashSet<long>?> GetInvoiceKeysByUstIslemTuruAsync(int firmaKodu, int donemKodu, string filters, long ustIslemTuruKey)
        {
            if (ustIslemTuruKey <= 0) return new HashSet<long>();
            try
            {
                var fullFilter = filters ?? string.Empty;
                var result = new HashSet<long>();
                var offset = 0;
                const int batch = 500;

                while (true)
                {
                    var rows = await QueryListWithOffsetByCandidatesAsync(
                        new[] { ("scf/json", "scf_fatura_listele_ayrintili") },
                        firmaKodu, donemKodu, fullFilter, batch, offset, throwOnAllFail: false);

                    if (rows.Count == 0) break;

                    foreach (var r in rows)
                    {
                        var ustKey = GetLong(r, "_key_sis_ust_islem_turu", "ustislemturukey");
                        if (!ustKey.HasValue || ustKey.Value != ustIslemTuruKey) continue;

                        var invoiceKey =
                            GetLong(r, "_key_scf_fatura", "_key_fatura", "faturakey", "fatura_key", "_key_scf_fatura_fisi")
                            ?? GetLong(r, "_key_scf_fatura_karti");
                        if (invoiceKey.HasValue && invoiceKey.Value > 0)
                            result.Add(invoiceKey.Value);
                    }

                    if (rows.Count < batch) break;
                    offset += rows.Count;
                    if (offset >= 20000) break;
                }

                _logger.LogInformation("UstIslemTuru key scan: firma={Firma} donem={Donem} ustKey={UstKey} filters={Filters} keyCount={Count}",
                    firmaKodu, donemKodu, ustIslemTuruKey, filters, result.Count);
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "UstIslemTuru key scan failed. Returning null.");
                return null;
            }
        }

        private static bool RowHasAnyDynamicProp(JsonElement row, string? preferredColumn)
        {
            if (row.ValueKind != JsonValueKind.Object) return false;
            if (!string.IsNullOrWhiteSpace(preferredColumn) && row.TryGetProperty(preferredColumn, out _)) return true;
            return row.TryGetProperty("__dinamik__2", out _)
                   || row.TryGetProperty("__dinamik__1", out _)
                   || row.TryGetProperty("__dinamik__00002", out _)
                   || row.TryGetProperty("__dinamik__00001", out _);
        }

        public async Task<Dinamik2ScanResult> ScanInvoiceKeysWithSubelerDinamik2Async(int firmaKodu, int donemKodu, string filters, CancellationToken cancellationToken = default)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var keys = await GetDistributableInvoiceKeysAsync(firmaKodu, donemKodu, filters) ?? new HashSet<long>();
            sw.Stop();
            return new Dinamik2ScanResult
            {
                Keys = keys,
                BatchesFetched = 0,
                RowsScannedApprox = keys.Count,
                CapReached = false,
                ElapsedMs = sw.Elapsed.TotalMilliseconds
            };
        }

        public async Task<DiaInvoiceDetail> GetInvoiceAsync(int firmaKodu, int donemKodu, long key)
        {
            var sid = await _session.GetSessionIdAsync();
            var req = new DiaScfFaturaGetirRequest
            {
                Payload = new DiaInvoiceGetInput
            { 
                SessionId = sid, 
                FirmaKodu = firmaKodu, 
                DonemKodu = donemKodu,
                    Key = key
                }
            };

            _logger.LogInformation("DIA scf_fatura_getir payload: firma_kodu={Firma} donem_kodu={Donem} key={Key}", firmaKodu, donemKodu, key);
            var res = await PostAsync<DiaScfFaturaGetirRequest, DiaWsResponse<DiaInvoiceDetail>>("scf/json", req);
            if (res.Result == null)
            {
                // Tenant/anahtar uyumsuzluğu veya geçici DİA hatası: listeleme/scan akışını patlatmayalım.
                _logger.LogWarning("DIA scf_fatura_getir returned null result. firma={Firma} donem={Donem} key={Key}", firmaKodu, donemKodu, key);
                return new DiaInvoiceDetail { Key = key, Lines = new List<DiaInvoiceLine>() };
            }

            var result = res.Result;
            _logger.LogInformation("DIA scf_fatura_getir result: key={Key} m_kalemler_count={Count}", key, result.Lines?.Count ?? 0);
            return result;
        }

        public async Task<DiaInvoiceDetail> GetInvoiceAsyncWithDonemFallback(int firmaKodu, int preferredDonemKodu, long key)
        {
            var order = new List<int>();
            if (preferredDonemKodu > 0) order.Add(preferredDonemKodu);
            try
            {
                // Önce yetkili ağaçtaki dönemleri dene (en güvenilir kaynak).
                var ctx = await GetAuthorizedCompanyPeriodBranchAsync();
                var company = ctx.FirstOrDefault(c => c.FirmaKodu == firmaKodu);
                if (company != null)
                {
                    foreach (var p in (company.Donemler ?? new List<DiaAuthorizedPeriodItem>()))
                        if (p.DonemKodu > 0 && !order.Contains(p.DonemKodu)) order.Add(p.DonemKodu);
                    foreach (var p in (company.DonemFallback ?? new List<DiaAuthorizedPeriodItem>()))
                        if (p.DonemKodu > 0 && !order.Contains(p.DonemKodu)) order.Add(p.DonemKodu);
                    foreach (var p in (company.DonemListFallback ?? new List<DiaAuthorizedPeriodItem>()))
                        if (p.DonemKodu > 0 && !order.Contains(p.DonemKodu)) order.Add(p.DonemKodu);
                }

                var periods = await GetPeriodsByFirmaAsync(firmaKodu);
                foreach (var p in periods)
                {
                    if (p.DonemKodu > 0 && !order.Contains(p.DonemKodu))
                        order.Add(p.DonemKodu);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "GetPeriodsByFirmaAsync failed for invoice fallback firma={Firma}", firmaKodu);
            }

            if (order.Count == 0) order.Add(preferredDonemKodu > 0 ? preferredDonemKodu : 1);

            Exception? last = null;
            foreach (var d in order.Distinct())
            {
                if (d <= 0) continue;
                try
                {
                    return await GetInvoiceAsync(firmaKodu, d, key);
                }
                catch (Exception ex)
                {
                    last = ex;
                    _logger.LogWarning(ex, "scf_fatura_getir failed (try next donem): firma={Firma} donem={Donem} key={Key}", firmaKodu, d, key);
                }
            }

            _logger.LogWarning(last, "GetInvoiceAsyncWithDonemFallback exhausted. Returning empty detail. firma={Firma} preferredDonem={Donem} key={Key}",
                firmaKodu, preferredDonemKodu, key);
            return new DiaInvoiceDetail { Key = key, Lines = new List<DiaInvoiceLine>() };
        }

        public async Task<List<JsonElement>> GetInvoiceLinesViewAsync(int firmaKodu, int donemKodu, long invoiceKey)
        {
            // Some tenants return null on scf_fatura_getir; use line list view as fallback.
            var candidates = new[]
            {
                ("scf/json", "scf_fatura_kalemi_liste_view"),
                ("scf/json", "scf_fatura_kalemi_listele_view"),
                ("scf/json", "scf_fatura_kalemi_listele"),
            };

            var filters = new[]
            {
                $"[_key_scf_fatura] = {invoiceKey}",
                $"[faturakey] = {invoiceKey}",
                $"[fatura_key] = {invoiceKey}",
            };

            foreach (var f in filters)
            {
                var rows = await QueryListByCandidatesAsync(candidates, firmaKodu, donemKodu, f, 500, throwOnAllFail: false);
                if (rows.Count > 0) return rows;
            }

            return new List<JsonElement>();
        }

        public async Task<(string? cariKodu, string? cariUnvan, long? cariKey)> GetInvoiceCariFromListAsync(int firmaKodu, int donemKodu, long invoiceKey)
        {
            try
            {
                var filters = $"[_key] = {invoiceKey}";
                _logger.LogInformation("GetInvoiceCariFromListAsync: firma={Firma} donem={Donem} filters={Filters}", firmaKodu, donemKodu, filters);
                var rows = await QueryListAsync("scf/json", "scf_fatura_listele", firmaKodu, donemKodu, filters, 1);
                var r = rows.FirstOrDefault();
                if (r.ValueKind == JsonValueKind.Undefined) return (null, null, null);

                var kodu = GetString(r, "carikartkodu", "__carikartkodu", "cari_kodu", "carikodu");
                var unvan = GetString(r, "cariunvan", "__cariunvan", "cari_unvan", "unvan", "adi");
                var key = GetLong(r, "_key_scf_carikart", "key_scf_carikart", "carikey", "cari_key", "_key_carikart");
                if (string.IsNullOrWhiteSpace(kodu) && string.IsNullOrWhiteSpace(unvan) && !(key is > 0) && r.ValueKind == JsonValueKind.Object)
                {
                    var props = r.EnumerateObject().Select(p => p.Name).Take(40).ToList();
                    _logger.LogWarning("GetInvoiceCariFromListAsync: invoiceKey={InvoiceKey} cari not found in row. sampleProps={Props}", invoiceKey, string.Join(",", props));
                }
                _logger.LogInformation("GetInvoiceCariFromListAsync: invoiceKey={InvoiceKey} resolved cariKodu={CariKodu} cariUnvan={CariUnvan} cariKey={CariKey}", invoiceKey, kodu ?? "-", unvan ?? "-", key?.ToString() ?? "-");
                return (kodu, unvan, key);
            }
            catch
            {
                return (null, null, null);
            }
        }

        public async Task<List<DiaAuthorizedBranchItem>> GetSubelerDepolarForFirmaAsync(int firmaKodu, int donemKodu)
        {
            // Dokümantasyon: dönem/şube/depo için sis_yetkili_firma_donem_sube_depo veya sis_firma_getir kullan.
            // Bazı tenantlarda sis_sube_listele/sis_depo_listele yok; bu yüzden liste servislerine bağlanmıyoruz.

            try
            {
                var ctx = await GetAuthorizedCompanyPeriodBranchAsync();
                var company = ctx.FirstOrDefault(c => c.FirmaKodu == firmaKodu);
                if (company != null)
                {
                    var branches = (company.Subeler ?? new List<DiaAuthorizedBranchItem>())
                        .Where(s => s.Key > 0 && !string.IsNullOrWhiteSpace(s.SubeAdi))
                        .Select(s =>
                        {
                            s.Depolar = (s.Depolar ?? new List<DiaAuthorizedDepotItem>())
                                .Where(d => d.Key > 0 && !string.IsNullOrWhiteSpace(d.DepoAdi))
                                .ToList();
                            return s;
                        })
                        .Where(s => s.Depolar.Count > 0)
                        .GroupBy(s => s.Key)
                        .Select(g => g.First())
                        .OrderBy(s => s.SubeAdi, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    if (branches.Count > 0) return branches;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "GetAuthorizedCompanyPeriodBranchAsync failed in GetSubelerDepolarForFirmaAsync. firma={Firma}", firmaKodu);
            }

            try
            {
                var enrich = await GetFirmaGetirEnrichmentAsync(firmaKodu);
                if (enrich.Subeler.Count > 0)
                {
                    _logger.LogInformation("Branches from sis_firma_getir: firma={Firma} count={Count}", firmaKodu, enrich.Subeler.Count);
                    return enrich.Subeler;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "GetSubelerDepolarForFirmaAsync via sis_firma_getir failed. firma={Firma}", firmaKodu);
            }

            return new List<DiaAuthorizedBranchItem>();
        }

        public async Task<DiaInvoiceAddResponse> CreateInvoiceAsync(int firmaKodu, int donemKodu, DiaInvoiceAddCardInput card)
        {
            var sid = await _session.GetSessionIdAsync();
            var req = new DiaScfFaturaEkleRequest
            {
                Payload = new DiaInvoiceAddInput
            {
                SessionId = sid,
                FirmaKodu = firmaKodu,
                DonemKodu = donemKodu,
                    Kart = card
                }
            };

            _logger.LogInformation("DIA scf_fatura_ekle payload: targetFirma={Firma} targetDonem={Donem} kalemCount={Count}",
                firmaKodu, donemKodu, card.Lines.Count);

            var res = await PostAsync<DiaScfFaturaEkleRequest, DiaInvoiceAddResponse>("scf/json", req);
            _logger.LogInformation("DIA scf_fatura_ekle result: key={Key} msg={Msg}", res.Key, res.Message);
            return res;
        }

        public async Task<DiaCariHesapFisiAddResponse> CreateVirmanAsync(int firmaKodu, int donemKodu, DiaCariHesapFisiCardInput card)
        {
            var sid = await _session.GetSessionIdAsync();
            var req = new DiaScfCariHesapFisiEkleRequest
            {
                Payload = new DiaCariHesapFisiAddInput
                {
                    SessionId = sid,
                    FirmaKodu = firmaKodu,
                    DonemKodu = donemKodu,
                    Kart = card
                }
            };

            _logger.LogInformation("DIA scf_carihesap_fisi_ekle payload: targetFirma={Firma} targetDonem={Donem} turu={Turu} kalemCount={Count}",
                firmaKodu, donemKodu, card.Turu, card.Lines.Count);

            var res = await PostAsync<DiaScfCariHesapFisiEkleRequest, DiaCariHesapFisiAddResponse>("scf/json", req);
            _logger.LogInformation("DIA scf_carihesap_fisi_ekle result: key={Key} msg={Msg}", res.Key, res.Message);
            return res;
        }

        public async Task<JsonElement> GetVirmanAsync(int firmaKodu, int donemKodu, long key)
        {
            var sid = await _session.GetSessionIdAsync();
            var req = new DiaScfCariHesapFisiGetirRequest
            {
                Payload = new DiaCariHesapFisiGetInput
                {
                    SessionId = sid,
                    FirmaKodu = firmaKodu,
                    DonemKodu = donemKodu,
                    Key = key
                }
            };

            _logger.LogInformation("DIA scf_carihesap_fisi_getir payload: firma_kodu={Firma} donem_kodu={Donem} key={Key}", firmaKodu, donemKodu, key);
            var res = await PostAsync<DiaScfCariHesapFisiGetirRequest, DiaWsResponse<JsonElement>>("scf/json", req);
            if (res.Result.ValueKind == JsonValueKind.Undefined || res.Result.ValueKind == JsonValueKind.Null)
                throw new InvalidOperationException("scf_carihesap_fisi_getir returned null/empty result.");
            return res.Result;
        }

        public async Task<long?> FindCariKeyByCodeAsync(int firmaKodu, int donemKodu, string cariKartKodu)
        {
            if (string.IsNullOrWhiteSpace(cariKartKodu)) return null;
            var kod = cariKartKodu.Trim();
            // Tenant farkı: cari kod alanı değişebiliyor veya filter ignore olabiliyor.
            // Bu yüzden hem farklı kolon adlarıyla filtre dene, hem de dönen satırlarda kesin eşleşme ara.
            var filterCandidates = new[]
            {
                $"[carikartkodu] = '{Escape(kod)}'",
                $"[kodu] = '{Escape(kod)}'",
                $"[kartkodu] = '{Escape(kod)}'",
                $"[carikodu] = '{Escape(kod)}'",
            };

            List<JsonElement> rows = new();
            foreach (var f in filterCandidates)
            {
                rows = await QueryListAsync("scf/json", "scf_carikart_listele", firmaKodu, donemKodu, f, 200);
                if (rows.Count > 0) break;
            }
            if (rows.Count == 0) return null;

            static string Canon(string? s)
                => MasterCodeNormalizer.Normalize(s) ?? string.Empty;

            var requested = Canon(kod);
            // Bazı tenantlarda filter ignore olabiliyor; bu yüzden kesin eşleşen satırı seç.
            foreach (var r in rows)
            {
                var code = Canon(GetString(r, "carikartkodu", "kodu", "kartkodu", "carikodu"));
                if (!string.IsNullOrWhiteSpace(code) && code == requested)
                    return GetLong(r, "_key", "key");
            }

            _logger.LogWarning(
                "FindCariKeyByCodeAsync: no exact match. targetFirma={Firma} targetDonem={Donem} requested={Requested} rows={RowCount} sampleCodes={Samples}",
                firmaKodu,
                donemKodu,
                kod,
                rows.Count,
                string.Join(", ", rows.Take(8).Select(r => GetString(r, "carikartkodu", "kodu", "kartkodu", "carikodu") ?? "-")));

            // kesin eşleşme yoksa yanlış cari'ye düşmemek için NULL döndür (fallback unvan araması üst katmanda var)
            return null;
        }

        public async Task<long?> FindCariKeyByUnvanAsync(int firmaKodu, int donemKodu, string cariUnvan)
        {
            if (string.IsNullOrWhiteSpace(cariUnvan)) return null;

            // Tam eşitlik tenantlarda çok kırılgan (nokta/boşluk/farklı yazım).
            // Normalize + LIKE ile ara.
            var q = cariUnvan.Trim();
            var q2 = NormalizeCariTitle(q);
            var filters =
                $"([cariunvan] LIKE '%{Escape(q)}%' OR [unvan] LIKE '%{Escape(q)}%' OR [adi] LIKE '%{Escape(q)}%')";

            // Bazı tenantlarda kayıtlar normalize edilmiş şekilde tutuluyor; ikinci arama.
            var filters2 =
                $"([cariunvan] LIKE '%{Escape(q2)}%' OR [unvan] LIKE '%{Escape(q2)}%' OR [adi] LIKE '%{Escape(q2)}%')";

            var rows = await QueryListAsync("scf/json", "scf_carikart_listele", firmaKodu, donemKodu, filters, 10);
            if (rows.Count == 0 && q2 != q)
                rows = await QueryListAsync("scf/json", "scf_carikart_listele", firmaKodu, donemKodu, filters2, 10);

            return rows.Select(r => GetLong(r, "_key", "key")).FirstOrDefault(v => v.HasValue);
        }

        private static string NormalizeCariTitle(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
            return raw.Trim().ToUpperInvariant()
                .Replace("İ", "I")
                .Replace("İ", "I")
                .Replace("Ş", "S")
                .Replace("Ğ", "G")
                .Replace("Ü", "U")
                .Replace("Ö", "O")
                .Replace("Ç", "C");
        }

        public async Task<long?> FindCariAddressKeyAsync(int firmaKodu, int donemKodu, long cariKey)
        {
            var filters = $"[_key_scf_carikart] = {cariKey}";
            var rows = await QueryListAsync("scf/json", "scf_carikart_adresleri_listele", firmaKodu, donemKodu, filters, 5);
            return rows.Select(r => GetLong(r, "_key", "key")).FirstOrDefault(v => v.HasValue);
        }

        public async Task<(string? kodu, string? unvan)> GetCariInfoByKeyAsync(int firmaKodu, int donemKodu, long cariKey)
        {
            var rows = await QueryListAsync("scf/json", "scf_carikart_listele", firmaKodu, donemKodu, $"[_key] = {cariKey}", 1);
            var r = rows.FirstOrDefault();
            if (r.ValueKind == JsonValueKind.Undefined) return (null, null);
            return (GetString(r, "carikartkodu", "kodu"), GetString(r, "cariunvan", "unvan", "adi"));
        }

        public async Task<DiaTargetStockResolveResult> ResolveTargetStockAsync(int firmaKodu, int donemKodu, string stokKod, string? sourceAciklama = null, bool preferHizmet = false)
        {
            if (string.IsNullOrWhiteSpace(stokKod))
                return new DiaTargetStockResolveResult { StokKodu = string.Empty, ServiceUsed = "none", EndpointUsed = "none", RowCount = 0 };
            var kod = stokKod.Trim();

            // Bazı firmalarda stok kodları farklı formatta tutulabiliyor (örn. havuz: STK003, hedef: st003).
            // Önce birebir kodu, sonra deterministik alternatifleri dene.
            static List<string> BuildCodeCandidates(string requested)
            {
                var list = new List<string>();
                if (!string.IsNullOrWhiteSpace(requested)) list.Add(requested.Trim());

                var up = requested.Trim().ToUpperInvariant();
                var low = requested.Trim().ToLowerInvariant();
                if (!string.IsNullOrWhiteSpace(low) && !string.Equals(low, requested.Trim(), StringComparison.Ordinal))
                    list.Add(low);
                if (!string.IsNullOrWhiteSpace(up) && !string.Equals(up, requested.Trim(), StringComparison.OrdinalIgnoreCase))
                    list.Add(up);
                if (up.StartsWith("STK", StringComparison.OrdinalIgnoreCase) && up.Length > 3)
                {
                    var rest = up.Substring(3);
                    // STK003 -> ST003
                    list.Add("ST" + rest);
                    // STK003 -> S003 (son çare)
                    list.Add("S" + rest);

                    // case-sensitive tenantlarda küçük harf kod tutulabiliyor
                    list.Add(("ST" + rest).ToLowerInvariant());
                    list.Add(("S" + rest).ToLowerInvariant());
                }

                // Bazı firmalarda sayı kısmındaki baştaki 0'lar farklı tutulabiliyor (örn. ST018 <-> ST18).
                // Prefix+Digits şeklini yakalayıp 0'suz alternatifi ekle.
                try
                {
                    var m = System.Text.RegularExpressions.Regex.Match(up, @"^([A-Z]+)(0+)(\d+)$");
                    if (m.Success)
                    {
                        var prefix = m.Groups[1].Value;
                        var digits = m.Groups[3].Value;
                        if (!string.IsNullOrWhiteSpace(prefix) && !string.IsNullOrWhiteSpace(digits))
                        {
                            list.Add(prefix + digits);
                            list.Add((prefix + digits).ToLowerInvariant());
                        }
                    }
                }
                catch
                {
                    // ignore regex issues; candidates are best-effort
                }

                // uniq (case-sensitive)
                // Not: Bazı tenantlarda filtreler case-sensitive olabildiği için
                //      "ST053" ve "st053" ikisini de denememiz gerekiyor.
                var uniq = new List<string>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var s in list)
                    if (!string.IsNullOrWhiteSpace(s) && seen.Add(s))
                        uniq.Add(s);
                return uniq;
            }

            var codeCandidates = BuildCodeCandidates(kod);
            _logger.LogInformation("ResolveTargetStockAsync request: firma={Firma} donem={Donem} stokKod={StokKod} preferHizmet={PreferHizmet} candidates={Candidates}",
                firmaKodu, donemKodu, stokKod, preferHizmet, string.Join(",", codeCandidates));

            static string Canon(string? s)
                => string.IsNullOrWhiteSpace(s) ? string.Empty : s.Trim().ToUpperInvariant();

            static long? ChooseStockKeyByCode(List<JsonElement> rows, string requestedCode)
            {
                if (rows.Count == 0) return null;
                var req = Canon(requestedCode);
                static string NormalizeLoose(string? s)
                {
                    if (string.IsNullOrWhiteSpace(s)) return string.Empty;
                    // keep only letters/digits; normalize Turkish chars; ignore case
                    var up = s.Trim().ToUpperInvariant()
                        .Replace("İ", "I")
                        .Replace("İ", "I")
                        .Replace("Ş", "S")
                        .Replace("Ğ", "G")
                        .Replace("Ü", "U")
                        .Replace("Ö", "O")
                        .Replace("Ç", "C");
                    var chars = new List<char>(up.Length);
                    foreach (var ch in up)
                        if (char.IsLetterOrDigit(ch))
                            chars.Add(ch);
                    return new string(chars.ToArray());
                }

                static (string prefix, string digits, string suffix) SplitParts(string normalized)
                {
                    if (string.IsNullOrWhiteSpace(normalized)) return (string.Empty, string.Empty, string.Empty);
                    var i = 0;
                    while (i < normalized.Length && char.IsLetter(normalized[i])) i++;
                    var j = i;
                    while (j < normalized.Length && char.IsDigit(normalized[j])) j++;
                    var prefix = normalized[..i];
                    var digits = normalized[i..j];
                    var suffix = normalized[j..];
                    return (prefix, digits, suffix);
                }

                static string TrimLeadingZeros(string digits)
                {
                    if (string.IsNullOrEmpty(digits)) return string.Empty;
                    var k = 0;
                    while (k < digits.Length && digits[k] == '0') k++;
                    return k >= digits.Length ? "0" : digits[k..];
                }

                var reqLoose = NormalizeLoose(req);
                var reqParts = SplitParts(reqLoose);
                var reqDigitsTz = TrimLeadingZeros(reqParts.digits);
                var reqAllDigits = req.Length > 0 && req.All(char.IsDigit);

                foreach (var r in rows)
                {
                    var code = Canon(GetString(r, "stokkartkodu", "kartkodu", "kodu", "stok_kodu", "stokkodu"));
                    if (!string.IsNullOrWhiteSpace(code) && code == req)
                        return GetLong(r, "_key", "key");
                }

                // Aggressive but deterministic fallback:
                // User expectation: "hedef firmada stok varsa (prefix/0/case farklı olsa bile) otomatik bulunsun, soru sorma".
                // Safety: only pick when we can produce a clear best match by code-core scoring.
                //
                // Extra safety: purely numeric codes (örn. hizmet kartı "00000010") stok kodlarıyla substring/son-rakam
                // eşleşmeleriyle yanlışlıkla eşleşebiliyor. Bu durumda sadece güçlü (>=900) skorları kabul et.
                var bestKey = (long?)null;
                var bestScore = int.MinValue;
                var bestTie = false;

                foreach (var r in rows)
                {
                    var raw = GetString(r, "stokkartkodu", "kartkodu", "kodu", "stok_kodu", "stokkodu");
                    var candLoose = NormalizeLoose(raw);
                    if (string.IsNullOrWhiteSpace(candLoose)) continue;

                    var candParts = SplitParts(candLoose);
                    var candDigitsTz = TrimLeadingZeros(candParts.digits);

                    var score = 0;
                    if (candLoose == reqLoose) score = 1000;
                    else if (!string.IsNullOrEmpty(reqParts.digits) && candParts.digits == reqParts.digits) score = 900;
                    else if (!string.IsNullOrEmpty(reqDigitsTz) && candDigitsTz == reqDigitsTz) score = 850;
                    else if (!string.IsNullOrEmpty(reqDigitsTz) && candLoose.EndsWith(reqDigitsTz, StringComparison.Ordinal)) score = 820;
                    else if (!string.IsNullOrEmpty(reqLoose) && candLoose.Contains(reqLoose, StringComparison.Ordinal)) score = 780;
                    else if (!string.IsNullOrEmpty(reqLoose) && reqLoose.Contains(candLoose, StringComparison.Ordinal)) score = 760;
                    else score = 0;

                    // Prefer same suffix if present (rare but can disambiguate)
                    if (!string.IsNullOrEmpty(reqParts.suffix) && candParts.suffix == reqParts.suffix) score += 30;
                    // Prefer shorter overall delta
                    score -= Math.Min(40, Math.Abs(candLoose.Length - reqLoose.Length));

                    var key = GetLong(r, "_key", "key");
                    if (!key.HasValue) continue;

                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestKey = key;
                        bestTie = false;
                    }
                    else if (score == bestScore && bestKey.HasValue && key.Value != bestKey.Value)
                    {
                        bestTie = true;
                    }
                }

                // Threshold: if we cannot achieve a strong, unique match, return null (let upper layer fail fast).
                var minScore = 820;
                if (reqAllDigits)
                    minScore = 900;

                if (bestKey.HasValue && !bestTie && bestScore >= minScore)
                    return bestKey;

                return null;
            }

            // IMPORTANT:
            // scf_fatura_ekle payload'ındaki "_key_kalemturu" tenant'a göre "scf_kalemturu" ya da (daha sık) "scf_stokkart" key'i ister.
            // Aynı stokkartkodu için stk/json ve scf/json farklı key uzayları döndürebilir; yanlış key seçimi "lot takiplidir" gibi
            // yanıltıcı hatalara sebep olabiliyor. Bu yüzden scf/json her zaman öncelikli.

            long? kalemTuruKey = null;
            long? stokKartKey = null;
            string? matchedCandidate = null;
            string? matchedTargetCode = null;
            string? matchedTargetAciklama = null;
            var endpointUsed = "none";
            var serviceUsed = "none";
            var rowCount = 0;
            var isHizmetKart = false;

            static long? ChooseHizmetKeyByCode(List<JsonElement> rows, string requestedCode)
            {
                if (rows.Count == 0) return null;
                var req = Canon(requestedCode);
                foreach (var r in rows)
                {
                    var code = Canon(GetString(r, "hizmetkartkodu", "kartkodu", "kodu", "hizmetkodu"));
                    if (!string.IsNullOrWhiteSpace(code) && code == req)
                        return GetLong(r, "_key", "key");
                }
                return null;
            }

            async Task<bool> TryResolveHizmetKartAsync()
            {
                foreach (var c in codeCandidates)
                {
                    var filters = new[]
                    {
                        $"[hizmetkartkodu] = '{Escape(c)}'",
                        $"[kartkodu] = '{Escape(c)}'",
                        $"[kodu] = '{Escape(c)}'",
                        $"[hizmetkodu] = '{Escape(c)}'",
                    };
                    foreach (var f in filters)
                    {
                        var rows = await QueryListByCandidatesAsync(
                            new[] { ("scf/json", "scf_hizmetkart_listele") },
                            firmaKodu, donemKodu, f, 50, throwOnAllFail: false);
                        rowCount = Math.Max(rowCount, rows.Count);
                        var chosen = ChooseHizmetKeyByCode(rows, c);
                        if (chosen.HasValue)
                        {
                            stokKartKey = chosen;
                            isHizmetKart = true;
                            if (endpointUsed == "none")
                            {
                                endpointUsed = "scf/json";
                                serviceUsed = "scf_hizmetkart_listele";
                            }
                            matchedCandidate = c;
                            matchedTargetCode = GetString(rows.First(r => GetLong(r, "_key", "key") == chosen), "hizmetkartkodu", "kartkodu", "kodu", "hizmetkodu");
                            matchedTargetAciklama = GetString(rows.First(r => GetLong(r, "_key", "key") == chosen), "aciklama", "adi", "hizmetadi");
                            _logger.LogInformation("ResolveTargetStockAsync hizmetkart hit (scf): requested={Requested} matchedCandidate={Candidate} filter={Filter} targetHizmetKartKey={Key} rowCount={RowCount}",
                                stokKod, c, f, stokKartKey, rows.Count);
                            return true;
                        }
                    }
                }

                return false;
            }

            // Hizmet kalemleri: önce hizmet kartı çöz. Stok fuzzy-match'e hiç girme (yanlış st0101 gibi eşleşmeleri engeller).
            if (preferHizmet)
            {
                await TryResolveHizmetKartAsync();
                if (!stokKartKey.HasValue)
                {
                    return new DiaTargetStockResolveResult
                    {
                        StokKodu = kod,
                        TargetKalemTuruKey = null,
                        TargetStokKartKey = null,
                        IsHizmetKart = false,
                        ServiceUsed = serviceUsed,
                        EndpointUsed = endpointUsed,
                        RowCount = rowCount,
                        MatchedCandidate = matchedCandidate,
                        MatchedTargetCode = matchedTargetCode,
                        MatchedTargetAciklama = matchedTargetAciklama
                    };
                }
            }

            // 1) Try SCF kalemturu master first (try candidates)
            if (!preferHizmet)
            {
                foreach (var c in codeCandidates)
                {
                    var filters = $"[stokkartkodu] = '{Escape(c)}'";
            var kalemRows = await QueryListByCandidatesAsync(
                new[] { ("scf/json", "scf_kalemturu_listele") },
                firmaKodu, donemKodu, filters, 5, throwOnAllFail: false);
                    rowCount = Math.Max(rowCount, kalemRows.Count);
                    kalemTuruKey = kalemRows.Select(r => GetLong(r, "_key", "key")).FirstOrDefault(v => v.HasValue);
            if (kalemTuruKey.HasValue)
            {
                        endpointUsed = "scf/json";
                        serviceUsed = "scf_kalemturu_listele";
                        _logger.LogInformation("ResolveTargetStockAsync kalemturu hit: requested={Requested} matchedCandidate={Candidate} targetKalemTuruKey={Key} rowCount={RowCount}",
                            stokKod, c, kalemTuruKey, kalemRows.Count);
                        break;
                    }
                }
            }
            else
            {
                // HZMT: kalemturu master bazen hizmet kart kodu üzerinden bulunur.
                foreach (var c in codeCandidates)
                {
                    var hizmetKalemFilters = new[]
                    {
                        $"[hizmetkartkodu] = '{Escape(c)}'",
                        $"[kartkodu] = '{Escape(c)}'",
                        $"[kodu] = '{Escape(c)}'",
                        $"[hizmetkodu] = '{Escape(c)}'",
                    };
                    foreach (var f in hizmetKalemFilters)
                    {
                        var kalemRows = await QueryListByCandidatesAsync(
                            new[] { ("scf/json", "scf_kalemturu_listele") },
                            firmaKodu, donemKodu, f, 5, throwOnAllFail: false);
                        rowCount = Math.Max(rowCount, kalemRows.Count);
                        kalemTuruKey = kalemRows.Select(r => GetLong(r, "_key", "key")).FirstOrDefault(v => v.HasValue);
                        if (kalemTuruKey.HasValue)
                        {
                            if (endpointUsed == "none")
                            {
                                endpointUsed = "scf/json";
                                serviceUsed = "scf_kalemturu_listele";
                            }
                            _logger.LogInformation("ResolveTargetStockAsync kalemturu hit (hizmet): requested={Requested} matchedCandidate={Candidate} filter={Filter} targetKalemTuruKey={Key} rowCount={RowCount}",
                                stokKod, c, f, kalemTuruKey, kalemRows.Count);
                            break;
                        }
                    }
                    if (kalemTuruKey.HasValue) break;
                }
            }

            // 2) Resolve stock card key with SCF priority.
            // Not all tenants/services accept the same filter field name; try common variants.
            var scfKey = (long?)null;
            var stockRowsScf = new List<JsonElement>();
            static string[] BuildFieldFilters(string code)
                => new[]
                {
                    $"[stokkartkodu] = '{Escape(code)}'",
                    $"[kartkodu] = '{Escape(code)}'",
                    $"[kodu] = '{Escape(code)}'",
                    $"[stok_kodu] = '{Escape(code)}'",
                    $"[stokkodu] = '{Escape(code)}'",
                };

            if (!preferHizmet)
            {
                foreach (var c in codeCandidates)
                {
                    foreach (var f in BuildFieldFilters(c))
                    {
                        var rows = await QueryListByCandidatesAsync(
                            new[] { ("scf/json", "scf_stokkart_listele") },
                            firmaKodu, donemKodu, f, 50, throwOnAllFail: false);
                        rowCount = Math.Max(rowCount, rows.Count);
                        var chosen = ChooseStockKeyByCode(rows, c);
                        if (!chosen.HasValue && rows.Count >= 50)
                        {
                            // Bazı tenantlarda filters görmezden gelinebiliyor ve her zaman ilk N kaydı döndürüyor.
                            // Bu durumda sayfalamalı arayıp exact kodu bul.
                            const int pageSize = 500;
                            const int maxOffset = 5000; // 0..5000 => 11 sayfa (maks 5500 kayıt tarar)
                            for (var offset = 0; offset <= maxOffset && !chosen.HasValue; offset += pageSize)
                            {
                                var page = await QueryListAsync("scf/json", "scf_stokkart_listele", firmaKodu, donemKodu, f, pageSize, offset);
                                rowCount = Math.Max(rowCount, page.Count);
                                chosen = ChooseStockKeyByCode(page, c);
                                if (chosen.HasValue)
                                {
                                    _logger.LogWarning(
                                        "ResolveTargetStockAsync paging used (scf_stokkart_listele): requested={Requested} candidate={Candidate} filter={Filter} offset={Offset} pageSize={PageSize}",
                                        stokKod, c, f, offset, pageSize);
                                    rows = page;
                                    break;
                                }
                                if (page.Count < pageSize) break; // end
                            }
                        }
                        if (chosen.HasValue)
                        {
                            stockRowsScf = rows;
                            scfKey = chosen;
                            matchedCandidate = c;
                            matchedTargetCode = GetString(rows.First(r => GetLong(r, "_key", "key") == chosen), "stokkartkodu", "kartkodu", "kodu", "stok_kodu", "stokkodu");
                            matchedTargetAciklama = GetString(rows.First(r => GetLong(r, "_key", "key") == chosen), "aciklama", "adi", "urunadi", "stokadi");
                            if (rows.Count > 1)
                                _logger.LogInformation("ResolveTargetStockAsync scf stock filter used: requested={Requested} candidate={Candidate} filter={Filter} matchedKey={Key} rowCount={RowCount}",
                                    stokKod, c, f, chosen, rows.Count);
                            break;
                        }
                    }
                    if (scfKey.HasValue) break;
                }
                if (scfKey.HasValue)
                {
                    stokKartKey = scfKey;
                    if (endpointUsed == "none")
                    {
                        endpointUsed = "scf/json";
                        serviceUsed = "scf_stokkart_listele";
                    }
                    _logger.LogInformation("ResolveTargetStockAsync stokkart hit (scf): stokKod={StokKod} targetStokKartKey={Key} rowCount={RowCount}",
                        stokKod, stokKartKey, stockRowsScf.Count);
                }
                else
                {
                    // stk/json bazı tenantlarda yok; yine de candidates üzerinden dene.
                    foreach (var c in codeCandidates)
                    {
                        var stkFilters = $"[stokkartkodu] = '{Escape(c)}'";
                        var stockRowsStk = await QueryListByCandidatesAsync(
                            new[] { ("stk/json", "stk_stokkart_listele") },
                            firmaKodu, donemKodu, stkFilters, 5, throwOnAllFail: false);
                        rowCount = Math.Max(rowCount, stockRowsStk.Count);
                        var stkKey = ChooseStockKeyByCode(stockRowsStk, c);
                        if (stkKey.HasValue)
                        {
                            stokKartKey = stkKey;
                            if (endpointUsed == "none")
                            {
                                endpointUsed = "stk/json";
                                serviceUsed = "stk_stokkart_listele";
                            }
                            _logger.LogInformation("ResolveTargetStockAsync stokkart hit (stk): requested={Requested} candidate={Candidate} targetStokKartKey={Key} rowCount={RowCount}",
                                stokKod, c, stokKartKey, stockRowsStk.Count);
                            break;
                        }
                    }
                    if (!stokKartKey.HasValue)
                    {
                        foreach (var c in codeCandidates)
                        {
                            var kartFilters = $"[stokkartkodu] = '{Escape(c)}'";
                            var stockRowsKart = await QueryListByCandidatesAsync(
                                new[] { ("stk/json", "stk_kart_listele") },
                                firmaKodu, donemKodu, kartFilters, 5, throwOnAllFail: false);
                            rowCount = Math.Max(rowCount, stockRowsKart.Count);
                            var kartKey = stockRowsKart.Select(r => GetLong(r, "_key", "key")).FirstOrDefault(v => v.HasValue);
                            if (kartKey.HasValue)
                            {
                                stokKartKey = kartKey;
                                if (endpointUsed == "none")
                                {
                                    endpointUsed = "stk/json";
                                    serviceUsed = "stk_kart_listele";
                                }
                                _logger.LogInformation("ResolveTargetStockAsync stokkart hit (kart): requested={Requested} candidate={Candidate} targetStokKartKey={Key} rowCount={RowCount}",
                                    stokKod, c, stokKartKey, stockRowsKart.Count);
                                break;
                            }
                        }
                    }
                }
            }

            // 3) Fallbacks for kalemturu key
            if (!kalemTuruKey.HasValue && stockRowsScf.Count > 0)
            {
                // some tenants expose kalemturu reference on scf_stokkart row
                var fromScfStockRow = stockRowsScf
                    .Select(r => GetLong(r, "_key_scf_kalemturu", "_key_kalemturu"))
                    .FirstOrDefault(v => v.HasValue);
                if (fromScfStockRow.HasValue)
                {
                    kalemTuruKey = fromScfStockRow;
                    _logger.LogInformation("ResolveTargetStockAsync fallback from scf stock row field: stokKod={StokKod} targetKalemTuruKey={Key}", stokKod, fromScfStockRow);
                }
            }

            // 3b) Hizmet kartı fallback:
            // Bazı kalemler HZMT olup stokkartkodu yerine hizmetkartkodu ile gelir. Bu durumda stock lookup boş kalır.
            if (!preferHizmet && !stokKartKey.HasValue)
            {
                await TryResolveHizmetKartAsync();
            }

            if (!kalemTuruKey.HasValue)
            {
                // If still unknown, prefer scf stock card key (most common for MLZM)
                if (scfKey.HasValue)
                {
                    kalemTuruKey = scfKey;
                    _logger.LogWarning("ResolveTargetStockAsync fallback by scf stokkart key: stokKod={StokKod} targetKalemTuruKey={Key} reason=kalemturu-not-found", stokKod, scfKey);
                }
                else if (stokKartKey.HasValue)
                {
                kalemTuruKey = stokKartKey;
                _logger.LogWarning("ResolveTargetStockAsync fallback by stokkart key: stokKod={StokKod} targetKalemTuruKey={Key} reason=kalemturu-not-found", stokKod, stokKartKey);
                }
            }

            return new DiaTargetStockResolveResult
            {
                StokKodu = kod,
                TargetKalemTuruKey = kalemTuruKey,
                TargetStokKartKey = stokKartKey,
                IsHizmetKart = isHizmetKart,
                ServiceUsed = serviceUsed,
                EndpointUsed = endpointUsed,
                RowCount = rowCount,
                MatchedCandidate = matchedCandidate,
                MatchedTargetCode = matchedTargetCode,
                MatchedTargetAciklama = matchedTargetAciklama
            };
        }

        public async Task<long?> FindKalemBirimKeyAsync(int firmaKodu, int donemKodu, long? targetKalemTuruKey, long? targetStokKartKey, string? sourceBirimText, bool isHizmetKart = false)
        {
            var normalized = NormalizeUnit(sourceBirimText);
            _logger.LogInformation("FindKalemBirimKeyAsync request: firma={Firma} donem={Donem} targetKalemTuruKey={KalemTuru} targetStokKartKey={StokKart} sourceBirim={Birim} normalized={Norm}",
                firmaKodu, donemKodu, targetKalemTuruKey, targetStokKartKey, sourceBirimText, normalized);

            // 1) MASTER DATA resolver (service-specific filters)
            var candidates = new List<(string endpoint, string service, string filter)>();
            if (targetKalemTuruKey.HasValue)
                candidates.Add(("scf/json", "scf_kalem_birimleri_listele", $"[_key_scf_kalemturu] = {targetKalemTuruKey.Value}"));
            if (targetStokKartKey.HasValue)
            {
                if (isHizmetKart)
                {
                    // Hizmet kartı kalemlerinde birim listesi bu servisten geliyor.
                    candidates.Add(("scf/json", "scf_hizmetkart_birimleri_listele", $"[_key_scf_hizmetkart] = {targetStokKartKey.Value}"));
                }
                else
                {
                    // Bazı tenantlarda DIA stok kalemi biriminde "scf_stokkart_birimleri" satır _key'ini bekliyor.
                    candidates.Add(("scf/json", "scf_stokkart_birimleri_listele", $"[_key_scf_stokkart] = {targetStokKartKey.Value}"));
                }
                candidates.Add(("stk/json", "stk_kalem_birimleri_listele", $"[_key_stk_stokkart] = {targetStokKartKey.Value}"));
                candidates.Add(("stk/json", "stk_stokkart_birimleri_listele", $"[_key_stk_stokkart] = {targetStokKartKey.Value}"));
            }

            foreach (var c in candidates)
            {
                _logger.LogInformation("FindKalemBirimKeyAsync candidate: endpoint={Endpoint} service={Service} filter={Filter}",
                    c.endpoint, c.service, c.filter);
                var rows = await QueryListByCandidatesAsync(
                    new[] { (c.endpoint, c.service) },
                    firmaKodu, donemKodu, c.filter, 200, throwOnAllFail: false);
                _logger.LogInformation("FindKalemBirimKeyAsync candidate result: service={Service} rowCount={RowCount}",
                    c.service, rows.Count);

                // Hizmet kartı birimlerinde DIA, payload'ta satır _key'ini bekliyor.
                // Diğer listelerde ise bazen _key_sis_stok_birim_* gibi alanlar birim key'i olabiliyor.
                var chosen = c.service == "scf_hizmetkart_birimleri_listele" && isHizmetKart && targetStokKartKey.HasValue
                    ? ChooseHizmetUnitRowKey(rows, normalized, targetStokKartKey.Value)
                    : (c.service == "scf_stokkart_birimleri_listele" && !isHizmetKart && targetStokKartKey.HasValue
                        ? ChooseStokKartUnitRowKey(rows, normalized, targetStokKartKey.Value)
                        : (c.service == "scf_hizmetkart_birimleri_listele"
                            ? ChooseUnitKeyPreferRowKey(rows, normalized)
                            : ChooseUnitKey(rows, normalized)));
                if (chosen.HasValue)
                {
                    _logger.LogInformation("FindKalemBirimKeyAsync chosen from master-data: service={Service} chosenKey={Key}", c.service, chosen);
                    return chosen;
                }
            }

            // 2) stokkart üzerinde varsayılan birim key (only if we have stokKartKey)
            if (targetStokKartKey.HasValue)
            {
                // Bazı tenantlarda filtre görmezden gelinebiliyor; bu yüzden 1 satır yerine
                // biraz daha fazla çekip _key eşleşen satırı seçiyoruz.
                var stockRows = await QueryListByCandidatesAsync(
                    new[] { ("scf/json", "scf_stokkart_listele") },
                    firmaKodu, donemKodu, $"[_key] = {targetStokKartKey.Value}", 50, throwOnAllFail: false);

                var exactRow = stockRows.FirstOrDefault(r =>
                    GetLong(r, "_key", "key") == targetStokKartKey.Value);
                var rowToUse = exactRow.ValueKind == JsonValueKind.Undefined ? stockRows.FirstOrDefault() : exactRow;

                var fromStock = rowToUse.ValueKind == JsonValueKind.Undefined
                    ? null
                    : GetFirstLongFromAnyProperty(rowToUse,
                        "_key_scf_kalem_birimleri",
                        "_key_sis_stok_birim_listesi",
                        "_key_sis_stok_birim",
                        "anabirimkey",
                        "birimkey");
                if (fromStock.HasValue)
                {
                    _logger.LogInformation("FindKalemBirimKeyAsync fallback resolved from stock master: key={Key}", fromStock);
                    return fromStock;
                }
            }

            // 3) Son fallback: ayrıntılı listeden geçmiş satırlar (debug/tenant workaround)
            if (targetKalemTuruKey.HasValue)
            {
                var detailRows = await QueryListByCandidatesAsync(
                    new[] { ("scf/json", "scf_fatura_listele_ayrintili") },
                    firmaKodu, donemKodu, $"[_key_kalemturu] = {targetKalemTuruKey.Value}", 50, throwOnAllFail: false);
                var fromDetail = detailRows
                    .Select(r => GetLong(r, "_key_scf_kalem_birimleri", "fatbirimkey"))
                    .FirstOrDefault(v => v.HasValue);
                if (fromDetail.HasValue)
                {
                    _logger.LogInformation("FindKalemBirimKeyAsync fallback resolved from detay list: key={Key}", fromDetail);
                    return fromDetail;
                }
            }

            return null;
        }

        public async Task<long?> FindOdemePlaniKeyByCodeAsync(int firmaKodu, int donemKodu, string odemePlaniKodu)
        {
            var target = (odemePlaniKodu ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(target)) return null;

            // NOT: Bazı tenantlarda liste servislerinde filter ignore olabiliyor ve
            // "[kodu] = '00000002'" gibi sorgularda yanlış/ilk satır dönebiliyor.
            // Ödeme planı kritik olduğu için burada her zaman güvenli yol:
            // Tüm listeyi çekip uygulama tarafında kod eşleştir.
            static string Norm(string s)
            {
                var t = (s ?? string.Empty).Trim();
                // "00000002" gibi kodlarda boşluk vs. sorunlarını azalt.
                return t;
            }

            static string NormDigits(string s)
            {
                var t = Norm(s);
                if (t.Length == 0) return t;
                for (var i = 0; i < t.Length; i++)
                    if (!char.IsDigit(t[i]))
                        return t;
                var trimmed = t.TrimStart('0');
                return trimmed.Length == 0 ? "0" : trimmed;
            }

            var all = await QueryListAsync("scf/json", "scf_odeme_plani_listele", firmaKodu, donemKodu, string.Empty, 1000);
            var want = Norm(target);
            var wantDigits = NormDigits(target);
            foreach (var r in all)
            {
                var key = GetLong(r, "_key", "key");
                if (!key.HasValue || key.Value <= 0) continue;
                var kodu = Norm(GetString(r, "kodu", "odemeplani", "odemeplani_kodu") ?? string.Empty);
                if (string.Equals(kodu, want, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(NormDigits(kodu), wantDigits, StringComparison.OrdinalIgnoreCase))
                    return key.Value;
            }

            return null;
        }

        public async Task<(string? kodu, string? aciklama, string? ilksatirOdemeTipi, string? ikkKodu, string? ikkAciklama)> GetOdemePlaniInfoByKeyAsync(int firmaKodu, int donemKodu, long odemePlaniKey)
        {
            var filters = $"[_key] = {odemePlaniKey}";
            var rows = await QueryListAsync("scf/json", "scf_odeme_plani_listele", firmaKodu, donemKodu, filters, 1);
            var row = rows.FirstOrDefault();
            if (row.ValueKind == JsonValueKind.Undefined)
                return (null, null, null, null, null);

            return (
                GetString(row, "kodu", "odemeplani", "odemeplani_kodu"),
                GetString(row, "aciklama", "odemeplaniack", "adi"),
                GetString(row, "ilksatirodemetipi"),
                GetString(row, "ikkkodu"),
                GetString(row, "ikkaciklama")
            );
        }

        public async Task<long?> FindBankaOdemePlaniKeyByCodeAsync(int firmaKodu, int donemKodu, string bankaOdemePlaniKodu)
        {
            var target = (bankaOdemePlaniKodu ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(target)) return null;

            // NOT: Bazı tenantlarda liste servislerinde filter ignore olabiliyor ve
            // "[kodu] = '00000002'" gibi sorgularda yanlış/ilk satır dönebiliyor.
            // Banka ödeme planı da ödeme planı kadar kritik; bu yüzden listeyi çekip
            // kodu uygulama tarafında eşleştiriyoruz.
            static string Norm(string s) => (s ?? string.Empty).Trim();
            static string NormDigits(string s)
            {
                var t = Norm(s);
                if (t.Length == 0) return t;
                for (var i = 0; i < t.Length; i++)
                    if (!char.IsDigit(t[i]))
                        return t;
                var trimmed = t.TrimStart('0');
                return trimmed.Length == 0 ? "0" : trimmed;
            }

            var want = Norm(target);
            var wantDigits = NormDigits(target);

            var rows = await QueryListByCandidatesAsync(
                new[]
                {
                    ("bcs/json", "bcs_banka_odeme_plani_listele"),
                    ("bcs/json", "bcs_bankaodemeplani_listele"),
                    ("bcs/json", "bcs_banka_odemeplanilistele"),
                    ("scf/json", "scf_banka_odeme_plani_listele"),
                },
                firmaKodu, donemKodu, string.Empty, 2000, throwOnAllFail: false);

            foreach (var r in rows)
            {
                var key = GetLong(r, "_key", "key");
                if (!key.HasValue || key.Value <= 0) continue;
                var kodu = Norm(GetString(r, "kodu") ?? string.Empty);
                if (string.Equals(kodu, want, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(NormDigits(kodu), wantDigits, StringComparison.OrdinalIgnoreCase))
                    return key.Value;
            }

            // Fallback: yine de filter ile dene (bazı servisler boş filter ile sayfalıyor olabilir)
            var filters = $"[kodu] = '{Escape(want)}'";
            var rows2 = await QueryListByCandidatesAsync(
                new[]
                {
                    ("bcs/json", "bcs_banka_odeme_plani_listele"),
                    ("bcs/json", "bcs_bankaodemeplani_listele"),
                    ("bcs/json", "bcs_banka_odemeplanilistele"),
                    ("scf/json", "scf_banka_odeme_plani_listele"),
                },
                firmaKodu, donemKodu, filters, 50, throwOnAllFail: false);
            return rows2.Select(r => GetLong(r, "_key", "key")).FirstOrDefault(v => v.HasValue);
        }

        public async Task<(string? kodu, string? bankahesapKodu, long? keyBcsBankahesabi)> GetBankaOdemePlaniInfoByKeyAsync(int firmaKodu, int donemKodu, long bankaOdemePlaniKey)
        {
            var filters = $"[_key] = {bankaOdemePlaniKey}";
            var rows = await QueryListByCandidatesAsync(
                new[]
                {
                    ("bcs/json", "bcs_banka_odeme_plani_listele"),
                    ("bcs/json", "bcs_bankaodemeplani_listele"),
                    ("bcs/json", "bcs_banka_odemeplanilistele"),
                    ("scf/json", "scf_banka_odeme_plani_listele"),
                },
                firmaKodu, donemKodu, filters, 1, throwOnAllFail: false);
            var row = rows.FirstOrDefault();
            if (row.ValueKind == JsonValueKind.Undefined)
                return (null, null, null);

            return (
                GetString(row, "kodu"),
                GetString(row, "bankahesapkodu", "hesapkodu"),
                GetLong(row, "_key_bcs_bankahesabi")
            );
        }

        public async Task<long?> FindBankaHesabiKeyByHesapKoduAsync(int firmaKodu, int donemKodu, string hesapKodu)
        {
            var filters = $"[hesapkodu] = '{Escape(hesapKodu)}'";
            var rows = await QueryListByCandidatesAsync(
                new[]
                {
                    ("bcs/json", "bcs_bankahesabi_listele"),
                    ("bcs/json", "bcs_banka_hesabi_listele"),
                    ("scf/json", "scf_bankahesabi_listele"),
                },
                firmaKodu, donemKodu, filters, 5, throwOnAllFail: false);
            return rows.Select(r => GetLong(r, "_key", "key")).FirstOrDefault(v => v.HasValue);
        }

        public async Task<long?> FindProjeKeyByCodeAsync(int firmaKodu, int donemKodu, string projeKodu)
        {
            var filters = $"[kodu] = '{Escape(projeKodu)}'";
            var rows = await QueryListAsync("prj/json", "prj_proje_listele", firmaKodu, donemKodu, filters, 5);
            return rows.Select(r => GetLong(r, "_key", "key")).FirstOrDefault(v => v.HasValue);
        }

        public async Task<long?> FindDovizKeyByCodeAsync(int firmaKodu, int donemKodu, string dovizKodu)
        {
            var rows = await QueryListAsync("sis/json", "sis_doviz_listele", firmaKodu, donemKodu, string.Empty, 500);
            var target = (dovizKodu ?? string.Empty).Trim();
            foreach (var r in rows)
            {
                var key = GetLong(r, "_key", "key");
                if (!key.HasValue) continue;
                var kodu = GetString(r, "kodu");
                var adi = GetString(r, "adi");
                var uzunAdi = GetString(r, "uzunadi");
                if (Eq(target, kodu) || Eq(target, adi) || Eq(target, uzunAdi))
                    return key;
            }
            return null;
        }

        public async Task<List<DiaAuthorizedCurrencyItem>> GetCurrenciesAsync(int firmaKodu, int donemKodu)
        {
            var rows = await QueryListAsync("sis/json", "sis_doviz_listele", firmaKodu, donemKodu, string.Empty, 500);
            return rows
                .Select(r => new DiaAuthorizedCurrencyItem
                {
                    Key = GetLong(r, "_key", "key") ?? 0,
                    Kodu = GetString(r, "kodu"),
                    Adi = GetString(r, "adi"),
                    UzunAdi = GetString(r, "uzunadi"),
                    AnaDovizMiRaw = GetRaw(r, "anadovizmi"),
                    RaporlamaDovizMiRaw = GetRaw(r, "raporlamadovizmi")
                })
                .Where(x => x.Key > 0)
                .ToList();
        }

        public async Task<string?> FindDovizKuruByDateAsync(int firmaKodu, int donemKodu, long sisDovizKey, string tarih)
        {
            if (sisDovizKey <= 0) return null;
            if (string.IsNullOrWhiteSpace(tarih)) return null;

            // sis_doviz_kuru_listele: filtre alanları tenant'a göre string dönüyor; en güvenilir filtreler:
            // - _key_sis_doviz (currency key)
            // - tarih (YYYY-MM-DD)
            var filters = $"[_key_sis_doviz] = {sisDovizKey} AND [tarih] = '{Escape(tarih.Trim())}'";
            var rows = await QueryListByCandidatesAsync(
                new[] { ("sis/json", "sis_doviz_kuru_listele") },
                firmaKodu, donemKodu, filters, 5, throwOnAllFail: false);

            var row = rows.FirstOrDefault();
            if (row.ValueKind == JsonValueKind.Undefined) return null;

            // kur1 genelde efektif kur; bazı sistemlerde kur2/kur4 alım/satım olabilir.
            var kur1 = GetString(row, "kur1", "dovizkuru", "kur");
            if (!string.IsNullOrWhiteSpace(kur1)) return kur1;

            return GetString(row, "kur2", "kur3", "kur4");
        }

        public async Task<long?> FindInvoiceOdemePlaniKeyFromDetailAsync(int firmaKodu, int donemKodu, long invoiceKey)
        {
            if (invoiceKey <= 0) return null;
            try
            {
                // Ayrıntılı listede bazı tenantlarda kalem satırlarında _key_scf_odeme_plani dolu olabiliyor.
                var filters = $"[_key_scf_fatura] = {invoiceKey}";
                var rows = await QueryListByCandidatesAsync(
                    new[] { ("scf/json", "scf_fatura_listele_ayrintili") },
                    firmaKodu, donemKodu, filters, 200, throwOnAllFail: false);

                var key = rows
                    .Select(r => GetLong(r, "_key_scf_odeme_plani", "odemeplanikey", "_key_odeme_plani"))
                    .FirstOrDefault(v => v.HasValue && v.Value > 0);
                return key;
            }
            catch
            {
                return null;
            }
        }

        public async Task<long?> FindSatisElemaniKeyByCodeAsync(int firmaKodu, int donemKodu, string satisElemaniKodu)
        {
            var target = (satisElemaniKodu ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(target)) return null;

            var candidates = new[]
            {
                ("scf/json", "scf_satiselemani_listele"),
                ("scf/json", "scf_satiselemanlari_listele"),
            };

            static string Norm(string? s)
                => (s ?? string.Empty).Trim().ToUpperInvariant().Replace("İ", "I");

            // Filter bazen ignore olabildiği için önce full liste.
            var all = await QueryListByCandidatesAsync(candidates, firmaKodu, donemKodu, string.Empty, 2000, throwOnAllFail: false);
            foreach (var r in all)
            {
                var k = GetLong(r, "_key", "key");
                if (!k.HasValue || k.Value <= 0) continue;
                var kod = GetString(r, "kodu", "kod");
                if (!string.IsNullOrWhiteSpace(kod) && Norm(kod) == Norm(target))
                    return k.Value;
            }

            var filters = $"[kodu] = '{Escape(target)}'";
            var rows = await QueryListByCandidatesAsync(candidates, firmaKodu, donemKodu, filters, 50, throwOnAllFail: false);
            return rows.Select(r => GetLong(r, "_key", "key")).FirstOrDefault(v => v.HasValue && v.Value > 0);
        }

        public async Task<long?> FindCariYetkiliKeyByCodeAsync(int firmaKodu, int donemKodu, string cariKartKodu, string yetkiliKodu)
        {
            var cari = (cariKartKodu ?? string.Empty).Trim();
            var yet = (yetkiliKodu ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(cari) || string.IsNullOrWhiteSpace(yet)) return null;

            // scf_carikart_yetkili_listele çoğu tenantta carikartkodu + kodu döner.
            // Filter bazen ignore olabildiği için önce geniş çekip uygulama içinde eşleştiriyoruz.
            var all = await QueryListByCandidatesAsync(
                new[] { ("scf/json", "scf_carikart_yetkili_listele") },
                firmaKodu, donemKodu, string.Empty, 5000, throwOnAllFail: false);

            static string Norm(string? s)
                => (s ?? string.Empty).Trim().ToUpperInvariant().Replace("İ", "I");

            foreach (var r in all)
            {
                var k = GetLong(r, "_key", "key");
                if (!k.HasValue || k.Value <= 0) continue;
                var rowCari = GetString(r, "carikartkodu", "__carikartkodu", "cari_kodu");
                if (string.IsNullOrWhiteSpace(rowCari)) continue;
                if (Norm(rowCari) != Norm(cari)) continue;
                var rowKod = GetString(r, "kodu", "kod", "yetkikodu", "yetki_kodu");
                if (string.IsNullOrWhiteSpace(rowKod)) continue;
                if (Norm(rowKod) == Norm(yet)) return k.Value;
            }

            var filters = $"[carikartkodu] = '{Escape(cari)}' AND [kodu] = '{Escape(yet)}'";
            var rows = await QueryListByCandidatesAsync(
                new[] { ("scf/json", "scf_carikart_yetkili_listele") },
                firmaKodu, donemKodu, filters, 50, throwOnAllFail: false);

            return rows.Select(r => GetLong(r, "_key", "key")).FirstOrDefault(v => v.HasValue && v.Value > 0);
        }

        private async Task<List<JsonElement>> QueryListAsync(string endpoint, string serviceName, int firmaKodu, int donemKodu, string filters, int limit)
            => await QueryListAsync(endpoint, serviceName, firmaKodu, donemKodu, filters, limit, 0);

        private async Task<List<JsonElement>> QueryListAsync(string endpoint, string serviceName, int firmaKodu, int donemKodu, string filters, int limit, int offset)
        {
            var retryCount = 0;
            while (retryCount < 2)
            {
                var sid = await _session.GetSessionIdAsync();
                var payload = new DiaInvoiceListInput
            {
                SessionId = sid,
                FirmaKodu = firmaKodu,
                DonemKodu = donemKodu,
                    Filters = filters ?? string.Empty,
                    Sorts = "_key desc",
                    Params = string.Empty,
                    Limit = limit,
                    Offset = offset
                };

                var req = new Dictionary<string, object> { [serviceName] = payload };
                var response = await _httpClient.PostAsJsonAsync(endpoint, req);
                if (!response.IsSuccessStatusCode)
                    throw new Exception($"DIA HTTP Error: {response.StatusCode} at {endpoint}/{serviceName}");

                var result = await response.Content.ReadFromJsonAsync<DiaWsResponse<JsonElement>>();
                if (result == null) throw new Exception($"DIA {serviceName} response is null");

                if (result.Message == "INVALID_SESSION" || result.Code == 401)
                {
                    await _session.InvalidateAsync();
                    retryCount++;
                    continue;
                }

                if (result.Result.ValueKind != JsonValueKind.Array) return new List<JsonElement>();
                return result.Result.EnumerateArray().ToList();
            }

            throw new Exception($"DIA {serviceName} failed after session retry.");
        }

        private async Task<List<JsonElement>> QueryListByCandidatesAsync((string endpoint, string service)[] candidates, int firmaKodu, int donemKodu, string filters, int limit, bool throwOnAllFail = true)
        {
            Exception? last = null;
            foreach (var c in candidates)
            {
                try
                {
                    _logger.LogInformation("DIA list candidate try: endpoint={Endpoint} service={Service} filters={Filters}", c.endpoint, c.service, filters);
                    var rows = await QueryListAsync(c.endpoint, c.service, firmaKodu, donemKodu, filters, limit);
                    _logger.LogInformation("DIA list candidate success: endpoint={Endpoint} service={Service} rowCount={RowCount}",
                        c.endpoint, c.service, rows.Count);
                    return rows;
                }
                catch (Exception ex)
                {
                    last = ex;
                    _logger.LogWarning(ex, "DIA list candidate failed: {Endpoint}/{Service}", c.endpoint, c.service);
                }
            }

            if (throwOnAllFail && last != null) throw last;
            return new List<JsonElement>();
        }

        private async Task<List<JsonElement>> QueryListWithOffsetByCandidatesAsync((string endpoint, string service)[] candidates, int firmaKodu, int donemKodu, string filters, int limit, int offset, bool throwOnAllFail = true)
        {
            Exception? last = null;
            foreach (var c in candidates)
            {
                try
                {
                    _logger.LogInformation("DIA list candidate try (offset): endpoint={Endpoint} service={Service} filters={Filters} limit={Limit} offset={Offset}",
                        c.endpoint, c.service, filters, limit, offset);
                    var rows = await QueryListWithOffsetAsync(c.endpoint, c.service, firmaKodu, donemKodu, filters, limit, offset);
                    _logger.LogInformation("DIA list candidate success (offset): endpoint={Endpoint} service={Service} rowCount={RowCount}",
                        c.endpoint, c.service, rows.Count);
                    return rows;
                }
                catch (Exception ex)
                {
                    last = ex;
                    _logger.LogWarning(ex, "DIA list candidate failed (offset): {Endpoint}/{Service}", c.endpoint, c.service);
                }
            }

            if (throwOnAllFail && last != null) throw last;
            return new List<JsonElement>();
        }

        private async Task<List<JsonElement>> QueryListWithOffsetAsync(string endpoint, string serviceName, int firmaKodu, int donemKodu, string filters, int limit, int offset)
        {
            var retryCount = 0;
            while (retryCount < 2)
            {
                var sid = await _session.GetSessionIdAsync();
                var payload = new DiaInvoiceListInput
                {
                    SessionId = sid,
                    FirmaKodu = firmaKodu,
                    DonemKodu = donemKodu,
                    Filters = filters ?? string.Empty,
                    Sorts = "_key desc",
                    Params = string.Empty,
                    Limit = limit,
                    Offset = offset
                };

                var req = new Dictionary<string, object> { [serviceName] = payload };
                var response = await _httpClient.PostAsJsonAsync(endpoint, req);
                if (!response.IsSuccessStatusCode)
                    throw new Exception($"DIA HTTP Error: {response.StatusCode} at {endpoint}/{serviceName}");

                var result = await response.Content.ReadFromJsonAsync<DiaWsResponse<JsonElement>>();
                if (result == null) throw new Exception($"DIA {serviceName} response is null");

                if (result.Message == "INVALID_SESSION" || result.Code == 401)
                {
                    await _session.InvalidateAsync();
                    retryCount++;
                    continue;
                }

                if (result.Result.ValueKind != JsonValueKind.Array) return new List<JsonElement>();
                return result.Result.EnumerateArray().ToList();
            }

            throw new Exception($"DIA {serviceName} failed after session retry.");
        }

        private static long? GetFirstLongFromProperty(JsonElement row, string propertyName)
        {
            if (!row.TryGetProperty(propertyName, out var p)) return null;
            if (p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out var n)) return n;
            if (p.ValueKind == JsonValueKind.String && long.TryParse(p.GetString(), out var s)) return s;
            if (p.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in p.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Number && item.TryGetInt64(out var an)) return an;
                    if (item.ValueKind == JsonValueKind.String && long.TryParse(item.GetString(), out var astr)) return astr;
                    if (item.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var inner in item.EnumerateArray())
                        {
                            if (inner.ValueKind == JsonValueKind.Number && inner.TryGetInt64(out var inn)) return inn;
                            if (inner.ValueKind == JsonValueKind.String && long.TryParse(inner.GetString(), out var ins)) return ins;
                        }
                    }
                }
            }
            return null;
        }

        private static long? GetFirstLongFromAnyProperty(JsonElement row, params string[] propertyNames)
        {
            foreach (var name in propertyNames)
            {
                var v = GetFirstLongFromProperty(row, name);
                if (v.HasValue) return v;
            }
            return null;
        }

        private static long? ChooseUnitKey(List<JsonElement> rows, string? sourceBirimText)
        {
            if (rows.Count == 0) return null;
            var src = NormalizeUnit(sourceBirimText);

            if (!string.IsNullOrWhiteSpace(src))
            {
                foreach (var r in rows)
                {
                    // Tenant/service farkları:
                    // - bazı listelerde birim key'i _key yerine _key_sis_stok_birim_listesi / _key_sis_stok_birim olarak gelebiliyor.
                    // - bazılarında _key_scf_kalem_birimleri dönebiliyor.
                    var key = GetLong(r,
                        "_key_scf_kalem_birimleri",
                        "_key_sis_stok_birim_listesi",
                        "_key_sis_stok_birim",
                        "_key",
                        "key",
                        "birimkey",
                        "fatbirimkey");
                    if (!key.HasValue) continue;

                    var birimKodu = NormalizeUnit(GetString(r, "birimkodu", "kodu"));
                    var birimAdi = NormalizeUnit(GetString(r, "birimadi", "adi", "kisaadi"));
                    if (src == birimKodu || src == birimAdi) return key;
                }
            }

            // default/ana birim fallback
            foreach (var r in rows)
            {
                var isDefault = ParseBoolLike(r, "ontanimli", "varsayilan", "anabirim", "ana");
                if (!isDefault) continue;
                var key = GetLong(r,
                    "_key_scf_kalem_birimleri",
                    "_key_sis_stok_birim_listesi",
                    "_key_sis_stok_birim",
                    "_key",
                    "key",
                    "birimkey",
                    "fatbirimkey");
                if (key.HasValue) return key;
            }

            return GetLong(rows[0],
                "_key_scf_kalem_birimleri",
                "_key_sis_stok_birim_listesi",
                "_key_sis_stok_birim",
                "_key",
                "key",
                "birimkey",
                "fatbirimkey");
        }

        private static long? ChooseUnitKeyPreferRowKey(List<JsonElement> rows, string? sourceBirimText)
        {
            if (rows.Count == 0) return null;
            var src = NormalizeUnit(sourceBirimText);

            static long? RowKey(JsonElement r)
                => GetLong(r, "_key", "key");

            if (!string.IsNullOrWhiteSpace(src))
            {
                foreach (var r in rows)
                {
                    var key = RowKey(r);
                    if (!key.HasValue) continue;

                    var birimKodu = NormalizeUnit(GetString(r, "birimkodu", "kodu"));
                    var birimAdi = NormalizeUnit(GetString(r, "birimadi", "adi", "kisaadi"));
                    if (src == birimKodu || src == birimAdi) return key;
                }
            }

            foreach (var r in rows)
            {
                var isDefault = ParseBoolLike(r, "ontanimli", "varsayilan", "anabirim", "ana");
                if (!isDefault) continue;
                var key = RowKey(r);
                if (key.HasValue) return key;
            }

            return RowKey(rows[0]);
        }

        private static long? ChooseHizmetUnitRowKey(List<JsonElement> rows, string? sourceBirimText, long expectedHizmetKartKey)
        {
            if (rows.Count == 0) return null;
            var src = NormalizeUnit(sourceBirimText);

            static string Norm(string? s) => NormalizeUnit(s);
            static long? RowKey(JsonElement r) => GetLong(r, "_key", "key");

            IEnumerable<JsonElement> Filtered()
                => rows.Where(r => GetLong(r, "_key_scf_hizmetkart", "_key_hizmetkart") == expectedHizmetKartKey);

            var filtered = Filtered().ToList();
            if (filtered.Count == 0) filtered = rows;

            if (!string.IsNullOrWhiteSpace(src))
            {
                foreach (var r in filtered)
                {
                    var key = RowKey(r);
                    if (!key.HasValue) continue;
                    var birimKodu = Norm(GetString(r, "birimkodu", "kodu"));
                    var birimAdi = Norm(GetString(r, "birimadi", "adi", "kisaadi"));
                    if (src == birimKodu || src == birimAdi) return key;
                }
            }

            foreach (var r in filtered)
            {
                var isDefault = ParseBoolLike(r, "ontanimli", "varsayilan", "anabirim", "ana");
                if (!isDefault) continue;
                var key = RowKey(r);
                if (key.HasValue) return key;
            }

            return RowKey(filtered[0]);
        }

        private static long? ChooseStokKartUnitRowKey(List<JsonElement> rows, string? sourceBirimText, long expectedStokKartKey)
        {
            if (rows.Count == 0) return null;
            var src = NormalizeUnit(sourceBirimText);

            static string Norm(string? s) => NormalizeUnit(s);
            static long? RowKey(JsonElement r) => GetLong(r, "_key", "key");

            IEnumerable<JsonElement> Filtered()
                => rows.Where(r => GetLong(r, "_key_scf_stokkart", "_key_stokkart") == expectedStokKartKey);

            var filtered = Filtered().ToList();
            if (filtered.Count == 0) filtered = rows;

            if (!string.IsNullOrWhiteSpace(src))
            {
                foreach (var r in filtered)
                {
                    var key = RowKey(r);
                    if (!key.HasValue) continue;
                    var birimKodu = Norm(GetString(r, "birimkodu", "kodu"));
                    var birimAdi = Norm(GetString(r, "birimadi", "adi", "kisaadi"));
                    if (src == birimKodu || src == birimAdi) return key;
                }
            }

            foreach (var r in filtered)
            {
                var isDefault = ParseBoolLike(r, "ontanimli", "varsayilan", "anabirim", "ana");
                if (!isDefault) continue;
                var key = RowKey(r);
                if (key.HasValue) return key;
            }

            return RowKey(filtered[0]);
        }

        private static string NormalizeUnit(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
            var s = raw.Trim().ToUpperInvariant()
                .Replace("İ", "I")
                .Replace("Ş", "S")
                .Replace("Ğ", "G")
                .Replace("Ü", "U")
                .Replace("Ö", "O")
                .Replace("Ç", "C");
            // common unit normalization
            return s switch
            {
                "ADET" => "AD",
                "AD" => "AD",
                "KILOGRAM" => "KG",
                "KILO" => "KG",
                "KG" => "KG",
                "GRAM" => "GR",
                "GR" => "GR",
                "LITRE" => "LT",
                "LT" => "LT",
                _ => s
            };
        }

        private static bool ParseBoolLike(JsonElement row, params string[] names)
        {
            foreach (var n in names)
            {
                if (!row.TryGetProperty(n, out var p)) continue;
                if (p.ValueKind == JsonValueKind.True) return true;
                if (p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var num) && num != 0) return true;
                if (p.ValueKind == JsonValueKind.String)
                {
                    var s = p.GetString();
                    if (string.Equals(s, "t", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(s, "true", StringComparison.OrdinalIgnoreCase)
                        || s == "1")
                        return true;
                }
            }
            return false;
        }

        private static long? GetLong(JsonElement element, params string[] names)
        {
            foreach (var n in names)
            {
                if (!element.TryGetProperty(n, out var p)) continue;
                if (p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out var num)) return num;
                if (p.ValueKind == JsonValueKind.String && long.TryParse(p.GetString(), out var str)) return str;
            }
            return null;
        }

        private static string? GetString(JsonElement element, params string[] names)
        {
            foreach (var n in names)
            {
                if (!element.TryGetProperty(n, out var p)) continue;
                if (p.ValueKind == JsonValueKind.String) return p.GetString();
                if (p.ValueKind == JsonValueKind.Number) return p.GetRawText();
            }
            return null;
        }

        private static JsonElement GetRaw(JsonElement element, params string[] names)
        {
            foreach (var n in names)
            {
                if (element.TryGetProperty(n, out var p)) return p;
            }
            return default;
        }

        private static string? ExtractDynamicFromDetailRow(JsonElement row, string? preferredColumn)
        {
            var candidates = new List<string>();
            if (!string.IsNullOrWhiteSpace(preferredColumn))
                candidates.Add(preferredColumn);
            candidates.AddRange(new[] { "__dinamik__2", "__dinamik__1", "__dinamik__00002", "__dinamik__00001" });

            foreach (var c in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!row.TryGetProperty(c, out var p)) continue;
                var value = p.ValueKind == JsonValueKind.String ? p.GetString() : p.GetRawText();
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
            return null;
        }

        private static string NormalizeText(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
            return raw.Trim().ToUpperInvariant()
                .Replace("İ", "I")
                .Replace("İ", "I")
                .Replace("Ş", "S")
                .Replace("Ğ", "G")
                .Replace("Ü", "U")
                .Replace("Ö", "O")
                .Replace("Ç", "C");
        }

        private static bool Eq(string a, string? b) =>
            !string.IsNullOrWhiteSpace(b) && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

        private static string Escape(string raw) => raw.Replace("'", "''");
    }
}

