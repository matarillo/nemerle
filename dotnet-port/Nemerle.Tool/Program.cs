// WP-I2 (task 2) dotnet-tool PoC shim. See Nemerle.Tool.csproj for why this exists: a
// `dotnet tool`'s entry point must be the assembly the packaging csproj itself builds, so
// this tiny program's only job is to re-invoke the bundled, pre-built `ncc.dll` (packed
// alongside this shim under tools\<tfm>\any\) with the standard reference rsp
// (ncc.default.rsp, produced by dotnet-port\pack-tool.ps1) pre-pended, forwarding the
// user's original arguments byte-for-byte via ProcessStartInfo.ArgumentList (no shell or
// PowerShell re-tokenization of ncc's `-name:value` switches -- see pack-tool.ps1's header
// comment for why a naive text-based wrapper is fragile for this exact CLI shape).
using System.Diagnostics;

var here = AppContext.BaseDirectory;
var nccDll = Path.Combine(here, "ncc.dll");
var rsp = Path.Combine(here, "ncc.default.rsp");

if (!File.Exists(nccDll))
{
    Console.Error.WriteLine($"nemerle-ncc: bundled ncc.dll not found next to the tool shim ({nccDll}).");
    return 1;
}

var psi = new ProcessStartInfo
{
    FileName = "dotnet",
    UseShellExecute = false,
};
psi.ArgumentList.Add(nccDll);
psi.ArgumentList.Add($"-from-file:{rsp}");
foreach (var a in args)
    psi.ArgumentList.Add(a);

using var proc = Process.Start(psi);
if (proc is null)
{
    Console.Error.WriteLine("nemerle-ncc: failed to start 'dotnet'.");
    return 1;
}
proc.WaitForExit();
var exitCode = proc.ExitCode;

// Best-effort convenience, mirroring ncc.cmd (pack-tool.ps1): on a successful compile,
// copy the bundled Nemerle*.dll into the CURRENT directory so a produced program that
// uses the stdlib beyond compile-time-only macros (e.g. printf) can actually run --
// ordinary .NET assembly probing only looks beside the entry assembly / shared framework
// (no GAC), see 12-selfhost-blockers-log.md / 18-testsuite-log.md section 3b.
if (exitCode == 0)
{
    foreach (var dll in Directory.GetFiles(here, "Nemerle*.dll"))
    {
        try
        {
            var dest = Path.Combine(Environment.CurrentDirectory, Path.GetFileName(dll));
            if (!string.Equals(Path.GetFullPath(dll), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
                File.Copy(dll, dest, overwrite: true);
        }
        catch
        {
            // Non-fatal: e.g. current directory not writable, or IS the layout dir itself.
        }
    }
}

return exitCode;
