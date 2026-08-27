namespace Connapse.Core;

/// <summary>One rule from a cloud permission grant, and how to compare a document URI to it.</summary>
/// <param name="Value">The URI, or URI prefix, the grant permits.</param>
/// <param name="IsExact">True when only this exact URI is permitted, false when anything beneath it is.</param>
public readonly record struct GrantMatch(string Value, bool IsExact);

/// <summary>
/// Reads the scope string an S3 access grant reports.
/// </summary>
/// <remarks>
/// Here, beside <see cref="SearchScopes"/>, because the shape produced and the SQL that consumes it
/// have to agree and are written in three files. This repository has been bitten three times by a
/// format decided in one place and parsed in another.
/// <para>
/// AWS documents the same grant in more than one shape. A whole-bucket grant appears as
/// <c>s3://bucket*</c> on one page and <c>s3://bucket/*</c> on another, and a prefix grant appears
/// both with and without a trailing asterisk. All of them have to arrive at one representation, or
/// the filter's behaviour depends on which form the API happened to return.
/// </para>
/// </remarks>
public static class GrantScope
{
    private const string S3Scheme = "s3://";

    /// <param name="grantScope">The <c>GrantScope</c> from an access grant.</param>
    /// <param name="isObjectScope">
    /// True when the grant named a single object — <c>S3PrefixType=Object</c>. Object grants match
    /// by equality, because as a prefix a grant for <c>report.pdf</c> also admits
    /// <c>report.pdf.bak</c>.
    /// </param>
    public static GrantMatch Parse(string grantScope, bool isObjectScope = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(grantScope);

        string trimmed = grantScope.Trim();
        if (!trimmed.StartsWith(S3Scheme, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Not an S3 grant scope: '{grantScope}'.", nameof(grantScope));
        }

        string value = trimmed.TrimEnd('*');
        string body = value[S3Scheme.Length..];

        if (body.Length == 0)
        {
            throw new ArgumentException(
                $"Grant scope names no bucket: '{grantScope}'.", nameof(grantScope));
        }

        if (isObjectScope)
            return new GrantMatch(value, IsExact: true);

        // A bucket-only scope, and the one shape that cannot be taken literally. Without the
        // trailing slash "s3://acme" prefix-matches "s3://acme-secrets/", so the slash is what
        // confines the grant to the bucket that was actually named.
        if (!body.Contains('/'))
            return new GrantMatch(S3Scheme + body + "/", IsExact: false);

        return new GrantMatch(value, IsExact: false);
    }
}
