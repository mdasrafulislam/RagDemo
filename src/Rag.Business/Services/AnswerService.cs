using System.ClientModel;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Rag.Business.Exceptions;
using Rag.Business.Models;
using Rag.Business.Options;

namespace Rag.Business.Services;

public interface IAnswerService
{
    /// <summary>
    /// Answers <paramref name="question"/> using only <paramref name="context"/>.
    /// </summary>
    /// <remarks>
    /// Never answers from the model's own knowledge. When the context does not contain the
    /// answer it reports that via <see cref="GeneratedAnswer.AnsweredFromContext"/> rather than
    /// guessing — a plausible invented answer is worse than no answer, because the caller cannot
    /// tell the difference.
    /// </remarks>
    Task<GeneratedAnswer> GenerateAsync(
        string question,
        IReadOnlyList<AnswerContextChunk> context,
        CancellationToken cancellationToken);
}

/// <summary>
/// Generates grounded answers with an OpenAI chat model.
/// </summary>
/// <remarks>
/// The value of this class is the prompt, not the plumbing. A RAG system that answers fluently
/// from the model's training data instead of the retrieved text is worse than useless: it is
/// confidently wrong in a way the caller cannot detect. The refusal sentinel, the source labels,
/// and temperature 0 all exist to make that failure mode visible rather than plausible.
/// </remarks>
public sealed partial class AnswerService(
    IChatClient chatClient,
    IOptions<OpenAiOptions> options,
    ILogger<AnswerService> logger) : IAnswerService
{
    /// <summary>
    /// The model emits this exactly when the supplied text does not answer the question. A
    /// sentinel rather than free prose, so the caller gets a boolean to branch on instead of
    /// having to pattern-match apologies.
    /// </summary>
    private const string InsufficientContextSentinel = "INSUFFICIENT_CONTEXT";

    private const string SystemPrompt = $"""
        You answer questions using ONLY the numbered source excerpts provided in the user
        message. The excerpts are the entire body of knowledge available to you for this task.

        Rules:
        1. Base every statement on the excerpts. Do not use your own knowledge, do not infer
           beyond what the text supports, and do not fill gaps with what is typically true.
        2. Cite the source of each claim inline using its label, like [S1] or [S2, S3]. Cite the
           specific excerpt the statement came from.
        3. If the excerpts do not contain the answer, reply with exactly
           {InsufficientContextSentinel} on the first line, then one sentence naming what is
           missing. Do this even when you are confident you know the answer from elsewhere —
           reporting the gap is the correct outcome, not a failure.
        4. If the excerpts only partially answer the question, give the part they support, then
           state plainly what they do not cover.
        5. If two excerpts conflict, say so and cite both rather than silently choosing one.
        6. Answer in prose at the length the question warrants. Do not restate the question, do
           not describe your process, and do not add caveats the excerpts do not support.
        """;

    private readonly OpenAiOptions _options = options.Value;

    [GeneratedRegex(@"\[S(\d+(?:\s*,\s*S?\d+)*)\]", RegexOptions.CultureInvariant)]
    private static partial Regex CitationPattern();

    public async Task<GeneratedAnswer> GenerateAsync(
        string question,
        IReadOnlyList<AnswerContextChunk> context,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);
        ArgumentNullException.ThrowIfNull(context);

        if (context.Count == 0)
        {
            // The caller short-circuits before this point; if it did not, refuse rather than
            // invite an ungrounded answer.
            throw new UpstreamException(
                "answer.no_context",
                "Refusing to generate an answer with no retrieved context.");
        }

        var (userPrompt, labelToRecordId) = BuildUserPrompt(question, context);

        var chatOptions = new ChatOptions { MaxOutputTokens = _options.MaxOutputTokens };

        // Temperature is nullable on purpose: grounded extraction wants 0, but some newer
        // OpenAI models reject any non-default temperature. Configuring null omits it.
        //if (_options.Temperature.HasValue)
        //{
        //    chatOptions.Temperature = (float)_options.Temperature.Value;
        //}

        ChatResponse response;
        try
        {
            List<ChatMessage> messages =
            [
                new(ChatRole.System, SystemPrompt),
                new(ChatRole.User, userPrompt),
            ];

            response = await chatClient
                .GetResponseAsync(messages, chatOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ClientResultException ex) when (ex.Status == 429)
        {
            logger.LogWarning(ex, "OpenAI throttled the chat request for model {Model}.", _options.ChatModel);
            throw new UpstreamException(
                "answer.throttled",
                "The OpenAI chat endpoint is rate limited. Retry shortly.",
                ex);
        }
        catch (ClientResultException ex) when (ex.Status is 401 or 403)
        {
            logger.LogError(ex, "OpenAI rejected the API key for chat ({Status}).", ex.Status);
            throw new UpstreamException(
                "answer.unauthorized",
                $"OpenAI rejected the API key, or it lacks access to '{_options.ChatModel}'.",
                ex);
        }
        catch (ClientResultException ex) when (ex.Status == 404)
        {
            throw new UpstreamException(
                "answer.model_not_found",
                $"OpenAI does not recognise the chat model '{_options.ChatModel}'.",
                ex);
        }
        catch (ClientResultException ex)
        {
            logger.LogError(ex, "OpenAI returned {Status} for a chat request.", ex.Status);
            throw new UpstreamException(
                "answer.request_failed",
                $"The OpenAI chat endpoint returned HTTP {ex.Status}.",
                ex);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected failure calling the OpenAI chat endpoint.");
            throw new UpstreamException("answer.unavailable", $"OpenAI is unavailable: {ex.Message}", ex);
        }

        var text = response.Text?.Trim() ?? string.Empty;
        var finishReason = response.FinishReason?.ToString();

        if (string.IsNullOrEmpty(text))
        {
            throw new UpstreamException(
                "answer.empty_response",
                $"The chat model returned no text (finish reason: {finishReason ?? "unknown"}).");
        }

        var answeredFromContext = !text.StartsWith(InsufficientContextSentinel, StringComparison.Ordinal);

        if (!answeredFromContext)
        {
            // Strip the sentinel so callers display the explanation, not the marker.
            text = text[InsufficientContextSentinel.Length..].TrimStart(':', ' ', '\r', '\n');
            if (string.IsNullOrWhiteSpace(text))
            {
                text = "The retrieved text does not contain the answer to that question.";
            }
        }

        // A truncated answer that reads as complete is a quiet correctness problem, so make it
        // visible in the text rather than only in a finish reason the caller may not read.
        if (string.Equals(finishReason, "Length", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning(
                "Chat answer hit the {MaxOutputTokens}-token output cap and was truncated.",
                _options.MaxOutputTokens);
            text += "\n\n[Answer truncated at the configured output token limit.]";
        }

        return new GeneratedAnswer(
            text,
            answeredFromContext,
            ExtractCitedRecordIds(text, labelToRecordId),
            response.Usage?.InputTokenCount is { } input ? (int)input : null,
            response.Usage?.OutputTokenCount is { } output ? (int)output : null,
            finishReason);
    }

    /// <summary>
    /// Renders the excerpts with short labels (S1, S2, …) rather than raw record ids. Models
    /// handle short labels more reliably, and a raw id like 4127 risks being confused with a
    /// number appearing in the document text.
    /// </summary>
    private static (string Prompt, Dictionary<int, long> LabelToRecordId) BuildUserPrompt(
        string question,
        IReadOnlyList<AnswerContextChunk> context)
    {
        var labelToRecordId = new Dictionary<int, long>(context.Count);
        var builder = new StringBuilder();

        builder.AppendLine("Source excerpts:").AppendLine();

        for (var i = 0; i < context.Count; i++)
        {
            var label = i + 1;
            labelToRecordId[label] = context[i].RecordId;

            builder.Append(CultureInfo.InvariantCulture, $"[S{label}]");
            builder.AppendLine();
            builder.AppendLine(context[i].Text.Trim());
            builder.AppendLine();
        }

        builder.AppendLine("Question:");
        builder.Append(question.Trim());

        return (builder.ToString(), labelToRecordId);
    }

    private static IReadOnlyList<long> ExtractCitedRecordIds(
        string answer,
        Dictionary<int, long> labelToRecordId)
    {
        // Preserves first-mention order and de-duplicates, so a caller can render sources in the
        // order the answer refers to them.
        var ordered = new List<long>();
        var seen = new HashSet<long>();

        foreach (var match in CitationPattern().Matches(answer).Cast<Match>())
        {
            foreach (var part in match.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries))
            {
                var digits = part.TrimStart('S', 's');
                if (int.TryParse(digits, CultureInfo.InvariantCulture, out var label) &&
                    labelToRecordId.TryGetValue(label, out var recordId) &&
                    seen.Add(recordId))
                {
                    ordered.Add(recordId);
                }
            }
        }

        return ordered;
    }
}
