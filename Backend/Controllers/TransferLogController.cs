using DiaErpIntegration.API.Models.Api;
using Microsoft.AspNetCore.Mvc;

namespace DiaErpIntegration.API.Controllers;

[ApiController]
[Route("api/transfer")]
public sealed class TransferLogController : ControllerBase
{
    private readonly ILogger<TransferLogController> _logger;

    public TransferLogController(ILogger<TransferLogController> logger)
    {
        _logger = logger;
    }

    [HttpGet("log/recent")]
    public async Task<ActionResult<TransferLogBatchDto>> Recent([FromQuery] int limit = 500, CancellationToken ct = default)
    {
        try
        {
            limit = Math.Clamp(limit, 1, 5000);
            var dir = Path.Combine(AppContext.BaseDirectory, "App_Data");
            var path = Path.Combine(dir, "transfer_logs.jsonl");
            if (!System.IO.File.Exists(path))
                return Ok(new TransferLogBatchDto { Items = new List<TransferLogItemDto>() });

            // Basit yaklaşım: dosya küçük/orta; son N satırı al.
            // (Gerekirse daha sonra tail/seek optimizasyonu yapılır.)
            var lines = await System.IO.File.ReadAllLinesAsync(path, ct);
            var slice = lines.Length <= limit ? lines : lines.Skip(lines.Length - limit);
            var items = new List<TransferLogItemDto>();
            foreach (var ln in slice)
            {
                try
                {
                    var it = System.Text.Json.JsonSerializer.Deserialize<TransferLogItemDto>(ln);
                    if (it != null && it.InvoiceKey > 0) items.Add(it);
                }
                catch { /* ignore bad line */ }
            }
            return Ok(new TransferLogBatchDto { Items = items });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Transfer log read failed");
            return Ok(new TransferLogBatchDto { Items = new List<TransferLogItemDto>() });
        }
    }

    [HttpPost("log")]
    public async Task<ActionResult> Log([FromBody] TransferLogBatchDto? batch, CancellationToken ct)
    {
        try
        {
            if (batch?.Items == null || batch.Items.Count == 0)
                return Ok(new { success = true, written = 0 });

            var dir = Path.Combine(AppContext.BaseDirectory, "App_Data");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "transfer_logs.jsonl");

            var lines = batch.Items
                .Where(static i => i is not null && i.InvoiceKey > 0)
                .Select(static i => System.Text.Json.JsonSerializer.Serialize(i))
                .ToList();

            if (lines.Count == 0)
                return Ok(new { success = true, written = 0 });

            await System.IO.File.AppendAllLinesAsync(path, lines, ct);
            _logger.LogInformation("Transfer logs written: count={Count} path={Path}", lines.Count, path);
            return Ok(new { success = true, written = lines.Count });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Transfer log write failed");
            // UI fire-and-forget; 500 spam'i kes ama sunucu tarafında logla.
            return Ok(new { success = false, written = 0, message = "log_write_failed" });
        }
    }
}

