// --------------------------------------------------------------------------------
// GenICamXml.cs
//
// reads register declarations out of a genicam device description, so a device
// does not have to restate by hand what its own xml already says.
// --------------------------------------------------------------------------------

using System.Globalization;
using System.Xml.Linq;

namespace GigEVirtual;

internal record XmlRegister(uint Address, int Length, RegAccess Access, Endianness Endianness, string Name);

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

            string? lengthText = Child(element, "Length");
            if (lengthText is null || !TryNumber(lengthText, out long length) || length <= 0) continue;

            RegAccess access = Child(element, "AccessMode") == "RO"
                ? RegAccess.ReadOnly
                : RegAccess.ReadWrite;

            // note the schema's spelling. left out means little-endian, which is
            // the genicam default and what device registers overwhelmingly use.
            // the big-endian bootstrap registers are ones we define ourselves, so
            // this only ever applies to device space.
            Endianness endianness = Child(element, "Endianess") == "BigEndian"
                ? Endianness.Big
                : Endianness.Little;

            string name = element.Attribute("Name")?.Value
                ?? element.Attribute("Comment")?.Value ?? "(unnamed)";

            foreach (uint address in Addresses(element, byName))
            {
                // several features often describe the same register, one per
                // field. they agree on the address and length, so the first wins.
                if (found.ContainsKey(address)) continue;

                found[address] = new XmlRegister(address, (int)length, access, endianness, name);
            }
        }

        return [.. found.Values.OrderBy(r => r.Address)];
    }

    // every address this register can sit at. an address is the sum of the node's
    // Address and pAddress parts, which is how a description expresses base plus
    // offset. a part that selects between listed options multiplies the result:
    // whichever the selector ends up pointing at, we want the register to exist
    // there, and that beats evaluating the selector at runtime.
    private static IEnumerable<uint> Addresses(XElement element, Dictionary<string, XElement> byName)
    {
        List<long> totals = [0];
        bool any = false;

        foreach (XElement part in element.Elements())
        {
            string kind = part.Name.LocalName;
            List<long> options;

            if (kind == "Address")
            {
                if (!TryNumber(part.Value, out long literal)) return [];
                options = [literal];
            }
            else if (kind == "pAddress")
            {
                options = Options(part.Value.Trim(), byName);
                if (options.Count == 0) return [];
            }
            else continue;

            any = true;
            totals = [.. totals.SelectMany(_ => options, (running, option) => running + option)];
        }

        if (!any) return [];

        return totals.Where(t => t >= 0 && t <= uint.MaxValue).Select(t => (uint)t).Distinct();
    }

    // what a pAddress can resolve to. a plain <Integer><Value> gives one answer.
    // one carrying a pIndex picks from ValueIndexed entries at runtime, so every
    // entry is a possibility. anything else, such as a formula, gives none.
    private static List<long> Options(string name, Dictionary<string, XElement> byName)
    {
        if (!byName.TryGetValue(name, out XElement? node)) return [];
        if (node.Name.LocalName != "Integer") return [];

        if (node.Elements().Any(e => e.Name.LocalName == "pIndex"))
        {
            List<long> indexed = [];

            foreach (XElement option in node.Elements()
                .Where(e => e.Name.LocalName is "ValueIndexed" or "ValueDefault"))
            {
                if (TryNumber(option.Value, out long value)) indexed.Add(value);
            }

            return indexed;
        }

        string? text = Child(node, "Value");
        return text is not null && TryNumber(text, out long constant) ? [constant] : [];
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