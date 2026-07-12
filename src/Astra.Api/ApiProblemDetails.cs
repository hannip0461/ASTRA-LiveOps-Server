using System.Diagnostics;
using Astra.Domain;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using Orleans.Runtime;

namespace Astra.Api;

public static class ApiProblemDetailsExtensions
{
    public static void AddApiProblemDetails(this IServiceCollection services)
    {
        services.AddProblemDetails(options =>
        {
            options.CustomizeProblemDetails = context =>
                ApiProblemDetails.Enrich(context.HttpContext, context.ProblemDetails);
        });
        services.AddExceptionHandler<ApiExceptionHandler>();
    }
}

internal static class ApiProblemDetails
{
    public static IResult Validation(
        HttpContext context,
        IReadOnlyDictionary<string, string[]> errors,
        string code = "validation_failed",
        string title = "Request validation failed")
    {
        var problem = new HttpValidationProblemDetails(errors)
        {
            Status = StatusCodes.Status400BadRequest,
            Title = title,
            Detail = "One or more request fields are invalid."
        };
        Enrich(context, problem, code);
        return Results.Json(
            problem,
            statusCode: problem.Status,
            contentType: "application/problem+json");
    }

    public static async Task WriteStatusCodeAsync(HttpContext context)
    {
        var descriptor = FromStatusCode(context.Response.StatusCode);
        await WriteAsync(
            context,
            descriptor.Status,
            descriptor.Code,
            descriptor.Title,
            descriptor.Detail,
            context.RequestAborted);
    }

    public static async ValueTask<bool> WriteAsync(
        HttpContext context,
        int status,
        string code,
        string title,
        string detail,
        CancellationToken cancellationToken)
    {
        context.Response.StatusCode = status;
        var problem = new ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = detail
        };
        Enrich(context, problem, code);

        var service = context.RequestServices.GetRequiredService<IProblemDetailsService>();
        if (await service.TryWriteAsync(
            new ProblemDetailsContext
            {
                HttpContext = context,
                ProblemDetails = problem
            }))
        {
            return true;
        }

        context.Response.ContentType = "application/problem+json";
        await context.Response.WriteAsJsonAsync(
            problem,
            cancellationToken: cancellationToken);
        return true;
    }

    public static void Enrich(
        HttpContext context,
        ProblemDetails problem,
        string? code = null)
    {
        var effectiveCode = code ?? FromStatusCode(problem.Status ?? context.Response.StatusCode).Code;
        problem.Status ??= context.Response.StatusCode;
        problem.Type ??= $"urn:astra:problem:{effectiveCode}";
        problem.Instance ??= context.Request.Path;
        problem.Extensions.TryAdd("code", effectiveCode);
        problem.Extensions.TryAdd(
            "traceId",
            Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier);
    }

    private static ProblemDescriptor FromStatusCode(int status) => status switch
    {
        StatusCodes.Status400BadRequest => new(status, "invalid_request", "Invalid request", "The request could not be parsed or validated."),
        StatusCodes.Status401Unauthorized => new(status, "authentication_required", "Authentication required", "A valid access token is required."),
        StatusCodes.Status403Forbidden => new(status, "permission_denied", "Permission denied", "The authenticated operator lacks the required role."),
        StatusCodes.Status404NotFound => new(status, "resource_not_found", "Resource not found", "The requested resource does not exist."),
        StatusCodes.Status405MethodNotAllowed => new(status, "method_not_allowed", "Method not allowed", "The HTTP method is not supported for this resource."),
        StatusCodes.Status413PayloadTooLarge => new(status, "request_too_large", "Request too large", "The request body exceeds the configured limit."),
        StatusCodes.Status415UnsupportedMediaType => new(status, "unsupported_media_type", "Unsupported media type", "A supported JSON content type is required."),
        StatusCodes.Status429TooManyRequests => new(status, "rate_limited", "Too many requests", "The request rate limit has been exceeded."),
        _ when status >= 500 => new(status, "service_error", "Service error", "The request could not be completed."),
        _ => new(status, $"http_{status}", "Request failed", "The request could not be completed.")
    };

    internal sealed record ProblemDescriptor(
        int Status,
        string Code,
        string Title,
        string Detail);
}

internal sealed class ApiExceptionHandler(ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext context,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException && context.RequestAborted.IsCancellationRequested)
        {
            return false;
        }

        var descriptor = Describe(exception);
        if (descriptor.Status >= StatusCodes.Status500InternalServerError)
        {
            logger.LogError(
                exception,
                "API request failed. Code={ProblemCode} TraceId={TraceId}",
                descriptor.Code,
                Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier);
        }

        return await ApiProblemDetails.WriteAsync(
            context,
            descriptor.Status,
            descriptor.Code,
            descriptor.Title,
            descriptor.Detail,
            cancellationToken);
    }

    internal static ApiProblemDetails.ProblemDescriptor Describe(Exception exception) => exception switch
    {
        IdempotencyConflictException => Problem(
            StatusCodes.Status409Conflict,
            "idempotency_conflict",
            "Idempotency conflict",
            "The idempotency key was already used for a different command."),
        InsufficientCurrencyException => Problem(
            StatusCodes.Status409Conflict,
            "insufficient_currency",
            "Insufficient currency",
            "The account does not have enough currency for this command."),
        MailAlreadyClaimedException => Problem(
            StatusCodes.Status409Conflict,
            "mail_already_claimed",
            "Mail already claimed",
            "The mail reward was already claimed."),
        MailNotEligibleException => Problem(
            StatusCodes.Status403Forbidden,
            "mail_not_eligible",
            "Mail not eligible",
            "The player is not included in the mail target snapshot."),
        MailNotFoundException => Problem(
            StatusCodes.Status404NotFound,
            "mail_not_found",
            "Mail not found",
            "The requested mail definition does not exist."),
        InvalidAccountCommandException => Problem(
            StatusCodes.Status400BadRequest,
            "command_invalid",
            "Invalid command",
            "The command violates a domain rule."),
        ContentUnavailableException => Problem(
            StatusCodes.Status409Conflict,
            "content_unavailable",
            "Content unavailable",
            "No active content snapshot can serve this command."),
        ContentMismatchException => Problem(
            StatusCodes.Status409Conflict,
            "content_mismatch",
            "Content mismatch",
            "The requested content is not active."),
        ContentVersionConflictException => Problem(
            StatusCodes.Status409Conflict,
            "content_version_conflict",
            "Content version conflict",
            "The content version conflicts with an existing snapshot."),
        ContentVersionInactiveException => Problem(
            StatusCodes.Status409Conflict,
            "content_version_inactive",
            "Content version inactive",
            "The requested content version cannot be activated."),
        BadHttpRequestException badRequest => Problem(
            badRequest.StatusCode,
            badRequest.StatusCode == StatusCodes.Status413PayloadTooLarge
                ? "request_too_large"
                : "invalid_request",
            "Invalid request",
            "The request body or route values are invalid."),
        OverflowException => Problem(
            StatusCodes.Status409Conflict,
            "numeric_overflow",
            "Numeric limit exceeded",
            "The command would exceed an account numeric limit."),
        TimeoutException => Problem(
            StatusCodes.Status504GatewayTimeout,
            "dependency_timeout",
            "Dependency timeout",
            "A required backend operation did not complete in time."),
        OrleansException or NpgsqlException => Problem(
            StatusCodes.Status503ServiceUnavailable,
            "dependency_unavailable",
            "Dependency unavailable",
            "A required backend service is unavailable."),
        _ => Problem(
            StatusCodes.Status500InternalServerError,
            "internal_error",
            "Internal server error",
            "The request could not be completed.")
    };

    private static ApiProblemDetails.ProblemDescriptor Problem(
        int status,
        string code,
        string title,
        string detail) => new(status, code, title, detail);
}
