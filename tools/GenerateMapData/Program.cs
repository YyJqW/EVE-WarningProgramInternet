using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GenerateMapData;

/// <summary>
/// 开发期工具：从 EVE 官方 SDE 生成离线星系跳跃图与中英文星系名。
/// 生成的 map-data.json 随客户端打包，运行时不再访问任何在线接口。
/// </summary>
internal static class Program
{
    private const string DefaultSdeUrl =
        "https://eve-static-data-export.s3-eu-west-1.amazonaws.com/tranquility/sde.zip";
    private const string EsiBase = "https://esi.evetech.net/latest";

    private static readonly Regex SolarSystemIdRegex =
        new(@"^solarSystemID:\s*(\d+)\s*$", RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex GateIdRegex =
        new(@"^\s{2}(\d+):\s*$", RegexOptions.Compiled);

    private static readonly Regex GateDestinationRegex =
        new(@"^\s{4}destination:\s*(\d+)\s*$", RegexOptions.Compiled);

    private static readonly HttpClient Http = CreateHttpClient();

    private static async Task<int> Main(string[] args)
    {
        var options = ParseArgs(args);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            string zipPath = ResolveSdeArchive(options);
            Console.WriteLine($"[1/5] SDE: {zipPath}");

            var systems = ReadSystemsFromSde(zipPath);
            Console.WriteLine($"[2/5] 解析到 {systems.Count} 个星系");

            var (gateOwner, edges) = BuildGraph(systems, zipPath);
            Console.WriteLine($"[3/5] 星门 {gateOwner.Count} 条，去重后跳跃边 {edges.Count} 条");

            var names = await LoadNamesAsync(
                systems, gateOwner, options.FetchChinese, options.ChineseLimit, options.CachePath);
            Console.WriteLine($"[4/5] 英文名 {names.Count(n => n.Value.English is not null)} 个，中文名 {names.Count(n => n.Value.Chinese is not null)} 个");

            if (options.Validate)
            {
                await ValidateDistancesAsync(edges);
            }

            WriteOutput(options.OutputPath, systems, edges, names);
            Console.WriteLine($"[5/5] 已写出 {options.OutputPath}，用时 {stopwatch.Elapsed:hh\\:mm\\:ss}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"生成失败: {ex}");
            return 1;
        }
    }

    #region 参数

    private sealed class Options
    {
        public string? SdePath { get; init; }
        public string OutputPath { get; init; } = Path.Combine("EVE预警.Shared", "Data", "map-data.json");
        public string CachePath { get; init; } = Path.Combine("tools", "GenerateMapData", "zh-cache.json");
        public bool FetchChinese { get; init; } = true;
        public int? ChineseLimit { get; init; }
        public bool Validate { get; init; } = true;
    }

    private static Options ParseArgs(string[] args)
    {
        string? sde = null;
        string output = Path.Combine("EVE预警.Shared", "Data", "map-data.json");
        string cache = Path.Combine("tools", "GenerateMapData", "zh-cache.json");
        bool fetchChinese = true;
        int? chineseLimit = null;
        bool validate = true;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--sde":
                    sde = RequireValue(args, ref i);
                    break;
                case "--out":
                    output = RequireValue(args, ref i);
                    break;
                case "--cache":
                    cache = RequireValue(args, ref i);
                    break;
                case "--no-zh":
                    fetchChinese = false;
                    break;
                case "--zh-limit":
                    chineseLimit = int.Parse(RequireValue(args, ref i));
                    break;
                case "--skip-validate":
                    validate = false;
                    break;
                case "--help":
                case "-h":
                    PrintHelp();
                    Environment.Exit(0);
                    break;
                default:
                    throw new ArgumentException($"未知参数: {args[i]}");
            }
        }

        return new Options
        {
            SdePath = sde,
            OutputPath = output,
            CachePath = cache,
            FetchChinese = fetchChinese,
            ChineseLimit = chineseLimit,
            Validate = validate,
        };
    }

    private static string RequireValue(string[] args, ref int index)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"参数 {args[index]} 缺少值");
        }

        return args[++index];
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            用法: GenerateMapData [选项]

              --sde <path>       指定 sde.zip 路径；不指定则下载到临时目录
              --out <path>       输出 JSON 路径（默认 EVE预警.Shared/Data/map-data.json）
              --cache <path>     中文名缓存路径（默认 tools/GenerateMapData/zh-cache.json）
              --no-zh            不拉取中文名
              --zh-limit <n>     仅拉取前 n 个中文名（用于快速验证）
              --skip-validate    跳过与 ESI route 的抽样校验

            运行时程序不访问网络；本工具仅在开发期使用。
            """);
    }

    #endregion

    #region SDE 解析

    private sealed record SdeSystem(int Id, string FolderName);

    private static string ResolveSdeArchive(Options options)
    {
        if (!string.IsNullOrWhiteSpace(options.SdePath))
        {
            if (!File.Exists(options.SdePath))
            {
                throw new FileNotFoundException("指定的 SDE 文件不存在", options.SdePath);
            }

            return options.SdePath;
        }

        string cacheDir = Path.Combine(Path.GetTempPath(), "EVE预警-SDE");
        Directory.CreateDirectory(cacheDir);
        string target = Path.Combine(cacheDir, "sde.zip");

        if (File.Exists(target) && new FileInfo(target).Length > 100_000_000)
        {
            Console.WriteLine("复用已下载的 SDE 缓存");
            return target;
        }

        Console.WriteLine("下载 SDE（约 112 MB）...");
        using var response = Http.GetAsync(DefaultSdeUrl, HttpCompletionOption.ResponseHeadersRead)
            .GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();
        using var input = response.Content.ReadAsStream();
        using var output = File.Create(target);
        input.CopyTo(output);
        return target;
    }

    private static List<SdeSystem> ReadSystemsFromSde(string zipPath)
    {
        var systems = new List<SdeSystem>();
        using var archive = ZipFile.OpenRead(zipPath);

        foreach (var entry in archive.Entries)
        {
            string fullName = entry.FullName.Replace('\\', '/');
            if (!fullName.StartsWith("universe/eve/", StringComparison.OrdinalIgnoreCase) ||
                !fullName.EndsWith("/solarsystem.yaml", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string[] parts = fullName.Split('/');
            if (parts.Length < 6)
            {
                continue;
            }

            string folderName = parts[^2];
            using var stream = entry.Open();
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            string text = reader.ReadToEnd();

            Match idMatch = SolarSystemIdRegex.Match(text);
            if (!idMatch.Success)
            {
                continue;
            }

            systems.Add(new SdeSystem(int.Parse(idMatch.Groups[1].Value), folderName));
        }

        return systems
            .GroupBy(s => s.Id)
            .Select(g => g.First())
            .OrderBy(s => s.Id)
            .ToList();
    }

    /// <summary>
    /// 从每个星系文件读取 stargates 块。destination 是星门 ID，需要先用
    /// 「星门 → 所属星系」索引换算成星系之间的边。
    /// </summary>
    private static (Dictionary<int, int> GateOwner, HashSet<(int, int)> Edges) BuildGraph(
        List<SdeSystem> systems, string zipPath)
    {
        var gateOwner = new Dictionary<int, int>();
        var gateDestination = new Dictionary<int, int>();
        var knownIds = systems.Select(s => s.Id).ToHashSet();

        using (var archive = ZipFile.OpenRead(zipPath))
        {
            foreach (var entry in archive.Entries)
            {
                string fullName = entry.FullName.Replace('\\', '/');
                if (!fullName.StartsWith("universe/eve/", StringComparison.OrdinalIgnoreCase) ||
                    !fullName.EndsWith("/solarsystem.yaml", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                using var stream = entry.Open();
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                string text = reader.ReadToEnd();

                Match idMatch = SolarSystemIdRegex.Match(text);
                if (!idMatch.Success)
                {
                    continue;
                }

                int systemId = int.Parse(idMatch.Groups[1].Value);
                if (!knownIds.Contains(systemId))
                {
                    continue;
                }

                ParseGates(text, systemId, gateOwner, gateDestination);
            }
        }

        var edges = new HashSet<(int, int)>();
        foreach (var (gateId, destinationGateId) in gateDestination)
        {
            if (gateOwner.TryGetValue(gateId, out int from) &&
                gateOwner.TryGetValue(destinationGateId, out int to) &&
                from != to)
            {
                edges.Add(from < to ? (from, to) : (to, from));
            }
        }

        return (gateOwner, edges);
    }

    private static void ParseGates(
        string text,
        int systemId,
        Dictionary<int, int> gateOwner,
        Dictionary<int, int> gateDestination)
    {
        bool inGates = false;
        int currentGate = 0;

        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');

            if (!inGates)
            {
                if (line.Trim() == "stargates:")
                {
                    inGates = true;
                }

                continue;
            }

            if (line.Length == 0)
            {
                continue;
            }

            if (line[0] != ' ')
            {
                // 已离开 stargates 块
                break;
            }

            Match gateMatch = GateIdRegex.Match(line);
            if (gateMatch.Success)
            {
                currentGate = int.Parse(gateMatch.Groups[1].Value);
                gateOwner[currentGate] = systemId;
                continue;
            }

            Match destMatch = GateDestinationRegex.Match(line);
            if (destMatch.Success && currentGate != 0)
            {
                gateDestination[currentGate] = int.Parse(destMatch.Groups[1].Value);
            }
        }
    }

    #endregion

    #region 名称获取

    private sealed class NamePair
    {
        public string? English { get; set; }
        public string? Chinese { get; set; }
    }

    private static async Task<Dictionary<int, NamePair>> LoadNamesAsync(
        List<SdeSystem> systems,
        Dictionary<int, int> gateOwner,
        bool fetchChinese,
        int? chineseLimit,
        string cachePath)
    {
        var result = systems.ToDictionary(
            s => s.Id,
            s => new NamePair { English = s.FolderName });

        // 英文名批量获取：POST /universe/names/ 每批 1000 个。
        var ids = systems.Select(s => s.Id).ToList();
        for (int offset = 0; offset < ids.Count; offset += 1000)
        {
            var batch = ids.Skip(offset).Take(1000).ToList();
            try
            {
                var names = await FetchEnglishNamesAsync(batch);
                foreach (var (id, name) in names)
                {
                    if (result.TryGetValue(id, out var pair))
                    {
                        pair.English = name;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"英文名批次 {offset} 获取失败，回退目录名: {ex.Message}");
            }
        }

        if (!fetchChinese)
        {
            return result;
        }

        var cache = LoadChineseCache(cachePath);
        // 只对拥有星门的星系拉取中文名：虫洞/深渊星系不在跳跃图内，且请求会返回 404。
        var gatedSystemIds = gateOwner.Values.ToHashSet();
        var targets = systems
            .Where(s => gatedSystemIds.Contains(s.Id))
            .Select(s => s.Id)
            .Where(id => !cache.ContainsKey(id))
            .ToList();

        if (chineseLimit is > 0)
        {
            targets = targets.Take(chineseLimit.Value).ToList();
        }

        Console.WriteLine($"需要拉取中文名的星系: {targets.Count}（已缓存 {cache.Count}）");

        int completed = 0;
        int failed = 0;
        var cacheLock = new object();
        var options = new ParallelOptions { MaxDegreeOfParallelism = 12 };

        await Parallel.ForEachAsync(targets, options, async (id, ct) =>
        {
            string? chineseName = await FetchChineseNameAsync(id, ct);
            if (chineseName is not null)
            {
                lock (cacheLock)
                {
                    cache[id] = chineseName;
                }
            }
            else
            {
                Interlocked.Increment(ref failed);
            }

            int done = Interlocked.Increment(ref completed);
            if (done % 250 == 0)
            {
                SaveChineseCache(cachePath, cache, cacheLock);
                Console.WriteLine($"  中文名进度 {done}/{targets.Count}，失败 {failed}");
            }
        });

        SaveChineseCache(cachePath, cache, cacheLock);

        foreach (var (id, name) in cache)
        {
            if (result.TryGetValue(id, out var pair))
            {
                pair.Chinese = name;
            }
        }

        return result;
    }

    private static async Task<Dictionary<int, string>> FetchEnglishNamesAsync(List<int> ids)
    {
        string json = JsonSerializer.Serialize(ids);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await Http.PostAsync($"{EsiBase}/universe/names/", content);
        response.EnsureSuccessStatusCode();
        string body = await response.Content.ReadAsStringAsync();

        var result = new Dictionary<int, string>();
        using var document = JsonDocument.Parse(body);
        foreach (var element in document.RootElement.EnumerateArray())
        {
            if (element.TryGetProperty("category", out var category) &&
                category.GetString() == "solar_system" &&
                element.TryGetProperty("id", out var idElement) &&
                element.TryGetProperty("name", out var nameElement))
            {
                result[idElement.GetInt32()] = nameElement.GetString() ?? "";
            }
        }

        return result;
    }

    private static async Task<string?> FetchChineseNameAsync(int systemId, CancellationToken ct)
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(
                    HttpMethod.Get, $"{EsiBase}/universe/systems/{systemId}/?datasource=tranquility");
                request.Headers.AcceptLanguage.Add(new StringWithQualityHeaderValue("zh"));

                using var response = await Http.SendAsync(request, ct);
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    return null;
                }

                if ((int)response.StatusCode == 429 || (int)response.StatusCode >= 500)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(500 * (attempt + 1)), ct);
                    continue;
                }

                response.EnsureSuccessStatusCode();
                string body = await response.Content.ReadAsStringAsync(ct);
                using var document = JsonDocument.Parse(body);
                return document.RootElement.TryGetProperty("name", out var name)
                    ? name.GetString()
                    : null;
            }
            catch (HttpRequestException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500 * (attempt + 1)), ct);
            }
        }

        return null;
    }

    private static Dictionary<int, string> LoadChineseCache(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                string json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<Dictionary<int, string>>(json) ?? new();
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"读取中文名缓存失败，将重新拉取: {ex.Message}");
        }

        return new Dictionary<int, string>();
    }

    private static void SaveChineseCache(
        string path, Dictionary<int, string> cache, object cacheLock)
    {
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string json;
            lock (cacheLock)
            {
                json = JsonSerializer.Serialize(cache);
            }

            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"保存中文名缓存失败: {ex.Message}");
        }
    }

    #endregion

    #region 校验

    /// <summary>
    /// 抽样用 ESI route 校验本地 BFS 的跳数，防止星门图解析错误。
    /// 仅开发期使用。
    /// </summary>
    private static async Task ValidateDistancesAsync(HashSet<(int, int)> edges)
    {
        var adjacency = BuildAdjacency(edges);
        var random = new Random(20261024);
        var nodeIds = adjacency.Keys.ToList();
        int checkedCount = 0;
        int mismatch = 0;

        for (int i = 0; i < 20 && nodeIds.Count > 1; i++)
        {
            int from = nodeIds[random.Next(nodeIds.Count)];
            int to = nodeIds[random.Next(nodeIds.Count)];
            if (from == to)
            {
                continue;
            }

            int? local = BfsDistance(adjacency, from, to);
            if (local is null)
            {
                continue;
            }

            try
            {
                using var response = await Http.GetAsync($"{EsiBase}/route/{from}/{to}/?datasource=tranquility");
                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                string body = await response.Content.ReadAsStringAsync();
                using var document = JsonDocument.Parse(body);
                int esiJumps = document.RootElement.GetArrayLength() - 1;
                checkedCount++;
                if (esiJumps != local.Value)
                {
                    mismatch++;
                    Console.Error.WriteLine(
                        $"校验不一致: {from} → {to}，本地 {local.Value} 跳，ESI {esiJumps} 跳");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"校验请求失败，跳过: {ex.Message}");
            }
        }

        Console.WriteLine($"抽样校验完成: {checkedCount} 对，不一致 {mismatch} 对");
        if (mismatch > 0)
        {
            throw new InvalidOperationException("本地跳跃图与 ESI 不一致，生成中止");
        }
    }

    private static Dictionary<int, List<int>> BuildAdjacency(HashSet<(int, int)> edges)
    {
        var adjacency = new Dictionary<int, List<int>>();
        foreach (var (a, b) in edges)
        {
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

        return adjacency;
    }

    private static int? BfsDistance(Dictionary<int, List<int>> adjacency, int from, int to)
    {
        if (from == to)
        {
            return 0;
        }

        var visited = new HashSet<int> { from };
        var queue = new Queue<(int Node, int Distance)>();
        queue.Enqueue((from, 0));

        while (queue.Count > 0)
        {
            var (node, distance) = queue.Dequeue();
            if (!adjacency.TryGetValue(node, out var neighbors))
            {
                continue;
            }

            foreach (int neighbor in neighbors)
            {
                if (neighbor == to)
                {
                    return distance + 1;
                }

                if (visited.Add(neighbor))
                {
                    queue.Enqueue((neighbor, distance + 1));
                }
            }
        }

        return null;
    }

    #endregion

    #region 输出

    private static void WriteOutput(
        string outputPath,
        List<SdeSystem> systems,
        HashSet<(int, int)> edges,
        Dictionary<int, NamePair> names)
    {
        string? dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        using var stream = File.Create(outputPath);
        using var writer = new Utf8JsonWriter(stream);
        writer.WriteStartObject();
        writer.WriteNumber("version", 1);
        writer.WriteString("generatedAt", DateTimeOffset.UtcNow);
        writer.WriteString("source", "EVE SDE sde.zip + ESI zh names");

        writer.WriteStartArray("systems");
        foreach (var system in systems.OrderBy(s => s.Id))
        {
            names.TryGetValue(system.Id, out NamePair? pair);
            writer.WriteStartArray();
            writer.WriteNumberValue(system.Id);
            writer.WriteStringValue(pair?.English ?? system.FolderName);
            writer.WriteStringValue(pair?.Chinese ?? pair?.English ?? system.FolderName);
            writer.WriteEndArray();
        }

        writer.WriteEndArray();

        writer.WriteStartArray("jumps");
        foreach (var (a, b) in edges.OrderBy(e => e.Item1).ThenBy(e => e.Item2))
        {
            writer.WriteStartArray();
            writer.WriteNumberValue(a);
            writer.WriteNumberValue(b);
            writer.WriteEndArray();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    #endregion

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("EVE-Warning-MapData-Generator", "1.0"));
        return client;
    }
}
