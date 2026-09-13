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
        public PrerequisiteRow(string name, string neededBy, bool present, string detail, bool blocking)
        {
            Name = name;
            NeededBy = neededBy;
            Present = present;
            Detail = detail;
            Blocking = blocking;
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

        public string Status => Present ? "found" : (Blocking ? "missing" : "will be installed");
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

        /// <summary>Welcome first, then whatever the plan says, then the progress and finish pages.</summary>
        public WizardPage? CurrentPage => _index >= 0 && _index < _pages.Count ? _pages[_index] : (WizardPage?)null;

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
            Plan.ControlPlaneUrls = Read("CP_URLS", Plan.ControlPlaneUrls);
            Plan.PortalUrls = Read("PORTAL_URLS", Plan.PortalUrls);
            Plan.DatabaseName = Read("DB_NAME", Plan.DatabaseName);

            RebuildPages();
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

        internal void OnError(string message) => Error = message;

        private void RebuildPages()
        {
            _pages = Plan.Pages();
            Raise(nameof(CurrentPage));
            Raise(nameof(CanGoNext));
            Raise(nameof(NextBlockedBecause));
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
                Prerequisites.Add(new PrerequisiteRow(".NET 10 Runtime", "all components", netCore.Present, netCore.Detail, blocking: false));
                Prerequisites.Add(new PrerequisiteRow("ASP.NET Core 10 Runtime", "control plane, portal", aspNet.Present, aspNet.Detail, blocking: false));
                Prerequisites.Add(new PrerequisiteRow(".NET Framework 4.7.2", "net472 applications only", framework.Present, framework.Detail, blocking: false));

                var engines = await Task.WhenAll(
                    ContainerEngineDetection.ProbeWslcAsync(),
                    ContainerEngineDetection.ProbeDockerAsync()).ConfigureAwait(true);

                foreach (var engine in engines)
                {
                    Prerequisites.Add(new PrerequisiteRow(
                        engine.Engine, "container isolation (optional)", engine.Available, engine.Detail, blocking: false));
                }

                // The first available engine becomes the default on the Agent page, which is what
                // section 6.7 specifies. None is a perfectly good answer.
                var usable = engines.FirstOrDefault(e => e.Available);
                if (usable != null && string.IsNullOrWhiteSpace(Plan.AgentEngine))
                {
                    Plan.AgentEngine = usable.Engine;
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
