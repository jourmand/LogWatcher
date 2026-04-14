using LogWatcher.Configuration;
using LogWatcher.Services;
using LogWatcher.Workers;
using System.Net.Http.Headers;

var builder = Host.CreateApplicationBuilder(args);

// ── Configuration ─────────────────────────────────────────────────────────────
builder.Services.Configure<AppSettings>(
    builder.Configuration.GetSection("AppSettings"));

// ── Named HTTP client for GitHub ──────────────────────────────────────────────
builder.Services.AddHttpClient("github", (sp, client) =>
{
    var cfg = sp.GetRequiredService<IConfiguration>()
                .GetSection("AppSettings:GitHub")
                .Get<GitHubSettings>();

    if (cfg == null)
        throw new InvalidOperationException(
            "AppSettings:GitHub section is missing from configuration.");

    client.BaseAddress = new Uri("https://api.github.com");
    client.DefaultRequestHeaders.UserAgent.ParseAdd("LogWatcher/1.0");
    client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    client.DefaultRequestHeaders.Authorization =
        new AuthenticationHeaderValue("Bearer", cfg.PersonalAccessToken);
    client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    client.Timeout = TimeSpan.FromSeconds(30);
});

builder.Services.AddHttpClient("freshservice", (sp, client) =>
{
    var cfg = sp.GetRequiredService<IConfiguration>()
        .GetSection("AppSettings:FreshService")
        .Get<FreshServiceSettings>();

    if (cfg == null)
        throw new InvalidOperationException(
            "AppSettings:FreshService section is missing from configuration.");

    if (!string.IsNullOrWhiteSpace(cfg.BaseUrl))
        client.BaseAddress = new Uri(cfg.BaseUrl.TrimEnd('/') + "/");

    client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    client.DefaultRequestHeaders.UserAgent.ParseAdd("LogWatcher/1.0");

    if (!string.IsNullOrWhiteSpace(cfg.ApiKey))
    {
        var basic = Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes($"{cfg.ApiKey}:X"));
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Basic", basic);
    }

    client.Timeout = TimeSpan.FromSeconds(30);
});

builder.Services.AddHttpClient(); // default unnamed client (Slack)

// ── Singleton services ────────────────────────────────────────────────────────
builder.Services.AddSingleton<DeduplicationStore>();
builder.Services.AddSingleton<ElasticsearchPoller>();    // direct ES client
builder.Services.AddSingleton<McpElasticsearchService>(); // MCP ES client
builder.Services.AddSingleton<ErrorClassifier>();
builder.Services.AddSingleton<FreshServiceService>();

// ── Scoped services ───────────────────────────────────────────────────────────
builder.Services.AddScoped<GitHubService>();
builder.Services.AddScoped<NotificationService>();

// ── Background workers ────────────────────────────────────────────────────────
// builder.Services.AddHostedService<LogWatcherWorker>();
// builder.Services.AddHostedService<PrPollerWorker>();
builder.Services.AddHostedService<FreshServiceWorker>();

// ── Logging ───────────────────────────────────────────────────────────────────
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(opts =>
{
    opts.TimestampFormat = "HH:mm:ss ";
    opts.SingleLine = true;
});

var host = builder.Build();
host.Run();
