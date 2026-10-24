using System.Text;

namespace EVE预警
{
    internal static class Program
    {
        /// <summary>
        /// 程序入口：捕获启动期与 UI 线程异常，写入日志，避免“双击无反应”。
        /// </summary>
        [STAThread]
        static void Main()
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (_, e) => ReportFatal(e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (_, e) => ReportFatal(e.ExceptionObject as Exception);

            WriteStartupMarker();

            try
            {
                ApplicationConfiguration.Initialize();
                Application.Run(new Main());
            }
            catch (Exception ex)
            {
                ReportFatal(ex);
            }
        }

        private static string AppDataDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EVE预警");

        private static void WriteStartupMarker()
        {
            try
            {
                Directory.CreateDirectory(AppDataDir);
                string info =
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] EVE预警 启动 | exe={Environment.ProcessPath} | " +
                    $"OS={Environment.OSVersion} | x64={Environment.Is64BitProcess} | " +
                    $".NET={Environment.Version}";
                File.WriteAllText(Path.Combine(AppDataDir, "startup.log"), info + Environment.NewLine);
            }
            catch
            {
                // 日志写入失败不阻塞启动
            }
        }

        private static void ReportFatal(Exception? ex)
        {
            string detail = ex?.ToString() ?? "未知异常";
            string logPath = Path.Combine(AppDataDir, "startup-error.log");
            try
            {
                Directory.CreateDirectory(AppDataDir);
                File.AppendAllText(logPath, $"[{DateTime.Now:O}] {detail}{Environment.NewLine}{Environment.NewLine}");
            }
            catch
            {
                // 忽略日志错误
            }

            try
            {
                MessageBox.Show(
                    $"程序发生错误，详细信息已写入：\n{logPath}\n\n{ex?.Message}",
                    "EVE预警 - 运行错误",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            catch
            {
                // 窗口都创建不了时只能依赖日志
            }
        }
    }
}
