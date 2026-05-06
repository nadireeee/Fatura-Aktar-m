using System.Text.Json.Serialization;

namespace DiaErpIntegration.API.Models.DiaV3Json;

// RPR module: rpr_raporsonuc_getir
// Endpoint: POST /rpr/json
// Result is base64 encoded payload; backend must decode + JSON parse.

public sealed class DiaRprRaporSonucGetirRequest
{
    [JsonPropertyName("rpr_raporsonuc_getir")]
    public DiaRprRaporSonucGetirInput Payload { get; set; } = new();
}

public sealed class DiaRprRaporSonucGetirInput : DiaErpIntegration.API.Models.DiaWsRequestBase
{
    [JsonPropertyName("report_code")]
    public string ReportCode { get; set; } = string.Empty;

    [JsonPropertyName("format_type")]
    public string FormatType { get; set; } = "json";

    // DİA dokümanı "AnyDict" diyor; burada serbest parametre sözlüğü gönderiyoruz.
    [JsonPropertyName("param")]
    public Dictionary<string, object?> Param { get; set; } = new();
}

