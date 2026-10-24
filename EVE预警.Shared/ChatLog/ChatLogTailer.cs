using System.Text;

namespace EVE预警.ChatLog
{
    /// <summary>
    /// 单个聊天日志文件的增量读取器：按字节偏移只读新增内容，
    /// 避免像整文件重读那样随日志增长而变慢。
    /// </summary>
    public sealed class ChatLogTailer
    {
        private const int MaxBytesPerRead = 4 * 1024 * 1024;
        private const int MaxPendingBytes = 64 * 1024;
        private const int HeaderReadSize = 16 * 1024;

        private readonly object _gate = new();
        private readonly string _path;
        private Encoding _encoding = Encoding.Unicode;
        private long _offset;
        private byte[] _pending = Array.Empty<byte>();

        public ChatLogTailer(string path)
        {
            _path = path;
            ReloadHeader();
        }

        public string Path => _path;

        public ChatLogHeader? Header { get; private set; }

        public string? LastError { get; private set; }

        /// <summary>启动时调用：跳过文件已有内容，只处理后续新增消息。</summary>
        public void SkipToEnd()
        {
            lock (_gate)
            {
                try
                {
                    using FileStream stream = OpenRead();
                    _offset = stream.Length;
                    _pending = Array.Empty<byte>();
                    LastError = null;
                }
                catch (Exception ex)
                {
                    LastError = $"打开日志失败: {ex.Message}";
                }
            }
        }

        public void ReloadHeader()
        {
            try
            {
                using FileStream stream = OpenRead();
                int length = (int)Math.Min(stream.Length, HeaderReadSize);
                if (length <= 0)
                {
                    LastError = null;
                    return;
                }

                var buffer = new byte[length];
                int read = ReadFully(stream, buffer);
                _encoding = ChatLogParser.DetectEncoding(buffer.AsSpan(0, read));
                string text = _encoding.GetString(buffer, 0, read);
                Header = ChatLogParser.ParseHeader(text.Split('\n'));
                LastError = null;
            }
            catch (Exception ex)
            {
                LastError = $"读取日志头失败: {ex.Message}";
            }
        }

        public IReadOnlyList<ChatLogMessage> ReadNewLines()
        {
            lock (_gate)
            {
                var messages = new List<ChatLogMessage>();
                try
                {
                    using FileStream stream = OpenRead();
                    long length = stream.Length;

                    if (length < _offset)
                    {
                        // 文件被轮转或截断
                        _offset = 0;
                        _pending = Array.Empty<byte>();
                        Header = null;
                        ReloadHeader();
                    }

                    if (length <= _offset)
                    {
                        return messages;
                    }

                    long remaining = Math.Min(length - _offset, MaxBytesPerRead);
                    stream.Seek(_offset, SeekOrigin.Begin);
                    var buffer = new byte[remaining];
                    int read = ReadFully(stream, buffer);
                    if (read <= 0)
                    {
                        return messages;
                    }

                    _offset += read;
                    if (read < buffer.Length)
                    {
                        Array.Resize(ref buffer, read);
                    }

                    byte[] combined;
                    if (_pending.Length == 0)
                    {
                        combined = buffer;
                    }
                    else
                    {
                        combined = new byte[_pending.Length + buffer.Length];
                        Buffer.BlockCopy(_pending, 0, combined, 0, _pending.Length);
                        Buffer.BlockCopy(buffer, 0, combined, _pending.Length, buffer.Length);
                    }

                    int cut = FindLastLineEnd(combined);
                    if (cut <= 0)
                    {
                        _pending = combined.Length > MaxPendingBytes ? Array.Empty<byte>() : combined;
                        return messages;
                    }

                    string text = _encoding.GetString(combined, 0, cut);
                    _pending = cut >= combined.Length
                        ? Array.Empty<byte>()
                        : combined[cut..];
                    if (_pending.Length > MaxPendingBytes)
                    {
                        _pending = Array.Empty<byte>();
                    }

                    if (Header == null)
                    {
                        ReloadHeader();
                    }

                    string channel = Header?.ChannelName ?? "";
                    foreach (string rawLine in text.Split('\n'))
                    {
                        if (!ChatLogParser.TryParseLine(rawLine, out ChatLogMessage parsed))
                        {
                            continue;
                        }

                        messages.Add(parsed with
                        {
                            ChannelName = channel,
                            FilePath = _path,
                        });
                    }

                    LastError = null;
                }
                catch (Exception ex)
                {
                    LastError = $"读取日志失败: {ex.Message}";
                }

                return messages;
            }
        }

        private FileStream OpenRead() =>
            new(
                _path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096,
                FileOptions.SequentialScan);

        private static int ReadFully(Stream stream, byte[] buffer)
        {
            int total = 0;
            while (total < buffer.Length)
            {
                int read = stream.Read(buffer, total, buffer.Length - total);
                if (read <= 0)
                {
                    break;
                }

                total += read;
            }

            return total;
        }

        private int FindLastLineEnd(byte[] buffer)
        {
            if (_encoding.CodePage == Encoding.Unicode.CodePage)
            {
                for (int i = buffer.Length - 2; i >= 0; i -= 2)
                {
                    if (buffer[i] == 0x0A && buffer[i + 1] == 0x00)
                    {
                        return i + 2;
                    }
                }

                return -1;
            }

            if (_encoding.CodePage == Encoding.BigEndianUnicode.CodePage)
            {
                for (int i = buffer.Length - 2; i >= 0; i -= 2)
                {
                    if (buffer[i] == 0x00 && buffer[i + 1] == 0x0A)
                    {
                        return i + 2;
                    }
                }

                return -1;
            }

            for (int i = buffer.Length - 1; i >= 0; i--)
            {
                if (buffer[i] == 0x0A)
                {
                    return i + 1;
                }
            }

            return -1;
        }
    }
}
