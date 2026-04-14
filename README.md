# LogWatcher — Autonomous Bug-Fixing Pipeline (.NET 8)

Reads your Elasticsearch application logs, uses Claude AI to classify errors,
creates GitHub Issues, and assigns them to GitHub Copilot coding agent to
automatically produce pull requests for your review.

## Architecture

```
Elasticsearch → Log Poller → AI Classifier → Deduplicator → GitHub Issue → Copilot Agent → PR
                                                                                          ↓
                                                                              Slack notification
```

Two always-running `BackgroundService` workers:
- **LogWatcherWorker** — polls ES every 60s, classifies, creates issues
- **PrPollerWorker** — polls GitHub every 30s, notifies when PRs are ready

## Prerequisites

1. **.NET 8 SDK**
2. **Elasticsearch** with application logs
3. **GitHub Copilot Pro/Pro+/Business/Enterprise** subscription
4. **Copilot cloud agent enabled** on your repository
   (Repo Settings → Copilot → Coding agent → Enable)
5. **GitHub Personal Access Token** (fine-grained) with:
   - Actions: Read & Write
   - Contents: Read & Write
   - Issues: Read & Write
   - Pull Requests: Read & Write
6. **Anthropic API key** (for error classification)
7. **(Optional)** Slack incoming webhook URL for notifications

## Quick Start

```bash
# 1. Clone / copy the project
cd LogWatcher

# 2. Fill in your credentials
#    Edit appsettings.json  OR  use environment variables (recommended for prod)

# 3. Run
dotnet run
```

## Configuration

Edit `appsettings.json` or set environment variables:

| Setting | Env var | Description |
|---------|---------|-------------|
| `Elasticsearch:Url` | `AppSettings__Elasticsearch__Url` | ES endpoint |
| `Elasticsearch:IndexPattern` | `AppSettings__Elasticsearch__IndexPattern` | e.g. `logs-*` |
| `Elasticsearch:LevelField` | `AppSettings__Elasticsearch__LevelField` | Field name for log level |
| `GitHub:PersonalAccessToken` | `AppSettings__GitHub__PersonalAccessToken` | Your PAT |
| `GitHub:Owner` | `AppSettings__GitHub__Owner` | GitHub username/org |
| `GitHub:Repo` | `AppSettings__GitHub__Repo` | Repository name |
| `GitHub:MaxIssuesPerHour` | `AppSettings__GitHub__MaxIssuesPerHour` | Rate limit guard (default: 5) |
| `Anthropic:ApiKey` | `AppSettings__Anthropic__ApiKey` | Claude API key |
| `Watcher:PollIntervalSeconds` | `AppSettings__Watcher__PollIntervalSeconds` | ES poll interval (default: 60) |
| `Watcher:DeduplicationWindowHours` | `AppSettings__Watcher__DeduplicationWindowHours` | Dedup window (default: 24h) |
| `Notifications:SlackWebhookUrl` | `AppSettings__Notifications__SlackWebhookUrl` | Slack webhook (optional) |

## Docker

```bash
docker build -t logwatcher .

docker run -d \
  --name logwatcher \
  -v logwatcher-data:/app/data \
  -e AppSettings__Elasticsearch__Url=http://your-es:9200 \
  -e AppSettings__GitHub__PersonalAccessToken=ghp_xxx \
  -e AppSettings__GitHub__Owner=your-username \
  -e AppSettings__GitHub__Repo=your-repo \
  -e AppSettings__Anthropic__ApiKey=sk-ant-xxx \
  -e AppSettings__Notifications__SlackWebhookUrl=https://hooks.slack.com/... \
  logwatcher
```

## Elasticsearch field mapping

The watcher maps these fields from your log documents (configurable):

| Config key | Default | Typical Serilog/NLog value |
|-----------|---------|---------------------------|
| `LevelField` | `level` | `level` / `Level` |
| `MessageField` | `message` | `message` / `@message` |
| `ExceptionField` | `exception` | `exception` |
| `ServiceField` | `service` | `ServiceName` / `Application` |
| `TimestampField` | `@timestamp` | `@timestamp` |

## Important notes

- **Always review Copilot PRs** before merging — never auto-merge in production
- The SQLite database (`logwatcher.db`) persists seen fingerprints across restarts
- Rate limiting is enforced: max 5 issues/hour by default (configurable)
- Only "code-fixable" errors create issues — infra errors (OOM, disk full) are skipped
