using System.Collections.Concurrent;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO;
using DiaErpIntegration.API.Models.Api;
using DiaErpIntegration.API.Models;
using DiaErpIntegration.API.Models.DiaV3Json;
using DiaErpIntegration.API.Options;
using Microsoft.Extensions.Options;

namespace DiaErpIntegration.API.Services;

public sealed class InvoiceTransferService
{
    private static readonly System.Threading.AsyncLocal<List<Models.Api.InvoiceSkippedExtraFieldDto>?> _skippedExtraFields = new();

    private static List<Models.Api.InvoiceSkippedExtraFieldDto> GetSkippedListOrCreate()
    {
        if (_skippedExtraFields.Value == null) _skippedExtraFields.Value = new List<Models.Api.InvoiceSkippedExtraFieldDto>();
        return _skippedExtraFields.Value!;
    }
    // Mükerrer aktarım engeli (memory dedup, 24h TTL):
    // Aynı kaynak fatura aynı hedef bağlamına tekrar gönderilemez.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTimeOffset at, long createdKey)> _dedup = new();

    private static string DedupKey(InvoiceTransferRequestDto req)
        => $"{req.SourceFirmaKodu}|{req.SourceDonemKodu}|{req.SourceInvoiceKey}|{req.TargetFirmaKodu}|{req.TargetDonemKodu}|{req.TargetSubeKey}|{req.TargetDepoKey}";

    private static bool DedupFresh(DateTimeOffset at)
        => at != DateTimeOffset.MinValue && (DateTimeOffset.UtcNow - at).TotalHours < 24;
    private static readonly ConcurrentDictionary<string, byte> _duplicateRegistry = new();
    private static readonly ConcurrentDictionary<long, ConcurrentDictionary<long, byte>> _sourceTransferredLines = new();
    private static readonly ConcurrentDictionary<long, int> _sourceInvoiceLineTotals = new();
    private static readonly ConcurrentDictionary<string, SourceLineTargetContext> _sourceLineTargets = new();
    private static readonly ConcurrentDictionary<string, int> _fisnoCounters = new();
    private static readonly ConcurrentDictionary<string, byte> _fisnoInitialized = new();

    /// <summary>Kaynak fatura detayı — anahtar yalnızca invoiceKey (DİA tarafında benzersiz kabul).</summary>
    private static readonly ConcurrentDictionary<long, (DateTimeOffset at, DiaInvoiceDetail detail)> _transferSourceDetailCache = new();
    private static readonly TimeSpan _transferSourceDetailTtl = TimeSpan.FromMinutes(3);

    private static readonly ConcurrentDictionary<string, (DateTimeOffset at, (string? kodu, string? unvan) info)> _transferCariInfoByKeyCache = new();
    private static readonly ConcurrentDictionary<string, (DateTimeOffset at, (string? cariKodu, string? cariUnvan, long? cariKey) info)> _transferInvoiceCariListCache = new();
    private static readonly TimeSpan _transferCariCacheTtl = TimeSpan.FromMinutes(15);

    private static string TransferCariInfoKey(int firma, int donem, long cariKey) => $"{firma}|{donem}|cari|{cariKey}";
    private static string TransferInvoiceCariListKey(int firma, int donem, long invoiceKey) => $"{firma}|{donem}|fatura|{invoiceKey}";

    private static readonly object _stateFileGate = new();
    private static readonly string _stateFilePath = Path.Combine(AppContext.BaseDirectory, "transfer-state.json");

    static InvoiceTransferService()
    {
        LoadStateFromFile();
    }

    private sealed class TransferStateSnapshot
    {
        public List<string> DuplicateKeys { get; set; } = new();
        public Dictionary<long, List<long>> SourceTransferredLines { get; set; } = new();
        public Dictionary<long, int> SourceInvoiceLineTotals { get; set; } = new();
        public Dictionary<string, SourceLineTargetContextSnapshot> SourceLineTargets { get; set; } = new();
        public Dictionary<string, int> FisnoCounters { get; set; } = new();
    }

    private sealed class SourceLineTargetContextSnapshot
    {
        public string? TargetFirmaKodu { get; set; }
        public string? TargetSubeKodu { get; set; }
        public string? TargetDonemKodu { get; set; }
    }

    private static void LoadStateFromFile()
    {
        try
        {
            if (!File.Exists(_stateFilePath)) return;
            var json = File.ReadAllText(_stateFilePath);
            if (string.IsNullOrWhiteSpace(json)) return;

            var snapshot = JsonSerializer.Deserialize<TransferStateSnapshot>(json);
            if (snapshot is null) return;

            foreach (var k in snapshot.DuplicateKeys)
                _duplicateRegistry.TryAdd(k, 1);

            foreach (var outer in snapshot.SourceTransferredLines)
            {
                var inner = _sourceTransferredLines.GetOrAdd(outer.Key, _ => new ConcurrentDictionary<long, byte>());
                foreach (var lineKey in outer.Value)
                    inner.TryAdd(lineKey, 1);
            }

            foreach (var kv in snapshot.SourceInvoiceLineTotals)
                _sourceInvoiceLineTotals.TryAdd(kv.Key, kv.Value);

            foreach (var kv in snapshot.SourceLineTargets)
            {
                var ctx = kv.Value;
                _sourceLineTargets.TryAdd(kv.Key, new SourceLineTargetContext
                {
                    TargetFirmaKodu = ctx.TargetFirmaKodu,
                    TargetSubeKodu = ctx.TargetSubeKodu,
                    TargetDonemKodu = ctx.TargetDonemKodu
                });
            }

            foreach (var kv in snapshot.FisnoCounters)
                _fisnoCounters[kv.Key] = kv.Value;
        }
        catch
        {
            // fail-safe: state dosyası bozuksa uygulama çalışmaya devam eder.
        }
    }

    private static void SaveStateToFile()
    {
        try
        {
            lock (_stateFileGate)
            {
                var snapshot = new TransferStateSnapshot
                {
                    DuplicateKeys = _duplicateRegistry.Keys.ToList(),
                    SourceTransferredLines = _sourceTransferredLines.ToDictionary(
                        outer => outer.Key,
                        outer => outer.Value.Keys.ToList()),
                    SourceInvoiceLineTotals = _sourceInvoiceLineTotals.ToDictionary(kv => kv.Key, kv => kv.Value),
                    SourceLineTargets = _sourceLineTargets.ToDictionary(
                        kv => kv.Key,
                        kv => new SourceLineTargetContextSnapshot
                        {
                            TargetFirmaKodu = kv.Value.TargetFirmaKodu,
                            TargetSubeKodu = kv.Value.TargetSubeKodu,
                            TargetDonemKodu = kv.Value.TargetDonemKodu
                        }),
                    FisnoCounters = _fisnoCounters.ToDictionary(kv => kv.Key, kv => kv.Value),
                };

                Directory.CreateDirectory(Path.GetDirectoryName(_stateFilePath)!);
                var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_stateFilePath, json);
            }
        }
        catch
        {
            // fail-safe: kalıcılık yapılamazsa da transfer akışı etkilenmez.
        }
    }

    /// <summary><see cref="DiaOptions.TransferDisableDedup"/> açıkken kalıcı state ve disk yazımı yok.</summary>
    private void MaybeSaveTransferState()
    {
        if (_opt.TransferDisableDedup) return;
        SaveStateToFile();
    }

    private readonly IDiaWsClient _dia;
    private readonly ILogger<InvoiceTransferService> _logger;
    private readonly DiaOptions _opt;

    public InvoiceTransferService(IDiaWsClient dia, ILogger<InvoiceTransferService> logger, IOptions<DiaOptions> opt)
    {
        _dia = dia;
        _logger = logger;
        _opt = opt.Value;
    }

    private async Task<(string? kodu, string? unvan)> GetCariInfoByKeyCachedAsync(int firmaKodu, int donemKodu, long cariKey, CancellationToken ct = default)
    {
        var k = TransferCariInfoKey(firmaKodu, donemKodu, cariKey);
        if (_transferCariInfoByKeyCache.TryGetValue(k, out var hit) && (DateTimeOffset.UtcNow - hit.at) < _transferCariCacheTtl)
            return hit.info;
        var info = await _dia.GetCariInfoByKeyAsync(firmaKodu, donemKodu, cariKey);
        _transferCariInfoByKeyCache[k] = (DateTimeOffset.UtcNow, info);
        return info;
    }

    private async Task<(string? cariKodu, string? cariUnvan, long? cariKey)> GetInvoiceCariFromListCachedAsync(int firmaKodu, int donemKodu, long invoiceKey, CancellationToken ct = default)
    {
        var k = TransferInvoiceCariListKey(firmaKodu, donemKodu, invoiceKey);
        if (_transferInvoiceCariListCache.TryGetValue(k, out var hit) && (DateTimeOffset.UtcNow - hit.at) < _transferCariCacheTtl)
            return hit.info;
        var info = await _dia.GetInvoiceCariFromListAsync(firmaKodu, donemKodu, invoiceKey);
        _transferInvoiceCariListCache[k] = (DateTimeOffset.UtcNow, info);
        return info;
    }

    public async Task<InvoiceTransferResultDto> TransferAsync(InvoiceTransferRequestDto req, CancellationToken ct = default)
    {
        _skippedExtraFields.Value = new List<Models.Api.InvoiceSkippedExtraFieldDto>();
        var result = new InvoiceTransferResultDto { Success = false };

        if (req.SourceInvoiceKey <= 0) return Fail("validation", "source_invoice_invalid", "sourceInvoiceKey boş/geçersiz");
        if (req.TargetFirmaKodu <= 0) return Fail("validation", "target_firma_missing", "targetFirmaKodu boş/geçersiz");

        // Ham _opt loglanmaz: Password/ApiKey sızmasın. Yapı aynı fikir: {@Settings} + kritik switch.
        _logger.LogError("CONFIG RAW DiaSettings (sanitized): {@Settings}", DiaSettingsForDebugLog(_opt));

        if (_opt.TransferRawMode)
        {
            _logger.LogWarning("TRANSFER RAW MODE (zero-read / hedef: yalnız scf_fatura_ekle) invoiceKey={Key}", req.SourceInvoiceKey);
            return await TransferRawAsync(req, ct);
        }

        var dkey = DedupKey(req);
        if (!_opt.TransferDisableDedup && _dedup.TryGetValue(dkey, out var dhit) && DedupFresh(dhit.at))
        {
            return Fail("validation", "duplicate_transfer_blocked",
                $"Bu fatura bu hedefe daha önce aktarıldı. sourceInvoiceKey={req.SourceInvoiceKey} createdInvoiceKey={dhit.createdKey}");
        }

        _logger.LogInformation("Transfer request: selectedInvoiceKey={Invoice} selectedKalemKeys={KalemKeys} targetFirma={Firma} targetDonem={Donem} targetSube={Sube} targetDepo={Depo}",
            req.SourceInvoiceKey, string.Join(",", req.SelectedKalemKeys), req.TargetFirmaKodu, req.TargetDonemKodu, req.TargetSubeKey, req.TargetDepoKey);

        _logger.LogWarning(
            "TRANSFER runtime config: TransferRequireSnapshot={TransferRequireSnapshot} | invalid snapshot → legacy only when this is false",
            _opt.TransferRequireSnapshot);

        if (req.HeaderSnapshot != null && req.SelectedLineSnapshots is { Count: > 0 })
        {
            NormalizeRawHeaderBusinessDefaults(req);
            NormalizeRawSnapshotLineKdv(req);
        }

        var snapshotOk = IsValidSnapshot(req, out var snapshotReject);
        _logger.LogWarning(
            "SNAPSHOT CHECK invoiceKey={Key} headerNull={HeaderNull} lineCount={LineCount} requireSnapshot={Require} validSnapshot={Valid} snapshotReject={Reject}",
            req.SourceInvoiceKey,
            req.HeaderSnapshot == null,
            req.SelectedLineSnapshots?.Count ?? 0,
            _opt.TransferRequireSnapshot,
            snapshotOk,
            snapshotReject ?? "-");

        if (snapshotOk)
        {
            _logger.LogWarning("SNAPSHOT PATH ACTIVE invoiceKey={Key}", req.SourceInvoiceKey);
            return await TransferFromSnapshotAsync(req, ct);
        }

        // Geçersiz veya eksik snapshot: katı modda reddet; aksi halde DİA'dan scf_fatura_getir ile legacy yol.
        if (!snapshotOk && HasSnapshotIntent(req) && _opt.TransferRequireSnapshot)
            return Fail("validation", "snapshot_invalid",
                $"Snapshot geçersiz (invoice={req.SourceInvoiceKey}). {snapshotReject ?? "Alan eksik veya boş."}");

        if (!snapshotOk && !HasSnapshotIntent(req) && _opt.TransferRequireSnapshot)
            return Fail("validation", "snapshot_required",
                $"SNAPSHOT ZORUNLU — eksik payload (invoice={req.SourceInvoiceKey}). headerSnapshot ve dolu selectedLineSnapshots gönderin; scf_fatura_getir yolu kapalı.");

        if (!snapshotOk && HasSnapshotIntent(req))
            _logger.LogWarning(
                "SNAPSHOT geçersiz/eksik — legacy aktarım denenecek (TransferRequireSnapshot=false). invoice={Key} reject={Reject}",
                req.SourceInvoiceKey, snapshotReject ?? "-");

        _logger.LogWarning(
            "LEGACY TRANSFER PATH (scf_fatura_getir / cache / fallback) invoiceKey={Key}",
            req.SourceInvoiceKey);

        // Kaynak fatura: TTL içinde her zaman cache (lines boş bile); sonra en fazla 3× aynı dönem; çoklu dönem sadece sourceDonemKodu<=0 iken max 3 dönem.
        DiaInvoiceDetail src;
        {
            if (_transferSourceDetailCache.TryGetValue(req.SourceInvoiceKey, out var hit)
                && (DateTimeOffset.UtcNow - hit.at) < _transferSourceDetailTtl)
            {
                src = hit.detail;
                _logger.LogInformation("Transfer source invoice from memory cache: invoice={Invoice} lines={Lines}",
                    req.SourceInvoiceKey, src.Lines?.Count ?? 0);
            }
            else
            {
                const int maxNormalAttempts = 3;
                DiaInvoiceDetail? last = null;
                for (var i = 1; i <= maxNormalAttempts; i++)
                {
                    last = await _dia.GetInvoiceAsync(req.SourceFirmaKodu, req.SourceDonemKodu, req.SourceInvoiceKey);
                    if (last.Lines is { Count: > 0 })
                        break;
                    if (i < maxNormalAttempts)
                        await Task.Delay(400, ct);
                }

                src = last ?? new DiaInvoiceDetail { Key = req.SourceInvoiceKey, Lines = new List<DiaInvoiceLine>() };

                if (src.Lines == null || src.Lines.Count == 0)
                {
                    if (!_opt.TransferDisableLegacyFallback && req.SourceDonemKodu <= 0)
                    {
                        _logger.LogWarning(
                            "scf_fatura_getir boş kalem; sınırlı dönem fallback (max 3 WS). invoice={Invoice} firma={Firma}",
                            req.SourceInvoiceKey, req.SourceFirmaKodu);
                        src = await _dia.GetInvoiceAsyncWithLimitedDonemFallback(req.SourceFirmaKodu, req.SourceDonemKodu, req.SourceInvoiceKey, maxPeriodAttempts: 3);
                    }
                    else
                    {
                        _logger.LogWarning(
                            "scf_fatura_getir boş kalem; sourceDonemKodu>0 olduğu için çoklu dönem taraması atlandı. invoice={Invoice} donem={Donem}",
                            req.SourceInvoiceKey, req.SourceDonemKodu);
                    }
                }
            }
        }

        // Bazı anlarda DIA scf_fatura_getir null/boş dönebiliyor. Seçili kalemleri bulabilmek için line-view ile toparla.
        if (!_opt.TransferDisableLegacyFallback && (src.Lines == null || src.Lines.Count == 0) && req.SelectedKalemKeys.Count > 0)
        {
            try
            {
                var viewRows = await _dia.GetInvoiceLinesViewAsync(req.SourceFirmaKodu, req.SourceDonemKodu, req.SourceInvoiceKey);
                if (viewRows.Count > 0)
                {
                    var recovered = new List<DiaInvoiceLine>();
                    foreach (var r in viewRows)
                    {
                        if (r.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                        var line = System.Text.Json.JsonSerializer.Deserialize<DiaInvoiceLine>(r.GetRawText());
                        if (line != null && line.Key > 0) recovered.Add(line);
                    }

                    if (recovered.Count > 0)
                    {
                        src.Lines = recovered;
                        _logger.LogWarning(
                            "Recovered source invoice lines from view fallback. sourceInvoiceKey={Invoice} viewRowCount={ViewCount} recoveredCount={Recovered}",
                            req.SourceInvoiceKey, viewRows.Count, recovered.Count);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Invoice line view fallback failed. sourceInvoiceKey={Invoice}", req.SourceInvoiceKey);
            }
        }

        _transferSourceDetailCache[req.SourceInvoiceKey] = (DateTimeOffset.UtcNow, src);

        // UX: Kalem seçilmemişse "tüm kalemleri aktar" (toplu seçimde kalem seçmeden transfer için).
        if (req.SelectedKalemKeys.Count == 0)
        {
            var allKeys = (src.Lines ?? new List<DiaInvoiceLine>())
                .Where(l => l != null && l.Key > 0)
                .Select(l => l.Key)
                .Distinct()
                .ToList();

            if (allKeys.Count == 0)
                return Fail("validation", "source_lines_empty", "Kaynak faturada kalem bulunamadı (scf_fatura_getir boş).");

            req.SelectedKalemKeys = allKeys;
            _logger.LogInformation(
                "Transfer auto-selected all lines (no selection provided). sourceInvoiceKey={Invoice} count={Count} keysFirst={First}",
                req.SourceInvoiceKey, allKeys.Count, string.Join(",", allKeys.Take(20)));
        }

        var targetCtx = await ResolveTargetContextAsync(req, src);
        if (!targetCtx.ok)
            return Fail(targetCtx.stage, targetCtx.code, targetCtx.message);

        req.TargetDonemKodu = targetCtx.targetDonemKodu;
        req.TargetSubeKey = targetCtx.targetSubeKey;
        req.TargetDepoKey = targetCtx.targetDepoKey;

        var srcLines = (src.Lines ?? new List<DiaInvoiceLine>())
            .Where(l => req.SelectedKalemKeys.Contains(l.Key))
            .ToList();
        // Bu proje için dinamik kalem şube alanı tek: __dinamik__fatsube
        const string dynamicColumn = "__dinamik__fatsube";

        if (srcLines.Count == 0)
        {
            // Fallback: bazı UI akışlarında satır anahtarı yerine sıra no taşınmış olabilir.
            var bySiraNo = (src.Lines ?? new List<DiaInvoiceLine>())
                .Where(l => l.SiraNo.HasValue && req.SelectedKalemKeys.Contains(l.SiraNo.Value))
                .ToList();
            if (bySiraNo.Count > 0)
            {
                _logger.LogWarning(
                    "source_lines_not_found recovered by siraNo fallback. sourceInvoiceKey={Invoice} selected={Selected} matchedCount={Count}",
                    req.SourceInvoiceKey, string.Join(",", req.SelectedKalemKeys), bySiraNo.Count);
                srcLines = bySiraNo;
            }
        }

        if (srcLines.Count == 0 && req.SelectedLineSnapshots is { Count: > 0 })
        {
            var recovered = RecoverSourceLinesByCompositeSnapshot(src.Lines ?? new List<DiaInvoiceLine>(), req.SelectedLineSnapshots);
            if (recovered.Count > 0)
            {
                _logger.LogWarning(
                    "source_lines_not_found recovered by composite snapshot fallback. sourceInvoiceKey={Invoice} snapshotCount={SnapshotCount} matchedCount={Count}",
                    req.SourceInvoiceKey, req.SelectedLineSnapshots.Count, recovered.Count);
                srcLines = recovered;
            }
        }

        if (srcLines.Count == 0)
        {
            var available = (src.Lines ?? new List<DiaInvoiceLine>())
                .Select(l => $"{l.Key}(sira={l.SiraNo?.ToString() ?? "-"})")
                .Take(40)
                .ToList();
            _logger.LogWarning(
                "source_lines_not_found: sourceInvoiceKey={Invoice} selected={Selected} availableFirst={Available}",
                req.SourceInvoiceKey, string.Join(",", req.SelectedKalemKeys), string.Join(",", available));
            return Fail("validation", "source_lines_not_found", "Seçilen kalemler kaynak faturada bulunamadı.");
        }

        // scf_fatura_getir'de __carikartkodu boş; kod _key + lists/cari_hesap veya cari_kodu alanında olabiliyor.
        // BuildTargetCardAsync ile aynı zenginleştirme; aksi halde "cari_kodu" validasyonu yersiz yere keser.
        await EnrichSourceHeaderCariIfMissingAsync(req, src, ct);

        // Bazı tenantlarda scf_fatura_getir BelgeNo/BelgeNo2 boş dönebiliyor ama RPR'de fatura_no var.
        // Snapshot gönderildiyse, legacy validasyonda "fatura_no eksik" kesmemek için header snapshot'tan doldur.
        if (string.IsNullOrWhiteSpace(src.BelgeNo) && string.IsNullOrWhiteSpace(src.BelgeNo2))
        {
            var snapNo = (req.HeaderSnapshot?.InvoiceNo ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(snapNo))
                src.BelgeNo2 = snapNo;
        }

        // Zorunlu alan validasyonu (kontör optimizasyonu):
        // Eksikse hedef eşleştirme/ekleme adımlarına girmeden hızlıca kes.
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(src.FisNo)) missing.Add("fisno");
        if (string.IsNullOrWhiteSpace(src.BelgeNo) && string.IsNullOrWhiteSpace(src.BelgeNo2)) missing.Add("fatura_no");
        if (string.IsNullOrWhiteSpace(src.Tarih)) missing.Add("tarih");
        if (string.IsNullOrWhiteSpace(src.Saat)) missing.Add("saat");
        var cariKod = (GetResolvedSourceCariKod(src) ?? "").Trim();
        if (string.IsNullOrWhiteSpace(cariKod)) missing.Add("cari_kodu");

        foreach (var l in srcLines)
        {
            var kod = (l.KalemRef?.StokKartKodu ?? l.KalemRef?.HizmetKartKodu ?? "").Trim();
            if (string.IsNullOrWhiteSpace(kod))
            {
                missing.Add("stok_hizmet_kodu");
                break;
            }
        }

        if (missing.Count > 0)
            return Fail("validation", "required_fields_missing",
                $"Kaynak faturada zorunlu alan(lar) eksik: {string.Join(", ", missing.Distinct())}");

        // İş kuralı (revize): Her zaman FATURA oluştur.
        // Kalem üzerinde dinamik şube seçimi varsa sadece hedef şube eşleştirmesinde kullanılır.
        result.CreatedTargetType = "FATURA";

        // Kalem dinamik şube iş kuralı:
        // - Dağıtılacak(Kalem) modunda: dinamik şube doluysa hedef şube O şube olmalı (çelişemez).
        // - Tüm Faturalar modunda: dinamik şube ignore edilir (hedef şube/depo kullanıcı seçimi).
        if (req.UseDynamicBranch != false)
        {
            var dynBranches = srcLines
                .Select(l => NormalizeDynamicBranch(l.ExtractDinamikSubelerRaw(dynamicColumn)))
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (dynBranches.Count > 0)
            {
            if (dynBranches.Count > 1)
                return Fail("validation", "dynamic_branch_multiple", $"Seçili kalemlerde birden fazla şube var ({string.Join(", ", dynBranches)}). Aynı anda aktarım için tek şube seçili kalemleri birlikte seçin.");

            var desired = dynBranches[0];

            static string Canon(string s) => s.Trim().ToUpperInvariant()
                .Replace("İ", "I").Replace("İ", "I")
                .Replace("Ş", "S").Replace("Ğ", "G").Replace("Ü", "U").Replace("Ö", "O").Replace("Ç", "C");

            var targetBranches = await _dia.GetSubelerDepolarForFirmaAsync(req.TargetFirmaKodu, req.TargetDonemKodu);
            var desiredCanon = Canon(desired);
            var match = targetBranches.FirstOrDefault(b => Canon(b.SubeAdi) == desiredCanon)
                        ?? targetBranches.FirstOrDefault(b => Canon(b.SubeAdi).Contains(desiredCanon))
                        ?? targetBranches.FirstOrDefault(b => desiredCanon.Contains(Canon(b.SubeAdi)));

            // Sıkı kural: dinamik şube adı hedefte bulunmalı ve hedef şube seçimiyse birebir aynı olmalı.
            // Aksi halde kalem farklı şubeye aktarılmış olur (istenmeyen).
            if (match == null || match.Key <= 0)
                return Fail("resolve_target", "target_branch_not_found_for_dynamic",
                    $"Kalem şubesi hedef firmada bulunamadı. Kalem şubesi='{desired}'. Hedef firmada şubeler: {string.Join(", ", targetBranches.Take(10).Select(b => b.SubeAdi))}");

            if (req.TargetSubeKey > 0 && req.TargetSubeKey != match.Key)
                return Fail("validation", "target_branch_conflict",
                    $"Kalem şubesi='{desired}' ancak hedef şube seçimi farklı. Kalem şubesi ile aynı şubeyi seçin.");

            req.TargetSubeKey = match.Key;
            }
        }

        if (req.UseDynamicBranch != false)
        {
            foreach (var l in srcLines)
            {
                var raw = l.ExtractDinamikSubelerRaw(dynamicColumn);
                _logger.LogInformation("Transfer dynamic field debug: sourceInvoiceKey={Invoice} selectedLineKey={LineKey} resolvedDynamicColumn={DynamicColumn} dynamicRaw={DinamikRaw} normalized={Normalized} calculatedTransferType={Type}",
                    req.SourceInvoiceKey, l.Key, dynamicColumn, raw ?? "-", NormalizeDynamicBranch(raw) ?? "-", result.CreatedTargetType);
            }
        }

        static List<DiaInvoiceLine> RecoverSourceLinesByCompositeSnapshot(
            List<DiaInvoiceLine> all,
            List<InvoiceTransferLineSnapshotDto> snaps)
        {
            // 1) key varsa yine dene
            var byKey = new List<DiaInvoiceLine>();
            var keys = snaps.Select(s => s.SourceLineKey).Where(k => k is > 0).Select(k => k!.Value).ToHashSet();
            if (keys.Count > 0)
                byKey = all.Where(l => keys.Contains(l.Key)).ToList();
            if (byKey.Count > 0) return byKey;

            // 2) Kompozit eşleştirme: stokKodu + miktar + birimFiyati + tutar + açıklama (toleranslı)
            var used = new HashSet<long>();
            var matched = new List<DiaInvoiceLine>();

            foreach (var s in snaps)
            {
                var stok = MasterCodeNormalizer.Normalize(s.StokKartKodu);
                if (string.IsNullOrWhiteSpace(stok)) continue;

                var cand = all
                    .Where(l => !used.Contains(l.Key))
                    .Where(l => MasterCodeNormalizer.Normalize(l.KalemRef?.StokKartKodu) == stok)
                    .ToList();
                if (cand.Count == 0) continue;

                // score: exact-ish numeric matches + açıklama benzerliği
                DiaInvoiceLine? best = null;
                var bestScore = -1;
                foreach (var l in cand)
                {
                    var score = 0;
                    if (ApproxEq(l.Miktar, s.Miktar)) score += 3;
                    if (ApproxEq(l.BirimFiyati, s.BirimFiyati)) score += 2;
                    if (ApproxEq(l.Tutari, s.Tutar)) score += 3;

                    var la = (l.KalemRef?.Aciklama ?? string.Empty).Trim();
                    var sa = (s.Aciklama ?? string.Empty).Trim();
                    if (!string.IsNullOrWhiteSpace(sa) && !string.IsNullOrWhiteSpace(la))
                    {
                        if (string.Equals(la, sa, StringComparison.OrdinalIgnoreCase)) score += 2;
                        else if (la.Contains(sa, StringComparison.OrdinalIgnoreCase) || sa.Contains(la, StringComparison.OrdinalIgnoreCase)) score += 1;
                    }

                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = l;
                    }
                }

                // minimum güven eşiği: stok eşit + (tutar veya miktar) eşitliği
                if (best != null && bestScore >= 3)
                {
                    used.Add(best.Key);
                    matched.Add(best);
                }
            }

            return matched;
        }

        static bool ApproxEq(decimal? a, decimal? b, decimal tol = 0.01m)
        {
            if (!a.HasValue || !b.HasValue) return false;
            return Math.Abs(a.Value - b.Value) <= tol;
        }

        var linesToTransfer = new List<DiaInvoiceLine>();
        var dupSkippedSourceKeys = new List<long>();
        foreach (var line in srcLines)
        {
            var dupKey = BuildDuplicateKey(req.SourceInvoiceKey, line.Key, req.TargetFirmaKodu, req.TargetDonemKodu, req.TargetSubeKey, req.TargetDepoKey);
            if (!_opt.TransferDisableDedup && _duplicateRegistry.ContainsKey(dupKey))
            {
                result.DuplicateSkippedCount++;
                dupSkippedSourceKeys.Add(line.Key);
                continue;
            }
            linesToTransfer.Add(line);
        }

        if (linesToTransfer.Count == 0)
        {
            result.Success = false;
            result.Message = "Tüm seçili kalemler daha önce aynı hedefe aktarılmış (duplicate).";
            return result;
        }

        DiaInvoiceAddCardInput card;
        try
        {
            card = await BuildTargetCardAsync(req, src, linesToTransfer, dynamicColumn, ct);
        }
        catch (InvalidOperationException ex)
        {
            return Fail("mapping", "mapping_failed", ex.Message);
        }
        _logger.LogInformation("Transfer debug: sourceInvoiceKey={Invoice} selectedSourceLineKeys={LineKeys} resolvedTargetSubeKey={Sube} resolvedTargetDepoKey={Depo} resolvedTargetCariKey={Cari}",
            req.SourceInvoiceKey, string.Join(",", linesToTransfer.Select(x => x.Key)), req.TargetSubeKey, req.TargetDepoKey, card.KeyScfCariKart);
        string? createdKeyStr;
        int createdCode;
        string createdMsg;
        List<long> createdKalemKeys = new();

        if (!_opt.TransferRawMode)
        {
            try
            {
                var json = System.Text.Json.JsonSerializer.Serialize(card, new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = false,
                    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
                });
                _logger.LogInformation("Transfer debug payload scf_fatura_ekle json: {Json}", json);
            }
            catch
            {
                _logger.LogInformation("Transfer debug payload scf_fatura_ekle: {@Payload}", card);
            }
        }

        result.DiaPayload = card;
        var createRes = await _dia.CreateInvoiceAsync(req.TargetFirmaKodu, req.TargetDonemKodu, card);
        createdKeyStr = createRes.Key;
        createdCode = createRes.Code;
        createdMsg = createRes.Message ?? "DİA scf_fatura_ekle";
        result.DiaResponse = $"service=scf_fatura_ekle; code={createRes.Code}; msg={createRes.Message}; key={createRes.Key}";
        createdKalemKeys = createRes.Extra?.KalemlerKeys ?? new List<long>();
        _logger.LogInformation("Transfer create response: service=scf_fatura_ekle code={Code} msg={Msg} key={Key}", createRes.Code, createRes.Message, createRes.Key);

        // Tenant kuralı: bazı fatura tiplerinde fisno zorunlu. DIA bunu sadece mesajla bildiriyor.
        // UI'da kullanıcıdan istemek yerine, deterministik bir fisno üretip 1 kez retry yapıyoruz.
        if ((createdCode != 200 || string.IsNullOrWhiteSpace(createdKeyStr)) &&
            IsFisNoRequiredMessage(createRes.Message))
        {
            // Hedef firmada fisno, havuzdaki formatla (örn NTS000...) sırayla devam etsin.
            var generatedFisNo = await GenerateFisNoLikeSourceAsync(req, src, "F");
            _logger.LogWarning(
                "DIA requires fisno. Retrying scf_fatura_ekle with generated fisno={FisNo}. sourceInvoiceKey={Invoice} targetFirma={Firma} targetDonem={Donem}",
                generatedFisNo, req.SourceInvoiceKey, req.TargetFirmaKodu, req.TargetDonemKodu);

            card.Fisno = generatedFisNo;
            result.DiaPayload = card; // güncel payload'ı döndür
            var retry = await _dia.CreateInvoiceAsync(req.TargetFirmaKodu, req.TargetDonemKodu, card);
            createdKeyStr = retry.Key;
            createdCode = retry.Code;
            createdMsg = retry.Message ?? createdMsg;
            result.DiaResponse = $"service=scf_fatura_ekle(retry-fisno); code={retry.Code}; msg={retry.Message}; key={retry.Key}";
            createdKalemKeys = retry.Extra?.KalemlerKeys ?? createdKalemKeys;
            _logger.LogInformation("Transfer create response (retry-fisno): code={Code} msg={Msg} key={Key}", retry.Code, retry.Message, retry.Key);
        }

        if (createdCode != 200 || string.IsNullOrWhiteSpace(createdKeyStr))
        {
            // DIA bazı hatalarda sadece genel mesaj döner (ör. "lot takiplidir").
            // Kullanıcı açısından hangi stok/kalem yüzünden düştüğünü belirtmek kritik.
            var stockList = linesToTransfer
                .Select(l => new
                {
                    lineKey = l.Key,
                    stok = MasterCodeNormalizer.Normalize(l.KalemRef?.StokKartKodu)
                })
                .Where(x => !string.IsNullOrWhiteSpace(x.stok) || x.lineKey > 0)
                .Take(10)
                .Select(x => $"{x.stok ?? "-"}#{x.lineKey}")
                .ToList();

            var enriched = createdMsg;
            if (stockList.Count > 0)
                enriched = $"{createdMsg} (kalemler={string.Join(",", stockList)})";

            return Fail("create_document", "document_create_failed", enriched);
        }

        long? verifiedCreatedKey = long.TryParse(createdKeyStr, out var createdKeyParsed) ? createdKeyParsed : null;
        if (!verifiedCreatedKey.HasValue)
        {
            result.Errors.Add("create service key parse edilemedi.");
            result.Message = "Aktarım başarısız: oluşturulan hedef fatura anahtarı okunamadı.";
            result.Details = "createdInvoiceKey parse failed.";
            result.CreatedVerified = false;
            result.CreatedVerifyMessage = "created-key-parse-failed";
            return result;
        }

        if (_opt.TransferDisableVerify)
        {
            result.CreatedVerified = true;
            result.CreatedVerifyMessage = "verify-disabled";
            if (!_opt.TransferDisableDedup)
                _dedup[dkey] = (DateTimeOffset.UtcNow, verifiedCreatedKey.Value);
            _logger.LogInformation("Transfer verify skipped (TransferDisableVerify). targetKey={Key}", verifiedCreatedKey.Value);
        }
        else
        {
            try
            {
                var verify = await _dia.GetInvoiceAsyncWithDonemFallback(req.TargetFirmaKodu, req.TargetDonemKodu, verifiedCreatedKey.Value);
                if (verify == null || verify.Key <= 0)
                    throw new InvalidOperationException("Hedef fatura doğrulama boş döndü.");
                _logger.LogInformation("Transfer verify success: type=FATURA targetKey={Key} targetFirma={Firma} targetDonem={Donem} fisno={FisNo}",
                    verifiedCreatedKey.Value, req.TargetFirmaKodu, req.TargetDonemKodu, verify.FisNo ?? "-");
                result.CreatedVerified = true;
                result.CreatedVerifyMessage = "verified";

                if (!_opt.TransferDisableDedup)
                    _dedup[dkey] = (DateTimeOffset.UtcNow, verifiedCreatedKey.Value);
            }
            catch (Exception ex)
            {
                result.CreatedVerified = false;
                result.CreatedVerifyMessage = $"verify-failed:{ex.Message}";
                result.Message = "Oluşturuldu ama doğrulanamadı.";
                result.Details = "Target verify failed after create.";
                result.CreatedInvoiceKey = verifiedCreatedKey;
                result.TargetFirmaKodu = req.TargetFirmaKodu;
                result.TargetDonemKodu = req.TargetDonemKodu;
                result.TargetSubeKey = req.TargetSubeKey;
                result.TargetDepoKey = req.TargetDepoKey;
                return result; // kritik kural: doğrulanmadıysa 'tam başarı' sayma
            }
        }

        if (!_opt.TransferDisableDedup)
        {
            foreach (var line in linesToTransfer)
            {
                var dupKey = BuildDuplicateKey(req.SourceInvoiceKey, line.Key, req.TargetFirmaKodu, req.TargetDonemKodu, req.TargetSubeKey, req.TargetDepoKey);
                _duplicateRegistry.TryAdd(dupKey, 1);
            }

            MarkSourceTransferState(req.SourceInvoiceKey, src.Lines?.Count ?? 0, linesToTransfer.Select(x => x.Key), req.TargetFirmaKodu, req.TargetSubeKey, req.TargetDonemKodu);
        }

        MaybeSaveTransferState();

        result.Success = true;
        result.Message = createdMsg;
        result.CreatedInvoiceKey = verifiedCreatedKey;
        result.CreatedKalemKeys = createdKalemKeys;
        result.TransferredLineCount = linesToTransfer.Count;
        result.TransferredSourceKalemKeys = linesToTransfer.Select(x => x.Key).Distinct().ToList();
        result.DuplicateSkippedSourceKalemKeys = dupSkippedSourceKeys.Distinct().ToList();
        result.TargetFirmaKodu = req.TargetFirmaKodu;
        result.TargetDonemKodu = req.TargetDonemKodu;
        result.TargetSubeKey = req.TargetSubeKey;
        result.TargetDepoKey = req.TargetDepoKey;

        _logger.LogInformation("Transfer success: createdTargetInvoiceKey={InvoiceKey} transferredLineCount={Count} duplicateSkippedCount={Dup}",
            result.CreatedInvoiceKey, result.TransferredLineCount, result.DuplicateSkippedCount);

        if (_skippedExtraFields.Value is { Count: > 0 })
        {
            // Aynı isim tekrar edebilir (merge + farklı kaynaklar); UI'da şişmesin.
            result.SkippedExtraFields = _skippedExtraFields.Value
                .GroupBy(x => $"{x.Scope}:{x.Name}:{x.Reason}", StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
        }
        return result;

        InvoiceTransferResultDto Fail(string stage, string code, string msg)
        {
            _logger.LogWarning("Transfer failed: stage={Stage} code={Code} msg={Msg}", stage, code, msg);
            return new InvoiceTransferResultDto
            {
                Success = false,
                Message = msg,
                Errors = new List<string> { code, msg },
                FailureStage = stage,
                FailureCode = code,
                SkippedExtraFields = _skippedExtraFields.Value ?? new List<Models.Api.InvoiceSkippedExtraFieldDto>(),
            };
        }
    }

    private async Task<InvoiceTransferResultDto> TransferFromSnapshotAsync(InvoiceTransferRequestDto req, CancellationToken ct)
    {
        InvoiceTransferResultDto FailSnap(string stage, string code, string msg) => new()
        {
            Success = false,
            Message = msg,
            Errors = new List<string> { code, msg },
            FailureStage = stage,
            FailureCode = code,
        };

        var h = req.HeaderSnapshot!;
        var lineSnaps = req.SelectedLineSnapshots.Where(s => s != null).ToList();

        _logger.LogInformation("SNAPSHOT_TRANSFER invoiceKey={InvoiceKey} lineCount={LineCount} getInvoiceSkipped=true",
            req.SourceInvoiceKey, lineSnaps.Count);

        if (string.IsNullOrWhiteSpace((h.CariCode ?? "").Trim()))
            return FailSnap("validation", "snapshot_header_cari", "headerSnapshot.cariCode zorunlu.");
        if (string.IsNullOrWhiteSpace(h.Date))
            return FailSnap("validation", "snapshot_header_date", "headerSnapshot.date zorunlu.");
        if (string.IsNullOrWhiteSpace((h.InvoiceNo ?? "").Trim()))
            return FailSnap("validation", "snapshot_header_invoice_no", "headerSnapshot.invoiceNo (fatura no) zorunlu.");
        if (string.IsNullOrWhiteSpace((h.FisNo ?? "").Trim()))
            return FailSnap("validation", "snapshot_header_fis_no", "headerSnapshot.fisNo (fiş no) zorunlu.");

        foreach (var s in lineSnaps)
        {
            var c = FirstNonEmpty(s.ItemCode, s.StokKartKodu)?.Trim();
            if (string.IsNullOrWhiteSpace(c))
                return FailSnap("validation", "snapshot_line_code", "Satır snapshot'ta itemCode/stokKartKodu zorunlu.");
        }

        var synLines = new List<DiaInvoiceLine>();
        var idx = 0;
        foreach (var s in lineSnaps)
            synLines.Add(CreateSyntheticLineFromSnapshot(s, idx++, req.SourceInvoiceKey));

        var src = CreateSyntheticInvoiceDetailFromHeader(req.SourceInvoiceKey, h, synLines);
        req.SelectedKalemKeys = synLines.Select(l => l.Key).Distinct().ToList();

        var dkey = DedupKey(req);
        if (!_opt.TransferDisableDedup && _dedup.TryGetValue(dkey, out var dhit) && DedupFresh(dhit.at))
        {
            return FailSnap("validation", "duplicate_transfer_blocked",
                $"Bu fatura bu hedefe daha önce aktarıldı. sourceInvoiceKey={req.SourceInvoiceKey} createdInvoiceKey={dhit.createdKey}");
        }

        var targetCtx = await ResolveTargetContextAsync(req, src);
        if (!targetCtx.ok)
            return FailSnap(targetCtx.stage, targetCtx.code, targetCtx.message);

        req.TargetDonemKodu = targetCtx.targetDonemKodu;
        req.TargetSubeKey = targetCtx.targetSubeKey;
        req.TargetDepoKey = targetCtx.targetDepoKey;

        const string dynamicColumn = "__dinamik__fatsube";

        if (req.UseDynamicBranch != false)
        {
            var dynBranches = synLines
                .Select(l => NormalizeDynamicBranch(l.ExtractDinamikSubelerRaw(dynamicColumn)))
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (dynBranches.Count > 0)
            {
            if (dynBranches.Count > 1)
                return FailSnap("validation", "dynamic_branch_multiple",
                    $"Seçili kalemlerde birden fazla şube var ({string.Join(", ", dynBranches)}). Aynı anda aktarım için tek şube seçili kalemleri birlikte seçin.");

            var desired = dynBranches[0];
            static string CanonSb(string s) => s.Trim().ToUpperInvariant()
                .Replace("İ", "I").Replace("İ", "I")
                .Replace("Ş", "S").Replace("Ğ", "G").Replace("Ü", "U").Replace("Ö", "O").Replace("Ç", "C");

            var targetBranches = await _dia.GetSubelerDepolarForFirmaAsync(req.TargetFirmaKodu, req.TargetDonemKodu);
            var desiredCanon = CanonSb(desired);
            var match = targetBranches.FirstOrDefault(b => CanonSb(b.SubeAdi) == desiredCanon)
                        ?? targetBranches.FirstOrDefault(b => CanonSb(b.SubeAdi).Contains(desiredCanon))
                        ?? targetBranches.FirstOrDefault(b => desiredCanon.Contains(CanonSb(b.SubeAdi)));

            if (match == null || match.Key <= 0)
                return FailSnap("resolve_target", "target_branch_not_found_for_dynamic",
                    $"Kalem şubesi hedef firmada bulunamadı. Kalem şubesi='{desired}'. Hedef firmada şubeler: {string.Join(", ", targetBranches.Take(10).Select(b => b.SubeAdi))}");

            if (req.TargetSubeKey > 0 && req.TargetSubeKey != match.Key)
                return FailSnap("validation", "target_branch_conflict",
                    $"Kalem şubesi='{desired}' ancak hedef şube seçimi farklı. Kalem şubesi ile aynı şubeyi seçin.");

            req.TargetSubeKey = match.Key;
            }
        }

        var result = new InvoiceTransferResultDto { Success = false, CreatedTargetType = "FATURA" };

        var linesToTransfer = new List<DiaInvoiceLine>();
        var dupSkippedSourceKeys = new List<long>();
        foreach (var line in synLines)
        {
            var dupKey = BuildDuplicateKey(req.SourceInvoiceKey, line.Key, req.TargetFirmaKodu, req.TargetDonemKodu, req.TargetSubeKey, req.TargetDepoKey);
            if (!_opt.TransferDisableDedup && _duplicateRegistry.ContainsKey(dupKey))
            {
                result.DuplicateSkippedCount++;
                dupSkippedSourceKeys.Add(line.Key);
                continue;
            }
            linesToTransfer.Add(line);
        }

        if (linesToTransfer.Count == 0)
        {
            result.Success = false;
            result.Message = "Tüm seçili kalemler daha önce aynı hedefe aktarılmış (duplicate).";
            return result;
        }

        DiaInvoiceAddCardInput card;
        try
        {
            card = await BuildTargetCardAsync(req, src, linesToTransfer, dynamicColumn, ct, snapshotFirst: true);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "SNAPSHOT_TRANSFER mapping failed invoiceKey={Key}", req.SourceInvoiceKey);
            return FailSnap("mapping", "mapping_failed", ex.Message);
        }

        string? createdKeyStr;
        int createdCode;
        string createdMsg;
        List<long> createdKalemKeys = new();

        if (!_opt.TransferRawMode)
        {
            try
            {
                var json = System.Text.Json.JsonSerializer.Serialize(card, new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = false,
                    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
                });
                _logger.LogInformation("SNAPSHOT_TRANSFER scf_fatura_ekle json: {Json}", json);
            }
            catch
            {
                _logger.LogInformation("SNAPSHOT_TRANSFER scf_fatura_ekle: {@Payload}", card);
            }
        }

        result.DiaPayload = card;
        var createRes = await _dia.CreateInvoiceAsync(req.TargetFirmaKodu, req.TargetDonemKodu, card);
        createdKeyStr = createRes.Key;
        createdCode = createRes.Code;
        createdMsg = createRes.Message ?? "DİA scf_fatura_ekle";
        result.DiaResponse = $"service=scf_fatura_ekle; code={createRes.Code}; msg={createRes.Message}; key={createRes.Key}";
        createdKalemKeys = createRes.Extra?.KalemlerKeys ?? new List<long>();
        _logger.LogInformation("SNAPSHOT_TRANSFER create response: code={Code} msg={Msg} key={Key}", createRes.Code, createRes.Message, createRes.Key);

        if ((createdCode != 200 || string.IsNullOrWhiteSpace(createdKeyStr)) &&
            IsFisNoRequiredMessage(createRes.Message))
        {
            var generatedFisNo = await GenerateFisNoLikeSourceAsync(req, src, "F");
            _logger.LogWarning("SNAPSHOT_TRANSFER DIA requires fisno. Retrying with generated fisno={FisNo}. invoice={Invoice}",
                generatedFisNo, req.SourceInvoiceKey);
            card.Fisno = generatedFisNo;
            result.DiaPayload = card;
            var retry = await _dia.CreateInvoiceAsync(req.TargetFirmaKodu, req.TargetDonemKodu, card);
            createdKeyStr = retry.Key;
            createdCode = retry.Code;
            createdMsg = retry.Message ?? createdMsg;
            result.DiaResponse = $"service=scf_fatura_ekle(retry-fisno); code={retry.Code}; msg={retry.Message}; key={retry.Key}";
            createdKalemKeys = retry.Extra?.KalemlerKeys ?? createdKalemKeys;
        }

        if (createdCode != 200 || string.IsNullOrWhiteSpace(createdKeyStr))
        {
            var stockList = linesToTransfer
                .Select(l => new { lineKey = l.Key, stok = MasterCodeNormalizer.Normalize(l.KalemRef?.StokKartKodu ?? l.KalemRef?.HizmetKartKodu) })
                .Where(x => !string.IsNullOrWhiteSpace(x.stok) || x.lineKey > 0)
                .Take(10)
                .Select(x => $"{x.stok ?? "-"}#{x.lineKey}")
                .ToList();
            var enriched = createdMsg;
            if (stockList.Count > 0)
                enriched = $"{createdMsg} (kalemler={string.Join(",", stockList)})";
            return FailSnap("create_document", "document_create_failed", enriched);
        }

        if (!long.TryParse(createdKeyStr, out var createdKeyParsed))
        {
            return FailSnap("create_document", "created_key_parse", "Oluşturulan hedef fatura anahtarı okunamadı.");
        }

        if (!_opt.TransferDisableDedup)
        {
            foreach (var line in linesToTransfer)
            {
                var dupKey = BuildDuplicateKey(req.SourceInvoiceKey, line.Key, req.TargetFirmaKodu, req.TargetDonemKodu, req.TargetSubeKey, req.TargetDepoKey);
                _duplicateRegistry.TryAdd(dupKey, 1);
            }

            MarkSourceTransferState(req.SourceInvoiceKey, synLines.Count, linesToTransfer.Select(x => x.Key), req.TargetFirmaKodu, req.TargetSubeKey, req.TargetDonemKodu);
            _dedup[dkey] = (DateTimeOffset.UtcNow, createdKeyParsed);
        }

        MaybeSaveTransferState();

        result.Success = true;
        result.Message = createdMsg;
        result.CreatedInvoiceKey = createdKeyParsed;
        result.CreatedKalemKeys = createdKalemKeys;
        result.TransferredLineCount = linesToTransfer.Count;
        result.TransferredSourceKalemKeys = linesToTransfer.Select(x => x.Key).Distinct().ToList();
        result.DuplicateSkippedSourceKalemKeys = dupSkippedSourceKeys.Distinct().ToList();
        result.TargetFirmaKodu = req.TargetFirmaKodu;
        result.TargetDonemKodu = req.TargetDonemKodu;
        result.TargetSubeKey = req.TargetSubeKey;
        result.TargetDepoKey = req.TargetDepoKey;
        result.CreatedVerified = true;
        result.CreatedVerifyMessage = "snapshot-skip-target-getir";

        _logger.LogInformation("SNAPSHOT_TRANSFER success: createdKey={Key} lines={Count}", createdKeyParsed, linesToTransfer.Count);
        return result;
    }

    /// <summary>
    /// Sıfır okuma: sunucu kaynak/ hedef master WS çağırmaz; istemci snapshot içinde hedef <c>_key</c> değerlerini taşır.
    /// Tek hedef çağrı: <see cref="IDiaWsClient.CreateInvoiceAsync"/> — retry / fisno üretimi yok.
    /// </summary>
    private async Task<InvoiceTransferResultDto> TransferRawAsync(InvoiceTransferRequestDto req, CancellationToken ct)
    {
        InvoiceTransferResultDto FailRaw(string code, string msg) => new()
        {
            Success = false,
            Message = msg,
            Errors = new List<string> { code, msg },
            FailureStage = "raw_transfer",
            FailureCode = code,
        };

        if (req.HeaderSnapshot == null || req.SelectedLineSnapshots is not { Count: > 0 })
            return FailRaw("snapshot_required", "TransferRawMode: headerSnapshot ve dolu selectedLineSnapshots zorunlu.");

        NormalizeRawHeaderBusinessDefaults(req);
        NormalizeRawSnapshotLineKdv(req);

        if (!TryValidateRawSnapshot(req, out var rawReject))
            return FailRaw("raw_snapshot_invalid", rawReject ?? "RAW snapshot eksik veya hatalı.");

        // Havuz (kaynak) faturadaki ödeme planı + banka ödeme planı + banka hesabı — legacy ile aynı kod çözümü.
        // RAW snapshot istemci bu _key'leri taşımadığı için sunucu kaynak scf_fatura_getir üzerinden eşler.
        DiaInvoiceDetail srcForBanking;
        try
        {
            srcForBanking = await _dia.GetInvoiceAsyncWithLimitedDonemFallback(req.SourceFirmaKodu, req.SourceDonemKodu, req.SourceInvoiceKey, 3);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RAW: kaynak fatura okunamadı (banka/ödeme eşlemesi) sourceInvoiceKey={Key}", req.SourceInvoiceKey);
            return FailRaw("raw_source_read", $"RAW aktarım için kaynak fatura okunamadı (ödeme/banka eşlemesi): {ex.Message}");
        }

        try
        {
            var banking = await ResolveTargetOdemePlaniAndBankingAsync(req, srcForBanking, ct);
            var hSnap = req.HeaderSnapshot!;
            if (banking.targetOdemePlaniKey is long op && op > 0)
                hSnap.TargetOdemePlaniKey = op;

            foreach (var ln in req.SelectedLineSnapshots)
            {
                if (ln == null) continue;
                if (banking.targetOdemePlaniKey is long op2 && op2 > 0)
                    ln.TargetKeyScfOdemePlani = op2;
                if (banking.targetBankaOdemePlaniKey is long bp && bp > 0)
                    ln.TargetKeyScfBankaOdemePlani = bp;
                if (banking.targetBankaHesabiKey is long bh && bh > 0)
                    ln.TargetKeyBcsBankahesabi = bh;
            }

            _logger.LogInformation(
                "RAW banking from pool invoice: sourceInvoiceKey={Inv} targetOdemePlaniKey={Odeme} targetBankaOdemePlaniKey={BankPlan} targetBcsBankaHesabiKey={BankAcc}",
                req.SourceInvoiceKey,
                banking.targetOdemePlaniKey,
                banking.targetBankaOdemePlaniKey,
                banking.targetBankaHesabiKey);
        }
        catch (InvalidOperationException ex)
        {
            return FailRaw("raw_banking_map", ex.Message);
        }

        long? rawCariYetkiliKey;
        try
        {
            rawCariYetkiliKey = await ResolveRawHeaderCariYetkiliKeyAsync(req, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RAW yetkili çözümlemesi atlandı sourceInvoiceKey={Key}", req.SourceInvoiceKey);
            rawCariYetkiliKey = null;
        }

        DiaInvoiceAddCardInput card;
        try
        {
            card = BuildRawInvoiceCardFromSnapshot(req, rawCariYetkiliKey);
        }
        catch (InvalidOperationException ex)
        {
            return FailRaw("raw_mapping_failed", ex.Message);
        }

        var result = new InvoiceTransferResultDto
        {
            Success = false,
            CreatedTargetType = "FATURA",
            DiaPayload = card,
        };

        var createRes = await _dia.CreateInvoiceAsync(req.TargetFirmaKodu, req.TargetDonemKodu, card);
        var createdKeyStr = createRes.Key;
        var createdCode = createRes.Code;
        var createdMsg = createRes.Message ?? "DİA scf_fatura_ekle";
        result.DiaResponse = $"service=scf_fatura_ekle; code={createRes.Code}; msg={createRes.Message}; key={createRes.Key}";

        if ((createdCode != 200 || string.IsNullOrWhiteSpace(createdKeyStr)) &&
            IsFisNoRequiredMessage(createRes.Message))
        {
            return FailRaw("fisno_required",
                "RAW mode: fisno zorunlu; headerSnapshot.fisNo istemci göndermeli (sunucu üretmez / ikinci CreateInvoice yok).");
        }

        if (createdCode != 200 || string.IsNullOrWhiteSpace(createdKeyStr))
            return FailRaw("create_document", createdMsg);

        if (!long.TryParse(createdKeyStr, out var createdKeyParsed))
            return FailRaw("create_document", "Oluşturulan hedef fatura anahtarı okunamadı.");

        result.Success = true;
        result.Message = createdMsg;
        result.CreatedInvoiceKey = createdKeyParsed;
        result.CreatedKalemKeys = createRes.Extra?.KalemlerKeys ?? new List<long>();
        result.TransferredLineCount = req.SelectedLineSnapshots.Count;
        result.TargetFirmaKodu = req.TargetFirmaKodu;
        result.TargetDonemKodu = req.TargetDonemKodu;
        result.TargetSubeKey = req.TargetSubeKey;
        result.TargetDepoKey = req.TargetDepoKey;
        result.CreatedVerified = true;
        result.CreatedVerifyMessage = "raw-zero-read";

        if (!_opt.TransferDisableDedup)
        {
            var dkey = DedupKey(req);
            _dedup[dkey] = (DateTimeOffset.UtcNow, createdKeyParsed);
        }

        _logger.LogInformation("RAW_TRANSFER success: createdKey={Key} lines={Count}", createdKeyParsed, result.TransferredLineCount);
        return result;
    }

    private static bool TryValidateRawSnapshot(InvoiceTransferRequestDto req, out string? reject)
    {
        reject = null;
        var h = req.HeaderSnapshot!;
        if (req.TargetDonemKodu <= 0)
        {
            reject = "targetDonemKodu>0 gerekli";
            return false;
        }

        if (req.TargetSubeKey <= 0)
        {
            reject = "targetSubeKey>0 gerekli";
            return false;
        }

        if (req.TargetDepoKey <= 0)
        {
            reject = "targetDepoKey>0 gerekli";
            return false;
        }

        if (string.IsNullOrWhiteSpace((h.CariCode ?? "").Trim()))
        {
            reject = "headerSnapshot.cariCode (kaynak cari kodu)";
            return false;
        }

        if (string.IsNullOrWhiteSpace(h.Date))
        {
            reject = "headerSnapshot.date";
            return false;
        }

        if (string.IsNullOrWhiteSpace((h.InvoiceNo ?? "").Trim()))
        {
            reject = "headerSnapshot.invoiceNo (fatura no)";
            return false;
        }

        if (string.IsNullOrWhiteSpace((h.FisNo ?? "").Trim()))
        {
            reject = "headerSnapshot.fisNo (fiş no)";
            return false;
        }

        // time / invoiceTypeCode: NormalizeRawHeaderBusinessDefaults ile doldurulur (zorunlu iş kuralı değil).

        if (!(h.TargetCariKey is > 0))
        {
            reject = "headerSnapshot.targetCariKey>0 (RAW: hedef cari _key istemci tarafından sağlanmalı)";
            return false;
        }

        if (!(h.TargetSisDovizKey is > 0))
        {
            reject = "headerSnapshot.targetSisDovizKey>0";
            return false;
        }

        var i = 0;
        foreach (var s in req.SelectedLineSnapshots)
        {
            i++;
            if (s is null)
            {
                reject = $"selectedLineSnapshots[{i}] null";
                return false;
            }

            if (!(s.TargetKeyKalemTuru is > 0))
            {
                reject = $"satır {i}: targetKeyKalemTuru>0";
                return false;
            }

            if (!(s.TargetKeyKalemBirim is > 0))
            {
                reject = $"satır {i}: targetKeyKalemBirim>0";
                return false;
            }

            if (s.Miktar is not decimal m || m <= 0)
            {
                reject = $"satır {i}: miktar>0";
                return false;
            }

            if (s.BirimFiyati is null || s.BirimFiyati.Value < 0)
            {
                reject = $"satır {i}: birimFiyati>=0";
                return false;
            }

            if (!s.Tutar.HasValue)
            {
                reject = $"satır {i}: tutar (RAW: sunucu miktar×fiyat hesaplamaz)";
                return false;
            }

            if (string.IsNullOrWhiteSpace(s.KalemTuru))
            {
                reject = $"satır {i}: kalemTuru (MLZM/HZMT — RAW tahmini yok)";
                return false;
            }

            var lineDv = s.TargetKeySisDoviz ?? h.TargetSisDovizKey;
            if (!(lineDv is > 0))
            {
                reject = $"satır {i}: targetKeySisDoviz veya başlık targetSisDovizKey";
                return false;
            }
        }

        return true;
    }

    /// <summary>RAW: başlıktaki yetkili — önce snapshot’ta hedef _key / kaynak kod; yoksa kaynak scf_fatura_getir.</summary>
    private async Task<long?> ResolveRawHeaderCariYetkiliKeyAsync(InvoiceTransferRequestDto req, CancellationToken ct)
    {
        var h = req.HeaderSnapshot!;
        var cariTrim = (h.CariCode ?? "").Trim();
        if (string.IsNullOrWhiteSpace(cariTrim))
            return null;

        if (h.TargetCariYetkiliKey is > 0)
            return h.TargetCariYetkiliKey;

        if (!string.IsNullOrWhiteSpace(h.CariYetkiliKodu))
        {
            var yk = h.CariYetkiliKodu.Trim();
            if (!IsJunkYetkiliKodu(yk))
            {
                var key = await _dia.FindCariYetkiliKeyByCodeAsync(req.TargetFirmaKodu, req.TargetDonemKodu, cariTrim, yk);
                if (key is > 0)
                {
                    _logger.LogInformation("RAW yetkili: snapshot cariYetkiliKodu → targetKey={Key}", key);
                    return key;
                }

                _logger.LogWarning("RAW yetkili: hedefte kod bulunamadı cari={Cari} yetkiliKod={Kod}", cariTrim, yk);
            }
            else
            {
                _logger.LogInformation("RAW yetkili: snapshot’ta anlamsız/placeholder kod; getir yoluna geçiliyor kod={Kod}", yk);
            }
        }

        ct.ThrowIfCancellationRequested();
        var srcInv = await _dia.GetInvoiceAsync(req.SourceFirmaKodu, req.SourceDonemKodu, req.SourceInvoiceKey);
        var srcKod = TryParseYetkiliKodFromInvoiceExtras(srcInv);
        if (string.IsNullOrWhiteSpace(srcKod) || IsJunkYetkiliKodu(srcKod))
        {
            if (!string.IsNullOrWhiteSpace(srcKod) && IsJunkYetkiliKodu(srcKod))
                _logger.LogInformation("RAW yetkili: kaynak faturada anlamsız/placeholder kod; alan boş sourceInvoiceKey={Key} kod={Kod}", req.SourceInvoiceKey, srcKod);
            else
                _logger.LogInformation("RAW yetkili: kaynak faturada yetkili kodu yok sourceInvoiceKey={Key}", req.SourceInvoiceKey);
            return null;
        }

        var resolved = await _dia.FindCariYetkiliKeyByCodeAsync(req.TargetFirmaKodu, req.TargetDonemKodu, cariTrim, srcKod.Trim());
        if (resolved is > 0)
            _logger.LogInformation("RAW yetkili: scf_fatura_getir → kod={Kod} targetKey={Key}", srcKod, resolved);
        else
            _logger.LogWarning("RAW yetkili: kaynak kod hedefte yok cari={Cari} kod={Kod}", cariTrim, srcKod);

        return resolved is > 0 ? resolved : null;
    }

    private static bool IsJunkYetkiliKodu(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return true;
        var t = s.Trim();
        if (t.Length > 64) return true;
        if (t is "-" or "." or "," or "0") return true;
        if (t.Equals("test", StringComparison.OrdinalIgnoreCase)) return true;
        if (t.Equals("deneme", StringComparison.OrdinalIgnoreCase)) return true;
        if (t.Equals("yok", StringComparison.OrdinalIgnoreCase)) return true;
        if (t.Equals("null", StringComparison.OrdinalIgnoreCase)) return true;
        // uzun saf rakam = genelde _key; kod olarak gönderme
        if (t.Length >= 10 && t.All(char.IsDigit)) return true;
        return false;
    }

    /// <summary>
    /// İş kuralı: başlıkta zorunlu olan cari / tarih / fatura no / fiş no (RAW doğrulamasında).
    /// Saat ve <c>turu</c> raporda yoksa güvenli varsayılan (kontör için ek WS yok).
    /// </summary>
    private static void NormalizeRawHeaderBusinessDefaults(InvoiceTransferRequestDto req)
    {
        if (req.HeaderSnapshot is not { } h) return;
        if (string.IsNullOrWhiteSpace(h.Time))
            h.Time = "12:00:00";
        if (!h.InvoiceTypeCode.HasValue || h.InvoiceTypeCode.Value <= 0)
        {
            var fromLabel = TryResolveInvoiceTypeCodeFromLabel(h.InvoiceType);
            h.InvoiceTypeCode = fromLabel > 0 ? fromLabel : 5;
        }
    }

    /// <summary>Rapor <c>invoiceType</c> etiketinden DİA <c>turu</c> kodu (havuzda sık görülenler).</summary>
    private static int TryResolveInvoiceTypeCodeFromLabel(string? invoiceTypeLabel)
    {
        if (string.IsNullOrWhiteSpace(invoiceTypeLabel)) return 0;
        static string Norm(string s) => s.Trim().ToUpperInvariant()
            .Replace("İ", "I", StringComparison.Ordinal).Replace("Ş", "S", StringComparison.Ordinal)
            .Replace("Ğ", "G", StringComparison.Ordinal).Replace("Ü", "U", StringComparison.Ordinal)
            .Replace("Ö", "O", StringComparison.Ordinal).Replace("Ç", "C", StringComparison.Ordinal);
        var k = Norm(invoiceTypeLabel);
        if (k.Contains("VERILEN HIZMET", StringComparison.Ordinal)) return 5;
        if (k.Contains("ALINAN HIZMET", StringComparison.Ordinal)) return 4;
        if (k.Contains("TOPTAN SATIS", StringComparison.Ordinal)) return 3;
        if (k.Contains("PERAKENDE SATIS", StringComparison.Ordinal)) return 2;
        if (k.Contains("MAL ALIM", StringComparison.Ordinal)) return 1;
        return 0;
    }

    private static void NormalizeRawSnapshotLineKdv(InvoiceTransferRequestDto req)
    {
        foreach (var s in req.SelectedLineSnapshots)
        {
            if (s is null || !s.KdvYuzde.HasValue) continue;
            var tut = s.Tutar ?? 0m;
            s.KdvYuzde = DiaMappers.NormalizeSnapshotLineKdvPercent(s.KdvYuzde.Value, tut, s.KdvTutari);
        }
    }

    private static string? TryParseYetkiliKodFromInvoiceExtras(DiaInvoiceDetail? src)
    {
        if (src?.ExtraFields == null || src.ExtraFields.Count == 0)
            return null;

        if (src.ExtraFields.TryGetValue("yetkikodu", out var y))
        {
            var s = y.ValueKind == JsonValueKind.String ? y.GetString() : y.GetRawText();
            if (!string.IsNullOrWhiteSpace(s))
            {
                var tr = s.Trim();
                if (!IsJunkYetkiliKodu(tr))
                    return tr;
            }
        }

        if (src.ExtraFields.TryGetValue("_key_scf_carikart_yetkili", out var yo))
        {
            var pc = ParseCode(yo, "yetkikodu", "kodu", "kod");
            if (!IsJunkYetkiliKodu(pc))
                return pc?.Trim();
        }

        return null;
    }

    private static string BuildRawHeaderKurString(InvoiceTransferHeaderSnapshotDto h)
    {
        if (!string.IsNullOrWhiteSpace(h.HeaderDovizKuru))
            return h.HeaderDovizKuru!.Trim();
        if (h.ExchangeRate is > 0m)
            return decimal.Round(h.ExchangeRate.Value, 6, MidpointRounding.AwayFromZero)
                .ToString("0.000000", CultureInfo.InvariantCulture);
        return "1.000000";
    }

    private static DiaInvoiceAddCardInput BuildRawInvoiceCardFromSnapshot(InvoiceTransferRequestDto req, long? resolvedCariYetkiliKey)
    {
        var h = req.HeaderSnapshot!;
        var headerKur = BuildRawHeaderKurString(h);
        var headerCurr = h.CurrencyCode ?? string.Empty;
        var linesOut = new List<DiaInvoiceAddLineInput>();
        var idx = 0;
        foreach (var s in req.SelectedLineSnapshots)
        {
            if (s == null) continue;
            idx++;

            var lineDv = s.TargetKeySisDoviz ?? h.TargetSisDovizKey!.Value;
            // Başlık dövizini satıra varsayılan alma: USD başlıkta TL satırlar yanlışlıkla USD sayılıp başlık kuru ile çarpılıyordu.
            var lineCurrRaw = (s.LineCurrencyCode ?? string.Empty).Trim();

            decimal? snapKurDec = null;
            if (!string.IsNullOrWhiteSpace(s.DovizKuru))
                snapKurDec = ParseHeaderKurDecimal(s.DovizKuru);
            var lcCanon = CanonicalCurrency(lineCurrRaw);
            var hcCanon = CanonicalCurrency(headerCurr);
            // Snapshot’ta kur=1 iken satır/başlık dövizi farklıysa (ör. USD fiş + TL satır) 1’i koru; aksi halde başlık kuru ile çarpım riski.
            if (snapKurDec is 1m
                && !string.Equals(lcCanon, hcCanon, StringComparison.Ordinal)
                && hcCanon != "TL")
                snapKurDec = null;

            var hdrKDec = ParseHeaderKurDecimal(headerKur) ?? 0m;
            var hc = hcCanon;
            var lc = lcCanon;
            var lcForResolve = string.IsNullOrEmpty(lc) ? "TL" : lc;
            // Sadece satır dövizi açıkça başlıkla aynıysa başlık kuru yansıt (hedef _key eşleşmesi tek başına yeterli değil).
            var mirrorLineKurToHeader = !string.IsNullOrEmpty(hc) && hc != "TL"
                && hdrKDec > 0m
                && !string.IsNullOrEmpty(lc)
                && string.Equals(lc, hc, StringComparison.Ordinal);

            // DİA yabancı para faturada kalem dövizi = fiş dövizi iken satır kurunun 1.000000 gitmesi
            // yerel/tutar sütunlarını havuzdan koparır; başlık kuru ile aynı gönderilir (ör. USD hizmet faturası).
            string lineDovizKurStr;
            string lineRaporlamaDovizKurStr;
            if (mirrorLineKurToHeader)
            {
                lineDovizKurStr = FormatDiaKurStringInvariant6(headerKur);
                lineRaporlamaDovizKurStr = lineDovizKurStr;
            }
            else
            {
                var resolvedLineKur = ResolveLineKurForDiaPayload(
                    snapKurDec,
                    headerKur,
                    hc,
                    lcForResolve);
                var lineDovizForFormat = string.IsNullOrEmpty(lineCurrRaw) ? "TL" : lineCurrRaw;
                (lineDovizKurStr, lineRaporlamaDovizKurStr) = FormatLineKurStringsForDiaAdd(
                    resolvedLineKur,
                    headerCurr,
                    lineDovizForFormat,
                    headerKur);
            }

            var bf = s.BirimFiyati;
            var mq = s.Miktar;
            decimal? ybf = s.YerelBirimFiyati ?? s.BirimFiyati;
            decimal? sbf = s.SonBirimFiyati ?? s.BirimFiyati;
            decimal? tutCalc = s.Tutar;

            if (mirrorLineKurToHeader
                && bf is > 0m && mq is > 0m)
            {
                var fcSubtotal = bf.Value * mq.Value;
                var snapYerel = s.YerelBirimFiyati;
                if (!snapYerel.HasValue || Math.Abs(snapYerel.Value - bf.Value) < 0.01m)
                    ybf = decimal.Round(bf.Value * hdrKDec, 10, MidpointRounding.AwayFromZero);
                var snapSon = s.SonBirimFiyati;
                if (!snapSon.HasValue || Math.Abs(snapSon.Value - bf.Value) < 0.01m)
                    sbf = ybf;

                if (tutCalc is > 0m)
                {
                    var tol = Math.Max(0.01m, 0.001m * fcSubtotal);
                    if (Math.Abs(tutCalc.Value - fcSubtotal) <= tol)
                        tutCalc = decimal.Round(fcSubtotal * hdrKDec, 4, MidpointRounding.AwayFromZero);
                }
            }

            linesOut.Add(new DiaInvoiceAddLineInput
            {
                KeyKalemTuru = s.TargetKeyKalemTuru,
                KeyKalemBirim = s.TargetKeyKalemBirim,
                KeyDepoSource = req.TargetDepoKey,
                KeyDoviz = lineDv,
                KeyScfOdemePlani = s.TargetKeyScfOdemePlani is > 0 ? s.TargetKeyScfOdemePlani : null,
                KeyScfBankaOdemePlani = s.TargetKeyScfBankaOdemePlani is > 0 ? s.TargetKeyScfBankaOdemePlani : null,
                KeyBcsBankahesabi = s.TargetKeyBcsBankahesabi is > 0 ? s.TargetKeyBcsBankahesabi : null,
                KeyPrjProje = s.TargetPrjProjeKey is > 0 ? s.TargetPrjProjeKey : null,
                KalemTuru = s.KalemTuru!.Trim(),
                AnaMiktar = RawInvariantDecimal(s.Miktar),
                Miktar = RawInvariantDecimal(s.Miktar),
                BirimFiyati = RawInvariantDecimal(s.BirimFiyati),
                SonBirimFiyati = RawInvariantDecimal(sbf),
                YerelBirimFiyati = RawInvariantDecimal(ybf),
                Tutari = RawInvariantDecimal(tutCalc),
                Kdv = s.KdvYuzde.HasValue ? RawInvariantDecimal(s.KdvYuzde) : null,
                KdvTutari = null,
                KdvDurumu = "H",
                DovizKuru = lineDovizKurStr,
                RaporlamaDovizKuru = lineRaporlamaDovizKurStr,
                SiraNo = idx,
                Note = string.IsNullOrWhiteSpace(s.Aciklama) ? null : s.Aciklama,
                Variants = new List<object>(),
            });
        }

        if (linesOut.Count == 0)
            throw new InvalidOperationException("RAW: en az bir satır gerekli.");

        return new DiaInvoiceAddCardInput
        {
            KeyPrjProje = h.TargetProjeKey is > 0 ? h.TargetProjeKey : null,
            KeyScfCariKart = h.TargetCariKey,
            KeyScfCariKartAdresleri = h.TargetCariAdresKey is > 0 ? h.TargetCariAdresKey : null,
            KeyScfCariKartYetkili = resolvedCariYetkiliKey is > 0 ? resolvedCariYetkiliKey : null,
            KeyScfOdemePlani = h.TargetOdemePlaniKey is > 0 ? h.TargetOdemePlaniKey : null,
            KeySisSubeSource = req.TargetSubeKey,
            KeySisDepoSource = req.TargetDepoKey,
            KeySisDoviz = h.TargetSisDovizKey,
            KeySisDovizRaporlama = h.TargetSisDovizRaporlamaKey is > 0 ? h.TargetSisDovizRaporlamaKey : null,
            Aciklama1 = string.IsNullOrWhiteSpace(h.CariName) ? null : h.CariName,
            BelgeNo2 = h.InvoiceNo,
            BelgeNo = h.InvoiceNo,
            Fisno = string.IsNullOrWhiteSpace(h.FisNo) ? null : h.FisNo.Trim(),
            DovizKuru = string.IsNullOrWhiteSpace(h.HeaderDovizKuru) ? headerKur : h.HeaderDovizKuru!.Trim(),
            RaporlamaDovizKuru = string.IsNullOrWhiteSpace(h.HeaderRaporlamaDovizKuru)
                ? headerKur
                : h.HeaderRaporlamaDovizKuru!.Trim(),
            Tarih = h.Date,
            Saat = h.Time,
            Turu = h.InvoiceTypeCode,
            Lines = linesOut,
        };
    }

    /// <summary>RAW satır sayıları — legacy ile aynı <see cref="FormatDiaLineDecimal"/> (DİA decimal(20,10) / yerelbirimfiyati limiti).</summary>
    private static string RawInvariantDecimal(decimal? v) => FormatDiaLineDecimal(v);

    private static System.Text.Json.JsonElement JsonEmptyObj() =>
        System.Text.Json.JsonDocument.Parse("{}").RootElement;

    private static System.Text.Json.JsonElement JsonStrEl(string? s) =>
        System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(s ?? string.Empty)).RootElement;

    private static bool IsSnapshotHizmetLine(InvoiceTransferLineSnapshotDto s)
    {
        var label = (s.LineTypeLabel ?? string.Empty).Trim().ToUpperInvariant();
        if (label.Contains("HIZ", StringComparison.Ordinal) || label.Contains("HZMT", StringComparison.Ordinal))
            return true;
        if (string.Equals(label, "H", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    private static DiaInvoiceLine CreateSyntheticLineFromSnapshot(InvoiceTransferLineSnapshotDto s, int idx, long sourceInvoiceKey)
    {
        var itemCode = MasterCodeNormalizer.Normalize(FirstNonEmpty(s.ItemCode, s.StokKartKodu)) ?? string.Empty;
        var preferHizmet = IsSnapshotHizmetLine(s);
        var lineKey = s.SourceLineKey is > 0
            ? s.SourceLineKey.Value
            : sourceInvoiceKey * 1_000_000L + idx + 1;

        var line = new DiaInvoiceLine
        {
            Key = lineKey,
            SiraNo = idx + 1,
            KalemTuruRaw = preferHizmet ? "HZMT" : "MLZM",
            Miktar = s.Miktar,
            BirimFiyati = s.BirimFiyati,
            Tutari = s.Tutar,
            SonBirimFiyati = s.BirimFiyati,
            YerelBirimFiyati = s.BirimFiyati,
            KdvDurumuRaw = "H",
            KdvYuzde = s.KdvYuzde,
            Kdv = s.KdvYuzde,
            KalemRef = new DiaLineStokHizmetRef
            {
                StokKartKodu = preferHizmet ? null : itemCode,
                HizmetKartKodu = preferHizmet ? itemCode : null,
                Aciklama = s.Aciklama
            },
            BirimRaw = JsonStrEl(s.BirimAdi),
            VariantsRaw = JsonEmptyObj(),
            KeySisDovizRaw = JsonEmptyObj(),
            ProjeRaw = JsonEmptyObj(),
            Dinamik1Raw = JsonEmptyObj(),
            Dinamik2Raw = JsonEmptyObj(),
            Dinamik00001Raw = JsonEmptyObj(),
            Dinamik00002Raw = JsonEmptyObj(),
            KeyScfIrsaliyeRaw = JsonEmptyObj(),
            KeyScfIrsaliyeKalemiRaw = JsonEmptyObj(),
            ExtraFields = new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.OrdinalIgnoreCase)
        };
        if (!string.IsNullOrWhiteSpace(s.DynamicBranch))
            line.ExtraFields["__dinamik__fatsube"] = JsonStrEl(s.DynamicBranch.Trim());
        return line;
    }

    private static DiaInvoiceDetail CreateSyntheticInvoiceDetailFromHeader(long invoiceKey, InvoiceTransferHeaderSnapshotDto h, List<DiaInvoiceLine> lines)
    {
        string? kur = null;
        if (h.ExchangeRate.HasValue)
            kur = h.ExchangeRate.Value.ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture);

        var je = JsonEmptyObj();
        return new DiaInvoiceDetail
        {
            Key = invoiceKey,
            FisNo = h.FisNo,
            BelgeNo = h.InvoiceNo,
            BelgeNo2 = h.InvoiceNo,
            Tarih = h.Date,
            Saat = string.IsNullOrWhiteSpace(h.Time) ? "12:00:00" : h.Time,
            Turu = h.InvoiceTypeCode,
            CariKartKodu = h.CariCode,
            CariUnvan = h.CariName,
            DovizTuru = h.CurrencyCode,
            DovizKuru = kur ?? "1.000000",
            RaporlamaDovizKuru = kur ?? "1.000000",
            Lines = lines,
            Altlar = new List<DiaInvoiceAlt>(),
            KeyPrjProjeRaw = je,
            KeyScfCariKartRaw = je,
            KeyScfCariKartRaw2 = je,
            KeyScfCariKartAdresleriRaw = je,
            KeyScfOdemePlaniRaw = je,
            KeySisSubeSourceRaw = je,
            KeySisDepoSourceRaw = je,
            KeySisDovizRaw = je,
            KeySisDovizRaporlamaRaw = je
        };
    }

    private async Task<(bool ok, string stage, string code, string message, int targetDonemKodu, long targetSubeKey, long targetDepoKey)> ResolveTargetContextAsync(
        InvoiceTransferRequestDto req,
        DiaInvoiceDetail src)
    {
        var ctx = await _dia.GetAuthorizedCompanyPeriodBranchAsync();
        var company = ctx.FirstOrDefault(c => c.FirmaKodu == req.TargetFirmaKodu);
        if (company == null)
        {
            // Bazı tenantlarda yetkili ağaç eksik/slow dönebiliyor. Transferi burada bloklama;
            // hedef dönem/şube/depo bilgilerini sis_firma_getir / yetkili listesinin yedekleri ile çöz.
            _logger.LogWarning(
                "Target company not found in authorized tree. Falling back to sis_firma_getir/period-branch list. targetFirmaKodu={Firma}",
                req.TargetFirmaKodu);
        }

        var periods = company?.Donemler?.Count > 0
            ? company.Donemler
            : (company?.DonemFallback?.Count > 0 ? company.DonemFallback : company?.DonemListFallback ?? new List<DiaAuthorizedPeriodItem>());
        if (periods.Count == 0)
            periods = await _dia.GetPeriodsByFirmaAsync(req.TargetFirmaKodu);
        if (periods.Count == 0)
            return (false, "target_period_resolve", "target_periods_empty", "Hedef firmada dönem listesi boş.", 0, 0, 0);

        var period = ResolvePeriodByInvoiceDate(periods, src.Tarih);
        if (period == null)
            return (false, "target_period_resolve", "target_period_unresolved", "Kaynak fatura tarihine uygun hedef dönem bulunamadı.", 0, 0, 0);

        var targetDonemKodu = period.DonemKodu;

        var branches = (company?.Subeler ?? new List<DiaAuthorizedBranchItem>())
            .Where(s => s.Key > 0 && !string.IsNullOrWhiteSpace(s.SubeAdi))
            .ToList();
        if (branches.Count == 0)
        {
            branches = await _dia.GetSubelerDepolarForFirmaAsync(req.TargetFirmaKodu, targetDonemKodu);
            branches = branches.Where(s => s.Key > 0 && !string.IsNullOrWhiteSpace(s.SubeAdi)).ToList();
        }
        if (branches.Count == 0)
            return (false, "target_branch_resolve", "target_branches_empty", "Hedef firmada şube bulunamadı.", targetDonemKodu, 0, 0);

        var chosenBranch = (req.TargetSubeKey > 0 ? branches.FirstOrDefault(b => b.Key == req.TargetSubeKey) : null) ?? branches[0];

        var depots = (chosenBranch.Depolar ?? new List<DiaAuthorizedDepotItem>())
            .Where(d => d.Key > 0 && !string.IsNullOrWhiteSpace(d.DepoAdi))
            .ToList();
        if (depots.Count == 0)
            return (false, "target_depot_resolve", "target_depot_empty", "Seçilen şubede depo bulunamadı.", targetDonemKodu, chosenBranch.Key, 0);

        var chosenDepot = (req.TargetDepoKey > 0 ? depots.FirstOrDefault(d => d.Key == req.TargetDepoKey) : null) ?? depots[0];

        return (true, string.Empty, string.Empty, string.Empty, targetDonemKodu, chosenBranch.Key, chosenDepot.Key);
    }

    private static DiaAuthorizedPeriodItem? ResolvePeriodByInvoiceDate(List<DiaAuthorizedPeriodItem> periods, string? sourceInvoiceDate)
    {
        if (periods.Count == 0) return null;

        if (!DateTime.TryParse(sourceInvoiceDate, out var invDate))
        {
            var def = periods.FirstOrDefault(p => string.Equals(p.Ontanimli, "t", StringComparison.OrdinalIgnoreCase));
            return def ?? periods[0];
        }

        var exact = periods.FirstOrDefault(p =>
            DateTime.TryParse(p.BaslangicTarihi, out var b) &&
            DateTime.TryParse(p.BitisTarihi, out var e) &&
            invDate.Date >= b.Date && invDate.Date <= e.Date);
        if (exact != null) return exact;

        var yearMatch = periods.FirstOrDefault(p =>
            DateTime.TryParse(p.BaslangicTarihi, out var b) &&
            DateTime.TryParse(p.BitisTarihi, out var e) &&
            invDate.Year >= b.Year && invDate.Year <= e.Year);
        if (yearMatch != null) return yearMatch;

        var def2 = periods.FirstOrDefault(p => string.Equals(p.Ontanimli, "t", StringComparison.OrdinalIgnoreCase));
        return def2 ?? periods[0];
    }

    private static string? NormalizeDynamicBranch(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw
            .Replace("\u00A0", " ") // NBSP
            .Replace("\u200B", "")  // zero-width
            .Replace("\u200C", "")
            .Replace("\u200D", "")
            .Replace("\uFEFF", "")
            .Trim();

        if (string.IsNullOrWhiteSpace(s)) return null;

        // Placeholder değerler ("---", "-", "—") boş kabul edilmeli.
        if (System.Text.RegularExpressions.Regex.IsMatch(s, @"^[-—–]+$"))
            return null;

        var u = s.Trim().ToUpperInvariant();
        if (u is "0" or "NULL" or "UNDEFINED" or "NONE" or "N/A")
            return null;

        return s;
    }

    private async Task<(long? targetOdemePlaniKey, long? targetBankaOdemePlaniKey, long? targetBankaHesabiKey)> ResolveTargetOdemePlaniAndBankingAsync(InvoiceTransferRequestDto req, DiaInvoiceDetail src, CancellationToken ct)
    {
        string? srcOdemePlaniKod = null;
        var srcOdemePlaniKey = ParseFirstLong(src.KeyScfOdemePlaniRaw);
        string? srcBankPlanKodu = null;
        string? srcOdemePlaniAck = null;

        // NOT: scf_fatura_listele bazı tenantlarda filters parametresini ignore edebiliyor.
        // Bu da [_key]=... ile istenen kaydı bulmak için uzun taramalara yol açıp aktarımı kilitleyebiliyor.
        // Bu yüzden ödeme planını list view'den okumuyoruz; direkt key->kodu çözümü kullanıyoruz.

        // Fallback: detail'daki key'den çöz.
        if (string.IsNullOrWhiteSpace(srcOdemePlaniKod))
            srcOdemePlaniKod = ParseCode(src.KeyScfOdemePlaniRaw, "kodu", "odemeplani", "odemeplani_kodu");

        // Eğer hala boşsa: ayrıntılı listeden (scf_fatura_listele_ayrintili) ödeme planı key'ini bulmayı dene.
        if (string.IsNullOrWhiteSpace(srcOdemePlaniKod) && (srcOdemePlaniKey is null or 0))
        {
            try
            {
                var keyFromDetail = await _dia.FindInvoiceOdemePlaniKeyFromDetailAsync(req.SourceFirmaKodu, req.SourceDonemKodu, src.Key);
                if (keyFromDetail is > 0)
                {
                    srcOdemePlaniKey = keyFromDetail;
                }
            }
            catch
            {
                // ignore
            }
        }

        if (string.IsNullOrWhiteSpace(srcOdemePlaniKod) && srcOdemePlaniKey is > 0)
        {
            try
            {
                // Dönem uyuşmazlığı olabiliyor; dönem fallback ile dene.
                var donemCandidates = new List<int>();
                if (req.SourceDonemKodu > 0) donemCandidates.Add(req.SourceDonemKodu);
                try
                {
                    var periods = await _dia.GetPeriodsByFirmaAsync(req.SourceFirmaKodu);
                    donemCandidates.AddRange(periods.Select(p => p.DonemKodu).Where(x => x > 0));
                }
                catch { /* ignore */ }

                foreach (var d in donemCandidates.Distinct())
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var info = await _dia.GetOdemePlaniInfoByKeyAsync(req.SourceFirmaKodu, d, srcOdemePlaniKey.Value);
                        srcOdemePlaniKod = info.kodu;
                        srcOdemePlaniAck = info.aciklama;
                        srcBankPlanKodu = info.ikkKodu;
                        if (!string.IsNullOrWhiteSpace(srcOdemePlaniKod))
                            break;
                    }
                    catch
                    {
                        // try next donem
                    }
                }
            }
            catch
            {
                // ignore: optional fallback only
            }
        }

        static bool LooksEmptyPlanCode(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return true;
            var t = s.Trim();
            return t == "0" || t == "-" || t.Equals("NULL", StringComparison.OrdinalIgnoreCase);
        }

        if (LooksEmptyPlanCode(srcOdemePlaniKod))
            return (null, null, null);

        var targetOdemeKey = await _dia.FindOdemePlaniKeyByCodeAsync(req.TargetFirmaKodu, req.TargetDonemKodu, srcOdemePlaniKod!);
        if (!targetOdemeKey.HasValue)
            throw new InvalidOperationException($"Hedef firmada ödeme planı bulunamadı. Kaynak ödeme planı kodu: {srcOdemePlaniKod}");

        // Debug: kaynak/ hedef ödeme planı eşleşmesini görünür yap.
        try
        {
            var tInfo = await _dia.GetOdemePlaniInfoByKeyAsync(req.TargetFirmaKodu, req.TargetDonemKodu, targetOdemeKey.Value);
            _logger.LogInformation(
                "Transfer odeme plani resolved: sourceInvoiceKey={Invoice} sourceKey={SourceKey} sourceKodu={SourceKodu} sourceAck={SourceAck} targetKey={TargetKey} targetKodu={TargetKodu} targetAck={TargetAck}",
                src.Key, srcOdemePlaniKey, srcOdemePlaniKod, srcOdemePlaniAck ?? "-", targetOdemeKey, tInfo.kodu ?? "-", tInfo.aciklama ?? "-");
        }
        catch
        {
            _logger.LogInformation(
                "Transfer odeme plani resolved: sourceInvoiceKey={Invoice} sourceKey={SourceKey} sourceKodu={SourceKodu} sourceAck={SourceAck} targetKey={TargetKey}",
                src.Key, srcOdemePlaniKey, srcOdemePlaniKod, srcOdemePlaniAck ?? "-", targetOdemeKey);
        }

        // Kaynak fatura payload'unda banka ödeme planı kodu (ikk) boş gelebiliyor; hedef firmadaki aynı kodlu
        // ödeme planı master kaydında ikk doluysa (ör. "BANKA" planı → banka ödeme planı), havuzla birebir eşleme buradan tamamlanır.
        if (string.IsNullOrWhiteSpace((srcBankPlanKodu ?? string.Empty).Trim()))
        {
            try
            {
                var tPlan = await _dia.GetOdemePlaniInfoByKeyAsync(req.TargetFirmaKodu, req.TargetDonemKodu, targetOdemeKey!.Value);
                var ikk = (tPlan.ikkKodu ?? string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace(ikk))
                    srcBankPlanKodu = ikk;
            }
            catch
            {
                // ignore: banka planı zorunlu değilse devam
            }
        }

        // Banka ödeme planı + banka hesabı kuralı:
        // Kaynak ödeme planının "ikkkodu" doluysa, bu banka ödeme planı kodudur.
        // Banka ödeme planı hedefte olmalı ve bağlı banka hesabı da bulunmalı.
        //
        // Not: Daha önce hedef ödeme planındaki ikkkodu baz alınıyordu; bu, kaynakta banka planı yokken
        // hedefte varmış gibi gereksiz DIA çağrılarına ve transferin iptal olmasına yol açabiliyordu.
        var bankPlanKodu = (srcBankPlanKodu ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(bankPlanKodu))
            return (targetOdemeKey, null, null);

        var targetBankPlanKey = await _dia.FindBankaOdemePlaniKeyByCodeAsync(req.TargetFirmaKodu, req.TargetDonemKodu, bankPlanKodu);
        if (!targetBankPlanKey.HasValue)
            throw new InvalidOperationException($"Hedef firmada banka ödeme planı bulunamadı. Ödeme planı={srcOdemePlaniKod} banka ödeme planı kodu={bankPlanKodu}");

        var bankPlanInfo = await _dia.GetBankaOdemePlaniInfoByKeyAsync(req.TargetFirmaKodu, req.TargetDonemKodu, targetBankPlanKey.Value);
        var targetBankAccountKey = bankPlanInfo.keyBcsBankahesabi;
        var targetBankAccountKodu = (bankPlanInfo.bankahesapKodu ?? string.Empty).Trim();

        if (!targetBankAccountKey.HasValue && !string.IsNullOrWhiteSpace(targetBankAccountKodu))
            targetBankAccountKey = await _dia.FindBankaHesabiKeyByHesapKoduAsync(req.TargetFirmaKodu, req.TargetDonemKodu, targetBankAccountKodu);

        if (!targetBankAccountKey.HasValue)
            throw new InvalidOperationException($"Banka ödeme planına bağlı banka hesabı bulunamadı. bankaPlanKodu={bankPlanKodu} bankahesapKodu={FirstNonEmpty(targetBankAccountKodu, "-")}");

        return (targetOdemeKey, targetBankPlanKey, targetBankAccountKey);
    }

    public static string BuildDuplicateKey(long sourceInvoiceKey, long sourceLineKey, int targetFirmaKodu, int targetDonemKodu, long targetSubeKey, long targetDepoKey)
        => $"{sourceInvoiceKey}|{sourceLineKey}|{targetFirmaKodu}|{targetDonemKodu}|{targetSubeKey}|{targetDepoKey}";

    public (TransferStatus status, int bekleyenKalemSayisi) GetSourceInvoiceTransferSnapshot(long sourceInvoiceKey, int? totalLineCount = null)
    {
        var transferredCount = _sourceTransferredLines.TryGetValue(sourceInvoiceKey, out var lines) ? lines.Count : 0;
        var total = totalLineCount ?? (_sourceInvoiceLineTotals.TryGetValue(sourceInvoiceKey, out var knownTotal) ? knownTotal : 0);

        if (transferredCount <= 0)
        {
            return (TransferStatus.Bekliyor, total > 0 ? total : 0);
        }

        if (total > 0 && transferredCount >= total)
        {
            return (TransferStatus.Aktarildi, 0);
        }

        var pending = total > 0 ? Math.Max(0, total - transferredCount) : 0;
        return (TransferStatus.Kismi, pending);
    }

    public (TransferStatus status, string? targetFirmaKodu, string? targetSubeKodu, string? targetDonemKodu) GetSourceLineTransferSnapshot(long sourceInvoiceKey, long sourceLineKey)
    {
        var lineDict = _sourceTransferredLines.TryGetValue(sourceInvoiceKey, out var dict) ? dict : null;
        var isTransferred = lineDict is not null && lineDict.ContainsKey(sourceLineKey);
        if (!isTransferred)
            return (TransferStatus.Bekliyor, null, null, null);

        var ctxKey = BuildSourceLineContextKey(sourceInvoiceKey, sourceLineKey);
        if (_sourceLineTargets.TryGetValue(ctxKey, out var ctx))
        {
            return (TransferStatus.Aktarildi, ctx.TargetFirmaKodu, ctx.TargetSubeKodu, ctx.TargetDonemKodu);
        }

        return (TransferStatus.Aktarildi, null, null, null);
    }

    private static void MarkSourceTransferState(long sourceInvoiceKey, int totalLineCount, IEnumerable<long> transferredLineKeys, int targetFirmaKodu, long targetSubeKey, int targetDonemKodu)
    {
        if (totalLineCount > 0)
            _sourceInvoiceLineTotals[sourceInvoiceKey] = totalLineCount;

        var lineDict = _sourceTransferredLines.GetOrAdd(sourceInvoiceKey, _ => new ConcurrentDictionary<long, byte>());
        foreach (var lineKey in transferredLineKeys)
        {
            lineDict.TryAdd(lineKey, 1);
            _sourceLineTargets[BuildSourceLineContextKey(sourceInvoiceKey, lineKey)] = new SourceLineTargetContext
            {
                TargetFirmaKodu = targetFirmaKodu.ToString(),
                TargetSubeKodu = targetSubeKey.ToString(),
                TargetDonemKodu = targetDonemKodu.ToString()
            };
        }
    }

    private static string BuildSourceLineContextKey(long sourceInvoiceKey, long sourceLineKey) => $"{sourceInvoiceKey}|{sourceLineKey}";

    public int ClearTransferState(long? sourceInvoiceKey, long? sourceLineKey)
    {
        // İhtiyaç: Kullanıcı DIA'da hedef kaydı silerse, bizdeki "aktarılmış" state'i sıfırlamak gerekir.
        // Bu method hem UI hem de diag endpoint'inden çağrılır.
        var cleared = 0;

        static bool DedupKeyMatchesSourceInvoice(string dedupKey, long invoiceKey)
        {
            var parts = dedupKey.Split('|', StringSplitOptions.None);
            return parts.Length >= 3 && long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var inv) && inv == invoiceKey;
        }

        if (sourceInvoiceKey is > 0 && sourceLineKey is > 0)
        {
            if (_sourceTransferredLines.TryGetValue(sourceInvoiceKey.Value, out var dict))
            {
                if (dict.TryRemove(sourceLineKey.Value, out _)) cleared++;
                if (dict.IsEmpty) _sourceTransferredLines.TryRemove(sourceInvoiceKey.Value, out _);
            }
            _sourceLineTargets.TryRemove(BuildSourceLineContextKey(sourceInvoiceKey.Value, sourceLineKey.Value), out _);

            // Duplicate key'leri bu line için temizle (firma/dönem/şube/depo kombinasyonları bilinmediği için prefix ile).
            var prefix = $"{sourceInvoiceKey.Value}|{sourceLineKey.Value}|";
            foreach (var k in _duplicateRegistry.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
            {
                if (_duplicateRegistry.TryRemove(k, out _)) cleared++;
            }

            foreach (var dk in _dedup.Keys.Where(k => DedupKeyMatchesSourceInvoice(k, sourceInvoiceKey.Value)).ToList())
            {
                if (_dedup.TryRemove(dk, out _)) cleared++;
            }
        }
        else if (sourceInvoiceKey is > 0)
        {
            if (_sourceTransferredLines.TryRemove(sourceInvoiceKey.Value, out var dict))
            {
                cleared += dict.Count;
                foreach (var lk in dict.Keys)
                    _sourceLineTargets.TryRemove(BuildSourceLineContextKey(sourceInvoiceKey.Value, lk), out _);
            }

            var prefix = $"{sourceInvoiceKey.Value}|";
            foreach (var k in _duplicateRegistry.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
            {
                if (_duplicateRegistry.TryRemove(k, out _)) cleared++;
            }
            _sourceInvoiceLineTotals.TryRemove(sourceInvoiceKey.Value, out _);

            foreach (var dk in _dedup.Keys.Where(k => DedupKeyMatchesSourceInvoice(k, sourceInvoiceKey.Value)).ToList())
            {
                if (_dedup.TryRemove(dk, out _)) cleared++;
            }
        }
        else
        {
            cleared += _duplicateRegistry.Count;
            _duplicateRegistry.Clear();
            cleared += _sourceTransferredLines.Sum(kvp => kvp.Value.Count);
            _sourceTransferredLines.Clear();
            _sourceLineTargets.Clear();
            _sourceInvoiceLineTotals.Clear();
            _dedup.Clear();
        }

        MaybeSaveTransferState();
        return cleared;
    }

    public object GetTransferStateDebugSnapshot(long? sourceInvoiceKey = null)
    {
        try
        {
            var dupCount = _duplicateRegistry.Count;
            var dedupCount = _dedup.Count;
            var invCount = _sourceTransferredLines.Count;
            var lineCount = _sourceTransferredLines.Sum(kvp => kvp.Value.Count);

            if (sourceInvoiceKey is > 0)
            {
                var prefix = $"{sourceInvoiceKey.Value}|";
                var dupKeysForInvoice = _duplicateRegistry.Keys
                    .Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
                    .Take(50)
                    .ToList();
                var lineKeys = _sourceTransferredLines.TryGetValue(sourceInvoiceKey.Value, out var dict)
                    ? dict.Keys.Take(200).ToList()
                    : new List<long>();

                return new
                {
                    duplicateRegistryCount = dupCount,
                    dedupCount,
                    sourceInvoicesWithAnyTransferredLines = invCount,
                    totalTransferredLines = lineCount,
                    sourceInvoiceKey = sourceInvoiceKey.Value,
                    sourceTransferredLineKeys = lineKeys,
                    duplicateRegistryKeysForInvoice = dupKeysForInvoice,
                };
            }

            return new
            {
                duplicateRegistryCount = dupCount,
                dedupCount,
                sourceInvoicesWithAnyTransferredLines = invCount,
                totalTransferredLines = lineCount,
            };
        }
        catch (Exception ex)
        {
            return new { error = ex.Message };
        }
    }

    private sealed class SourceLineTargetContext
    {
        public string? TargetFirmaKodu { get; init; }
        public string? TargetSubeKodu { get; init; }
        public string? TargetDonemKodu { get; init; }
    }

    private async Task<DiaInvoiceAddCardInput> BuildTargetCardAsync(InvoiceTransferRequestDto req, DiaInvoiceDetail src, List<DiaInvoiceLine> lines, string? dynamicColumn, CancellationToken ct, bool snapshotFirst = false)
    {
        // Eşleştirme: carikartkodu (kaynak header __carikartkodu / JSON carikartkodu).
        // Not: bazı tenantlarda __carikartkodu boş gelebilir; KeyScfCariKartRaw ise sadece numeric _key döndürür.
        // Bu durumda, kaynak cari key ile kod+unvanı tekrar çekmek gerekir.
        var srcCariKod = FirstNonEmpty(
            src.CariKartKodu,
            src.CariKartKoduPlain,
            src.CariKoduSnake,
            src.CariKoduCompact,
            TryCariKodFromInvoiceExtra(src),
            ParseCode(src.KeyScfCariKartRaw, "carikartkodu", "__carikartkodu", "cari_kodu", "carikodu", "kodu"),
            ParseCode(src.KeyScfCariKartRaw2, "carikartkodu", "__carikartkodu", "cari_kodu", "carikodu", "kodu"));

        var srcCariUnvan = FirstNonEmpty(src.CariUnvan, src.CariUnvanPlain);
        var srcCariKey = ParseFirstLong(src.KeyScfCariKartRaw) ?? ParseFirstLong(src.KeyScfCariKartRaw2);

        static bool LooksLikeMissingCode(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return true;
            var t = s.Trim();
            return t == "0" || t == "-" || t.Equals("NULL", StringComparison.OrdinalIgnoreCase);
        }

        if (!snapshotFirst && (LooksLikeMissingCode(srcCariKod) || string.IsNullOrWhiteSpace(srcCariUnvan)) && srcCariKey is > 0)
        {
            try
            {
                var srcInfo = await GetCariInfoByKeyCachedAsync(req.SourceFirmaKodu, req.SourceDonemKodu, srcCariKey.Value, ct);
                if (LooksLikeMissingCode(srcCariKod) && !string.IsNullOrWhiteSpace(srcInfo.kodu))
                    srcCariKod = srcInfo.kodu;
                if (string.IsNullOrWhiteSpace(srcCariUnvan) && !string.IsNullOrWhiteSpace(srcInfo.unvan))
                    srcCariUnvan = srcInfo.unvan;
            }
            catch
            {
                // ignore: fallback only
            }
        }

        // Son fallback: bazı tenantlarda scf_fatura_getir cari bilgilerini hiç dönmeyebilir.
        // Bu durumda scf_fatura_listele'den header cari bilgilerini çek.
        if (!snapshotFirst && (LooksLikeMissingCode(srcCariKod) || string.IsNullOrWhiteSpace(srcCariUnvan) || !(srcCariKey is > 0)))
        {
            var fromList = await GetInvoiceCariFromListCachedAsync(req.SourceFirmaKodu, req.SourceDonemKodu, src.Key, ct);
            if (LooksLikeMissingCode(srcCariKod) && !string.IsNullOrWhiteSpace(fromList.cariKodu))
                srcCariKod = fromList.cariKodu;
            if (string.IsNullOrWhiteSpace(srcCariUnvan) && !string.IsNullOrWhiteSpace(fromList.cariUnvan))
                srcCariUnvan = fromList.cariUnvan;
            if (!(srcCariKey is > 0) && fromList.cariKey is > 0)
                srcCariKey = fromList.cariKey;

            if ((LooksLikeMissingCode(srcCariKod) || string.IsNullOrWhiteSpace(srcCariUnvan)) && srcCariKey is > 0)
            {
                try
                {
                    var srcInfo = await GetCariInfoByKeyCachedAsync(req.SourceFirmaKodu, req.SourceDonemKodu, srcCariKey.Value, ct);
                    if (LooksLikeMissingCode(srcCariKod) && !string.IsNullOrWhiteSpace(srcInfo.kodu))
                        srcCariKod = srcInfo.kodu;
                    if (string.IsNullOrWhiteSpace(srcCariUnvan) && !string.IsNullOrWhiteSpace(srcInfo.unvan))
                        srcCariUnvan = srcInfo.unvan;
                }
                catch
                {
                    // ignore: fallback only
                }
            }
        }

        // Cari kodu "as-is" mantığıyla ele al: sadece trim.
        // Not: MasterCodeNormalizer noktalama/boşluk silerek kodu "değiştirmişiz" gibi gösteriyor.
        var srcCariKodTrim = (srcCariKod ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(srcCariKodTrim) || srcCariKodTrim == "0")
        {
            _logger.LogWarning(
                "Transfer cari resolve failed: sourceInvoiceKey={Invoice} srcCariKod={CariKod} srcCariUnvan={CariUnvan} srcCariKey={CariKey} keyScfCariKartRawKind={Kind} keyScfCariKartRaw2Kind={Kind2}",
                req.SourceInvoiceKey,
                srcCariKod ?? "-",
                srcCariUnvan ?? "-",
                srcCariKey?.ToString() ?? "-",
                src.KeyScfCariKartRaw.ValueKind.ToString(),
                src.KeyScfCariKartRaw2.ValueKind.ToString());
            throw new InvalidOperationException("Kaynak faturadan carikartkodu okunamadı.");
        }
        _logger.LogInformation("Transfer cari resolve start: sourceCariKodu={SourceKodu} sourceCariUnvan={SourceUnvan} sourceCariKey={SourceKey} targetFirma={TargetFirma} targetDonem={TargetDonem}",
            srcCariKod, srcCariUnvan, srcCariKey, req.TargetFirmaKodu, req.TargetDonemKodu);

        // CRITICAL: Cari eşleşmesi yalnızca KOD ile yapılmalı.
        // Ünvan ile fallback yapmak yanlış cariye eşleşip (hesap kodu/ünvan/yetkili) hatalı aktarım oluşturabiliyor.
        var targetCariKey = await _dia.FindCariKeyByCodeAsync(req.TargetFirmaKodu, req.TargetDonemKodu, srcCariKodTrim);
        if (!targetCariKey.HasValue)
            throw new InvalidOperationException($"Hedef firmada cari bulunamadı. Kaynak carikartkodu: {srcCariKodTrim} (ünvan={srcCariUnvan ?? "-"})");

        var targetCariInfo = await GetCariInfoByKeyCachedAsync(req.TargetFirmaKodu, req.TargetDonemKodu, targetCariKey.Value, ct);
        _logger.LogInformation("Transfer cari mapping: sourceCariKodu={SourceKodu} sourceCariUnvan={SourceUnvan} resolvedTargetCariKey={TargetKey} targetCariKodu={TargetKodu} targetCariUnvan={TargetUnvan}",
            srcCariKod, srcCariUnvan, targetCariKey, targetCariInfo.kodu, targetCariInfo.unvan);

        var targetCariAdresKey = await _dia.FindCariAddressKeyAsync(req.TargetFirmaKodu, req.TargetDonemKodu, targetCariKey.Value);

        long? targetOdemePlaniKey = null;
        long? targetBankaOdemePlaniKey = null;
        long? targetBankaHesabiKey = null;
        if (!snapshotFirst)
        {
            try
            {
                var banking = await ResolveTargetOdemePlaniAndBankingAsync(req, src, ct);
                targetOdemePlaniKey = banking.targetOdemePlaniKey;
                targetBankaOdemePlaniKey = banking.targetBankaOdemePlaniKey;
                targetBankaHesabiKey = banking.targetBankaHesabiKey;
            }
            catch (InvalidOperationException ex)
            {
                throw new InvalidOperationException(ex.Message);
            }
        }

        var srcProjeKod = ParseCode(src.KeyPrjProjeRaw, "kodu", "projekodu");
        long? targetProjeKey = null;
        if (!string.IsNullOrWhiteSpace(srcProjeKod))
            targetProjeKey = await _dia.FindProjeKeyByCodeAsync(req.TargetFirmaKodu, req.TargetDonemKodu, srcProjeKod);

        // Cari Yetkili: kaynakta yetkili kodu varsa hedefte de aynı koddan key resolve edip set et.
        long? targetCariYetkiliKey = null;
        var sourceHasYetkili = false;
        try
        {
            string? srcYetkiliKod = null;
            if (src.ExtraFields != null)
            {
                // bazı tenantlarda düz kolon gelebiliyor
                if (src.ExtraFields.TryGetValue("yetkikodu", out var y))
                {
                    srcYetkiliKod = y.ValueKind == System.Text.Json.JsonValueKind.String ? y.GetString() : y.GetRawText();
                }

                // bazı tenantlarda _key_scf_carikart_yetkili objesi gelebiliyor (önce gerçek kod alanı)
                if (string.IsNullOrWhiteSpace(srcYetkiliKod) &&
                    src.ExtraFields.TryGetValue("_key_scf_carikart_yetkili", out var yo))
                    srcYetkiliKod = ParseCode(yo, "yetkikodu", "kodu", "kod");
            }

            if (IsJunkYetkiliKodu(srcYetkiliKod))
                srcYetkiliKod = null;

            if (!string.IsNullOrWhiteSpace(srcYetkiliKod))
            {
                sourceHasYetkili = true;
                targetCariYetkiliKey = await _dia.FindCariYetkiliKeyByCodeAsync(req.TargetFirmaKodu, req.TargetDonemKodu, srcCariKodTrim, srcYetkiliKod);
            }
        }
        catch
        {
            // optional
        }

        // Kaynak dövizleri: DIA çoğu tenantta _key_sis_doviz alanını "sadece key" olarak döndürür (2328 gibi).
        // Bu yüzden önce key->kod/adi çöz, sonra string alanlara düş.
        // Snapshot-first: kaynak tarafında ekstra sis_doviz listesi çağırma; header string kodları kullanılır.
        var sourceCurrencies = snapshotFirst
            ? new List<DiaAuthorizedCurrencyItem>()
            : await _dia.GetCurrenciesAsync(req.SourceFirmaKodu, req.SourceDonemKodu);
        static string? CurrencyCodeFromList(List<DiaAuthorizedCurrencyItem> list, long? key)
        {
            if (!key.HasValue || key.Value <= 0) return null;
            var it = list.FirstOrDefault(x => x.Key == key.Value);
            if (it == null) return null;
            return FirstNonEmpty(it.Kodu, it.Adi, it.UzunAdi);
        }

        var srcDovizKod = FirstNonEmpty(
            CurrencyCodeFromList(sourceCurrencies, ParseFirstLong(src.KeySisDovizRaw)),
            ParseCode(src.KeySisDovizRaw, "kodu", "dovizkodu", "dovizadi", "kod", "adi", "uzunadi"),
            src.DovizTuru);

        var srcRaporlamaDovizKod = FirstNonEmpty(
            CurrencyCodeFromList(sourceCurrencies, ParseFirstLong(src.KeySisDovizRaporlamaRaw)),
            ParseCode(src.KeySisDovizRaporlamaRaw, "kodu", "dovizkodu", "dovizadi", "kod", "adi", "uzunadi"),
            src.DovizTuru);

        // Not: bazı tenantlarda ayrıntılı listede/satırda döviz her zaman TL gelebilir.
        // Satır dövizi sadece açıkça farklıysa dikkate alacağız.
        var srcLineDovizKod = lines
            .Select(l => CurrencyCodeFromList(sourceCurrencies, ParseFirstLong(l.KeySisDovizRaw)) ?? ParseCode(l.KeySisDovizRaw, "kodu", "dovizkodu", "dovizadi", "kod", "adi", "uzunadi"))
            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        var srcLineRaporlamaDovizKod = string.Empty;
        var srcLineDovizAdi = string.Empty;
        var srcLineDovizKodu = srcLineDovizKod;
        var srcRaporlamaDovizKuru = src.RaporlamaDovizKuru;

        _logger.LogInformation("Transfer source currency debug: sourceInvoiceCurrencyCode={InvoiceCode} sourceInvoiceReportCurrencyCode={InvoiceReportCode} sourceSelectedLineCurrencyCode={LineCode} sourceSelectedLineReportCurrencyCode={LineReportCode} sourceLineDovizAdi={LineDovizAdi} sourceLineDovizKodu={LineDovizKodu} sourceLineRaporlamaDovizKuru={LineRaporlamaKuru}",
            srcDovizKod, srcRaporlamaDovizKod, srcLineDovizKod, srcLineRaporlamaDovizKod, srcLineDovizAdi, srcLineDovizKodu, srcRaporlamaDovizKuru);

        var targetCtx = await _dia.GetAuthorizedCompanyPeriodBranchAsync();
        var targetFirma = targetCtx.FirstOrDefault(x => x.FirmaKodu == req.TargetFirmaKodu);
        var targetDovizler = targetFirma?.Dovizler ?? new List<DiaAuthorizedCurrencyItem>();
        if (targetDovizler.Count == 0)
            targetDovizler = await _dia.GetCurrenciesAsync(req.TargetFirmaKodu, req.TargetDonemKodu);

        _logger.LogInformation("Transfer target currency list: {TargetCurrencies}",
            string.Join(" | ", targetDovizler.Select(d => $"_key={d.Key},kodu={d.Kodu},adi={d.Adi},uzunadi={d.UzunAdi},ana={ParseBool(d.AnaDovizMiRaw)},rapor={ParseBool(d.RaporlamaDovizMiRaw)}")));

        var targetMainCurrency = targetDovizler.FirstOrDefault(d => ParseBool(d.AnaDovizMiRaw))
                                 ?? targetDovizler.FirstOrDefault(d => CanonicalCurrency(FirstNonEmpty(d.Kodu, d.Adi, d.UzunAdi)) == "TL")
                                 ?? targetDovizler.FirstOrDefault();
        var targetReportCurrency = targetDovizler.FirstOrDefault(d => ParseBool(d.RaporlamaDovizMiRaw))
                                   ?? targetMainCurrency;

        // Başlık dövizi: fiş üstündeki döviz (USD/EUR vb.). Satır dövizi (TL) farklı olabildiği için
        // asla satır dövizini başlık _key_sis_doviz çözümünde önceliklendirme.
        var headerDovizKod = FirstNonEmpty(srcDovizKod, src.DovizTuru);
        var invoiceOrMainDovizKod = FirstNonEmpty(srcDovizKod, targetMainCurrency?.Kodu, targetMainCurrency?.Adi, targetMainCurrency?.UzunAdi, "TL");
        var reportOrMainDovizKod = FirstNonEmpty(srcRaporlamaDovizKod, targetReportCurrency?.Kodu, targetReportCurrency?.Adi, targetReportCurrency?.UzunAdi, invoiceOrMainDovizKod);

        // ÖNEMLİ: Fatura dövizi (header) ile kalem dövizleri farklı olabilir.
        // Header dövizini kalemlerden "yükseltmeyiz"; header ne ise o gönderilir.

        var (targetDovizKey, targetDovizMatch) = await ResolveTargetCurrencyWithFallbackAsync(req.TargetFirmaKodu, req.TargetDonemKodu, targetDovizler, headerDovizKod ?? invoiceOrMainDovizKod);
        if (!targetDovizKey.HasValue)
            (targetDovizKey, targetDovizMatch) = await ResolveTargetCurrencyWithFallbackAsync(req.TargetFirmaKodu, req.TargetDonemKodu, targetDovizler, invoiceOrMainDovizKod);
        if (!targetDovizKey.HasValue)
            targetDovizKey = targetMainCurrency?.Key;
        if (!targetDovizKey.HasValue)
            throw new InvalidOperationException("Hedef firmada uygun döviz bulunamadı.");

        var (targetRaporlamaDovizKey, targetRaporMatch) = await ResolveTargetCurrencyWithFallbackAsync(req.TargetFirmaKodu, req.TargetDonemKodu, targetDovizler, reportOrMainDovizKod);
        if (!targetRaporlamaDovizKey.HasValue)
            targetRaporlamaDovizKey = targetReportCurrency?.Key ?? targetDovizKey;

        _logger.LogInformation("Transfer currency resolved: sourceHeaderCurrency={SourceCurrency} resolvedTargetKeySisDoviz={Doviz} via={DovizMatch} resolvedTargetKeySisDovizRaporlama={Raporlama} via={RaporMatch}",
            headerDovizKod,
            targetDovizKey, targetDovizMatch, targetRaporlamaDovizKey, targetRaporMatch);

        // Kur: kaynakta boşsa ve döviz TL değilse, sis_doviz_kuru_listele üzerinden tarih bazlı çek.
        var srcHeaderKuru = string.IsNullOrWhiteSpace(src.DovizKuru) ? null : src.DovizKuru;
        if (!snapshotFirst && string.IsNullOrWhiteSpace(srcHeaderKuru) && CanonicalCurrency(headerDovizKod) != "TL")
        {
            var srcHeaderKey = ParseFirstLong(src.KeySisDovizRaw);
            if (srcHeaderKey is > 0 && !string.IsNullOrWhiteSpace(src.Tarih))
            {
                try
                {
                    srcHeaderKuru = await _dia.FindDovizKuruByDateAsync(req.SourceFirmaKodu, req.SourceDonemKodu, srcHeaderKey.Value, src.Tarih);
                }
                catch { /* ignore */ }
            }
        }

        var resolvedLines = new List<DiaInvoiceAddLineInput>();

        // Snapshot override: frontend'den gelen satır değerlerini (miktar/fiyat/tutar/birim) birebir taşımak için.
        // Not: stok/hizmet eşleştirmesi yine hedefte kart çözümleme ile yapılır (key gerektirir).
        var snapshotByLineKey = new Dictionary<long, InvoiceTransferLineSnapshotDto>();
        if (req.SelectedLineSnapshots is { Count: > 0 })
        {
            foreach (var s in req.SelectedLineSnapshots)
            {
                if (s?.SourceLineKey is > 0 && !snapshotByLineKey.ContainsKey(s.SourceLineKey.Value))
                    snapshotByLineKey[s.SourceLineKey.Value] = s;
            }
        }

        // Kalemlerde "liste ayrıntılı / view" tarafında bulunan kolonları da taşımak için
        // scf_fatura_kalemi_liste_view vb. servislerden satır satır primitive alanları çekip lineKey ile eşleştiriyoruz.
        // (İrsaliye ilişkili alanlar sanitize aşamasında zaten atılır.)
        Dictionary<long, Dictionary<string, System.Text.Json.JsonElement>>? sourceLineViewExtras = null;
        if (!snapshotFirst)
        {
            try
            {
                var viewRows = await _dia.GetInvoiceLinesViewAsync(req.SourceFirmaKodu, req.SourceDonemKodu, req.SourceInvoiceKey);
                if (viewRows is { Count: > 0 })
                {
                    sourceLineViewExtras = new Dictionary<long, Dictionary<string, System.Text.Json.JsonElement>>();
                    foreach (var r in viewRows)
                    {
                        if (r.ValueKind != System.Text.Json.JsonValueKind.Object) continue;

                        static long? ReadLong(System.Text.Json.JsonElement e, params string[] names)
                        {
                            foreach (var n in names)
                            {
                                if (e.TryGetProperty(n, out var v))
                                {
                                    if (v.ValueKind == System.Text.Json.JsonValueKind.Number && v.TryGetInt64(out var n64)) return n64;
                                    if (v.ValueKind == System.Text.Json.JsonValueKind.String && long.TryParse(v.GetString(), out var p)) return p;
                                }
                            }
                            return null;
                        }

                        var lineKey = ReadLong(r, "faturakalemkey", "_key", "key", "kalemkey", "kalem_key");
                        if (lineKey is not > 0) continue;

                        var dict = new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.OrdinalIgnoreCase);
                        foreach (var p in r.EnumerateObject())
                            dict[p.Name] = p.Value;

                        sourceLineViewExtras[lineKey.Value] = dict;
                    }
                }
            }
            catch
            {
                // ignore: view endpoint tenant'a göre değişebilir; getir akışı yine çalışır.
            }
        }

        foreach (var l in lines)
        {
            ct.ThrowIfCancellationRequested();
            snapshotByLineKey.TryGetValue(l.Key, out var snap);

            var rawStok = MasterCodeNormalizer.Normalize(l.KalemRef?.StokKartKodu);
            var rawHizmet = MasterCodeNormalizer.Normalize(l.KalemRef?.HizmetKartKodu);
            var kalemTuruNorm = string.IsNullOrWhiteSpace(l.KalemTuruRaw)
                ? string.Empty
                : l.KalemTuruRaw.Trim().ToUpperInvariant();
            var preferHizmet =
                kalemTuruNorm.Contains("HZMT", StringComparison.Ordinal) ||
                kalemTuruNorm.Contains("HIZMET", StringComparison.Ordinal) ||
                kalemTuruNorm.Contains("HİZMET", StringComparison.Ordinal) ||
                (string.IsNullOrWhiteSpace(rawStok) && !string.IsNullOrWhiteSpace(rawHizmet));

            var stokKod = MasterCodeNormalizer.Normalize(FirstNonEmpty(snap?.StokKartKodu, preferHizmet ? FirstNonEmpty(rawHizmet, rawStok) : FirstNonEmpty(rawStok, rawHizmet)));
            var stokAciklama = FirstNonEmpty(snap?.Aciklama, l.KalemRef?.Aciklama);
            var dynRaw = l.ExtractDinamikSubelerRaw(dynamicColumn);
            if (string.IsNullOrWhiteSpace(stokKod))
                throw new InvalidOperationException($"Kaynak kalemde stok/hizmet kart kodu bulunamadı. lineKey={l.Key}");

            _logger.LogInformation("Transfer line resolve start: sourceInvoiceKey={Invoice} sourceLineKey={Line} sourceKalemTuru={KalemTuru} preferHizmet={PreferHizmet} sourceStokKodu={StokKod} sourceStokAciklama={StokAciklama}",
                req.SourceInvoiceKey, l.Key, l.KalemTuruRaw, preferHizmet, stokKod, stokAciklama);
            _logger.LogInformation("Transfer line dynamic debug: sourceInvoiceKey={Invoice} sourceLineKey={Line} source__dinamik__fatsube={DynRaw} normalized={Norm} calculatedTransferType={Type}",
                req.SourceInvoiceKey, l.Key, dynRaw ?? "-", NormalizeDynamicBranch(dynRaw) ?? "-", string.IsNullOrWhiteSpace(NormalizeDynamicBranch(dynRaw)) ? "FATURA" : "VİRMAN");

            var stockResolve = await _dia.ResolveTargetStockAsync(req.TargetFirmaKodu, req.TargetDonemKodu, stokKod, stokAciklama, preferHizmet);
            _logger.LogInformation("Transfer line resolve stock result: sourceStokKodu={StokKod} sourceStokAciklama={StokAciklama} resolvedTargetKalemTuruKey={KalemTuruKey} resolvedTargetStokKartKey={StokKartKey}",
                stokKod, stokAciklama, stockResolve.TargetKalemTuruKey, stockResolve.TargetStokKartKey);
            if (!stockResolve.TargetStokKartKey.HasValue)
                throw new InvalidOperationException(preferHizmet
                    ? $"Hedef firmada hizmet kartı bulunamadı: {stokKod}"
                    : $"Hedef firmada stok kartı bulunamadı: {stokKod}");
            if (!stockResolve.TargetKalemTuruKey.HasValue && !stockResolve.IsHizmetKart)
                throw new InvalidOperationException($"Stok bulundu ancak kalem türü eşleşmesi yok. Stok={stokKod}");

            // Güvenlik: Kod fallback'iyle yanlış stoğa düşme.
            // Kaynak açıklama varsa ve hedef açıklama bariz farklıysa hard-fail.
            if (!stockResolve.IsHizmetKart &&
                !string.IsNullOrWhiteSpace(stokAciklama) &&
                !string.IsNullOrWhiteSpace(stockResolve.MatchedTargetAciklama) &&
                !string.Equals(MasterCodeNormalizer.Normalize(stokAciklama), MasterCodeNormalizer.Normalize(stockResolve.MatchedTargetAciklama), StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(MasterCodeNormalizer.Normalize(stokKod), MasterCodeNormalizer.Normalize(stockResolve.MatchedTargetCode), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Hedefte yanlış stoğa eşleşme riski: kaynak=[{stokKod}] {stokAciklama} -> hedef=[{stockResolve.MatchedTargetCode}] {stockResolve.MatchedTargetAciklama}. " +
                    $"Bu stok hedef firmada aynı kodla yok; lütfen hedefte aynı stok kodunu açın veya eşleştirme tablosu kullanın.");
            }

            // DİA'da satır _key_kalemturu alanı tenant'a göre değişebilse de,
            // en güvenilir yaklaşım: kalem türü key'ini (varsa) göndermek, yoksa stok/hizmet kart key'i ile fallback yapmak.
            // (Type hatası / veri türü uyuşmuyor durumlarında stok/hizmet kart key'i bazen yanlış uzaya düşebiliyor.)
            var keyKalemTuruToSend = stockResolve.TargetKalemTuruKey ?? stockResolve.TargetStokKartKey;

            // DIA bazı tenantlarda birimi şu şekilde döndürüyor:
            //   [[218906, "Kilogram"], 218906]
            // Bu durumda "birim kodu" olarak 218906 seçilirse hedefte yanlış birim key'i çözülüyor.
            // Önce metinsel birim adını/kodunu yakalamayı dene.
            string? birimKod = null;
            try
            {
                if (l.BirimRaw.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    var arr = l.BirimRaw.EnumerateArray().ToList();
                    foreach (var item in arr)
                    {
                        if (item.ValueKind == System.Text.Json.JsonValueKind.Array)
                        {
                            var inner = item.EnumerateArray().ToList();
                            if (inner.Count >= 2 && inner[1].ValueKind == System.Text.Json.JsonValueKind.String)
                            {
                                var s = inner[1].GetString();
                                if (!string.IsNullOrWhiteSpace(s)) { birimKod = s; break; }
                            }
                        }
                        else if (item.ValueKind == System.Text.Json.JsonValueKind.Object)
                        {
                            var s = ParseCode(item, "birimkodu", "kodu", "birimadi", "adi", "kisaadi");
                            if (!string.IsNullOrWhiteSpace(s)) { birimKod = s; break; }
                        }
                        else if (item.ValueKind == System.Text.Json.JsonValueKind.String)
                        {
                            var s = item.GetString();
                            if (!string.IsNullOrWhiteSpace(s)) { birimKod = s; break; }
                        }
                    }
                }
                if (string.IsNullOrWhiteSpace(birimKod))
                {
                    birimKod = FirstNonEmpty(
                        ParseCode(l.BirimRaw, "birimkodu", "kodu", "birim"),
                        ParseCode(l.BirimRaw, "birimadi", "adi", "kisaadi"),
                        l.BirimRaw.ValueKind == System.Text.Json.JsonValueKind.String ? l.BirimRaw.GetString() : null);
                }
            }
            catch
            {
                birimKod = FirstNonEmpty(
                    ParseCode(l.BirimRaw, "birimkodu", "kodu", "birim"),
                    ParseCode(l.BirimRaw, "birimadi", "adi", "kisaadi"),
                    l.BirimRaw.ValueKind == System.Text.Json.JsonValueKind.String ? l.BirimRaw.GetString() : null);
            }
            birimKod = FirstNonEmpty(snap?.BirimAdi, birimKod);
            var sourceBirimKey = ParseFirstLong(l.BirimRaw);
            var targetBirimKey = await _dia.FindKalemBirimKeyAsync(req.TargetFirmaKodu, req.TargetDonemKodu, stockResolve.TargetKalemTuruKey, stockResolve.TargetStokKartKey, birimKod, stockResolve.IsHizmetKart);
            _logger.LogInformation("Transfer line resolve unit result: sourceStokKodu={StokKod} sourceStokAciklama={StokAciklama} sourceBirimText={SourceBirimText} sourceBirimKey={SourceBirimKey} resolvedTargetKalemTuruKey={KalemTuruKey} resolvedTargetStokKartKey={StokKartKey} resolvedTargetBirimKey={BirimKey}",
                stokKod, stokAciklama, birimKod, sourceBirimKey, stockResolve.TargetKalemTuruKey, stockResolve.TargetStokKartKey, targetBirimKey);
            if (!targetBirimKey.HasValue)
            {
                // İş kuralı: birim eşleşmezse hard-fail yapma; hedef stok kartının varsayılan birimi ile devam et.
                var fallbackUnit = await _dia.FindKalemBirimKeyAsync(req.TargetFirmaKodu, req.TargetDonemKodu, stockResolve.TargetKalemTuruKey, stockResolve.TargetStokKartKey, null, stockResolve.IsHizmetKart);
                if (fallbackUnit.HasValue)
                {
                    _logger.LogWarning(
                        "Transfer unit fallback used: sourceStokKodu={StokKod} sourceBirimText={SourceBirimText} resolvedFallbackTargetUnitKey={BirimKey}",
                        stokKod, birimKod, fallbackUnit);
                    targetBirimKey = fallbackUnit;
                }
                else
                {
                    throw new InvalidOperationException($"Hedef firmada uygun birim bulunamadı: {stokKod} / {FirstNonEmpty(birimKod, sourceBirimKey?.ToString(), "-")}. Stok bulundu ancak birim eşleşmesi yok.");
                }
            }

            _logger.LogInformation("Transfer mapping line: sourceInvoiceKey={Invoice} selectedSourceLineKey={Line} sourceStokKodu={Stok} sourceBirimText={SourceBirim} sourceBirimNormalized={SourceBirimNorm} sourceBirimKey={SourceBirimKey} resolvedTargetKalemTuruKey={KalemTuru} resolvedTargetStokKartKey={StokKart} chosenTargetUnitKey={Birim}",
                req.SourceInvoiceKey, l.Key, stokKod, birimKod, NormalizeUnit(birimKod), sourceBirimKey, stockResolve.TargetKalemTuruKey, stockResolve.TargetStokKartKey, targetBirimKey);

            // Satır dövizi bazen header'dan farklı olabiliyor; kaynak satırdan çözmeyi dene.
            static string? ExtraCurrency(DiaInvoiceLine line)
            {
                try
                {
                    if (line.ExtraFields != null && line.ExtraFields.Count > 0)
                    {
                        foreach (var k in new[] { "kalemdovizi", "dovizturu", "doviz", "kalem_doviz", "dovizadi" })
                        {
                            if (line.ExtraFields.TryGetValue(k, out var v))
                            {
                                if (v.ValueKind == System.Text.Json.JsonValueKind.String)
                                {
                                    var s = v.GetString();
                                    if (!string.IsNullOrWhiteSpace(s)) return s;
                                }
                                if (v.ValueKind == System.Text.Json.JsonValueKind.Object)
                                {
                                    var s = ParseCode(v, "kodu", "kod", "adi", "uzunadi", "dovizadi");
                                    if (!string.IsNullOrWhiteSpace(s)) return s;
                                }
                            }
                        }
                    }
                }
                catch { /* ignore */ }
                return null;
            }

            var srcLineDovizKod2 = FirstNonEmpty(
                CurrencyCodeFromList(sourceCurrencies, ParseFirstLong(l.KeySisDovizRaw)),
                ParseCode(l.KeySisDovizRaw, "kodu", "dovizkodu", "dovizadi", "kod", "adi", "uzunadi"));

            var lineCurrencyCode = FirstNonEmpty(ExtraCurrency(l), srcLineDovizKod2, srcLineDovizKod, headerDovizKod, srcDovizKod);
            // ÖNEMLİ: Header dövizi ile satır dövizi farklı olabilir (UI'da da böyle seçilebiliyor).
            // Bu yüzden satır dövizini header'a zorla eşitlemeyiz.
            var (targetLineDovizKey, _) = await ResolveTargetCurrencyWithFallbackAsync(req.TargetFirmaKodu, req.TargetDonemKodu, targetDovizler, lineCurrencyCode);
            var targetLineDovizKeyFinal = targetLineDovizKey ?? targetDovizKey;

            // Varyantlar birebir: kaynakta array ise aynen taşı.
            List<object> variantsToSend = new();
            try
            {
                if (l.VariantsRaw.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    // Bazı tenantlarda Variants içinde malzeme özellik kalemi gibi master link objeleri gelebiliyor
                    // ve hedef firmada karşılığı yoksa create_document düşüyor.
                    // Bu yüzden "malzeme_ozellik/ozellik_kalemi" içeren objeleri variant listesinden çıkar.
                    var arr = l.VariantsRaw.EnumerateArray().ToList();
                    var safe = new List<System.Text.Json.JsonElement>();
                    foreach (var it in arr)
                    {
                        try
                        {
                            if (it.ValueKind == System.Text.Json.JsonValueKind.Object)
                            {
                                var bad = false;
                                foreach (var p in it.EnumerateObject())
                                {
                                    var n = p.Name ?? string.Empty;
                                    if (n.Contains("malzeme_ozellik", StringComparison.OrdinalIgnoreCase)
                                        || n.Contains("ozellik_kalemi", StringComparison.OrdinalIgnoreCase)
                                        || n.Contains("malzemeozellik", StringComparison.OrdinalIgnoreCase)
                                        || n.Contains("scf_malzeme_ozellik", StringComparison.OrdinalIgnoreCase))
                                    {
                                        bad = true;
                                        break;
                                    }
                                }
                                if (bad)
                                {
                                    GetSkippedListOrCreate().Add(new Models.Api.InvoiceSkippedExtraFieldDto
                                    {
                                        Scope = "line",
                                        Name = "variants",
                                        Reason = "blocked_name"
                                    });
                                    continue;
                                }
                            }
                            safe.Add(it);
                        }
                        catch
                        {
                            // ignore malformed variant item
                        }
                    }
                    // JsonElement -> object deserialize
                    var json = "[" + string.Join(",", safe.Select(x => x.GetRawText())) + "]";
                    variantsToSend = System.Text.Json.JsonSerializer.Deserialize<List<object>>(json) ?? new List<object>();
                }
            }
            catch
            {
                variantsToSend = new List<object>();
            }

            // Snapshot miktar/fiyat/tutar override
            var srcMiktar = snap?.Miktar ?? l.Miktar;
            var srcBirimFiyati = snap?.BirimFiyati ?? l.BirimFiyati;
            var srcTutari = snap?.Tutar ?? l.Tutari;

            // Snapshot indirim/masraf: DIA line input'ta doğrudan alan yok; primitive extra field olarak taşımaya çalışırız.
            Dictionary<string, System.Text.Json.JsonElement>? snapExtras = null;
            if (snap != null && (snap.Indirim.HasValue || snap.Masraf.HasValue))
            {
                snapExtras = new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.OrdinalIgnoreCase);
                if (snap.Indirim.HasValue)
                    snapExtras["indirim"] = System.Text.Json.JsonDocument.Parse(FormatDiaLineDecimal(snap.Indirim)).RootElement.Clone();
                if (snap.Masraf.HasValue)
                    snapExtras["masraf"] = System.Text.Json.JsonDocument.Parse(FormatDiaLineDecimal(snap.Masraf)).RootElement.Clone();
            }

            string lineDovizKurStr;
            string lineRaporlamaDovizKurStr;
            if (_opt.TransferMirrorHeaderKurOnLines)
                (lineDovizKurStr, lineRaporlamaDovizKurStr) = FormatLineKurMirrorHeader(src, headerDovizKod, srcHeaderKuru);
            else if (targetDovizKey is long hdrDvk
                     && targetLineDovizKeyFinal == hdrDvk
                     && CanonicalCurrency(headerDovizKod) != "TL")
            {
                // Aynı fiş dövizi (USD/USD vb.): ResolveLineKur bilinçli 1 döner; DİA satırda başlık kuru beklenir.
                var kurStr = FirstNonEmpty(src.DovizKuru, srcHeaderKuru, src.RaporlamaDovizKuru);
                if (ParseHeaderKurDecimal(kurStr) is decimal pk && pk > 0m)
                    (lineDovizKurStr, lineRaporlamaDovizKurStr) = FormatLineKurMirrorHeader(src, headerDovizKod, srcHeaderKuru);
                else
                    (lineDovizKurStr, lineRaporlamaDovizKurStr) = FormatLineKurStringsForDiaAdd(
                        ResolveLineKurForDiaPayload(
                            l.DovizKuru,
                            srcHeaderKuru,
                            CanonicalCurrency(headerDovizKod),
                            CanonicalCurrency(lineCurrencyCode)),
                        headerDovizKod,
                        src.DovizTuru,
                        srcHeaderKuru);
            }
            else
                (lineDovizKurStr, lineRaporlamaDovizKurStr) = FormatLineKurStringsForDiaAdd(
                    ResolveLineKurForDiaPayload(
                        l.DovizKuru,
                        srcHeaderKuru,
                        CanonicalCurrency(headerDovizKod),
                        CanonicalCurrency(lineCurrencyCode)),
                    headerDovizKod,
                    src.DovizTuru,
                    srcHeaderKuru);

            resolvedLines.Add(new DiaInvoiceAddLineInput
            {
                KeyBcsBankahesabi = targetBankaHesabiKey,
                KeyKalemTuru = keyKalemTuruToSend,
                KeyPrjProje = targetProjeKey,
                KeyScfBankaOdemePlani = targetBankaOdemePlaniKey,
                KeyScfOdemePlani = targetOdemePlaniKey,
                KeyKalemBirim = targetBirimKey,
                KeyDepoSource = req.TargetDepoKey,
                KeyDoviz = targetLineDovizKeyFinal,
                KalemTuru = string.IsNullOrWhiteSpace(l.KalemTuruRaw) ? "MLZM" : l.KalemTuruRaw,
                AnaMiktar = FormatDiaLineDecimal(srcMiktar),
                Miktar = FormatDiaLineDecimal(srcMiktar),
                BirimFiyati = FormatDiaLineDecimal(srcBirimFiyati),
                SonBirimFiyati = FormatDiaLineDecimal(l.SonBirimFiyati ?? srcBirimFiyati),
                // Kaynak satırda yerel birim fiyat varsa birebir kopyala (dövizli faturalarda toplamların tutması için kritik).
                YerelBirimFiyati = FormatDiaLineDecimal(l.YerelBirimFiyati ?? srcBirimFiyati),
                Tutari = FormatDiaLineDecimal(srcTutari),
                Kdv = FormatDiaLineDecimal(DiaMappers.ResolveInvoiceLineKdvPercent(l)),
                KdvTutari = FormatDiaLineDecimal(l.KdvTutari),
                // Kaynaktaki KDV D/H bilgisini olduğu gibi taşı (kullanıcı beklentisi: aynen aktarım).
                KdvDurumu = string.IsNullOrWhiteSpace(l.KdvDurumuRaw) ? "H" : l.KdvDurumuRaw,
                // Satır dovizkuru + raporlamadovizkuru: T.Kur şişmesini kesmek için TL'de 1.000000; diğerlerinde çözülmüş kur (0 veya >100 → 1), 6 ondalık invariant.
                DovizKuru = lineDovizKurStr,
                RaporlamaDovizKuru = lineRaporlamaDovizKurStr,
                SiraNo = l.SiraNo,
                Note = l.Note,
                Note2 = l.Note2,
                Variants = variantsToSend,
                ExtraFields = SanitizeInvoiceLineExtraFields(
                    MergeExtraFields(
                        MergeExtraFields(l.ExtraFields, sourceLineViewExtras != null && sourceLineViewExtras.TryGetValue(l.Key, out var ex) ? ex : null),
                        snapExtras))
            });
        }

        // Alt indirim/masraf (m_altlar) birebir aktarım:
        // Kaynakta varsa hedefe aynı şekilde gönder, yoksa hiç gönderme.
        var resolvedAltlar = new List<DiaInvoiceAddAltInput>();
        if (src.Altlar is { Count: > 0 })
        {
            foreach (var a in src.Altlar)
            {
                if (a == null) continue;
                // Yeni kayıtta _key her zaman 0/null olmalı.
                // Alt kalem dövizi, satır dövizinden farklı olabilir; kaynak alt satırdan çöz.
                var altCurrencyCode = FirstNonEmpty(
                    CurrencyCodeFromList(sourceCurrencies, ParseFirstLong(a.KeySisDovizRaw)),
                    ParseCode(a.KeySisDovizRaw, "kodu", "dovizkodu", "dovizadi", "kod", "adi", "uzunadi"),
                    headerDovizKod,
                    srcDovizKod);
                var (targetAltDovizKey, _) = await ResolveTargetCurrencyWithFallbackAsync(req.TargetFirmaKodu, req.TargetDonemKodu, targetDovizler, altCurrencyCode);
                if (!targetAltDovizKey.HasValue)
                    targetAltDovizKey = targetDovizKey;

                resolvedAltlar.Add(new DiaInvoiceAddAltInput
                {
                    Key = 0,
                    KeyKalemTuru = a.KeyKalemTuru ?? 0,
                    KeyScfHediyeCeki = a.KeyScfHediyeCeki ?? 0,
                    // Kaynaktaki _key_scf_sif gibi bağlı doküman key'leri hedef firmada geçersizdir.
                    // Bunları taşımak DIA tarafında "veri türü uyuşmuyor" / tip hatasına sebep olabiliyor.
                    KeyScfSif = 0,
                    KeySisDoviz = targetAltDovizKey,
                    Deger = a.Deger,
                    // Kaynak alt satırda kur varsa onu kullan; yoksa header kuru fallback.
                    DovizKuru = string.IsNullOrWhiteSpace(a.DovizKuru) ? (string.IsNullOrWhiteSpace(src.DovizKuru) ? "1.000000" : src.DovizKuru) : a.DovizKuru,
                    Etkin = a.Etkin,
                    KalemTuru = a.KalemTuru,
                    Turu = a.Turu,
                    Tutar = a.Tutar
                });
            }
        }

        _logger.LogInformation("Transfer mapping header: sourceInvoiceKey={Invoice} resolvedTargetCariKey={Cari} resolvedTargetCariAddressKey={Adres} resolvedTargetOdemePlaniKey={Odeme} resolvedTargetDovizKey={Doviz} resolvedTargetSubeKey={Sube} resolvedTargetDepoKey={Depo}",
            req.SourceInvoiceKey, targetCariKey, targetCariAdresKey, targetOdemePlaniKey, targetDovizKey, req.TargetSubeKey, req.TargetDepoKey);
        _logger.LogInformation("Transfer final payload currency fields: _key_sis_doviz={Doviz} _key_sis_doviz_raporlama={Raporlama} dovizkuru={DovizKuru} raporlamadovizkuru={RaporlamaKuru}",
            targetDovizKey, targetRaporlamaDovizKey, src.DovizKuru, src.RaporlamaDovizKuru);

        // Satış elemanı (header) birebir aktarım:
        // Kaynakta satış elemanı varsa hedefte bulunmalı; yoksa boş gidebilir.
        long? targetSatisElemaniKey = null;
        string? srcSatisElemaniKodu = null;
        if (src.ExtraFields != null)
        {
            // 1) _key_scf_satiselemani objesi içinden kodu
            if (src.ExtraFields.TryGetValue("_key_scf_satiselemani", out var seObj))
                srcSatisElemaniKodu = ParseCode(seObj, "kodu", "kod");

            // 2) _key_satiselemanlari[] içinden ilk satırın kodu
            if (string.IsNullOrWhiteSpace(srcSatisElemaniKodu) &&
                src.ExtraFields.TryGetValue("_key_satiselemanlari", out var seArr) &&
                seArr.ValueKind == System.Text.Json.JsonValueKind.Array &&
                seArr.GetArrayLength() > 0)
            {
                var first = seArr.EnumerateArray().FirstOrDefault();
                srcSatisElemaniKodu = ParseCode(first, "kodu", "kod");
            }
        }

        if (!string.IsNullOrWhiteSpace(srcSatisElemaniKodu))
        {
            // Satış elemanı zorunlu değil:
            // - hedefte bulunursa key set et
            // - bulunamazsa boş geç (hata verme)
            targetSatisElemaniKey = await _dia.FindSatisElemaniKeyByCodeAsync(req.TargetFirmaKodu, req.TargetDonemKodu, srcSatisElemaniKodu);
        }

        return new DiaInvoiceAddCardInput
        {
            KeyPrjProje = targetProjeKey,
            KeyScfCariKart = targetCariKey,
            KeyScfCariKartAdresleri = targetCariAdresKey,
            // Kaynakta yetkili yoksa hedefte de boş kalsın: explicit 0 gönder.
            KeyScfCariKartYetkili = sourceHasYetkili ? (targetCariYetkiliKey ?? 0) : 0,
            KeyScfOdemePlani = targetOdemePlaniKey,
            KeyScfSatisElemani = targetSatisElemaniKey,
            KeySatisElemanlari = targetSatisElemaniKey.HasValue && targetSatisElemaniKey.Value > 0
                ? new List<DiaKeyRef> { new() { Key = targetSatisElemaniKey.Value } }
                : new List<DiaKeyRef>(),
            KeySisSubeSource = req.TargetSubeKey,
            KeySisDepoSource = req.TargetDepoKey,
            KeySisDoviz = targetDovizKey,
            KeySisDovizRaporlama = targetRaporlamaDovizKey,
            // Birebir: kaynakta boşsa boş gönder.
            Aciklama1 = src.Aciklama1,
            Aciklama2 = src.Aciklama2,
            Aciklama3 = src.Aciklama3,
            BelgeNo2 = src.BelgeNo2,
            BelgeNo = src.BelgeNo,
            // Not: fisno bazı tenantlarda otomatik üretilir; hedef "Fatura No" için belgeno/belgeno2 taşınır.
            // Ancak bazı fatura tiplerinde fisno zorunlu olabilir; ilk denemede boş bırakıp
            // "fisno zorunlu" hatası gelirse üst katmanda otomatik üretip retry ederiz.
            Fisno = string.IsNullOrWhiteSpace(src.FisNo) ? null : src.FisNo,
            DovizKuru = string.IsNullOrWhiteSpace(srcHeaderKuru) ? "1.000000" : srcHeaderKuru,
            RaporlamaDovizKuru = string.IsNullOrWhiteSpace(src.RaporlamaDovizKuru) ? "1.000000" : src.RaporlamaDovizKuru,
            Tarih = src.Tarih,
            Saat = string.IsNullOrWhiteSpace(src.Saat) ? DateTime.Now.ToString("HH:mm:ss") : src.Saat,
            Turu = src.Turu,
            SevkAdresi1 = src.SevkAdresi1,
            SevkAdresi2 = src.SevkAdresi2,
            SevkAdresi3 = src.SevkAdresi3,
            Altlar = resolvedAltlar,
            Lines = resolvedLines,
            // Kaynak response'daki "Diğer/Detay" gibi alanları primitive whitelist ile taşı.
            ExtraFields = SanitizeInvoiceHeaderExtraFields(src.ExtraFields)
        };
    }

    private static Dictionary<string, System.Text.Json.JsonElement>? SanitizeInvoiceLineExtraFields(
        Dictionary<string, System.Text.Json.JsonElement>? extra,
        List<Models.Api.InvoiceSkippedExtraFieldDto>? skipped = null)
    {
        if (extra == null || extra.Count == 0) return null;
        skipped ??= GetSkippedListOrCreate();

        static System.Text.Json.JsonElement? TryFlattenToPrimitive(System.Text.Json.JsonElement raw)
        {
            try
            {
                if (raw.ValueKind is System.Text.Json.JsonValueKind.String
                    or System.Text.Json.JsonValueKind.Number
                    or System.Text.Json.JsonValueKind.True
                    or System.Text.Json.JsonValueKind.False
                    or System.Text.Json.JsonValueKind.Null)
                    return raw;

                if (raw.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    // En yaygın güvenli pattern: {kodu/aciklama/adi/uzunadi: "..."} -> tek stringe indir
                    foreach (var p in new[] { "kodu", "kod", "aciklama", "adi", "uzunadi", "deger", "text" })
                    {
                        if (raw.TryGetProperty(p, out var v) &&
                            v.ValueKind is System.Text.Json.JsonValueKind.String or System.Text.Json.JsonValueKind.Number)
                        {
                            var s = v.ValueKind == System.Text.Json.JsonValueKind.String ? (v.GetString() ?? string.Empty) : v.GetRawText();
                            s = s.Trim();
                            if (string.IsNullOrWhiteSpace(s)) continue;
                            return System.Text.Json.JsonDocument.Parse($"\"{System.Text.Json.JsonEncodedText.Encode(s).ToString()}\"").RootElement;
                        }
                    }
                    return null;
                }

                if (raw.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    // Array -> virgülle birleştir (yalnızca string/number veya flatten edilebilen object elemanlar)
                    var parts = new List<string>();
                    foreach (var item in raw.EnumerateArray())
                    {
                        if (item.ValueKind is System.Text.Json.JsonValueKind.String)
                        {
                            var s = (item.GetString() ?? string.Empty).Trim();
                            if (!string.IsNullOrWhiteSpace(s)) parts.Add(s);
                            continue;
                        }
                        if (item.ValueKind is System.Text.Json.JsonValueKind.Number)
                        {
                            parts.Add(item.GetRawText());
                            continue;
                        }
                        if (item.ValueKind == System.Text.Json.JsonValueKind.Object)
                        {
                            var flattened = TryFlattenToPrimitive(item);
                            if (flattened.HasValue && flattened.Value.ValueKind == System.Text.Json.JsonValueKind.String)
                            {
                                var s = (flattened.Value.GetString() ?? string.Empty).Trim();
                                if (!string.IsNullOrWhiteSpace(s)) parts.Add(s);
                            }
                        }
                    }
                    if (parts.Count == 0) return null;
                    var joined = string.Join(", ", parts);
                    return System.Text.Json.JsonDocument.Parse($"\"{System.Text.Json.JsonEncodedText.Encode(joined).ToString()}\"").RootElement;
                }
            }
            catch
            {
                // ignore
            }
            return null;
        }

        // Satır bazında irsaliye *bağlantısı* taşınmasın.
        // Ama irsaliye numarası/tarihi gibi primitive alanlar taşınabilir.
        static bool IsBlockedLineName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return true;
            var n = name.Trim();
            if (n.StartsWith("_", StringComparison.OrdinalIgnoreCase)) return true;
            if (n.StartsWith("__", StringComparison.OrdinalIgnoreCase)) return true;
            if (n.StartsWith("m_", StringComparison.OrdinalIgnoreCase)) return true;

            // Not: malzeme_ozellik alanlarını tamamen drop etmiyoruz.
            // Aşağıda "yeniden adlandırarak" güvenli biçimde taşıyoruz (DİA master lookup tetiklenmesin).

            // İzin verilen irsaliye alanları (primitive): no/tarih
            if (n.Equals("irsaliyeno", StringComparison.OrdinalIgnoreCase)) return false;
            if (n.Equals("irsaliyetarih", StringComparison.OrdinalIgnoreCase)) return false;
            if (n.Equals("irsaliye_no", StringComparison.OrdinalIgnoreCase)) return false;
            if (n.Equals("irsaliye_tarih", StringComparison.OrdinalIgnoreCase)) return false;

            // Bağlantı/ilişki alanları engellenir
            if (n.Contains("_key_scf_irsaliye", StringComparison.OrdinalIgnoreCase)) return true;
            if (n.Contains("_key_scf_irsali", StringComparison.OrdinalIgnoreCase)) return true;
            if (n.Contains("irsaliye", StringComparison.OrdinalIgnoreCase) || n.Contains("irsali", StringComparison.OrdinalIgnoreCase))
            {
                // Diğer tüm irsaliye ile ilgili kolonları güvenlik için engelle.
                return true;
            }
            return false;
        }

        var cleaned = new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in extra)
        {
            var name = kv.Key ?? string.Empty;
            if (IsBlockedLineName(name))
            {
                skipped?.Add(new Models.Api.InvoiceSkippedExtraFieldDto { Scope = "line", Name = name, Reason = "blocked_name" });
                continue;
            }

            // key/master id alanlarını asla gönderme
            if (name.Contains("_key", StringComparison.OrdinalIgnoreCase) || name.EndsWith("key", StringComparison.OrdinalIgnoreCase))
            {
                skipped?.Add(new Models.Api.InvoiceSkippedExtraFieldDto { Scope = "line", Name = name, Reason = "contains_key" });
                continue;
            }

            var v = kv.Value;
            var flattened = TryFlattenToPrimitive(v);
            if (flattened.HasValue)
            {
                // DİA bazı field adlarını "ilişki" gibi yorumlayıp master araması yapabiliyor.
                // Bu durumda değeri KORUYUP sadece field adını güvenli isimle taşıyoruz.
                // (Kafadan değer üretmiyoruz / veri kaybetmiyoruz.)
                var safeName = name;
                if (safeName.Contains("malzeme_ozellik", StringComparison.OrdinalIgnoreCase)
                    || safeName.Contains("ozellik_kalemi", StringComparison.OrdinalIgnoreCase)
                    || safeName.Contains("malzemeozellik", StringComparison.OrdinalIgnoreCase))
                {
                    safeName = "x_" + safeName;
                }

                // DİA create endpoint'inde bazı combo alanları "None" kabul etmiyor.
                // Örn: efaturatipkodu -> None hatası. Bu alanlar boş string olmalı.
                if (name.Equals("efaturatipkodu", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("efaturasenaryosu", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("efatalias", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("disentegrator", StringComparison.OrdinalIgnoreCase))
                {
                    if (v.ValueKind == System.Text.Json.JsonValueKind.Null)
                    {
                        cleaned[name] = System.Text.Json.JsonDocument.Parse("\"\"").RootElement;
                        continue;
                    }
                    if (v.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        // "None"/"NULL" gibi değerleri de boş string'e çek
                        var s = v.GetString();
                        if (string.Equals(s, "None", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(s, "NULL", StringComparison.OrdinalIgnoreCase))
                        {
                            cleaned[name] = System.Text.Json.JsonDocument.Parse("\"\"").RootElement;
                            continue;
                        }
                    }
                }
                cleaned[safeName] = flattened.Value;
            }
        }

        return cleaned.Count == 0 ? null : cleaned;
    }

    private static Dictionary<string, System.Text.Json.JsonElement>? MergeExtraFields(
        Dictionary<string, System.Text.Json.JsonElement>? primary,
        Dictionary<string, System.Text.Json.JsonElement>? secondary)
    {
        if ((primary == null || primary.Count == 0) && (secondary == null || secondary.Count == 0)) return null;
        if (secondary == null || secondary.Count == 0) return primary;
        if (primary == null || primary.Count == 0) return secondary;

        var merged = new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in secondary) merged[kv.Key] = kv.Value;
        foreach (var kv in primary) merged[kv.Key] = kv.Value; // primary wins
        return merged;
    }

    private static Dictionary<string, System.Text.Json.JsonElement>? SanitizeInvoiceHeaderExtraFields(
        Dictionary<string, System.Text.Json.JsonElement>? extra,
        List<Models.Api.InvoiceSkippedExtraFieldDto>? skipped = null)
    {
        if (extra == null || extra.Count == 0) return null;
        skipped ??= GetSkippedListOrCreate();

        static System.Text.Json.JsonElement? TryFlattenToPrimitive(System.Text.Json.JsonElement raw)
        {
            try
            {
                if (raw.ValueKind is System.Text.Json.JsonValueKind.String
                    or System.Text.Json.JsonValueKind.Number
                    or System.Text.Json.JsonValueKind.True
                    or System.Text.Json.JsonValueKind.False
                    or System.Text.Json.JsonValueKind.Null)
                    return raw;

                if (raw.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    foreach (var p in new[] { "kodu", "kod", "aciklama", "adi", "uzunadi", "deger", "text" })
                    {
                        if (raw.TryGetProperty(p, out var v) &&
                            v.ValueKind is System.Text.Json.JsonValueKind.String or System.Text.Json.JsonValueKind.Number)
                        {
                            var s = v.ValueKind == System.Text.Json.JsonValueKind.String ? (v.GetString() ?? string.Empty) : v.GetRawText();
                            s = s.Trim();
                            if (string.IsNullOrWhiteSpace(s)) continue;
                            return System.Text.Json.JsonDocument.Parse($"\"{System.Text.Json.JsonEncodedText.Encode(s).ToString()}\"").RootElement;
                        }
                    }
                    return null;
                }

                if (raw.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    var parts = new List<string>();
                    foreach (var item in raw.EnumerateArray())
                    {
                        if (item.ValueKind == System.Text.Json.JsonValueKind.String)
                        {
                            var s = (item.GetString() ?? string.Empty).Trim();
                            if (!string.IsNullOrWhiteSpace(s)) parts.Add(s);
                            continue;
                        }
                        if (item.ValueKind == System.Text.Json.JsonValueKind.Number)
                        {
                            parts.Add(item.GetRawText());
                            continue;
                        }
                        if (item.ValueKind == System.Text.Json.JsonValueKind.Object)
                        {
                            var flattened = TryFlattenToPrimitive(item);
                            if (flattened.HasValue && flattened.Value.ValueKind == System.Text.Json.JsonValueKind.String)
                            {
                                var s = (flattened.Value.GetString() ?? string.Empty).Trim();
                                if (!string.IsNullOrWhiteSpace(s)) parts.Add(s);
                            }
                        }
                    }
                    if (parts.Count == 0) return null;
                    var joined = string.Join(", ", parts);
                    return System.Text.Json.JsonDocument.Parse($"\"{System.Text.Json.JsonEncodedText.Encode(joined).ToString()}\"").RootElement;
                }
            }
            catch
            {
                // ignore
            }
            return null;
        }

        // CRITICAL:
        // - ExtraFields içinde _key_* gibi kaynak master key'ler de gelebiliyor.
        // - Bunlar hedef firmada yok ve DIA bazen bunlarla "scf_irsaliye" gibi bağlı doküman arayıp create'i düşürüyor.
        // Bu yüzden sadece "business" alanlarını geçirip, tüm sistem/key/meta alanlarını eliyoruz.
        static bool IsBlockedName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return true;
            var n = name.Trim();

            // Sistem/meta alanları
            if (n.StartsWith("_", StringComparison.OrdinalIgnoreCase)) return true;
            if (n.StartsWith("__", StringComparison.OrdinalIgnoreCase)) return true;
            if (n.StartsWith("m_", StringComparison.OrdinalIgnoreCase)) return true;

            // Bilinen, zaten map edilen alanlar
            if (n.Equals("fisno", StringComparison.OrdinalIgnoreCase)) return true;
            if (n.Equals("tarih", StringComparison.OrdinalIgnoreCase)) return true;
            if (n.Equals("saat", StringComparison.OrdinalIgnoreCase)) return true;
            if (n.Equals("turu", StringComparison.OrdinalIgnoreCase)) return true;
            if (n.Equals("dovizkuru", StringComparison.OrdinalIgnoreCase)) return true;
            if (n.Equals("raporlamadovizkuru", StringComparison.OrdinalIgnoreCase)) return true;
            if (n.Equals("dovizturu", StringComparison.OrdinalIgnoreCase)) return true;
            if (n.Equals("belgeno", StringComparison.OrdinalIgnoreCase)) return true;
            if (n.Equals("belgeno2", StringComparison.OrdinalIgnoreCase)) return true;
            if (n.Equals("aciklama1", StringComparison.OrdinalIgnoreCase)) return true;
            if (n.Equals("aciklama2", StringComparison.OrdinalIgnoreCase)) return true;
            if (n.Equals("aciklama3", StringComparison.OrdinalIgnoreCase)) return true;
            if (n.Equals("sevkadresi1", StringComparison.OrdinalIgnoreCase)) return true;
            if (n.Equals("sevkadresi2", StringComparison.OrdinalIgnoreCase)) return true;
            if (n.Equals("sevkadresi3", StringComparison.OrdinalIgnoreCase)) return true;

            // İrsaliye no/tarih gibi primitive alanlar taşınabilir; bağlantı key'leri zaten aşağıda eleniyor.
            if (n.Equals("irsaliyeno", StringComparison.OrdinalIgnoreCase)) return false;
            if (n.Equals("irsaliyetarih", StringComparison.OrdinalIgnoreCase)) return false;

            return false;
        }

        var cleaned = new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in extra)
        {
            var name = kv.Key ?? string.Empty;
            if (IsBlockedName(name))
            {
                skipped?.Add(new Models.Api.InvoiceSkippedExtraFieldDto { Scope = "header", Name = name, Reason = "blocked_name" });
                continue;
            }

            // Ek güvenlik: key/master id alanlarını asla gönderme
            if (name.Contains("_key", StringComparison.OrdinalIgnoreCase) || name.EndsWith("key", StringComparison.OrdinalIgnoreCase))
            {
                skipped?.Add(new Models.Api.InvoiceSkippedExtraFieldDto { Scope = "header", Name = name, Reason = "contains_key" });
                continue;
            }

            // Sadece primitive alanlar (string/number/bool/null) güvenli şekilde taşınır.
            // Object/Array yapıları tenant'a göre değiştiği için create çağrısında tip hatası üretebiliyor.
            var v = kv.Value;
            var flattened = TryFlattenToPrimitive(v);
            if (flattened.HasValue)
            {
                if (name.Equals("efaturatipkodu", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("efaturasenaryosu", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("efatalias", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("disentegrator", StringComparison.OrdinalIgnoreCase))
                {
                    if (v.ValueKind == System.Text.Json.JsonValueKind.Null)
                    {
                        cleaned[name] = System.Text.Json.JsonDocument.Parse("\"\"").RootElement;
                        continue;
                    }
                    if (v.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        var s = v.GetString();
                        if (string.Equals(s, "None", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(s, "NULL", StringComparison.OrdinalIgnoreCase))
                        {
                            cleaned[name] = System.Text.Json.JsonDocument.Parse("\"\"").RootElement;
                            continue;
                        }
                    }
                }
                cleaned[name] = flattened.Value;
            }
            else
            {
                skipped?.Add(new Models.Api.InvoiceSkippedExtraFieldDto { Scope = "header", Name = name, Reason = "not_primitive" });
            }
        }

        return cleaned.Count == 0 ? null : cleaned;
    }

    private static bool IsFisNoRequiredMessage(string? msg)
    {
        if (string.IsNullOrWhiteSpace(msg)) return false;
        return msg.Contains("fisno", StringComparison.OrdinalIgnoreCase) &&
               (msg.Contains("girilmelidir", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("zorunlu", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("girilmesi", StringComparison.OrdinalIgnoreCase));
    }

    private string GenerateFisNo(InvoiceTransferRequestDto req, DiaInvoiceDetail src)
    {
        // Eski fallback (kullanılmamalı): havuz fisno formatı yoksa.
        return GenerateFisNoFor(req, src, "F");
    }

    private string GenerateFisNoFor(InvoiceTransferRequestDto req, DiaInvoiceDetail src, string tip)
    {
        var datePart = DateTime.TryParse(src.Tarih, out var d) ? d.ToString("yyMMdd") : DateTime.UtcNow.ToString("yyMMdd");
        var counterKey = $"{req.TargetFirmaKodu}|{req.TargetDonemKodu}|{tip}|{datePart}";
        var next = _fisnoCounters.AddOrUpdate(counterKey, 1, (_, old) => old + 1);
        MaybeSaveTransferState(); // sayaç kalıcı olsun
        var raw = $"{tip}{datePart}{next:0000}";
        return raw.Length <= 16 ? raw : raw[..16];
    }

    private async Task<string> GenerateFisNoLikeSourceAsync(InvoiceTransferRequestDto req, DiaInvoiceDetail src, string tip)
    {
        // İstenen davranış:
        // - Havuzdaki fisno formatı neyse (örn NTS0000000000098)
        // - Hedef firmada da aynı prefix + aynı padding ile sırayla devam etsin.
        //
        // Kaynak fisno'dan template çıkar:
        //   PREFIX = harfler (NTS)
        //   WIDTH  = sondaki rakam sayısı (13)
        //
        // Hedefte PREFIX ile başlayan fisno'ların en büyüğünü bul, +1 yap.
        var sourceFis = (src.FisNo ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(sourceFis))
        {
            // Kaynakta fisno yoksa eski günlük sayaç fallback
            return GenerateFisNoFor(req, src, tip);
        }

        var prefix = new string(sourceFis.TakeWhile(char.IsLetter).ToArray());
        var digitsPart = new string(sourceFis.Skip(prefix.Length).TakeWhile(char.IsDigit).ToArray());
        if (string.IsNullOrWhiteSpace(prefix) || string.IsNullOrWhiteSpace(digitsPart))
            return GenerateFisNoFor(req, src, tip);

        var width = digitsPart.Length;
        var counterKey = $"{req.TargetFirmaKodu}|{req.TargetDonemKodu}|{prefix}|{width}";

        // İlk kullanımda hedefteki max'ı okuyup sayaç başlangıcını ayarla (max+1).
        if (_fisnoInitialized.TryAdd(counterKey, 1))
        {
            try
            {
                var filters = $"[fisno] LIKE '{prefix}%'";
                var max = 0;
                var offset = 0;
                const int pageSize = 200;
                while (offset < 5000) // safety cap
                {
                    var page = await _dia.GetInvoicesAsync(req.TargetFirmaKodu, req.TargetDonemKodu, filters, pageSize, offset);
                    if (page.Count == 0) break;
                    foreach (var it in page)
                    {
                        var f = (it.FisNo ?? string.Empty).Trim();
                        if (!f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                        var numStr = new string(f.Substring(prefix.Length).TakeWhile(char.IsDigit).ToArray());
                        if (numStr.Length == 0) continue;
                        if (int.TryParse(numStr, out var n) && n > max) max = n;
                    }
                    if (page.Count < pageSize) break;
                    offset += page.Count;
                }

                if (max > 0)
                {
                    _fisnoCounters.AddOrUpdate(counterKey, max, (_, old) => Math.Max(old, max));
                    MaybeSaveTransferState();
                    _logger.LogInformation(
                        "Fisno initialized from target: prefix={Prefix} width={Width} max={Max} targetFirma={Firma} targetDonem={Donem}",
                        prefix, width, max, req.TargetFirmaKodu, req.TargetDonemKodu);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Fisno init from target failed. counterKey={Key}", counterKey);
            }
        }

        var next = _fisnoCounters.AddOrUpdate(counterKey, 1, (_, old) => old + 1);
        MaybeSaveTransferState();
        var nextStr = next.ToString().PadLeft(width, '0');
        var finalFis = $"{prefix}{nextStr}";
        // DIA alan limiti farklı olabilir; yine de çok uzunsa kırp.
        return finalFis.Length <= 16 ? finalFis : finalFis[..16];
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
        return s switch
        {
            "ADET" => "AD",
            "AD" => "AD",
            _ => s
        };
    }

    private static string? ParseCode(System.Text.Json.JsonElement raw, params string[] candidateNames)
    {
        try
        {
            if (raw.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                var s = raw.GetString();
                return string.IsNullOrWhiteSpace(s) ? null : s;
            }
            if (raw.ValueKind == System.Text.Json.JsonValueKind.Number)
                return raw.GetRawText();
            if (raw.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                foreach (var name in candidateNames)
                {
                    if (!raw.TryGetProperty(name, out var p)) continue;
                    if (p.ValueKind == System.Text.Json.JsonValueKind.String)
                        return p.GetString();
                    if (p.ValueKind == System.Text.Json.JsonValueKind.Number)
                        return p.GetRawText();
                    if (p.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        var first = p.EnumerateArray().FirstOrDefault();
                        if (first.ValueKind == System.Text.Json.JsonValueKind.String) return first.GetString();
                        if (first.ValueKind == System.Text.Json.JsonValueKind.Number) return first.GetRawText();
                        if (first.ValueKind == System.Text.Json.JsonValueKind.Object)
                        {
                            foreach (var nested in candidateNames)
                            {
                                if (!first.TryGetProperty(nested, out var n)) continue;
                                if (n.ValueKind == System.Text.Json.JsonValueKind.String) return n.GetString();
                                if (n.ValueKind == System.Text.Json.JsonValueKind.Number) return n.GetRawText();
                            }
                        }
                    }
                }
            }
            if (raw.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var item in raw.EnumerateArray())
                {
                    var parsed = ParseCode(item, candidateNames);
                    if (!string.IsNullOrWhiteSpace(parsed)) return parsed;
                }
            }
        }
        catch
        {
            // ignore
        }
        return null;
    }

    private async Task<bool> IsTargetPeriodCompatible(InvoiceTransferRequestDto req, DiaInvoiceDetail src)
    {
        if (!DateTime.TryParse(src.Tarih, out var invDate)) return true;
        var ctx = await _dia.GetAuthorizedCompanyPeriodBranchAsync();
        var firma = ctx.FirstOrDefault(f => f.FirmaKodu == req.TargetFirmaKodu);
        if (firma == null) return false;
        var periods = firma.Donemler;
        if (periods.Count == 0)
            periods = await _dia.GetPeriodsByFirmaAsync(req.TargetFirmaKodu);
        if (periods.Count == 0) return false;

        var selectedDonem = periods.FirstOrDefault(d => d.DonemKodu == req.TargetDonemKodu);
        if (selectedDonem == null) return false;

        var compatible = periods.FirstOrDefault(d =>
            DateTime.TryParse(d.BaslangicTarihi, out var pb) &&
            DateTime.TryParse(d.BitisTarihi, out var pe) &&
            invDate.Date >= pb.Date && invDate.Date <= pe.Date);
        if (compatible == null) return false;
        return compatible.DonemKodu == req.TargetDonemKodu;
    }

    private async Task<(long? key, string matchInfo)> ResolveTargetCurrencyWithFallbackAsync(int firmaKodu, int donemKodu, List<DiaAuthorizedCurrencyItem> list, string? sourceValue)
    {
        if (string.IsNullOrWhiteSpace(sourceValue))
        {
            var tl = ResolveTargetCurrencyKey(list, "TL");
            if (tl.HasValue) return (tl, "authorized-list:TL-fallback");
        }
        var key = ResolveTargetCurrencyKey(list, sourceValue);
        if (key.HasValue) return (key, $"authorized-list:{sourceValue}");
        if (string.IsNullOrWhiteSpace(sourceValue)) return (null, "empty-source");
        var fallback = await _dia.FindDovizKeyByCodeAsync(firmaKodu, donemKodu, sourceValue);
        return (fallback, fallback.HasValue ? $"sis_doviz_listele:{sourceValue}" : $"not-found:{sourceValue}");
    }

    private static long? ResolveTargetCurrencyKey(List<DiaAuthorizedCurrencyItem> list, string? rawCode)
    {
        if (string.IsNullOrWhiteSpace(rawCode)) return null;
        var canon = CanonicalCurrency(rawCode);
        var hit = list.FirstOrDefault(d =>
            CanonicalCurrency(d.Kodu) == canon ||
            CanonicalCurrency(d.Adi) == canon ||
            CanonicalCurrency(d.UzunAdi) == canon);
        return hit?.Key;
    }

    private static string CanonicalCurrency(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var s = raw.Trim().ToUpperInvariant()
            .Replace("İ", "I")
            .Replace("Ş", "S")
            .Replace("Ğ", "G")
            .Replace("Ü", "U")
            .Replace("Ö", "O")
            .Replace("Ç", "C");

        return s switch
        {
            "TRY" or "TL" or "TURK LIRASI" or "TURK LIRASI (TL)" or "TURK LIRASI TL" or "TÜRK LİRASI" => "TL",
            "USD" or "ABD DOLARI" => "USD",
            "EUR" or "EURO" or "AVRO" => "EUR",
            _ => s
        };
    }

    /// <summary>IOptions bind: <c>DiaSettings</c> section. Structured log; secrets yalnızca set/unset.</summary>
    private static object DiaSettingsForDebugLog(DiaOptions o) => new
    {
        o.Mode,
        o.BaseUrl,
        UserSet = !string.IsNullOrEmpty(o.Username),
        PassSet = !string.IsNullOrEmpty(o.Password),
        ApiKeySet = !string.IsNullOrEmpty(o.ApiKey),
        o.DefaultSourceFirmaKodu,
        o.DefaultSourceDonemKodu,
        o.DefaultSourceSubeKey,
        o.PoolFirmaKodu,
        o.PoolFirmaAdi,
        o.BranchDynamicColumnOverride,
        o.SessionTtlMinutes,
        o.TransferInvoiceTimeoutSeconds,
        o.TransferConcurrency,
        o.TransferBatchSize,
        o.TransferRequireSnapshot,
        o.TransferMaxRetry,
        o.DiaHttpTimeoutSeconds,
        o.TransferDisableVerify,
        o.TransferDisableDedup,
        o.TransferDisableLegacyFallback,
        o.TransferMirrorHeaderKurOnLines,
        o.TransferRawMode,
    };

    /// <summary>Kısmen gönderilmiş snapshot (legacy'e düşmesin diye).</summary>
    private static bool HasSnapshotIntent(InvoiceTransferRequestDto req)
        => req.HeaderSnapshot != null || (req.SelectedLineSnapshots?.Count > 0);

    /// <summary>
    /// TransferFromSnapshotAsync ile uyumlu: yapısal olarak dolu + işlevsel alanlar (cari, tarih, tür, döviz, satır kod/miktar/fiyat/KDV).
    /// </summary>
    private static bool IsValidSnapshot(InvoiceTransferRequestDto req, out string? rejectReason)
    {
        rejectReason = null;
        if (req.HeaderSnapshot is null || req.SelectedLineSnapshots is not { Count: > 0 })
        {
            rejectReason = "headerSnapshot veya selectedLineSnapshots boş";
            return false;
        }

        var h = req.HeaderSnapshot;
        if (string.IsNullOrWhiteSpace((h.CariCode ?? "").Trim()))
        {
            rejectReason = "headerSnapshot.cariCode";
            return false;
        }
        if (string.IsNullOrWhiteSpace(h.Date))
        {
            rejectReason = "headerSnapshot.date";
            return false;
        }
        if (string.IsNullOrWhiteSpace((h.InvoiceNo ?? "").Trim()))
        {
            rejectReason = "headerSnapshot.invoiceNo";
            return false;
        }
        if (string.IsNullOrWhiteSpace((h.FisNo ?? "").Trim()))
        {
            rejectReason = "headerSnapshot.fisNo";
            return false;
        }
        // invoiceTypeCode / currencyCode bazı tenant raporlarında boş gelebilir.
        // TransferFromSnapshotAsync bunları zorunlu kılmıyor; eksikse legacy'e düşmek yerine snapshot yolu denenmeli.

        foreach (var s in req.SelectedLineSnapshots)
        {
            if (s is null)
            {
                rejectReason = "selectedLineSnapshots null satır";
                return false;
            }
            var c = FirstNonEmpty(s.ItemCode, s.StokKartKodu)?.Trim();
            if (string.IsNullOrWhiteSpace(c))
            {
                rejectReason = "satır itemCode/stokKartKodu";
                return false;
            }
            if (s.Miktar is not decimal m || m <= 0)
            {
                rejectReason = "satır miktar>0";
                return false;
            }
            if (s.BirimFiyati is null)
            {
                rejectReason = "satır birimFiyati";
                return false;
            }
            if (s.BirimFiyati.Value < 0)
            {
                rejectReason = "satır birimFiyati<0";
                return false;
            }
            if (!s.KdvYuzde.HasValue)
            {
                rejectReason = "satır kdvYuzde (muaf için 0)";
                return false;
            }
            var kdv = s.KdvYuzde.Value;
            if (kdv < 0 || kdv > 100)
            {
                rejectReason = "satır kdvYuzde 0..100";
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// scf_fatura_getir bazen __carikartkodu döndürmez; kod <c>cari_kodu</c> / ref içi / liste ile çözülür.
    /// Validasyon kesmeden önce header'a yazar (BuildTargetCard ile uyumlu).
    /// </summary>
    private async Task EnrichSourceHeaderCariIfMissingAsync(
        InvoiceTransferRequestDto req,
        DiaInvoiceDetail src,
        CancellationToken ct)
    {
        var existing = GetResolvedSourceCariKod(src);
        if (!LooksLikeMissingCariCode(existing))
            return;

        var srcCariKey = ParseFirstLong(src.KeyScfCariKartRaw) ?? ParseFirstLong(src.KeyScfCariKartRaw2);
        if (srcCariKey is > 0)
        {
            try
            {
                var srcInfo = await GetCariInfoByKeyCachedAsync(req.SourceFirmaKodu, req.SourceDonemKodu, srcCariKey.Value, ct);
                if (!LooksLikeMissingCariCode(srcInfo.kodu) && !string.IsNullOrWhiteSpace(srcInfo.kodu))
                {
                    src.CariKartKodu = srcInfo.kodu.Trim();
                    return;
                }
            }
            catch
            {
                // ignore — fallback listeye bırak
            }
        }

        try
        {
            var fromList = await GetInvoiceCariFromListCachedAsync(req.SourceFirmaKodu, req.SourceDonemKodu, src.Key, ct);
            if (!string.IsNullOrWhiteSpace(fromList.cariKodu))
                src.CariKartKodu = fromList.cariKodu.Trim();
        }
        catch
        {
            // ignore
        }
    }

    private static string? GetResolvedSourceCariKod(DiaInvoiceDetail src)
    {
        var r = FirstNonEmpty(
            src.CariKartKodu,
            src.CariKartKoduPlain,
            src.CariKoduSnake,
            src.CariKoduCompact,
            TryCariKodFromInvoiceExtra(src),
            ParseCode(src.KeyScfCariKartRaw, "carikartkodu", "__carikartkodu", "cari_kodu", "carikodu", "kodu"),
            ParseCode(src.KeyScfCariKartRaw2, "carikartkodu", "__carikartkodu", "cari_kodu", "carikodu", "kodu"));
        return string.IsNullOrWhiteSpace(r) ? null : r.Trim();
    }

    private static string? TryCariKodFromInvoiceExtra(DiaInvoiceDetail src)
    {
        if (src.ExtraFields == null) return null;
        foreach (var key in new[] { "cari_kodu", "carikodu", "carikartkodu", "__carikartkodu" })
        {
            if (!src.ExtraFields.TryGetValue(key, out var el)) continue;
            if (el.ValueKind == JsonValueKind.String)
            {
                var s = el.GetString();
                if (!string.IsNullOrWhiteSpace(s)) return s.Trim();
            }
            if (el.ValueKind == JsonValueKind.Number)
                return el.GetRawText();
        }
        return null;
    }

    private static bool LooksLikeMissingCariCode(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return true;
        var t = s.Trim();
        return t == "0" || t == "-" || t.Equals("NULL", StringComparison.OrdinalIgnoreCase);
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var v in values)
        {
            if (!string.IsNullOrWhiteSpace(v)) return v;
        }
        return string.Empty;
    }

    private static bool ParseBool(System.Text.Json.JsonElement raw)
    {
        try
        {
            if (raw.ValueKind == System.Text.Json.JsonValueKind.True) return true;
            if (raw.ValueKind == System.Text.Json.JsonValueKind.False) return false;
            if (raw.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                var s = raw.GetString();
                return string.Equals(s, "t", StringComparison.OrdinalIgnoreCase)
                       || string.Equals(s, "true", StringComparison.OrdinalIgnoreCase)
                       || s == "1";
            }
            if (raw.ValueKind == System.Text.Json.JsonValueKind.Number && raw.TryGetInt32(out var n))
                return n != 0;
        }
        catch
        {
            // ignore
        }
        return false;
    }

    private static long? ParseFirstLong(System.Text.Json.JsonElement raw)
    {
        try
        {
            if (raw.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                foreach (var prop in raw.EnumerateObject())
                {
                    if ((prop.NameEquals("key") || prop.NameEquals("_key") || prop.NameEquals("id"))
                        && prop.Value.ValueKind == System.Text.Json.JsonValueKind.Number
                        && prop.Value.TryGetInt64(out var objNum))
                    {
                        return objNum;
                    }

                    if ((prop.NameEquals("key") || prop.NameEquals("_key") || prop.NameEquals("id"))
                        && prop.Value.ValueKind == System.Text.Json.JsonValueKind.String
                        && long.TryParse(prop.Value.GetString(), out var objStr))
                    {
                        return objStr;
                    }
                }
            }

            if (raw.ValueKind == System.Text.Json.JsonValueKind.Number && raw.TryGetInt64(out var n))
                return n;
            if (raw.ValueKind == System.Text.Json.JsonValueKind.String && long.TryParse(raw.GetString(), out var s))
                return s;
            if (raw.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                var arr = raw.EnumerateArray().ToList();
                if (arr.Count > 0)
                {
                    if (arr[0].ValueKind == System.Text.Json.JsonValueKind.Number && arr[0].TryGetInt64(out var a))
                        return a;
                    if (arr[0].ValueKind == System.Text.Json.JsonValueKind.String && long.TryParse(arr[0].GetString(), out var asStr))
                        return asStr;

                    if (arr[0].ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        var inner = arr[0].EnumerateArray().ToList();
                        if (inner.Count > 0)
                        {
                            if (inner[0].ValueKind == System.Text.Json.JsonValueKind.Number && inner[0].TryGetInt64(out var i))
                                return i;
                            if (inner[0].ValueKind == System.Text.Json.JsonValueKind.String && long.TryParse(inner[0].GetString(), out var iStr))
                                return iStr;
                        }
                    }
                }
            }
        }
        catch
        {
            // ignore
        }
        return null;
    }

    /// <summary>
    /// Satır <c>dovizkuru</c> ve <c>raporlamadovizkuru</c> için DİA'nın beklediği sabit 6 ondalık (örn. <c>1.000000</c>).
    /// Başlık veya <paramref name="lineDovizKodu"/> TL ise kur 1 (yabancı başlıkta TL satır).
    /// </summary>
    private static (string DovizKuru, string RaporlamaDovizKuru) FormatLineKurStringsForDiaAdd(
        decimal resolvedLineKur,
        string? headerDovizKod,
        string? lineDovizKodu,
        string? srcHeaderKurString)
    {
        decimal kur;
        if (CanonicalCurrency(headerDovizKod) == "TL" || CanonicalCurrency(lineDovizKodu) == "TL")
            kur = 1m;
        else
        {
            kur = resolvedLineKur;
            if (IsAbsurdInvoiceExchangeRate(kur))
                kur = ParseDecimalSafe(srcHeaderKurString);
            if (kur <= 0m)
                kur = 1m;
        }

        var s = kur.ToString("0.000000", CultureInfo.InvariantCulture);
        return (s, s);
    }

    /// <summary>
    /// FAST/birebir: satır kuru alanlarını fatura başlığındaki stringlerden üret (parse → 6 hane). TL ise 1.000000.
    /// </summary>
    private static (string DovizKuru, string RaporlamaDovizKuru) FormatLineKurMirrorHeader(DiaInvoiceDetail src, string? headerDovizKod, string? srcHeaderKuruFallback)
    {
        if (CanonicalCurrency(headerDovizKod) == "TL" || CanonicalCurrency(src.DovizTuru) == "TL")
            return ("1.000000", "1.000000");

        var dkSrc = FirstNonEmpty(src.DovizKuru, srcHeaderKuruFallback);
        var rkSrc = FirstNonEmpty(src.RaporlamaDovizKuru, src.DovizKuru, srcHeaderKuruFallback);
        return (FormatDiaKurStringInvariant6(dkSrc), FormatDiaKurStringInvariant6(rkSrc));
    }

    private static string FormatDiaKurStringInvariant6(string? raw)
    {
        var d = ParseHeaderKurDecimal(raw);
        if (!d.HasValue || d.Value <= 0m) return "1.000000";
        var rounded = decimal.Round(d.Value, 10, MidpointRounding.AwayFromZero);
        return rounded.ToString("0.000000", CultureInfo.InvariantCulture);
    }

    /// <summary>Kaynak başlık <c>dovizkuru</c> string'i için güvenli parse (TR/US).</summary>
    private static decimal ParseDecimalSafe(string? s) => ParseHeaderKurDecimal(s) ?? 1m;

    /// <summary>
    /// scf_fatura_getir satırındaki <c>dovizkuru</c> bazen hatalı (1e6 vb.); fatura ile aynı dövizde kur her zaman 1 olmalı.
    /// </summary>
    private static decimal ResolveLineKurForDiaPayload(
        decimal? rawLineKur,
        string? headerKurString,
        string invoiceCurrencyCanon,
        string lineCurrencyCanon)
    {
        var headerK = ParseHeaderKurDecimal(headerKurString) ?? 1m;
        if (string.IsNullOrEmpty(lineCurrencyCanon))
            lineCurrencyCanon = invoiceCurrencyCanon;

        // Yabancı para başlıkta TL tutarlı satır: havuzda satır kuru 1; başlık kurunun satıra taşınmaması gerekir.
        if (lineCurrencyCanon == "TL"
            && !string.IsNullOrEmpty(invoiceCurrencyCanon)
            && invoiceCurrencyCanon != "TL")
            return 1m;

        if (!string.IsNullOrEmpty(invoiceCurrencyCanon)
            && !string.IsNullOrEmpty(lineCurrencyCanon)
            && invoiceCurrencyCanon == lineCurrencyCanon)
            return 1m;

        decimal candidate;
        if (!rawLineKur.HasValue)
            candidate = headerK;
        else if (!IsAbsurdInvoiceExchangeRate(rawLineKur.Value))
            candidate = rawLineKur.Value;
        else
            candidate = IsAbsurdInvoiceExchangeRate(headerK) ? 1m : headerK;

        if (IsAbsurdInvoiceExchangeRate(candidate))
            candidate = 1m;
        if (candidate <= 0m)
            candidate = 1m;
        return decimal.Round(candidate, 10, MidpointRounding.AwayFromZero);
    }

    /// <summary>Faturalarda makul olmayan kur (alan karışımı / yanlış parse).</summary>
    private static bool IsAbsurdInvoiceExchangeRate(decimal k)
    {
        var a = Math.Abs(k);
        if (a == 0m) return true;
        return a > 100_000m || (a < 0.0000001m && a > 0m);
    }

    /// <summary>
    /// DİA fatura satırı sayısal alanları genelde decimal(20,10) ile doğrular; fazla basamak → document_create_failed (yerelbirimfiyati vb.).
    /// RAW snapshot da aynı formata zorlanır (<see cref="RawInvariantDecimal"/>).
    /// </summary>
    private static string FormatDiaLineDecimal(decimal? value)
    {
        var d = decimal.Round(value ?? 0m, 10, MidpointRounding.AwayFromZero);
        const decimal max = 9999999999.9999999999m;
        if (d > max) d = max;
        else if (d < -max) d = -max;
        return d.ToString(CultureInfo.InvariantCulture);
    }

    private static decimal? ParseHeaderKurDecimal(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var t = s.Trim();
        if (decimal.TryParse(t, NumberStyles.Any, CultureInfo.InvariantCulture, out var d)) return d;
        if (decimal.TryParse(t, NumberStyles.Any, CultureInfo.GetCultureInfo("tr-TR"), out d)) return d;
        return null;
    }
}
