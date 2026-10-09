using System;
using System.Buffers;

namespace Runic.CommandLine;

internal sealed class CommandJsonBufferWriter : IBufferWriter<byte>
{
    private readonly ArrayBufferWriter<byte> _inner = new();
    private readonly int _maximumBytes;
    private readonly string _errorKind;
    private readonly string _errorMessage;

    internal CommandJsonBufferWriter(int maximumBytes, string errorKind, string errorMessage)
    {
        _maximumBytes = maximumBytes;
        _errorKind = errorKind;
        _errorMessage = errorMessage;
    }

    internal int WrittenCount => _inner.WrittenCount;
    internal ReadOnlySpan<byte> WrittenSpan => _inner.WrittenSpan;

    public void Advance(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (count > _maximumBytes - _inner.WrittenCount) throw TooLarge();
        _inner.Advance(count);
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        ValidateSizeHint(sizeHint);
        return _inner.GetMemory(Math.Max(sizeHint, 1));
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        ValidateSizeHint(sizeHint);
        return _inner.GetSpan(Math.Max(sizeHint, 1));
    }

    private void ValidateSizeHint(int sizeHint)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sizeHint);
        // Utf8JsonWriter reserves the worst-case escaped size of a string before
        // it knows the actual byte count. Bound scratch space independently and
        // enforce the exact serialized-byte limit in Advance.
        if (sizeHint > (long)_maximumBytes * 6 + 256) throw TooLarge();
    }

    private CommandProtocolException TooLarge() => new(_errorKind, _errorMessage);
}
