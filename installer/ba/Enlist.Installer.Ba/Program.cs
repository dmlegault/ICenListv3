using System;

using WixToolset.BootstrapperApplicationApi;

namespace Enlist.Installer.Ba
{
    /// <summary>
    /// The bootstrapper's own entry point.
    ///
    /// WiX 5 runs a bootstrapper application OUT OF PROCESS: Burn does not load a DLL, it starts this
    /// executable and hands it a pipe name and an API version on the command line. Getting that wrong
    /// is not subtle but it is opaque - pointing the bundle at a DLL produces ERROR_BAD_EXE_FORMAT
    /// from CreateProcessW, with nothing in the log about a bootstrapper at all.
    ///
    /// ManagedBootstrapperApplication.Run does the whole handshake: it reads those arguments, connects
    /// back to the engine over a pipe, and drives the application it is given until the engine quits.
    /// It is one P/Invoke into mbanative.dll, which therefore has to sit beside this executable - see
    /// the csproj, which copies it out of the NuGet package by hand.
    ///
    /// NO STAThread here, which is the opposite of what a WPF entry point usually wants. The host
    /// initialises COM on this thread itself, and marking it STA makes that fail with
    /// RPC_E_CHANGED_MODE, "cannot change thread mode after it is set". The wizard does not need it:
    /// the base class hands Run() an STA thread of its own. See EnlistBootstrapperApplication.Run.
    /// </summary>
    public static class Program
    {
        public static int Main()
        {
            AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash(e.ExceptionObject as Exception);

            try
            {
                ManagedBootstrapperApplication.Run(new EnlistBootstrapperApplication());
                return 0;
            }
            catch (Exception ex)
            {
                LogCrash(ex);
                return 1;
            }
        }

        /// <summary>
        /// Where a failure goes when there is nowhere else for it to go.
        ///
        /// THIS IS NOT BELT AND BRACES. A bootstrapper that dies takes its pipe with it, and all the
        /// engine can say is 0x800700e8, "the pipe is being closed" - which names the symptom and
        /// nothing else. Worse, the catch in Main only covers Main's thread: the wizard runs on the
        /// thread the base class starts for Run(), so an exception there ends the process without
        /// passing through anything above. That is exactly how this failed for a week, with a bundle
        /// that exited 0 and a window that never appeared.
        ///
        /// So every thread that can throw logs through here, and Run() catches for itself.
        /// </summary>
        internal static void LogCrash(Exception? exception)
        {
            if (exception is null)
            {
                return;
            }

            try
            {
                var path = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Temp",
                    "enlist-bootstrapper-crash.log");
                System.IO.File.AppendAllText(
                    path,
                    DateTime.Now.ToString("O") + Environment.NewLine + exception + Environment.NewLine + Environment.NewLine);
            }
            catch
            {
                // If even that fails there is nothing further to try.
            }
        }
    }
}
