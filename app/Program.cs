using SldlWeb.Components;
using SldlWeb.Hubs;
using SldlWeb.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddSignalR();
builder.Services.AddSingleton<DownloadService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.UseStaticFiles();
app.UseAntiforgery();

app.MapHub<DownloadHub>("/downloadHub");

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
