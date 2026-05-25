using System.Diagnostics;
using Serilog;

namespace Hook.Shared.Core;

internal static class FrontendBootstrapper
{
    public static void EnsureBuilt(IHostEnvironment env)
    {
        if (!env.IsDevelopment()) return;

        var wwwroot = Path.Combine(env.ContentRootPath, "wwwroot");
        if (File.Exists(Path.Combine(wwwroot, "index.html"))) return;

        var frontendDir = Path.GetFullPath(Path.Combine(env.ContentRootPath, "..", "..", "frontend"));
        if (!File.Exists(Path.Combine(frontendDir, "package.json"))) return;

        Log.Information("[frontend] wwwroot empty — building in {FrontendDir}", frontendDir);
        Run(frontendDir, "ci");
        Run(frontendDir, "run", "build");
    }

    private static void Run(string cwd, params string[] args)
    {
        // On Windows, route through cmd.exe so npm.cmd's `%~dp0` shim resolves
        // npm-cli.js from the npm install location, not the project cwd's
        // (missing) node_modules. Direct `npm.cmd` launches under ProcessStartInfo
        // can trip MODULE_NOT_FOUND when cwd has no node_modules yet.
        ProcessStartInfo psi;
        if (OperatingSystem.IsWindows())
        {
            psi = new ProcessStartInfo("cmd.exe") { WorkingDirectory = cwd, UseShellExecute = false };
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add("npm");
        }
        else
        {
            psi = new ProcessStartInfo("npm") { WorkingDirectory = cwd, UseShellExecute = false };
        }
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start `npm {string.Join(' ', args)}` in '{cwd}'.");
        proc.WaitForExit();
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"`npm {string.Join(' ', args)}` in '{cwd}' exited {proc.ExitCode}.");
    }
}
