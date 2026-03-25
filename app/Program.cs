using SldlWeb.Components;
using SldlWeb.Hubs;
using SldlWeb.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddSignalR();
builder.Services.AddSingleton<SettingsService>();
builder.Services.AddSingleton<JobRestorer>();
builder.Services.AddSingleton<DownloadService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.UseStaticFiles();
app.UseAntiforgery();

app.MapHub<DownloadHub>("/downloadHub");

app.MapGet("/api/jobs/{jobId}/tracks/{trackIndex:int}/stream", (string jobId, int trackIndex, DownloadService svc) =>
{
    var job = svc.GetJob(jobId);
    if (job is null || trackIndex < 0 || trackIndex >= job.Tracks.Count)
        return Results.NotFound();

    var track = job.Tracks[trackIndex];
    if (string.IsNullOrEmpty(track.DownloadPath) || !System.IO.File.Exists(track.DownloadPath))
        return Results.NotFound();

    // Ensure the file is within the job's download directory (prevent path traversal)
    var resolvedPath = Path.GetFullPath(track.DownloadPath);
    var jobDir = Path.GetFullPath(job.DownloadPath);
    if (!resolvedPath.StartsWith(jobDir, StringComparison.OrdinalIgnoreCase))
        return Results.NotFound();

    var ext = Path.GetExtension(resolvedPath).ToLowerInvariant();
    var contentType = ext switch
    {
        ".mp3" => "audio/mpeg",
        ".flac" => "audio/flac",
        ".wav" => "audio/wav",
        ".m4a" or ".aac" => "audio/mp4",
        ".ogg" => "audio/ogg",
        ".opus" => "audio/ogg",
        ".wma" => "audio/x-ms-wma",
        _ => "application/octet-stream"
    };

    return Results.File(resolvedPath, contentType, enableRangeProcessing: true);
});

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
