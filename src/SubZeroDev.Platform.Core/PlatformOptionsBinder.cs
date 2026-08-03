using Microsoft.Extensions.Configuration;
using SubZeroDev.Platform.Abstractions;

namespace SubZeroDev.Platform.Core;

/// <summary>Binds and validates <see cref="PlatformOptions"/> from configuration.</summary>
/// <remarks>Bound by hand rather than by the generic binder, because the contract requires a
/// missing setting to name <em>the configuration source expected to supply it</em>, and a reflection
/// binder cannot say that. Doing it here is also what lets every constraint in the settings
/// inventory fail startup with the setting and the constraint named.</remarks>
internal static class PlatformOptionsBinder
{
    /// <summary>The section every Platform setting hangs from.</summary>
    internal const string SectionName = "Platform";

    /// <summary>Named in a missing-setting error, so an operator knows where to put the value.</summary>
    private const string ExpectedSource =
        "appsettings.json, an environment variable, or any other configured provider";

    internal static Result<PlatformOptions, ConfigurationError> Bind(
        IConfiguration configuration,
        string environment,
        HostRole role)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(SectionName);
        var reader = new Reader(section);

        var provider = reader.RequiredEnum<PersistenceProvider>("Persistence:Provider");
        var connectionString = reader.RequiredString("Persistence:ConnectionString");
        var busyWait = reader.TimeSpan("Persistence:SqliteBusyWaitBound", TimeSpan.FromSeconds(5), Positive);

        var processedRetention = reader.RequiredTimeSpan("Outbox:ProcessedRetention", Positive);
        var poisonedRetention = reader.RequiredTimeSpan("Outbox:PoisonedRetention", Positive);
        var claimWindow = reader.TimeSpan("Outbox:ClaimWindow", TimeSpan.FromMinutes(5), Positive);
        var poisonAttempts = reader.Int32("Outbox:PoisonAttemptCount", 12, AtLeast(1));
        var backoffBase = reader.TimeSpan("Outbox:RetryBackoffBase", TimeSpan.FromSeconds(30), Positive);
        var backoffFactor = reader.Double("Outbox:RetryBackoffFactor", 2, GreaterThan(1));
        var backoffCap = reader.TimeSpan("Outbox:RetryBackoffCap", TimeSpan.FromHours(6), Positive);
        var deferralAge = reader.TimeSpan("Outbox:DeferralAge", TimeSpan.FromHours(24), Positive);
        var deferralRetry = reader.TimeSpan("Outbox:DeferralRetryInterval", TimeSpan.FromMinutes(1), Positive);
        var tickBudget = reader.Int32("Outbox:DispatchTickBudget", 20, Between(1, 1_000));
        var pruneBatch = reader.Int32("Outbox:PruneBatchSize", 500, Between(1, 5_000));
        var dispatchInterval = reader.TimeSpan("Outbox:DispatchInterval", TimeSpan.FromSeconds(5), Positive);

        var leaseDuration = reader.TimeSpan("Lease:Duration", TimeSpan.FromMinutes(5), Positive);

        var heartbeat = reader.TimeSpan("HostRegistration:HeartbeatInterval", TimeSpan.FromSeconds(15), Positive);
        var registrationRetention = reader.TimeSpan("HostRegistration:RetentionWindow", TimeSpan.FromDays(7), Positive);
        var peerGrace = reader.TimeSpan("HostRegistration:PeerAbsenceGrace", TimeSpan.FromSeconds(60), NonNegative);

        var backlogAge = reader.TimeSpan("Health:BacklogAgeThreshold", TimeSpan.FromMinutes(5), Positive);
        var pendingCount = reader.Int64("Health:PendingCountThreshold", 100_000, AtLeast(1L));

        var drainWindow = reader.TimeSpan("Hosting:GracefulShutdownDrainWindow", TimeSpan.FromSeconds(30), Positive);
        var probePort = reader.Int32("Hosting:WorkerProbePort", 5100, Between(1, 65_535));
        var loopbackOnly = reader.Boolean("Hosting:WorkerProbeLoopbackOnly", true);

        if (reader.Error is { } error)
        {
            return Result<PlatformOptions, ConfigurationError>.Failure(error);
        }

        // Joint constraints, checked only once every value is individually valid — otherwise a
        // missing setting would be reported as an inconsistency between it and something else.
        if (poisonedRetention <= processedRetention)
        {
            return Failure(ConfigurationError.InconsistentSettings(
                Key("Outbox:PoisonedRetention"),
                Key("Outbox:ProcessedRetention"),
                "the poison retention window must be strictly longer than the processed one, so forensics outlive routine cleanup"));
        }

        if (backoffCap < backoffBase)
        {
            return Failure(ConfigurationError.InconsistentSettings(
                Key("Outbox:RetryBackoffCap"),
                Key("Outbox:RetryBackoffBase"),
                "the backoff cap cannot be shorter than the base delay"));
        }

        if (drainWindow >= claimWindow)
        {
            return Failure(ConfigurationError.InconsistentSettings(
                Key("Hosting:GracefulShutdownDrainWindow"),
                Key("Outbox:ClaimWindow"),
                "the drain window must be shorter than the claim window that backstops it"));
        }

        return Result<PlatformOptions, ConfigurationError>.Success(new PlatformOptions
        {
            ServiceName = reader.OptionalString("ServiceName"),
            ServiceVersion = reader.OptionalString("ServiceVersion"),
            Environment = environment,
            Role = role,
            Persistence = new PersistenceOptions
            {
                Provider = provider,
                ConnectionString = connectionString ?? string.Empty,
                SqliteBusyWaitBound = busyWait,
            },
            Outbox = new OutboxOptions
            {
                ProcessedRetention = processedRetention,
                PoisonedRetention = poisonedRetention,
                ClaimWindow = claimWindow,
                PoisonAttemptCount = poisonAttempts,
                RetryBackoffBase = backoffBase,
                RetryBackoffFactor = backoffFactor,
                RetryBackoffCap = backoffCap,
                DeferralAge = deferralAge,
                DeferralRetryInterval = deferralRetry,
                DispatchTickBudget = tickBudget,
                PruneBatchSize = pruneBatch,
                DispatchInterval = dispatchInterval,
            },
            Lease = new LeaseOptions { Duration = leaseDuration },
            HostRegistration = new HostRegistrationOptions
            {
                HeartbeatInterval = heartbeat,
                RetentionWindow = registrationRetention,
                PeerAbsenceGrace = peerGrace,
            },
            Health = new HealthOptions
            {
                BacklogAgeThreshold = backlogAge,
                PendingCountThreshold = pendingCount,
            },
            Hosting = new HostingOptions
            {
                GracefulShutdownDrainWindow = drainWindow,
                WorkerProbePort = probePort,
                WorkerProbeLoopbackOnly = loopbackOnly,
            },
        });
    }

    internal static string Key(string path) => $"{SectionName}:{path}";

    private static Result<PlatformOptions, ConfigurationError> Failure(ConfigurationError error) =>
        Result<PlatformOptions, ConfigurationError>.Failure(error);

    private static (bool Ok, string Constraint) Positive(TimeSpan value) =>
        (value > System.TimeSpan.Zero, "must be greater than zero");

    private static (bool Ok, string Constraint) NonNegative(TimeSpan value) =>
        (value >= System.TimeSpan.Zero, "cannot be negative");

    private static Func<int, (bool, string)> AtLeast(int minimum) =>
        value => (value >= minimum, $"must be at least {minimum}");

    private static Func<long, (bool, string)> AtLeast(long minimum) =>
        value => (value >= minimum, $"must be at least {minimum}");

    private static Func<int, (bool, string)> Between(int minimum, int maximum) =>
        value => (value >= minimum && value <= maximum, $"must be between {minimum} and {maximum}");

    private static Func<double, (bool, string)> GreaterThan(double minimum) =>
        value => (value > minimum, $"must be greater than {minimum}");

    /// <summary>Reads one value at a time and keeps the first failure, so validation reports the
    /// setting an operator must fix rather than the last one checked.</summary>
    private sealed class Reader(IConfiguration section)
    {
        internal ConfigurationError? Error { get; private set; }

        internal string? OptionalString(string path) => Raw(path);

        internal string? RequiredString(string path)
        {
            var raw = Raw(path);
            if (!string.IsNullOrWhiteSpace(raw))
            {
                return raw;
            }

            Fail(ConfigurationError.MissingRequiredSetting(Key(path), ExpectedSource));
            return null;
        }

        internal TEnum RequiredEnum<TEnum>(string path)
            where TEnum : struct, Enum
        {
            var raw = Raw(path);
            if (string.IsNullOrWhiteSpace(raw))
            {
                Fail(ConfigurationError.MissingRequiredSetting(Key(path), ExpectedSource));
                return default;
            }

            if (Enum.TryParse<TEnum>(raw, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed))
            {
                return parsed;
            }

            Fail(ConfigurationError.InvalidSetting(
                Key(path),
                $"must be one of {string.Join(", ", Enum.GetNames<TEnum>())}"));
            return default;
        }

        internal TimeSpan RequiredTimeSpan(string path, Func<TimeSpan, (bool Ok, string Constraint)> constraint)
        {
            var raw = Raw(path);
            if (string.IsNullOrWhiteSpace(raw))
            {
                Fail(ConfigurationError.MissingRequiredSetting(Key(path), ExpectedSource));
                return default;
            }

            return ParseTimeSpan(path, raw, constraint);
        }

        internal TimeSpan TimeSpan(
            string path,
            TimeSpan fallback,
            Func<TimeSpan, (bool Ok, string Constraint)> constraint)
        {
            var raw = Raw(path);
            return string.IsNullOrWhiteSpace(raw) ? fallback : ParseTimeSpan(path, raw, constraint);
        }

        internal int Int32(string path, int fallback, Func<int, (bool Ok, string Constraint)> constraint)
        {
            var raw = Raw(path);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return fallback;
            }

            if (!int.TryParse(raw, out var parsed))
            {
                Fail(ConfigurationError.InvalidSetting(Key(path), "must be a whole number"));
                return fallback;
            }

            return Check(path, parsed, constraint, fallback);
        }

        internal long Int64(string path, long fallback, Func<long, (bool Ok, string Constraint)> constraint)
        {
            var raw = Raw(path);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return fallback;
            }

            if (!long.TryParse(raw, out var parsed))
            {
                Fail(ConfigurationError.InvalidSetting(Key(path), "must be a whole number"));
                return fallback;
            }

            return Check(path, parsed, constraint, fallback);
        }

        internal double Double(string path, double fallback, Func<double, (bool Ok, string Constraint)> constraint)
        {
            var raw = Raw(path);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return fallback;
            }

            if (!double.TryParse(raw, out var parsed))
            {
                Fail(ConfigurationError.InvalidSetting(Key(path), "must be a number"));
                return fallback;
            }

            return Check(path, parsed, constraint, fallback);
        }

        internal bool Boolean(string path, bool fallback)
        {
            var raw = Raw(path);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return fallback;
            }

            if (bool.TryParse(raw, out var parsed))
            {
                return parsed;
            }

            Fail(ConfigurationError.InvalidSetting(Key(path), "must be true or false"));
            return fallback;
        }

        private TimeSpan ParseTimeSpan(
            string path,
            string raw,
            Func<TimeSpan, (bool Ok, string Constraint)> constraint)
        {
            if (!System.TimeSpan.TryParse(raw, out var parsed))
            {
                Fail(ConfigurationError.InvalidSetting(Key(path), "must be a duration such as 7.00:00:00 or 00:05:00"));
                return default;
            }

            return Check(path, parsed, constraint, default);
        }

        private T Check<T>(string path, T value, Func<T, (bool Ok, string Constraint)> constraint, T fallback)
        {
            var (ok, description) = constraint(value);
            if (ok)
            {
                return value;
            }

            Fail(ConfigurationError.InvalidSetting(Key(path), description));
            return fallback;
        }

        private string? Raw(string path) => section[path];

        private void Fail(ConfigurationError error) => Error ??= error;
    }
}
