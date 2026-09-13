using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

using Enlist.Installer.Detection;

using WixToolset.BootstrapperApplicationApi;

namespace Enlist.Installer.Ba
{
    // THERE IS NO FACTORY HERE, and there was one until the compiler objected to it.
    //
    // WiX 4 loaded a bootstrapper in-process and found it through an assembly-level
    // BootstrapperApplicationFactory attribute. WiX 5 starts it as a process instead, so
    // ManagedBootstrapperApplication.Run is handed the application directly and the factory is never
    // consulted - both types are marked [Obsolete] saying exactly that. Carrying one was harmless and
    // actively misleading: it read like the hosting contract while being ignored.

    /// <summary>
    /// The wizard, and the conversation with the Burn engine underneath it.
    ///
    /// Burn calls this on ITS thread and expects to be answered promptly; WPF insists on owning its
    /// own. The base class has already arranged for that, which is the thing to know before touching
    /// Run(): its OnStartup creates an STA thread named "UIThread" and calls Run() on it. So Run() is
    /// where the window belongs, and every engine callback marshals onto that thread's dispatcher.
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

        /// <summary>
        /// THIS THREAD IS ALREADY THE UI THREAD. Do not start another one.
        ///
        /// The base class's OnStartup does this and then returns:
        ///
        ///     Thread uiThread = new Thread(this.Run);
        ///     uiThread.SetApartmentState(ApartmentState.STA);
        ///     uiThread.Start();
        ///
        /// so Run() arrives on a thread that is already STA and has nothing pumping it. Building the
        /// window here and calling Dispatcher.Run() is the whole pattern.
        ///
        /// It used to start a second STA thread with its own WPF Application on it, on the theory
        /// that this thread could not be made STA - true of Main, not of here. That version never
        /// showed a window and never said why: the engine reported only 0x800700e8, "the pipe is
        /// being closed", because an exception on a thread nobody catches for ends the process, and
        /// the process dying is all the engine can see. Hence the catch at the bottom.
        /// </summary>
        protected override void Run()
        {
            try
            {
                // Before Detect, in every display mode, because a package's DetectCondition is
                // evaluated during Detect and reads whatever the variables say at that moment.
                PublishRuntimeDetection();

                if (!Interactive)
                {
                    // Quiet or embedded: detect, plan, apply, report. The bundle's variables were set
                    // on the command line and there is nobody to ask about them.
                    Trace("silent, detecting");
                    this.engine.Detect();
                    _finished.WaitOne();
                    Trace("silent, quitting " + _result);
                    this.engine.Quit(_result);
                    return;
                }

                // Captured before anything can raise a callback, because every one of them marshals
                // onto it and a callback that arrives first would otherwise find it null.
                _dispatcher = Dispatcher.CurrentDispatcher;
                _wizard = new WizardViewModel(this.engine, _command!, this);

                var window = new WizardWindow { DataContext = _wizard };
                _wizard.CloseRequested += (_, __) => window.Close();

                // Closing the window ends the pump, which ends Run(), which quits the engine. One
                // route out, whether the operator finished, cancelled or closed the window.
                window.Closed += (_, __) =>
                {
                    Trace("window closed");
                    Dispatcher.CurrentDispatcher.InvokeShutdown();
                };

                window.Show();
                Trace("window shown");

                // Detect only once the window exists, so its first results have somewhere to be shown.
                this.engine.Detect();

                Dispatcher.Run();
                Trace("pump ended, quitting " + _result);
                this.engine.Quit(_result);
            }
            catch (Exception ex)
            {
                Program.LogCrash(ex);
                Trace("FAILED: " + ex);

                // Quit rather than let the process die: a returned error code is something the engine
                // can log and show. A broken pipe is not.
                try { this.engine.Quit(unchecked((int)0x80004005)); } catch { }
            }
            finally
            {
                Trace("Run returning");
            }
        }

        /// <summary>
        /// Tells the engine what is actually installed, because for ASP.NET Core the engine cannot
        /// work it out for itself.
        ///
        /// THE PROBLEM. A shared framework records itself as one registry value NAMED for each
        /// version installed, with no "latest" to read. A Burn RegistrySearch can ask whether one
        /// exact name exists and nothing else, so the bundle's own check asks for the exact patch it
        /// ships - and a machine with 10.0.11 fails a check for 10.0.12 and downloads 11 MB it does
        /// not need. The base runtime escaped this only because hostfxr happens to publish a Version
        /// value that Burn can compare; ASP.NET Core has no equivalent.
        ///
        /// THE FIX. RuntimeDetection enumerates the key and compares properly, which is exactly the
        /// thing a bootstrapper can do and a declarative search cannot. It has always been able to -
        /// the Prerequisites page has been showing the right answer while the engine downloaded
        /// anyway - so all that was missing was saying so in a variable the chain can read.
        ///
        /// The RegistrySearch is still there and the condition is an OR, so a bundle built with
        /// -StandardBootstrapper keeps the old exact-patch behaviour rather than losing detection
        /// altogether and installing the runtime every time.
        /// </summary>
        private void PublishRuntimeDetection()
        {
            try
            {
                var minimum = RuntimeDetection.ParseMinimum(Read("DotnetRuntimeMinimum"), new Version(10, 0, 0));
                var aspNet = RuntimeDetection.Check(RuntimeDetection.AspNetCore, minimum);

                this.engine.SetVariableString("AspNetCorePresent", aspNet.Present ? "1" : "0", formatted: false);
                Trace("ASP.NET Core " + minimum + " or newer: " + aspNet);
            }
            catch (Exception ex)
            {
                // Detection failing must not stop an install. Left unset, the variable stays "0" and
                // the chain falls back to the bundle's own search - which over-installs at worst.
                Program.LogCrash(ex);
                Trace("runtime detection failed, falling back to the bundle's own search: " + ex.Message);
            }
        }

        private string Read(string variable)
        {
            try
            {
                return this.engine.ContainsVariable(variable) ? this.engine.GetVariableString(variable) : "";
            }
            catch
            {
                return "";
            }
        }

        /// <summary>
        /// A line in the ENGINE's log, which is the one an operator already has.
        ///
        /// Worth the noise. This bootstrapper is a separate process, so when it goes wrong the engine
        /// can say only 0x800700e8 - the pipe closed - which is true of every possible cause. A
        /// handful of milestones turns that into a position: shown, detected, pump ended, returning.
        /// </summary>
        private void Trace(string message)
        {
            try { this.engine?.Log(LogLevel.Standard, "enList BA: " + message); } catch { }
        }

        // ---- Engine callbacks -----------------------------------------------------------------
        //
        // Each one hands off to the wizard when there is one, and does the minimum when there is not.
        // The base class's events are used rather than overridden methods so the wizard can subscribe
        // to what it needs without this class growing a method per phase.

        protected override void OnShutdown(ShutdownEventArgs args)
        {
            base.OnShutdown(args);
            Trace("OnShutdown, action " + args.Action);
        }

        protected override void OnDestroy(DestroyEventArgs args)
        {
            base.OnDestroy(args);
            Trace("OnDestroy, reload " + args.Reload);
        }

        protected override void OnDetectComplete(DetectCompleteEventArgs args)
        {
            base.OnDetectComplete(args);
            Trace("OnDetectComplete, status " + args.Status);

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

        /// <summary>
        /// The packages are installed; now the part only a bootstrapper can do.
        ///
        /// The MSIs lay down files and create stopped services and nothing else, on purpose. The
        /// schema, the portal's key and the agent's enrollment all happen here - in EVERY display
        /// mode, because a silent install needs its credentials just as much as a watched one, and
        /// the plan for a silent install is rebuilt from the bundle's variables.
        ///
        /// On a background thread: Burn calls this on its own thread and expects it back promptly,
        /// and applying a schema to a cold LocalDB is not prompt. Finish is called when the work is
        /// done rather than when Apply finished, which is what keeps the engine from quitting out
        /// from under a half-configured install.
        /// </summary>
        protected override void OnApplyComplete(ApplyCompleteEventArgs args)
        {
            base.OnApplyComplete(args);

            if (args.Status < 0 || _command!.Action != LaunchAction.Install)
            {
                // A failed apply has nothing to configure, and an uninstall or repair has nothing to
                // mint. Rolling either into the credential steps would be a surprising place to
                // create an API key.
                if (_wizard != null) { OnUi(() => _wizard.OnApplyComplete(args)); }
                Finish(args.Status);
                return;
            }

            _ = Task.Run(async () =>
            {
                IReadOnlyList<PostInstallResult> results;
                try
                {
                    var plan = _wizard?.Plan ?? InstallPlan.FromVariables(name => Read(name));
                    var runner = new PostInstallRunner(
                        plan,
                        progress: message =>
                        {
                            Trace(message);
                            if (_wizard != null) { OnUi(() => _wizard.OnPostInstallProgress(message)); }
                        },
                        log: Trace);

                    results = await runner.RunAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Program.LogCrash(ex);
                    Trace("post-install failed: " + ex.Message);
                    results = new[] { new PostInstallResult(PostInstallStepKind.ApplySchema, false, false, ex.Message) };
                }

                foreach (var result in results)
                {
                    Trace((result.Succeeded ? "ok: " : "FAILED: ") + result.Message);
                }

                // A required step that failed makes the install a failure even though every package
                // installed: a control plane with no schema, or a portal with no key, is not a working
                // install and saying otherwise would be the installer's last word on the subject.
                var failed = results.Any(r => !r.Succeeded && !r.Optional);
                var status = failed ? unchecked((int)0x80004005) : args.Status;

                if (_wizard != null) { OnUi(() => _wizard.OnPostInstallComplete(args, results)); }
                Finish(status);
            });
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
