#:package Azure.Storage.Queues@12.*
#:package Azure.Storage.Blobs@12.*
// prints: <queue msg count> <poison count> <blob count>
using Azure.Storage.Blobs;
using Azure.Storage.Queues;
var cs = "DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;BlobEndpoint=http://127.0.0.1:27000/devstoreaccount1;QueueEndpoint=http://127.0.0.1:27001/devstoreaccount1;";
var q = new QueueClient(cs, "doc-logs");
var p = new QueueClient(cs, "doc-logs-poison");
int qc = (await q.GetPropertiesAsync()).Value.ApproximateMessagesCount;
int pc = p.Exists() ? (await p.GetPropertiesAsync()).Value.ApproximateMessagesCount : 0;
int blobs = 0;
var c = new BlobContainerClient(cs, "doc-logs");
if (c.Exists()) await foreach (var b in c.GetBlobsAsync()) blobs++;
Console.WriteLine($"{qc} {pc} {blobs}");
