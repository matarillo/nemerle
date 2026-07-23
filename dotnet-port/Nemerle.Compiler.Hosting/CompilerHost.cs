using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Loader;
using System.Threading;

using Nemerle.Collections;
using Nemerle.Core;
using Nemerle.Utility;

namespace Nemerle.Compiler.Hosting
{
    /// <summary>
    /// In-process bridge to Nemerle.Compiler.dll's ManagerClass API, modeled on
    /// ncc\codedom\NemerleCodeCompiler.n's CompileAssemblyFromFileBatch (the only prior
    /// in-process hosting of the compiler, confirmed still valid despite codedom itself being
    /// excluded from the core build) and ncc\main.n's CLI wiring
    /// (CompilationOptions.GetCommonOptions + Getopt.Parse). See
    /// dotnet-port\docs\20-inproc-task-plan.md / dotnet-port\docs\20-inproc-task-log.md.
    ///
    /// Intended to be loaded, together with Nemerle.Compiler.dll/Nemerle.dll/
    /// Nemerle.Macros.dll, into one collectible AssemblyLoadContext per compile (see
    /// Nemerle.MSBuild.Tasks.NccCompile/NccLoadContext) so ManagerClass's process-wide-looking
    /// static/[ThreadStatic] state (Instance, Location's file table, etc.) is isolated across
    /// concurrent/sequential compiles in the same MSBuild node, and so that unloading the ALC
    /// after a compile releases file locks on the reference assemblies it loaded. The only
    /// types that cross the ALC boundary at the public API below are CoreLib ones (string,
    /// int, Action&lt;...&gt;), which share type identity across ALCs -- everything
    /// Nemerle/compiler-specific stays inside this ALC.
    /// </summary>
    public static class CompilerHost
    {
        /// <summary>
        /// Compiles like ncc's own CLI: <paramref name="args"/> uses the identical
        /// -target:/-out:/-ref:/-debug/... switch syntax parsed by
        /// ncc\CompilationOptions.n's GetCommonOptions()+Getopt.Parse, plus bare source file
        /// paths (matched by a NonOption handler, exactly like
        /// NemerleCodeCompiler.CompileAssemblyFromFileBatch's `sources.Add` handler).
        /// Diagnostics are reported structurally via <paramref name="onDiagnostic"/>
        /// (file, line, col, endLine, endCol, severity: 0=info/1=warning/2=error, message)
        /// instead of parsed back out of text output. <paramref name="onOutput"/> receives
        /// whatever ncc would otherwise have written to Console.Out (line by line).
        /// Never calls Environment.Exit and never lets BailOutException/ICE/etc. escape.
        /// Returns the number of errors (0 = success).
        /// </summary>
        public static int Compile(
            string[] args,
            Action<string, int, int, int, int, int, string> onDiagnostic,
            Action<string> onOutput)
        {
            if (args == null) throw new ArgumentNullException(nameof(args));
            if (onDiagnostic == null) throw new ArgumentNullException(nameof(onDiagnostic));
            if (onOutput == null) throw new ArgumentNullException(nameof(onOutput));

            var errorCount = 0;

            void Report(int severity, Location loc, string msg)
            {
                // Mirrors ncc\parsing\Utility.n's Message.Error/Warning findLoc AND
                // NemerleCodeCompiler's own err_event: by the time ErrorOccured/WarningOccured
                // fire, Message.Error/Warning has usually already substituted
                // LocationStack.Top() for Location.Default, but this covers the rare direct
                // RunErrorOccured/RunWarningOccured caller that doesn't.
                var resolved = loc == Location.Default ? LocationStack.Top() : loc;
                onDiagnostic(resolved.File ?? "", resolved.Line, resolved.Column, resolved.EndLine, resolved.EndColumn, severity, msg);
            }

            var cOptions = new CompilationOptions();

            // Must be set before `new HostedManager(...)`: ManagerClass's own constructor
            // calls the (overridden) CreateComponentsFactory() synchronously, before any
            // instance field of HostedManager could be assigned -- see HostedManager.cs.
            HostedManager.CurrentAlc = AssemblyLoadContext.GetLoadContext(typeof(CompilerHost).Assembly);
            var man = new HostedManager(cOptions);

            man.ErrorOccured += (loc, msg) => { errorCount++; Report(2, loc, msg); };
            man.WarningOccured += (loc, msg) => Report(1, loc, msg);
            // Since the WP-M1 1-line ncc\parsing\Utility.n fix (the "N$code: $m" prefix is now
            // applied BEFORE RunWarningOccured fires), coded warnings arrive here as
            // "Nxxxx: message"; Nemerle.MSBuild.Tasks.NccCompile.ReportDiagnostic peels the
            // "Nxxxx" off into MSBuild's structured warning-code column.
            man.MessageOccured += (loc, msg) => Report(0, loc, msg);

            var sources = new List<ISource>();
            var nonOption = new Getopt.CliOption.NonOption(
                "", "Specify file to compile", new FuncVoidString(s => sources.Add(new FileSource(s, cOptions.Warnings))));
            var opts = new Nemerle.Core.list<Getopt.CliOption>.Cons(nonOption, cOptions.GetCommonOptions());

            var parseErrors = new List<string>();
            var errorFn = new FuncVoidString(m => parseErrors.Add(m));

            Getopt.Parse(errorFn, opts, NList.FromArray(args));

            foreach (var m in parseErrors)
            {
                errorCount++;
                onDiagnostic("", 0, 0, 0, 0, 2, m);
            }

            var outputWriter = new LineForwardingWriter(onOutput);
            man.InitOutput(outputWriter);
            cOptions.ProgressBar = false;
            // ANSI color escapes would corrupt MSBuild's own log output (the whole point of
            // structured diagnostics is to stop scraping colored/formatted text).
            cOptions.ColorMessages = false;
            cOptions.IgnoreConfusion = true;
            cOptions.Sources = NList.FromArray(sources.ToArray());

            // Same as ncc\main.n:163 (Options.LibraryPaths ::= <own assembly's directory>) --
            // lets bare `-ref:` names and the standard-library auto-resolution
            // (ncc\passes.n LoadCoreStdlibReferences) find this layout's Nemerle*.dll.
            var hostingDir = Path.GetDirectoryName(typeof(CompilerHost).Assembly.Location) ?? "";
            if (hostingDir.Length > 0)
                cOptions.LibraryPaths = new Nemerle.Core.list<string>.Cons(hostingDir, cOptions.LibraryPaths);

            if (sources.Count == 0)
            {
                errorCount++;
                onDiagnostic("", 0, 0, 0, 0, 2, "need at least one file to compile");
                outputWriter.FlushPending();
                return errorCount;
            }

            // Same pattern as ncc\codedom\NemerleCodeCompiler.n:168-192 / ncc\main.n:140-192:
            // run the actual compile on a dedicated, generously-stacked thread (the compiler's
            // recursive-descent parser/typer can blow the default 1MB thread stack on deeply
            // nested input), and never let any of these exception kinds propagate out --
            // convert them into a diagnostic + non-zero error count instead of
            // Environment.Exit (main.n's `bomb`) or an unhandled exception (this is a hosted
            // library call, not a process).
            void CompilerThreadProc()
            {
                try
                {
                    man.Run();
                }
                catch (FileNotFoundException e)
                {
                    errorCount++;
                    onDiagnostic("", 0, 0, 0, 0, 2, e.Message);
                }
                catch (Recovery)
                {
                    // Matches codedom: Recovery means the compiler already reported the
                    // underlying error(s) via ErrorOccured; nothing extra to surface here,
                    // but make sure the result is still treated as a failure even if for some
                    // reason no ErrorOccured fired.
                    if (errorCount == 0)
                        errorCount++;
                }
                catch (BailOutException)
                {
                    // Message.MaybeBailout() is intentionally never called by this host (it
                    // would be a no-op without it anyway), but ncc's own code can still throw
                    // BailOutException directly in a few ICE-ish paths; treat as a plain
                    // failure, matching codedom's exception table.
                    if (errorCount == 0)
                        errorCount++;
                }
                catch (System.ArgumentException e)
                {
                    errorCount++;
                    onDiagnostic("", 0, 0, 0, 0, 2, e.Message);
                }
                catch (Nemerle.Core.MatchFailureException e)
                {
                    errorCount++;
                    onDiagnostic("", 0, 0, 0, 0, 2, e.Message);
                }
                catch (ICE e)
                {
                    errorCount++;
                    onDiagnostic("", 0, 0, 0, 0, 2, e.Message);
                }
                catch (Nemerle.Core.AssertionException e)
                {
                    errorCount++;
                    onDiagnostic("", 0, 0, 0, 0, 2, e.Message);
                }
                catch (Nemerle.Core.AssemblyFindException e)
                {
                    errorCount++;
                    onDiagnostic("", 0, 0, 0, 0, 2, e.Message);
                }
                catch (Exception e)
                {
                    errorCount++;
                    onDiagnostic("", 0, 0, 0, 0, 2, "internal compiler error: " + e.Message);
                }
            }

            // Same sizing as ncc\main.n's needs_bigger_stack() path (20 * 1024 KB, x8 on
            // 64-bit): 160MB on 64-bit processes, 20MB on 32-bit.
            var stackKilos = 20 * 1024 * (IntPtr.Size == 8 ? 8 : 1);
            var thread = new Thread(CompilerThreadProc, stackKilos * 1024)
            {
                Name = "Nemerle compiler thread (hosted)"
            };
            thread.Start();
            thread.Join();

            outputWriter.FlushPending();

            return errorCount;
        }
    }
}
