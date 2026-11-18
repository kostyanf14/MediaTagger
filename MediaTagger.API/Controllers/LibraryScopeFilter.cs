using System.Collections;
using MediaTagger.API.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace MediaTagger.API.Controllers;

/// <summary>Leaves an endpoint out of the library check — for one that cannot reveal or change anything.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class OutsideLibraryScopeAttribute : Attribute { }

/// <summary>
/// Refuses any request that names a file or folder outside the library. It looks at
/// every argument of an action — query values and request bodies alike — for the
/// names a path travels under, so a new endpoint is covered without remembering to ask.
/// </summary>
public sealed class LibraryScopeFilter : IActionFilter
{
    private static readonly HashSet<string> PathNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "filePath", "filePaths", "folderPath", "rootPath", "rootPaths", "path", "destination"
    };

    private const int MaxDepth = 4;

    private readonly LibraryScope _scope;
    private readonly ILogger<LibraryScopeFilter> _logger;

    public LibraryScopeFilter(LibraryScope scope, ILogger<LibraryScopeFilter> logger)
    {
        _scope = scope;
        _logger = logger;
    }

    public void OnActionExecuting(ActionExecutingContext context)
    {
        if (context.ActionDescriptor.EndpointMetadata.OfType<OutsideLibraryScopeAttribute>().Any()) return;

        var roots = _scope.Roots();
        foreach (var (name, value) in context.ActionArguments)
        {
            var outside = FindOutside(name, value, roots, 0);
            if (outside == null) continue;
            _logger.LogWarning("Refused {Path}: it is outside the library", outside);
            context.Result = new ObjectResult(new { code = "outside_library" }) { StatusCode = StatusCodes.Status403Forbidden };
            return;
        }
    }

    public void OnActionExecuted(ActionExecutedContext context) { }

    private static string? FindOutside(string name, object? value, IReadOnlyList<string> roots, int depth)
    {
        if (value == null || depth > MaxDepth) return null;

        if (value is string text)
            return PathNames.Contains(name) && !LibraryScope.Allows(text, roots) ? text : null;

        if (value is IEnumerable items)
        {
            foreach (var item in items)
                if (FindOutside(name, item, roots, depth + 1) is { } hit) return hit;
            return null;
        }

        var type = value.GetType();
        if (type.IsPrimitive || type.IsEnum || type.Namespace?.StartsWith("System") == true) return null;

        foreach (var property in type.GetProperties())
        {
            if (property.GetIndexParameters().Length > 0 || !property.CanRead) continue;
            if (FindOutside(property.Name, property.GetValue(value), roots, depth + 1) is { } hit) return hit;
        }
        return null;
    }
}
