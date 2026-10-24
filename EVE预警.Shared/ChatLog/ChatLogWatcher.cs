namespace EVE预警.ChatLog
{
    /// <summary>
    /// 监听聊天日志目录：FileSystemWatcher 负责低延迟，3 秒轮询负责兜底
    /// （文件监视在部分环境下会丢失事件，轮询保证不漏消息）。
    /// </summary>
    public sealed class ChatLogWatcher : IDisposable
    {
        private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(300);

        private readonly object _gate = new();
        private readonly string _directory;
        private readonly Dictionary<string, ChatLogTailer> _tailers =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime> _dirty =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, long> _lastLength =
            new(StringComparer.OrdinalIgnoreCase);

        private FileSystemWatcher? _watcher;
        private System.Threading.Timer? _processTimer;
        private System.Threading.Timer? _pollTimer;
        private bool _started;
        private bool _disposed;

        public ChatLogWatcher(string directory)
        {
            _directory = directory;
        }

        public event Action<ChatLogMessage>? MessageReceived;

        public event Action<string>? ErrorOccurred;

        public string Directory => _directory;

        public string? LastError { get; private set; }

        public int FileCount
        {
            get
            {
                lock (_gate)
                {
                    return _tailers.Count;
                }
            }
        }

        /// <summary>启动监听。skipExisting=true 时跳过已有日志内容，避免回放历史。</summary>
        public void Start(bool skipExisting)
        {
            lock (_gate)
            {
                if (_started || _disposed)
                {
                    return;
                }

                _started = true;
            }

            if (!System.IO.Directory.Exists(_directory))
            {
                SetError($"日志目录不存在: {_directory}");
                return;
            }

            try
            {
                foreach (string file in System.IO.Directory.EnumerateFiles(_directory, "*.txt"))
                {
                    var tailer = new ChatLogTailer(file);
                    if (skipExisting)
                    {
                        tailer.SkipToEnd();
                    }

                    lock (_gate)
                    {
                        _tailers[file] = tailer;
                        _lastLength[file] = SafeLength(file);
                    }
                }

                _watcher = new FileSystemWatcher(_directory, "*.txt")
                {
                    NotifyFilter = NotifyFilters.LastWrite |
                                   NotifyFilters.Size |
                                   NotifyFilters.FileName |
                                   NotifyFilters.CreationTime,
                    IncludeSubdirectories = false,
                };
                _watcher.Created += (_, e) => MarkDirty(e.FullPath);
                _watcher.Changed += (_, e) => MarkDirty(e.FullPath);
                _watcher.Renamed += (_, e) =>
                {
                    RemoveFile(e.OldFullPath);
                    MarkDirty(e.FullPath);
                };
                _watcher.Deleted += (_, e) => RemoveFile(e.FullPath);
                _watcher.Error += (_, e) =>
                {
                    SetError($"文件监视异常（已启用轮询兜底）: {e.GetException().Message}");
                    MarkAllDirty();
                };
                _watcher.EnableRaisingEvents = true;

                _processTimer = new System.Threading.Timer(_ => SafeProcessDirty(), null, 300, 300);
                _pollTimer = new System.Threading.Timer(_ => SafePoll(), null, 3000, 3000);
                LastError = null;
            }
            catch (Exception ex)
            {
                SetError($"启动日志监听失败: {ex.Message}");
            }
        }

        public void Stop()
        {
            lock (_gate)
            {
                _started = false;
            }

            if (_watcher != null)
            {
                try
                {
                    _watcher.EnableRaisingEvents = false;
                    _watcher.Dispose();
                }
                catch
                {
                    // 忽略释放异常
                }

                _watcher = null;
            }

            _processTimer?.Dispose();
            _processTimer = null;
            _pollTimer?.Dispose();
            _pollTimer = null;

            lock (_gate)
            {
                _dirty.Clear();
            }
        }

        /// <summary>当前已识别到的频道名（设置窗口用于展示可选频道）。</summary>
        public IReadOnlyList<string> GetDiscoveredChannels()
        {
            lock (_gate)
            {
                return _tailers.Values
                    .Select(t => t.Header?.ChannelName)
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .Select(n => n!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }

        /// <summary>手动把全部文件标记为待读取（设置变更后立即处理积压内容）。</summary>
        public void MarkAllDirty()
        {
            lock (_gate)
            {
                DateTime now = DateTime.UtcNow;
                foreach (string path in _tailers.Keys)
                {
                    _dirty[path] = now;
                }
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Stop();
            lock (_gate)
            {
                _tailers.Clear();
                _lastLength.Clear();
            }
        }

        private void SetError(string message)
        {
            LastError = message;
            ErrorOccurred?.Invoke(message);
        }

        private void MarkDirty(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _dirty[path] = DateTime.UtcNow;
            }
        }

        private void RemoveFile(string path)
        {
            lock (_gate)
            {
                _tailers.Remove(path);
                _lastLength.Remove(path);
                _dirty.Remove(path);
            }
        }

        private void SafeProcessDirty()
        {
            try
            {
                ProcessDirty();
            }
            catch (Exception ex)
            {
                SetError($"处理日志时出错: {ex.Message}");
            }
        }

        private void ProcessDirty()
        {
            List<string>? ready = null;
            DateTime now = DateTime.UtcNow;

            lock (_gate)
            {
                foreach (var pair in _dirty)
                {
                    if (now - pair.Value < Debounce)
                    {
                        continue;
                    }

                    (ready ??= new List<string>()).Add(pair.Key);
                }

                if (ready != null)
                {
                    foreach (string path in ready)
                    {
                        _dirty.Remove(path);
                    }
                }
            }

            if (ready == null)
            {
                return;
            }

            foreach (string path in ready)
            {
                ProcessFile(path);
            }
        }

        private void ProcessFile(string path)
        {
            ChatLogTailer? tailer;

            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                if (!File.Exists(path))
                {
                    _tailers.Remove(path);
                    _lastLength.Remove(path);
                    return;
                }

                if (!_tailers.TryGetValue(path, out tailer))
                {
                    tailer = new ChatLogTailer(path);
                    _tailers[path] = tailer;
                }
            }

            if (tailer == null)
            {
                return;
            }

            IReadOnlyList<ChatLogMessage> messages = tailer.ReadNewLines();

            lock (_gate)
            {
                _lastLength[path] = SafeLength(path);
                if (tailer.LastError != null)
                {
                    SetError(tailer.LastError);
                }
            }

            foreach (ChatLogMessage message in messages)
            {
                MessageReceived?.Invoke(message);
            }
        }

        private void SafePoll()
        {
            try
            {
                Poll();
            }
            catch (Exception ex)
            {
                SetError($"轮询日志目录时出错: {ex.Message}");
            }
        }

        private void Poll()
        {
            if (!System.IO.Directory.Exists(_directory))
            {
                return;
            }

            foreach (string path in System.IO.Directory.EnumerateFiles(_directory, "*.txt"))
            {
                long length = SafeLength(path);
                bool shouldProcess = false;
                bool isNew = false;

                lock (_gate)
                {
                    if (!_tailers.ContainsKey(path))
                    {
                        _tailers[path] = new ChatLogTailer(path);
                        _lastLength[path] = 0;
                        isNew = true;
                    }

                    if (isNew || !_lastLength.TryGetValue(path, out long previous) || previous != length)
                    {
                        shouldProcess = true;
                    }
                }

                if (shouldProcess)
                {
                    MarkDirty(path);
                }
            }
        }

        private static long SafeLength(string path)
        {
            try
            {
                return new FileInfo(path).Length;
            }
            catch
            {
                return 0;
            }
        }
    }
}
