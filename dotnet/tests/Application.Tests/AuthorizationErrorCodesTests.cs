using FluentAssertions;
using TalentMatch.Application.Authorization;

namespace TalentMatch.Application.Tests;

public sealed class AuthorizationErrorCodesTests
{
    [Theory]
    [InlineData(AuthorizationErrorCodes.WrongTenant, 401)]
    [InlineData(AuthorizationErrorCodes.TokenStale, 401)]
    [InlineData(AuthorizationErrorCodes.AssignmentMissing, 403)]
    [InlineData(AuthorizationErrorCodes.IdentityDisabled, 403)]
    [InlineData(AuthorizationErrorCodes.InvalidScope, 400)]
    [InlineData(AuthorizationErrorCodes.Forbidden, 403)]
    [InlineData(AuthorizationErrorCodes.NotFound, 404)]
    [InlineData(AuthorizationErrorCodes.VersionConflict, 409)]
    [InlineData(AuthorizationErrorCodes.InvalidJobScope, 400)]
    public void Resolve_ReturnsStablePublicMapping(string code, int expectedStatusCode)
    {
        var error = AuthorizationErrorCodes.Resolve(code);

        error.Code.Should().Be(code);
        error.StatusCode.Should().Be(expectedStatusCode);
        error.Message.Should().NotBeNullOrWhiteSpace();
        error.Code.Should().Be(error.Code.ToLowerInvariant());
    }

    [Fact]
    public void Resolve_UnknownCode_DoesNotExposeIt()
    {
        var error = AuthorizationErrorCodes.Resolve("database-secret-error");

        error.Code.Should().Be(AuthorizationErrorCodes.InternalError);
        error.StatusCode.Should().Be(500);
        error.Message.Should().NotContain("database-secret-error");
    }
}