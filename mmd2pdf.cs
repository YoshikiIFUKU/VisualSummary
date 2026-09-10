// mmd2pdf - Mermaid 記法のテキストを図にして PDF を生成・表示する
// ビルド: build.bat（Windows 標準の .NET Framework csc.exe を使用、SDK 不要）
// 描画: exe に埋め込んだ mermaid.js を、Windows 標準の Microsoft Edge（ヘッドレス）で実行して PDF 化する
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

[assembly: AssemblyTitle("mmd2pdf")]
[assembly: AssemblyVersion("1.0.1.0")]

static class Program
{
    [DllImport("kernel32.dll")]
    static extern uint GetConsoleProcessList(uint[] list, uint count);

    static readonly string[] Themes = { "default", "neutral", "dark", "forest", "base" };

    static int Main(string[] args)
    {
        ConfigureOutput(args);
        int code = Run(args);
        // ダブルクリックやドラッグ＆ドロップで起動した場合、結果を読めるように待つ
        if (code != 0 && OwnsConsole() && !Console.IsInputRedirected)
        {
            Console.WriteLine();
            Console.WriteLine("何かキーを押すと終了します...");
            Console.ReadKey(true);
        }
        return code;
    }

    static Encoding ParseEncoding(string name)
    {
        switch (name.ToLowerInvariant())
        {
            case "utf8": case "utf-8": return new UTF8Encoding(false);
            case "sjis": case "shift_jis": case "cp932": return Encoding.GetEncoding(932);
            default: return null;
        }
    }

    // 標準出力・標準エラーが他のプログラムに渡される（リダイレクトされる）場合は、
    // コンソールの設定に左右されないよう、既定でシステムの ANSI コードページ（日本語 Windows では Shift_JIS）で書き出す。
    // --encoding で明示指定もできる。画面に直接出す場合はコンソールの設定のまま
    static void ConfigureOutput(string[] args)
    {
        Encoding enc = Encoding.Default;
        int i = Array.FindIndex(args, a => a == "-e" || a == "--encoding");
        if (i >= 0 && i + 1 < args.Length && ParseEncoding(args[i + 1]) != null) enc = ParseEncoding(args[i + 1]);
        if (Console.IsOutputRedirected)
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), enc) { AutoFlush = true });
        if (Console.IsErrorRedirected)
            Console.SetError(new StreamWriter(Console.OpenStandardError(), enc) { AutoFlush = true });
    }

    static bool OwnsConsole()
    {
        try { return GetConsoleProcessList(new uint[2], 2) <= 1; } catch { return false; }
    }

    static int Run(string[] args)
    {
        string input = null, output = null, theme = "default";
        bool open = true, overwrite = false;

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a == "-o" || a == "--output")
            {
                if (++i >= args.Length) return Usage("-o の後に出力先の PDF パスを指定してください。");
                output = args[i];
            }
            else if (a == "-t" || a == "--theme")
            {
                if (++i >= args.Length) return Usage("--theme の後にテーマ名を指定してください。");
                theme = args[i].ToLowerInvariant();
                if (Array.IndexOf(Themes, theme) < 0) return Usage("未対応のテーマです: " + args[i]);
            }
            else if (a == "-e" || a == "--encoding")
            {
                if (++i >= args.Length) return Usage("--encoding の後に utf8 または sjis を指定してください。");
                if (ParseEncoding(args[i]) == null) return Usage("未対応の文字コードです: " + args[i]);
            }
            else if (a == "--no-open") open = false;
            else if (a == "-y" || a == "--overwrite") overwrite = true;
            else if (a == "-h" || a == "--help" || a == "/?") { Usage(null); return 0; }
            else if (a.Length > 1 && a[0] == '-') return Usage("不明なオプションです: " + a);
            else if (input == null) input = a;
            else return Usage("引数が多すぎます: " + a);
        }
        // 入力ファイルの指定がなく、標準入力にデータが渡されていればそれを読む
        if (input == null && Console.IsInputRedirected) input = "-";
        if (input == null) return Usage(null);

        string text;
        if (input == "-")
        {
            using (var stdin = Console.OpenStandardInput()) text = DecodeText(ReadAll(stdin));
            if (output == null) output = Path.Combine(Environment.CurrentDirectory, "diagram.pdf");
        }
        else
        {
            if (!File.Exists(input)) return Fail("入力ファイルが見つかりません: " + input);
            text = DecodeText(File.ReadAllBytes(input));
            if (output == null) output = Path.ChangeExtension(Path.GetFullPath(input), ".pdf");
        }
        output = Path.GetFullPath(output);
        if (File.Exists(output) && !overwrite)
            return Fail("出力先の PDF が既に存在します（上書きするには --overwrite を指定してください）: " + output);

        List<string> diagrams = ExtractDiagrams(text);
        if (diagrams.Count == 0) return Fail("Mermaid の図が見つかりません（入力が空です）。");

        string edge = FindEdge();
        if (edge == null) return Fail("Microsoft Edge が見つかりません。Edge をインストールしてください。");

        string work = Path.Combine(Path.GetTempPath(), "mmd2pdf_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            string html = Path.Combine(work, "diagram.html");
            File.WriteAllText(html, BuildHtml(diagrams, theme), new UTF8Encoding(false));

            try { if (File.Exists(output)) File.Delete(output); }
            catch (IOException) { return Fail("出力先の PDF が他のアプリで開かれているため上書きできません: " + output); }
            Directory.CreateDirectory(Path.GetDirectoryName(output));

            string err = RenderPdf(edge, html, output, Path.Combine(work, "profile"));
            if (err != null) return Fail(err);
        }
        finally
        {
            try { Directory.Delete(work, true); } catch { }
        }

        Console.WriteLine("PDF を生成しました（" + diagrams.Count + " ページ）: " + output);
        if (open)
        {
            try { Process.Start(new ProcessStartInfo(output) { UseShellExecute = true }); }
            catch (Exception e) { Console.Error.WriteLine("PDF を開けませんでした: " + e.Message); }
        }
        return 0;
    }

    static byte[] ReadAll(Stream s)
    {
        using (var ms = new MemoryStream()) { s.CopyTo(ms); return ms.ToArray(); }
    }

    // UTF-8（BOM 有無）/ UTF-16 / Shift_JIS を自動判別して文字列にする
    static string DecodeText(byte[] b)
    {
        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return Encoding.UTF8.GetString(b, 3, b.Length - 3);
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return Encoding.Unicode.GetString(b, 2, b.Length - 2);
        if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(b, 2, b.Length - 2);
        try { return new UTF8Encoding(false, true).GetString(b); }
        catch (DecoderFallbackException) { return Encoding.GetEncoding(932).GetString(b); }
    }

    // Markdown の ```mermaid ブロックがあればそれぞれを 1 図として取り出す。なければ全体を 1 図とみなす
    static List<string> ExtractDiagrams(string text)
    {
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var list = new List<string>();
        var fence = new Regex(@"^[ \t]*(`{3,}|~{3,})[ \t]*mermaid[^\n]*\n(.*?)^[ \t]*\1[ \t]*$",
            RegexOptions.Multiline | RegexOptions.Singleline);
        foreach (Match m in fence.Matches(text))
            if (m.Groups[2].Value.Trim().Length > 0) list.Add(m.Groups[2].Value);
        if (list.Count == 0 && text.Trim().Length > 0) list.Add(text);
        return list;
    }

    static string FindEdge()
    {
        var candidates = new List<string>();
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            using (var k = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\msedge.exe"))
                if (k != null && k.GetValue(null) is string) candidates.Add(((string)k.GetValue(null)).Trim('"'));
        }
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft\Edge\Application\msedge.exe"));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Microsoft\Edge\Application\msedge.exe"));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\Edge\Application\msedge.exe"));
        foreach (var c in candidates) if (File.Exists(c)) return c;
        return null;
    }

    static string RenderPdf(string edge, string html, string pdf, string profile)
    {
        string arguments = string.Format(
            "--headless --disable-gpu --no-first-run --no-default-browser-check --disable-extensions " +
            "--no-pdf-header-footer --virtual-time-budget=20000 --user-data-dir=\"{0}\" --print-to-pdf=\"{1}\" \"{2}\"",
            profile, pdf, new Uri(html).AbsoluteUri);
        var psi = new ProcessStartInfo(edge, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        using (var p = Process.Start(psi))
        {
            p.OutputDataReceived += delegate { };
            p.ErrorDataReceived += delegate { };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            if (!p.WaitForExit(90000))
            {
                try { p.Kill(); } catch { }
                return "Edge での PDF 生成がタイムアウトしました。";
            }
        }
        if (!File.Exists(pdf) || new FileInfo(pdf).Length == 0) return "PDF の生成に失敗しました。";
        return null;
    }

    static string BuildHtml(List<string> diagrams, string theme)
    {
        string mermaidJs;
        using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("mermaid.min.js"))
        using (var r = new StreamReader(s, Encoding.UTF8)) mermaidJs = r.ReadToEnd();

        var sources = new StringBuilder("[");
        for (int i = 0; i < diagrams.Count; i++)
        {
            if (i > 0) sources.Append(',');
            sources.Append(JsString(diagrams[i]));
        }
        sources.Append(']');

        return HtmlTemplate
            .Replace("__BG__", theme == "dark" ? "#1e1e1e" : "#ffffff")
            .Replace("__THEME__", JsString(theme))
            .Replace("__SOURCES__", sources.ToString())
            .Replace("__MERMAID__", mermaidJs.Replace("</script", "<\\/script"));
    }

    static string JsString(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (char c in s)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\t': sb.Append("\\t"); break;
                case '<': sb.Append("\\u003c"); break;
                case '>': sb.Append("\\u003e"); break;
                case '\u2028': sb.Append("\\u2028"); break;
                case '\u2029': sb.Append("\\u2029"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }

    // 各図を 1 ページとし、ページサイズを図の大きさにぴったり合わせる（名前付き @page）
    const string HtmlTemplate = @"<!doctype html>
<html><head><meta charset=""utf-8"">
<style>
html,body{margin:0;padding:0;background:__BG__;-webkit-print-color-adjust:exact;print-color-adjust:exact}
.box{box-sizing:border-box;display:block}
.err{margin:0;color:#c00;font:14px/1.6 Consolas,""Yu Gothic UI"",monospace;white-space:pre-wrap}
</style>
<script>__MERMAID__</script>
</head><body>
<script>
(async function(){
  var sources = __SOURCES__;
  var M = 32, css = '';
  mermaid.initialize({ startOnLoad:false, theme:__THEME__, securityLevel:'strict',
    fontFamily:'""Yu Gothic UI"",""Meiryo"",""Segoe UI"",sans-serif' });
  for (var i = 0; i < sources.length; i++) {
    var box = document.createElement('div');
    box.className = 'box';
    document.body.appendChild(box);
    var w, h;
    try {
      var res = await mermaid.render('mmd' + i, sources[i]);
      box.innerHTML = res.svg;
      var svg = box.querySelector('svg');
      var vb = svg.viewBox && svg.viewBox.baseVal;
      if (vb && vb.width) { w = vb.width; h = vb.height; }
      else { var b = svg.getBBox(); w = b.width; h = b.height; }
      svg.removeAttribute('style');
      svg.setAttribute('width', w);
      svg.setAttribute('height', h);
      svg.style.display = 'block';
    } catch (e) {
      var pre = document.createElement('pre');
      pre.className = 'err';
      pre.textContent = '図 ' + (i + 1) + ' の Mermaid 記法にエラーがあります:\n\n' + (e && e.message ? e.message : e) + '\n\n--- 入力 ---\n' + sources[i];
      box.appendChild(pre);
      w = 760; h = Math.max(300, pre.scrollHeight);
      var junk = document.getElementById('dmmd' + i); if (junk) junk.remove();
    }
    var pw = Math.ceil(w) + 2 * M, ph = Math.ceil(h) + 2 * M;
    css += '@page p' + i + '{size:' + pw + 'px ' + ph + 'px;margin:0}';
    box.style.page = 'p' + i;
    box.style.padding = M + 'px';
    box.style.width = pw + 'px';
    box.style.height = ph + 'px';
    box.style.overflow = 'hidden';
  }
  var st = document.createElement('style');
  st.textContent = css;
  document.head.appendChild(st);
})();
</script>
</body></html>";

    static int Usage(string error)
    {
        if (error != null) Console.Error.WriteLine("エラー: " + error + Environment.NewLine);
        Console.WriteLine(
@"mmd2pdf - Mermaid の図を PDF にして開きます

使い方:
  mmd2pdf <入力ファイル> [-o 出力.pdf] [--overwrite] [--theme テーマ] [--no-open]
  mmd2pdf -o 出力.pdf [--overwrite] [--theme テーマ] [--no-open] < 入力
      入力ファイルを省略すると標準入力から読み込みます（""-"" を指定しても同じ）

入力ファイル:
  .mmd などの Mermaid 記法のテキスト、または ```mermaid ブロックを含む Markdown。
  Markdown に複数の図があれば、1 図 1 ページの PDF になります。
  exe に入力ファイルをドラッグ＆ドロップしても使えます。
  文字コードは UTF-8 / UTF-16 / Shift_JIS を自動判別します。

オプション:
  -o, --output   出力 PDF のパス（省略時は入力と同じ場所・同じ名前の .pdf、
                 標準入力の場合はカレントフォルダの diagram.pdf）
  -y, --overwrite  出力 PDF が既にあれば上書きする（指定しない場合はエラー）
  -t, --theme    default / neutral / dark / forest / base（省略時は default）
  -e, --encoding メッセージをリダイレクトで受け取る場合の文字コード utf8 / sjis
                 （省略時はシステム既定。日本語 Windows では Shift_JIS）
  --no-open      生成後に PDF を開かない

動作環境: Windows 10/11（Microsoft Edge を使用。追加インストール不要）");
        return error == null ? 1 : 2;
    }

    static int Fail(string message)
    {
        Console.Error.WriteLine("エラー: " + message);
        return 1;
    }
}
