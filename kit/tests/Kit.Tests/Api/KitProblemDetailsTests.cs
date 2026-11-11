using System;
using System.Linq;
using Jellyfin.Plugin.Kit.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Jellyfin.Plugin.Kit.Tests.Api;

public sealed class KitProblemDetailsTests
{
    public static TheoryData<string, int, string?> Kinds => new()
    {
        { nameof(PluginErrorKind.Invalid), 400, null },
        { nameof(PluginErrorKind.Forbidden), 403, null },
        { nameof(PluginErrorKind.FeatureDisabled), 403, "urn:jellyfin-plugin-kit:feature-disabled" },
        { nameof(PluginErrorKind.Locked), 403, "urn:jellyfin-plugin-kit:locked" },
        { nameof(PluginErrorKind.NotFound), 404, null },
        { nameof(PluginErrorKind.Conflict), 409, null },
        { nameof(PluginErrorKind.PayloadTooLarge), 413, null },
        { nameof(PluginErrorKind.Unavailable), 503, null }
    };

    [Fact]
    public void Kinds_CoverEveryEnumValue()
    {
        Assert.Equal(Enum.GetNames<PluginErrorKind>().Order(), Kinds.Select(row => row.Data.Item1).Order());
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void For_MapsKindToStatusTitleAndType(string kindName, int status, string? type)
    {
        var kind = Enum.Parse<PluginErrorKind>(kindName);
        var body = KitProblemDetails.For(kind, "Something happened.");

        Assert.Equal(status, body.Status);
        Assert.Equal(type, body.Type);
        Assert.Equal(Microsoft.AspNetCore.WebUtilities.ReasonPhrases.GetReasonPhrase(status), body.Title);
        Assert.Equal("Something happened.", body.Detail);
        Assert.Null(body.Instance);
        Assert.Empty(body.Extensions);
    }

    [Fact]
    public void For_AddsExtensions_ButNeverReplacesTheBodysOwnMembersOrTheCode()
    {
        var extensions = new System.Collections.Generic.Dictionary<string, object?>
        {
            ["errors"] = new[] { "a" },
            ["candidateCount"] = 5,
            ["code"] = "Other",
            ["Status"] = 200,
            ["detail"] = "Replaced."
        };

        var body = KitProblemDetails.For(PluginErrorKind.Invalid, "Detail.", "Coded", extensions);

        Assert.Equal(400, body.Status);
        Assert.Equal("Detail.", body.Detail);
        Assert.Equal(["candidateCount", "code", "errors"], body.Extensions.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("Coded", body.Extensions["code"]);
        Assert.Equal(5, body.Extensions["candidateCount"]);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Result_CarriesTheSameBodyAndStatus(string kindName, int status, string? type)
    {
        var kind = Enum.Parse<PluginErrorKind>(kindName);
        var result = KitProblemDetails.Result(kind, "Detail.");

        Assert.Equal(status, result.StatusCode);
        var body = Assert.IsType<ProblemDetails>(result.Value);
        Assert.Equal(type, body.Type);
        Assert.Equal(status, body.Status);
        Assert.Equal("Detail.", body.Detail);
    }

    [Fact]
    public void StatusCodes_MatchTheKitsErrorKinds()
    {
        Assert.Equal(StatusCodes.Status403Forbidden, KitProblemDetails.GetStatusCode(PluginErrorKind.Locked));
        Assert.Equal(StatusCodes.Status403Forbidden, KitProblemDetails.GetStatusCode(PluginErrorKind.FeatureDisabled));
        Assert.Throws<ArgumentOutOfRangeException>(() => KitProblemDetails.GetStatusCode((PluginErrorKind)99));
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Factories_SetTheKindAndMessage(string kindName, int status, string? type)
    {
        var kind = Enum.Parse<PluginErrorKind>(kindName);
        _ = status;
        _ = type;
        var factory = typeof(PluginException).GetMethod(kind.ToString(), [typeof(string)])!;

        var exception = Assert.IsType<PluginException>(factory.Invoke(null, ["Message."]));

        Assert.Equal(kind, exception.Kind);
        Assert.Equal("Message.", exception.Message);
    }

    [Fact]
    public void Constructors_DefaultToInvalid()
    {
        var inner = new InvalidOperationException("inner");

        Assert.Equal(PluginErrorKind.Invalid, new PluginException().Kind);
        Assert.Equal(PluginErrorKind.Invalid, new PluginException("x").Kind);
        Assert.Same(inner, new PluginException("x", inner).InnerException);
        Assert.Equal(PluginErrorKind.Unavailable, new PluginException(PluginErrorKind.Unavailable, "x", inner).Kind);
    }
}
