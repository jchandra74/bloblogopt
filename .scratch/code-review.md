# Code Review

Date: 2026-08-28  
Scope: Entire current codebase  
Aspects: maintainability and complexity, security, and performance

## Overall assessment

This is a compact, well-documented proof of concept with a sensible append-only architecture. It is not yet production-ready. The principal release blockers are missing API access control, unbounded queue-message expansion, weak per-message fault isolation, and full-file processing for every log-page request.

No P0 issues were found.

Severity guide:

- **P0** — immediate critical failure or compromise.
- **P1** — release blocker before production use.
- **P2** — important correctness, scalability, or maintainability work.
- **P3** — worthwhile hardening or cleanup.

## Findings

### 1. P1 — The externally exposed log API has no authentication or authorization

Evidence: [`apphost.cs:29-33`](apphost.cs#L29), [`src/LogApi/Program.cs:9-21`](src/LogApi/Program.cs#L9), [`src/LogApi/Program.cs:45-66`](src/LogApi/Program.cs#L45)

If deployed as configured, callers can enumerate document IDs and retrieve rendered logs and full exception details. This can expose operational and customer-sensitive data. The configured endpoint is also plain HTTP unless TLS is supplied elsewhere.

Recommended direction:

- Make the service private by default.
- Require TLS at the service or trusted ingress.
- Add authentication and per-document authorization.
- Treat rendered messages and exception text as sensitive data.

### 2. P1 — Queue payload limits are assumed rather than enforced

Evidence: [`src/Producer/QueueLogSink.cs:16`](src/Producer/QueueLogSink.cs#L16), [`src/Producer/QueueLogSink.cs:34-36`](src/Producer/QueueLogSink.cs#L34), [`src/Producer/QueueLogSink.cs:44-48`](src/Producer/QueueLogSink.cs#L44)

`size` counts UTF-16 characters rather than encoded UTF-8 bytes. The 128-KiB raw cap assumes favorable gzip compression, and a single record over the cap is still sent by itself. Incompressible or non-ASCII content can therefore exceed the Azure Queue encoded-message limit.

A failed send faults the batch, prevents `SentLines` from advancing, and can eventually fill the 500,000-event Serilog buffer.

Recommended direction:

- Measure the actual compressed and Base64-encoded size before sending.
- Split before the encoded limit is exceeded.
- Explicitly reject, truncate, or route individually oversized records.
- Bound concurrent queue sends.
- Add boundary tests using non-ASCII and incompressible content.

### 3. P1 — Queue-message decompression and parsing are unbounded

Evidence: [`src/Consumer/Worker.cs:35`](src/Consumer/Worker.cs#L35), [`src/Consumer/Worker.cs:51-58`](src/Consumer/Worker.cs#L51), [`src/Consumer/Worker.cs:62-67`](src/Consumer/Worker.cs#L62), [`src/Consumer/Worker.cs:101-105`](src/Consumer/Worker.cs#L101)

A principal with queue-send permission can submit a small gzip payload that expands into a very large string. The worker then creates further copies through JSON parsing, per-document `StringBuilder` instances, and UTF-8 conversion.

For expanded size \(U\), processing requires \(O(U)\) time and \(O(U)\) space per batch, while \(U\) is not bounded by the compressed queue-message size. With 32 messages per receive and four pipelines per replica, this can exhaust the consumer fleet's memory.

Recommended direction:

- Decompress through a counting stream.
- Enforce limits on expanded bytes, record count, individual record length, and distinct documents.
- Reject or poison envelopes that exceed any limit.
- Validate the envelope version and schema before retaining records.

### 4. P1 — Malformed messages permanently consume worker pipelines

Evidence: [`src/Consumer/Worker.cs:27`](src/Consumer/Worker.cs#L27), [`src/Consumer/Worker.cs:30-60`](src/Consumer/Worker.cs#L30), [`src/Consumer/Worker.cs:101-105`](src/Consumer/Worker.cs#L101)

Base64 decoding, gzip decompression, JSON parsing, and required-property access occur without a per-message exception boundary. Each failure permanently removes one of a replica's four `RunLoop` tasks. Repeated deliveries progressively drain capacity before the poison threshold is reached. Enough malformed messages can exhaust the fleet's worker loops.

Because `Task.WhenAll` waits for every child, the first failed loop does not immediately stop the worker; it silently reduces that replica's capacity. The background service surfaces the failure only after all loops have completed or faulted.

Recommended direction:

- Catch decoding and validation failures per queue message.
- Preserve the receive loop after a bad message.
- Apply an explicit retry/poison policy to invalid messages.
- Keep append and acknowledgement failures distinct from invalid-payload failures.

### 5. P1 — API pagination does not limit server work

Evidence: [`src/LogApi/Program.cs:26-43`](src/LogApi/Program.cs#L26), [`src/LogApi/Program.cs:45-56`](src/LogApi/Program.cs#L45)

Every page request downloads the entire append blob, creates a complete string, splits and parses every record, deduplicates all entries, globally sorts them, and only then applies `Skip` and `Take`. Page size therefore provides no CPU, memory, or network protection.

For blob size \(S\) and \(N\) records, each request takes \(O(S + N\log N)\) time and \(O(S + N)\) auxiliary space. With \(C\) concurrent requests, memory and network work scale as \(O(CS)\).

Recommended direction:

- Store or build an index suitable for ordered paging.
- Consider time/sequence-keyed segments rather than one indefinitely growing blob.
- Cache parsed/indexed results by blob ETag where appropriate.
- Enforce document-size and request-work limits.
- Add rate limiting.
- Pass `HttpContext.RequestAborted` through storage and processing calls.
- Move `Skip`/`Take` before response projection as a small immediate allocation improvement.

### 6. P1 — The two-minute visibility timeout is never renewed

Evidence: [`src/Consumer/Worker.cs:35`](src/Consumer/Worker.cs#L35), [`src/Consumer/Worker.cs:51-93`](src/Consumer/Worker.cs#L51)

Under a large batch, storage throttling, or retries, messages can become visible while still being processed. Another pipeline can append the same contents again, causing duplicate work, blob growth, and a positive feedback loop. Deleting with a stale pop receipt can then fault a worker loop.

Read-time deduplication preserves the logical output but does not prevent the storage, bandwidth, and processing cost.

Recommended direction:

- Renew visibility with `UpdateMessageAsync` while work remains active.
- Size receive batches and concurrency against a measured processing-time budget.
- Record lease-renewal, stale-receipt, retry, and duplicate metrics.
- Consider independently idempotent or independently acknowledged message processing.

### 7. P2 — The distributed JSON contract is duplicated and stringly typed

Evidence: [`src/Producer/QueueLogSink.cs:26-33`](src/Producer/QueueLogSink.cs#L26), [`src/Producer/QueueLogSink.cs:44`](src/Producer/QueueLogSink.cs#L44), [`src/Consumer/Worker.cs:51-56`](src/Consumer/Worker.cs#L51), [`src/LogApi/Program.cs:37-54`](src/LogApi/Program.cs#L37)

The producer owns a private record type and manually constructs the envelope, while the consumer and API repeatedly access raw JSON property names. The producer emits envelope version `v: 1`, but the consumer never validates or dispatches on it.

Schema evolution requires coordinated edits across three services and incompatible changes fail only at runtime.

Recommended direction:

- Introduce a shared, versioned contract and codec package.
- Deserialize into typed records with strict validation.
- Add producer/consumer/API compatibility tests.
- Define forward- and backward-compatibility behavior for unknown versions and fields.

### 8. P2 — Append-blob rollover is explicitly unimplemented

Evidence: [`src/Consumer/Worker.cs:66-89`](src/Consumer/Worker.cs#L66)

When a document reaches the append-block limit, its writes fault. Because queue messages are acknowledged only after all document appends succeed, one exhausted document causes unrelated messages and documents in the receive batch to replay.

The line-boundary splitter has a related progress bug: if one highly compressible JSON record expands beyond the 3-MiB append chunk, `Array.LastIndexOf` can return `-1`, producing `len == 0`. The loop then cannot advance; the zero-length append may either fail immediately or repeatedly consume calls.

Recommended direction:

- Roll to generation or segment blobs such as `log-2.jsonl`.
- Update the read API to enumerate and merge segments.
- Reject or specially handle any single record larger than the append chunk.
- Assert that every chunking-loop iteration advances `off`.

### 9. P2 — Concurrency is high, scattered, and not governed by a storage-account budget

Evidence: [`apphost.cs:13-17`](apphost.cs#L13), [`src/Consumer/Worker.cs:27`](src/Consumer/Worker.cs#L27), [`src/Consumer/Worker.cs:35`](src/Consumer/Worker.cs#L35), [`src/Consumer/Worker.cs:62-64`](src/Consumer/Worker.cs#L62)

Four replicas multiplied by four receive loops and up to 32 parallel document appends permits as many as 512 concurrent blob operations. Adding replicas multiplies that value further. SDK retries can turn throttling into synchronized retry storms and increase the likelihood of exceeding the visibility timeout.

Recommended direction:

- Bind replica-local concurrency, dequeue size, visibility, and retry settings through validated options.
- Apply a shared or adaptive concurrency budget.
- Use exponential backoff with jitter and expose throttling/retry telemetry.
- Log the resolved capacity model at startup.

### 10. P2 — Mixed-document envelopes amplify blob requests

Evidence: [`src/Producer/QueueLogSink.cs:22-38`](src/Producer/QueueLogSink.cs#L22), [`src/Consumer/Worker.cs:39-67`](src/Consumer/Worker.cs#L39)

The producer preserves mixed document order in each envelope. The consumer then groups the receive window by document and performs one or more blob requests per distinct document. A receive window containing \(L\) lines can therefore require \(O(D)\), worst-case \(O(L)\), append operations.

Recommended direction:

- Partition envelopes by document or a stable document shard where feasible.
- Alternatively, maintain bounded per-document buffers with a flush-age limit.
- Measure average append size, distinct documents per receive, request cost, and throttling before selecting a strategy.

### 11. P2 — Existing verification has drifted and there are no automated tests

Evidence: [`apphost.cs:23-27`](apphost.cs#L23), [`tools/verify.cs:25-26`](tools/verify.cs#L25)

The active workload creates 200-line documents and makes 10% of documents use the larger line count, while `verify.cs` requires exactly 100 lines. The verifier therefore reports healthy current output as bad.

No automated test project covers:

- encoded payload boundaries;
- decompression limits;
- invalid-envelope poisoning;
- append-before-delete replay;
- visibility renewal;
- concurrent appends;
- schema compatibility;
- deduplication and stable ordering;
- paging and large-document behavior.

Recommended direction:

- Replace fixed verifier expectations with shared workload configuration.
- Convert critical checks into automated unit, contract, and Azurite integration tests.

### 12. P2 — Producer completion and backpressure depend on global mutable state

Evidence: [`src/Producer/QueueLogSink.cs:12`](src/Producer/QueueLogSink.cs#L12), [`src/Producer/QueueLogSink.cs:49`](src/Producer/QueueLogSink.cs#L49), [`src/Producer/LoadGenerator.cs:40-46`](src/Producer/LoadGenerator.cs#L40)

`LoadGenerator` knows the sink implementation and polls the public static `SentLines` counter. Multiple sink instances or repeated in-process tests share stale state, and replacing the sink requires modifying the generator.

Recommended direction:

- Use an injected instance-level progress and drain abstraction.
- Prefer a bounded channel or queue with explicit completion and backpressure semantics.

### 13. P2 — `RunLoop` combines too many failure-sensitive responsibilities

Evidence: [`src/Consumer/Worker.cs:30-98`](src/Consumer/Worker.cs#L30)

One method owns dequeueing, poison policy, decompression, schema parsing, grouping, append-blob creation, block splitting, acknowledgement, and metrics. Changes to any concern can accidentally alter the critical append-before-delete boundary, and direct SDK calls leave few practical unit-test seams.

Recommended direction:

- Extract an envelope decoder and validator.
- Extract a document appender with explicit rollover behavior.
- Introduce a per-message lifecycle coordinator that owns retry, poison, append, and acknowledgement decisions.

### 14. P3 — `/api/documents` is capped but not pageable

Evidence: [`src/LogApi/Program.cs:9-18`](src/LogApi/Program.cs#L9)

The endpoint always starts at the beginning and returns at most 1,000 IDs. Documents after that prefix are unreachable, while repeated requests rescan the same beginning of the container.

Its time complexity is \(O(Q)\), where \(Q\) is the number of blobs scanned to find the requested IDs, and its auxiliary space is \(O(k)\).

Recommended direction:

- Expose opaque Azure continuation tokens.
- Use hierarchical prefix listing where appropriate.
- Propagate request cancellation.

### 15. P3 — The producer retains and starts all envelope sends concurrently

Evidence: [`src/Producer/QueueLogSink.cs:20`](src/Producer/QueueLogSink.cs#L20), [`src/Producer/QueueLogSink.cs:34-39`](src/Producer/QueueLogSink.cs#L34)

`EmitBatchAsync` collects every `Send` task and awaits them together. In the pathological case where each record becomes its own envelope, this creates \(O(E)\) simultaneous queue calls and retains \(O(R)\) payload data until completion.

Recommended direction:

- Use a small concurrency limiter or a bounded send pipeline.
- Avoid retaining every envelope and intermediate buffer until the slowest request completes.

### 16. P3 — The load generator performs avoidable nested scanning

Evidence: [`src/Producer/LoadGenerator.cs:22-44`](src/Producer/LoadGenerator.cs#L22)

For generator chunk \(j\), let \(P_j\) be its document count and \(H_j\) its longest document. Runtime is:

\[
\Theta\left(\sum_j P_jH_j\right)
\]

rather than \(\Theta(L)\) for \(L\) emitted lines. One monster document makes the loop repeatedly scan every completed shorter document in the same chunk.

This is confined to test-load generation, so it is lower priority. A per-document task or priority queue would reduce the loop to \(\Theta(L)\).

## Complexity inventory

Variables:

- \(B\): log events in a producer batch.
- \(R\): total serialized bytes in that producer batch.
- \(E\): envelopes generated from the batch.
- \(M\): queue messages received, currently at most 32 per receive.
- \(U\): total expanded bytes in a consumer batch.
- \(L\): records in that consumer batch.
- \(D\): distinct documents in the batch.
- \(K\): append blocks written.
- \(S\): bytes in one document blob.
- \(N\): raw records in that blob.
- \(T\): blobs in the container.
- \(k\): document IDs requested from the listing endpoint.

| Operation | Time complexity | Auxiliary space | External operations |
|---|---:|---:|---:|
| Producer `EmitBatchAsync` | \(O(R)\) | \(O(R + E)\) | \(O(E)\) queue sends |
| Producer `Send` per envelope | \(O(r)\) | \(O(r)\) | 1 queue send |
| Consumer decode/group/write | \(O(U + L)\) CPU | \(O(U + D + M)\) | \(O(D + K + M)\) blob/delete calls |
| List documents | \(O(Q)\), for blobs scanned | \(O(k)\) | Paged blob listing |
| Read one log page | \(O(S + N\log N)\) | \(O(S + N)\) | Full blob download |
| Load generator | \(\Theta(\sum_j P_jH_j)\) | \(O(P + \text{sink queue})\) | Logging-dependent |
| `tools/verify.cs` | \(O(T\log T)\) | \(O(T)\) | Lists all blobs, downloads 20 |
| `tools/depth.cs` | \(O(T)\) | \(O(1)\) | Lists all blobs |

Important scaling consequences:

- API page size does not alter the asymptotic work of reading a document.
- Consumer memory is linear in expanded rather than compressed message size.
- Mixed-document envelopes turn line packing efficiency into per-document blob-request amplification.
- At fleet scale, concurrency is multiplied by replicas, loops per replica, and per-loop append parallelism.

## Positive observations

- The repository has a clear domain glossary and an explicit design/specification under `.scratch/doc-log-fast-drain/`.
- The at-least-once delivery and read-time deduplication strategy is documented consistently.
- Queue receives use the service's maximum batch size and blob writes use append semantics rather than download-modify-upload.
- General logs and document logs are cleanly separated at the producer.
- The source surface is small enough that the major production-hardening changes remain tractable.

## Validation performed

- Producer, Consumer, and LogApi compiled successfully with zero compiler warnings.
- Dependency audit reported no known vulnerable packages from the configured sources.
- No automated test project was found.
- The checked-in `devstoreaccount1` credential in `tools/` is Azurite's canonical public emulator credential, not a production secret. The emulator should nevertheless remain loopback-only or otherwise private.
- No source files were changed as part of the review.

