﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Security;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ICSharpCode.SharpZipLib.GZip;
using ICSharpCode.SharpZipLib.Tar;
using ICSharpCode.SharpZipLib.Zip;
using Jackett.Common.Models.Config;
using Jackett.Common.Models.GitHub;
using Jackett.Common.Services.Interfaces;
using Jackett.Common.Utils;
using Jackett.Common.Utils.Clients;
using Newtonsoft.Json;
using NLog;

namespace Jackett.Common.Services
{

    public class UpdateService: IUpdateService
    {
        Logger logger;
        WebClient client;
        IConfigurationService configService;
        ManualResetEvent locker = new ManualResetEvent(false);
        ITrayLockService lockService;
        private ServerConfig serverConfig;
        bool forceupdatecheck = false;

        public UpdateService(Logger l, WebClient c, IConfigurationService cfg, ITrayLockService ls, ServerConfig sc)
        {
            logger = l;
            client = c;
            configService = cfg;
            lockService = ls;
            serverConfig = sc;
        }

        private string ExePath()
        {
            var location = new Uri(Assembly.GetEntryAssembly().GetName().CodeBase);
            return new FileInfo(location.AbsolutePath).FullName;
        }

        public void StartUpdateChecker()
        {
            Task.Factory.StartNew(UpdateWorkerThread);
        }

        public void CheckForUpdatesNow()
        {
            forceupdatecheck = true;
            locker.Set();
        }

        private async void UpdateWorkerThread()
        {
            var delayHours = 1; // first check after 1 hour (for users not running jackett 24/7)
            while (true)
            {
                locker.WaitOne((int)new TimeSpan(delayHours, 0, 0).TotalMilliseconds);
                locker.Reset();
                await CheckForUpdates();
                delayHours = 24; // following checks only once/24 hours
            }
        }

        private bool AcceptCert(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors)
        {
            return true;
        }

        private async Task CheckForUpdates()
        {
            if (serverConfig.RuntimeSettings.NoUpdates)
            {
                logger.Info($"Updates are disabled via --NoUpdates.");
                return;
            }
            if (serverConfig.UpdateDisabled && !forceupdatecheck)
            {
                logger.Info($"Skipping update check as it is disabled.");
                return;
            }

            forceupdatecheck = true;

            var isWindows = System.Environment.OSVersion.Platform != PlatformID.Unix;
            if (Debugger.IsAttached)
            {
                logger.Info($"Skipping checking for new releases as the debugger is attached.");
                return;
            }

            bool trayIsRunning = false;
            if (isWindows)
            {
                trayIsRunning = Process.GetProcessesByName("JackettTray").Length > 0;
            }

            try
            {

                var response = await client.GetString(new WebRequest()
                {
                    Url = "https://api.github.com/repos/Jackett/Jackett/releases",
                    Encoding = Encoding.UTF8,
                    EmulateBrowser = false
                });

                if(response.Status != System.Net.HttpStatusCode.OK)
                {
                    logger.Error("Failed to get the release list: " + response.Status);
                }

                var releases = JsonConvert.DeserializeObject<List<Release>>(response.Content);

                if (!serverConfig.UpdatePrerelease)
                {
                    releases = releases.Where(r => !r.Prerelease).ToList();
                }

                if (releases.Count > 0)
                {
                    var latestRelease = releases.OrderByDescending(o => o.Created_at).First();
                    var currentVersion = $"v{GetCurrentVersion()}";

                    if (latestRelease.Name != currentVersion && currentVersion != "v0.0.0.0")
                    {
                        logger.Info($"New release found.  Current: {currentVersion} New: {latestRelease.Name}");
                        try
                        {
                            var tempDir = await DownloadRelease(latestRelease.Assets, isWindows,  latestRelease.Name);
                            // Copy updater
                            var installDir = Path.GetDirectoryName(ExePath());
                            var updaterPath = Path.Combine(tempDir, "Jackett", "JackettUpdater.exe");
                            if (updaterPath != null)
                                StartUpdate(updaterPath, installDir, isWindows, serverConfig.RuntimeSettings.NoRestart, trayIsRunning);
                        }
                        catch (Exception e)
                        {
                            logger.Error(e, "Error performing update.");
                        }
                    }
                    else
                    {
                        logger.Info($"Checked for a updated release but none was found. Current: {currentVersion} Latest: {latestRelease.Name}");
                    }
                }
            }
            catch (Exception e)
            {
                logger.Error(e, "Error checking for updates.");
            }
            finally
            {
                if (!isWindows)
                {
                    System.Net.ServicePointManager.ServerCertificateValidationCallback -= AcceptCert;
                }
            }
        }

        private string GetCurrentVersion()
        {
            var assembly = Assembly.GetExecutingAssembly();
            var fvi = FileVersionInfo.GetVersionInfo(assembly.Location);
            return fvi.ProductVersion;
        }

        private WebRequest SetDownloadHeaders(WebRequest req)
        {
            req.Headers = new Dictionary<string, string>()
            {
                { "Accept", "application/octet-stream" }
            };

            return req;
        }

        public void CleanupTempDir()
        {
            var tempDir = Path.GetTempPath();

            if (!Directory.Exists(tempDir))
            {
                logger.Error("Temp dir doesn't exist: " + tempDir.ToString());
                return;
            }
            
            try { 
                DirectoryInfo d = new DirectoryInfo(tempDir);
                foreach (var dir in d.GetDirectories("JackettUpdate-*"))
                {
                    try {
                        logger.Info("Deleting JackettUpdate temp files from " + dir.FullName);
                        dir.Delete(true);
                    }
                    catch (Exception e)
                    {
                        logger.Error("Error while deleting temp files from " + dir.FullName);
                        logger.Error(e);
                    }
                }
            }
            catch (Exception e)
            {
                logger.Error("Unexpected error while deleting temp files from " + tempDir.ToString());
                logger.Error(e);
            }
        }

        private async Task<string> DownloadRelease(List<Asset> assets, bool isWindows, string version)
        {
            var targetAsset = assets.Where(a => isWindows ? a.Browser_download_url.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) : a.Browser_download_url.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)).FirstOrDefault();

            if (targetAsset == null)
            {
                logger.Error("Failed to find asset to download!");
                return null;
            }

            var url = targetAsset.Browser_download_url;

            var data = await client.GetBytes(SetDownloadHeaders(new WebRequest() { Url = url, EmulateBrowser = true, Type = RequestType.GET }));

            while (data.IsRedirect)
            {
                data = await client.GetBytes(new WebRequest() { Url = data.RedirectingTo, EmulateBrowser = true, Type = RequestType.GET });
            }

            var tempDir = Path.Combine(Path.GetTempPath(), "JackettUpdate-" + version + "-" + DateTime.Now.Ticks);

            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }

            Directory.CreateDirectory(tempDir);

            if (isWindows)
            {
                var zipPath = Path.Combine(tempDir, "Update.zip");
                File.WriteAllBytes(zipPath, data.Content);
                var fastZip = new FastZip();
                fastZip.ExtractZip(zipPath, tempDir, null);
            }
            else
            {
                var gzPath = Path.Combine(tempDir, "Update.tar.gz");
                File.WriteAllBytes(gzPath, data.Content);
                Stream inStream = File.OpenRead(gzPath);
                Stream gzipStream = new GZipInputStream(inStream);

                TarArchive tarArchive = TarArchive.CreateInputTarArchive(gzipStream);
                tarArchive.ExtractContents(tempDir);
                tarArchive.Close();
                gzipStream.Close();
                inStream.Close();
            }

            return tempDir;
        }

        private void StartUpdate(string updaterExePath, string installLocation, bool isWindows, bool NoRestart, bool trayIsRunning)
        {
            string appType = "Console";
            //DI once off Owin
            IProcessService processService = new ProcessService(logger);
            IServiceConfigService windowsService = new WindowsServiceConfigService(processService, logger);

            if (isWindows && windowsService.ServiceExists() && windowsService.ServiceRunning())
            {
                appType = "WindowsService";
            }

            var exe = Path.GetFileName(ExePath());
            var args = string.Join(" ", Environment.GetCommandLineArgs().Skip(1).Select(a => a.Contains(" ") ? "\"" +a + "\"" : a )).Replace("\"", "\\\"");

            var startInfo = new ProcessStartInfo();
            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;

            // Note: add a leading space to the --Args argument to avoid parsing as arguments
            if (isWindows)
            {
                startInfo.Arguments = $"--Path \"{installLocation}\" --Type \"{appType}\" --Args \" {args}\"";
                startInfo.FileName = Path.Combine(updaterExePath);
            }
            else
            {
                // Wrap mono
                args = exe + " " + args;
                exe = "mono";

                startInfo.Arguments = $"{Path.Combine(updaterExePath)} --Path \"{installLocation}\" --Type \"{appType}\" --Args \" {args}\"";
                startInfo.FileName = "mono";
            }

            try
            {
                var pid = Process.GetCurrentProcess().Id;
                startInfo.Arguments += $" --KillPids \"{pid}\"";
            }
            catch (Exception e)
            {
                logger.Error("Unexpected error while retriving the PID");
                logger.Error(e);
            }

            if (NoRestart)
            {
                startInfo.Arguments += " --NoRestart";
            }

            if (trayIsRunning && appType == "Console")
            {
                startInfo.Arguments += " --StartTray";
            }

            logger.Info($"Starting updater: {startInfo.FileName} {startInfo.Arguments}");
            var procInfo = Process.Start(startInfo);
            logger.Info($"Updater started process id: {procInfo.Id}");

            if (!NoRestart)
            {
                if (isWindows)
                {
                    logger.Info("Signal sent to lock service");
                    lockService.Signal();
                    Thread.Sleep(2000);
                }

                logger.Info("Exiting Jackett..");

                //TODO: Remove once off Owin
                if (EnvironmentUtil.IsRunningLegacyOwin)
                {
                    Engine.Exit(0);
                }
                else
                {
                    Environment.Exit(0);
                }
            }
        }
    }
}
