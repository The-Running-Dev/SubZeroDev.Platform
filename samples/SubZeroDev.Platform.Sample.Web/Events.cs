using Microsoft.Extensions.Logging;
using SubZeroDev.Platform.Abstractions;

namespace SubZeroDev.Platform.Sample.Web;

/// <summary>The names both roles register the handler triple under. Public so the worker project can
/// register the identical binding without redeclaring the literal.</summary>
public static class SampleEventTypes
{
    /// <summary>The stable name for <see cref="OrderPlaced"/>.</summary>
    public static EventTypeName OrderPlaced { get; } = new("sample.order-placed");
}

/// <summary>Raised in the same transaction as the order it describes.</summary>
/// <param name="OrderId">The order's id.</param>
/// <param name="CatalogueItemId">The catalogue item ordered.</param>
/// <param name="Quantity">How many were ordered.</param>
public sealed record OrderPlaced(string OrderId, string CatalogueItemId, int Quantity) : IIntegrationEvent;

/// <summary>Handles the sample event in the worker. CI kills the web process after commit and
/// verifies that a restarted worker reaches this handler from the durable outbox row.</summary>
public sealed class OrderPlacedHandler(ILogger<OrderPlacedHandler> logger) : IIntegrationEventHandler<OrderPlaced>
{
    /// <inheritdoc/>
    public Task<Result<HandlerError>> HandleAsync(OrderPlaced @event, CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Order {OrderId} placed for {Quantity} of {CatalogueItemId}.",
            @event.OrderId,
            @event.Quantity,
            @event.CatalogueItemId);

        return Task.FromResult(Result<HandlerError>.Success());
    }
}
