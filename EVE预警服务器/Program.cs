namespace EVE预警服务器
{
    internal static class Program
    {
        /// <summary>
        /// 程序入口：捕获启动期异常并写日志，便于 VM 无人值守排查。
        /// </summary>
        [STAThread]
        static void Main()
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (_, e) => ReportFatal(e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (_, e) => ReportFatal(e.ExceptionObject as Exception);

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

        private static void ReportFatal(Exception? ex)
        {
            string detail = ex?.ToString() ?? "未知异常";
            string logPath = Path.Combine(AppDataDir, "server-error.log");
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
                    $"服务器发生错误，详细信息已写入：\n{logPath}\n\n{ex?.Message}",
                    "EVE预警服务器 - 运行错误",
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
