using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace AnyCam.Core.Transport;

/// <summary>
/// Minimal XML property list reader/writer — enough for usbmuxd messages.
/// Values map to: dict → Dictionary&lt;string, object&gt;, array → List&lt;object&gt;, string, long, double, bool, byte[], DateTime.
/// </summary>
public static class Plist
{
    public static string Serialize(IReadOnlyDictionary<string, object> root)
    {
        var builder = new StringBuilder();
        builder.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        builder.Append("<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n");
        builder.Append("<plist version=\"1.0\">\n");
        WriteValue(builder, root);
        builder.Append("</plist>\n");
        return builder.ToString();
    }

    private static void WriteValue(StringBuilder builder, object value)
    {
        switch (value)
        {
            case IReadOnlyDictionary<string, object> dict:
                builder.Append("<dict>");
                foreach (var (key, item) in dict)
                {
                    builder.Append("<key>").Append(Escape(key)).Append("</key>");
                    WriteValue(builder, item);
                }
                builder.Append("</dict>");
                break;
            case IEnumerable<object> list when value is not string:
                builder.Append("<array>");
                foreach (var item in list)
                    WriteValue(builder, item);
                builder.Append("</array>");
                break;
            case string text:
                builder.Append("<string>").Append(Escape(text)).Append("</string>");
                break;
            case bool flag:
                builder.Append(flag ? "<true/>" : "<false/>");
                break;
            case int or long or uint or ushort or short or byte:
                builder.Append("<integer>").Append(Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)).Append("</integer>");
                break;
            case double or float:
                builder.Append("<real>").Append(Convert.ToDouble(value, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture)).Append("</real>");
                break;
            case byte[] data:
                builder.Append("<data>").Append(Convert.ToBase64String(data)).Append("</data>");
                break;
            case DateTime date:
                builder.Append("<date>").Append(date.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)).Append("</date>");
                break;
            default:
                throw new NotSupportedException($"Cannot write {value.GetType()} to a plist");
        }
    }

    private static string Escape(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    public static object Parse(string xml)
    {
        var document = XDocument.Parse(xml, LoadOptions.None);
        var plist = document.Root ?? throw new FormatException("Empty plist");
        var first = plist.Name.LocalName == "plist" ? plist.Elements().FirstOrDefault() : plist;
        return first is null ? new Dictionary<string, object>() : ReadValue(first);
    }

    public static Dictionary<string, object> ParseDictionary(string xml) =>
        Parse(xml) as Dictionary<string, object> ?? throw new FormatException("Plist root is not a dictionary");

    private static object ReadValue(XElement element)
    {
        switch (element.Name.LocalName)
        {
            case "dict":
                var dict = new Dictionary<string, object>(StringComparer.Ordinal);
                string? key = null;
                foreach (var child in element.Elements())
                {
                    if (child.Name.LocalName == "key")
                        key = child.Value;
                    else if (key is not null)
                    {
                        dict[key] = ReadValue(child);
                        key = null;
                    }
                }
                return dict;
            case "array":
                return element.Elements().Select(ReadValue).ToList();
            case "string":
                return element.Value;
            case "integer":
                return long.Parse(element.Value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture);
            case "real":
                return double.Parse(element.Value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture);
            case "true":
                return true;
            case "false":
                return false;
            case "data":
                return Convert.FromBase64String(string.Concat(element.Value.Where(c => !char.IsWhiteSpace(c))));
            case "date":
                return DateTime.Parse(element.Value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
            default:
                return element.Value;
        }
    }
}
