using System.Diagnostics;
using OpenTelemetry;

namespace SubZeroDev.Platform.Observability;

/// <summary>Defence-in-depth over span attributes: Platform's own instrumentation never sets a
/// sensitive tag (the unit-of-work span carries only <c>db.system</c> and <c>operation</c>, and the
/// official ASP.NET Core/HTTP instrumentation is not configured to capture headers or bodies), but a
/// third-party library sharing the process could still add one to the ambient
/// <see cref="Activity"/>. This runs before export and overwrites any tag whose key matches the
/// fixed redaction boundary.</summary>
internal sealed class RedactingActivityProcessor : BaseProcessor<Activity>
{
    public override void OnEnd(Activity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);

        List<KeyValuePair<string, object?>>? sensitive = null;
        foreach (var tag in activity.TagObjects)
        {
            if (Redaction.IsSensitiveKey(tag.Key))
            {
                (sensitive ??= []).Add(tag);
            }
        }

        if (sensitive is null)
        {
            return;
        }

        foreach (var tag in sensitive)
        {
            activity.SetTag(tag.Key, Redaction.RedactedValue);
        }
    }
}
