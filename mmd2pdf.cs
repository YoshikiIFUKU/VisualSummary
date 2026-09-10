// mmd2pdf - Mermaid 記法のテキストを図にして PDF を生成・表示する
// ビルド: build.bat（Windows 標準の .NET Framework csc.exe を使用、SDK 不要）
// 描画: exe に埋め込んだ mermaid.js を、Windows 標準の Microsoft Edge（ヘッドレス）で実行して PDF 化する
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

[assembly: AssemblyTitle("mmd2pdf")]
[assembly: AssemblyVersion("1.0.4.0")]

static class Program
{
    [DllImport("kernel32.dll")]
    static extern uint GetConsoleProcessList(uint[] list, uint count);

    // ---- SYSTEM からログイン中のユーザーとして実行し直すための Win32 API ----
    [DllImport("kernel32.dll")]
    static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("wtsapi32.dll", SetLastError = true)]
    static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);
    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool WTSEnumerateSessions(IntPtr server, int reserved, int version, out IntPtr info, out int count);
    [DllImport("wtsapi32.dll")]
    static extern void WTSFreeMemory(IntPtr memory);
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool DuplicateTokenEx(IntPtr token, uint access, IntPtr attributes, int impersonationLevel, int tokenType, out IntPtr newToken);
    [DllImport("userenv.dll", SetLastError = true)]
    static extern bool CreateEnvironmentBlock(out IntPtr env, IntPtr token, bool inherit);
    [DllImport("userenv.dll", SetLastError = true)]
    static extern bool DestroyEnvironmentBlock(IntPtr env);
    [DllImport("userenv.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool GetUserProfileDirectory(IntPtr token, StringBuilder path, ref uint size);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcessAsUser(IntPtr token, string application, StringBuilder commandLine, IntPtr processAttributes,
        IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr env, string currentDirectory,
        ref STARTUPINFO startupInfo, out PROCESS_INFORMATION processInfo);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool TerminateProcess(IntPtr process, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO
    {
        public int cb;
        public string lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WTS_SESSION_INFO
    {
        public int SessionId;
        public string WinStationName;
        public int State;
    }

    const uint MAXIMUM_ALLOWED = 0x02000000;
    const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    const uint CREATE_NO_WINDOW = 0x08000000;
    const int WTSActive = 0;
    const uint CREATE_BREAKAWAY_FROM_JOB = 0x01000000;

    [DllImport("kernel32.dll")]
    static extern IntPtr GetConsoleWindow();
    [DllImport("user32.dll")]
    static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("kernel32.dll")]
    static extern bool FreeConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool IsProcessInJob(IntPtr process, IntPtr job, out bool result);
    [DllImport("kernel32.dll")]
    static extern IntPtr GetCurrentProcess();

    // 自分がジョブオブジェクト内で動いているか（サービスが子プロセスをジョブで管理していると Edge が制限を受けることがある）
    static string JobState()
    {
        try
        {
            bool inJob;
            if (IsProcessInJob(GetCurrentProcess(), IntPtr.Zero, out inJob)) return inJob ? "あり" : "なし";
        }
        catch { }
        return "不明";
    }

    // 子プロセスとして実行されているとき、結果を書き出すファイル
    static string childResultPath;

    // ログイン中のユーザーのトークンを取得する（物理コンソールのセッションを優先し、なければリモートデスクトップなどの有効なセッション）
    static bool QueryActiveUserToken(out IntPtr token, out uint session)
    {
        session = WTSGetActiveConsoleSessionId();
        if (session != 0xFFFFFFFF && WTSQueryUserToken(session, out token)) return true;
        token = IntPtr.Zero;
        IntPtr info;
        int count;
        if (!WTSEnumerateSessions(IntPtr.Zero, 0, 1, out info, out count)) return false;
        try
        {
            int size = Marshal.SizeOf(typeof(WTS_SESSION_INFO));
            for (int i = 0; i < count; i++)
            {
                var s = (WTS_SESSION_INFO)Marshal.PtrToStructure(new IntPtr(info.ToInt64() + i * size), typeof(WTS_SESSION_INFO));
                if (s.State == WTSActive && WTSQueryUserToken((uint)s.SessionId, out token))
                {
                    session = (uint)s.SessionId;
                    return true;
                }
            }
        }
        finally { WTSFreeMemory(info); }
        return false;
    }

    static string Win32Message(string what)
    {
        return what + "（" + new Win32Exception(Marshal.GetLastWin32Error()).Message + "）";
    }

    static string Quote(string s)
    {
        return "\"" + s + "\"";
    }

    // SYSTEM から、ログイン中のユーザーとして自分自身を起動し直し、PDF の生成と表示を任せる
    static int RunInUserSession(byte[] raw, string output, string theme, bool open)
    {
        IntPtr userToken = IntPtr.Zero, primary = IntPtr.Zero, env = IntPtr.Zero;
        string inFile = null, resultFile = null;
        try
        {
            uint session;
            if (!QueryActiveUserToken(out userToken, out session))
                return Fail("SYSTEM アカウントでは Edge を起動できないため、ログイン中のユーザーとして実行しようとしましたが、" +
                    "ログインしているユーザーが見つかりませんでした。" + Win32Message(""));
            if (!DuplicateTokenEx(userToken, MAXIMUM_ALLOWED, IntPtr.Zero, 2 /* SecurityImpersonation */, 1 /* TokenPrimary */, out primary))
                return Fail(Win32Message("ユーザーのトークンを複製できませんでした"));

            // 入力と結果の受け渡しは、ユーザーが読み書きできるユーザーの一時フォルダで行う
            var profile = new StringBuilder(260);
            uint len = (uint)profile.Capacity;
            if (!GetUserProfileDirectory(primary, profile, ref len))
                return Fail(Win32Message("ユーザーのプロファイルフォルダを取得できませんでした"));
            string temp = Path.Combine(profile.ToString(), @"AppData\Local\Temp");
            Directory.CreateDirectory(temp);
            string id = Guid.NewGuid().ToString("N");
            inFile = Path.Combine(temp, "mmd2pdf_" + id + ".in");
            resultFile = Path.Combine(temp, "mmd2pdf_" + id + ".result");
            File.WriteAllBytes(inFile, raw);

            Log("ジョブオブジェクト内で実行: " + JobState());
            if (!CreateEnvironmentBlock(out env, primary, false))
            {
                Log(Win32Message("ユーザーの環境変数を作成できなかったため、現在の環境変数で起動します"));
                env = IntPtr.Zero;
            }
            string exe = Assembly.GetExecutingAssembly().Location;
            string dir = Path.GetDirectoryName(exe);
            var childArgs = new StringBuilder();
            childArgs.Append(Quote(inFile))
               .Append(" -o ").Append(Quote(output))
               .Append(" --overwrite --theme ").Append(theme)
               .Append(" --child-result ").Append(Quote(resultFile));
            if (!open) childArgs.Append(" --no-open");

            string userName;
            using (var wi = new WindowsIdentity(primary)) userName = wi.Name;
            Log("SYSTEM アカウントで実行されているため、ログイン中のユーザー " + userName + "（セッション " + session + "）として実行し直します。");

            // まずタスクスケジューラー経由で起動する（ユーザーが自分で起動したのとほぼ同じ環境になる）
            uint code;
            int? taskCode = RunViaTaskScheduler(userName, exe, childArgs.ToString(), dir, resultFile);
            if (taskCode.HasValue) code = (uint)taskCode.Value;
            else
            {
                Log("タスクスケジューラーで起動できなかったため、ユーザーとして直接起動します。");
                string cmd = Quote(exe) + " " + childArgs;
                var si = new STARTUPINFO();
                si.cb = Marshal.SizeOf(typeof(STARTUPINFO));
                si.lpDesktop = @"winsta0\default";
                PROCESS_INFORMATION pi;
                // 呼び出し元のジョブオブジェクトの制限を受けないよう、まずジョブから切り離して起動する（許可されていなければ切り離さずに起動）
                uint flags = CREATE_UNICODE_ENVIRONMENT | CREATE_NO_WINDOW;
                bool started = CreateProcessAsUser(primary, exe, new StringBuilder(cmd), IntPtr.Zero, IntPtr.Zero, false,
                    flags | CREATE_BREAKAWAY_FROM_JOB, env, dir, ref si, out pi);
                if (started) Log("ジョブオブジェクトから切り離して起動しました。");
                else
                {
                    Log(Win32Message("ジョブオブジェクトから切り離して起動できなかったため、切り離さずに起動します"));
                    started = CreateProcessAsUser(primary, exe, new StringBuilder(cmd), IntPtr.Zero, IntPtr.Zero, false,
                        flags, env, dir, ref si, out pi);
                }
                if (!started)
                    return Fail(Win32Message("ログイン中のユーザーとして起動できませんでした"));

                try
                {
                    if (WaitForSingleObject(pi.hProcess, 300000) != 0)
                    {
                        TerminateProcess(pi.hProcess, 1);
                        return Fail("ログイン中のユーザーとして実行した処理がタイムアウトしました。");
                    }
                    GetExitCodeProcess(pi.hProcess, out code);
                }
                finally
                {
                    CloseHandle(pi.hThread);
                    CloseHandle(pi.hProcess);
                }
            }

            string result = File.Exists(resultFile) ? File.ReadAllText(resultFile, Encoding.UTF8).TrimEnd() : "（結果ファイルがありません）";
            Log("---- ユーザーとして実行した結果 ここから ----");
            Log(result);
            Log("---- ユーザーとして実行した結果 ここまで ----");

            // 呼び出し元には、子プロセスの結果行・エラー行をそのまま返す
            bool inError = false;
            foreach (string l in result.Replace("\r\n", "\n").Split('\n'))
            {
                if (l.StartsWith("結果: ")) Console.WriteLine(l.Substring(4));
                else if (l.StartsWith("エラー: ")) inError = true;
                else if (l.StartsWith("終了コード: ")) inError = false;
                if (inError) Console.Error.WriteLine(l);
            }
            if (code != 0 && !result.Contains("エラー: "))
                Console.Error.WriteLine("エラー: ログイン中のユーザーとして実行した処理が失敗しました（終了コード " + code + "）。");
            return (int)code;
        }
        finally
        {
            if (env != IntPtr.Zero) DestroyEnvironmentBlock(env);
            if (primary != IntPtr.Zero) CloseHandle(primary);
            if (userToken != IntPtr.Zero) CloseHandle(userToken);
            try { if (inFile != null) File.Delete(inFile); } catch { }
            try { if (resultFile != null) File.Delete(resultFile); } catch { }
            try { if (resultFile != null) File.Delete(resultFile + ".tmp"); } catch { }
        }
    }

    // タスクスケジューラーで、ログイン中のユーザーとして（対話型トークンで）子プロセスを実行する。
    // タスクを登録・実行できなかった場合は null、実行した場合は子プロセスの終了コードを返す。タスクは最後に必ず削除する
    static int? RunViaTaskScheduler(string userName, string exe, string arguments, string dir, string resultFile)
    {
        string name = "mmd2pdf_" + Guid.NewGuid().ToString("N");
        string xmlFile = Path.Combine(Path.GetTempPath(), name + ".xml");
        string xml =
            "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
            "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n" +
            "  <RegistrationInfo><Description>mmd2pdf の一時タスク（実行後に自動で削除されます）</Description></RegistrationInfo>\r\n" +
            "  <Principals><Principal id=\"Author\"><UserId>" + XmlEscape(userName) + "</UserId>" +
            "<LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>\r\n" +
            "  <Settings><MultipleInstancesPolicy>Parallel</MultipleInstancesPolicy>" +
            "<DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>" +
            "<ExecutionTimeLimit>PT10M</ExecutionTimeLimit><Hidden>true</Hidden><Priority>5</Priority></Settings>\r\n" +
            "  <Actions Context=\"Author\"><Exec><Command>" + XmlEscape(exe) + "</Command>" +
            "<Arguments>" + XmlEscape(arguments) + "</Arguments>" +
            "<WorkingDirectory>" + XmlEscape(dir) + "</WorkingDirectory></Exec></Actions>\r\n" +
            "</Task>\r\n";
        bool created = false, finished = false;
        try
        {
            File.WriteAllText(xmlFile, xml, Encoding.Unicode);
            string outText;
            if (Schtasks("/Create /TN " + Quote(name) + " /XML " + Quote(xmlFile) + " /F", out outText) != 0)
            {
                Log("タスクを登録できませんでした: " + outText);
                return null;
            }
            created = true;
            if (Schtasks("/Run /TN " + Quote(name), out outText) != 0)
            {
                Log("タスクを実行できませんでした: " + outText);
                return null;
            }
            Log("タスクスケジューラーでユーザーとして起動しました（タスク名: " + name + "）。");

            // 子プロセスは終了時に結果ファイルを書き出す（一時ファイルから名前を変えるので、見つかった時点で書き込みは完了している）
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < 300)
            {
                if (File.Exists(resultFile))
                {
                    finished = true;
                    MatchCollection ms = Regex.Matches(File.ReadAllText(resultFile, Encoding.UTF8), @"^終了コード: (-?\d+)\r?$", RegexOptions.Multiline);
                    return ms.Count > 0 ? int.Parse(ms[ms.Count - 1].Groups[1].Value) : 1;
                }
                System.Threading.Thread.Sleep(500);
            }
            Log("タスクスケジューラーで起動した処理が 300 秒以内に終わりませんでした。");
            return 1;
        }
        catch (Exception e)
        {
            Log("タスクスケジューラーでの起動中にエラーが発生しました: " + e.Message);
            return created ? (int?)1 : null;
        }
        finally
        {
            string ignored;
            if (created && !finished) Schtasks("/End /TN " + Quote(name), out ignored);
            if (created) Schtasks("/Delete /TN " + Quote(name) + " /F", out ignored);
            try { File.Delete(xmlFile); } catch { }
        }
    }

    static int Schtasks(string arguments, out string output)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.Default,
            StandardErrorEncoding = Encoding.Default
        };
        using (var p = Process.Start(psi))
        {
            var err = p.StandardError.ReadToEndAsync();
            string o = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            output = (o + " " + err.Result).Trim();
            return p.ExitCode;
        }
    }

    static string XmlEscape(string s)
    {
        return System.Security.SecurityElement.Escape(s);
    }

    static string BuildLogBlock(string[] args, int code)
    {
        var sb = new StringBuilder();
        sb.AppendLine("==== " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  mmd2pdf " + Assembly.GetExecutingAssembly().GetName().Version);
        sb.AppendLine("引数: " + string.Join(" ", Array.ConvertAll(args, a => a.IndexOf(' ') >= 0 ? "\"" + a + "\"" : a)));
        sb.AppendLine("実行ユーザー: " + Environment.UserDomainName + "\\" + Environment.UserName +
            (Environment.UserInteractive ? "" : "（非対話セッション）"));
        sb.AppendLine("カレントフォルダ: " + Environment.CurrentDirectory);
        sb.Append(logBody);
        sb.AppendLine("終了コード: " + code);
        sb.AppendLine();
        return sb.ToString();
    }

    static readonly string[] Themes = { "default", "neutral", "dark", "forest", "base" };

    static int Main(string[] args)
    {
        ConfigureOutput(args);
        logPath = FindLogPath(args);
        int ci = Array.IndexOf(args, "--child-result");
        if (ci >= 0 && ci + 1 < args.Length) childResultPath = args[ci + 1];
        if (childResultPath != null)
        {
            // 子プロセス（タスクスケジューラーなどから起動）はコンソール画面を使わないので、すぐに隠して切り離す
            IntPtr w = GetConsoleWindow();
            if (w != IntPtr.Zero) ShowWindow(w, 0 /* SW_HIDE */);
            FreeConsole();
            Console.SetOut(TextWriter.Null);
            Console.SetError(TextWriter.Null);
        }
        int code = Run(args);
        WriteLog(args, code);
        // ユーザーとして実行し直された子プロセスは、結果を親（SYSTEM 側）に渡す
        if (childResultPath != null)
        {
            try
            {
                // 親が書きかけのファイルを読まないよう、一時ファイルに書いてから名前を変える
                string tmp = childResultPath + ".tmp";
                File.WriteAllText(tmp, BuildLogBlock(args, code), new UTF8Encoding(false));
                if (File.Exists(childResultPath)) File.Delete(childResultPath);
                File.Move(tmp, childResultPath);
            }
            catch { }
            return code;
        }
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

    // --log または環境変数 MMD2PDF_LOG で指定されたファイルに、受け取った入力と結果を追記する（原因調査用）
    static string logPath;
    static readonly StringBuilder logBody = new StringBuilder();

    static string FindLogPath(string[] args)
    {
        int i = Array.IndexOf(args, "--log");
        if (i >= 0 && i + 1 < args.Length) return args[i + 1];
        string env = Environment.GetEnvironmentVariable("MMD2PDF_LOG");
        return string.IsNullOrWhiteSpace(env) ? null : env;
    }

    static void Log(string line)
    {
        logBody.AppendLine(line);
    }

    static void WriteLog(string[] args, int code)
    {
        if (logPath == null) return;
        try
        {
            string full = Path.GetFullPath(logPath);
            string dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            // BOM 付き UTF-8（新規作成時のみ BOM が入る）にして、メモ帳でも文字化けしないようにする
            File.AppendAllText(full, BuildLogBlock(args, code), new UTF8Encoding(true));
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("ログを書き込めませんでした: " + e.Message);
        }
    }

    static bool OwnsConsole()
    {
        try { return GetConsoleProcessList(new uint[2], 2) == 1; } catch { return false; }
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
            else if (a == "--child-result")
            {
                // 内部用: SYSTEM から起動し直された子プロセスが結果を書き出すファイル
                if (++i >= args.Length) return Usage("--child-result の後にパスを指定してください。");
            }
            else if (a == "--log")
            {
                if (++i >= args.Length) return Usage("--log の後にログファイルのパスを指定してください。");
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

        byte[] raw;
        if (input == "-")
        {
            using (var stdin = Console.OpenStandardInput()) raw = ReadAll(stdin);
            if (output == null) output = Path.Combine(Environment.CurrentDirectory, "diagram.pdf");
        }
        else
        {
            if (!File.Exists(input)) return Fail("入力ファイルが見つかりません: " + input);
            raw = File.ReadAllBytes(input);
            if (output == null) output = Path.ChangeExtension(Path.GetFullPath(input), ".pdf");
        }
        string encName;
        string text = DecodeText(raw, out encName);
        Log("入力: " + (input == "-" ? "標準入力" : Path.GetFullPath(input)) + "（" + raw.Length + " バイト、文字コード: " + encName + "）");
        Log("先頭バイト: " + (raw.Length == 0 ? "（なし）" : BitConverter.ToString(raw, 0, Math.Min(raw.Length, 16))));
        Log("---- 受け取った内容 ここから ----");
        Log(text.TrimEnd('\r', '\n'));
        Log("---- 受け取った内容 ここまで ----");
        output = Path.GetFullPath(output);
        if (File.Exists(output) && !overwrite)
            return Fail("出力先の PDF が既に存在します（上書きするには --overwrite を指定してください）: " + output);

        // SYSTEM アカウントでは Edge が起動しないため、ログイン中のユーザーとして実行し直す
        if (childResultPath == null && WindowsIdentity.GetCurrent().IsSystem)
            return RunInUserSession(raw, output, theme, open);

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

        string done = "PDF を生成しました（" + diagrams.Count + " ページ）: " + output;
        Console.WriteLine(done);
        Log("結果: " + done);
        if (open && !Environment.UserInteractive)
        {
            // サービスなど非対話セッションから起動された場合、PDF を開いてもユーザーの画面には表示されない
            Console.Error.WriteLine("非対話セッション（サービスなど）で実行されているため、PDF は開きません。");
            Log("注意: 非対話セッションで実行されているため、PDF は開きませんでした。");
            open = false;
        }
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
    static string DecodeText(byte[] b, out string name)
    {
        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) { name = "UTF-8（BOM 付き）"; return Encoding.UTF8.GetString(b, 3, b.Length - 3); }
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) { name = "UTF-16 LE"; return Encoding.Unicode.GetString(b, 2, b.Length - 2); }
        if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) { name = "UTF-16 BE"; return Encoding.BigEndianUnicode.GetString(b, 2, b.Length - 2); }
        try
        {
            string s = new UTF8Encoding(false, true).GetString(b);
            name = "UTF-8";
            return s;
        }
        catch (DecoderFallbackException)
        {
            name = "Shift_JIS";
            return Encoding.GetEncoding(932).GetString(b);
        }
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
        string edgeVersion;
        try { edgeVersion = FileVersionInfo.GetVersionInfo(edge).FileVersion; } catch { edgeVersion = "不明"; }
        Log("実行環境: Edge " + edgeVersion + "、ジョブオブジェクト内: " + JobState() +
            "、TEMP=" + Path.GetTempPath() + "、LOCALAPPDATA=" + Environment.GetEnvironmentVariable("LOCALAPPDATA") +
            "、USERPROFILE=" + Environment.GetEnvironmentVariable("USERPROFILE"));
        string err = RunEdge(edge, html, pdf, profile + "1", false);
        if (err == null) return null;
        // サービス（SYSTEM アカウントなど）で実行すると Edge のサンドボックスが起動に失敗しやすいため、無効にして再試行する
        Log("1 回目の Edge 実行で PDF を生成できなかったため、--no-sandbox を付けて再試行します。");
        string err2 = RunEdge(edge, html, pdf, profile + "2", true);
        if (err2 == null)
        {
            Log("--no-sandbox での再試行で PDF を生成できました。");
            return null;
        }
        return "PDF の生成に失敗しました。" + Environment.NewLine +
            "  [1 回目]" + err + Environment.NewLine +
            "  [2 回目: --no-sandbox]" + err2;
    }

    // Edge で PDF を 1 回生成する。成功なら null、失敗なら原因調査用の詳細を返す
    static string RunEdge(string edge, string html, string pdf, string profile, bool noSandbox)
    {
        string arguments = string.Format(
            "--headless --disable-gpu --no-first-run --no-default-browser-check --disable-extensions " +
            "--enable-logging=stderr --v=0 " + (noSandbox ? "--no-sandbox " : "") +
            "--no-pdf-header-footer --virtual-time-budget=20000 --user-data-dir=\"{0}\" --print-to-pdf=\"{1}\" \"{2}\"",
            profile, pdf, new Uri(html).AbsoluteUri);
        var psi = new ProcessStartInfo(edge, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        // 失敗時の原因調査用に Edge の出力を残しておく
        var log = new List<string>();
        DataReceivedEventHandler collect = delegate(object sender, DataReceivedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(e.Data)) lock (log) log.Add(e.Data.Trim());
        };
        int exitCode;
        using (var p = Process.Start(psi))
        {
            p.OutputDataReceived += collect;
            p.ErrorDataReceived += collect;
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            if (!p.WaitForExit(90000))
            {
                try { p.Kill(); } catch { }
                return "（90 秒でタイムアウト）" + EdgeDetail(edge, pdf, null, log);
            }
            p.WaitForExit(); // 非同期で読んでいる出力を最後まで受け取る
            exitCode = p.ExitCode;
        }
        if (!File.Exists(pdf) || new FileInfo(pdf).Length == 0)
            return EdgeDetail(edge, pdf, exitCode, log);
        return null;
    }

    static string EdgeDetail(string edge, string pdf, int? exitCode, List<string> log)
    {
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("  Edge: " + edge);
        sb.AppendLine("  出力先: " + pdf);
        if (exitCode.HasValue)
            sb.AppendLine("  Edge の終了コード: " + exitCode.Value + "（0x" + ((uint)exitCode.Value).ToString("X8") + "）" +
                ((uint)exitCode.Value == 0xC0000005 ? " アクセス違反で Edge が異常終了しました" : ""));
        sb.AppendLine("  実行ユーザー: " + Environment.UserDomainName + "\\" + Environment.UserName +
            (Environment.UserInteractive ? "" : "（非対話セッション）"));
        lock (log)
        {
            if (log.Count == 0) sb.Append("  Edge からの出力はありませんでした。");
            else
            {
                sb.AppendLine("  Edge からの出力（最後の " + Math.Min(log.Count, 15) + " 行）:");
                for (int i = Math.Max(0, log.Count - 15); i < log.Count; i++) sb.AppendLine("    " + log[i]);
            }
        }
        return sb.ToString().TrimEnd();
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
        if (error != null)
        {
            Console.Error.WriteLine("エラー: " + error + Environment.NewLine);
            Log("エラー: " + error);
        }
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
  --log ファイル  受け取った入力の内容と結果をログファイルに追記する（UTF-8）
                 環境変数 MMD2PDF_LOG にパスを設定しても有効になる
  --no-open      生成後に PDF を開かない

動作環境: Windows 10/11（Microsoft Edge を使用。追加インストール不要）");
        return error == null ? 1 : 2;
    }

    static int Fail(string message)
    {
        Console.Error.WriteLine("エラー: " + message);
        Log("エラー: " + message);
        return 1;
    }
}
