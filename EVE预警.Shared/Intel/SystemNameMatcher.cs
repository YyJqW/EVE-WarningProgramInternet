namespace EVE预警.Intel
{
    /// <summary>
    /// 从自由文本（聊天频道消息）中识别星系名。
    /// 采用「首字符分桶 + 桶内最长优先」的扫描方式，同时支持英文词边界与中文子串。
    /// </summary>
    public static class SystemNameMatcher
    {
        private static readonly object IndexLock = new();
        private static Dictionary<char, List<(string Name, int SystemId)>>? _buckets;

        public static IReadOnlyList<int> FindSystems(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return Array.Empty<int>();
            }

            EnsureIndex();
            Dictionary<char, List<(string Name, int SystemId)>>? buckets = _buckets;
            if (buckets == null)
            {
                return Array.Empty<int>();
            }

            var result = new List<int>();
            var seen = new HashSet<int>();
            int index = 0;

            while (index < text.Length)
            {
                char key = BucketKey(text[index]);
                if (buckets.TryGetValue(key, out var candidates))
                {
                    bool matched = false;
                    foreach (var (name, systemId) in candidates)
                    {
                        int end = index + name.Length;
                        if (end > text.Length)
                        {
                            continue;
                        }

                        if (string.Compare(
                                text, index, name, 0, name.Length,
                                StringComparison.OrdinalIgnoreCase) != 0)
                        {
                            continue;
                        }

                        if (IsAsciiName(name))
                        {
                            if (index > 0 && IsAsciiAlphanumeric(text[index - 1]))
                            {
                                continue;
                            }

                            if (end < text.Length && IsAsciiAlphanumeric(text[end]))
                            {
                                continue;
                            }
                        }

                        if (seen.Add(systemId))
                        {
                            result.Add(systemId);
                        }

                        index = end;
                        matched = true;
                        break;
                    }

                    if (matched)
                    {
                        continue;
                    }
                }

                index++;
            }

            return result;
        }

        private static void EnsureIndex()
        {
            if (_buckets != null)
            {
                return;
            }

            lock (IndexLock)
            {
                if (_buckets != null)
                {
                    return;
                }

                var buckets = new Dictionary<char, List<(string, int)>>();
                foreach (var (systemId, name) in MapData.EnumerateIndexedNames())
                {
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    char key = BucketKey(name[0]);
                    if (!buckets.TryGetValue(key, out var list))
                    {
                        buckets[key] = list = new List<(string, int)>();
                    }

                    list.Add((name, systemId));
                }

                foreach (var list in buckets.Values)
                {
                    // 最长优先，避免 "Old Man Star" 被 "Old" 之类的短名截断
                    list.Sort((a, b) => b.Item1.Length.CompareTo(a.Item1.Length));
                }

                _buckets = buckets;
            }
        }

        private static char BucketKey(char c) =>
            c < 128 ? char.ToUpperInvariant(c) : c;

        private static bool IsAsciiName(string name)
        {
            foreach (char c in name)
            {
                if (c >= 128)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsAsciiAlphanumeric(char c) =>
            c < 128 && char.IsLetterOrDigit(c);
    }
}
