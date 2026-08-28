#:package Azure.Storage.Blobs@12.*
// samples N random doc blobs: asserts a valid line count (200, or 2000 for "big" docs), no duplicate (src,seq), docGuid matches path
using System.Text.Json;
using Azure.Storage.Blobs;
var cs = "DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;BlobEndpoint=http://127.0.0.1:27000/devstoreaccount1;QueueEndpoint=http://127.0.0.1:27001/devstoreaccount1;";
var c = new BlobContainerClient(cs, "doc-logs");
var names = new List<string>();
await foreach (var b in c.GetBlobsAsync()) names.Add(b.Name);
Console.WriteLine($"blobs: {names.Count}");
var rnd = new Random(42);
int bad = 0;
foreach (var name in names.OrderBy(_ => rnd.Next()).Take(20))
{
    var text = (await c.GetBlobClient(name).DownloadContentAsync()).Value.Content.ToString();
    var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
    var keys = new HashSet<string>();
    int dupes = 0, wrongDoc = 0;
    foreach (var l in lines)
    {
        using var d = JsonDocument.Parse(l);
        var r = d.RootElement;
        if (!keys.Add(r.GetProperty("src").GetString() + ":" + r.GetProperty("seq").GetInt64())) dupes++;
        if (name != r.GetProperty("docGuid").GetString() + "/log.jsonl") wrongDoc++;
    }
    var ok = (lines.Length == 200 || lines.Length == 2000) && dupes == 0 && wrongDoc == 0; // ponytail: matches apphost.cs LINES_PER_DOC/BIG_DOC_LINES; keep in sync if those change
    if (!ok) { bad++; Console.WriteLine($"BAD {name}: lines={lines.Length} dupes={dupes} wrongDoc={wrongDoc}"); }
}
Console.WriteLine(bad == 0 ? "ALL 20 SAMPLES OK: valid JSON lines each, no dupes, right doc" : $"{bad} bad");
