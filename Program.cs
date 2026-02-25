// Program.cs
// FNT <-> XML converter
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;

internal static class Program
{
    private const int HeaderSizeBytes = 12;
    private const int GlyphSizeBytes = 16;

    private static readonly XNamespace XsiNs = "http://www.w3.org/2001/XMLSchema-instance";
    private static readonly XNamespace XsdNs = "http://www.w3.org/2001/XMLSchema";

    private static int Main(string[] args)
    {
        bool hadError = false;

        if (args == null || args.Length == 0)
        {
            PrintUsage();
            PauseIfInteractive();
            return 2;
        }

        bool noPause = args.Any(a => a.Equals("--nopause", StringComparison.OrdinalIgnoreCase));
        List<string> inputs = ExpandInputs(args.Where(a => !a.StartsWith("-", StringComparison.Ordinal)).ToArray());

        if (inputs.Count == 0)
        {
            Console.Error.WriteLine("No .fnt or .xml files found in the provided inputs.");
            if (!noPause) PauseIfInteractive();
            return 2;
        }

        foreach (string path in inputs)
        {
            try
            {
                string ext = Path.GetExtension(path).ToLowerInvariant();

                if (ext == ".fnt")
                {
                    Console.WriteLine("[+] FNT -> XML: " + path);
                    FntModel model = ParseFnt(path);

                    XDocument xml = ToArabiaStyleXml(model);
                    string outXml = Path.ChangeExtension(path, ".xml");
                    xml.Save(outXml);

                    Console.WriteLine("    -> Wrote: " + outXml);
                    if (model.TrailingBytes != null && model.TrailingBytes.Length > 0)
                        Console.WriteLine("    (Note) Preserved trailing bytes: " + model.TrailingBytes.Length);
                }
                else if (ext == ".xml")
                {
                    Console.WriteLine("[+] XML -> FNT: " + path);
                    FntModel model = ParseArabiaStyleXml(path);

                    string outFnt = Path.ChangeExtension(path, ".fnt");
                    WriteFnt(outFnt, model);

                    Console.WriteLine("    -> Wrote: " + outFnt);
                    if (model.TrailingBytes != null && model.TrailingBytes.Length > 0)
                        Console.WriteLine("    (Note) Wrote trailing bytes: " + model.TrailingBytes.Length);
                }
                else
                {
                    Console.WriteLine("[-] Skipped (unsupported extension): " + path);
                }
            }
            catch (Exception ex)
            {
                hadError = true;
                Console.Error.WriteLine("[!] Failed: " + path);
                Console.Error.WriteLine("    " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        if (hadError && !noPause)
            PauseIfInteractive();

        return hadError ? 1 : 0;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("FNT <-> XML converter (arabia.xml-like format)");
        Console.WriteLine();
        Console.WriteLine("Drag & drop .fnt (exports .xml) or .xml (rebuilds .fnt) onto the exe.");
        Console.WriteLine("Options: --nopause");
        Console.WriteLine();
    }

    private static void PauseIfInteractive()
    {
        if (Console.IsOutputRedirected || Console.IsErrorRedirected) return;
        Console.WriteLine();
        Console.Write("Press any key to exit...");
        Console.ReadKey(true);
        Console.WriteLine();
    }

    private static List<string> ExpandInputs(string[] rawArgs)
    {
        List<string> results = new List<string>();

        foreach (string a in rawArgs)
        {
            if (string.IsNullOrWhiteSpace(a)) continue;
            string path = a.Trim('"');

            if (File.Exists(path))
            {
                string ext = Path.GetExtension(path).ToLowerInvariant();
                if (ext == ".fnt" || ext == ".xml")
                    results.Add(Path.GetFullPath(path));
                continue;
            }

            if (Directory.Exists(path))
            {
                results.AddRange(Directory.GetFiles(path, "*.fnt", SearchOption.TopDirectoryOnly).Select(Path.GetFullPath));
                results.AddRange(Directory.GetFiles(path, "*.xml", SearchOption.TopDirectoryOnly).Select(Path.GetFullPath));
                continue;
            }

            if (path.Contains('*') || path.Contains('?'))
            {
                string dir = Path.GetDirectoryName(path);
                if (string.IsNullOrEmpty(dir)) dir = Directory.GetCurrentDirectory();
                string pattern = Path.GetFileName(path);

                if (Directory.Exists(dir))
                {
                    results.AddRange(
                        Directory.GetFiles(dir, pattern, SearchOption.TopDirectoryOnly)
                            .Where(p =>
                            {
                                string ext = Path.GetExtension(p).ToLowerInvariant();
                                return ext == ".fnt" || ext == ".xml";
                            })
                            .Select(Path.GetFullPath));
                }
            }
        }

        return results.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ================================
    // FNT PARSE / WRITE
    // ================================

    private static FntModel ParseFnt(string path)
    {
        FileInfo fileInfo = new FileInfo(path);
        if (fileInfo.Length < HeaderSizeBytes)
            throw new InvalidDataException("File too small (" + fileInfo.Length + " bytes).");

        using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (BinaryReader br = new BinaryReader(fs, Encoding.UTF8, true))
        {
            FntModel m = new FntModel();
            m.SourcePath = Path.GetFullPath(path);
            m.FileSizeBytes = fileInfo.Length;

            m.UnknownHeader1 = br.ReadUInt16();
            m.GlyphWidth1 = br.ReadUInt16();
            m.GlyphWidth2 = br.ReadUInt16();
            m.GlyphHeight1 = br.ReadUInt16();
            m.GlyphHeight2 = br.ReadUInt16();
            m.GlyphCount = br.ReadUInt16();

            long expectedMin = HeaderSizeBytes + (long)m.GlyphCount * GlyphSizeBytes;
            if (fileInfo.Length < expectedMin)
            {
                throw new InvalidDataException(
                    "Truncated or format mismatch. glyph_count=" + m.GlyphCount +
                    " requires at least " + expectedMin + " bytes, file is " + fileInfo.Length + ".");
            }

            m.Glyphs = new List<FntGlyph>(m.GlyphCount);

            for (int i = 0; i < m.GlyphCount; i++)
            {
                FntGlyph g = new FntGlyph();
                g.CharacterCodeUnit = br.ReadUInt16();
                g.Unknown1 = br.ReadInt16();
                g.YOffset = br.ReadUInt16();
                g.XOffset = br.ReadUInt16();
                g.Unknown2 = br.ReadInt16();
                g.Unknown3 = br.ReadInt16();
                g.UnknownPadding1 = br.ReadInt16();
                g.UnknownPadding2 = br.ReadInt16();
                m.Glyphs.Add(g);
            }

            int remaining = (int)(fs.Length - fs.Position);
            m.TrailingBytes = remaining > 0 ? br.ReadBytes(remaining) : new byte[0];
            return m;
        }
    }

    private static void WriteFnt(string outPath, FntModel m)
    {
        if (m == null) throw new ArgumentNullException("m");
        if (m.Glyphs == null) m.Glyphs = new List<FntGlyph>();

        m.GlyphCount = (ushort)m.Glyphs.Count;

        using (FileStream fs = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None))
        using (BinaryWriter bw = new BinaryWriter(fs, Encoding.UTF8, true))
        {
            bw.Write(m.UnknownHeader1);
            bw.Write(m.GlyphWidth1);
            bw.Write(m.GlyphWidth2);
            bw.Write(m.GlyphHeight1);
            bw.Write(m.GlyphHeight2);
            bw.Write(m.GlyphCount);

            for (int i = 0; i < m.Glyphs.Count; i++)
            {
                FntGlyph g = m.Glyphs[i];

                bw.Write(g.CharacterCodeUnit);
                bw.Write(g.Unknown1);
                bw.Write(g.YOffset);
                bw.Write(g.XOffset);
                bw.Write(g.Unknown2);
                bw.Write(g.Unknown3);
                bw.Write(g.UnknownPadding1);
                bw.Write(g.UnknownPadding2);
            }

            if (m.TrailingBytes != null && m.TrailingBytes.Length > 0)
                bw.Write(m.TrailingBytes);
        }
    }

    // ================================
    // XML WRITE
    // ================================

    private static XDocument ToArabiaStyleXml(FntModel m)
    {
        if (m.Glyphs == null) m.Glyphs = new List<FntGlyph>();
        m.GlyphCount = (ushort)m.Glyphs.Count;

        XElement glyphMapEntry =
            new XElement("FfntEntry",
                new XAttribute(XsiNs + "type", "GlyphMap"),
                new XElement("Header",
                    new XAttribute("Unknown1", m.UnknownHeader1),
                    new XAttribute("GlyphWidth1", m.GlyphWidth1),
                    new XAttribute("GlyphWidth2", m.GlyphWidth2),
                    new XAttribute("GlyphHeight1", m.GlyphHeight1),
                    new XAttribute("GlyphHeight2", m.GlyphHeight2),
                    new XAttribute("GlyphCount", m.GlyphCount)
                ),
                new XElement("Glyphs",
                    m.Glyphs.Select(g =>
                        new XElement("Glyph",
                            new XAttribute("Character", SafeDisplayFromCodeUnit(g.CharacterCodeUnit)),
                            new XAttribute("XOffset", g.XOffset),
                            new XAttribute("YOffset", g.YOffset),
                            new XAttribute("Unknown1", g.Unknown1),
                            new XAttribute("Unknown2", g.Unknown2),
                            new XAttribute("Unknown3", g.Unknown3),
                            new XAttribute("UnknownPadding1", g.UnknownPadding1),
                            new XAttribute("UnknownPadding2", g.UnknownPadding2)
                        )
                    )
                ),
                new XElement("TrailingBytesBase64",
                    (m.TrailingBytes != null && m.TrailingBytes.Length > 0)
                        ? Convert.ToBase64String(m.TrailingBytes)
                        : ""
                )
            );

        XElement root =
            new XElement("FfntFile",
                new XAttribute(XNamespace.Xmlns + "xsi", XsiNs),
                new XAttribute(XNamespace.Xmlns + "xsd", XsdNs),
                new XElement("Entries",
                    glyphMapEntry
                )
            );

        return new XDocument(new XDeclaration("1.0", "utf-8", "yes"), root);
    }

    // ================================
    // XML PARSE
    // ================================

    private static FntModel ParseArabiaStyleXml(string xmlPath)
    {
        XDocument doc = XDocument.Load(xmlPath);
        XElement root = doc.Root;
        if (root == null || root.Name.LocalName != "FfntFile")
            throw new InvalidDataException("Invalid XML: expected <FfntFile> root.");

        XElement entries = root.Element("Entries");
        if (entries == null) throw new InvalidDataException("Invalid XML: missing <Entries>.");

        XElement glyphMap = entries.Elements("FfntEntry")
            .FirstOrDefault(e => ((string)e.Attribute(XsiNs + "type")) == "GlyphMap");

        if (glyphMap == null)
            throw new InvalidDataException("Invalid XML: missing <FfntEntry xsi:type=\"GlyphMap\">.");

        XElement headerEl = glyphMap.Element("Header");
        if (headerEl == null) throw new InvalidDataException("Invalid XML: GlyphMap missing <Header>.");

        FntModel m = new FntModel();
        m.SourcePath = Path.GetFullPath(xmlPath);

        m.UnknownHeader1 = ReadU16Attr(headerEl, "Unknown1");
        m.GlyphWidth1 = ReadU16Attr(headerEl, "GlyphWidth1");
        m.GlyphWidth2 = ReadU16Attr(headerEl, "GlyphWidth2");
        m.GlyphHeight1 = ReadU16Attr(headerEl, "GlyphHeight1");
        m.GlyphHeight2 = ReadU16Attr(headerEl, "GlyphHeight2");

        XElement glyphsEl = glyphMap.Element("Glyphs");
        if (glyphsEl == null) throw new InvalidDataException("Invalid XML: GlyphMap missing <Glyphs>.");

        List<FntGlyph> glyphs = new List<FntGlyph>();

        foreach (XElement gEl in glyphsEl.Elements("Glyph"))
        {
            XAttribute charAttr = gEl.Attribute("Character");
            if (charAttr == null)
                throw new InvalidDataException("Glyph missing Character attribute.");

            string charText = charAttr.Value; // keep as-is (space allowed)
            if (charText.Length == 0)
                throw new InvalidDataException("Glyph Character is empty.");

            ushort codeUnit;
            if (!TryParseCharacterToCodeUnit(charText, out codeUnit))
                throw new InvalidDataException("Invalid Glyph Character: '" + charText + "'.");

            FntGlyph g = new FntGlyph();
            g.CharacterCodeUnit = codeUnit;

            g.XOffset = ReadU16Attr(gEl, "XOffset");
            g.YOffset = ReadU16Attr(gEl, "YOffset");

            g.Unknown1 = ReadI16Attr(gEl, "Unknown1");
            g.Unknown2 = ReadI16Attr(gEl, "Unknown2");
            g.Unknown3 = ReadI16Attr(gEl, "Unknown3");
            g.UnknownPadding1 = ReadI16Attr(gEl, "UnknownPadding1");
            g.UnknownPadding2 = ReadI16Attr(gEl, "UnknownPadding2");

            glyphs.Add(g);
        }

        m.Glyphs = glyphs;
        m.GlyphCount = (ushort)glyphs.Count;

        XElement trailingEl = glyphMap.Element("TrailingBytesBase64");
        if (trailingEl != null)
        {
            string b64 = (trailingEl.Value ?? "").Trim();
            m.TrailingBytes = b64.Length > 0 ? Convert.FromBase64String(b64) : new byte[0];
        }
        else
        {
            m.TrailingBytes = new byte[0];
        }

        return m;
    }

    // ================================
    // Character parsing
    // ================================

    private static bool TryParseCharacterToCodeUnit(string s, out ushort code)
    {
        code = 0;
        if (s == null) return false;

        if (s.Length == 1)
        {
            code = (ushort)s[0];
            return !IsSurrogate(code);
        }

        string t = s.Trim();

        if (t == "\\t") { code = 0x0009; return true; }
        if (t == "\\n") { code = 0x000A; return true; }
        if (t == "\\r") { code = 0x000D; return true; }

        // \uXXXX
        if (t.Length == 6 && t.StartsWith("\\u", StringComparison.OrdinalIgnoreCase))
        {
            string hex = t.Substring(2);
            if (IsHex4(hex))
            {
                code = Convert.ToUInt16(hex, 16);
                return !IsSurrogate(code);
            }
        }

        // U+XXXX
        if (t.Length == 6 && t.StartsWith("U+", StringComparison.OrdinalIgnoreCase))
        {
            string hex = t.Substring(2);
            if (IsHex4(hex))
            {
                code = Convert.ToUInt16(hex, 16);
                return !IsSurrogate(code);
            }
        }

        // 0xXXXX
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            string hex = t.Substring(2);
            if (hex.Length > 0 && hex.Length <= 4 && hex.All(IsHexChar))
            {
                code = Convert.ToUInt16(hex, 16);
                return !IsSurrogate(code);
            }
        }

        // decimal
        ushort dec;
        if (ushort.TryParse(t, out dec))
        {
            code = dec;
            return !IsSurrogate(code);
        }

        return false;
    }

    private static string SafeDisplayFromCodeUnit(ushort codeUnit)
    {
        if (IsSurrogate(codeUnit))
            return "\\u" + codeUnit.ToString("X4");

        char c = (char)codeUnit;

        if (char.IsControl(c))
        {
            if (codeUnit == 0x0009) return "\\t";
            if (codeUnit == 0x000A) return "\\n";
            if (codeUnit == 0x000D) return "\\r";
            return "\\u" + codeUnit.ToString("X4");
        }

        return c.ToString();
    }

    private static bool IsSurrogate(ushort codeUnit) => codeUnit >= 0xD800 && codeUnit <= 0xDFFF;

    private static bool IsHex4(string s) => s.Length == 4 && s.All(IsHexChar);

    private static bool IsHexChar(char c) =>
        (c >= '0' && c <= '9') ||
        (c >= 'a' && c <= 'f') ||
        (c >= 'A' && c <= 'F');

    private static ushort ReadU16Attr(XElement el, string attrName)
    {
        XAttribute a = el.Attribute(attrName);
        if (a == null) throw new InvalidDataException("Missing attribute " + attrName + ".");
        return Convert.ToUInt16(a.Value.Trim());
    }

    private static short ReadI16Attr(XElement el, string attrName)
    {
        XAttribute a = el.Attribute(attrName);
        if (a == null) throw new InvalidDataException("Missing attribute " + attrName + ".");
        return Convert.ToInt16(a.Value.Trim());
    }

    private sealed class FntModel
    {
        public string SourcePath = "";
        public long FileSizeBytes;

        // Header
        public ushort UnknownHeader1;
        public ushort GlyphWidth1;
        public ushort GlyphWidth2;
        public ushort GlyphHeight1;
        public ushort GlyphHeight2;
        public ushort GlyphCount;

        public List<FntGlyph> Glyphs = new List<FntGlyph>();
        public byte[] TrailingBytes = Array.Empty<byte>();
    }

    private sealed class FntGlyph
    {
        public ushort CharacterCodeUnit;
        public short Unknown1;
        public ushort YOffset;
        public ushort XOffset;
        public short Unknown2;
        public short Unknown3;
        public short UnknownPadding1;
        public short UnknownPadding2;
    }
}