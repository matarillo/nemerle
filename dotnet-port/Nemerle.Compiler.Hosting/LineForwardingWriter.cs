using System;
using System.IO;
using System.Text;

namespace Nemerle.Compiler.Hosting
{
    /// <summary>
    /// TextWriter handed to ManagerClass.InitOutput (mirrors ncc\codedom\NemerleCodeCompiler.n
    /// using a System.IO.StringWriter and ncc\main.n using Console.Out). Forwards complete
    /// lines to the caller-supplied onOutput callback instead of accumulating everything in
    /// memory or writing to a process-global Console -- MSBuild's Task.Log is the only
    /// sensible sink here, and Console.SetOut would affect the whole (shared, node-reused)
    /// MSBuild process, which ncc\20-inproc-task-plan.md explicitly rules out.
    ///
    /// All Write*/WriteLine overloads funnel through the same char-by-char buffer instead of
    /// relying on TextWriter's own cross-overload delegation (its default Write(string)
    /// implementation does not necessarily call an overridden Write(char)), so every code
    /// path -- including Console-style Write(string)/WriteLine(string) that ncc's own
    /// progress/status code might use -- is captured.
    /// </summary>
    internal sealed class LineForwardingWriter : TextWriter
    {
        private readonly Action<string> _onOutput;
        private readonly StringBuilder _buffer = new StringBuilder();

        public LineForwardingWriter(Action<string> onOutput)
        {
            _onOutput = onOutput;
        }

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value) => Append(value.ToString());
        public override void Write(string? value) => Append(value ?? "");
        public override void WriteLine(string? value) => Append((value ?? "") + "\n");
        public override void WriteLine() => Append("\n");

        /// <summary>Forwards any partial (unterminated) final line. Call once after the
        /// compile finishes.</summary>
        public void FlushPending()
        {
            if (_buffer.Length > 0)
            {
                _onOutput(_buffer.ToString());
                _buffer.Clear();
            }
        }

        private void Append(string s)
        {
            foreach (var c in s)
            {
                if (c == '\n')
                {
                    _onOutput(_buffer.ToString());
                    _buffer.Clear();
                }
                else if (c != '\r')
                {
                    _buffer.Append(c);
                }
            }
        }
    }
}
