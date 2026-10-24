using EVE预警.Audio;
using EVE预警.ChatLog;
using EVE预警.Intel;

namespace EVE预警
{
    /// <summary>设置窗口按下「确定」后返回的结果。</summary>
    public sealed class IntelAlertSettingsResult
    {
        public bool Enabled { get; set; }
        public string LogDirectory { get; set; } = "";
        public string HomeSystem { get; set; } = "";
        public int JumpRange { get; set; }
        public List<string> Channels { get; set; } = new();
        public string LocalSoundPath { get; set; } = "";
        public int LocalVolume { get; set; } = 100;
        public string IntelSoundPath { get; set; } = "";
        public int IntelVolume { get; set; } = 100;
        public int LoopSeconds { get; set; } = 30;
        public int CooldownSeconds { get; set; } = 60;
    }

    /// <summary>模拟预警请求：携带设置窗口中尚未保存的参数。</summary>
    public sealed record IntelSimulationRequest(
        string HomeSystem,
        int JumpRange,
        IReadOnlyList<string> Channels);

    /// <summary>
    /// 「预警频道设置」对话框，按 CropSelectForm 的方式纯代码构建控件。
    /// 本地色块预警与频道预警使用两套独立的音频文件和音量。
    /// </summary>
    public sealed class IntelAlertSettingsForm : Form
    {
        private readonly CheckBox _checkEnabled;
        private readonly TextBox _textDirectory;
        private readonly Button _buttonDetect;
        private readonly Button _buttonBrowseDirectory;
        private readonly TextBox _textHomeSystem;
        private readonly NumericUpDown _numericJumps;
        private readonly CheckedListBox _checkedChannels;
        private readonly Button _buttonRefreshChannels;
        private readonly TextBox _textLocalSoundPath;
        private readonly Button _buttonBrowseLocalSound;
        private readonly Button _buttonPreviewLocalSound;
        private readonly NumericUpDown _numericLocalVolume;
        private readonly TextBox _textIntelSoundPath;
        private readonly Button _buttonBrowseIntelSound;
        private readonly Button _buttonPreviewIntelSound;
        private readonly NumericUpDown _numericIntelVolume;
        private readonly NumericUpDown _numericLoopSeconds;
        private readonly NumericUpDown _numericCooldown;
        private readonly TextBox _textStatus;
        private readonly Button _buttonSimulate;
        private readonly Button _buttonOk;
        private readonly Button _buttonCancel;

        private readonly AlertSoundPlayer _previewPlayer = new();
        private readonly HashSet<string> _initialChannels;

        public IntelAlertSettingsForm(
            bool enabled,
            string logDirectory,
            string homeSystem,
            int jumpRange,
            IEnumerable<string> channels,
            string localSoundPath,
            int localVolume,
            string intelSoundPath,
            int intelVolume,
            int loopSeconds,
            int cooldownSeconds)
        {
            _initialChannels = new HashSet<string>(
                channels.Where(c => !string.IsNullOrWhiteSpace(c)),
                StringComparer.OrdinalIgnoreCase);

            MapData.EnsureLoaded();

            Text = "预警与音频设置";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(660, 640);
            Font = new Font("Microsoft YaHei UI", 9F);

            _checkEnabled = new CheckBox
            {
                AutoSize = true,
                Location = new Point(12, 14),
                Text = "启用聊天日志预警频道",
                Checked = enabled,
            };

            _textDirectory = new TextBox
            {
                Location = new Point(100, 46),
                Size = new Size(380, 27),
                Text = logDirectory,
            };

            _buttonDetect = new Button
            {
                Location = new Point(490, 45),
                Size = new Size(88, 29),
                Text = "自动检测",
            };
            _buttonDetect.Click += (_, _) => DetectDirectory();

            _buttonBrowseDirectory = new Button
            {
                Location = new Point(584, 45),
                Size = new Size(64, 29),
                Text = "浏览",
            };
            _buttonBrowseDirectory.Click += (_, _) => BrowseDirectory();

            _textHomeSystem = new TextBox
            {
                Location = new Point(100, 86),
                Size = new Size(150, 27),
                Text = homeSystem,
            };
            ConfigureAutoComplete(_textHomeSystem);

            _numericJumps = new NumericUpDown
            {
                Location = new Point(350, 86),
                Size = new Size(60, 27),
                Minimum = 0,
                Maximum = 15,
                Value = Math.Clamp(jumpRange, 0, 15),
            };

            _checkedChannels = new CheckedListBox
            {
                Location = new Point(100, 126),
                Size = new Size(410, 120),
                CheckOnClick = true,
                IntegralHeight = false,
            };

            _buttonRefreshChannels = new Button
            {
                Location = new Point(520, 125),
                Size = new Size(128, 29),
                Text = "刷新频道",
            };
            _buttonRefreshChannels.Click += (_, _) => RefreshChannels();

            // ---- 本地色块预警音频 ----
            Label localTitle = CreateSectionLabel("本地色块预警音频（红/白等图标）", 12, 252);

            _textLocalSoundPath = new TextBox
            {
                Location = new Point(100, 276),
                Size = new Size(260, 27),
                Text = localSoundPath,
            };

            _buttonBrowseLocalSound = new Button
            {
                Location = new Point(366, 275),
                Size = new Size(70, 29),
                Text = "浏览",
            };
            _buttonBrowseLocalSound.Click += (_, _) => BrowseSound(_textLocalSoundPath);

            _numericLocalVolume = new NumericUpDown
            {
                Location = new Point(100, 310),
                Size = new Size(60, 27),
                Minimum = 0,
                Maximum = 100,
                Value = Math.Clamp(localVolume, 0, 100),
            };

            _buttonPreviewLocalSound = new Button
            {
                Location = new Point(442, 275),
                Size = new Size(70, 29),
                Text = "试听",
            };
            _buttonPreviewLocalSound.Click += (_, _) =>
                PreviewSound(_textLocalSoundPath, _numericLocalVolume);

            // ---- 频道预警音频 ----
            Label intelTitle = CreateSectionLabel("频道预警音频（本地频道 + 服务器广播）", 12, 344);

            _textIntelSoundPath = new TextBox
            {
                Location = new Point(100, 368),
                Size = new Size(260, 27),
                Text = intelSoundPath,
            };

            _buttonBrowseIntelSound = new Button
            {
                Location = new Point(366, 367),
                Size = new Size(70, 29),
                Text = "浏览",
            };
            _buttonBrowseIntelSound.Click += (_, _) => BrowseSound(_textIntelSoundPath);

            _numericIntelVolume = new NumericUpDown
            {
                Location = new Point(100, 402),
                Size = new Size(60, 27),
                Minimum = 0,
                Maximum = 100,
                Value = Math.Clamp(intelVolume, 0, 100),
            };

            _buttonPreviewIntelSound = new Button
            {
                Location = new Point(442, 367),
                Size = new Size(70, 29),
                Text = "试听",
            };
            _buttonPreviewIntelSound.Click += (_, _) =>
                PreviewSound(_textIntelSoundPath, _numericIntelVolume);

            _numericCooldown = new NumericUpDown
            {
                Location = new Point(100, 438),
                Size = new Size(60, 27),
                Minimum = 5,
                Maximum = 600,
                Value = Math.Clamp(cooldownSeconds, 5, 600),
            };

            _numericLoopSeconds = new NumericUpDown
            {
                Location = new Point(100, 470),
                Size = new Size(60, 27),
                Minimum = 0,
                Maximum = 600,
                Value = Math.Clamp(loopSeconds, 0, 600),
            };

            _textStatus = new TextBox
            {
                Location = new Point(100, 500),
                Size = new Size(548, 84),
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BackColor = SystemColors.Window,
            };

            _buttonSimulate = new Button
            {
                Location = new Point(100, 596),
                Size = new Size(150, 29),
                Text = "模拟一条预警消息",
            };
            _buttonSimulate.Click += (_, _) => SimulateMessage();

            _buttonOk = new Button
            {
                Location = new Point(390, 596),
                Size = new Size(120, 29),
                Text = "确定",
                DialogResult = DialogResult.None,
            };
            _buttonOk.Click += (_, _) => Confirm();

            _buttonCancel = new Button
            {
                Location = new Point(520, 596),
                Size = new Size(120, 29),
                Text = "取消",
                DialogResult = DialogResult.Cancel,
            };

            Controls.AddRange(new Control[]
            {
                _checkEnabled,
                CreateLabel("日志目录:", 12, 49),
                _textDirectory,
                _buttonDetect,
                _buttonBrowseDirectory,
                CreateLabel("所在星系:", 12, 89),
                _textHomeSystem,
                CreateLabel("警报跳数:", 260, 89),
                _numericJumps,
                CreateLabel("跳", 416, 89),
                CreateLabel("监控频道:", 12, 129),
                _checkedChannels,
                _buttonRefreshChannels,
                localTitle,
                CreateLabel("音频文件:", 12, 279),
                _textLocalSoundPath,
                _buttonBrowseLocalSound,
                _buttonPreviewLocalSound,
                CreateLabel("音量:", 12, 313),
                _numericLocalVolume,
                CreateLabel("%", 166, 313),
                intelTitle,
                CreateLabel("音频文件:", 12, 371),
                _textIntelSoundPath,
                _buttonBrowseIntelSound,
                _buttonPreviewIntelSound,
                CreateLabel("音量:", 12, 405),
                _numericIntelVolume,
                CreateLabel("%", 166, 405),
                CreateLabel("告警冷却:", 12, 441),
                _numericCooldown,
                CreateLabel("秒", 166, 441),
                CreateLabel("警报循环时长:", 12, 473),
                _numericLoopSeconds,
                CreateLabel("秒（0=只播一次）", 166, 473),
                CreateLabel("状态:", 12, 503),
                _textStatus,
                _buttonSimulate,
                _buttonOk,
                _buttonCancel,
            });

            AcceptButton = _buttonOk;
            CancelButton = _buttonCancel;

            Shown += (_, _) =>
            {
                if (MapData.LoadError != null)
                {
                    AppendStatus(MapData.LoadError);
                }

                if (string.IsNullOrWhiteSpace(_textDirectory.Text))
                {
                    DetectDirectory();
                }

                RefreshChannels();
            };
        }

        /// <summary>由主窗口订阅：请求用当前设置模拟一条消息，验证整条链路。</summary>
        public event Action<IntelSimulationRequest>? SimulateRequested;

        /// <summary>用户取消或未确认时为 null。</summary>
        public IntelAlertSettingsResult? Result { get; private set; }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _previewPlayer.Dispose();
            }

            base.Dispose(disposing);
        }

        private static Label CreateLabel(string text, int x, int y) =>
            new()
            {
                AutoSize = true,
                Location = new Point(x, y),
                Text = text,
            };

        private static Label CreateSectionLabel(string text, int x, int y) =>
            new()
            {
                AutoSize = true,
                Location = new Point(x, y),
                Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
                Text = text,
            };

        private void ConfigureAutoComplete(TextBox textBox)
        {
            string[] names = MapData.AllNames;
            if (names.Length == 0)
            {
                return;
            }

            var source = new AutoCompleteStringCollection();
            source.AddRange(names);
            textBox.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
            textBox.AutoCompleteSource = AutoCompleteSource.CustomSource;
            textBox.AutoCompleteCustomSource = source;
        }

        private void DetectDirectory()
        {
            string? found = ChatLogLocator.FindExisting(_textDirectory.Text);
            if (found == null)
            {
                AppendStatus("未能自动找到 Chatlogs 目录，请手动选择。");
                return;
            }

            _textDirectory.Text = found;
            AppendStatus($"已检测到日志目录: {found}");
            RefreshChannels();
        }

        private void BrowseDirectory()
        {
            using var dialog = new FolderBrowserDialog
            {
                Description = "选择 EVE 客户端的 Chatlogs 目录",
                UseDescriptionForTitle = true,
                SelectedPath = Directory.Exists(_textDirectory.Text)
                    ? _textDirectory.Text
                    : "",
            };

            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                _textDirectory.Text = dialog.SelectedPath;
                RefreshChannels();
            }
        }

        private void RefreshChannels()
        {
            string directory = _textDirectory.Text.Trim();
            var checkedBefore = new HashSet<string>(
                _checkedChannels.CheckedItems.Cast<string>(),
                StringComparer.OrdinalIgnoreCase);

            _checkedChannels.Items.Clear();

            IReadOnlyList<ChatLogFileInfo> files = ChatLogDirectoryScanner.Scan(directory);
            var channels = files
                .Where(f => f.Header != null)
                .Select(f => f.Header!.ChannelName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // 配置中存在但当前未发现的频道也保留，避免设置丢失
            foreach (string channel in _initialChannels)
            {
                if (!channels.Contains(channel, StringComparer.OrdinalIgnoreCase))
                {
                    channels.Add(channel);
                }
            }

            foreach (string channel in channels)
            {
                bool shouldCheck =
                    checkedBefore.Contains(channel) ||
                    _initialChannels.Contains(channel) ||
                    LooksLikeIntelChannel(channel);
                _checkedChannels.Items.Add(channel, shouldCheck);
            }

            int parsed = files.Count(f => f.Header != null);
            AppendStatus($"扫描到 {files.Count} 个日志文件，识别出 {parsed} 个频道。");
            if (files.Count > 0 && parsed == 0)
            {
                AppendStatus("未能识别频道头：请确认选择的是 Chatlogs 目录，或日志格式与预期不同。");
            }
        }

        private static bool LooksLikeIntelChannel(string channel) =>
            channel.Contains("intel", StringComparison.OrdinalIgnoreCase) ||
            channel.Contains("预警", StringComparison.OrdinalIgnoreCase) ||
            channel.Contains("情报", StringComparison.OrdinalIgnoreCase);

        private void BrowseSound(TextBox target)
        {
            using var dialog = new OpenFileDialog
            {
                Title = "选择告警音频文件",
                Filter = "音频文件|*.wav;*.mp3;*.aiff;*.aif;*.wma;*.m4a|所有文件|*.*",
                CheckFileExists = true,
            };

            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                target.Text = dialog.FileName;
            }
        }

        private void PreviewSound(TextBox pathBox, NumericUpDown volumeBox)
        {
            string path = pathBox.Text.Trim();
            int volume = (int)volumeBox.Value;

            if (path.Length > 0 && !AlertSoundPlayer.TryValidate(path, out string? error))
            {
                AppendStatus($"试听失败: {error}");
                MessageBox.Show(this, $"无法播放该音频:\n{error}", "试听失败",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            _previewPlayer.Preview(path.Length == 0 ? null : path, volume / 100f);
            AppendStatus(path.Length == 0
                ? $"已播放系统提示音（音量 {volume}%；系统音的实际音量由 Windows 音量控制）"
                : $"正在试听: {path}（音量 {volume}%）");
        }

        private void SimulateMessage()
        {
            string home = _textHomeSystem.Text.Trim();
            if (MapData.ResolveSystem(home) == null)
            {
                MessageBox.Show(this, "所在星系无法识别，无法模拟。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (_checkedChannels.CheckedItems.Count == 0)
            {
                MessageBox.Show(this, "请至少勾选一个监控频道。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            SimulateRequested?.Invoke(new IntelSimulationRequest(
                home,
                (int)_numericJumps.Value,
                _checkedChannels.CheckedItems.Cast<string>().ToList()));
            AppendStatus($"已模拟一条提到「{home}」的频道消息。");
        }

        private void Confirm()
        {
            var result = new IntelAlertSettingsResult
            {
                Enabled = _checkEnabled.Checked,
                LogDirectory = _textDirectory.Text.Trim(),
                HomeSystem = _textHomeSystem.Text.Trim(),
                JumpRange = (int)_numericJumps.Value,
                Channels = _checkedChannels.CheckedItems.Cast<string>().ToList(),
                LocalSoundPath = _textLocalSoundPath.Text.Trim(),
                LocalVolume = (int)_numericLocalVolume.Value,
                IntelSoundPath = _textIntelSoundPath.Text.Trim(),
                IntelVolume = (int)_numericIntelVolume.Value,
                LoopSeconds = (int)_numericLoopSeconds.Value,
                CooldownSeconds = (int)_numericCooldown.Value,
            };

            if (result.Enabled)
            {
                if (string.IsNullOrWhiteSpace(result.LogDirectory) ||
                    !Directory.Exists(result.LogDirectory))
                {
                    ShowValidation("日志目录不存在，请重新选择。");
                    return;
                }

                if (MapData.ResolveSystem(result.HomeSystem) == null)
                {
                    ShowValidation(
                        MapData.LoadError ??
                        $"无法识别星系「{result.HomeSystem}」，请使用自动补全中的名称。");
                    return;
                }

                if (result.Channels.Count == 0)
                {
                    ShowValidation("请至少勾选一个要监控的频道。");
                    return;
                }
            }

            if (!string.IsNullOrWhiteSpace(result.LocalSoundPath) &&
                !AlertSoundPlayer.TryValidate(result.LocalSoundPath, out string? localError))
            {
                ShowValidation($"本地色块预警音频不可用: {localError}");
                return;
            }

            if (!string.IsNullOrWhiteSpace(result.IntelSoundPath) &&
                !AlertSoundPlayer.TryValidate(result.IntelSoundPath, out string? intelError))
            {
                ShowValidation($"频道预警音频不可用: {intelError}");
                return;
            }

            Result = result;
            DialogResult = DialogResult.OK;
            Close();
        }

        private void ShowValidation(string message)
        {
            MessageBox.Show(this, message, "请检查设置",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private void AppendStatus(string message)
        {
            string line = $"[{DateTime.Now:HH:mm:ss}] {message}";
            _textStatus.AppendText(_textStatus.TextLength == 0 ? line : Environment.NewLine + line);
        }
    }
}
