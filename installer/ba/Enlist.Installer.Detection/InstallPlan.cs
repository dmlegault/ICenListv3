using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Enlist.Installer.Detection
{
    /// <summary>Which of section 3's install types was chosen.</summary>
    public enum InstallType
    {
        Server,
        AgentOnly,
        Custom,
    }

    /// <summary>
    /// Everything the wizard collects, and the bundle variables it becomes.
    ///
    /// Kept here, beside detection, rather than in the WPF assembly, for the same reason: a
    /// bootstrapper's pages cannot be exercised by a test and this can. What the pages add on top is
    /// binding and navigation; which pages appear, what they must have before Next is allowed, and
    /// what the whole thing turns into on the way to Burn are all decisions, and all here.
    ///
    /// The variable names are section 10's, which are the MSI property names, which are the silent
    /// install's command line. One set of names end to end: the wizard is a way of filling them in,
    /// not a second interface onto the same install.
    /// </summary>
    public sealed class InstallPlan
    {
        public InstallType Type { get; set; } = InstallType.Server;

        public bool ControlPlane { get; set; }

        public bool Portal { get; set; }

        public bool Agent { get; set; }

        // Control plane
        public string ControlPlaneInstallDir { get; set; } = "";
        public string ControlPlaneDataDir { get; set; } = "";
        public string ControlPlaneUrls { get; set; } = "https://+:5293";
        public string ControlPlaneAccount { get; set; } = @"NT AUTHORITY\NetworkService";
        public string ControlPlanePassword { get; set; } = "";

        // Database
        // The same default the MSI falls back to, rather than empty. Both end up installing against
        // LocalDB, but an empty box on the Database page reads as a required field nobody filled in,
        // and blocks Next on a page the operator had no reason to touch.
        public string DatabaseServer { get; set; } = @"(localdb)\MSSQLLocalDB";
        public string DatabaseName { get; set; } = "EnlistControlPlane";
        public bool DatabaseWindowsAuthentication { get; set; } = true;
        public string DatabaseUser { get; set; } = "";
        public string DatabasePassword { get; set; } = "";

        // Portal
        public string PortalInstallDir { get; set; } = "";
        public string PortalDataDir { get; set; } = "";
        public string PortalUrls { get; set; } = "https://+:5231";
        public string PortalControlPlaneUrl { get; set; } = "";
        public string PortalAccount { get; set; } = @"NT AUTHORITY\NetworkService";
        public string PortalPassword { get; set; } = "";

        // Agent
        public string AgentInstallDir { get; set; } = "";
        public string AgentDataDir { get; set; } = "";
        public string AgentName { get; set; } = Environment.MachineName;
        public string AgentTags { get; set; } = "";
        public string AgentControlPlaneUrl { get; set; } = "";
        public string AgentEngine { get; set; } = "";
        public string AgentImage { get; set; } = "";
        public string AgentAccount { get; set; } = "LocalSystem";
        public string AgentPassword { get; set; } = "";

        /// <summary>
        /// Shown once, on the Agent page, and exchanged for the agent's own credential BEFORE the
        /// service is created. Never a bundle variable and never an MSI property: a join token in a
        /// service's binPath is readable by any local user out of the process list, which section 8
        /// calls out by name.
        /// </summary>
        public string AgentJoinToken { get; set; } = "";

        /// <summary>Whether each component is actually being installed, once the type and the switches are combined.</summary>
        /// <summary>
        /// The plan a SILENT install is working to, rebuilt from the bundle's own variables.
        ///
        /// A quiet install has no wizard, so nothing has assembled a plan - but the post-install
        /// steps need one, and "mint the portal's key" has to happen whether or not anybody was
        /// watching. The variables are the same ones section 10 documents on the command line, so a
        /// silent install and a wizard install reach these steps with the same information.
        ///
        /// AGENT_JOINTOKEN is read here and is the only secret among them. It is a bundle variable and
        /// never an MSI property: nothing passes it to a package, so it cannot reach a service's
        /// binPath, and the bundle declares it Hidden so Burn's own log prints it as asterisks.
        /// </summary>
        public static InstallPlan FromVariables(Func<string, string?> read)
        {
            if (read == null)
            {
                throw new ArgumentNullException(nameof(read));
            }

            string Value(string name, string fallback)
            {
                var value = read(name);
                return string.IsNullOrWhiteSpace(value) ? fallback : value!.Trim();
            }

            bool Flag(string name) => Value(name, "0") == "1";

            var plan = new InstallPlan();

            var type = Value("INSTALLTYPE", nameof(InstallType.Server));
            plan.Type = type.Equals(nameof(InstallType.AgentOnly), StringComparison.OrdinalIgnoreCase) ? InstallType.AgentOnly
                : type.Equals(nameof(InstallType.Custom), StringComparison.OrdinalIgnoreCase) ? InstallType.Custom
                : InstallType.Server;

            // ADD rather than override, exactly as each package's InstallCondition does. A Server
            // install with InstallAgent=1 has an agent, and reading these any other way would have the
            // bootstrapper working to a different plan from the one the chain just installed.
            plan.ControlPlane = Flag("InstallControlPlane");
            plan.Portal = Flag("InstallPortal");
            plan.Agent = Flag("InstallAgent");

            plan.ControlPlaneInstallDir = Value("CP_INSTALLDIR", "");
            plan.ControlPlaneUrls = Value("CP_URLS", plan.ControlPlaneUrls);
            plan.ControlPlaneAccount = Value("CP_ACCOUNT", plan.ControlPlaneAccount);

            plan.DatabaseServer = Value("DB_SERVER", plan.DatabaseServer);
            plan.DatabaseName = Value("DB_NAME", plan.DatabaseName);
            plan.DatabaseWindowsAuthentication = !Value("DB_AUTH", "Windows").Equals("Sql", StringComparison.OrdinalIgnoreCase);
            plan.DatabaseUser = Value("DB_USER", "");
            plan.DatabasePassword = Value("DB_PASSWORD", "");

            plan.PortalInstallDir = Value("PORTAL_INSTALLDIR", "");
            plan.PortalUrls = Value("PORTAL_URLS", plan.PortalUrls);
            plan.PortalControlPlaneUrl = Value("PORTAL_CPURL", "");
            plan.PortalAccount = Value("PORTAL_ACCOUNT", plan.PortalAccount);

            plan.AgentInstallDir = Value("AGENT_INSTALLDIR", "");
            plan.AgentDataDir = Value("AGENT_DATADIR", "");
            plan.AgentName = Value("AGENT_NAME", plan.AgentName);
            plan.AgentControlPlaneUrl = Value("AGENT_CPURL", "");
            plan.AgentEngine = Value("AGENT_ENGINE", "");
            plan.AgentImage = Value("AGENT_IMAGE", plan.AgentImage);
            plan.AgentAccount = Value("AGENT_ACCOUNT", plan.AgentAccount);
            plan.AgentJoinToken = Value("AGENT_JOINTOKEN", "");

            return plan;
        }

        /// <summary>
        /// Where each component actually lands, which is what the post-install steps have to launch
        /// from.
        ///
        /// These MIRROR the MSIs rather than being read back from them, and the defaults have to stay
        /// in step with the StandardDirectory elements in ControlPlane.wxs, Portal.wxs and Agent.wxs.
        /// Reading the installed location out of the registry afterwards would be more honest, but the
        /// bootstrapper needs the path in the same breath as it needs the plan, and an empty value
        /// here means exactly what an empty property means there: use the default.
        ///
        /// ProgramFiles resolves to the 64-bit directory because the bundle and the bootstrapper are
        /// both x64, matching ProgramFiles64Folder in the packages.
        /// </summary>
        public string ResolvedControlPlaneDir => Resolve(ControlPlaneInstallDir, ProgramFiles, "enList", "ControlPlane");

        public string ResolvedPortalDir => Resolve(PortalInstallDir, ProgramFiles, "enList", "Portal");

        public string ResolvedAgentDir => Resolve(AgentInstallDir, ProgramFiles, "enList", "Agent");

        public string ResolvedAgentDataDir => Resolve(AgentDataDir, CommonAppData, "enList", "Agent");

        private static string ProgramFiles => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        private static string CommonAppData => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

        private static string Resolve(string chosen, params string[] fallback) =>
            string.IsNullOrWhiteSpace(chosen) ? Path.Combine(fallback) : chosen.Trim();

        public bool InstallsControlPlane => Type == InstallType.Server || ControlPlane;

        public bool InstallsPortal => Type == InstallType.Server || Portal;

        public bool InstallsAgent => Type == InstallType.AgentOnly || Agent;

        /// <summary>Nothing selected. Section 3: "Custom with all three unticked disables Next."</summary>
        public bool InstallsNothing => !InstallsControlPlane && !InstallsPortal && !InstallsAgent;

        /// <summary>
        /// The pages this plan has to walk through, in order, skipping the ones for components that
        /// are not being installed (section 5's flow). Welcome and License are not here: they come
        /// before anything is known and never vary.
        /// </summary>
        public IReadOnlyList<WizardPage> Pages()
        {
            var pages = new List<WizardPage> { WizardPage.InstallType, WizardPage.Prerequisites };

            if (InstallsControlPlane)
            {
                pages.Add(WizardPage.ControlPlane);
                pages.Add(WizardPage.Database);
            }

            if (InstallsPortal)
            {
                pages.Add(WizardPage.Portal);
            }

            if (InstallsAgent)
            {
                pages.Add(WizardPage.Agent);
            }

            pages.Add(WizardPage.Ready);
            return pages;
        }

        /// <summary>
        /// The bundle variables this plan becomes. Only what was actually set: an empty value would
        /// override the bundle's own default with nothing, which for a listen URL or a service account
        /// is the difference between a working service and one that will not start.
        ///
        /// The join token is deliberately absent - see AgentJoinToken.
        /// </summary>
        public IReadOnlyDictionary<string, string> ToBundleVariables()
        {
            var variables = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["INSTALLTYPE"] = Type.ToString(),
                ["InstallControlPlane"] = InstallsControlPlane ? "1" : "0",
                ["InstallPortal"] = InstallsPortal ? "1" : "0",
                ["InstallAgent"] = InstallsAgent ? "1" : "0",
            };

            if (InstallsControlPlane)
            {
                Set(variables, "CP_INSTALLDIR", ControlPlaneInstallDir);
                Set(variables, "CP_DATADIR", ControlPlaneDataDir);
                Set(variables, "CP_URLS", ControlPlaneUrls);
                Set(variables, "CP_ACCOUNT", ControlPlaneAccount);
                Set(variables, "CP_PASSWORD", ControlPlanePassword);
                Set(variables, "DB_SERVER", DatabaseServer);
                Set(variables, "DB_NAME", DatabaseName);
                variables["DB_AUTH"] = DatabaseWindowsAuthentication ? "Windows" : "Sql";
            }

            if (InstallsPortal)
            {
                Set(variables, "PORTAL_INSTALLDIR", PortalInstallDir);
                Set(variables, "PORTAL_DATADIR", PortalDataDir);
                Set(variables, "PORTAL_URLS", PortalUrls);
                Set(variables, "PORTAL_CPURL", PortalControlPlaneUrl);
                Set(variables, "PORTAL_ACCOUNT", PortalAccount);
                Set(variables, "PORTAL_PASSWORD", PortalPassword);
            }

            if (InstallsAgent)
            {
                Set(variables, "AGENT_INSTALLDIR", AgentInstallDir);
                Set(variables, "AGENT_DATADIR", AgentDataDir);
                Set(variables, "AGENT_NAME", AgentName);
                Set(variables, "AGENT_CPURL", AgentControlPlaneUrl);
                Set(variables, "AGENT_ENGINE", AgentEngine);

                // Only alongside an engine. An image with no engine to run it is a value that reads
                // as a choice nobody made.
                if (!string.IsNullOrWhiteSpace(AgentEngine))
                {
                    Set(variables, "AGENT_IMAGE", AgentImage);
                }

                Set(variables, "AGENT_ACCOUNT", AgentAccount);
                Set(variables, "AGENT_PASSWORD", AgentPassword);
            }

            return variables;
        }

        /// <summary>
        /// Why Next is disabled, or null when it is not. One reason at a time, naming the field: a
        /// greyed-out button with no explanation is the worst thing a wizard page can do.
        /// </summary>
        public string? WhyNextIsBlocked(WizardPage page)
        {
            switch (page)
            {
                case WizardPage.InstallType:
                    return InstallsNothing ? "Choose at least one component to install." : null;

                case WizardPage.ControlPlane:
                    if (string.IsNullOrWhiteSpace(ControlPlaneUrls))
                    {
                        return "The control plane needs an address to listen on.";
                    }

                    return NeedsPassword(ControlPlaneAccount, ControlPlanePassword)
                        ? "Enter the password for " + ControlPlaneAccount + "."
                        : null;

                case WizardPage.Database:
                    if (string.IsNullOrWhiteSpace(DatabaseServer))
                    {
                        return "Enter the SQL Server to use.";
                    }

                    if (string.IsNullOrWhiteSpace(DatabaseName))
                    {
                        return "Enter the database name.";
                    }

                    return !DatabaseWindowsAuthentication && string.IsNullOrWhiteSpace(DatabaseUser)
                        ? "Enter the SQL login to connect as."
                        : null;

                case WizardPage.Portal:
                    if (string.IsNullOrWhiteSpace(PortalUrls))
                    {
                        return "The portal needs an address to listen on.";
                    }

                    if (string.IsNullOrWhiteSpace(PortalControlPlaneUrl))
                    {
                        return "The portal needs the control plane's URL.";
                    }

                    return NeedsPassword(PortalAccount, PortalPassword)
                        ? "Enter the password for " + PortalAccount + "."
                        : null;

                case WizardPage.Agent:
                    if (string.IsNullOrWhiteSpace(AgentName))
                    {
                        return "The agent needs a name, unique across the fleet.";
                    }

                    if (string.IsNullOrWhiteSpace(AgentControlPlaneUrl))
                    {
                        return "The agent needs the control plane's URL.";
                    }

                    if (!string.IsNullOrWhiteSpace(AgentEngine) && string.IsNullOrWhiteSpace(AgentImage))
                    {
                        return "Choose a runner image, or set container isolation to None.";
                    }

                    return NeedsPassword(AgentAccount, AgentPassword)
                        ? "Enter the password for " + AgentAccount + "."
                        : null;

                default:
                    return null;
            }
        }

        /// <summary>
        /// Tags, as the Agent page takes them ("env=prod role=web") and as PUT /api/agents/{name}/tags
        /// wants them. Malformed entries are dropped rather than guessed at; the page shows what was
        /// understood, so dropping something silently is visible.
        /// </summary>
        public IReadOnlyDictionary<string, string> ParsedTags()
        {
            var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(AgentTags))
            {
                return tags;
            }

            foreach (var pair in AgentTags.Split(new[] { ' ', '\t', ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var split = pair.IndexOf('=');
                if (split <= 0 || split == pair.Length - 1)
                {
                    continue;
                }

                tags[pair.Substring(0, split).Trim()] = pair.Substring(split + 1).Trim();
            }

            return tags;
        }

        /// <summary>
        /// The built-in service accounts have no password and must not be asked for one; anything else
        /// is a real account that does.
        /// </summary>
        private static bool NeedsPassword(string account, string password)
        {
            if (string.IsNullOrWhiteSpace(account))
            {
                return false;
            }

            var builtIn = new[]
            {
                "LocalSystem",
                @"NT AUTHORITY\NetworkService",
                @"NT AUTHORITY\LocalService",
                @"NT AUTHORITY\SYSTEM",
            };

            // A group-managed service account ends in $ and authenticates without a password too.
            if (account.Trim().EndsWith("$", StringComparison.Ordinal))
            {
                return false;
            }

            return !builtIn.Any(b => string.Equals(b, account.Trim(), StringComparison.OrdinalIgnoreCase))
                && string.IsNullOrEmpty(password);
        }

        private static void Set(IDictionary<string, string> variables, string name, string value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                variables[name] = value.Trim();
            }
        }
    }

    /// <summary>The pages of section 5's flow that can vary. Welcome, License, Installing and Finished do not.</summary>
    public enum WizardPage
    {
        InstallType,
        Prerequisites,
        ControlPlane,
        Database,
        Portal,
        Agent,
        Ready,
    }
}
