using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;
using SubZeroDev.Platform.Hosting;
using SubZeroDev.Platform.Licensing;
using SubZeroDev.Platform.Persistence;
using SubZeroDev.Platform.Testing;

namespace SubZeroDev.Platform.Tests;

/// <summary>D5-S12: the Licensing module — offline verification, the one verified-licence row and its
/// monotonic guard, the four outcomes and their identical-but-separately-named fallbacks, grace, and
/// the revocation seam that is never called.</summary>
public sealed class LicensingTests : IDisposable
{
    private static readonly FeatureName PaidFeature = new("paid-simulation");
    private static readonly FeatureName OtherPaidFeature = new("paid-export-designer");

    private readonly List<string> _temporaryPaths = [];

    public void Dispose()
    {
        foreach (var path in _temporaryPaths)
        {
            foreach (var candidate in new[] { path, path + "-wal", path + "-shm" })
            {
                try
                {
                    File.Delete(candidate);
                }
                catch (IOException)
                {
                    // Best-effort cleanup; a lingering handle is an orphaned temp file, not a failure.
                }
            }
        }
    }

    // S12.1 -----------------------------------------------------------------------------------

    [Fact]
    public async Task S12_1_A_validly_signed_document_verifies_offline_and_writes_one_row_carrying_every_named_column()
    {
        using var signer = new TestLicenceSigner("key-2026-a");
        var document = WriteDocument(signer, tier: "Studio", features: [PaidFeature], graceDays: 45);

        var sink = new RecordingAuditSink(isDurable: true);
        await using var host = await StartHostAsync(sink, signer.Options(document));

        var outcome = await VerifyAsync(host);
        Assert.True(outcome.IsSuccess);
        Assert.Equal(LicenceVerificationOutcome.Verified, outcome.Value);

        // Exactly one row for the installation — not one per host, not one per tenant (I-L1).
        Assert.Equal(1, await CountAsync(host, "verified_licence"));

        var row = await ReadRowAsync(host);
        Assert.Equal("Studio", row["tier"]);
        Assert.Equal(PaidFeature.Value, row["features"]);
        Assert.False(string.IsNullOrWhiteSpace(row["issued_at"]));
        Assert.False(string.IsNullOrWhiteSpace(row["expires_at"]));
        Assert.False(string.IsNullOrWhiteSpace(row["grace_ends_at"]));
        Assert.False(string.IsNullOrWhiteSpace(row["verified_at"]));

        // The fingerprint is of the document, and the key id names which accepted key verified it.
        Assert.Equal(FingerprintOf(document), row["document_fingerprint"]);
        Assert.Equal("key-2026-a", row["key_id"]);

        var claims = host.Services.GetRequiredService<ILicenceState>().Current;
        Assert.NotNull(claims);
        Assert.Equal(host.Clock.UtcNow, claims.VerifiedAt);

        // Grace and expiry were computed at verification: the grace end is the document's expiry plus
        // the 45 days it named, not anything derived from a later instant.
        Assert.Equal(claims.ExpiresAt!.Value.AddDays(45), claims.GraceEndsAt);
    }

    // S12.2 -----------------------------------------------------------------------------------

    [Fact]
    public async Task S12_2_Claims_survive_a_restart_with_the_document_deleted()
    {
        using var signer = new TestLicenceSigner("key-2026-a");
        var database = TemporaryPath("db");
        var document = WriteDocument(signer, tier: "Studio", features: [PaidFeature], graceDays: 30);

        await using (var first = await StartHostAsync(new RecordingAuditSink(isDurable: true), signer.Options(document), database))
        {
            Assert.True((await VerifyAsync(first)).IsSuccess);
        }

        // The restart: same installation (the database), document gone.
        File.Delete(document);

        await using var restarted = await StartHostAsync(
            new RecordingAuditSink(isDurable: true), signer.Options(document), database);

        var outcome = await VerifyAsync(restarted);
        Assert.True(outcome.IsSuccess);
        Assert.Equal(LicenceVerificationOutcome.Unavailable, outcome.Value);

        var claims = restarted.Services.GetRequiredService<ILicenceState>().Current;
        Assert.NotNull(claims);
        Assert.Equal(new LicenceTier("Studio"), claims.Tier);
        Assert.Contains(PaidFeature, claims.Features);

        // Resolved from the stored row, so the feature is still entitled.
        Assert.True(await GrantsAsync(restarted, PaidFeature));
    }

    // S12.3 -----------------------------------------------------------------------------------

    [Fact]
    public async Task S12_3_Fifty_consecutive_Unavailable_or_ClockUnusable_verifications_leave_every_column_unchanged()
    {
        using var signer = new TestLicenceSigner("key-2026-a");
        var document = WriteDocument(signer, tier: "Studio", features: [PaidFeature], graceDays: 30);

        var sink = new RecordingAuditSink(isDurable: true);
        await using var host = await StartHostAsync(sink, signer.Options(document));

        Assert.True((await VerifyAsync(host)).IsSuccess);
        var before = await ReadRowAsync(host);

        // Twenty-five Unavailable: the document is gone.
        File.Delete(document);
        for (var i = 0; i < 25; i++)
        {
            host.Clock.Advance(TimeSpan.FromMinutes(1));
            var outcome = await VerifyAsync(host);
            Assert.True(outcome.IsSuccess);
            Assert.Equal(LicenceVerificationOutcome.Unavailable, outcome.Value);
        }

        // Twenty-five ClockUnusable: the clock now reads before the stored verification instant.
        host.Clock.SetTo(DateTimeOffset.Parse(before["verified_at"], null).AddDays(-1));
        for (var i = 0; i < 25; i++)
        {
            var outcome = await VerifyAsync(host);
            Assert.True(outcome.IsSuccess);
            Assert.Equal(LicenceVerificationOutcome.ClockUnusable, outcome.Value);
        }

        var after = await ReadRowAsync(host);
        Assert.Equal(before, after);
    }

    // S12.4 -----------------------------------------------------------------------------------

    [Fact]
    public async Task S12_4_A_tampered_or_wrongly_signed_document_grants_no_tier()
    {
        using var signer = new TestLicenceSigner("key-2026-a");
        using var stranger = new TestLicenceSigner("key-2026-a");

        // Signed by a key the deployment does not accept, under an id it does — the wrongly-signed case.
        var wronglySigned = WriteDocument(stranger, tier: "Studio", features: [PaidFeature], graceDays: 30);

        var sink = new RecordingAuditSink(isDurable: true);
        await using var host = await StartHostAsync(sink, signer.Options(wronglySigned));

        var outcome = await VerifyAsync(host);
        Assert.True(outcome.IsSuccess);
        Assert.Equal(LicenceVerificationOutcome.Invalid, outcome.Value);

        // Ignored entirely: not "the claims it would have granted, unverified" (I-L4).
        var state = host.Services.GetRequiredService<ILicenceState>();
        Assert.Null(state.Current);
        Assert.Equal(LicenceTier.Community, state.Tier);
        Assert.False(await GrantsAsync(host, PaidFeature));

        // And no error path wrote a row (I-L2).
        Assert.Equal(0, await CountAsync(host, "verified_licence"));

        // Tampering with the payload of a document signed by an accepted key is Invalid too.
        var tampered = WriteDocument(signer, tier: "Studio", features: [PaidFeature], graceDays: 30, tamper: true);
        await using var second = await StartHostAsync(new RecordingAuditSink(isDurable: true), signer.Options(tampered));

        var tamperedOutcome = await VerifyAsync(second);
        Assert.Equal(LicenceVerificationOutcome.Invalid, tamperedOutcome.Value);
        Assert.Equal(LicenceTier.Community, second.Services.GetRequiredService<ILicenceState>().Tier);
    }

    // S12.5 -----------------------------------------------------------------------------------

    [Fact]
    public async Task S12_5_A_fresh_installation_with_no_row_resolves_Community_and_grants_only_the_baseline()
    {
        using var signer = new TestLicenceSigner("key-2026-a");

        var sink = new RecordingAuditSink(isDurable: true);
        await using var host = await StartHostAsync(sink, signer.Options(TemporaryPath("absent-licence")));

        Assert.True((await VerifyAsync(host)).IsSuccess);

        var state = host.Services.GetRequiredService<ILicenceState>();
        Assert.Null(state.Current);
        Assert.Equal(LicenceTier.Community, state.Tier);
        Assert.Equal(0, await CountAsync(host, "verified_licence"));

        // Licensing grants nothing; the Community baseline's own features are Core's to grant, and it
        // names none by default — so no feature is entitled on a fresh installation.
        Assert.False(await GrantsAsync(host, PaidFeature));
        Assert.False(await GrantsAsync(host, OtherPaidFeature));
    }

    // S12.6 -----------------------------------------------------------------------------------

    [Fact]
    public async Task S12_6_Invalid_and_Unavailable_are_separately_named_in_the_log_and_the_audit_record()
    {
        using var signer = new TestLicenceSigner("key-2026-a");
        using var stranger = new TestLicenceSigner("key-2026-a");

        var invalidDocument = WriteDocument(stranger, tier: "Studio", features: [PaidFeature], graceDays: 30);
        var absentDocument = TemporaryPath("absent-licence");

        // The audit half, on hosts where the module's own verifier is the only one that runs.
        var invalidSink = new RecordingAuditSink(isDurable: true);
        await using (var host = await StartHostAsync(invalidSink, signer.Options(invalidDocument)))
        {
            Assert.Equal(LicenceVerificationOutcome.Invalid, (await VerifyAsync(host)).Value);
        }

        var unavailableSink = new RecordingAuditSink(isDurable: true);
        await using (var host = await StartHostAsync(unavailableSink, signer.Options(absentDocument)))
        {
            Assert.Equal(LicenceVerificationOutcome.Unavailable, (await VerifyAsync(host)).Value);
        }

        // The log half, on their own hosts: a verifier built with a captured logger is a second
        // instance, and its own once-per-detection state is deliberately not shared with the module's.
        var invalidLog = new RecordingLogger();
        await using (var host = await StartHostAsync(new RecordingAuditSink(isDurable: true), signer.Options(invalidDocument)))
        {
            Assert.Equal(LicenceVerificationOutcome.Invalid, (await VerifyWithLoggerAsync(host, invalidLog)).Value);
        }

        var unavailableLog = new RecordingLogger();
        await using (var host = await StartHostAsync(new RecordingAuditSink(isDurable: true), signer.Options(absentDocument)))
        {
            Assert.Equal(
                LicenceVerificationOutcome.Unavailable, (await VerifyWithLoggerAsync(host, unavailableLog)).Value);
        }

        // Separately named in the audit record. The resource reference is where the outcome goes,
        // because AuditEvent carries no free-form detail member and none may be added
        // (design/20-contract.md, Audit §6) — so this is the only place the record can keep them apart.
        var invalidRecord = Assert.Single(RecordsFor(invalidSink, "Invalid:Community"));
        var unavailableRecord = Assert.Single(RecordsFor(unavailableSink, "Unavailable:Community"));
        Assert.Empty(RecordsFor(invalidSink, "Unavailable:Community"));
        Assert.Empty(RecordsFor(unavailableSink, "Invalid:Community"));
        Assert.Contains(
            nameof(LicenceVerificationOutcome.Invalid),
            invalidRecord.Resource!.Value.Id,
            StringComparison.Ordinal);
        Assert.Contains(
            nameof(LicenceVerificationOutcome.Unavailable),
            unavailableRecord.Resource!.Value.Id,
            StringComparison.Ordinal);
        Assert.NotEqual(invalidRecord.Resource!.Value.Id, unavailableRecord.Resource!.Value.Id);

        // Separately named in the log, and at different levels: "invalid" is logged loudly and sends
        // an operator to the key; "unavailable" sends them to the file.
        Assert.Contains(
            invalidLog.Entries,
            entry => entry.Level == LogLevel.Error
                && entry.Message.Contains(nameof(LicenceVerificationOutcome.Invalid), StringComparison.Ordinal));
        Assert.Contains(
            unavailableLog.Entries,
            entry => entry.Message.Contains(nameof(LicenceVerificationOutcome.Unavailable), StringComparison.Ordinal));
        Assert.DoesNotContain(
            unavailableLog.Entries,
            entry => entry.Message.Contains(nameof(LicenceVerificationOutcome.Invalid), StringComparison.Ordinal));

        // Both fell back identically, which is the half of S12.6 that makes the naming matter: two
        // names for one behaviour, so the operator is sent to the right place.
        Assert.Equal(invalidRecord.Outcome, unavailableRecord.Outcome);
    }

    // S12.7 -----------------------------------------------------------------------------------

    [Fact]
    public async Task S12_7_A_backwards_clock_is_evaluated_at_the_stored_verification_instant()
    {
        using var signer = new TestLicenceSigner("key-2026-a");

        // Expired ten days before verification, with thirty days of grace: at the stored verification
        // instant the licence is twenty days inside grace, so it still grants.
        var withinGrace = WriteDocument(
            signer, tier: "Studio", features: [PaidFeature], graceDays: 30, expiresInDays: -10);

        await using var granting = await StartHostAsync(
            new RecordingAuditSink(isDurable: true), signer.Options(withinGrace));

        Assert.True((await VerifyAsync(granting)).IsSuccess);
        var verifiedAt = granting.Services.GetRequiredService<ILicenceState>().Current!.VerifiedAt;

        // Rewinding the clock a year does not expire it early.
        granting.Clock.SetTo(verifiedAt.AddYears(-1));
        Assert.Equal(LicenceVerificationOutcome.ClockUnusable, (await VerifyAsync(granting)).Value);
        Assert.True(await GrantsAsync(granting, PaidFeature));

        // The other way to be wrong: a licence already past its grace end at the stored verification
        // instant must not have grace extended by a clock that reads earlier.
        var pastGrace = WriteDocument(
            signer, tier: "Studio", features: [PaidFeature], graceDays: 30, expiresInDays: -100);

        await using var lapsed = await StartHostAsync(
            new RecordingAuditSink(isDurable: true), signer.Options(pastGrace));

        Assert.True((await VerifyAsync(lapsed)).IsSuccess);
        Assert.False(await GrantsAsync(lapsed, PaidFeature));

        lapsed.Clock.SetTo(lapsed.Services.GetRequiredService<ILicenceState>().Current!.VerifiedAt.AddYears(-1));
        Assert.False(await GrantsAsync(lapsed, PaidFeature));
    }

    // S12.8 -----------------------------------------------------------------------------------

    [Fact]
    public async Task S12_8_Two_hosts_converge_on_the_later_verification__the_older_answers_SupersededByNewerVerification()
    {
        using var signer = new TestLicenceSigner("key-2026-a");
        var database = TemporaryPath("db");

        var olderDocument = WriteDocument(signer, tier: "Studio", features: [PaidFeature], graceDays: 30);
        var newerDocument = WriteDocument(signer, tier: "Enterprise", features: [OtherPaidFeature], graceDays: 30);

        // One installation, two hosts, two clocks.
        await using var older = await StartHostAsync(
            new RecordingAuditSink(isDurable: true), signer.Options(olderDocument), database);
        await using var newer = await StartHostAsync(
            new RecordingAuditSink(isDurable: true), signer.Options(newerDocument), database);

        // Each host's clock is advanced explicitly, so the two verification instants are ordered by
        // construction rather than by whatever the two startup verifications raced to do.
        older.Clock.Advance(TimeSpan.FromHours(1));
        Assert.True((await VerifyAsync(older)).IsSuccess);

        newer.Clock.Advance(TimeSpan.FromHours(2));
        Assert.True((await VerifyAsync(newer)).IsSuccess);
        Assert.Equal(new LicenceTier("Enterprise"), newer.Services.GetRequiredService<ILicenceState>().Tier);

        // The host still holding the replaced document re-verifies with its own, earlier clock. The
        // monotonic guard rejects the stale write.
        var superseded = await VerifyAsync(older);
        Assert.False(superseded.IsSuccess);
        Assert.Equal(nameof(LicensingError.SupersededByNewerVerification), superseded.Error.Code);
        Assert.False(superseded.Error.IsRetryable);

        // Both hosts converged on the newer verification, and the row still says so.
        Assert.Equal(new LicenceTier("Enterprise"), older.Services.GetRequiredService<ILicenceState>().Tier);
        Assert.Equal("Enterprise", (await ReadRowAsync(older))["tier"]);
        Assert.Equal(1, await CountAsync(older, "verified_licence"));
    }

    // S12.9 -----------------------------------------------------------------------------------

    [Fact]
    public async Task S12_9_After_grace_new_paid_work_is_refused_while_running_work_finishes_and_reads_and_exports_succeed()
    {
        var clock = new FakeClock();
        var holder = new LicenceStateHolder();
        var issuedAt = clock.UtcNow;

        holder.Set(
            new LicenceClaims(
                new LicenceTier("Studio"),
                new HashSet<FeatureName> { PaidFeature },
                issuedAt,
                issuedAt.AddDays(1),
                issuedAt.AddDays(31),
                issuedAt),
            LicenceVerificationOutcome.Verified);

        var permission = new PermissionName("Test.Licensing.Use");

        var (app, client) = await WebHostUnderTest.StartAsync(
            services =>
            {
                services.AddSingleton<IPermissionCatalog>(new StubPermissionCatalog(permission));
                services.AddSingleton<IPermissionProvider>(new StubPermissionProvider(
                    "licensing-permission",
                    (_, _, _) => Result<IReadOnlySet<PermissionName>, AuthorizationError>.Success(
                        new HashSet<PermissionName> { permission })));
                services.AddKeyedSingleton<IEntitlementContributor>(
                    EntitlementContributorRegistration.ServiceKey,
                    new LicensingEntitlementContributor(holder, clock));
            },
            mapEndpoints: application =>
            {
                // Admits NEW paid-feature work, so it declares the feature and is gated at step 5.
                application.MapPost("/simulations", () => Results.Ok(new { started = true }))
                    .RequiresPlatformAuthorization(permission, PaidFeature);

                // Work already accepted, running or scheduled, plus reads, lists and exports of
                // existing data: none of these admits new paid work, so none declares a feature —
                // entitlement was checked at the admission that produced the data (I-L8).
                application.MapPost("/simulations/running/complete", () => Results.Ok(new { completed = true }))
                    .RequiresPlatformAuthorization(permission, feature: null);
                application.MapGet("/simulations", () => Results.Ok(new[] { "existing" }))
                    .RequiresPlatformAuthorization(permission, feature: null);
                application.MapGet("/simulations/existing", () => Results.Ok(new { id = "existing" }))
                    .RequiresPlatformAuthorization(permission, feature: null);
                application.MapGet("/simulations/existing/export", () => Results.Ok(new { rows = 3 }))
                    .RequiresPlatformAuthorization(permission, feature: null);
            });

        await using (app)
        using (client)
        {
            // Inside grace: expired, but grace has not ended, so new paid work is still admitted.
            clock.Advance(TimeSpan.FromDays(2));
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/simulations", null)).StatusCode);

            // Past the grace end.
            clock.Advance(TimeSpan.FromDays(40));

            // 402, not 403: the fixed request order answers an entitlement refusal separately from an
            // authorization refusal, so an operator can tell "you may not" from "this deployment is
            // not entitled".
            Assert.Equal(
                HttpStatusCode.PaymentRequired,
                (await client.PostAsync("/simulations", null)).StatusCode);

            Assert.Equal(
                HttpStatusCode.OK,
                (await client.PostAsync("/simulations/running/complete", null)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/simulations")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/simulations/existing")).StatusCode);
            Assert.Equal(
                HttpStatusCode.OK,
                (await client.GetAsync("/simulations/existing/export")).StatusCode);
        }
    }

    // S12.10 ----------------------------------------------------------------------------------

    [Fact]
    public async Task S12_10_Grace_defaults_to_thirty_days_and_no_option_can_set_it()
    {
        using var signer = new TestLicenceSigner("key-2026-a");

        // The document names no grace at all.
        var document = WriteDocument(signer, tier: "Studio", features: [PaidFeature], graceDays: null);

        await using var host = await StartHostAsync(new RecordingAuditSink(isDurable: true), signer.Options(document));
        Assert.True((await VerifyAsync(host)).IsSuccess);

        var claims = host.Services.GetRequiredService<ILicenceState>().Current;
        Assert.NotNull(claims);
        Assert.Equal(claims.ExpiresAt!.Value.AddDays(30), claims.GraceEndsAt);

        // And the options type exposes no member by which a deployment could set it (I-L6).
        var settable = typeof(LicensingOptions)
            .GetProperties()
            .Select(property => property.Name)
            .Concat(typeof(LicensingOptions).GetFields().Select(field => field.Name))
            .Concat(typeof(LicensingOptions)
                .GetConstructors()
                .SelectMany(constructor => constructor.GetParameters())
                .Select(parameter => parameter.Name ?? string.Empty));

        Assert.DoesNotContain(settable, name => name.Contains("grace", StringComparison.OrdinalIgnoreCase));
    }

    // S12.11 ----------------------------------------------------------------------------------

    [Fact]
    public async Task S12_11_Signing_keys_are_a_required_ordered_option_and_the_second_key_verifies()
    {
        using var first = new TestLicenceSigner("key-2025-retiring");
        using var second = new TestLicenceSigner("key-2026-current");

        // Required: no key set at all is refused outright, because Platform compiles none in (I-L7).
        Assert.Throws<ArgumentException>(() => new LicensingOptions([], "licence.json"));

        // A document signed by the SECOND key in the ordered set verifies — rotation without a flag day.
        var document = WriteDocument(second, tier: "Studio", features: [PaidFeature], graceDays: 30);
        var options = new LicensingOptions([first.AcceptedKey, second.AcceptedKey], document);

        await using var host = await StartHostAsync(new RecordingAuditSink(isDurable: true), options);

        Assert.Equal(LicenceVerificationOutcome.Verified, (await VerifyAsync(host)).Value);
        Assert.Equal("key-2026-current", (await ReadRowAsync(host))["key_id"]);
    }

    // S12.12 ----------------------------------------------------------------------------------

    [Fact]
    public async Task S12_12_A_host_whose_document_is_unreadable_starts_reports_ready_and_serves()
    {
        using var signer = new TestLicenceSigner("key-2026-a");

        // Not merely absent: a directory where a file is expected, which faults the read.
        var unreadable = TemporaryPath("unreadable-licence");
        Directory.CreateDirectory(unreadable);

        try
        {
            var sink = new RecordingAuditSink(isDurable: true);

            // Starting at all is the first half: the startup verification runs and never throws.
            await using var host = await StartHostAsync(sink, signer.Options(unreadable), startupVerification: true);

            var readiness = await host.ProbeAsync(HealthCheckKind.Readiness, CancellationToken.None);
            Assert.Equal(HealthStatus.Healthy, readiness.Aggregate);

            // Licensing registers no health check at all, so nothing about a licence can degrade
            // readiness.
            Assert.DoesNotContain(
                readiness.Entries,
                check => check.Name.Value.Contains("licen", StringComparison.OrdinalIgnoreCase));

            // And it serves: a verification failure never fails a request either.
            Assert.True((await VerifyAsync(host)).IsSuccess);
            Assert.False(await GrantsAsync(host, PaidFeature));
            Assert.Equal(LicenceTier.Community, host.Services.GetRequiredService<ILicenceState>().Tier);
        }
        finally
        {
            Directory.Delete(unreadable);
        }
    }

    // S12.13 ----------------------------------------------------------------------------------

    [Fact]
    public async Task S12_13_The_revocation_seam_is_never_called__absent_or_registered()
    {
        using var signer = new TestLicenceSigner("key-2026-a");
        var document = WriteDocument(signer, tier: "Studio", features: [PaidFeature], graceDays: 30);

        // Absent.
        await using (var without = await StartHostAsync(
            new RecordingAuditSink(isDurable: true), signer.Options(document), startupVerification: true))
        {
            Assert.Empty(without.Services.GetServices<IRevocationCheck>());
            await ExerciseEveryPathAsync(without);
        }

        // Registered, with a check that fails the test the moment anything calls it.
        var exploding = new ExplodingRevocationCheck();
        await using (var with = await StartHostAsync(
            new RecordingAuditSink(isDurable: true),
            signer.Options(document),
            extra: services => services.AddSingleton<IRevocationCheck>(exploding),
            startupVerification: true))
        {
            Assert.Same(exploding, Assert.Single(with.Services.GetServices<IRevocationCheck>()));
            await ExerciseEveryPathAsync(with);
        }

        // Startup, readiness and every request: no outbound call on any of the three (I-L5).
        Assert.Equal(0, exploding.Calls);
    }

    // S12.14 ----------------------------------------------------------------------------------

    [Fact]
    public async Task S12_14_A_state_change_writes_one_audit_record_per_detection__a_thousand_requests_write_one()
    {
        using var signer = new TestLicenceSigner("key-2026-a");

        // Already expired and past grace at verification: one detected state, held for a thousand checks.
        var document = WriteDocument(
            signer, tier: "Studio", features: [PaidFeature], graceDays: 30, expiresInDays: -90);

        var sink = new RecordingAuditSink(isDurable: true);
        await using var host = await StartHostAsync(sink, signer.Options(document));

        Assert.True((await VerifyAsync(host)).IsSuccess);

        // Exactly one record for THIS detected state. (The host's own startup verification is a real,
        // earlier detection of a different state — it ran before the test applied migrations — so the
        // assertion is per state, which is what "once per detection" means.)
        var afterDetection = Assert.Single(RecordsFor(sink, "Verified:Studio"));
        Assert.Equal(PlatformAuditActions.LicenceStateChanged.Value, afterDetection.Action.Value);
        Assert.Equal(AuditClass.Required, afterDetection.Class);

        var baseline = LicensingRecords(sink).Count;

        // A thousand requests against that one expired licence.
        var evaluator = host.Services.GetRequiredService<IEntitlementEvaluator>();
        var scopeFactory = host.Services.GetRequiredService<IOperationScopeFactory>();
        using (scopeFactory.Begin(TenantId.Implicit, Principal.LocalSystem))
        {
            for (var i = 0; i < 1000; i++)
            {
                Assert.False((await evaluator.EvaluateAsync(PaidFeature, CancellationToken.None)).Granted);
            }
        }

        // A thousand entitlement evaluations wrote nothing at all: request-time evaluation reads the
        // in-memory claims and never reaches the detection or audit path.
        Assert.Equal(baseline, LicensingRecords(sink).Count);
        Assert.Single(RecordsFor(sink, "Verified:Studio"));

        // Re-detecting the same state writes nothing further either — once per detection, not once
        // per check.
        for (var i = 0; i < 5; i++)
        {
            host.Clock.Advance(TimeSpan.FromMinutes(1));
            Assert.True((await VerifyAsync(host)).IsSuccess);
        }

        Assert.Equal(baseline, LicensingRecords(sink).Count);

        // A genuine change in detected state does write a further record.
        File.Delete(document);
        host.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(LicenceVerificationOutcome.Unavailable, (await VerifyAsync(host)).Value);
        Assert.Equal(baseline + 1, LicensingRecords(sink).Count);
    }

    // Helpers -----------------------------------------------------------------------------------

    /// <summary>Runs every path a deployment without outbound network takes — startup (already done by
    /// the caller), readiness, and a request — so a registered revocation check that was consulted on
    /// any of them would have thrown by the time this returns.</summary>
    private static async Task ExerciseEveryPathAsync(IPlatformTestHost host)
    {
        var readiness = await host.ProbeAsync(HealthCheckKind.Readiness, CancellationToken.None);
        Assert.Equal(HealthStatus.Healthy, readiness.Aggregate);
        await host.ProbeAsync(HealthCheckKind.Liveness, CancellationToken.None);

        Assert.True((await VerifyAsync(host)).IsSuccess);
        await GrantsAsync(host, PaidFeature);
    }

    /// <summary>Starts a host with Licensing registered.</summary>
    /// <remarks><paramref name="startupVerification"/> defaults to false because
    /// <see cref="PlatformTestHost"/> applies migrations <em>after</em> the host starts, so the
    /// startup verification would always run against a database with no <c>verified_licence</c> table
    /// and record a spurious <c>Unavailable</c> detection ahead of whatever the test means to
    /// exercise. Removing it is the same idiom <see cref="PlatformTestHost"/> already uses to strip
    /// <c>BackgroundWorkService</c>'s timer. S12.12 and S12.13 pass true, because for those two the
    /// startup path is the thing under test.</remarks>
    private async Task<IPlatformTestHost> StartHostAsync(
        RecordingAuditSink sink,
        LicensingOptions options,
        string? databasePath = null,
        Action<IServiceCollection>? extra = null,
        bool startupVerification = false)
    {
        var builder = PlatformTestHost.CreateBuilder().WithProvider(PersistenceProvider.Sqlite);

        if (databasePath is not null)
        {
            builder = builder.WithSetting("Persistence:ConnectionString", $"Data Source={databasePath}");
        }

        var host = await builder
            .WithServices(services =>
            {
                services.AddSingleton<IPlatformModule, LicensingModule>();
                services.AddSingleton(options);
                services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuditSink>(sink));

                extra?.Invoke(services);

                if (!startupVerification)
                {
                    var startup = services
                        .Where(descriptor => descriptor.ImplementationType == typeof(LicenceStartupVerification))
                        .ToList();

                    foreach (var descriptor in startup)
                    {
                        services.Remove(descriptor);
                    }
                }
            })
            .StartAsync(CancellationToken.None);

        var migrated = await host.Services.GetRequiredService<IMigrationRunner>().ApplyAsync(CancellationToken.None);
        Assert.True(migrated.IsSuccess);
        return host;
    }

    /// <summary>Verification is triggered explicitly rather than left to the startup hosted service,
    /// because the test host applies migrations after start — and because a slice that runs
    /// verification on a timer is exactly what <c>design/10-design.md</c> forbids, so there is no tick
    /// to wait for.</summary>
    private static Task<Result<LicenceVerificationOutcome, LicensingError>> VerifyAsync(IPlatformTestHost host) =>
        host.Services.GetRequiredService<ILicenceVerifier>().VerifyOnceAsync(CancellationToken.None);

    /// <summary>Verifies through a verifier built with <paramref name="logger"/> in place of the
    /// container's, so a test can read what the verifier logged.</summary>
    private static Task<Result<LicenceVerificationOutcome, LicensingError>> VerifyWithLoggerAsync(
        IPlatformTestHost host, RecordingLogger logger) =>
        ActivatorUtilities.CreateInstance<LicenceVerifier>(host.Services, logger)
            .VerifyOnceAsync(CancellationToken.None);

    private static async Task<bool> GrantsAsync(IPlatformTestHost host, FeatureName feature)
    {
        var evaluator = host.Services.GetRequiredService<IEntitlementEvaluator>();
        var scopeFactory = host.Services.GetRequiredService<IOperationScopeFactory>();
        using var scope = scopeFactory.Begin(TenantId.Implicit, Principal.LocalSystem);
        return (await evaluator.EvaluateAsync(feature, CancellationToken.None)).Granted;
    }

    /// <summary>Every licensing record naming one detected state. The state's name lives in the
    /// resource reference, which is the only member of an audit record that can carry it.</summary>
    private static IReadOnlyList<AuditEvent> RecordsFor(RecordingAuditSink sink, string state) =>
        LicensingRecords(sink)
            .Where(record => record.Resource?.Id == state)
            .ToList();

    private static IReadOnlyList<AuditEvent> LicensingRecords(RecordingAuditSink sink) =>
        sink.Received
            .Where(record => record.Action.Value == PlatformAuditActions.LicenceStateChanged.Value)
            .ToList();

    private static async Task<int> CountAsync(IPlatformTestHost host, string table)
    {
        var unitOfWork = host.Services.GetRequiredService<IUnitOfWork>();
        var ambient = host.Services.GetRequiredService<IAmbientTransactionAccessor>();
        var result = await unitOfWork.ExecuteAsync(
            TransactionIntent.ReadOnly,
            async ct =>
            {
                await using var command = ambient.Current!.Connection.CreateCommand();
                command.Transaction = ambient.Current!.Transaction;
                command.CommandText = $"SELECT COUNT(*) FROM {table};";
                return Convert.ToInt32(await command.ExecuteScalarAsync(ct));
            },
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        return result.Value;
    }

    /// <summary>Reads every column of the one row, as strings, so a before/after comparison covers the
    /// whole row rather than the columns the test remembered to name (S12.3).</summary>
    private static async Task<Dictionary<string, string>> ReadRowAsync(IPlatformTestHost host)
    {
        var unitOfWork = host.Services.GetRequiredService<IUnitOfWork>();
        var ambient = host.Services.GetRequiredService<IAmbientTransactionAccessor>();
        var result = await unitOfWork.ExecuteAsync(
            TransactionIntent.ReadOnly,
            async ct =>
            {
                await using var command = ambient.Current!.Connection.CreateCommand();
                command.Transaction = ambient.Current!.Transaction;
                command.CommandText = "SELECT * FROM verified_licence;";

                var row = new Dictionary<string, string>(StringComparer.Ordinal);
                await using var reader = await command.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct))
                {
                    for (var i = 0; i < reader.FieldCount; i++)
                    {
                        row[reader.GetName(i)] = reader.IsDBNull(i) ? "<null>" : reader.GetValue(i).ToString()!;
                    }
                }

                return row;
            },
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.NotEmpty(result.Value);
        return result.Value;
    }

    private static string FingerprintOf(string path) =>
        Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    private string TemporaryPath(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), $"platform-{prefix}-{Guid.NewGuid():N}");
        _temporaryPaths.Add(path);
        return path;
    }

    /// <summary>Writes a licence document, signed with <paramref name="signer"/>'s private key in the
    /// format <c>LicenceDocumentReader</c> verifies. Uses the reader's own serialisation and signing
    /// helpers rather than a second implementation of the format, which would drift.</summary>
    private string WriteDocument(
        TestLicenceSigner signer,
        string tier,
        IReadOnlyList<FeatureName> features,
        int? graceDays,
        int expiresInDays = 365,
        bool tamper = false)
    {
        var issuedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var path = TemporaryPath("licence") + ".json";
        File.WriteAllBytes(
            path,
            signer.Mint(tier, features, issuedAt, issuedAt.AddDays(expiresInDays), graceDays, tamper));
        return path;
    }

    /// <summary>An accepted key pair and the documents it mints.</summary>
    private sealed class TestLicenceSigner(string keyId) : IDisposable
    {
        private readonly RSA _key = RSA.Create(2048);

        /// <summary>The public half, as a deployment would supply it.</summary>
        internal LicenceSigningKey AcceptedKey
        {
            get
            {
                var publicKey = RSA.Create();
                publicKey.ImportSubjectPublicKeyInfo(_key.ExportSubjectPublicKeyInfo(), out _);
                return new LicenceSigningKey(keyId, publicKey);
            }
        }

        internal LicensingOptions Options(string documentPath) => new([AcceptedKey], documentPath);

        internal byte[] Mint(
            string tier,
            IReadOnlyList<FeatureName> features,
            DateTimeOffset issuedAt,
            DateTimeOffset? expiresAt,
            int? graceDays,
            bool tamper)
        {
            var payload = new LicenceDocumentPayload(
                tier,
                features.Select(feature => feature.Value).ToList(),
                issuedAt.ToString("O"),
                expiresAt?.ToString("O"),
                graceDays);

            var payloadBytes = LicenceDocumentReader.SerializePayload(payload);
            var signature = LicenceDocumentReader.Sign(_key, payloadBytes);

            if (tamper)
            {
                // The signature stays valid for the ORIGINAL payload, and the payload that travels is
                // a different one — which is what a tampered document is.
                var tamperedPayload = payload with { Tier = tier + "-elevated" };
                payloadBytes = LicenceDocumentReader.SerializePayload(tamperedPayload);
            }

            return LicenceDocumentReader.SerializeEnvelope(new LicenceDocumentEnvelope(
                keyId,
                Convert.ToBase64String(payloadBytes),
                Convert.ToBase64String(signature)));
        }

        public void Dispose() => _key.Dispose();
    }

    /// <summary>A revocation check that fails the test the moment anything calls it. S12.13's whole
    /// assertion is that nothing does.</summary>
    private sealed class ExplodingRevocationCheck : IRevocationCheck
    {
        private int _calls;

        internal int Calls => _calls;

        public string Name => "exploding-revocation";

        public Task<bool> IsRevokedAsync(string documentFingerprint, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            throw new InvalidOperationException(
                "The revocation seam was consulted. I-L5 says it is consulted on no path a deployment "
                + "without outbound network takes — not the request path, not startup, not readiness.");
        }
    }

    /// <summary>Captures what the verifier logs, so S12.6 can assert that "invalid" and "unavailable"
    /// reach an operator by different names.</summary>
    /// <remarks>Injected into a verifier built for the test rather than registered as an
    /// <see cref="ILoggerProvider"/>: Platform composes Serilog with <c>writeToProviders: false</c>,
    /// so a registered provider is never written to and would assert nothing.</remarks>
    private sealed class RecordingLogger : ILogger<LicenceVerifier>
    {
        private readonly List<(LogLevel Level, string Message)> _entries = [];
        private readonly Lock _gate = new();

        internal IReadOnlyList<(LogLevel Level, string Message)> Entries
        {
            get { lock (_gate) { return _entries.ToList(); } }
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_gate)
            {
                _entries.Add((logLevel, formatter(state, exception)));
            }
        }
    }
}
