# 04 - Aspire AppHost wiring

Type: task
Status: resolved

## Question

Wire apphost.cs: Azurite (queue + blob), a producer/load-generator project, a consumer
project with WithReplicas(N). Skeleton only needs to start clean under aspire start.
Use the aspireify skill.

## Answer

Wired and validated 2026-08-28. apphost.cs now declares:
- `storage` = AddAzureStorage("storage").RunAsEmulator() (Azurite container)
- queues service + blobs service, queue `doc-logs`, queue `doc-logs-poison`, blob container `doc-logs` (resource names log-queue/poison-queue/log-container; queue/container names on storage are the doc-logs ones)
- `consumer` project (src/Consumer, worker skeleton) x4 replicas, references queues+blobs+container, WaitFor(storage)
- `producer` project (src/Producer, worker skeleton), references log queue, WaitFor(storage)

Validated: aspire start clean; storage + producer healthy via aspire wait; 4 consumer
replicas running and logging. Gotchas hit and fixed: AddQueue/AddBlobContainer hang off
the storage resource (not the queue/blob service), and a queue + container cannot share
one resource name. Note: `aspire wait consumer` fails on replicated resources (replica
names are random suffixes); use `aspire logs consumer` instead.
No ServiceDefaults project (ponytail: console logs suffice for the sim; add if dashboard metrics wanted).
