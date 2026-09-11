using System.Text.Json;
using WriteMe.Core;

namespace WriteMe.SyncServer;

internal static class SharedRoutes
{
    internal const string SessionCookie = "writeme_session";
    private static string Actor(HttpContext context) => (string)context.Items["account"]!;
    private static async Task<T> Body<T>(HttpContext context)
    {
        if (!context.Request.HasJsonContentType()) throw new InvalidDataException("请使用 JSON 请求");
        var bytes = await SyncProtocol.ReadLimitedAsync(context.Request.Body, 32768, context.RequestAborted);
        return JsonSerializer.Deserialize<T>(bytes, SyncProtocol.Json) ?? throw new InvalidDataException("请求为空");
    }
    internal static bool SameOrigin(HttpContext context, bool required = false)
    {
        var origin = context.Request.Headers.Origin.ToString();
        return origin.Length == 0 ? !required : Uri.TryCreate(origin, UriKind.Absolute, out var uri) && uri.Scheme == context.Request.Scheme && uri.Authority.Equals(context.Request.Host.Value, StringComparison.OrdinalIgnoreCase);
    }
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/config", () => Results.Json(new { shared = true, protocol = SharedProtocol.Version }));
        app.MapPost("/api/session", async (HttpContext context, SyncRepository repository) =>
        {
            if (!SameOrigin(context)) return Results.StatusCode(403);
            var login = await Body<SyncLogin>(context); var result = repository.Login(login);
            if (result == null) return Results.Json(new { error = "账号或密码不正确" }, statusCode: 401);
            context.Response.Cookies.Append(SessionCookie, result.Token, new CookieOptions { HttpOnly = true, Secure = context.Request.IsHttps, SameSite = SameSiteMode.Strict, Path = "/", Expires = DateTimeOffset.FromUnixTimeMilliseconds(result.ExpiresAt), IsEssential = true });
            return Results.Json(repository.Profile(result.AccountId), SyncProtocol.Json);
        }).RequireRateLimiting("login");
        app.MapGet("/api/me", (HttpContext c, SyncRepository r) => Results.Json(r.Profile(Actor(c)), SyncProtocol.Json));
        app.MapPost("/api/profile", async (HttpContext c, SyncRepository r, SharedHub hub) =>
        {
            var profile = r.SetupProfile(Actor(c), await Body<SharedProfileSetup>(c), (string)c.Items["token"]!); await hub.Recheck(account: profile.Id); return Results.Json(profile, SyncProtocol.Json);
        });
        app.MapGet("/api/admin/accounts", (HttpContext c, SyncRepository r) => Results.Json(r.Accounts(Actor(c)), SyncProtocol.Json));
        app.MapPost("/api/admin/accounts", async (HttpContext c, SyncRepository r) => Results.Json(r.AddAccount(Actor(c), await Body<SharedAccountInput>(c)), SyncProtocol.Json));
        app.MapPatch("/api/admin/accounts/{id}", async (string id, HttpContext c, SyncRepository r, SharedHub hub) =>
        {
            var profile = r.ChangeAccount(Actor(c), id, await Body<SharedAccountChange>(c)); await hub.Recheck(account: id); return Results.Json(profile, SyncProtocol.Json);
        });
        app.MapGet("/api/workspaces", (HttpContext c, SyncRepository r) => Results.Json(r.Workspaces(Actor(c)), SyncProtocol.Json));
        app.MapPost("/api/workspaces", async (HttpContext c, SyncRepository r) => Results.Json(r.CreateWorkspace(Actor(c), (await Body<SharedWorkspaceInput>(c)).Name), SyncProtocol.Json));
        app.MapGet("/api/workspaces/{workspace}/members", (string workspace, HttpContext c, SyncRepository r) => Results.Json(r.Members(Actor(c), workspace), SyncProtocol.Json));
        app.MapPut("/api/workspaces/{workspace}/members", async (string workspace, HttpContext c, SyncRepository r, SharedHub hub) =>
        { r.SetMember(Actor(c), workspace, await Body<SharedMemberInput>(c)); await hub.Recheck(workspace: workspace); return Results.NoContent(); });
        app.MapDelete("/api/workspaces/{workspace}/members/{id}", async (string workspace, string id, HttpContext c, SyncRepository r, SharedHub hub) =>
        { r.RemoveMember(Actor(c), workspace, id); await hub.Recheck(workspace: workspace, account: id); return Results.NoContent(); });
        app.MapGet("/api/workspaces/{workspace}/documents", (string workspace, bool? trash, HttpContext c, SyncRepository r) => Results.Json(r.SharedDocuments(Actor(c), workspace, trash ?? false), SyncProtocol.Json));
        app.MapPost("/api/workspaces/{workspace}/documents", async (string workspace, HttpContext c, SyncRepository r) => Results.Json(r.CreateSharedDocument(Actor(c), workspace, (await Body<SharedDocumentInput>(c)).Title), SyncProtocol.Json));
        app.MapGet("/api/shared/{id}", (string id, HttpContext c, SyncRepository r) => Results.Json(r.SharedDocument(Actor(c), id), SyncProtocol.Json));
        app.MapDelete("/api/shared/{id}", async (string id, HttpContext c, SyncRepository r, SharedHub hub) => { r.TrashSharedDocument(Actor(c), id, true); await hub.Recheck(document: id); return Results.NoContent(); });
        app.MapPost("/api/shared/{id}/restore", (string id, HttpContext c, SyncRepository r) => { r.TrashSharedDocument(Actor(c), id, false); return Results.NoContent(); });
        app.Map("/api/shared/{id}/connect", async (string id, HttpContext c, SharedHub hub) => await hub.Connect(c, id, c.RequestAborted));
    }
}
