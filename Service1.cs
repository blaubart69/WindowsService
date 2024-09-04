using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ProkopService1
{
    public partial class Service1 : ServiceBase
    {
        readonly string servicelog = @"c:\temp\ProkopSvc.log";
        readonly string reg_keyname = @"HKLM\Software\OntapGrepper";
        readonly string reg_name_filesscanned = "TotalFilesScanned";
        CancellationTokenSource cts;
        TextWriter logWriter;
        Task syslogTask;
        Task registryTask;
        RegistryKey regkey;
        Int64 ATOMIC_filesScanned;

        object loggerLock = new object();

        public Service1()
        {
            InitializeComponent();
        }
        ~Service1()
        {
            if (regkey != null)
            {
                regkey.Close();
            }
            if ( cts != null)
            {
                cts.Dispose();
            }
            if ( logWriter != null )
            {
                logWriter.Close();
            }
        }

        private void Log(string msg)
        {
            lock (loggerLock)
            {
                logWriter.WriteLine(msg);
                logWriter.Flush();
            }
        }
        void OnSyslogMessage(string msg)
        {
            if (msg.Contains("bumsti"))
            {
                Interlocked.Increment(ref ATOMIC_filesScanned);
            }
        }
        Int64 ReadNumberFromRegistry(string path)
        {
            return 
                Int64.Parse(regkey.GetValue(reg_name_filesscanned, "0").ToString());
        }
        void SyslogListener(CancellationToken ct)
        {
            UdpClient syslog = null;
            try
            {
                syslog = new UdpClient(514);
                Log($"Syslog listening: {syslog.Client.LocalEndPoint}");

                for (;;)
                {
                    var recvTask = syslog.ReceiveAsync();
                    recvTask.Wait(ct);
                    var msgBytes = recvTask.Result.Buffer;
                    if (msgBytes == null)
                    {
                        // seltsam
                    }
                    else if (msgBytes.Length == 0)
                    {
                        // seltsam 2
                    }
                    else
                    {
                        string msg = Encoding.UTF8.GetString(msgBytes);
                        Log($"MSG: {msg}");
                        OnSyslogMessage(msg);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                Log("I: Syslog stopping because of cancelation");
            }
            catch (Exception ex)
            {
                try 
                {
                    // try to inform somebody why we died
                    Log($"X: Syslog {ex}"); 
                }
                finally
                {
                    // wenn wir sterben, stirbt alles
                    base.Stop();
                }
            }
            finally 
            {
                if (syslog != null) 
                {
                    syslog.Close();
                }
            }
        }
        void RegistryUpdater(CancellationToken ct)
        {
            try
            {
                Int64 lastFilesScanned = 0;
                for (;;)
                {
                    Task.Delay(TimeSpan.FromSeconds(5)).Wait(ct);
                    var filesScanned = Interlocked.Read(ref ATOMIC_filesScanned);
                    if ( lastFilesScanned == filesScanned)
                    {
                        // do nothing
                    }
                    else
                    {
                        regkey.SetValue(reg_name_filesscanned, filesScanned.ToString(), RegistryValueKind.String);
                        regkey.Flush();
                        lastFilesScanned = filesScanned;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                Log("I: RegistryUpdater stopping because of cancelation");
            }
            catch (Exception ex)
            {
                Log($"X: RegistryUpdater {ex}");
            }
        }

        protected override void OnStart(string[] args)
        {
            Log("OnStart");
            logWriter = new StreamWriter(servicelog);
            ATOMIC_filesScanned = 0;
            cts = new CancellationTokenSource();
            regkey = Registry.LocalMachine.OpenSubKey(reg_keyname);
            syslogTask   = Task.Run(() => SyslogListener(cts.Token));
            registryTask = Task.Run(() => RegistryUpdater(cts.Token));
        }

        protected override void OnStop()
        {
            cts.Cancel();
            Log("I: OnStop called. pressing Cancel. Waiting for tasks to shutdown...");
            if ( Task.WaitAll(new Task[] { syslogTask, registryTask }, TimeSpan.FromSeconds(2) ) )
            {
                Log($"I: all tasks have been ended within 2 secs");
            }
            else
            {
                Log("W: tasks could not be ended gracefully");
            }

            logWriter.Close();
        }
    }
}
