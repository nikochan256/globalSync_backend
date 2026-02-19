// ============================================================
//  Valentine Build — Wi-Fi Direct Clipboard Server
//  .NET 8 / ASP.NET Core Minimal API
//  + Auto-sync to Windows clipboard via System.Windows.Forms
//  + Bidirectional sync: laptop copies are pushed to phone
// ============================================================

using System.Text;
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

// POST /clipboard — receive text from phone
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

    ClipboardStore.Text          = body.Text;
    ClipboardStore.LastSetByPhone = true;

    var sender = context.Connection.RemoteIpAddress?.MapToIPv4().ToString();
    Console.WriteLine($"[CLIPBOARD RECEIVED] From {sender} — \"{Truncate(ClipboardStore.Text, 60)}\"");

    SetWindowsClipboard(ClipboardStore.Text);

    await context.Response.WriteAsJsonAsync(new { message = "Clipboard updated." });
});

// GET /clipboard — phone polls for latest text
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
Console.WriteLine("  Phone     : http://192.168.137.74:5001");
Console.WriteLine("  Clipboard : auto-syncing (bidirectional) ✓");
Console.WriteLine("===========================================");

app.Run();

// ── Helpers (top-level) ───────────────────────────────────────
string Truncate(string value, int maxLength) =>
    value.Length <= maxLength ? value : value[..maxLength] + "…";

static void SetWindowsClipboard(string text)
{
    Exception? threadException = null;
    var staThread = new Thread(() =>
    {
        try   { Clipboard.SetText(text); }
        catch (Exception ex) { threadException = ex; }
    });
    staThread.SetApartmentState(ApartmentState.STA);
    staThread.Start();
    staThread.Join();
    if (threadException is not null) throw threadException;
}

// ── Shared clipboard store ────────────────────────────────────
static class ClipboardStore
{
    public static string Text             { get; set; } = string.Empty;
    public static bool   LastSetByPhone   { get; set; } = false;
}

// ── Background sync service ───────────────────────────────────
class ClipboardSyncService : BackgroundService
{
    private const string PhoneIP   = "192.168.137.74";  // phone's actual Wi-Fi Direct IP
    private const int    PhonePort = 5001;

    private readonly HttpClient _http = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Console.WriteLine("[SYNC] Clipboard sync service started (bidirectional).");

        string lastWindowsClipboard = string.Empty;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                string currentWindows = GetWindowsClipboardText();

                bool windowsClipboardChanged = !string.IsNullOrEmpty(currentWindows)
                                               && currentWindows != lastWindowsClipboard;

                if (windowsClipboardChanged)
                {
                    // Always advance the tracker so the next change is evaluated fresh.
                    lastWindowsClipboard = currentWindows;

                    if (ClipboardStore.LastSetByPhone)
                    {
                        // Phone just sent this text — laptop wrote it to Windows clipboard.
                        // Don't push it back. Clear flag and move on.
                        ClipboardStore.LastSetByPhone = false;
                        Console.WriteLine("[ECHO SUPPRESSED] Skipping phone-originated text.");
                    }
                    else
                    {
                        // Genuine laptop copy — push to phone.
                        Console.WriteLine($"[LAPTOP COPY DETECTED] \"{Truncate(currentWindows, 60)}\"");
                        ClipboardStore.Text          = currentWindows;
                        ClipboardStore.LastSetByPhone = false;
                        await PushToPhoneAsync(currentWindows, stoppingToken);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SYNC ERROR] {ex.Message}");
            }

            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
    }

    private async Task PushToPhoneAsync(string text, CancellationToken ct)
    {
        try
        {
            var json     = JsonSerializer.Serialize(new { text });
            var content  = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await _http.PostAsync($"http://{PhoneIP}:{PhonePort}/clipboard", content, ct);
            Console.WriteLine($"[PUSHED TO PHONE] Status: {(int)response.StatusCode}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PUSH ERROR] Could not reach phone: {ex.Message}");
        }
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
            catch (Exception ex) { threadException = ex; }
        });

        staThread.SetApartmentState(ApartmentState.STA);
        staThread.Start();
        staThread.Join();

        if (threadException is not null) throw threadException;
        return result;
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + "…";
}

// ── Types ────────────────────────────────────────────────────
record ClipboardPayload(string Text);




