using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommandLine;
using Emby.Server.Implementations;
using Jellyfin.Server.Implementations;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Model.IO;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Serilog;
using Serilog.Extensions.Logging;
using SQLitePCL;
using static MediaBrowser.Controller.Extensions.ConfigurationExtensions;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace Jellyfin.Server
{
    /// <summary>
    /// Class containing the entry point of the application.
    /// </summary>
    public static class Program
    {
        /// <summary>
        /// The name of logging configuration file containing application defaults.
        /// </summary>
        public const string LoggingConfigFileDefault = "logging.default.json";

        /// <summary>
        /// The name of the logging configuration file containing the system-specific override settings.
        /// </summary>
        public const string LoggingConfigFileSystem = "logging.json";

        private static readonly CancellationTokenSource _tokenSource = new CancellationTokenSource();
        private static readonly ILoggerFactory _loggerFactory = new SerilogLoggerFactory();
        private static ILogger _logger = NullLogger.Instance;
        private static bool _restartOnShutdown;

        /// <summary>
        /// The entry point of the application.
        /// </summary>
        /// <param name="args">The command line arguments passed.</param>
        /// <returns><see cref="Task" />.</returns>
        public static Task Main(string[] args)
        {
            static Task ErrorParsingArguments(IEnumerable<Error> errors)
            {
                Environment.ExitCode = 1;
                return Task.CompletedTask;
            }

            // Parse the command line arguments and either start the app or exit indicating error
            return Parser.Default.ParseArguments<StartupOptions>(args)
                .MapResult(StartApp, ErrorParsingArguments);
        }

        /// <summary>
        /// Shuts down the application.
        /// </summary>
        internal static void Shutdown()
        {
            if (!_tokenSource.IsCancellationRequested)
            {
                _tokenSource.Cancel();
            }
        }

        /// <summary>
        /// Restarts the application.
        /// </summary>
        internal static void Restart()
        {
            _restartOnShutdown = true;

            Shutdown();
        }

        private static async Task StartApp(StartupOptions options)
        {
            var stopWatch = new Stopwatch();
            stopWatch.Start();

            // Log all uncaught exceptions to std error
            static void UnhandledExceptionToConsole(object sender, UnhandledExceptionEventArgs e) =>
                Console.Error.WriteLine("Unhandled Exception\n" + e.ExceptionObject.ToString());
            AppDomain.CurrentDomain.UnhandledException += UnhandledExceptionToConsole;

            ServerApplicationPaths appPaths = CreateApplicationPaths(options);

            // $JELLYFIN_LOG_DIR needs to be set for the logger configuration manager
            Environment.SetEnvironmentVariable("JELLYFIN_LOG_DIR", appPaths.LogDirectoryPath);

            // Enable cl-va P010 interop for tonemapping on Intel VAAPI
            Environment.SetEnvironmentVariable("NEOReadDebugKeys", "1");
            Environment.SetEnvironmentVariable("EnableExtendedVaFormats", "1");

            await InitLoggingConfigFile(appPaths).ConfigureAwait(false);

            // Create an instance of the application configuration to use for application startup
            IConfiguration startupConfig = CreateAppConfiguration(options, appPaths);

            // Initialize logging framework
            InitializeLoggingFramework(startupConfig, appPaths);
            _logger = _loggerFactory.CreateLogger("Main");

            // Log uncaught exceptions to the logging instead of std error
            AppDomain.CurrentDomain.UnhandledException -= UnhandledExceptionToConsole;
            AppDomain.CurrentDomain.UnhandledException += (_, e)
                => _logger.LogCritical((Exception)e.ExceptionObject, "Unhandled Exception");

            // Intercept Ctrl+C and Ctrl+Break
            Console.CancelKeyPress += (_, e) =>
            {
                if (_tokenSource.IsCancellationRequested)
                {
                    return; // Already shutting down
                }

                e.Cancel = true;
                _logger.LogInformation("Ctrl+C, shutting down");
                Environment.ExitCode = 128 + 2;
                Shutdown();
            };

            // Register a SIGTERM handler
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                if (_tokenSource.IsCancellationRequested)
                {
                    return; // Already shutting down
                }

                _logger.LogInformation("Received a SIGTERM signal, shutting down");
                Environment.ExitCode = 128 + 15;
                Shutdown();
            };

            _logger.LogInformation(
                "Jellyfin version: {Version}",
                Assembly.GetEntryAssembly()!.GetName().Version!.ToString(3));

            ApplicationHost.LogEnvironmentInfo(_logger, appPaths);

            // If hosting the web client, validate the client content path
            if (startupConfig.HostWebClient())
            {
                string? webContentPath = appPaths.WebPath;
                if (!Directory.Exists(webContentPath) || !Directory.EnumerateFiles(webContentPath).Any())
                {
                    _logger.LogError(
                        "The server is expected to host the web client, but the provided content directory is either " +
                        "invalid or empty: {WebContentPath}. If you do not want to host the web client with the " +
                        "server, you may set the '--nowebclient' command line flag, or set" +
                        "'{ConfigKey}=false' in your config settings.",
                        webContentPath,
                        HostWebClientKey);
                    Environment.ExitCode = 1;
                    return;
                }
            }

            PerformStaticInitialization();
            Migrations.MigrationRunner.RunPreStartup(appPaths, _loggerFactory);

            var appHost = new CoreAppHost(
                appPaths,
                _loggerFactory,
                options,
                startupConfig);

            try
            {
                var serviceCollection = new ServiceCollection();
                appHost.Init(serviceCollection);

                var webHost = new WebHostBuilder().ConfigureWebHostBuilder(appHost, serviceCollection, options, startupConfig, appPaths).Build();

                // Re-use the web host service provider in the app host since ASP.NET doesn't allow a custom service collection.
                appHost.ServiceProvider = webHost.Services;
                await appHost.InitializeServices().ConfigureAwait(false);
                Migrations.MigrationRunner.Run(appHost, _loggerFactory);

                try
                {
                    await webHost.StartAsync(_tokenSource.Token).ConfigureAwait(false);

                    if (startupConfig.UseUnixSocket() && Environment.OSVersion.Platform == PlatformID.Unix)
                    {
                        var socketPath = GetUnixSocketPath(startupConfig, appPaths);

                        SetUnixSocketPermissions(startupConfig, socketPath);
                    }
                }
                catch (Exception ex) when (ex is not TaskCanceledException)
                {
                    _logger.LogError("Kestrel failed to start! This is most likely due to an invalid address or port bind - correct your bind configuration in network.xml and try again.");
                    throw;
                }

                await appHost.RunStartupTasksAsync(_tokenSource.Token).ConfigureAwait(false);

                stopWatch.Stop();

                _logger.LogInformation("Startup complete {Time:g}", stopWatch.Elapsed);

                // Block main thread until shutdown
                await Task.Delay(-1, _tokenSource.Token).ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                // Don't throw on cancellation
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "Error while starting server.");
            }
            finally
            {
                // Don't throw additional exception if startup failed.
                if (appHost.ServiceProvider != null)
                {
                    _logger.LogInformation("Running query planner optimizations in the database... This might take a while");
                    // Run before disposing the application
                    using var context = appHost.Resolve<JellyfinDbProvider>().CreateContext();
                    if (context.Database.IsSqlite())
                    {
                        context.Database.ExecuteSqlRaw("PRAGMA optimize");
                    }
                }

                await appHost.DisposeAsync().ConfigureAwait(false);
            }

            if (_restartOnShutdown)
            {
                StartNewInstance(options);
            }
        }

        /// <summary>
        /// Call static initialization methods for the application.
        /// </summary>
        public static void PerformStaticInitialization()
        {
            // Make sure we have all the code pages we can get
            // Ref: https://docs.microsoft.com/en-us/dotnet/api/system.text.codepagesencodingprovider.instance?view=netcore-3.0#remarks
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            // Increase the max http request limit
            // The default connection limit is 10 for ASP.NET hosted applications and 2 for all others.
            ServicePointManager.DefaultConnectionLimit = Math.Max(96, ServicePointManager.DefaultConnectionLimit);

            // Disable the "Expect: 100-Continue" header by default
            // http://stackoverflow.com/questions/566437/http-post-returns-the-error-417-expectation-failed-c
            ServicePointManager.Expect100Continue = false;

            Batteries_V2.Init();
            if (raw.sqlite3_enable_shared_cache(1) != raw.SQLITE_OK)
            {
                _logger.LogWarning("Failed to enable shared cache for SQLite");
            }
        }

        /// <summary>
        /// Configure the web host builder.
        /// </summary>
        /// <param name="builder">The builder to configure.</param>
        /// <param name="appHost">The application host.</param>
        /// <param name="serviceCollection">The application service collection.</param>
        /// <param name="commandLineOpts">The command line options passed to the application.</param>
        /// <param name="startupConfig">The application configuration.</param>
        /// <param name="appPaths">The application paths.</param>
        /// <returns>The configured web host builder.</returns>
        public static IWebHostBuilder ConfigureWebHostBuilder(
            this IWebHostBuilder builder,
            ApplicationHost appHost,
            IServiceCollection serviceCollection,
            StartupOptions commandLineOpts,
            IConfiguration startupConfig,
            IApplicationPaths appPaths)
        {
            return builder
                .UseKestrel((builderContext, options) =>
                {
                    var addresses = appHost.NetManager.GetAllBindInterfaces();

                    bool flagged = false;
                    foreach (IPObject netAdd in addresses)
                    {
                        _logger.LogInformation("Kestrel listening on {Address}", netAdd.Address == IPAddress.IPv6Any ? "All Addresses" : netAdd);
                        options.Listen(netAdd.Address, appHost.HttpPort);
                        if (appHost.ListenWithHttps)
                        {
                            options.Listen(
                                netAdd.Address,
                                appHost.HttpsPort,
                                listenOptions => listenOptions.UseHttps(appHost.Certificate));
                        }
                        else if (builderContext.HostingEnvironment.IsDevelopment())
                        {
                            try
                            {
                                options.Listen(
                                    netAdd.Address,
                                    appHost.HttpsPort,
                                    listenOptions => listenOptions.UseHttps());
                            }
                            catch (InvalidOperationException)
                            {
                                if (!flagged)
                                {
                                    _logger.LogWarning("Failed to listen to HTTPS using the ASP.NET Core HTTPS development certificate. Please ensure it has been installed and set as trusted.");
                                    flagged = true;
                                }
                            }
                        }
                    }

                    // Bind to unix socket (only on unix systems)
                    if (startupConfig.UseUnixSocket() && Environment.OSVersion.Platform == PlatformID.Unix)
                    {
                        var socketPath = GetUnixSocketPath(startupConfig, appPaths);

                        // Workaround for https://github.com/aspnet/AspNetCore/issues/14134
                        if (File.Exists(socketPath))
                        {
                            File.Delete(socketPath);
                        }

                        options.ListenUnixSocket(socketPath);
                        _logger.LogInformation("Kestrel listening to unix socket {SocketPath}", socketPath);
                    }
                })
                .ConfigureAppConfiguration(config => config.ConfigureAppConfiguration(commandLineOpts, appPaths, startupConfig))
                .UseSerilog()
                .ConfigureServices(services =>
                {
                    // Merge the external ServiceCollection into ASP.NET DI
                    services.Add(serviceCollection);
                })
                .UseStartup<Startup>();
        }

        /// <summary>
        /// Create the data, config and log paths from the variety of inputs(command line args,
        /// environment variables) or decide on what default to use. For Windows it's %AppPath%
        /// for everything else the
        /// <a href="https://specifications.freedesktop.org/basedir-spec/basedir-spec-latest.html">XDG approach</a>
        /// is followed.
        /// </summary>
        /// <param name="options">The <see cref="StartupOptions" /> for this instance.</param>
        /// <returns><see cref="ServerApplicationPaths" />.</returns>
        private static ServerApplicationPaths CreateApplicationPaths(StartupOptions options)
        {
            // dataDir
            // IF      --datadir
            // ELSE IF $JELLYFIN_DATA_DIR
            // ELSE IF windows, use <%APPDATA%>/jellyfin
            // ELSE IF $XDG_DATA_HOME then use $XDG_DATA_HOME/jellyfin
            // ELSE    use $HOME/.local/share/jellyfin
            var dataDir = options.DataDir;
            if (string.IsNullOrEmpty(dataDir))
            {
                dataDir = Environment.GetEnvironmentVariable("JELLYFIN_DATA_DIR");

                if (string.IsNullOrEmpty(dataDir))
                {
                    // LocalApplicationData follows the XDG spec on unix machines
                    dataDir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "jellyfin");
                }
            }

            // configDir
            // IF      --configdir
            // ELSE IF $JELLYFIN_CONFIG_DIR
            // ELSE IF --datadir, use <datadir>/config (assume portable run)
            // ELSE IF <datadir>/config exists, use that
            // ELSE IF windows, use <datadir>/config
            // ELSE IF $XDG_CONFIG_HOME use $XDG_CONFIG_HOME/jellyfin
            // ELSE    $HOME/.config/jellyfin
            var configDir = options.ConfigDir;
            if (string.IsNullOrEmpty(configDir))
            {
                configDir = Environment.GetEnvironmentVariable("JELLYFIN_CONFIG_DIR");

                if (string.IsNullOrEmpty(configDir))
                {
                    if (options.DataDir != null
                        || Directory.Exists(Path.Combine(dataDir, "config"))
                        || OperatingSystem.IsWindows())
                    {
                        // Hang config folder off already set dataDir
                        configDir = Path.Combine(dataDir, "config");
                    }
                    else
                    {
                        // $XDG_CONFIG_HOME defines the base directory relative to which
                        // user specific configuration files should be stored.
                        configDir = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");

                        // If $XDG_CONFIG_HOME is either not set or empty,
                        // a default equal to $HOME /.config should be used.
                        if (string.IsNullOrEmpty(configDir))
                        {
                            configDir = Path.Combine(
                                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                                ".config");
                        }

                        configDir = Path.Combine(configDir, "jellyfin");
                    }
                }
            }

            // cacheDir
            // IF      --cachedir
            // ELSE IF $JELLYFIN_CACHE_DIR
            // ELSE IF windows, use <datadir>/cache
            // ELSE IF XDG_CACHE_HOME, use $XDG_CACHE_HOME/jellyfin
            // ELSE    HOME/.cache/jellyfin
            var cacheDir = options.CacheDir;
            if (string.IsNullOrEmpty(cacheDir))
            {
                cacheDir = Environment.GetEnvironmentVariable("JELLYFIN_CACHE_DIR");

                if (string.IsNullOrEmpty(cacheDir))
                {
                    if (OperatingSystem.IsWindows())
                    {
                        // Hang cache folder off already set dataDir
                        cacheDir = Path.Combine(dataDir, "cache");
                    }
                    else
                    {
                        // $XDG_CACHE_HOME defines the base directory relative to which
                        // user specific non-essential data files should be stored.
                        cacheDir = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");

                        // If $XDG_CACHE_HOME is either not set or empty,
                        // a default equal to $HOME/.cache should be used.
                        if (string.IsNullOrEmpty(cacheDir))
                        {
                            cacheDir = Path.Combine(
                                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                                ".cache");
                        }

                        cacheDir = Path.Combine(cacheDir, "jellyfin");
                    }
                }
            }

            // webDir
            // IF      --webdir
            // ELSE IF $JELLYFIN_WEB_DIR
            // ELSE    <bindir>/jellyfin-web
            var webDir = options.WebDir;
            if (string.IsNullOrEmpty(webDir))
            {
                webDir = Environment.GetEnvironmentVariable("JELLYFIN_WEB_DIR");

                if (string.IsNullOrEmpty(webDir))
                {
                    // Use default location under ResourcesPath
                    webDir = Path.Combine(AppContext.BaseDirectory, "jellyfin-web");
                }
            }

            // logDir
            // IF      --logdir
            // ELSE IF $JELLYFIN_LOG_DIR
            // ELSE IF --datadir, use <datadir>/log (assume portable run)
            // ELSE    <datadir>/log
            var logDir = options.LogDir;
            if (string.IsNullOrEmpty(logDir))
            {
                logDir = Environment.GetEnvironmentVariable("JELLYFIN_LOG_DIR");

                if (string.IsNullOrEmpty(logDir))
                {
                    // Hang log folder off already set dataDir
                    logDir = Path.Combine(dataDir, "log");
                }
            }

            // Normalize paths. Only possible with GetFullPath for now - https://github.com/dotnet/runtime/issues/2162
            dataDir = Path.GetFullPath(dataDir);
            logDir = Path.GetFullPath(logDir);
            configDir = Path.GetFullPath(configDir);
            cacheDir = Path.GetFullPath(cacheDir);
            webDir = Path.GetFullPath(webDir);

            // Ensure the main folders exist before we continue
            try
            {
                Directory.CreateDirectory(dataDir);
                Directory.CreateDirectory(logDir);
                Directory.CreateDirectory(configDir);
                Directory.CreateDirectory(cacheDir);
            }
            catch (IOException ex)
            {
                Console.Error.WriteLine("Error whilst attempting to create folder");
                Console.Error.WriteLine(ex.ToString());
                Environment.Exit(1);
            }

            return new ServerApplicationPaths(dataDir, logDir, configDir, cacheDir, webDir);
        }

        /// <summary>
        /// Initialize the logging configuration file using the bundled resource file as a default if it doesn't exist
        /// already.
        /// </summary>
        /// <param name="appPaths">The application paths.</param>
        /// <returns>A task representing the creation of the configuration file, or a completed task if the file already exists.</returns>
        public static async Task InitLoggingConfigFile(IApplicationPaths appPaths)
        {
            // Do nothing if the config file already exists
            string configPath = Path.Combine(appPaths.ConfigurationDirectoryPath, LoggingConfigFileDefault);
            if (File.Exists(configPath))
            {
                return;
            }

            // Get a stream of the resource contents
            // NOTE: The .csproj name is used instead of the assembly name in the resource path
            const string ResourcePath = "Jellyfin.Server.Resources.Configuration.logging.json";
            Stream resource = typeof(Program).Assembly.GetManifestResourceStream(ResourcePath)
                ?? throw new InvalidOperationException($"Invalid resource path: '{ResourcePath}'");
            await using (resource.ConfigureAwait(false))
            {
                Stream dst = new FileStream(configPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, IODefaults.FileStreamBufferSize, FileOptions.Asynchronous);
                await using (dst.ConfigureAwait(false))
                {
                    // Copy the resource contents to the expected file path for the config file
                    await resource.CopyToAsync(dst).ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// Create the application configuration.
        /// </summary>
        /// <param name="commandLineOpts">The command line options passed to the program.</param>
        /// <param name="appPaths">The application paths.</param>
        /// <returns>The application configuration.</returns>
        public static IConfiguration CreateAppConfiguration(StartupOptions commandLineOpts, IApplicationPaths appPaths)
        {
            return new ConfigurationBuilder()
                .ConfigureAppConfiguration(commandLineOpts, appPaths)
                .Build();
        }

        private static IConfigurationBuilder ConfigureAppConfiguration(
            this IConfigurationBuilder config,
            StartupOptions commandLineOpts,
            IApplicationPaths appPaths,
            IConfiguration? startupConfig = null)
        {
            // Use the swagger API page as the default redirect path if not hosting the web client
            var inMemoryDefaultConfig = ConfigurationOptions.DefaultConfiguration;
            if (startupConfig != null && !startupConfig.HostWebClient())
            {
                inMemoryDefaultConfig[DefaultRedirectKey] = "api-docs/swagger";
            }

            return config
                .SetBasePath(appPaths.ConfigurationDirectoryPath)
                .AddInMemoryCollection(inMemoryDefaultConfig)
                .AddJsonFile(LoggingConfigFileDefault, optional: false, reloadOnChange: true)
                .AddJsonFile(LoggingConfigFileSystem, optional: true, reloadOnChange: true)
                .AddEnvironmentVariables("JELLYFIN_")
                .AddInMemoryCollection(commandLineOpts.ConvertToConfig());
        }

        /// <summary>
        /// Initialize Serilog using configuration and fall back to defaults on failure.
        /// </summary>
        private static void InitializeLoggingFramework(IConfiguration configuration, IApplicationPaths appPaths)
        {
            try
            {
                // Serilog.Log is used by SerilogLoggerFactory when no logger is specified
                Log.Logger = new LoggerConfiguration()
                    .ReadFrom.Configuration(configuration)
                    .Enrich.FromLogContext()
                    .Enrich.WithThreadId()
                    .CreateLogger();
            }
            catch (Exception ex)
            {
                Log.Logger = new LoggerConfiguration()
                    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss}] [{Level:u3}] [{ThreadId}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
                    .WriteTo.Async(x => x.File(
                        Path.Combine(appPaths.LogDirectoryPath, "log_.log"),
                        rollingInterval: RollingInterval.Day,
                        outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz}] [{Level:u3}] [{ThreadId}] {SourceContext}: {Message}{NewLine}{Exception}",
                        encoding: Encoding.UTF8))
                    .Enrich.FromLogContext()
                    .Enrich.WithThreadId()
                    .CreateLogger();

                Log.Logger.Fatal(ex, "Failed to create/read logger configuration");
            }
        }

        private static void StartNewInstance(StartupOptions options)
        {
            _logger.LogInformation("Starting new instance");

            var module = options.RestartPath;

            if (string.IsNullOrWhiteSpace(module))
            {
                module = Environment.GetCommandLineArgs()[0];
            }

            string commandLineArgsString;
            if (options.RestartArgs != null)
            {
                commandLineArgsString = options.RestartArgs;
            }
            else
            {
                commandLineArgsString = string.Join(
                    ' ',
                    Environment.GetCommandLineArgs().Skip(1).Select(NormalizeCommandLineArgument));
            }

            _logger.LogInformation("Executable: {0}", module);
            _logger.LogInformation("Arguments: {0}", commandLineArgsString);

            Process.Start(module, commandLineArgsString);
        }

        private static string NormalizeCommandLineArgument(string arg)
        {
            if (!arg.Contains(' ', StringComparison.Ordinal))
            {
                return arg;
            }

            return "\"" + arg + "\"";
        }

        private static string GetUnixSocketPath(IConfiguration startupConfig, IApplicationPaths appPaths)
        {
            var socketPath = startupConfig.GetUnixSocketPath();

            if (string.IsNullOrEmpty(socketPath))
            {
                var xdgRuntimeDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
                var socketFile = "jellyfin.sock";
                if (xdgRuntimeDir == null)
                {
                    // Fall back to config dir
                    socketPath = Path.Join(appPaths.ConfigurationDirectoryPath, socketFile);
                }
                else
                {
                    socketPath = Path.Join(xdgRuntimeDir, socketFile);
                }
            }

            return socketPath;
        }

        private static void SetUnixSocketPermissions(IConfiguration startupConfig, string socketPath)
        {
            var socketPerms = startupConfig.GetUnixSocketPermissions();

            if (!string.IsNullOrEmpty(socketPerms))
            {
                #pragma warning disable SA1300 // Entrypoint is case sensitive.
                [DllImport("libc")]
                static extern int chmod(string pathname, int mode);
                #pragma warning restore SA1300

                var exitCode = chmod(socketPath, Convert.ToInt32(socketPerms, 8));

                if (exitCode < 0)
                {
                    _logger.LogError("Failed to set Kestrel unix socket permissions to {SocketPerms}, return code: {ExitCode}", socketPerms, exitCode);
                }
                else
                {
                    _logger.LogInformation("Kestrel unix socket permissions set to {SocketPerms}", socketPerms);
                }
            }
        }
    }
}
