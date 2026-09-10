# Aware (Gridgistics) — architecture notes

Reference notes taken from `C:\Users\rober\source\repos\Aware`, c. 2009, ~256,000 lines of C# across
102 projects targeting .NET 3.5 / CLR 2.0. Recorded because several problems enList is solving now
were solved there first, and the shape of those solutions is worth having written down rather than
rediscovered.

This is a description of what the code does, not an endorsement of porting it. Much of it depends on
.NET Framework facilities that no longer exist.

---

## 1. Process hosting: the native CLR host

`Gridgistics.Aware.CLRHost` is a C++ console executable (~2,500 lines including helpers) that hosts
the CLR itself rather than being a managed program.

```
AwareCLRHost.cpp
  CoInitializeEx / CoInitializeSecurity     COM security from the process token
  CorBindToRuntimeEx("v2.0.50727", "svr",   explicit CLR version, server GC,
      STARTUP_SERVER_GC |                   multi-domain-host loader optimisation
      STARTUP_LOADER_OPTIMIZATION_MULTI_DOMAIN_HOST)
  SetHostControl(HostControl)               only IHostPolicyManager is supplied
  ICLRPolicyManager                         failure actions and timeouts (below)
  ICLRHostProtectionManager                 blocks eSelfAffectingProcessMgmt | eUI | eMayLeakOnAbort
  Start()
  IAssemblyCache::QueryAssemblyInfo         locate the managed shim IN THE GAC by strong name
  ExecuteInDefaultAppDomain(shim, "LocalCLRManager", "EntryPoint", commandLine)
```

### Why this matters

**The host contributes no managed dependency closure.** Exactly one managed assembly enters the
process from the host side — `Gridgistics.Aware.CLRHosting`, a ~250-line shim with no dependencies
beyond `System`, loaded from the GAC by strong name. There is no logging framework, no scheduler, no
web stack belonging to the host. A hosted application therefore has nothing to conflict *with*.

This is the point that a managed launcher cannot fully replicate, and it is the direct answer to
"why not just make AppInstanceLegacy.exe smaller": you can get close, but a managed exe always brings
at least its own resolution context and whatever its entry assembly references.

**Assembly binding is NOT overridden natively.** `HostControl`'s constructor leaves
`m_pHostAssemblyManager` NULL; `GetHostManager` only ever returns the policy manager. So the conflict
elimination comes purely from the host having no closure — not from `IHostAssemblyStore` or custom
probing. Worth knowing, because the hosting API's assembly-binding surface is the part usually
assumed to be doing the work.

**What genuinely requires C++** is the reliability policy, none of which is reachable from managed
code:

| Setting | Value |
|---|---|
| `SetActionOnFailure(FAIL_NonCriticalResource)` | `eThrowException` |
| `SetActionOnFailure(FAIL_CriticalResource)` | `eUnloadAppDomain` |
| `SetActionOnFailure(FAIL_OrphanedLock)` | `eUnloadAppDomain` |
| `SetActionOnFailure(FAIL_FatalRuntime)` | `eDisableRuntime` |
| `SetUnhandledExceptionPolicy` | `eHostDeterminedPolicy` — a plugin's unhandled exception does not kill the process |
| `SetTimeoutAndAction(OPR_ThreadAbort, 10s)` | `eRudeAbortThread` |
| `SetTimeoutAndAction(OPR_AppDomainUnload, 20s)` | `eRudeUnloadAppDomain` |

This is the same class of facility SQL Server's CLR host uses. Much of it was deprecated or made
inert in CLR 4, and none of it exists in .NET Core+, so the policy layer does not port forward even
if the structural idea does.

### The managed shim

`Gridgistics.Aware.ClrHosting/LocalClrManager.cs` — the whole thing:

```csharp
setupInformation.ApplicationBase   = Path.GetDirectoryName(executable);
setupInformation.ConfigurationFile = Path.GetFileName(executable) + ".config";
newDomain = AppDomain.CreateDomain("RootHostedDomain", null, setupInformation);
newDomain.ExecuteAssembly(executable, null, arguments);
// finally: AppDomain.Unload(newDomain)
```

Command line format is `assembly^<path>;arguments^a~b~c`.

Two things worth noticing:

- The AppDomain here is **not** doing plugin-versus-plugin isolation. It supplies `ApplicationBase`
  and `ConfigurationFile` so the hosted application gets its own probing path and its own binding
  redirects. The conflict elimination already happened upstream, by the host having no closure.
- `ExecuteAssembly` runs the target's own `Main`. There is no cross-domain object graph, no
  `MarshalByRefObject`, no serialization boundary — the parent launches and waits. This sidesteps the
  marshalling burden that is normally the reason to avoid AppDomains.

### Per-service host copies

`Gridgistics.Installers.Services/ServiceProjectInstaller.cs` calls
`LocalCLRManager.CreateClrHost(commonFilesPath, assemblyDirectory, exeName)`, which copies
`AwareGenericCLRHost.exe` from `CommonProgramFiles\Gridgistics\Aware\Runtime` into the application's
own directory **renamed to that service's exe name**. Each Windows Service is therefore its own
native host process with a meaningful name in Task Manager and the SCM.

---

## 2. The contract model is attributes, not interfaces

Aware's service contract is declared entirely by attribute, which is the model worth studying for
enList — it is what removes the need for a shared contract assembly.

```csharp
[ManagedService]
[PublishOnTcp(SecurityMode.None)]
[PublishOnNamedPipe(NetNamedPipeSecurityMode.None)]
[ServiceBehavior(InstanceContextMode = InstanceContextMode.PerSession,
                 ConcurrencyMode     = ConcurrencyMode.Multiple)]
[ServiceImplementationDescription(typeof(ISimpleService), "Makes the simple service really simple")]
public partial class SimpleService : ManagedService, ISimpleService { }
```

`Gridgistics.Aware/Services/Publishing/` holds the transport attributes — `PublishOnTcpAttribute`,
`PublishOnMsmqAttribute`, `PublishOnNamedPipeAttribute`, `PublishOnMulticastAttribute`,
`PublishOnWSDualHttpAttribute`, `PublishServiceContractToWebServerAttribute`, plus
`ServiceContractIgnore`. Each derives from an abstract `PublishOnAttribute` that returns a WCF
`Binding` and a URI:

```csharp
public abstract Binding CreateBinding();
public abstract Uri CreateUniqueUri(ContractDescription contract, ConfigurationProfile profile);
public virtual  Boolean CanBeAppliedTo(ContractDescription contract, out String reason);
public virtual  void AttachEndpointBehaviors(ServiceEndpoint endpoint);
```

So *how a service is exposed* is a compile-time declaration on the class, and the host reads it. The
same pattern extends to descriptions (`ServiceContractDescription`, `OperationContractDescription`),
task policy (`[TaskPolicy(MaxExecutionSeconds = 10)]`), execution shape
(`[TaskExecutionProfile(ExecutionType.CpuBound)]`), and environment behaviour
(`EnvironmentBehaviorAttribute`).

**Relevance to enList:** metadata carries values a scan can read without constructing anything. This
is precisely the limitation documented in enList's `ManifestEnricher` — `IAppService.Name` is an
instance property, so a metadata-only scan cannot learn it, which is why the enrichment pipeline
exists at all. An attribute-based contract deletes that whole problem.

---

## 3. Manifests are the deployment unit

`Gridgistics.Aware/Manifests/Manifest.cs` — an XML-serialisable manifest that **carries the files
themselves**, not just a description of them.

```
ManifestType         BusinessProcess | Service | ServiceExtensionCollection |
                     ManagementExtensions | WebApplication | Installer | Generic
ManifestHostingModel InProcess | ExternalProcess |
                     ExternalProcessesWithProcessorAffinity |
                     ExternalProcessesWithOutProcessorAffinity | NotApplicable
```

Notable members:

- `ImageBytes`, `ManifestFileEntries` / `ManifestFileEntry` — actual file content travels with the
  manifest, which is how a service gets deployed to a remote machine.
- `RemoveFileData()`, `RemoveFileDataExcept(...)`, `RemoveFileDataExceptConfiguration()` — the same
  object is used both as a heavyweight deployment payload and as a lightweight descriptor, by
  stripping the bytes.
- `GetSharedLibraryEntries()` / `HasSharedLibraries()` — per-file `SharedLibrary` flag.
- `ToSetup()` returns an `AppDomainSetup` built from `ResolvedBaseDirectory` + `ConfigurationFile`.
  The manifest *is* the AppDomain configuration.
- `ProbingPaths`, `Publisher`, `Version`, `FriendlyName`, `Application`, `TypeName`, `UniqueId`,
  `AutoDeploy`, `DeployedBy`, `DeployedTimeStamp`, `MarkValid()` / `MarkInvalid()`.

Compare to enList's `ApplicationManifest`, which is a pure description — files are deployed
separately by zip upload. Aware folded deployment and description into one artifact so that a manifest
could be pushed to a machine over WCF.

---

## 4. Distributed hosting and roles

`Gridgistics.Aware.ServiceHosting/Hosts/`:

- `BaseHostService` → `ConsoleHostService` — every Windows Service host derives from this. The
  concrete hosts (`CoreHostService`, `AgentHostService`, `TasksHostService`, `LocalRuntimeService`,
  `ServiceHostService`, `BusinessProcessesHostService`) are nearly empty; all behaviour is in the base.
- On start it: starts a `NetworkMonitor`, sets a `DelayedFileWatcher` on `*.config` for hot config
  reload, connects a `LocalServiceRegistry` and registers it in the current AppDomain, then starts
  `AwareHost` with descriptors read from the `Aware/serviceHost` config section.
- **Role gating:** `ServerRole` is `All | SDK | Core | ServiceHost` (flags). A host only starts its
  services if `(this.ServerRoles & AwareLocalRuntime.ServerRole) != 0`. The same binaries are
  deployed everywhere and the machine's assigned role decides what actually runs.

`DynamicHost` / `DynamicHostProcessManager` implement the hosting models above — spawning external
processes, optionally with processor affinity, tracked by a `hostKey` GUID with a keep-alive timer and
a `maximumMachineUtilization` ceiling.

`Gridgistics.Aware.RegistryHost` provides the service registry and `ClusteringManager`
(`GetAssignedCluster(machineName)`, `UpdateRegistry`, `RegistryChanged`), plus `LoadBalancingIdManager`
and `LocalRegistryTracker`.

Client-side service resolution lives in `Gridgistics.Aware/Services/Local/` and is a small state
machine — `OfflineState`, `ConnectingState`, `ConnectingTimedOutState`, `DirectConnectionState`,
`OnlineState`, `RegistryState`, `ShuttingDownState` — over a `ChannelFactoryCache` with a
`SafeChannelFactory`. Load balancing strategies (`FirstInLine`, `Random`) sit behind
`ILoadBalancingStrategy`.

---

## 5. Distributed work: business processes and task brokering

Two distinct mechanisms.

**Business processes** (`Gridgistics.Aware.BusinessProcesses`, `.BusinessProcessServices` at 15k lines)
are Windows Workflow Foundation workflows. A developer writes a `BusinessProcessHost` subclass with
`Startup`/`Shutdown` events, and optionally a `BusinessProcessScheduledInitializer` that fills in
process parameters on a schedule. Supporting types cover audit, custom events, quality measurements,
search criteria, principals, and related-workflow tracking.

**Task brokering** (`Gridgistics.Aware.TaskBrokering`, `.TaskBrokeringServices`) is the
compute-distribution path. `IDuplexTaskExecutionBroker` is a duplex WCF contract with a callback
(`IsAlive`) so the broker can detect dead executors. Executors register and deregister with an
`ExecutorChangeReason` — `Registration`, `ClusterChanged`, `QueueDepthChanged`,
`PlannedDeregistration`, `UnplannedDeregistration`, `Reinstated`. Scheduling decisions use
`TaskExecutorHeuristics`, keyed per `(ManifestReference, activityName)`, so placement learns from
observed behaviour per deployed manifest.

Task authoring is again attribute-driven:

```csharp
[Serializable]
[TaskPolicy(MaxExecutionSeconds = 10)]
[TaskExecutionProfile(ExecutionType.CpuBound)]   // CpuBound: 1 thread/proc
public sealed class DistributionTask : MonteCarloDistributedTask
{
    protected override Random OnCreateRandomNumberGenerator(int uniqueProcessorId, int totalProcessors)
        => new MultiplicativeLaggedFibonacciGenerator(-1, 10, uniqueProcessorId, totalProcessors);
}
```

`ExecutionType` drives thread allocation — `CpuBound` one thread per processor, `Mixed` two,
`IOBound` three. The SPRNG generator is parameterised by `uniqueProcessorId` / `totalProcessors` so
parallel Monte Carlo streams don't correlate.

---

## 6. Monitoring, statistics, tracing

Three separate layers.

**Live counters** — `Services/Hosting/OperationStatistics.cs`. Per-operation `CurrentlyExecuting`,
`AverageExecutionTime`, execution and sample counts, with a `samplingFrequency` so not every call is
timed. `OperationExecutionContext` wraps a `Stopwatch` and increments/decrements around a call.

**Rolled-up statistics** — `Services/Statistics/`:
`OperationContractExecutionInstance` → `OperationContractHourlySummary` → `OperationContractDailySummary`,
and the same for service contracts. Hourly rows carry an `Hour` field, daily rows a `Date`.

**Monitoring** — `Gridgistics.Aware.Monitoring.Shared` defines `ICentralMonitoringService` (6
operations) and `ILocalMonitoringService`. `LocalMonitoringRule` binds a Windows Event Log
(`LogName`, `SourceName`) to a `MonitoringGroupId` with `TrackErrors` / `TrackWarnings` /
`TrackInformation` flags. `LocalMonitoringManager` and `EventLogMonitor` collect;
`MonitoringEvent` carries `SourceMachine`, `Timestamp`, `Description`, `EventCategory`, `RuleType`
up to the central service. So each machine filters its own event log by rule and forwards matches.

**Instrumentation** — `QualityMeasurementBreachEvent`, `ServiceNotRespondingEvent`,
`WorkflowHostNotRespondingEvent`.

**Runtime tracing** — `Services/RuntimeTracing/` is a per-request distributed trace:
`IRuntimeTracer`, `RuntimeTraceContext`, `RuntimeTraceClientContext`, `RuntimeTraceEntry`,
`RuntimeTraceFilters`, `RuntimeTracingServiceBehavior`, `RuntimeTracingStatus`. Attached as a WCF
service behaviour and filterable, i.e. tracing that can be switched on for a subset of traffic in
production. `Gridgistics.Aware.ServiceTracer` (4.2k lines) is the viewer.

---

## 7. Tooling

Roughly a quarter of the codebase is tooling rather than runtime:

- `Gridgistics.Aware.VisualStudioAddin` (8.7k), `.VisualStudioIntegration` (3.4k),
  `.VisualStudioWizards` (2.2k), `.ToolboxRegistrar` — project templates, wizards, designer support.
- `Gridgistics.Aware.ManifestBuilder` + `.Shared` (8.6k) — building deployment manifests.
- `Gridgistics.Aware.Deployer.Shared` (6.1k) — a deployment wizard with in-place editing of the
  target's `app.config` (`AppSettingsSectionEditor`, `ConnectionStringsSectionEditor`,
  `ConfigurationProfileModifier`), so configuration is edited at deploy time rather than baked in.
- `Gridgistics.Aware.EnterpriseManager` (8.3k) + `.Views` (34k) — the management console.
- `Gridgistics.Aware.NetworkDoctor` (3.5k) — network diagnostics.
- `Gridgistics.Aware.ServiceAnalyzer`, `.RemoteMachineInstaller`, `.MsBuildTasks`,
  `Gridgistics.Installers.*` (Services, Firewall, Logging, VisualStudio, Replacements).

---

## 8. What is relevant to enList

Directly transferable:

1. **A host with no managed closure removes host-versus-plugin conflicts entirely.** This is the
   idea worth taking. It does not require C++ for the core benefit — a managed launcher with zero
   package references, creating an AppDomain whose `ApplicationBase` is the plugin folder, gets most
   of the way. The native host is what adds the failure-policy layer on top.
2. **Attributes instead of a shared contract interface.** Removes the shared assembly, and removes
   the need for enList's whole enrichment pipeline, because names and schedules become readable from
   metadata.
3. **A shared contract that must exist can be strong-named into the GAC** rather than copied into
   every plugin folder. Available on .NET Framework, and it is how Aware kept its shim out of the
   application's probing path.
4. **`ExecuteAssembly` avoids AppDomain marshalling** — launch the target's own entry point rather
   than holding cross-domain object references.
5. **Role-based gating** (`ServerRole`) — deploy everything everywhere, let the machine's role decide
   what starts. Relevant if enList grows past one server.
6. **Filterable runtime tracing as a service behaviour** — closer to what enList now does with
   OpenTelemetry, but the "switch it on for a subset in production" framing is the useful part.

Not transferable:

- The `ICLRPolicyManager` reliability layer — deprecated in CLR 4, absent in .NET Core+.
- AppDomains at all on the modern side; `AssemblyLoadContext` is the replacement and enList already
  uses it.
- WCF, WF, MSMQ, and the GAC on the modern side.
