namespace EVE预警.Intel
{
    /// <summary>服务器广播的预警频道消息（客户端消费用）。</summary>
    public sealed record IntelAlertPayload(
        string Galaxy,
        string System,
        int Jumps,
        string Channel,
        string Author,
        string Text,
        DateTime TimestampUtc);
}
