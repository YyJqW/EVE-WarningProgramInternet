using NAudio.Wave;

namespace EVE预警.Audio
{
    /// <summary>
    /// 告警音频播放：优先播放用户指定的文件（WAV/MP3/AIFF 等），
    /// 未指定或解码失败时使用内置提示音。支持音量、循环时长与手动停止。
    /// </summary>
    public sealed class AlertSoundPlayer : IDisposable
    {
        private static readonly TimeSpan GlobalCooldown = TimeSpan.FromMilliseconds(200);

        private readonly object _gate = new();
        private WaveOutEvent? _output;
        private WaveStream? _stream;
        private System.Threading.Timer? _loopTimer;
        private int _generation;
        private DateTime _lastPlayUtc = DateTime.MinValue;
        private bool _disposed;

        public string? LastError { get; private set; }

        /// <summary>当前是否有告警音在播放（含循环中）。</summary>
        public bool IsPlaying
        {
            get
            {
                lock (_gate)
                {
                    return _output != null;
                }
            }
        }

        /// <summary>
        /// 播放告警音。loopDuration 大于 0 时循环播放，直到手动停止或时长结束。
        /// volume 取值 0-1。
        /// </summary>
        public void Play(string? customPath, float volume = 1f, TimeSpan loopDuration = default)
        {
            _ = Task.Run(() => PlayCore(customPath, volume, loopDuration, applyCooldown: true));
        }

        /// <summary>设置窗口「试听」使用：忽略全局冷却，单次播放，并立即停止当前声音。</summary>
        public void Preview(string? customPath, float volume = 1f)
        {
            _ = Task.Run(() => PlayCore(customPath, volume, TimeSpan.Zero, applyCooldown: false));
        }

        /// <summary>立即停止当前告警（主界面「停止警报」按钮）。</summary>
        public void Stop()
        {
            lock (_gate)
            {
                _generation++;
                StopAndDisposeLocked();
            }
        }

        /// <summary>设置窗口「浏览」后的校验。</summary>
        public static bool TryValidate(string? path, out string? error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(path))
            {
                error = "未指定音频文件";
                return false;
            }

            if (!File.Exists(path))
            {
                error = "文件不存在";
                return false;
            }

            try
            {
                using var reader = new AudioFileReader(path);
                return true;
            }
            catch (Exception ex)
            {
                error = $"无法解码该音频: {ex.Message}";
                return false;
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _generation++;
                StopAndDisposeLocked();
            }
        }

        private void PlayCore(string? customPath, float volume, TimeSpan loopDuration, bool applyCooldown)
        {
            float clampedVolume = Math.Clamp(volume, 0f, 1f);

            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                if (applyCooldown)
                {
                    DateTime now = DateTime.UtcNow;
                    if (now - _lastPlayUtc < GlobalCooldown)
                    {
                        return;
                    }

                    _lastPlayUtc = now;
                }

                StopAndDisposeLocked();
                int generation = ++_generation;

                try
                {
                    WaveStream source = CreateSource(customPath);
                    WaveStream playable = loopDuration > TimeSpan.Zero
                        ? new LoopStream(source)
                        : source;

                    var output = new WaveOutEvent();
                    output.Init(playable);
                    try
                    {
                        output.Volume = clampedVolume;
                    }
                    catch
                    {
                        // 部分声卡不支持软件音量，忽略
                    }

                    output.PlaybackStopped += (_, _) => OnPlaybackStopped(output, playable, generation);
                    _output = output;
                    _stream = playable;

                    if (loopDuration > TimeSpan.Zero)
                    {
                        _loopTimer = new System.Threading.Timer(
                            _ => StopGeneration(generation),
                            null,
                            loopDuration,
                            Timeout.InfiniteTimeSpan);
                    }

                    output.Play();
                    LastError = null;
                }
                catch (Exception ex)
                {
                    LastError = $"播放音频失败: {ex.Message}";
                    StopAndDisposeLocked();
                }
            }
        }

        private WaveStream CreateSource(string? customPath)
        {
            if (!string.IsNullOrWhiteSpace(customPath) && File.Exists(customPath))
            {
                try
                {
                    return new AudioFileReader(customPath);
                }
                catch (Exception ex)
                {
                    LastError = $"播放自定义音频失败，使用内置提示音: {ex.Message}";
                }
            }

            return CreateDefaultTone();
        }

        /// <summary>
        /// 内置提示音：880Hz 短促蜂鸣（0.35 秒，带淡入淡出）。
        /// 生成 PCM 数据而不是调用 SystemSounds，这样音量与循环都可控。
        /// </summary>
        private static WaveStream CreateDefaultTone()
        {
            const int sampleRate = 44100;
            const double seconds = 0.35;
            int samples = (int)(sampleRate * seconds);
            var buffer = new byte[samples * 2];
            double fadeSamples = sampleRate * 0.01;

            for (int i = 0; i < samples; i++)
            {
                double t = (double)i / sampleRate;
                double fade = Math.Min(1.0, Math.Min(i / fadeSamples, (samples - i) / fadeSamples));
                short value = (short)(Math.Sin(2 * Math.PI * 880 * t) * 12000 * fade);
                buffer[i * 2] = (byte)(value & 0xFF);
                buffer[i * 2 + 1] = (byte)((value >> 8) & 0xFF);
            }

            return new RawSourceWaveStream(
                new MemoryStream(buffer),
                new WaveFormat(sampleRate, 16, 1));
        }

        private void StopGeneration(int generation)
        {
            lock (_gate)
            {
                if (generation != _generation)
                {
                    return;
                }

                StopAndDisposeLocked();
            }
        }

        private void OnPlaybackStopped(WaveOutEvent output, WaveStream stream, int generation)
        {
            lock (_gate)
            {
                if (generation == _generation && ReferenceEquals(_output, output))
                {
                    _output = null;
                    _stream = null;
                    _loopTimer?.Dispose();
                    _loopTimer = null;
                }
            }

            try
            {
                output.Dispose();
            }
            catch
            {
                // 忽略释放异常
            }

            try
            {
                stream.Dispose();
            }
            catch
            {
                // 忽略释放异常
            }
        }

        private void StopAndDisposeLocked()
        {
            _loopTimer?.Dispose();
            _loopTimer = null;

            WaveOutEvent? output = _output;
            WaveStream? stream = _stream;
            _output = null;
            _stream = null;

            try
            {
                output?.Stop();
            }
            catch
            {
                // 忽略停止异常
            }

            try
            {
                output?.Dispose();
            }
            catch
            {
                // 忽略释放异常
            }

            try
            {
                stream?.Dispose();
            }
            catch
            {
                // 忽略释放异常
            }
        }

        /// <summary>把任意 WaveStream 变成无限循环流；停止由外部计时器负责。</summary>
        private sealed class LoopStream : WaveStream
        {
            private readonly WaveStream _source;

            public LoopStream(WaveStream source)
            {
                _source = source;
            }

            public override WaveFormat WaveFormat => _source.WaveFormat;

            public override long Length => long.MaxValue;

            public override long Position
            {
                get => _source.Position;
                set => _source.Position = value;
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                int total = 0;
                while (total < count)
                {
                    int read = _source.Read(buffer, offset + total, count - total);
                    if (read == 0)
                    {
                        if (_source.Position == 0)
                        {
                            break;
                        }

                        _source.Position = 0;
                        continue;
                    }

                    total += read;
                }

                return total;
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _source.Dispose();
                }

                base.Dispose(disposing);
            }
        }
    }
}
