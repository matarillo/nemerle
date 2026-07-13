using Nemerle.Builtins;
using Nemerle.Compiler;
using Nemerle.Compiler.Parsetree;
using Nemerle.Completion2;

using TupleIntInt = Nemerle.Builtins.Tuple<int, int>;
using TupleStringIntInt = Nemerle.Builtins.Tuple<string, int, int>;

namespace Nemerle.LanguageServer.Engine;

/// <summary>
/// Thread-safe IIdeSource backed only by the LSP document buffer.  All public
/// line/column coordinates follow the engine's 1-based contract; offsets and
/// .NET string indexing are UTF-16 code units, matching LSP.
/// </summary>
internal sealed class InMemoryNemerleSource : IIdeSource
{
    private readonly object _gate = new();
    private string _text;
    private int _version;
    private CompileUnit? _compileUnit;
    private TopDeclaration[] _topDeclarations = [];
    private IList<RegionInfo> _regions = [];

    public InMemoryNemerleSource(string uri, string path, string text, int version)
    {
        Uri = uri;
        Path = path;
        FileIndex = Location.GetFileIndex(path);
        _text = text;
        _version = version;
    }

    public string Uri { get; }
    public string Path { get; }
    public int FileIndex { get; }

    public CompileUnit CompileUnit
    {
        get { lock (_gate) return _compileUnit!; }
        set { lock (_gate) _compileUnit = value; }
    }

    public int CurrentVersion
    {
        get { lock (_gate) return _version; }
    }

    public int LineCount
    {
        get
        {
            lock (_gate)
            {
                var count = 1;
                for (var i = 0; i < _text.Length; i++)
                {
                    if (_text[i] == '\r')
                    {
                        if (i + 1 < _text.Length && _text[i + 1] == '\n')
                            i++;
                        count++;
                    }
                    else if (_text[i] == '\n')
                    {
                        count++;
                    }
                }

                return count;
            }
        }
    }

    public List<RelocationRequest> RelocationRequestsQueue { get; } = [];

    public void Update(string text, int version)
    {
        lock (_gate)
        {
            _text = text;
            _version = version;
        }
    }

    public string GetText()
    {
        lock (_gate) return _text;
    }

    public TupleStringIntInt GetTextCurrentVersionAndFileIndex()
    {
        lock (_gate) return new TupleStringIntInt(_text, _version, FileIndex);
    }

    public string GetRegion(int lineStart, int colStart, int lineEnd, int colEnd)
    {
        lock (_gate)
        {
            var start = GetOffset(_text, lineStart, colStart);
            var end = GetOffset(_text, lineEnd, colEnd);
            if (end < start)
                throw new ArgumentOutOfRangeException(nameof(lineEnd));
            return _text.Substring(start, end - start);
        }
    }

    public string GetRegion(Location location) =>
        GetRegion(location.Line, location.Column, location.EndLine, location.EndColumn);

    public string GetLine(int line)
    {
        lock (_gate)
        {
            var start = GetOffset(_text, line, 1);
            var end = start;
            while (end < _text.Length && _text[end] is not ('\r' or '\n'))
                end++;
            return _text.Substring(start, end - start);
        }
    }

    public int GetPositionOfLineIndex(int line, int col)
    {
        lock (_gate) return GetOffset(_text, line, col);
    }

    public TupleIntInt GetLineIndexOfPosition(int pos)
    {
        lock (_gate)
        {
            if (pos < 0 || pos > _text.Length)
                throw new ArgumentOutOfRangeException(nameof(pos));

            var line = 1;
            var col = 1;
            for (var i = 0; i < pos; i++)
            {
                if (_text[i] == '\r')
                {
                    if (i + 1 < pos && _text[i + 1] == '\n')
                        i++;
                    line++;
                    col = 1;
                }
                else if (_text[i] == '\n')
                {
                    line++;
                    col = 1;
                }
                else
                {
                    col++;
                }
            }

            return new TupleIntInt(line, col);
        }
    }

    public void SetRegions(IList<RegionInfo> regions, int sourceVersion)
    {
        lock (_gate)
        {
            if (sourceVersion == _version)
                _regions = regions;
        }
    }

    public void SetTopDeclarations(TopDeclaration[] topDeclarations)
    {
        lock (_gate) _topDeclarations = topDeclarations;
    }

    public void LockWrite() => Monitor.Enter(_gate);
    public void UnlockWrite() => Monitor.Exit(_gate);
    public void LockReadWrite() => Monitor.Enter(_gate);
    public void UnlocReadkWrite() => Monitor.Exit(_gate);

    public override string ToString() => $"InMemoryNemerleSource: {Path}";

    private static int GetOffset(string text, int targetLine, int targetColumn)
    {
        if (targetLine < 1)
            throw new ArgumentOutOfRangeException(nameof(targetLine));
        if (targetColumn < 1)
            throw new ArgumentOutOfRangeException(nameof(targetColumn));

        var line = 1;
        var offset = 0;
        while (line < targetLine)
        {
            if (offset >= text.Length)
                throw new ArgumentOutOfRangeException(nameof(targetLine));

            if (text[offset] == '\r')
            {
                offset++;
                if (offset < text.Length && text[offset] == '\n')
                    offset++;
                line++;
            }
            else if (text[offset] == '\n')
            {
                offset++;
                line++;
            }
            else
            {
                offset++;
            }
        }

        var result = offset + targetColumn - 1;
        if (result > text.Length)
            throw new ArgumentOutOfRangeException(nameof(targetColumn));

        for (var i = offset; i < result; i++)
        {
            if (text[i] is '\r' or '\n')
                throw new ArgumentOutOfRangeException(nameof(targetColumn));
        }

        return result;
    }
}
