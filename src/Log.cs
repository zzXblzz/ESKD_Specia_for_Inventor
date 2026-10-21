using System;
using System.IO;

namespace GroupSpecB
{
    /// <summary>Журнал ошибок надстройки: %APPDATA%\GroupSpecB\GroupSpecB.log. Ошибки, которые не показываются
    /// пользователю (сохранение не должно срываться из-за надстройки и т. п.), пишутся сюда, чтобы их можно было
    /// найти. Файл обрезается, когда превышает 1 МБ.</summary>
    public static class Log
    {
        const long MaxBytes = 1024 * 1024;
        static readonly object Sync = new object();

        public static string FilePath
        {
            get
            {
                return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                              "GroupSpecB", "GroupSpecB.log");
            }
        }

        public static void Write(string where, Exception ex)
        {
            Write(where + ": " + (ex == null ? "" : ex.GetType().Name + ": " + ex.Message +
                                  (ex.StackTrace != null ? "\r\n" + ex.StackTrace : "")));
        }

        public static void Write(string message)
        {
            try
            {
                lock (Sync)
                {
                    string path = FilePath;
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
                    FileInfo fi = new FileInfo(path);
                    if (fi.Exists && fi.Length > MaxBytes) File.Delete(path);
                    File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + "\r\n");
                }
            }
            catch { }   // журнал не должен ломать работу надстройки
        }
    }
}
