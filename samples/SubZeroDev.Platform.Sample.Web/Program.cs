using System.Data.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;
using SubZeroDev.Platform.Hosting;
using SubZeroDev.Platform.Persistence;
using SubZeroDev.Platform.Sample.Web;

var builder = WebApplication.CreateBuilder(args);

// Modules are ordinary registrations, and they go on before the standard call: a module
// contributes services, and nothing can be added once the container is built.
builder.Services.AddSingleton<IPlatformModule, CatalogueModule>();
builder.Services.AddSingleton<IPlatformModule, OrdersModule>();

// Migrate mode is a one-shot command, not a third host role — it exits before AddPlatformWebHost
// ever runs, and never serves HTTP or probes.
if (args is ["migrate"])
{
    return await builder.RunPlatformMigrateModeAsync(CancellationToken.None);
}

// What declaring Operated now costs a consumer (D5-S8): an authentication provider (I-C1) and a
// sink declaring IsDurable (I-C2), or the host refuses to start. See Composition.cs — S9 and S13
// replace both with the real modules.
builder.Services.AddSingleton<IAuthenticationProvider,
    OperatedComposition.NoCredentialAuthenticationProvider>();
builder.Services.AddSingleton<IAuditSink>(
    new OperatedComposition.FileAuditSink("sample-audit.log"));

// The only mandatory Platform call. Health, readiness and correlation come with it.
builder.AddPlatformWebHost();

// Persistence is optional and wires itself in — Hosting does not reference this package, so a host
// composed without this call is a supported shape with a smaller readiness surface.
builder.Services.AddPlatformPersistence();

// The web host registers the same triple the worker does, in order to enqueue — it never
// constructs the handler, but the registration is a statement both roles make identically.
builder.Services.AddPlatformEventHandler<OrderPlaced, OrderPlacedHandler>(SampleEventTypes.OrderPlaced);

var app = builder.Build();

// D5-S1.1: an adopter must be able to see, from the log alone, which shape this host claims to be.
app.Logger.LogInformation(
    "Composition profile: {CompositionProfile}",
    app.Services.GetRequiredService<PlatformOptions>().CompositionProfile);

app.MapGet("/", (ICurrentCorrelation correlation, ICurrentTenant tenant) => new
{
    correlation = correlation.Current.TraceId,
    tenant = tenant.Current.Value,
});

// Exists to be called: an unhandled failure must return an envelope carrying the correlation and
// nothing else, which is only demonstrable against something that actually throws.
app.MapGet("/boom", void () => throw new InvalidOperationException(
    "Sample failure with detail that must not reach the wire."));

// Writes to both modules' tables in one transaction over one connection — Orders enlists through a
// raw DbCommand against the ambient transaction rather than opening a connection of its own, which
// is what makes the two rows commit or roll back together.
app.MapPost("/orders", async (
    CreateOrderRequest request,
    IUnitOfWork unitOfWork,
    IAmbientTransactionAccessor ambient,
    IProviderCapability capability,
    IOutboxWriter outbox,
    IClock clock,
    ICurrentTenant tenant,
    ICurrentPrincipal principal,
    CancellationToken cancellationToken) =>
{
    var result = await unitOfWork.ExecuteAsync(
        TransactionIntent.Write,
        async token =>
        {
            var current = ambient.Current!;
            var now = capability.FormatInstant(clock.UtcNow);
            var tenantValue = tenant.Current.ToString();
            var createdBy = principal.Current.Id.ToString();

            var itemId = Guid.NewGuid().ToString();
            await using (var insertItem = current.Connection.CreateCommand())
            {
                insertItem.Transaction = current.Transaction;
                insertItem.CommandText =
                    "INSERT INTO catalogue_items (id, name, tenant, created_at, created_by) "
                    + "VALUES (@id, @name, @tenant, @createdAt, @createdBy);";
                AddParameter(insertItem, "@id", itemId);
                AddParameter(insertItem, "@name", request.Name);
                AddParameter(insertItem, "@tenant", tenantValue);
                AddParameter(insertItem, "@createdAt", now);
                AddParameter(insertItem, "@createdBy", createdBy);
                await insertItem.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }

            var orderId = Guid.NewGuid().ToString();
            await using (var insertOrder = current.Connection.CreateCommand())
            {
                insertOrder.Transaction = current.Transaction;
                insertOrder.CommandText =
                    "INSERT INTO orders (id, catalogue_item_id, quantity, tenant, created_at, created_by) "
                    + "VALUES (@id, @itemId, @quantity, @tenant, @createdAt, @createdBy);";
                AddParameter(insertOrder, "@id", orderId);
                AddParameter(insertOrder, "@itemId", itemId);
                AddParameter(insertOrder, "@quantity", request.Quantity);
                AddParameter(insertOrder, "@tenant", tenantValue);
                AddParameter(insertOrder, "@createdAt", now);
                AddParameter(insertOrder, "@createdBy", createdBy);
                await insertOrder.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }

            // Enlists in the same transaction as both product rows above — a rollback after this
            // point leaves neither the order nor this outbox row.
            outbox.Enqueue(new OrderPlaced(orderId, itemId, request.Quantity));

            return new CreateOrderResponse(itemId, orderId);
        },
        cancellationToken).ConfigureAwait(false);

    return result.IsSuccess ? Results.Ok(result.Value) : Results.Problem(result.Error.Detail);
});

app.Run();

return 0;

static void AddParameter(DbCommand command, string name, object? value)
{
    var parameter = command.CreateParameter();
    parameter.ParameterName = name;
    parameter.Value = value ?? DBNull.Value;
    command.Parameters.Add(parameter);
}

internal sealed record CreateOrderRequest(string Name, int Quantity);

internal sealed record CreateOrderResponse(string CatalogueItemId, string OrderId);
