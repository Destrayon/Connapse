using Connapse.Core;

namespace Connapse.Application.Ports;

/// <summary>
/// A connection's settings as a caller gives them: the config in the JSON shape it is stored in, and the
/// credential when the provider takes one (an SFTP key and its passphrase, or a custom source server's token).
/// </summary>
public sealed record ConnectionSettings(string? ConfigJson, string? PrivateKey = null, string? Passphrase = null, string? Token = null);

/// <summary>Why the rules refused settings: they are wrong, or something they had to ask couldn't answer.</summary>
public enum SettingsRefusal
{
    Invalid,
    Unavailable,
}

/// <summary>
/// The rules' answer: the config and secret to store (a null secret leaves a stored one alone), or what is
/// wrong and with which field.
/// </summary>
/// <param name="Note">Something the caller should be told about what is stored, such as a host that was rewritten.</param>
public sealed record SettingsCheck(string? ConfigJson, string? Secret, string? Note = null)
{
    public string? Error { get; init; }

    public string? Field { get; init; }

    public SettingsRefusal Refusal { get; init; }

    /// <summary>The settings were checked against the live service just now, so that is the connection's health.</summary>
    public bool Verified { get; init; }

    public bool Ok => Error is null;

    public static SettingsCheck Refuse(string error, string? field = null, SettingsRefusal refusal = SettingsRefusal.Invalid) =>
        new(null, null) { Error = error, Field = field, Refusal = refusal };
}

/// <summary>
/// What every provider requires of a connection's settings before they are stored: the Connections form's
/// rules, a GitHub installation the App still has, an S3 bucket's region, a custom source server that answers,
/// and an SFTP host as this deployment can reach it. They need provider lookups that live in Web, which answers
/// this for connections.create and connections.update.
/// </summary>
public interface IConnectionSettingsRules
{
    /// <summary>
    /// Whether <paramref name="settings"/> may be stored for a connection named <paramref name="name"/>, and what
    /// to store. <paramref name="current"/> is the connection being changed, or null for a new one.
    /// </summary>
    Task<SettingsCheck> CheckAsync(
        string name, ConnectionProvider provider, ConnectionSettings settings, Connection? current, CancellationToken ct = default);
}
