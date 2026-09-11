using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;
using SubZeroDev.Platform.Hosting;
using SubZeroDev.Platform.Mcp;

namespace SubZeroDev.Platform.Tests;

/// <summary>D5-S15: the transport, the connection principal and invocation. Drives the MCP transport
/// end to end — a real streamable-HTTP client against <see cref="WebHostUnderTest"/> — since that is
/// what proves the SDK's own session binding and Platform's own filters actually wired together,
/// rather than only unit-testing internal plumbing.</summary>
public sealed class McpInvocationTests
{
    private static readonly PermissionName UsePermission = new("Sample.Tool.Use");
    private const string PrincipalHeader = "X-Test-Principal";

    /// <summary>S15.7 — listing returns exposed tools only.</summary>
    [Fact]
    public async Task Listing_returns_exposed_tools_only()
    {
        await using var harness = await Harness.StartAsync(
            exposed: [new ToolName("visible")],
            extra: [new StubToolProducer("producer",
                new ToolDefinition(new ToolName("visible"), "Visible.", Schema(), UsePermission, null),
                new ToolDefinition(new ToolName("hidden"), "Hidden.", Schema(), UsePermission, null))]);

        await using var client = await harness.ConnectAsync("alice");
        var tools = await client.ListToolsAsync(cancellationToken: CancellationToken.None);

        Assert.Single(tools);
        Assert.Equal("visible", tools[0].Name);
    }

    /// <summary>S15.6 — an unregistered tool and a registered-but-unexposed tool produce the identical
    /// answer on call, and it is Platform's own answer rather than the SDK's default.</summary>
    [Fact]
    public async Task Unregistered_and_unexposed_tools_answer_identically_on_call()
    {
        await using var unexposedHarness = await Harness.StartAsync(
            exposed: [],
            extra: [new StubToolProducer("producer",
                new ToolDefinition(new ToolName("hidden"), "Not exposed.", Schema(), UsePermission, null))]);
        await using var neverRegisteredHarness = await Harness.StartAsync(exposed: []);

        await using var unexposedClient = await unexposedHarness.ConnectAsync("alice");
        await using var unregisteredClient = await neverRegisteredHarness.ConnectAsync("alice");

        var unexposed = await unexposedClient.CallToolAsync(
            "hidden", new Dictionary<string, object?>(), cancellationToken: CancellationToken.None);
        var unregistered = await unregisteredClient.CallToolAsync(
            "hidden", new Dictionary<string, object?>(), cancellationToken: CancellationToken.None);

        Assert.True(unexposed.IsError);
        Assert.True(unregistered.IsError);
        Assert.Equal(TextOf(unexposed), TextOf(unregistered));
        Assert.DoesNotContain("forbidden", TextOf(unexposed), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>S15.4 and S15.9 — an authorization denial never reaches the invoker, and still writes
    /// exactly one audit record naming the tool as the action, class <c>Required</c>.</summary>
    [Fact]
    public async Task A_denied_call_never_reaches_the_invoker_and_is_audited_once()
    {
        var invoker = new StubToolInvoker("producer");
        await using var harness = await Harness.StartAsync(
            exposed: [new ToolName("guarded")],
            extra: [
                new StubToolProducer("producer",
                    new ToolDefinition(new ToolName("guarded"), "Guarded.", Schema(), UsePermission, null)),
                invoker,
            ],
            grantsPermission: false);

        await using var client = await harness.ConnectAsync("alice");
        var result = await client.CallToolAsync(
            "guarded", new Dictionary<string, object?>(), cancellationToken: CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal(0, invoker.CallCount);

        var records = harness.Audit.Received.Where(e => e.Action == new AuditAction("guarded")).ToList();
        Assert.Single(records);
        Assert.Equal(AuditOutcome.Denied, records[0].Outcome);
        Assert.Equal(AuditClass.Required, records[0].Class);
    }

    /// <summary>S15.2 and S15.9 — a successful call writes exactly one audit record with the tool as
    /// the action, class <c>Required</c>, and no arguments anywhere on the record.</summary>
    [Fact]
    public async Task A_successful_call_is_audited_once_with_no_arguments()
    {
        var invoker = new StubToolInvoker("producer", (_, _) => ToolInvocationResult.Success("ok"));
        await using var harness = await Harness.StartAsync(
            exposed: [new ToolName("greet")],
            extra: [
                new StubToolProducer("producer",
                    new ToolDefinition(new ToolName("greet"), "Greets.", Schema("name"), UsePermission, null)),
                invoker,
            ]);

        await using var client = await harness.ConnectAsync("alice");
        var result = await client.CallToolAsync(
            "greet",
            new Dictionary<string, object?> { ["name"] = "super-secret-value" },
            cancellationToken: CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal(1, invoker.CallCount);

        var records = harness.Audit.Received.Where(e => e.Action == new AuditAction("greet")).ToList();
        Assert.Single(records);
        Assert.Equal(AuditOutcome.Allowed, records[0].Outcome);
        Assert.Equal(AuditClass.Required, records[0].Class);

        // No representation of AuditEvent carries a payload or argument member at all (I-U6's
        // structural half) — the record type itself has no place a value could have leaked to.
        Assert.DoesNotContain("super-secret-value", records[0].ToString());
    }

    /// <summary>S15.8 — invalid arguments name no argument value.</summary>
    [Fact]
    public async Task Invalid_arguments_name_no_argument_value()
    {
        await using var harness = await Harness.StartAsync(
            exposed: [new ToolName("typed")],
            extra: [new StubToolProducer("producer",
                new ToolDefinition(new ToolName("typed"), "Typed.", Schema("count", "number"), UsePermission, null))]);

        await using var client = await harness.ConnectAsync("alice");
        var result = await client.CallToolAsync(
            "typed",
            new Dictionary<string, object?> { ["count"] = "not-a-number-but-a-secret" },
            cancellationToken: CancellationToken.None);

        Assert.True(result.IsError);
        Assert.DoesNotContain("not-a-number-but-a-secret", TextOf(result), StringComparison.Ordinal);
    }

    /// <summary>S15.5 — a tool whose schema names a resource parameter is authorized scoped to the
    /// parsed resource id.</summary>
    [Fact]
    public async Task A_tool_naming_a_resource_parameter_is_authorized_scoped_to_it()
    {
        ResourceRef? seen = null;
        var invoker = new StubToolInvoker("producer", (_, _) => ToolInvocationResult.Success("ok"));
        await using var harness = await Harness.StartAsync(
            exposed: [new ToolName("read-doc")],
            extra: [
                new StubToolProducer("producer",
                    new ToolDefinition(new ToolName("read-doc"), "Reads.", Schema("resourceId"), UsePermission, null)),
                invoker,
            ],
            onEvaluate: (_, _, resource) => seen = resource);

        await using var client = await harness.ConnectAsync("alice");
        await client.CallToolAsync(
            "read-doc",
            new Dictionary<string, object?> { ["resourceId"] = "doc-42" },
            cancellationToken: CancellationToken.None);

        Assert.Equal(new ResourceRef("read-doc", "doc-42"), seen);
    }

    /// <summary>S15.10 — two calls on one long-lived connection carry different correlations.</summary>
    [Fact]
    public async Task Two_calls_on_one_connection_carry_different_correlations()
    {
        var invoker = new StubToolInvoker("producer", (_, _) => ToolInvocationResult.Success("ok"));
        await using var harness = await Harness.StartAsync(
            exposed: [new ToolName("ping")],
            extra: [
                new StubToolProducer("producer",
                    new ToolDefinition(new ToolName("ping"), "Pings.", Schema(), UsePermission, null)),
                invoker,
            ]);

        await using var client = await harness.ConnectAsync("alice");
        await client.CallToolAsync("ping", new Dictionary<string, object?>(), cancellationToken: CancellationToken.None);
        await client.CallToolAsync("ping", new Dictionary<string, object?>(), cancellationToken: CancellationToken.None);

        var records = harness.Audit.Received.Where(e => e.Action == new AuditAction("ping")).ToList();
        Assert.Equal(2, records.Count);
        Assert.NotEqual(records[0].Correlation, records[1].Correlation);
    }

    /// <summary>S15.11 — a connection dropped mid-invocation cancels the call through the existing
    /// cancellation plumbing and leaves no half-applied write: an invoker still running when the
    /// caller's token is cancelled never gets to finish, and no audit record is written for that
    /// call — nothing records a call that never completed.</summary>
    [Fact]
    public async Task A_cancelled_call_writes_no_audit_record_and_never_completes_the_invoker()
    {
        var started = new TaskCompletionSource();
        var invokerSawCancellation = new TaskCompletionSource<bool>();
        var invoker = new StubToolInvoker("producer", async (_, _, cancellationToken) =>
        {
            started.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                invokerSawCancellation.SetResult(true);
                throw;
            }

            return ToolInvocationResult.Success("unreachable");
        });

        await using var harness = await Harness.StartAsync(
            exposed: [new ToolName("slow")],
            extra: [
                new StubToolProducer("producer",
                    new ToolDefinition(new ToolName("slow"), "Slow.", Schema(), UsePermission, null)),
                invoker,
            ]);

        await using var client = await harness.ConnectAsync("alice");

        using var callCancellation = new CancellationTokenSource();
        var callTask = client.CallToolAsync(
            "slow", new Dictionary<string, object?>(), cancellationToken: callCancellation.Token).AsTask();

        await started.Task;
        callCancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => callTask);
        await invokerSawCancellation.Task;

        var records = harness.Audit.Received.Where(e => e.Action == new AuditAction("slow")).ToList();
        Assert.Empty(records);
    }

    /// <summary>S15.1 — a session request carrying a different principal than the one the session was
    /// established with ends the exchange; the SDK's own session-principal binding is what enforces
    /// this (design/90-decisions.md, 2026-08-29), and this proves Platform's bridge onto
    /// <c>HttpContext.User</c> actually engages it.</summary>
    [Fact]
    public async Task A_session_request_carrying_a_different_principal_ends_the_exchange()
    {
        await using var harness = await Harness.StartAsync(exposed: []);

        using var initial = harness.NewHttpClient("alice");
        using var initializeRequest = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = JsonContent.Create(new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "initialize",
                @params = new
                {
                    protocolVersion = "2025-06-18",
                    capabilities = new { },
                    clientInfo = new { name = "s15-test", version = "1.0" },
                },
            }),
        };
        initializeRequest.Headers.Accept.Add(new("application/json"));
        initializeRequest.Headers.Accept.Add(new("text/event-stream"));

        using var initializeResponse = await initial.SendAsync(initializeRequest, CancellationToken.None);
        Assert.True(
            initializeResponse.IsSuccessStatusCode,
            $"initialize failed: {(int)initializeResponse.StatusCode} {await initializeResponse.Content.ReadAsStringAsync(CancellationToken.None)}");

        var sessionId = Assert.Single(initializeResponse.Headers.GetValues("Mcp-Session-Id"));

        using var different = harness.NewHttpClient("bob");
        using var listRequest = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = JsonContent.Create(new { jsonrpc = "2.0", id = 2, method = "tools/list" }),
        };
        listRequest.Headers.Accept.Add(new("application/json"));
        listRequest.Headers.Accept.Add(new("text/event-stream"));
        listRequest.Headers.Add("Mcp-Session-Id", sessionId);

        using var deniedResponse = await different.SendAsync(listRequest, CancellationToken.None);

        Assert.Equal(HttpStatusCode.Forbidden, deniedResponse.StatusCode);
    }

    private static string TextOf(CallToolResult result) =>
        string.Join(" ", result.Content.OfType<TextContentBlock>().Select(block => block.Text));

    private static JsonElement Schema(params string[] namesAndOptionalType)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "object");
            writer.WriteStartObject("properties");

            // Two shapes: a list of bare property names (string-typed, no constraint beyond
            // presence), or exactly one (name, jsonType) pair for a typed-property schema.
            if (namesAndOptionalType.Length == 2 && namesAndOptionalType[1] is "number" or "string" or "boolean")
            {
                writer.WriteStartObject(namesAndOptionalType[0]);
                writer.WriteString("type", namesAndOptionalType[1]);
                writer.WriteEndObject();
            }
            else
            {
                foreach (var name in namesAndOptionalType)
                {
                    writer.WriteStartObject(name);
                    writer.WriteString("type", "string");
                    writer.WriteEndObject();
                }
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return JsonDocument.Parse(stream.ToArray()).RootElement;
    }

    /// <summary>Builds and owns one MCP-capable host, plus the ability to connect a real client to
    /// it with a chosen test principal.</summary>
    private sealed class Harness(WebApplication app, RecordingAuditSink audit, HttpClient httpClient) : IAsyncDisposable
    {
        internal RecordingAuditSink Audit { get; } = audit;

        internal static async Task<Harness> StartAsync(
            IReadOnlyCollection<ToolName> exposed,
            IReadOnlyCollection<object>? extra = null,
            bool grantsPermission = true,
            Action<Principal, TenantId, ResourceRef?>? onEvaluate = null)
        {
            var audit = new RecordingAuditSink("mcp-invocation-tests", isDurable: true);

            var (app, client) = await WebHostUnderTest.StartAsync(
                services =>
                {
                    services.AddSingleton<IPlatformModule, McpModule>();
                    services.AddSingleton(new McpOptions { ExposedTools = exposed.ToHashSet() });
                    services.AddSingleton<IPermissionCatalog>(new StubPermissionCatalog(UsePermission));
                    services.AddSingleton<IPermissionProvider>(new StubPermissionProvider(
                        "test-permission-provider",
                        (principal, tenant, resource) =>
                        {
                            onEvaluate?.Invoke(principal, tenant, resource);
                            return grantsPermission
                                ? Result<IReadOnlySet<PermissionName>, AuthorizationError>.Success(
                                    new HashSet<PermissionName> { UsePermission })
                                : Result<IReadOnlySet<PermissionName>, AuthorizationError>.Success(
                                    new HashSet<PermissionName>());
                        }));
                    services.AddSingleton<IAuthenticationProvider>(new TestPrincipalAuthenticationProvider());

                    foreach (var item in extra ?? [])
                    {
                        switch (item)
                        {
                            case IToolProducer producer:
                                services.AddSingleton(producer);
                                break;
                            case IToolInvoker invoker:
                                services.AddSingleton(invoker);
                                break;
                        }
                    }

                    services.AddSingleton<IAuditSink>(audit);
                },
                composeOperatedDefaults: false,
                mapEndpoints: application => application.MapPlatformMcp());

            return new Harness(app, audit, client);
        }

        internal HttpClient NewHttpClient(string principal)
        {
            var client = new HttpClient { BaseAddress = httpClient.BaseAddress };
            client.DefaultRequestHeaders.Add(PrincipalHeader, principal);
            return client;
        }

        internal async Task<McpClient> ConnectAsync(string principal)
        {
            var transport = new HttpClientTransport(
                new HttpClientTransportOptions
                {
                    Endpoint = new Uri(httpClient.BaseAddress!, "/mcp"),
                    TransportMode = HttpTransportMode.StreamableHttp,
                    AdditionalHeaders = new Dictionary<string, string> { [PrincipalHeader] = principal },
                },
                NewHttpClient(principal));

            return await McpClient.CreateAsync(transport, cancellationToken: CancellationToken.None);
        }

        public async ValueTask DisposeAsync()
        {
            httpClient.Dispose();
            await app.DisposeAsync();
        }
    }

    /// <summary>Authenticates from the <c>X-Test-Principal</c> header — the harness's stand-in for a
    /// real credential, distinguishing test principals by name while keeping each one non-anonymous
    /// (<see cref="PrincipalKind.Account"/>) so the SDK's own session-principal binding actually
    /// constrains it (an anonymous session carries no such constraint).</summary>
    private sealed class TestPrincipalAuthenticationProvider : IAuthenticationProvider
    {
        public string Name => "test-principal";

        public Task<Result<Principal, AuthenticationError>> AuthenticateAsync(
            IAuthenticationRequest request, CancellationToken cancellationToken)
        {
            if (!request.Headers.TryGetValue(PrincipalHeader, out var values) || values.Count == 0)
            {
                return Task.FromResult(Result<Principal, AuthenticationError>.Success(Principal.Anonymous));
            }

            var name = values[0];
            var principal = new Principal(new PrincipalId("test", name), PrincipalKind.Account, name, null);
            return Task.FromResult(Result<Principal, AuthenticationError>.Success(principal));
        }
    }

    /// <summary>Records every call handed to it, and answers a test-chosen result.</summary>
    private sealed class StubToolInvoker : IToolInvoker
    {
        private readonly Func<ToolName, IDictionary<string, JsonElement>, CancellationToken, Task<ToolInvocationResult>>? _answer;

        internal StubToolInvoker(
            string producerName, Func<ToolName, IDictionary<string, JsonElement>, ToolInvocationResult>? answer = null)
        {
            Name = new ToolProducerName(producerName);
            _answer = answer is null ? null : (tool, arguments, _) => Task.FromResult(answer(tool, arguments));
        }

        internal StubToolInvoker(
            string producerName,
            Func<ToolName, IDictionary<string, JsonElement>, CancellationToken, Task<ToolInvocationResult>> answer)
        {
            Name = new ToolProducerName(producerName);
            _answer = answer;
        }

        public ToolProducerName Name { get; }

        internal int CallCount { get; private set; }

        public async ValueTask<ToolInvocationResult> InvokeAsync(
            ToolName tool, IDictionary<string, JsonElement> arguments, CancellationToken cancellationToken)
        {
            CallCount++;
            return _answer is null
                ? ToolInvocationResult.Success()
                : await _answer(tool, arguments, cancellationToken).ConfigureAwait(false);
        }
    }
}
