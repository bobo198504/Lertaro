using System.Text;
using Lertaro.Core.Extensions;

namespace Lertaro.Core.Wire;

public static class PipeRequestBinarySerializer
{
    private const int Magic = 0x51504C53; // SLPQ

    private const int VersionString = 1;
    private const int VersionIpc = 3;

    public static Task WriteStringAsync(Stream stream, string command, CancellationToken token = default)
    {
        using var payload = new MemoryStream();
        using (var writer = new BinaryWriter(payload, Encoding.UTF8, leaveOpen: true))
            writer.Write(command ?? string.Empty);
        return WriteFrameAsync(stream, VersionString, payload.ToArray(), token);
    }

    public static async Task<string> ReadStringAsync(Stream stream, CancellationToken token = default)
    {
        var payload = await ReadFrameAsync(stream, VersionString, token).ConfigureAwait(false);
        using var ms = new MemoryStream(payload);
        using var reader = new BinaryReader(ms, Encoding.UTF8);
        return reader.ReadString();
    }

    public static Task WriteMessageAsync(Stream stream, IpcMessage msg, CancellationToken token = default)
    {
        using var payload = new MemoryStream();
        using (var writer = new BinaryWriter(payload, Encoding.UTF8, leaveOpen: true))
            IpcMessagePayloadCodec.WritePayload(writer, msg);
        return WriteFrameAsync(stream, VersionIpc, payload.ToArray(), token);
    }

    public static async Task<IpcMessage> ReadMessageAsync(Stream stream, CancellationToken token = default)
    {
        var payload = await ReadFrameAsync(stream, VersionIpc, token).ConfigureAwait(false);
        using var ms = new MemoryStream(payload);
        using var reader = new BinaryReader(ms, Encoding.UTF8);
        return IpcMessagePayloadCodec.ReadPayload(reader);
    }

    private static async Task WriteFrameAsync(Stream stream, int version, byte[] payload, CancellationToken token)
    {
        using var frame = new MemoryStream();
        using (var writer = new BinaryWriter(frame, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(Magic);
            writer.Write(version);
            writer.Write(payload.Length);
            writer.Write(payload);
        }

        await stream.WriteAsync(frame.ToArray(), token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadFrameAsync(Stream stream, int expectedVersion, CancellationToken token)
    {
        var magic = await stream.ReadInt32Async(token).ConfigureAwait(false);
        if (magic != Magic)
            throw new InvalidDataException("Invalid pipe request binary header.");
        var version = await stream.ReadInt32Async(token).ConfigureAwait(false);
        if (version != expectedVersion)
            throw new InvalidDataException($"Unsupported pipe request version: {version}.");
        var length = await stream.ReadInt32Async(token).ConfigureAwait(false);
        if (length < 0 || length > 10 * 1024 * 1024)
            throw new InvalidDataException($"Invalid IPC payload length: {length}");
        return await stream.ReadExactlyAsync(length, token).ConfigureAwait(false);
}
}
