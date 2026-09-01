using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CADMCP.Plugin;

internal static class FrameProtocol
{
    public const int MaxFrameBytes = 8 * 1024 * 1024;
    public static async Task<JObject?> ReadAsync(NetworkStream stream, CancellationToken token)
    {
        var header = new byte[4]; if (!await ReadExactAsync(stream, header, token).ConfigureAwait(false)) return null;
        var length = (header[0] << 24) | (header[1] << 16) | (header[2] << 8) | header[3];
        if (length <= 0 || length > MaxFrameBytes) throw new InvalidDataException("非法帧长度: " + length);
        var payload = new byte[length]; if (!await ReadExactAsync(stream, payload, token).ConfigureAwait(false)) throw new EndOfStreamException();
        return JObject.Parse(Encoding.UTF8.GetString(payload));
    }
    public static async Task WriteAsync(NetworkStream stream, JObject value, CancellationToken token)
    {
        var payload = Encoding.UTF8.GetBytes(value.ToString(Formatting.None));
        if (payload.Length > MaxFrameBytes) throw new InvalidDataException("响应超过最大帧长度");
        var frame = new byte[payload.Length + 4]; frame[0] = (byte)(payload.Length >> 24); frame[1] = (byte)(payload.Length >> 16); frame[2] = (byte)(payload.Length >> 8); frame[3] = (byte)payload.Length;
        Buffer.BlockCopy(payload, 0, frame, 4, payload.Length); await stream.WriteAsync(frame, 0, frame.Length, token).ConfigureAwait(false);
    }
    private static async Task<bool> ReadExactAsync(NetworkStream stream, byte[] bytes, CancellationToken token)
    {
        var offset = 0; while (offset < bytes.Length) { var read = await stream.ReadAsync(bytes, offset, bytes.Length - offset, token).ConfigureAwait(false); if (read == 0) return false; offset += read; } return true;
    }
}
