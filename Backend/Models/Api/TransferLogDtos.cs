using System.Text.Json.Serialization;

namespace DiaErpIntegration.API.Models.Api;

public sealed class TransferLogBatchDto
{
    [JsonPropertyName("items")]
    public List<TransferLogItemDto> Items { get; set; } = new();
}

public sealed class TransferLogItemDto
{
    [JsonPropertyName("invoiceKey")]
    public long InvoiceKey { get; set; }

    [JsonPropertyName("lineCount")]
    public int LineCount { get; set; }

    [JsonPropertyName("targetFirma")]
    public int TargetFirma { get; set; }

    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }

    [JsonPropertyName("timestamp")]
    public string Timestamp { get; set; } = "";
}

