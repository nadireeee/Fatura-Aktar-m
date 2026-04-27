namespace DiaErpIntegration.API.Services;

public static class MasterCodeNormalizer
{
    /// <summary>
    /// Tenantlar arası kod formatı farklılıklarını azaltmak için kanonikleştirir.
    /// - trim + upper
    /// - Türkçe karakterleri normalize eder
    /// - boşluk ve noktalama (.,-/, vb.) kaldırır
    /// </summary>
    public static string? Normalize(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var s = code.Trim().ToUpperInvariant()
            .Replace("İ", "I")
            .Replace("İ", "I")
            .Replace("Ş", "S")
            .Replace("Ğ", "G")
            .Replace("Ü", "U")
            .Replace("Ö", "O")
            .Replace("Ç", "C");

        // only keep letters/digits
        Span<char> buf = s.Length <= 256 ? stackalloc char[s.Length] : new char[s.Length];
        var j = 0;
        foreach (var ch in s)
        {
            if (char.IsLetterOrDigit(ch))
                buf[j++] = ch;
        }
        return j == 0 ? null : new string(buf[..j]);
    }
}
