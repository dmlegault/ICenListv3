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
    /// back to the engine, and drives the application it is given until the engine says to quit.
    ///
    /// NO STAThread here, which is the opposite of what a WPF entry point usually wants. The host
    /// initialises COM on this thread itself, and marking it STA makes that fail with
    /// RPC_E_CHANGED_MODE, "cannot change thread mode after it is set" - the bootstrapper exits before
    /// a window can exist. The wizard gets its own STA thread instead, inside the application.
    /// </summary>
    public static class Program
    {
        public static int Main()
        {
            try
            {
                ManagedBootstrapperApplication.Run(new EnlistBootstrapperApplication());
                return 0;
            }
            catch (Exception ex)
            {
                // Nothing above this catches, and a bootstrapper that dies silently leaves an operator
                // with a setup window that never appeared and a log that says only that the process
                // exited. The engine's own log picks this up through the non-zero exit code; the text
                // goes where a person can actually find it.
                try
                {
                    var path = System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "Temp",
                        "enlist-bootstrapper-crash.log");
                    System.IO.File.AppendAllText(path, DateTime.Now.ToString("O") + Environment.NewLine + ex + Environment.NewLine + Environment.NewLine);
                }
                catch
                {
                    // If even that fails there is nothing further to try.
                }

                return 1;
            }
        }
    }
}
