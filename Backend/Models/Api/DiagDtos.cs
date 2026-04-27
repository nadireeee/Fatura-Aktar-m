using System.Text.Json.Serialization;

namespace DiaErpIntegration.API.Models.Api;

public sealed class DiagVersionDto
{
    [JsonPropertyName("instanceId")]
    public string InstanceId { get; set; } = "";

    [JsonPropertyName("startedAtUtc")]
    public string StartedAtUtc { get; set; } = "";

    [JsonPropertyName("assembly")]
    public string Assembly { get; set; } = "";

    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("informationalVersion")]
    public string InformationalVersion { get; set; } = "";
}

