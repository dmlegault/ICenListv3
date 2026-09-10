using Enlist.Portal.Components;
using Enlist.Portal.Services;

using Microsoft.Extensions.Hosting.WindowsServices;

using MudBlazor.Services;

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

// The one thing this app talks to — never an agent directly (design doc section 8: "The portal never
// talks to an agent"). Base URL is the same kind of config as Enlist.Agent's --control-plane, just
// read from configuration instead of a command-line flag since this runs under IIS/a web host rather
// than being launched with arguments.
var controlPlaneUrl = builder.Configuration["ControlPlane:BaseUrl"]
    ?? throw new InvalidOperationException("ControlPlane:BaseUrl must be configured (appsettings.json or ControlPlane__BaseUrl).");
builder.Services.AddHttpClient<ControlPlaneApiClient>(http => http.BaseAddress = new Uri(controlPlaneUrl.TrimEnd('/') + "/"));
builder.Services.AddScoped<ApplicationPolicyStateToggler>();
builder.Services.AddScoped<AgentReportCache>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
