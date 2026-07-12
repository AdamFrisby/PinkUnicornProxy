using System.Text.RegularExpressions;

namespace PinkUnicornProxy.Rewriting;

internal static class TextScrubber
{
    private static readonly Regex SentenceBoundary = new(
        @"(?<=[.!?])\s+|\r?\n+",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex InlineCode = new(
        @"`[^`]*`",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex Url = new(
        @"(?:\b[a-z][a-z0-9+.-]*://|\bmailto:|\bwww\.)\S+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex MarkdownLink = new(
        @"\[[^\]]+\]\([^)]+\)",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex Whitespace = new(
        @"\s+",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex CodeFence = new(
        @"^(?:[ \t]*(?:```|~~~)|(?: {4,}| {0,3}\t+)\S)",
        RegexOptions.CultureInvariant | RegexOptions.Multiline,
        TimeSpan.FromMilliseconds(100));

    public static ScrubResult RemoveClaim(string text, string rejectedClaim)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(rejectedClaim))
        {
            return new ScrubResult(text, false, false);
        }

        string normalizedClaim = Normalize(rejectedClaim);
        if (normalizedClaim.Length == 0)
        {
            return new ScrubResult(text, false, false);
        }

        if (ContainsCodeFence(text))
        {
            bool protectedMatch = ContainsClaim(Normalize(text), normalizedClaim);
            return new ScrubResult(text, false, protectedMatch);
        }

        string[] segments = SentenceBoundary.Split(text);
        bool[] removed = new bool[segments.Length];
        bool protectedMatchFound = false;

        for (int index = 0; index < segments.Length; index++)
        {
            string segment = segments[index];
            if (string.IsNullOrWhiteSpace(segment))
            {
                continue;
            }

            string searchable = RemoveProtectedSpans(segment);
            bool plainMatch = ContainsClaim(Normalize(searchable), normalizedClaim);
            bool anyMatch = ContainsClaim(Normalize(segment), normalizedClaim);
            bool hasProtectedSpan = ContainsProtectedSpan(segment);

            if (plainMatch)
            {
                if (hasProtectedSpan)
                {
                    protectedMatchFound = true;
                    continue;
                }

                removed[index] = true;
                continue;
            }

            protectedMatchFound |= anyMatch;
        }

        RemoveCrossSegmentClaims(segments, removed, normalizedClaim, ref protectedMatchFound);

        List<string> retained = new(segments.Length);
        for (int index = 0; index < segments.Length; index++)
        {
            if (!removed[index] && !string.IsNullOrWhiteSpace(segments[index]))
            {
                retained.Add(segments[index].Trim());
            }
        }

        bool changed = removed.Any(static value => value);
        string result = string.Join(' ', retained).Trim();
        return new ScrubResult(result, changed, protectedMatchFound);
    }

    private static void RemoveCrossSegmentClaims(
        string[] segments,
        bool[] removed,
        string normalizedClaim,
        ref bool protectedMatchFound)
    {
        const int maximumWindow = 2;

        for (int start = 0; start < segments.Length - 1; start++)
        {
            if (removed[start] || string.IsNullOrWhiteSpace(segments[start]))
            {
                continue;
            }

            List<string> plainWindow = [RemoveProtectedSpans(segments[start])];
            List<string> fullWindow = [segments[start]];
            bool windowHasProtectedSpan = ContainsProtectedSpan(fullWindow[0]);

            int limit = Math.Min(segments.Length, start + maximumWindow);
            for (int end = start + 1; end < limit; end++)
            {
                if (removed[end])
                {
                    break;
                }

                string plain = RemoveProtectedSpans(segments[end]);
                plainWindow.Add(plain);
                fullWindow.Add(segments[end]);
                windowHasProtectedSpan |= ContainsProtectedSpan(segments[end]);

                bool plainMatch = ContainsClaim(
                    Normalize(string.Join(' ', plainWindow)),
                    normalizedClaim);
                bool anyMatch = ContainsClaim(
                    Normalize(string.Join(' ', fullWindow)),
                    normalizedClaim);

                if (!plainMatch)
                {
                    protectedMatchFound |= anyMatch;
                    continue;
                }

                if (windowHasProtectedSpan)
                {
                    protectedMatchFound = true;
                    break;
                }

                for (int remove = start; remove <= end; remove++)
                {
                    removed[remove] = true;
                }

                break;
            }
        }
    }

    private static string RemoveProtectedSpans(string value) =>
        MarkdownLink.Replace(
            Url.Replace(InlineCode.Replace(value, string.Empty), string.Empty),
            string.Empty);

    private static string Normalize(string value) => Whitespace.Replace(value, " ").Trim();

    internal static bool ContainsCodeFence(string value) => CodeFence.IsMatch(value);

    internal static bool ContainsProtectedSpan(string value) =>
        ContainsCodeFence(value)
        || value.Contains('`')
        || !string.Equals(RemoveProtectedSpans(value), value, StringComparison.Ordinal);

    private static bool ContainsClaim(string value, string claim)
    {
        int searchFrom = 0;
        while (searchFrom <= value.Length - claim.Length)
        {
            int index = value.IndexOf(claim, searchFrom, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                return false;
            }

            bool startsAtBoundary = index == 0
                || !IsWordCharacter(claim[0])
                || !IsWordCharacter(value[index - 1]);
            int end = index + claim.Length;
            bool endsAtBoundary = end == value.Length
                || !IsWordCharacter(claim[^1])
                || !IsWordCharacter(value[end]);

            if (startsAtBoundary && endsAtBoundary)
            {
                return true;
            }

            searchFrom = index + 1;
        }

        return false;
    }

    private static bool IsWordCharacter(char value) => char.IsLetterOrDigit(value) || value == '_';
}

internal sealed record ScrubResult(string Text, bool Changed, bool ProtectedMatchFound);
