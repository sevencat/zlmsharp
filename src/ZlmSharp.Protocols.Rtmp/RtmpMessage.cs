namespace ZlmSharp.Protocols.Rtmp;

/// <summary>RTMP 消息类型（RTMP 1.0 §5.4）。</summary>
public static class RtmpMessageType
{
    public const byte SetChunkSize = 1;
    public const byte Abort = 2;
    public const byte Acknowledgement = 3;
    public const byte UserControl = 4;
    public const byte WindowAcknowledgementSize = 5;
    public const byte SetPeerBandwidth = 6;
    public const byte Audio = 8;
    public const byte Video = 9;
    public const byte DataAmf0 = 18;
    public const byte SharedObjectAmf0 = 19;
    public const byte CommandAmf0 = 20;
}

/// <summary>RTMP 常量（客户端/服务端实现共用的魔数）。</summary>
public static class RtmpConstants
{
    public const byte Version = 3;

    public const int HandshakeSize = 1536;

    public const int DefaultChunkSize = 128;

    /// <summary>单条消息上限，防御性限制，避免恶意长度导致大块分配。</summary>
    public const int MaxMessageSize = 32 * 1024 * 1024;

    public const uint DefaultStreamId = 1;

    /// <summary>FLV video tag 里的 AVC codec id。</summary>
    public const byte FlvCodecAvc = 7;

    /// <summary>FLV audio tag 里的 AAC sound format。</summary>
    public const byte FlvSoundFormatAac = 10;
}

/// <summary>重组完成的一条 RTMP 消息。</summary>
public sealed record RtmpMessage(byte Type, uint StreamId, uint Timestamp, byte[] Payload)
{
    public override string ToString() => $"type={Type} stream={StreamId} ts={Timestamp} len={Payload.Length}";
}
