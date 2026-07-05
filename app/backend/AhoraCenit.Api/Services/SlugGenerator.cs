using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AhoraCenit.Api.Services;

/// <summary>
/// Utility to turn arbitrary strings into URL/DNS-friendly slugs, and to
/// resolve collisions by appending a numeric suffix.
/// </summary>
public static partial class SlugGenerator
{
    /// <summary>
    /// Normalizes a string into a slug: lowercase, strips diacritics,
    /// replaces any non-alphanumeric character with '-', collapses repeated
    /// '-' and trims them from both ends.
    /// </summary>
    public static string Slugify(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        var normalized = input.Normalize(NormalizationForm.FormD);
        var withoutDiacritics = new StringBuilder(normalized.Length);

        foreach (var c in normalized)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category != UnicodeCategory.NonSpacingMark)
            {
                withoutDiacritics.Append(c);
            }
        }

        var lower = withoutDiacritics.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant();

        var replaced = NonAlphanumericRegex().Replace(lower, "-");
        var collapsed = RepeatedDashesRegex().Replace(replaced, "-");

        return collapsed.Trim('-');
    }

    /// <summary>
    /// Combines two names into a base slug, e.g. product + client name.
    /// </summary>
    public static string Slugify(string first, string second)
    {
        var a = Slugify(first);
        var b = Slugify(second);
        return string.IsNullOrEmpty(a) ? b : string.IsNullOrEmpty(b) ? a : $"{a}-{b}";
    }

    /// <summary>
    /// Given a desired base slug, finds a unique variant by appending
    /// "-2", "-3", ... as needed, using <paramref name="existsAsync"/> to
    /// check for collisions.
    /// </summary>
    public static async Task<string> ResolveUniqueAsync(
        string baseSlug,
        Func<string, Task<bool>> existsAsync)
    {
        var candidate = baseSlug;
        var suffix = 2;

        while (await existsAsync(candidate))
        {
            candidate = $"{baseSlug}-{suffix}";
            suffix++;
        }

        return candidate;
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonAlphanumericRegex();

    [GeneratedRegex("-{2,}")]
    private static partial Regex RepeatedDashesRegex();
}
