using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Net.Http.Headers;

namespace KnS.Server.Endpoints;

/// <summary>
/// The packaged browser build (KNS_WEB_ROOT) at the public base: "/" is the game (index.html), "/studio" the Studio (studio.html), and
/// everything else a file. A file.gz next to a file is sent instead to clients that accept gzip. Hash-named folders (kns_data-*,
/// studio-assets/*) never change and are cached for a year; pages are revalidated.
/// </summary>
public static class WebFiles
{
    private const string OriginalNameKey = "kns.web.original";

    public static void Use(IApplicationBuilder app, string webRoot)
    {
        var files = new PhysicalFileProvider(webRoot);
        var types = new FileExtensionContentTypeProvider();
        app.Use(async (context, next) =>
        {
            // The host's implicit routing matched an API endpoint against the full path; files are not endpoints.
            context.SetEndpoint(null);
            string path = context.Request.Path.Value ?? "/";
            if (path.Length == 0 || path.EndsWith('/'))
            {
                path += "index.html";
            }
            else if (path.Equals("/studio", StringComparison.OrdinalIgnoreCase))
            {
                path = "/studio.html";
            }

            context.Items[OriginalNameKey] = path;
            if (context.Request.Headers.AcceptEncoding.ToString().Contains("gzip", StringComparison.OrdinalIgnoreCase)
                && files.GetFileInfo(path + ".gz").Exists)
            {
                context.Response.Headers.ContentEncoding = "gzip";
                context.Response.Headers.Vary = "Accept-Encoding";
                path += ".gz";
            }

            context.Request.Path = path;
            await next();
        });
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = files,
            ContentTypeProvider = types,
            OnPrepareResponse = prepared =>
            {
                HttpContext context = prepared.Context;
                string original = context.Items[OriginalNameKey] as string ?? prepared.File.Name;
                if (types.TryGetContentType(original, out string? type))
                {
                    context.Response.ContentType = type;
                }

                bool immutable = original.StartsWith("/kns_data-", StringComparison.Ordinal)
                    || original.StartsWith("/studio-assets/", StringComparison.Ordinal);
                context.Response.Headers[HeaderNames.CacheControl] = immutable ? "public, max-age=31536000, immutable" : "no-cache";
            },
        });
        app.Run(context =>
        {
            context.Response.Headers.Remove("Content-Encoding");
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        });
    }
}
