using System.Text;

namespace Nemerle.ProjectInfo;

/// <summary>
/// One parameter of one overload, as the IDE engine describes it
/// (<c>MethodTipInfo.GetParameterInfo(index, parameter)</c> returns the triple
/// <c>(name, display, doc)</c>).  <see cref="Display"/> is already rendered in
/// Nemerle notation - <c>name : type</c> - by the engine.
/// </summary>
public sealed record NemerleTipParameter(string Name, string Display, string? Documentation);

/// <summary>
/// One overload of the call the caret sits in: <c>GetName</c> / <c>GetType</c>
/// (the return type) / <c>GetDescription</c> (the XmlDoc summary, empty for
/// members declared in source) plus its parameters.
/// </summary>
public sealed record NemerleTipSignature(
    string Name,
    string ReturnType,
    string? Documentation,
    IReadOnlyList<NemerleTipParameter> Parameters);

/// <summary>
/// The engine's whole method tip, flattened into plain data.  The server fills
/// this in on the engine's worker thread; everything below is a pure function of
/// it.  <see cref="DefaultSignature"/> mirrors <c>MethodTipInfo.DefaultMethod</c>
/// and <see cref="ParameterIndex"/> mirrors <c>MethodTipInfo.ParameterIndex</c>.
/// </summary>
public sealed record NemerleMethodTip(
    IReadOnlyList<NemerleTipSignature> Signatures,
    int DefaultSignature,
    int ParameterIndex);

/// <summary>
/// An LSP <c>ParameterInformation</c>: the parameter's own label text plus the
/// half-open <c>[Start, End)</c> range of UTF-16 code units it occupies inside
/// the owning signature's label.  The offsets are what make an editor highlight
/// the right parameter; see <see cref="SignatureHelpMapping"/> for why they are
/// preferred over the string form.
/// </summary>
public sealed record NemerleSignatureParameterLabel(
    string Label,
    int Start,
    int End,
    string? Documentation);

/// <summary>An LSP <c>SignatureInformation</c>: the composed label and its parameters.</summary>
public sealed record NemerleSignatureLabel(
    string Label,
    IReadOnlyList<NemerleSignatureParameterLabel> Parameters,
    string? Documentation);

/// <summary>
/// An LSP <c>SignatureHelp</c>.  <see cref="ActiveSignature"/> and
/// <see cref="ActiveParameter"/> are already normalized: the signature index is
/// always a valid index into <see cref="Signatures"/>, the parameter index is
/// never negative (but may point past the last parameter - see
/// <see cref="SignatureHelpMapping"/>).
/// </summary>
public sealed record NemerleSignatureHelp(
    IReadOnlyList<NemerleSignatureLabel> Signatures,
    int ActiveSignature,
    int ActiveParameter);

/// <summary>
/// Turns the IDE engine's method tip into LSP-shaped signature help.  Pure and
/// free of any engine or OmniSharp dependency, like <see cref="HoverMarkup"/> /
/// <see cref="CompletionMapping"/> / <see cref="SemanticTokenMapping"/>, so the
/// composition rules below are pinned by unit tests instead of only by an
/// end-to-end scenario (WP-P1).
///
/// <para><b>How the label is composed.</b>  The engine has no rendered signature
/// string - it exposes the pieces (name, return type, and one
/// <c>name : type</c> display per parameter).  The label is therefore built here
/// as <c>Name(p0, p1) : ReturnType</c>, i.e. Nemerle's own declaration notation,
/// which is what hover already shows for the same member.  A member with no
/// parameters renders as <c>Name() : ReturnType</c>, and a member whose return
/// type the engine could not name drops the <c>" : "</c> tail rather than
/// printing an empty type.</para>
///
/// <para><b>Why explicit offsets instead of substring matching.</b>  LSP lets a
/// <c>ParameterInformation.label</c> be either a string that the client locates
/// inside the signature label, or an explicit <c>[start, end)</c> pair.  The
/// string form is ambiguous exactly where signature help matters most: two
/// parameters of the same type (<c>Add(a : int, b : int) : int</c>) share no
/// text, but a call like <c>Fold(f : int -&gt; int, x : int) : int</c> contains
/// <c>int</c> several times, and a client that searches for the first match
/// bolds the wrong span.  Since the label is composed here, the exact offsets
/// are known for free while building it, so they are always emitted; the server
/// falls back to the string form only for a client that did not advertise
/// <c>labelOffsetSupport</c>.</para>
///
/// <para><b>Normalization.</b>  XmlDoc summaries arrive with the source file's
/// line breaks and indentation still in them (the engine's
/// <c>XmlDocReader</c> concatenates the text nodes and trims only the ends).
/// Every string is therefore whitespace-collapsed to a single line: a signature
/// label must be one line to be usable in a popup, and a multi-line label would
/// also make the parameter offsets point into text the editor renders
/// differently.</para>
/// </summary>
public static class SignatureHelpMapping
{
    /// <summary>
    /// Maps the engine tip to LSP signature help, or null when there is nothing
    /// to show (no tip, or a tip with no overloads).  Null is deliberate: an
    /// empty <c>SignatureHelp</c> container makes VS Code keep an empty popup
    /// open, while a null response closes it.
    /// </summary>
    public static NemerleSignatureHelp? ToSignatureHelp(NemerleMethodTip? tip)
    {
        if (tip is null || tip.Signatures.Count == 0)
            return null;

        var signatures = new List<NemerleSignatureLabel>(tip.Signatures.Count);
        foreach (var signature in tip.Signatures)
            signatures.Add(ToSignatureLabel(signature));

        // MethodTipInfo.DefaultMethod is the index of the overload the engine
        // resolved the call to; it stays -1 when the resolution was ambiguous
        // (Project.Type.n uses List.FindIndex, whose miss value is -1, and
        // OverloadsMethodTipInfo's guard does not filter it out).  An
        // out-of-range activeSignature is a protocol error, so fall back to the
        // first overload - the list is sorted by parameter count, so that is
        // the simplest arity, which is also the least surprising default.
        var activeSignature = tip.DefaultSignature;
        if (activeSignature < 0 || activeSignature >= signatures.Count)
            activeSignature = 0;

        // ParameterIndex counts the commas typed so far.  It is deliberately
        // NOT clamped to the active overload's parameter count: LSP says an
        // out-of-range activeParameter highlights nothing, which is the right
        // rendering for a call that already has more arguments than the
        // overload takes.  Only a negative value (which no engine path
        // produces, but which would be a protocol error) is corrected.
        var activeParameter = Math.Max(0, tip.ParameterIndex);

        return new NemerleSignatureHelp(signatures, activeSignature, activeParameter);
    }

    private static NemerleSignatureLabel ToSignatureLabel(NemerleTipSignature signature)
    {
        var builder = new StringBuilder();
        builder.Append(Collapse(signature.Name));
        builder.Append('(');

        var parameters = new List<NemerleSignatureParameterLabel>(signature.Parameters.Count);
        for (var index = 0; index < signature.Parameters.Count; index++)
        {
            if (index > 0)
                builder.Append(", ");

            var parameter = signature.Parameters[index];
            // The engine's display ("name : type") is the useful form; fall back
            // to the bare name, and finally to a placeholder, so the offsets
            // below always cover a non-empty span.
            var display = Collapse(parameter.Display);
            if (display.Length == 0)
                display = Collapse(parameter.Name);
            if (display.Length == 0)
                display = "_";

            var start = builder.Length;
            builder.Append(display);
            parameters.Add(new NemerleSignatureParameterLabel(
                display,
                start,
                builder.Length,
                Documentation(parameter.Documentation)));
        }

        builder.Append(')');

        var returnType = Collapse(signature.ReturnType);
        if (returnType.Length > 0)
            builder.Append(" : ").Append(returnType);

        return new NemerleSignatureLabel(
            builder.ToString(),
            parameters,
            Documentation(signature.Documentation));
    }

    private static string? Documentation(string? text)
    {
        var collapsed = Collapse(text);
        return collapsed.Length == 0 ? null : collapsed;
    }

    /// <summary>
    /// Trims and collapses every run of whitespace (including the line breaks
    /// and indentation an XmlDoc summary carries over from its source file) into
    /// a single space.
    /// </summary>
    private static string Collapse(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var builder = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }
}
