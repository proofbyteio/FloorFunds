using System.Globalization;
using System.Text;

namespace Catalog.Transport;

/// <summary>Compact JSON writer with a fixed insertion order. It never invents a numeric zero for an absent version.</summary>
internal sealed class JsonText
{
    private readonly StringBuilder _text = new();
    private readonly Stack<bool> _comma = new();

    public string Text => _text.ToString();

    public void BeginObject()
    {
        _text.Append('{');
        _comma.Push(false);
    }

    public void EndObject()
    {
        _text.Append('}');
        _comma.Pop();
    }

    public void BeginArray()
    {
        _text.Append('[');
        _comma.Push(false);
    }

    public void EndArray()
    {
        _text.Append(']');
        _comma.Pop();
    }

    public void Property(string name, string value)
    {
        Name(name);
        String(value);
    }

    public void Property(string name, long value)
    {
        Name(name);
        Number(value);
    }

    public void Property(string name, bool value)
    {
        Name(name);
        Boolean(value);
    }

    public void Name(string name)
    {
        Separator();
        String(name);
        _text.Append(':');
    }

    public void Item() => Separator();

    public void String(string value)
    {
        _text.Append('"');
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '"':
                    _text.Append("\\\"");
                    break;
                case '\\':
                    _text.Append("\\\\");
                    break;
                case '\b':
                    _text.Append("\\b");
                    break;
                case '\f':
                    _text.Append("\\f");
                    break;
                case '\n':
                    _text.Append("\\n");
                    break;
                case '\r':
                    _text.Append("\\r");
                    break;
                case '\t':
                    _text.Append("\\t");
                    break;
                case '<' or '>' or '&':
                    _text.Append("\\u");
                    _text.Append(((int)ch).ToString("X4", CultureInfo.InvariantCulture));
                    break;
                default:
                    if (char.IsControl(ch))
                    {
                        _text.Append("\\u");
                        _text.Append(((int)ch).ToString("X4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        _text.Append(ch);
                    }
                    break;
            }
        }
        _text.Append('"');
    }

    public void Number(long value) => _text.Append(value.ToString(CultureInfo.InvariantCulture));

    public void Boolean(bool value) => _text.Append(value ? "true" : "false");

    private void Separator()
    {
        if (_comma.Peek())
            _text.Append(',');
        else
        {
            _comma.Pop();
            _comma.Push(true);
        }
    }
}
