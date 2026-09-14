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

        /// <summary>Let a service account read the private key of the certificate it will serve TLS with.</summary>
        GrantCertificateAccess,

        /// <summary>
        /// Copy the runner image download from beside the setup into the agent's Images folder. No
        /// process: the runner copies the file itself. <see cref="PostInstallStep.Executable"/> is the
        /// archive and the one argument is the destination folder.
        /// </summary>
        StageRunnerImage,
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
        /// The Burn package IDs, which must match the MsiPackage Id attributes in Bundle.wxs.
        /// verify.ps1 asserts they do, because a mismatch would silently skip every step for that
        /// component and report the install as a success.
        /// </summary>
        public static class Packages
        {
            public const string ControlPlane = "ControlPlane";
            public const string Portal = "Portal";
            public const string Agent = "Agent";
        }

        /// <summary>
        /// The steps for a FIRST install of this plan: every package it names is taken to have just
        /// been installed. Convenient for anything that is not a bootstrapper watching a real apply.
        /// The bootstrapper uses the overload that says what actually ran.
        /// </summary>
        public static IReadOnlyList<PostInstallStep> Steps(InstallPlan plan)
        {
            if (plan == null)
            {
                throw new ArgumentNullException(nameof(plan));
            }

            var all = new List<string>();
            if (plan.InstallsControlPlane) { all.Add(Packages.ControlPlane); }
            if (plan.InstallsPortal) { all.Add(Packages.Portal); }
            if (plan.InstallsAgent) { all.Add(Packages.Agent); }

            return Steps(plan, all);
        }

        /// <summary>
        /// The steps for this plan, restricted to the components whose packages Burn actually
        /// installed, modified, repaired or upgraded in THIS apply.
        ///
        /// WHY THE PLAN ALONE IS NOT ENOUGH. Adding an agent to a machine already hosting the control
        /// plane and portal is `setup.exe` run again - an Install action - with the agent switched on.
        /// The plan still says Server, so it still names the control plane and the portal; Burn,
        /// correctly, executes only the Agent package. Computing steps from the plan re-ran every one
        /// of them, and create-api-key --replace revoked the key the already-running portal held. The
        /// portal reads its key once, at startup, so it carried on presenting a revoked key and got 401
        /// from every call until somebody restarted it. Found in the database after a live run:
        ///
        ///     portal  created 13:21:51  revoked 13:21:59   (the key the running portal started with)
        ///     portal  created 13:22:00  live               (minted when the agent was ADDED)
        ///
        /// WHY GATING ON EXECUTION IS SAFE, and not merely convenient: every enList service's
        /// ServiceControl is Stop="both", so executing a package stops that component's service. Anything
        /// re-keyed or re-granted here is therefore picked up when the service next starts. A package
        /// Burn did NOT execute still has its service running exactly as it was, and that is the one
        /// whose credentials must not move underneath it.
        ///
        /// Empty is a perfectly ordinary answer - an agent-only install with no join token has nothing
        /// to do here, and neither does a modify that touched no enList package.
        /// </summary>
        public static IReadOnlyList<PostInstallStep> Steps(InstallPlan plan, IEnumerable<string> executedPackages)
        {
            if (plan == null)
            {
                throw new ArgumentNullException(nameof(plan));
            }

            var executed = new HashSet<string>(executedPackages ?? new string[0], StringComparer.OrdinalIgnoreCase);
            var controlPlaneRan = plan.InstallsControlPlane && executed.Contains(Packages.ControlPlane);
            var portalRan = plan.InstallsPortal && executed.Contains(Packages.Portal);
            var agentRan = plan.InstallsAgent && executed.Contains(Packages.Agent);

            var steps = new List<PostInstallStep>();

            // FIRST, because it is the difference between a service that can serve TLS and one that
            // binds and then fails every handshake. A certificate in LocalMachine\My is readable by
            // anyone; its private key is a separate file whose ACL names only whoever imported it - so
            // the default service account, NETWORK SERVICE, cannot use a certificate an administrator
            // installed. Nothing about the certificate looks wrong when this is missing, which is what
            // makes it worth doing before anything else has a chance to obscure it.
            if (controlPlaneRan && !string.IsNullOrWhiteSpace(plan.ControlPlaneCertificate))
            {
                steps.Add(new PostInstallStep(
                    PostInstallStepKind.GrantCertificateAccess,
                    "Granting the control plane access to its certificate",
                    Path.Combine(plan.ResolvedControlPlaneDir, "Enlist.ControlPlane.exe"),
                    new[]
                    {
                        "grant-certificate-access",
                        "--thumbprint", plan.ControlPlaneCertificate,
                        "--account", plan.ControlPlaneAccount,
                    },
                    needsDatabase: false,
                    optional: false));
            }

            if (portalRan && !string.IsNullOrWhiteSpace(plan.PortalCertificate))
            {
                steps.Add(new PostInstallStep(
                    PostInstallStepKind.GrantCertificateAccess,
                    "Granting the portal access to its certificate",
                    Path.Combine(plan.ResolvedPortalDir, "Enlist.Portal.exe"),
                    new[]
                    {
                        "grant-certificate-access",
                        "--thumbprint", plan.PortalCertificate,
                        "--account", plan.PortalAccount,
                    },
                    needsDatabase: false,
                    optional: false));
            }

            if (controlPlaneRan)
            {
                steps.Add(new PostInstallStep(
                    PostInstallStepKind.ApplySchema,
                    "Preparing the control plane database",
                    Path.Combine(plan.ResolvedControlPlaneDir, "Enlist.ControlPlane.exe"),
                    new[] { "apply-schema" },
                    needsDatabase: true,
                    optional: false));
            }

            // Gated on the PORTAL having just been installed, and on the control plane merely being
            // PRESENT - the two conditions are deliberately different.
            //
            // The portal is what needs a key, so it is the portal's package that decides. If Burn did
            // not execute it, its service is still running with the key it read at startup, and minting
            // a new one with --replace would revoke that key out from under it. That is the defect this
            // gate exists for: adding an agent to a Server box re-keyed a portal nobody had touched.
            //
            // The control plane only has to be installed, not re-installed: create-api-key writes to
            // its database directly, and that database is there whether or not its package ran this
            // time. A portal added to an existing control plane host still gets its key.
            //
            // A portal installed on its own - a split tier - gets its key from an Operator on the
            // control plane host, and says so on the Finish page rather than silently starting without one.
            if (portalRan && plan.InstallsControlPlane)
            {
                steps.Add(new PostInstallStep(
                    PostInstallStepKind.CreatePortalKey,
                    "Creating the portal's key",
                    Path.Combine(plan.ResolvedControlPlaneDir, "Enlist.ControlPlane.exe"),
                    // --replace, because the control plane's database is Permanent and survives an
                    // uninstall. Removing enList and putting it back finds the portal's key already
                    // there, and without this the install fails on its third step with a duplicate
                    // name - which is the right answer for a person at a terminal and the wrong one
                    // for an installer. A key is readable once, so the portal needs a new one anyway.
                    new[] { "create-api-key", "--name", PortalKeyName, "--role", "Operator", "--expires", "never", "--replace" },
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

            // The runner image, when it was handed out beside the setup. Copied into the agent's Images
            // folder, where the agent loads it into its engine as its own account - which is the only way
            // it reaches a LocalSystem agent's wslc store. Before enrollment, so it is in place however
            // soon the service is started. Optional: without it the install is sound and every process
            // application runs; the Finish page says how to add the image later.
            var runnerImage = agentRan ? plan.RunnerImageDownloadBesideSetup() : null;
            if (runnerImage != null)
            {
                steps.Add(new PostInstallStep(
                    PostInstallStepKind.StageRunnerImage,
                    "Copying the runner image for the agent",
                    runnerImage,
                    new[] { plan.ResolvedAgentImagesDir },
                    needsDatabase: false,
                    optional: true));
            }

            // A join token is optional: an agent against a control plane running Off needs none, and
            // an operator may prefer to enroll by hand later.
            if (agentRan && !string.IsNullOrWhiteSpace(plan.AgentJoinToken))
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
