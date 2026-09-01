using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Pgvector.Npgsql;
using Rag.Repository.Options;

namespace Rag.Repository;

public static class DependencyInjection
{
    /// <summary>
    /// Registers the data-access tier. Called by the business tier, so the web tier never has
    /// to know the database exists.
    /// </summary>
    public static IServiceCollection AddRepository(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<RepositoryOptions>()
            .Bind(configuration.GetSection(RepositoryOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        var connectionString = configuration.GetConnectionString("Postgres");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "ConnectionStrings:Postgres is not configured. Set it via user-secrets or the environment.");
        }

        // One pooled data source for the process.
        services.AddSingleton(_ =>
        {
            var builder = new NpgsqlDataSourceBuilder(connectionString);

            // Maps PlanInfo.chunk_embedding (VECTOR(1536)) to Pgvector's Vector type.
            // NOTE: UseVector() ships in the `Pgvector` package, in the `Pgvector.Npgsql`
            // namespace. There is no `Pgvector.Npgsql` package.
            builder.UseVector();

            return builder.Build();
        });

        services.AddScoped<IPlanInfoRepository, PlanInfoRepository>();

        return services;
    }
}
