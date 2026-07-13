using System.Runtime.Loader;

namespace Nemerle.Compiler.Hosting
{
    /// <summary>
    /// Subclasses Nemerle.Compiler.ManagerClass purely to reach its `protected virtual
    /// CreateComponentsFactory()` extension point (ncc\passes.n:431) -- the only injection
    /// seam that lets a caller of ManagerClass, without touching ncc's own sources, swap in a
    /// LibraryReferenceManager that routes reference loading into a specific
    /// AssemblyLoadContext instead of the CLR default one (ncc\passes.n:540-541 /
    /// ncc\external\LibraryReferenceManager.n:224-234; see dotnet-port\20-inproc-task-plan.md
    /// investigation point 1).
    ///
    /// CreateComponentsFactory() is called from INSIDE ManagerClass's own base constructor
    /// (via ResetCompilerState, ncc\passes.n:381-404), i.e. before any field initializer or
    /// constructor-body statement of this derived class has run -- the classic "virtual call
    /// in a base constructor" hazard. An ordinary instance field set from this class's own
    /// constructor body would therefore still be null/default at the time
    /// CreateComponentsFactory() executes. CompilerHost.Compile works around this by setting
    /// the [ThreadStatic] CurrentAlc slot on the dedicated compiler thread BEFORE constructing
    /// this class (mirroring how ManagerClass.Instance itself is [ThreadStatic] and is only
    /// meaningful on the thread that calls Run()).
    /// </summary>
    internal sealed class HostedManager : ManagerClass
    {
        [System.ThreadStatic]
        internal static AssemblyLoadContext? CurrentAlc;

        public HostedManager(CompilationOptions options)
            : base(options)
        {
        }

        protected override CompilerComponentsFactory CreateComponentsFactory()
        {
            return new HostedComponentsFactory(CurrentAlc);
        }
    }
}
