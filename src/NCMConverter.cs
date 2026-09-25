// =============================================================================
//  NCM Converter - Windows Edition
//  网易云音乐 NCM 格式解密转换工具
//
//  纯 C# / .NET Framework 4.x 实现，不依赖 Python、不需要 pip 安装任何东西，
//  只用 Windows 自带的 .NET Framework 运行时即可运行。
//
//  编译（Windows 自带 csc.exe，见 build.bat）：
//    csc /target:winexe /out:NCMConverter.exe NCMConverter.cs
//
//  算法参考: taurusxin/ncmdump (C++)、allenfrostline/pyNCMDUMP (Python)
//  注意: 为兼容 .NET Framework 内置的旧版 csc.exe，本项目代码仅使用 C# 5.0 语法。
// =============================================================================

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace NCMConverter
{
    // =========================================================================
    //  Mini JSON 解析器
    //  .NET Framework 里可用的 JavascriptSerializer / DataContractJsonSerializer
    //  需要额外引用 System.Web.Extensions 或 System.Runtime.Serialization，
    //  为了让 build.bat 保持零依赖，这里自己实现一个够用的小解析器。
    // =========================================================================
    internal sealed class MiniJson
    {
        private readonly string _s;
        private int _i;

        private MiniJson(string text)
        {
            _s = text;
            _i = 0;
        }

        public static object Parse(string text)
        {
            if (string.IsNullOrEmpty(text))
                throw new NcmException("JSON 为空");
            MiniJson p = new MiniJson(text);
            p.SkipWs();
            object value = p.ParseValue();
            return value;
        }

        private void SkipWs()
        {
            while (_i < _s.Length)
            {
                char c = _s[_i];
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n')
                    _i++;
                else
                    break;
            }
        }

        private object ParseValue()
        {
            SkipWs();
            if (_i >= _s.Length)
                throw new NcmException("JSON 意外结束");

            char c = _s[_i];
            switch (c)
            {
                case '{': return ParseObject();
                case '[': return ParseArray();
                case '"': return ParseString();
                case 't': ReadWord("true"); return true;
                case 'f': ReadWord("false"); return false;
                case 'n': ReadWord("null"); return null;
                case '-':
                case '+':
                case '.':
                    return ParseNumber();
                default:
                    if (c >= '0' && c <= '9')
                        return ParseNumber();
                    throw new NcmException("JSON 非法字符 '" + c + "' @ " + _i);
            }
        }

        private void ReadWord(string word)
        {
            if (_i + word.Length > _s.Length || string.CompareOrdinal(_s, _i, word, 0, word.Length) != 0)
                throw new NcmException("JSON 关键字解析失败: " + word);
            _i += word.Length;
        }

        private Dictionary<string, object> ParseObject()
        {
            Dictionary<string, object> obj = new Dictionary<string, object>(StringComparer.Ordinal);
            _i++; // '{'
            SkipWs();
            if (_i < _s.Length && _s[_i] == '}')
            {
                _i++;
                return obj;
            }
            while (true)
            {
                SkipWs();
                if (_i >= _s.Length)
                    throw new NcmException("JSON 对象未闭合");
                if (_s[_i] != '"')
                    throw new NcmException("JSON 对象键缺少引号 @ " + _i);
                string key = ParseString();
                SkipWs();
                if (_i >= _s.Length || _s[_i] != ':')
                    throw new NcmException("JSON 缺少 ':' @ " + _i);
                _i++;
                obj[key] = ParseValue();
                SkipWs();
                if (_i >= _s.Length)
                    throw new NcmException("JSON 对象未闭合");
                if (_s[_i] == ',')
                {
                    _i++;
                    continue;
                }
                if (_s[_i] == '}')
                {
                    _i++;
                    break;
                }
                throw new NcmException("JSON 对象非法字符 '" + _s[_i] + "' @ " + _i);
            }
            return obj;
        }

        private List<object> ParseArray()
        {
            List<object> arr = new List<object>();
            _i++; // '['
            SkipWs();
            if (_i < _s.Length && _s[_i] == ']')
            {
                _i++;
                return arr;
            }
            while (true)
            {
                arr.Add(ParseValue());
                SkipWs();
                if (_i >= _s.Length)
                    throw new NcmException("JSON 数组未闭合");
                if (_s[_i] == ',')
                {
                    _i++;
                    continue;
                }
                if (_s[_i] == ']')
                {
                    _i++;
                    break;
                }
                throw new NcmException("JSON 数组非法字符 '" + _s[_i] + "' @ " + _i);
            }
            return arr;
        }

        private string ParseString()
        {
            _i++; // '"'
            StringBuilder sb = new StringBuilder();
            while (true)
            {
                if (_i >= _s.Length)
                    throw new NcmException("JSON 字符串未闭合");
                char c = _s[_i];
                if (c == '"')
                {
                    _i++;
                    break;
                }
                if (c == '\\')
                {
                    _i++;
                    if (_i >= _s.Length)
                        throw new NcmException("JSON 转义序列不完整");
                    char e = _s[_i];
                    _i++;
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (_i + 4 > _s.Length)
                                throw new NcmException("JSON \\u 转义不完整");
                            sb.Append((char)Convert.ToInt32(_s.Substring(_i, 4), 16));
                            _i += 4;
                            break;
                        default:
                            sb.Append(e);
                            break;
                    }
                    continue;
                }
                sb.Append(c);
                _i++;
            }
            return sb.ToString();
        }

        private object ParseNumber()
        {
            int start = _i;
            if (_i < _s.Length && (_s[_i] == '-' || _s[_i] == '+'))
                _i++;
            while (_i < _s.Length && (_s[_i] == '.' || (_s[_i] >= '0' && _s[_i] <= '9')
                                      || _s[_i] == 'e' || _s[_i] == 'E' || _s[_i] == '-' || _s[_i] == '+'))
                _i++;
            string num = _s.Substring(start, _i - start);
            double d;
            if (double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                return d;
            throw new NcmException("JSON 数字解析失败: " + num);
        }
    }

    // =========================================================================
    //  异常 & 数据结构
    // =========================================================================

    internal class NcmException : Exception
    {
        public NcmException(string message) : base(message) { }
    }

    internal sealed class NcmInfo
    {
        public byte[] Rc4Key;
        public string MusicName = "";
        public string ArtistName = "";
        public string AlbumName = "";
        public string MetaFormat = "";
        public long AudioOffset;
        public long AudioSize;
        public byte[] CoverData = new byte[0];
    }

    // =========================================================================
    //  核心解密逻辑
    // =========================================================================

    internal static class NcmCrypto
    {
        private static readonly byte[] CoreKey = HexToBytes("687a4852416d736f356b496e62617857");
        private static readonly byte[] MetaKey = HexToBytes("2331346c6a6b5f215c5d2630553c2728");
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("CTENFDAM");
        private const byte XorKey = 0x64;
        private const byte XorMeta = 0x63;
        private const int BufferSize = 1024 * 1024;

        private static byte[] HexToBytes(string hex)
        {
            byte[] b = new byte[hex.Length / 2];
            for (int i = 0; i < b.Length; i++)
                b[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return b;
        }

        /// <summary>标准 RC4 KSA，生成初始 S-Box。</summary>
        private static byte[] BuildSBox(byte[] key)
        {
            byte[] s = new byte[256];
            for (int i = 0; i < 256; i++)
                s[i] = (byte)i;
            int j = 0;
            for (int i = 0; i < 256; i++)
            {
                j = (j + s[i] + key[i % key.Length]) & 0xff;
                byte t = s[i];
                s[i] = s[j];
                s[j] = t;
            }
            return s;
        }

        /// <summary>
        /// 生成 256 字节密钥流表。
        /// 网易的变体 RC4 中 j 不累积、S-Box 不交换，
        /// 因此密钥流以 256 字节为周期循环，可以一次性预计算。
        /// </summary>
        private static byte[] BuildKeyStream(byte[] key)
        {
            byte[] s = BuildSBox(key);
            byte[] ks = new byte[256];
            for (int p = 0; p < 256; p++)
            {
                int i = (p + 1) & 0xff;
                int j = (i + s[i]) & 0xff;
                ks[p] = s[(s[i] + s[j]) & 0xff];
            }
            return ks;
        }

        private static byte[] AesEcbDecrypt(byte[] key, byte[] data)
        {
            if (data.Length == 0)
                return new byte[0];
            if (data.Length % 16 != 0)
                throw new NcmException("AES 输入长度不是 16 的倍数 (" + data.Length + ")");

            using (AesManaged aes = new AesManaged())
            {
                aes.Key = (byte[])key.Clone();
                aes.IV = new byte[16];
                aes.Mode = CipherMode.ECB;
                aes.Padding = PaddingMode.None;
                using (ICryptoTransform tr = aes.CreateDecryptor())
                {
                    byte[] outBuf = new byte[data.Length];
                    tr.TransformBlock(data, 0, data.Length, outBuf, 0);
                    return outBuf;
                }
            }
        }

        private static byte[] StripPkcs7(byte[] data)
        {
            if (data.Length == 0)
                return data;
            int pad = data[data.Length - 1];
            if (pad > 0 && pad <= 16 && data.Length >= pad)
            {
                bool allPad = true;
                for (int i = data.Length - pad; i < data.Length; i++)
                {
                    if (data[i] != pad)
                    {
                        allPad = false;
                        break;
                    }
                }
                if (allPad)
                {
                    byte[] r = new byte[data.Length - pad];
                    Array.Copy(data, r, r.Length);
                    return r;
                }
            }
            return data;
        }

        private static bool StartsWithAscii(byte[] data, string ascii)
        {
            if (data.Length < ascii.Length)
                return false;
            for (int i = 0; i < ascii.Length; i++)
            {
                if (data[i] != (byte)ascii[i])
                    return false;
            }
            return true;
        }

        /// <summary>读取并解密 NCM 文件头。</summary>
        public static NcmInfo ReadHeader(string path)
        {
            NcmInfo info = new NcmInfo();

            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536))
            using (BinaryReader br = new BinaryReader(fs, Encoding.UTF8))
            {
                // 1. Magic
                byte[] magic = br.ReadBytes(8);
                if (magic.Length != 8 || !StartsWithAscii(magic, "CTENFDAM"))
                    throw new NcmException("不是有效的 NCM 文件（文件头不匹配）");

                // 2. 跳过 2 字节 gap，读取密钥块长度
                br.ReadBytes(2);
                byte[] lenBuf = br.ReadBytes(4);
                if (lenBuf.Length != 4)
                    throw new NcmException("文件太短，无法读取密钥长度");
                long keyLen = BitConverter.ToUInt32(lenBuf, 0);

                // 3. 密钥块 → RC4 密钥
                if (keyLen <= 0 || keyLen > 4 * 1024 * 1024)
                    throw new NcmException("密钥块长度异常: " + keyLen);
                byte[] keyData = ReadExactly(br, (int)keyLen, "密钥数据");
                for (int i = 0; i < keyData.Length; i++)
                    keyData[i] ^= XorKey;

                byte[] plainKey = AesEcbDecrypt(CoreKey, keyData);
                if (!StartsWithAscii(plainKey, "neteasecloudmusic"))
                    throw new NcmException("密钥块解密后标志位不匹配，文件可能已损坏");

                byte[] body = new byte[plainKey.Length - 17];
                Array.Copy(plainKey, 17, body, 0, body.Length);
                info.Rc4Key = StripPkcs7(body);
                if (info.Rc4Key.Length == 0)
                    throw new NcmException("RC4 密钥为空");

                // 4. 元数据
                byte[] metaLenBuf = br.ReadBytes(4);
                if (metaLenBuf.Length != 4)
                    throw new NcmException("无法读取元数据长度");
                long metaLen = BitConverter.ToUInt32(metaLenBuf, 0);
                if (metaLen <= 0 || metaLen > 16L * 1024 * 1024)
                    throw new NcmException("元数据长度异常: " + metaLen);
                byte[] metaRaw = ReadExactly(br, (int)metaLen, "元数据");
                for (int i = 0; i < metaRaw.Length; i++)
                    metaRaw[i] ^= XorMeta;

                ParseMetadata(info, metaRaw);

                // 5. 封面图（未加密）
                br.ReadBytes(4);   // CRC32
                br.ReadBytes(5);   // 5 字节 gap
                byte[] coverLenBuf = br.ReadBytes(4);
                long coverLen = (coverLenBuf.Length == 4) ? BitConverter.ToUInt32(coverLenBuf, 0) : 0;
                if (coverLen > 0 && coverLen < 50L * 1024 * 1024)
                    info.CoverData = ReadExactly(br, (int)coverLen, "封面数据");

                // 6. 音频流位置
                info.AudioOffset = fs.Position;
                info.AudioSize = fs.Length - info.AudioOffset;
                if (info.AudioSize < 0)
                    info.AudioSize = 0;
            }

            return info;
        }

        private static byte[] ReadExactly(BinaryReader br, int count, string what)
        {
            byte[] buf = new byte[count];
            int off = 0;
            while (off < count)
            {
                int n = br.Read(buf, off, count - off);
                if (n <= 0)
                    throw new NcmException(what + "不完整: 期望 " + count + " 字节，实际 " + off + " 字节");
                off += n;
            }
            return buf;
        }

        private static void ParseMetadata(NcmInfo info, byte[] xoredRaw)
        {
            string text = Encoding.UTF8.GetString(xoredRaw);
            const string marker = "key(Don't modify):";
            int pos = text.IndexOf(marker, StringComparison.Ordinal);
            if (pos < 0)
                throw new NcmException("元数据格式异常：缺少 key(Don't modify) 前缀");

            // 只保留 base64 字母表内的字符
            string b64 = FilterBase64(text.Substring(pos + marker.Length));

            // 补齐 '='
            int mod = b64.Length % 4;
            if (mod != 0)
                b64 = b64 + new string('=', 4 - mod);

            byte[] encrypted;
            try
            {
                encrypted = Convert.FromBase64String(b64);
            }
            catch (Exception ex)
            {
                throw new NcmException("元数据 Base64 解码失败: " + ex.Message);
            }

            // 补齐到 16 字节边界
            int padNeed = (16 - encrypted.Length % 16) % 16;
            if (padNeed != 0)
            {
                byte[] tmp = new byte[encrypted.Length + padNeed];
                Array.Copy(encrypted, tmp, encrypted.Length);
                encrypted = tmp;
            }

            byte[] plain = StripPkcs7(AesEcbDecrypt(MetaKey, encrypted));
            string jsonText = Encoding.UTF8.GetString(plain);
            if (jsonText.StartsWith("music:", StringComparison.Ordinal))
                jsonText = jsonText.Substring(6);

            jsonText = ExtractJsonObject(jsonText);

            Dictionary<string, object> meta;
            try
            {
                meta = (Dictionary<string, object>)MiniJson.Parse(jsonText);
            }
            catch (NcmException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new NcmException("元数据 JSON 解析失败: " + ex.Message);
            }

            info.MusicName = GetString(meta, "musicName");
            info.AlbumName = GetString(meta, "album");
            info.MetaFormat = GetString(meta, "format");
            if (string.IsNullOrEmpty(info.AlbumName))
                info.AlbumName = GetString(meta, "albumName");

            // artist: [["歌手名", id], ...] 或 ["歌手名", ...]，多位歌手用 、 连接
            object artists;
            if (meta.TryGetValue("artist", out artists) && artists is List<object>)
            {
                List<object> list = (List<object>)artists;
                List<string> names = new List<string>();
                foreach (object item in list)
                {
                    string name = FlattenArtist(item);
                    if (!string.IsNullOrEmpty(name))
                        names.Add(name);
                }
                info.ArtistName = string.Join("、", names.ToArray());
            }
        }

        private static string FlattenArtist(object node)
        {
            List<object> arr = node as List<object>;
            if (arr == null)
                return node == null ? "" : Convert.ToString(node);
            if (arr.Count == 0)
                return "";
            return FlattenArtist(arr[0]);
        }

        private static string GetString(Dictionary<string, object> obj, string key)
        {
            object v;
            if (obj.TryGetValue(key, out v) && v != null)
            {
                string s = v as string;
                if (s != null)
                    return s;
                if (v is double || v is float || v is int || v is long)
                    return Convert.ToString(v, CultureInfo.InvariantCulture);
            }
            return "";
        }

        private static string FilterBase64(string s)
        {
            StringBuilder sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') ||
                    (c >= '0' && c <= '9') || c == '+' || c == '/' || c == '=')
                    sb.Append(c);
            }
            return sb.ToString();
        }

        private static string ExtractJsonObject(string s)
        {
            int start = s.IndexOf('{');
            if (start < 0)
                throw new NcmException("元数据中找不到 JSON 对象");
            int depth = 0;
            bool inStr = false;
            bool escaped = false;
            for (int i = start; i < s.Length; i++)
            {
                char c = s[i];
                if (inStr)
                {
                    if (escaped)
                    {
                        escaped = false;
                    }
                    else if (c == '\\')
                    {
                        escaped = true;
                    }
                    else if (c == '"')
                    {
                        inStr = false;
                    }
                    continue;
                }
                if (c == '"')
                {
                    inStr = true;
                }
                else if (c == '{')
                {
                    depth++;
                }
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0)
                        return s.Substring(start, i - start + 1);
                }
            }
            throw new NcmException("元数据 JSON 未闭合");
        }

        /// <summary>把 NCM 的音频流解密写入目标文件。</summary>
        public static long DecryptAudio(string srcPath, NcmInfo info, string destPath)
        {
            byte[] ks = BuildKeyStream(info.Rc4Key);
            byte[] buffer = new byte[BufferSize];
            long written = 0;

            using (FileStream fin = new FileStream(srcPath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize))
            using (FileStream fout = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize))
            {
                fin.Seek(info.AudioOffset, SeekOrigin.Begin);
                long remaining = info.AudioSize;
                while (remaining > 0)
                {
                    int want = (int)Math.Min(buffer.Length, remaining);
                    int read = fin.Read(buffer, 0, want);
                    if (read <= 0)
                        break;

                    int k = (int)(written & 0xFF);
                    for (int i = 0; i < read; i++)
                        buffer[i] ^= ks[(k + i) & 0xFF];

                    fout.Write(buffer, 0, read);
                    written += read;
                    remaining -= read;
                }
            }
            return written;
        }

        /// <summary>读取一小段解密后的音频头，用于探测真实格式。</summary>
        public static byte[] PeekAudioHeader(string srcPath, NcmInfo info, int count)
        {
            byte[] ks = BuildKeyStream(info.Rc4Key);
            int n = (int)Math.Min(count, info.AudioSize);
            if (n <= 0)
                return new byte[0];
            byte[] buf = new byte[n];
            using (FileStream fin = new FileStream(srcPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536))
            {
                fin.Seek(info.AudioOffset, SeekOrigin.Begin);
                int off = 0;
                while (off < n)
                {
                    int r = fin.Read(buf, off, n - off);
                    if (r <= 0)
                        break;
                    off += r;
                }
            }
            for (int i = 0; i < buf.Length; i++)
                buf[i] ^= ks[i & 0xFF];
            return buf;
        }

        public static string DetectFormat(byte[] head)
        {
            if (head.Length >= 3 && head[0] == 'I' && head[1] == 'D' && head[2] == '3')
                return "mp3";
            if (head.Length >= 4 && head[0] == 'f' && head[1] == 'L' && head[2] == 'a' && head[3] == 'C')
                return "flac";
            if (head.Length >= 4 && head[0] == 'R' && head[1] == 'I' && head[2] == 'F' && head[3] == 'F')
                return "wav";
            if (head.Length >= 4 && head[0] == 'O' && head[1] == 'g' && head[2] == 'g' && head[3] == 'S')
                return "ogg";
            if (head.Length >= 2 && head[0] == 0xFF && (head[1] & 0xE0) == 0xE0)
                return "mp3";
            if (head.Length >= 12 && head[4] == 'f' && head[5] == 't' && head[6] == 'y' && head[7] == 'p')
                return "m4a";
            return "unknown";
        }
    }

    // =========================================================================
    //  通用工具
    // =========================================================================

    internal static class Util
    {
        public static string SanitizeFileName(string s)
        {
            if (string.IsNullOrEmpty(s))
                return "";
            StringBuilder sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                bool bad = c == '\\' || c == '/' || c == ':' || c == '*' || c == '?' ||
                           c == '"' || c == '<' || c == '>' || c == '|' || c < 32 || c == 127;
                sb.Append(bad ? '_' : c);
            }
            string r = sb.ToString().Trim().TrimEnd('.', ' ');
            return r.Length == 0 ? "unnamed" : r;
        }

        public static bool IsGarbled(string s)
        {
            if (string.IsNullOrEmpty(s))
                return true;
            int bad = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c < 32 || (c >= 128 && c < 160))
                    bad++;
            }
            return bad * 10 > s.Length * 3;
        }

        public static string SizeText(long bytes)
        {
            double mb = bytes / (1024.0 * 1024.0);
            if (mb >= 1)
                return mb.ToString("F1", CultureInfo.InvariantCulture) + " MB";
            return (bytes / 1024.0).ToString("F0", CultureInfo.InvariantCulture) + " KB";
        }
    }

    // =========================================================================
    //  转换流程
    // =========================================================================

    internal sealed class ConvertResult
    {
        public bool Success;
        public bool Skipped;
        public string Message;
        public string Detail;

        public ConvertResult(bool success, bool skipped, string message, string detail)
        {
            Success = success;
            Skipped = skipped;
            Message = message;
            Detail = detail;
        }
    }

    internal static class Converter
    {
        public static string FindFfmpeg()
        {
            try
            {
                string local = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg.exe");
                if (File.Exists(local))
                    return local;

                string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
                foreach (string dir in pathEnv.Split(';'))
                {
                    if (dir.Length == 0)
                        continue;
                    try
                    {
                        string cand = Path.Combine(dir.Trim('"'), "ffmpeg.exe");
                        if (File.Exists(cand))
                            return cand;
                    }
                    catch (ArgumentException) { }
                }
            }
            catch (ArgumentException) { }
            return null;
        }

        public static bool FfmpegAvailable
        {
            get { return FindFfmpeg() != null; }
        }

        public static bool Transcode(string input, string output, string bitrate)
        {
            string ffmpeg = FindFfmpeg();
            if (ffmpeg == null)
                throw new NcmException("未找到 ffmpeg.exe（放到本程序同目录即可启用 MP3 转码）");

            Directory.CreateDirectory(Path.GetDirectoryName(output));

            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = ffmpeg;
            psi.Arguments = "-y -i \"" + input + "\" -codec:a libmp3lame -b:a " + bitrate +
                            " -map_metadata 0 -id3v2_version 3 \"" + output + "\"";
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardError = true;
            psi.RedirectStandardOutput = true;

            using (Process p = Process.Start(psi))
            {
                string err = p.StandardError.ReadToEnd();
                p.WaitForExit();
                if (p.ExitCode != 0)
                {
                    string tail = err.Trim();
                    int nl = tail.LastIndexOf('\n');
                    if (nl >= 0)
                        tail = tail.Substring(nl + 1).Trim();
                    throw new NcmException("ffmpeg 转码失败: " + tail);
                }
            }
            return true;
        }

        /// <summary>把一个 NCM（或直接把 FLAC/WAV）转换成目标文件。</summary>
        public static ConvertResult Convert(string inputPath, string outputDir, bool overwrite,
                                            bool extractCover, bool toMp3)
        {
            string ext = Path.GetExtension(inputPath).ToLowerInvariant();

            try
            {
                string destDir = string.IsNullOrEmpty(outputDir)
                    ? Path.GetDirectoryName(inputPath)
                    : outputDir;

                // ── 直接拖入的 FLAC/WAV：调 ffmpeg 转 MP3 ──
                if (ext == ".flac" || ext == ".wav")
                {
                    string mp3Name = Path.GetFileNameWithoutExtension(inputPath) + ".mp3";
                    string mp3Path = Path.Combine(destDir, mp3Name);
                    if (File.Exists(mp3Path) && !overwrite)
                        return new ConvertResult(false, true, "已存在同名文件，跳过: " + mp3Name, "");

                    Directory.CreateDirectory(destDir);
                    Transcode(inputPath, mp3Path, "320k");
                    return new ConvertResult(true, false, mp3Name, Util.SizeText(new FileInfo(mp3Path).Length));
                }

                // ── NCM 解密 ──
                if (ext != ".ncm")
                    return new ConvertResult(false, false, "不支持的文件类型: " + ext, "");

                NcmInfo info = NcmCrypto.ReadHeader(inputPath);
                string guess = NcmCrypto.DetectFormat(NcmCrypto.PeekAudioHeader(inputPath, info, 16));
                if (guess == "unknown")
                    guess = string.IsNullOrEmpty(info.MetaFormat) ? "unknown" : info.MetaFormat.ToLowerInvariant();

                string fmt = string.IsNullOrEmpty(info.MetaFormat)
                    ? guess
                    : info.MetaFormat.ToLowerInvariant();
                if (fmt != "mp3" && fmt != "flac" && fmt != "wav" && fmt != "ogg" && fmt != "m4a" && fmt != "wma")
                    fmt = (guess == "unknown") ? "mp3" : guess;

                string name = BuildName(info, fmt, Path.GetFileNameWithoutExtension(inputPath));
                string destPath = Path.Combine(destDir, name);

                if (File.Exists(destPath) && !overwrite)
                    return new ConvertResult(false, true, "已存在同名文件，跳过: " + name, "");

                Directory.CreateDirectory(destDir);
                long bytes = NcmCrypto.DecryptAudio(inputPath, info, destPath);

                StringBuilder sb = new StringBuilder();
                sb.Append(name).Append(" (").Append(Util.SizeText(bytes)).Append(')');

                // 封面
                if (extractCover && info.CoverData != null && info.CoverData.Length > 0)
                {
                    try
                    {
                        string coverExt = ".jpg";
                        if (info.CoverData.Length > 4 &&
                            info.CoverData[0] == 0x89 && info.CoverData[1] == 0x50 &&
                            info.CoverData[2] == 0x4E && info.CoverData[3] == 0x47)
                            coverExt = ".png";
                        string coverPath = Path.Combine(destDir, Path.GetFileNameWithoutExtension(name) + coverExt);
                        File.WriteAllBytes(coverPath, info.CoverData);
                        sb.Append(" +封面");
                    }
                    catch (Exception ex)
                    {
                        sb.Append(" (封面提取失败: ").Append(ex.Message).Append(')');
                    }
                }

                // FLAC/WAV/OGG → MP3（需要 ffmpeg）
                if (toMp3 && (fmt == "flac" || fmt == "wav" || fmt == "ogg"))
                {
                    try
                    {
                        string mp3Path = Path.Combine(destDir, Path.GetFileNameWithoutExtension(name) + ".mp3");
                        Transcode(destPath, mp3Path, "320k");
                        File.Delete(destPath);
                        sb.Append(" → ").Append(Path.GetFileName(mp3Path));
                    }
                    catch (Exception ex)
                    {
                        sb.Append(" (转 MP3 失败: ").Append(ex.Message).Append(')');
                    }
                }

                return new ConvertResult(true, false, sb.ToString(), Path.GetFileNameWithoutExtension(inputPath));
            }
            catch (NcmException ex)
            {
                return new ConvertResult(false, false, ex.Message, Path.GetFileName(inputPath));
            }
            catch (IOException ex)
            {
                return new ConvertResult(false, false, "IO 错误: " + ex.Message, Path.GetFileName(inputPath));
            }
            catch (UnauthorizedAccessException ex)
            {
                return new ConvertResult(false, false, "没有权限: " + ex.Message, Path.GetFileName(inputPath));
            }
            catch (Exception ex)
            {
                return new ConvertResult(false, false, "错误: " + ex.Message, Path.GetFileName(inputPath));
            }
        }

        private static string BuildName(NcmInfo info, string fmt, string fallbackStem)
        {
            string artist = Util.IsGarbled(info.ArtistName) ? "" : info.ArtistName.Trim();
            string song = Util.IsGarbled(info.MusicName) ? "" : info.MusicName.Trim();
            if (song.Length == 0)
                song = fallbackStem;

            string name = artist.Length > 0
                ? Util.SanitizeFileName(artist) + " - " + Util.SanitizeFileName(song)
                : Util.SanitizeFileName(song);

            return name + "." + fmt;
        }

        /// <summary>把命令行输入（文件/文件夹/通配符）展开为文件列表。</summary>
        public static List<string> ExpandInputs(IEnumerable<string> inputs, bool recursive)
        {
            SearchOption opt = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            List<string> result = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string raw in inputs)
            {
                string item = raw.Trim().Trim('"');
                if (item.Length == 0)
                    continue;

                try
                {
                    if (Directory.Exists(item))
                    {
                        // 目录只扫描 .ncm，避免把上次转换出来的 FLAC/WAV 又捡回来二次处理。
                        // 想要转 MP3 的话，直接把 FLAC/WAV 文件拖进窗口即可。
                        AddAll(result, seen, Directory.GetFiles(item, "*.ncm", opt));
                    }
                    else if (File.Exists(item))
                    {
                        AddAll(result, seen, new string[] { item });
                    }
                    else if (item.IndexOf('*') >= 0 || item.IndexOf('?') >= 0)
                    {
                        string dir = Path.GetDirectoryName(item);
                        if (dir.Length == 0)
                            dir = ".";
                        string pattern = Path.GetFileName(item);
                        if (Directory.Exists(dir))
                            AddAll(result, seen, Directory.GetFiles(dir, pattern, opt));
                    }
                }
                catch (Exception) { /* 跳过无法访问的路径 */ }
            }
            return result;
        }

        private static void AddAll(List<string> list, HashSet<string> seen, IEnumerable<string> items)
        {
            foreach (string f in items)
            {
                string full = Path.GetFullPath(f);
                if (seen.Add(full))
                    list.Add(full);
            }
        }
    }

    // =========================================================================
    //  Console / Win32 互操作（用于 winexe 也能在 cmd 里输出）
    // =========================================================================

    internal static class Native
    {
        public const int AttachParentProcess = -1;

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AttachConsole(int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool FreeConsole();
    }

    // =========================================================================
    //  主程序
    // =========================================================================

    internal static class Program
    {
        internal const string Version = "2.0.0";

        [STAThread]
        private static int Main(string[] argv)
        {
            // 有命令行参数 → CLI 模式，挂到父控制台输出
            if (argv != null && argv.Length > 0)
            {
                bool attached = Native.AttachConsole(Native.AttachParentProcess);
                int code = RunCli(argv);
                if (attached)
                {
                    Console.Out.Flush();
                    Native.FreeConsole();
                }
                return code;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                Application.Run(new MainForm());
            }
            catch (Exception ex)
            {
                MessageBox.Show("程序异常退出:\n" + ex.Message, "NCM Converter",
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
            return 0;
        }

        private static int RunCli(string[] argv)
        {
            List<string> inputs = new List<string>();
            string outDir = null;
            bool force = false, recursive = false, cover = false, toMp3 = false, quiet = false, dry = false;

            for (int i = 0; i < argv.Length; i++)
            {
                string a = argv[i];
                switch (a)
                {
                    case "-o":
                    case "--output":
                        if (i + 1 < argv.Length) outDir = argv[++i];
                        break;
                    case "-f":
                    case "--force": force = true; break;
                    case "-r":
                    case "--recursive": recursive = true; break;
                    case "--cover": cover = true; break;
                    case "--to-mp3": toMp3 = true; break;
                    case "-q":
                    case "--quiet": quiet = true; break;
                    case "--dry-run": dry = true; break;
                    case "-h":
                    case "--help": PrintHelp(); return 0;
                    case "-v":
                    case "--version":
                        Console.WriteLine("NCMConverter " + Version + " (.NET Framework, no Python)");
                        return 0;
                    default:
                        if (a.StartsWith("-"))
                            Console.WriteLine("未知参数: " + a);
                        else
                            inputs.Add(a);
                        break;
                }
            }

            if (inputs.Count == 0)
            {
                PrintHelp();
                return 2;
            }

            List<string> files = Converter.ExpandInputs(inputs, recursive);
            if (files.Count == 0)
            {
                Console.WriteLine("错误: 未找到任何 .ncm / .flac / .wav 文件");
                return 2;
            }

            if (dry)
            {
                Console.WriteLine("将要转换 " + files.Count + " 个文件:");
                foreach (string f in files)
                    Console.WriteLine("  " + f);
                return 0;
            }

            int ok = 0, skipped = 0, failed = 0;
            for (int i = 0; i < files.Count; i++)
            {
                if (!quiet)
                    Console.WriteLine("[" + (i + 1) + "/" + files.Count + "] " + Path.GetFileName(files[i]));

                ConvertResult r = Converter.Convert(files[i], outDir, force, cover, toMp3);
                if (r.Success)
                {
                    ok++;
                    if (!quiet)
                        Console.WriteLine("  [OK] " + r.Message);
                }
                else if (r.Skipped)
                {
                    skipped++;
                    Console.WriteLine("  [跳过] " + r.Message);
                }
                else
                {
                    failed++;
                    Console.WriteLine("  [失败] " + r.Message);
                }
            }

            if (!quiet)
            {
                Console.WriteLine(new string('-', 50));
                Console.WriteLine("完成: 成功 " + ok + ", 跳过 " + skipped + ", 失败 " + failed);
            }

            if (failed == 0 && skipped == 0) return 0;
            if (ok > 0) return 1;
            return 2;
        }

        private static void PrintHelp()
        {
            Console.WriteLine();
            Console.WriteLine("NCM Converter " + Version + " - 网易云音乐 NCM 解密工具 (纯 .NET Framework)");
            Console.WriteLine("------------------------------------------------------------------");
            Console.WriteLine("用法: NCMConverter.exe <文件|文件夹|通配符> [选项]");
            Console.WriteLine("      不带参数运行时打开图形界面。");
            Console.WriteLine();
            Console.WriteLine("选项:");
            Console.WriteLine("  -o, --output <目录>   输出目录（默认与源文件同目录）");
            Console.WriteLine("  -f, --force           覆盖已存在的输出文件");
            Console.WriteLine("  -r, --recursive       递归搜索子文件夹");
            Console.WriteLine("      --cover           同时导出封面图");
            Console.WriteLine("      --to-mp3          解密后若是 FLAC/WAV/OGG，用 ffmpeg 转 MP3 (320k)");
            Console.WriteLine("      --dry-run         仅列出将要转换的文件");
            Console.WriteLine("  -q, --quiet           静默模式");
            Console.WriteLine("  -v, --version         显示版本");
            Console.WriteLine("  -h, --help            显示本帮助");
            Console.WriteLine();
            Console.WriteLine("示例:");
            Console.WriteLine("  NCMConverter.exe song.ncm");
            Console.WriteLine("  NCMConverter.exe D:\\Music\\NCM\\ -r --cover");
            Console.WriteLine("  NCMConverter.exe *.ncm -o D:\\Music\\MP3\\");
            Console.WriteLine();
        }
    }

    // =========================================================================
    //  GUI
    // =========================================================================

    internal sealed class FlatProgress : Control
    {
        private int _value;
        private int _maximum = 100;

        public FlatProgress()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Panel;
            ForeColor = Theme.Accent;
            Height = 6;
        }

        public int Maximum
        {
            get { return _maximum; }
            set { _maximum = value < 1 ? 1 : value; Invalidate(); }
        }

        public int Value
        {
            get { return _value; }
            set { _value = value; Invalidate(); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            if (_value > 0 && _maximum > 0)
            {
                int w = (int)Math.Round((double)_value / _maximum * Width);
                if (w > Width) w = Width;
                using (SolidBrush b = new SolidBrush(ForeColor))
                    e.Graphics.FillRectangle(b, 0, 0, w, Height);
            }
        }
    }

    internal static class Theme
    {
        public static readonly Color Back = Color.FromArgb(0x1e, 0x1e, 0x2e);
        public static readonly Color Panel = Color.FromArgb(0x28, 0x28, 0x40);
        public static readonly Color Fore = Color.FromArgb(0xcd, 0xd6, 0xf4);
        public static readonly Color Accent = Color.FromArgb(0x89, 0xb4, 0xfa);
        public static readonly Color Green = Color.FromArgb(0xa6, 0xe3, 0xa1);
        public static readonly Color Red = Color.FromArgb(0xf3, 0x8b, 0xa8);
        public static readonly Color Yellow = Color.FromArgb(0xf9, 0xe2, 0xaf);
        public static readonly Color Border = Color.FromArgb(0x45, 0x47, 0x5a);
        public static readonly Color Dim = Color.FromArgb(0x6c, 0x70, 0x86);
    }

    internal sealed class FlatButton : Button
    {
        public FlatButton()
        {
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 1;
            BackColor = Theme.Panel;
            ForeColor = Theme.Fore;
            FlatAppearance.BorderColor = Theme.Border;
            FlatAppearance.MouseOverBackColor = Color.FromArgb(0x36, 0x36, 0x54);
            FlatAppearance.MouseDownBackColor = Color.FromArgb(0x45, 0x47, 0x5a);
            UseCompatibleTextRendering = true;
        }
    }

    internal sealed class MainForm : Form
    {
        private Panel _dropPanel;
        private Label _dropLabel;
        private FlatButton _btnSelect;
        private FlatProgress _progress;
        private Label _statusLabel;
        private RichTextBox _logBox;
        private TextBox _txtOutDir;
        private CheckBox _cbToMp3;
        private CheckBox _cbCover;
        private CheckBox _cbOverwrite;

        private BackgroundWorker _worker;

        public MainForm()
        {
            Text = "NCM 转换器 (v" + Program.Version + ")";
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(720, 560);
            MinimumSize = new Size(560, 460);
            BackColor = Theme.Back;
            ForeColor = Theme.Fore;
            Font = BaseFont(9f);
            AllowDrop = true;

            BuildUi();

            DragEnter += OnDragEnter;
            DragDrop += OnDragDrop;
        }

        private static Font BaseFont(float size)
        {
            FontFamily preferred = null;
            foreach (string cand in new string[] { "Microsoft YaHei UI", "Microsoft YaHei", "Segoe UI" })
            {
                foreach (FontFamily ff in FontFamily.Families)
                {
                    if (string.Equals(ff.Name, cand, StringComparison.OrdinalIgnoreCase))
                    {
                        preferred = ff;
                        break;
                    }
                }
                if (preferred != null) break;
            }
            if (preferred == null)
                preferred = FontFamily.GenericSansSerif;
            return new Font(preferred, size, FontStyle.Regular);
        }

        private void BuildUi()
        {
            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.BackColor = Theme.Back;
            root.ColumnCount = 1;
            root.RowCount = 6;
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 56f));    // 标题
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));    // 拖拽区
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 8f));     // 进度条
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 26f));    // 状态
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 176f));   // 日志
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46f));    // 底部栏
            root.Padding = new Padding(20, 16, 20, 16);
            Controls.Add(root);

            // ── 标题 ──
            Panel header = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Back, Margin = Padding.Empty };
            Label title = new Label
            {
                Text = "NCM / FLAC → MP3 转换器",
                ForeColor = Theme.Accent,
                AutoSize = true,
                BackColor = Theme.Back,
                Location = new Point(0, 6),
            };
            title.Font = new Font(title.Font.FontFamily, 16f, FontStyle.Bold);
            Label sub = new Label
            {
                Text = "拖入文件到这里，无需 Python，纯 Windows 原生运行",
                ForeColor = Theme.Dim,
                AutoSize = true,
                BackColor = Theme.Back,
                Location = new Point(2, 34),
            };
            header.Controls.Add(title);
            header.Controls.Add(sub);
            root.Controls.Add(header, 0, 0);

            // ── 拖拽区 ──
            Panel dropOuter = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.Back,
                Margin = new Padding(0, 8, 0, 12),
            };
            _dropPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.Panel,
                Margin = Padding.Empty,
                Padding = new Padding(2),
            };
            Panel inner = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.Panel,
                Margin = Padding.Empty,
            };
            _dropLabel = new Label
            {
                Text = "把 .ncm / .flac / .wav 文件拖到这里",
                ForeColor = Theme.Dim,
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleCenter,
                BackColor = Theme.Panel,
                Dock = DockStyle.Top,
                Height = 60,
                Margin = new Padding(0, 60, 0, 0),
            };
            _dropLabel.Font = new Font(_dropLabel.Font.FontFamily, 12f);

            FlowLayoutPanel btnWrap = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 46,
                BackColor = Theme.Panel,
                FlowDirection = FlowDirection.LeftToRight,
                Padding = new Padding(0, 4, 0, 0),
            };
            _btnSelect = new FlatButton { Text = "选择文件", Height = 34, Width = 150, Margin = new Padding(0) };
            FlatButton btnSelectDir = new FlatButton { Text = "选择文件夹", Height = 34, Width = 150, Margin = new Padding(12, 0, 0, 0) };
            _btnSelect.Click += delegate { ChooseFiles(); };
            btnSelectDir.Click += delegate { ChooseFolder(); };
            btnWrap.Controls.Add(_btnSelect);
            btnWrap.Controls.Add(btnSelectDir);

            // 让两个按钮横向居中
            inner.Resize += delegate
            {
                int contentW = _btnSelect.Width + 12 + btnSelectDir.Width;
                int left = Math.Max(0, (inner.Width - contentW) / 2);
                btnWrap.Padding = new Padding(left, 4, 0, 0);
            };

            inner.Controls.Add(btnWrap);
            inner.Controls.Add(_dropLabel);
            _dropPanel.Controls.Add(inner);
            dropOuter.Controls.Add(_dropPanel);

            _dropPanel.AllowDrop = true;
            dropOuter.AllowDrop = true;
            inner.AllowDrop = true;
            _dropLabel.AllowDrop = true;
            _dropPanel.DragEnter += OnDragEnter;
            _dropPanel.DragDrop += OnDragDrop;
            root.Controls.Add(dropOuter, 0, 1);

            // ── 进度条 ──
            _progress = new FlatProgress { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 4) };
            root.Controls.Add(_progress, 0, 2);

            // ── 状态 ──
            _statusLabel = new Label
            {
                Text = "就绪，等待文件...",
                ForeColor = Theme.Dim,
                Dock = DockStyle.Fill,
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleLeft,
                BackColor = Theme.Back,
            };
            root.Controls.Add(_statusLabel, 0, 3);

            // ── 日志 ──
            GroupBox logGroup = new GroupBox
            {
                Text = "转换日志",
                ForeColor = Theme.Accent,
                BackColor = Theme.Back,
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 6, 0, 6),
            };
            _logBox = new RichTextBox
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.Panel,
                ForeColor = Theme.Fore,
                BorderStyle = BorderStyle.None,
                ReadOnly = true,
                DetectUrls = false,
                ScrollBars = RichTextBoxScrollBars.Vertical,
            };
            _logBox.Font = new Font("Consolas", 9f);
            logGroup.Controls.Add(_logBox);
            root.Controls.Add(logGroup, 0, 4);

            // ── 底部栏 ──
            Panel bottom = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Back, Margin = Padding.Empty };

            Label lblOut = new Label
            {
                Text = "输出目录:",
                ForeColor = Theme.Fore,
                AutoSize = true,
                BackColor = Theme.Back,
                Location = new Point(0, 12),
            };
            _txtOutDir = new TextBox
            {
                BackColor = Theme.Panel,
                ForeColor = Theme.Fore,
                BorderStyle = BorderStyle.FixedSingle,
                Location = new Point(66, 8),
                Width = 190,
                Anchor = AnchorStyles.Top | AnchorStyles.Left,
            };
            FlatButton btnBrowse = new FlatButton
            {
                Text = "浏览",
                Location = new Point(262, 7),
                Height = 26,
                Width = 54,
            };
            btnBrowse.Click += delegate { BrowseOutDir(); };

            _cbToMp3 = new CheckBox
            {
                Text = "FLAC 自动转 MP3",
                ForeColor = Theme.Fore,
                BackColor = Theme.Back,
                AutoSize = true,
                Location = new Point(330, 10),
                Checked = true,
            };
            _cbCover = new CheckBox
            {
                Text = "提取封面图",
                ForeColor = Theme.Fore,
                BackColor = Theme.Back,
                AutoSize = true,
                Location = new Point(330, 30),
                Checked = false,
            };
            _cbOverwrite = new CheckBox
            {
                Text = "覆盖同名文件",
                ForeColor = Theme.Fore,
                BackColor = Theme.Back,
                AutoSize = true,
                Location = new Point(462, 10),
                Checked = false,
            };

            FlatButton btnClear = new FlatButton
            {
                Text = "清空日志",
                Height = 26,
                Width = 80,
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
            };
            btnClear.Click += delegate { _logBox.Clear(); };

            bottom.Controls.Add(lblOut);
            bottom.Controls.Add(_txtOutDir);
            bottom.Controls.Add(btnBrowse);
            bottom.Controls.Add(_cbToMp3);
            bottom.Controls.Add(_cbCover);
            bottom.Controls.Add(_cbOverwrite);
            bottom.Controls.Add(btnClear);
            bottom.Resize += delegate { btnClear.Location = new Point(bottom.Width - 80, 7); };
            bottom.Paint += delegate { };
            root.Controls.Add(bottom, 0, 5);

            // ffmpeg 可用性
            if (!Converter.FfmpegAvailable)
            {
                _cbToMp3.Enabled = false;
                _cbToMp3.Checked = false;
                _cbToMp3.Text = "FLAC 自动转 MP3（缺少 ffmpeg）";
            }
        }

        // ── 文件选择 ──────────────────────────────────────────────────

        private void ChooseFiles()
        {
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Title = "选择文件";
                dlg.Filter = "支持的文件 (*.ncm;*.flac;*.wav)|*.ncm;*.flac;*.wav|NCM 文件 (*.ncm)|*.ncm|" +
                             "FLAC 文件 (*.flac)|*.flac|WAV 文件 (*.wav)|*.wav|所有文件 (*.*)|*.*";
                dlg.Multiselect = true;
                if (dlg.ShowDialog(this) == DialogResult.OK)
                    StartConvert(dlg.FileNames);
            }
        }

        private void ChooseFolder()
        {
            using (FolderBrowserDialog dlg = new FolderBrowserDialog())
            {
                dlg.Description = "选择包含 NCM / FLAC / WAV 的文件夹";
                if (dlg.ShowDialog(this) == DialogResult.OK)
                    StartConvert(new string[] { dlg.SelectedPath });
            }
        }

        private void BrowseOutDir()
        {
            using (FolderBrowserDialog dlg = new FolderBrowserDialog())
            {
                dlg.Description = "选择输出目录";
                if (dlg.ShowDialog(this) == DialogResult.OK)
                    _txtOutDir.Text = dlg.SelectedPath;
            }
        }

        // ── 拖拽 ──────────────────────────────────────────────────────

        private void OnDragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effect = DragDropEffects.Copy;
                if (_dropPanel != null)
                {
                    _dropPanel.BackColor = Color.FromArgb(0x2f, 0x2f, 0x4e);
                    _dropLabel.ForeColor = Theme.Accent;
                }
            }
        }

        private void OnDragDrop(object sender, DragEventArgs e)
        {
            if (_dropPanel != null)
            {
                _dropPanel.BackColor = Theme.Panel;
                _dropLabel.ForeColor = Theme.Dim;
            }
            if (!e.Data.GetDataPresent(DataFormats.FileDrop))
                return;

            string[] paths = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (paths != null && paths.Length > 0)
                StartConvert(paths);
        }

        // ── 转换 ──────────────────────────────────────────────────────

        private void StartConvert(IEnumerable<string> paths)
        {
            if (_worker != null && _worker.IsBusy)
            {
                MessageBox.Show(this, "还有任务正在转换，请稍候。", "NCM 转换器",
                                MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            List<string> targets = Converter.ExpandInputs(paths, true);
            targets = targets.FindAll(delegate(string p)
            {
                string ext = Path.GetExtension(p).ToLowerInvariant();
                return ext == ".ncm" || ext == ".flac" || ext == ".wav";
            });

            if (targets.Count == 0)
            {
                MessageBox.Show(this, "没有找到 .ncm / .flac / .wav 文件。", "NCM 转换器",
                                MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            _progress.Maximum = targets.Count;
            _progress.Value = 0;
            Log("开始转换 " + targets.Count + " 个文件", Theme.Dim);
            _statusLabel.Text = "转换中... 0/" + targets.Count;
            _btnSelect.Enabled = false;

            ConvertArgs args = new ConvertArgs();
            args.Files = targets;
            args.OutDir = _txtOutDir.Text.Trim();
            args.Overwrite = _cbOverwrite.Checked;
            args.Cover = _cbCover.Checked;
            args.ToMp3 = _cbToMp3.Checked;

            _worker = new BackgroundWorker();
            _worker.WorkerReportsProgress = true;
            _worker.DoWork += WorkerDoWork;
            _worker.ProgressChanged += WorkerProgress;
            _worker.RunWorkerCompleted += WorkerCompleted;
            _worker.RunWorkerAsync(args);
        }

        private void WorkerDoWork(object sender, DoWorkEventArgs e)
        {
            BackgroundWorker w = (BackgroundWorker)sender;
            ConvertArgs args = (ConvertArgs)e.Argument;

            int ok = 0, failed = 0, skipped = 0;
            for (int i = 0; i < args.Files.Count; i++)
            {
                ConvertResult r = Converter.Convert(args.Files[i], args.OutDir, args.Overwrite,
                                                    args.Cover, args.ToMp3);
                if (r.Success) ok++;
                else if (r.Skipped) skipped++;
                else failed++;

                WorkerState st = new WorkerState();
                st.Index = i + 1;
                st.Total = args.Files.Count;
                st.FileName = Path.GetFileName(args.Files[i]);
                st.Result = r;
                st.Ok = ok;
                st.Failed = failed;
                st.Skipped = skipped;
                w.ReportProgress(i, st);
            }

            WorkerState final = new WorkerState();
            final.Index = args.Files.Count;
            final.Total = args.Files.Count;
            final.Last = true;
            final.Ok = ok;
            final.Failed = failed;
            final.Skipped = skipped;
            e.Result = final;
        }

        private void WorkerProgress(object sender, ProgressChangedEventArgs e)
        {
            WorkerState st = (WorkerState)e.UserState;
            _progress.Value = st.Index;

            if (st.Result != null)
            {
                if (st.Result.Success)
                    Log("  [OK] " + st.Result.Message, Theme.Green);
                else if (st.Result.Skipped)
                    Log("  [跳过] " + st.Result.Message, Theme.Yellow);
                else
                    Log("  [失败] " + st.FileName + ": " + st.Result.Message, Theme.Red);
            }

            _statusLabel.Text = "转换中... " + st.Index + "/" + st.Total +
                                "，成功 " + st.Ok + "，失败 " + st.Failed + "，跳过 " + st.Skipped;
        }

        private void WorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            _btnSelect.Enabled = true;
            if (e.Error != null)
            {
                _progress.Value = 0;
                _statusLabel.Text = "转换出错";
                Log("异常: " + e.Error.Message, Theme.Red);
                return;
            }

            WorkerState st = (WorkerState)e.Result;
            _progress.Value = _progress.Maximum;
            string summary = "完成！成功 " + st.Ok + "，失败 " + st.Failed + "，跳过 " + st.Skipped;
            _statusLabel.Text = summary;
            Log(summary, st.Failed > 0 ? Theme.Yellow : Theme.Green);
            _worker.Dispose();
            _worker = null;
        }

        // ── 日志 ──────────────────────────────────────────────────────

        private void Log(string text, Color color)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action<string, Color>(Log), text, color);
                return;
            }
            int start = _logBox.TextLength;
            _logBox.AppendText(text + "\n");
            _logBox.Select(start, text.Length);
            _logBox.SelectionColor = color;
            _logBox.Select(_logBox.TextLength, 0);
            _logBox.ScrollToCaret();
        }

        private sealed class ConvertArgs
        {
            public List<string> Files;
            public string OutDir;
            public bool Overwrite;
            public bool Cover;
            public bool ToMp3;
        }

        private sealed class WorkerState
        {
            public int Index;
            public int Total;
            public string FileName;
            public ConvertResult Result;
            public int Ok;
            public int Failed;
            public int Skipped;
            public bool Last;
        }
    }
}
