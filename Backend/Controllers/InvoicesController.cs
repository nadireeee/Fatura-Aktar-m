using DiaErpIntegration.API.Models.Api;
using DiaErpIntegration.API.Services;
using Microsoft.AspNetCore.Mvc;

namespace DiaErpIntegration.API.Controllers;

[ApiController]
[Route("api/invoices")]
public sealed class InvoicesController : ControllerBase
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTimeOffset at, List<InvoiceListRowDto> rows)> _lastListCache = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTimeOffset at, bool value)> _distributableDecisionCache = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTimeOffset at, InvoiceDetailDto dto)> _lastDetailCache = new();

    private readonly IDiaWsClient _dia;
    private readonly ILogger<InvoicesController> _logger;
    private readonly InvoiceTransferService _transfer;

    public InvoicesController(IDiaWsClient dia, ILogger<InvoicesController> logger, InvoiceTransferService transfer)
    {
        _dia = dia;
        _logger = logger;
        _transfer = transfer;
    }

    [HttpPost("list")]
    public async Task<ActionResult<List<InvoiceListRowDto>>> List([FromBody] InvoiceListRequestDto req)
    {
        // DİA tarafı bazen ilk çağrıda 500 dönebiliyor; hızlı bir retry UI'yi 502'ye düşürmeden toparlıyor.
        Exception? last = null;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                var res = await ListCore(req);
                if (res.Result is OkObjectResult ok && ok.Value is List<InvoiceListRowDto> rows)
                {
                    var cacheKey = $"{req.FirmaKodu}|{req.DonemKodu}|{req.SourceSubeKey}|{req.SourceDepoKey}|{req.OnlyDistributable}|{req.OnlyNonDistributable}|{req.Filters}";
                    _lastListCache[cacheKey] = (DateTimeOffset.UtcNow, rows);
                }
                return res;
            }
            catch (Exception ex)
            {
                last = ex;
                _logger.LogWarning(ex, "Invoice list attempt {Attempt} failed: firma={Firma} donem={Donem}", attempt, req.FirmaKodu, req.DonemKodu);
                await Task.Delay(600);
            }
        }

        _logger.LogError(last, "Invoice list failed after retries: firma={Firma} donem={Donem}", req.FirmaKodu, req.DonemKodu);
        var cacheKey2 = $"{req.FirmaKodu}|{req.DonemKodu}|{req.SourceSubeKey}|{req.SourceDepoKey}|{req.OnlyDistributable}|{req.OnlyNonDistributable}|{req.Filters}";
        if (_lastListCache.TryGetValue(cacheKey2, out var hit2) && (DateTimeOffset.UtcNow - hit2.at).TotalSeconds < 600)
        {
            _logger.LogWarning("Returning cached invoice list due to DIA failure. cacheAgeSeconds={Age}", (DateTimeOffset.UtcNow - hit2.at).TotalSeconds);
            return Ok(hit2.rows);
        }

        var prefix2 = $"{req.FirmaKodu}|{req.DonemKodu}|";
        var altHit2 = _lastListCache
            .Where(kvp => kvp.Key.StartsWith(prefix2, StringComparison.Ordinal))
            .Select(kvp => kvp.Value)
            .OrderByDescending(v => v.at)
            .FirstOrDefault();
        if (altHit2.rows != null && altHit2.rows.Count > 0 && (DateTimeOffset.UtcNow - altHit2.at).TotalSeconds < 600)
        {
            _logger.LogWarning(
                "Returning cached invoice list (same firma/donem) due to DIA failure. cacheAgeSeconds={Age}",
                (DateTimeOffset.UtcNow - altHit2.at).TotalSeconds);
            return Ok(altHit2.rows);
        }

        return Problem(
            detail: last?.Message,
            statusCode: StatusCodes.Status502BadGateway,
            title: "Fatura listesi alınamadı");
    }

    private static async Task<T> WithTimeout<T>(Task<T> task, int ms)
    {
        var done = await Task.WhenAny(task, Task.Delay(ms));
        if (done != task) throw new TimeoutException($"Operation timed out after {ms}ms");
        return await task;
    }

    private async Task<ActionResult<List<InvoiceListRowDto>>> ListCore(InvoiceListRequestDto req)
    {
        if (req.OnlyDistributable && req.OnlyNonDistributable)
            return BadRequest("only_distributable ve only_non_distributable aynı anda true olamaz.");

        var filterParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(req.Filters)) filterParts.Add(req.Filters);
        if (req.UstIslemTuruKey is > 0)
            filterParts.Add($"[_key_sis_ust_islem_turu] = {req.UstIslemTuruKey.Value}");
        // Not: Şube/depo filtre kolonları tenant'a göre değişebiliyor ve bazı tenantlarda listeyi yanlışlıkla 0'a düşürüyor.
        // Güvenilir filtreyi aşağıda yetkili ağaç isimleriyle post-filter olarak uyguluyoruz.
        var filters = string.Join(" AND ", filterParts);

        _logger.LogInformation("Invoice list request: firma_kodu={Firma} donem_kodu={Donem} sourceSubeKey={SubeKey} sourceDepoKey={DepoKey} onlyDistributable={OnlyDistributable} filters={Filters} limit={Limit} offset={Offset}",
            req.FirmaKodu, req.DonemKodu, req.SourceSubeKey, req.SourceDepoKey, req.OnlyDistributable, filters, req.Limit, req.Offset);
        _logger.LogInformation("Invoice list final filters string: {Filters}", filters);

        List<DiaErpIntegration.API.Models.DiaV3Json.DiaInvoiceListItem> items;

        if (req.OnlyDistributable || req.OnlyNonDistributable)
        {
            // Performans: mümkünse ayrıntılı listeden (scf_fatura_listele_ayrintili) distributable key set üret.
            // Bu tenant/kolon uyumsuzluğunda null döner ve detail-scan fallback çalışır.
            var fastDistributableKeys = await _dia.GetDistributableInvoiceKeysAsync(req.FirmaKodu, req.DonemKodu, filters);
            var fastUstIslemKeys = req.UstIslemTuruKey is > 0
                ? await _dia.GetInvoiceKeysByUstIslemTuruAsync(req.FirmaKodu, req.DonemKodu, filters, req.UstIslemTuruKey.Value)
                : null;
            if (fastDistributableKeys != null)
            {
                var desiredEndFast = req.Offset + req.Limit;
                var seenFast = 0;
                var resultSliceFast = new List<DiaErpIntegration.API.Models.DiaV3Json.DiaInvoiceListItem>(req.Limit);
                const int fastBatch = 200;
                var fastOffset = 0;

                while (seenFast < desiredEndFast)
                {
                    var page = await WithTimeout(_dia.GetInvoicesAsync(req.FirmaKodu, req.DonemKodu, filters, fastBatch, fastOffset), 15000);
                    if (page.Count == 0) break;

                    foreach (var it in page)
                    {
                        if (fastUstIslemKeys != null && fastUstIslemKeys.Count > 0 && !fastUstIslemKeys.Contains(it.Key))
                            continue;
                        var has = fastDistributableKeys.Contains(it.Key);
                        var keep = req.OnlyDistributable ? has : !has;
                        if (!keep) continue;

                        if (seenFast >= req.Offset && resultSliceFast.Count < req.Limit)
                            resultSliceFast.Add(it);

                        seenFast++;
                        if (seenFast >= desiredEndFast && resultSliceFast.Count >= req.Limit)
                            break;
                    }

                    fastOffset += page.Count;
                    if (page.Count < fastBatch) break;
                }

                items = resultSliceFast;
                _logger.LogInformation("Invoice list fast filtered-scan returned count={Count} offset={Offset} limit={Limit} mode={Mode} fastKeyCount={FastKeyCount}",
                    items.Count, req.Offset, req.Limit, req.OnlyDistributable ? "only_distributable" : "only_non_distributable", fastDistributableKeys.Count);
            }
            else
            {
            // Kritik kural:
            // - Dağıtılacak = line bazında dinamik şube kolonu dolu olan fatura.
            // - Tüm Faturalar (yeni istek) = kalemlerinde bu alan HİÇ dolu olmayan fatura.
            // scf_fatura_listele satırında bu bilgi yok; bu yüzden veri seti boyunca sayfalar halinde tarayıp
            // offset/limit'i filtrelenmiş listeye göre uygularız.
            var desiredEnd = req.Offset + req.Limit;
            var distributableSeen = 0;
            var resultSlice = new List<DiaErpIntegration.API.Models.DiaV3Json.DiaInvoiceListItem>(req.Limit);
            var ustKeySet = req.UstIslemTuruKey is > 0
                ? (await _dia.GetInvoiceKeysByUstIslemTuruAsync(req.FirmaKodu, req.DonemKodu, filters, req.UstIslemTuruKey.Value) ?? new HashSet<long>())
                : new HashSet<long>();

            const int candidateBatch = 60;
            var candidateOffset = 0;
            using var detailGate = new SemaphoreSlim(20);
            var dynamicColumn = await _dia.ResolveDynamicBranchColumnAsync(req.FirmaKodu, req.DonemKodu);

            async Task<(DiaErpIntegration.API.Models.DiaV3Json.DiaInvoiceListItem item, bool hasDistributableLine)> ScanDistributableAsync(
                DiaErpIntegration.API.Models.DiaV3Json.DiaInvoiceListItem it)
            {
                var cacheKey = $"{req.FirmaKodu}|{req.DonemKodu}|{it.Key}|{dynamicColumn}";
                if (_distributableDecisionCache.TryGetValue(cacheKey, out var hit) &&
                    (DateTimeOffset.UtcNow - hit.at).TotalMinutes < 5)
                {
                    return (it, hit.value);
                }

                await detailGate.WaitAsync();
                try
                {
                    // Bazı tenantlarda scf_fatura_kalemi_*_view servisleri yok (404).
                    // Liste ayrımı için tek güvenilir yol: fatura detayından kalemlerde dinamik şube alanını kontrol etmek.
                    var det = await _dia.GetInvoiceAsyncWithDonemFallback(req.FirmaKodu, req.DonemKodu, it.Key);
                    var has = InvoiceDistributableRules.InvoiceHasAnyDistributableLine(det.Lines, dynamicColumn);

                    _distributableDecisionCache[cacheKey] = (DateTimeOffset.UtcNow, has);
                    return (it, has);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "OnlyDistributable scan: detail fetch failed for invoiceKey={Key}", it.Key);
                    return (it, false);
                }
                finally
                {
                    detailGate.Release();
                }
            }

            while (distributableSeen < desiredEnd)
            {
                var page = await WithTimeout(_dia.GetInvoicesAsync(req.FirmaKodu, req.DonemKodu, filters, candidateBatch, candidateOffset), 15000);
                if (page.Count == 0) break;

                var scanned = await Task.WhenAll(page.Select(ScanDistributableAsync));
                foreach (var row in scanned)
                {
                    if (req.UstIslemTuruKey is > 0 && ustKeySet.Count > 0 && !ustKeySet.Contains(row.item.Key))
                        continue;
                    // filter predicate
                    var keep = req.OnlyDistributable ? row.hasDistributableLine : !row.hasDistributableLine;
                    if (!keep) continue;

                    if (distributableSeen >= req.Offset && resultSlice.Count < req.Limit)
                        resultSlice.Add(row.item);

                    distributableSeen++;
                    if (distributableSeen >= desiredEnd && resultSlice.Count >= req.Limit)
                        break;
                }

                candidateOffset += page.Count;
                if (page.Count < candidateBatch) break;
            }

            items = resultSlice;
            _logger.LogInformation("Invoice list filtered-scan returned count={Count} offset={Offset} limit={Limit} mode={Mode}",
                items.Count, req.Offset, req.Limit, req.OnlyDistributable ? "only_distributable" : "only_non_distributable");
            }
        }
        else
        {
            // Şube/depo post-filter uyguluyoruz; o yüzden burada "önceki sayfa"larda kalıp 0 göstermemek için
            // yeterli kayıt yakalayana kadar sayfaları ilerleterek topla.
            var collected = new List<DiaErpIntegration.API.Models.DiaV3Json.DiaInvoiceListItem>(req.Limit);
            var takeTarget = req.Limit;
            var offset = req.Offset;
            var fetchLimit = Math.Max(req.Limit, 200);
            var safetyPages = 0;

            while (collected.Count < takeTarget && safetyPages < 20)
            {
                var page = await WithTimeout(_dia.GetInvoicesAsync(req.FirmaKodu, req.DonemKodu, filters, fetchLimit, offset), 15000);
                if (page.Count == 0) break;
                if (req.UstIslemTuruKey is > 0)
                {
                    var ustKeySet = await _dia.GetInvoiceKeysByUstIslemTuruAsync(req.FirmaKodu, req.DonemKodu, filters, req.UstIslemTuruKey.Value) ?? new HashSet<long>();
                    if (ustKeySet.Count > 0)
                        collected.AddRange(page.Where(p => ustKeySet.Contains(p.Key)));
                    else
                        collected.AddRange(page);
                }
                else
                {
                    collected.AddRange(page);
                }
                offset += page.Count;
                safetyPages++;
                if (page.Count < fetchLimit) break;
            }

            items = collected.Take(req.Limit).ToList();
            _logger.LogInformation("Invoice list DIA returned count={Count} collected={Collected} pages={Pages}",
                items.Count, collected.Count, safetyPages);

            // Fallback test: filtreli istek 0 döndüyse (özellikle şube filtresi), filtresiz dene ve logla.
            if (items.Count == 0 && !string.IsNullOrWhiteSpace(filters))
            {
                var fallback = await _dia.GetInvoicesAsync(req.FirmaKodu, req.DonemKodu, string.Empty, req.Limit, req.Offset);
                _logger.LogWarning("Invoice list fallback (no filters) returned count={Count}. Original filters={Filters}", fallback.Count, filters);
            }
        }

        // Güvenilir şube/depo filtresi: bazı tenantlarda scf_fatura_listele filtre alanları farklı olabilir.
        // Bu yüzden, seçili key için şube/depo adını yetkili ağaçtan bulup sonuç listesini isim bazında daraltırız.
        // (Yanlış pozitifleri azaltmak için normalize + tam eşitlik.)
        static string Norm(string? s) =>
            string.IsNullOrWhiteSpace(s) ? string.Empty : s.Trim().ToUpperInvariant();

        if ((req.SourceSubeKey is > 0) || (req.SourceDepoKey is > 0))
        {
            try
            {
                var ctx = await _dia.GetAuthorizedCompanyPeriodBranchAsync();
                var company = ctx.FirstOrDefault(c => c.FirmaKodu == req.FirmaKodu);
                DiaErpIntegration.API.Models.DiaV3Json.DiaAuthorizedBranchItem? br = null;
                DiaErpIntegration.API.Models.DiaV3Json.DiaAuthorizedDepotItem? dp = null;

                if (company != null)
                {
                    if (req.SourceSubeKey is > 0)
                        br = company.Subeler.FirstOrDefault(s => s.Key == req.SourceSubeKey.Value);

                    if (req.SourceDepoKey is > 0)
                        dp = company.Subeler.SelectMany(s => s.Depolar).FirstOrDefault(d => d.Key == req.SourceDepoKey.Value);
                }

                // Bazı tenantlarda yetkili ağaçta şubeler/depolar boş gelebiliyor.
                // UI şube/depo filtreleri key bazlı çalıştığı için, key->ad eşlemesini firma bazlı listeden de deneyelim.
                if ((br == null && req.SourceSubeKey is > 0) || (dp == null && req.SourceDepoKey is > 0))
                {
                    var probeDonem = req.DonemKodu > 0 ? req.DonemKodu : 1;
                    var branchesFallback = await _dia.GetSubelerDepolarForFirmaAsync(req.FirmaKodu, probeDonem);
                    if (br == null && req.SourceSubeKey is > 0)
                        br = branchesFallback.FirstOrDefault(s => s.Key == req.SourceSubeKey.Value);
                    if (dp == null && req.SourceDepoKey is > 0)
                        dp = branchesFallback.SelectMany(s => s.Depolar ?? new List<DiaErpIntegration.API.Models.DiaV3Json.DiaAuthorizedDepotItem>())
                            .FirstOrDefault(d => d.Key == req.SourceDepoKey.Value);
                }

                if (br != null)
                {
                    var name = Norm(br.SubeAdi);
                    items = items.Where(i => Norm(i.SourceSubeAdi) == name).ToList();
                }
                if (dp != null)
                {
                    var name = Norm(dp.DepoAdi);
                    items = items.Where(i => Norm(i.SourceDepoAdi) == name).ToList();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Invoice list post-filter (sube/depo) failed.");
            }
        }

        var mapped = items.Select(i =>
        {
            var snap = _transfer.GetSourceInvoiceTransferSnapshot(i.Key);
            bool? hasLineBranch = req.OnlyDistributable ? true : req.OnlyNonDistributable ? false : null;
            var transferType = "FATURA";
            return i.ToApiDto(snap.status, snap.bekleyenKalemSayisi, hasLineBranch, transferType);
        }).ToList();
        return Ok(mapped);
    }

    [HttpGet("{key:long}")]
    public async Task<IActionResult> Get([FromRoute] long key, [FromQuery] int firmaKodu, [FromQuery] int donemKodu)
    {
        _logger.LogInformation("Invoice detail request: key={Key} firmaKodu={Firma} donemKodu={Donem}", key, firmaKodu, donemKodu);
        Exception? last = null;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                var inv = await WithTimeout(_dia.GetInvoiceAsyncWithDonemFallback(firmaKodu, donemKodu, key), 45000);
                var dto = inv.ToApiDto();
                dto.Kalemler = (inv.Lines ?? new List<DiaErpIntegration.API.Models.DiaV3Json.DiaInvoiceLine>())
                    .Select(l =>
                    {
                        var lineSnap = _transfer.GetSourceLineTransferSnapshot(key, l.Key);
                        return l.ToApiDto(lineSnap.status, lineSnap.targetFirmaKodu, lineSnap.targetSubeKodu, lineSnap.targetDonemKodu);
                    })
                    .ToList();

                if (dto.Kalemler.Count == 0)
                {
                    var rows = await WithTimeout(_dia.GetInvoiceLinesViewAsync(firmaKodu, donemKodu, key), 30000);
                    if (rows.Count > 0)
                    {
                        static string? S(System.Text.Json.JsonElement e, params string[] names)
                        {
                            foreach (var n in names)
                                if (e.ValueKind == System.Text.Json.JsonValueKind.Object && e.TryGetProperty(n, out var v) && v.ValueKind != System.Text.Json.JsonValueKind.Null)
                                    return v.ToString();
                            return null;
                        }
                        static long? L(System.Text.Json.JsonElement e, params string[] names)
                        {
                            foreach (var n in names)
                                if (e.ValueKind == System.Text.Json.JsonValueKind.Object && e.TryGetProperty(n, out var v))
                                {
                                    if (v.ValueKind == System.Text.Json.JsonValueKind.Number && v.TryGetInt64(out var n64)) return n64;
                                    if (v.ValueKind == System.Text.Json.JsonValueKind.String && long.TryParse(v.GetString(), out var p)) return p;
                                }
                            return null;
                        }
                        static decimal? D(System.Text.Json.JsonElement e, params string[] names)
                        {
                            foreach (var n in names)
                                if (e.ValueKind == System.Text.Json.JsonValueKind.Object && e.TryGetProperty(n, out var v))
                                {
                                    if (v.ValueKind == System.Text.Json.JsonValueKind.Number && v.TryGetDecimal(out var dec)) return dec;
                                    if (v.ValueKind == System.Text.Json.JsonValueKind.String && decimal.TryParse(v.GetString(), out var p)) return p;
                                }
                            return null;
                        }
                        static int? I(System.Text.Json.JsonElement e, params string[] names)
                        {
                            var l = L(e, names);
                            if (l.HasValue) return (int)l.Value;
                            return null;
                        }

                        dto.Kalemler = rows.Select(r =>
                        {
                            var lineKey = L(r, "_key", "key") ?? 0;
                            var snap = _transfer.GetSourceLineTransferSnapshot(key, lineKey);
                            var rawKdv = D(r, "kdvyuzde", "kdvorani", "kdvyuzdesi", "kdv");
                            var rawKdvTutari = D(r, "kdvtutari") ?? 0m;
                            var rawDur = (S(r, "kdvdurumu") ?? string.Empty).Trim().ToUpperInvariant();

                            // UI'da en çok görülen sorun:
                            // Kaynakta KDV tutarı 0 iken bazı satırlarda `kdv` alanı % değil (veya kart default'u),
                            // bu da %20 gibi yanlış görüntüye sebep oluyor. Transferdeki mantıkla hizala.
                            // Hariç/işlenmedi => %0 kabul et.
                            var kdvToShow = (rawKdvTutari == 0m && rawKdv.GetValueOrDefault() != 0m && (rawDur == "H" || rawDur == "I"))
                                ? 0m
                                : rawKdv;

                            return new DiaErpIntegration.API.Models.Api.InvoiceLineDto
                            {
                                Key = lineKey > 0 ? lineKey.ToString() : (S(r, "key") ?? ""),
                                SiraNo = I(r, "sirano", "sira"),
                                KalemTuru = I(r, "kalemturu", "turu"),
                                StokHizmetKodu = S(r, "stokhizmetkodu", "stokkodu", "kartkodu"),
                                StokHizmetAciklama = S(r, "stokhizmetaciklama", "aciklama"),
                                Birim = S(r, "birim", "birimkodu", "birimadi"),
                                Miktar = D(r, "miktar"),
                                BirimFiyati = D(r, "birimfiyati"),
                                SonBirimFiyati = D(r, "sonbirimfiyati"),
                                Tutari = D(r, "tutari"),
                                Kdv = kdvToShow,
                                KdvTutari = rawKdvTutari,
                                IndirimToplam = D(r, "indirimtoplam"),
                                DepoAdi = S(r, "depoadi"),
                                Note = S(r, "note"),
                                Note2 = S(r, "note2"),
                                ProjeKodu = S(r, "projekodu"),
                                ProjeAciklama = S(r, "projeaciklama"),
                                DinamikSubelerRaw = S(r, "__dinamik__1", "__dinamik__2", "__dinamik__00001", "__dinamik__00002"),
                                DinamikSubelerNormalized = null,
                                TransferStatus = snap.status,
                                TargetFirmaKodu = snap.targetFirmaKodu,
                                TargetSubeKodu = snap.targetSubeKodu,
                                TargetDonemKodu = snap.targetDonemKodu,
                            };
                        }).ToList();
                    }
                }

                // Eğer hem scf_fatura_getir hem de view fallback kalem döndürmüyorsa,
                // UI/transfer akışında "boş ama başarılı" gibi görünmesin; hata dön.
                if (dto.Kalemler == null || dto.Kalemler.Count == 0)
                    throw new InvalidOperationException($"DIA fatura kalemleri alınamadı. key={key} firma={firmaKodu} donem={donemKodu}");

                _logger.LogInformation("Invoice detail response: key={Key} m_kalemler_count={Count}", key, dto.Kalemler?.Count ?? 0);
                _lastDetailCache[$"{firmaKodu}|{donemKodu}|{key}"] = (DateTimeOffset.UtcNow, dto);
                return Ok(dto);
            }
            catch (Exception ex)
            {
                last = ex;
                _logger.LogWarning(ex, "Invoice detail attempt {Attempt} failed: key={Key} firma={Firma} donem={Donem}", attempt, key, firmaKodu, donemKodu);
                await Task.Delay(600);
            }
        }

        _logger.LogError(last, "Invoice detail failed after retries: key={Key} firma={Firma} donem={Donem}", key, firmaKodu, donemKodu);
        var ckey = $"{firmaKodu}|{donemKodu}|{key}";
        if (_lastDetailCache.TryGetValue(ckey, out var hit) && (DateTimeOffset.UtcNow - hit.at).TotalSeconds < 600)
        {
            _logger.LogWarning("Returning cached invoice detail due to DIA failure. cacheAgeSeconds={Age}", (DateTimeOffset.UtcNow - hit.at).TotalSeconds);
            return Ok(hit.dto);
        }

        return Problem(
            detail: last?.Message,
            statusCode: StatusCodes.Status502BadGateway,
            title: "Fatura detayı alınamadı");
    }

    [HttpGet("virman/{key:long}")]
    public async Task<IActionResult> GetVirman([FromRoute] long key, [FromQuery] int firmaKodu, [FromQuery] int donemKodu)
    {
        _logger.LogInformation("Virman detail request: key={Key} firmaKodu={Firma} donemKodu={Donem}", key, firmaKodu, donemKodu);
        try
        {
            var raw = await WithTimeout(_dia.GetVirmanAsync(firmaKodu, donemKodu, key), 20000);
            return Ok(raw);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Virman detail failed: key={Key} firmaKodu={Firma} donemKodu={Donem}", key, firmaKodu, donemKodu);
            return Problem(detail: ex.Message, statusCode: StatusCodes.Status502BadGateway, title: "Virman getirilemedi");
        }
    }

    [HttpPost("transfer")]
    public async Task<ActionResult<InvoiceTransferResultDto>> Transfer([FromBody] InvoiceTransferRequestDto? req, CancellationToken ct)
    {
        if (req is null)
            return UnprocessableEntity(new InvoiceTransferResultDto
            {
                Success = false,
                Message = "İstek gövdesi boş.",
                Errors = new List<string> { "body_null" },
                FailureStage = "validation",
                FailureCode = "body_null"
            });

        if (req.SourceInvoiceKey <= 0)
            return UnprocessableEntity(new InvoiceTransferResultDto
            {
                Success = false,
                Message = "Kaynak fatura anahtarı geçersiz.",
                Errors = new List<string> { "source_invoice_invalid" },
                FailureStage = "validation",
                FailureCode = "source_invoice_invalid"
            });

        // Hedef dönem/şube/depo frontend'den gelmeyebilir; backend otomatik çözer.
        if (req.TargetFirmaKodu <= 0)
            return UnprocessableEntity(new InvoiceTransferResultDto
            {
                Success = false,
                Message = "Hedef firma eksik.",
                Errors = new List<string> { "target_firma_missing" },
                FailureStage = "validation",
                FailureCode = "target_firma_missing"
            });

        // Kalem seçimi opsiyonel: toplu aktarımda kalem seçilmezse backend tüm kalemleri otomatik seçer.
        req.SelectedKalemKeys ??= new List<long>();

        _logger.LogInformation("Transfer endpoint request: sourceFirmaKodu={SourceFirma} sourceDonemKodu={SourceDonem} sourceSubeKey={SourceSube} sourceDepoKey={SourceDepo} sourceInvoiceKey={SourceInvoice} selectedKalemKeys={SelectedKalemKeys} targetFirmaKodu={TargetFirma} targetSubeKey={TargetSube} targetDepoKey={TargetDepo} targetDonemKodu={TargetDonem}",
            req.SourceFirmaKodu, req.SourceDonemKodu, req.SourceSubeKey, req.SourceDepoKey, req.SourceInvoiceKey, (req.SelectedKalemKeys.Count == 0 ? "<empty>" : string.Join(",", req.SelectedKalemKeys)),
            req.TargetFirmaKodu, req.TargetSubeKey, req.TargetDepoKey, req.TargetDonemKodu);

        try
        {
            var res = await _transfer.TransferAsync(req, ct);
            if (!res.Success) return UnprocessableEntity(res);
            return Ok(res);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Transfer endpoint unhandled exception.");
            var traceId = HttpContext.TraceIdentifier;
            var err = new InvoiceTransferResultDto
            {
                Success = false,
                Message = "Transfer sırasında beklenmeyen hata oluştu.",
                Details = ex.Message,
                DiaPayload = req is null ? null : req,
                DiaResponse = ex.ToString(),
                TraceId = traceId,
                Errors = new List<string> { ex.Message }
            };
            return StatusCode(500, err);
        }
    }
}

