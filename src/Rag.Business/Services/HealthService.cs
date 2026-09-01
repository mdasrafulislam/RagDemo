using Rag.Business.Models;
using Rag.Repository;

namespace Rag.Business.Services;

public interface IHealthService
{
    /// <param name="deep">
    /// When true, also calls the embedding endpoint. Off by default because every embedding call
    /// consumes OpenAI quota, and a frequently-polled probe would spend real money to report
    /// nothing new.
    /// </param>
    Task<HealthResult> CheckAsync(bool deep, CancellationToken cancellationToken);
}

public sealed class HealthService(
    IPlanInfoRepository repository,
    IEmbeddingService embeddingService) : IHealthService
{
    public async Task<HealthResult> CheckAsync(bool deep, CancellationToken cancellationToken)
    {
        var databaseOk = await repository.CanConnectAsync(cancellationToken).ConfigureAwait(false);
        var databaseStatus = databaseOk ? "ok" : "unreachable";

        var embeddingStatus = "not checked (pass deep=true to verify)";
        var embeddingOk = true;

        if (deep)
        {
            try
            {
                // One throwaway word: enough to prove the deployment answers and returns the
                // expected 1536 dimensions, which is the failure this check exists to catch.
                var probe = await embeddingService
                    .GenerateAsync(["health"], cancellationToken)
                    .ConfigureAwait(false);

                embeddingOk = probe.Count == 1;
                embeddingStatus = embeddingOk ? "ok" : $"unexpected response ({probe.Count} vectors)";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                embeddingOk = false;
                embeddingStatus = ex.Message;
            }
        }

        return new HealthResult(databaseOk && embeddingOk, databaseStatus, embeddingStatus);
    }
}
