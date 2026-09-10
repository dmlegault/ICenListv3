namespace Enlist.ControlPlane.Contracts;

/// <summary>
/// An imperative, on-demand action against ONE running target on ONE agent — deliberately outside
/// the declarative policy model (ApplicationPolicyDto/DesiredState/CronOverrides). Stopping a single
/// service or a single job must not require restarting the rest of that application, which is exactly
/// what going through DesiredState/CronOverrides and a reconcile would do. Delivered the same way a
/// policy change is — pushed over the application-policy hub to the one agent group — but carries a
/// payload, unlike the no-payload ApplicationPoliciesChanged nudge, since there's no REST resource
/// for the agent to re-fetch here.
///
/// TargetKind is "Service" or "Job"; Action is "Start" or "Stop" — one verb for both kinds, because
/// from the operator's chair they're the same question ("is this thing allowed to run right now?").
/// For a service, Stop/Start map directly onto the runner's own StopServiceCommand/
/// StartServiceCommand. For a job, Start maps onto JobScheduler.Register on the agent side (the cron
/// schedule is remembered by AgentHost, not resent here), and Stop maps onto Unregister PLUS the
/// runner's CancelJobCommand — stopping a job means both "no more firings" and "unwind the run that
/// is underway", and the schedule alone cannot deliver the second.
/// </summary>
public sealed record AgentCommandRequest(string ApplicationName, string TargetKind, string TargetName, string Action)
{
    public const string Service = "Service";
    public const string Job = "Job";
    public const string Start = "Start";
    public const string Stop = "Stop";
}
