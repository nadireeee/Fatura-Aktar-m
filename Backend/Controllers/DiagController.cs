using System.Reflection;
using DiaErpIntegration.API.Models.Api;
using DiaErpIntegration.API.Services;
using Microsoft.AspNetCore.Mvc;

namespace DiaErpIntegration.API.Controllers;

[ApiController]
[Route("api/diag")]
public sealed class DiagController : ControllerBase
{
    private static readonly string _instanceId = Guid.NewGuid().ToString("N");
    private static readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;

    [HttpGet("version")]
    public ActionResult<DiagVersionDto> GetVersion()
    {
        var asm = typeof(DiagController).Assembly;
        var name = asm.GetName();
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";

        return Ok(new DiagVersionDto
        {
            InstanceId = _instanceId,
            StartedAtUtc = _startedAt.ToString("O"),
            Assembly = name.Name ?? "DiaErpIntegration.API",
            Version = name.Version?.ToString() ?? "",
            InformationalVersion = info
        });
    }

    private readonly InvoiceTransferService _transfer;

    public DiagController(InvoiceTransferService transfer)
    {
        _transfer = transfer;
    }

    public sealed class ClearTransferStateRequest
    {
        public long? SourceInvoiceKey { get; set; }
        public long? SourceLineKey { get; set; }
    }

    [HttpPost("clear-transfer-state")]
    public IActionResult ClearTransferState([FromBody] ClearTransferStateRequest req)
    {
        var cleared = _transfer.ClearTransferState(req.SourceInvoiceKey, req.SourceLineKey);
        return Ok(new { cleared });
    }

    [HttpGet("transfer-state")]
    public IActionResult GetTransferState([FromQuery] long? sourceInvoiceKey = null)
    {
        var snapshot = _transfer.GetTransferStateDebugSnapshot(sourceInvoiceKey);
        return Ok(snapshot);
    }
}

