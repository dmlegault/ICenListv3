# branding

`enlist.ico` is the iC mark for every enList executable a machine runs — the three services (`Enlist.ControlPlane.exe`, `Enlist.Portal.exe`, `enlist-agent.exe`) and both runners (`enlist-runner.exe` from `Enlist.Runner` and from `Enlist.Runner.Legacy`) — set by `<ApplicationIcon>` in each project. It is what Task Manager shows for a running service, and for the runner process behind each application the agent hosts.

It is the same file as `installer\ba\Enlist.Installer.Ba\media\enlist.ico`, which brands the installers. Two copies, because the products should not reach into the installer's tree for their appearance. They cannot drift unnoticed: `installer\verify.ps1` reads each of these executables out of its built MSI and compares its icon with the installer's copy byte for byte. Change one, change both.

The icon is written into the executable by the .NET SDK, and only a build running on Windows can write it into a Windows executable. That is where the installer is built.
