using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;

using Enlist.Installer.Detection;

using WixToolset.BootstrapperApplicationApi;

namespace Enlist.Installer.Ba
{
    /// <summary>One row of the Prerequisites table.</summary>
    public sealed class PrerequisiteRow
    {
        public PrerequisiteRow(string name, string neededBy, bool present, string detail, bool blocking, string whenMissing)
        {
            Name = name;
            NeededBy = neededBy;
            Present = present;
            Detail = detail;
            Blocking = blocking;
            WhenMissing = whenMissing;
        }

        public string Name { get; }

        public string NeededBy { get; }

        public bool Present { get; }

        public string Detail { get; }

        /// <summary>
        /// Whether its absence stops the install. Almost nothing is: the bundle installs the .NET
        /// runtimes itself, a container engine is optional by design, and .NET Framework 4.7.2 only
        /// decides whether net472 applications can run here.
        /// </summary>
        public bool Blocking { get; }

        /// <summary>
        /// What to say when it is absent, stated per row rather than inferred.
        ///
        /// Inferring it read "will be installed" for anything non-blocking, which is true of the
        /// runtimes the bundle chains and quite false of a container engine - nothing here installs
        /// Docker, and promising to is worse than saying nothing.
        /// </summary>
        public string WhenMissing { get; }

        public string Status => Present ? "found" : WhenMissing;
    }

    /// <summary>
    /// The wizard: which page is showing, what the buttons do, and the three things that take long
    /// enough to need a spinner.
    ///
    /// It owns an InstallPlan and asks it every question with a decision in it - which pages exist,
    /// whether Next is allowed and why not, what the bundle variables should be. That split is
    /// deliberate and is what makes the wizard's behaviour testable: everything asserted by
    /// InstallPlanTests is exercised through this class without a window ever existing.
    /// </summary>
    public sealed class WizardViewModel : INotifyPropertyChanged
    {
        private readonly IEngine _engine;
        private readonly IBootstrapperCommand _command;
        private readonly EnlistBootstrapperApplication _ba;

        private IReadOnlyList<WizardPage> _pages = new WizardPage[0];
        private int _index;
        private string _status = "";
        private int _progress;
        private bool _busy;
        private string? _error;
        private bool _complete;
        private string _finishNote = "";

        public WizardViewModel(IEngine engine, IBootstrapperCommand command, EnlistBootstrapperApplication ba)
        {
            _engine = engine;
            _command = command;
            _ba = ba;

            NextCommand = new RelayCommand(_ => GoNext(), _ => CanGoNext);
            BackCommand = new RelayCommand(_ => GoBack(), _ => CanGoBack);
            CancelCommand = new RelayCommand(_ => Close());
            VerifyControlPlaneCommand = new RelayCommand(_ => VerifyControlPlaneAsync(), _ => !Busy);
            TestDatabaseCommand = new RelayCommand(_ => TestDatabaseAsync(), _ => !Busy);
            RecheckCommand = new RelayCommand(_ => DetectPrerequisitesAsync(), _ => !Busy);

            Plan = new InstallPlan();
            Prerequisites = new ObservableCollection<PrerequisiteRow>();
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public event EventHandler? CloseRequested;

        public InstallPlan Plan { get; }

        public ObservableCollection<PrerequisiteRow> Prerequisites { get; }

        public ICommand NextCommand { get; }

        public ICommand BackCommand { get; }

        public ICommand CancelCommand { get; }

        public ICommand VerifyControlPlaneCommand { get; }

        public ICommand TestDatabaseCommand { get; }

        public ICommand RecheckCommand { get; }

        // ---- The editable fields -------------------------------------------------------------
        //
        // WRAPPERS, rather than binding the pages straight at Plan.X, and the reason is not style.
        // InstallPlan is a plain settings object with no change notification, so a binding to
        // Plan.DatabaseServer updates the plan and tells nobody. The Next button still re-enabled,
        // because CommandManager re-queries on its own, but the line beside it saying WHY Next was
        // disabled is an ordinary binding and simply went stale - it would still be demanding a SQL
        // Server seconds after one had been typed in.
        //
        // Going through here means every edit ends in Revalidate(), and the reason tracks the field.

        public InstallType Type
        {
            get => Plan.Type;
            set
            {
                Plan.Type = value;
                RebuildPages();
                Raise(nameof(IsServer));
                Raise(nameof(IsAgentOnly));
                Raise(nameof(IsCustom));
                Revalidate();
            }
        }

        // One bool per choice, because a WPF RadioButton binds IsChecked and has no notion of an
        // enum. Only a true is acted on: unchecking is what the other radio's check already did.
        public bool IsServer { get => Type == InstallType.Server; set { if (value) { Type = InstallType.Server; } } }

        public bool IsAgentOnly { get => Type == InstallType.AgentOnly; set { if (value) { Type = InstallType.AgentOnly; } } }

        public bool IsCustom { get => Type == InstallType.Custom; set { if (value) { Type = InstallType.Custom; } } }

        public bool WantControlPlane
        {
            get => Plan.ControlPlane;
            set { Plan.ControlPlane = value; RebuildPages(); Raise(); Revalidate(); }
        }

        public bool WantPortal
        {
            get => Plan.Portal;
            set { Plan.Portal = value; RebuildPages(); Raise(); Revalidate(); }
        }

        public bool WantAgent
        {
            get => Plan.Agent;
            set { Plan.Agent = value; RebuildPages(); Raise(); Revalidate(); }
        }

        public string ControlPlaneUrls
        {
            get => Plan.ControlPlaneUrls;
            set { Plan.ControlPlaneUrls = value; Raise(); Revalidate(); }
        }

        public string ControlPlaneInstallDir
        {
            get => Plan.ControlPlaneInstallDir;
            set { Plan.ControlPlaneInstallDir = value; Raise(); Revalidate(); }
        }

        public string ControlPlaneDataDir
        {
            get => Plan.ControlPlaneDataDir;
            set { Plan.ControlPlaneDataDir = value; Raise(); Revalidate(); }
        }

        public string ControlPlaneAccount
        {
            get => Plan.ControlPlaneAccount;
            set { Plan.ControlPlaneAccount = value; Raise(); Revalidate(); }
        }

        public string ControlPlanePassword
        {
            get => Plan.ControlPlanePassword;
            set { Plan.ControlPlanePassword = value; Raise(); Revalidate(); }
        }

        /// <summary>
        /// The certificates this machine could serve with, offered rather than typed.
        ///
        /// A thumbprint is forty hex characters that certmgr renders with spaces and an invisible
        /// left-to-right mark. Typing one correctly is possible; PASTING one correctly is the thing
        /// that fails, and it fails as "certificate not found" for a certificate that is right there.
        /// </summary>
        public ObservableCollection<CertificateChoice> Certificates { get; } = new ObservableCollection<CertificateChoice>();

        public CertificateChoice? ControlPlaneCertificate
        {
            get => Certificates.FirstOrDefault(c => c.Thumbprint == Plan.ControlPlaneCertificate);
            set
            {
                Plan.ControlPlaneCertificate = value?.Thumbprint ?? "";
                Raise();
                Raise(nameof(ControlPlaneCertificateProblem));
                Revalidate();
            }
        }

        /// <summary>Said beside the choice rather than discovered at first start: an expired certificate is a service that will not serve.</summary>
        public string ControlPlaneCertificateProblem =>
            ControlPlaneCertificate?.Problem ?? ControlPlaneCertificate?.Caution ?? "";

        public CertificateChoice? PortalCertificate
        {
            get => Certificates.FirstOrDefault(c => c.Thumbprint == Plan.PortalCertificate);
            set
            {
                Plan.PortalCertificate = value?.Thumbprint ?? "";
                Raise();
                Raise(nameof(PortalCertificateProblem));
                Revalidate();
            }
        }

        public string PortalCertificateProblem =>
            PortalCertificate?.Problem ?? PortalCertificate?.Caution ?? "";

        public string DatabaseServer
        {
            get => Plan.DatabaseServer;
            set { Plan.DatabaseServer = value; Raise(); Revalidate(); }
        }

        public string DatabaseName
        {
            get => Plan.DatabaseName;
            set { Plan.DatabaseName = value; Raise(); Revalidate(); }
        }

        public bool DatabaseWindowsAuth
        {
            get => Plan.DatabaseWindowsAuthentication;
            set
            {
                Plan.DatabaseWindowsAuthentication = value;
                Raise();
                Raise(nameof(DatabaseSqlAuth));
                Revalidate();
            }
        }

        public bool DatabaseSqlAuth { get => !DatabaseWindowsAuth; set { if (value) { DatabaseWindowsAuth = false; } } }

        public string DatabaseUser
        {
            get => Plan.DatabaseUser;
            set { Plan.DatabaseUser = value; Raise(); Revalidate(); }
        }

        public string DatabasePassword
        {
            get => Plan.DatabasePassword;
            set { Plan.DatabasePassword = value; Raise(); Revalidate(); }
        }

        public string PortalUrls
        {
            get => Plan.PortalUrls;
            set { Plan.PortalUrls = value; Raise(); Revalidate(); }
        }

        public string PortalControlPlaneUrl
        {
            get => Plan.PortalControlPlaneUrl;
            set { Plan.PortalControlPlaneUrl = value; Raise(); Revalidate(); }
        }

        public string PortalInstallDir
        {
            get => Plan.PortalInstallDir;
            set { Plan.PortalInstallDir = value; Raise(); Revalidate(); }
        }

        public string PortalDataDir
        {
            get => Plan.PortalDataDir;
            set { Plan.PortalDataDir = value; Raise(); Revalidate(); }
        }

        public string PortalAccount
        {
            get => Plan.PortalAccount;
            set { Plan.PortalAccount = value; Raise(); Revalidate(); }
        }

        public string PortalPassword
        {
            get => Plan.PortalPassword;
            set { Plan.PortalPassword = value; Raise(); Revalidate(); }
        }

        public string AgentName
        {
            get => Plan.AgentName;
            set { Plan.AgentName = value; Raise(); Revalidate(); }
        }

        public string AgentTags
        {
            get => Plan.AgentTags;
            set { Plan.AgentTags = value; Raise(); Revalidate(); }
        }

        public string AgentControlPlaneUrl
        {
            get => Plan.AgentControlPlaneUrl;
            set { Plan.AgentControlPlaneUrl = value; Raise(); Revalidate(); }
        }

        public string AgentJoinToken
        {
            get => Plan.AgentJoinToken;
            set { Plan.AgentJoinToken = value; Raise(); Revalidate(); }
        }

        public string AgentEngine
        {
            get => Plan.AgentEngine;
            set { Plan.AgentEngine = value; Raise(); Revalidate(); }
        }

        public string AgentImage
        {
            get => Plan.AgentImage;
            set { Plan.AgentImage = value; Raise(); Revalidate(); }
        }

        /// <summary>
        /// Re-asks the plan whether Next is allowed, why not, and what the Ready page should say.
        /// Every edit ends here.
        /// </summary>
        private void Revalidate()
        {
            Raise(nameof(CanGoNext));
            Raise(nameof(NextBlockedBecause));
            Raise(nameof(Summary));
            Raise(nameof(StepLabel));
            Raise(nameof(AgentEngineNote));
            Raise(nameof(HasAgentEngineNote));
        }

        /// <summary>
        /// On the Agent page, under the engine: where the runner image comes from - found beside this
        /// setup and copied in, or to be put in the agent's Images folder - in the plan's words
        /// (InstallPlan.RunnerImageNote), so they are tested and follow what was typed.
        ///
        /// There used to be a second, red note for wslc under a service account, whose per-account image
        /// store made an operator-loaded image invisible to the agent. The agent now loads the image from
        /// its Images folder itself, as its own account, so that trap is gone and so is the note.
        /// </summary>
        public string AgentEngineNote => Plan.RunnerImageNote(brief: true) ?? "";

        public bool HasAgentEngineNote => AgentEngineNote.Length > 0;

        /// <summary>The same note on the Finish page, set once the install has succeeded - the last thing the operator reads.</summary>
        public string FinishNote
        {
            get => _finishNote;
            private set { _finishNote = value; Raise(); Raise(nameof(HasFinishNote)); }
        }

        public bool HasFinishNote => !string.IsNullOrEmpty(_finishNote);

        /// <summary>Welcome first, then whatever the plan says, then the progress and finish pages.</summary>
        public WizardPage? CurrentPage => _index >= 0 && _index < _pages.Count ? _pages[_index] : (WizardPage?)null;

        /// <summary>
        /// "Step 3 of 6", in the banner. The count is the plan's, so it drops from six steps to three
        /// the moment Agent only is chosen rather than counting pages nobody will see.
        /// </summary>
        public string StepLabel =>
            OnWelcome || Installing || Complete || _pages.Count == 0
                ? ""
                : "Step " + (_index + 1) + " of " + _pages.Count;

        /// <summary>
        /// What Next is about to do, in sentences, on the Ready page. Assembled from the plan rather
        /// than written out, so it cannot describe an install different from the one that will run.
        /// </summary>
        public IReadOnlyList<string> Summary
        {
            get
            {
                var lines = new List<string>();

                if (Plan.InstallsControlPlane)
                {
                    lines.Add("Install the control plane, listening on " + Plan.ControlPlaneUrls + ", as " + Plan.ControlPlaneAccount + ".");
                    lines.Add("Use " + Plan.DatabaseName + " on " + Plan.DatabaseServer +
                              (Plan.DatabaseWindowsAuthentication ? ", with Windows authentication." : ", with the SQL login " + Plan.DatabaseUser + "."));
                }

                if (Plan.InstallsPortal)
                {
                    lines.Add("Install the portal, listening on " + Plan.PortalUrls + ", as " + Plan.PortalAccount + ".");
                }

                if (Plan.InstallsAgent)
                {
                    lines.Add("Install the agent as " + Plan.AgentName + ", reporting to " + Plan.AgentControlPlaneUrl + ".");
                    lines.Add(string.IsNullOrWhiteSpace(Plan.AgentEngine)
                        ? "Run applications as processes - no container engine was chosen."
                        : "Run applications with " + Plan.AgentEngine + ", using " + Plan.AgentImage + ".");
                }

                // Said once, here, because it is the answer to "will this take the machine down".
                lines.Add("Every service is created stopped, and nothing outside its own folders is changed.");
                return lines;
            }
        }

        public bool OnWelcome { get; private set; } = true;

        public bool Installing { get; private set; }

        public bool Complete
        {
            get => _complete;
            private set { _complete = value; Raise(); }
        }

        public string Status
        {
            get => _status;
            private set { _status = value; Raise(); }
        }

        public int Progress
        {
            get => _progress;
            private set { _progress = value; Raise(); }
        }

        public bool Busy
        {
            get => _busy;
            private set { _busy = value; Raise(); Raise(nameof(CanGoNext)); }
        }

        /// <summary>The last thing that went wrong, shown rather than swallowed. Null when nothing has.</summary>
        public string? Error
        {
            get => _error;
            private set { _error = value; Raise(); }
        }

        /// <summary>What the Verify button last said about the control plane.</summary>
        public string ControlPlaneVerdict { get; private set; } = "";

        /// <summary>What the Test button last said about the database.</summary>
        public string DatabaseVerdict { get; private set; } = "";

        public bool CanGoBack => !Installing && !Complete && (_index > 0 || !OnWelcome);

        public bool CanGoNext
        {
            get
            {
                if (Busy || Installing || Complete)
                {
                    return false;
                }

                return OnWelcome || CurrentPage == null || Plan.WhyNextIsBlocked(CurrentPage.Value) == null;
            }
        }

        /// <summary>Why Next is disabled, for the tooltip beside it. Empty when it is not.</summary>
        public string NextBlockedBecause =>
            OnWelcome || CurrentPage == null ? "" : Plan.WhyNextIsBlocked(CurrentPage.Value) ?? "";

        internal void OnDetectComplete(DetectCompleteEventArgs args)
        {
            if (args.Status < 0)
            {
                Error = "This machine could not be examined (0x" + args.Status.ToString("x8") + "). The install cannot continue.";
                return;
            }

            // Whatever the command line already set is what the pages start from, so a half-specified
            // silent command line and the wizard agree rather than quietly diverging.
            Plan.AgentName = Read("AGENT_NAME", Plan.AgentName);
            Plan.AgentControlPlaneUrl = Read("AGENT_CPURL", Plan.AgentControlPlaneUrl);
            Plan.AgentImage = Read("AGENT_IMAGE", Plan.AgentImage);
            Plan.AgentEngine = Read("AGENT_ENGINE", Plan.AgentEngine);

            // Where the setup was started from, which is where the runner image download is looked for.
            Plan.SetupFolder = Read("WixBundleOriginalSourceFolder", Plan.SetupFolder);
            Plan.ControlPlaneUrls = Read("CP_URLS", Plan.ControlPlaneUrls);
            Plan.ControlPlaneAccount = Read("CP_ACCOUNT", Plan.ControlPlaneAccount);
            Plan.PortalUrls = Read("PORTAL_URLS", Plan.PortalUrls);
            Plan.PortalControlPlaneUrl = Read("PORTAL_CPURL", Plan.PortalControlPlaneUrl);
            Plan.PortalAccount = Read("PORTAL_ACCOUNT", Plan.PortalAccount);
            Plan.DatabaseServer = Read("DB_SERVER", Plan.DatabaseServer);
            Plan.DatabaseName = Read("DB_NAME", Plan.DatabaseName);

            // Written straight onto the plan above, which notifies nobody, so every wrapper is
            // announced here. Without this the pages open showing their defaults rather than what
            // the command line asked for - the values would be right and invisible.
            foreach (var field in new[]
            {
                nameof(AgentName), nameof(AgentControlPlaneUrl), nameof(AgentImage), nameof(AgentEngine),
                nameof(ControlPlaneUrls), nameof(ControlPlaneAccount), nameof(PortalUrls),
                nameof(PortalControlPlaneUrl), nameof(PortalAccount), nameof(DatabaseServer), nameof(DatabaseName),
            })
            {
                Raise(field);
            }

            // Read once, here, rather than per page: opening the machine store is not free and both
            // certificate pages offer the same list.
            Certificates.Clear();
            foreach (var certificate in CertificateDetection.Available())
            {
                Certificates.Add(certificate);
            }

            // A thumbprint given on the command line may name a certificate that is in the store but
            // was not enumerable, so the plan's value is left exactly as it arrived; the pages simply
            // show nothing selected. Overwriting it with null here would silently discard a perfectly
            // good silent-install setting the moment a wizard was opened.
            Raise(nameof(ControlPlaneCertificate));
            Raise(nameof(PortalCertificate));

            RebuildPages();
            Revalidate();
        }

        internal void OnProgress(int percentage, string packageId)
        {
            Progress = percentage;
            Status = "Installing " + Friendly(packageId) + "...";
        }

        internal void OnDownloadProgress(int percentage, string packageId)
        {
            Progress = percentage;
            Status = "Downloading " + Friendly(packageId) + "...";
        }

        /// <summary>
        /// Apply finished and FAILED, or this is not an install. The success path does not come
        /// through here: the packages being on disk is not the end of an install, and the wizard stays
        /// on the progress page until the credentials are settled too.
        /// </summary>
        internal void OnApplyComplete(ApplyCompleteEventArgs args)
        {
            Installing = false;
            Complete = true;
            Progress = 100;

            if (args.Status >= 0)
            {
                Status = "enList is installed.";
                return;
            }

            // The log path is the only useful thing to offer here, and Burn always has one.
            Status = "The install did not finish (0x" + args.Status.ToString("x8") + ").";
            Error = "The full log is at " + Read("WixBundleLog", "the path in %TEMP%") + ".";
        }

        /// <summary>Still on the progress page: the schema, the portal's key, this agent's enrollment.</summary>
        internal void OnPostInstallProgress(string message)
        {
            Status = message;

            // The bar has nothing left to measure - Burn's progress ended at Apply - so it goes
            // indeterminate rather than sitting at 100% while work is visibly still happening.
            Busy = true;
        }

        /// <summary>
        /// Everything is done. What the operator is told depends on what actually happened, and a
        /// step that failed is said plainly rather than folded into a general success.
        /// </summary>
        internal void OnPostInstallComplete(ApplyCompleteEventArgs args, IReadOnlyList<PostInstallResult> results)
        {
            Busy = false;
            Installing = false;
            Complete = true;
            Progress = 100;

            var failures = results.Where(r => !r.Succeeded).ToList();
            var blocking = failures.Where(r => !r.Optional).ToList();

            if (blocking.Count > 0)
            {
                Status = "enList is installed, but it is not ready to use.";
                Error = string.Join("  ", blocking.Select(r => r.Message))
                    + "  The full log is at " + Read("WixBundleLog", "the path in %TEMP%") + ".";
                return;
            }

            Status = "enList is installed.";

            // Only on an install that worked: on a failed one it would be one more thing to read in front
            // of the thing that actually needs doing.
            FinishNote = Plan.RunnerImageNote() ?? "";

            if (failures.Count > 0)
            {
                // Optional means the install is sound and one thing did not happen - almost always
                // enrollment against a control plane that is not running yet, which on a single-box
                // install it is not, because every service here is created stopped.
                Error = string.Join("  ", failures.Select(r => r.Message))
                    + "  Everything else is installed; this can be done again later.";
                return;
            }

            // Said once, here, because a page saying "installed" over a set of stopped services is
            // the single most likely thing to be mistaken for a broken install.
            Error = "Every service is installed and stopped. Start them once the listen addresses have certificates.";
        }

        internal void OnError(string message) => Error = message;

        private void RebuildPages()
        {
            _pages = Plan.Pages();

            // The index can now be past the end: choosing Agent only on a plan that had six pages
            // leaves three. Clamping here rather than in CurrentPage keeps Back and the step count
            // agreeing with what is on screen.
            if (_index >= _pages.Count)
            {
                _index = Math.Max(0, _pages.Count - 1);
            }

            Raise(nameof(CurrentPage));
            Raise(nameof(CanGoNext));
            Raise(nameof(NextBlockedBecause));
            Raise(nameof(StepLabel));
            Raise(nameof(Summary));
        }

        private void GoNext()
        {
            if (OnWelcome)
            {
                OnWelcome = false;
                _index = 0;
                Raise(nameof(OnWelcome));
                AfterNavigation();
                return;
            }

            // The install type decides which pages follow it, so the list is rebuilt on the way out
            // of that page rather than once at the start.
            if (CurrentPage == WizardPage.InstallType)
            {
                RebuildPages();
            }

            if (CurrentPage == WizardPage.Prerequisites && Prerequisites.Count == 0)
            {
                DetectPrerequisitesAsync();
            }

            if (CurrentPage == WizardPage.Ready)
            {
                StartInstall();
                return;
            }

            if (_index < _pages.Count - 1)
            {
                _index++;
            }

            AfterNavigation();
        }

        private void GoBack()
        {
            if (_index == 0)
            {
                OnWelcome = true;
                Raise(nameof(OnWelcome));
            }
            else
            {
                _index--;
            }

            AfterNavigation();
        }

        private void AfterNavigation()
        {
            Error = null;
            Raise(nameof(CurrentPage));
            Raise(nameof(CanGoNext));
            Raise(nameof(CanGoBack));
            Raise(nameof(NextBlockedBecause));
            Raise(nameof(StepLabel));
            Raise(nameof(Summary));

            if (CurrentPage == WizardPage.Prerequisites && Prerequisites.Count == 0)
            {
                DetectPrerequisitesAsync();
            }
        }

        private void StartInstall()
        {
            foreach (var variable in Plan.ToBundleVariables())
            {
                _engine.SetVariableString(variable.Key, variable.Value, formatted: false);
            }

            Installing = true;
            Status = "Preparing...";
            Raise(nameof(Installing));
            Raise(nameof(CanGoNext));
            Raise(nameof(CanGoBack));

            _ba.BeginPlan(_command.Action);
        }

        private async void DetectPrerequisitesAsync()
        {
            Busy = true;
            try
            {
                Prerequisites.Clear();

                var required = new Version(10, 0, 0);
                var netCore = RuntimeDetection.Check(RuntimeDetection.NetCore, required);
                var aspNet = RuntimeDetection.Check(RuntimeDetection.AspNetCore, required);
                var framework = RuntimeDetection.CheckNetFramework472();

                // Neither runtime blocks: the bundle installs whichever is missing, which is what a
                // bundle is for.
                Prerequisites.Add(new PrerequisiteRow(".NET 10 Runtime", "all components", netCore.Present, netCore.Detail, blocking: false, whenMissing: "will be installed"));
                Prerequisites.Add(new PrerequisiteRow("ASP.NET Core 10 Runtime", "control plane, portal", aspNet.Present, aspNet.Detail, blocking: false, whenMissing: "will be installed"));

                // Not installed by anything here: it ships with Windows, and its absence only means
                // net472 applications cannot run on this machine.
                Prerequisites.Add(new PrerequisiteRow(".NET Framework 4.7.2", "net472 applications only", framework.Present, framework.Detail, blocking: false, whenMissing: "not present"));

                var engines = await Task.WhenAll(
                    ContainerEngineDetection.ProbeWslcAsync(),
                    ContainerEngineDetection.ProbeDockerAsync()).ConfigureAwait(true);

                foreach (var engine in engines)
                {
                    Prerequisites.Add(new PrerequisiteRow(
                        engine.Engine, "container isolation (optional)", engine.Available, engine.Detail,
                        blocking: false, whenMissing: "not available"));
                }

                // wslc when it answers, then Docker, then none - the engine that is there for an unattended
                // service after a reboot. ContainerEngineDetection.DefaultEngine says why, and why it was
                // briefly the other way round.
                var proposed = ContainerEngineDetection.DefaultEngine(engines);
                if (proposed != null && string.IsNullOrWhiteSpace(Plan.AgentEngine))
                {
                    // Through the wrapper, not Plan.AgentEngine: the Agent page's field is already bound,
                    // and a value written straight onto the plan would be right and invisible.
                    AgentEngine = proposed;
                }
            }
            finally
            {
                Busy = false;
            }
        }

        private async void VerifyControlPlaneAsync()
        {
            var url = CurrentPage == WizardPage.Portal ? Plan.PortalControlPlaneUrl : Plan.AgentControlPlaneUrl;

            Busy = true;
            ControlPlaneVerdict = "Checking...";
            Raise(nameof(ControlPlaneVerdict));
            try
            {
                var result = await Probes.VerifyControlPlaneAsync(url).ConfigureAwait(true);
                ControlPlaneVerdict = result.Message;
            }
            finally
            {
                Busy = false;
                Raise(nameof(ControlPlaneVerdict));
            }
        }

        private async void TestDatabaseAsync()
        {
            Busy = true;
            DatabaseVerdict = "Connecting...";
            Raise(nameof(DatabaseVerdict));
            try
            {
                var connection = Probes.BuildConnectionString(
                    Plan.DatabaseServer, Plan.DatabaseName, Plan.DatabaseWindowsAuthentication, Plan.DatabaseUser, Plan.DatabasePassword);

                var result = await Probes.TestDatabaseAsync(connection).ConfigureAwait(true);
                DatabaseVerdict = result.Message;
            }
            finally
            {
                Busy = false;
                Raise(nameof(DatabaseVerdict));
            }
        }

        private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);

        private string Read(string variable, string fallback)
        {
            try
            {
                return _engine.ContainsVariable(variable) && !string.IsNullOrWhiteSpace(_engine.GetVariableString(variable))
                    ? _engine.GetVariableString(variable)
                    : fallback;
            }
            catch
            {
                // A variable the engine will not hand over is not worth failing the wizard for.
                return fallback;
            }
        }

        private static string Friendly(string packageId)
        {
            switch (packageId)
            {
                case "NetCoreRuntime": return "the .NET runtime";
                case "AspNetCoreRuntime": return "the ASP.NET Core runtime";
                case "ControlPlane": return "the control plane";
                case "Portal": return "the portal";
                case "Agent": return "the agent";
                default: return packageId;
            }
        }

        private void Raise([CallerMemberName] string? property = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }

    /// <summary>The smallest ICommand that does the job, so the wizard needs no MVVM framework in a BA that must stay small.</summary>
    public sealed class RelayCommand : ICommand
    {
        private readonly Action<object?> _execute;
        private readonly Func<object?, bool>? _canExecute;

        public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
        {
            _execute = execute;
            _canExecute = canExecute;
        }

        public event EventHandler? CanExecuteChanged
        {
            add { CommandManager.RequerySuggested += value; }
            remove { CommandManager.RequerySuggested -= value; }
        }

        public bool CanExecute(object? parameter) => _canExecute == null || _canExecute(parameter);

        public void Execute(object? parameter) => _execute(parameter);
    }
}
