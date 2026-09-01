using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenAI;
using Rag.Business.Options;
using Rag.Business.Services;
using Rag.Repository;

namespace Rag.Business;

public static class DependencyInjection
{
    /// <summary>
    /// Registers the business tier and, beneath it, the data-access tier. The web tier calls
    /// only this, so it never has to know the database or OpenAI exist.
    /// </summary>
    /// <param name="contentRootPath">
    /// Base directory a relative <c>Documents:RootPath</c> resolves against — the web tier's
    /// content root. Passed in so this tier needs no dependency on ASP.NET hosting types.
    /// </param>
    public static IServiceCollection AddBusiness(
        this IServiceCollection services,
        IConfiguration configuration,
        string contentRootPath)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRootPath);

        // ValidateOnStart: a missing or malformed setting fails at boot with a clear message,
        // rather than on the first request that happens to need it.
        AddValidatedOptions<OpenAiOptions>(services, configuration, OpenAiOptions.SectionName);
        AddValidatedOptions<DocumentsOptions>(services, configuration, DocumentsOptions.SectionName);
        AddValidatedOptions<ChunkingOptions>(services, configuration, ChunkingOptions.SectionName);
        AddValidatedOptions<SearchOptions>(services, configuration, SearchOptions.SectionName);

        services.AddSingleton(new DocumentRootPath(contentRootPath));

        AddOpenAi(services, configuration);

        services.AddScoped<IDocumentFileService, DocumentFileService>();
        services.AddSingleton<IChunkingService, ChunkingService>();
        services.AddScoped<IEmbeddingService, EmbeddingService>();
        services.AddScoped<IAnswerService, AnswerService>();
        services.AddScoped<IIngestionService, IngestionService>();
        services.AddScoped<ISearchService, SearchService>();
        services.AddScoped<IHealthService, HealthService>();

        // The tier below.
        services.AddRepository(configuration);

        return services;
    }

    private static void AddValidatedOptions<TOptions>(
        IServiceCollection services,
        IConfiguration configuration,
        string sectionName)
        where TOptions : class
    {
        services.AddOptions<TOptions>()
            .Bind(configuration.GetSection(sectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
    }

    private static void AddOpenAi(IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(OpenAiOptions.SectionName);
        var apiKey = section["ApiKey"];

        // Semantic Kernel's registration extensions need the client and model names at
        // registration time, so these values are read eagerly. The bound options object is still
        // validated by ValidateOnStart; these checks exist only so a missing value produces a
        // clear message here rather than an obscure failure later.
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                $"{OpenAiOptions.SectionName}:ApiKey is not configured. " +
                "Set it via user-secrets or the environment — never in appsettings.json.");
        }

        var embeddingModel = Fallback(section["EmbeddingModel"], "text-embedding-3-small");
        var chatModel = Fallback(section["ChatModel"], "gpt-4o-mini");

        // Parsed by hand rather than via GetValue<T>, which would pull in the
        // configuration-binder package for two integers.
        var maxRetries = int.TryParse(section["MaxRetries"], out var retries) ? retries : 5;
        var timeoutSeconds = int.TryParse(section["TimeoutSeconds"], out var timeout) ? timeout : 100;

        // Retry is configured on the client rather than layered on with a second policy: this
        // pipeline already honours Retry-After on 429, which is the expected response when a
        // project's rate limit is reached.
        var clientOptions = new OpenAIClientOptions
        {
            RetryPolicy = new ClientRetryPolicy(maxRetries),
            NetworkTimeout = TimeSpan.FromSeconds(timeoutSeconds),
        };

        // Endpoint is optional and exists only for an OpenAI-compatible gateway or proxy; left
        // unset, the client talks to api.openai.com.
        var endpoint = section["Endpoint"];
        if (!string.IsNullOrWhiteSpace(endpoint))
        {
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri))
            {
                throw new InvalidOperationException(
                    $"{OpenAiOptions.SectionName}:Endpoint must be an absolute URL when set. " +
                    "Leave it empty to use api.openai.com.");
            }

            clientOptions.Endpoint = endpointUri;
        }

        var organization = section["Organization"];
        if (!string.IsNullOrWhiteSpace(organization))
        {
            clientOptions.OrganizationId = organization;
        }

        // One client shared by embeddings and chat, so retry, timeout, endpoint, and
        // organisation settings cannot drift between the two calls one request makes.
        var client = new OpenAIClient(new ApiKeyCredential(apiKey), clientOptions);

        // Register Microsoft.Extensions.AI's IEmbeddingGenerator<string, Embedding<float>> and
        // IChatClient, which EmbeddingService and AnswerService consume.
        services.AddOpenAIEmbeddingGenerator(embeddingModel, client);
        services.AddOpenAIChatClient(chatModel, client);
    }

    private static string Fallback(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value;
}
