using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using CrowDB.Engine;
using CrowDB.Server;
using CrowDB.Server.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi;

var builder = WebApplication.CreateBuilder(args);

// API key authentication (docs/005, amended by docs/007): the key is a
// deployment credential in every environment — no server serves
// unauthenticated data, so local `dotnet run` needs CROWDB__APIKEY just like
// the container does (the README shows the one-liner). A start without a key
// fails fast instead of quietly serving everyone.
var apiKey = builder.Configuration["CrowDB:ApiKey"];
if (string.IsNullOrWhiteSpace(apiKey))
    throw new InvalidOperationException(
        "CrowDB:ApiKey is required: every request must be authenticated. " +
        "Set it, for example with the CROWDB__APIKEY environment variable.");

// The engine keeps everything in one log file inside this directory. The value
// in appsettings.json ("../../data") resolves to the repo-root data/ directory.
var dataDirectory = Path.GetFullPath(
    builder.Configuration["CrowDB:DataDirectory"] ?? "data",
    builder.Environment.ContentRootPath);

// The caller owns the directory, the store owns the file inside it — the same
// split as in the engine tests (TempDirectory creates the directory, the store
// gets <directory>/crowdb.log). data/ is gitignored, so a fresh clone has none.
Directory.CreateDirectory(dataDirectory);

// One store for the whole process: it owns the log file handle. Constructing
// it replays the log and rebuilds the in-memory index (recovery).
var logFile = Path.Combine(dataDirectory, Constants.LogFilename);
builder.Services
    .AddSingleton<Store>(sp => new Store(logFile, sp.GetRequiredService<ILogger<Store>>()));

// The API reference (docs/006): OpenApiDocumentService builds the OpenAPI
// document from the mapped endpoints at request time. With
// GenerateDocumentationFile set, the XML comments on the handlers become its
// summaries, parameter notes, and response descriptions — the routes document
// themselves.
builder.Services.AddOpenApi(options =>
{
    // The document mirrors the deployment shape (docs/006 §3): a key is
    // always configured (docs/007), so every operation is documented as
    // requiring the X-Api-Key scheme — Scalar's authentication panel carries
    // the key into the "try it" requests.
    options.AddDocumentTransformer((document, _, _) =>
    {
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes[ApiKeyAuthentication.SchemeName] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey,
            In = ParameterLocation.Header,
            Name = ApiKeyAuthentication.HeaderName,
            Description = "The deployment key — configured server-side as CrowDB:ApiKey.",
        };

        document.Security ??= [];
        document.Security.Add(new OpenApiSecurityRequirement
        {
            [new OpenApiSecuritySchemeReference(ApiKeyAuthentication.SchemeName, document)] = [],
        });

        return Task.CompletedTask;
    });
});


// Logging is already wired by WebApplication.CreateBuilder: it adds the console
// logger, and appsettings*.json sets the levels. The store gets its ILogger<Store>
// from the host's service provider, so engine logs use that same pipeline — the
// engine itself only knows the ILogger abstraction.
var app = builder.Build();

// Behind a reverse proxy — Railway's edge (docs/005) — Kestrel sees plain
// HTTP and, potentially, an internal host: the edge terminates TLS and
// *overwrites* X-Forwarded-Proto/-Host/-For with the truth (https, the public
// domain). Without trusting them, the API reference would advertise an
// http:// URL to an https:// page and the browser would block every "try it"
// request as mixed content (docs/006 §4). Railway publishes no stable proxy
// addresses, so the container trusts its immediate peer instead of a
// known-network list; the edge replaces any copies a caller sent.
var forwardedHeaders = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost,
};
forwardedHeaders.KnownNetworks.Clear();
forwardedHeaders.KnownProxies.Clear();
app.UseForwardedHeaders(forwardedHeaders);

// The constructor above guarantees a key, so the server has exactly one
// mode; logging which one it is matters the day another appears.
app.Logger.LogInformation(
    "API key authentication is enabled ({Header}).", ApiKeyAuthentication.HeaderName);

app.UseApiKey(apiKey);

app.MapCrowDbEndpoints();

app.Run();

// The API tests (tests/CrowDB.Server.Tests) boot this program in-process with
// WebApplicationFactory<Program>, which needs the entry point to be public.
public partial class Program;
