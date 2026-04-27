using System.Collections.Concurrent;
using DiaErpIntegration.API.Models;

namespace DiaErpIntegration.API.Services;

/// <summary>
/// Firma + dönem + filtre için <c>scf_fatura_listele_ayrintili</c> taraması sonucu (ŞUBELER/__dinamik__2 dolu fatura anahtarları) kısa süreli önbellek.
/// </summary>
public sealed class InvoiceDinamik2ClassificationCache
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<InvoiceDinamik2ClassificationCache> _logger;
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(90);

    private sealed class Entry
    {
        public required HashSet<long> Keys { get; init; }
        public DateTimeOffset ExpiresAt { get; init; }
        public Dinamik2ScanResult? LastScan { get; init; }
    }

    private readonly ConcurrentDictionary<string, Entry> _cache = new();

    public InvoiceDinamik2ClassificationCache(
        IServiceScopeFactory scopeFactory,
        ILogger<InvoiceDinamik2ClassificationCache> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    private static string CacheKey(int firmaKodu, int donemKodu, string filters)
        => $"{firmaKodu}|{donemKodu}|{filters}";

    /// <returns>FromCache true ise Keys önbellekten; ScanStats önbellekteki son tarama özeti veya null.</returns>
    public async Task<(HashSet<long> Keys, Dinamik2ScanResult? ScanStats, bool FromCache)> GetOrScanAsync(
        int firmaKodu,
        int donemKodu,
        string filters,
        CancellationToken cancellationToken = default)
    {
        var k = CacheKey(firmaKodu, donemKodu, filters ?? string.Empty);
        if (_cache.TryGetValue(k, out var hit) && hit.ExpiresAt > DateTimeOffset.UtcNow)
        {
            _logger.LogDebug("ŞUBELER dinamik2 anahtar önbellek isabeti: {Key} count={Count}", k, hit.Keys.Count);
            return (new HashSet<long>(hit.Keys), hit.LastScan, true);
        }

        using var scope = _scopeFactory.CreateScope();
        var dia = scope.ServiceProvider.GetRequiredService<IDiaWsClient>();
        var scan = await dia.ScanInvoiceKeysWithSubelerDinamik2Async(firmaKodu, donemKodu, filters ?? string.Empty, cancellationToken)
            .ConfigureAwait(false);

        var entry = new Entry
        {
            Keys = new HashSet<long>(scan.Keys),
            ExpiresAt = DateTimeOffset.UtcNow.Add(Ttl),
            LastScan = scan
        };
        _cache[k] = entry;
        _logger.LogInformation(
            "ŞUBELER dinamik2 tarama tamamlandı (önbelleğe yazıldı): {Key} count={Count} batches={Batches} cap={Cap} ms={Ms}",
            k,
            scan.Keys.Count,
            scan.BatchesFetched,
            scan.CapReached,
            scan.ElapsedMs);
        return (new HashSet<long>(scan.Keys), scan, false);
    }
}
