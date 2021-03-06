﻿using Kudu.Contracts.Infrastructure;
using Kudu.Contracts.Settings;
using Kudu.Contracts.Tracing;
using Kudu.Core;
using Kudu.Core.Deployment;
using Kudu.Core.Helpers;
using Kudu.Core.Infrastructure;
using Kudu.Core.Tracing;
using Kudu.Services.Infrastructure;
using Kudu.Services.Web.Tracing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Kudu.Core.Settings;
using Environment = Kudu.Core.Environment;

namespace Kudu.Services.Web
{
    public static class KuduWebUtil
    {
         // CORE TODO this is a hack, esp. considering the hardcoded path separator, hardcoded "Debug\netcoreapp2.0", etc.
        // The idea is that we want Kudu.Services.Web to run kudu.dll from the directory it was built to when we are locally
        // debugging, but in release, we will put the published app into a dedicated directory.
        // Note that the paths are relative to the ApplicationBasePath (Kudu.Services.Web bin directory)
        private const string KuduConsoleFilename = "kudu.dll";
        private const string KuduConsoleRelativePath = "KuduConsole";
        private static Dictionary<string, IOperationLock> _namedLocks ;
        // CORE TODO Is this still true?
        // Due to a bug in Ninject we can't use Dispose to clean up WindowsLockFile so we shut it down manually
        private static LockFile _deploymentLock;


        // <summary>
        // This method initializes status,ssh,hooks & deployment locks used by Kudu to ensure
        // synchronized operations. This method creates a dictionary of locks which is injected
        // into various controllers to resolve the locks they need.
        // <list type="bullet">  
        //     <listheader>  
        //         <term>Locks used by Kudu:</term>  
        //     </listheader>  
        //     <item>  
        //         <term>Status Lock</term>  
        //         <description>Used by DeploymentStatusManager</description>  
        //     </item> 
        //     <item>  
        //         <term>SSH Lock</term>  
        //         <description>Used by SSHKeyController</description>  
        //     </item> 
        //     <item>  
        //         <term>Hooks Lock</term>  
        //         <description>Used by WebHooksManager</description>  
        //     </item> 
        //     <item>  
        //         <term>Deployment Lock</term>  
        //         <description>
        //             Used by DeploymentController, DeploymentManager, SettingsController, 
        //             FetchDeploymentManager, LiveScmController, ReceivePackHandlerMiddleware
        //         </description>  
        //     </item> 
        // </list>  
        // </summary>
        // <remarks>
        //     Uses File watcher.
        //     This originally used Ninject's "WhenInjectedInto" in .Net project for specific instances. IServiceCollection
        //     doesn't support this concept, or anything similar like named instances. There are a few possibilities, 
        //     but the hack solution for now is just injecting a dictionary of locks and letting each dependent resolve 
        //     the one it needs.
        // </remarks>
        private static void SetupLocks(ITraceFactory traceFactory, IEnvironment environment)
        {
            var lockPath = Path.Combine(environment.SiteRootPath, Constants.LockPath);
            var deploymentLockPath = Path.Combine(lockPath, Constants.DeploymentLockFile);
            var statusLockPath = Path.Combine(lockPath, Constants.StatusLockFile);
            var sshKeyLockPath = Path.Combine(lockPath, Constants.SSHKeyLockFile);
            var hooksLockPath = Path.Combine(lockPath, Constants.HooksLockFile);
            _deploymentLock = new DeploymentLockFile(deploymentLockPath, traceFactory);
            _deploymentLock.InitializeAsyncLocks();
            
            var statusLock = new LockFile(statusLockPath, traceFactory);
            statusLock.InitializeAsyncLocks();
            var sshKeyLock = new LockFile(sshKeyLockPath, traceFactory);
            sshKeyLock.InitializeAsyncLocks();
            var hooksLock = new LockFile(hooksLockPath, traceFactory);
            hooksLock.InitializeAsyncLocks(); 
            
            _namedLocks = new Dictionary<string, IOperationLock>
            {
                { "status", statusLock },
                { "ssh", sshKeyLock },
                { "hooks", hooksLock },
                { "deployment", _deploymentLock }
            };
        }

        public static Uri GetAbsoluteUri(HttpContext httpContext)
        {
            var request = httpContext.Request;
            UriBuilder uriBuilder = new UriBuilder();
            uriBuilder.Scheme = request.Scheme;
            uriBuilder.Host = request.Host.Host;
            uriBuilder.Path = request.Path.ToString();
            uriBuilder.Query = request.QueryString.ToString();
            return uriBuilder.Uri;
        }

        public static ITracer GetTracer(IServiceProvider serviceProvider)
        {
            var environment = serviceProvider.GetRequiredService<IEnvironment>();
            var level = serviceProvider.GetRequiredService<IDeploymentSettingsManager>().GetTraceLevel();
            var contextAccessor = serviceProvider.GetRequiredService<IHttpContextAccessor>();
            var httpContext = contextAccessor.HttpContext;
            var requestTraceFile = TraceServices.GetRequestTraceFile(httpContext);
            if (level <= TraceLevel.Off || requestTraceFile == null) return NullTracer.Instance;
            var textPath = Path.Combine(environment.TracePath, requestTraceFile);
            return new CascadeTracer(new XmlTracer(environment.TracePath, level), new TextTracer(textPath, level), new ETWTracer(environment.RequestId, TraceServices.GetHttpMethod(httpContext)));
        }

        public static string GetRequestTraceFile(IServiceProvider serviceProvider)
        {
            var traceLevel = serviceProvider.GetRequiredService<IDeploymentSettingsManager>().GetTraceLevel();
            // CORE TODO Need TraceServices implementation - Done , Testing left
            if (traceLevel <= TraceLevel.Off) return null;
            var contextAccessor = serviceProvider.GetRequiredService<IHttpContextAccessor>();
            var httpContext = contextAccessor.HttpContext;
            return TraceServices.GetRequestTraceFile(httpContext);
        }

        public static ITracer GetTracerWithoutContext(IEnvironment environment, IDeploymentSettingsManager settings)
        {
            // when file system has issue, this can throw (environment.TracePath calls EnsureDirectory).
            // prefer no-op tracer over outage.  
            return OperationManager.SafeExecute(() =>
            {
                var traceLevel = settings.GetTraceLevel();
                return traceLevel > TraceLevel.Off ? new XmlTracer(environment.TracePath, traceLevel) : NullTracer.Instance;
            }) ?? NullTracer.Instance;
        }

        public static ILogger GetLogger(IServiceProvider serviceProvider)
        {
            var environment = serviceProvider.GetRequiredService<IEnvironment>();
            var level = serviceProvider.GetRequiredService<IDeploymentSettingsManager>().GetTraceLevel();
            var contextAccessor = serviceProvider.GetRequiredService<IHttpContextAccessor>();
            var httpContext = contextAccessor.HttpContext;
            var requestTraceFile = TraceServices.GetRequestTraceFile(httpContext);
            if (level <= TraceLevel.Off || requestTraceFile == null) return NullLogger.Instance;
            var textPath = Path.Combine(environment.DeploymentTracePath, requestTraceFile);
            return new TextLogger(textPath);
        }

        public static IEnvironment GetEnvironment(IHostingEnvironment hostingEnvironment, IDeploymentSettingsManager settings = null, HttpContext httpContext = null)
        {
            var root = PathResolver.ResolveRootPath();
            var siteRoot = Path.Combine(root, Constants.SiteFolder);
            var repositoryPath = Path.Combine(siteRoot, settings == null ? Constants.RepositoryPath : settings.GetRepositoryPath());
            // CORE TODO see if we can refactor out PlatformServices as high up as we can?
            var binPath = AppContext.BaseDirectory;
            var requestId = httpContext?.Request.GetRequestId();

            // CORE TODO Clean this up
            var kuduConsoleFullPath = Path.Combine(AppContext.BaseDirectory, 
                hostingEnvironment.IsDevelopment() ? @"..\..\..\..\Kudu.Console\bin\Debug\netcoreapp2.2" : KuduConsoleRelativePath, 
                KuduConsoleFilename);
            //kuduConsoleFullPath = Path.Combine(System.AppContext.BaseDirectory, KuduConsoleRelativePath, KuduConsoleFilename);


            // CORE TODO Environment now requires an HttpContextAccessor, which I have set to null here
            return new Core.Environment(root, EnvironmentHelper.NormalizeBinPath(binPath), repositoryPath, requestId, kuduConsoleFullPath, null);
        }

        /// <summary>
        /// Ensures %HOME% is correctly set
        /// </summary>
        public static void EnsureHomeEnvironmentVariable()
        {
            // CORE TODO Hard-coding this for now while exploring. Have a look at what
            // PlatformServices.Default and the injected IHostingEnvironment have at runtime.
            if (Directory.Exists(System.Environment.ExpandEnvironmentVariables(@"%HOME%"))) return;

            //For Debug
            System.Environment.SetEnvironmentVariable("HOME", OSDetector.IsOnWindows() ? @"F:\kudu-debug" : "/home");

            /*
            // If MapPath("/_app") returns a valid folder, set %HOME% to that, regardless of
            // it current value. This is the non-Azure code path.
            string path = HostingEnvironment.MapPath(Constants.MappedSite);
            if (Directory.Exists(path))
            {
                path = Path.GetFullPath(path);
                System.Environment.SetEnvironmentVariable("HOME", path);
            }
            */
        }
        
        public static void EnsureValidDeploymentXmlSettings(IEnvironment environment)
        {
            var path = GetSettingsPath(environment);
            if (File.Exists(path))
            {
                try
                {
                    var settings = new DeploymentSettingsManager(new XmlSettings.Settings(path));
                    settings.GetValue(SettingsKeys.TraceLevel);
                }
                catch (Exception ex)
                {
                    DateTime lastWriteTimeUtc = DateTime.MinValue;
                    OperationManager.SafeExecute(() => lastWriteTimeUtc = File.GetLastWriteTimeUtc(path));
                    // trace initialization error
                    KuduEventSource.Log.KuduException(
                        ServerConfiguration.GetApplicationName(),
                        "Startup.cs",
                        string.Empty,
                        string.Empty,
                        string.Format("Invalid '{0}' is detected and deleted.  Last updated time was {1}.", path, lastWriteTimeUtc),
                        ex.ToString());
                    File.Delete(path);
                }
            }
        }

        public static void PrependFoldersToPath(IEnvironment environment)
        {
            var folders = PathUtilityFactory.Instance.GetPathFolders(environment);

            var path = System.Environment.GetEnvironmentVariable("PATH");
            // Ignore any folder that doesn't actually exist
            string additionalPaths = String.Join(Path.PathSeparator.ToString(), folders.Where(dir => Directory.Exists(dir)));

            // Make sure we haven't already added them. This can happen if the Kudu appdomain restart (since it's still same process)
            if (path.Contains(additionalPaths)) return;
            path = additionalPaths + Path.PathSeparator + path;

            // PHP 7 was mistakenly added to the path unconditionally on Azure. To work around, if we detect
            // some PHP v5.x anywhere on the path, we yank the unwanted PHP 7
            // TODO: remove once the issue is fixed on Azure
            if (path.Contains(@"PHP\v5"))
            {
                path = path.Replace(@"D:\Program Files (x86)\PHP\v7.0" + Path.PathSeparator, String.Empty);
            }

            System.Environment.SetEnvironmentVariable("PATH", path);
        }

        public static void EnsureDotNetCoreEnvironmentVariable(IEnvironment environment)
        {
            // Skip this as it causes huge files to be downloaded to the temp folder
            SetEnvironmentVariableIfNotYetSet("DOTNET_SKIP_FIRST_TIME_EXPERIENCE", "true");

            // Don't download xml comments, as they're large and provide no benefits outside of a dev machine
            SetEnvironmentVariableIfNotYetSet("NUGET_XMLDOC_MODE", "skip");

            if (Core.Environment.IsAzureEnvironment())
            {
                // On Azure, restore nuget packages to d:\home\.nuget so they're persistent. It also helps
                // work around https://github.com/projectkudu/kudu/issues/2056.
                // Note that this only applies to project.json scenarios (not packages.config)
                SetEnvironmentVariableIfNotYetSet("NUGET_PACKAGES", Path.Combine(environment.RootPath, ".nuget"));

                // Set the telemetry environment variable
                SetEnvironmentVariableIfNotYetSet("DOTNET_CLI_TELEMETRY_PROFILE", "AzureKudu");
            }
            else
            {
                // Set it slightly differently if outside of Azure to differentiate
                SetEnvironmentVariableIfNotYetSet("DOTNET_CLI_TELEMETRY_PROFILE", "Kudu");
            }
        }

        public static LockFile GetDeploymentLock(ITraceFactory traceFactory, IEnvironment environment)
        {
            if (_namedLocks == null || _deploymentLock == null)
            {
                GetNamedLocks(traceFactory, environment);
            }
            return _deploymentLock;
        }

        public static Dictionary<string, IOperationLock> GetNamedLocks(ITraceFactory traceFactory, IEnvironment environment)
        {
            if (_namedLocks == null)
            {
                SetupLocks(traceFactory, environment);
            }
            return _namedLocks;
        }

        // <summary>
        // 
        // </summary>
        public static void EnsureSiteBitnessEnvironmentVariable()
        {
            SetEnvironmentVariableIfNotYetSet("SITE_BITNESS", System.Environment.Is64BitProcess ? Constants.X64Bit : Constants.X86Bit);
        }

        public static void SetEnvironmentVariableIfNotYetSet(string name, string value)
        {
            if (System.Environment.GetEnvironmentVariable(name) == null)
            {
                System.Environment.SetEnvironmentVariable(name, value);
            }
        }

        public static string GetSettingsPath(IEnvironment environment)
        {
            return Path.Combine(environment.DeploymentsPath, Constants.DeploySettingsPath);
        }

    }
}
