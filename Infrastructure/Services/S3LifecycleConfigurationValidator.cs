using Amazon.S3;
using Amazon.S3.Model;

namespace Infrastructure.Services;

/// <summary>
/// Verifies the bucket lifecycle rules that protect unreferenced PII/blob
/// candidates.  S3 lifecycle is a safety net for process crashes, so a bucket
/// probe that only checks credentials is not sufficient.
/// </summary>
public interface IS3LifecycleHealthProbe
{
    Task ValidateLifecycleAsync(CancellationToken cancellationToken = default);
}

internal static class S3LifecycleConfigurationValidator
{
    public static async Task RequireAsync(
        IAmazonS3 client,
        string bucket,
        IReadOnlyList<S3LifecycleRequirement> requirements,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (string.IsNullOrWhiteSpace(bucket))
            throw new InvalidOperationException("S3 bucket is required for lifecycle validation.");

        GetLifecycleConfigurationResponse response;
        try
        {
            response = await client.GetLifecycleConfigurationAsync(
                    new GetLifecycleConfigurationRequest { BucketName = bucket },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (AmazonS3Exception ex) when (
            string.Equals(ex.ErrorCode, "NoSuchLifecycleConfiguration", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"S3 bucket '{bucket}' has no lifecycle configuration.",
                ex);
        }

        var rules = response.Configuration?.Rules ?? [];
        var missing = new List<string>();
        foreach (var requirement in requirements)
        {
            if (!rules.Any(rule => requirement.Matches(rule)))
                missing.Add(requirement.Description);
        }

        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"S3 bucket '{bucket}' lifecycle configuration is missing: {string.Join(", ", missing)}");
        }
    }
}

internal abstract record S3LifecycleRequirement(string Description)
{
    public abstract bool Matches(LifecycleRule rule);

    public static S3LifecycleRequirement Prefix(string prefix) =>
        new PrefixRequirement(prefix);

    public static S3LifecycleRequirement Tag(string key, string value) =>
        new TagRequirement(key, value);

    private sealed record PrefixRequirement(string Value)
        : S3LifecycleRequirement($"prefix '{Value}'")
    {
        public override bool Matches(LifecycleRule rule) =>
            rule.Status == LifecycleRuleStatus.Enabled
            && rule.Filter?.LifecycleFilterPredicate is LifecyclePrefixPredicate predicate
            && string.Equals(predicate.Prefix, Value, StringComparison.Ordinal);
    }

    private sealed record TagRequirement(string Key, string Value)
        : S3LifecycleRequirement($"tag '{Key}={Value}'")
    {
        public override bool Matches(LifecycleRule rule) =>
            rule.Status == LifecycleRuleStatus.Enabled
            && rule.Filter?.LifecycleFilterPredicate is LifecycleTagPredicate predicate
            && string.Equals(predicate.Tag?.Key, Key, StringComparison.Ordinal)
            && string.Equals(predicate.Tag?.Value, Value, StringComparison.Ordinal);
    }
}
