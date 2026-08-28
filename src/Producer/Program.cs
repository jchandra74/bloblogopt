using Azure.Storage.Queues;
using Producer;
using Serilog;
using Serilog.Configuration;
using Serilog.Filters;

var builder = Host.CreateApplicationBuilder(args);
builder.AddAzureQueue("log-queue");
builder.Services.AddHostedService<LoadGenerator>();
var host = builder.Build();

var sink = new QueueLogSink(host.Services.GetRequiredService<QueueClient>());
Log.Logger = new LoggerConfiguration()
    .WriteTo.Logger(lc => lc
        .Filter.ByIncludingOnly(Matching.WithProperty("DocGuid"))
        .WriteTo.Sink(sink, new BatchingOptions { BatchSizeLimit = 5000, QueueLimit = 500_000 }))
    .WriteTo.Logger(lc => lc
        .Filter.ByExcluding(Matching.WithProperty("DocGuid"))
        .WriteTo.Console())
    .CreateLogger();

try { host.Run(); } finally { Log.CloseAndFlush(); }
