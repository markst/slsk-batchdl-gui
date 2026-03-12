using Microsoft.AspNetCore.SignalR;

namespace SldlWeb.Hubs;

public class DownloadHub : Hub
{
    public async Task JoinJob(string jobId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, jobId);
    }

    public async Task LeaveJob(string jobId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, jobId);
    }
}
