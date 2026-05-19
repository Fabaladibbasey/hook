using Hook.Shared.Security;
using Microsoft.AspNetCore.Http;
using Shouldly;

namespace Hook.UnitTests.Shared;

public class SecurityHeadersMiddlewareTests
{
    [Fact]
    public void WriteHeaders_Production_SetsAllSixBaselineHeadersAndHsts()
    {
        var headers = new HeaderDictionary();

        SecurityHeadersMiddleware.WriteHeaders(headers, emitHsts: true);

        headers["Content-Security-Policy"].ToString().ShouldContain("default-src 'self'");
        headers["Content-Security-Policy"].ToString().ShouldContain("frame-ancestors 'none'");
        headers["X-Content-Type-Options"].ToString().ShouldBe("nosniff");
        headers["X-Frame-Options"].ToString().ShouldBe("DENY");
        headers["Referrer-Policy"].ToString().ShouldBe("no-referrer");
        headers["Permissions-Policy"].ToString().ShouldContain("camera=()");
        headers["Cross-Origin-Opener-Policy"].ToString().ShouldBe("same-origin");
        headers["Strict-Transport-Security"].ToString().ShouldBe("max-age=63072000; includeSubDomains; preload");
    }

    [Fact]
    public void WriteHeaders_Development_OmitsHsts()
    {
        var headers = new HeaderDictionary();

        SecurityHeadersMiddleware.WriteHeaders(headers, emitHsts: false);

        headers.ContainsKey("Strict-Transport-Security").ShouldBeFalse();
        headers["Content-Security-Policy"].ToString().ShouldNotBeNullOrEmpty();
        headers["X-Frame-Options"].ToString().ShouldBe("DENY");
    }
}
