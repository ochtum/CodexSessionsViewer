using System.Collections.Concurrent;
using System.Globalization;
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
    private const string DefaultPricingCatalogUrl = "https://raw.githubusercontent.com/BerriAI/litellm/main/model_prices_and_context_window.json";
    private const string DefaultPricingCatalogCachePath = ".cache/model-pricing-cache.json";
    private const string DefaultOpenAiModelsEndpoint = "https://api.openai.com/v1/models";
    private static readonly TimeSpan DefaultPricingCatalogRefreshInterval = TimeSpan.FromHours(12);
    private static readonly TimeSpan DefaultPricingCatalogRequestTimeout = TimeSpan.FromSeconds(10);
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
    private static readonly JsonSerializerOptions PricingJsonOptions = new()
    {
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };
    private static readonly JsonSerializerOptions PricingJsonWriteOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    private static readonly JsonDocumentOptions PricingJsonDocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    private readonly IWebHostEnvironment _environment;
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ModelCatalogService> _logger;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _pricingRefreshLock = new(1, 1);
    private readonly ConcurrentDictionary<string, byte> _loggedUnpricedModels = new(StringComparer.OrdinalIgnoreCase);

    private PricingCatalogSnapshot? _pricingSnapshot;
    private PricingCatalogRefreshSnapshot _pricingRefreshSnapshot = PricingCatalogRefreshSnapshot.Empty;
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

    public CostBreakdownUsd? TryCalculateCostBreakdownUsd(
        string rawModel,
        long inputTokens,
        long cachedInputTokens,
        long outputTokens,
        long reasoningOutputTokens)
    {
        if (!TryResolvePricing(rawModel, out var pricing))
        {
            return null;
        }

        var normalizedInputTokens = Math.Max(inputTokens, 0);
        var normalizedCachedInputTokens = Math.Clamp(cachedInputTokens, 0, normalizedInputTokens);
        var normalizedOutputTokens = Math.Max(outputTokens, 0);
        var normalizedReasoningOutputTokens = Math.Clamp(reasoningOutputTokens, 0, normalizedOutputTokens);
        var nonCachedInputTokens = Math.Max(normalizedInputTokens - normalizedCachedInputTokens, 0);
        var nonReasoningOutputTokens = Math.Max(normalizedOutputTokens - normalizedReasoningOutputTokens, 0);

        var inputCostUsd = ((decimal)nonCachedInputTokens / 1_000_000m) * pricing.InputCostPerMillionTokens;
        var cachedInputCostUsd = ((decimal)normalizedCachedInputTokens / 1_000_000m) * pricing.CachedInputCostPerMillionTokens;
        var outputCostUsd = ((decimal)nonReasoningOutputTokens / 1_000_000m) * pricing.OutputCostPerMillionTokens;
        var reasoningCostUsd = ((decimal)normalizedReasoningOutputTokens / 1_000_000m) * pricing.OutputCostPerMillionTokens;

        return new CostBreakdownUsd(
            inputCostUsd,
            cachedInputCostUsd,
            outputCostUsd,
            reasoningCostUsd);
    }

    public decimal? TryCalculateCostUsd(string rawModel, long inputTokens, long cachedInputTokens, long outputTokens)
    {
        return TryCalculateCostBreakdownUsd(rawModel, inputTokens, cachedInputTokens, outputTokens, 0)?.TotalCostUsd;
    }

    public ModelCatalogStatusDto GetStatus()
    {
        var pricing = GetPricingCatalog();
        var pricingSettings = GetPricingCatalogSettings();
        var pricingRefresh = _pricingRefreshSnapshot;
        var openAi = _openAiSnapshot;
        return new ModelCatalogStatusDto
        {
            PricingCatalogPath = pricing.Path,
            PricingCatalogUpdatedAt = FormatTimestamp(pricing.LastWriteTimeUtc),
            PricingModelCount = pricing.Models.Count,
            AliasCount = pricing.Aliases.Count,
            PricingCatalogUrl = pricingSettings.CatalogUrl,
            PricingCatalogCachePath = pricingSettings.CachePath,
            PricingCatalogLastRefreshedAt = FormatTimestamp(pricingRefresh.LastRefreshedAt),
            PricingCatalogLastError = pricingRefresh.LastError,
            OpenAiApiConfigured = openAi.ApiKeyConfigured,
            OpenAiModelsEndpoint = openAi.Endpoint,
            OpenAiModelsLastRefreshedAt = FormatTimestamp(openAi.LastRefreshedAt),
            OpenAiModelCount = openAi.ModelIds.Count,
            OpenAiModelsLastError = openAi.LastError,
        };
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        return Task.WhenAll(
            RunPricingCatalogRefreshLoopAsync(stoppingToken),
            RunOpenAiCatalogRefreshLoopAsync(stoppingToken));
    }

    private async Task RunPricingCatalogRefreshLoopAsync(CancellationToken stoppingToken)
    {
        var loggedDisabled = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            var settings = GetPricingCatalogSettings();
            if (!settings.IsRemoteEnabled)
            {
                if (!loggedDisabled)
                {
                    _logger.LogInformation(
                        "Pricing catalog auto-refresh is disabled. Set {Section}:CatalogUrl to enable LiteLLM pricing sync.",
                        PricingSectionName);
                    loggedDisabled = true;
                }

                await DelayAsync(TimeSpan.FromMinutes(5), stoppingToken);
                continue;
            }

            loggedDisabled = false;
            await RefreshPricingCatalogAsync(settings, stoppingToken);
            await DelayAsync(settings.RefreshInterval, stoppingToken);
        }
    }

    private async Task RunOpenAiCatalogRefreshLoopAsync(CancellationToken stoppingToken)
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

    private async Task RefreshPricingCatalogAsync(PricingCatalogSettings settings, CancellationToken cancellationToken)
    {
        await _pricingRefreshLock.WaitAsync(cancellationToken);
        try
        {
            using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            requestCancellation.CancelAfter(settings.RequestTimeout);

            using var request = new HttpRequestMessage(HttpMethod.Get, settings.CatalogUrl);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.UserAgent.ParseAdd("CodexSessionsViewer/1.0");

            var client = _httpClientFactory.CreateClient();
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                requestCancellation.Token);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(requestCancellation.Token);
            using var document = await JsonDocument.ParseAsync(
                stream,
                PricingJsonDocumentOptions,
                requestCancellation.Token);

            var catalogDocument = MergePricingCatalogDocuments(
                ConvertLiteLlmCatalog(document.RootElement),
                LoadSupplementalPricingCatalogDocument(settings.FallbackCatalogPath));
            var snapshot = await PersistPricingCatalogCacheAsync(
                settings.CachePath,
                catalogDocument,
                requestCancellation.Token);

            lock (_sync)
            {
                _pricingSnapshot = snapshot;
            }

            _pricingRefreshSnapshot = new PricingCatalogRefreshSnapshot(
                settings.CatalogUrl,
                settings.CachePath,
                DateTimeOffset.UtcNow,
                string.Empty);

            _logger.LogInformation(
                "Refreshed pricing catalog: {Count} models from {Url}",
                snapshot.Models.Count,
                settings.CatalogUrl);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException ex)
        {
            _pricingRefreshSnapshot = _pricingRefreshSnapshot with
            {
                CatalogUrl = settings.CatalogUrl,
                CachePath = settings.CachePath,
                LastError = ex.Message,
            };
            _logger.LogWarning(ex, "Timed out refreshing pricing catalog from {Url}", settings.CatalogUrl);
        }
        catch (Exception ex)
        {
            _pricingRefreshSnapshot = _pricingRefreshSnapshot with
            {
                CatalogUrl = settings.CatalogUrl,
                CachePath = settings.CachePath,
                LastError = ex.Message,
            };
            _logger.LogWarning(ex, "Failed to refresh pricing catalog from {Url}", settings.CatalogUrl);
        }
        finally
        {
            _pricingRefreshLock.Release();
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
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            pricing = default!;
            return false;
        }

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

        var catalog = GetPricingCatalog();
        foreach (var candidate in BuildPricingCandidates(trimmed))
        {
            if (TryResolvePricingCandidate(catalog, candidate, out pricing))
            {
                return true;
            }
        }

        pricing = default!;
        LogMissingPricing(trimmed);
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

        var candidates = BuildPricingCandidates(model);
        foreach (var candidate in candidates)
        {
            if (snapshot.ModelIds.Contains(candidate))
            {
                return true;
            }

            foreach (var officialModel in snapshot.ModelIds)
            {
                if (candidate.StartsWith(officialModel + "-", StringComparison.OrdinalIgnoreCase)
                    || officialModel.StartsWith(candidate + "-", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static IReadOnlyList<string> BuildPricingCandidates(string rawModel)
    {
        var candidates = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddCandidate(rawModel);
        AddCandidate(NormalizePricingModel(rawModel));

        return candidates;

        void AddCandidate(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            var trimmed = value.Trim();
            if (seen.Add(trimmed))
            {
                candidates.Add(trimmed);
            }
        }
    }

    private static bool TryResolvePricingCandidate(
        PricingCatalogSnapshot catalog,
        string candidate,
        out PricingCatalogEntry pricing)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = candidate;
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
        var settings = GetPricingCatalogSettings();
        var resolvedPath = File.Exists(settings.CachePath)
            ? settings.CachePath
            : settings.FallbackCatalogPath;
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

            _pricingSnapshot = LoadPricingCatalogWithFallback(settings, resolvedPath, fileInfo, version);
            return _pricingSnapshot;
        }
    }

    private PricingCatalogSnapshot LoadPricingCatalogWithFallback(
        PricingCatalogSettings settings,
        string path,
        FileInfo fileInfo,
        long version)
    {
        try
        {
            return LoadPricingCatalog(path, fileInfo, version);
        }
        catch (Exception ex) when (!string.Equals(path, settings.FallbackCatalogPath, PathComparison))
        {
            _logger.LogWarning(
                ex,
                "Failed to load pricing catalog cache {Path}. Falling back to {FallbackPath}.",
                path,
                settings.FallbackCatalogPath);

            var fallbackInfo = new FileInfo(settings.FallbackCatalogPath);
            var fallbackVersion = ComputeCatalogVersion(settings.FallbackCatalogPath, fallbackInfo);
            return LoadPricingCatalog(settings.FallbackCatalogPath, fallbackInfo, fallbackVersion);
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
        return CreatePricingCatalogSnapshot(path, version, fileInfo.LastWriteTimeUtc, document);
    }

    private PricingCatalogDocument LoadSupplementalPricingCatalogDocument(string path)
    {
        try
        {
            var fileInfo = new FileInfo(path);
            if (!fileInfo.Exists)
            {
                return new PricingCatalogDocument();
            }

            using var stream = fileInfo.OpenRead();
            return JsonSerializer.Deserialize<PricingCatalogDocument>(stream, PricingJsonOptions) ?? new PricingCatalogDocument();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load supplemental pricing catalog from {Path}", path);
            return new PricingCatalogDocument();
        }
    }

    private PricingCatalogSnapshot CreatePricingCatalogSnapshot(
        string path,
        long version,
        DateTimeOffset? lastWriteTimeUtc,
        PricingCatalogDocument document)
    {
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
            lastWriteTimeUtc,
            models,
            aliases);
    }

    private PricingCatalogSettings GetPricingCatalogSettings()
    {
        var configuredFallbackPath = _configuration.GetValue<string>($"{PricingSectionName}:CatalogPath");
        var configuredCatalogUrl = _configuration.GetValue<string>($"{PricingSectionName}:CatalogUrl");
        var configuredCachePath = _configuration.GetValue<string>($"{PricingSectionName}:CatalogCachePath");
        var refreshIntervalMinutes = _configuration.GetValue<int?>($"{PricingSectionName}:CatalogRefreshIntervalMinutes");
        var requestTimeoutSeconds = _configuration.GetValue<int?>($"{PricingSectionName}:CatalogRequestTimeoutSeconds");

        return new PricingCatalogSettings(
            string.IsNullOrWhiteSpace(configuredCatalogUrl) ? DefaultPricingCatalogUrl : configuredCatalogUrl.Trim(),
            ResolveAppRelativePath(string.IsNullOrWhiteSpace(configuredFallbackPath)
                ? DefaultPricingCatalogPath
                : configuredFallbackPath.Trim()),
            ResolveAppRelativePath(string.IsNullOrWhiteSpace(configuredCachePath)
                ? DefaultPricingCatalogCachePath
                : configuredCachePath.Trim()),
            refreshIntervalMinutes.HasValue && refreshIntervalMinutes.Value > 0
                ? TimeSpan.FromMinutes(refreshIntervalMinutes.Value)
                : DefaultPricingCatalogRefreshInterval,
            requestTimeoutSeconds.HasValue && requestTimeoutSeconds.Value > 0
                ? TimeSpan.FromSeconds(requestTimeoutSeconds.Value)
                : DefaultPricingCatalogRequestTimeout);
    }

    private string ResolveAppRelativePath(string path)
    {
        if (Path.IsPathRooted(path))
        {
            return Path.GetFullPath(path);
        }

        return Path.GetFullPath(Path.Combine(_environment.ContentRootPath, path));
    }

    private async Task<PricingCatalogSnapshot> PersistPricingCatalogCacheAsync(
        string cachePath,
        PricingCatalogDocument document,
        CancellationToken cancellationToken)
    {
        var cacheDirectory = Path.GetDirectoryName(cachePath);
        if (!string.IsNullOrWhiteSpace(cacheDirectory))
        {
            Directory.CreateDirectory(cacheDirectory);
        }

        var json = JsonSerializer.Serialize(document, PricingJsonWriteOptions);
        var tempPath = cachePath + ".tmp";

        try
        {
            await File.WriteAllTextAsync(tempPath, json, cancellationToken);
            File.Move(tempPath, cachePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }

        var fileInfo = new FileInfo(cachePath);
        var version = ComputeCatalogVersion(cachePath, fileInfo);
        return CreatePricingCatalogSnapshot(
            cachePath,
            version,
            fileInfo.Exists ? fileInfo.LastWriteTimeUtc : DateTimeOffset.UtcNow,
            document);
    }

    private static PricingCatalogDocument ConvertLiteLlmCatalog(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("LiteLLM pricing catalog root must be a JSON object.");
        }

        var models = new Dictionary<string, PricingCatalogEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in root.EnumerateObject())
        {
            if (property.NameEquals("sample_spec") || property.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (!TryConvertLiteLlmPricingEntry(property.Value, out var entry))
            {
                continue;
            }

            models[property.Name] = entry;
        }

        return new PricingCatalogDocument
        {
            Models = models,
            Aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        };
    }

    private static PricingCatalogDocument MergePricingCatalogDocuments(
        PricingCatalogDocument primary,
        PricingCatalogDocument supplemental)
    {
        var models = new Dictionary<string, PricingCatalogEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in primary.Models ?? new Dictionary<string, PricingCatalogEntry>())
        {
            if (!string.IsNullOrWhiteSpace(pair.Key))
            {
                models[pair.Key.Trim()] = pair.Value;
            }
        }

        foreach (var pair in supplemental.Models ?? new Dictionary<string, PricingCatalogEntry>())
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || models.ContainsKey(pair.Key.Trim()))
            {
                continue;
            }

            models[pair.Key.Trim()] = pair.Value;
        }

        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in primary.Aliases ?? new Dictionary<string, string>())
        {
            if (!string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
            {
                aliases[pair.Key.Trim()] = pair.Value.Trim();
            }
        }

        foreach (var pair in supplemental.Aliases ?? new Dictionary<string, string>())
        {
            if (!string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
            {
                aliases[pair.Key.Trim()] = pair.Value.Trim();
            }
        }

        return new PricingCatalogDocument
        {
            Models = models,
            Aliases = aliases,
        };
    }

    private static bool TryConvertLiteLlmPricingEntry(JsonElement element, out PricingCatalogEntry entry)
    {
        var hasInputCost = TryReadDecimalProperty(element, "input_cost_per_token", out var inputCostPerToken);
        var hasCachedInputCost = TryReadDecimalProperty(element, "cache_read_input_token_cost", out var cachedInputCostPerToken);
        var hasOutputCost = TryReadDecimalProperty(element, "output_cost_per_token", out var outputCostPerToken);

        if (!hasInputCost && !hasCachedInputCost && !hasOutputCost)
        {
            entry = default!;
            return false;
        }

        entry = new PricingCatalogEntry
        {
            InputCostPerMillionTokens = hasInputCost ? inputCostPerToken * 1_000_000m : 0m,
            CachedInputCostPerMillionTokens = hasCachedInputCost ? cachedInputCostPerToken * 1_000_000m : 0m,
            OutputCostPerMillionTokens = hasOutputCost ? outputCostPerToken * 1_000_000m : 0m,
        };
        return true;
    }

    private static bool TryReadDecimalProperty(JsonElement element, string propertyName, out decimal value)
    {
        value = 0m;
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetDecimal(out value))
        {
            return true;
        }

        return property.ValueKind == JsonValueKind.String
            && decimal.TryParse(
                property.GetString(),
                NumberStyles.Float | NumberStyles.AllowThousands,
                CultureInfo.InvariantCulture,
                out value);
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

    public sealed record CostBreakdownUsd(
        decimal InputCostUsd,
        decimal CachedInputCostUsd,
        decimal OutputCostUsd,
        decimal ReasoningCostUsd)
    {
        public decimal TotalCostUsd => InputCostUsd + CachedInputCostUsd + OutputCostUsd + ReasoningCostUsd;
    }

    private sealed record PricingCatalogRefreshSnapshot(
        string CatalogUrl,
        string CachePath,
        DateTimeOffset? LastRefreshedAt,
        string LastError)
    {
        public static PricingCatalogRefreshSnapshot Empty { get; } = new(
            string.Empty,
            string.Empty,
            null,
            string.Empty);
    }

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

    private sealed record PricingCatalogSettings(
        string CatalogUrl,
        string FallbackCatalogPath,
        string CachePath,
        TimeSpan RefreshInterval,
        TimeSpan RequestTimeout)
    {
        public bool IsRemoteEnabled => Uri.TryCreate(CatalogUrl, UriKind.Absolute, out _);
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
