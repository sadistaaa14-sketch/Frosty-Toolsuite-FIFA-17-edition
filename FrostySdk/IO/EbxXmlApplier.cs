using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using FrostySdk.Attributes;
using FrostySdk.Ebx;
using FrostySdk.Managers;

namespace FrostySdk.IO
{
    /// <summary>
    /// What an xml import managed to apply. Anything the applier could not understand is reported
    /// rather than guessed at, so a partially understood file cannot silently corrupt an asset.
    /// </summary>
    public sealed class EbxXmlApplyResult
    {
        /// <summary>How many class elements were matched to an object in the asset.</summary>
        public int ObjectsApplied;

        /// <summary>How many properties were written.</summary>
        public int PropertiesApplied;

        /// <summary>How many elements were left untouched.</summary>
        public int Skipped;

        /// <summary>Human readable notes about what was skipped and why.</summary>
        public readonly List<string> Warnings = new List<string>();

        /// <summary>True when at least one object was matched, i.e. the file belongs to this asset.</summary>
        public bool RootMatched => ObjectsApplied > 0;

        /// <summary>Records a warning, keeping the list bounded so a huge asset cannot flood the log.</summary>
        internal void Warn(string message)
        {
            if (Warnings.Count < 100)
                Warnings.Add(message);
            else if (Warnings.Count == 100)
                Warnings.Add("... further warnings suppressed");
        }
    }

    /// <summary>
    /// Applies an xml dump (as written by <see cref="EbxXmlWriter"/>) back onto a live ebx asset.
    ///
    /// The asset itself acts as the template: objects are matched by class name and by the guid the
    /// writer emitted, and only the properties the file mentions are written. That way a file the
    /// applier only partly understands still produces a valid asset, fields whose xml text cannot be
    /// represented (Sha1, BoxedValueRef, ...) keep their existing values instead of being zeroed,
    /// and the asset's type, name and file guid never change.
    /// </summary>
    public static class EbxXmlApplier
    {
        private const BindingFlags PropertyBindingFlags = BindingFlags.Public | BindingFlags.Instance;
        private const BindingFlags GuidFieldBindingFlags = BindingFlags.NonPublic | BindingFlags.Instance;

        private sealed class Context
        {
            public AssetManager Am;
            public EbxXmlApplyResult Result;
            /// <summary>Every object of the asset, keyed by the guid text the writer printed for it.</summary>
            public Dictionary<string, object> ByGuid = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Applies <paramref name="xml"/> onto <paramref name="liveObjects"/> (an asset's object list).
        /// </summary>
        /// <param name="liveObjects">The objects of the asset that is being imported into.</param>
        /// <param name="xml">The parsed xml document.</param>
        /// <param name="am">
        /// The asset manager, used to resolve external pointer references back to their file guids.
        /// Passing null simply means external pointers are reported and skipped.
        /// </param>
        public static EbxXmlApplyResult Apply(IEnumerable<object> liveObjects, List<EbxXmlClassNode> xml, AssetManager am = null)
        {
            EbxXmlApplyResult result = new EbxXmlApplyResult();
            if (xml == null || xml.Count == 0)
            {
                result.Warn("the file contains no objects");
                return result;
            }

            Context context = new Context { Am = am, Result = result };

            Dictionary<string, object> byKey = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, object> byClass = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

            if (liveObjects != null)
            {
                foreach (object obj in liveObjects)
                {
                    if (obj == null)
                        continue;

                    string className = obj.GetType().Name;
                    string guid = GuidText(obj);

                    if (!string.IsNullOrEmpty(guid))
                    {
                        byKey[className + "|" + guid] = obj;
                        if (!context.ByGuid.ContainsKey(guid))
                            context.ByGuid[guid] = obj;
                    }

                    if (!byClass.ContainsKey(className))
                        byClass[className] = obj;
                }
            }

            foreach (EbxXmlClassNode node in xml)
            {
                if (node == null || string.IsNullOrEmpty(node.ClassName))
                    continue;

                object target = null;

                if (!string.IsNullOrEmpty(node.Guid))
                    byKey.TryGetValue(node.ClassName + "|" + node.Guid, out target);

                if (target == null)
                    byClass.TryGetValue(node.ClassName, out target);

                if (target == null)
                {
                    result.Skipped++;
                    result.Warn("no " + node.ClassName + " object in this asset (skipped)");
                    continue;
                }

                ApplyClass(target, node, context);
                result.ObjectsApplied++;
            }

            return result;
        }

        private static void ApplyClass(object target, EbxXmlClassNode node, Context context)
        {
            Type type = target.GetType();

            foreach (EbxXmlPropertyNode property in node.Properties)
            {
                if (property == null || string.IsNullOrEmpty(property.Name))
                    continue;

                PropertyInfo info = type.GetProperty(property.Name, PropertyBindingFlags);
                if (info == null || info.SetMethod == null || !info.CanWrite)
                {
                    context.Result.Skipped++;
                    continue;
                }

                if (info.GetCustomAttribute<IsTransientAttribute>() != null)
                {
                    // Transient fields are runtime only and are not written to xml.
                    context.Result.Skipped++;
                    continue;
                }

                try
                {
                    ApplyProperty(target, info, property, context);
                }
                catch (Exception ex)
                {
                    context.Result.Skipped++;
                    context.Result.Warn(type.Name + "." + property.Name + ": " + ex.Message);
                }
            }
        }

        private static void ApplyProperty(object target, PropertyInfo info, EbxXmlPropertyNode node, Context context)
        {
            Type propertyType = info.PropertyType;

            if (propertyType == typeof(PointerRef))
            {
                if (TryParsePointer(node, context, out PointerRef pointer))
                {
                    info.SetValue(target, pointer);
                    context.Result.PropertiesApplied++;
                }
                else
                {
                    context.Result.Skipped++;
                    context.Result.Warn(info.Name + ": could not resolve pointer '" + node.Value + "'");
                }
                return;
            }

            Type itemType = ListItemType(propertyType);
            if (itemType != null)
            {
                if (node.Members == null)
                {
                    context.Result.Skipped++;
                    return;
                }

                IList list = info.GetValue(target) as IList;
                if (list == null)
                {
                    list = (IList)Activator.CreateInstance(propertyType);
                    info.SetValue(target, list);
                }

                // Match the length the file asks for: extra entries are dropped, missing ones added.
                while (list.Count > node.Members.Count)
                    list.RemoveAt(list.Count - 1);

                for (int i = 0; i < node.Members.Count; i++)
                {
                    object element = i < list.Count ? list[i] : null;
                    if (!TryApplyNode(ref element, itemType, node.Members[i], context))
                    {
                        context.Result.Skipped++;
                        continue;
                    }

                    if (i < list.Count)
                        list[i] = element;
                    else
                        list.Add(element);
                }

                context.Result.PropertiesApplied++;
                return;
            }

            object value = info.GetValue(target);
            if (TryApplyNode(ref value, propertyType, node, context))
            {
                info.SetValue(target, value);
                context.Result.PropertiesApplied++;
            }
            else
            {
                context.Result.Skipped++;
            }
        }

        /// <summary>
        /// Applies one property value node. <paramref name="value"/> is the boxed current value and
        /// receives the new one, which is how nested structs are written back correctly.
        /// </summary>
        private static bool TryApplyNode(ref object value, Type type, EbxXmlPropertyNode node, Context context)
        {
            if (node == null)
                return false;

            if (node.IsClass)
            {
                object instance = value;
                if (instance == null)
                {
                    if (type.IsValueType)
                    {
                        instance = Activator.CreateInstance(type);
                    }
                    else if (type.GetConstructor(Type.EmptyTypes) != null)
                    {
                        instance = Activator.CreateInstance(type);
                    }
                }

                if (instance == null)
                {
                    context.Result.Warn(type.Name + ": cannot create an instance of this type (skipped)");
                    return false;
                }

                ApplyClass(instance, node.Class, context);
                value = instance;
                return true;
            }

            if (node.IsList)
            {
                // A list nested inside something that is not a list - nothing sensible to do.
                context.Result.Warn(node.Name + ": unexpected list (skipped)");
                return false;
            }

            object converted = ConvertLeaf(node.Value, type, context, node.Name);
            if (converted == null)
                return false;

            value = converted;
            return true;
        }

        /// <summary>
        /// Converts the text the writer emitted for a leaf back into the target type. This mirrors
        /// <c>EbxXmlWriter.FieldToXml</c>; returning null means "leave the property alone".
        /// </summary>
        private static object ConvertLeaf(string text, Type type, Context context, string name)
        {
            if (text == null)
                return null;

            if (type == typeof(string))
                return text;

            if (type == typeof(bool))
            {
                if (bool.TryParse(text.Trim(), out bool boolean))
                    return boolean;
                string trimmed = text.Trim();
                if (trimmed == "1") return true;
                if (trimmed == "0") return false;
                return null;
            }

            if (type.IsEnum)
            {
                string trimmed = text.Trim();
                try
                {
                    if (Enum.IsDefined(type, trimmed))
                        return Enum.Parse(type, trimmed);
                }
                catch
                {
                    // fall through to the numeric form below
                }

                object numeric = ParseInteger(trimmed, Enum.GetUnderlyingType(type));
                return numeric == null ? null : Enum.ToObject(type, numeric);
            }

            if (type == typeof(float))
                return float.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float single) ? (object)single : null;

            if (type == typeof(double))
                return double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double dbl) ? (object)dbl : null;

            if (type == typeof(Guid))
                return Guid.TryParse(text.Trim(), out Guid guid) ? (object)guid : null;

            if (type == typeof(CString))
                return new CString(text);

            if (type == typeof(ResourceRef))
            {
                string hex = text.Trim();
                return ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong rid) ? (object)new ResourceRef(rid) : null;
            }

            if (type == typeof(FileRef))
                return new FileRef(Unwrap(text, "FileRef"));

            if (type == typeof(TypeRef))
            {
                string inner = Unwrap(text, "TypeRef");
                if (inner == "(null)" || inner.Length == 0)
                    return new TypeRef("");
                return Guid.TryParse(inner, out Guid typeGuid) ? (object)new TypeRef(typeGuid) : new TypeRef(inner);
            }

            object integer = ParseInteger(text, type);
            if (integer != null)
                return integer;

            // Sha1, BoxedValueRef and anything else whose ToString is not reversible: the existing
            // value is kept and the field is reported so nothing is silently lost.
            context.Result.Warn(name + ": cannot read a " + type.Name + " value from '" + Shorten(text) + "' (left unchanged)");
            return null;
        }

        /// <summary>
        /// Parses an integer field. The writer prints uint/int fields as "name [XXXXXXXX]" and always
        /// includes the hexadecimal form, so that is preferred; a bare 0x form and plain decimal are
        /// also accepted. Signed targets are converted from the same bit pattern (unchecked), because
        /// the writer prints the raw bits.
        /// </summary>
        private static object ParseInteger(string text, Type type)
        {
            if (string.IsNullOrEmpty(text))
                return null;

            string value = text.Trim();

            int open = value.LastIndexOf('[');
            int close = value.LastIndexOf(']');
            if (open >= 0 && close > open)
                value = value.Substring(open + 1, close - open - 1).Trim();

            ulong bits;
            if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || value.StartsWith("0X", StringComparison.Ordinal))
            {
                if (!ulong.TryParse(value.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bits))
                    return null;
            }
            else if (!ulong.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out bits))
            {
                if (!ulong.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bits))
                    return null;
            }

            if (type == typeof(byte)) return (byte)bits;
            if (type == typeof(sbyte)) return unchecked((sbyte)(byte)bits);
            if (type == typeof(ushort)) return (ushort)bits;
            if (type == typeof(short)) return unchecked((short)(ushort)bits);
            if (type == typeof(uint)) return (uint)bits;
            if (type == typeof(int)) return unchecked((int)(uint)bits);
            if (type == typeof(ulong)) return bits;
            if (type == typeof(long)) return unchecked((long)bits);

            return null;
        }

        /// <summary>
        /// Reads the three pointer forms the writer emits: "nullptr", "[Ebx] name [class guid]" for a
        /// reference to another asset, and "[Class] guid" for a reference inside this asset.
        /// </summary>
        private static bool TryParsePointer(EbxXmlPropertyNode node, Context context, out PointerRef pointer)
        {
            pointer = new PointerRef(null);

            string text = (node.Value ?? "").Trim();
            if (text.Length == 0 || text.Equals("nullptr", StringComparison.OrdinalIgnoreCase))
                return true;

            if (text.StartsWith("[Ebx]", StringComparison.OrdinalIgnoreCase))
            {
                int lastOpen = text.LastIndexOf('[');
                if (lastOpen <= 0)
                    return false;

                string name = text.Substring(4, lastOpen - 4).Trim();
                string guidText = text.Substring(lastOpen + 1).TrimEnd(']').Trim();

                if (context.Am == null || !Guid.TryParse(guidText, out Guid classGuid))
                    return false;

                EbxAssetEntry entry = context.Am.GetEbxEntry(name);
                if (entry == null)
                    return false;

                // An entry's Guid is the file guid of the asset it describes.
                pointer = new PointerRef(new EbxImportReference { FileGuid = entry.Guid, ClassGuid = classGuid });
                return true;
            }

            if (text.StartsWith("[", StringComparison.Ordinal))
            {
                int close = text.IndexOf(']');
                if (close <= 1)
                    return false;

                string guidText = text.Substring(close + 1).Trim();
                if (!Guid.TryParse(guidText, out Guid internalGuid))
                    return false;

                if (context.ByGuid.TryGetValue(guidText, out object referenced))
                {
                    pointer = new PointerRef(referenced);
                    return true;
                }

                // An internal pointer to an object the live asset does not have (for example a
                // sub-object the dump was taken from a different revision of). Report, do not guess.
                return false;
            }

            return false;
        }

        private static Type ListItemType(Type type)
        {
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
                return type.GetGenericArguments()[0];
            return null;
        }

        private static string GuidText(object obj)
        {
            FieldInfo field = obj.GetType().GetField("__Guid", GuidFieldBindingFlags);
            if (field == null)
                return null;

            try
            {
                return field.GetValue(obj) is AssetClassGuid guid ? guid.ToString() : null;
            }
            catch
            {
                return null;
            }
        }

        private static string Unwrap(string text, string wrapper)
        {
            string value = (text ?? "").Trim();
            string prefix = wrapper + " '";
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && value.EndsWith("'", StringComparison.Ordinal))
                return value.Substring(prefix.Length, value.Length - prefix.Length - 1);
            return value;
        }

        private static string Shorten(string text)
        {
            string value = text ?? "";
            return value.Length <= 40 ? value : value.Substring(0, 40) + "...";
        }
    }
}
