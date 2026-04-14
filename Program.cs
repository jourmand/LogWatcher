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
    client.DefaultRequestHeaders.Accept
          .ParseAdd("application/vnd.github+json");
    client.DefaultRequestHeaders.Authorization =
        new AuthenticationHeaderValue("Bearer", cfg.PersonalAccessToken);
    client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    client.Timeout = TimeSpan.FromSeconds(30);
});

// Default unnamed client (used by NotificationService for Slack)
builder.Services.AddHttpClient();

// ── Singleton services ────────────────────────────────────────────────────────
// These hold state (DB connection, ES client, checkpoint) so must be Singleton
builder.Services.AddSingleton<DeduplicationStore>();
builder.Services.AddSingleton<ElasticsearchPoller>();
builder.Services.AddSingleton<ErrorClassifier>();

// ── Scoped services ───────────────────────────────────────────────────────────
// GitHubService and NotificationService are stateless — Scoped is fine.
// Workers resolve them via IServiceScopeFactory (injected automatically by host).
builder.Services.AddScoped<GitHubService>();
builder.Services.AddScoped<NotificationService>();

// ── Background workers ────────────────────────────────────────────────────────
builder.Services.AddHostedService<LogWatcherWorker>();
builder.Services.AddHostedService<PrPollerWorker>();

// ── Logging ───────────────────────────────────────────────────────────────────
builder.Logging.ClearProviders();
builder.Logging.AddConsole(opts =>
{
    opts.TimestampFormat = "HH:mm:ss ";
});

var host = builder.Build();
host.Run();
