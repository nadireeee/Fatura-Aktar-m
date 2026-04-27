namespace DiaErpIntegration.API.Services;

public sealed class DiaTargetStockResolveResult
{
    public string StokKodu { get; init; } = "";

    // scf_fatura_ekle line._key_kalemturu expects this (kalemturu master key)
    public long? TargetKalemTuruKey { get; init; }

    // unit resolvers like stk_stokkart_birimleri_listele expect this (stock card key)
    public long? TargetStokKartKey { get; init; }

    // True when resolved via hizmet kartı endpoints (HZMT lines).
    public bool IsHizmetKart { get; init; }

    public string? ServiceUsed { get; init; }
    public string? EndpointUsed { get; init; }
    public int RowCount { get; init; }

    public string? MatchedCandidate { get; init; }
    public string? MatchedTargetCode { get; init; }
    public string? MatchedTargetAciklama { get; init; }
}

