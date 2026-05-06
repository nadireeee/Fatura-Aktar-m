using DiaErpIntegration.API.Models.Api;
using DiaErpIntegration.API.Options;
using DiaErpIntegration.API.Services;
using Microsoft.AspNetCore.Mvc;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace DiaErpIntegration.API.Controllers;

[ApiController]
[Route("api")]
public sealed class FaturaAktarController : ControllerBase
{
    private static readonly JsonSerializerOptions LogJson = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static string SerializeTransferRequestForLog(InvoiceTransferRequestDto r)
    {
        try
        {
            var preview = new
            {
                r.SourceFirmaKodu,
                r.SourceDonemKodu,
                r.SourceSubeKey,
                r.SourceDepoKey,
                r.SourceInvoiceKey,
                selectedKalemCount = r.SelectedKalemKeys?.Count ?? 0,
                selectedKalemPreview = r.SelectedKalemKeys?.Take(40).ToArray(),
                snapshotCount = r.SelectedLineSnapshots?.Count ?? 0,
                snapshotPreview = r.SelectedLineSnapshots?.Take(5).ToArray(),
                headerSnapshot = r.HeaderSnapshot != null,
                r.TargetFirmaKodu,
                r.TargetDonemKodu,
                r.TargetSubeKey,
                r.TargetDepoKey,
            };
            return JsonSerializer.Serialize(preview, LogJson);
        }
        catch
        {
            return "{}";
        }
    }

    private readonly InvoiceTransferService _transfer;
    private readonly ILogger<FaturaAktarController> _logger;
    private readonly DiaOptions _opt;

    public FaturaAktarController(InvoiceTransferService transfer, IOptions<DiaOptions> opt, ILogger<FaturaAktarController> logger)
    {
        _transfer = transfer;
        _opt = opt.Value;
        _logger = logger;
    }

    [HttpPost("fatura-aktar")]
    public async Task<ActionResult<FaturaAktarResponseDto>> Aktar([FromBody] FaturaAktarRequestDto? req, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        if (req is null)
            return UnprocessableEntity(new FaturaAktarResponseDto { Success = false });

        if (req.TargetFirmaKodu <= 0)
            return BadRequest(new { code = "target_firma_missing", message = "Hedef firma seçilmedi." });

        if (req.Invoices == null || req.Invoices.Count == 0)
            return Ok(new FaturaAktarResponseDto { Success = true, Total = 0, SuccessCount = 0, FailedCount = 0, DurationMs = 0, Results = new List<FaturaAktarResultItemDto>() });

        // Aynı fatura birden fazla gelmesin.
        var invoiceItems = req.Invoices
            .Where(x => x.SourceInvoiceKey > 0)
            .GroupBy(x => x.SourceInvoiceKey)
            .Select(g =>
            {
                var merged = g.SelectMany(x => x.SelectedKalemKeys ?? new List<long>())
                    .Where(k => k > 0)
                    .Distinct()
                    .ToList();
                var snaps = g.SelectMany(x => x.SelectedLineSnapshots ?? new List<InvoiceTransferLineSnapshotDto>())
                    .Where(s => s != null)
                    .ToList();
                var header = g.Select(x => x.HeaderSnapshot).FirstOrDefault(h => h != null);
                return new FaturaAktarInvoiceItemDto
                {
                    SourceInvoiceKey = g.Key,
                    SelectedKalemKeys = merged,
                    SelectedLineSnapshots = snaps,
                    HeaderSnapshot = header
                };
            })
            .ToList();

        _logger.LogDebug(
            "fatura-aktar: {InvoiceCount} fatura, paralellik={Concurrency}",
            invoiceItems.Count,
            _opt.TransferConcurrency > 0
                ? Math.Max(1, _opt.TransferConcurrency)
                : Math.Max(1, Math.Min(8, Environment.ProcessorCount)));

        // Paralellik: DiaSettings:TransferConcurrency (>0) yoksa min(8, CPU).
        // Önceki sürüm: toplu gruplar semaphore alıp grup *içinde* faturaları sırayla işliyordu → pratikte tek iş parçacığı.
        var concurrency = _opt.TransferConcurrency > 0
            ? Math.Max(1, _opt.TransferConcurrency)
            : Math.Max(1, Math.Min(8, Environment.ProcessorCount));
        // 0 = yeniden deneme yok (snapshot/debug); varsayılan için Options veya negatif kullanımına göre 2.
        var maxRetry = _opt.TransferMaxRetry >= 0 ? _opt.TransferMaxRetry : 2;
        static TimeSpan Backoff(int attempt) => attempt switch
        {
            1 => TimeSpan.FromSeconds(1),
            2 => TimeSpan.FromSeconds(3),
            _ => TimeSpan.FromSeconds(5)
        };

        var bag = new ConcurrentBag<FaturaAktarResultItemDto>();

        InvoiceTransferResultDto UnexpectedFail(Exception ex)
        {
            if (ex is OperationCanceledException)
            {
                var timeoutSec = _opt.TransferInvoiceTimeoutSeconds > 0 ? _opt.TransferInvoiceTimeoutSeconds : 45;
                var msg = ct.IsCancellationRequested
                    ? "Aktarım iptal edildi (istek/iptal)."
                    : $"Zaman aşımı veya DİA yanıtı kesildi (tek fatura limiti ~{timeoutSec} sn). DiaSettings:TransferInvoiceTimeoutSeconds artırın; gerekirse TransferMaxRetry > 0 yapın.";
                return new InvoiceTransferResultDto
                {
                    Success = false,
                    Message = msg,
                    FailureStage = ct.IsCancellationRequested ? "canceled" : "timeout",
                    FailureCode = ct.IsCancellationRequested ? "transfer_canceled" : "transfer_timeout",
                    Errors = new List<string> { (ex.Message ?? "").Trim() }
                };
            }

            var detail = (ex.Message ?? "").Trim();
            return new InvoiceTransferResultDto
            {
                Success = false,
                Message = detail.Length > 0
                    ? $"Aktarım sırasında beklenmeyen hata: {detail}"
                    : "Aktarım sırasında beklenmeyen hata oluştu.",
                FailureStage = "unexpected",
                FailureCode = "unexpected_transfer_error",
                Errors = new List<string> { detail }
            };
        }

        async Task<InvoiceTransferResultDto> TransferCoreAsync(FaturaAktarInvoiceItemDto inv, int retryAttempt, CancellationToken invoiceCt)
        {
            var one = new InvoiceTransferRequestDto
            {
                SourceFirmaKodu = req.SourceFirmaKodu,
                SourceDonemKodu = req.SourceDonemKodu,
                SourceSubeKey = req.SourceSubeKey,
                SourceDepoKey = req.SourceDepoKey,
                SourceInvoiceKey = inv.SourceInvoiceKey,
                SelectedKalemKeys = inv.SelectedKalemKeys ?? new List<long>(),
                SelectedLineSnapshots = inv.SelectedLineSnapshots ?? new List<InvoiceTransferLineSnapshotDto>(),
                HeaderSnapshot = inv.HeaderSnapshot,
                TargetFirmaKodu = req.TargetFirmaKodu,
                TargetDonemKodu = req.TargetDonemKodu,
                TargetSubeKey = req.TargetSubeKey,
                TargetDepoKey = req.TargetDepoKey,
                UseDynamicBranch = inv.UseDynamicBranch
            };

            var invoiceSw = Stopwatch.StartNew();
            try
            {
                var resNullable = await _transfer.TransferAsync(one, invoiceCt);
                InvoiceTransferResultDto res = resNullable ?? new InvoiceTransferResultDto
                {
                    Success = false,
                    Message = "Aktarım boş sonuç döndü.",
                    FailureStage = "unexpected",
                    FailureCode = "null_result"
                };
                invoiceSw.Stop();
                _logger.LogInformation(
                    "Invoice {InvoiceKey} transfer finished in {Duration}ms success={Success} targetFirma={TargetFirma} retryAttempt={RetryAttempt}",
                    inv.SourceInvoiceKey, invoiceSw.ElapsedMilliseconds, res.Success == true, req.TargetFirmaKodu, retryAttempt);
                return res;
            }
            catch (Exception ex)
            {
                invoiceSw.Stop();
                _logger.LogError(ex,
                    "TRANSFER ERROR invoice={InvoiceKey} durationMs={Duration} targetFirma={TargetFirma} retryAttempt={RetryAttempt} request={Request}",
                    inv.SourceInvoiceKey, invoiceSw.ElapsedMilliseconds, req.TargetFirmaKodu, retryAttempt, SerializeTransferRequestForLog(one));
                throw;
            }
        }

        async Task ProcessInvoiceWithRetryAsync(FaturaAktarInvoiceItemDto inv)
        {
            var attempt = 0; // 0=first try, then 1..maxRetry retries
            while (true)
            {
                try
                {
                    var timeoutSec = _opt.TransferInvoiceTimeoutSeconds > 0 ? _opt.TransferInvoiceTimeoutSeconds : 45;
                    using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec));
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
                    var res = await TransferCoreAsync(inv, attempt, linked.Token);
                    bag.Add(new FaturaAktarResultItemDto { SourceInvoiceKey = inv.SourceInvoiceKey, Result = res });
                    return;
                }
                catch (OperationCanceledException ex) when (!ct.IsCancellationRequested && attempt < maxRetry)
                {
                    attempt++;
                    _logger.LogWarning(ex, "Invoice retry (timeout/cancel) invoiceKey={InvoiceKey} attempt={Attempt}/{MaxRetry}",
                        inv.SourceInvoiceKey, attempt, maxRetry);
                    await Task.Delay(Backoff(attempt), ct);
                }
                catch (Exception ex) when (attempt < maxRetry)
                {
                    attempt++;
                    _logger.LogWarning(ex, "Invoice retry invoiceKey={InvoiceKey} attempt={Attempt}/{MaxRetry}",
                        inv.SourceInvoiceKey, attempt, maxRetry);
                    await Task.Delay(Backoff(attempt), ct);
                }
                catch (Exception ex)
                {
                    // Final failure (no more retries): record single failure result
                    bag.Add(new FaturaAktarResultItemDto { SourceInvoiceKey = inv.SourceInvoiceKey, Result = UnexpectedFail(ex) });
                    return;
                }
            }
        }

        await Parallel.ForEachAsync(
            invoiceItems,
            new ParallelOptions { MaxDegreeOfParallelism = concurrency, CancellationToken = ct },
            async (inv, token) =>
            {
                token.ThrowIfCancellationRequested();
                await ProcessInvoiceWithRetryAsync(inv);
            });

        // UI'da stabil sıra için: request sırasına göre diz.
        var byKey = bag
            .GroupBy(x => x.SourceInvoiceKey)
            .ToDictionary(g => g.Key, g => g.First());
        var items = invoiceItems
            .Select(x => byKey.TryGetValue(x.SourceInvoiceKey, out var it)
                ? it
                : new FaturaAktarResultItemDto
                {
                    SourceInvoiceKey = x.SourceInvoiceKey,
                    Result = new InvoiceTransferResultDto
                    {
                        Success = false,
                        Message = "Aktarım sonucu bulunamadı.",
                        FailureStage = "unexpected",
                        FailureCode = "missing_result"
                    }
                })
            .ToList();

        var successCount = items.Count(i => i.Result?.Success == true);
        var failedCount = items.Count - successCount;
        sw.Stop();
        return Ok(new FaturaAktarResponseDto
        {
            Success = items.All(i => i.Result?.Success == true),
            Total = items.Count,
            SuccessCount = successCount,
            FailedCount = failedCount,
            DurationMs = sw.ElapsedMilliseconds,
            Results = items
        });
    }
}

