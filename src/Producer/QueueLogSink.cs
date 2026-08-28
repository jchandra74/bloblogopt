using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Azure.Storage.Queues;
using Serilog.Core;
using Serilog.Events;

namespace Producer;

public sealed class QueueLogSink(QueueClient queue) : IBatchedLogEventSink
{
    public static long SentLines; // ponytail: crude backpressure counter read by LoadGenerator

    static readonly string Src = Guid.NewGuid().ToString("N")[..8];
    long _seq;
    const int RawCap = 128 * 1024; // ponytail: raw-JSON cap sized so gzip+base64 stays under the 48 KiB queue limit

    public async Task EmitBatchAsync(IReadOnlyCollection<LogEvent> batch)
    {
        var sends = new List<Task>();
        var lines = new List<string>();
        int size = 0;
        foreach (var e in batch)
        {
            var docGuid = e.Properties.TryGetValue("DocGuid", out var v) && v is ScalarValue s ? s.Value?.ToString() : null;
            var rec = JsonSerializer.Serialize(new Rec(
                e.Timestamp.UtcDateTime.ToString("O"),
                e.Level.ToString(),
                e.RenderMessage(),
                e.Exception?.ToString(),
                docGuid,
                Src,
                Interlocked.Increment(ref _seq)));
            if (size + rec.Length > RawCap && lines.Count > 0) { sends.Add(Send(lines)); lines = []; size = 0; }
            lines.Add(rec);
            size += rec.Length + 1;
        }
        if (lines.Count > 0) sends.Add(Send(lines));
        await Task.WhenAll(sends);
    }

    async Task Send(List<string> lines)
    {
        var json = "{\"v\":1,\"lines\":[" + string.Join(',', lines) + "]}";
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            gz.Write(Encoding.UTF8.GetBytes(json));
        await queue.SendMessageAsync(Convert.ToBase64String(ms.ToArray()));
        Interlocked.Add(ref SentLines, lines.Count);
    }

    public Task OnEmptyBatchAsync() => Task.CompletedTask;

    record Rec(string ts, string level, string msg, string? ex, string? docGuid, string src, long seq);
}
