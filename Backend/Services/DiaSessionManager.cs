using System.Text.Json;
using System.Text.Json.Serialization;
using DiaErpIntegration.API.Models;
using DiaErpIntegration.API.Options;
using Microsoft.Extensions.Options;

namespace DiaErpIntegration.API.Services;

/// <summary>
/// DİA WS v3 session_id yönetimi:
/// - Login yapar, session_id cache'ler
/// - INVALID_SESSION alınırsa 1 kez re-login dener
/// </summary>
public sealed class DiaSessionManager
{
    private readonly HttpClient _http;
    private readonly DiaOptions _opt;
    private readonly ILogger<DiaSessionManager> _logger;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private string _sessionId = string.Empty;
    private DateTimeOffset _expiresAt = DateTimeOffset.MinValue;

    public DiaSessionManager(HttpClient http, IOptions<DiaOptions> opt, ILogger<DiaSessionManager> logger)
    {
        _http = http;
        _opt = opt.Value;
        _logger = logger;
    }

    public async Task<string> GetSessionIdAsync(CancellationToken ct = default)
    {
        // Fast path
        if (!string.IsNullOrWhiteSpace(_sessionId) && DateTimeOffset.UtcNow < _expiresAt)
            return _sessionId;

        await _gate.WaitAsync(ct);
        try
        {
            if (!string.IsNullOrWhiteSpace(_sessionId) && DateTimeOffset.UtcNow < _expiresAt)
                return _sessionId;

            _logger.LogInformation("DIA login starting for {U}", _opt.Username);

            var req = new DiaLoginRequest
            {
                Login = new DiaLoginData
                {
                    Username = _opt.Username,
                    Password = _opt.Password,
                    DisconnectSameUser = "True",
                    Params = new Dictionary<string, string>
                    {
                        ["apikey"] = _opt.ApiKey
                    }
                }
            };

            DiaLoginResponse? body = null;
            Exception? lastEx = null;
            // DİA testi sırasında /sis/json ara sıra 500 dönebiliyor; kısa retry uyguluyoruz.
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    using var res = await _http.PostAsJsonAsync("sis/json", req, ct);
                    if (!res.IsSuccessStatusCode)
                    {
                        var errBody = await res.Content.ReadAsStringAsync(ct);
                        throw new HttpRequestException($"DIA login HTTP {(int)res.StatusCode}: {errBody}");
                    }

                    var rawLogin = await res.Content.ReadAsStringAsync(ct);
                    var excerpt = rawLogin.Length > 2048 ? rawLogin[..2048] + "…(truncated)" : rawLogin;
                    _logger.LogInformation("DIA login raw JSON (excerpt): {Excerpt}", excerpt);
                    try
                    {
                        using var doc = JsonDocument.Parse(rawLogin);
                        var root = doc.RootElement;
                        if (root.TryGetProperty("result", out var resultEl))
                        {
                            var rs = resultEl.ValueKind == JsonValueKind.Object || resultEl.ValueKind == JsonValueKind.Array
                                ? resultEl.GetRawText()
                                : resultEl.ToString();
                            var rEx = rs.Length > 800 ? rs[..800] + "…" : rs;
                            _logger.LogInformation(
                                "DIA login 'result' present (kullanıcı varsayılan firma/dönem bilgisi burada olabilir): {Fragment}",
                                rEx);
                        }
                    }
                    catch (Exception logEx)
                    {
                        _logger.LogDebug(logEx, "DIA login JSON parse (logging only) skipped.");
                    }

                    body = JsonSerializer.Deserialize<DiaLoginResponse>(rawLogin, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        NumberHandling = JsonNumberHandling.AllowReadingFromString,
                    });
                    if (body is null || body.Code != 200 || string.IsNullOrWhiteSpace(body.SessionId))
                        throw new InvalidOperationException("DIA login failed (code != 200).");

                    break;
                }
                catch (Exception ex) when (attempt < 3)
                {
                    lastEx = ex;
                    _logger.LogWarning(ex, "DIA login attempt {Attempt}/3 failed; retrying...", attempt);
                    await Task.Delay(250 * attempt, ct);
                }
            }

            if (body is null || body.Code != 200 || string.IsNullOrWhiteSpace(body.SessionId))
                throw lastEx ?? new InvalidOperationException("DIA login failed after retries.");

            _sessionId = body.SessionId;
            _expiresAt = DateTimeOffset.UtcNow.AddMinutes(Math.Max(1, _opt.SessionTtlMinutes));
            var suf = _sessionId.Length >= 4 ? _sessionId[^4..] : _sessionId;
            _logger.LogInformation("DIA session ready idSuffix={Suffix}", suf);
            return _sessionId;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task InvalidateAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            _sessionId = string.Empty;
            _expiresAt = DateTimeOffset.MinValue;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>İstek loglarında korelasyon için oturum kimliğinin son parçası (tam session_id loglanmaz).</summary>
    public string GetDiagnosticsSessionSuffix()
    {
        if (string.IsNullOrWhiteSpace(_sessionId)) return "(no-session)";
        return _sessionId.Length >= 6 ? _sessionId[^6..] : _sessionId;
    }
}

