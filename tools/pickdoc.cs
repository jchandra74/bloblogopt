#:package Azure.Storage.Blobs@12.*
using Azure.Storage.Blobs;
var cs = "DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;BlobEndpoint=http://127.0.0.1:27000/devstoreaccount1;QueueEndpoint=http://127.0.0.1:27001/devstoreaccount1;";
var c = new BlobContainerClient(cs, "doc-logs");
await foreach (var b in c.GetBlobsAsync()) { Console.WriteLine(b.Name.Split('/')[0]); break; }
