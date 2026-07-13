using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Nemerle.MSBuild.Tasks
{
    /// <summary>
    /// In-process replacement for the &lt;Exec dotnet ncc.dll ...&gt; CoreCompile step
    /// (dotnet-port\msbuild\Nemerle.Core.targets). Loads Nemerle.Compiler.Hosting.dll (and,
    /// transitively, Nemerle.Compiler.dll/Nemerle.dll/Nemerle.Macros.dll) into a fresh
    /// collectible AssemblyLoadContext per call and invokes
    /// Nemerle.Compiler.Hosting.CompilerHost.Compile via reflection, translating its
    /// structured diagnostics into Log.LogError/LogWarning/LogMessage instead of parsing
    /// stdout text. See dotnet-port\20-inproc-task-plan.md.
    /// </summary>
    public sealed class NccCompile : Microsoft.Build.Utilities.Task
    {
        [Required]
        public ITaskItem[] Sources { get; set; } = Array.Empty<ITaskItem>();

        public ITaskItem[] References { get; set; } = Array.Empty<ITaskItem>();

        [Required]
        public string OutputAssembly { get; set; } = "";

        public string TargetType { get; set; } = "library";

        public bool EmitDebug { get; set; }

        /// <summary>Extra ncc CLI switches, whitespace-separated (verbatim, same shape as the
        /// old $(CustomArguments)-style escape hatch).</summary>
        public string AdditionalOptions { get; set; } = "";

        [Required]
        public string NccLayoutDir { get; set; } = "";

        public override bool Execute()
        {
            var layoutDir = NccLayoutDir;
            if (layoutDir.Length > 0 &&
                layoutDir[layoutDir.Length - 1] != Path.DirectorySeparatorChar &&
                layoutDir[layoutDir.Length - 1] != Path.AltDirectorySeparatorChar)
            {
                layoutDir += Path.DirectorySeparatorChar;
            }

            var hostingPath = Path.Combine(layoutDir, "Nemerle.Compiler.Hosting.dll");
            if (!File.Exists(hostingPath))
            {
                Log.LogError(
                    "NccLayoutDir ({0}) does not contain Nemerle.Compiler.Hosting.dll -- run dotnet-port\\pack-tool.ps1 first (or pass -p:NccLayoutDir=<path>), or set -p:NemerleUseExec=true to fall back to the out-of-process compiler.",
                    layoutDir);
                return false;
            }

            var alc = new NccLoadContext(layoutDir);
            int errorCount;
            try
            {
                var hostingAsm = alc.LoadFromAssemblyPath(hostingPath);
                var hostType = hostingAsm.GetType("Nemerle.Compiler.Hosting.CompilerHost", throwOnError: true)!;
                var compileMethod = hostType.GetMethod("Compile", BindingFlags.Public | BindingFlags.Static)
                    ?? throw new MissingMethodException("Nemerle.Compiler.Hosting.CompilerHost", "Compile");

                var args = BuildArgs();

                Action<string, int, int, int, int, int, string> onDiagnostic =
                    (file, line, col, endLine, endCol, severity, message) => ReportDiagnostic(file, line, col, endLine, endCol, severity, message);
                Action<string> onOutput = line =>
                {
                    if (line.Length > 0)
                        Log.LogMessage(MessageImportance.Low, line);
                };

                var result = compileMethod.Invoke(null, new object[] { args, onDiagnostic, onOutput });
                errorCount = result is int i ? i : int.MaxValue;
            }
            catch (TargetInvocationException tie) when (tie.InnerException != null)
            {
                Log.LogErrorFromException(tie.InnerException, showStackTrace: true);
                errorCount = 1;
            }
            finally
            {
                // Best-effort: releases the collectible ALC's hold on Nemerle.Compiler.dll and
                // every reference assembly AlcLibraryReferenceManager loaded through it, so a
                // node-reused MSBuild process doesn't keep those files locked for the next
                // build (dotnet-port\20-inproc-task-plan.md investigation point 1 / risk table
                // row 3). Actual unload completion is not guaranteed by a single Unload() call
                // (the CLR unloads only once nothing references the ALC's objects/types
                // anymore), so this is paired with a couple of forced collections; verified
                // empirically (dotnet-port\20-inproc-task-log.md) that file locks are in fact
                // released by the time the next CoreCompile runs.
                alc.Unload();
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }

            return errorCount == 0 && !Log.HasLoggedErrors;
        }

        private void ReportDiagnostic(string file, int line, int col, int endLine, int endCol, int severity, string message)
        {
            var resolvedFile = string.IsNullOrEmpty(file) ? (BuildEngine?.ProjectFileOfTaskNode ?? "") : file;

            switch (severity)
            {
                case 2:
                    Log.LogError("nemerle", null, null, resolvedFile, line, col, endLine, endCol, message);
                    break;
                case 1:
                    Log.LogWarning("nemerle", null, null, resolvedFile, line, col, endLine, endCol, message);
                    break;
                default:
                    Log.LogMessage(
                        "nemerle", null, null, resolvedFile, line, col, endLine, endCol,
                        MessageImportance.Low, message);
                    break;
            }
        }

        private string[] BuildArgs()
        {
            var list = new List<string>
            {
                "-target:" + (string.IsNullOrEmpty(TargetType) ? "library" : TargetType),
            };

            if (EmitDebug)
                list.Add("-debug");

            list.Add("-out:" + OutputAssembly);

            foreach (var r in References)
                list.Add("-ref:" + r.ItemSpec);

            if (!string.IsNullOrWhiteSpace(AdditionalOptions))
            {
                list.AddRange(AdditionalOptions.Split(
                    new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
            }

            foreach (var s in Sources)
                list.Add(s.ItemSpec);

            return list.ToArray();
        }
    }
}
