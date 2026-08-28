using System.Text.Json;
using Azure;
using Azure.Storage.Blobs;

var builder = WebApplication.CreateBuilder(args);
builder.AddAzureBlobContainerClient("log-container");
var app = builder.Build();

app.MapGet("/api/documents", async (BlobContainerClient container, int? limit) =>
{
    var ids = new List<string>();
    await foreach (var b in container.GetBlobsAsync())
    {
        var id = b.Name.Split('/')[0];
        if (ids.Count == 0 || ids[^1] != id) ids.Add(id); // blob listing is sorted, so prefixes dedupe adjacently
        if (ids.Count >= Math.Clamp(limit ?? 25, 1, 1000)) break;
    }
    return Results.Ok(ids);
});

app.MapGet("/api/documents/{docGuid}/logs", async (string docGuid, BlobContainerClient container, int? page_size, int? page) =>
{
    int size = Math.Clamp(page_size ?? 25, 1, 1000);
    int pageNo = Math.Max(page ?? 1, 1);

    string content;
    try
    {
        content = (await container.GetBlobClient($"{docGuid}/log.jsonl").DownloadContentAsync()).Value.Content.ToString();
    }
    catch (RequestFailedException e) when (e.Status == 404)
    {
        return Results.NotFound(new { error = $"no logs for document {docGuid}" });
    }

    // ponytail: whole file parsed per request; fine for <= a few MB per doc. Cache/stream if docs ever grow past that.
    var entries = content.Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(l => JsonSerializer.Deserialize<JsonElement>(l))
        .DistinctBy(e => e.GetProperty("src").GetString() + ":" + e.GetProperty("seq").GetInt64())
        .OrderBy(e => e.GetProperty("ts").GetString(), StringComparer.Ordinal) // ISO-8601 UTC sorts lexically
        .ThenBy(e => e.GetProperty("src").GetString(), StringComparer.Ordinal)
        .ThenBy(e => e.GetProperty("seq").GetInt64())
        .ToList();

    var lines = entries
        .Select((e, i) => new
        {
            rowId = i + 1,
            ts = e.GetProperty("ts").GetString(),
            level = e.GetProperty("level").GetString(),
            msg = e.GetProperty("msg").GetString(),
            ex = e.TryGetProperty("ex", out var x) && x.ValueKind != JsonValueKind.Null ? x.GetString() : null,
            src = e.GetProperty("src").GetString(),
            seq = e.GetProperty("seq").GetInt64(),
        })
        .Skip((pageNo - 1) * size).Take(size);

    return Results.Ok(new
    {
        docGuid,
        page = pageNo,
        pageSize = size,
        totalLines = entries.Count,
        totalPages = (entries.Count + size - 1) / size,
        lines,
    });
});

app.Run();
