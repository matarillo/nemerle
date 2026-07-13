using System;
using System.Reflection;
using System.Runtime.Loader;

namespace Nemerle.Compiler.Hosting
{
    /// <summary>
    /// Subclasses Nemerle.Compiler.LibraryReferenceManager purely to reach its protected
    /// virtual assemblyLoad(string)/assemblyLoad(AssemblyName)/assemblyLoadFrom(string) hooks
    /// (ncc\external\LibraryReferenceManager.n:224-234) and route every reference load
    /// through the hosting AssemblyLoadContext instead of Assembly.Load/Assembly.LoadFrom's
    /// default-ALC behavior. This is what makes reference dll file locks release when the
    /// ALC is unloaded after a compile (see NccCompile.Execute's alc.Unload(), the fix for
    /// the "file in use" / node-reuse risk noted in dotnet-port\20-inproc-task-plan.md,
    /// investigation point 1 / risk table row 3).
    /// </summary>
    internal sealed class AlcLibraryReferenceManager : LibraryReferenceManager
    {
        private readonly AssemblyLoadContext? _alc;

        public AlcLibraryReferenceManager(ManagerClass man, Nemerle.Core.list<string> lib_paths, AssemblyLoadContext? alc)
            : base(man, lib_paths)
        {
            _alc = alc;
        }

        protected override Assembly assemblyLoad(string name)
        {
            return _alc != null
                ? _alc.LoadFromAssemblyName(new AssemblyName(name))
                : Assembly.Load(name);
        }

        protected override Assembly assemblyLoad(AssemblyName name)
        {
            return _alc != null
                ? _alc.LoadFromAssemblyName(name)
                : Assembly.Load(name);
        }

        protected override Assembly assemblyLoadFrom(string path)
        {
            try
            {
                return _alc != null
                    ? _alc.LoadFromAssemblyPath(path)
                    : Assembly.LoadFrom(path);
            }
            catch (BadImageFormatException)
            {
                // Same fallback as the base implementation: somebody gave us a 32-bit
                // reference on a 64-bit host or vice versa -- resolve by full name instead.
                return assemblyLoad(AssemblyName.GetAssemblyName(path).FullName);
            }
        }
    }
}
