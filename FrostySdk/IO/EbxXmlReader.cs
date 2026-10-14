using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;

namespace FrostySdk.IO
{
    /// <summary>
    /// One property (field) of an ebx class as it appears in the xml written by <see cref="EbxXmlWriter"/>.
    /// A property is exactly one of: a leaf holding text, a list of members, or a single nested class.
    /// </summary>
    public sealed class EbxXmlPropertyNode
    {
        /// <summary>The element name, e.g. "BoneNames", "right" or "member".</summary>
        public string Name;

        /// <summary>The Count attribute, or -1 when the element does not carry one.</summary>
        public int Count = -1;

        /// <summary>The Index attribute, or -1 when the element does not carry one.</summary>
        public int Index = -1;

        /// <summary>
        /// The trimmed text of a leaf element, "" for an explicitly empty leaf, and null when the
        /// element is a list or holds a nested class.
        /// </summary>
        public string Value;

        /// <summary>The member entries of a list, in Index order, or null when the element is not a list.</summary>
        public List<EbxXmlPropertyNode> Members;

        /// <summary>The nested class when the element holds one, otherwise null.</summary>
        public EbxXmlClassNode Class;

        /// <summary>True when the element carries member entries (a list, possibly empty).</summary>
        public bool IsList => Members != null;

        /// <summary>True when the element holds a single nested class.</summary>
        public bool IsClass => Class != null;

        /// <summary>True when the element is a plain value: no members and no nested class.</summary>
        public bool IsLeaf => Value != null && Members == null && Class == null;
    }

    /// <summary>
    /// A class element from an ebx xml document: its class name, the instance guid from its Guid
    /// attribute and its properties in document order.
    /// </summary>
    public sealed class EbxXmlClassNode
    {
        /// <summary>The element name, which is the ebx class name, e.g. "SkeletonAsset".</summary>
        public string ClassName;

        /// <summary>The raw text of the Guid attribute, or "" when the element does not carry one.</summary>
        public string Guid = "";

        /// <summary>The direct properties of the class, in document order.</summary>
        public List<EbxXmlPropertyNode> Properties = new List<EbxXmlPropertyNode>();

        /// <summary>
        /// Returns the first property with the given name, matching exactly first and then ignoring
        /// case, or null when the class has no such property.
        /// </summary>
        public EbxXmlPropertyNode Find(string name)
        {
            if (string.IsNullOrEmpty(name) || Properties == null)
                return null;

            EbxXmlPropertyNode exact = Properties.FirstOrDefault(p => p.Name == name);
            if (exact != null)
                return exact;

            return Properties.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// Reads the ebx-as-xml form produced by <see cref="EbxXmlWriter"/> back into a plain object tree.
    ///
    /// Only the structure is interpreted here - element names, the Count/Index/Guid attributes and
    /// leaf text. Turning the tree back into typed ebx objects is a separate step, so this parser can
    /// be exercised without a loaded game.
    ///
    /// The writer does not escape the values it writes (it appends them straight into a string
    /// builder), so a value that happens to contain an unescaped '&amp;' or '&lt;' produces technically
    /// malformed xml. Parsing therefore runs twice over: a fast pass with <see cref="XmlReader"/>, and
    /// if that throws, a tolerant character scan that reads whatever is actually there instead of
    /// failing the whole file.
    /// </summary>
    public static class EbxXmlReader
    {
        /// <summary>
        /// Parses a whole document. The result holds one entry per top level class element.
        /// </summary>
        public static List<EbxXmlClassNode> Parse(string xmlText)
        {
            List<EbxXmlClassNode> classes = new List<EbxXmlClassNode>();
            if (string.IsNullOrEmpty(xmlText))
                return classes;

            List<RawElement> roots;
            try
            {
                roots = ReadWithXmlReader(xmlText);
            }
            catch (XmlException)
            {
                // Unescaped characters in a value: fall back to reading the text directly.
                roots = ReadTolerantly(xmlText);
            }

            foreach (RawElement root in roots)
                classes.Add(ToClass(root));

            return classes;
        }

        /// <summary>
        /// Parses a file written by <see cref="EbxXmlWriter"/>.
        /// </summary>
        public static List<EbxXmlClassNode> ParseFile(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return new List<EbxXmlClassNode>();

            return Parse(File.ReadAllText(path));
        }

        private static XmlReaderSettings CreateSettings()
        {
            return new XmlReaderSettings
            {
                // The writer emits a fragment (no declaration, possibly several top level elements).
                ConformanceLevel = ConformanceLevel.Fragment,
                DtdProcessing = DtdProcessing.Ignore,
                ValidationType = ValidationType.None,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
                IgnoreWhitespace = false,
                CheckCharacters = false
            };
        }

        private static List<RawElement> ReadWithXmlReader(string xmlText)
        {
            List<RawElement> roots = new List<RawElement>();
            using (StringReader stringReader = new StringReader(xmlText))
            using (XmlReader reader = XmlReader.Create(stringReader, CreateSettings()))
            {
                while (reader.Read())
                {
                    if (reader.NodeType == XmlNodeType.Element)
                    {
                        RawElement element = ReadRawElement(reader);
                        if (element != null)
                            roots.Add(element);
                    }
                }
            }
            return roots;
        }

        private static RawElement ReadRawElement(XmlReader reader)
        {
            RawElement element = new RawElement { Name = reader.LocalName };
            element.Attributes = ReadAttributes(reader);

            if (reader.IsEmptyElement)
                return element;

            int depth = reader.Depth;
            StringBuilder text = new StringBuilder();

            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth)
                    break;

                switch (reader.NodeType)
                {
                    case XmlNodeType.Text:
                    case XmlNodeType.CDATA:
                    case XmlNodeType.Whitespace:
                    case XmlNodeType.SignificantWhitespace:
                        text.Append(reader.Value);
                        break;

                    case XmlNodeType.Element:
                        RawElement child = ReadRawElement(reader);
                        if (child != null)
                            element.Children.Add(child);
                        break;

                    case XmlNodeType.EntityReference:
                        text.Append(reader.Name);
                        break;
                }
            }

            element.Text = text.ToString();
            return element;
        }

        private static Dictionary<string, string> ReadAttributes(XmlReader reader)
        {
            Dictionary<string, string> attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (reader.MoveToFirstAttribute())
            {
                do
                {
                    attributes[reader.LocalName] = reader.Value;
                }
                while (reader.MoveToNextAttribute());
                reader.MoveToElement();
            }
            return attributes;
        }

        #region -- tolerant scan (used only when XmlReader rejects the document) --

        private static List<RawElement> ReadTolerantly(string xmlText)
        {
            TolerantScanner scanner = new TolerantScanner(xmlText);
            List<RawElement> roots = new List<RawElement>();

            while (true)
            {
                RawElement element = scanner.ReadElement();
                if (element == null)
                    break;
                roots.Add(element);
            }

            return roots;
        }

        /// <summary>
        /// A small scanner for exactly the grammar the writer emits. It never throws: anything it
        /// cannot make sense of is skipped, and text is kept verbatim.
        /// </summary>
        private sealed class TolerantScanner
        {
            private readonly string text;
            private int position;

            public TolerantScanner(string inText)
            {
                text = inText ?? "";
            }

            /// <summary>Reads the next element, or returns null at the end of the document.</summary>
            public RawElement ReadElement()
            {
                while (true)
                {
                    int open = text.IndexOf('<', position);
                    if (open < 0)
                        return null;

                    position = open;
                    if (open + 1 >= text.Length)
                        return null;

                    char next = text[open + 1];
                    if (next == '!' || next == '?')
                    {
                        // Comments, doctypes and declarations carry nothing we need.
                        int skip = text.IndexOf('>', open + 1);
                        if (skip < 0)
                            return null;
                        position = skip + 1;
                        continue;
                    }

                    if (next == '/')
                    {
                        // A stray close tag: skip it and keep looking.
                        int close = text.IndexOf('>', open + 1);
                        if (close < 0)
                            return null;
                        position = close + 1;
                        continue;
                    }

                    RawElement element = ReadElementAt();
                    return element;
                }
            }

            private RawElement ReadElementAt()
            {
                int nameStart = position + 1;
                int nameEnd = nameStart;
                while (nameEnd < text.Length && !IsNameTerminator(text[nameEnd]))
                    nameEnd++;

                string name = text.Substring(nameStart, nameEnd - nameStart).Trim();
                if (name.Length == 0)
                {
                    position = nameEnd + 1;
                    return null;
                }

                int tagEnd = FindTagEnd(nameEnd);
                if (tagEnd < 0)
                {
                    position = text.Length;
                    return null;
                }

                bool selfClosing = text[tagEnd - 1] == '/';
                RawElement element = new RawElement { Name = name };
                element.Attributes = ParseAttributes(nameEnd, selfClosing ? tagEnd - 1 : tagEnd);
                position = tagEnd + 1;

                if (selfClosing)
                    return element;

                StringBuilder content = new StringBuilder();
                while (position < text.Length)
                {
                    int open = text.IndexOf('<', position);
                    if (open < 0)
                    {
                        content.Append(text.Substring(position));
                        position = text.Length;
                        break;
                    }

                    content.Append(text.Substring(position, open - position));

                    if (open + 1 >= text.Length)
                    {
                        position = text.Length;
                        break;
                    }

                    if (text[open + 1] == '/')
                    {
                        int close = text.IndexOf('>', open);
                        if (close < 0)
                        {
                            position = text.Length;
                            break;
                        }
                        position = close + 1;
                        break;
                    }

                    if (text[open + 1] == '!' || text[open + 1] == '?')
                    {
                        int skip = text.IndexOf('>', open + 1);
                        if (skip < 0)
                        {
                            position = text.Length;
                            break;
                        }
                        position = skip + 1;
                        continue;
                    }

                    position = open;
                    int beforeChild = position;
                    RawElement child = ReadElementAt();
                    if (child == null)
                    {
                        // Never spin: if the scan did not move forward, step past this '<'.
                        if (position <= beforeChild)
                            position = open + 1;

                        if (position >= text.Length)
                            break;
                        continue;
                    }

                    element.Children.Add(child);
                }

                element.Text = content.ToString();
                return element;
            }

            /// <summary>Finds the '>' that closes the tag that starts before <paramref name="from"/>.</summary>
            private int FindTagEnd(int from)
            {
                char quote = '\0';
                for (int i = from; i < text.Length; i++)
                {
                    char c = text[i];
                    if (quote != '\0')
                    {
                        if (c == quote)
                            quote = '\0';
                        continue;
                    }

                    if (c == '"' || c == '\'')
                    {
                        quote = c;
                        continue;
                    }

                    if (c == '>')
                        return i;
                }
                return -1;
            }

            private Dictionary<string, string> ParseAttributes(int from, int to)
            {
                Dictionary<string, string> attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                int i = from;
                while (i < to)
                {
                    while (i < to && char.IsWhiteSpace(text[i]))
                        i++;
                    int nameStart = i;
                    while (i < to && !IsNameTerminator(text[i]) && text[i] != '=' && !char.IsWhiteSpace(text[i]))
                        i++;

                    if (i >= to)
                        break;

                    string attributeName = text.Substring(nameStart, i - nameStart);
                    while (i < to && char.IsWhiteSpace(text[i]))
                        i++;

                    if (i >= to || text[i] != '=')
                        continue;

                    i++;
                    while (i < to && char.IsWhiteSpace(text[i]))
                        i++;

                    if (i >= to)
                        break;

                    char quote = text[i];
                    if (quote != '"' && quote != '\'')
                        continue;

                    i++;
                    int valueStart = i;
                    while (i < to && text[i] != quote)
                        i++;

                    attributes[attributeName] = text.Substring(valueStart, i - valueStart);
                    if (i < to)
                        i++;
                }
                return attributes;
            }

            private static bool IsNameTerminator(char c)
            {
                return c == '>' || c == '/' || c == '<' || char.IsWhiteSpace(c);
            }
        }

        #endregion

        #region -- shared conversion --

        private static EbxXmlClassNode ToClass(RawElement raw)
        {
            EbxXmlClassNode node = new EbxXmlClassNode
            {
                ClassName = raw.Name,
                Guid = GetAttribute(raw, "Guid")
            };

            foreach (RawElement child in raw.Children)
                node.Properties.Add(ToProperty(child));

            return node;
        }

        private static EbxXmlPropertyNode ToProperty(RawElement raw)
        {
            EbxXmlPropertyNode node = new EbxXmlPropertyNode
            {
                Name = raw.Name,
                Count = ParseInt(GetAttribute(raw, "Count"), -1),
                Index = ParseInt(GetAttribute(raw, "Index"), -1)
            };

            List<RawElement> members = raw.Children.Where(c => c.Name == "member").ToList();

            if (members.Count > 0)
            {
                // A list: its members are the interesting children, anything else is ignored.
                node.Members = members.Select(ToProperty).ToList();
                SortMembers(node.Members);
                return node;
            }

            if (node.Count >= 0)
            {
                // An empty list, written as <Foo Count="0"></Foo>.
                node.Members = new List<EbxXmlPropertyNode>();
                return node;
            }

            if (raw.Children.Count > 0)
            {
                // A nested struct or object.
                node.Class = ToClass(raw.Children[0]);
                return node;
            }

            node.Value = raw.Text == null ? "" : raw.Text.Trim();
            return node;
        }

        private static void SortMembers(List<EbxXmlPropertyNode> members)
        {
            if (members == null || members.Count < 2)
                return;

            // OrderBy is a stable sort, so members without an Index keep the order they were written in.
            List<EbxXmlPropertyNode> ordered = members
                .OrderBy(m => m.Index < 0 ? int.MaxValue : m.Index)
                .ToList();

            members.Clear();
            members.AddRange(ordered);
        }

        private static string GetAttribute(RawElement element, string name)
        {
            if (element == null || element.Attributes == null)
                return "";

            string value;
            return element.Attributes.TryGetValue(name, out value) ? value : "";
        }

        private static int ParseInt(string value, int fallback)
        {
            if (string.IsNullOrEmpty(value))
                return fallback;

            int result;
            return int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result) ? result : fallback;
        }

        /// <summary>A node of the raw element tree both parsers produce.</summary>
        private sealed class RawElement
        {
            public string Name;
            public Dictionary<string, string> Attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public string Text = "";
            public List<RawElement> Children = new List<RawElement>();
        }

        #endregion
    }
}
