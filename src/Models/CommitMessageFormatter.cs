namespace SourceGit.Models
{
    public static class CommitMessageFormatter
    {
        public static string NormalizeLineEndingsForGit(string message)
        {
            return string.IsNullOrEmpty(message) ? message : message.ReplaceLineEndings("\n");
        }

        public static string NormalizeForGit(string message)
        {
            if (string.IsNullOrEmpty(message))
                return message;

            var normalized = message.ReplaceLineEndings("\n");
            var lineStart = 0;

            while (lineStart < normalized.Length)
            {
                var lineEnd = normalized.IndexOf('\n', lineStart);
                if (lineEnd < 0)
                    return normalized;

                if (IsLineWhiteSpace(normalized, lineStart, lineEnd))
                {
                    lineStart = lineEnd + 1;
                    continue;
                }

                var nextLineStart = lineEnd + 1;
                if (nextLineStart >= normalized.Length)
                    return normalized;

                var nextLineEnd = normalized.IndexOf('\n', nextLineStart);
                if (nextLineEnd < 0)
                    nextLineEnd = normalized.Length;

                return IsLineWhiteSpace(normalized, nextLineStart, nextLineEnd)
                    ? normalized
                    : normalized.Insert(nextLineStart, "\n");
            }

            return normalized;
        }

        private static bool IsLineWhiteSpace(string text, int start, int end)
        {
            for (var i = start; i < end; i++)
            {
                if (!char.IsWhiteSpace(text[i]))
                    return false;
            }

            return true;
        }
    }
}
