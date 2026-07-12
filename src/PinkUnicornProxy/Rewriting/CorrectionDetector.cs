using System.Text.RegularExpressions;

namespace PinkUnicornProxy.Rewriting;

internal static class CorrectionDetector
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

    private static readonly Regex ExplicitMarkers = new(
        """
        ^\s*\[\[\s*forget\s*:\s*(?<rejected>.*?)\s*\]\]
        (?:\s*\[\[\s*(?:truth|replace(?:ment)?)\s*:\s*(?<replacement>.*?)\s*\]\])?\s*$
        """,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.IgnorePatternWhitespace | RegexOptions.Singleline,
        MatchTimeout);

    private static readonly Regex CorrectionPrefix = new(
        @"^\s*correction\s*:\s*(?<replacement>\S.*?)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline,
        MatchTimeout);

    private static readonly Regex SubjectCorrection = new(
        """
        ^\s*(?:no\s*[,;:]?\s*)?
        (?:(?:(?:it|this|that)(?:\s+(?:is|was)|['’]s)\s+not)|
           (?:(?:it|this|that)\s+(?:isn['’]t|wasn['’]t))|
           (?:the\s+(?:cause|issue|problem|fault|bug|reason|culprit|assumption)\s+(?:(?:is|was)\s+not|(?:isn['’]t|wasn['’]t))))
        \s+(?<rejected>[^.!?;\r\n]+?)
        \s*(?:[.!?]+|[,;]|—|\s+-\s)\s*
        (?:instead\s*[,;:]?\s*)?(?:but\s+)?
        (?:(?:(?:it|this|that)(?:\s+is|['’]s))|
           (?:the\s+(?:(?:actual|real)\s+)?(?:cause|issue|problem|fault|bug|reason|culprit)\s+(?:is|was)))
        \s+(?<replacement>.+?)\s*[.!?]*\s*$
        """,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.IgnorePatternWhitespace | RegexOptions.Singleline,
        MatchTimeout);

    private static readonly Regex ShortCorrection = new(
        """
        ^\s*no\s*[,;:]?\s+not\s+(?<rejected>[^.!?;\r\n]+?)
        \s*(?:[.!?]+|[,;]|—|\s+-\s)\s*
        (?:instead\s*[,;:]?\s*)?(?:but\s+)?
        (?:(?:(?:it|this|that)(?:\s+is|['’]s))\s+)?
        (?<replacement>.+?)\s*[.!?]*\s*$
        """,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.IgnorePatternWhitespace | RegexOptions.Singleline,
        MatchTimeout);

    private static readonly Regex SubjectRejection = new(
        """
        ^\s*(?:no\s*[,;:]?\s*)?
        (?:(?:(?:it|this|that)(?:\s+(?:is|was)|['’]s)\s+not)|
           (?:(?:it|this|that)\s+(?:isn['’]t|wasn['’]t))|
           (?:the\s+(?:cause|issue|problem|fault|bug|reason|culprit|assumption)\s+(?:(?:is|was)\s+not|(?:isn['’]t|wasn['’]t))))
        \s+(?<rejected>[^.!?;\r\n]+?)\s*[.!?]*\s*$
        """,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.IgnorePatternWhitespace | RegexOptions.Singleline,
        MatchTimeout);

    private static readonly Regex ShortRejection = new(
        @"^\s*no\s*[,;:]?\s+not\s+(?<rejected>[^.!?;\r\n]+?)\s*[.!?]*\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline,
        MatchTimeout);

    private static readonly HashSet<string> DeicticClaims = new(StringComparer.OrdinalIgnoreCase)
    {
        "it",
        "that",
        "this",
        "the idea",
        "the assumption",
        "the earlier assumption",
        "the previous assumption",
    };

    public static bool TryDetect(string text, int maximumCharacters, out CorrectionIntent? intent)
    {
        intent = null;

        if (string.IsNullOrWhiteSpace(text) || text.Length > maximumCharacters)
        {
            return false;
        }

        Match markers = ExplicitMarkers.Match(text);
        if (markers.Success)
        {
            string rejected = CleanClause(markers.Groups["rejected"].Value);
            if (rejected.Length == 0)
            {
                return false;
            }

            string affirmative = markers.Groups["replacement"].Success
                ? EnsureTerminalPunctuation(CleanClause(markers.Groups["replacement"].Value))
                : NeutralPrompt;

            intent = new CorrectionIntent(rejected, affirmative, IsDeictic(rejected), "explicit-marker");
            return true;
        }

        Match correction = CorrectionPrefix.Match(text);
        if (correction.Success)
        {
            string replacement = CleanClause(correction.Groups["replacement"].Value);
            if (replacement.Length == 0)
            {
                return false;
            }

            intent = new CorrectionIntent(null, EnsureTerminalPunctuation(replacement), true, "correction-prefix");
            return true;
        }

        if (TryMatchReplacement(SubjectCorrection, text, "subject-replacement", out intent)
            || TryMatchReplacement(ShortCorrection, text, "short-replacement", out intent))
        {
            return true;
        }

        if (TryMatchRejection(SubjectRejection, text, "subject-rejection", out intent)
            || TryMatchRejection(ShortRejection, text, "short-rejection", out intent))
        {
            return true;
        }

        return false;
    }

    private static bool TryMatchReplacement(
        Regex regex,
        string text,
        string rule,
        out CorrectionIntent? intent)
    {
        intent = null;
        Match match = regex.Match(text);
        if (!match.Success)
        {
            return false;
        }

        string rejected = CleanClause(match.Groups["rejected"].Value);
        string replacement = CleanClause(match.Groups["replacement"].Value);
        if (rejected.Length == 0 || replacement.Length == 0)
        {
            return false;
        }

        intent = new CorrectionIntent(
            rejected,
            EnsureTerminalPunctuation($"It is {replacement}"),
            IsDeictic(rejected),
            rule);
        return true;
    }

    private static bool TryMatchRejection(
        Regex regex,
        string text,
        string rule,
        out CorrectionIntent? intent)
    {
        intent = null;
        Match match = regex.Match(text);
        if (!match.Success)
        {
            return false;
        }

        string rejected = CleanClause(match.Groups["rejected"].Value);
        if (rejected.Length == 0)
        {
            return false;
        }

        intent = new CorrectionIntent(rejected, NeutralPrompt, IsDeictic(rejected), rule);
        return true;
    }

    private static bool IsDeictic(string value) => DeicticClaims.Contains(value);

    private static string CleanClause(string value)
    {
        string result = value.Trim();
        result = result.Trim('"', '\'', '“', '”', '‘', '’');
        return result.Trim().TrimEnd('.', '!', '?', ';', ',').Trim();
    }

    private static string EnsureTerminalPunctuation(string value)
    {
        string trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            return NeutralPrompt;
        }

        char final = trimmed[^1];
        return final is '.' or '!' or '?' ? trimmed : $"{trimmed}.";
    }

    private const string NeutralPrompt = "Reassess the issue using the remaining evidence.";
}
