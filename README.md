# BlobLogOpt

Proof of concept for a document-log pipeline that scales horizontally: **Serilog → Azure Storage Queue → parallel consumers → per-document Append Blobs → paged read API**.

It replaces a single-consumer download-modify-upload funnel (which backs up past 2M+ queue messages at peak) with a wide, uncoordinated consumer fleet and read-time ordering. See [README.html](README.html) for the full write-up: architecture diagram, wire formats, measured results, limitations, and a porting checklist.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Docker Desktop (runs the Azurite storage emulator)
- Aspire CLI 13.5.3: `dotnet tool install -g Aspire.Cli`

## Getting started

```bash
aspire start
```

This builds everything and starts Azurite, the producer (load generator), 4 consumer replicas, and the log API. The dashboard URL is printed on start — logs, resource state, and the blob container are all browsable there.

```bash
aspire stop
```

Tears it down.

### Try the API

```bash
curl http://localhost:27080/api/documents?limit=3
curl "http://localhost:27080/api/documents/<docGuid>/logs?page_size=25&page=1"
```

### Check what's happening

```bash
dotnet run tools/depth.cs    # queue depth, poison count, blob count
dotnet run tools/verify.cs   # samples 20 docs, checks line counts / dupes / doc match
dotnet run tools/pickdoc.cs  # grab a random DocGuid to query
```

## Project layout

| Path | What it is |
|---|---|
| `apphost.cs` | Aspire AppHost — Azurite, queues, blob container, and the three services below. All load-test knobs are env vars here. |
| `src/Producer` | Load generator + `QueueLogSink`, a Serilog batching sink that ships log lines to the queue. |
| `src/Consumer` | Drains the queue and appends each document's lines to its blob. 4 replicas × 4 pipelines, no coordination. |
| `src/LogApi` | Minimal API: list documents, read a document's logs (paged). |
| `tools/` | Single-file C# scripts for poking at the running system (`dotnet run tools/<file>.cs`). |
| `.scratch/doc-log-fast-drain/` | Design docs and decision history. |

## Status

POC, not production-hardened. Runs behind a private network — the log API is the only externally-reachable piece, and it's deliberately left without auth (see README.html §9, §12 for what was and wasn't hardened).
