using System.Text.Json;
using WriteMe.Core;

namespace WriteMe.SyncServer;

internal static class SharedRoutes
{
    internal const string SessionCookie = "writeme_session";
    private static string Actor(HttpContext context) => (string)context.Items["account"]!;
    private static async Task<T> Body<T>(HttpContext context, int maximum = 32768)
    {
        if (!context.Request.HasJsonContentType()) throw new InvalidDataException("请使用 JSON 请求");
        var bytes = await SyncProtocol.ReadLimitedAsync(context.Request.Body, maximum, context.RequestAborted);
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
            var login = await Body<SyncLogin>(context); var result = repository.Login(login, SyncRepository.SessionDevice(context.Request.Headers.UserAgent.ToString()));
            if (result == null) return Results.Json(new { error = "账号或密码不正确" }, statusCode: 401);
            context.Response.Cookies.Append(SessionCookie, result.Token, new CookieOptions { HttpOnly = true, Secure = context.Request.IsHttps, SameSite = SameSiteMode.Strict, Path = "/", Expires = DateTimeOffset.FromUnixTimeMilliseconds(result.ExpiresAt), IsEssential = true });
            return Results.Json(repository.Profile(result.AccountId), SyncProtocol.Json);
        }).RequireRateLimiting("login");
        app.MapGet("/api/me", (HttpContext c, SyncRepository r) => Results.Json(r.Profile(Actor(c)), SyncProtocol.Json));
        app.MapPost("/api/profile", async (HttpContext c, SyncRepository r, SharedHub hub) =>
        {
            var profile = r.SetupProfile(Actor(c), await Body<SharedProfileSetup>(c), (string)c.Items["token"]!); await hub.Recheck(account: profile.Id); return Results.Json(profile, SyncProtocol.Json);
        });
        app.MapPatch("/api/profile", async (HttpContext c, SyncRepository r, SharedHub hub) =>
        {
            var profile = r.EditProfile(Actor(c), await Body<SharedProfileChange>(c, 262144)); await hub.Recheck(account: profile.Id); return Results.Json(profile, SyncProtocol.Json);
        });
        app.MapPost("/api/profile/password", async (HttpContext c, SyncRepository r, SharedHub hub) =>
        {
            var profile = r.ChangeOwnPassword(Actor(c), await Body<SharedPasswordChange>(c), (string)c.Items["token"]!); await hub.Recheck(account: profile.Id); return Results.Json(profile, SyncProtocol.Json);
        }).RequireRateLimiting("login");
        app.MapGet("/api/avatars/{account}/{version}", (string account, string version, HttpContext c, SyncRepository r) =>
        {
            var image = r.AvatarImage(Actor(c), account, version); if (image == null) return Results.NotFound();
            c.Response.Headers.CacheControl = "private, max-age=600"; c.Response.Headers.XContentTypeOptions = "nosniff"; return Results.Bytes(image, "image/png");
        });
        app.MapGet("/api/admin/accounts", (HttpContext c, SyncRepository r) => Results.Json(r.Accounts(Actor(c)), SyncProtocol.Json));
        app.MapGet("/api/admin/accounts/usage", (HttpContext c, SyncRepository r, SharedHub hub) =>
        {
            var accounts = r.AccountUsages(Actor(c)); var connections = hub.AccountConnections();
            return Results.Json(accounts.Select(account => account with { OnlineConnections = connections.GetValueOrDefault(account.Profile.Id) }), SyncProtocol.Json);
        });
        app.MapGet("/api/admin/accounts/{id}/data", (string id, HttpContext c, SyncRepository r, SharedHub hub) =>
        {
            var data = r.AccountData(Actor(c), id, (string)c.Items["token"]!); return Results.Json(data with { Usage = data.Usage with { OnlineConnections = hub.AccountConnections().GetValueOrDefault(id) } }, SyncProtocol.Json);
        });
        app.MapPost("/api/admin/accounts/{id}/sessions/revoke", async (string id, HttpContext c, SyncRepository r, SharedHub hub) =>
        { r.RevokeAccountSessions(Actor(c), id, null, (string)c.Items["token"]!); await hub.Recheck(account: id); return Results.NoContent(); });
        app.MapDelete("/api/admin/accounts/{id}/sessions/{session}", async (string id, string session, HttpContext c, SyncRepository r, SharedHub hub) =>
        { r.RevokeAccountSessions(Actor(c), id, session, (string)c.Items["token"]!); await hub.Recheck(account: id); return Results.NoContent(); });
        app.MapPost("/api/admin/accounts", async (HttpContext c, SyncRepository r) => Results.Json(r.AddAccount(Actor(c), await Body<SharedAccountInput>(c)), SyncProtocol.Json));
        app.MapPatch("/api/admin/accounts/{id}", async (string id, HttpContext c, SyncRepository r, SharedHub hub) =>
        {
            var profile = r.ChangeAccount(Actor(c), id, await Body<SharedAccountChange>(c), (string)c.Items["token"]!); await hub.Recheck(account: id); return Results.Json(profile, SyncProtocol.Json);
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
