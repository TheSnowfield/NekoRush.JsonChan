﻿using System.Text;
using NekoRush.JsonChan.Exceptions;
using NekoRush.JsonChan.Utils;

// ReSharper disable RedundantAssignment
// ReSharper disable MemberCanBePrivate.Global

namespace NekoRush.JsonChan;

internal class JsonParser
{
    [Flags]
    private enum ParseStatus
    {
        ExceptObjStart = 1, /*    {  */
        ExceptObjEnd = 2, /*      }  */
        ExceptArrayStart = 4, /*  [  */
        ExceptArrayEnd = 8, /*    ]  */
        ExceptComma = 16, /*      ,  */
        ExceptQuote = 32, /*      "  */
        ExceptColon = 64 /*       :  */
    }

    private readonly Stream _jsonStream;
    private readonly DynamicValue _dynamicValue;

    public JsonParser(Stream json)
    {
        _jsonStream = json;
        _jsonStream.Seek(0, SeekOrigin.Begin);

        _dynamicValue = new();
    }

    public JsonParser(IEnumerable<byte> json)
        : this(new MemoryStream(json.ToArray()))
    {
    }

    public JsonParser(string json)
        : this(Encoding.UTF8.GetBytes(json))
    {
    }

    public dynamic Parse()
    {
        // Asserts the stream can read
        if (!_jsonStream.CanRead)
            throw new InvalidJsonException("Invalid JSON: Stream cannot read.");

        var currExcept = ParseStatus.ExceptObjStart | ParseStatus.ExceptArrayStart;
        var currString = string.Empty;
        var needValue = false;

        do
        {
            // Read the token
            var currChar = _jsonStream.ReadByte();
            switch (currChar)
            {
                case < 0:
                    goto ret;

                // Ignore all white characters
                case ' ':
                    continue;

                // Push object context
                case '{':
                    AssertExcepts(currExcept, ParseStatus.ExceptObjStart);
                    _dynamicValue.PushContext(currString);
                    needValue = false;
                    currExcept = ParseStatus.ExceptQuote | ParseStatus.ExceptArrayStart | ParseStatus.ExceptObjStart | ParseStatus.ExceptObjEnd;
                    break;

                // Pop object context
                case '}':
                    AssertExcepts(currExcept, ParseStatus.ExceptObjEnd);

                    // Pop context 
                    _dynamicValue.PopContext();
                    currExcept = ParseStatus.ExceptComma | ParseStatus.ExceptArrayEnd | ParseStatus.ExceptObjEnd;
                    break;

                case '[':
                    AssertExcepts(currExcept, ParseStatus.ExceptArrayStart);
                    _dynamicValue.PushArrayContext(currString);
                    needValue = false;
                    currExcept = ParseStatus.ExceptQuote | ParseStatus.ExceptArrayStart | ParseStatus.ExceptObjStart | ParseStatus.ExceptArrayEnd;
                    break;

                case ']':
                    AssertExcepts(currExcept, ParseStatus.ExceptArrayEnd);

                    // Pop context 
                    _dynamicValue.PopContext();
                    currExcept = ParseStatus.ExceptComma | ParseStatus.ExceptArrayEnd | ParseStatus.ExceptObjEnd;
                    break;

                // Parse the next field
                case ',':
                    AssertExcepts(currExcept, ParseStatus.ExceptComma);
                    currExcept = ParseStatus.ExceptQuote | ParseStatus.ExceptArrayStart | ParseStatus.ExceptObjStart;
                    break;

                // Parse field value
                case ':':
                    AssertExcepts(currExcept, ParseStatus.ExceptColon);
                    needValue = true;
                    currExcept = ParseStatus.ExceptQuote | ParseStatus.ExceptArrayStart | ParseStatus.ExceptObjStart;
                    break;

                // Parse strings
                case '"':
                    AssertExcepts(currExcept, ParseStatus.ExceptQuote);

                    if (needValue)
                    {
                        needValue = false;
                        _dynamicValue.PutValue(currString, ParseString());
                    }
                    else if (_dynamicValue.IsArrayContext())
                    {
                        _dynamicValue.PutArrayValue(ParseString());
                    }
                    else currString = ParseString();

                    currExcept = ParseStatus.ExceptComma | ParseStatus.ExceptArrayEnd | ParseStatus.ExceptObjEnd | ParseStatus.ExceptColon;
                    break;

                // Parse Numbers
                case '-':
                case >= '0' and <= '9':
                    _jsonStream.Seek(-1, SeekOrigin.Current);

                    if (needValue)
                    {
                        needValue = false;
                        _dynamicValue.PutValue(currString, ParseNumber());
                    }
                    else if (_dynamicValue.IsArrayContext())
                    {
                        _dynamicValue.PutArrayValue(ParseNumber());
                    }
                    else currString = ParseString();

                    currExcept = ParseStatus.ExceptComma | ParseStatus.ExceptArrayEnd | ParseStatus.ExceptObjEnd;
                    break;

                // Parse boolean
                case 't':
                case 'f':

                    if (needValue)
                    {
                        needValue = false;
                        _dynamicValue.PutValue(currString, ParseBoolean());
                    }
                    else if (_dynamicValue.IsArrayContext())
                    {
                        _dynamicValue.PutArrayValue(ParseBoolean());
                    }

                    currExcept = ParseStatus.ExceptComma | ParseStatus.ExceptArrayEnd | ParseStatus.ExceptObjEnd;

                    break;

                case 'n':

                    // Compare remain characters (null)
                    if (ReadAndCompare(3, "ull"))
                    {
                        if (needValue)
                        {
                            needValue = false;
                            _dynamicValue.PutValue(currString, null!);
                        }
                        else if (_dynamicValue.IsArrayContext())
                        {
                            _dynamicValue.PutArrayValue(null!);
                        }
                    }

                    // Throw the exception
                    else
                    {
                        throw new InvalidJsonException(_jsonStream.Position,
                            $"Unexpected token in JSON at position {_jsonStream.Position}.");
                    }

                    currExcept = ParseStatus.ExceptComma | ParseStatus.ExceptArrayEnd | ParseStatus.ExceptObjEnd;
                    break;
            }
        } while (_jsonStream.Length > 0);

        ret:
        return _dynamicValue.Value;
    }

    private dynamic ParseNumber()
    {
        var list = new List<char>();
        var charBuf = 0;
        var dotCount = 0;
        var subCount = 0;

        do
        {
            charBuf = _jsonStream.ReadByte();

            switch (charBuf)
            {
                // EOF
                case -1: break;

                case '.':
                    if (list.Count == 0 || dotCount > 1)
                        throw new InvalidJsonException("");
                    list.Add('.');
                    dotCount++;
                    break;

                case '-':
                    if (subCount > 1)
                        throw new InvalidJsonException("");
                    list.Add('-');
                    subCount++;
                    break;

                case >= '0' and <= '9':
                    list.Add((char) charBuf);
                    break;

                default:
                    var str = new string(list.ToArray());
                    _jsonStream.Seek(-1, SeekOrigin.Current);

                    if (subCount > 0 && !str.StartsWith('-'))
                        throw new InvalidJsonException("");

                    // Parse number 
                    if (dotCount == 0 && subCount == 0) return ulong.Parse(str);
                    if (dotCount == 0 && subCount != 0) return long.Parse(str);
                    return double.Parse(str);
            }
        } while (charBuf != -1);

        throw new InvalidJsonException(_jsonStream.Position,
            $"Unexpected EOF in JSON at position {_jsonStream.Position}.");
    }

    private bool ParseBoolean()
    {
        if (ReadAndCompare(3, "rue")) return true;
        if (ReadAndCompare(4, "alse")) return false;

        throw new InvalidJsonException(_jsonStream.Position,
            $"Unexpected bool in JSON at position {_jsonStream.Position}.");
    }

    private string ParseString()
    {
        var list = new List<byte>();
        var charBuf = 0;

        do
        {
            charBuf = _jsonStream.ReadByte();

            switch (charBuf)
            {
                // EOF
                case -1: break;

                case '\\':
                    switch (_jsonStream.ReadByte())
                    {
                        case '"':
                            list.Add((byte) '"');
                            break;

                        case '\\':
                            list.Add((byte) '\\');
                            break;
                    }

                    break;

                case '"':
                    return Encoding.UTF8.GetString(list.ToArray());

                default:
                    list.Add((byte) charBuf);
                    break;
            }
        } while (charBuf != -1);

        throw new InvalidJsonException(_jsonStream.Position,
            $"Unexpected EOF in JSON at position {_jsonStream.Position}.");
    }

    private bool ReadAndCompare(int length, string chars)
    {
        if (_jsonStream.Length < length)
            throw new InvalidJsonException("");

        var charBuf = new byte[length];
        _jsonStream.Read(charBuf, 0, length);

        var result = chars == Encoding.UTF8.GetString(charBuf);
        if (!result) _jsonStream.Seek(-length, SeekOrigin.Current);
        return result;
    }

    private void AssertExcepts(ParseStatus current, ParseStatus excepted)
    {
        if ((current & excepted) == 0)
        {
            throw new InvalidJsonException(_jsonStream.Position,
                $"Unexpected token in JSON at position {_jsonStream.Position}.");
        }

        // assert ok
    }
}