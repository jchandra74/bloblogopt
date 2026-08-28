using System.Diagnostics;
using Serilog;

namespace Producer;

public sealed class LoadGenerator(IConfiguration cfg, IHostApplicationLifetime life) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        int docs = cfg.GetValue("DOCS", 20000);
        int linesPerDoc = cfg.GetValue("LINES_PER_DOC", 200);
        int parallelDocs = cfg.GetValue("PARALLEL_DOCS", 200); // docs in flight at once, mimics real pod concurrency
        int rate = cfg.GetValue("RATE", 0);                    // lines/sec, 0 = unlimited
        long maxLines = cfg.GetValue("MAX_LINES", 2_000_000L);
        int bigPct = cfg.GetValue("BIG_DOC_PCT", 10);          // % of docs that are "monsters"
        int bigLines = cfg.GetValue("BIG_DOC_LINES", 2000);
        Log.Information("Load generator: {Docs:N0} docs, {Lines}/{Big} lines ({Pct}% big), rate {Rate}, cap {Max:N0}",
            docs, linesPerDoc, bigLines, bigPct, rate == 0 ? "max" : rate.ToString(), maxLines);
        var sw = Stopwatch.StartNew();
        var rnd = new Random(42);
        long emitted = 0;
        for (int start = 0; start < docs && emitted < maxLines && !ct.IsCancellationRequested; start += parallelDocs)
        {
            var chunk = new (string Guid, int Lines)[Math.Min(parallelDocs, docs - start)];
            for (int i = 0; i < chunk.Length; i++)
                chunk[i] = (Guid.NewGuid().ToString(), rnd.Next(100) < bigPct ? bigLines : linesPerDoc);
            int maxDocLines = 0;
            foreach (var c in chunk) maxDocLines = Math.Max(maxDocLines, c.Lines);
            for (int line = 0; line < maxDocLines && emitted < maxLines && !ct.IsCancellationRequested; line++)
                foreach (var (guid, docLines) in chunk)
                {
                    if (line >= docLines) continue;
                    Log.ForContext("DocGuid", guid).Information("Processing step {Step} of document", line);
                    emitted++;
                    if (rate > 0 && emitted % 100 == 0)
                        while (emitted > sw.Elapsed.TotalSeconds * rate && !ct.IsCancellationRequested)
                            await Task.Delay(20, ct); // pace to RATE lines/sec
                    if (emitted % 50_000 == 0)
                    {
                        while (emitted - Interlocked.Read(ref QueueLogSink.SentLines) > 300_000 && !ct.IsCancellationRequested)
                            await Task.Delay(100, ct); // ponytail: crude backpressure so the batching queue never drops events
                        Log.Information("Emitted {Emitted:N0}, enqueued {Sent:N0}, {Sec:F0}s", emitted, Interlocked.Read(ref QueueLogSink.SentLines), sw.Elapsed.TotalSeconds);
                    }
                }
        }
        while (Interlocked.Read(ref QueueLogSink.SentLines) < emitted && !ct.IsCancellationRequested)
            await Task.Delay(200, ct);
        Log.Information("DONE: {Total:N0} lines enqueued in {Sec:F1}s", emitted, sw.Elapsed.TotalSeconds);
        life.StopApplication();
    }
}
