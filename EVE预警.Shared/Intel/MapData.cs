using System.Reflection;
using System.Text.Json;

namespace EVE预警.Intel
{
    /// <summary>
    /// 离线星系数据：星系中英文名索引 + 星门跳跃图。
    /// 数据在开发期由 tools/GenerateMapData 生成并嵌入程序，运行时完全不访问网络。
    /// </summary>
    public static class MapData
    {
        private const string EmbeddedResourceName = "EVE预警.Data.map-data.json";

        private static readonly object InitLock = new();
        private static bool _initialized;

        private static readonly Dictionary<int, string> EnglishById = new();
        private static readonly Dictionary<int, string> ChineseById = new();
        private static readonly Dictionary<int, int[]> AdjacencyMap = new();
        private static readonly Dictionary<string, int> NameIndex = new(StringComparer.OrdinalIgnoreCase);
        private static readonly List<string> AllNamesList = new();
        private static string _allNamesCacheKey = "";
        private static string[] _allNamesCache = Array.Empty<string>();

        public static string? LoadError { get; private set; }

        public static bool IsLoaded
        {
            get
            {
                EnsureLoaded();
                return LoadError == null;
            }
        }

        public static IReadOnlyDictionary<int, int[]> Adjacency
        {
            get
            {
                EnsureLoaded();
                return AdjacencyMap;
            }
        }

        /// <summary>用于自动补全的全部星系名（中英文、去重、排序）。</summary>
        public static string[] AllNames
        {
            get
            {
                EnsureLoaded();
                lock (InitLock)
                {
                    // 别名文件变化后重新生成缓存
                    if (_allNamesCacheKey != NameIndex.Count.ToString())
                    {
                        _allNamesCache = AllNamesList
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                            .ToArray();
                        _allNamesCacheKey = NameIndex.Count.ToString();
                    }

                    return _allNamesCache;
                }
            }
        }

        public static void EnsureLoaded()
        {
            if (_initialized)
            {
                return;
            }

            lock (InitLock)
            {
                if (_initialized)
                {
                    return;
                }

                _initialized = true;
                try
                {
                    LoadCore();
                }
                catch (Exception ex)
                {
                    LoadError = $"加载星系数据失败: {ex.Message}";
                }
            }
        }

        /// <summary>把用户输入解析为星系 ID；支持中英文名、去空格写法、别名和纯数字 ID。</summary>
        public static int? ResolveSystem(string? name)
        {
            EnsureLoaded();
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            string trimmed = name.Trim();
            if (int.TryParse(trimmed, out int numeric) && EnglishById.ContainsKey(numeric))
            {
                return numeric;
            }

            lock (InitLock)
            {
                if (NameIndex.TryGetValue(trimmed, out int id))
                {
                    return id;
                }

                string compact = CompactName(trimmed);
                if (NameIndex.TryGetValue(compact, out id))
                {
                    return id;
                }
            }

            return null;
        }

        public static string GetDisplayName(int systemId)
        {
            EnsureLoaded();
            lock (InitLock)
            {
                if (ChineseById.TryGetValue(systemId, out string? chinese) &&
                    !string.IsNullOrWhiteSpace(chinese))
                {
                    return chinese;
                }

                return EnglishById.TryGetValue(systemId, out string? english) ? english : systemId.ToString();
            }
        }

        public static string GetEnglishName(int systemId)
        {
            EnsureLoaded();
            lock (InitLock)
            {
                return EnglishById.TryGetValue(systemId, out string? english) ? english : systemId.ToString();
            }
        }

        /// <summary>某个星系参与建立索引的名称（中英文），供名称匹配器使用。</summary>
        public static IEnumerable<(int SystemId, string Name)> EnumerateIndexedNames()
        {
            EnsureLoaded();
            List<(int, string)> snapshot;
            lock (InitLock)
            {
                snapshot = NameIndex
                    .Select(kv => (kv.Value, kv.Key))
                    .Where(pair => !string.IsNullOrWhiteSpace(pair.Item2) && pair.Item2.Length >= 2)
                    .ToList();
            }

            return snapshot;
        }

        private static void LoadCore()
        {
            string json = ReadMapJson();
            using var document = JsonDocument.Parse(json);

            foreach (var system in document.RootElement.GetProperty("systems").EnumerateArray())
            {
                int id = system[0].GetInt32();
                string english = system[1].GetString() ?? "";
                string chinese = system[2].GetString() ?? "";

                EnglishById[id] = english;
                ChineseById[id] = chinese;

                AddName(english, id);
                AddName(chinese, id);
                AddName(CompactName(english), id);
                AddName(CompactName(chinese), id);
            }

            var adjacency = new Dictionary<int, List<int>>();
            foreach (var jump in document.RootElement.GetProperty("jumps").EnumerateArray())
            {
                int a = jump[0].GetInt32();
                int b = jump[1].GetInt32();
                if (a == b)
                {
                    continue;
                }

                if (!adjacency.TryGetValue(a, out var listA))
                {
                    adjacency[a] = listA = new List<int>();
                }

                if (!adjacency.TryGetValue(b, out var listB))
                {
                    adjacency[b] = listB = new List<int>();
                }

                listA.Add(b);
                listB.Add(a);
            }

            foreach (var (id, neighbors) in adjacency)
            {
                AdjacencyMap[id] = neighbors.Distinct().ToArray();
            }

            LoadAliases();
        }

        private static string ReadMapJson()
        {
            // 优先使用用户覆盖文件，便于不重新编译即可修正地图。
            string userPath = Path.Combine(GetAppDataDirectory(), "map-data.json");
            if (File.Exists(userPath))
            {
                try
                {
                    string userJson = File.ReadAllText(userPath);
                    using var probe = JsonDocument.Parse(userJson);
                    if (probe.RootElement.TryGetProperty("systems", out _) &&
                        probe.RootElement.TryGetProperty("jumps", out _))
                    {
                        return userJson;
                    }
                }
                catch
                {
                    // 用户文件损坏时回退到内置数据
                }
            }

            using Stream? stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream(EmbeddedResourceName);
            if (stream == null)
            {
                throw new InvalidOperationException(
                    $"未找到内置资源 {EmbeddedResourceName}");
            }

            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        private static void LoadAliases()
        {
            string path = Path.Combine(GetAppDataDirectory(), "system-aliases.json");
            if (!File.Exists(path))
            {
                return;
            }

            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    return;
                }

                foreach (var property in document.RootElement.EnumerateObject())
                {
                    string alias = property.Name?.Trim() ?? "";
                    if (alias.Length < 2)
                    {
                        continue;
                    }

                    int? targetId = null;
                    if (property.Value.ValueKind == JsonValueKind.Number &&
                        property.Value.TryGetInt32(out int numericId))
                    {
                        targetId = numericId;
                    }
                    else if (property.Value.ValueKind == JsonValueKind.String)
                    {
                        targetId = ResolveWithoutAliases(property.Value.GetString());
                    }

                    if (targetId is int id && EnglishById.ContainsKey(id))
                    {
                        NameIndex[alias] = id;
                        AllNamesList.Add(alias);
                    }
                }
            }
            catch
            {
                // 别名文件格式错误时忽略，不影响主数据
            }
        }

        private static int? ResolveWithoutAliases(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            string trimmed = name.Trim();
            if (NameIndex.TryGetValue(trimmed, out int id))
            {
                return id;
            }

            return NameIndex.TryGetValue(CompactName(trimmed), out id) ? id : null;
        }

        private static void AddName(string? name, int id)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            string trimmed = name.Trim();
            if (!NameIndex.ContainsKey(trimmed))
            {
                NameIndex[trimmed] = id;
                AllNamesList.Add(trimmed);
            }
        }

        private static string CompactName(string name) =>
            string.Concat(name.Where(c => !char.IsWhiteSpace(c)));

        internal static string GetAppDataDirectory()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string dir = Path.Combine(appData, "EVE预警");
            try
            {
                Directory.CreateDirectory(dir);
                return dir;
            }
            catch
            {
                return AppDomain.CurrentDomain.BaseDirectory;
            }
        }
    }
}
