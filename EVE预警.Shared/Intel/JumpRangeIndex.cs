namespace EVE预警.Intel
{
    /// <summary>
    /// 以「所在星系」为起点、限定跳数的 BFS 结果缓存。
    /// 设置变更时重建一次，之后每条聊天消息只做字典查询。
    /// </summary>
    public sealed class JumpRangeIndex
    {
        private readonly Dictionary<int, int> _distances;

        public JumpRangeIndex(int homeSystemId, int maxJumps)
        {
            HomeSystemId = homeSystemId;
            MaxJumps = Math.Max(0, maxJumps);
            _distances = Build(homeSystemId, MaxJumps);
        }

        public int HomeSystemId { get; }

        public int MaxJumps { get; }

        public int ReachableCount => _distances.Count;

        /// <summary>命中范围内星系时返回 true；0 跳表示家星系本身。</summary>
        public bool TryGetJumps(int systemId, out int jumps) =>
            _distances.TryGetValue(systemId, out jumps);

        private static Dictionary<int, int> Build(int homeSystemId, int maxJumps)
        {
            var distances = new Dictionary<int, int>(capacity: 256) { [homeSystemId] = 0 };
            if (maxJumps <= 0)
            {
                return distances;
            }

            IReadOnlyDictionary<int, int[]> adjacency = MapData.Adjacency;
            var queue = new Queue<int>();
            queue.Enqueue(homeSystemId);

            while (queue.Count > 0)
            {
                int current = queue.Dequeue();
                int distance = distances[current];
                if (distance >= maxJumps)
                {
                    continue;
                }

                if (!adjacency.TryGetValue(current, out int[]? neighbors))
                {
                    continue;
                }

                foreach (int neighbor in neighbors)
                {
                    if (distances.ContainsKey(neighbor))
                    {
                        continue;
                    }

                    distances[neighbor] = distance + 1;
                    queue.Enqueue(neighbor);
                }
            }

            return distances;
        }
    }
}
