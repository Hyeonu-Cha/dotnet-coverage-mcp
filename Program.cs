using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using DotNetCoverageMcp.Services;

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
    .AddMcpServer(options =>
    {
        // Sent in the initialize result; clients put it in the model's context, so it is the
        // one place to state the intended workflow before any tool is chosen.
        options.ServerInstructions = """
            .NET test coverage tools. To bring files to a coverage target (every file in the scope at 80% line and branch by default):
            1. get_test_environment once per test project; write every test with the framework attributes, mocking and assertion libraries it reports. describe_test_target on a source file tells how to construct its class in a test (which dependencies to mock, which need a real instance) and whether a unit or integration test fits.
            2. run_tests_with_coverage on the test project. Omit filter to run every test; use skipReport: true inside the loop.
            3. get_coverage_gaps with the Cobertura path and the scope (a .cs file, folder, .csproj, or comma-separated batch from get_source_files). It lists, per file below target, the methods to test with their uncovered branches and the source around them. Do not read the coverage XML yourself.
            4. append_test_code to add tests, then repeat from 2. testsFailed / buildError responses say what to fix; get_coverage_diff shows what changed; cleanup_session at the end.
            Entry points, DI registration, generated code, data-only models and test projects are not targets and are skipped automatically.
            """;
    })
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
