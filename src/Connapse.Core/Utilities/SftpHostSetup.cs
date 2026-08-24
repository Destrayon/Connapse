namespace Connapse.Core.Utilities;

/// <summary>The operator's own machine, which decides what the setup command looks like.</summary>
public enum HostPlatform { Windows, MacOS, Linux }

/// <summary>
/// What the setup command reported back. Every field comes from the host, because the host
/// is the only place that knows it — which is the reason for the round trip at all.
/// </summary>
/// <param name="Username">The account the SSH server will authenticate.</param>
/// <param name="HomePath">
/// The home directory as SFTP presents it. On Windows that carries a leading slash before
/// the drive letter, which is the detail that costs people an hour.
/// </param>
/// <param name="Fingerprint">
/// The server's host key, read on the machine itself. Arriving out of band is what turns
/// trust-on-first-use into verified-first-use.
/// </param>
/// <param name="Drives">
/// Filesystem roots the account can reach, as SFTP paths — <c>/C:/</c>, <c>/D:/</c> and so
/// on. Windows only; empty elsewhere, where everything already hangs off <c>/</c>.
/// <para>
/// Reported because the home directory is not where people keep things. A second drive is
/// completely normal, and a flow that can only reach <c>C:</c> would be answering a question
/// nobody asked.
/// </para>
/// </param>
public record SftpHostSetupResult(
    string Username,
    string HomePath,
    string Fingerprint,
    IReadOnlyList<string>? Drives = null)
{
    public IReadOnlyList<string> Drives { get; init; } = Drives ?? [];
}

/// <summary>
/// Builds the one command an operator runs on their own machine, and reads back what it
/// reports.
/// <para>
/// Connapse cannot configure the host — that boundary is the entire reason the container is
/// worth running. What it can do is everything either side of the boundary: generate the key,
/// write the command, and consume the result. The operator copies twice and types nothing.
/// </para>
/// </summary>
public static class SftpHostSetup
{
    /// <summary>
    /// Delimits the block the operator pastes back. Deliberately unmistakable, because the
    /// usual failure is pasting the whole terminal buffer, and that should still work.
    /// </summary>
    public const string BeginMarker = "----- BEGIN CONNAPSE SETUP -----";

    public const string EndMarker = "----- END CONNAPSE SETUP -----";

    /// <summary>
    /// The command for <paramref name="platform"/>, with <paramref name="publicKeyLine"/>
    /// already embedded.
    /// </summary>
    /// <remarks>
    /// Meant to be read before it is run. It enables a network service and installs an
    /// authorized key, so it is shown in full rather than offered as a download or a pipe
    /// from a URL — the operator should be able to see exactly what they are agreeing to at
    /// the moment they agree to it.
    /// </remarks>
    public static string GenerateScript(string publicKeyLine, HostPlatform platform)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publicKeyLine);

        // The key is base64 plus a sanitised comment, so it cannot contain a quote or a
        // newline — SshKeyPairGenerator guarantees that. Asserted rather than assumed,
        // because everything below embeds it into a shell string.
        if (publicKeyLine.Any(c => c is '\n' or '\r' or '\'' or '"'))
            throw new ArgumentException(
                "A public key line cannot contain quotes or newlines.", nameof(publicKeyLine));

        return platform switch
        {
            HostPlatform.Windows => WindowsScript(publicKeyLine),
            HostPlatform.MacOS => UnixScript(publicKeyLine, macOs: true),
            HostPlatform.Linux => UnixScript(publicKeyLine, macOs: false),
            _ => throw new ArgumentOutOfRangeException(nameof(platform)),
        };
    }

    private static string WindowsScript(string publicKeyLine) =>
        $$"""
        # Connapse — allow this computer to be indexed. Run in PowerShell as Administrator.
        # Safe to run more than once.

        # 0. Stop if not elevated, rather than half-succeeding.
        #    Without elevation the steps below fail in ways that leave SSH looking configured
        #    while it is not, and the symptom is an authentication failure with nothing to
        #    explain it.
        if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
                 ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
            Write-Host 'Run this in a PowerShell window opened with "Run as Administrator".' -ForegroundColor Red
            return
        }

        # 1. Make sure an SSH server is installed and running.
        if (-not (Get-Service sshd -ErrorAction SilentlyContinue)) {
            Add-WindowsCapability -Online -Name OpenSSH.Server~~~~0.0.1.0 | Out-Null
        }
        Set-Service -Name sshd -StartupType Automatic
        if ((Get-Service sshd).Status -ne 'Running') { Start-Service sshd }

        if (-not (Get-NetFirewallRule -Name 'OpenSSH-Server-In-TCP' -ErrorAction SilentlyContinue)) {
            New-NetFirewallRule -Name 'OpenSSH-Server-In-TCP' -DisplayName 'OpenSSH Server (sshd)' `
                -Enabled True -Direction Inbound -Protocol TCP -Action Allow -LocalPort 22 | Out-Null
        }

        # 2. Work out which authorized_keys file sshd will actually read.
        #    For an account in the Administrators group it is NOT the one in your profile —
        #    sshd_config redirects those to a machine-wide file with a locked-down ACL. This
        #    is the single most common reason Windows SSH keys silently fail to authenticate.
        #
        #    Asked of the group itself, not of this process's token. UAC filters the
        #    Administrators SID out of an unelevated token, so a token check reports False for
        #    an account that genuinely is a member — and the key would then be written where
        #    sshd will never look.
        $mySid = ([Security.Principal.WindowsIdentity]::GetCurrent()).User.Value
        try {
            $isAdmin = $null -ne (Get-LocalGroupMember -Group 'Administrators' -ErrorAction Stop |
                                  Where-Object { $_.SID.Value -eq $mySid })
        } catch {
            # Domain-joined machines can refuse that cmdlet. Elevated, the token is unfiltered
            # and this is accurate — and step 0 guarantees we are elevated.
            $isAdmin = ([Security.Principal.WindowsIdentity]::GetCurrent()).Groups.Value -contains 'S-1-5-32-544'
        }

        if ($isAdmin) {
            $keyFile = Join-Path $env:ProgramData 'ssh\administrators_authorized_keys'
        } else {
            $keyFile = Join-Path $env:USERPROFILE '.ssh\authorized_keys'
        }

        New-Item -ItemType Directory -Force -Path (Split-Path $keyFile) | Out-Null
        if (-not (Test-Path $keyFile)) { New-Item -ItemType File -Path $keyFile | Out-Null }

        # 3. Add Connapse's key, but only once, and without disturbing any other key present.
        $key = '{{publicKeyLine}}'
        if (-not (Select-String -Path $keyFile -SimpleMatch -Pattern $key -Quiet)) {
            Add-Content -Path $keyFile -Value $key
        }

        if ($isAdmin) {
            # sshd refuses to read this file unless only SYSTEM and Administrators can write it.
            icacls $keyFile /inheritance:r /grant 'SYSTEM:F' 'Administrators:F' | Out-Null
        }

        # 4. Report back what only this machine knows.
        #    Wrapped, because the key is already installed by this point and a failure to read
        #    the fingerprint must not throw away a setup that otherwise worked. An empty
        #    fingerprint costs the operator verified-first-use, not the connection.
        $fingerprint = ''
        try {
            $hostKey = Get-ChildItem (Join-Path $env:ProgramData 'ssh') -Filter 'ssh_host_*_key.pub' -ErrorAction Stop |
                       Sort-Object { $_.Name -notlike '*ed25519*' } | Select-Object -First 1
            $line = & ssh-keygen -lf $hostKey.FullName 2>$null
            if ($LASTEXITCODE -eq 0 -and $line) { $fingerprint = ($line -split ' ')[1] }
        } catch { }

        # Every drive the account can reach, because the home folder is not where most people
        # keep things and a second drive is entirely normal.
        $drives = (Get-PSDrive -PSProvider FileSystem | ForEach-Object { "/$($_.Name):/" }) -join ','

        Write-Host ''
        Write-Host '{{BeginMarker}}'
        Write-Host "user=$env:USERNAME"
        Write-Host "home=/$($env:USERPROFILE -replace '\\','/')"
        Write-Host "drives=$drives"
        Write-Host "fingerprint=$fingerprint"
        Write-Host '{{EndMarker}}'
        Write-Host ''
        Write-Host 'Copy the block above, including both marker lines, back into Connapse.'
        """;

    private static string UnixScript(string publicKeyLine, bool macOs)
    {
        string enable = macOs
            ? """
              # 1. Turn on Remote Login, which is what serves SFTP on macOS.
              sudo systemsetup -setremotelogin on
              """
            : """
              # 1. Make sure an SSH server is installed and running.
              #    The unit is 'ssh' on Debian and Ubuntu, 'sshd' most other places.
              sudo systemctl enable --now sshd 2>/dev/null || sudo systemctl enable --now ssh
              """;

        // $$ so a single brace is literal — the awk program below needs them, and doubling
        // every interpolation is the lesser evil against escaping the shell.
        return $$"""
        # Connapse — allow this computer to be indexed. Safe to run more than once.

        {{enable}}

        # 2. Add Connapse's key, but only once, and without disturbing any other key present.
        mkdir -p ~/.ssh && touch ~/.ssh/authorized_keys
        chmod 700 ~/.ssh && chmod 600 ~/.ssh/authorized_keys

        KEY='{{publicKeyLine}}'
        grep -qxF "$KEY" ~/.ssh/authorized_keys || echo "$KEY" >> ~/.ssh/authorized_keys

        # 3. Report back what only this machine knows.
        HOSTKEY=$(ls /etc/ssh/ssh_host_ed25519_key.pub 2>/dev/null || ls /etc/ssh/ssh_host_rsa_key.pub)
        FINGERPRINT=$(ssh-keygen -lf "$HOSTKEY" | awk '{print $2}')

        printf '\n{{BeginMarker}}\n'
        printf 'user=%s\n' "$(whoami)"
        printf 'home=%s\n' "$HOME"
        printf 'fingerprint=%s\n' "$FINGERPRINT"
        printf '{{EndMarker}}\n\n'
        echo 'Copy the block above, including both marker lines, back into Connapse.'
        """;
    }

    /// <summary>
    /// Reads the block the setup command printed. Returns null when the text does not contain
    /// a usable one.
    /// </summary>
    /// <remarks>
    /// Tolerant on purpose. Operators paste whole terminal buffers, with prompts, wrapping,
    /// and the command echoed above the output — so this finds the markers rather than
    /// expecting the text to begin at them, and ignores anything outside.
    /// </remarks>
    public static SftpHostSetupResult? ParseResult(string? pasted)
    {
        if (string.IsNullOrWhiteSpace(pasted))
            return null;

        int start = pasted.IndexOf(BeginMarker, StringComparison.Ordinal);
        int end = pasted.IndexOf(EndMarker, StringComparison.Ordinal);

        // Without both markers there is nothing to be confident about. Guessing at loose
        // key=value lines would happily accept a paste from somewhere else entirely.
        if (start < 0 || end < 0 || end <= start)
            return null;

        string body = pasted[(start + BeginMarker.Length)..end];

        string? user = null, home = null, fingerprint = null;
        IReadOnlyList<string> drives = [];

        foreach (string raw in body.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string line = raw.Trim();
            int split = line.IndexOf('=');
            if (split <= 0) continue;

            string name = line[..split].Trim();
            string value = line[(split + 1)..].Trim();
            if (value.Length == 0) continue;

            switch (name)
            {
                case "user": user = value; break;
                case "home": home = value; break;
                case "fingerprint": fingerprint = NormaliseFingerprint(value); break;

                // Optional. Older setup commands did not report it, and a Unix host has
                // nothing to report — neither is a reason to reject the block.
                case "drives":
                    drives = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    break;
            }
        }

        return user is null || home is null || fingerprint is null
            ? null
            : new SftpHostSetupResult(user, home, fingerprint, drives);
    }

    /// <summary>
    /// <c>ssh-keygen -l</c> prints <c>SHA256:…</c>, which is already the stored form. An
    /// operator pasting a fingerprint by hand may well omit the prefix, so it is added back
    /// rather than treated as a mismatch later, when the only symptom would be a refused
    /// connection.
    /// </summary>
    private static string NormaliseFingerprint(string value) =>
        value.StartsWith("SHA256:", StringComparison.Ordinal) ? value : $"SHA256:{value}";
}
