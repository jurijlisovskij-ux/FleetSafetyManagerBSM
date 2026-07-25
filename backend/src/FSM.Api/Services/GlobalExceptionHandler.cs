using Microsoft.AspNetCore.Diagnostics;

namespace FSM.Api.Services;

public sealed class GlobalExceptionHandler(
    ILogger<GlobalExceptionHandler> logger,
    IHostEnvironment environment) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        logger.LogError(
            exception,
            "An unexpected error occurred while processing the request.");

        await Results.Problem(
            statusCode: StatusCodes.Status500InternalServerError,
            title: "Unexpected server error",
            detail: environment.IsDevelopment()
                ? exception.Message
                : "An unexpected error occurred.")
            .ExecuteAsync(httpContext);

        return true;
    }
}
