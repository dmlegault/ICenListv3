using System;
using System.Collections.Generic;
using System.IO;

namespace Enlist.Installer.Detection
{
    /// <summary>What a post-install step is doing, and what the runner has to hand it.</summary>
    public enum PostInstallStepKind
    {
        /// <summary>Create the control plane database if it is absent and apply migrations.</summary>
        ApplySchema,

        /// <summary>Mint the portal's own Operator key. The runner captures it from stdout.</summary>
        CreatePortalKey,

        /// <summary>Store that key, protected, in the portal's appsettings.json. The runner supplies it.</summary>
        StorePortalKey,

        /// <summary>Exchange the join token for this agent's credential. The runner supplies the token.</summary>
        EnrollAgent,
    }

    /// <summary>
    /// One command the bootstrapper runs after the MSIs are installed.
    ///
    /// NO SECRETS LIVE HERE. Not the join token, not the API key, not a SQL password - and that is
    /// the reason this is a description rather than a command line. A step is exactly the sort of
    /// thing that ends up in a log or a progress message, so anything confidential is appended by the
    /// runner at the moment of launching and never written down. <see cref="NeedsDatabase"/> says
    /// "this one needs the connection string" without being the connection string.
    /// </summary>
    public sealed class PostInstallStep
    {
        public PostInstallStep(
            PostInstallStepKind kind,
            string description,
            string executable,
            IReadOnlyList<string> arguments,
            bool needsDatabase,
            bool optional)
        {
            Kind = kind;
            Description = description;
            Executable = executable;
            Arguments = arguments;
            NeedsDatabase = needsDatabase;
            Optional = optional;
        }

        public PostInstallStepKind Kind { get; }

        /// <summary>Shown while it runs, in the operator's words rather than the verb's.</summary>
        public string Description { get; }

        public string Executable { get; }

        /// <summary>Safe to log, every one of them. See the class remarks.</summary>
        public IReadOnlyList<string> Arguments { get; }

        /// <summary>The runner puts the connection string in the child's environment, never on its command line.</summary>
        public bool NeedsDatabase { get; }

        /// <summary>
        /// Whether failing it fails the install.
        ///
        /// Enrollment is optional and the rest are not, which is not a judgement about importance but
        /// about what a failure MEANS. A schema that will not apply, or a key that cannot be minted,
        /// means the thing being installed cannot work. Enrollment reaching no control plane usually
        /// means the control plane is not running yet - on a single-box install it certainly is not,
        /// because every service here is created stopped - and the answer is to enroll later, not to
        /// roll back a perfectly good install.
        /// </summary>
        public bool Optional { get; }
    }

    /// <summary>
    /// The work that only a bootstrapper can do: everything the MSIs deliberately refuse to
    /// (Installer-UI-Design section 2, "the MSIs stay dumb") and section 8's two credentials.
    ///
    /// Ordered, and the order is a dependency chain rather than a preference: there is no key to
    /// store until one is minted, and nothing to mint it in until the schema exists.
    /// </summary>
    public static class PostInstall
    {
        public const string PortalKeyName = "portal";

        /// <summary>
        /// The steps for this plan, in order. Empty is a perfectly ordinary answer - an agent-only
        /// install with no join token has nothing to do here.
        /// </summary>
        public static IReadOnlyList<PostInstallStep> Steps(InstallPlan plan)
        {
            if (plan == null)
            {
                throw new ArgumentNullException(nameof(plan));
            }

            var steps = new List<PostInstallStep>();

            if (plan.InstallsControlPlane)
            {
                steps.Add(new PostInstallStep(
                    PostInstallStepKind.ApplySchema,
                    "Preparing the control plane database",
                    Path.Combine(plan.ResolvedControlPlaneDir, "Enlist.ControlPlane.exe"),
                    new[] { "apply-schema" },
                    needsDatabase: true,
                    optional: false));
            }

            // Both, deliberately. create-api-key writes to the database directly, so the portal's key
            // can only be minted where the control plane's database is reachable. A portal installed
            // on its own - a split tier - gets its key from an Operator on the control plane host, and
            // says so on the Finish page rather than silently starting without one.
            if (plan.InstallsControlPlane && plan.InstallsPortal)
            {
                steps.Add(new PostInstallStep(
                    PostInstallStepKind.CreatePortalKey,
                    "Creating the portal's key",
                    Path.Combine(plan.ResolvedControlPlaneDir, "Enlist.ControlPlane.exe"),
                    new[] { "create-api-key", "--name", PortalKeyName, "--role", "Operator", "--expires", "never" },
                    needsDatabase: true,
                    optional: false));

                steps.Add(new PostInstallStep(
                    PostInstallStepKind.StorePortalKey,
                    "Storing the portal's key",
                    Path.Combine(plan.ResolvedPortalDir, "Enlist.Portal.exe"),
                    // "protect" only. The key itself is appended by the runner; --store and the
                    // service account follow it there, because they have to come after the secret.
                    new[] { "protect" },
                    needsDatabase: false,
                    optional: false));
            }

            // A join token is optional: an agent against a control plane running Off needs none, and
            // an operator may prefer to enroll by hand later.
            if (plan.InstallsAgent && !string.IsNullOrWhiteSpace(plan.AgentJoinToken))
            {
                steps.Add(new PostInstallStep(
                    PostInstallStepKind.EnrollAgent,
                    "Enrolling this agent",
                    Path.Combine(plan.ResolvedAgentDir, "enlist-agent.exe"),
                    new[]
                    {
                        "enroll",
                        "--control-plane", plan.AgentControlPlaneUrl,
                        "--agent", plan.AgentName,
                        "--data", plan.ResolvedAgentDataDir,
                        "--service-account", plan.AgentAccount,
                    },
                    needsDatabase: false,
                    optional: true));
            }

            return steps;
        }

        /// <summary>
        /// The key out of <c>create-api-key</c>'s output, which prints it on its own line as
        /// "  key: enlk_...".
        ///
        /// Matched on the prefix rather than the line, because the surrounding words are for a person
        /// and may reasonably be reworded; enlk_ is the credential format and is not free to change
        /// (PortalCredential refuses anything else). Returns null rather than guessing - a step that
        /// produced no key has failed, and inventing one would move the failure somewhere worse.
        /// </summary>
        public static string? ParseApiKey(string? output)
        {
            if (string.IsNullOrWhiteSpace(output))
            {
                return null;
            }

            foreach (var line in output!.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var index = line.IndexOf("enlk_", StringComparison.Ordinal);
                if (index < 0)
                {
                    continue;
                }

                var key = line.Substring(index).Trim();

                // Nothing follows the key on that line today; stopping at the first space keeps that
                // from mattering if anything ever does.
                var space = key.IndexOf(' ');
                if (space > 0)
                {
                    key = key.Substring(0, space);
                }

                return key.Length > "enlk_".Length ? key : null;
            }

            return null;
        }
    }
}
