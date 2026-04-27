using System.ComponentModel.DataAnnotations;

namespace DiaErpIntegration.API.Options;

public sealed class DiaOptions
{
    /// <summary>Mock | Real</summary>
    public string Mode { get; set; } = "Mock";

    /// <summary>Örn: https://api.dia.com.tr/api/v3/</summary>
    public string BaseUrl { get; set; } = string.Empty;

    // Real modda zorunlu (Program.cs içinde kontrol ediliyor)
    public string Username { get; set; } = string.Empty;

    // Real modda zorunlu (Program.cs içinde kontrol ediliyor)
    public string Password { get; set; } = string.Empty;

    // Real modda zorunlu (Program.cs içinde kontrol ediliyor)
    public string ApiKey { get; set; } = string.Empty;

    // Optional defaults (0 => unset). These should be treated as fallbacks only.
    public int DefaultSourceFirmaKodu { get; set; } = 0;
    public int DefaultSourceDonemKodu { get; set; } = 0;
    public long DefaultSourceSubeKey { get; set; } = 0;

    // Pool (Havuz) firm is fixed and must be set in Real mode.
    public int PoolFirmaKodu { get; set; } = 0;
    public string PoolFirmaAdi { get; set; } = string.Empty;
    // Opsiyonel: dinamik şube kolonu için manual override (örn: __dinamik__2)
    public string BranchDynamicColumnOverride { get; set; } = string.Empty;

    /// <summary>Login sonrası session cache süresi (dakika).</summary>
    public int SessionTtlMinutes { get; set; } = 30;
}

