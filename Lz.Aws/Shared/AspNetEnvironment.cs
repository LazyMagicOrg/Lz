namespace Lz.Aws.Shared;

/// <summary>
/// The <c>ASPNETCORE_ENVIRONMENT</c> a deployed service runs under: <c>Development</c> in dev, <c>Production</c>
/// everywhere else. One rule for every component that defines a container or function, so the topologies cannot
/// disagree.
///
/// <para>WHY NOT DEVELOPMENT IN TEST (DecoupledCd V-21, owner, 2026-10-17). Development turns on ASP.NET's developer
/// exception page, which hands a caller the stack trace of any unhandled exception, and an environment that admits
/// only what dev passed should run as prod will. Until 2026-10-20 the Fargate and Lambda tenant components said
/// <c>env == "prod" ? "Production" : "Development"</c>, so test ran as Development; the FargateAlb components already
/// said this. Dev keeps Development for the exception page and for ASP.NET's dependency-injection check at
/// startup, which only Development runs.</para>
/// </summary>
public static class AspNetEnvironment
{
    public const string Variable = "ASPNETCORE_ENVIRONMENT";

    /// <summary>The value for <paramref name="environment"/> (an lz environment name: dev, test, prod, ...).</summary>
    public static string For(string? environment)
        => string.Equals(environment, "dev", StringComparison.OrdinalIgnoreCase) ? "Development" : "Production";
}
