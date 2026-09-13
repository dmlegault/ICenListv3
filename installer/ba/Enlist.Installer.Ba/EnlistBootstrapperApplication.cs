using System;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

using WixToolset.BootstrapperApplicationApi;

[assembly: BootstrapperApplicationFactory(typeof(Enlist.Installer.Ba.EnlistBootstrapperApplicationFactory))]

namespace Enlist.Installer.Ba
{
    /// <summary>How mbanative finds the wizard. The assembly attribute above is the whole contract.</summary>
    public sealed class EnlistBootstrapperApplicationFactory : BaseBootstrapperApplicationFactory
    {
        protected override IBootstrapperApplication Create(IEngine engine, IBootstrapperCommand command) =>
            new EnlistBootstrapperApplication();
    }

    /// <summary>
    /// The wizard, and the conversation with the Burn engine underneath it.
    ///
    /// Burn calls this on ITS thread and expects to be answered promptly; WPF insists on owning its
    /// own. So the run below starts a dispatcher on the BA thread and marshals between the two, which
    /// is the one genuinely fiddly thing in this file and the reason everything else is kept out of
    /// it.
    ///
    /// SILENT INSTALLS NEVER REACH THE WINDOW. When Burn is running quiet or passive, this plans and
    /// applies straight through, which is what makes the surface section 10 documents work identically
    /// with or without a wizard. The window is one way of filling in variables, not a second way of
    /// installing.
    /// </summary>
    public sealed class EnlistBootstrapperApplication : BootstrapperApplication
    {
        private IBootstrapperCommand? _command;
        private readonly ManualResetEvent _finished = new ManualResetEvent(false);

        private WizardViewModel? _wizard;
        private Dispatcher? _dispatcher;
        private int _result = 0;

        /// <summary>
        /// Parameterless, and the engine is NOT passed in. ManagedBootstrapperApplication.Run connects
        /// to Burn and assigns the base class's engine field; the command arrives separately, on the
        /// Create callback below. Taking an IEngine in a constructor compiles and then hands the
        /// wizard a null engine, which fails much later and nowhere near the cause.
        /// </summary>
        public EnlistBootstrapperApplication()
        {
        }

        protected override void OnCreate(CreateEventArgs args)
        {
            base.OnCreate(args);
            _command = args.Command;
        }

        /// <summary>Whether a person is watching. Everything interactive is conditional on this.</summary>
        private bool Interactive =>
            _command != null && (_command.Display == Display.Full || _command.Display == Display.Passive);

        protected override void Run()
        {
            if (!Interactive)
            {
                // Quiet or embedded: detect, plan, apply, report. The bundle's variables were set on
                // the command line and there is nobody to ask about them.
                this.engine.Detect();
                _finished.WaitOne();
                this.engine.Quit(_result);
                return;
            }

            // WPF needs a single-threaded apartment, and this thread is not one: the host has already
            // initialised COM on it, so it cannot be made one (RPC_E_CHANGED_MODE). The wizard
            // therefore gets a thread of its own, and every engine callback marshals onto it through
            // the dispatcher captured here.
            _wizard = new WizardViewModel(this.engine, _command!, this);

            var ready = new ManualResetEvent(false);
            var ui = new Thread(() =>
            {
                var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                var window = new WizardWindow { DataContext = _wizard };

                _wizard.CloseRequested += (_, __) => application.Dispatcher.BeginInvoke(new Action(() =>
                {
                    window.Close();
                    application.Shutdown();
                }));

                _dispatcher = application.Dispatcher;
                window.Show();
                ready.Set();
                application.Run();
            });

            ui.SetApartmentState(ApartmentState.STA);
            ui.IsBackground = false;
            ui.Start();

            // Detect only once the window exists, so its first results have somewhere to be shown.
            ready.WaitOne();
            this.engine.Detect();

            ui.Join();
            this.engine.Quit(_result);
        }

        // ---- Engine callbacks -----------------------------------------------------------------
        //
        // Each one hands off to the wizard when there is one, and does the minimum when there is not.
        // The base class's events are used rather than overridden methods so the wizard can subscribe
        // to what it needs without this class growing a method per phase.

        protected override void OnDetectComplete(DetectCompleteEventArgs args)
        {
            base.OnDetectComplete(args);

            if (_wizard != null)
            {
                OnUi(() => _wizard.OnDetectComplete(args));
                return;
            }

            // Silent: straight on to planning whatever the command line asked for.
            this.engine.Plan(_command!.Action);
        }

        protected override void OnPlanComplete(PlanCompleteEventArgs args)
        {
            base.OnPlanComplete(args);

            if (args.Status >= 0)
            {
                this.engine.Apply(IntPtr.Zero);
            }
            else
            {
                Finish(args.Status);
            }
        }

        protected override void OnApplyComplete(ApplyCompleteEventArgs args)
        {
            base.OnApplyComplete(args);
            if (_wizard != null) { OnUi(() => _wizard.OnApplyComplete(args)); }
            Finish(args.Status);
        }

        protected override void OnExecuteProgress(ExecuteProgressEventArgs args)
        {
            base.OnExecuteProgress(args);
            var percent = args.OverallPercentage; var package = args.PackageId;
            if (_wizard != null) { OnUi(() => _wizard.OnProgress(percent, package)); }
        }

        protected override void OnCacheAcquireProgress(CacheAcquireProgressEventArgs args)
        {
            base.OnCacheAcquireProgress(args);

            // Downloading a .NET runtime is most of the wait on a machine without one, and a progress
            // bar that only moves during execution looks stuck for the whole of it.
            var percent = args.OverallPercentage; var package = args.PackageOrContainerId;
            if (_wizard != null) { OnUi(() => _wizard.OnDownloadProgress(percent, package)); }
        }

        protected override void OnError(ErrorEventArgs args)
        {
            base.OnError(args);
            var message = args.ErrorMessage;
            if (_wizard != null) { OnUi(() => _wizard.OnError(message)); }

            // Never retry silently. An error the operator cannot see, recovered from in a way they
            // did not choose, is how an install ends up in a state nobody can explain.
            args.Result = Result.Ok;
        }

        /// <summary>Runs an action on the wizard's thread. Every engine callback arrives on the host's.</summary>
        private void OnUi(Action action)
        {
            if (_dispatcher == null)
            {
                return;
            }

            _dispatcher.BeginInvoke(action);
        }

        private void Finish(int status)
        {
            _result = status;
            _finished.Set();
        }

        /// <summary>The wizard asks for this when Install is pressed.</summary>
        internal void BeginPlan(LaunchAction action) => this.engine.Plan(action);
    }
}
