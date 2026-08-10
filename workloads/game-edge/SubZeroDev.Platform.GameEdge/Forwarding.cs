using SubZeroDev.Platform.Abstractions;

namespace SubZeroDev.Platform.GameEdge;

/// <summary>One request to forward. Carries no operation id and no parsed body — the edge does not
/// know which operation it is carrying. <see cref="PathAndQuery"/> is forwarded unaltered.</summary>
/// <param name="Method">The inbound HTTP method.</param>
/// <param name="PathAndQuery">The inbound path and query string, verbatim.</param>
/// <param name="Body">The inbound body, verbatim.</param>
/// <param name="ContentType">The inbound <c>Content-Type</c>, when present.</param>
/// <param name="Trace">The ambient scope's trace context, written to the outbound <c>traceparent</c>.</param>
public sealed record ForwardedRequest(
    HttpMethod Method,
    string PathAndQuery,
    ReadOnlyMemory<byte> Body,
    string? ContentType,
    TraceContext Trace);

/// <summary>The workload's answer, unaltered. <see cref="Body"/> is bytes and is returned
/// byte-for-byte — any re-encoding here would fail Stage 2's byte comparison invisibly.</summary>
/// <param name="StatusCode">The workload's status code.</param>
/// <param name="Body">The workload's response body, verbatim.</param>
/// <param name="ContentType">The workload's <c>Content-Type</c>, when present.</param>
public sealed record ForwardedResponse(
    int StatusCode,
    ReadOnlyMemory<byte> Body,
    string? ContentType);

/// <summary>Forwards one request to the workload. Retries nothing — a retry against a request whose
/// outcome is unknown would be a second action.</summary>
public interface IGameWorkloadForwarder
{
    /// <summary>Forwards <paramref name="request"/> and returns the workload's response, or why it
    /// could not be obtained.</summary>
    /// <param name="request">The request to forward.</param>
    /// <param name="cancellationToken">Cancelled when the caller disconnects.</param>
    /// <returns>The workload's response, or an <see cref="EdgeError"/>.</returns>
    Task<Result<ForwardedResponse, EdgeError>> ForwardAsync(
        ForwardedRequest request,
        CancellationToken cancellationToken);
}

/// <inheritdoc cref="IGameWorkloadForwarder"/>
internal sealed class GameWorkloadForwarder(HttpClient httpClient, GameEdgeOptions options) : IGameWorkloadForwarder
{
    /// <inheritdoc/>
    public async Task<Result<ForwardedResponse, EdgeError>> ForwardAsync(
        ForwardedRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(options.ForwardTimeout);

        using var message = new HttpRequestMessage(
            request.Method,
            new Uri(options.WorkloadBaseAddress, request.PathAndQuery));

        if (!request.Body.IsEmpty)
        {
            message.Content = new ByteArrayContent(request.Body.ToArray());
            if (request.ContentType is { } contentType)
            {
                message.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
            }
        }

        message.Headers.TryAddWithoutValidation("traceparent", request.Trace.TraceParent);
        if (request.Trace.TraceState is { } traceState)
        {
            message.Headers.TryAddWithoutValidation("tracestate", traceState);
        }

        HttpResponseMessage response;
        try
        {
            // Exactly one attempt: no loop, no retry. `ResponseHeadersRead` so the timeout budget
            // covers the body read below rather than only the connect-and-headers phase.
            response = await httpClient
                .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, budget.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Result<ForwardedResponse, EdgeError>.Failure(EdgeError.WorkloadTimeout());
        }
        catch (HttpRequestException)
        {
            return Result<ForwardedResponse, EdgeError>.Failure(EdgeError.WorkloadUnreachable());
        }

        using (response)
        {
            byte[] body;
            try
            {
                body = await response.Content.ReadAsByteArrayAsync(budget.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return Result<ForwardedResponse, EdgeError>.Failure(EdgeError.WorkloadTimeout());
            }

            return Result<ForwardedResponse, EdgeError>.Success(new ForwardedResponse(
                (int)response.StatusCode,
                body,
                response.Content.Headers.ContentType?.ToString()));
        }
    }
}
