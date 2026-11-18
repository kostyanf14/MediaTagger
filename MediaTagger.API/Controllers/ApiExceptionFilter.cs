using MediaTagger.API.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace MediaTagger.API.Controllers;

/// <summary>
/// Turns the failures a file operation can run into into answers the UI can show —
/// which file, and why — instead of a bare 500.
/// </summary>
public sealed class ApiExceptionFilter : IExceptionFilter
{
    private readonly ILogger<ApiExceptionFilter> _logger;

    public ApiExceptionFilter(ILogger<ApiExceptionFilter> logger) => _logger = logger;

    public void OnException(ExceptionContext context)
    {
        switch (context.Exception)
        {
            case TagsFileUnreadableException unreadable:
                _logger.LogWarning("Refused an edit: {Msg}", unreadable.Message);
                context.Result = new ConflictObjectResult(new { code = "tags_unreadable", path = unreadable.Path });
                context.ExceptionHandled = true;
                break;
            case IOException or UnauthorizedAccessException:
                _logger.LogWarning("File operation failed: {Msg}", context.Exception.Message);
                context.Result = new ConflictObjectResult(new { code = "io", message = context.Exception.Message });
                context.ExceptionHandled = true;
                break;
        }
    }
}
