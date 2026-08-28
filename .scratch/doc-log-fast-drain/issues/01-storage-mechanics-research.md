# 01 - Storage mechanics research

Type: research
Status: resolved

## Question

Facts a consumer design waits on, for Azure Storage (and Azurite fidelity):

1. Append Blob: is AppendBlock safe from many concurrent writers (atomic per call)? Max block size? 50,000-block cap per blob — real? What happens when hit?
2. Storage Queue: max messages per GetMessages batch, visibility timeout behavior, throughput limits per queue, message size limit after base64.
3. Azurite: does it faithfully emulate the above (append blobs, batch dequeue, visibility timeout)?
4. Serilog: recommended way to build a batched custom sink today (IBatchedLogEventSink?), routing by property (DocGuid) to one sink vs console for the rest, CloseAndFlush guarantees.

## Answer

All assumptions confirmed against primary sources. Full findings with citations:
[research/storage-mechanics.md](../research/storage-mechanics.md)

- AppendBlock is atomic per call and safe for uncoordinated writers; 4 MiB/block (100 MiB on 2022-11-02+); 50,000-block cap is real (409 Conflict when hit, ~195 GiB min max size) — far away at our flush rates. appendpos/maxsize conditional headers exist (412) but are single-writer tools.
- Queue: 32 messages max per Get Messages; visibility timeout 30 s default / 7 d max, redelivery via timeout expiry; ~2,000 msg/s per queue target; 64 KiB message, 48 KiB usable after Base64; DequeueCount starts at 1 and increments per dequeue — poison-after-5 works as planned.
- Azurite 3.37.0 (mcr.microsoft.com/azure-storage/azurite) supports append blobs and the full queue API (batch dequeue, visibility timeout, DequeueCount). Caveat: "Concurrent Append" is listed as not/limited supported and Azurite gives no TPS guarantee — the sim proves mechanics, not Azure-grade throughput.
- Serilog: batching is in core since 4.0 (current 4.4.0) — implement IBatchedLogEventSink + WriteTo.Sink(sink, BatchingOptions); PeriodicBatching package is legacy. Default BufferingTimeLimit is 2 s (matches our flush target). Route DocGuid events via sub-loggers (WriteTo.Logger + Filter.ByIncludingOnly(Matching.WithProperty("DocGuid"))) or WriteTo.Conditional. Log.CloseAndFlush() drains the batch queue and waits for in-flight batches on graceful shutdown (unbounded wait, one delivery attempt).
