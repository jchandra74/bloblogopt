#:package Aspire.Hosting.Azure.Storage@13.5.3
#:sdk Aspire.AppHost.Sdk@13.5.3+d7d0b6759ce4b936c76bc4775814d27db560dd6d

var builder = DistributedApplication.CreateBuilder(args);

var storage = builder.AddAzureStorage("storage").RunAsEmulator(e => e.WithBlobPort(27000).WithQueuePort(27001));
var queues = storage.AddQueues("queues");
var blobs = storage.AddBlobs("blobs");
var logQueue = storage.AddQueue("log-queue", "doc-logs");
var poisonQueue = storage.AddQueue("poison-queue", "doc-logs-poison");
var logContainer = storage.AddBlobContainer("log-container", "doc-logs");

builder.AddProject("consumer", "src/Consumer/Consumer.csproj")
    .WithReference(logQueue).WithReference(poisonQueue).WithReference(logContainer)
    .WaitFor(storage)
    .WithEnvironment("START_DELAY_SEC", "0")
    .WithReplicas(4);

builder.AddProject("producer", "src/Producer/Producer.csproj")
    .WithReference(logQueue)
    .WaitFor(storage)
    .WithEnvironment("DOCS", "20000")
    .WithEnvironment("LINES_PER_DOC", "200")
    .WithEnvironment("PARALLEL_DOCS", "200")
    .WithEnvironment("RATE", "0")
    .WithEnvironment("MAX_LINES", "100000")
    .WithEnvironment("BIG_DOC_PCT", "10");

builder.AddProject("logapi", "src/LogApi/LogApi.csproj")
    .WithReference(logContainer)
    .WaitFor(storage)
    .WithHttpEndpoint(port: 27080)
    .WithExternalHttpEndpoints();

builder.Build().Run();
