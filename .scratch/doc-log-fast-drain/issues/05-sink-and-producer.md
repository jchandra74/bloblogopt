# 05 - Serilog sink + load generator

Type: task
Status: resolved
Blocked by: 02, 04

## Question

Build the batched Serilog sink (DocGuid lines -> packed queue messages, 1-2s flush,
flush on shutdown; non-DocGuid lines -> console) and a load generator that emits the
strawman load (2M lines, ~20k docs) through it.

## Answer

Built and validated 2026-08-28.

- [QueueLogSink.cs](../../../src/Producer/QueueLogSink.cs): IBatchedLogEventSink (Serilog 4 core batching). Packs records into {"v":1,"lines":[...]} envelopes, raw-JSON cap 128 KiB -> gzip -> base64 -> SendMessageAsync. Stamps src (random per instance) + seq (Interlocked counter) per record. Parallel sends per batch.
- [LoadGenerator.cs](../../../src/Producer/LoadGenerator.cs): DOCS x LINES_PER_DOC round-robin emit via Log.ForContext("DocGuid", ...). Crude backpressure: pauses when emitted - sent > 300k so the Serilog queue (limit 500k) never drops. Waits for full flush, logs DONE, stops host (CloseAndFlush in Program finally).
- [Program.cs](../../../src/Producer/Program.cs): sub-loggers route DocGuid events to the queue sink, everything else to console. Aspire client: builder.AddAzureQueue("log-queue") -> QueueClient via DI.
- apphost: producer gets DOCS=20000, LINES_PER_DOC=100.

Validation run: 2,000,000 lines enqueued in 13.3 s, enqueued == emitted (zero drops).
