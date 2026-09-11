using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using WriteMe.Core;

namespace WriteMe.SyncServer;

public sealed record SyncHostOptions(string DataDirectory, string? SetupUser, string? SetupPassword, string Urls = "http://127.0.0.1:0", bool Quiet = false, string? WebRoot = null);

public static class SyncServerHost
{
    public static WebApplication Build(SyncHostOptions options)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], ApplicationName = typeof(SyncServerHost).Assembly.FullName });
        builder.WebHost.UseUrls(options.Urls);
        builder.WebHost.ConfigureKestrel(server => server.Limits.MaxRequestBodySize = NoteStore.MaximumAssetSize + 1024);
        if (options.Quiet) builder.Logging.ClearProviders();
        builder.Services.AddSingleton(_ => new SyncRepository(options.DataDirectory, options.SetupUser, options.SetupPassword));
        builder.Services.AddSingleton<SharedHub>();
        builder.Services.AddRateLimiter(rate =>
        {
            rate.RejectionStatusCode = 429;
            rate.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "local",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 12, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true }));
        });
        var app = builder.Build();
        _ = app.Services.GetRequiredService<SyncRepository>();
        app.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            try { await next(context); }
            catch (SharedAccessException ex) when (!context.Response.HasStarted)
            { context.Response.StatusCode = ex.Status; await context.Response.WriteAsJsonAsync(new { error = ex.Message }, context.RequestAborted); }
            catch (Exception ex) when (ex is InvalidDataException or JsonException or ArgumentException)
            { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new { error = "请求格式或内容无效" }, context.RequestAborted); }
        });
        app.UseRateLimiter();
        app.UseWebSockets(new() { KeepAliveInterval = TimeSpan.FromSeconds(20) });
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/api") && context.Request.Path != "/api/login" && context.Request.Path != "/api/session" && context.Request.Path != "/api/config")
            {
                var header = context.Request.Headers.Authorization.ToString(); var token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..] : "";
                var cookie = token.Length == 0;
                if (cookie) token = context.Request.Cookies[SharedRoutes.SessionCookie] ?? "";
                if (!SharedRoutes.SameOrigin(context, cookie && (context.Request.Method != "GET" || context.WebSockets.IsWebSocketRequest))) { context.Response.StatusCode = 403; return; }
                var account = context.RequestServices.GetRequiredService<SyncRepository>().Authenticate(token);
                if (account == null) { context.Response.StatusCode = 401; return; }
                context.Items["account"] = account; context.Items["token"] = token;
            }
            await next(context);
        });
        app.MapGet("/health", () => Results.Json(new { status = "ok", protocol = 1 }));
        app.MapPost("/api/login", async (HttpContext context, SyncRepository repository) =>
        {
            var bytes = await SyncProtocol.ReadLimitedAsync(context.Request.Body, 8192, context.RequestAborted);
            var login = JsonSerializer.Deserialize<SyncLogin>(bytes, SyncProtocol.Json) ?? throw new InvalidDataException();
            var result = repository.Login(login); return result == null ? Results.Unauthorized() : Results.Json(result, SyncProtocol.Json);
        }).RequireRateLimiting("login");
        app.MapPost("/api/logout", async (HttpContext context, SyncRepository repository, SharedHub hub) => { repository.Logout((string)context.Items["token"]!); context.Response.Cookies.Delete(SharedRoutes.SessionCookie); await hub.Recheck(account: (string)context.Items["account"]!); return Results.NoContent(); });
        app.MapPost("/api/sync", async (HttpContext context, SyncRepository repository) =>
        {
            var bytes = await SyncProtocol.ReadLimitedAsync(context.Request.Body, SyncProtocol.MaximumBatchBytes, context.RequestAborted);
            var request = JsonSerializer.Deserialize<SyncRequest>(bytes, SyncProtocol.Json) ?? throw new InvalidDataException();
            return Results.Json(repository.Exchange((string)context.Items["account"]!, request), SyncProtocol.Json);
        });
        app.MapMethods("/api/assets/{id}", ["HEAD"], (string id, HttpContext context, SyncRepository repository) => repository.AssetPath((string)context.Items["account"]!, id) == null ? Results.NotFound() : Results.Ok());
        app.MapGet("/api/assets/{id}", (string id, HttpContext context, SyncRepository repository) => repository.AssetPath((string)context.Items["account"]!, id) is { } path ? Results.File(path, "application/octet-stream") : Results.NotFound());
        app.MapPut("/api/assets/{id}", async (string id, HttpContext context, SyncRepository repository) =>
        {
            await repository.SaveAssetAsync((string)context.Items["account"]!, id, context.Request.Body, context.RequestAborted); return Results.NoContent();
        });
        SharedRoutes.Map(app);
        var webRoot = options.WebRoot ?? Path.Combine(AppContext.BaseDirectory, "wwwroot");
        if (Directory.Exists(webRoot))
        {
            app.Use(async (context, next) => { if (context.Request.Path == "/") context.Response.Redirect("/team"); else await next(context); });
            var provider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(webRoot);
            app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = provider });
            app.UseStaticFiles(new StaticFileOptions { FileProvider = provider });
            app.MapFallback(async context => { if (context.Request.Path.StartsWithSegments("/api")) context.Response.StatusCode = 404; else { context.Response.ContentType = "text/html; charset=utf-8"; await context.Response.SendFileAsync(Path.Combine(webRoot, "index.html")); } });
        }
        return app;
    }
}

public static class Program
{
    public static async Task Main(string[] args)
    {
        var directory = Environment.GetEnvironmentVariable("WRITEME_SYNC_DATA") ?? Path.Combine(AppContext.BaseDirectory, "sync-data");
        var user = Environment.GetEnvironmentVariable("WRITEME_SETUP_USER"); var password = Environment.GetEnvironmentVariable("WRITEME_SETUP_PASSWORD");
        if (args.Length == 2 && args[0] == "--add-user")
        {
            var secret = Console.ReadLine() ?? "";
            using var repository = new SyncRepository(directory, user, password); repository.CreateAccount(args[1], secret); Console.WriteLine("账号已创建"); return;
        }
        await using var app = SyncServerHost.Build(new(directory, user, password, Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? "http://0.0.0.0:8080"));
        await app.RunAsync();
    }
}
