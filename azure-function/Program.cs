using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.ApplicationInsights.WorkerService;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults() // no lambda here, avoids ambiguity
    .ConfigureServices(services =>
    {
        services.AddApplicationInsightsTelemetryWorkerService();
    })
    .Build();

host.Run();
