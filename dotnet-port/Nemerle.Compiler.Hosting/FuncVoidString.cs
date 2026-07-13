using System;

namespace Nemerle.Compiler.Hosting
{
    /// <summary>
    /// Nemerle function-type values (e.g. the `string -&gt; void` type used by
    /// Nemerle.Utility.Getopt.Parse's error_fn parameter and by
    /// Getopt.CliOption.NonOption's handler field) are not .NET delegates: they compile to
    /// instances of the abstract class Nemerle.Builtins.FunctionVoid&lt;T&gt;, whose single
    /// abstract member `apply_void(T)` a caller must override. This is the C# adapter that
    /// lets us pass an ordinary Action&lt;string&gt; wherever ncc's own Nemerle sources
    /// expect a `string -&gt; void` value (confirmed by reflecting the built
    /// Nemerle.dll/Nemerle.Compiler.dll -- see dotnet-port\20-inproc-task-log.md).
    /// </summary>
    internal sealed class FuncVoidString : Nemerle.Builtins.FunctionVoid<string>
    {
        private readonly Action<string> _action;

        public FuncVoidString(Action<string> action)
        {
            _action = action;
        }

        public override void apply_void(string p1) => _action(p1);
    }
}
