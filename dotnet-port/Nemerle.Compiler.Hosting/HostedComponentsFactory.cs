using System.Runtime.Loader;

namespace Nemerle.Compiler.Hosting
{
    /// <summary>
    /// Subclasses Nemerle.Compiler.CompilerComponentsFactory (the [AbstractFactory]-generated
    /// class at ncc\misc\ComponentsFactory.n) purely to override its `public virtual
    /// CreateLibraryReferenceManager(ManagerClass, list&lt;string&gt;)` factory method
    /// (confirmed virtual by reflecting Nemerle.Compiler.dll) and hand back our
    /// ALC-routing AlcLibraryReferenceManager instead of a plain LibraryReferenceManager.
    /// </summary>
    internal sealed class HostedComponentsFactory : CompilerComponentsFactory
    {
        private readonly AssemblyLoadContext? _alc;

        public HostedComponentsFactory(AssemblyLoadContext? alc)
        {
            _alc = alc;
        }

        public override LibraryReferenceManager CreateLibraryReferenceManager(ManagerClass man, Nemerle.Core.list<string> lib_paths)
        {
            return new AlcLibraryReferenceManager(man, lib_paths, _alc);
        }
    }
}
