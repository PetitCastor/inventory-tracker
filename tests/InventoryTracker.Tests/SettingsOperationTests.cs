using System.Net;
using System.Net.Http;
using InventoryTracker.App;

namespace InventoryTracker.Tests;

public sealed class SettingsOperationTests
{
    [Theory]
    [InlineData(typeof(DirectoryNotFoundException))]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    [InlineData(typeof(HttpRequestException))]
    [InlineData(typeof(InvalidDataException))]
    public void Expected_operation_failures_have_a_user_message(Type exceptionType)
    {
        var exception = (Exception)Activator.CreateInstance(exceptionType)!;

        Assert.True(FormOperationErrors.TryGetMessage(exception, out var message));
        Assert.False(string.IsNullOrWhiteSpace(message));
    }

    [Fact]
    public void Programming_failures_are_not_classified_as_user_errors()
    {
        var exception = new InvalidOperationException("broken invariant");

        Assert.False(FormOperationErrors.TryGetMessage(exception, out _));
    }

    [Fact]
    public void Http_status_error_message_keeps_the_response_detail()
    {
        var exception = new HttpRequestException("rate limited", null, HttpStatusCode.TooManyRequests);

        Assert.True(FormOperationErrors.TryGetMessage(exception, out var message));
        Assert.Contains("rate limited", message, StringComparison.OrdinalIgnoreCase);
    }
}
