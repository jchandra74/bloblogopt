using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Specialized;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;

namespace Consumer;

public sealed class Worker(
    [FromKeyedServices("log-queue")] QueueClient queue,
    [FromKeyedServices("poison-queue")] QueueClient poison,
    BlobContainerClient container,
    IConfiguration cfg,
    ILogger<Worker> log) : BackgroundService
{
    long _lines;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Yield();
        int startDelay = cfg.GetValue("START_DELAY_SEC", 0); // burst test: let the producer build a backlog first
        if (startDelay > 0) await Task.Delay(TimeSpan.FromSeconds(startDelay), ct);
        // ponytail: 4 independent dequeue pipelines per replica; bump if Azurite stops being the bottleneck
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => RunLoop(ct)));
    }

    async Task RunLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            QueueMessage[] msgs;
            try { msgs = (await queue.ReceiveMessagesAsync(32, TimeSpan.FromMinutes(30), ct)).Value; }
            catch (OperationCanceledException) { break; }
            if (msgs.Length == 0) { await Task.Delay(500, ct); continue; }

            var keep = new List<QueueMessage>();
            var byDoc = new Dictionary<string, StringBuilder>();
            long batchLines = 0;
            foreach (var m in msgs)
            {
                if (m.DequeueCount > 2) // allow 2 appearances (1 retry), then deadletter
                {
                    await poison.SendMessageAsync(m.MessageText, ct);
                    await queue.DeleteMessageAsync(m.MessageId, m.PopReceipt, ct);
                    log.LogWarning("Poisoned message {Id} after {N} dequeues", m.MessageId, m.DequeueCount);
                    continue;
                }
                try
                {
                    using var doc = JsonDocument.Parse(Gunzip(m.MessageText));
                    foreach (var el in doc.RootElement.GetProperty("lines").EnumerateArray())
                    {
                        var id = el.GetProperty("docGuid").GetString()!;
                        if (!byDoc.TryGetValue(id, out var sb)) byDoc[id] = sb = new StringBuilder();
                        sb.Append(el.GetRawText()).Append('\n');
                        batchLines++;
                    }
                    keep.Add(m);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // ponytail: leave it in the queue on bad decode/parse; DequeueCount>5 above poisons it after retries
                    log.LogWarning(ex, "Malformed message {Id}, dequeue count {N}", m.MessageId, m.DequeueCount);
                }
            }

            await Parallel.ForEachAsync(byDoc,
                new ParallelOptions { MaxDegreeOfParallelism = 32, CancellationToken = ct },
                async (kv, c) =>
            {
                var blob = container.GetAppendBlobClient($"{kv.Key}/log.jsonl");
                var bytes = Encoding.UTF8.GetBytes(kv.Value.ToString());
                const int Max = 3 * 1024 * 1024; // stay under the 4 MiB AppendBlock cap
                int off = 0;
                while (off < bytes.Length)
                {
                    int len = Math.Min(Max, bytes.Length - off);
                    if (off + len < bytes.Length) // split only at a line boundary so a foreign interleaved block can't corrupt a record
                    {
                        int nl = Array.LastIndexOf(bytes, (byte)'\n', off + len - 1, len);
                        len = nl >= 0
                            ? nl - off + 1
                            // one record's line is bigger than a chunk; take the whole line instead of a zero-length block
                            : (Array.IndexOf(bytes, (byte)'\n', off + len) is int next && next >= 0 ? next + 1 : bytes.Length) - off;
                    }
                    using var ms = new MemoryStream(bytes, off, len);
                    try
                    {
                        await blob.AppendBlockAsync(ms, cancellationToken: c);
                    }
                    catch (RequestFailedException e) when (e.Status == 404)
                    {
                        // append-first: create only on first touch, then retry once
                        await blob.CreateIfNotExistsAsync(cancellationToken: c);
                        ms.Position = 0;
                        await blob.AppendBlockAsync(ms, cancellationToken: c);
                    }
                    off += len;
                }
                // ponytail: on 409 BlockCountExceedsLimit roll to log-2.jsonl; unreachable at sim scale, so not implemented
            });

            // ponytail: batch-level all-or-nothing — delete only after every append succeeded; a crash replays the batch, dupes deduped at read
            await Task.WhenAll(keep.Select(m => queue.DeleteMessageAsync(m.MessageId, m.PopReceipt, ct)));

            var total = Interlocked.Add(ref _lines, batchLines);
            if (total / 100_000 != (total - batchLines) / 100_000)
                log.LogInformation("Appended {Total:N0} lines so far", total);
        }
    }

    const int MaxExpandedBytes = 16 * 1024 * 1024; // ponytail: governor cap against a runaway gzip expansion; raise if legit batches ever get this big

    static string Gunzip(string b64)
    {
        using var gz = new GZipStream(new MemoryStream(Convert.FromBase64String(b64)), CompressionMode.Decompress);
        using var ms = new MemoryStream();
        var buf = new byte[81920];
        int total = 0, n;
        while ((n = gz.Read(buf, 0, buf.Length)) > 0)
        {
            total += n;
            if (total > MaxExpandedBytes) throw new InvalidDataException($"decompressed message exceeds {MaxExpandedBytes:N0} bytes");
            ms.Write(buf, 0, n);
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }
}
