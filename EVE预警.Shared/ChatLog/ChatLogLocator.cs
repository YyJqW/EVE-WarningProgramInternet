namespace EVE预警.ChatLog
{
    /// <summary>定位 EVE 聊天日志目录；支持配置覆盖与常见的 Documents / OneDrive 位置。</summary>
    public static class ChatLogLocator
    {
        public static IReadOnlyList<string> GetCandidates(string? configured)
        {
            var candidates = new List<string>();

            if (!string.IsNullOrWhiteSpace(configured))
            {
                candidates.Add(configured.Trim());
            }

            string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (!string.IsNullOrWhiteSpace(documents))
            {
                candidates.Add(Path.Combine(documents, "EVE", "logs", "Chatlogs"));
            }

            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(profile))
            {
                candidates.Add(Path.Combine(profile, "OneDrive", "Documents", "EVE", "logs", "Chatlogs"));
            }

            return candidates
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static string? FindExisting(string? configured) =>
            GetCandidates(configured).FirstOrDefault(Directory.Exists);
    }
}
