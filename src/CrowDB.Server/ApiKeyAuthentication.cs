using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace CrowDB.Server;

/// <summary>
/// API-key authentication at the HTTP edge (docs/005 §2): when a key is
/// configured, every request must carry it in <see cref="HeaderName"/>, and the
/// pipeline answers 401 before routing — no endpoint, let alone a store
/// operation, ever sees an unauthenticated request. With no key configured the
/// middleware is a no-op; Program only lets that happen in Development
/// (docs/005 §3).
/// </summary>
public static class ApiKeyAuthentication
{
    /// <summary>The header a deployment puts its key in.</summary>
    public const string HeaderName = "X-Api-Key";

    /// <summary>The name the key appears under in the API reference (docs/006 §3).</summary>
    public const string SchemeName = "ApiKeyAuth";

    /// <summary>
    /// The API reference (docs/006 §3): public even when a key is configured.
    /// A browser loads /scalar and its scripts, and the page fetches the
    /// OpenAPI document — none of which can carry the header. The paths are
    /// matched per segment, so "/scalar-extra" is not the reference.
    /// </summary>
    private const string ScalarPathPrefix = "/scalar";

    /// <summary>The prefix of the generated OpenAPI document, public like the UI.</summary>
    private const string OpenApiPathPrefix = "/openapi";

    public static void UseApiKey(this WebApplication app, string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return;

        var expected = Encoding.UTF8.GetBytes(apiKey);

        app.Use(async (context, next) =>
        {
            var path = context.Request.Path;
            if (path.StartsWithSegments(ScalarPathPrefix) || path.StartsWithSegments(OpenApiPathPrefix))
            {
                await next(context);
                return;
            }

            var provided = context.Request.Headers[HeaderName].ToString();

            // Fixed-time comparison (docs/005 §5): a wrong key must not be
            // distinguishable from the right one by how long the check takes.
            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(provided), expected))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            await next(context);
        });
    }
}
