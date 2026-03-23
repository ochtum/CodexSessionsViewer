using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodexSessionsViewer.Models;
using Microsoft.AspNetCore.Hosting;

namespace CodexSessionsViewer.Services;

public sealed class ModelCatalogService : BackgroundService
{
    private const string PricingSectionName = "Pricing";
    private const string OpenAiSectionName = "OpenAI";
    private const string DefaultPricingCatalogPath = "model-pricing.json";
    private const string DefaultOpenAiModelsEndpoint = "https://api.openai.com/v1/models";
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
    private static readonly JsonSerializerOptions PricingJsonOptions = new()
    {
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private readonly IWebHostEnvironment _environment;
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ModelCatalogService> _logger;
    private readonly object _sync = new();
    private readonly ConcurrentDictionary<string, byte> _loggedUnpricedModels = new(StringComparer.OrdinalIgnoreCase);

    private PricingCatalogSnapshot? _pricingSnapshot;
    private OpenAiModelCatalogSnapshot _openAiSnapshot = OpenAiModelCatalogSnapshot.Empty;

    public ModelCatalogService(
        IWebHostEnvironment environment,
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        ILogger<ModelCatalogService> logger)
    {
        _environment = environment;
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public long GetPricingVersion()
    {
        return GetPricingCatalog().Version;
    }

    public decimal? TryCalculateCostUsd(string rawModel, long inputTokens, long cachedInputTokens, long outputTokens)
    {
        if (!TryResolvePricing(rawModel, out var pricing))
        {
            return null;
        }

        var nonCachedInputTokens = Math.Max(inputTokens - cachedInputTokens, 0);
        return ((decimal)nonCachedInputTokens / 1_000_000m) * pricing.InputCostPerMillionTokens
            + ((decimal)Math.Max(cachedInputTokens, 0) / 1_000_000m) * pricing.CachedInputCostPerMillionTokens
            + ((decimal)Math.Max(outputTokens, 0) / 1_000_000m) * pricing.OutputCostPerMillionTokens;
    }

    public ModelCatalogStatusDto GetStatus()
    {
        var pricing = GetPricingCatalog();
        var openAi = _openAiSnapshot;
        return new ModelCatalogStatusDto
        {
            PricingCatalogPath = pricing.Path,
            PricingCatalogUpdatedAt = FormatTimestamp(pricing.LastWriteTimeUtc),
            PricingModelCount = pricing.Models.Count,
            AliasCount = pricing.Aliases.Count,
            OpenAiApiConfigured = openAi.ApiKeyConfigured,
            OpenAiModelsEndpoint = openAi.Endpoint,
            OpenAiModelsLastRefreshedAt = FormatTimestamp(openAi.LastRefreshedAt),
            OpenAiModelCount = openAi.ModelIds.Count,
            OpenAiModelsLastError = openAi.LastError,
        };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var loggedDisabled = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            var settings = GetOpenAiCatalogSettings();
            if (!settings.IsEnabled)
            {
                SetOpenAiSnapshot(OpenAiModelCatalogSnapshot.Disabled(settings.Endpoint, settings.IsApiKeyConfigured));
                if (!loggedDisabled)
                {
                    _logger.LogInformation(
                        "OpenAI model catalog auto-refresh is disabled. Set OPENAI_API_KEY or OpenAI:ApiKey to enable official model discovery.");
                    loggedDisabled = true;
                }

                await DelayAsync(TimeSpan.FromMinutes(5), stoppingToken);
                continue;
            }

            loggedDisabled = false;
            await RefreshOpenAiCatalogAsync(settings, stoppingToken);
            await DelayAsync(settings.RefreshInterval, stoppingToken);
        }
    }

    private static async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task RefreshOpenAiCatalogAsync(OpenAiCatalogSettings settings, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, settings.Endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.UserAgent.ParseAdd("CodexSessionsViewer/1.0");

            var client = _httpClientFactory.CreateClient();
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

            var modelIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (document.RootElement.TryGetProperty("data", out var data)
                && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in data.EnumerateArray())
                {
                    var id = item.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String
                        ? idElement.GetString()
                        : null;
                    if (!string.IsNullOrWhiteSpace(id))
                    {
                        modelIds.Add(id.Trim());
                    }
                }
            }

            SetOpenAiSnapshot(new OpenAiModelCatalogSnapshot(
                true,
                settings.Endpoint,
                DateTimeOffset.UtcNow,
                string.Empty,
                modelIds));

            _logger.LogInformation("Refreshed OpenAI model catalog: {Count} models from {Endpoint}", modelIds.Count, settings.Endpoint);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            var previous = _openAiSnapshot;
            SetOpenAiSnapshot(new OpenAiModelCatalogSnapshot(
                true,
                settings.Endpoint,
                previous.LastRefreshedAt,
                ex.Message,
                previous.ModelIds));
            _logger.LogWarning(ex, "Failed to refresh OpenAI model catalog from {Endpoint}", settings.Endpoint);
        }
    }

    private bool TryResolvePricing(string rawModel, out PricingCatalogEntry pricing)
    {
        var trimmed = rawModel.Trim();
        if (IsOpenRouterFreeModel(trimmed))
        {
            pricing = new PricingCatalogEntry
            {
                InputCostPerMillionTokens = 0m,
                CachedInputCostPerMillionTokens = 0m,
                OutputCostPerMillionTokens = 0m,
            };
            return true;
        }

        var normalized = NormalizePricingModel(rawModel);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            pricing = default!;
            return false;
        }

        var catalog = GetPricingCatalog();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = normalized;
        while (!string.IsNullOrWhiteSpace(current) && visited.Add(current))
        {
            if (TryGetExactOrVersionedModelPricing(catalog, current, out pricing))
            {
                return true;
            }

            if (!catalog.Aliases.TryGetValue(current, out current!))
            {
                break;
            }
        }

        pricing = default!;
        LogMissingPricing(normalized);
        return false;
    }

    private void LogMissingPricing(string normalizedModel)
    {
        if (!_loggedUnpricedModels.TryAdd(normalizedModel, 0))
        {
            return;
        }

        var catalog = GetPricingCatalog();
        if (IsKnownOfficialOpenAiModel(normalizedModel))
        {
            _logger.LogWarning(
                "Official OpenAI model {Model} was detected but has no entry in pricing catalog {CatalogPath}. Cost will stay unavailable until the catalog is updated.",
                normalizedModel,
                catalog.Path);
            return;
        }

        _logger.LogInformation(
            "Model {Model} has no pricing entry in catalog {CatalogPath}. Cost will stay unavailable for this model.",
            normalizedModel,
            catalog.Path);
    }

    private bool IsKnownOfficialOpenAiModel(string model)
    {
        var snapshot = _openAiSnapshot;
        if (snapshot.ModelIds.Count == 0)
        {
            return false;
        }

        if (snapshot.ModelIds.Contains(model))
        {
            return true;
        }

        foreach (var candidate in snapshot.ModelIds)
        {
            if (model.StartsWith(candidate + "-", StringComparison.OrdinalIgnoreCase)
                || candidate.StartsWith(model + "-", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryGetExactOrVersionedModelPricing(
        PricingCatalogSnapshot catalog,
        string model,
        out PricingCatalogEntry pricing)
    {
        if (catalog.Models.TryGetValue(model, out pricing!))
        {
            return true;
        }

        foreach (var pair in catalog.Models.OrderByDescending(item => item.Key.Length))
        {
            if (model.StartsWith(pair.Key + "-", StringComparison.OrdinalIgnoreCase))
            {
                pricing = pair.Value;
                return true;
            }
        }

        pricing = default!;
        return false;
    }

    private PricingCatalogSnapshot GetPricingCatalog()
    {
        var resolvedPath = ResolvePricingCatalogPath();
        var fileInfo = new FileInfo(resolvedPath);
        var version = ComputeCatalogVersion(resolvedPath, fileInfo);
        var cached = _pricingSnapshot;
        if (cached is not null
            && cached.Version == version
            && string.Equals(cached.Path, resolvedPath, PathComparison))
        {
            return cached;
        }

        lock (_sync)
        {
            cached = _pricingSnapshot;
            if (cached is not null
                && cached.Version == version
                && string.Equals(cached.Path, resolvedPath, PathComparison))
            {
                return cached;
            }

            _pricingSnapshot = LoadPricingCatalog(resolvedPath, fileInfo, version);
            return _pricingSnapshot;
        }
    }

    private PricingCatalogSnapshot LoadPricingCatalog(string path, FileInfo fileInfo, long version)
    {
        if (!fileInfo.Exists)
        {
            _logger.LogWarning("Pricing catalog file was not found: {Path}", path);
            return new PricingCatalogSnapshot(
                path,
                version,
                null,
                new Dictionary<string, PricingCatalogEntry>(StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        }

        using var stream = fileInfo.OpenRead();
        var document = JsonSerializer.Deserialize<PricingCatalogDocument>(stream, PricingJsonOptions) ?? new PricingCatalogDocument();

        var models = new Dictionary<string, PricingCatalogEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in document.Models ?? new Dictionary<string, PricingCatalogEntry>())
        {
            if (string.IsNullOrWhiteSpace(pair.Key))
            {
                continue;
            }

            models[pair.Key.Trim()] = pair.Value;
        }

        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in document.Aliases ?? new Dictionary<string, string>())
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value))
            {
                continue;
            }

            aliases[pair.Key.Trim()] = pair.Value.Trim();
        }

        return new PricingCatalogSnapshot(
            path,
            version,
            fileInfo.LastWriteTimeUtc,
            models,
            aliases);
    }

    private string ResolvePricingCatalogPath()
    {
        var configuredPath = _configuration.GetValue<string>($"{PricingSectionName}:CatalogPath");
        var path = string.IsNullOrWhiteSpace(configuredPath)
            ? DefaultPricingCatalogPath
            : configuredPath.Trim();
        if (Path.IsPathRooted(path))
        {
            return Path.GetFullPath(path);
        }

        return Path.GetFullPath(Path.Combine(_environment.ContentRootPath, path));
    }

    private OpenAiCatalogSettings GetOpenAiCatalogSettings()
    {
        var apiKey = _configuration.GetValue<string>($"{OpenAiSectionName}:ApiKey");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        }

        var endpoint = _configuration.GetValue<string>($"{OpenAiSectionName}:ModelsEndpoint");
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            endpoint = DefaultOpenAiModelsEndpoint;
        }

        var refreshIntervalMinutes = _configuration.GetValue<int?>($"{OpenAiSectionName}:ModelsRefreshIntervalMinutes") ?? 720;
        if (refreshIntervalMinutes <= 0)
        {
            refreshIntervalMinutes = 720;
        }

        return new OpenAiCatalogSettings(
            endpoint.Trim(),
            apiKey?.Trim() ?? string.Empty,
            TimeSpan.FromMinutes(refreshIntervalMinutes));
    }

    private void SetOpenAiSnapshot(OpenAiModelCatalogSnapshot snapshot)
    {
        lock (_sync)
        {
            _openAiSnapshot = snapshot;
        }
    }

    private static string NormalizePricingModel(string rawModel)
    {
        var model = rawModel.Trim();
        foreach (var prefix in new[] { "openai/", "azure/", "openrouter/openai/" })
        {
            if (model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return model[prefix.Length..];
            }
        }

        return model;
    }

    private static bool IsOpenRouterFreeModel(string model)
    {
        return string.Equals(model, "openrouter/free", StringComparison.OrdinalIgnoreCase)
            || (model.StartsWith("openrouter/", StringComparison.OrdinalIgnoreCase)
                && model.EndsWith(":free", StringComparison.OrdinalIgnoreCase));
    }

    private static long ComputeCatalogVersion(string path, FileInfo fileInfo)
    {
        return HashCode.Combine(
            path,
            fileInfo.Exists ? fileInfo.LastWriteTimeUtc.Ticks : 0L,
            fileInfo.Exists ? fileInfo.Length : 0L);
    }

    private static string FormatTimestamp(DateTimeOffset? value)
    {
        return value.HasValue ? value.Value.ToString("O") : string.Empty;
    }

    private sealed record PricingCatalogSnapshot(
        string Path,
        long Version,
        DateTimeOffset? LastWriteTimeUtc,
        IReadOnlyDictionary<string, PricingCatalogEntry> Models,
        IReadOnlyDictionary<string, string> Aliases);

    private sealed record PricingCatalogDocument
    {
        [JsonPropertyName("models")]
        public Dictionary<string, PricingCatalogEntry>? Models { get; init; }

        [JsonPropertyName("aliases")]
        public Dictionary<string, string>? Aliases { get; init; }
    }

    private sealed record PricingCatalogEntry
    {
        [JsonPropertyName("input_cost_per_million_tokens")]
        public decimal InputCostPerMillionTokens { get; init; }

        [JsonPropertyName("cached_input_cost_per_million_tokens")]
        public decimal CachedInputCostPerMillionTokens { get; init; }

        [JsonPropertyName("output_cost_per_million_tokens")]
        public decimal OutputCostPerMillionTokens { get; init; }
    }

    private sealed record OpenAiCatalogSettings(
        string Endpoint,
        string ApiKey,
        TimeSpan RefreshInterval)
    {
        public bool IsApiKeyConfigured => !string.IsNullOrWhiteSpace(ApiKey);

        public bool IsEnabled => IsApiKeyConfigured && Uri.TryCreate(Endpoint, UriKind.Absolute, out _);
    }

    private sealed record OpenAiModelCatalogSnapshot(
        bool ApiKeyConfigured,
        string Endpoint,
        DateTimeOffset? LastRefreshedAt,
        string LastError,
        HashSet<string> ModelIds)
    {
        public static OpenAiModelCatalogSnapshot Empty { get; } = Disabled(DefaultOpenAiModelsEndpoint, false);

        public static OpenAiModelCatalogSnapshot Disabled(string endpoint, bool apiKeyConfigured)
        {
            return new OpenAiModelCatalogSnapshot(
                apiKeyConfigured,
                endpoint,
                null,
                string.Empty,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }
    }
}
