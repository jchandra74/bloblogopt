# Spec: fast document-log drain

Status: decided 2026-08-28. Sources: map.md decisions, ticket 01 (research), ticket 02 (schema).
Goal: replace the single-consumer, download-modify-upload document-log pipeline with a
design that drains a 2M-line backlog in minutes. Proven locally via Aspire + Azurite.

## Architecture

```
doc processor (many replicas)
  Serilog
    ├─ events WITHOUT DocGuid ──> Console sink            (out of scope here)
    └─ events WITH DocGuid ────> Batched queue sink
                                    │  gzip'd envelopes
                                    v
                          Azure Storage Queue  ── after 5 dequeues ──> poison queue
                                    │  batch dequeue (32)
                                    v
                        Document Log Handler (N replicas, no affinity)
                                    │  AppendBlock, JSON-lines
                                    v
                 per-document Append Blob:  <container>/<DocGuid>/log.jsonl
```

Why it is fast:
1. Ordering is a READ-time problem. Records carry `ts` + `(src, seq)`; the viewer sorts
   and dedupes. So consumers need no ordering, no affinity, no single-writer rule.
2. Envelopes pack hundreds of lines per queue message (gzip). 2M lines ~ 2-4k messages.
3. Append Blob replaces download-modify-upload. One atomic AppendBlock per doc-batch.

## Record (one JSON line per log event)

```json
{ "ts": "2026-08-28T10:15:30.1234567Z", "level": "Information", "msg": "rendered text",
  "ex": null, "docGuid": "<guid>", "src": "<producer-instance-id>", "seq": 12345 }
```

- `ts`: ISO-8601 UTC, from the moment of logging.
- `msg`: rendered message text. `ex`: full exception ToString or null.
- `src`: random id per producer instance at startup. `seq`: per-instance counter.
- Viewer contract: sort by `ts`, tie-break `(src, seq)`, drop duplicate `(src, seq)`.
- No structured props bag (additive later).

## Envelope (one queue message)

`{ "v": 1, "lines": [ ...records... ] }` -> gzip -> base64 -> queue message.
Cap the packed size so base64 output stays under 48 KiB.

## Producer side (Serilog sink)

- Serilog 4.x core batching: implement `Serilog.Core.IBatchedLogEventSink`
  (`EmitBatchAsync`), register `WriteTo.Sink(sink, new BatchingOptions())`.
  Default buffering time limit 2 s = our 1-2 s flush. PeriodicBatching package is legacy; do not use.
- Routing: sub-loggers — `Filter.ByIncludingOnly(Matching.WithProperty("DocGuid"))` for
  the queue sink; `ByExcluding` the property for console.
- Loss window: in-memory buffer until flush; `Log.CloseAndFlush()` drains on graceful
  shutdown. Hard-crash loss of the ~2 s buffer is accepted.

## Consumer (Document Log Handler)

- N identical replicas (Aspire `WithReplicas`; KEDA in prod). No coordination.
- Loop: `ReceiveMessages(32, visibilityTimeout: 2 min)` -> unpack envelopes ->
  group records by `docGuid` -> one `AppendBlock` per doc (all its lines concatenated
  as JSON-lines) -> delete each queue message only after every append it fed succeeded.
- Blob: `CreateIfNotExists` append blob at `<container>/<DocGuid>/log.jsonl`.
- At-least-once: crash between append and delete replays the message -> duplicate
  lines -> viewer dedupes by `(src, seq)`. Fine.
- Poison: on receive, if `DequeueCount > 5`, copy message to `<queue>-poison`, delete, continue.
- 50k-block cap: at ~100 lines/doc and batched appends, a doc uses a handful of blocks;
  cap is unreachable in practice. On 409 BlockCountExceedsLimit, roll to `log-2.jsonl`.
  # ponytail: rollover is the whole mitigation; no proactive block counting.

## Tuning defaults (all config knobs)

| Knob | Default |
|---|---|
| Sink flush | 2 s (BatchingOptions default) |
| Envelope cap | 48 KiB post-base64 |
| Dequeue batch | 32 |
| Visibility timeout | 2 min |
| Consumer replicas | 4 |
| Poison threshold | DequeueCount > 5 |

## Acceptance (ticket 07)

Load generator emits 2M lines across ~20k docs (~100 lines/doc) through the real sink.
Success: queue drains to empty in minutes (not hours) on a laptop against Azurite;
spot-check several docs' `log.jsonl` for valid JSON-lines and complete line counts.
Caveat: Azurite lists Concurrent Append support as limited — treat sim timing as a
lower bound on mechanics, not an Azure throughput benchmark.

## Out of scope

Production rollout/migration, the console->New Relic path, the viewer app, migrating
existing JSON-array files.
