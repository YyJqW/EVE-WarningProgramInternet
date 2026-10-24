using System.Text.Json;

namespace EVE预警服务器
{
    /// <summary>
    /// 服务器端配置：只保存监听端口。预警由客户端生产，服务器仅做广播。
    /// </summary>
    public class ServerConfig
    {
        public int Port { get; set; } = 2424;

        private static string GetConfigPath()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string dir = Path.Combine(appData, "EVE预警");
            try
            {
                Directory.CreateDirectory(dir);
            }
            catch
            {
                return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "server-config.json");
            }

            return Path.Combine(dir, "server-config.json");
        }

        public static ServerConfig Load()
        {
            try
            {
                string path = GetConfigPath();
                if (File.Exists(path))
                {
                    var config = JsonSerializer.Deserialize<ServerConfig>(File.ReadAllText(path));
                    if (config != null)
                    {
                        return config;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"加载服务器配置失败: {ex.Message}");
            }

            return new ServerConfig();
        }

        public static void Save(ServerConfig config)
        {
            try
            {
                string path = GetConfigPath();
                string json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(path, json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"保存服务器配置失败: {ex.Message}");
            }
        }
    }
}
