using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace EVE预警.ChatLog
{
    /// <summary>
    /// EVE 聊天日志解析。日志为 UTF-16LE，消息行格式：
    /// [ 2024.01.01 12:00:00 ] 角色名 > 消息内容
    /// 兼容「每行带 BOM」和「仅文件头带 BOM」两种写法。
    /// </summary>
    public static class ChatLogParser
    {
        private static readonly Regex MessageRegex = new(
            @"^\[\s(?<date>\d{4}\.\d{2}\.\d{2} \d{2}:\d{2}:\d{2})\s\]\s(?<author>[^>]+?)\s>\s?(?<message>.*)$",
            RegexOptions.Compiled);

        private static readonly string[] TimestampFormats = { "yyyy.MM.dd HH:mm:ss" };

        public static Encoding DetectEncoding(ReadOnlySpan<byte> head)
        {
            if (head.Length >= 3 &&
                head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF)
            {
                return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            }

            if (head.Length >= 2 && head[0] == 0xFF && head[1] == 0xFE)
            {
                return Encoding.Unicode;
            }

            if (head.Length >= 2 && head[0] == 0xFE && head[1] == 0xFF)
            {
                return Encoding.BigEndianUnicode;
            }

            // 无 BOM 时用零字节分布判断 UTF-16LE
            int samples = 0;
            int zeroAtOdd = 0;
            for (int i = 0; i + 1 < head.Length && samples < 200; i += 2, samples++)
            {
                if (head[i + 1] == 0)
                {
                    zeroAtOdd++;
                }
            }

            if (samples > 20 && zeroAtOdd > samples * 0.6)
            {
                return Encoding.Unicode;
            }

            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        }

        /// <summary>从文件头部若干行解析频道信息。</summary>
        public static ChatLogHeader? ParseHeader(IEnumerable<string> lines)
        {
            bool inHeader = false;
            int dividerCount = 0;
            string? channelName = null;
            string? channelId = null;
            string? listener = null;

            foreach (string rawLine in lines)
            {
                string line = rawLine.TrimEnd('\r').TrimStart('﻿');
                if (IsDivider(line))
                {
                    dividerCount++;
                    inHeader = dividerCount == 1;
                    if (dividerCount >= 2)
                    {
                        break;
                    }

                    continue;
                }

                if (!inHeader)
                {
                    continue;
                }

                int colon = line.IndexOf(':');
                if (colon <= 0)
                {
                    continue;
                }

                string key = line[..colon].Trim();
                string value = line[(colon + 1)..].Trim();
                switch (key)
                {
                    case "Channel Name":
                        channelName = value;
                        break;
                    case "Channel ID":
                        channelId = value;
                        break;
                    case "Listener":
                        listener = value;
                        break;
                }
            }

            if (string.IsNullOrWhiteSpace(channelName))
            {
                return null;
            }

            return new ChatLogHeader(
                channelName.Trim(),
                channelId?.Trim() ?? "",
                listener?.Trim() ?? "");
        }

        public static bool TryParseLine(string line, out ChatLogMessage message)
        {
            message = null!;
            if (string.IsNullOrEmpty(line))
            {
                return false;
            }

            string normalized = line.TrimStart('﻿').TrimEnd('\r');
            Match match = MessageRegex.Match(normalized);
            if (!match.Success)
            {
                return false;
            }

            if (!DateTime.TryParseExact(
                    match.Groups["date"].Value,
                    TimestampFormats,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out DateTime timestampUtc))
            {
                return false;
            }

            message = new ChatLogMessage(
                ChannelName: "",
                TimestampUtc: timestampUtc,
                Author: match.Groups["author"].Value.Trim(),
                Text: match.Groups["message"].Value,
                FilePath: "");
            return true;
        }

        private static bool IsDivider(string line)
        {
            string trimmed = line.Trim();
            if (trimmed.Length < 10)
            {
                return false;
            }

            foreach (char c in trimmed)
            {
                if (c != '-')
                {
                    return false;
                }
            }

            return true;
        }
    }
}
