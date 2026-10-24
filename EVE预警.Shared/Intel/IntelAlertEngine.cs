using EVE预警.ChatLog;

namespace EVE预警.Intel
{
    /// <summary>预警频道的运行配置（由设置窗口/配置文件构建）。</summary>
    public sealed class IntelAlertSettings
    {
        public bool Enabled { get; init; }

        public int HomeSystemId { get; init; }

        public int MaxJumps { get; init; }

        public IReadOnlyCollection<string> Channels { get; init; } = Array.Empty<string>();

        public int CooldownSeconds { get; init; } = 60;
    }

    /// <summary>一次命中的频道预警。</summary>
    public sealed record IntelAlert(
        int SystemId,
        string SystemName,
        int Jumps,
        string ChannelName,
        string Author,
        string Text,
        DateTime TimestampUtc);

    /// <summary>
    /// 判定聊天消息是否构成预警：频道过滤 → 星系名识别 → 跳数范围 → 去重/冷却。
    /// 事件在后台线程触发，调用方负责切回 UI 线程。
    /// </summary>
    public sealed class IntelAlertEngine
    {
        private const int SeenCapacity = 2000;

        private readonly object _gate = new();
        private readonly HashSet<string> _seenMessages = new(StringComparer.Ordinal);
        private readonly Queue<string> _seenOrder = new();
        private readonly Dictionary<int, DateTime> _lastAlertBySystem = new();

        private IntelAlertSettings? _settings;
        private HashSet<string> _channels = new(StringComparer.OrdinalIgnoreCase);
        private JumpRangeIndex? _jumpRange;

        public event Action<IntelAlert>? AlertRaised;

        public void Configure(IntelAlertSettings settings)
        {
            lock (_gate)
            {
                if (!settings.Enabled || settings.HomeSystemId <= 0)
                {
                    _settings = null;
                    _channels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    _jumpRange = null;
                    return;
                }

                _settings = settings;
                _channels = new HashSet<string>(
                    settings.Channels.Where(c => !string.IsNullOrWhiteSpace(c)),
                    StringComparer.OrdinalIgnoreCase);
                _jumpRange = new JumpRangeIndex(settings.HomeSystemId, settings.MaxJumps);
            }
        }

        public void ClearHistory()
        {
            lock (_gate)
            {
                _seenMessages.Clear();
                _seenOrder.Clear();
                _lastAlertBySystem.Clear();
            }
        }

        public void OnMessage(ChatLogMessage message)
        {
            IntelAlertSettings? settings;
            HashSet<string> channels;
            JumpRangeIndex? jumpRange;

            lock (_gate)
            {
                settings = _settings;
                channels = _channels;
                jumpRange = _jumpRange;
            }

            if (settings == null || jumpRange == null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(message.ChannelName) ||
                !channels.Contains(message.ChannelName))
            {
                return;
            }

            string dedupeKey = string.Concat(
                message.ChannelName, "|",
                message.TimestampUtc.ToString("O"), "|",
                message.Author, "|",
                message.Text);

            lock (_gate)
            {
                if (!_seenMessages.Add(dedupeKey))
                {
                    return;
                }

                _seenOrder.Enqueue(dedupeKey);
                while (_seenOrder.Count > SeenCapacity)
                {
                    _seenMessages.Remove(_seenOrder.Dequeue());
                }
            }

            IReadOnlyList<int> systems = SystemNameMatcher.FindSystems(message.Text);
            if (systems.Count == 0)
            {
                return;
            }

            int bestSystemId = 0;
            int bestJumps = int.MaxValue;
            foreach (int systemId in systems)
            {
                if (!jumpRange.TryGetJumps(systemId, out int jumps))
                {
                    continue;
                }

                if (jumps < bestJumps)
                {
                    bestJumps = jumps;
                    bestSystemId = systemId;
                }
            }

            if (bestSystemId == 0)
            {
                return;
            }

            DateTime now = DateTime.UtcNow;
            lock (_gate)
            {
                if (_lastAlertBySystem.TryGetValue(bestSystemId, out DateTime last) &&
                    (now - last).TotalSeconds < Math.Max(0, settings.CooldownSeconds))
                {
                    return;
                }

                _lastAlertBySystem[bestSystemId] = now;
            }

            var alert = new IntelAlert(
                SystemId: bestSystemId,
                SystemName: MapData.GetDisplayName(bestSystemId),
                Jumps: bestJumps,
                ChannelName: message.ChannelName,
                Author: message.Author,
                Text: message.Text,
                TimestampUtc: message.TimestampUtc);

            AlertRaised?.Invoke(alert);
        }
    }
}
