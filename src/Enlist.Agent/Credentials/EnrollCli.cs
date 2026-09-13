namespace Enlist.Agent.Credentials;

/// <summary>
/// <c>enlist-agent enroll</c> - exchange a join token for this agent's own credential, store it, and
/// exit. Nothing is started, nothing is reconciled, no service is touched.
///
///   enlist-agent enroll --control-plane &lt;url&gt; --join-token &lt;token&gt; [--agent &lt;name&gt;]
///                       [--data &lt;path&gt;] [--service-account &lt;account&gt;]
///
/// WHY THIS EXISTS SEPARATELY from --join-token on a normal start. The installer has to enroll, and
/// the two things it must not do are put the token in the service's binPath - <c>sc qc</c> shows a
/// service's arguments to anyone who can query it - or start the agent to get a credential and then
/// stop it again. Starting the agent means connecting the hub, reading assignments and reconciling
/// this machine, which is a great deal to set in motion as a side effect of storing a file.
///
/// So: the MSI creates the service stopped and without the token, this runs once with the token, and
/// the service starts afterwards with a credential already on disk. --join-token on a normal start
/// stays exactly as it was, for the operator re-enrolling a machine by hand.
///
/// --service-account is the account the service will run as, and it is not optional in practice: the
/// credential file's ACL is closed, and the installer is not the account that will read it. See
/// <see cref="AgentCredentialStore(string, string?)"/>.
///
/// Exit codes: 0 enrolled (or already enrolled and still accepted), 2 anything else. The token is
/// never echoed, not on success and not in an error.
/// </summary>
public static class EnrollCli
{
    public static bool IsVerb(string[] args) => args.Length > 0 && args[0].Equals("enroll", StringComparison.OrdinalIgnoreCase);

    public static async Task<int> RunAsync(string[] args)
    {
        string? controlPlaneUrl = null;
        string? joinToken = null;
        string? agentName = null;
        string? dataRoot = null;
        string? serviceAccount = null;

        for (var i = 1; i < args.Length; i++)
        {
            var flag = args[i];
            if (i + 1 >= args.Length)
            {
                return Usage($"{flag} needs a value.");
            }

            switch (flag)
            {
                case "--control-plane": controlPlaneUrl = args[++i]; break;
                case "--join-token": joinToken = args[++i]; break;
                case "--agent": agentName = args[++i]; break;
                case "--data": dataRoot = args[++i]; break;
                case "--service-account": serviceAccount = args[++i]; break;
                default: return Usage($"unknown option '{flag}'.");
            }
        }

        if (string.IsNullOrWhiteSpace(controlPlaneUrl))
        {
            return Usage("enroll needs --control-plane.");
        }

        if (string.IsNullOrWhiteSpace(joinToken))
        {
            return Usage("enroll needs --join-token.");
        }

        if (!Uri.TryCreate(controlPlaneUrl, UriKind.Absolute, out var baseUri) ||
            (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
        {
            return Usage($"--control-plane must be an absolute http(s) URL, got '{controlPlaneUrl}'.");
        }

        var effectiveName = string.IsNullOrWhiteSpace(agentName) ? Environment.MachineName : agentName!.Trim();
        var effectiveData = string.IsNullOrWhiteSpace(dataRoot)
            ? Path.Combine(AppContext.BaseDirectory, "Data")
            : dataRoot!.Trim();

        try
        {
            var store = new AgentCredentialStore(effectiveData, serviceAccount);
            await AgentEnrollment.ResolveAsync(baseUri, effectiveName, store, joinToken, Console.WriteLine, CancellationToken.None)
                .ConfigureAwait(false);

            return 0;
        }
        catch (AgentStartupException ex)
        {
            // The message names the control plane, the agent and the fix; the token is not in it.
            Console.Error.WriteLine($"enlist-agent enroll failed: {ex.Message}");
            return 2;
        }
        catch (HttpRequestException ex)
        {
            Console.Error.WriteLine(
                $"enlist-agent enroll could not reach the control plane at {baseUri}: {ex.Message} " +
                "The agent service is installed and stopped; enroll this machine once the control plane is reachable.");
            return 2;
        }
        catch (TaskCanceledException)
        {
            Console.Error.WriteLine(
                $"enlist-agent enroll timed out reaching the control plane at {baseUri}. " +
                "The agent service is installed and stopped; enroll this machine once the control plane is reachable.");
            return 2;
        }
    }

    private static int Usage(string error)
    {
        Console.Error.WriteLine(error);
        Console.Error.WriteLine("Usage: enlist-agent enroll --control-plane <url> --join-token <token> [--agent <name>] [--data <path>] [--service-account <account>]");
        return 2;
    }
}
