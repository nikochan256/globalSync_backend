// ============================================================
//  Valentine Build — Wi-Fi Direct Clipboard Server
//  .NET 8 / ASP.NET Core Minimal API
//  + Auto-sync to Windows clipboard via System.Windows.Forms
// ============================================================

using System.Text.Json;
using System.Windows.Forms;   // built-in on Windows — no NuGet needed

// ── Configuration ────────────────────────────────────────────
const string WifiDirectIP  = "192.168.137.1";
const string AllowedSubnet = "192.168.137.";
const int    Port          = 5000;

// ── Builder setup ────────────────────────────────────────────
var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseUrls($"http://{WifiDirectIP}:{Port}");
builder.Logging.SetMinimumLevel(LogLevel.Warning);

// Register the background service that syncs to the Windows clipboard.
builder.Services.AddHostedService<ClipboardSyncService>();

builder.Services.AddCors(options =>
{
    options.AddPolicy("WifiDirectOnly", policy =>
    {
        policy
            .SetIsOriginAllowed(origin =>
            {
                if (Uri.TryCreate(origin, UriKind.Absolute, out var uri))
                    return uri.Host.StartsWith(AllowedSubnet);
                return false;
            })
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

var app = builder.Build();
app.UseCors("WifiDirectOnly");

// ── Subnet-filter middleware ──────────────────────────────────
app.Use(async (context, next) =>
{
    var ipString = context.Connection.RemoteIpAddress?.MapToIPv4().ToString() ?? string.Empty;

    if (!ipString.StartsWith(AllowedSubnet))
    {
        Console.WriteLine($"[REJECTED] Request from {ipString} — not in subnet {AllowedSubnet}*");
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsync(
            $"403 Forbidden — only Wi-Fi Direct clients ({AllowedSubnet}x) are allowed.");
        return;
    }

    await next(context);
});

// ── Endpoints ────────────────────────────────────────────────

// POST /clipboard — receive text from another device
app.MapPost("/clipboard", async (HttpContext context) =>
{
    var body = await JsonSerializer.DeserializeAsync<ClipboardPayload>(
        context.Request.Body,
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

    if (body is null || string.IsNullOrEmpty(body.Text))
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsync("400 Bad Request — 'text' field is required.");
        return;
    }

    ClipboardStore.Text = body.Text;

    var sender = context.Connection.RemoteIpAddress?.MapToIPv4().ToString();
    Console.WriteLine($"[CLIPBOARD RECEIVED] From {sender} — \"{Truncate(ClipboardStore.Text, 60)}\"");

    await context.Response.WriteAsJsonAsync(new { message = "Clipboard updated." });
});

app.MapGet("/clipboard", (HttpContext context) =>
{
    var requester = context.Connection.RemoteIpAddress?.MapToIPv4().ToString();
    Console.WriteLine($"[CLIPBOARD REQUESTED] By {requester} — \"{Truncate(ClipboardStore.Text, 60)}\"");
    return Results.Json(new { text = ClipboardStore.Text });
});

// ── Start ─────────────────────────────────────────────────────
Console.WriteLine("===========================================");
Console.WriteLine("  Valentine Build — Clipboard Server");
Console.WriteLine($"  Listening : http://{WifiDirectIP}:{Port}");
Console.WriteLine($"  Subnet    : {AllowedSubnet}*");
Console.WriteLine("  Clipboard : auto-syncing to Windows ✓");
Console.WriteLine("===========================================");

app.Run();

// ── Helper ───────────────────────────────────────────────────
string Truncate(string value, int maxLength) =>
    value.Length <= maxLength ? value : value[..maxLength] + "…";

// ── Shared clipboard store ────────────────────────────────────
static class ClipboardStore
{
    public static string Text { get; set; } = string.Empty;
}

// ── Background sync service ───────────────────────────────────
// Watches ClipboardStore.Text every second. When it changes,
// writes the new value into the real Windows clipboard so that
// Ctrl+V works anywhere on the laptop.
class ClipboardSyncService : BackgroundService
{
    private string _lastSynced = string.Empty;

  protected override async Task ExecuteAsync(CancellationToken stoppingToken)
{
    Console.WriteLine("[SYNC] Clipboard sync service started.");

    string lastClipboardText = string.Empty;

    while (!stoppingToken.IsCancellationRequested)
    {
        try
        {
            string currentClipboard = GetWindowsClipboardText();

            if (!string.IsNullOrEmpty(currentClipboard) &&
                currentClipboard != lastClipboardText)
            {
                lastClipboardText = currentClipboard;

                Console.WriteLine(
                    $"[LAPTOP COPY DETECTED] \"{Truncate(currentClipboard, 60)}\""
                );
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[READ ERROR] {ex.Message}");
        }

        await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
    }
}

    // WHY A SEPARATE STA THREAD?
    //   Windows clipboard APIs require the calling thread to be in
    //   "Single-Threaded Apartment" (STA) mode. ASP.NET's background
    //   threads are MTA (multi-threaded apartment) by default, so
    //   calling Clipboard.SetText() directly would throw an exception.
    //   The fix: spin up a fresh thread, mark it as STA, do the work,
    //   then let it finish. This is the standard Windows pattern.
    private static void SetWindowsClipboard(string text)
    {
        Exception? threadException = null;

        var staThread = new Thread(() =>
        {
            try
            {
                Clipboard.SetText(text);   // System.Windows.Forms.Clipboard
            }
            catch (Exception ex)
            {
                threadException = ex;      // bubble the error back out
            }
        });

        staThread.SetApartmentState(ApartmentState.STA); // required for clipboard
        staThread.Start();
        staThread.Join(); // wait for it to finish before continuing

        if (threadException is not null)
            throw threadException;
    }


    private static string GetWindowsClipboardText()
{
    string result = string.Empty;
    Exception? threadException = null;

    var staThread = new Thread(() =>
    {
        try
        {
            if (Clipboard.ContainsText())
                result = Clipboard.GetText();
        }
        catch (Exception ex)
        {
            threadException = ex;
        }
    });

    staThread.SetApartmentState(ApartmentState.STA);
    staThread.Start();
    staThread.Join();

    if (threadException is not null)
        throw threadException;

    return result;
}

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + "…";
}

// ── Types ────────────────────────────────────────────────────
record ClipboardPayload(string Text);