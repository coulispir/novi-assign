using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;

using FluentAssertions;

namespace Functional.Tests.Infrastructure;

internal static class ErrorAssertions
{
    /// <summary>
    /// Asserts the status and the stable error <c>code</c> clients branch on, and that a message is present.
    /// </summary>
    public static async Task ShouldBeErrorAsync(this HttpResponseMessage response, HttpStatusCode expectedStatus, string expectedCode)
    {
        response.StatusCode.Should().Be(expectedStatus);

        var error = await response.Content.ReadFromJsonAsync<ErrorDto>();
        error!.Code.Should().Be(expectedCode);
        error.Error.Should().NotBeNullOrWhiteSpace();
    }
}
