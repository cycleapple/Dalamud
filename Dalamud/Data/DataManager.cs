using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Dalamud.Game;
using Dalamud.IoC;
using Dalamud.IoC.Internal;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using Dalamud.Utility.Timing;
using Lumina;
using Lumina.Data;
using Lumina.Excel;
using Lumina.Excel.Exceptions;

using Newtonsoft.Json;
using Serilog;

namespace Dalamud.Data;

/// <summary>
/// This class provides data for Dalamud-internal features, but can also be used by plugins if needed.
/// </summary>
[PluginInterface]
[ServiceManager.EarlyLoadedService]
#pragma warning disable SA1015
[ResolveVia<IDataManager>]
#pragma warning restore SA1015
internal sealed class DataManager : IInternalDisposableService, IDataManager
{
    private readonly Thread luminaResourceThread;
    private readonly CancellationTokenSource luminaCancellationTokenSource;
    private readonly RsvResolver rsvResolver;

    [ServiceManager.ServiceConstructor]
    private DataManager(Dalamud dalamud)
    {
        this.Language = (ClientLanguage)dalamud.StartInfo.Language;

        this.rsvResolver = new();

        try
        {
            Log.Verbose("Starting data load...");

            using (Timings.Start("Lumina Init"))
            {
                var luminaOptions = new LuminaOptions
                {
                    LoadMultithreaded = true,
                    CacheFileResources = true,
                    PanicOnSheetChecksumMismatch = false, // TW client has different sheet structure
                    RsvResolver = this.rsvResolver.TryResolve,
                    DefaultExcelLanguage = Lumina.Data.Language.ChineseTraditional, // TW client uses _cht suffix for Excel sheets
                };

                try
                {
                    this.GameData = new(
                        Path.Combine(Path.GetDirectoryName(Environment.ProcessPath)!, "sqpack"),
                        luminaOptions)
                    {
                        StreamPool = new(),
                    };
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Lumina GameData init failed");
                    Util.Fatal(
                        "Dalamud could not read required game data files. This likely means your game installation is corrupted or incomplete.\n\n" +
                        "Please repair your installation by right-clicking the login button in XIVLauncher and choosing \"Repair game files\".",
                        "Dalamud");

                    return;
                }

                Log.Information("Lumina is ready: {0}", this.GameData.DataPath);

                if (!dalamud.StartInfo.TroubleshootingPackData.IsNullOrEmpty())
                {
                    try
                    {
                        var tsInfo =
                            JsonConvert.DeserializeObject<LauncherTroubleshootingInfo>(
                                dalamud.StartInfo.TroubleshootingPackData);
                        this.HasModifiedGameDataFiles =
                            tsInfo?.IndexIntegrity is LauncherTroubleshootingInfo.IndexIntegrityResult.Failed or LauncherTroubleshootingInfo.IndexIntegrityResult.Exception;

                        if (this.HasModifiedGameDataFiles)
                            Log.Verbose("Game data integrity check failed!\n{TsData}", dalamud.StartInfo.TroubleshootingPackData);
                    }
                    catch
                    {
                        // ignored
                    }
                }
            }

            this.IsDataReady = true;

            this.luminaCancellationTokenSource = new();

            var luminaCancellationToken = this.luminaCancellationTokenSource.Token;
            this.luminaResourceThread = new(() =>
            {
                while (!luminaCancellationToken.IsCancellationRequested)
                {
                    if (this.GameData.FileHandleManager.HasPendingFileLoads)
                    {
                        this.GameData.ProcessFileHandleQueue();
                    }
                    else
                    {
                        Thread.Sleep(5);
                    }
                }
            });
            this.luminaResourceThread.Start();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not initialize Lumina");
            throw;
        }
    }

    /// <inheritdoc/>
    public ClientLanguage Language { get; private set; }

    /// <inheritdoc/>
    public GameData GameData { get; private set; }

    /// <inheritdoc/>
    public ExcelModule Excel => this.GameData.Excel;

    /// <inheritdoc/>
    public bool HasModifiedGameDataFiles { get; private set; }

    /// <summary>
    /// Gets a value indicating whether Game Data is ready to be read.
    /// </summary>
    internal bool IsDataReady { get; private set; }

    #region Lumina Wrappers

    // Language fallback order for TW client
    private static readonly Lumina.Data.Language[] LanguageFallbacks =
    [
        Lumina.Data.Language.ChineseTraditional2, // _tc suffix (TW client)
        Lumina.Data.Language.ChineseTraditional,  // _cht suffix
        Lumina.Data.Language.None,                // base sheet (no suffix)
        Lumina.Data.Language.English,             // _en suffix
        Lumina.Data.Language.Japanese,            // _ja suffix
        Lumina.Data.Language.ChineseSimplified,   // _chs suffix
    ];

    /// <inheritdoc/>
    public ExcelSheet<T> GetExcelSheet<T>(ClientLanguage? language = null, string? name = null) where T : struct, IExcelRow<T>
    {
        // TW client: try requested language first, then fall back through alternatives
        var requestedLang = language?.ToLumina();

        // If explicit language requested, try it first
        if (requestedLang != null)
        {
            try
            {
                return this.Excel.GetSheet<T>(requestedLang, name);
            }
            catch (UnsupportedLanguageException)
            {
                Log.Warning("Language {Lang} not supported for sheet {Name}, trying fallbacks", requestedLang, typeof(T).Name);
            }
        }

        // Try fallback languages
        Exception? lastException = null;
        foreach (var fallbackLang in LanguageFallbacks)
        {
            try
            {
                return this.Excel.GetSheet<T>(fallbackLang, name);
            }
            catch (UnsupportedLanguageException ex)
            {
                lastException = ex;
                // Continue to next fallback
            }
        }

        // Last resort - try with null (let Lumina pick default) then throw if still fails
        try
        {
            return this.Excel.GetSheet<T>(null, name);
        }
        catch (UnsupportedLanguageException)
        {
            Log.Error("All language fallbacks failed for sheet {Name}. Tried: ChineseTraditional2, ChineseTraditional, None, English, Japanese, ChineseSimplified, null", typeof(T).Name);
            throw;
        }
    }

    /// <inheritdoc/>
    public SubrowExcelSheet<T> GetSubrowExcelSheet<T>(ClientLanguage? language = null, string? name = null) where T : struct, IExcelSubrow<T>
    {
        // TW client: try requested language first, then fall back through alternatives
        var requestedLang = language?.ToLumina();

        // If explicit language requested, try it first
        if (requestedLang != null)
        {
            try
            {
                return this.Excel.GetSubrowSheet<T>(requestedLang, name);
            }
            catch (UnsupportedLanguageException)
            {
                Log.Warning("Language {Lang} not supported for subrow sheet {Name}, trying fallbacks", requestedLang, typeof(T).Name);
            }
        }

        // Try fallback languages
        foreach (var fallbackLang in LanguageFallbacks)
        {
            try
            {
                return this.Excel.GetSubrowSheet<T>(fallbackLang, name);
            }
            catch (UnsupportedLanguageException)
            {
                // Continue to next fallback
            }
        }

        // Last resort - try with null (let Lumina pick default) then throw if still fails
        try
        {
            return this.Excel.GetSubrowSheet<T>(null, name);
        }
        catch (UnsupportedLanguageException)
        {
            Log.Error("All language fallbacks failed for subrow sheet {Name}. Tried: ChineseTraditional2, ChineseTraditional, None, English, Japanese, ChineseSimplified, null", typeof(T).Name);
            throw;
        }
    }

    /// <inheritdoc/>
    public FileResource? GetFile(string path)
        => this.GetFile<FileResource>(path);

    /// <inheritdoc/>
    public T? GetFile<T>(string path) where T : FileResource
    {
        var filePath = GameData.ParseFilePath(path);
        if (filePath == null)
            return default;
        return this.GameData.Repositories.TryGetValue(filePath.Repository, out var repository) ? repository.GetFile<T>(filePath.Category, filePath) : default;
    }

    /// <inheritdoc/>
    public Task<T> GetFileAsync<T>(string path, CancellationToken cancellationToken) where T : FileResource =>
        GameData.ParseFilePath(path) is { } filePath &&
        this.GameData.Repositories.TryGetValue(filePath.Repository, out var repository)
            ? Task.Run(
                () => repository.GetFile<T>(filePath.Category, filePath) ?? throw new FileNotFoundException(
                          "Failed to load file, most likely because the file could not be found."),
                cancellationToken)
            : Task.FromException<T>(new FileNotFoundException("The file could not be found."));

    /// <inheritdoc/>
    public bool FileExists(string path)
        => this.GameData.FileExists(path);

    #endregion

    /// <inheritdoc/>
    void IInternalDisposableService.DisposeService()
    {
        this.luminaCancellationTokenSource.Cancel();
        this.GameData.Dispose();
        this.rsvResolver.Dispose();
    }

    private class LauncherTroubleshootingInfo
    {
        public enum IndexIntegrityResult
        {
            Failed,
            Exception,
            NoGame,
            ReferenceNotFound,
            ReferenceFetchFailure,
            Success,
        }

        public IndexIntegrityResult? IndexIntegrity { get; set; }
    }
}
