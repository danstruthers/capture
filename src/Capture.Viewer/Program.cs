// Serves the single-page viewer in wwwroot. The page is plain HTML, CSS and JavaScript; it talks
// to the capture client directly (WebSocket signaling, then WebRTC), so this host only hands out files.

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    // A test tool: always serve the current files, never a cached copy.
    OnPrepareResponse = context => context.Context.Response.Headers.CacheControl = "no-store",
});

app.Lifetime.ApplicationStarted.Register(() =>
{
    foreach (string url in app.Urls)
    {
        Console.WriteLine($"Capture Viewer is at {url}");
    }
});

app.Run();
