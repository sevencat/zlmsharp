namespace ZlmSharp.Codecs;

/// <summary>
/// 增量 Annex-B 拆分器：适用于 TCP/文件分片输入（起始码可能跨包）。
/// 每次 <see cref="TryRead"/> 返回一个完整 NALU（不含起始码）。
/// </summary>
public sealed class AnnexBSplitter
{
    private byte[] _buffer = new byte[16 * 1024];
    private int _length;
    private int _pendingStart = -1;

    public int BufferedBytes => _length;

    public void Append(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return;
        }

        EnsureCapacity(_length + data.Length);
        data.CopyTo(_buffer.AsSpan(_length));
        _length += data.Length;
    }

    public bool TryRead(out byte[]? nalUnit)
    {
        nalUnit = null;

        while (true)
        {
            if (_pendingStart < 0)
            {
                if (!AnnexB.TryFindStartCode(_buffer.AsSpan(0, _length), 0, out var codeIndex, out var codeLength))
                {
                    KeepTail(3);
                    return false;
                }

                _pendingStart = codeIndex + codeLength;
            }

            var span = _buffer.AsSpan(0, _length);
            if (!AnnexB.TryFindStartCode(span, _pendingStart, out var nextIndex, out var nextLength))
            {
                // 还没等到下一个起始码：丢掉 NALU 之前的垃圾数据，等待更多输入
                Compact(_pendingStart);
                return false;
            }

            var end = nextIndex;
            var nextCodeStart = nextIndex;
            if (nextLength == 3 && nextIndex > _pendingStart && span[nextIndex - 1] == 0)
            {
                end = nextIndex - 1;
                nextCodeStart = nextIndex - 1;
            }

            if (end > _pendingStart)
            {
                nalUnit = span[_pendingStart..end].ToArray();
                Compact(nextCodeStart);
                _pendingStart = -1;
                return true;
            }

            // 连续起始码（空 NALU）：跳过
            Compact(nextCodeStart);
            _pendingStart = -1;
        }
    }

    /// <summary>读取当前已完整的 NALU。</summary>
    public List<byte[]> ReadAll()
    {
        var result = new List<byte[]>();
        while (TryRead(out var nal))
        {
            result.Add(nal!);
        }

        return result;
    }

    /// <summary>流结束时调用：把最后一个没有后继起始码的 NALU 也吐出来。</summary>
    public List<byte[]> Flush()
    {
        var result = ReadAll();

        if (_pendingStart >= 0 && _length > _pendingStart)
        {
            var tail = _buffer.AsSpan(_pendingStart, _length - _pendingStart);
            if (tail.Length > 0 && tail[^1] == 0)
            {
                tail = tail[..^1];
            }

            if (!tail.IsEmpty)
            {
                result.Add(tail.ToArray());
            }
        }

        Reset();
        return result;
    }

    public void Reset()
    {
        _length = 0;
        _pendingStart = -1;
    }

    private void EnsureCapacity(int required)
    {
        if (required <= _buffer.Length)
        {
            return;
        }

        var size = _buffer.Length;
        while (size < required)
        {
            size *= 2;
        }

        Array.Resize(ref _buffer, size);
    }

    private void Compact(int from)
    {
        if (from <= 0)
        {
            return;
        }

        if (from >= _length)
        {
            // 数据恰好被完整消费：如果 NALU 内容起点在末尾，保持 pending=0 等待后续数据
            _length = 0;
            _pendingStart = _pendingStart >= from ? 0 : -1;
            return;
        }

        Buffer.BlockCopy(_buffer, from, _buffer, 0, _length - from);
        _length -= from;
        _pendingStart = _pendingStart >= from ? _pendingStart - from : -1;
    }

    private void KeepTail(int count)
    {
        if (_length <= count)
        {
            return;
        }

        Compact(_length - count);
    }
}
