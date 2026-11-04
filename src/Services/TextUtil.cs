using System.Globalization;
using System.Text;

namespace CinemaAgent.Services;

public static class TextUtil
{
    /// <summary>Małe litery bez polskich znaków diakrytycznych - do tolerancyjnego dopasowania.</summary>
    public static string Norm(string s)
    {
        var d = s.Replace('ł', 'l').Replace('Ł', 'L').Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var c in d)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    public static bool ContainsAllTokens(string haystack, string query)
    {
        var h = Norm(haystack);
        return Norm(query).Split(' ', StringSplitOptions.RemoveEmptyEntries).All(h.Contains);
    }

    public static readonly string[] PlDays =
        ["niedziela", "poniedziałek", "wtorek", "środa", "czwartek", "piątek", "sobota"];
}
