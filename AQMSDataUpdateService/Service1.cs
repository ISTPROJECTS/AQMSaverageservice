using AQMSDataUpdateLibrary;
using System;
using System.Collections.Specialized;
using System.Configuration;
using System.IO;
using System.ServiceProcess;
using System.Timers;

namespace AQMSDataUpdateService
{
    //Average Service for Server application
    public partial class Service1 : ServiceBase
    {
        Timer timer1 = new Timer();
        public Service1()
        {
            InitializeComponent();
        }

        protected override void OnStart(string[] args)
        {
            timer1.Elapsed += new ElapsedEventHandler(timer1_Elapsed);
            timer1.Interval = Convert.ToDouble(ConfigurationManager.AppSettings["Interval"].ToString());
            timer1.Enabled = true;
            timer1.Start();
        }

        protected override void OnStop()
        {
            timer1.Stop();
            //Log LogObj = new Log();
            //LogObj.writeLog("Average Data service stopped: " + DateTime.Now.ToString(),ConfigurationManager.AppSettings["LogfilePath"].ToString());
        }

        private void timer1_Elapsed(object sender, EventArgs e)
        {
            timer1.Stop();
            try
            {
                string strConnectionString = ConfigurationManager.ConnectionStrings[ConfigurationManager.AppSettings["envConString"].ToString()].ConnectionString;
                var appSettingsSection = ConfigurationManager.GetSection("appSettings") as NameValueCollection;
                AvgDataCalculationServer clsObj = new AvgDataCalculationServer(appSettingsSection);
                bool blnStatus = clsObj.CalculateParameterAvgs(strConnectionString);
            }
            catch (Exception ex)
            {
                try
                {
                    string logPath = @"C:\AQMSLogs\ServiceErrors.txt";
                    Directory.CreateDirectory(Path.GetDirectoryName(logPath));
                    File.AppendAllText(logPath, $"[{DateTime.Now}] timer1_Elapsed error: {ex.Message}{Environment.NewLine}{ex.StackTrace}{Environment.NewLine}");
                }
                catch { /* last-resort logging failed — nothing more we can do */ }
            }
            finally
            {
                timer1.Start();
            }
        }
    }
}
