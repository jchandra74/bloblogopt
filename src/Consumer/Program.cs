using Consumer;

var builder = Host.CreateApplicationBuilder(args);
builder.AddKeyedAzureQueue("log-queue");
builder.AddKeyedAzureQueue("poison-queue");
builder.AddAzureBlobContainerClient("log-container");
builder.Services.AddHostedService<Worker>();
builder.Build().Run();
