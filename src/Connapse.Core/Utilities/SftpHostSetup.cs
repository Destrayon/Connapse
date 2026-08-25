namespace Connapse.Core.Utilities;

/// <summary>The operator's own machine, which decides what the setup command looks like.</summary>
public enum HostPlatform { Windows, MacOS, Linux }

/// <summary>
/// Which machine the setup command is going to be run on.
/// </summary>
/// <remarks>
/// The difference is not cosmetic. Setting up the operator's own machine means bringing an SSH
/// server into existence — installing it, starting it, opening a port. A server they are
/// already connected to over SSH needs none of that, and doing it anyway would demand
/// administrator for work that installing a key into a user's own <c>authorized_keys</c> does
/// not need.
/// </remarks>
public enum SftpSetupTarget
{
    /// <summary>The machine running the browser. May need an SSH server before it can be read.</summary>
    ThisComputer,

    /// <summary>A host the operator already reaches over SSH. Only the key is installed.</summary>
    RemoteServer,
}

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
/// <param name="Addresses">
/// Names and addresses this machine believes it can be reached on — its hostname first, then its
/// non-loopback IPv4 addresses. Filtered on the host, which is the only place that can tell a
/// usable address from a useless one.
/// <para>
/// Suggestions, not answers. Whether Connapse can reach any of them depends on where Connapse
/// runs, and a machine cannot know that about itself.
/// </para>
/// </param>
public record SftpHostSetupResult(
    string Username,
    string HomePath,
    string Fingerprint,
    IReadOnlyList<string>? Drives = null,
    IReadOnlyList<string>? Addresses = null)
{
    public IReadOnlyList<string> Drives { get; init; } = Drives ?? [];

    public IReadOnlyList<string> Addresses { get; init; } = Addresses ?? [];
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
    /// Options prefixed to the <c>authorized_keys</c> entry, so the installed key can do nothing
    /// but SFTP.
    /// </summary>
    /// <remarks>
    /// Without these the entry is a bare public key, which grants everything the account can do:
    /// an interactive shell, port forwarding, agent forwarding. Connapse only ever needs to read
    /// files, and the path confinement it applies is an <i>application</i> rule — it bounds what
    /// Connapse does with the credential, not what the credential can do. Anyone who obtained the
    /// stored private key would not be bound by it at all.
    /// <para>
    /// <c>restrict</c> (OpenSSH 7.2+) disables port forwarding, agent forwarding, X11 and PTY
    /// allocation. <c>command</c> replaces whatever the client asks for, so a session requesting
    /// a shell gets the SFTP server instead. Both are supported by Win32-OpenSSH as well as
    /// portable OpenSSH.
    /// </para>
    /// <para>
    /// This does not make an administrator account safe to use — the key still authenticates as
    /// that account, and an SFTP session for an administrator can read everything on the machine.
    /// It bounds the <i>kind</i> of access, not its reach, which is why the wizard says to prefer
    /// a non-administrator account.
    /// </para>
    /// </remarks>
    public const string KeyRestrictions = """restrict,command="internal-sftp" """;

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
    public static string GenerateScript(
        string publicKeyLine,
        HostPlatform platform,
        SftpSetupTarget target = SftpSetupTarget.ThisComputer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publicKeyLine);

        // The key is base64 plus a sanitised comment, so it cannot contain a quote or a
        // newline — SshKeyPairGenerator guarantees that. Asserted rather than assumed,
        // because everything below embeds it into a shell string.
        if (publicKeyLine.Any(c => c is '\n' or '\r' or '\'' or '"'))
            throw new ArgumentException(
                "A public key line cannot contain quotes or newlines.", nameof(publicKeyLine));

        // The key is installed restricted, never bare. See KeyRestrictions.
        string entry = KeyRestrictions + publicKeyLine;

        return platform switch
        {
            HostPlatform.Windows => WindowsScript(entry, target),
            HostPlatform.MacOS => UnixScript(entry, macOs: true, target),
            HostPlatform.Linux => UnixScript(entry, macOs: false, target),
            _ => throw new ArgumentOutOfRangeException(nameof(platform)),
        };
    }

    private static string WindowsScript(string publicKeyLine, SftpSetupTarget target)
    {
        bool local = target == SftpSetupTarget.ThisComputer;

        // Bringing an SSH server into existence, which is only the local machine's problem. A
        // server reached over SSH is already running one — that is how the operator is about to
        // paste this into it.
        string serve = local
            ? """
              # Stop if not elevated, rather than half-succeeding.
              #    Without elevation the steps below fail in ways that leave SSH looking configured
              #    while it is not, and the symptom is an authentication failure with nothing to
              #    explain it.
              if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
                       ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
                  Write-Host 'Run this in a PowerShell window opened with "Run as Administrator".' -ForegroundColor Red
                  return
              }

              # Make sure an SSH server is installed and running.
              if (-not (Get-Service sshd -ErrorAction SilentlyContinue)) {
                  Add-WindowsCapability -Online -Name OpenSSH.Server~~~~0.0.1.0 | Out-Null
              }
              Set-Service -Name sshd -StartupType Automatic
              if ((Get-Service sshd).Status -ne 'Running') { Start-Service sshd }

              # Reachable from this machine's own networks, and no further. An unscoped rule would
              # also accept port 22 from whatever else is on a hotel or cafe network, and from the
              # internet on any host whose router forwards the port — a much larger change than
              # "let Connapse read my files", made silently while the operator was doing something
              # else. LocalSubnet still covers the Docker and WSL virtual switches, which is how a
              # Connapse container reaches its host.
              if (-not (Get-NetFirewallRule -Name 'Connapse-SFTP-In-TCP' -ErrorAction SilentlyContinue)) {
                  New-NetFirewallRule -Name 'Connapse-SFTP-In-TCP' -DisplayName 'Connapse SFTP (sshd)' `
                      -Enabled True -Direction Inbound -Protocol TCP -Action Allow -LocalPort 22 `
                      -Profile Any -RemoteAddress LocalSubnet | Out-Null
                  Write-Host 'Opened inbound TCP 22 to this machine''s local subnets only.' -ForegroundColor Cyan
              }

              """
            : "";

        // Elevation is demanded up front locally, because installing the server needs it
        // regardless. Remotely only one branch does — the machine-wide file an administrator
        // account's keys go in — so the membership question is asked first and elevation is
        // required only if the answer makes it necessary. Demanding it unconditionally would
        // put an administrator prompt in front of a step that writes one line into the
        // operator's own home directory.
        string elevate = local
            ? """
                  # Domain-joined machines can refuse that cmdlet. Elevated, the token is unfiltered
                  # and this is accurate — and the elevation check above guarantees we are elevated.
                  $isAdmin = ([Security.Principal.WindowsIdentity]::GetCurrent()).Groups.Value -contains 'S-1-5-32-544'
              """
            : """
                  # Unelevated, the token has the Administrators SID filtered out of it, so it
                  # cannot answer this — and guessing "not an administrator" would write the key
                  # where sshd will not read it. Say so instead.
                  if (-not $elevated) {
                      Write-Host 'Could not read the Administrators group. Re-run this in a PowerShell window opened with "Run as Administrator".' -ForegroundColor Red
                      return
                  }
                  $isAdmin = ([Security.Principal.WindowsIdentity]::GetCurrent()).Groups.Value -contains 'S-1-5-32-544'
              """;

        string adminNeedsElevation = local
            ? ""
            : """

              if ($isAdmin -and -not $elevated) {
                  Write-Host 'This account is in the Administrators group, so sshd reads its keys from a machine-wide file that only administrators may write.' -ForegroundColor Red
                  Write-Host 'Re-run this in a PowerShell window opened with "Run as Administrator".' -ForegroundColor Red
                  return
              }

              """;

        return $$"""
        # Connapse — {{(local ? "allow this computer to be indexed. Run in PowerShell as Administrator." : "allow this server to be indexed. Run in PowerShell on the server itself.")}}
        # Safe to run more than once.

        {{serve}}# Work out which authorized_keys file sshd will actually read.
        #    For an account in the Administrators group it is NOT the one in your profile —
        #    sshd_config redirects those to a machine-wide file with a locked-down ACL. This
        #    is the single most common reason Windows SSH keys silently fail to authenticate.
        #
        #    Asked of the group itself, not of this process's token. UAC filters the
        #    Administrators SID out of an unelevated token, so a token check reports False for
        #    an account that genuinely is a member — and the key would then be written where
        #    sshd will never look.
        $mySid = ([Security.Principal.WindowsIdentity]::GetCurrent()).User.Value
        $elevated = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
                    ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
        try {
            $isAdmin = $null -ne (Get-LocalGroupMember -Group 'Administrators' -ErrorAction Stop |
                                  Where-Object { $_.SID.Value -eq $mySid })
        } catch {
        {{elevate}}
        }
        {{adminNeedsElevation}}
        if ($isAdmin) {
            $keyFile = Join-Path $env:ProgramData 'ssh\administrators_authorized_keys'
        } else {
            $keyFile = Join-Path $env:USERPROFILE '.ssh\authorized_keys'
        }

        New-Item -ItemType Directory -Force -Path (Split-Path $keyFile) | Out-Null
        if (-not (Test-Path $keyFile)) { New-Item -ItemType File -Path $keyFile | Out-Null }

        # Add Connapse's key, but only once, and without disturbing any other key present.
        $key = '{{publicKeyLine}}'
        if (-not (Select-String -Path $keyFile -SimpleMatch -Pattern $key -Quiet)) {
            Add-Content -Path $keyFile -Value $key
        }

        if ($isAdmin) {
            # sshd refuses to read this file unless only SYSTEM and Administrators can write it.
            icacls $keyFile /inheritance:r /grant 'SYSTEM:F' 'Administrators:F' | Out-Null
        }

        # Say whether this host still accepts passwords over SSH.
        #    The restrictions on Connapse's key bind that key and nothing else, so they do not
        #    make the host key-only. Reported rather than changed: turning off password
        #    authentication is a decision about every account on the machine, and making it
        #    silently could lock someone out of a login they were relying on.
        $sshdConfig = Join-Path $env:ProgramData 'ssh\sshd_config'
        $passwordAuth = 'unknown'
        if (Test-Path $sshdConfig) {
            $setting = Select-String -Path $sshdConfig -Pattern '^\s*PasswordAuthentication\s+(\S+)' |
                       Select-Object -Last 1
            # OpenSSH defaults this to yes when the directive is absent or commented out.
            $passwordAuth = if ($setting) { $setting.Matches[0].Groups[1].Value } else { 'yes' }
        }

        # Report back what only this machine knows.
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

        # Addresses Connapse might reach this machine on. Filtered here for the same reason the
        # drives are listed here: only this machine can tell a usable address from a useless one.
        # Loopback answers to nobody else, 169.254.x means DHCP failed, and Hyper-V and WSL leave
        # virtual adapters behind that route nowhere from outside.
        $addresses = ''
        try {
            $addresses = (Get-NetIPAddress -AddressFamily IPv4 -ErrorAction Stop |
                          Where-Object { $_.IPAddress -notmatch '^(127\.|169\.254\.)' -and
                                         $_.InterfaceAlias -notmatch 'Loopback|WSL|Hyper-V|vEthernet' } |
                          Select-Object -ExpandProperty IPAddress -Unique) -join ','
        } catch { }

        Write-Host ''
        Write-Host '{{BeginMarker}}'
        Write-Host "user=$env:USERNAME"
        Write-Host "home=/$($env:USERPROFILE -replace '\\','/')"
        Write-Host "drives=$drives"
        Write-Host "fingerprint=$fingerprint"
        Write-Host "host=$env:COMPUTERNAME"
        # As above: the short name resolves for a client that appends a suffix, and not for one
        # that does not.
        $fqdn = ''
        try { $fqdn = [System.Net.Dns]::GetHostEntry($env:COMPUTERNAME).HostName } catch { }
        Write-Host "fqdn=$fqdn"
        Write-Host "addresses=$addresses"
        Write-Host '{{EndMarker}}'
        Write-Host ''
        Write-Host 'Copy the block above, including both marker lines, back into Connapse.'
        if (-not $fingerprint) {
            Write-Host 'The host key fingerprint could not be read, so the first connection will be trusted rather than verified. Everything else is set up.' -ForegroundColor Yellow
        }
        if ($passwordAuth -eq 'yes' -or $passwordAuth -eq 'unknown') {
            Write-Host ''
            Write-Host 'This machine also accepts SSH logins by password, for every account on it.' -ForegroundColor Yellow
            Write-Host "To allow keys only, set 'PasswordAuthentication no' in $sshdConfig and run: Restart-Service sshd" -ForegroundColor Yellow
        }
        """;
    }

    private static string UnixScript(string publicKeyLine, bool macOs, SftpSetupTarget target)
    {
        // Only the local machine may still need its SSH server switched on, and only that step
        // needs sudo. Installing a key into the operator's own ~/.ssh does not, so a remote
        // script asks for no privilege at all.
        string enable = target == SftpSetupTarget.RemoteServer
            ? ""
            : macOs
                ? """
                  # Turn on Remote Login, which is what serves SFTP on macOS.
                  sudo systemsetup -setremotelogin on

                  """
                : """
                  # Make sure an SSH server is installed and running.
                  #    The unit is 'ssh' on Debian and Ubuntu, 'sshd' most other places.
                  sudo systemctl enable --now sshd 2>/dev/null || sudo systemctl enable --now ssh

                  """;

        string headline = target == SftpSetupTarget.RemoteServer
            ? "allow this server to be indexed. Run it on the server itself."
            : "allow this computer to be indexed.";

        // $$ so a single brace is literal — the awk program below needs them, and doubling
        // every interpolation is the lesser evil against escaping the shell.
        return $$"""
        # Connapse — {{headline}} Safe to run more than once.

        {{enable}}# Add Connapse's key, but only once, and without disturbing any other key present.
        mkdir -p ~/.ssh && touch ~/.ssh/authorized_keys
        chmod 700 ~/.ssh && chmod 600 ~/.ssh/authorized_keys

        KEY='{{publicKeyLine}}'
        grep -qxF "$KEY" ~/.ssh/authorized_keys || echo "$KEY" >> ~/.ssh/authorized_keys

        # Say whether this host still accepts passwords over SSH.
        #    The restrictions on Connapse's key bind that key and nothing else, so they do not
        #    make the host key-only. Reported rather than changed: turning off password
        #    authentication is a decision about every account on the machine.
        #    OpenSSH defaults this to yes when the directive is absent or commented out.
        PASSWORD_AUTH=$(awk 'tolower($1)=="passwordauthentication" {v=$2} END {print (v?v:"yes")}'                         /etc/ssh/sshd_config 2>/dev/null || echo unknown)

        # Report back what only this machine knows.
        HOSTKEY=$(ls /etc/ssh/ssh_host_ed25519_key.pub 2>/dev/null || ls /etc/ssh/ssh_host_rsa_key.pub)
        FINGERPRINT=$(ssh-keygen -lf "$HOSTKEY" | awk '{print $2}')

        # Addresses Connapse might reach this machine on. Filtered here rather than in the UI,
        # because this is the only place that can tell a usable address from a useless one:
        # loopback answers only to this machine, 169.254.x means DHCP failed, and 172.17.x is
        # this machine's own Docker bridge. Suggesting any of them would waste the operator's
        # time proving they do not work.
        #
        # 'ip' on Linux, 'ifconfig' on macOS. Chosen on whether the first produced anything
        # rather than on its exit status: a pipeline reports the status of its last command, so
        # 'ip ... | awk | cut || ifconfig' would take the status of cut, which succeeds on empty
        # input, and the fallback would never run. Neither failing is fatal — an empty list
        # costs a suggestion, not the setup.
        ADDRESSES=$(ip -4 -o addr show scope global 2>/dev/null | awk '{print $4}' | cut -d/ -f1)
        [ -z "$ADDRESSES" ] && ADDRESSES=$(hostname -I 2>/dev/null | tr ' ' '\n')
        [ -z "$ADDRESSES" ] && ADDRESSES=$(ifconfig 2>/dev/null | awk '/inet /{print $2}')

        # 172.17 only, not the whole of 172.16/12: that range is a normal private network and a
        # server legitimately living on 172.20.x should not have its real address hidden. Docker's
        # default bridge is the specific thing worth dropping.
        ADDRESSES=$(printf '%s\n' "$ADDRESSES" \
                    | grep -Ev '^(127\.|169\.254\.|172\.17\.)' \
                    | grep -E '^[0-9]' | sort -u | paste -sd, -)

        # The markers go through %s rather than being the format string themselves. They begin
        # with dashes, and printf reads a leading '-' as an option — bash rejected the end
        # marker outright, so the block came back unterminated and would not parse. The begin
        # marker only escaped it because a leading \n put a character in front of the dashes.
        printf '\n%s\n' '{{BeginMarker}}'
        printf 'user=%s\n' "$(whoami)"
        printf 'home=%s\n' "$HOME"
        printf 'fingerprint=%s\n' "$FINGERPRINT"
        # Short name and fully-qualified name both, because they are not interchangeable from
        # somewhere else on the network. A workstation resolves 'divielserver' only because its
        # DNS client appends a connection-specific suffix; a container has no search domain and
        # looks up exactly what it is given, so the short name fails there while the FQDN works.
        printf 'host=%s\n' "$(hostname 2>/dev/null)"
        printf 'fqdn=%s\n' "$(hostname -f 2>/dev/null)"
        printf 'addresses=%s\n' "$ADDRESSES"
        printf '%s\n\n' '{{EndMarker}}'
        echo 'Copy the block above, including both marker lines, back into Connapse.'

        if [ "$PASSWORD_AUTH" != "no" ]; then
            echo ''
            echo 'This machine also accepts SSH logins by password, for every account on it.'
            echo "To allow keys only, set 'PasswordAuthentication no' in /etc/ssh/sshd_config and restart the SSH service."
        fi
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
        string? reportedHost = null;
        string? fqdn = null;
        IReadOnlyList<string> addresses = [];

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

                // Also optional, and for the same reason: an older command did not send these,
                // and a machine whose address lookup found nothing still set itself up correctly.
                case "host":
                    reportedHost = value;
                    break;

                case "fqdn":
                    fqdn = value;
                    break;

                case "addresses":
                    addresses = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    break;
            }
        }

        // The fingerprint is optional, and that is not a weakening — it is what stops the flow
        // dead-ending. Reading it needs elevation and can still fail, by which point sshd is
        // running and the key is installed. Requiring it here meant the block parsed to null,
        // the UI disabled both test and save, and the operator was left with a configured host,
        // an authorized Connapse key, and no way to finish or undo.
        //
        // Blank already has a defined meaning everywhere else: SshHostKeyPolicy reads it as
        // trust-on-first-use. So an absent fingerprint costs the stronger verified-first-use and
        // nothing else, which the panel says plainly rather than silently accepting.
        // Ordered by what is likeliest to work from wherever Connapse runs, which is not the
        // same as what works from the operator's desk. The fully-qualified name comes first: it
        // survives a DHCP lease changing, and unlike the short name it does not depend on the
        // resolver appending a search domain — a container's does not. The short name comes
        // last for exactly that reason, kept because it is the one the operator recognises.
        List<string> ordered = [];
        void Offer(string? candidate)
        {
            if (!string.IsNullOrWhiteSpace(candidate) &&
                !ordered.Contains(candidate, StringComparer.OrdinalIgnoreCase))
            {
                ordered.Add(candidate);
            }
        }

        Offer(fqdn);
        foreach (string address in addresses) Offer(address);
        Offer(reportedHost);

        IReadOnlyList<string> candidates = ordered;

        return user is null || home is null
            ? null
            : new SftpHostSetupResult(user, home, fingerprint ?? string.Empty, drives, candidates);
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
