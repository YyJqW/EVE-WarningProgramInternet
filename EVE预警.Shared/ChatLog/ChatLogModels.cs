namespace EVE预警.ChatLog
{
    /// <summary>聊天日志文件头信息。</summary>
    public sealed record ChatLogHeader(
        string ChannelName,
        string ChannelId,
        string Listener);

    /// <summary>从聊天日志中解析出的一条消息。Timestamp 为 UTC。</summary>
    public sealed record ChatLogMessage(
        string ChannelName,
        DateTime TimestampUtc,
        string Author,
        string Text,
        string FilePath);
}
