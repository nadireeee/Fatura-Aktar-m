using DiaErpIntegration.API.Services;
using DiaErpIntegration.API.Models.Api;
using DiaErpIntegration.API.Models.DiaV3Json;
using DiaErpIntegration.API.Options;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace DiaErpIntegration.API.Controllers
{
    [ApiController]
    [Route("api/lookups")]
    public class LookupsController : ControllerBase
    {
        private static readonly object _companiesCacheLock = new();
        private static DateTimeOffset _companiesCacheAt = DateTimeOffset.MinValue;
        private static List<CompanyDto> _companiesCache = new();

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, (DateTimeOffset at, List<DiaAuthorizedPeriodItem> periods)> _periodCache = new();
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTimeOffset at, List<DiaAuthorizedBranchItem> branches)> _branchCache = new();

        private static bool CacheFresh(DateTimeOffset at, int minutes) =>
            at != DateTimeOffset.MinValue && (DateTimeOffset.UtcNow - at).TotalMinutes < minutes;

        private readonly IDiaWsClient _diaClient;
        private readonly DiaOptions _opt;
        private readonly ILogger<LookupsController> _logger;

        public LookupsController(IDiaWsClient diaClient, IOptions<DiaOptions> opt, ILogger<LookupsController> logger)
        {
            _diaClient = diaClient;
            _opt = opt.Value;
            _logger = logger;
        }

        [HttpGet("default-source")]
        public IActionResult GetDefaultSource()
        {
            var defaultFirma = _opt.DefaultSourceFirmaKodu > 0
                ? _opt.DefaultSourceFirmaKodu
                : _opt.PoolFirmaKodu;
            return Ok(new DefaultSourceContextDto
            {
                DefaultSourceFirmaKodu = defaultFirma,
                DefaultSourceDonemKodu = _opt.DefaultSourceDonemKodu,
                DefaultSourceSubeKey = _opt.DefaultSourceSubeKey
            });
        }

        [HttpGet("pool")]
        public async Task<IActionResult> GetPool()
        {
            try
            {
                var ctx = await _diaClient.GetAuthorizedCompanyPeriodBranchAsync();
                var pool = ctx.FirstOrDefault(x => x.FirmaKodu == _opt.PoolFirmaKodu);
                if (pool != null)
                    return Ok(new PoolContextDto { PoolFirmaKodu = pool.FirmaKodu, PoolFirmaAdi = pool.FirmaAdi });

                // Mock modda (veya DIA geçici hata/cache boş) ctx boş gelebilir.
                // UI'nın hardcode'a düşmemesi için en azından config'deki PoolFirmaKodu ile 200 dön.
                return Ok(new PoolContextDto
                {
                    PoolFirmaKodu = _opt.PoolFirmaKodu,
                    PoolFirmaAdi = string.IsNullOrWhiteSpace(_opt.PoolFirmaAdi)
                        ? $"(Pool) FirmaKodu={_opt.PoolFirmaKodu}"
                        : _opt.PoolFirmaAdi
                });
            }
            catch
            {
                return StatusCode(502, new { message = "Lookup başarısız: havuz firma alınamadı." });
            }
        }

        [HttpGet("companies")]
        public async Task<IActionResult> GetCompanies()
        {
            try
            {
                var ctx = await _diaClient.GetAuthorizedCompanyPeriodBranchAsync();
                var fromAuthorized = ctx
                    .Select(c => new CompanyDto { FirmaKodu = c.FirmaKodu, FirmaAdi = c.FirmaAdi })
                    .ToList();

                // UI için kritik: firmalar yavaş gelirse tüm dropdown'lar disabled kalıyor.
                // Önce yetkili ağaçtan hızlı listeyi hazırla; geniş listeyi kısa timeout ile merge etmeye çalış.
                List<(int FirmaKodu, string FirmaAdi)> fromAll = new();
                var allTask = _diaClient.GetAllCompaniesAsync();
                var done = await Task.WhenAny(allTask, Task.Delay(1500));
                if (done == allTask)
                {
                    fromAll = await allTask;
                }
                else
                {
                    // Cache varsa onu döndür.
                    lock (_companiesCacheLock)
                    {
                        if (_companiesCache.Count > 0 && (DateTimeOffset.UtcNow - _companiesCacheAt).TotalMinutes < 10)
                            return Ok(_companiesCache);
                    }

                    // Arkaplanda cache'i tazele (isteği bloke etmez).
                    _ = allTask.ContinueWith(t =>
                    {
                        if (t.Status != TaskStatus.RanToCompletion) return;
                        try
                        {
                            var mergedLocal = new Dictionary<int, string>();
                            foreach (var c in t.Result)
                            {
                                if (c.FirmaKodu <= 0) continue;
                                if (!mergedLocal.ContainsKey(c.FirmaKodu))
                                    mergedLocal[c.FirmaKodu] = c.FirmaAdi;
                            }
                            foreach (var c in fromAuthorized)
                            {
                                if (c.FirmaKodu <= 0) continue;
                                if (!mergedLocal.ContainsKey(c.FirmaKodu) || string.IsNullOrWhiteSpace(mergedLocal[c.FirmaKodu]))
                                    mergedLocal[c.FirmaKodu] = c.FirmaAdi;
                            }
                            var companiesLocal = mergedLocal
                                .Select(x => new CompanyDto { FirmaKodu = x.Key, FirmaAdi = x.Value })
                                .OrderBy(c => c.FirmaKodu)
                                .ToList();
                            lock (_companiesCacheLock)
                            {
                                _companiesCache = companiesLocal;
                                _companiesCacheAt = DateTimeOffset.UtcNow;
                            }
                        }
                        catch { /* ignore */ }
                    });
                }
                var merged = new Dictionary<int, string>();

                foreach (var c in fromAll)
                {
                    if (c.FirmaKodu <= 0) continue;
                    if (!merged.ContainsKey(c.FirmaKodu))
                        merged[c.FirmaKodu] = c.FirmaAdi;
                }
                foreach (var c in fromAuthorized)
                {
                    if (c.FirmaKodu <= 0) continue;
                    if (!merged.ContainsKey(c.FirmaKodu) || string.IsNullOrWhiteSpace(merged[c.FirmaKodu]))
                        merged[c.FirmaKodu] = c.FirmaAdi;
                }

                var companies = merged
                    .Select(x => new CompanyDto { FirmaKodu = x.Key, FirmaAdi = x.Value })
                    .OrderBy(c => c.FirmaKodu)
                    .ToList();

                lock (_companiesCacheLock)
                {
                    _companiesCache = companies;
                    _companiesCacheAt = DateTimeOffset.UtcNow;
                }
                return Ok(companies);
            }
            catch
            {
                return StatusCode(502, new { message = "Lookup başarısız: firmalar alınamadı." });
            }
        }

        [HttpGet("periods")]
        public async Task<IActionResult> GetPeriods([FromQuery] int firmaKodu)
        {
            try
            {
                if (_periodCache.TryGetValue(firmaKodu, out var hit) && CacheFresh(hit.at, 10) && hit.periods.Count > 0)
                {
                    var cached = hit.periods
                        .Select(d => new PeriodDto
                        {
                            Key = d.Key,
                            DonemKodu = d.DonemKodu,
                            GorunenKod = FormatPeriodLabel(d),
                            Ontanimli = string.Equals(d.Ontanimli, "t", StringComparison.OrdinalIgnoreCase),
                            BaslangicTarihi = d.BaslangicTarihi,
                            BitisTarihi = d.BitisTarihi
                        })
                        .OrderByDescending(p => p.DonemKodu)
                        .ToList();
                    return Ok(cached);
                }

                var ctx = await _diaClient.GetAuthorizedCompanyPeriodBranchAsync();
                var company = ctx.FirstOrDefault(c => c.FirmaKodu == firmaKodu);
                // Yetkili ağaçta yoksa yine de firma bazlı dönem listesini çekmeye çalış.
                if (company == null)
                {
                    var sourcePeriodsFallback = await _diaClient.GetPeriodsByFirmaAsync(firmaKodu);
                    var periodsFallback = sourcePeriodsFallback
                        .Select(d => new PeriodDto
                        {
                            Key = d.Key,
                            DonemKodu = d.DonemKodu,
                            GorunenKod = FormatPeriodLabel(d),
                            Ontanimli = string.Equals(d.Ontanimli, "t", StringComparison.OrdinalIgnoreCase),
                            BaslangicTarihi = d.BaslangicTarihi,
                            BitisTarihi = d.BitisTarihi
                        })
                        .OrderByDescending(p => p.DonemKodu)
                        .ToList();
                    return Ok(periodsFallback);
                }

                var sourcePeriods = (company.Donemler.Count > 0
                    ? company.Donemler
                    : (company.DonemFallback.Count > 0
                        ? company.DonemFallback
                        : company.DonemListFallback));

                // Bazı tenantlarda yetkili context dönemleri boş dönebiliyor.
                // Bu durumda firma bazlı gerçek dönem listesini ayrı servisten çek.
                if (sourcePeriods.Count == 0)
                {
                    sourcePeriods = await _diaClient.GetPeriodsByFirmaAsync(firmaKodu);
                }

                // cache raw period items
                if (sourcePeriods.Count > 0)
                    _periodCache[firmaKodu] = (DateTimeOffset.UtcNow, sourcePeriods);

                var periods = sourcePeriods
                    .Select(d => new PeriodDto
                    {
                        Key = d.Key,
                        DonemKodu = d.DonemKodu,
                        GorunenKod = FormatPeriodLabel(d),
                        Ontanimli = string.Equals(d.Ontanimli, "t", StringComparison.OrdinalIgnoreCase),
                        BaslangicTarihi = d.BaslangicTarihi,
                        BitisTarihi = d.BitisTarihi
                    })
                    .OrderByDescending(p => p.DonemKodu)
                    .ToList();

                // Some tenants may return empty donemler in yetkili_firma response.
                // Fallback sadece HAVUZ firması için uygulanır.
                var isPool = firmaKodu == _opt.PoolFirmaKodu;
                if (periods.Count == 0 && isPool && _opt.DefaultSourceDonemKodu > 0)
                {
                    periods.Add(new PeriodDto
                    {
                        Key = _opt.DefaultSourceDonemKodu,
                        DonemKodu = _opt.DefaultSourceDonemKodu,
                        GorunenKod = _opt.DefaultSourceDonemKodu.ToString(),
                        Ontanimli = true,
                        BaslangicTarihi = null,
                        BitisTarihi = null
                    });
                }
                return Ok(periods);
            }
            catch
            {
                return StatusCode(502, new { message = "Lookup başarısız: dönemler alınamadı.", firmaKodu });
            }
        }

        [HttpGet("branches")]
        public async Task<IActionResult> GetBranches([FromQuery] int firmaKodu)
        {
            try
            {
                var probeDonem = _opt.DefaultSourceDonemKodu > 0 ? _opt.DefaultSourceDonemKodu : 1;
                var bKey = $"{firmaKodu}|{probeDonem}";
                if (_branchCache.TryGetValue(bKey, out var bhit) && CacheFresh(bhit.at, 10) && bhit.branches.Count > 0)
                {
                    var cached = bhit.branches
                        .Select(s => new BranchDto { Key = s.Key, SubeAdi = s.SubeAdi })
                        .OrderBy(s => s.SubeAdi)
                        .ToList();
                    return Ok(cached);
                }

                var ctx = await _diaClient.GetAuthorizedCompanyPeriodBranchAsync();
                var company = ctx.FirstOrDefault(c => c.FirmaKodu == firmaKodu);
                if (company == null || company.Subeler.Count == 0)
                {
                    // UI bloklanmasın: uzun süren DIA çağrısı olursa cache dön.
                    // Boş dönmek dropdown'u "Seçiniz"te bırakıp aktarımı kilitliyor; bu yüzden mümkünse son bilinen cache'i dön.
                    var task = _diaClient.GetSubelerDepolarForFirmaAsync(firmaKodu, probeDonem);
                    var done = await Task.WhenAny(task, Task.Delay(12000));
                    if (done != task)
                    {
                        if (_branchCache.TryGetValue(bKey, out var bhit2) && bhit2.branches.Count > 0)
                        {
                            var cached2 = bhit2.branches
                                .Select(s => new BranchDto { Key = s.Key, SubeAdi = s.SubeAdi })
                                .OrderBy(s => s.SubeAdi)
                                .ToList();
                            return Ok(cached2);
                        }
                        // 504 yerine boş liste dön: tarayıcı konsolunda hata spam'i olmasın.
                        // Kullanıcı tekrar denediğinde cache dolabilir.
                        _logger.LogWarning("Branches lookup timed out. firmaKodu={Firma} probeDonem={Donem}", firmaKodu, probeDonem);
                        return Ok(new List<BranchDto>());
                    }

                    var branchesFallback = await task;
                    var dtoFallback = branchesFallback
                        .Select(s => new BranchDto { Key = s.Key, SubeAdi = s.SubeAdi })
                        .OrderBy(s => s.SubeAdi)
                        .ToList();
                    if (branchesFallback.Count > 0)
                        _branchCache[bKey] = (DateTimeOffset.UtcNow, branchesFallback);
                    return Ok(dtoFallback);
                }

                var branches = company.Subeler
                    .Select(s => new BranchDto { Key = s.Key, SubeAdi = s.SubeAdi })
                    .OrderBy(s => s.SubeAdi)
                    .ToList();
                return Ok(branches);
            }
            catch
            {
                return StatusCode(502, new { message = "Lookup başarısız: şubeler alınamadı.", firmaKodu });
            }
        }

        [HttpGet("depots")]
        public async Task<IActionResult> GetDepots([FromQuery] int firmaKodu, [FromQuery] long subeKey)
        {
            try
            {
                var ctx = await _diaClient.GetAuthorizedCompanyPeriodBranchAsync();
                var company = ctx.FirstOrDefault(c => c.FirmaKodu == firmaKodu);
                if (company == null || company.Subeler.Count == 0)
                {
                    var probeDonem = _opt.DefaultSourceDonemKodu > 0 ? _opt.DefaultSourceDonemKodu : 1;
                    var branchesFallback = await _diaClient.GetSubelerDepolarForFirmaAsync(firmaKodu, probeDonem);
                    var branchFallback = branchesFallback.FirstOrDefault(s => s.Key == subeKey);
                    if (branchFallback == null) return Ok(new List<DepotDto>());
                    var depotsFallback = (branchFallback.Depolar ?? new List<DiaAuthorizedDepotItem>())
                        .Select(d => new DepotDto { Key = d.Key, DepoAdi = d.DepoAdi })
                        .OrderBy(d => d.DepoAdi)
                        .ToList();
                    return Ok(depotsFallback);
                }

                var branch = company.Subeler.FirstOrDefault(s => s.Key == subeKey);
                if (branch == null) return Ok(new List<DepotDto>());

                var depotsRaw = branch.Depolar ?? new List<DiaAuthorizedDepotItem>();
                if (depotsRaw.Count == 0)
                {
                    // Bazı tenantlarda yetkili ağaç depoları boş dönebiliyor; sis_depo_* ile fallback.
                    var probeDonem = company.Donemler.FirstOrDefault()?.DonemKodu
                        ?? company.DonemFallback.FirstOrDefault()?.DonemKodu
                        ?? company.DonemListFallback.FirstOrDefault()?.DonemKodu
                        ?? (_opt.DefaultSourceDonemKodu > 0 ? _opt.DefaultSourceDonemKodu : 1);
                    var branchesFallback = await _diaClient.GetSubelerDepolarForFirmaAsync(firmaKodu, probeDonem);
                    var hit = branchesFallback.FirstOrDefault(s => s.Key == subeKey);
                    depotsRaw = hit?.Depolar ?? new List<DiaAuthorizedDepotItem>();
                }

                var depots = depotsRaw
                    .Where(d => d.Key > 0)
                    .Select(d => new DepotDto { Key = d.Key, DepoAdi = d.DepoAdi })
                    .OrderBy(d => d.DepoAdi)
                    .ToList();
                return Ok(depots);
            }
            catch
            {
                return StatusCode(502, new { message = "Lookup başarısız: depolar alınamadı.", firmaKodu, subeKey });
            }
        }

        [HttpGet("invoice-types")]
        public async Task<IActionResult> GetInvoiceTypes(
            [FromQuery] int firmaKodu,
            [FromQuery] int donemKodu,
            [FromQuery] int? sourceSubeKey,
            [FromQuery] long? sourceDepoKey
        )
        {
            try
            {
                // Tenant farkı: şube/depo filtre kolonları liste servisinde tutarsız olabiliyor.
                // Fatura türleri için şube/depo filtresi uygulamayalım; dönem değişince türler güncellensin.
                var filters = string.Empty;

                var types = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var offset = 0;
                const int batchSize = 200;
                const int maxOffsetSafety = 20000; // sanity

                while (true)
                {
                    var page = await _diaClient.GetInvoicesAsync(firmaKodu, donemKodu, filters, batchSize, offset);
                    if (page.Count == 0) break;

                    foreach (var it in page)
                    {
                        var v = (it.TuruAck ?? it.TuruKisa ?? string.Empty).Trim();
                        if (!string.IsNullOrWhiteSpace(v))
                            types.Add(v);
                    }

                    if (page.Count < batchSize) break;
                    offset += batchSize;
                    if (offset > maxOffsetSafety) break;
                }

                var ordered = types
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                return Ok(ordered);
            }
            catch
            {
                // UI için fail-safe: boş liste döndür.
                return Ok(new List<string>());
            }
        }

        [HttpPost("resolve-target")]
        public async Task<IActionResult> ResolveTarget([FromBody] TargetResolveRequestDto? req)
        {
            if (req is null)
                return BadRequest(new { code = "body_null", message = "İstek gövdesi boş." });
            if (req.TargetFirmaKodu <= 0)
                return BadRequest(new { code = "target_firma_missing", message = "Hedef firma seçilmedi." });

            var ctx = await _diaClient.GetAuthorizedCompanyPeriodBranchAsync();
            var company = ctx.FirstOrDefault(c => c.FirmaKodu == req.TargetFirmaKodu);

            var fallbackReasons = new List<string>();

            List<DiaAuthorizedPeriodItem> periods;
            List<DiaAuthorizedBranchItem> branches;
            string targetFirmaAdi;

            if (company != null)
            {
                targetFirmaAdi = company.FirmaAdi;
                periods = (company.Donemler.Count > 0
                    ? company.Donemler
                    : (company.DonemFallback.Count > 0 ? company.DonemFallback : company.DonemListFallback));
                if (periods.Count == 0)
                {
                    if (_periodCache.TryGetValue(req.TargetFirmaKodu, out var phit) && CacheFresh(phit.at, 10))
                        periods = phit.periods;
                    else
                    {
                        periods = await _diaClient.GetPeriodsByFirmaAsync(req.TargetFirmaKodu);
                        _periodCache[req.TargetFirmaKodu] = (DateTimeOffset.UtcNow, periods);
                    }
                }

                branches = company.Subeler
                    .Where(s => s.Key > 0 && !string.IsNullOrWhiteSpace(s.SubeAdi))
                    .ToList();
                if (branches.Count == 0)
                {
                    var probeDonem = periods.FirstOrDefault()?.DonemKodu
                        ?? (req.SourceDonemKodu is > 0 ? req.SourceDonemKodu.Value : 1);
                    var bKey = $"{req.TargetFirmaKodu}|{probeDonem}";
                    if (_branchCache.TryGetValue(bKey, out var bhit) && CacheFresh(bhit.at, 10))
                        branches = bhit.branches;
                    else
                    {
                        branches = await _diaClient.GetSubelerDepolarForFirmaAsync(req.TargetFirmaKodu, probeDonem);
                        _branchCache[bKey] = (DateTimeOffset.UtcNow, branches);
                    }
                }
            }
            else
            {
                var all = await _diaClient.GetAllCompaniesAsync();
                var hit = all.FirstOrDefault(x => x.FirmaKodu == req.TargetFirmaKodu);
                targetFirmaAdi = (hit.FirmaKodu == req.TargetFirmaKodu && !string.IsNullOrWhiteSpace(hit.FirmaAdi))
                    ? hit.FirmaAdi
                    : $"Firma {req.TargetFirmaKodu}";

                if (_periodCache.TryGetValue(req.TargetFirmaKodu, out var phit2) && CacheFresh(phit2.at, 10))
                    periods = phit2.periods;
                else
                {
                    periods = await _diaClient.GetPeriodsByFirmaAsync(req.TargetFirmaKodu);
                    _periodCache[req.TargetFirmaKodu] = (DateTimeOffset.UtcNow, periods);
                }
                var probeDonem = periods.FirstOrDefault()?.DonemKodu
                    ?? (req.SourceDonemKodu is > 0 ? req.SourceDonemKodu.Value : 1);
                var bKey2 = $"{req.TargetFirmaKodu}|{probeDonem}";
                if (_branchCache.TryGetValue(bKey2, out var bhit2) && CacheFresh(bhit2.at, 10))
                    branches = bhit2.branches;
                else
                {
                    branches = await _diaClient.GetSubelerDepolarForFirmaAsync(req.TargetFirmaKodu, probeDonem);
                    _branchCache[bKey2] = (DateTimeOffset.UtcNow, branches);
                }
                fallbackReasons.Add("Hedef firma yetkili ağaçta yoktu; sis_* listeleri ile yedek çözümleme kullanıldı.");
            }

            if (periods.Count == 0)
                return BadRequest(new { code = "target_periods_empty", message = "Hedef firmada dönem listesi boş." });

            // SourceInvoiceDate gelmeyebilir (örn: kullanıcı henüz fatura seçmeden hedef firmayı seçti).
            // Bu durumda 400'e düşürmeyelim; hedef firmada öntanımlı/son dönemi seçelim.
            DiaAuthorizedPeriodItem? period;
            string? periodReason;
            string? periodError;
            if (string.IsNullOrWhiteSpace(req.SourceInvoiceDate) && !(req.SourceDonemKodu is > 0))
            {
                period = periods.FirstOrDefault(p => string.Equals(p.Ontanimli, "t", StringComparison.OrdinalIgnoreCase))
                    ?? periods.OrderByDescending(p => p.DonemKodu).FirstOrDefault();
                periodReason = "Kaynak fatura tarihi yoktu; hedef dönem öntanımlı/son dönem olarak seçildi.";
                periodError = null;
            }
            else
            {
                period = ResolvePeriodByInvoiceDate(periods, req.SourceInvoiceDate, req.SourceDonemKodu, out periodReason, out periodError);
            }
            if (period == null)
            {
                // DİA tenant farkı / tarih parse sorunlarında 400'e düşürmek UI'yi kilitliyor.
                // Burada fail-safe: hedef firmada öntanımlı/son döneme düş.
                period = periods.FirstOrDefault(p => string.Equals(p.Ontanimli, "t", StringComparison.OrdinalIgnoreCase))
                    ?? periods.OrderByDescending(p => p.DonemKodu).FirstOrDefault();
                if (period == null)
                    return BadRequest(new { code = "target_periods_empty", message = "Hedef firmada dönem listesi boş." });

                fallbackReasons.Add("Kaynak tarih/dönem ile hedef dönem eşleşmedi; öntanımlı/son dönem seçildi.");
            }
            if (!string.IsNullOrWhiteSpace(periodReason))
                fallbackReasons.Add(periodReason);

            branches = branches
                .Where(s => s.Key > 0 && !string.IsNullOrWhiteSpace(s.SubeAdi))
                .ToList();
            if (branches.Count == 0)
                return BadRequest(new { code = "target_branches_empty", message = "Hedef firmada şube/depo bulunamadı (yetkili liste ve sis_sube yedekleri boş)." });

            // Kullanıcı isteği:
            // - Hedef firmada şube/depo TEK ise otomatik seç.
            // - 1'den fazla ise kullanıcı seçsin (backend otomatik seçim yapmasın).
            DiaAuthorizedBranchItem? selectedBranch = null;
            DiaAuthorizedDepotItem? selectedDepot = null;

            if (branches.Count == 1)
            {
                selectedBranch = branches[0];
                fallbackReasons.Add($"Hedef şube otomatik: {selectedBranch.SubeAdi} (şubeSayısı=1).");

                var depots = (selectedBranch.Depolar ?? new List<DiaAuthorizedDepotItem>())
                    .Where(d => d.Key > 0 && !string.IsNullOrWhiteSpace(d.DepoAdi))
                    .ToList();
                if (depots.Count == 0)
                    return BadRequest(new { code = "target_depot_empty", message = "Seçilen şubede depo bulunamadı." });

                if (depots.Count == 1)
                {
                    selectedDepot = depots[0];
                    fallbackReasons.Add($"Hedef depo otomatik: {selectedDepot.DepoAdi} (depoSayısı=1).");
                }
                else
                {
                    fallbackReasons.Add($"Hedef depoda {depots.Count} kayıt var; kullanıcı seçim yapmalı.");
                }
            }
            else
            {
                fallbackReasons.Add($"Hedef şubede {branches.Count} kayıt var; kullanıcı seçim yapmalı.");
            }

            var result = new TargetResolveResultDto
            {
                TargetFirmaKodu = req.TargetFirmaKodu,
                TargetFirmaAdi = targetFirmaAdi,
                TargetSubeKey = selectedBranch?.Key ?? 0,
                TargetSubeAdi = selectedBranch?.SubeAdi ?? string.Empty,
                TargetDepoKey = selectedDepot?.Key ?? 0,
                TargetDepoAdi = selectedDepot?.DepoAdi ?? string.Empty,
                TargetDonemKodu = period.DonemKodu,
                TargetDonemKey = period.Key,
                TargetDonemLabel = FormatPeriodLabel(period),
                AutoSelected = selectedBranch != null && selectedDepot != null,
                FallbackUsed = fallbackReasons.Count > 0,
                FallbackReason = fallbackReasons.Count > 0 ? string.Join(" | ", fallbackReasons) : null
            };
            return Ok(result);
        }

        private static string FormatPeriodLabel(DiaErpIntegration.API.Models.DiaV3Json.DiaAuthorizedPeriodItem d)
        {
            // Kullanıcıya "02" gibi görünen kod yerine yıl/tarih aralığı göster.
            // donemkodu gerçek numeric code (örn: 4/5), ama label'da yıl istiyoruz.
            if (DateTime.TryParse(d.BaslangicTarihi, out var b) && DateTime.TryParse(d.BitisTarihi, out var e))
            {
                if (b.Year == e.Year) return $"{b.Year}";
                return $"{b:yyyy}–{e:yyyy}";
            }
            if (DateTime.TryParse(d.BaslangicTarihi, out var b2)) return $"{b2.Year}";
            return string.IsNullOrWhiteSpace(d.GorunenDonemKodu) ? d.DonemKodu.ToString() : d.GorunenDonemKodu;
        }

        /// <summary>
        /// Hedef dönem: öncelikle kaynak fatura tarihinin düştüğü [başlangıç, bitiş] aralığı;
        /// kod eşlemesi kullanılmaz. Tarih yoksa öntanımlı / ilk dönem.
        /// </summary>
        private static DiaAuthorizedPeriodItem? ResolvePeriodByInvoiceDate(
            List<DiaAuthorizedPeriodItem> periods,
            string? sourceInvoiceDate,
            int? sourceDonemKodu,
            out string? reason,
            out string? error)
        {
            reason = null;
            error = null;
            if (periods.Count == 0) return null;

            if (DateTime.TryParse(sourceInvoiceDate, out var invDate))
            {
                var exact = periods.FirstOrDefault(p =>
                    DateTime.TryParse(p.BaslangicTarihi, out var b) &&
                    DateTime.TryParse(p.BitisTarihi, out var e) &&
                    invDate.Date >= b.Date && invDate.Date <= e.Date);
                if (exact != null)
                    return exact;

                var yearMatch = periods.Where(p =>
                    DateTime.TryParse(p.BaslangicTarihi, out var b) &&
                    DateTime.TryParse(p.BitisTarihi, out var e) &&
                    invDate.Year >= b.Year && invDate.Year <= e.Year).ToList();
                if (yearMatch.Count > 0)
                {
                    reason = $"Fatura tarihi tam takvim aralığında değil; {invDate.Year} yılını kapsayan dönem seçildi.";
                    return yearMatch[0];
                }

                reason = "Fatura tarihi hiçbir dönem aralığına uymuyor; öntanımlı/ilk dönem.";
                var def = periods.FirstOrDefault(p => string.Equals(p.Ontanimli, "t", StringComparison.OrdinalIgnoreCase));
                return def ?? periods[0];
            }

            if (sourceDonemKodu is > 0)
            {
                var exactCode = periods.FirstOrDefault(p => p.DonemKodu == sourceDonemKodu.Value);
                if (exactCode != null)
                {
                    reason = "Kaynak fatura tarihi yok; kaynak dönem kodu ile hedef dönem eşlendi.";
                    return exactCode;
                }
            }

            reason = "Kaynak fatura tarihi yok; öntanımlı/ilk dönem.";
            var def2 = periods.FirstOrDefault(p => string.Equals(p.Ontanimli, "t", StringComparison.OrdinalIgnoreCase));
            return def2 ?? periods[0];
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
                .Replace("Ç", "C")
                .Replace("  ", " ");
        }
    }
}
