using System.Xml;
using System.Xml.Linq;

namespace DotNetCoverageMcp.Helpers;

/// <summary>
/// Loads the XML files this server reads: coverage reports, TRX files, Blame sequences and
/// project files. Any of them may come from the repository, which SECURITY.md treats as
/// untrusted input. XDocument.Load expands a document's own entities by default, up to 10 million
/// characters, so a 700-byte coverage report could return a million-character class name to the
/// agent. External entities were never resolved. DOCTYPEs are ignored rather than prohibited,
/// so a report from a writer that emits one still loads.
/// </summary>
internal static class XmlFile
{
    private static readonly XmlReaderSettings Settings = new() { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };

    public static XDocument Load(string path)
    {
        using var reader = XmlReader.Create(path, Settings);
        return XDocument.Load(reader);
    }
}
