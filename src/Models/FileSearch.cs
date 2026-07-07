using System;
using System.Collections.Generic;

namespace SourceGit.Models
{
    public sealed class FileSearchPattern
    {
        public FileSearchPattern(string[] tokens)
        {
            Tokens = tokens;
        }

        public string[] Tokens { get; }

        public bool IsEmpty => Tokens == null || Tokens.Length == 0;
    }

    public static class FileSearch
    {
        public static FileSearchPattern Parse(string filter)
        {
            if (string.IsNullOrWhiteSpace(filter))
                return new FileSearchPattern([]);

            var normalized = filter.Trim().Replace('\\', '/');
            var tokens = normalized.Split(WhiteSpaces, StringSplitOptions.RemoveEmptyEntries);
            return new FileSearchPattern(tokens);
        }

        public static bool Matches(string path, string filter)
        {
            return Matches(path, Parse(filter));
        }

        public static List<string> FilterAndSort(List<string> paths, FileSearchPattern pattern, int maxMatches = 0, bool excludeExactMatch = false)
        {
            var matches = maxMatches > 0 ? new List<ScoredPath>(Math.Min(paths.Count, maxMatches)) : new List<ScoredPath>();
            var worstIndex = -1;
            for (int i = 0; i < paths.Count; i++)
            {
                var path = paths[i];
                var score = GetScore(path, pattern);
                if (score < 0)
                    continue;

                if (excludeExactMatch && IsExactMatch(path, pattern))
                    continue;

                var match = new ScoredPath(path, score, i);
                if (maxMatches <= 0)
                {
                    matches.Add(match);
                }
                else if (matches.Count < maxMatches)
                {
                    matches.Add(match);
                    if (worstIndex < 0 || CompareScored(matches[worstIndex], match) < 0)
                        worstIndex = matches.Count - 1;
                }
                else if (CompareScored(match, matches[worstIndex]) < 0)
                {
                    matches[worstIndex] = match;
                    worstIndex = FindWorst(matches);
                }
            }

            matches.Sort(CompareScored);

            var result = new List<string>(matches.Count);
            foreach (var match in matches)
                result.Add(match.Path);

            return result;
        }

        public static bool Matches(string path, FileSearchPattern pattern)
        {
            return GetScore(path, pattern) >= 0;
        }

        public static bool IsExactMatch(string path, FileSearchPattern pattern)
        {
            return !pattern.IsEmpty &&
                !string.IsNullOrEmpty(path) &&
                pattern.Tokens.Length == 1 &&
                path.Length == pattern.Tokens[0].Length &&
                IsMatchAt(path, pattern.Tokens[0], 0);
        }

        public static int Compare(string left, string right, FileSearchPattern pattern)
        {
            var score = GetScore(left, pattern) - GetScore(right, pattern);
            return score != 0 ? score : NumericSort.Compare(left, right);
        }

        public static int GetScore(string path, FileSearchPattern pattern)
        {
            if (pattern.IsEmpty)
                return 0;

            if (string.IsNullOrEmpty(path))
                return -1;

            var firstMatch = -1;
            var start = 0;
            foreach (var token in pattern.Tokens)
            {
                var idx = IndexOf(path, token, start);
                if (idx < 0)
                    return -1;

                if (firstMatch < 0)
                    firstMatch = idx;

                start = idx + token.Length;
            }

            var nameOffset = 0;
            var nameRank = TryGetLastTextToken(pattern, out var lastTextToken) ?
                GetFileNameRank(path, lastTextToken, out nameOffset) :
                GetRank(path, pattern);

            return nameRank * 1000000 + Math.Min(nameOffset, 999) * 1000 + Math.Min(firstMatch, 999);
        }

        private static int GetRank(string path, FileSearchPattern pattern)
        {
            if (pattern.IsEmpty || string.IsNullOrEmpty(path))
                return 4;

            if (pattern.Tokens.Length == 1)
            {
                var token = pattern.Tokens[0];
                var nameStart = LastIndexOfSeparator(path) + 1;
                var nameLength = path.Length - nameStart;

                if (nameLength == token.Length && IsMatchAt(path, token, nameStart))
                    return 0;

                if (IsMatchAt(path, token, nameStart))
                    return 1;

                if (IndexOf(path, token, nameStart) >= 0)
                    return 2;

                if (HasSegmentStartingWith(path, token))
                    return 3;

                return 4;
            }

            var name = path[(LastIndexOfSeparator(path) + 1)..];
            if (Matches(name, pattern))
                return 2;

            return HasSegmentStartingWith(path, pattern.Tokens[0]) ? 3 : 4;
        }

        private static int GetFileNameRank(string path, string token, out int offset)
        {
            offset = 999;
            if (string.IsNullOrEmpty(token))
                return 8;

            var nameStart = LastIndexOfSeparator(path) + 1;
            var nameLength = path.Length - nameStart;
            var stemLength = GetStemLength(path, nameStart);

            if (stemLength == token.Length && IsMatchAt(path, token, nameStart))
            {
                offset = 0;
                return 0;
            }

            if (nameLength == token.Length && IsMatchAt(path, token, nameStart))
            {
                offset = 0;
                return 1;
            }

            if (IsMatchAt(path, token, nameStart))
            {
                offset = 0;
                return 2;
            }

            var stemEnd = nameStart + stemLength;
            var idx = IndexOf(path, token, nameStart);
            if (idx >= 0 && idx + token.Length <= stemEnd)
            {
                offset = idx - nameStart;
                return 4;
            }

            if (idx >= 0)
            {
                offset = idx - nameStart;
                return 5;
            }

            if (HasSegmentStartingWith(path, token))
            {
                offset = 0;
                return 6;
            }

            idx = IndexOf(path, token, 0);
            if (idx >= 0)
            {
                offset = idx;
                return 7;
            }

            return 8;
        }

        private static int GetStemLength(string path, int nameStart)
        {
            for (int i = path.Length - 1; i >= nameStart; i--)
            {
                if (path[i] == '.')
                    return i - nameStart;
            }

            return path.Length - nameStart;
        }

        private static bool TryGetLastTextToken(FileSearchPattern pattern, out string token)
        {
            for (int i = pattern.Tokens.Length - 1; i >= 0; i--)
            {
                token = pattern.Tokens[i];
                if (!IsSeparatorToken(token))
                    return true;
            }

            token = string.Empty;
            return false;
        }

        private static bool IsSeparatorToken(string token)
        {
            if (string.IsNullOrEmpty(token))
                return true;

            foreach (var c in token)
            {
                if (!IsSeparator(c))
                    return false;
            }

            return true;
        }

        private static int CompareScored(ScoredPath left, ScoredPath right)
        {
            var score = left.Score.CompareTo(right.Score);
            return score != 0 ? score : left.Order.CompareTo(right.Order);
        }

        private static int FindWorst(List<ScoredPath> matches)
        {
            var worst = 0;
            for (int i = 1; i < matches.Count; i++)
            {
                if (CompareScored(matches[worst], matches[i]) < 0)
                    worst = i;
            }

            return worst;
        }

        private static int IndexOf(string path, string token, int start)
        {
            if (string.IsNullOrEmpty(token))
                return start;

            if (token.IndexOf('/') < 0 || path.IndexOf('\\', start) < 0)
                return path.IndexOf(token, start, StringComparison.OrdinalIgnoreCase);

            var max = path.Length - token.Length;
            for (int i = Math.Max(0, start); i <= max; i++)
            {
                if (IsMatchAt(path, token, i))
                    return i;
            }

            return -1;
        }

        private static bool IsMatchAt(string path, string token, int start)
        {
            if (start < 0 || start + token.Length > path.Length)
                return false;

            if (token.IndexOf('/') < 0)
                return path.AsSpan(start).StartsWith(token, StringComparison.OrdinalIgnoreCase);

            for (int i = 0; i < token.Length; i++)
            {
                if (!EqualsNormalized(path[start + i], token[i]))
                    return false;
            }

            return true;
        }

        private static bool HasSegmentStartingWith(string path, string token)
        {
            if (string.IsNullOrEmpty(token))
                return false;

            var start = 0;
            while (start < path.Length)
            {
                if (IsMatchAt(path, token, start))
                    return true;

                var next = FindNextSeparator(path, start);
                if (next < 0)
                    break;

                start = next + 1;
            }

            return false;
        }

        private static int LastIndexOfSeparator(string path)
        {
            for (int i = path.Length - 1; i >= 0; i--)
            {
                if (IsSeparator(path[i]))
                    return i;
            }

            return -1;
        }

        private static int FindNextSeparator(string path, int start)
        {
            for (int i = start; i < path.Length; i++)
            {
                if (IsSeparator(path[i]))
                    return i;
            }

            return -1;
        }

        private static bool EqualsNormalized(char left, char right)
        {
            if (IsSeparator(left) && IsSeparator(right))
                return true;

            if (left == right)
                return true;

            if (IsAsciiLetter(left) && IsAsciiLetter(right))
                return (left | 0x20) == (right | 0x20);

            return char.ToUpperInvariant(left) == char.ToUpperInvariant(right);
        }

        private static bool IsSeparator(char c)
        {
            return c is '/' or '\\';
        }

        private static bool IsAsciiLetter(char c)
        {
            return (uint)((c | 0x20) - 'a') <= 'z' - 'a';
        }

        private static readonly char[] WhiteSpaces = [' ', '\t', '\r', '\n'];

        private readonly struct ScoredPath
        {
            public ScoredPath(string path, int score, int order)
            {
                Path = path;
                Score = score;
                Order = order;
            }

            public string Path { get; }
            public int Score { get; }
            public int Order { get; }
        }
    }
}
