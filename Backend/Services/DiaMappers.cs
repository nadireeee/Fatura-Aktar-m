using System.Text.Json;
using DiaErpIntegration.API.Models.Api;
using DiaErpIntegration.API.Models.DiaV3Json;
using TransferStatus = DiaErpIntegration.API.Models.TransferStatus;

namespace DiaErpIntegration.API.Services;

public static class DiaMappers
{
    public static InvoiceListRowDto ToApiDto(
        this DiaInvoiceListItem src,
        TransferStatus? transferStatus = null,
        int? bekleyenKalemSayisi = null,
        bool? hasLineBranchSelection = null,
        string? effectiveTransferType = null) => new()
    {
        Key = src.Key.ToString(),
        FisNo = src.FisNo,
        BelgeNo = src.BelgeNo,
        BelgeNo2 = src.BelgeNo2,
        Tarih = src.Tarih,
        Turu = src.Turu,
        TuruAck = src.TuruAck,
        TuruKisa = src.TuruKisa,
        CariKartKodu = src.CariKartKodu,
        CariUnvan = src.CariUnvan,
        SourceSubeAdi = src.SourceSubeAdi,
        SourceDepoAdi = src.SourceDepoAdi,
        DestSubeAdi = src.DestSubeAdi,
        DestDepoAdi = src.DestDepoAdi,
        FirmaAdi = src.FirmaAdi,
        DovizTuru = src.DovizTuru,
        Toplam = src.Toplam,
        ToplamKdv = src.ToplamKdv,
        Net = src.Net,
        Iptal = ParseBool(src.IptalRaw),
        OdemePlani = FirstNonEmpty(src.OdemePlani, src.OdemePlaniKodu, src.OdemePlaniUnderscore),
        OdemePlaniAck = FirstNonEmpty(src.OdemePlaniAck, src.OdemePlaniAckUnderscore),
        ProjeKodu = src.ProjeKodu,
        ProjeAciklama = src.ProjeAciklama,
        TransferStatus = transferStatus ?? TransferStatus.Bekliyor,
        BekleyenKalemSayisi = Math.Max(0, bekleyenKalemSayisi ?? 0),
        HasLineBranchSelection = hasLineBranchSelection ?? false,
        HasHeaderOnlyBranch = !(hasLineBranchSelection ?? false) && !string.IsNullOrWhiteSpace(src.SourceSubeAdi),
        EffectiveTransferType = effectiveTransferType ?? ((hasLineBranchSelection ?? false) ? "VİRMAN" : "FATURA"),
    };

    public static InvoiceDetailDto ToApiDto(this DiaInvoiceDetail src) => new()
    {
        Key = src.Key.ToString(),
        FisNo = src.FisNo,
        Tarih = src.Tarih,
        CariKartKodu = src.CariKartKodu,
        CariUnvan = src.CariUnvan,
        OdemePlani = ResolveOdemePlaniFromDetail(src).kodu,
        OdemePlaniAck = ResolveOdemePlaniFromDetail(src).aciklama,
        SatisElemani = TryResolveSatisElemani(src),
        Kalemler = (src.Lines ?? new List<DiaInvoiceLine>()).Select(l => l.ToApiDto()).ToList()
    };

    private static (string? kodu, string? aciklama) ResolveOdemePlaniFromDetail(DiaInvoiceDetail src)
    {
        try
        {
            // scf_fatura_getir: _key_scf_odeme_plani genelde object olarak döner (kodu/aciklama)
            var raw = src.KeyScfOdemePlaniRaw;
            if (raw.ValueKind == JsonValueKind.Object)
            {
                var kodu = ParseCode(raw, "kodu", "odemeplani", "odemeplani_kodu");
                var ack = ParseCode(raw, "aciklama", "odemeplaniack", "ack");
                return (kodu, ack);
            }

            // Bazı tenantlarda "odemeplani"/"odemeplaniack" gibi alanlar extension data'da olabilir.
            if (src.ExtraFields != null)
            {
                var kodu2 = src.ExtraFields.TryGetValue("odemeplani", out var k) ? ParseFlexibleText(k) : null;
                var ack2 = src.ExtraFields.TryGetValue("odemeplaniack", out var a) ? ParseFlexibleText(a) : null;
                if (!string.IsNullOrWhiteSpace(kodu2) || !string.IsNullOrWhiteSpace(ack2))
                    return (kodu2, ack2);
            }
        }
        catch
        {
            // ignore
        }
        return (null, null);
    }

    private static string? TryResolveSatisElemani(DiaInvoiceDetail src)
    {
        try
        {
            if (src.ExtraFields == null) return null;

            // 1) _key_scf_satiselemani objesi içinden aciklama
            if (src.ExtraFields.TryGetValue("_key_scf_satiselemani", out var seObj))
                return FirstNonEmpty(ParseCode(seObj, "aciklama", "adi", "uzunadi"), ParseCode(seObj, "kodu", "kod"));

            // 2) _key_satiselemanlari[] içinden ilk satır
            if (src.ExtraFields.TryGetValue("_key_satiselemanlari", out var seArr) &&
                seArr.ValueKind == JsonValueKind.Array &&
                seArr.GetArrayLength() > 0)
            {
                var first = seArr.EnumerateArray().FirstOrDefault();
                return FirstNonEmpty(ParseCode(first, "aciklama", "adi", "uzunadi"), ParseCode(first, "kodu", "kod"));
            }
        }
        catch
        {
            // ignore
        }
        return null;
    }

    private static string? ParseCode(JsonElement raw, params string[] props)
    {
        if (raw.ValueKind == JsonValueKind.Undefined || raw.ValueKind == JsonValueKind.Null)
            return null;

        try
        {
            if (raw.ValueKind == JsonValueKind.String)
            {
                var s = (raw.GetString() ?? string.Empty).Trim();
                return string.IsNullOrWhiteSpace(s) ? null : s;
            }

            if (raw.ValueKind == JsonValueKind.Number)
                return raw.GetRawText();

            if (raw.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in props)
                {
                    if (raw.TryGetProperty(p, out var v))
                    {
                        var t = ParseFlexibleText(v);
                        if (!string.IsNullOrWhiteSpace(t)) return t;
                    }
                }
            }
        }
        catch
        {
            // ignore
        }
        return null;
    }

    public static InvoiceLineDto ToApiDto(this DiaInvoiceLine src, TransferStatus? transferStatus = null, string? targetFirmaKodu = null, string? targetSubeKodu = null, string? targetDonemKodu = null) => new()
    {
        Key = src.Key.ToString(),
        SiraNo = src.SiraNo,
        KalemTuru = ParseInt(src.KalemTuruRaw),
        StokHizmetKodu = FirstNonEmpty(src.KalemRef?.StokKartKodu, src.KalemRef?.HizmetKartKodu),
        StokHizmetAciklama = src.KalemRef?.Aciklama,
        Birim = ParseBirim(src.BirimRaw),
        Miktar = src.Miktar,
        BirimFiyati = src.BirimFiyati,
        SonBirimFiyati = src.SonBirimFiyati,
        Tutari = src.Tutari,
        Kdv = ResolveInvoiceLineKdvPercent(src),
        KdvTutari = src.KdvTutari,
        IndirimToplam = src.IndirimToplam,
        DepoAdi = src.DepoSource?.DepoAdi,
        Note = src.Note,
        Note2 = src.Note2,
        ProjeKodu = ParseProject(src.ProjeRaw).kodu,
        ProjeAciklama = ParseProject(src.ProjeRaw).aciklama,
        DinamikSubelerRaw = ExtractDinamikSubelerRaw(src),
        DinamikSubelerNormalized = NormalizeDinamikSubeler(ExtractDinamikSubelerRaw(src)),
        TransferStatus = transferStatus ?? TransferStatus.Bekliyor,
        TargetFirmaKodu = targetFirmaKodu,
        TargetSubeKodu = targetSubeKodu,
        TargetDonemKodu = targetDonemKodu,
    };

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    /// <summary>
    /// scf_fatura_getir kaleminde KDV % için güvenilir değer: önce açık yüzde alanları, sonra tutar/durum tutarlılığı.
    /// </summary>
    public static decimal? ResolveInvoiceLineKdvPercent(DiaInvoiceLine l)
    {
        var dur = (l.KdvDurumuRaw ?? string.Empty).Trim().ToUpperInvariant();
        var tut = l.Tutari ?? 0m;
        var kdvTut = l.KdvTutari ?? 0m;
        var raw = l.Kdv ?? 0m;

        // KDV tutarı 0 iken bazı tenantlarda yüzde alanları (kdvyuzde/kdvorani/kdvyuzdesi) veya `kdv`
        // kart varsayılanı gibi non-zero dönebiliyor. Hariç/işlenmedi olduğunda %0 kabul et.
        if (tut != 0m && kdvTut == 0m && (dur == "H" || dur == "I"))
            return 0m;

        decimal? cand =
            l.KdvYuzde ?? l.KdvOrani ?? l.KdvYuzdesi ?? l.Kdv;

        var n = NormalizeExtremeKdvPercent(cand, tut, kdvTut);
        if (n.HasValue) return n;
        if (cand is > 0m and <= 100m) return cand.Value;
        return 0m;
    }

    /// <summary>RAW snapshot / istemci düzeltmesi — her zaman 0..100.</summary>
    public static decimal NormalizeSnapshotLineKdvPercent(decimal raw, decimal tutar, decimal? kdvTutari)
    {
        var kt = kdvTutari ?? 0m;
        var n = NormalizeExtremeKdvPercent(raw, tutar, kt);
        if (n.HasValue) return n.Value;
        if (raw is >= 0m and <= 100m) return raw;
        return 0m;
    }

    /// <summary>
    /// Bazı tenant&apos;larda KDV % <c>20</c> yerine <c>20000000</c> veya tutar kolonu yüzde sanılarak gelir.
    /// </summary>
    private static decimal? NormalizeExtremeKdvPercent(decimal? candidate, decimal tutar, decimal kdvTutari)
    {
        if (!candidate.HasValue) return null;
        var raw = candidate.Value;
        if (raw is >= 0m and <= 100m) return raw;
        if (raw < 0m) return 0m;

        var divM = raw / 1_000_000m;
        if (divM is > 0.01m and <= 100m) return decimal.Round(divM, 4, MidpointRounding.AwayFromZero);

        var div100K = raw / 100_000m;
        if (div100K is > 0.01m and <= 100m) return decimal.Round(div100K, 4, MidpointRounding.AwayFromZero);

        if (tutar > 0m && kdvTutari > 0m)
        {
            var p = kdvTutari / tutar * 100m;
            if (p is > 0m and <= 100.01m) return decimal.Round(p, 4, MidpointRounding.AwayFromZero);
        }

        if (tutar > 0m)
        {
            var p2 = raw / tutar * 100m;
            if (p2 is > 0.01m and <= 100.01m) return decimal.Round(p2, 4, MidpointRounding.AwayFromZero);
        }

        return null;
    }

    public static string? ExtractDinamikSubelerRaw(this DiaInvoiceLine src, string? preferredColumn = null)
    {
        if (!string.IsNullOrWhiteSpace(preferredColumn))
        {
            var preferred = ParsePreferredDynamicColumn(src, preferredColumn);
            if (!string.IsNullOrWhiteSpace(preferred)) return preferred;
        }

        // Bu proje için kritik: kalem şube alanı sadece __dinamik__fatsube.
        // (Tenant farkı için hala fallback tutuyoruz ama öncelik burası.)
        var fatsube = ParsePreferredDynamicColumn(src, "__dinamik__fatsube");
        if (!string.IsNullOrWhiteSpace(fatsube)) return fatsube;

        // Öncelik (tenant farkı destekli):
        // 1) m_kalemler[i].__dinamik__1 / __dinamik__2
        // 2) fallback: m_kalemler[i].__dinamik__00001 / __dinamik__00002
        // 3) fallback: nested _key_scf_irsaliye / _key_scf_irsaliye_kalemi içindeki aynı alanlar
        var direct = ParseFlexibleText(src.Dinamik1Raw);
        if (!string.IsNullOrWhiteSpace(direct)) return direct;

        var directAlt = ParseFlexibleText(src.Dinamik2Raw);
        if (!string.IsNullOrWhiteSpace(directAlt)) return directAlt;

        var direct2 = ParseFlexibleText(src.Dinamik00001Raw);
        if (!string.IsNullOrWhiteSpace(direct2)) return direct2;

        var direct2Alt = ParseFlexibleText(src.Dinamik00002Raw);
        if (!string.IsNullOrWhiteSpace(direct2Alt)) return direct2Alt;

        try
        {
            if (src.KeyScfIrsaliyeRaw.ValueKind == JsonValueKind.Object
                && TryGetNestedDynamic(src.KeyScfIrsaliyeRaw, out var nested))
            {
                return ParseFlexibleText(nested);
            }
        }
        catch
        {
            // ignore
        }

        try
        {
            if (src.KeyScfIrsaliyeKalemiRaw.ValueKind == JsonValueKind.Object
                && TryGetNestedDynamic(src.KeyScfIrsaliyeKalemiRaw, out var nested2))
            {
                return ParseFlexibleText(nested2);
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }

    private static string? ParsePreferredDynamicColumn(DiaInvoiceLine src, string preferredColumn)
    {
        var key = preferredColumn.Trim();
        if (!key.StartsWith("__dinamik__", StringComparison.OrdinalIgnoreCase)) return null;

        if (src.ExtraFields.TryGetValue(key, out var direct))
        {
            var parsed = ParseFlexibleText(direct);
            if (!string.IsNullOrWhiteSpace(parsed)) return parsed;
        }

        if (src.KeyScfIrsaliyeRaw.ValueKind == JsonValueKind.Object
            && src.KeyScfIrsaliyeRaw.TryGetProperty(key, out var nested))
        {
            var parsedNested = ParseFlexibleText(nested);
            if (!string.IsNullOrWhiteSpace(parsedNested)) return parsedNested;
        }

        if (src.KeyScfIrsaliyeKalemiRaw.ValueKind == JsonValueKind.Object
            && src.KeyScfIrsaliyeKalemiRaw.TryGetProperty(key, out var nested2))
        {
            var parsedNested2 = ParseFlexibleText(nested2);
            if (!string.IsNullOrWhiteSpace(parsedNested2)) return parsedNested2;
        }

        return null;
    }

    private static bool TryGetNestedDynamic(JsonElement obj, out JsonElement nested)
    {
        foreach (var name in new[] { "__dinamik__fatsube", "__dinamik__1", "__dinamik__2", "__dinamik__00001", "__dinamik__00002" })
        {
            if (obj.TryGetProperty(name, out nested))
                return true;
        }
        nested = default;
        return false;
    }

    private static string? NormalizeDinamikSubeler(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim();
        // Tek seçim olmalı; yine de tenant farkları için "AD | KOD" gibi değerleri tek stringte tutuyoruz.
        return s;
    }

    private static string? ParseBirim(JsonElement raw)
    {
        if (raw.ValueKind == JsonValueKind.Undefined || raw.ValueKind == JsonValueKind.Null)
            return null;

        // Tester notu: _key_scf_kalem_birimleri[0][1] gibi bir yapı gelebiliyor.
        // En yaygın varyantları yakalıyoruz.
        try
        {
            if (raw.ValueKind == JsonValueKind.Array)
            {
                var arr = raw.EnumerateArray().ToList();
                if (arr.Count > 0 && arr[0].ValueKind == JsonValueKind.Array)
                {
                    var inner = arr[0].EnumerateArray().ToList();
                    if (inner.Count > 1 && inner[1].ValueKind == JsonValueKind.String)
                        return inner[1].GetString();
                }
                if (arr.Count > 1 && arr[1].ValueKind == JsonValueKind.String)
                    return arr[1].GetString();
            }
            if (raw.ValueKind == JsonValueKind.String)
                return raw.GetString();
        }
        catch
        {
            // ignore
        }

        return null;
    }

    private static int? ParseInt(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (int.TryParse(raw, out var n)) return n;
        return null;
    }

    private static (string? kodu, string? aciklama) ParseProject(JsonElement raw)
    {
        if (raw.ValueKind == JsonValueKind.Undefined || raw.ValueKind == JsonValueKind.Null)
            return (null, null);

        try
        {
            if (raw.ValueKind == JsonValueKind.Object)
            {
                string? kodu = null;
                string? aciklama = null;
                if (raw.TryGetProperty("kodu", out var k) && k.ValueKind == JsonValueKind.String) kodu = k.GetString();
                if (raw.TryGetProperty("aciklama", out var a) && a.ValueKind == JsonValueKind.String) aciklama = a.GetString();
                return (kodu, aciklama);
            }
        }
        catch
        {
            // ignore
        }

        return (null, null);
    }

    private static bool? ParseBool(JsonElement raw)
    {
        if (raw.ValueKind == JsonValueKind.Undefined || raw.ValueKind == JsonValueKind.Null)
            return null;

        try
        {
            if (raw.ValueKind == JsonValueKind.True) return true;
            if (raw.ValueKind == JsonValueKind.False) return false;

            if (raw.ValueKind == JsonValueKind.Number)
            {
                if (raw.TryGetInt32(out var n)) return n != 0;
            }

            if (raw.ValueKind == JsonValueKind.String)
            {
                var s = (raw.GetString() ?? string.Empty).Trim();
                if (string.Equals(s, "t", StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(s, "f", StringComparison.OrdinalIgnoreCase)) return false;
                if (string.Equals(s, "true", StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(s, "false", StringComparison.OrdinalIgnoreCase)) return false;
                if (int.TryParse(s, out var n)) return n != 0;
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }

    private static string? ParseFlexibleText(JsonElement raw)
    {
        if (raw.ValueKind == JsonValueKind.Undefined || raw.ValueKind == JsonValueKind.Null)
            return null;

        try
        {
            if (raw.ValueKind == JsonValueKind.String)
            {
                var s = raw.GetString();
                return string.IsNullOrWhiteSpace(s) ? null : s;
            }

            if (raw.ValueKind == JsonValueKind.Number)
                return raw.GetRawText();

            if (raw.ValueKind == JsonValueKind.True) return "true";
            if (raw.ValueKind == JsonValueKind.False) return "false";

            if (raw.ValueKind == JsonValueKind.Object)
            {
                // DİA bazen seçim listesini obje olarak döndürür.
                // Olası alanlar: kodu/adi/aciklama/text/label/value
                foreach (var name in new[] { "kodu", "kod", "adi", "ad", "aciklama", "text", "label", "value", "name" })
                {
                    if (!raw.TryGetProperty(name, out var p)) continue;
                    var t = ParseFlexibleText(p);
                    if (!string.IsNullOrWhiteSpace(t)) return t;
                }
                // Anlamlı bir metin alanı yoksa dinamik seçim yapılmamış kabul et.
                return null;
            }

            if (raw.ValueKind == JsonValueKind.Array)
            {
                // Tek seçim olmalı ama bazı tenantlarda array dönebiliyor.
                foreach (var item in raw.EnumerateArray())
                {
                    var t = ParseFlexibleText(item);
                    if (!string.IsNullOrWhiteSpace(t)) return t;
                }
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }
}

