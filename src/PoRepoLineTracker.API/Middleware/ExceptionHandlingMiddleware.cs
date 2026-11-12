using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System;
using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace PoRepoLineTracker.Api.Middleware
{
    public class ExceptionHandlingMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<ExceptionHandlingMiddleware> _logger;

        public ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
        {
            _next = next;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext httpContext)
        {
            try
            {
                await _next(httpContext);
            }
            // The framework's own way of saying the CLIENT got it wrong — an unparseable route
            // parameter, a malformed body — and it carries the status code it wants (400). It used
            // to fall into the catch-all below and be reported as 500, which blames the server for
            // the caller's typo and files a routine client mistake as a logged incident.
            //
            // Every route taking a strongly-typed id reaches this path: RepositoryId.TryParse
            // correctly returns false for "not-a-guid", the binder turns that into this exception,
            // and GET /api/code-health/not-a-guid answered 500. Logged as a warning, not an error,
            // for the same reason — nothing here is broken.
            catch (BadHttpRequestException ex)
            {
                _logger.LogWarning(ex, "Malformed request rejected: {Message}", ex.Message);
                await HandleExceptionAsync(httpContext, ex, ex.StatusCode);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An unhandled exception occurred: {Message}", ex.Message);
                await HandleExceptionAsync(httpContext, ex, (int)HttpStatusCode.InternalServerError);
            }
        }

        private static async Task HandleExceptionAsync(HttpContext context, Exception exception, int statusCode)
        {
            context.Response.ContentType = "application/problem+json";
            context.Response.StatusCode = statusCode;

            var isClientError = statusCode is >= 400 and < 500;

            var problemDetails = new ProblemDetails
            {
                Status = statusCode,
                Title = isClientError
                    ? "The request could not be processed."
                    : "An error occurred while processing your request.",
                Detail = isClientError
                    ? "The request was not valid. Check the URL and the request body."
                    : "An unexpected internal server error has occurred. Please try again later.",
                Type = "https://tools.ietf.org/html/rfc7807#section-3.1" // Standard type for internal server error
            };

            // In development, you might want to expose more details
            if (context.RequestServices.GetService(typeof(Microsoft.AspNetCore.Hosting.IWebHostEnvironment)) is Microsoft.AspNetCore.Hosting.IWebHostEnvironment env
                && (env.IsDevelopment() || env.IsEnvironment("Test")))
            {
                problemDetails.Detail = exception.Message;
                problemDetails.Extensions.Add("traceId", System.Diagnostics.Activity.Current?.Id ?? context.TraceIdentifier);
                problemDetails.Extensions.Add("exception", exception.GetType().Name);
                problemDetails.Extensions.Add("stackTrace", exception.StackTrace);
            }

            var json = JsonSerializer.Serialize(problemDetails, ApiJsonContext.Default.ProblemDetails);
            await context.Response.WriteAsync(json);
        }
    }
}
