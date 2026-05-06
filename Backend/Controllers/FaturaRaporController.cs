using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using DiaErpIntegration.API.Models.Api;
using DiaErpIntegration.API.Options;
using DiaErpIntegration.API.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace DiaErpIntegration.API.Controllers;

[ApiController]
[Route("api")]
public sealed class FaturaRaporController : ControllerBase
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTimeOffset at, List<JsonElement> rows)> _cache = new();
    private static bool CacheFresh(DateTimeOffset at) =>
        at != DateTimeOffset.MinValue && (DateTimeOffset.UtcNow - at).TotalSeconds < 300; // TTL=300sn (5dk)

    private readonly IDiaWsClient _dia;
    private readonly DiaOptions _opt;
    private readonly ILogger<FaturaRaporController> _logger;

    public FaturaRaporController(IDiaWsClient dia, IOptions<DiaOptions> opt, ILogger<FaturaRaporController> logger)
    {
        _dia = dia;
        _opt = opt.Value;
        _logger = logger;
    }

    [HttpPost("fatura-getir")]
    public async Task<IActionResult> FaturaGetir([FromBody] FaturaGetirRequestDto? filtre, CancellationToken ct)
    {
        filtre ??= new FaturaGetirRequestDto();

        // Firma/Dönem burada FILTRE değil; WS bağlantı context'i.
        var reqFirma = filtre.firma_kodu ?? 0;
        var firmaKodu = (reqFirma > 0)
            ? reqFirma
            : (_opt.DefaultSourceFirmaKodu > 0 ? _opt.DefaultSourceFirmaKodu : _opt.PoolFirmaKodu);
        var reqDonem = filtre.donem_kodu ?? 0;
        var donemKodu = (reqDonem > 0)
            ? reqDonem
            : _opt.DefaultSourceDonemKodu; // 0/boş bırakılırsa DIA öntanımlıyı seçebilir
        var reportCode = string.IsNullOrWhiteSpace(filtre.report_code) ? "RPR000000004" : filtre.report_code!.Trim();

        var kaynakSube = filtre.kaynak_sube ?? 0;
        var kaynakDepo = filtre.kaynak_depo ?? 0;

        // DİA RPR bazı tenant'larda tüm parametreleri zorunlu ister (eksik gelince 501).
        // Bu yüzden parametreleri her zaman eksiksiz göndeririz (boş/null yerine default).
        // RPR SQL şablonu: genelde scf_fatura._level1 = secilifirma (firma), _level2 = secilidonem (dönem).
        // Yer tutucular: {firma_kodu}/{donem_kodu} ile {secilifirma}/{secilidonem} aynı sayısal değerlerle doldurulur.
        var param = new Dictionary<string, object?>
        {
            ["firma_kodu"] = firmaKodu,
            ["donem_kodu"] = donemKodu,
            ["secilifirma"] = firmaKodu,
            ["secilidonem"] = donemKodu,
            ["baslangic"] = filtre.baslangic ?? string.Empty,
            ["bitis"] = filtre.bitis ?? string.Empty,
            // Kritik: RPR parametresi tenant'a göre değişir.
            // Bazı raporlarda "Hepsi" boş string ile temsil edilir (TUM/DAGIT gibi sabit değerler ayrı seçeneklerdir).
            // Bu yüzden boş değeri zorla "TUM" yapmayız; geleni aynen geçiririz.
            ["fatura_tipi"] = filtre.fatura_tipi ?? string.Empty,
            ["kaynak_sube"] = kaynakSube,
            ["kaynak_depo"] = kaynakDepo,
            ["ust_islem"] = string.IsNullOrWhiteSpace(filtre.ust_islem) ? "TUM" : filtre.ust_islem!,
            ["cari_adi"] = filtre.cari_adi ?? string.Empty,
            ["fatura_no"] = filtre.fatura_no ?? string.Empty,
            ["fatura_turu"] = filtre.fatura_turu ?? string.Empty,
            ["kalem_sube"] = filtre.kalem_sube ?? string.Empty,
        };

        // Cache anahtarı (kritik): aynı filtre tekrar gelirse WS'e gitmesin.
        // Production: normalize + sabit property sırası + JSON serialize ile stabil key üret.
        static string NormTrim(string? s) => (s ?? string.Empty).Trim();

        var cacheKey = JsonSerializer.Serialize(new
        {
            firma_kodu = firmaKodu,
            donem_kodu = donemKodu,
            report_code = reportCode,
            baslangic = NormTrim(filtre.baslangic),
            bitis = NormTrim(filtre.bitis),
            fatura_tipi = NormTrim(filtre.fatura_tipi),
            kaynak_sube = kaynakSube,
            kaynak_depo = kaynakDepo,
            ust_islem = NormTrim(filtre.ust_islem) == string.Empty ? "TUM" : NormTrim(filtre.ust_islem),
            cari_adi = NormTrim(filtre.cari_adi),
            fatura_no = NormTrim(filtre.fatura_no),
            fatura_turu = NormTrim(filtre.fatura_turu),
            kalem_sube = NormTrim(filtre.kalem_sube),
            force_refresh = filtre.force_refresh == true,
        });

        var bypassCache = filtre.force_refresh == true;
        if (!bypassCache && _cache.TryGetValue(cacheKey, out var hit) && CacheFresh(hit.at))
        {
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(cacheKey)));
            _logger.LogInformation("RPR cache hit. cacheKeyHash={Hash} ageSeconds={Age}", hash, (DateTimeOffset.UtcNow - hit.at).TotalSeconds);
            return Ok(new { success = true, data = hit.rows });
        }

        try
        {
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(cacheKey)));
            _logger.LogInformation("RPR cache miss. Calling WS rpr_raporsonuc_getir. cacheKeyHash={Hash}", hash);
            _logger.LogInformation("RPR çağrısı başlıyor. Parametreler: {@request}", filtre);
            var rows = await _dia.GetRprReportRowsAsync(
                firmaKodu,
                donemKodu,
                reportCode: reportCode,
                param: param,
                cancellationToken: ct);

            // Debug: RPR kolon adları tenant/rapor alanlarına göre değişebiliyor.
            // İlk satırın alan adlarını logla ki frontend mapping'i birebir uyarlayabilelim.
            try
            {
                if (rows.Count > 0 && rows[0].ValueKind == JsonValueKind.Object)
                {
                    var keys = rows[0].EnumerateObject().Select(p => p.Name).OrderBy(x => x).ToList();
                    var excerpt = string.Join(", ", keys.Take(80));
                    _logger.LogInformation("RPR first-row keys (first 80): {Keys}", excerpt);

                    static string GetStr(JsonElement obj, params string[] names)
                    {
                        foreach (var n in names)
                        {
                            if (obj.TryGetProperty(n, out var v))
                                return v.ToString();
                        }
                        return string.Empty;
                    }
                    var fisno = GetStr(rows[0], "fisno", "FISNO", "fis_no", "FIS_NO");
                    var belgeno2 = GetStr(rows[0], "belgeno2", "BELGENO2", "fatura_no", "FATURA_NO");
                    var tarih = GetStr(rows[0], "tarih", "TARIH");
                    var subeSrc = GetStr(rows[0], "_key_sis_sube_source", "_KEY_SIS_SUBE_SOURCE", "key_sis_sube_source", "KEY_SIS_SUBE_SOURCE");
                    var depoSrc = GetStr(rows[0], "_key_sis_depo_source", "_KEY_SIS_DEPO_SOURCE", "key_sis_depo_source", "KEY_SIS_DEPO_SOURCE");
                    _logger.LogInformation("RPR first-row sample: fisno={FisNo} belgeno2={BelgeNo2} tarih={Tarih} subeSrc={SubeSrc} depoSrc={DepoSrc}",
                        fisno, belgeno2, tarih, subeSrc, depoSrc);
                }
            }
            catch { /* ignore logging failures */ }

            // Cache memory koruma: sınırsız büyümesin.
            if (_cache.Count > 500)
            {
                // Komple clear yerine en eski kayıtları sil (FIFO / timestamp'e göre).
                // ConcurrentDictionary sıralı değil; snapshot alıp en eski N anahtarı kaldırırız.
                var before = _cache.Count;
                var removeCount = Math.Min(100, Math.Max(1, before - 450)); // hedef: ~450 civarına düş
                var oldest = _cache
                    .OrderBy(kv => kv.Value.at)
                    .Take(removeCount)
                    .Select(kv => kv.Key)
                    .ToList();
                var removed = 0;
                foreach (var k in oldest)
                {
                    if (_cache.TryRemove(k, out _)) removed++;
                }
                _logger.LogWarning("RPR cache trimmed. before={Before} removed={Removed} after={After}",
                    before, removed, _cache.Count);
            }
            _cache[cacheKey] = (DateTimeOffset.UtcNow, rows);
            return Ok(new
            {
                success = true,
                data = rows
            });
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("decode", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(ex, "RPR decode failed. Returning 400.");
            return BadRequest(new { success = false, message = "Rapor verisi decode edilemedi" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DIA WS HATA (RPR endpoint)");
            return StatusCode(500, new
            {
                success = false,
                message = ex.Message,
                detail = ex.StackTrace
            });
        }
    }
}

