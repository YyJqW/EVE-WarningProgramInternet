using System.Text;

namespace EVE预警.ChatLog
{
    public sealed record ChatLogFileInfo(string Path, ChatLogHeader? Header, string? Error);

    /// <summary>扫描聊天日志目录，读取每个文件的频道头（设置窗口与监听器共用）。</summary>
    public static class ChatLogDirectoryScanner
    {
        private const int HeaderReadSize = 16 * 1024;

        public static IReadOnlyList<ChatLogFileInfo> Scan(string? directory)
        {
            var result = new List<ChatLogFileInfo>();
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                return result;
            }

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(directory, "*.txt").ToList();
            }
            catch (Exception ex)
            {
                return new[] { new ChatLogFileInfo(directory, null, ex.Message) };
            }

            foreach (string file in files)
            {
                result.Add(ReadOne(file));
            }

            return result
                .OrderByDescending(f => f.Header != null)
                .ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static ChatLogFileInfo ReadOne(string path)
        {
            try
            {
                using var stream = new FileStream(
                    path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                int length = (int)Math.Min(stream.Length, HeaderReadSize);
                if (length <= 0)
                {
                    return new ChatLogFileInfo(path, null, "空文件");
                }

                var buffer = new byte[length];
                int read = 0;
                while (read < buffer.Length)
                {
                    int n = stream.Read(buffer, read, buffer.Length - read);
                    if (n <= 0)
                    {
                        break;
                    }

                    read += n;
                }

                Encoding encoding = ChatLogParser.DetectEncoding(buffer.AsSpan(0, read));
                string text = encoding.GetString(buffer, 0, read);
                ChatLogHeader? header = ChatLogParser.ParseHeader(text.Split('\n'));
                return new ChatLogFileInfo(path, header, header == null ? "未识别到频道头" : null);
            }
            catch (Exception ex)
            {
                return new ChatLogFileInfo(path, null, ex.Message);
            }
        }
    }
}
