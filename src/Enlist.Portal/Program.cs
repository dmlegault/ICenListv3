using Enlist.Portal.Components;
using Enlist.Portal.Services;

using Microsoft.Extensions.Hosting.WindowsServices;

using MudBlazor.Services;

// `Enlist.Portal.exe protect <secret>` prints a DPAPI-protected configuration value and exits - how
// the installer (or an operator) stores the portal's control-plane key without leaving it in the
// clear. Handled before the web host exists, the way the control plane's own verbs are.
if (ProtectedSettings.IsVerb(args))
{
    return ProtectedSettings.Run(args);
}

// Hosting-mode neutral: the same binary runs under IIS, as a Windows Service, or from `dotnet run`.
//
// ContentRootPath has to be decided HERE, in the options, not afterwards. WebApplicationBuilder
// resolves the content root during CreateBuilder, from the current working directory — and the SCM
// starts a service with its working directory set to %SystemRoot%\System32. Setting it later is not
// possible either: ConfigureHostBuilder.UseContentRoot throws NotSupportedException, which is why
// UseWindowsService() (which would otherwise do this for us) cannot be used on a web host.
//
// This matters more here than in the control plane, and it is not hypothetical: a wrong content root
// silently breaks MapStaticAssets() below — every asset returns an empty 200 or throws from
// StaticAssetDevelopmentRuntimeHandler, and the page just loads unstyled with no error anywhere. That
// exact failure is already written up in Runbook.md from a `dotnet <dll>` launch; a service installed
// without this line reproduces it identically, and it is not obvious from the browser.
//
// Left null outside a service so IIS and `dotnet run` keep the content root they already resolve
// correctly. IsWindowsService() checks that the parent process is services.exe, so it is false under
// IIS (parent is the WAS-spawned worker) and false on non-Windows.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = WindowsServiceHelpers.IsWindowsService() ? AppContext.BaseDirectory : null,
});

// No-ops unless actually started by the SCM. Also routes logging to the Windows Event Log in that
// case — the only place a service can report the startup throw when ControlPlane:BaseUrl is missing.
builder.Services.AddWindowsService(options => options.ServiceName = "enlist-portal");

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddMudServices();

// Who may use this portal, and as what (Authentication-Design.md section 5): Windows identities, two
// group names. Required unless configuration says Off, and Off only on loopback - checked at startup
// below, with no override.
var authentication = builder.Configuration.GetSection(PortalAuthenticationOptions.SectionName).Get<PortalAuthenticationOptions>() ?? new PortalAuthenticationOptions();
builder.AddPortalAuthentication(authentication);

// The one thing this app talks to — never an agent directly (design doc section 8: "The portal never
// talks to an agent"). Base URL is the same kind of config as Enlist.Agent's --control-plane, just
// read from configuration instead of a command-line flag since this runs under IIS/a web host rather
// than being launched with arguments. The key beside it is the portal's own Operator credential to
// that control plane (PortalCredential); the person behind each call is named in a header.
var controlPlaneUrl = builder.Configuration["ControlPlane:BaseUrl"]
    ?? throw new InvalidOperationException("ControlPlane:BaseUrl must be configured (appsettings.json or ControlPlane__BaseUrl).");
builder.Services.AddSingleton(PortalCredential.FromConfiguration(builder.Configuration));
builder.Services.AddHttpClient<ControlPlaneApiClient>(http => http.BaseAddress = new Uri(controlPlaneUrl.TrimEnd('/') + "/"));
builder.Services.AddScoped<ApplicationPolicyStateToggler>();
builder.Services.AddScoped<AgentReportCache>();

var app = builder.Build();

if (!app.Services.GetRequiredService<PortalCredential>().HasKey)
{
    app.Logger.LogWarning("{Setting} is not configured: the portal calls the control plane with no credential, which only a control plane running with authentication Off (loopback) accepts.", PortalCredential.SettingName);
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

// The listener rules, then authentication and authorization - before antiforgery, which wants to
// know who the user is.
app.UsePortalAuthentication(authentication);
app.UseAntiforgery();

app.MapStaticAssets();
var components = app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// Under Required, no page and no circuit without a Windows identity: the browser is challenged
// (Negotiate) on the first request. Which pages that identity may see is then the two group policies
// on the components (Viewer for every page, Operator for Access). Static assets stay anonymous; a
// stylesheet is not a secret.
if (authentication.IsRequired)
{
    components.RequireAuthorization();
}

app.Run();

return 0;
