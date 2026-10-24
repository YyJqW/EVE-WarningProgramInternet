using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace EVE预警服务器
{
    public partial class Main : Form
    {
        // 服务器相关
        private TcpListener? _listener;
        private bool _isRunning = false;
        private CancellationTokenSource? _acceptCts;
        private System.Threading.Timer? _heartbeatTimer;

        // 客户端管理
        private readonly Dictionary<string, ClientInfo> _clients = new();
        private readonly object _clientsLock = new object();

        // 服务器配置（仅端口持久化）
        private ServerConfig _config = new();

        public Main()
        {
            InitializeComponent();
            InitializeListView();
            _config = ServerConfig.Load();
            textBoxPort.Text = _config.Port.ToString();

            // VM/无人值守场景：带 --autostart 启动时自动开始监听
            if (Environment.GetCommandLineArgs()
                .Any(a => string.Equals(a, "--autostart", StringComparison.OrdinalIgnoreCase)))
            {
                Shown += (_, _) =>
                {
                    AddLog("[服务器] 检测到 --autostart，自动开始监听");
                    buttonStart.PerformClick();
                };
            }
        }

        /// <summary>
        /// 初始化 ListView 列
        /// </summary>
        private void InitializeListView()
        {
            listViewOnlineUsers.Columns.Add("用户名", 150);
            listViewOnlineUsers.Columns.Add("星系", 150);
            listViewOnlineUsers.Columns.Add("连接时间", 150);
            listViewOnlineUsers.Columns.Add("最后心跳", 150);
        }

        /// <summary>
        /// 添加日志（同时写入 %APPDATA%\EVE预警\server.log，便于 VM 无人值守时排查）
        /// </summary>
        private void AddLog(string message)
        {
            string logEntry = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
            WriteLogFile(logEntry);

            if (InvokeRequired)
            {
                Invoke(new Action(() => AppendLogLine(logEntry)));
                return;
            }

            AppendLogLine(logEntry);
        }

        private void AppendLogLine(string logEntry)
        {
            richTextBoxLog.AppendText(logEntry + Environment.NewLine);
            richTextBoxLog.ScrollToCaret();
        }

        private static readonly object LogFileLock = new();

        private static void WriteLogFile(string line)
        {
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EVE预警");
                Directory.CreateDirectory(dir);
                lock (LogFileLock)
                {
                    File.AppendAllText(Path.Combine(dir, "server.log"), line + Environment.NewLine);
                }
            }
            catch
            {
                // 日志写入失败不影响主流程
            }
        }

        /// <summary>
        /// 更新在线用户列表
        /// </summary>
        private void UpdateOnlineUsersList()
        {
            if (InvokeRequired)
            {
                Invoke(new Action(UpdateOnlineUsersList));
                return;
            }

            listViewOnlineUsers.BeginUpdate();
            listViewOnlineUsers.Items.Clear();

            lock (_clientsLock)
            {
                foreach (var client in _clients.Values)
                {
                    var item = new ListViewItem(new[]
                    {
                        client.Username,
                        client.Galaxy,
                        client.ConnectTime.ToString("HH:mm:ss"),
                        client.LastHeartbeat.ToString("HH:mm:ss")
                    });
                    listViewOnlineUsers.Items.Add(item);
                }
            }

            listViewOnlineUsers.EndUpdate();
        }

        /// <summary>
        /// 启动服务器
        /// </summary>
        private async void buttonStart_Click(object sender, EventArgs e)
        {
            try
            {
                int port = int.TryParse(textBoxPort.Text, out var p) ? p : 2424;
                _config.Port = port;
                ServerConfig.Save(_config);

                _listener = new TcpListener(IPAddress.Any, port);
                _listener.Start();
                _isRunning = true;
                _acceptCts = new CancellationTokenSource();

                // 启动心跳定时器（30秒）
                _heartbeatTimer = new System.Threading.Timer(HeartbeatCallback, null, 30000, 30000);

                buttonStart.Enabled = false;
                buttonStop.Enabled = true;
                labelStatus.Text = "状态: 运行中";
                labelStatus.ForeColor = Color.Green;

                AddLog($"[服务器] 开始监听端口 {port}");

                // 启动接受客户端连接
                _ = AcceptClientsAsync();
            }
            catch (Exception ex)
            {
                AddLog($"[错误] 启动服务器失败: {ex.Message}");
                _isRunning = false;
                buttonStart.Enabled = true;
                buttonStop.Enabled = false;
                labelStatus.Text = "状态: 启动失败";
                labelStatus.ForeColor = Color.Red;
            }
        }

        /// <summary>
        /// 停止服务器
        /// </summary>
        private async void buttonStop_Click(object sender, EventArgs e)
        {
            await StopServerAsync();
        }

        private async Task StopServerAsync()
        {
            if (!_isRunning) return;

            _isRunning = false;
            _acceptCts?.Cancel();

            // 停止心跳定时器
            _heartbeatTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            _heartbeatTimer?.Dispose();
            _heartbeatTimer = null;

            // 断开所有客户端
            lock (_clientsLock)
            {
                foreach (var client in _clients.Values)
                {
                    try
                    {
                        client.Dispose();
                    }
                    catch { }
                }
                _clients.Clear();
            }

            // 停止监听
            _listener?.Stop();
            _listener = null;
            _acceptCts?.Dispose();
            _acceptCts = null;

            buttonStart.Enabled = true;
            buttonStop.Enabled = false;
            labelStatus.Text = "状态: 未启动";
            labelStatus.ForeColor = Color.Gray;

            UpdateOnlineUsersList();
            AddLog("[服务器] 服务器已停止");
        }

        /// <summary>
        /// 接受客户端连接
        /// </summary>
        private async Task AcceptClientsAsync()
        {
            if (_acceptCts == null) return;

            try
            {
                while (!_acceptCts.Token.IsCancellationRequested)
                {
                    var client = await _listener!.AcceptTcpClientAsync(_acceptCts.Token);
                    AddLog($"[连接] 新客户端连接: {((IPEndPoint)client.Client.RemoteEndPoint!).Address}");

                    // 启动处理客户端
                    _ = HandleClientAsync(client);
                }
            }
            catch (OperationCanceledException)
            {
                // 正常取消
            }
            catch (Exception ex)
            {
                if (_isRunning)
                {
                    AddLog($"[错误] 接受客户端连接失败: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 处理单个客户端
        /// </summary>
        private async Task HandleClientAsync(TcpClient tcpClient)
        {
            var clientInfo = new ClientInfo(tcpClient);
            string clientKey = "";

            try
            {
                var stream = tcpClient.GetStream();
                clientInfo.Stream = stream;

                // 读取消息循环
                var buffer = new byte[4096];
                var messageBuilder = new StringBuilder();

                while (_isRunning)
                {
                    int bytesRead = await stream.ReadAsync(buffer, clientInfo.Cts.Token);
                    if (bytesRead == 0)
                    {
                        // 客户端断开
                        AddLog($"[断开] 客户端断开连接: {clientInfo.Username ?? "未注册"}");
                        break;
                    }

                    string received = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                    messageBuilder.Append(received);

                    // 处理完整消息（按换行符分隔）
                    while (messageBuilder.Length > 0)
                    {
                        string msg = messageBuilder.ToString();
                        int newlineIndex = msg.IndexOf('\n');
                        if (newlineIndex == -1) break;

                        string message = msg.Substring(0, newlineIndex).Trim();
                        messageBuilder.Remove(0, newlineIndex + 1);

                        if (!string.IsNullOrEmpty(message))
                        {
                            ProcessMessage(clientInfo, message);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // 正常取消
            }
            catch (Exception ex)
            {
                AddLog($"[错误] 处理客户端消息失败: {clientInfo.Username ?? "未知"} - {ex.Message}");
            }
            finally
            {
                // 清理客户端
                clientKey = clientInfo.Key;
                lock (_clientsLock)
                {
                    if (!string.IsNullOrEmpty(clientKey) && _clients.ContainsKey(clientKey))
                    {
                        _clients.Remove(clientKey);
                        AddLog($"[离线] 客户端离线: {clientInfo.Username}-{clientInfo.Galaxy}");
                    }
                }

                clientInfo.Dispose();
                UpdateOnlineUsersList();
            }
        }

        /// <summary>
        /// 处理消息
        /// </summary>
        private void ProcessMessage(ClientInfo client, string json)
        {
            try
            {
                json = json.TrimStart('﻿');
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (!root.TryGetProperty("type", out var typeProp)) return;

                string messageType = typeProp.GetString() ?? "";

                switch (messageType)
                {
                    case "register":
                        HandleRegister(client, root);
                        break;

                    case "heartbeat":
                        HandleHeartbeat(client);
                        break;

                    case "alert":
                        HandleAlert(client, root);
                        break;

                    case "intel":
                        HandleClientIntel(client, root);
                        break;
                }
            }
            catch (Exception ex)
            {
                AddLog($"[警告] 解析消息失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 处理注册消息
        /// </summary>
        private void HandleRegister(ClientInfo client, JsonElement root)
        {
            string username = root.GetProperty("username").GetString() ?? "未知";
            string galaxy = root.GetProperty("galaxy").GetString() ?? "未知";

            client.Username = username;
            client.Galaxy = galaxy;
            client.Key = $"{username}-{galaxy}";

            lock (_clientsLock)
            {
                _clients[client.Key] = client;
            }

            AddLog($"[注册] 客户端注册: {username} - {galaxy}");
            UpdateOnlineUsersList();
        }

        /// <summary>
        /// 处理心跳
        /// </summary>
        private void HandleHeartbeat(ClientInfo client)
        {
            client.LastHeartbeat = DateTime.Now;
            client.MissedHeartbeats = 0;
        }

        /// <summary>
        /// 处理预警消息并广播
        /// </summary>
        private async void HandleAlert(ClientInfo client, JsonElement root)
        {
            string galaxy = root.GetProperty("galaxy").GetString() ?? "未知";

            // 构建广播消息
            var alertsArray = root.GetProperty("alerts");
            var alertsList = new List<string>();
            foreach (var alert in alertsArray.EnumerateArray())
            {
                alertsList.Add(alert.GetString() ?? "");
            }

            string message = string.Join(",", alertsList);

            var broadcastObj = new
            {
                galaxy = galaxy,
                message = message
            };

            string broadcastJson = JsonSerializer.Serialize(broadcastObj) + "\n";

            AddLog($"[预警] 收到预警: {galaxy} - {message}");

            // 广播给所有其他客户端
            await BroadcastAsync(broadcastJson, client.Key);
        }

        /// <summary>
        /// 转发客户端产出的预警频道消息给其他客户端（不回报给发送者，发送者本地已响过）。
        /// </summary>
        private async void HandleClientIntel(ClientInfo client, JsonElement root)
        {
            try
            {
                string system = GetString(root, "system") ?? GetString(root, "galaxy") ?? "未知";
                int jumps = root.TryGetProperty("jumps", out var jumpsProp) &&
                            jumpsProp.TryGetInt32(out int parsedJumps)
                    ? parsedJumps
                    : -1;
                string channel = Limit(GetString(root, "channel"), 64);
                string author = Limit(GetString(root, "author"), 64);
                string text = Limit(GetString(root, "message"), 500);
                string time = GetString(root, "time") ?? DateTime.UtcNow.ToString("O");

                var payload = new
                {
                    type = "intel",
                    galaxy = system,
                    system,
                    jumps,
                    channel,
                    author,
                    message = text,
                    time,
                };

                AddLog($"[预警频道] {client.Username}: {system} ({jumps}跳) | {channel}/{author}: {text}");
                await BroadcastAsync(JsonSerializer.Serialize(payload) + "\n", client.Key);
            }
            catch (Exception ex)
            {
                AddLog($"[警告] 处理频道预警失败: {ex.Message}");
            }
        }

        private static string? GetString(JsonElement root, string propertyName) =>
            root.TryGetProperty(propertyName, out JsonElement value) &&
            value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        private static string Limit(string? value, int maxLength)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "";
            }

            return value.Length <= maxLength ? value : value[..maxLength];
        }

        /// <summary>
        /// 广播消息
        /// </summary>
        private async Task BroadcastAsync(string message, string? excludeKey = null)
        {
            List<ClientInfo> clientsToBroadcast;

            lock (_clientsLock)
            {
                clientsToBroadcast = _clients.Values
                    .Where(c => c.Key != excludeKey && c.Stream?.CanWrite == true)
                    .ToList();
            }

            foreach (var client in clientsToBroadcast)
            {
                try
                {
                    var bytes = Encoding.UTF8.GetBytes(message);
                    await client.Stream!.WriteAsync(bytes, client.Cts.Token);
                    await client.Stream!.FlushAsync(client.Cts.Token);
                }
                catch (Exception ex)
                {
                    AddLog($"[警告] 广播失败: {client.Username} - {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 心跳检查回调
        /// </summary>
        private void HeartbeatCallback(object? state)
        {
            var clientsToRemove = new List<string>();

            lock (_clientsLock)
            {
                foreach (var client in _clients.Values)
                {
                    var timeSinceLastHeartbeat = DateTime.Now - client.LastHeartbeat;
                    if (timeSinceLastHeartbeat.TotalSeconds > 30)
                    {
                        client.MissedHeartbeats++;

                        if (client.MissedHeartbeats >= 3)
                        {
                            clientsToRemove.Add(client.Key);
                        }
                    }
                }

                // 移除超时客户端
                foreach (var key in clientsToRemove)
                {
                    if (_clients.TryGetValue(key, out var client))
                    {
                        AddLog($"[超时] 客户端心跳超时: {client.Username}-{client.Galaxy}");
                        client.Dispose();
                        _clients.Remove(key);
                    }
                }
            }

            if (clientsToRemove.Count > 0)
            {
                UpdateOnlineUsersList();
            }
        }


        /// <summary>
        /// 窗体关闭事件
        /// </summary>
        private async void Main_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (int.TryParse(textBoxPort.Text, out int port))
            {
                _config.Port = port;
            }

            ServerConfig.Save(_config);
            await StopServerAsync();
        }
    }

    /// <summary>
    /// 客户端信息
    /// </summary>
    public class ClientInfo : IDisposable
    {
        public TcpClient TcpClient { get; }
        public string Username { get; set; } = "";
        public string Galaxy { get; set; } = "";
        public string Key { get; set; } = "";
        public DateTime ConnectTime { get; }
        public DateTime LastHeartbeat { get; set; }
        public int MissedHeartbeats { get; set; }
        public NetworkStream? Stream { get; set; }
        public CancellationTokenSource Cts { get; }

        public ClientInfo(TcpClient tcpClient)
        {
            TcpClient = tcpClient;
            ConnectTime = DateTime.Now;
            LastHeartbeat = DateTime.Now;
            MissedHeartbeats = 0;
            Cts = new CancellationTokenSource();
        }

        public void Dispose()
        {
            try
            {
                Cts.Cancel();
                Stream?.Close();
                TcpClient.Close();
            }
            catch { }

            Cts.Dispose();
        }
    }
}
