using Enlist.ControlPlane.Contracts;

using MudBlazor;

namespace Enlist.Portal.Services;

/// <summary>
/// Shared enable/disable-policy flow used by both the (Phase 1) ApplicationPolicies page and the agent
/// detail panel — extracted rather than duplicated because the two call sites need identical,
/// non-trivial logic: an explicit-agent rule can just be flipped, but a tag-selector rule fans out to
/// every agent matching those tags (present AND future — see the confirmation text below), so it needs
/// to resolve and show who's actually affected before applying anything.
/// </summary>
public sealed class ApplicationPolicyStateToggler
{
    private readonly ControlPlaneApiClient _api;
    private readonly IDialogService _dialogService;
    private readonly ISnackbar _snackbar;

    public ApplicationPolicyStateToggler(ControlPlaneApiClient api, IDialogService dialogService, ISnackbar snackbar)
    {
        _api = api;
        _dialogService = dialogService;
        _snackbar = snackbar;
    }

    /// <summary>Returns true if the policy rule's DesiredState was actually changed (caller should refresh its own data), false if the user cancelled or the request failed.</summary>
    public async Task<bool> ToggleAsync(ApplicationPolicyDto policy, string newState)
    {
        if (policy.TagSelector is { Count: > 0 } selector)
        {
            List<AgentDto> agents;
            try
            {
                agents = await _api.GetAgentsAsync();
            }
            catch (ApiException ex)
            {
                _snackbar.Add(ex.Message, Severity.Error);
                return false;
            }

            var matchingNames = TagSelectorMatcher.ResolveAgentNames(agents, selector)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var verb = newState == DesiredStates.Running ? "Enable" : "Disable";
            var stateWord = newState == DesiredStates.Running ? "enabled (and running)" : "disabled (and stopped)";
            var currentList = matchingNames.Count == 0 ? "no registered agent right now" : string.Join(", ", matchingNames);

            var confirmed = await _dialogService.ShowMessageBox(
                $"{verb} '{policy.ApplicationName}' everywhere?",
                $"This policy rule targets agents by tag, not one specific agent. It currently matches: {currentList}. " +
                $"Any agent that matches these tags later will also come up {stateWord} automatically - this isn't limited to today's list.",
                yesText: $"{verb} for all matching agents",
                cancelText: "Cancel");

            if (confirmed != true)
            {
                return false;
            }
        }

        try
        {
            await _api.UpdateApplicationPolicyAsync(policy.Id, new UpdateApplicationPolicyRequest(null, newState, null, null));
            _snackbar.Add($"{policy.ApplicationName} {(newState == DesiredStates.Running ? "enabled" : "disabled")}.", Severity.Success);
            return true;
        }
        catch (ApiException ex)
        {
            _snackbar.Add(ex.Message, Severity.Error);
            return false;
        }
    }
}
