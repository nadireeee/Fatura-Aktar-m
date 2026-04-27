using System.Linq;
using DiaErpIntegration.API.Models.Api;
using DiaErpIntegration.API.Models.DiaV3Json;

namespace DiaErpIntegration.API.Services;

/// <summary>
/// Tek kaynaktan (yetkili firma ağacı) tutarlı lookup şekli — controller ve testler için.
/// </summary>
public static class LookupNormalizer
{
    public static CompanyLookupNormalizedDto NormalizeCompanyLookup(DiaAuthorizedCompanyPeriodBranchItem raw)
    {
        var donemler = new List<NormalizedDonemDto>();
        foreach (var d in raw.Donemler.Concat(raw.DonemFallback).Concat(raw.DonemListFallback)
                     .GroupBy(x => x.DonemKodu).Select(g => g.First()))
        {
            if (d.DonemKodu <= 0) continue;
            donemler.Add(new NormalizedDonemDto
            {
                Key = d.Key,
                DonemKodu = d.DonemKodu,
                GorunenKod = string.IsNullOrWhiteSpace(d.GorunenDonemKodu) ? d.DonemKodu.ToString() : d.GorunenDonemKodu,
                Ontanimli = string.Equals(d.Ontanimli, "t", StringComparison.OrdinalIgnoreCase),
                BaslangicTarihi = d.BaslangicTarihi,
                BitisTarihi = d.BitisTarihi
            });
        }

        var subeler = raw.Subeler.Select(s => new NormalizedSubeDto
        {
            Key = s.Key,
            SubeAdi = s.SubeAdi,
            Depolar = s.Depolar.Select(dep => new NormalizedDepoDto
            {
                Key = dep.Key,
                DepoAdi = dep.DepoAdi
            }).ToList()
        }).ToList();

        return new CompanyLookupNormalizedDto
        {
            FirmaKodu = raw.FirmaKodu,
            FirmaAdi = raw.FirmaAdi,
            Donemler = donemler,
            Subeler = subeler
        };
    }

    public static BranchDto ToBranchDto(DiaAuthorizedBranchItem s) =>
        new() { Key = s.Key, SubeAdi = s.SubeAdi };

    public static DepotDto ToDepotDto(DiaAuthorizedDepotItem d) =>
        new() { Key = d.Key, DepoAdi = d.DepoAdi };
}
