using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using CoverageMcpServer.Services;

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});

builder.Services.AddSingleton<IPathGuard, PathGuard>();
builder.Services.AddSingleton<IFileService, FileService>();
builder.Services.AddSingleton<ISessionManager, SessionManager>();
builder.Services.AddSingleton<IProcessRunner, ProcessRunner>();
builder.Services.AddSingleton<ICoberturaService, CoberturaService>();
builder.Services.AddSingleton<ICodeInserter, CodeInserter>();

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
