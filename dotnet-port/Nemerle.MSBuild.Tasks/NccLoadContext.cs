using System.Reflection;
using System.Runtime.Loader;

namespace Nemerle.MSBuild.Tasks
{
    /// <summary>
    /// One collectible ALC per compile (see NccCompile.Execute, which creates, uses and
    /// Unload()s one of these per call): resolves Nemerle.Compiler.Hosting.dll and everything
    /// it transitively needs (Nemerle.Compiler.dll, Nemerle.dll, Nemerle.Macros.dll,
    /// Nemerle.CoreEmit.dll) from the layout directory FIRST, so all of them end up loaded
    /// together into this one ALC (required for the ManagerClass/CompilerComponentsFactory/
    /// LibraryReferenceManager subclassing in Nemerle.Compiler.Hosting to type-check across
    /// its own base classes -- they must be the same Type identity, which requires being in
    /// the same load context). Anything not found in the layout directory (CoreLib, System.*,
    /// Microsoft.Build.*) falls through to the default resolution (returning null lets the
    /// runtime fall back to AssemblyLoadContext.Default), so those keep the single shared
    /// identity every ALC needs for cross-ALC calls to work at all (see CompilerHost.Compile's
    /// public surface: only CoreLib types cross back out to this task).
    ///
    /// Unloading this ALC after a compile is what releases file locks the compile took on
    /// reference assemblies it loaded via AlcLibraryReferenceManager (dotnet-port\
    /// 20-inproc-task-plan.md investigation point 1 / risk table row 3 -- MSBuild node reuse
    /// must not leave ProjectReference output dlls locked between builds).
    /// </summary>
    internal sealed class NccLoadContext : AssemblyLoadContext
    {
        private readonly string _layoutDir;

        public NccLoadContext(string layoutDir)
            : base(name: "Nemerle.Ncc." + System.Guid.NewGuid().ToString("N"), isCollectible: true)
        {
            _layoutDir = layoutDir;
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (string.IsNullOrEmpty(assemblyName.Name))
                return null;

            var candidate = System.IO.Path.Combine(_layoutDir, assemblyName.Name + ".dll");
            return System.IO.File.Exists(candidate) ? LoadFromAssemblyPath(candidate) : null;
        }
    }
}
