using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;

namespace SubZeroDev.Platform.Persistence;

/// <summary>Claims and dispatches one row at a time, bounded by the configured per-tick budget.</summary>
internal sealed class OutboxDispatcher(
    IOutboxStore store,
    IEventHandlerRegistry registry,
    IMigrationRunner migrations,
    IServiceScopeFactory serviceScopes,
    IOperationScopeFactory operationScopes,
    ITraceContextCodec traces,
    PlatformOptions options,
    InstanceId instance,
    IClock clock,
    ILogger<OutboxDispatcher> logger) : IBackgroundWork
{
    public BackgroundWorkName Name => PlatformBackgroundWork.OutboxDispatch;

    public HostRoles Roles => HostRoles.Worker;

    public TimeSpan Interval => options.Outbox.DispatchInterval;

    public bool RequiresLease => false;

    public async Task TickAsync(CancellationToken cancellationToken)
    {
        var status = await migrations.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (!status.IsSuccess || status.Value.Any(module => module.Pending.Count > 0))
        {
            return;
        }

        for (var dispatched = 0; dispatched < options.Outbox.DispatchTickBudget; dispatched++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            var claimed = await store.ClaimNextAsync(instance, cancellationToken).ConfigureAwait(false);
            if (!claimed.IsSuccess || claimed.Value is not { } message)
            {
                return;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                await store.ReleaseClaimAsync(message.Id, instance, CancellationToken.None).ConfigureAwait(false);
                return;
            }

            await DispatchAsync(message, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task DispatchAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        using var drain = new CancellationTokenSource();
        using var beginDrain = cancellationToken.Register(
            () => drain.CancelAfter(options.Hosting.GracefulShutdownDrainWindow));
        var dispatchToken = drain.Token;

        if (!registry.TryResolve(message.Type, out var registration))
        {
            await DeferOrPoisonAsync(message, DispatchError.HandlerUnresolved(), dispatchToken).ConfigureAwait(false);
            return;
        }

        object? payload;
        try
        {
            payload = JsonSerializer.Deserialize(message.Payload, registration.EventType, OutboxSerializer.Options);
            if (payload is null)
            {
                throw new JsonException("The deserialised event was null.");
            }
        }
        catch (JsonException)
        {
            await DeferOrPoisonAsync(message, DispatchError.PayloadUndeserializable(), dispatchToken).ConfigureAwait(false);
            return;
        }

        using var dependencyScope = serviceScopes.CreateScope();
        using var trace = traces.StartLinked(message.TraceContext, "platform.outbox.dispatch");
        using var operation = operationScopes.Begin(
            trace.Context, message.Correlation, message.Tenant, principal: null, message.Culture);

        var handler = dependencyScope.ServiceProvider.GetRequiredService(registration.HandlerType);
        var unitOfWork = dependencyScope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        Result<HandlerError>? handlerResult = null;

        var transaction = await unitOfWork.ExecuteAsync(
            TransactionIntent.Write,
            async token =>
            {
                handlerResult = await InvokeHandlerAsync(
                    handler, registration.EventType, payload, token).ConfigureAwait(false);
                if (!handlerResult.Value.IsSuccess)
                {
                    throw HandlerRejectedException.Instance;
                }
            },
            dispatchToken).ConfigureAwait(false);

        if (handlerResult is { } handled && !handled.IsSuccess)
        {
            await RecordHandlerFailureAsync(message, handled.Error, dispatchToken).ConfigureAwait(false);
            return;
        }

        if (!transaction.IsSuccess)
        {
            logger.LogWarning("Outbox message {MessageId} transaction did not commit: {Code}.", message.Id, transaction.Error.Code);
            return;
        }

        await ObserveClaimedWriteAsync(
            message.Id,
            await store.MarkProcessedAsync(message.Id, instance, dispatchToken).ConfigureAwait(false))
            .ConfigureAwait(false);
    }

    private async Task RecordHandlerFailureAsync(
        OutboxMessage message, HandlerError error, CancellationToken cancellationToken)
    {
        Result<ClaimedWriteOutcome, TransactionError> written;
        if (error.Code == nameof(HandlerError.Permanent)
            || message.Attempts + 1 >= options.Outbox.PoisonAttemptCount)
        {
            written = await store.PoisonAsync(
                message.Id, instance, error.Code, PoisonAttemptMode.Increment, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var exponent = Math.Pow(options.Outbox.RetryBackoffFactor, message.Attempts);
            var ticks = Math.Min(
                options.Outbox.RetryBackoffCap.Ticks,
                options.Outbox.RetryBackoffBase.Ticks * exponent);
            var nextAttemptAt = clock.UtcNow + TimeSpan.FromTicks((long)ticks);
            written = await store.RecordFailureAsync(
                message.Id, instance, error.Code, nextAttemptAt, cancellationToken).ConfigureAwait(false);
        }

        await ObserveClaimedWriteAsync(message.Id, written).ConfigureAwait(false);
    }

    private async Task DeferOrPoisonAsync(
        OutboxMessage message, DispatchError error, CancellationToken cancellationToken)
    {
        Result<ClaimedWriteOutcome, TransactionError> written;
        if (message.FirstDeferredAt is { } firstDeferred
            && clock.UtcNow >= firstDeferred + options.Outbox.DeferralAge)
        {
            written = await store.PoisonAsync(
                message.Id, instance, error.Code, PoisonAttemptMode.Preserve, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            written = await store.DeferAsync(
                message.Id,
                instance,
                clock.UtcNow + options.Outbox.DeferralRetryInterval,
                cancellationToken).ConfigureAwait(false);
        }

        await ObserveClaimedWriteAsync(message.Id, written).ConfigureAwait(false);
    }

    private Task ObserveClaimedWriteAsync(
        OutboxMessageId id, Result<ClaimedWriteOutcome, TransactionError> written)
    {
        if (!written.IsSuccess)
        {
            logger.LogWarning("Outbox state write for {MessageId} failed: {Code}.", id, written.Error.Code);
        }
        else if (written.Value == ClaimedWriteOutcome.ClaimLost)
        {
            logger.LogWarning("Outbox state write for {MessageId} lost its claim; duplicate delivery is possible.", id);
        }

        return Task.CompletedTask;
    }

    private static async Task<Result<HandlerError>> InvokeHandlerAsync(
        object handler, Type eventType, object payload, CancellationToken cancellationToken)
    {
        try
        {
            var contract = typeof(IIntegrationEventHandler<>).MakeGenericType(eventType);
            var method = contract.GetMethod(nameof(IIntegrationEventHandler<IIntegrationEvent>.HandleAsync))!;
            var task = (Task)method.Invoke(handler, [payload, cancellationToken])!;
            await task.ConfigureAwait(false);
            return (Result<HandlerError>)task.GetType().GetProperty("Result")!.GetValue(task)!;
        }
        catch (TargetInvocationException exception) when (exception.InnerException is OperationCanceledException cancellation)
        {
            throw cancellation;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Result<HandlerError>.Failure(HandlerError.Transient());
        }
    }

    private sealed class HandlerRejectedException : Exception
    {
        internal static HandlerRejectedException Instance { get; } = new();
    }
}
