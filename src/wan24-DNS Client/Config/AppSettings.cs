using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.ComponentModel.DataAnnotations;
using wan24.Core;
using wan24.ObjectValidation;

namespace wan24.DNS.Config
{
    /// <summary>
    /// App settings
    /// </summary>
    public sealed class AppSettings : ValidatableObjectBase
    {
        /// <summary>
        /// Section name
        /// </summary>
        public const string SECTION_NAME = "Dns";

        /// <summary>
        /// Constructor
        /// </summary>
        static AppSettings()
        {
            Root = new ConfigurationBuilder().AddJsonFile(Path.Combine(Path.GetDirectoryName(typeof(AppSettings).Assembly.Location)!, "appsettings.json")).Build();
            Current = Root.GetSection(SECTION_NAME).Get<AppSettings>()?.ValidateObject(out _) ??
                throw new InvalidDataException($"Failed to load {nameof(AppSettings)} from appsettings.json section {SECTION_NAME}");
        }

        /// <summary>
        /// Constructor
        /// </summary>
        public AppSettings() : base() { }

        /// <summary>
        /// App settings root
        /// </summary>
        public static IConfigurationRoot Root { get; }

        /// <summary>
        /// Currrent app settings
        /// </summary>
        public static AppSettings Current { get; }

        /// <summary>
        /// Endpoints
        /// </summary>
        [CountLimit(1, long.MaxValue)]
        public string[] EndPoints { get; init; } = null!;

        /// <summary>
        /// Resolver
        /// </summary>
        public Uri Resolver { get; init; } = null!;

        /// <summary>
        /// Resolver authentication token
        /// </summary>
        public string ResolverAuthToken { get; init; } = null!;

        /// <summary>
        /// Logfile
        /// </summary>
        [MinLength(1), MaxLength(1024)]
        public string? LogFile { get; init; }

        /// <summary>
        /// Log level
        /// </summary>
        public LogLevel LogLevel { get; init; }

        /// <summary>
        /// Apply the app configuration
        /// </summary>
        /// <param name="isDevelopment">Is the development environment?</param>
        public static async Task ApplyAsync(bool isDevelopment)
        {
            // Logging
            ErrorHandling.ErrorHandler = (info) => Logging.WriteError(info.Exception.ToString());
            Logging.Logger = Current.LogFile is not null
                ? await FileLogger.CreateAsync(
                    Current.LogFile,
                    isDevelopment
                        ? LogLevel.Trace
                        : Current.LogLevel,
                    new ConsoleLogger(
                    isDevelopment
                            ? LogLevel.Trace
                            : Current.LogLevel
                        )
                    ).DynamicContext()
                : new ConsoleLogger(
                    isDevelopment
                        ? LogLevel.Trace
                        : Current.LogLevel
                    );
        }
    }
}
