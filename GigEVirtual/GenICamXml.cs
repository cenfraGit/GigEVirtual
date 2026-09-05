// --------------------------------------------------------------------------------
// GenICamXml.cs
//
// reads register declarations out of a genicam device description, so a device
// does not have to restate by hand what its own xml already says.
// --------------------------------------------------------------------------------

using System.Globalization;
using System.Xml.Linq;

namespace GigEVirtual;

internal record XmlRegister(uint Address, int Length, RegAccess Access, string Name);

internal static class GenICamXml
{
    // node types that describe a block of device memory. StructEntry is not one
    // of them: those are bit fields inside a StructReg and share its address.
    private static readonly string[] _registerNodes =
        ["IntReg", "StringReg", "FloatReg", "MaskedIntReg", "StructReg", "Register"];

    // every register the description places at a fixed address on the device
    // port, in address order. registers whose address depends on the current
    // value of another feature are left out: resolving those means evaluating
    // genicam expressions, and a device can declare the few it has by hand.
    public static List<XmlRegister> Registers(string xml)
    {
        XElement root = XDocument.Parse(xml).Root
            ?? throw new FormatException("device description has no root element");

        // index by Name so pAddress references can be followed
        Dictionary<string, XElement> byName = [];
        foreach (XElement element in root.Descendants())
        {
            string? name = element.Attribute("Name")?.Value;
            if (name is not null) byName.TryAdd(name, element);
        }

        var found = new Dictionary<uint, XmlRegister>();

        foreach (XElement element in root.Descendants())
        {
            if (!_registerNodes.Contains(element.Name.LocalName)) continue;

            // only the device port is reachable over GVCP. chunk data and event
            // ports are separate address spaces.
            if (Child(element, "pPort") != "Device") continue;

            if (!TryAddress(element, byName, out uint address)) continue;

            string? lengthText = Child(element, "Length");
            if (lengthText is null || !TryNumber(lengthText, out long length) || length <= 0) continue;

            // several features often describe the same register, one per field.
            // they agree on the address and length, so the first wins.
            if (found.ContainsKey(address)) continue;

            found[address] = new XmlRegister(
                address,
                (int)length,
                Child(element, "AccessMode") == "RO" ? RegAccess.ReadOnly : RegAccess.ReadWrite,
                element.Attribute("Name")?.Value ?? element.Attribute("Comment")?.Value ?? "(unnamed)");
        }

        return [.. found.Values.OrderBy(r => r.Address)];
    }

    // an address is the sum of every Address and pAddress the node carries, which
    // is how a description expresses base plus offset. returns false when any
    // part cannot be resolved to a constant.
    private static bool TryAddress(XElement element, Dictionary<string, XElement> byName, out uint address)
    {
        address = 0;
        long total = 0;
        bool any = false;

        foreach (XElement part in element.Elements())
        {
            string kind = part.Name.LocalName;

            if (kind == "Address")
            {
                if (!TryNumber(part.Value, out long literal)) return false;
                total += literal;
                any = true;
            }
            else if (kind == "pAddress")
            {
                if (!TryConstant(part.Value.Trim(), byName, out long resolved)) return false;
                total += resolved;
                any = true;
            }
        }

        if (!any || total < 0 || total > uint.MaxValue) return false;

        address = (uint)total;
        return true;
    }

    // a pAddress usually points at a plain <Integer><Value>. one carrying a
    // pIndex picks its value from a selector, so it is only knowable at runtime.
    private static bool TryConstant(string name, Dictionary<string, XElement> byName, out long value)
    {
        value = 0;

        if (!byName.TryGetValue(name, out XElement? node)) return false;
        if (node.Name.LocalName != "Integer") return false;
        if (node.Elements().Any(e => e.Name.LocalName == "pIndex")) return false;

        string? text = Child(node, "Value");
        return text is not null && TryNumber(text, out value);
    }

    private static string? Child(XElement element, string name) =>
        element.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim();

    private static bool TryNumber(string text, out long value)
    {
        text = text.Trim();

        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return long.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);

        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }
}