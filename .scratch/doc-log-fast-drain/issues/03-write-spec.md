# 03 - Write the spec

Type: task
Status: resolved
Blocked by: 01, 02

## Question

Consolidate all decisions into .scratch/doc-log-fast-drain/spec.md: architecture,
envelope/record schemas, consumer parallelism model, poison handling, tuning defaults,
and the 2M-line acceptance run.

## Answer

Spec written: [spec.md](../spec.md). Consolidates the architecture (read-time ordering,
gzip envelopes, append blobs, affinity-free parallel consumers), both schemas, producer
sink design (Serilog 4 IBatchedLogEventSink + sub-logger routing), consumer loop with
poison handling and 409 rollover, tuning defaults, and the 2M acceptance run.
