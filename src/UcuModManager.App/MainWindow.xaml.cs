using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Interop;
using System.Windows.Threading;
using UcuModManager.Core.BepInEx;
using UcuModManager.Core.Deployment;
using UcuModManager.Core.Games;
using UcuModManager.Core.Mods;
using UcuModManager.Core.Profiles;
using UcuModManager.Core.Storage;
using UcuModManager.Core.Virtualization;
using MessageBox = UcuModManager.App.DarkMessageBox;

namespace UcuModManager.App;

public partial class MainWindow : Window
{
    private const int WmGetMinMaxInfo = 0x0024;
    private const int MonitorDefaultToNearest = 0x00000002;
    private const int DwmWindowCornerPreferenceAttribute = 33;
    private const int DwmWindowCornerPreferenceRound = 2;
    private const int MaxImageCacheEntries = 96;
    private const int MaxImageDownloadBytes = 12 * 1024 * 1024;
    private const int ImageDownloadBufferSize = 81920;
    private const string SteamAppId = "4576510";
    private const string SteamGameFolderName = "Casualties Unknown Demo";
    private const int MinimumDependencyCandidateScore = 100;
    private const double HiddenModColumnWidth = 0;
    private const double ModFilesColumnWidth = 58;
    private const double ModPluginsColumnWidth = 70;
    private const double ModContentColumnWidth = 74;
    private const double ModConfigsColumnWidth = 72;

    private readonly ManagerSettingsService _settingsService = new();
    private readonly ModLibraryService _libraryService = new();
    private readonly ModImportService _importService = new();
    private readonly ModRemovalService _modRemovalService = new();
    private readonly ProfileService _profileService = new();
    private readonly UcuModpackService _ucuModpackService = new();
    private readonly GameInstallationValidator _gameValidator = new();
    private readonly BepInExInstallationProbe _bepInExProbe = new();
    private readonly BepInExInstaller _bepInExInstaller = new();
    private readonly ProfileDeployService _profileDeployService = new();
    private readonly VirtualizationPlanBuilder _virtualizationPlanBuilder = new();
    private readonly OverlayPreviewService _overlayPreviewService = new();
    private readonly VirtualizedLaunchPlanWriter _virtualizedLaunchPlanWriter = new();
    private readonly VirtualizedLaunchPlanValidator _virtualizedLaunchPlanValidator = new();
    private readonly VirtualizedGameImageBuilder _virtualizedGameImageBuilder = new();
    private readonly NexusModDownloadService _nexusModDownloadService = new();
    private readonly NexusModFilesService _nexusModFilesService = new();
    private readonly NexusMetadataCatalogService _nexusMetadataCatalogService = new();
    private readonly NexusMetadataMatcher _nexusMetadataMatcher = new();
    private readonly SecureSecretStore _secureSecretStore = new();
    private readonly HttpClient _imageHttpClient = new();
    private readonly Dictionary<string, BitmapImage> _imageCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _imageCacheOrder = new();
    private readonly ObservableCollection<ModRow> _mods = new();
    private readonly ObservableCollection<ProfileRow> _profiles = new();
    private readonly ObservableCollection<NexusCatalogRow> _nexusCatalogRows = new();
    private readonly ObservableCollection<UcuModpackModRow> _modpackProfileRows = new();
    private readonly ObservableCollection<UcuModpackModRow> _ucuModpackRows = new();
    private readonly ManagerPaths _managerPaths;

    private ManagerSettings _settings = ManagerSettings.Empty;
    private IReadOnlyList<ModLibraryEntry> _libraryEntries = Array.Empty<ModLibraryEntry>();
    private IReadOnlyList<NexusMetadataCatalogEntry> _nexusCatalogEntries = Array.Empty<NexusMetadataCatalogEntry>();
    private ModProfile? _currentProfile;
    private UcuModpackPackage? _importedUcuModpack;
    private string? _importedUcuModpackPath;
    private bool _importedUcuModpackIsPortable;
    private readonly HashSet<string> _manualDownloadModpackKeys = new(StringComparer.OrdinalIgnoreCase);
    private bool _isLoading;
    private bool _isLoadingProfiles;
    private bool _isLoadingSettings;
    private bool _isAutoLinkNexusRunning;
    private int _selectedImageRequestId;
    private int _nexusBrowserImageRequestId;
    private string? _lastNexusFilesCacheSummary;
    private HwndSource? _windowSource;

    public MainWindow()
    {
        InitializeComponent();
        _managerPaths = ResolveManagerPaths();
        _settings = _settingsService.Load(_managerPaths);
        if (ShouldResetNexusGameDomain(_settings.NexusGameDomain))
        {
            _settings = _settings with { NexusGameDomain = ManagerSettings.Empty.NexusGameDomain };
            _settingsService.Save(_managerPaths, _settings);
        }

        StorageRootText.Text = _managerPaths.RootPath;
        ModsListView.ItemsSource = _mods;
        ProfilesListBox.ItemsSource = _profiles;
        ModpackExportProfileComboBox.ItemsSource = _profiles;
        NexusCatalogListView.ItemsSource = _nexusCatalogRows;
        ModpackExportProfileListView.ItemsSource = _modpackProfileRows;
        ImportedUcuModpackListView.ItemsSource = _ucuModpackRows;
        SourceInitialized += MainWindow_SourceInitialized;
        Closed += MainWindow_Closed;
        Loaded += MainWindow_Loaded;
        ApplyModTableColumnSettings();
        TryAutoConfigureGameFolder();
        RefreshSetupStatus();
        LoadMods();
        RefreshSettingsStatus();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainWindow_Loaded;
        await ShowVirtualizationIntroIfNeededAsync();
        await RefreshNexusMetadataOnStartupAsync();
        if (_settings.AutoLinkNexusOnStartup)
        {
            await RunAutoLinkNexusAsync(
                showResults: false,
                showNoModsMessage: false,
                requireConfirmation: false,
                showBusyCursor: false,
                allowClearingUnmatchedExistingLinks: false);
        }
    }

    private async Task ShowVirtualizationIntroIfNeededAsync()
    {
        if (_settings.VirtualizationIntroShown)
        {
            return;
        }

        await Task.Yield();
        var selfTest = _virtualizedGameImageBuilder.RunSelfTest(
            _managerPaths.RootPath,
            GetVirtualizationProbeSourcePath());
        var dialog = new VirtualizationIntroDialog(new VirtualizationIntroState(
            selfTest.IsSupported,
            selfTest.LinkMode,
            selfTest.Message))
        {
            Owner = IsVisible ? this : null
        };
        dialog.ShowDialog();
        var answer = dialog.Result;

        try
        {
            var enabled = answer == MessageBoxResult.Yes;
            _settings = _settings with
            {
                VirtualizationEnabled = enabled,
                VirtualizationIntroShown = true
            };
            _settingsService.Save(_managerPaths, _settings);

            if (_currentProfile is not null)
            {
                _currentProfile = _currentProfile with
                {
                    Virtualization = _currentProfile.Virtualization with
                    {
                        UseExperimentalVirtualizedLaunch = enabled
                    }
                };
                _profileService.SaveProfile(_managerPaths, _currentProfile);
            }

            RefreshSettingsStatus();
            RefreshProfilePageStatus();
            RefreshVirtualLaunchStatus();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, exception.Message, "Save virtualization settings failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private string? GetVirtualizationProbeSourcePath()
    {
        if (string.IsNullOrWhiteSpace(_settings.GameRootPath))
        {
            return null;
        }

        var validation = _gameValidator.Validate(_settings.GameRootPath);
        return validation.IsValid
            ? GameInstallation.FromRootPath(validation.GameRootPath).ExecutablePath
            : null;
    }

    private async Task RefreshNexusMetadataOnStartupAsync()
    {
        try
        {
            var catalogLoad = await _nexusMetadataCatalogService.LoadAsync(_managerPaths, forceRefresh: true);
            _nexusCatalogEntries = catalogLoad.Entries;
            RefreshNexusMetadataStatusText(catalogLoad.Status);
        }
        catch (Exception exception) when (exception is HttpRequestException
            or TaskCanceledException
            or IOException
            or JsonException
            or InvalidOperationException
            or UnauthorizedAccessException)
        {
            RefreshNexusMetadataStatusText();
        }
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        _windowSource = HwndSource.FromHwnd(handle);
        _windowSource?.AddHook(WndProc);
        EnableRoundedWindowCorners(handle);
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        SourceInitialized -= MainWindow_SourceInitialized;
        Closed -= MainWindow_Closed;
        _windowSource?.RemoveHook(WndProc);
        _windowSource = null;

        _imageCache.Clear();
        _imageCacheOrder.Clear();
        _imageHttpClient.Dispose();
        _nexusModDownloadService.Dispose();
        _nexusModFilesService.Dispose();
        _nexusMetadataCatalogService.Dispose();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmGetMinMaxInfo)
        {
            ApplyMonitorWorkArea(hwnd, lParam);
            handled = true;
        }

        return IntPtr.Zero;
    }

    private static void ApplyMonitorWorkArea(IntPtr hwnd, IntPtr lParam)
    {
        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
        {
            return;
        }

        var monitorInfo = new MonitorInfo
        {
            Size = Marshal.SizeOf<MonitorInfo>()
        };
        if (!GetMonitorInfo(monitor, ref monitorInfo))
        {
            return;
        }

        var minMaxInfo = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        var workArea = monitorInfo.WorkArea;
        var monitorArea = monitorInfo.MonitorArea;
        minMaxInfo.MaxPosition.X = Math.Abs(workArea.Left - monitorArea.Left);
        minMaxInfo.MaxPosition.Y = Math.Abs(workArea.Top - monitorArea.Top);
        minMaxInfo.MaxSize.X = Math.Abs(workArea.Right - workArea.Left);
        minMaxInfo.MaxSize.Y = Math.Abs(workArea.Bottom - workArea.Top);
        Marshal.StructureToPtr(minMaxInfo, lParam, false);
    }

    private static void EnableRoundedWindowCorners(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var cornerPreference = DwmWindowCornerPreferenceRound;
        _ = DwmSetWindowAttribute(
            hwnd,
            DwmWindowCornerPreferenceAttribute,
            ref cornerPreference,
            Marshal.SizeOf<int>());
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        RefreshSetupStatus();
        LoadMods();
    }

    private void ChooseGameFolder_Click(object sender, RoutedEventArgs e)
    {
        var detectedGameRootPath = FindAutoDetectedGameRootPath();
        if (!string.IsNullOrWhiteSpace(detectedGameRootPath)
            && (string.IsNullOrWhiteSpace(_settings.GameRootPath) || !PathsEqual(detectedGameRootPath, _settings.GameRootPath)))
        {
            var answer = MessageBox.Show(
                this,
                $"Found Casualties Unknown Demo automatically:\n\n{detectedGameRootPath}\n\nYes: use this folder.\nNo: choose manually.\nCancel: keep current setting.",
                "Game folder found",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Information);
            if (answer == MessageBoxResult.Yes)
            {
                SaveGameFolder(detectedGameRootPath);
                return;
            }

            if (answer == MessageBoxResult.Cancel)
            {
                return;
            }
        }

        ChooseGameFolderManually(detectedGameRootPath);
    }

    private void ChooseGameFolderManually(string? initialDirectory = null)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select Casualties Unknown Demo folder"
        };

        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }
        else if (!string.IsNullOrWhiteSpace(_settings.GameRootPath) && Directory.Exists(_settings.GameRootPath))
        {
            dialog.InitialDirectory = _settings.GameRootPath;
        }

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        SaveGameFolder(dialog.FolderName);
    }

    private void SaveGameFolder(string gameRootPath)
    {
        var validation = _gameValidator.Validate(gameRootPath);
        _settings = _settings with { GameRootPath = validation.GameRootPath };
        _settingsService.Save(_managerPaths, _settings);

        RefreshSetupStatus();
        RefreshProfileSummary();
    }

    private void TryAutoConfigureGameFolder()
    {
        if (!string.IsNullOrWhiteSpace(_settings.GameRootPath)
            && _gameValidator.Validate(_settings.GameRootPath).IsValid)
        {
            return;
        }

        var detectedGameRootPath = FindAutoDetectedGameRootPath();
        if (!string.IsNullOrWhiteSpace(detectedGameRootPath))
        {
            SaveGameFolder(detectedGameRootPath);
        }
    }

    private string? FindAutoDetectedGameRootPath()
    {
        foreach (var candidatePath in EnumerateAutoGameFolderCandidates().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var validation = _gameValidator.Validate(candidatePath);
                if (validation.IsValid)
                {
                    return validation.GameRootPath;
                }
            }
            catch (Exception exception) when (exception is ArgumentException
                or NotSupportedException
                or PathTooLongException
                or IOException
                or UnauthorizedAccessException)
            {
                continue;
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateAutoGameFolderCandidates()
    {
        foreach (var steamRootPath in EnumerateSteamRootPaths().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var libraryRootPath in EnumerateSteamLibraryRootPaths(steamRootPath).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var steamAppsPath = Path.Combine(libraryRootPath, "steamapps");
                var installDirectoryName = TryReadSteamInstallDirectoryName(steamAppsPath);
                if (!string.IsNullOrWhiteSpace(installDirectoryName))
                {
                    yield return Path.Combine(steamAppsPath, "common", installDirectoryName);
                }

                yield return Path.Combine(steamAppsPath, "common", SteamGameFolderName);
            }
        }

        foreach (var fallbackPath in EnumerateCommonSteamFallbackPaths())
        {
            yield return fallbackPath;
        }
    }

    private static IEnumerable<string> EnumerateSteamRootPaths()
    {
        foreach (var registryPath in ReadSteamRootPathsFromRegistry())
        {
            yield return registryPath;
        }

        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(programFilesX86))
        {
            yield return Path.Combine(programFilesX86, "Steam");
        }
    }

    private static IEnumerable<string> ReadSteamRootPathsFromRegistry()
    {
        foreach (var registryView in new[] { RegistryView.Default, RegistryView.Registry32, RegistryView.Registry64 })
        {
            foreach (var registryHive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
            {
                foreach (var subKeyName in new[] { @"Software\Valve\Steam", @"Software\WOW6432Node\Valve\Steam" })
                {
                    using var key = TryOpenRegistrySubKey(registryHive, registryView, subKeyName);
                    if (key is null)
                    {
                        continue;
                    }

                    foreach (var valueName in new[] { "SteamPath", "InstallPath" })
                    {
                        var steamPath = NormalizeSteamPath(key.GetValue(valueName)?.ToString());
                        if (!string.IsNullOrWhiteSpace(steamPath))
                        {
                            yield return steamPath;
                        }
                    }
                }
            }
        }
    }

    private static RegistryKey? TryOpenRegistrySubKey(
        RegistryHive registryHive,
        RegistryView registryView,
        string subKeyName)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(registryHive, registryView);
            return baseKey.OpenSubKey(subKeyName);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException)
        {
            return null;
        }
    }

    private static IEnumerable<string> EnumerateSteamLibraryRootPaths(string steamRootPath)
    {
        if (!Directory.Exists(steamRootPath))
        {
            yield break;
        }

        yield return steamRootPath;

        var libraryFoldersPath = Path.Combine(steamRootPath, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(libraryFoldersPath))
        {
            yield break;
        }

        string[] lines;
        try
        {
            lines = File.ReadAllLines(libraryFoldersPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var line in lines)
        {
            var match = Regex.Match(line, "\"path\"\\s+\"(?<path>[^\"]+)\"", RegexOptions.IgnoreCase);
            var libraryPath = match.Success
                ? NormalizeSteamPath(match.Groups["path"].Value)
                : null;
            if (!string.IsNullOrWhiteSpace(libraryPath) && Directory.Exists(libraryPath))
            {
                yield return libraryPath;
            }
        }
    }

    private static string? TryReadSteamInstallDirectoryName(string steamAppsPath)
    {
        var appManifestPath = Path.Combine(steamAppsPath, $"appmanifest_{SteamAppId}.acf");
        if (!File.Exists(appManifestPath))
        {
            return null;
        }

        string[] lines;
        try
        {
            lines = File.ReadAllLines(appManifestPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        foreach (var line in lines)
        {
            var match = Regex.Match(line, "\"installdir\"\\s+\"(?<name>[^\"]+)\"", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                return match.Groups["name"].Value.Trim();
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateCommonSteamFallbackPaths()
    {
        foreach (var driveRootPath in EnumerateReadyDriveRootPaths())
        {
            yield return Path.Combine(driveRootPath, "Steam", "steamapps", "common", SteamGameFolderName);
            yield return Path.Combine(driveRootPath, "SteamLibrary", "steamapps", "common", SteamGameFolderName);
        }
    }

    private static IEnumerable<string> EnumerateReadyDriveRootPaths()
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            bool isReady;
            try
            {
                isReady = drive.IsReady;
            }
            catch (IOException)
            {
                continue;
            }

            if (isReady)
            {
                yield return drive.RootDirectory.FullName;
            }
        }
    }

    private static string? NormalizeSteamPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        return path.Trim()
            .Replace(@"\\", @"\")
            .Replace('/', Path.DirectorySeparatorChar);
    }

    private void ValidateGame_Click(object sender, RoutedEventArgs e)
    {
        RefreshSetupStatus();
    }

    private void InstallBepInExFromZip_Click(object sender, RoutedEventArgs e)
    {
        var installation = GetValidGameInstallationOrShowMessage();
        if (installation is null)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Filter = "BepInEx archives (*.zip)|*.zip|All files (*.*)|*.*",
            Multiselect = false,
            Title = "Select BepInEx archive"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        TryInstallBepInExArchive(dialog.FileName, installation);
    }

    private async void DeployProfile_Click(object sender, RoutedEventArgs e)
    {
        var installation = GetDeploymentReadyGameOrShowMessage();
        if (installation is null || _currentProfile is null)
        {
            return;
        }

        if (!await ResolveMissingDependenciesBeforeDeployAsync("Deploy Anyway", "deploy again"))
        {
            return;
        }

        var overlayPreview = BuildCurrentOverlayPreview();
        if (overlayPreview is null)
        {
            ConflictSummaryText.Text = "Overlay unavailable";
            ViewConflictsButton.IsEnabled = false;
            return;
        }

        if (overlayPreview.MissingSources.Count > 0)
        {
            MessageBox.Show(this, "Deployment cannot continue because one or more source files are missing.", "Deploy failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            var preDeployCleanResults = _profileDeployService.CleanOtherProfiles(
                _managerPaths,
                _currentProfile.Id,
                overlayPreview.GameRootPath);
            var blockedCleanResults = preDeployCleanResults
                .Where(result => result.PreservedFiles > 0)
                .ToArray();
            if (blockedCleanResults.Length > 0)
            {
                RefreshDeployStatus();
                RefreshProfileSummary();
                MessageBox.Show(
                    this,
                    BuildBlockedProfileCleanupMessage(blockedCleanResults),
                    "Deploy stopped",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            var result = _profileDeployService.Deploy(_managerPaths, overlayPreview);
            RefreshDeployStatus();
            RefreshProfileSummary();
            ShowDeployResult("Profile deployed", result, preDeployCleanResults);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            RefreshDeployStatus();
            MessageBox.Show(this, exception.Message, "Deploy failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void DirectLaunchGame_Click(object sender, RoutedEventArgs e)
    {
        var installation = GetValidGameInstallationOrShowMessage();
        if (installation is null)
        {
            return;
        }

        try
        {
            StartDirectGame(installation);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            MessageBox.Show(this, exception.Message, "Direct launch failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void VirtualLaunchGame_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProfile is null)
        {
            MessageBox.Show(this, "Load or create a profile before virtualized launch.", "Virtualized Launch", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!_settings.VirtualizationEnabled)
        {
            MessageBox.Show(this, "Enable Virtualized Launch in Settings first.", "Virtualized Launch", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!_currentProfile.Virtualization.UseExperimentalVirtualizedLaunch)
        {
            MessageBox.Show(this, "Enable virtualized launch for the active profile in Settings first.", "Virtualized Launch", MessageBoxButton.OK, MessageBoxImage.Information);
            RefreshVirtualLaunchStatus();
            return;
        }

        var installation = GetBepInExReadyGameOrShowMessage("starting a virtualized profile");
        if (installation is null)
        {
            return;
        }

        if (!await ResolveMissingDependenciesBeforeDeployAsync("Launch Anyway", "launch the virtualized profile again"))
        {
            return;
        }

        SaveProfileFromRows();
        var overlayPreview = BuildCurrentOverlayPreview();
        if (overlayPreview is null)
        {
            return;
        }

        if (overlayPreview.MissingSources.Count > 0)
        {
            MessageBox.Show(this, "Virtualized launch cannot continue because one or more source files are missing.", "Virtualized Launch", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            var cleanupRisk = GetVirtualLaunchCleanupRisk(_currentProfile.Id, overlayPreview.GameRootPath);
            if (cleanupRisk.HasRisk)
            {
                var cleanupAnswer = MessageBox.ShowCustom(
                    this,
                    BuildVirtualLaunchPreCleanupMessage(cleanupRisk),
                    "Clean Deploy before virtual launch",
                    MessageBoxImage.Warning,
                    new[]
                    {
                        new DarkMessageBoxButton("Clean Deploy and Launch", MessageBoxResult.Yes, Primary: true),
                        new DarkMessageBoxButton("Cancel", MessageBoxResult.Cancel, IsCancel: true)
                    });
                if (cleanupAnswer != MessageBoxResult.Yes)
                {
                    RefreshDeployStatus();
                    RefreshVirtualLaunchStatus();
                    return;
                }
            }

            var preLaunchCleanResults = CleanManagedDeploymentsBeforeVirtualLaunch(_currentProfile.Id, overlayPreview.GameRootPath);
            if (preLaunchCleanResults.Any(result => result.PreservedFiles > 0 || result.Warnings.Count > 0))
            {
                var answer = MessageBox.ShowCustom(
                    this,
                    BuildVirtualLaunchCleanupWarningMessage(preLaunchCleanResults),
                    "Virtualized Launch",
                    MessageBoxImage.Warning,
                    new[]
                    {
                        new DarkMessageBoxButton("Launch Anyway", MessageBoxResult.Yes, Primary: true),
                        new DarkMessageBoxButton("Cancel", MessageBoxResult.Cancel, IsCancel: true)
                    });
                if (answer != MessageBoxResult.Yes)
                {
                    RefreshDeployStatus();
                    RefreshVirtualLaunchStatus();
                    return;
                }
            }

            var progress = ShowProgress("Virtualized Launch", "Preparing virtualized profile", "Starting...");
            try
            {
                await UpdateVirtualLaunchProgressAsync(progress, "Writing launch plan...");
                var planPath = _virtualizedLaunchPlanWriter.Save(_managerPaths, _currentProfile, overlayPreview);

                await UpdateVirtualLaunchProgressAsync(progress, "Checking virtualized launch plan...");
                var validation = _virtualizedLaunchPlanValidator.ValidateFile(planPath);
                if (validation.HasErrors)
                {
                    progress.Close();
                    RefreshVirtualLaunchStatus();
                    MessageBox.Show(
                        this,
                        BuildVirtualLaunchValidationMessage(validation, planPath),
                        "Virtualized launch invalid",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                var plan = _virtualizedLaunchPlanValidator.Load(planPath);

                await UpdateVirtualLaunchProgressAsync(progress, "Saving profile config state...");
                SyncVirtualizedRuntimeStateToProfile(_currentProfile, Path.Combine(plan.ProfileRuntimePath, "game"));

                await UpdateVirtualLaunchProgressAsync(progress, "Building linked runtime image...");
                var image = await Task.Run(() => _virtualizedGameImageBuilder.Build(plan));

                await UpdateVirtualLaunchProgressAsync(progress, "Starting game...");
                progress.Close();
                StartVirtualizedGame(_currentProfile, image);
            }
            catch
            {
                progress.Close();
                throw;
            }

            RefreshVirtualLaunchStatus();
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            MessageBox.Show(this, exception.Message, "Virtualized launch failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static void StartDirectGame(GameInstallation installation)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = installation.ExecutablePath,
            WorkingDirectory = installation.RootPath,
            UseShellExecute = false
        };

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The game process could not be started.");
    }

    private static async Task UpdateVirtualLaunchProgressAsync(VirtualLaunchProgressDialog progress, string status)
    {
        progress.SetStatus(status);
        await progress.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
    }

    private VirtualLaunchProgressDialog ShowProgress(string title, string heading, string status)
    {
        var progress = new VirtualLaunchProgressDialog(title, heading, status)
        {
            Owner = IsVisible ? this : null
        };
        progress.Show();
        return progress;
    }

    private static void UpdateProgress(VirtualLaunchProgressDialog? progress, string status)
    {
        if (progress is null)
        {
            return;
        }

        progress.SetStatus(status);
        progress.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
    }

    private static void CloseProgress(VirtualLaunchProgressDialog? progress)
    {
        if (progress is null)
        {
            return;
        }

        try
        {
            progress.Close();
        }
        catch (InvalidOperationException)
        {
        }
    }

    private IReadOnlyList<ProfileDeployResult> CleanManagedDeploymentsBeforeVirtualLaunch(string activeProfileId, string gameRootPath)
    {
        var results = new List<ProfileDeployResult>();
        results.AddRange(_profileDeployService.CleanOtherProfiles(_managerPaths, activeProfileId, gameRootPath));

        var activeManifest = _profileDeployService.LoadManifest(_managerPaths, activeProfileId);
        if (activeManifest is not null && PathsEqual(activeManifest.GameRootPath, gameRootPath))
        {
            results.Add(_profileDeployService.Clean(_managerPaths, activeProfileId));
        }

        if (results.Count > 0)
        {
            RefreshDeployStatus();
        }

        return results
            .Where(result => result.DeletedFiles > 0 || result.PreservedFiles > 0 || result.Warnings.Count > 0)
            .ToArray();
    }

    private VirtualLaunchCleanupRisk GetVirtualLaunchCleanupRisk(string activeProfileId, string gameRootPath)
    {
        var activeManifest = _profileDeployService.LoadManifest(_managerPaths, activeProfileId);
        var activeFiles = activeManifest is not null && PathsEqual(activeManifest.GameRootPath, gameRootPath)
            ? activeManifest.Files.Count
            : 0;

        var otherProfiles = _profiles
            .Where(profile => !profile.Id.Equals(activeProfileId, StringComparison.OrdinalIgnoreCase))
            .Select(profile => new
            {
                profile.Name,
                Manifest = _profileDeployService.LoadManifest(_managerPaths, profile.Id)
            })
            .Where(item => item.Manifest is not null && PathsEqual(item.Manifest.GameRootPath, gameRootPath))
            .Select(item => $"{item.Name} ({item.Manifest!.Files.Count} files)")
            .ToArray();

        return new VirtualLaunchCleanupRisk(activeFiles, otherProfiles);
    }

    private void StartVirtualizedGame(ModProfile profile, VirtualizedGameImageBuildResult image)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = image.VirtualGameExecutablePath,
            WorkingDirectory = image.VirtualGameRootPath,
            UseShellExecute = false
        };

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The virtualized game process could not be started.");
        var syncGate = new object();
        var syncCompleted = false;
        void SyncOnce()
        {
            lock (syncGate)
            {
                if (syncCompleted)
                {
                    return;
                }

                syncCompleted = true;
            }

            try
            {
                SyncVirtualizedRuntimeStateToProfile(profile, image.VirtualGameRootPath);
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or NotSupportedException)
            {
                Debug.WriteLine($"Virtualized runtime state sync failed: {exception}");
            }
            finally
            {
                process.Dispose();
            }
        }

        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => SyncOnce();
        if (process.HasExited)
        {
            SyncOnce();
        }
    }

    private void SyncVirtualizedRuntimeStateToProfile(ModProfile profile, string virtualGameRootPath)
    {
        if (!profile.Virtualization.RedirectWritesToProfileState)
        {
            return;
        }

        var managerRoot = EnsureTrailingSeparator(Path.GetFullPath(_managerPaths.RootPath));
        var virtualGameRoot = EnsureTrailingSeparator(Path.GetFullPath(virtualGameRootPath));
        var profileBepInExRoot = EnsureTrailingSeparator(Path.GetFullPath(profile.ProfileBepInExPath));
        if (!IsInsideRoot(virtualGameRoot, managerRoot)
            || !IsInsideRoot(profileBepInExRoot, managerRoot))
        {
            throw new InvalidOperationException("Virtualized state sync resolved outside the manager storage folder.");
        }

        CopyChangedDirectoryFiles(
            Path.Combine(virtualGameRoot, "BepInEx", "config"),
            Path.Combine(profileBepInExRoot, "config"));
    }

    private static void CopyChangedDirectoryFiles(string sourceDirectoryPath, string targetDirectoryPath)
    {
        if (!Directory.Exists(sourceDirectoryPath))
        {
            return;
        }

        var sourceRoot = EnsureTrailingSeparator(Path.GetFullPath(sourceDirectoryPath));
        var targetRoot = EnsureTrailingSeparator(Path.GetFullPath(targetDirectoryPath));
        Directory.CreateDirectory(targetRoot);

        foreach (var sourcePath in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceRoot, sourcePath);
            var targetPath = Path.GetFullPath(Path.Combine(targetRoot, relativePath));
            if (!IsInsideRoot(targetPath, targetRoot))
            {
                throw new InvalidOperationException($"Virtualized state sync target escaped the profile folder: {relativePath}");
            }

            if (File.Exists(targetPath))
            {
                var sourceInfo = new FileInfo(sourcePath);
                var targetInfo = new FileInfo(targetPath);
                if (sourceInfo.Length == targetInfo.Length
                    && sourceInfo.LastWriteTimeUtc == targetInfo.LastWriteTimeUtc)
                {
                    continue;
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.Copy(sourcePath, targetPath, overwrite: true);
        }
    }

    private async Task<bool> ResolveMissingDependenciesBeforeDeployAsync(string continueButtonText, string retryActionText)
    {
        var report = await BuildMissingDependencyReportAsync();
        if (report.Issues.Count == 0)
        {
            return true;
        }

        var hasAutomaticAction = report.Issues.Any(issue => issue.InstalledProviders.Count > 0 || issue.Candidates.Count > 0);
        if (!hasAutomaticAction)
        {
            var missingOnlyAnswer = MessageBox.ShowCustom(
                this,
                BuildMissingDependencyMessage(
                    report,
                    "No reliable Nexus metadata candidate was found for these DLLs."),
                "Missing dependencies",
                MessageBoxImage.Warning,
                new[]
                {
                    new DarkMessageBoxButton(continueButtonText, MessageBoxResult.Yes, Primary: true),
                    new DarkMessageBoxButton("Cancel", MessageBoxResult.Cancel, IsCancel: true)
                });
            return missingOnlyAnswer == MessageBoxResult.Yes;
        }

        var answer = MessageBox.ShowCustom(
            this,
            BuildMissingDependencyMessage(
                report,
                "Automatic Nexus downloads require a configured API key and may require Nexus Premium."),
            "Missing dependencies",
            MessageBoxImage.Warning,
            new[]
            {
                new DarkMessageBoxButton("Fix Dependencies", MessageBoxResult.Yes, Primary: true),
                new DarkMessageBoxButton(continueButtonText, MessageBoxResult.No),
                new DarkMessageBoxButton("Cancel", MessageBoxResult.Cancel, IsCancel: true)
            });

        if (answer == MessageBoxResult.Yes)
        {
            await EnableOrDownloadMissingDependencyCandidatesAsync(report, retryActionText);
            return false;
        }

        if (answer == MessageBoxResult.No)
        {
            return true;
        }

        return false;
    }

    private async Task<MissingDependencyReport> BuildMissingDependencyReportAsync()
    {
        var seeds = BuildEnabledMissingDependencySeeds();
        if (seeds.Count == 0)
        {
            return new MissingDependencyReport(Array.Empty<MissingDependencyIssue>(), null);
        }

        IReadOnlyList<NexusMetadataCatalogEntry> catalog = _nexusCatalogEntries;
        string? catalogWarning = null;
        try
        {
            var catalogLoad = await _nexusMetadataCatalogService.LoadAsync(_managerPaths);
            catalog = catalogLoad.Entries;
            _nexusCatalogEntries = catalogLoad.Entries;
            catalogWarning = catalogLoad.Warning;
        }
        catch (Exception exception) when (exception is HttpRequestException
            or TaskCanceledException
            or IOException
            or JsonException
            or InvalidOperationException)
        {
            catalogWarning = $"Nexus metadata catalog is unavailable: {exception.Message}";
        }

        var issues = seeds
            .Select(seed => new MissingDependencyIssue(
                seed.ModName,
                seed.AssemblyName,
                seed.InstalledProviders,
                FindMissingDependencyCandidates(seed.AssemblyName, catalog)))
            .ToArray();
        return new MissingDependencyReport(issues, catalogWarning);
    }

    private IReadOnlyList<MissingDependencySeed> BuildEnabledMissingDependencySeeds()
    {
        var enabledModIds = _mods
            .Where(mod => mod.IsEnabled)
            .Select(mod => mod.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (enabledModIds.Count == 0)
        {
            return Array.Empty<MissingDependencySeed>();
        }

        var enabledEntries = _libraryEntries
            .Where(entry => enabledModIds.Contains(entry.Mod.Id))
            .ToArray();
        var activeProviders = BuildAssemblyProviderIndex(enabledEntries);
        var installedProviders = BuildAssemblyProviderIndex(_libraryEntries);
        var seeds = new List<MissingDependencySeed>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in enabledEntries)
        {
            var ownAssemblyNames = entry.Mod.Assemblies
                .Select(assembly => assembly.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var dependencyNames = entry.Mod.AssemblyReferences
                .Where(reference => !reference.IsKnownGameOrFrameworkReference)
                .Where(reference => !AssemblyReferenceClassifier.IsKnownGameOrFrameworkAssembly(reference.Name))
                .Where(reference => !ownAssemblyNames.Contains(reference.Name))
                .Select(reference => reference.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            foreach (var dependencyName in dependencyNames)
            {
                var activeDependencyProviders = activeProviders.TryGetValue(dependencyName, out var activeProviderList)
                    ? activeProviderList.Where(provider => !provider.ModId.Equals(entry.Mod.Id, StringComparison.OrdinalIgnoreCase)).ToArray()
                    : Array.Empty<MissingDependencyProvider>();
                if (activeDependencyProviders.Length > 0)
                {
                    continue;
                }

                var inactiveInstalledProviders = installedProviders.TryGetValue(dependencyName, out var installedProviderList)
                    ? installedProviderList
                        .Where(provider => !provider.ModId.Equals(entry.Mod.Id, StringComparison.OrdinalIgnoreCase))
                        .Where(provider => !enabledModIds.Contains(provider.ModId))
                        .DistinctBy(provider => provider.ModId, StringComparer.OrdinalIgnoreCase)
                        .ToArray()
                    : Array.Empty<MissingDependencyProvider>();
                var key = $"{entry.Mod.Id}|{dependencyName}";
                if (seen.Add(key))
                {
                    seeds.Add(new MissingDependencySeed(
                        entry.Mod.Id,
                        entry.Mod.Name,
                        dependencyName,
                        inactiveInstalledProviders));
                }
            }
        }

        return seeds;
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<MissingDependencyProvider>> BuildAssemblyProviderIndex(
        IEnumerable<ModLibraryEntry> entries)
    {
        var providers = new Dictionary<string, List<MissingDependencyProvider>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            foreach (var assemblyName in entry.Mod.Assemblies.Select(assembly => assembly.Name).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!providers.TryGetValue(assemblyName, out var assemblyProviders))
                {
                    assemblyProviders = new List<MissingDependencyProvider>();
                    providers[assemblyName] = assemblyProviders;
                }

                if (!assemblyProviders.Any(provider => provider.ModId.Equals(entry.Mod.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    assemblyProviders.Add(new MissingDependencyProvider(entry.Mod.Id, entry.Mod.Name));
                }
            }
        }

        return providers.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<MissingDependencyProvider>)pair.Value,
            StringComparer.OrdinalIgnoreCase);
    }

    private IReadOnlyList<MissingDependencyCandidate> FindMissingDependencyCandidates(
        string assemblyName,
        IReadOnlyList<NexusMetadataCatalogEntry> catalog)
    {
        if (catalog.Count == 0)
        {
            return Array.Empty<MissingDependencyCandidate>();
        }

        var normalizedAssemblyName = NormalizeDependencyLookupName(assemblyName);
        if (string.IsNullOrWhiteSpace(normalizedAssemblyName))
        {
            return Array.Empty<MissingDependencyCandidate>();
        }

        return catalog
            .Select(entry => new
            {
                Entry = entry,
                Score = ScoreMissingDependencyCandidate(normalizedAssemblyName, entry)
            })
            .Where(candidate => candidate.Score >= MinimumDependencyCandidateScore)
            .GroupBy(candidate => BuildNexusCatalogEntryKey(candidate.Entry), StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(candidate => candidate.Score)
                .ThenBy(candidate => candidate.Entry.Name, StringComparer.OrdinalIgnoreCase)
                .First())
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Entry.Name, StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .Select(candidate => new MissingDependencyCandidate(assemblyName, candidate.Entry))
            .ToArray();
    }

    private static int ScoreMissingDependencyCandidate(
        string normalizedAssemblyName,
        NexusMetadataCatalogEntry entry)
    {
        var score = 0;
        foreach (var dllName in entry.DllNames)
        {
            score = Math.Max(score, ScoreDependencyCandidateName(normalizedAssemblyName, dllName, exactScore: 120, versionedScore: 100));
        }

        foreach (var dllName in entry.DllVersions.Keys)
        {
            score = Math.Max(score, ScoreDependencyCandidateName(normalizedAssemblyName, dllName, exactScore: 125, versionedScore: 105));
        }

        score = Math.Max(score, ScoreDependencyCandidateName(normalizedAssemblyName, entry.Name, exactScore: 85, versionedScore: 60));
        score = Math.Max(score, ScoreDependencyCandidateName(normalizedAssemblyName, entry.SourceName, exactScore: 70, versionedScore: 50));
        return score;
    }

    private static int ScoreDependencyCandidateName(
        string normalizedAssemblyName,
        string? candidateName,
        int exactScore,
        int versionedScore)
    {
        var normalizedCandidateName = NormalizeDependencyLookupName(candidateName);
        if (string.IsNullOrWhiteSpace(normalizedCandidateName))
        {
            return 0;
        }

        if (normalizedCandidateName.Equals(normalizedAssemblyName, StringComparison.Ordinal))
        {
            return exactScore;
        }

        return HasVersionSuffix(normalizedCandidateName, normalizedAssemblyName)
            ? versionedScore
            : 0;
    }

    private static bool HasVersionSuffix(string normalizedCandidateName, string normalizedAssemblyName)
    {
        if (!normalizedCandidateName.StartsWith(normalizedAssemblyName, StringComparison.Ordinal)
            || normalizedCandidateName.Length == normalizedAssemblyName.Length)
        {
            return false;
        }

        var suffix = normalizedCandidateName[normalizedAssemblyName.Length..];
        return char.IsDigit(suffix[0])
            || (suffix.Length > 1 && suffix[0] == 'v' && char.IsDigit(suffix[1]));
    }

    private static string NormalizeDependencyLookupName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var fileName = Path.GetFileName(value.Trim());
        var extension = Path.GetExtension(fileName);
        if (extension.Equals(".dll", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".zip", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".rar", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".7z", StringComparison.OrdinalIgnoreCase))
        {
            fileName = Path.GetFileNameWithoutExtension(fileName);
        }

        return Regex.Replace(fileName, @"[^A-Za-z0-9]+", string.Empty, RegexOptions.CultureInvariant)
            .ToLowerInvariant();
    }

    private async Task EnableOrDownloadMissingDependencyCandidatesAsync(MissingDependencyReport report, string retryActionText)
    {
        var installedProviderIds = report.Issues
            .SelectMany(issue => issue.InstalledProviders)
            .DistinctBy(provider => provider.ModId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        EnableInstalledModsInActiveProfile(installedProviderIds.Select(provider => provider.ModId).ToArray());

        var candidates = GetUniqueMissingDependencyCandidates(report, includeInstalledProviderIssues: false);
        if (candidates.Count == 0)
        {
            var enabledNames = installedProviderIds.Length == 0
                ? "No automatic dependency candidates were available."
                : $"Enabled installed dependency providers: {string.Join(", ", installedProviderIds.Select(provider => provider.ModName))}.";
            MessageBox.Show(
                this,
                $"{enabledNames}\n\nReview the profile, then {retryActionText}.",
                "Missing dependencies",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var apiKey = GetConfiguredNexusApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            var answer = MessageBox.Show(
                this,
                "Nexus API key is not configured, so automatic dependency download is unavailable.\n\nOpen Nexus files pages for manual download instead?",
                "Missing dependencies",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);
            if (answer == MessageBoxResult.Yes)
            {
                OpenMissingDependencyCandidatePages(report);
            }

            return;
        }

        var downloadedArchives = new List<string>();
        var downloadFailures = new List<ModInstallOutcome>();
        var failedCandidates = new List<MissingDependencyCandidate>();
        var downloadRootPath = Path.Combine(_managerPaths.DownloadsPath, "dependencies");
        foreach (var candidate in candidates)
        {
            try
            {
                var target = await ResolveNexusCatalogDownloadTargetAsync(candidate.Entry, apiKey);
                if (target is null)
                {
                    downloadFailures.Add(ModInstallOutcome.Failure(
                        $"{GetDependencyCandidateDisplayName(candidate.Entry)}: dependency download",
                        "Metadata does not contain a usable Nexus file target."));
                    failedCandidates.Add(candidate);
                    continue;
                }

                var source = CreateNexusCatalogSource(candidate.Entry, target);
                var download = await _nexusModDownloadService.DownloadUpdateArchiveAsync(
                    source,
                    target.FileId,
                    apiKey,
                    downloadRootPath,
                    BuildNexusCatalogArchiveName(candidate.Entry, target));
                downloadedArchives.Add(download.ArchivePath);
            }
            catch (Exception exception) when (exception is HttpRequestException
                or TaskCanceledException
                or JsonException
                or IOException
                or InvalidOperationException
                or UnauthorizedAccessException)
            {
                downloadFailures.Add(ModInstallOutcome.Failure(
                    $"{GetDependencyCandidateDisplayName(candidate.Entry)}: dependency download",
                    exception.Message));
                failedCandidates.Add(candidate);
            }
        }

        if (downloadedArchives.Count > 0)
        {
            InstallModArchives(downloadedArchives, downloadFailures);
            MessageBox.Show(
                this,
                $"Dependency candidates were downloaded and installed.\n\nReview the active profile, then {retryActionText}.",
                "Missing dependencies",
                MessageBoxButton.OK,
                downloadFailures.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
            return;
        }

        ShowModInstallResults(downloadFailures);
        var fallbackAnswer = MessageBox.Show(
            this,
            "No dependency archives were downloaded.\n\nAutomatic Nexus downloads may require Nexus Premium for this account.\nOpen Nexus files pages for manual download?",
            "Missing dependencies",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (fallbackAnswer == MessageBoxResult.Yes)
        {
            OpenMissingDependencyCandidatePages(report with
            {
                Issues = report.Issues
                    .Select(issue => issue with
                    {
                        Candidates = issue.Candidates
                            .Where(candidate => failedCandidates.Any(failed => SameNexusCatalogEntry(failed.Entry, candidate.Entry)))
                            .ToArray()
                    })
                    .Where(issue => issue.Candidates.Count > 0)
                    .ToArray()
            });
        }
    }

    private void OpenMissingDependencyCandidatePages(MissingDependencyReport report)
    {
        var candidates = GetUniqueMissingDependencyCandidates(report, includeInstalledProviderIssues: true);
        if (candidates.Count == 0)
        {
            MessageBox.Show(
                this,
                "No Nexus page candidates were found for the missing dependencies.",
                "Missing dependencies",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        foreach (var candidate in candidates.Take(8))
        {
            var downloadReference = candidate.Entry.DownloadReference;
            var gameDomain = FirstNonEmpty(candidate.Entry.NexusGameDomain, downloadReference?.GameDomain);
            var modId = candidate.Entry.NexusModId ?? downloadReference?.ModId;
            if (!string.IsNullOrWhiteSpace(gameDomain) && modId is not null)
            {
                OpenNexusFilesPage(gameDomain, modId.Value);
            }
            else if (!string.IsNullOrWhiteSpace(candidate.Entry.NexusPageUrl))
            {
                OpenUri(candidate.Entry.NexusPageUrl, "Open Nexus page failed");
            }
        }

        if (candidates.Count > 8)
        {
            MessageBox.Show(
                this,
                $"Opened 8 Nexus pages. {candidates.Count - 8} more dependency pages were skipped to avoid opening too many browser tabs.",
                "Missing dependencies",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    private static IReadOnlyList<MissingDependencyCandidate> GetUniqueMissingDependencyCandidates(
        MissingDependencyReport report,
        bool includeInstalledProviderIssues)
    {
        return report.Issues
            .Where(issue => includeInstalledProviderIssues || issue.InstalledProviders.Count == 0)
            .SelectMany(issue => issue.Candidates)
            .GroupBy(candidate => BuildNexusCatalogEntryKey(candidate.Entry), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }

    private static bool SameNexusCatalogEntry(NexusMetadataCatalogEntry first, NexusMetadataCatalogEntry second)
    {
        return BuildNexusCatalogEntryKey(first).Equals(BuildNexusCatalogEntryKey(second), StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildMissingDependencyMessage(MissingDependencyReport report, string actionText)
    {
        var lines = new List<string>
        {
            "Enabled profile mods are missing DLL dependencies:",
            string.Empty
        };

        var shown = 0;
        foreach (var group in report.Issues.GroupBy(issue => issue.ModName).OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            lines.Add(group.Key);
            foreach (var issue in group.OrderBy(issue => issue.AssemblyName, StringComparer.OrdinalIgnoreCase))
            {
                shown++;
                lines.Add($"- {issue.AssemblyName}");
                if (issue.InstalledProviders.Count > 0)
                {
                    lines.Add($"  Installed but disabled: {string.Join(", ", issue.InstalledProviders.Select(provider => provider.ModName))}");
                }

                if (issue.Candidates.Count > 0)
                {
                    lines.Add($"  Nexus candidate: {string.Join(", ", issue.Candidates.Select(candidate => GetDependencyCandidateDisplayName(candidate.Entry)).Take(2))}");
                }
                else if (issue.InstalledProviders.Count == 0)
                {
                    lines.Add("  Nexus candidate: not found");
                }

                if (shown >= 12)
                {
                    break;
                }
            }

            if (shown >= 12)
            {
                break;
            }

            lines.Add(string.Empty);
        }

        var remaining = report.Issues.Count - shown;
        if (remaining > 0)
        {
            lines.Add($"... {remaining} more missing dependencies");
            lines.Add(string.Empty);
        }

        if (!string.IsNullOrWhiteSpace(report.CatalogWarning))
        {
            lines.Add(report.CatalogWarning);
            lines.Add(string.Empty);
        }

        lines.Add(actionText);
        return string.Join("\n", lines);
    }

    private static string GetDependencyCandidateDisplayName(NexusMetadataCatalogEntry entry)
    {
        var downloadReference = entry.DownloadReference;
        var modId = entry.NexusModId ?? downloadReference?.ModId;
        var version = GetKnownModVersion(entry.BestVersion);
        var name = string.IsNullOrWhiteSpace(entry.Name) ? "Unnamed Nexus mod" : entry.Name!.Trim();
        var versionText = string.IsNullOrWhiteSpace(version) ? string.Empty : $" {version}";
        var modIdText = modId is null ? string.Empty : $" (Nexus #{modId.Value})";
        return $"{name}{versionText}{modIdText}";
    }

    private static string BuildNexusCatalogEntryKey(NexusMetadataCatalogEntry entry)
    {
        var downloadReference = entry.DownloadReference;
        var gameDomain = FirstNonEmpty(entry.NexusGameDomain, downloadReference?.GameDomain);
        var modId = entry.NexusModId ?? downloadReference?.ModId;
        if (!string.IsNullOrWhiteSpace(gameDomain) && modId is not null)
        {
            return $"{gameDomain}:{modId.Value}";
        }

        return FirstNonEmpty(entry.Id, entry.Name, entry.SourceName, entry.DownloadUrl) ?? "unknown";
    }

    private void CleanDeployment_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProfile is null)
        {
            return;
        }

        var manifest = _profileDeployService.LoadManifest(_managerPaths, _currentProfile.Id);
        if (manifest is null)
        {
            RefreshDeployStatus();
            MessageBox.Show(this, "There are no deployed files for the active profile.", "Nothing to clean", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var answer = MessageBox.Show(
            this,
            $"Clean {manifest.Files.Count} managed deployed files from the game folder? Changed or unmanaged files will be preserved.",
            "Clean deployment",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            var result = _profileDeployService.Clean(_managerPaths, _currentProfile.Id);
            RefreshDeployStatus();
            ShowDeployResult("Deployment cleaned", result);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            RefreshDeployStatus();
            MessageBox.Show(this, exception.Message, "Clean failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void DownloadAndInstallBepInEx_Click(object sender, RoutedEventArgs e)
    {
        var installation = GetValidGameInstallationOrShowMessage();
        if (installation is null)
        {
            return;
        }

        try
        {
            var release = BepInExRelease.Current;
            Directory.CreateDirectory(_managerPaths.DownloadsPath);
            var archivePath = Path.Combine(_managerPaths.DownloadsPath, release.ArchiveFileName);

            SetStatus(BepInExStatusText, $"Downloading BepInEx {release.Version}...", "WarningBrush");
            BepInExDetailText.Text = release.DownloadUri.ToString();

            using var client = new HttpClient
            {
                Timeout = TimeSpan.FromMinutes(3)
            };
            await using (var source = await client.GetStreamAsync(release.DownloadUri))
            await using (var destination = File.Create(archivePath))
            {
                await source.CopyToAsync(destination);
            }

            TryInstallBepInExArchive(archivePath, installation);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            RefreshSetupStatus();
            MessageBox.Show(this, exception.Message, "BepInEx download failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeRestore_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            this,
            "UCU ModManager\n"
            + "Development Build · Alpha Public\n\n"
            + "Dev: Arch Blake\n"
            + "Nexus metadata repository: Jimmyking\n\n"
            + "Special thanks: Horus and VoidYuum",
            "About UCU ModManager",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e)
    {
        MoveSelectedMod(-1);
    }

    private void MoveDown_Click(object sender, RoutedEventArgs e)
    {
        MoveSelectedMod(1);
    }

    private void EnabledCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (_isLoading)
        {
            return;
        }

        SaveProfileFromRows();
    }

    private void ToggleAllMods_Click(object sender, RoutedEventArgs e)
    {
        if (_isLoading || _mods.Count == 0)
        {
            return;
        }

        var enableAll = _mods.Any(mod => !mod.IsEnabled);
        foreach (var mod in _mods)
        {
            mod.IsEnabled = enableAll;
        }

        SaveProfileFromRows();
    }

    private void ViewConflicts_Click(object sender, RoutedEventArgs e)
    {
        var overlayPreview = BuildCurrentOverlayPreview();
        if (overlayPreview is null || overlayPreview.Conflicts.Count == 0)
        {
            MessageBox.Show(this, "No active mod conflicts were found.", "Mod Conflicts", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        MessageBox.Show(
            this,
            BuildConflictDetailsMessage(overlayPreview.Conflicts),
            "Mod Conflicts",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private void ViewWarnings_Click(object sender, RoutedEventArgs e)
    {
        var overlayPreview = BuildCurrentOverlayPreview();
        var message = BuildWarningsDetailsMessage(overlayPreview);
        if (string.IsNullOrWhiteSpace(message))
        {
            MessageBox.Show(this, "No active warnings were found.", "Mod Warnings", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        MessageBox.Show(
            this,
            message,
            "Mod Warnings",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private void InstallModArchives_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Mod archives (*.zip)|*.zip|All files (*.*)|*.*",
            Multiselect = true,
            Title = "Install Mods"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        InstallModArchives(dialog.FileNames);
    }

    private void InstallModFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select folder with mod archives"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var archives = Directory.EnumerateFiles(dialog.FolderName, "*.zip", SearchOption.TopDirectoryOnly)
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (archives.Length == 0)
        {
            MessageBox.Show(this, "No .zip mod archives were found in the selected folder.", "No archives found", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        InstallModArchives(archives);
    }

    private async void LinkNexus_Click(object sender, RoutedEventArgs e)
    {
        var selectedMod = ModsListView.SelectedItem as ModRow;
        if (selectedMod is null)
        {
            MessageBox.Show(this, "Select an installed mod first.", "No mod selected", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var libraryEntry = _libraryEntries.FirstOrDefault(entry => entry.Mod.Id.Equals(selectedMod.Id, StringComparison.OrdinalIgnoreCase));
        if (libraryEntry is null)
        {
            MessageBox.Show(this, "The selected mod could not be found in the manager library.", "Link Nexus", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dialog = new NexusLinkDialog(new NexusLinkDialogInitial(
            libraryEntry.Mod.Name,
            _settings.NexusGameDomain,
            libraryEntry.Mod.Version,
            libraryEntry.Manifest.Source))
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true || dialog.Result is null)
        {
            return;
        }

        try
        {
            var completion = await BuildLinkedNexusManifestAsync(
                libraryEntry,
                dialog.Result);
            var updatedManifest = completion.Manifest;

            _libraryService.SaveManifest(libraryEntry.ManifestPath, updatedManifest);
            LoadMods();
            SelectModById(selectedMod.Id);
            var linkedSource = updatedManifest.Source;
            var versionLine = string.IsNullOrWhiteSpace(linkedSource?.FileVersion)
                ? string.Empty
                : $"\nVersion: {linkedSource.FileVersion}";
            var metadataLine = string.IsNullOrWhiteSpace(completion.Result?.ErrorMessage)
                ? string.Empty
                : $"\n\nMetadata completion note: {completion.Result.ErrorMessage}";
            MessageBox.Show(
                this,
                $"Linked '{libraryEntry.Mod.Name}' to Nexus #{linkedSource?.ModId}.{versionLine}{metadataLine}",
                "Link Nexus",
                MessageBoxButton.OK,
                string.IsNullOrWhiteSpace(completion.Result?.ErrorMessage)
                    ? MessageBoxImage.Information
                    : MessageBoxImage.Warning);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or HttpRequestException
            or TaskCanceledException)
        {
            MessageBox.Show(this, exception.Message, "Link Nexus failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void AutoLinkNexus_Click(object sender, RoutedEventArgs e)
    {
        await RunAutoLinkNexusAsync(
            showResults: true,
            showNoModsMessage: true,
            requireConfirmation: true,
            showBusyCursor: true,
            allowClearingUnmatchedExistingLinks: true);
    }

    private async Task RunAutoLinkNexusAsync(
        bool showResults,
        bool showNoModsMessage,
        bool requireConfirmation,
        bool showBusyCursor,
        bool allowClearingUnmatchedExistingLinks)
    {
        if (_libraryEntries.Count == 0)
        {
            if (showNoModsMessage)
            {
                MessageBox.Show(this, "No installed mods were found.", "Auto Link Nexus", MessageBoxButton.OK, MessageBoxImage.Information);
            }

            return;
        }

        if (_isAutoLinkNexusRunning)
        {
            if (showResults)
            {
                MessageBox.Show(this, "Auto Link Nexus is already running.", "Auto Link Nexus", MessageBoxButton.OK, MessageBoxImage.Information);
            }

            return;
        }

        if (requireConfirmation)
        {
            var answer = MessageBox.Show(
                this,
                "Automatically link installed mods to Nexus using the Casualties Unknown metadata catalog?\n\nThe manager will compare archive names, saved Nexus ids, mod names, and DLL names. Review the results before updating mods.",
                "Auto Link Nexus",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes)
            {
                return;
            }
        }

        VirtualLaunchProgressDialog? progressDialog = null;
        try
        {
            _isAutoLinkNexusRunning = true;
            AutoLinkNexusButton.IsEnabled = false;
            if (showBusyCursor)
            {
                Mouse.OverrideCursor = Cursors.Wait;
                progressDialog = ShowProgress("Auto Link Nexus", "Linking mods to Nexus", "Loading metadata catalog...");
            }

            SetAutoLinkStatus("Auto Link: loading metadata catalog...", "WarningBrush");
            await Task.Yield();

            var progress = new Progress<string>(message =>
            {
                SetAutoLinkStatus(message, "WarningBrush");
                UpdateProgress(progressDialog, message.Replace("Auto Link: ", string.Empty, StringComparison.OrdinalIgnoreCase));
            });
            var summary = await AutoLinkNexusModsAsync(progress, allowClearingUnmatchedExistingLinks);
            LoadMods();
            SetAutoLinkStatus(
                $"Auto Link: linked {summary.Linked}, repaired {summary.Repaired}, cleared {summary.Cleared}, skipped {summary.Skipped}",
                summary.Skipped == 0 && summary.ApiErrors == 0 && summary.SearchErrors == 0 ? "AccentBrush" : "WarningBrush");
            if (showResults)
            {
                ShowAutoLinkNexusResults(summary);
            }
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or HttpRequestException
            or TaskCanceledException)
        {
            SetAutoLinkStatus("Auto Link failed", "DangerBrush");
            if (showResults)
            {
                MessageBox.Show(this, exception.Message, "Auto Link Nexus failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally
        {
            _isAutoLinkNexusRunning = false;
            AutoLinkNexusButton.IsEnabled = true;
            CloseProgress(progressDialog);
            if (showBusyCursor)
            {
                Mouse.OverrideCursor = null;
            }
        }
    }

    private async Task<NexusLinkCompletion> BuildLinkedNexusManifestAsync(
        ModLibraryEntry entry,
        NexusLinkDialogResult result)
    {
        var source = new ModSourceInfo(
            "NexusMods",
            NormalizeNexusGameDomain(result.GameDomain),
            result.ModId,
            result.FileId,
            GetKnownModVersion(result.FileVersion) ?? GetKnownModVersion(entry.Mod.Version),
            entry.Manifest.Source?.FileTimestamp,
            entry.Manifest.Source?.SourceArchiveFileName ?? entry.Manifest.SourceArchiveFileName);
        var manifest = ApplyNexusSource(entry.Manifest, source);
        return await CompleteNexusSourceFromCatalogAsync(entry with { Manifest = manifest });
    }

    private async Task<NexusAutoLinkSummary> AutoLinkNexusModsAsync(
        IProgress<string>? progress = null,
        bool allowClearingUnmatchedExistingLinks = true)
    {
        var linked = 0;
        var completed = 0;
        var repaired = 0;
        var cleared = 0;
        var alreadyLinked = 0;
        var skipped = 0;
        var apiErrors = 0;
        var searchLinked = 0;
        var searchErrors = 0;
        var usedApi = false;
        var details = new List<string>();
        NexusMetadataCatalogLoadResult catalogLoad;
        try
        {
            catalogLoad = await _nexusMetadataCatalogService.LoadAsync(_managerPaths);
            if (!string.IsNullOrWhiteSpace(catalogLoad.Warning))
            {
                details.Add(catalogLoad.Warning);
            }
        }
        catch (InvalidOperationException exception)
        {
            return new NexusAutoLinkSummary(0, 0, 0, 0, 0, _libraryEntries.Count, 0, 0, 1, false, new[] { exception.Message });
        }

        var processed = 0;
        foreach (var entry in _libraryEntries)
        {
            processed++;
            progress?.Report($"Auto Link: {processed}/{_libraryEntries.Count} {entry.Mod.Name}");
            var match = _nexusMetadataMatcher.FindBestMatch(entry, catalogLoad.Entries);

            if (entry.Manifest.Source?.CanCheckUpdates == true)
            {
                if (match is null)
                {
                    if (allowClearingUnmatchedExistingLinks)
                    {
                        _libraryService.SaveManifest(entry.ManifestPath, ClearUnreliableNexusSource(entry.Manifest));
                        cleared++;
                        details.Add($"{entry.Mod.Name}: cleared unreliable Nexus link (not found in metadata catalog)");
                    }
                    else
                    {
                        skipped++;
                        details.Add($"{entry.Mod.Name}: kept existing Nexus link; metadata match was not found during startup");
                    }

                    continue;
                }

                var updatedSource = CreateNexusSourceFromMetadata(entry, match.Entry);
                var updatedManifest = ApplyNexusSource(entry.Manifest, updatedSource);
                var result = CheckNexusUpdateFromMetadata(updatedManifest, match.Entry);
                updatedManifest = ApplyNexusUpdateCheckResult(updatedManifest, result);
                _libraryService.SaveManifest(entry.ManifestPath, updatedManifest);

                if (entry.Manifest.Source.ModId != updatedSource.ModId)
                {
                    repaired++;
                    details.Add(BuildAutoLinkDetail(entry.Mod.Name, updatedManifest.Source, $"repaired from metadata ({match.Reason}, score {match.Score})"));
                }
                else
                {
                    completed++;
                    details.Add(BuildAutoLinkDetail(entry.Mod.Name, updatedManifest.Source, $"refreshed from metadata ({match.Reason}, score {match.Score})"));
                }

                continue;
            }

            if (match is null)
            {
                skipped++;
                details.Add($"{entry.Mod.Name}: skipped, no reliable metadata match found");
                continue;
            }

            var manifest = ApplyNexusSource(entry.Manifest, CreateNexusSourceFromMetadata(entry, match.Entry));
            var checkResult = CheckNexusUpdateFromMetadata(manifest, match.Entry);
            manifest = ApplyNexusUpdateCheckResult(manifest, checkResult);
            _libraryService.SaveManifest(entry.ManifestPath, manifest);
            linked++;
            searchLinked++;

            details.Add(BuildAutoLinkDetail(entry.Mod.Name, manifest.Source, $"linked from metadata ({match.Reason}, score {match.Score})"));
        }

        return new NexusAutoLinkSummary(linked, completed, repaired, cleared, alreadyLinked, skipped, apiErrors, searchLinked, searchErrors, usedApi, details);
    }

    private static bool VersionsEqual(string first, string second)
    {
        return NormalizeComparableVersion(first)
            .Equals(NormalizeComparableVersion(second), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeComparableVersion(string version)
    {
        var value = version.Trim();
        if (value.StartsWith('v') || value.StartsWith('V'))
        {
            value = value[1..];
        }

        var forIndex = value.IndexOf("-for-", StringComparison.OrdinalIgnoreCase);
        if (forIndex >= 0)
        {
            value = value[..forIndex];
        }

        return value.Replace('-', '.');
    }

    private static string GetSourceArchiveFileName(ModLibraryEntry entry)
    {
        return string.IsNullOrWhiteSpace(entry.Manifest.Source?.SourceArchiveFileName)
            ? entry.Manifest.SourceArchiveFileName
            : entry.Manifest.Source!.SourceArchiveFileName;
    }

    private static ModManifest ApplyNexusUpdateCheckResult(ModManifest manifest, NexusUpdateCheckResult result)
    {
        if (manifest.Source is null)
        {
            return manifest;
        }

        var source = manifest.Source with
        {
            GameDomain = string.IsNullOrWhiteSpace(result.GameDomain)
                ? manifest.Source.GameDomain
                : result.GameDomain,
            ModId = result.NexusModId ?? manifest.Source.ModId,
            LastUpdateStatus = BuildUpdateStatusText(result),
            LastLatestVersion = result.LatestVersion,
            LastLatestFileId = result.LatestFileId,
            LastCheckedAt = DateTimeOffset.UtcNow
        };

        if (result.Status.Equals("Latest version", StringComparison.OrdinalIgnoreCase)
            && !result.IsUpdateAvailable
            && string.IsNullOrWhiteSpace(result.ErrorMessage))
        {
            source = source with
            {
                FileId = source.FileId ?? result.LatestFileId,
                FileVersion = ResolveLatestVersionForCurrentInstall(source, result)
            };
        }

        return manifest with { Source = source };
    }

    private static string? ResolveLatestVersionForCurrentInstall(ModSourceInfo source, NexusUpdateCheckResult result)
    {
        var latestVersion = GetKnownModVersion(result.LatestVersion);
        var currentVersion = GetKnownModVersion(source.FileVersion);
        var archiveVersion = GetKnownModVersion(ModSourceDetector.DetectVersion(source.SourceArchiveFileName));
        if (string.IsNullOrWhiteSpace(latestVersion))
        {
            return currentVersion ?? archiveVersion;
        }

        if (source.FileId is not null
            && source.FileId.Value.ToString().Equals(TrimSimpleVersion(latestVersion), StringComparison.OrdinalIgnoreCase))
        {
            return latestVersion;
        }

        if (source.FileId is not null && result.LatestFileId is not null && source.FileId.Value == result.LatestFileId.Value)
        {
            return latestVersion;
        }

        if (!string.IsNullOrWhiteSpace(archiveVersion) && VersionsEqual(archiveVersion, latestVersion))
        {
            return latestVersion;
        }

        if (!string.IsNullOrWhiteSpace(archiveVersion)
            && CompareSemanticVersions(archiveVersion, latestVersion) is int archiveComparison
            && archiveComparison >= 0)
        {
            return archiveVersion;
        }

        if (!string.IsNullOrWhiteSpace(currentVersion)
            && CompareSemanticVersions(currentVersion, latestVersion) is int currentComparison
            && currentComparison >= 0)
        {
            return currentVersion;
        }

        return currentVersion ?? archiveVersion ?? latestVersion;
    }

    private static ModManifest ApplyNexusSource(ModManifest manifest, ModSourceInfo source)
    {
        var normalizedSource = source with
        {
            FileVersion = GetKnownModVersion(source.FileVersion)
        };
        var version = normalizedSource.FileVersion
            ?? GetKnownModVersion(manifest.Mod.Version)
            ?? "unknown";
        return manifest with
        {
            Mod = manifest.Mod with { Version = version },
            Source = normalizedSource
        };
    }

    private static ModManifest ClearUnreliableNexusSource(ModManifest manifest)
    {
        if (manifest.Source is null)
        {
            return manifest;
        }

        return manifest with
        {
            Source = manifest.Source with
            {
                ModId = null,
                FileId = null,
                LastUpdateStatus = null,
                LastLatestVersion = null,
                LastLatestFileId = null,
                LastCheckedAt = null
            }
        };
    }

    private string NormalizeNexusGameDomain(string? gameDomain)
    {
        var domain = string.IsNullOrWhiteSpace(gameDomain)
            ? _settings.NexusGameDomain
            : gameDomain.Trim();
        return ShouldResetNexusGameDomain(domain)
            ? ManagerSettings.Empty.NexusGameDomain
            : domain;
    }

    private static bool ShouldCompleteNexusSource(ModSourceInfo source)
    {
        return source.FileId is null || GetKnownModVersion(source.FileVersion) is null;
    }

    private static string? GetKnownModVersion(string? version)
    {
        return string.IsNullOrWhiteSpace(version) || version.Equals("unknown", StringComparison.OrdinalIgnoreCase)
            ? null
            : version.Trim();
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
    }

    private static string? SanitizeFileNameSegment(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(value
            .Select(character => invalid.Contains(character) ? '_' : character)
            .ToArray())
            .Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? null : cleaned;
    }

    private static string BuildAutoLinkDetail(string modName, ModSourceInfo? source, string action)
    {
        var version = string.IsNullOrWhiteSpace(source?.FileVersion)
            ? string.Empty
            : $", version {source.FileVersion}";
        return $"{modName}: {action} Nexus #{source?.ModId}{version}";
    }

    private void ShowAutoLinkNexusResults(NexusAutoLinkSummary summary)
    {
        var lines = _settings.ShowAdvancedModColumns
            ? BuildAdvancedAutoLinkResultLines(summary)
            : BuildCompactAutoLinkResultLines(summary);

        MessageBox.Show(
            this,
            string.Join("\n", lines),
            "Auto Link Nexus",
            MessageBoxButton.OK,
            summary.ApiErrors == 0 && summary.SearchErrors == 0 && summary.Skipped == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private static List<string> BuildCompactAutoLinkResultLines(NexusAutoLinkSummary summary)
    {
        var totalReady = summary.Linked + summary.Completed + summary.Repaired + summary.AlreadyLinked;
        var lines = new List<string>
        {
            "Auto Link completed.",
            string.Empty,
            $"Ready Nexus links: {totalReady}",
            $"New links: {summary.Linked}",
            $"Refreshed existing links: {summary.Completed}",
            $"Repaired links: {summary.Repaired}"
        };

        if (summary.Cleared > 0)
        {
            lines.Add($"Cleared unreliable links: {summary.Cleared}");
        }

        if (summary.Skipped > 0)
        {
            lines.Add($"Skipped: {summary.Skipped}");
        }

        var errors = summary.ApiErrors + summary.SearchErrors;
        if (errors > 0)
        {
            lines.Add($"Errors: {errors}");
        }

        if (summary.Skipped > 0 || errors > 0)
        {
            lines.Add(string.Empty);
            lines.Add("Enable Advanced Mode to see the detailed matching report.");
        }

        return lines;
    }

    private static List<string> BuildAdvancedAutoLinkResultLines(NexusAutoLinkSummary summary)
    {
        var lines = new List<string>
        {
            $"Linked: {summary.Linked}",
            $"Linked by metadata: {summary.SearchLinked}",
            $"Repaired: {summary.Repaired}",
            $"Cleared unreliable: {summary.Cleared}",
            $"Completed existing links: {summary.Completed}",
            $"Already linked: {summary.AlreadyLinked}",
            $"Skipped: {summary.Skipped}",
            $"Metadata errors: {summary.SearchErrors}"
        };

        if (!summary.UsedApi)
        {
            lines.Add("Nexus API was not used for matching. The metadata catalog provided ids, versions, and images.");
        }

        if (summary.Details.Count > 0)
        {
            lines.Add(string.Empty);
            lines.AddRange(summary.Details.Take(12));
            if (summary.Details.Count > 12)
            {
                lines.Add($"... {summary.Details.Count - 12} more");
            }
        }

        return lines;
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        var checkableEntries = GetCheckableNexusEntries();
        if (checkableEntries.Length == 0)
        {
            MessageBox.Show(this, "No installed mods are linked to a Nexus mod id yet.", "Check Updates", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var progress = ShowProgress("Check Updates", "Checking Nexus updates", "Refreshing metadata...");
            var results = await CheckNexusUpdatesAsync(checkableEntries);
            CloseProgress(progress);
            ShowUpdateCheckResults(results);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException)
        {
            CloseProgress(OwnedWindows.OfType<VirtualLaunchProgressDialog>().FirstOrDefault(window => window.Title == "Check Updates"));
            MessageBox.Show(this, exception.Message, "Check Updates failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void UpdateMods_Click(object sender, RoutedEventArgs e)
    {
        var apiKey = GetConfiguredNexusApiKey();
        var checkableEntries = GetCheckableNexusEntries();
        if (checkableEntries.Length == 0)
        {
            MessageBox.Show(this, "No installed mods are linked to a Nexus mod id yet.", "Update Mods", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var checkProgress = ShowProgress("Update Mods", "Checking updates", "Refreshing metadata...");
            var results = await CheckNexusUpdatesAsync(checkableEntries);
            CloseProgress(checkProgress);
            var updatePlans = BuildNexusUpdatePlans(checkableEntries, results);
            var skippedUpdates = results
                .Where(result => result.IsUpdateAvailable)
                .Where(result => !CanDownloadUpdate(result))
                .ToArray();
            var apiErrorFallbacks = results
                .Where(result => !string.IsNullOrWhiteSpace(result.ErrorMessage))
                .ToArray();
            if (updatePlans.Count == 0)
            {
                ShowNoDownloadableUpdates(results, skippedUpdates);
                OfferManualFallbackForUpdates(skippedUpdates.Concat(apiErrorFallbacks));
                return;
            }

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                MessageBox.Show(
                    this,
                    "Nexus API key is not configured. Update checks use the metadata catalog, but automatic downloads still need your Nexus Mods API key. You can download manually from Nexus and use Install Mods.",
                    "Nexus API key required",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                OfferManualFallbackForUpdates(updatePlans.Select(plan => plan.Result).Concat(skippedUpdates));
                SelectNavigationView("Settings");
                return;
            }

            var answer = MessageBox.Show(
                this,
                BuildUpdateModsPrompt(updatePlans, skippedUpdates),
                "Update Mods",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes)
            {
                return;
            }

            var downloadedArchives = new List<string>();
            var downloadFailures = new List<ModInstallOutcome>();
            var failedDownloadPlans = new List<NexusModUpdatePlan>();
            var downloadRootPath = Path.Combine(_managerPaths.DownloadsPath, "nexus-updates");
            var progress = ShowProgress("Update Mods", "Downloading updates", "Starting downloads...");
            foreach (var updatePlan in updatePlans)
            {
                var row = _mods.FirstOrDefault(mod => mod.Id.Equals(updatePlan.Entry.Mod.Id, StringComparison.OrdinalIgnoreCase));
                if (row is not null)
                {
                    row.UpdateStatus = "Downloading...";
                    ModsListView.Items.Refresh();
                }
                UpdateProgress(progress, $"Downloading {updatePlan.Entry.Mod.Name}...");

                try
                {
                    var source = updatePlan.Entry.Manifest.Source! with
                    {
                        GameDomain = updatePlan.Result.GameDomain,
                        ModId = updatePlan.Result.NexusModId,
                        FileId = updatePlan.Result.LatestFileId,
                        FileVersion = updatePlan.Result.LatestVersion
                    };
                    var download = await _nexusModDownloadService.DownloadUpdateArchiveAsync(
                        source,
                        updatePlan.Result.LatestFileId!.Value,
                        apiKey,
                        downloadRootPath,
                        BuildNexusUpdateArchiveFileName(updatePlan));
                    downloadedArchives.Add(download.ArchivePath);

                    if (row is not null)
                    {
                        row.UpdateStatus = "Downloaded";
                    }
                }
                catch (Exception exception) when (exception is HttpRequestException
                    or TaskCanceledException
                    or JsonException
                    or IOException
                    or InvalidOperationException
                    or UnauthorizedAccessException)
                {
                    downloadFailures.Add(ModInstallOutcome.Failure($"{updatePlan.Entry.Mod.Name}: Nexus download", exception.Message));
                    failedDownloadPlans.Add(updatePlan);
                    if (row is not null)
                    {
                        row.UpdateStatus = "Download failed";
                    }
                }

                ModsListView.Items.Refresh();
            }

            if (downloadedArchives.Count == 0)
            {
                CloseProgress(progress);
                ShowModInstallResults(downloadFailures);
                OfferManualFallbackForUpdates(skippedUpdates
                    .Concat(apiErrorFallbacks)
                    .Concat(failedDownloadPlans.Select(plan => plan.Result)));
                return;
            }

            UpdateProgress(progress, "Installing downloaded archives...");
            foreach (var row in _mods.Where(mod => downloadedArchives.Count > 0 && mod.UpdateStatus == "Downloaded"))
            {
                row.UpdateStatus = "Install queued";
            }

            ModsListView.Items.Refresh();
            CloseProgress(progress);
            InstallModArchives(downloadedArchives, downloadFailures);
            OfferManualFallbackForUpdates(skippedUpdates
                .Concat(apiErrorFallbacks)
                .Concat(failedDownloadPlans.Select(plan => plan.Result)));
        }
        catch (Exception exception) when (exception is HttpRequestException
            or TaskCanceledException
            or JsonException
            or IOException
            or InvalidOperationException
            or UnauthorizedAccessException)
        {
            CloseProgress(OwnedWindows.OfType<VirtualLaunchProgressDialog>().FirstOrDefault(window => window.Title == "Update Mods"));
            MessageBox.Show(this, exception.Message, "Update Mods failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void RemoveSelectedMod_Click(object sender, RoutedEventArgs e)
    {
        var selectedMod = ModsListView.SelectedItem as ModRow;
        if (selectedMod is null)
        {
            MessageBox.Show(this, "Select an installed mod first.", "No mod selected", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var deploymentManifest = _currentProfile is null
            ? null
            : _profileDeployService.LoadManifest(_managerPaths, _currentProfile.Id);
        var deployedFiles = deploymentManifest?.Files
            .Count(file => file.OwningModId.Equals(selectedMod.Id, StringComparison.OrdinalIgnoreCase)) ?? 0;

        var message = $"Remove '{selectedMod.Name}' from UCU ModManager storage?";
        if (deployedFiles > 0)
        {
            message += $"\n\nThis mod currently has {deployedFiles} managed deployed files in the game folder. Choose Yes to clean the active deployment first, No to remove manager storage only, or Cancel.";
            var answer = MessageBox.Show(this, message, "Remove installed mod", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
            if (answer == MessageBoxResult.Cancel)
            {
                return;
            }

            if (answer == MessageBoxResult.Yes && _currentProfile is not null)
            {
                var cleanResult = _profileDeployService.Clean(_managerPaths, _currentProfile.Id);
                ShowDeployResult("Deployment cleaned", cleanResult);
            }
        }
        else
        {
            var answer = MessageBox.Show(this, message, "Remove installed mod", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes)
            {
                return;
            }
        }

        try
        {
            _modRemovalService.RemoveInstalledMod(_managerPaths, selectedMod.Id);
            LoadMods();
            MessageBox.Show(this, $"Removed '{selectedMod.Name}'.", "Mod removed", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, exception.Message, "Remove failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void NavigationListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized)
        {
            return;
        }

        var selectedView = (NavigationListBox.SelectedItem as ListBoxItem)?.Tag as string ?? "Mods";
        ModsView.Visibility = selectedView.Equals("Mods", StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible
            : Visibility.Collapsed;
        ProfilesView.Visibility = selectedView.Equals("Profiles", StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible
            : Visibility.Collapsed;
        NexusBrowserView.Visibility = selectedView.Equals("NexusBrowser", StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible
            : Visibility.Collapsed;
        ModpacksView.Visibility = selectedView.Equals("Modpacks", StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible
            : Visibility.Collapsed;
        SettingsView.Visibility = selectedView.Equals("Settings", StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (ProfilesView.Visibility == Visibility.Visible)
        {
            SaveProfileFromRows();
            RefreshProfilePageStatus();
        }

        if (SettingsView.Visibility == Visibility.Visible)
        {
            RefreshSettingsStatus();
        }

        if (NexusBrowserView.Visibility == Visibility.Visible)
        {
            _ = EnsureNexusCatalogLoadedAsync(forceRefresh: false);
        }

        if (ModpacksView.Visibility == Visibility.Visible)
        {
            RefreshModpacksView();
        }
    }

    private void SelectNavigationView(string tag)
    {
        foreach (var item in NavigationListBox.Items.OfType<ListBoxItem>())
        {
            if (string.Equals(item.Tag as string, tag, StringComparison.OrdinalIgnoreCase))
            {
                NavigationListBox.SelectedItem = item;
                return;
            }
        }
    }

    private async void RefreshNexusCatalog_Click(object sender, RoutedEventArgs e)
    {
        await EnsureNexusCatalogLoadedAsync(forceRefresh: true);
    }

    private async void RefreshNexusMetadata_Click(object sender, RoutedEventArgs e)
    {
        RefreshNexusMetadataButton.IsEnabled = false;
        NexusMetadataStatusText.Text = "Refreshing Nexus metadata from GitHub...";
        NexusMetadataStatusText.Foreground = (Brush)FindResource("MutedTextBrush");

        try
        {
            var catalogLoad = await _nexusMetadataCatalogService.LoadAsync(_managerPaths, forceRefresh: true);
            _nexusCatalogEntries = catalogLoad.Entries;
            RefreshNexusMetadataStatusText(catalogLoad.Status);
            if (NexusBrowserView.Visibility == Visibility.Visible)
            {
                ApplyNexusCatalogFilter();
            }
        }
        catch (Exception exception) when (exception is HttpRequestException
            or TaskCanceledException
            or IOException
            or JsonException
            or InvalidOperationException
            or UnauthorizedAccessException)
        {
            RefreshNexusMetadataStatusText();
            MessageBox.Show(this, exception.Message, "Nexus metadata refresh failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            RefreshNexusMetadataButton.IsEnabled = true;
        }
    }

    private async Task EnsureNexusCatalogLoadedAsync(bool forceRefresh)
    {
        if (!forceRefresh && _nexusCatalogEntries.Count > 0)
        {
            ApplyNexusCatalogFilter();
            return;
        }

        RefreshNexusCatalogButton.IsEnabled = false;
        NexusCatalogStatusText.Text = forceRefresh
            ? "Refreshing Nexus metadata catalog..."
            : "Loading Nexus metadata catalog...";

        try
        {
            var catalogLoad = await _nexusMetadataCatalogService.LoadAsync(_managerPaths, forceRefresh);
            _nexusCatalogEntries = catalogLoad.Entries;
            ApplyNexusCatalogFilter();
            RefreshNexusMetadataStatusText(catalogLoad.Status);
            var source = catalogLoad.IsFromCache ? "cache" : "network";
            NexusCatalogStatusText.Text = $"{_nexusCatalogEntries.Count} mods loaded from {source}. Metadata: {FormatLocalDateTime(catalogLoad.Status.CatalogLastModifiedAt)}.";
            if (!string.IsNullOrWhiteSpace(catalogLoad.Warning))
            {
                NexusCatalogStatusText.Text += $" {catalogLoad.Warning}";
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException or JsonException or InvalidOperationException)
        {
            NexusCatalogStatusText.Text = "Nexus catalog failed to load.";
            MessageBox.Show(this, exception.Message, "Nexus catalog failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            RefreshNexusCatalogButton.IsEnabled = true;
        }
    }

    private void NexusCatalogSearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyNexusCatalogFilter();
    }

    private void ApplyNexusCatalogFilter()
    {
        var selectedId = (NexusCatalogListView.SelectedItem as NexusCatalogRow)?.CatalogId;
        var query = NexusCatalogSearchTextBox.Text.Trim();
        var filtered = _nexusCatalogEntries
            .Where(entry => MatchesNexusCatalogQuery(entry, query))
            .Select(entry => NexusCatalogRow.FromEntry(entry, IsCatalogEntryInstalled(entry)))
            .OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        _nexusCatalogRows.Clear();
        foreach (var row in filtered)
        {
            _nexusCatalogRows.Add(row);
        }

        NexusCatalogStatusText.Text = $"{filtered.Length} of {_nexusCatalogEntries.Count} mods shown.";
        var selected = _nexusCatalogRows.FirstOrDefault(row => row.CatalogId.Equals(selectedId, StringComparison.OrdinalIgnoreCase))
            ?? _nexusCatalogRows.FirstOrDefault();
        NexusCatalogListView.SelectedItem = selected;
        if (selected is null)
        {
            ShowNexusCatalogDetails(null);
        }
    }

    private static bool MatchesNexusCatalogQuery(NexusMetadataCatalogEntry entry, string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        return new[]
        {
            entry.Name,
            entry.Author,
            entry.NexusModId?.ToString(),
            entry.Version,
            entry.BepInExVersion,
            entry.DllVersion,
            entry.SourceName
        }
        .Concat(entry.DllNames)
        .Any(value => !string.IsNullOrWhiteSpace(value)
            && value.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    private bool IsCatalogEntryInstalled(NexusMetadataCatalogEntry entry)
    {
        var downloadReference = entry.DownloadReference;
        var gameDomain = FirstNonEmpty(entry.NexusGameDomain, downloadReference?.GameDomain);
        var modId = entry.NexusModId ?? downloadReference?.ModId;
        if (string.IsNullOrWhiteSpace(gameDomain) || modId is null)
        {
            return false;
        }

        return _libraryEntries.Any(libraryEntry =>
            libraryEntry.Manifest.Source?.ModId == modId
            && string.Equals(libraryEntry.Manifest.Source.GameDomain, gameDomain, StringComparison.OrdinalIgnoreCase));
    }

    private void NexusCatalogListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ShowNexusCatalogDetails(NexusCatalogListView.SelectedItem as NexusCatalogRow);
    }

    private void ShowNexusCatalogDetails(NexusCatalogRow? row)
    {
        if (row is null)
        {
            NexusCatalogNameText.Text = "No mod selected";
            NexusCatalogMetaText.Text = string.Empty;
            NexusCatalogStatsText.Text = string.Empty;
            NexusCatalogDescriptionText.Text = string.Empty;
            NexusCatalogImage.Source = null;
            DownloadNexusCatalogButton.IsEnabled = false;
            OpenNexusCatalogButton.IsEnabled = false;
            return;
        }

        var entry = row.Entry;
        NexusCatalogNameText.Text = row.Name;
        NexusCatalogMetaText.Text = $"{row.Version} - {row.Author} - Nexus #{row.NexusModIdText}";
        NexusCatalogStatsText.Text = $"Downloads: {row.Downloads} - Endorsements: {FormatNullableCount(entry.Statistics?.Endorsements)}";
        NexusCatalogDescriptionText.Text = CleanNexusDescription(entry.Description);
        DownloadNexusCatalogButton.IsEnabled = row.CanDownload;
        OpenNexusCatalogButton.IsEnabled = !string.IsNullOrWhiteSpace(entry.NexusPageUrl);
        SetNexusCatalogImage(entry.Images.FirstOrDefault() ?? entry.BestIconUrl);
    }

    private void SetNexusCatalogImage(string? imageUrl)
    {
        var requestId = ++_nexusBrowserImageRequestId;
        NexusCatalogImage.Source = null;
        _ = LoadNexusCatalogImageAsync(imageUrl, requestId);
    }

    private async Task LoadNexusCatalogImageAsync(string? imageUrl, int requestId)
    {
        if (string.IsNullOrWhiteSpace(imageUrl)
            || !Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri))
        {
            return;
        }

        if (_imageCache.TryGetValue(uri.AbsoluteUri, out var cachedImage))
        {
            if (requestId == _nexusBrowserImageRequestId)
            {
                NexusCatalogImage.Source = cachedImage;
            }

            return;
        }

        try
        {
            var image = await DownloadBitmapImageAsync(uri);
            if (image is null)
            {
                return;
            }

            CacheImage(uri.AbsoluteUri, image);

            if (requestId == _nexusBrowserImageRequestId)
            {
                NexusCatalogImage.Source = image;
            }
        }
        catch (Exception exception) when (exception is HttpRequestException
            or TaskCanceledException
            or IOException
            or InvalidDataException
            or NotSupportedException
            or ObjectDisposedException)
        {
        }
    }

    private async void DownloadNexusCatalog_Click(object sender, RoutedEventArgs e)
    {
        if (NexusCatalogListView.SelectedItem is not NexusCatalogRow selectedRow)
        {
            MessageBox.Show(this, "Select a Nexus mod first.", "No mod selected", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var apiKey = GetConfiguredNexusApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            ShowNexusCatalogManualFallback(selectedRow.Entry, "Nexus API key is required to download mods automatically.");
            return;
        }

        DownloadNexusCatalogButton.IsEnabled = false;
        NexusCatalogStatusText.Text = $"Preparing download: {selectedRow.Name}";

        try
        {
            var target = await ResolveNexusCatalogDownloadTargetAsync(selectedRow.Entry, apiKey);
            if (target is null)
            {
                ShowNexusCatalogManualFallback(selectedRow.Entry, "This catalog entry does not expose a downloadable Nexus file id.");
                return;
            }

            var source = CreateNexusCatalogSource(selectedRow.Entry, target);
            var download = await _nexusModDownloadService.DownloadUpdateArchiveAsync(
                source,
                target.FileId,
                apiKey,
                Path.Combine(_managerPaths.DownloadsPath, "nexus-browser"),
                BuildNexusCatalogArchiveName(selectedRow.Entry, target));
            NexusCatalogStatusText.Text = $"Downloaded {selectedRow.Name}. Installing...";
            InstallModArchives(new[] { download.ArchivePath });
            NexusCatalogStatusText.Text = $"Installed {selectedRow.Name}.";
        }
        catch (Exception exception) when (exception is HttpRequestException
            or TaskCanceledException
            or JsonException
            or IOException
            or InvalidOperationException
            or UnauthorizedAccessException)
        {
            NexusCatalogStatusText.Text = "Nexus download failed.";
            ShowNexusCatalogManualFallback(selectedRow.Entry, exception.Message);
        }
        finally
        {
            DownloadNexusCatalogButton.IsEnabled = NexusCatalogListView.SelectedItem is NexusCatalogRow row && row.CanDownload;
        }
    }

    private async Task<NexusDownloadTarget?> ResolveNexusCatalogDownloadTargetAsync(
        NexusMetadataCatalogEntry entry,
        string apiKey)
    {
        var downloadReference = entry.DownloadReference;
        var gameDomain = NormalizeNexusGameDomain(FirstNonEmpty(entry.NexusGameDomain, downloadReference?.GameDomain));
        var modId = entry.NexusModId ?? downloadReference?.ModId;
        if (string.IsNullOrWhiteSpace(gameDomain) || modId is null)
        {
            return null;
        }

        var fingerprint = NexusModFilesService.BuildCacheFingerprint(
            gameDomain,
            modId.Value,
            downloadReference?.FileId,
            entry.BestVersion,
            entry.Name);
        var filesResult = await _nexusModFilesService.LoadOrRefreshAsync(
            _managerPaths,
            gameDomain,
            modId.Value,
            fingerprint,
            apiKey);
        var file = ChooseBestNexusFile(filesResult.Files, entry.BestVersion);
        if (file is not null)
        {
            return new NexusDownloadTarget(gameDomain, modId.Value, file.FileId, file.Version, file.FileName);
        }

        return downloadReference?.FileId is null
            ? null
            : new NexusDownloadTarget(
                gameDomain,
                modId.Value,
                downloadReference.FileId.Value,
                GetKnownModVersion(entry.BestVersion) ?? entry.BestVersion,
                entry.Name);
    }

    private static NexusModFileInfo? ChooseBestNexusFile(
        IReadOnlyList<NexusModFileInfo> files,
        string? preferredVersion)
    {
        if (files.Count == 0)
        {
            return null;
        }

        var currentFiles = files.Where(file => !file.IsOldVersion).ToArray();
        var candidates = currentFiles.Length == 0 ? files : currentFiles;
        var knownPreferredVersion = GetKnownModVersion(preferredVersion);
        if (!string.IsNullOrWhiteSpace(knownPreferredVersion))
        {
            var versionMatch = candidates.FirstOrDefault(file => VersionsEqual(file.Version, knownPreferredVersion));
            if (versionMatch is not null)
            {
                return versionMatch;
            }
        }

        return ChooseLatestUpdateFile(candidates);
    }

    private ModSourceInfo CreateNexusCatalogSource(
        NexusMetadataCatalogEntry entry,
        NexusDownloadTarget target)
    {
        return new ModSourceInfo(
            "NexusMods",
            target.GameDomain,
            target.ModId,
            target.FileId,
            GetKnownModVersion(target.Version) ?? GetKnownModVersion(entry.BestVersion),
            null,
            target.FileName ?? entry.Name ?? $"nexus-{target.ModId}-{target.FileId}.zip",
            null,
            GetKnownModVersion(entry.BestVersion) ?? entry.BestVersion,
            DateTimeOffset.UtcNow,
            entry.Name,
            entry.Author,
            entry.NexusPageUrl,
            entry.BestIconUrl,
            entry.Images,
            entry.Description,
            entry.Statistics?.Endorsements,
            entry.Statistics?.UniqueDownloads,
            entry.Statistics?.TotalDownloads,
            entry.Statistics?.TotalViews,
            target.FileId);
    }

    private static string BuildNexusCatalogArchiveName(
        NexusMetadataCatalogEntry entry,
        NexusDownloadTarget target)
    {
        var name = SanitizeFileNameSegment(entry.Name) ?? "nexus-mod";
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return $"{name}-{target.ModId}-{target.FileId}-{timestamp}.zip";
    }

    private void OpenNexusCatalog_Click(object sender, RoutedEventArgs e)
    {
        if (NexusCatalogListView.SelectedItem is NexusCatalogRow selectedRow
            && !string.IsNullOrWhiteSpace(selectedRow.Entry.NexusPageUrl))
        {
            OpenUri(selectedRow.Entry.NexusPageUrl, "Open Nexus page failed");
        }
    }

    private void ShowNexusCatalogManualFallback(NexusMetadataCatalogEntry entry, string reason)
    {
        var lines = new List<string>
        {
            reason,
            string.Empty,
            "Automatic downloads may require Nexus Premium for this account.",
            "Open the Nexus files page manually?"
        };
        var answer = MessageBox.Show(
            this,
            string.Join("\n", lines),
            "Manual Nexus Download",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        var downloadReference = entry.DownloadReference;
        var gameDomain = FirstNonEmpty(entry.NexusGameDomain, downloadReference?.GameDomain);
        var modId = entry.NexusModId ?? downloadReference?.ModId;
        if (!string.IsNullOrWhiteSpace(gameDomain) && modId is not null)
        {
            OpenNexusFilesPage(gameDomain, modId.Value);
            return;
        }

        if (!string.IsNullOrWhiteSpace(entry.NexusPageUrl))
        {
            OpenUri(entry.NexusPageUrl, "Open Nexus page failed");
        }
    }

    private void RefreshModpacksView()
    {
        RefreshModpackExportProfilePreview();

        if (_importedUcuModpack is null)
        {
            ModpacksStatusText.Text = "Export a profile as a lightweight .UCU recipe or a portable .UCUP package, or import a modpack from another user.";
            _ucuModpackRows.Clear();
            ImportedUcuNameText.Text = "No modpack imported";
            ImportedUcuPathText.Text = string.Empty;
            ImportedUcuSummaryText.Text = "Imported .UCU and .UCUP modpacks will appear here for review before installation.";
            InstallImportedUcuModpackButton.IsEnabled = false;
            OpenImportedUcuPagesButton.IsEnabled = false;
            return;
        }

        ShowImportedUcuModpack(_importedUcuModpack, _importedUcuModpackPath);
    }

    private void RefreshModpackExportProfilePreview()
    {
        _modpackProfileRows.Clear();
        var profile = CreateProfileSnapshotForExport(GetSelectedModpackExportProfileId());
        if (profile is null)
        {
            ModpackExportProfileSummaryText.Text = "No profile selected.";
            return;
        }

        var package = BuildUcuModpackPackage(profile, portable: false);
        foreach (var row in package.Mods
            .OrderBy(mod => mod.Priority)
            .Select(mod => UcuModpackModRow.FromProfileMod(mod, _settings.ShowAdvancedModColumns)))
        {
            _modpackProfileRows.Add(row);
        }

        var enabled = package.Mods.Count(mod => mod.IsEnabled);
        var linked = package.Mods.Count(mod => GetUcuModId(mod) is not null && !string.IsNullOrWhiteSpace(GetUcuGameDomain(mod)));
        var portableReady = package.Mods.Count(mod => !string.IsNullOrWhiteSpace(mod.SourceArchiveFileName));
        ModpackExportProfileSummaryText.Text = $"{package.Mods.Count} mods, {enabled} enabled, {linked} Nexus linked, {portableReady} portable-ready.";
    }

    private void ExportUcuModpack_Click(object sender, RoutedEventArgs e)
    {
        var profile = CreateProfileSnapshotForExport(GetSelectedModpackExportProfileId());
        if (profile is null)
        {
            MessageBox.Show(this, "Choose a profile to export first.", "Export .UCU", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var package = BuildUcuModpackPackage(profile, portable: false);
        if (package.Mods.Count == 0)
        {
            MessageBox.Show(this, "The active profile does not contain mods that can be exported.", "Export .UCU", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var unavailable = package.Mods.Count(mod => GetUcuModId(mod) is null || string.IsNullOrWhiteSpace(GetUcuGameDomain(mod)));
        if (unavailable > 0)
        {
            var answer = MessageBox.Show(
                this,
                $"{unavailable} mod(s) in this profile are not linked to Nexus and cannot be downloaded automatically on another PC.\n\nExport the .UCU file anyway?",
                "Export .UCU",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes)
            {
                return;
            }
        }

        var dialog = new SaveFileDialog
        {
            Title = "Export UCU Modpack",
            Filter = "UCU Modpack (*.ucu)|*.ucu",
            AddExtension = true,
            DefaultExt = ".ucu",
            FileName = $"{SanitizeFileNameSegment(profile.Name) ?? "UCU-Modpack"}.ucu"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var progress = ShowProgress("Export .UCU", "Exporting modpack recipe", "Writing .UCU file...");
            _ucuModpackService.Save(dialog.FileName, package);
            CloseProgress(progress);
            ModpacksStatusText.Text = $"Exported .UCU modpack: {Path.GetFileName(dialog.FileName)}";
            MessageBox.Show(this, $"Exported {package.Mods.Count} mod(s).", "Export .UCU", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException)
        {
            CloseProgress(OwnedWindows.OfType<VirtualLaunchProgressDialog>().FirstOrDefault(window => window.Title == "Export .UCU"));
            MessageBox.Show(this, exception.Message, "Export .UCU failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ExportUcupModpack_Click(object sender, RoutedEventArgs e)
    {
        var profile = CreateProfileSnapshotForExport(GetSelectedModpackExportProfileId());
        if (profile is null)
        {
            MessageBox.Show(this, "Choose a profile to export first.", "Export .UCUP", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var package = BuildUcuModpackPackage(profile, portable: true);
        if (package.Mods.Count == 0)
        {
            MessageBox.Show(this, "The selected profile does not contain mods that can be exported.", "Export .UCUP", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Export Portable UCU Modpack",
            Filter = "Portable UCU Modpack (*.ucup)|*.ucup",
            AddExtension = true,
            DefaultExt = ".ucup",
            FileName = $"{SanitizeFileNameSegment(profile.Name) ?? "UCU-Modpack"}.ucup"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var progress = ShowProgress("Export .UCUP", "Packing portable modpack", "Writing profile and mod archives...");
            SavePortableUcuModpack(dialog.FileName, package, profile);
            CloseProgress(progress);
            ModpacksStatusText.Text = $"Exported .UCUP modpack: {Path.GetFileName(dialog.FileName)}";
            MessageBox.Show(this, $"Exported portable modpack with {package.Mods.Count} mod(s).", "Export .UCUP", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException)
        {
            CloseProgress(OwnedWindows.OfType<VirtualLaunchProgressDialog>().FirstOrDefault(window => window.Title == "Export .UCUP"));
            MessageBox.Show(this, exception.Message, "Export .UCUP failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private string? GetSelectedModpackExportProfileId()
    {
        return (ModpackExportProfileComboBox.SelectedItem as ProfileRow)?.Id
            ?? _currentProfile?.Id;
    }

    private void ModpackExportProfileComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingProfiles)
        {
            return;
        }

        RefreshModpackExportProfilePreview();
    }

    private ModProfile? CreateProfileSnapshotForExport(string? profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId))
        {
            return null;
        }

        _libraryEntries = _libraryService.LoadLibrary(_managerPaths);
        if (_currentProfile is not null
            && profileId.Equals(_currentProfile.Id, StringComparison.OrdinalIgnoreCase)
            && _mods.Count > 0)
        {
            SaveProfileFromRows();
            return CreateProfileFromRows();
        }

        return _profileService.LoadOrCreateProfile(_managerPaths, profileId, _libraryEntries);
    }

    private UcuModpackPackage BuildUcuModpackPackage(ModProfile profile, bool portable)
    {
        var libraryById = _libraryEntries.ToDictionary(entry => entry.Mod.Id, StringComparer.OrdinalIgnoreCase);
        var profileEntries = profile.Mods.Count > 0
            ? profile.Mods
            : _libraryEntries
                .Select((entry, index) => new ProfileModEntry(entry.Mod.Id, true, index))
                .ToArray();
        var mods = profileEntries
            .OrderBy(entry => entry.Priority)
            .Where(entry => libraryById.ContainsKey(entry.ModId))
            .Select(entry =>
            {
                var libraryEntry = libraryById[entry.ModId];
                var source = libraryEntry.Manifest.Source;
                return new UcuModpackMod(
                    libraryEntry.Mod.Name,
                    entry.IsEnabled,
                    entry.Priority,
                    source?.GameDomain,
                    source?.ModId,
                    source?.FileId ?? source?.LastLatestFileId,
                    source?.FileVersion ?? source?.LastLatestVersion ?? libraryEntry.Mod.Version,
                    source?.PageUrl,
                    source?.SourceArchiveFileName ?? libraryEntry.Manifest.SourceArchiveFileName,
                    BuildUcuDownloadUrl(
                        source?.GameDomain,
                        source?.ModId,
                        source?.FileId ?? source?.LastLatestFileId),
                    portable ? BuildPortableModArchiveFileName(libraryEntry, entry.Priority) : null);
            })
            .ToArray();

        return new UcuModpackPackage(
            UcuModpackPackage.CurrentFormatVersion,
            UcuModpackPackage.DefaultCreatedBy,
            DateTimeOffset.UtcNow,
            profile.Name,
            mods,
            portable ? UcuModpackPackage.PackageKindPortable : UcuModpackPackage.PackageKindRecipe);
    }

    private void SavePortableUcuModpack(string filePath, UcuModpackPackage package, ModProfile profile)
    {
        var libraryById = _libraryEntries.ToDictionary(entry => entry.Mod.Id, StringComparer.OrdinalIgnoreCase);
        var packageModsByPriority = package.Mods.ToDictionary(mod => mod.Priority);
        var fullFilePath = Path.GetFullPath(filePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullFilePath)!);
        if (File.Exists(fullFilePath))
        {
            File.Delete(fullFilePath);
        }

        using var stream = File.Create(fullFilePath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        var manifestEntry = archive.CreateEntry("modpack.json", CompressionLevel.Optimal);
        using (var manifestStream = manifestEntry.Open())
        {
            JsonSerializer.Serialize(manifestStream, package, new JsonSerializerOptions { WriteIndented = true });
        }

        foreach (var profileEntry in profile.Mods.OrderBy(entry => entry.Priority))
        {
            if (!libraryById.TryGetValue(profileEntry.ModId, out var libraryEntry)
                || !packageModsByPriority.TryGetValue(profileEntry.Priority, out var packageMod)
                || string.IsNullOrWhiteSpace(packageMod.EmbeddedArchiveFileName))
            {
                continue;
            }

            WritePortableModArchive(archive, packageMod.EmbeddedArchiveFileName!, libraryEntry);
        }
    }

    private static void WritePortableModArchive(
        ZipArchive packageArchive,
        string archiveFileName,
        ModLibraryEntry libraryEntry)
    {
        var nestedEntry = packageArchive.CreateEntry($"mods/{archiveFileName}", CompressionLevel.Optimal);
        using var nestedStream = nestedEntry.Open();
        using var modArchive = new ZipArchive(nestedStream, ZipArchiveMode.Create);
        var filesRoot = Path.Combine(libraryEntry.ModDirectoryPath, "files");
        foreach (var file in libraryEntry.Mod.Files)
        {
            var relativePath = file.NormalizedTargetRelativePath;
            var sourcePath = Path.GetFullPath(Path.Combine(filesRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            var filesRootFull = EnsureTrailingSeparator(Path.GetFullPath(filesRoot));
            if (!sourcePath.StartsWith(filesRootFull, StringComparison.OrdinalIgnoreCase) || !File.Exists(sourcePath))
            {
                continue;
            }

            modArchive.CreateEntryFromFile(sourcePath, relativePath, CompressionLevel.Optimal);
        }
    }

    private static string BuildPortableModArchiveFileName(ModLibraryEntry libraryEntry, int priority)
    {
        var name = SanitizeFileNameSegment(libraryEntry.Manifest.Source?.SourceArchiveFileName);
        if (string.IsNullOrWhiteSpace(name))
        {
            name = SanitizeFileNameSegment(libraryEntry.Mod.Name) ?? $"mod-{priority + 1}";
        }

        if (!Path.GetExtension(name).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            name = $"{Path.GetFileNameWithoutExtension(name)}.zip";
        }

        return $"{priority + 1:000}-{name}";
    }

    private void ImportUcuModpack_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import UCU Modpack",
            Filter = "UCU Modpacks (*.ucu;*.ucup)|*.ucu;*.ucup|Recipe Modpack (*.ucu)|*.ucu|Portable Modpack (*.ucup)|*.ucup|All files (*.*)|*.*",
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var progress = ShowProgress("Import Modpack", "Loading modpack", "Reading modpack file...");
            var isPortable = Path.GetExtension(dialog.FileName).Equals(".ucup", StringComparison.OrdinalIgnoreCase);
            var package = isPortable
                ? LoadPortableUcuModpackManifest(dialog.FileName)
                : _ucuModpackService.Load(dialog.FileName);
            UpdateProgress(progress, "Preparing modpack preview...");
            _importedUcuModpack = package;
            _importedUcuModpackPath = dialog.FileName;
            _importedUcuModpackIsPortable = isPortable;
            _manualDownloadModpackKeys.Clear();
            ShowImportedUcuModpack(package, dialog.FileName);
            CloseProgress(progress);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            CloseProgress(OwnedWindows.OfType<VirtualLaunchProgressDialog>().FirstOrDefault(window => window.Title == "Import Modpack"));
            MessageBox.Show(this, exception.Message, "Import .UCU failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static UcuModpackPackage LoadPortableUcuModpackManifest(string filePath)
    {
        using var archive = ZipFile.OpenRead(filePath);
        var entry = archive.GetEntry("modpack.json")
            ?? throw new InvalidOperationException("The .UCUP file does not contain modpack.json.");
        using var stream = entry.Open();
        var package = JsonSerializer.Deserialize<UcuModpackPackage>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("The .UCUP manifest is empty or invalid.");
        if (package.Mods.Count == 0)
        {
            throw new InvalidOperationException("The .UCUP file does not contain any mods.");
        }

        return package with
        {
            Mods = package.Mods.OrderBy(mod => mod.Priority).ToArray(),
            PackageKind = UcuModpackPackage.PackageKindPortable
        };
    }

    private void OpenImportedUcuPages_Click(object sender, RoutedEventArgs e)
    {
        if (_importedUcuModpack is null)
        {
            MessageBox.Show(this, "Import a modpack first.", "Open Nexus Pages", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var pages = _importedUcuModpack.Mods
            .Select(mod => new
            {
                GameDomain = GetUcuGameDomain(mod),
                ModId = GetUcuModId(mod)
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.GameDomain) && item.ModId is not null)
            .GroupBy(item => $"{item.GameDomain}:{item.ModId}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        if (pages.Length == 0)
        {
            MessageBox.Show(this, "This modpack does not contain Nexus links.", "Open Nexus Pages", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var answer = MessageBox.Show(
            this,
            $"This will open {pages.Length} Nexus tab(s) in your browser.\n\nOpening many tabs can cause a short lag. Continue?",
            "Open Nexus Pages",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        foreach (var page in pages)
        {
            OpenNexusFilesPage(page.GameDomain!, page.ModId!.Value);
        }
    }

    private void ShowImportedUcuModpack(UcuModpackPackage package, string? filePath)
    {
        _ucuModpackRows.Clear();
        foreach (var row in package.Mods
            .OrderBy(mod => mod.Priority)
            .Select(mod => UcuModpackModRow.FromMod(
                mod,
                FindInstalledLibraryEntryForUcuMod(mod) is not null,
                _manualDownloadModpackKeys.Contains(BuildUcuModpackKey(mod)),
                _settings.ShowAdvancedModColumns)))
        {
            _ucuModpackRows.Add(row);
        }

        var linked = package.Mods.Count(mod => GetUcuModId(mod) is not null && !string.IsNullOrWhiteSpace(GetUcuGameDomain(mod)));
        var enabled = package.Mods.Count(mod => mod.IsEnabled);
        var kind = _importedUcuModpackIsPortable ? ".UCUP portable" : ".UCU recipe";
        ModpacksStatusText.Text = $"Imported {kind}: {package.Mods.Count} mods. Linked to Nexus: {linked}.";
        ImportedUcuNameText.Text = package.ProfileName;
        ImportedUcuPathText.Text = string.IsNullOrWhiteSpace(filePath) ? string.Empty : filePath;
        ImportedUcuSummaryText.Text = $"Enabled: {enabled}\nDisabled: {package.Mods.Count - enabled}\nCreated: {package.CreatedAt.LocalDateTime:g}\nType: {kind}";
        InstallImportedUcuModpackButton.IsEnabled = package.Mods.Count > 0;
        OpenImportedUcuPagesButton.IsEnabled = linked > 0;
    }

    private async void InstallImportedUcuModpack_Click(object sender, RoutedEventArgs e)
    {
        if (_importedUcuModpack is null)
        {
            MessageBox.Show(this, "Import a .UCU file first.", "Install .UCU", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var apiKey = _importedUcuModpackIsPortable ? null : GetConfiguredNexusApiKey();
        if (!_importedUcuModpackIsPortable && string.IsNullOrWhiteSpace(apiKey))
        {
            _manualDownloadModpackKeys.Clear();
            foreach (var mod in _importedUcuModpack.Mods)
            {
                if (GetUcuModId(mod) is not null && !string.IsNullOrWhiteSpace(GetUcuGameDomain(mod)))
                {
                    _manualDownloadModpackKeys.Add(BuildUcuModpackKey(mod));
                }
            }

            ShowImportedUcuModpack(_importedUcuModpack, _importedUcuModpackPath);
            MessageBox.Show(
                this,
                "Nexus API key is required to download a .UCU recipe automatically. Use Open All Nexus Pages for manual download, or import a .UCUP portable modpack.",
                "Nexus API key required",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var answer = MessageBox.Show(
            this,
            $"Download and install '{_importedUcuModpack.ProfileName}' as a new profile?\n\nMods: {_importedUcuModpack.Mods.Count}",
            "Install .UCU Modpack",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        InstallImportedUcuModpackButton.IsEnabled = false;
        var progress = ShowProgress(
            _importedUcuModpackIsPortable ? "Install .UCUP" : "Install .UCU",
            _importedUcuModpackIsPortable ? "Installing portable modpack" : "Installing modpack recipe",
            "Preparing modpack...");
        try
        {
            if (_importedUcuModpackIsPortable)
            {
                InstallPortableUcuModpack(_importedUcuModpack, _importedUcuModpackPath, progress);
            }
            else
            {
                await InstallUcuModpackAsync(_importedUcuModpack, apiKey!, progress);
            }
            CloseProgress(progress);
        }
        catch (Exception exception) when (exception is HttpRequestException
            or TaskCanceledException
            or JsonException
            or IOException
            or InvalidOperationException
            or UnauthorizedAccessException)
        {
            CloseProgress(progress);
            MessageBox.Show(this, exception.Message, "Install .UCU failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            InstallImportedUcuModpackButton.IsEnabled = _importedUcuModpack is not null;
        }
    }

    private async Task InstallUcuModpackAsync(
        UcuModpackPackage package,
        string apiKey,
        VirtualLaunchProgressDialog? progress)
    {
        _manualDownloadModpackKeys.Clear();
        var modIdsByPriority = new Dictionary<int, string>();
        var outcomes = new List<ModInstallOutcome>();
        var importPlans = new List<UcuModpackInstallPlan>();
        var downloadRootPath = Path.Combine(
            _managerPaths.DownloadsPath,
            "ucu-modpacks",
            $"{SanitizeFileNameSegment(package.ProfileName) ?? "modpack"}-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}");

        foreach (var mod in package.Mods.OrderBy(mod => mod.Priority))
        {
            var existing = FindInstalledLibraryEntryForUcuMod(mod, requireSameFile: true);
            if (existing is not null)
            {
                modIdsByPriority[mod.Priority] = existing.Mod.Id;
                continue;
            }

            try
            {
                ModpacksStatusText.Text = $"Downloading {mod.Name}...";
                UpdateProgress(progress, $"Downloading {mod.Name}...");
                var target = await ResolveUcuDownloadTargetAsync(mod, apiKey);
                if (target is null)
                {
                    _manualDownloadModpackKeys.Add(BuildUcuModpackKey(mod));
                    outcomes.Add(ModInstallOutcome.Failure(mod.Name, "No Nexus file id is available for automatic download."));
                    continue;
                }

                var source = CreateUcuDownloadSource(mod, target);
                var download = await _nexusModDownloadService.DownloadUpdateArchiveAsync(
                    source,
                    target.FileId,
                    apiKey,
                    downloadRootPath,
                    BuildUcuArchiveName(mod, target));
                var preview = _importService.PreviewZip(download.ArchivePath, _managerPaths);
                importPlans.Add(new UcuModpackInstallPlan(mod, download.ArchivePath, preview));
            }
            catch (Exception exception) when (exception is HttpRequestException
                or TaskCanceledException
                or JsonException
                or IOException
                or InvalidDataException
                or InvalidOperationException
                or UnauthorizedAccessException)
            {
                _manualDownloadModpackKeys.Add(BuildUcuModpackKey(mod));
                outcomes.Add(ModInstallOutcome.Failure(mod.Name, exception.Message));
            }
        }

        if (importPlans.Count > 0)
        {
            var plannedImports = importPlans
                .Select(plan => new ModInstallPlan(plan.ArchivePath, plan.Preview))
                .ToArray();
            var deployedUpdateModIds = plannedImports
                .Where(plan => plan.Preview.Action == ModImportAction.Updated)
                .Where(plan => CountDeployedFilesForMod(plan.Preview.ModId) > 0)
                .Select(plan => plan.Preview.ModId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (deployedUpdateModIds.Count > 0)
            {
                var cleanAnswer = MessageBox.Show(
                    this,
                    BuildDeployedUpdatePrompt(plannedImports, deployedUpdateModIds),
                    "Update deployed mods",
                    MessageBoxButton.YesNoCancel,
                    MessageBoxImage.Warning);
                if (cleanAnswer == MessageBoxResult.Cancel)
                {
                    return;
                }

                if (cleanAnswer == MessageBoxResult.Yes)
                {
                    var cleanResults = CleanDeploymentsForMods(deployedUpdateModIds);
                    if (cleanResults.Any(result => result.PreservedFiles > 0))
                    {
                        RefreshDeployStatus();
                        MessageBox.Show(this, BuildBlockedUpdateCleanupMessage(cleanResults), "Update stopped", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                }
            }
        }

        foreach (var plan in importPlans)
        {
            try
            {
                ModpacksStatusText.Text = $"Installing {plan.Mod.Name}...";
                UpdateProgress(progress, $"Installing {plan.Mod.Name}...");
                var result = _importService.ImportZip(plan.ArchivePath, _managerPaths);
                modIdsByPriority[plan.Mod.Priority] = result.Manifest.Mod.Id;
                outcomes.Add(ModInstallOutcome.Success(Path.GetFileName(plan.ArchivePath), result));
            }
            catch (Exception exception) when (exception is IOException
                or InvalidDataException
                or InvalidOperationException
                or UnauthorizedAccessException)
            {
                outcomes.Add(ModInstallOutcome.Failure(Path.GetFileName(plan.ArchivePath), exception.Message));
            }
        }

        _libraryEntries = _libraryService.LoadLibrary(_managerPaths);
        foreach (var mod in package.Mods.OrderBy(mod => mod.Priority))
        {
            if (modIdsByPriority.ContainsKey(mod.Priority))
            {
                continue;
            }

            var existing = FindInstalledLibraryEntryForUcuMod(mod, requireSameFile: false);
            if (existing is not null)
            {
                modIdsByPriority[mod.Priority] = existing.Mod.Id;
                _manualDownloadModpackKeys.Remove(BuildUcuModpackKey(mod));
            }
        }

        if (modIdsByPriority.Count == 0)
        {
            ShowImportedUcuModpack(package, _importedUcuModpackPath);
            ShowModInstallResults(outcomes);
            MessageBox.Show(
                this,
                "No mods were installed, so an empty profile was not created.\n\nUse Open All Nexus Pages to download the required mods manually, then install the downloaded archives with Install Mods.",
                "Install .UCU incomplete",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        UpdateProgress(progress, "Creating profile...");
        var profile = CreateProfileFromUcuModpack(package, modIdsByPriority);
        _settings = _settings with { ActiveProfileId = profile.Id };
        _settingsService.Save(_managerPaths, _settings);
        LoadMods();
        ShowImportedUcuModpack(package, _importedUcuModpackPath);
        ModpacksStatusText.Text = _manualDownloadModpackKeys.Count == 0
            ? $"Installed .UCU profile '{profile.Name}'."
            : $"Installed partial .UCU profile '{profile.Name}'. Manual downloads needed: {_manualDownloadModpackKeys.Count}.";
        ShowModInstallResults(outcomes);
        MessageBox.Show(this, $"Created profile '{profile.Name}'.", "Install .UCU complete", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void InstallPortableUcuModpack(
        UcuModpackPackage package,
        string? packagePath,
        VirtualLaunchProgressDialog? progress)
    {
        if (string.IsNullOrWhiteSpace(packagePath) || !File.Exists(packagePath))
        {
            MessageBox.Show(this, "The imported .UCUP file is not available.", "Install .UCUP", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var modIdsByPriority = new Dictionary<int, string>();
        var outcomes = new List<ModInstallOutcome>();
        var importPlans = new List<UcuModpackInstallPlan>();
        var extractRoot = Path.Combine(
            _managerPaths.CachePath,
            "ucup-import",
            $"{SanitizeFileNameSegment(package.ProfileName) ?? "modpack"}-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(extractRoot);
            using var archive = ZipFile.OpenRead(packagePath);
            foreach (var mod in package.Mods.OrderBy(mod => mod.Priority))
            {
                UpdateProgress(progress, $"Reading {mod.Name}...");
                var existing = FindInstalledLibraryEntryForUcuMod(mod, requireSameFile: false)
                    ?? FindInstalledLibraryEntryByNameAndVersion(mod);
                if (existing is not null)
                {
                    modIdsByPriority[mod.Priority] = existing.Mod.Id;
                    continue;
                }

                try
                {
                    if (string.IsNullOrWhiteSpace(mod.EmbeddedArchiveFileName))
                    {
                        outcomes.Add(ModInstallOutcome.Failure(mod.Name, "The portable modpack does not contain an embedded archive for this mod."));
                        continue;
                    }

                    var entry = archive.GetEntry($"mods/{mod.EmbeddedArchiveFileName}")
                        ?? throw new InvalidOperationException($"Embedded archive was not found: {mod.EmbeddedArchiveFileName}");
                    var archiveDirectoryPath = Path.Combine(extractRoot, $"{mod.Priority + 1:000}");
                    Directory.CreateDirectory(archiveDirectoryPath);
                    var archivePath = Path.Combine(archiveDirectoryPath, BuildPortableImportArchiveFileName(mod));
                    entry.ExtractToFile(archivePath, overwrite: true);
                    var preview = _importService.PreviewZip(archivePath, _managerPaths);
                    importPlans.Add(new UcuModpackInstallPlan(mod, archivePath, preview));
                }
                catch (Exception exception) when (exception is IOException
                    or InvalidDataException
                    or InvalidOperationException
                    or UnauthorizedAccessException)
                {
                    outcomes.Add(ModInstallOutcome.Failure(mod.Name, exception.Message));
                }
            }

            RunUcuImportPlans(importPlans, outcomes, modIdsByPriority, progress);
            if (modIdsByPriority.Count == 0)
            {
                ShowModInstallResults(outcomes);
                MessageBox.Show(this, "No mods were installed, so an empty profile was not created.", "Install .UCUP incomplete", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _libraryEntries = _libraryService.LoadLibrary(_managerPaths);
            UpdateProgress(progress, "Creating profile...");
            var profile = CreateProfileFromUcuModpack(package, modIdsByPriority);
            _settings = _settings with { ActiveProfileId = profile.Id };
            _settingsService.Save(_managerPaths, _settings);
            LoadMods();
            ModpacksStatusText.Text = $"Installed .UCUP profile '{profile.Name}'.";
            ShowModInstallResults(outcomes);
            MessageBox.Show(this, $"Created profile '{profile.Name}'.", "Install .UCUP complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        finally
        {
            try
            {
                if (Directory.Exists(extractRoot))
                {
                    Directory.Delete(extractRoot, recursive: true);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private void RunUcuImportPlans(
        IReadOnlyList<UcuModpackInstallPlan> importPlans,
        List<ModInstallOutcome> outcomes,
        Dictionary<int, string> modIdsByPriority,
        VirtualLaunchProgressDialog? progress = null)
    {
        if (importPlans.Count == 0)
        {
            return;
        }

        var plannedImports = importPlans
            .Select(plan => new ModInstallPlan(plan.ArchivePath, plan.Preview))
            .ToArray();
        var deployedUpdateModIds = plannedImports
            .Where(plan => plan.Preview.Action == ModImportAction.Updated)
            .Where(plan => CountDeployedFilesForMod(plan.Preview.ModId) > 0)
            .Select(plan => plan.Preview.ModId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (deployedUpdateModIds.Count > 0)
        {
            var cleanAnswer = MessageBox.Show(
                this,
                BuildDeployedUpdatePrompt(plannedImports, deployedUpdateModIds),
                "Update deployed mods",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Warning);
            if (cleanAnswer == MessageBoxResult.Cancel)
            {
                return;
            }

            if (cleanAnswer == MessageBoxResult.Yes)
            {
                var cleanResults = CleanDeploymentsForMods(deployedUpdateModIds);
                if (cleanResults.Any(result => result.PreservedFiles > 0))
                {
                    RefreshDeployStatus();
                    MessageBox.Show(this, BuildBlockedUpdateCleanupMessage(cleanResults), "Update stopped", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }
        }

        foreach (var plan in importPlans)
        {
            try
            {
                ModpacksStatusText.Text = $"Installing {plan.Mod.Name}...";
                UpdateProgress(progress, $"Installing {plan.Mod.Name}...");
                var result = _importService.ImportZip(plan.ArchivePath, _managerPaths);
                modIdsByPriority[plan.Mod.Priority] = result.Manifest.Mod.Id;
                outcomes.Add(ModInstallOutcome.Success(Path.GetFileName(plan.ArchivePath), result));
            }
            catch (Exception exception) when (exception is IOException
                or InvalidDataException
                or InvalidOperationException
                or UnauthorizedAccessException)
            {
                outcomes.Add(ModInstallOutcome.Failure(Path.GetFileName(plan.ArchivePath), exception.Message));
            }
        }
    }

    private ModProfile CreateProfileFromUcuModpack(
        UcuModpackPackage package,
        IReadOnlyDictionary<int, string> modIdsByPriority)
    {
        var created = _profileService.CreateProfile(_managerPaths, package.ProfileName, _libraryEntries);
        var usedModIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var profileEntries = new List<ProfileModEntry>();

        foreach (var mod in package.Mods.OrderBy(mod => mod.Priority))
        {
            if (!modIdsByPriority.TryGetValue(mod.Priority, out var modId)
                || !usedModIds.Add(modId))
            {
                continue;
            }

            profileEntries.Add(new ProfileModEntry(modId, mod.IsEnabled, profileEntries.Count));
        }

        foreach (var libraryEntry in _libraryEntries
            .Where(entry => !usedModIds.Contains(entry.Mod.Id))
            .OrderBy(entry => entry.Mod.Name, StringComparer.OrdinalIgnoreCase))
        {
            profileEntries.Add(new ProfileModEntry(libraryEntry.Mod.Id, false, profileEntries.Count));
        }

        var profile = created with { Mods = profileEntries };
        _profileService.SaveProfile(_managerPaths, profile);
        return profile;
    }

    private ModLibraryEntry? FindInstalledLibraryEntryForUcuMod(
        UcuModpackMod mod,
        bool requireSameFile = false)
    {
        var gameDomain = GetUcuGameDomain(mod);
        var modId = GetUcuModId(mod);
        if (modId is null || string.IsNullOrWhiteSpace(gameDomain))
        {
            return null;
        }

        var matches = _libraryEntries
            .Where(entry => entry.Manifest.Source?.ModId == modId)
            .Where(entry => string.Equals(entry.Manifest.Source?.GameDomain, gameDomain, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var fileId = GetUcuFileId(mod);
        if (!requireSameFile || fileId is null)
        {
            return matches.FirstOrDefault();
        }

        return matches.FirstOrDefault(entry => entry.Manifest.Source?.FileId == fileId);
    }

    private ModLibraryEntry? FindInstalledLibraryEntryByNameAndVersion(UcuModpackMod mod)
    {
        if (GetUcuModId(mod) is not null || !string.IsNullOrWhiteSpace(GetUcuGameDomain(mod)))
        {
            return null;
        }

        var stableId = ModPackage.CreateStableId(mod.Name);
        var expectedVersion = GetKnownModVersion(mod.Version);
        return _libraryEntries.FirstOrDefault(entry =>
        {
            var nameMatches = entry.Mod.Id.Equals(stableId, StringComparison.OrdinalIgnoreCase)
                || entry.Mod.Name.Equals(mod.Name, StringComparison.OrdinalIgnoreCase);
            if (!nameMatches)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(expectedVersion))
            {
                return true;
            }

            return VersionsEqual(entry.Mod.Version, expectedVersion)
                || VersionsEqual(entry.Manifest.Source?.FileVersion ?? string.Empty, expectedVersion);
        });
    }

    private async Task<NexusDownloadTarget?> ResolveUcuDownloadTargetAsync(
        UcuModpackMod mod,
        string apiKey)
    {
        var gameDomainValue = GetUcuGameDomain(mod);
        var modId = GetUcuModId(mod);
        if (modId is null || string.IsNullOrWhiteSpace(gameDomainValue))
        {
            return null;
        }

        var gameDomain = NormalizeNexusGameDomain(gameDomainValue);
        var fileId = GetUcuFileId(mod);
        if (fileId is not null)
        {
            return new NexusDownloadTarget(gameDomain, modId.Value, fileId.Value, mod.Version, mod.SourceArchiveFileName);
        }

        var fingerprint = NexusModFilesService.BuildCacheFingerprint(
            gameDomain,
            modId.Value,
            null,
            mod.Version,
            mod.SourceArchiveFileName);
        var filesResult = await _nexusModFilesService.LoadOrRefreshAsync(
            _managerPaths,
            gameDomain,
            modId.Value,
            fingerprint,
            apiKey);
        var file = ChooseBestNexusFile(filesResult.Files, mod.Version);
        return file is null
            ? null
            : new NexusDownloadTarget(gameDomain, modId.Value, file.FileId, file.Version, file.FileName);
    }

    private static string? BuildUcuDownloadUrl(string? gameDomain, int? modId, int? fileId)
    {
        if (string.IsNullOrWhiteSpace(gameDomain) || modId is null)
        {
            return null;
        }

        return fileId is null
            ? $"nexus://{gameDomain.Trim()}/{modId.Value}"
            : $"nexus://{gameDomain.Trim()}/{modId.Value}/{fileId.Value}";
    }

    private static string? GetUcuGameDomain(UcuModpackMod mod)
    {
        return FirstNonEmpty(
            mod.GameDomain,
            NexusMetadataDownloadReference.TryParse(mod.DownloadUrl)?.GameDomain,
            ParseNexusPageReference(mod.PageUrl)?.GameDomain);
    }

    private static string BuildUcuModpackKey(UcuModpackMod mod)
    {
        var gameDomain = GetUcuGameDomain(mod) ?? "unknown";
        var modId = GetUcuModId(mod)?.ToString() ?? mod.Name;
        var fileId = GetUcuFileId(mod)?.ToString() ?? "latest";
        return $"{gameDomain}:{modId}:{fileId}";
    }

    private static int? GetUcuModId(UcuModpackMod mod)
    {
        return mod.NexusModId
            ?? NexusMetadataDownloadReference.TryParse(mod.DownloadUrl)?.ModId
            ?? ParseNexusPageReference(mod.PageUrl)?.ModId;
    }

    private static int? GetUcuFileId(UcuModpackMod mod)
    {
        return mod.FileId ?? NexusMetadataDownloadReference.TryParse(mod.DownloadUrl)?.FileId;
    }

    private static NexusMetadataDownloadReference? ParseNexusPageReference(string? pageUrl)
    {
        if (string.IsNullOrWhiteSpace(pageUrl)
            || !Uri.TryCreate(pageUrl, UriKind.Absolute, out var uri)
            || !uri.Host.Contains("nexusmods.com", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length < 3
            || !segments[1].Equals("mods", StringComparison.OrdinalIgnoreCase)
            || !int.TryParse(segments[2], out var modId))
        {
            return null;
        }

        return new NexusMetadataDownloadReference(segments[0], modId, null);
    }

    private static ModSourceInfo CreateUcuDownloadSource(UcuModpackMod mod, NexusDownloadTarget target)
    {
        return new ModSourceInfo(
            "NexusMods",
            target.GameDomain,
            target.ModId,
            target.FileId,
            GetKnownModVersion(target.Version) ?? GetKnownModVersion(mod.Version),
            null,
            target.FileName ?? mod.SourceArchiveFileName ?? mod.Name,
            null,
            GetKnownModVersion(mod.Version) ?? mod.Version,
            DateTimeOffset.UtcNow,
            mod.Name,
            null,
            mod.PageUrl,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            target.FileId);
    }

    private static string BuildUcuArchiveName(UcuModpackMod mod, NexusDownloadTarget target)
    {
        var name = SanitizeFileNameSegment(mod.Name) ?? "ucu-mod";
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return $"{name}-{target.ModId}-{target.FileId}-{timestamp}.zip";
    }

    private static string BuildPortableImportArchiveFileName(UcuModpackMod mod)
    {
        var sourceName = SanitizeFileNameSegment(mod.SourceArchiveFileName);
        if (string.IsNullOrWhiteSpace(sourceName)
            || LooksLikePortableArchiveName(sourceName))
        {
            sourceName = SanitizeFileNameSegment(mod.Name) ?? $"mod-{mod.Priority + 1}";
        }

        return Path.GetExtension(sourceName).Equals(".zip", StringComparison.OrdinalIgnoreCase)
            ? sourceName
            : $"{Path.GetFileNameWithoutExtension(sourceName)}.zip";
    }

    private static bool LooksLikePortableArchiveName(string fileName)
    {
        return Regex.IsMatch(Path.GetFileNameWithoutExtension(fileName), @"^\d{3}-", RegexOptions.CultureInvariant);
    }

    private void SaveNexusApiKey_Click(object sender, RoutedEventArgs e)
    {
        var apiKey = NexusApiKeyPasswordBox.Password.Trim();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            MessageBox.Show(this, "Enter a Nexus Mods API key first.", "Nexus API key required", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            SaveNexusGameDomain();
            _secureSecretStore.SaveNexusApiKey(_managerPaths, apiKey);
            NexusApiKeyPasswordBox.Password = string.Empty;
            RefreshSettingsStatus();
            MessageBox.Show(this, "Nexus API key saved for the current Windows user.", "Nexus API key saved", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            MessageBox.Show(this, exception.Message, "Save failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void TestNexusApiKey_Click(object sender, RoutedEventArgs e)
    {
        var apiKey = NexusApiKeyPasswordBox.Password.Trim();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            apiKey = GetConfiguredNexusApiKey() ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            MessageBox.Show(this, "Enter or save a Nexus Mods API key first.", "Nexus API key required", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            SaveNexusGameDomain();
            NexusApiKeyStatusText.Text = "Testing Nexus API key...";
            var userName = await TestNexusApiKey(apiKey);
            NexusApiKeyStatusText.Text = string.IsNullOrWhiteSpace(userName)
                ? "Nexus API key is valid."
                : $"Nexus API key is valid for {userName}.";
            NexusApiKeyStatusText.Foreground = (Brush)FindResource("AccentBrush");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException)
        {
            NexusApiKeyStatusText.Text = "Nexus API key test failed.";
            NexusApiKeyStatusText.Foreground = (Brush)FindResource("DangerBrush");
            MessageBox.Show(this, exception.Message, "Nexus API key test failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ClearNexusApiKey_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _secureSecretStore.ClearNexusApiKey(_managerPaths);
            NexusApiKeyPasswordBox.Password = string.Empty;
            RefreshSettingsStatus();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, exception.Message, "Clear failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void SaveNexusGameDomain()
    {
        var domain = NexusGameDomainTextBox.Text.Trim();
        if (ShouldResetNexusGameDomain(domain))
        {
            domain = ManagerSettings.Empty.NexusGameDomain;
        }

        _settings = _settings with { NexusGameDomain = domain };
        _settingsService.Save(_managerPaths, _settings);
    }

    private void ProfilesListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingProfiles || ProfilesListBox.SelectedItem is not ProfileRow selectedProfile)
        {
            return;
        }

        if (_currentProfile?.Id.Equals(selectedProfile.Id, StringComparison.OrdinalIgnoreCase) == true)
        {
            return;
        }

        SaveProfileFromRows();
        ActivateProfile(selectedProfile.Id);
    }

    private void CreateProfile_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SaveProfileFromRows();
            var profile = _profileService.CreateProfile(
                _managerPaths,
                CreateSuggestedProfileName("New Profile"),
                _libraryEntries);
            ActivateProfile(profile.Id);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, exception.Message, "Create profile failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void DuplicateProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProfile is null)
        {
            return;
        }

        try
        {
            SaveProfileFromRows();
            var sourceProfile = _currentProfile;
            var profile = _profileService.DuplicateProfile(
                _managerPaths,
                sourceProfile.Id,
                CreateSuggestedProfileName($"{sourceProfile.Name} Copy"),
                _libraryEntries);
            ActivateProfile(profile.Id);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, exception.Message, "Duplicate profile failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void RenameProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProfile is null)
        {
            return;
        }

        var profileName = ProfileNameTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(profileName))
        {
            MessageBox.Show(this, "Enter a profile name first.", "Profile name required", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            SaveProfileFromRows();
            var profile = _profileService.RenameProfile(_managerPaths, _currentProfile.Id, profileName, _libraryEntries);
            ActivateProfile(profile.Id);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, exception.Message, "Rename profile failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProfile is null)
        {
            return;
        }

        if (_currentProfile.Id.Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "The default profile cannot be deleted.", "Delete profile", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var profile = _currentProfile;
        var answer = MessageBox.Show(
            this,
            $"Delete profile '{profile.Name}'? Installed mods will stay in the manager library.",
            "Delete profile",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        var manifest = _profileDeployService.LoadManifest(_managerPaths, profile.Id);
        if (manifest is not null)
        {
            answer = MessageBox.Show(
                this,
                $"This profile has {manifest.Files.Count} managed files deployed to the game folder. Clean them before deleting the profile?",
                "Clean deployed files",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes)
            {
                return;
            }

            try
            {
                var cleanResult = _profileDeployService.Clean(_managerPaths, profile.Id);
                ShowDeployResult("Deployment cleaned", cleanResult);
                if (cleanResult.PreservedFiles > 0)
                {
                    MessageBox.Show(
                        this,
                        "Some deployed files were preserved because they changed or could not be safely removed. Resolve them before deleting this profile.",
                        "Delete profile stopped",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    RefreshDeployStatus();
                    return;
                }
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                RefreshDeployStatus();
                MessageBox.Show(this, exception.Message, "Clean failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }

        try
        {
            _profileService.DeleteProfile(_managerPaths, profile.Id);
            ActivateProfile("default");
            MessageBox.Show(this, $"Deleted profile '{profile.Name}'.", "Profile deleted", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, exception.Message, "Delete profile failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void InstallModArchives(
        IReadOnlyList<string> archivePaths,
        IReadOnlyList<ModInstallOutcome>? initialOutcomes = null)
    {
        var progress = ShowProgress("Install Mods", "Installing mod archives", "Reading archives...");
        var plannedImports = new List<ModInstallPlan>();
        var outcomes = initialOutcomes?.ToList() ?? new List<ModInstallOutcome>();

        try
        {
            foreach (var archivePath in archivePaths)
            {
                try
                {
                    UpdateProgress(progress, $"Reading {Path.GetFileName(archivePath)}...");
                    var preview = _importService.PreviewZip(archivePath, _managerPaths);
                    plannedImports.Add(new ModInstallPlan(archivePath, preview));
                }
                catch (Exception exception) when (exception is IOException
                    or InvalidDataException
                    or InvalidOperationException
                    or UnauthorizedAccessException)
                {
                    outcomes.Add(ModInstallOutcome.Failure(Path.GetFileName(archivePath), exception.Message));
                }
            }

            var deployedUpdateModIds = plannedImports
                .Where(plan => plan.Preview.Action == ModImportAction.Updated)
                .Where(plan => CountDeployedFilesForMod(plan.Preview.ModId) > 0)
                .Select(plan => plan.Preview.ModId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            IReadOnlyList<ProfileDeployResult> preUpdateCleanResults = Array.Empty<ProfileDeployResult>();
            if (deployedUpdateModIds.Count > 0)
            {
                CloseProgress(progress);
                var answer = MessageBox.Show(
                    this,
                    BuildDeployedUpdatePrompt(plannedImports, deployedUpdateModIds),
                    "Update deployed mods",
                    MessageBoxButton.YesNoCancel,
                    MessageBoxImage.Warning);
                if (answer == MessageBoxResult.Cancel)
                {
                    return;
                }

                if (answer == MessageBoxResult.Yes)
                {
                    try
                    {
                        preUpdateCleanResults = CleanDeploymentsForMods(deployedUpdateModIds);
                        var blockedCleanResults = preUpdateCleanResults
                            .Where(result => result.PreservedFiles > 0)
                            .ToArray();
                        if (blockedCleanResults.Length > 0)
                        {
                            RefreshDeployStatus();
                            MessageBox.Show(
                                this,
                                BuildBlockedUpdateCleanupMessage(blockedCleanResults),
                                "Update stopped",
                                MessageBoxButton.OK,
                                MessageBoxImage.Warning);
                            return;
                        }
                    }
                    catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException)
                    {
                        RefreshDeployStatus();
                        MessageBox.Show(this, exception.Message, "Clean failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                }

                progress = ShowProgress("Install Mods", "Installing mod archives", "Installing archives...");
            }

            foreach (var plannedImport in plannedImports)
            {
                try
                {
                    UpdateProgress(progress, $"Installing {Path.GetFileName(plannedImport.ArchivePath)}...");
                    var result = _importService.ImportZip(plannedImport.ArchivePath, _managerPaths);
                    outcomes.Add(ModInstallOutcome.Success(Path.GetFileName(plannedImport.ArchivePath), result));
                }
                catch (Exception exception) when (exception is IOException
                    or InvalidDataException
                    or InvalidOperationException
                    or UnauthorizedAccessException)
                {
                    outcomes.Add(ModInstallOutcome.Failure(Path.GetFileName(plannedImport.ArchivePath), exception.Message));
                }
            }

            var newlyInstalledModIds = outcomes
                .Where(outcome => outcome.Result?.Action == ModImportAction.Installed)
                .Select(outcome => outcome.Result!.Manifest.Mod.Id)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            UpdateProgress(progress, "Refreshing mod list...");
            LoadMods();
            EnableInstalledModsInActiveProfile(newlyInstalledModIds);
            CloseProgress(progress);
            ShowModInstallResults(outcomes, preUpdateCleanResults);
        }
        finally
        {
            CloseProgress(progress);
        }
    }

    private void ShowUpdateCheckResults(IReadOnlyList<NexusUpdateCheckResult> results)
    {
        var updates = results.Where(result => result.IsUpdateAvailable).ToArray();
        var errors = results.Where(result => !string.IsNullOrWhiteSpace(result.ErrorMessage)).ToArray();
        var latestVersion = results.Count(result => result.Status == "Latest version");
        var lines = new List<string>
        {
            $"Checked: {results.Count}",
            $"Latest version: {latestVersion}",
            $"Updates available: {updates.Length}",
            $"Metadata errors: {errors.Length}"
        };

        var detailLines = updates
            .Select(result => $"{GetModName(result.ModId)}: latest {result.LatestVersion ?? result.LatestFileId?.ToString() ?? "unknown"}")
            .Concat(errors.Select(result => $"{GetModName(result.ModId)}: {result.ErrorMessage}"))
            .Take(12)
            .ToArray();
        if (detailLines.Length > 0)
        {
            lines.Add(string.Empty);
            lines.AddRange(detailLines);
        }

        if (!string.IsNullOrWhiteSpace(_lastNexusFilesCacheSummary))
        {
            lines.Add(string.Empty);
            lines.Add(_lastNexusFilesCacheSummary);
        }

        MessageBox.Show(
            this,
            string.Join("\n", lines),
            "Check Updates",
            MessageBoxButton.OK,
            errors.Length == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private async Task<IReadOnlyList<NexusUpdateCheckResult>> CheckNexusUpdatesAsync(
        IReadOnlyList<ModLibraryEntry> checkableEntries)
    {
        foreach (var row in _mods.Where(mod => mod.CanCheckUpdates))
        {
            row.UpdateStatus = "Checking...";
        }

        ModsListView.Items.Refresh();

        var catalogLoad = await _nexusMetadataCatalogService.LoadAsync(_managerPaths, forceRefresh: true);
        RefreshNexusMetadataStatusText(catalogLoad.Status);
        var results = new List<NexusUpdateCheckResult>();
        var manifestsByModId = new Dictionary<string, ModManifest>(StringComparer.OrdinalIgnoreCase);
        var progressDialog = OwnedWindows.OfType<VirtualLaunchProgressDialog>()
            .FirstOrDefault(window => window.Title is "Check Updates" or "Update Mods");
        var processed = 0;
        foreach (var entry in checkableEntries)
        {
            processed++;
            UpdateProgress(progressDialog, $"Checking {processed}/{checkableEntries.Count}: {entry.Mod.Name}");
            var match = _nexusMetadataMatcher.FindBestMatch(entry, catalogLoad.Entries);
            var (result, manifest) = match is null
                ? (new NexusUpdateCheckResult(
                    entry.Mod.Id,
                    "Metadata missing",
                    false,
                    null,
                    null,
                    "The linked mod was not found in the metadata catalog.",
                    entry.Manifest.Source?.GameDomain,
                    entry.Manifest.Source?.ModId),
                    entry.Manifest)
                : CheckNexusUpdateWithMetadata(entry, match.Entry);
            results.Add(result);
            manifestsByModId[entry.Mod.Id] = manifest;
            PersistNexusUpdateCheck(entry, result, manifest);
            ApplyUpdateResultToRow(result);
        }

        ModsListView.Items.Refresh();
        return await ConfirmNexusUpdateResultsWithFilesAsync(checkableEntries, results, manifestsByModId);
    }

    private async Task<IReadOnlyList<NexusUpdateCheckResult>> ConfirmNexusUpdateResultsWithFilesAsync(
        IReadOnlyList<ModLibraryEntry> entries,
        IReadOnlyList<NexusUpdateCheckResult> results,
        IReadOnlyDictionary<string, ModManifest> manifestsByModId)
    {
        _lastNexusFilesCacheSummary = null;
        var entriesById = entries.ToDictionary(entry => entry.Mod.Id, StringComparer.OrdinalIgnoreCase);
        var candidates = results
            .Where(result => result.NexusModId is not null)
            .Where(result => !string.IsNullOrWhiteSpace(result.GameDomain))
            .Where(result =>
                entriesById.TryGetValue(result.ModId, out var entry)
                && NeedsNexusFilesConfirmation(
                    manifestsByModId.TryGetValue(result.ModId, out var manifest) ? manifest : entry.Manifest,
                    result))
            .ToArray();
        if (candidates.Length == 0)
        {
            return results;
        }

        var apiKey = GetConfiguredNexusApiKey();
        var confirmedResults = results.ToArray();
        var cached = 0;
        var refreshed = 0;
        var failed = 0;
        var corrected = 0;
        foreach (var result in candidates)
        {
            if (!entriesById.TryGetValue(result.ModId, out var entry))
            {
                continue;
            }

            var manifest = manifestsByModId.TryGetValue(result.ModId, out var savedManifest)
                ? savedManifest
                : entry.Manifest;
            var row = _mods.FirstOrDefault(mod => mod.Id.Equals(result.ModId, StringComparison.OrdinalIgnoreCase));
            var previousStatus = row?.UpdateStatus;
            NexusModFilesLoadResult? filesResult = null;
            var handledWithoutFiles = false;
            var fingerprint = BuildNexusFilesFingerprint(result.GameDomain!, result.NexusModId!.Value, result);
            if (row is not null)
            {
                row.UpdateStatus = "Verifying files...";
                ModsListView.Items.Refresh();
            }

            try
            {
                filesResult = _nexusModFilesService.TryLoadCached(
                    _managerPaths,
                    result.GameDomain!,
                    result.NexusModId!.Value,
                    fingerprint);
                if (filesResult is not null)
                {
                    cached++;
                }
                else if (!string.IsNullOrWhiteSpace(apiKey))
                {
                    filesResult = await _nexusModFilesService.LoadOrRefreshAsync(
                        _managerPaths,
                        result.GameDomain!,
                        result.NexusModId!.Value,
                        fingerprint,
                        apiKey,
                        forceRefresh: false);
                    if (filesResult.IsFromCache)
                    {
                        cached++;
                    }
                    else
                    {
                        refreshed++;
                    }
                }
                else
                {
                    filesResult = TryLoadCompatibleCachedFiles(result);
                    if (filesResult is null)
                    {
                        failed++;
                    }
                    else
                    {
                        cached++;
                    }
                }

                if (filesResult is null)
                {
                    if (result.IsUpdateAvailable && !CanDownloadUpdate(result))
                    {
                        var unconfirmed = result with
                        {
                            Status = "Needs file check",
                            IsUpdateAvailable = false
                        };
                        var unconfirmedResultIndex = Array.FindIndex(
                            confirmedResults,
                            item => item.ModId.Equals(result.ModId, StringComparison.OrdinalIgnoreCase));
                        if (unconfirmedResultIndex >= 0)
                        {
                            confirmedResults[unconfirmedResultIndex] = unconfirmed;
                        }

                        corrected++;
                        handledWithoutFiles = true;
                        PersistNexusUpdateCheck(entry, unconfirmed, manifest);
                        ApplyUpdateResultToRow(unconfirmed);
                    }

                    continue;
                }

                var confirmed = ConfirmNexusUpdateWithFiles(manifest, result, filesResult.Files);
                var resultIndex = Array.FindIndex(
                    confirmedResults,
                    item => item.ModId.Equals(result.ModId, StringComparison.OrdinalIgnoreCase));
                if (resultIndex >= 0)
                {
                    confirmedResults[resultIndex] = confirmed;
                }

                if (!confirmed.Equals(result))
                {
                    corrected++;
                    PersistNexusUpdateCheck(entry, confirmed, manifest);
                }

                ApplyUpdateResultToRow(confirmed);
            }
            catch (Exception exception) when (exception is HttpRequestException
                or TaskCanceledException
                or JsonException
                or IOException
                or InvalidOperationException
                or UnauthorizedAccessException)
            {
                failed++;
            }
            finally
            {
                if (row is not null && filesResult is null && !handledWithoutFiles)
                {
                    row.UpdateStatus = previousStatus ?? BuildUpdateStatusText(result);
                    ModsListView.Items.Refresh();
                }
            }
        }

        _lastNexusFilesCacheSummary = $"Nexus files: {corrected} corrected, {cached} reused from cache, {refreshed} refreshed, {failed} skipped or failed.";
        ShowSelectedMod(ModsListView.SelectedItem as ModRow);
        ModsListView.Items.Refresh();
        return confirmedResults;
    }

    private NexusModFilesLoadResult? TryLoadCompatibleCachedFiles(NexusUpdateCheckResult result)
    {
        if (string.IsNullOrWhiteSpace(result.GameDomain) || result.NexusModId is null)
        {
            return null;
        }

        var cached = _nexusModFilesService.TryLoadAnyCached(
            _managerPaths,
            result.GameDomain,
            result.NexusModId.Value);
        return cached is not null && CachedFilesMatchExpectedLatest(cached.Files, result)
            ? cached
            : null;
    }

    private static bool CachedFilesMatchExpectedLatest(
        IReadOnlyList<NexusModFileInfo> files,
        NexusUpdateCheckResult result)
    {
        if (files.Count == 0)
        {
            return false;
        }

        if (result.LatestFileId is not null && files.Any(file => file.FileId == result.LatestFileId.Value))
        {
            return true;
        }

        var latestVersion = GetKnownModVersion(result.LatestVersion);
        return !string.IsNullOrWhiteSpace(latestVersion)
            && files
                .Where(IsCurrentNexusFile)
                .Select(file => GetKnownModVersion(file.Version))
                .Any(version => !string.IsNullOrWhiteSpace(version) && VersionsEqual(version!, latestVersion));
    }

    private void ApplyUpdateResultToRow(NexusUpdateCheckResult result)
    {
        var row = _mods.FirstOrDefault(mod => mod.Id.Equals(result.ModId, StringComparison.OrdinalIgnoreCase));
        if (row is null)
        {
            return;
        }

        row.UpdateStatus = BuildUpdateStatusText(result);
        row.LatestVersion = result.LatestVersion ?? string.Empty;
        row.NexusVersion = result.LatestVersion ?? row.NexusVersion;
        row.LatestNexusFileId = result.LatestFileId;
        row.LatestNexusFileName = result.LatestFileName ?? row.LatestNexusFileName;
    }

    private void PersistNexusUpdateCheck(ModLibraryEntry entry, NexusUpdateCheckResult result, ModManifest manifest)
    {
        if (manifest.Source is null)
        {
            return;
        }

        _libraryService.SaveManifest(entry.ManifestPath, ApplyNexusUpdateCheckResult(manifest, result));
    }

    private ModLibraryEntry[] GetCheckableNexusEntries()
    {
        return _libraryEntries
            .Where(entry => entry.Manifest.Source?.CanCheckUpdates == true)
            .ToArray();
    }

    private static bool CanDownloadUpdate(NexusUpdateCheckResult result)
    {
        return result.LatestFileId is not null
            && result.NexusModId is not null
            && !string.IsNullOrWhiteSpace(result.GameDomain);
    }

    private static string BuildNexusFilesFingerprint(
        string gameDomain,
        int modId,
        NexusUpdateCheckResult result)
    {
        return NexusModFilesService.BuildCacheFingerprint(
            gameDomain,
            modId,
            result.LatestFileId,
            result.LatestVersion,
            result.LatestFileName);
    }

    private static string BuildNexusFilesFingerprint(ModRow mod)
    {
        var latestVersion = string.IsNullOrWhiteSpace(mod.LatestVersion)
            ? mod.NexusVersion
            : mod.LatestVersion;
        return NexusModFilesService.BuildCacheFingerprint(
            mod.GameDomain,
            mod.NexusModId!.Value,
            mod.LatestNexusFileId ?? mod.CurrentNexusFileId,
            latestVersion,
            mod.LatestNexusFileName);
    }

    private static IReadOnlyList<NexusModUpdatePlan> BuildNexusUpdatePlans(
        IReadOnlyList<ModLibraryEntry> entries,
        IReadOnlyList<NexusUpdateCheckResult> results)
    {
        var entriesById = entries.ToDictionary(entry => entry.Mod.Id, StringComparer.OrdinalIgnoreCase);
        return results
            .Where(result => result.IsUpdateAvailable)
            .Where(CanDownloadUpdate)
            .Where(result => entriesById.ContainsKey(result.ModId))
            .Select(result => new NexusModUpdatePlan(entriesById[result.ModId], result))
            .ToArray();
    }

    private void ShowNoDownloadableUpdates(
        IReadOnlyList<NexusUpdateCheckResult> results,
        IReadOnlyList<NexusUpdateCheckResult> skippedUpdates)
    {
        var errors = results.Where(result => !string.IsNullOrWhiteSpace(result.ErrorMessage)).ToArray();
        var lines = new List<string>
        {
            "No downloadable updates were found.",
            $"Updates requiring manual review: {skippedUpdates.Count}",
            $"Metadata errors: {errors.Length}",
            "If Nexus refuses automatic download links, the account may need Nexus Premium."
        };

        var detailLines = skippedUpdates
            .Select(result => $"{GetModName(result.ModId)}: update found, but Nexus did not provide a downloadable file id")
            .Concat(errors.Select(result => $"{GetModName(result.ModId)}: {result.ErrorMessage}"))
            .Take(10)
            .ToArray();
        if (detailLines.Length > 0)
        {
            lines.Add(string.Empty);
            lines.AddRange(detailLines);
        }

        MessageBox.Show(
            this,
            string.Join("\n", lines),
            "Update Mods",
            MessageBoxButton.OK,
            errors.Length == 0 && skippedUpdates.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private string BuildUpdateModsPrompt(
        IReadOnlyList<NexusModUpdatePlan> updatePlans,
        IReadOnlyList<NexusUpdateCheckResult> skippedUpdates)
    {
        var lines = new List<string>
        {
            $"Download and install {updatePlans.Count} Nexus update(s)?",
            string.Empty
        };

        lines.AddRange(updatePlans
            .Select(plan => $"{plan.Entry.Mod.Name}: {plan.Result.LatestVersion ?? plan.Result.LatestFileName ?? plan.Result.LatestFileId?.ToString() ?? "latest"}")
            .Take(10));
        if (updatePlans.Count > 10)
        {
            lines.Add($"... {updatePlans.Count - 10} more");
        }

        if (skippedUpdates.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add($"Manual review needed: {skippedUpdates.Count}");
        }

        lines.Add(string.Empty);
        lines.Add("Archives will be saved in the manager downloads folder before installation.");
        lines.Add("If automatic download fails, Nexus may require Premium for API downloads; the manager can open the mod files page for manual download.");
        return string.Join("\n", lines);
    }

    private static string BuildNexusUpdateArchiveFileName(NexusModUpdatePlan updatePlan)
    {
        var modName = string.IsNullOrWhiteSpace(updatePlan.Entry.Mod.Name)
            ? updatePlan.Entry.Mod.Id
            : updatePlan.Entry.Mod.Name;
        var nexusModId = updatePlan.Result.NexusModId?.ToString() ?? "mod";
        var fileId = updatePlan.Result.LatestFileId?.ToString() ?? "file";
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return $"{modName}-{nexusModId}-{fileId}-{timestamp}.zip";
    }

    private void OfferManualFallbackForUpdates(IEnumerable<NexusUpdateCheckResult> updateResults)
    {
        var manualResults = updateResults
            .Where(result => result.NexusModId is not null)
            .Where(result => !string.IsNullOrWhiteSpace(result.GameDomain))
            .GroupBy(result => $"{result.GameDomain}:{result.NexusModId}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        if (manualResults.Length == 0)
        {
            return;
        }

        var lines = new List<string>
        {
            "Some updates need manual download from Nexus.",
            "This often means Nexus did not provide an automatic download link. A common reason is that the account may need Nexus Premium for API downloads.",
            string.Empty,
            "Open the Nexus files page now? After downloading the archive manually, use Install Mods."
        };

        var answer = MessageBox.Show(
            this,
            string.Join("\n", lines),
            "Manual Nexus Download",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        foreach (var result in manualResults.Take(8))
        {
            OpenNexusFilesPage(result.GameDomain!, result.NexusModId!.Value);
        }

        if (manualResults.Length > 8)
        {
            MessageBox.Show(
                this,
                $"Opened 8 Nexus pages. {manualResults.Length - 8} more update pages were skipped to avoid opening too many browser tabs.",
                "Manual Nexus Download",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    private void OpenNexusFilesPage(string gameDomain, int modId)
    {
        var uri = $"https://www.nexusmods.com/{gameDomain}/mods/{modId}?tab=files";
        OpenUri(uri, "Open Nexus page failed");
    }

    private void OpenUri(string uri, string title)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(uri)
            {
                UseShellExecute = true
            });
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            MessageBox.Show(
                this,
                $"Could not open the Nexus page automatically.\n\n{uri}\n\n{exception.Message}",
                title,
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private async Task<NexusLinkCompletion> CompleteNexusSourceFromCatalogAsync(ModLibraryEntry entry)
    {
        if (entry.Manifest.Source?.CanCheckUpdates != true)
        {
            return new NexusLinkCompletion(entry.Manifest, null);
        }

        try
        {
            var catalogLoad = await _nexusMetadataCatalogService.LoadAsync(_managerPaths);
            var match = _nexusMetadataMatcher.FindBestMatch(entry, catalogLoad.Entries);
            if (match is null)
            {
                return new NexusLinkCompletion(
                    entry.Manifest,
                    new NexusUpdateCheckResult(
                        entry.Mod.Id,
                        "Metadata missing",
                        false,
                        null,
                        null,
                        "The linked Nexus mod is not in the metadata catalog.",
                        entry.Manifest.Source.GameDomain,
                        entry.Manifest.Source.ModId));
            }

            var (result, manifest) = CheckNexusUpdateWithMetadata(entry, match.Entry);
            return new NexusLinkCompletion(ApplyNexusUpdateCheckResult(manifest, result), result);
        }
        catch (Exception exception) when (exception is InvalidOperationException or HttpRequestException or TaskCanceledException or IOException)
        {
            return new NexusLinkCompletion(
                entry.Manifest,
                new NexusUpdateCheckResult(
                    entry.Mod.Id,
                    "Metadata error",
                    false,
                    null,
                    null,
                    exception.Message,
                    entry.Manifest.Source.GameDomain,
                    entry.Manifest.Source.ModId));
        }
    }

    private (NexusUpdateCheckResult Result, ModManifest Manifest) CheckNexusUpdateWithMetadata(
        ModLibraryEntry entry,
        NexusMetadataCatalogEntry metadata)
    {
        var manifest = ApplyNexusSource(entry.Manifest, CreateNexusSourceFromMetadata(entry, metadata));
        return (CheckNexusUpdateFromMetadata(manifest, metadata), manifest);
    }

    private ModSourceInfo CreateNexusSourceFromMetadata(
        ModLibraryEntry entry,
        NexusMetadataCatalogEntry metadata)
    {
        var archiveFileName = GetSourceArchiveFileName(entry);
        var detectedVersion = ModSourceDetector.DetectVersion(archiveFileName);
        var detectedSource = ModSourceDetector.Detect(archiveFileName, detectedVersion);
        var downloadReference = metadata.DownloadReference;
        var source = entry.Manifest.Source;
        var gameDomain = FirstNonEmpty(
            metadata.NexusGameDomain,
            downloadReference?.GameDomain,
            source?.GameDomain,
            _settings.NexusGameDomain);
        var modId = metadata.NexusModId
            ?? downloadReference?.ModId
            ?? source?.ModId
            ?? detectedSource?.ModId;
        var fileId = source?.FileId ?? detectedSource?.FileId;
        var fileVersion = GetKnownModVersion(source?.FileVersion)
            ?? GetKnownModVersion(detectedSource?.FileVersion)
            ?? GetKnownModVersion(entry.Mod.Version)
            ?? GetKnownModVersion(detectedVersion)
            ?? GetKnownModVersion(metadata.BestVersion);

        return new ModSourceInfo(
            "NexusMods",
            NormalizeNexusGameDomain(gameDomain),
            modId,
            fileId,
            fileVersion,
            source?.FileTimestamp ?? detectedSource?.FileTimestamp,
            source?.SourceArchiveFileName ?? archiveFileName,
            source?.LastUpdateStatus,
            source?.LastLatestVersion,
            source?.LastCheckedAt,
            metadata.Name,
            metadata.Author,
            metadata.NexusPageUrl,
            metadata.BestIconUrl,
            metadata.Images,
            metadata.Description,
            metadata.Statistics?.Endorsements,
            metadata.Statistics?.UniqueDownloads,
            metadata.Statistics?.TotalDownloads,
            metadata.Statistics?.TotalViews,
            source?.LastLatestFileId ?? downloadReference?.FileId);
    }

    private NexusUpdateCheckResult CheckNexusUpdateFromMetadata(
        ModManifest manifest,
        NexusMetadataCatalogEntry metadata)
    {
        var source = manifest.Source;
        if (source is null || source.ModId is null)
        {
            return new NexusUpdateCheckResult(manifest.Mod.Id, "Not linked", false, null, null, null);
        }

        var downloadReference = metadata.DownloadReference;
        var latestVersion = GetDisplayLatestVersion(metadata);
        var latestFileId = downloadReference?.FileId;
        var gameDomain = FirstNonEmpty(metadata.NexusGameDomain, downloadReference?.GameDomain, source.GameDomain);
        var nexusModId = metadata.NexusModId ?? downloadReference?.ModId ?? source.ModId;
        var isUpdateAvailable = IsCatalogUpdateAvailable(manifest, source, latestVersion, latestFileId, metadata);
        return new NexusUpdateCheckResult(
            manifest.Mod.Id,
            isUpdateAvailable ? "Update available" : "Latest version",
            isUpdateAvailable,
            latestVersion,
            latestFileId,
            null,
            gameDomain,
            nexusModId,
            metadata.Name);
    }

    private static bool IsCatalogUpdateAvailable(
        ModManifest manifest,
        ModSourceInfo source,
        string? latestVersion,
        int? latestFileId,
        NexusMetadataCatalogEntry metadata)
    {
        if (source.FileId is not null && latestFileId is not null && source.FileId.Value == latestFileId.Value)
        {
            return false;
        }

        if (source.FileId is not null && latestFileId is not null && source.FileId.Value > latestFileId.Value)
        {
            return false;
        }

        var installedVersions = GetInstalledComparableVersions(manifest);
        var catalogVersions = GetCatalogComparableVersions(metadata).ToArray();
        if (installedVersions.Any(installedVersion =>
            !string.IsNullOrWhiteSpace(latestVersion) && VersionsEqual(installedVersion, latestVersion)
            || catalogVersions.Any(catalogVersion => VersionsEqual(installedVersion, catalogVersion))))
        {
            return false;
        }

        if (source.FileId is not null && !string.IsNullOrWhiteSpace(latestVersion)
            && source.FileId.Value.ToString().Equals(TrimSimpleVersion(latestVersion), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (installedVersions.Count > 0 && catalogVersions.Length > 0)
        {
            var comparisons = installedVersions
                .SelectMany(installedVersion => catalogVersions
                    .Select(catalogVersion => CompareSemanticVersions(installedVersion, catalogVersion)))
                .Where(comparison => comparison is not null)
                .Select(comparison => comparison!.Value)
                .ToArray();
            if (comparisons.Any(comparison => comparison >= 0))
            {
                return false;
            }

            if (comparisons.Any(comparison => comparison < 0))
            {
                return true;
            }
        }

        if (source.FileId is not null && latestFileId is not null)
        {
            return true;
        }

        return false;
    }

    private static bool NeedsNexusFilesConfirmation(ModManifest manifest, NexusUpdateCheckResult result)
    {
        if (result.NexusModId is null || string.IsNullOrWhiteSpace(result.GameDomain))
        {
            return false;
        }

        if (result.IsUpdateAvailable)
        {
            return true;
        }

        if (result.LatestFileId is not null)
        {
            return true;
        }

        var latestVersion = GetKnownModVersion(result.LatestVersion);
        if (string.IsNullOrWhiteSpace(latestVersion))
        {
            return false;
        }

        return GetInstalledComparableVersions(manifest)
            .Select(installedVersion => CompareSemanticVersions(installedVersion, latestVersion))
            .Any(comparison => comparison > 0);
    }

    private static NexusUpdateCheckResult ConfirmNexusUpdateWithFiles(
        ModManifest manifest,
        NexusUpdateCheckResult result,
        IReadOnlyList<NexusModFileInfo> files)
    {
        var latestFile = ChooseLatestUpdateFileForManifest(manifest, files);
        if (latestFile is null)
        {
            return result;
        }

        var latestVersion = GetKnownModVersion(latestFile.Version);
        var confirmed = result with
        {
            LatestVersion = latestVersion ?? result.LatestVersion,
            LatestFileId = latestFile.FileId,
            LatestFileName = latestFile.FileName
        };

        var installedVersions = GetInstalledComparableVersions(manifest);
        var sourceFileId = manifest.Source?.FileId;
        var sourceFile = sourceFileId is null
            ? null
            : files.FirstOrDefault(file => file.FileId == sourceFileId.Value);
        var sourceFileIsKnownOld = sourceFile is not null && !IsCurrentNexusFile(sourceFile);
        var matchesLatestFileId = sourceFileId is not null && sourceFileId.Value == latestFile.FileId;
        var localFileIdLooksNewer = !sourceFileIsKnownOld && sourceFileId is not null && sourceFileId.Value > latestFile.FileId;
        var matchesLatestVersion = !string.IsNullOrWhiteSpace(latestVersion)
            && installedVersions.Any(installedVersion => VersionIsSameOrNewer(installedVersion, latestVersion));

        return matchesLatestFileId || localFileIdLooksNewer || matchesLatestVersion
            ? confirmed with
            {
                Status = "Latest version",
                IsUpdateAvailable = false,
                ErrorMessage = null
            }
            : confirmed with
            {
                Status = "Update available",
                IsUpdateAvailable = true,
                ErrorMessage = null
            };
    }

    private static NexusModFileInfo? ChooseLatestUpdateFile(IReadOnlyList<NexusModFileInfo> files)
    {
        if (files.Count == 0)
        {
            return null;
        }

        var currentFiles = files
            .Where(IsCurrentNexusFile)
            .ToArray();
        var candidates = currentFiles.Length > 0
            ? currentFiles
            : files.Where(file => !file.IsOldVersion).ToArray();
        if (candidates.Length == 0)
        {
            candidates = files.ToArray();
        }

        return candidates
            .OrderByDescending(file => file.UploadedAt ?? DateTimeOffset.MinValue)
            .ThenByDescending(file => file.IsPrimary)
            .ThenByDescending(file => file.FileId)
            .FirstOrDefault();
    }

    private static NexusModFileInfo? ChooseLatestUpdateFileForManifest(
        ModManifest manifest,
        IReadOnlyList<NexusModFileInfo> files)
    {
        var latestFile = ChooseLatestUpdateFile(files);
        if (latestFile is null)
        {
            return null;
        }

        var sourceFileId = manifest.Source?.FileId;
        if (sourceFileId is null)
        {
            return latestFile;
        }

        var sourceFile = files.FirstOrDefault(file => file.FileId == sourceFileId.Value);
        if (sourceFile is null || !IsCurrentNexusFile(sourceFile))
        {
            return latestFile;
        }

        var sourceVersion = GetKnownModVersion(sourceFile.Version);
        var latestVersion = GetKnownModVersion(latestFile.Version);
        return !string.IsNullOrWhiteSpace(sourceVersion)
            && !string.IsNullOrWhiteSpace(latestVersion)
            && VersionsEqual(sourceVersion, latestVersion)
            ? sourceFile
            : latestFile;
    }

    private static bool IsCurrentNexusFile(NexusModFileInfo file)
    {
        return !file.IsOldVersion
            && !file.Category.Contains("old", StringComparison.OrdinalIgnoreCase)
            && !file.Category.Contains("archived", StringComparison.OrdinalIgnoreCase);
    }

    private static bool VersionIsSameOrNewer(string installedVersion, string latestVersion)
    {
        if (VersionsEqual(installedVersion, latestVersion))
        {
            return true;
        }

        return CompareSemanticVersions(installedVersion, latestVersion) is int comparison
            && comparison >= 0;
    }

    private static IReadOnlyList<string> GetInstalledComparableVersions(ModManifest manifest)
    {
        var versions = new List<string>();
        AddInstalledComparableVersion(versions, manifest.Source?.FileVersion);
        AddInstalledComparableVersion(versions, manifest.Mod.Version);
        AddInstalledComparableVersion(versions, ModSourceDetector.DetectVersion(manifest.SourceArchiveFileName));
        AddInstalledComparableVersion(versions, ModSourceDetector.DetectVersion(manifest.Source?.SourceArchiveFileName ?? string.Empty));
        return versions;
    }

    private static void AddInstalledComparableVersion(List<string> versions, string? version)
    {
        var knownVersion = GetKnownModVersion(version);
        if (string.IsNullOrWhiteSpace(knownVersion)
            || versions.Any(existingVersion => VersionsEqual(existingVersion, knownVersion)))
        {
            return;
        }

        versions.Add(knownVersion);
    }

    private static string? GetDisplayLatestVersion(NexusMetadataCatalogEntry metadata)
    {
        return GetKnownModVersion(metadata.BestVersion)
            ?? GetCatalogComparableVersions(metadata).FirstOrDefault();
    }

    private static IEnumerable<string> GetCatalogComparableVersions(NexusMetadataCatalogEntry metadata)
    {
        var pluginVersions = new[]
        {
            metadata.BepInExVersion,
            metadata.DllVersion
        }
        .Concat(metadata.DllVersions.Values)
        .Select(GetKnownModVersion)
        .Where(version => !string.IsNullOrWhiteSpace(version))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

        foreach (var version in pluginVersions)
        {
            yield return version!;
        }

        var pageVersion = GetKnownModVersion(metadata.Version);
        if (pageVersion is null)
        {
            yield break;
        }

        if (pluginVersions.Length == 0
            || pluginVersions.Any(version => VersionsEqual(version!, pageVersion))
            || !LooksLikeSimpleVersionCounter(pageVersion))
        {
            yield return pageVersion;
        }
    }

    private static int? CompareSemanticVersions(string first, string second)
    {
        var firstParts = ParseSemanticVersionParts(first);
        var secondParts = ParseSemanticVersionParts(second);
        if (firstParts.Length == 0 || secondParts.Length == 0)
        {
            return null;
        }

        var maxLength = Math.Max(firstParts.Length, secondParts.Length);
        for (var index = 0; index < maxLength; index++)
        {
            var left = index < firstParts.Length ? firstParts[index] : 0;
            var right = index < secondParts.Length ? secondParts[index] : 0;
            if (left != right)
            {
                return left.CompareTo(right);
            }
        }

        return 0;
    }

    private static int[] ParseSemanticVersionParts(string version)
    {
        var value = NormalizeComparableVersion(version);
        return value
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => int.TryParse(part, out var parsed) ? parsed : (int?)null)
            .TakeWhile(part => part is not null)
            .Select(part => part!.Value)
            .ToArray();
    }

    private static bool LooksLikeSimpleVersionCounter(string version)
    {
        return int.TryParse(TrimSimpleVersion(version), out _);
    }

    private static string TrimSimpleVersion(string version)
    {
        var value = version.Trim();
        return value.StartsWith('v') || value.StartsWith('V')
            ? value[1..]
            : value;
    }

    private static bool ShouldResetNexusGameDomain(string? domain)
    {
        return string.IsNullOrWhiteSpace(domain)
            || domain.Equals("casualtiesunknowndemo", StringComparison.OrdinalIgnoreCase)
            || domain.Equals("casualtiesunknown", StringComparison.OrdinalIgnoreCase);
    }

    private string GetModName(string modId)
    {
        return _libraryEntries.FirstOrDefault(entry => entry.Mod.Id.Equals(modId, StringComparison.OrdinalIgnoreCase))?.Mod.Name
            ?? modId;
    }

    private string BuildConflictDetailsMessage(IReadOnlyList<OverlayConflict> conflicts)
    {
        var lines = new List<string>
        {
            $"Conflicts: {conflicts.Count} target file(s).",
            "Multiple enabled mods provide the same game file. The winner is the file that will be used.",
            string.Empty
        };

        foreach (var conflict in conflicts.Take(12))
        {
            var overwritten = conflict.Entries
                .Where(entry => !entry.IsWinner)
                .OrderBy(entry => entry.Priority)
                .ThenBy(entry => entry.OverlayOrder)
                .Select(entry => GetModName(entry.OwningModId))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            lines.Add(conflict.TargetRelativePath);
            lines.Add($"Winner: {GetModName(conflict.Winner.OwningModId)}");
            lines.Add(overwritten.Length == 0
                ? "Overwritten: none"
                : $"Overwritten: {string.Join(", ", overwritten)}");
            lines.Add(string.Empty);
        }

        if (conflicts.Count > 12)
        {
            lines.Add($"... {conflicts.Count - 12} more conflict(s)");
        }

        return string.Join("\n", lines).TrimEnd();
    }

    private string BuildWarningsDetailsMessage(OverlayPreview? overlayPreview)
    {
        var lines = new List<string>();
        var modWarnings = _mods
            .Where(mod => mod.WarningCount > 0)
            .Select(mod => new
            {
                mod.DisplayName,
                Warnings = mod.Warnings.Where(warning => !string.IsNullOrWhiteSpace(warning)).ToArray()
            })
            .Where(item => item.Warnings.Length > 0)
            .ToArray();

        var overlayWarnings = overlayPreview?.Warnings
            .Where(warning => !string.IsNullOrWhiteSpace(warning))
            .ToArray() ?? Array.Empty<string>();
        var missingSources = overlayPreview?.MissingSources ?? Array.Empty<OverlayPreviewEntry>();

        var total = modWarnings.Sum(item => item.Warnings.Length)
            + overlayWarnings.Length
            + missingSources.Count;
        if (total == 0)
        {
            return string.Empty;
        }

        lines.Add($"Warnings: {total} issue(s).");
        lines.Add("Warnings do not always block launch, but they are worth reviewing before sharing a modpack.");
        lines.Add(string.Empty);

        foreach (var item in modWarnings.Take(12))
        {
            lines.Add(item.DisplayName);
            foreach (var warning in item.Warnings.Take(4))
            {
                lines.Add($"- {FormatWarningForDisplay(warning)}");
            }

            if (item.Warnings.Length > 4)
            {
                lines.Add($"- ... {item.Warnings.Length - 4} more");
            }

            lines.Add(string.Empty);
        }

        if (overlayWarnings.Length > 0)
        {
            lines.Add("Profile overlay");
            foreach (var warning in overlayWarnings.Take(8))
            {
                lines.Add($"- {FormatWarningForDisplay(warning)}");
            }

            if (overlayWarnings.Length > 8)
            {
                lines.Add($"- ... {overlayWarnings.Length - 8} more");
            }

            lines.Add(string.Empty);
        }

        if (missingSources.Count > 0)
        {
            lines.Add("Missing source files");
            foreach (var missing in missingSources.Take(8))
            {
                lines.Add($"- {missing.TargetRelativePath} from {GetModName(missing.OwningModId)}");
            }

            if (missingSources.Count > 8)
            {
                lines.Add($"- ... {missingSources.Count - 8} more");
            }
        }

        return string.Join("\n", lines).TrimEnd();
    }

    private string FormatWarningForDisplay(string warning)
    {
        return _settings.ShowAdvancedModColumns
            ? warning
            : CompactWarningText(warning);
    }

    private static string CompactWarningText(string warning)
    {
        if (string.IsNullOrWhiteSpace(warning))
        {
            return "Unknown warning";
        }

        var text = Regex.Replace(warning.Trim(), @"\s+", " ");
        if (text.StartsWith("Potential external assembly references detected:", StringComparison.OrdinalIgnoreCase))
        {
            var details = text["Potential external assembly references detected:".Length..].Trim();
            return string.IsNullOrWhiteSpace(details)
                ? "External DLL references detected"
                : $"External DLL references: {TrimCompactWarning(details, 74)}";
        }

        if (text.StartsWith("Archive root '", StringComparison.OrdinalIgnoreCase))
        {
            return "Archive has an extra top-level folder.";
        }

        if (text.Contains("missing", StringComparison.OrdinalIgnoreCase)
            && text.Contains("depend", StringComparison.OrdinalIgnoreCase))
        {
            return TrimCompactWarning(text, 96);
        }

        if (text.Contains("outside", StringComparison.OrdinalIgnoreCase)
            && text.Contains("game", StringComparison.OrdinalIgnoreCase))
        {
            return "Skipped an unsafe path outside the game folder.";
        }

        if (text.Contains("source file is missing", StringComparison.OrdinalIgnoreCase)
            || text.Contains("missing source", StringComparison.OrdinalIgnoreCase))
        {
            return "A source file is missing.";
        }

        if (text.Contains("could not", StringComparison.OrdinalIgnoreCase)
            || text.Contains("failed", StringComparison.OrdinalIgnoreCase))
        {
            return TrimCompactWarning(text, 96);
        }

        return TrimCompactWarning(text, 110);
    }

    private static string TrimCompactWarning(string text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length <= maxLength)
        {
            return text;
        }

        return text[..Math.Max(0, maxLength - 3)].TrimEnd() + "...";
    }

    private static string BuildUpdateStatusText(NexusUpdateCheckResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
        {
            return result.Status.Contains("Metadata", StringComparison.OrdinalIgnoreCase)
                ? result.Status
                : "API error";
        }

        if (result.IsUpdateAvailable)
        {
            return string.IsNullOrWhiteSpace(result.LatestVersion)
                ? "Update available"
                : $"Update {result.LatestVersion}";
        }

        return result.Status;
    }

    private void ShowModInstallResults(
        IReadOnlyList<ModInstallOutcome> outcomes,
        IReadOnlyList<ProfileDeployResult>? preUpdateCleanResults = null)
    {
        var installedCount = outcomes.Count(outcome => outcome.Result?.Action == ModImportAction.Installed);
        var updatedCount = outcomes.Count(outcome => outcome.Result?.Action == ModImportAction.Updated);
        var failed = outcomes.Where(outcome => outcome.Error is not null).ToArray();
        var cleanedFiles = preUpdateCleanResults?.Sum(result => result.DeletedFiles) ?? 0;
        var warningCount = outcomes
            .Where(outcome => outcome.Result is not null)
            .Sum(outcome => outcome.Result!.Manifest.Warnings.Count);
        var dependencyWarnings = outcomes
            .Where(outcome => outcome.Result is not null)
            .SelectMany(outcome => BuildDependencyWarningLines(outcome.Result!))
            .ToArray();
        var archiveWarnings = outcomes
            .Where(outcome => outcome.Result is not null)
            .SelectMany(outcome => outcome.Result!.Manifest.Warnings
                .Select(warning => $"{outcome.Result!.Manifest.Mod.Name}: {warning}"))
            .ToArray();
        var deployedUpdates = outcomes
            .Where(outcome => outcome.Result?.Action == ModImportAction.Updated)
            .Where(outcome => CountDeployedFilesForMod(outcome.Result!.Manifest.Mod.Id) > 0)
            .Select(outcome => outcome.Result!.Manifest.Mod.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var lines = new List<string>
        {
            $"Installed: {installedCount}",
            $"Updated: {updatedCount}",
            $"Failed or skipped: {failed.Length}",
            $"Cleaned before update: {cleanedFiles}",
            $"Archive warnings: {warningCount}"
        };

        var detailLines = outcomes
            .Where(outcome => outcome.Result is not null)
            .Select(outcome => BuildSuccessfulInstallLine(outcome.Result!))
            .Concat(failed.Select(outcome => $"{outcome.ArchiveFileName}: {outcome.Error}"))
            .Concat(preUpdateCleanResults?.SelectMany(result => result.Warnings
                .Select(warning => $"{result.ProfileId}: {warning}")) ?? Array.Empty<string>())
            .Concat(archiveWarnings)
            .Concat(dependencyWarnings)
            .ToList();

        if (deployedUpdates.Length > 0)
        {
            detailLines.Add($"Deploy again to refresh updated deployed mods: {string.Join(", ", deployedUpdates)}");
        }

        if (detailLines.Count > 0)
        {
            lines.Add(string.Empty);
            lines.AddRange(detailLines.Take(14));
            if (detailLines.Count > 14)
            {
                lines.Add($"... {detailLines.Count - 14} more");
            }
        }

        MessageBox.Show(
            this,
            string.Join("\n", lines),
            "Install Mods",
            MessageBoxButton.OK,
            failed.Length == 0 && archiveWarnings.Length == 0 && dependencyWarnings.Length == 0
                ? MessageBoxImage.Information
                : MessageBoxImage.Warning);
    }

    private string BuildDeployedUpdatePrompt(
        IReadOnlyList<ModInstallPlan> plannedImports,
        IReadOnlySet<string> deployedUpdateModIds)
    {
        var updateLines = plannedImports
            .Where(plan => deployedUpdateModIds.Contains(plan.Preview.ModId))
            .Select(plan => $"{plan.Preview.SuggestedModName}: {CountDeployedFilesForMod(plan.Preview.ModId)} deployed files")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToArray();

        return "Some mods selected for update are currently deployed in the game folder. Clean the affected deployment before updating manager storage?\n\n"
            + string.Join("\n", updateLines)
            + "\n\nYes: clean deployment first. No: update manager storage only. Cancel: stop.";
    }

    private IReadOnlyList<ProfileDeployResult> CleanDeploymentsForMods(IReadOnlySet<string> modIds)
    {
        var results = new List<ProfileDeployResult>();
        foreach (var profile in _profiles)
        {
            var manifest = _profileDeployService.LoadManifest(_managerPaths, profile.Id);
            if (manifest is null || !manifest.Files.Any(file => modIds.Contains(file.OwningModId)))
            {
                continue;
            }

            results.Add(_profileDeployService.Clean(_managerPaths, profile.Id));
        }

        return results;
    }

    private static string BuildSuccessfulInstallLine(ModImportResult result)
    {
        var action = result.Action == ModImportAction.Updated ? "Updated" : "Installed";
        var previousArchive = result.PreviousManifest is null
            ? string.Empty
            : $" over {result.PreviousManifest.SourceArchiveFileName}";
        var libraryHint = LooksLikeDependencyLibrary(result.Manifest.Mod)
            ? " dependency/library candidate"
            : string.Empty;

        return $"{action}: {result.Manifest.Mod.Name} ({result.Manifest.Mod.Files.Count} files, {result.Manifest.Mod.Assemblies.Count} assemblies){previousArchive}{libraryHint}";
    }

    private static IReadOnlyList<string> BuildDependencyWarningLines(ModImportResult result)
    {
        var dependencyNames = result.Manifest.Mod.AssemblyReferences
            .Where(reference => !reference.IsKnownGameOrFrameworkReference)
            .Where(reference => !AssemblyReferenceClassifier.IsKnownGameOrFrameworkAssembly(reference.Name))
            .Select(reference => reference.Name)
            .Except(result.Manifest.Mod.Assemblies.Select(assembly => assembly.Name), StringComparer.OrdinalIgnoreCase)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return dependencyNames.Length == 0
            ? Array.Empty<string>()
            : new[] { $"{result.Manifest.Mod.Name}: external DLL refs: {string.Join(", ", dependencyNames)}" };
    }

    private static bool LooksLikeDependencyLibrary(ModPackage mod)
    {
        return mod.Assemblies.Count > 0
            && mod.Files.Count > 0
            && mod.Files.All(file =>
                Path.GetExtension(file.NormalizedTargetRelativePath).Equals(".dll", StringComparison.OrdinalIgnoreCase)
                && (file.TargetKind == ModTargetKind.BepInExPlugin || file.TargetKind == ModTargetKind.BepInExPatcher));
    }

    private int CountDeployedFilesForMod(string modId)
    {
        return _profiles.Sum(profile =>
            _profileDeployService.LoadManifest(_managerPaths, profile.Id)?.Files
                .Count(file => file.OwningModId.Equals(modId, StringComparison.OrdinalIgnoreCase)) ?? 0);
    }

    private void ModsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ShowSelectedMod(ModsListView.SelectedItem as ModRow);
    }

    private void LoadMods()
    {
        _isLoading = true;
        _isLoadingProfiles = true;
        try
        {
            _libraryEntries = _libraryService.LoadLibrary(_managerPaths);
            var profiles = _profileService.LoadProfiles(_managerPaths, _libraryEntries);
            _currentProfile = profiles.FirstOrDefault(profile => profile.Id.Equals(_settings.ActiveProfileId, StringComparison.OrdinalIgnoreCase))
                ?? profiles.First(profile => profile.Id.Equals("default", StringComparison.OrdinalIgnoreCase));
            if (!_settings.ActiveProfileId.Equals(_currentProfile.Id, StringComparison.OrdinalIgnoreCase))
            {
                _settings = _settings with { ActiveProfileId = _currentProfile.Id };
                _settingsService.Save(_managerPaths, _settings);
            }

            _profiles.Clear();
            foreach (var profile in profiles)
            {
                _profiles.Add(new ProfileRow(profile.Id, profile.Name));
            }

            ProfilesListBox.SelectedItem = _profiles.FirstOrDefault(profile => profile.Id.Equals(_currentProfile.Id, StringComparison.OrdinalIgnoreCase));
            ModpackExportProfileComboBox.SelectedItem = _profiles.FirstOrDefault(profile => profile.Id.Equals(_currentProfile.Id, StringComparison.OrdinalIgnoreCase));
            ProfileNameTextBox.Text = _currentProfile.Name;

            var libraryByModId = _libraryEntries.ToDictionary(entry => entry.Mod.Id, StringComparer.OrdinalIgnoreCase);
            var activeAssemblyProviders = BuildActiveProfileAssemblyProviders(_currentProfile, libraryByModId);
            _mods.Clear();
            foreach (var profileEntry in _currentProfile.Mods.OrderBy(entry => entry.Priority))
            {
                if (libraryByModId.TryGetValue(profileEntry.ModId, out var libraryEntry))
                {
                    _mods.Add(ModRow.FromEntry(libraryEntry, profileEntry.IsEnabled, profileEntry.Priority, activeAssemblyProviders));
                }
            }

            UpdateDisplayedModNames();
        }
        finally
        {
            _isLoadingProfiles = false;
            _isLoading = false;
        }

        RefreshProfileSummary();
        RefreshProfilePageStatus();
        RefreshDeployStatus();
        RefreshSettingsStatus();
        UpdateToggleAllModsButton();
        ModsListView.SelectedIndex = _mods.Count > 0 ? 0 : -1;
        if (_mods.Count == 0)
        {
            ShowSelectedMod(null);
        }

        if (ModpacksView.Visibility == Visibility.Visible)
        {
            RefreshModpacksView();
        }
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> BuildActiveProfileAssemblyProviders(
        ModProfile profile,
        IReadOnlyDictionary<string, ModLibraryEntry> libraryByModId)
    {
        var enabledModIds = profile.Mods
            .Where(entry => entry.IsEnabled)
            .Select(entry => entry.ModId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return libraryByModId.Values
            .Where(entry => enabledModIds.Contains(entry.Mod.Id))
            .SelectMany(entry => entry.Mod.Assemblies.Select(assembly => new
            {
                assembly.Name,
                entry.Mod.Id
            }))
            .GroupBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<string>)group
                    .Select(entry => entry.Id)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                StringComparer.OrdinalIgnoreCase);
    }

    private void SelectModById(string modId)
    {
        var row = _mods.FirstOrDefault(mod => mod.Id.Equals(modId, StringComparison.OrdinalIgnoreCase));
        if (row is not null)
        {
            ModsListView.SelectedItem = row;
        }
    }

    private void EnableInstalledModsInActiveProfile(IReadOnlyCollection<string> modIds)
    {
        if (modIds.Count == 0)
        {
            return;
        }

        var changed = false;
        foreach (var row in _mods.Where(mod => modIds.Contains(mod.Id, StringComparer.OrdinalIgnoreCase)))
        {
            if (!row.IsEnabled)
            {
                row.IsEnabled = true;
                changed = true;
            }
        }

        if (!changed)
        {
            return;
        }

        ModsListView.Items.Refresh();
        SaveProfileFromRows();
    }

    private void MoveSelectedMod(int direction)
    {
        var selectedIndex = ModsListView.SelectedIndex;
        if (selectedIndex < 0)
        {
            return;
        }

        var targetIndex = selectedIndex + direction;
        if (targetIndex < 0 || targetIndex >= _mods.Count)
        {
            return;
        }

        _mods.Move(selectedIndex, targetIndex);
        UpdateRowPriorities();
        ModsListView.SelectedIndex = targetIndex;
        SaveProfileFromRows();
    }

    private void SaveProfileFromRows()
    {
        if (_currentProfile is null)
        {
            return;
        }

        if (_mods.Count == 0 && _currentProfile.Mods.Count > 0 && _libraryEntries.Count > 0)
        {
            return;
        }

        UpdateRowPriorities();
        var profile = CreateProfileFromRows();
        _profileService.SaveProfile(_managerPaths, profile);
        _currentProfile = profile;
        ModsListView.Items.Refresh();
        RefreshProfileSummary();
        UpdateToggleAllModsButton();
        ShowSelectedMod(ModsListView.SelectedItem as ModRow);
    }

    private void UpdateToggleAllModsButton()
    {
        var hasMods = _mods.Count > 0;
        ToggleAllModsButton.IsEnabled = hasMods;
        ToggleAllModsButton.Content = hasMods && _mods.All(mod => mod.IsEnabled)
            ? "Disable All"
            : "Enable All";
    }

    private void ActivateProfile(string profileId)
    {
        _settings = _settings with { ActiveProfileId = profileId };
        _settingsService.Save(_managerPaths, _settings);
        LoadMods();
    }

    private string CreateSuggestedProfileName(string baseName)
    {
        var existingNames = _profiles
            .Select(profile => profile.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!existingNames.Contains(baseName))
        {
            return baseName;
        }

        var suffix = 2;
        string candidate;
        do
        {
            candidate = $"{baseName} {suffix}";
            suffix++;
        }
        while (existingNames.Contains(candidate));

        return candidate;
    }

    private void UpdateRowPriorities()
    {
        for (var index = 0; index < _mods.Count; index++)
        {
            _mods[index].Priority = index;
        }
    }

    private ModProfile CreateProfileFromRows()
    {
        var currentProfile = _currentProfile ?? ModProfile.CreateDefault(_managerPaths.RootPath);
        return currentProfile with
        {
            Mods = _mods
                .Select((row, index) => new ProfileModEntry(row.Id, row.IsEnabled, index))
                .ToArray()
        };
    }

    private void RefreshProfileSummary()
    {
        if (_currentProfile is null)
        {
            ProfileStatusText.Text = "No profile: no mods";
            ActiveProfileNameText.Text = "No profile";
            ActiveProfileSummaryText.Text = "No active profile loaded";
            ActiveProfileDeployText.Text = string.Empty;
            InstalledCountText.Text = "0";
            FileCountText.Text = "0";
            WarningCountText.Text = "0";
            WarningSummaryText.Text = "No active profile";
            ViewWarningsButton.IsEnabled = false;
            ConflictCountText.Text = "0";
            ConflictSummaryText.Text = "No active profile";
            ViewConflictsButton.IsEnabled = false;
            OverlayListView.ItemsSource = Array.Empty<OverlayRow>();
            RefreshVirtualLaunchStatus();
            return;
        }

        var overlayPreview = BuildCurrentOverlayPreview();
        if (overlayPreview is null)
        {
            WarningSummaryText.Text = "Overlay unavailable";
            ViewWarningsButton.IsEnabled = false;
            ConflictSummaryText.Text = "Overlay unavailable";
            ViewConflictsButton.IsEnabled = false;
            return;
        }

        var warningCount = _mods.Sum(mod => mod.WarningCount)
            + overlayPreview.Warnings.Count
            + overlayPreview.MissingSources.Count;

        ProfileStatusText.Text = $"{_currentProfile.Name}: {_mods.Count(mod => mod.IsEnabled)}/{_mods.Count} mods enabled, {overlayPreview.ActiveEntries.Count} active files";
        ActiveProfileNameText.Text = _currentProfile.Name;
        ActiveProfileSummaryText.Text = $"{_mods.Count(mod => mod.IsEnabled)}/{_mods.Count} mods enabled, {overlayPreview.ActiveEntries.Count} active files ready for deploy";
        ActiveProfileDeployText.Text = BuildActiveProfileDeployText(_currentProfile.Id);
        InstalledCountText.Text = _mods.Count.ToString();
        FileCountText.Text = overlayPreview.ActiveEntries.Count.ToString();
        WarningCountText.Text = warningCount.ToString();
        WarningSummaryText.Text = warningCount == 0
            ? "No warnings"
            : $"{warningCount} issue(s) need review";
        ViewWarningsButton.IsEnabled = warningCount > 0;
        ConflictCountText.Text = overlayPreview.Conflicts.Count.ToString();
        ConflictSummaryText.Text = overlayPreview.Conflicts.Count == 0
            ? "No overwritten files"
            : $"{overlayPreview.Conflicts.Count} target file(s) overwritten";
        ViewConflictsButton.IsEnabled = overlayPreview.Conflicts.Count > 0;
        OverlayListView.ItemsSource = overlayPreview.Entries
            .Select(OverlayRow.FromEntry)
            .ToArray();
        RefreshDeployStatus();
        RefreshVirtualLaunchStatus();
    }

    private string BuildActiveProfileDeployText(string activeProfileId)
    {
        var currentManifest = _profileDeployService.LoadManifest(_managerPaths, activeProfileId);
        if (currentManifest is not null)
        {
            return $"This profile is deployed: {currentManifest.Files.Count} managed files.";
        }

        var otherDeployedProfiles = GetOtherDeployedProfiles(activeProfileId)
            .Select(profile => profile.Name)
            .ToArray();
        return otherDeployedProfiles.Length == 0
            ? "No files are currently deployed for this profile."
            : $"Another profile is deployed: {string.Join(", ", otherDeployedProfiles)}. Deploy will clean it first.";
    }

    private IReadOnlyList<ProfileRow> GetOtherDeployedProfiles(string activeProfileId)
    {
        return _profiles
            .Where(profile => !profile.Id.Equals(activeProfileId, StringComparison.OrdinalIgnoreCase))
            .Where(profile =>
            {
                var manifest = _profileDeployService.LoadManifest(_managerPaths, profile.Id);
                return manifest is not null && PathsEqual(manifest.GameRootPath, GetOverlayGameRootPath());
            })
            .ToArray();
    }

    private void RefreshProfilePageStatus()
    {
        if (_currentProfile is null)
        {
            ProfilePageStatusText.Text = "No active profile";
            ProfileInstalledCountText.Text = "0";
            ProfileEnabledCountText.Text = "0";
            ProfileDeployedCountText.Text = "0";
            ProfileNameTextBox.Text = string.Empty;
            ProfileDetailsListBox.ItemsSource = Array.Empty<string>();
            return;
        }

        var manifest = _profileDeployService.LoadManifest(_managerPaths, _currentProfile.Id);
        ProfilePageStatusText.Text = $"Active profile: {_currentProfile.Name}";
        ProfileInstalledCountText.Text = _mods.Count.ToString();
        ProfileEnabledCountText.Text = _mods.Count(mod => mod.IsEnabled).ToString();
        ProfileDeployedCountText.Text = (manifest?.Files.Count ?? 0).ToString();
        ProfileDetailsListBox.ItemsSource = new[]
        {
            $"ID: {_currentProfile.Id}",
            $"Profile BepInEx state: {_currentProfile.ProfileBepInExPath}",
            manifest is null
                ? "Deploy state: clean"
                : $"Deploy state: {manifest.Files.Count} managed files, updated {manifest.UpdatedAt.LocalDateTime:g}",
            _settings.VirtualizationEnabled
                ? "Virtual launch: enabled globally"
                : "Virtual launch: disabled globally",
            _currentProfile.Virtualization.UseExperimentalVirtualizedLaunch
                ? "Virtual launch: enabled for this profile"
                : "Virtual launch: disabled for this profile",
            _currentProfile.Virtualization.RedirectWritesToProfileState
                ? "Virtual writes: profile state"
                : "Virtual writes: game default",
            $"Manager storage: {_managerPaths.RootPath}"
        };
    }

    private void ShowSelectedMod(ModRow? selectedMod)
    {
        if (selectedMod is null)
        {
            SelectedModNameText.Text = "No mod selected";
            SelectedModIdText.Text = string.Empty;
            SelectedModAuthorText.Text = string.Empty;
            OpenSelectedModNexusButton.IsEnabled = false;
            OpenSelectedModNexusButton.ToolTip = null;
            SelectedModImage.Source = null;
            SelectedModLargeImage.Source = null;
            SelectedNexusVersionText.Text = string.Empty;
            SelectedNexusDownloadsText.Text = string.Empty;
            SelectedNexusStatsText.Text = string.Empty;
            SelectedNexusDescriptionText.Text = string.Empty;
            ResetSelectedNexusFiles("Select a Nexus-linked mod to see available files.");
            SelectedFileCountText.Text = "0";
            SelectedPluginCountText.Text = "0";
            SelectedContentCountText.Text = "0";
            SelectedWarningCountText.Text = "0";
            DependenciesListView.ItemsSource = Array.Empty<DependencyRow>();
            WarningsListBox.ItemsSource = Array.Empty<string>();
            return;
        }

        SelectedModNameText.Text = selectedMod.DisplayName;
        SelectedModIdText.Text = $"{selectedMod.Id} - version {selectedMod.Version} - {selectedMod.SourceStatus} - {(selectedMod.IsEnabled ? "enabled" : "disabled")} - order {selectedMod.Priority}";
        SelectedModAuthorText.Text = string.IsNullOrWhiteSpace(selectedMod.Author)
            ? string.Empty
            : $"Author: {selectedMod.Author}";
        OpenSelectedModNexusButton.IsEnabled = !string.IsNullOrWhiteSpace(selectedMod.PageUrl);
        OpenSelectedModNexusButton.ToolTip = string.IsNullOrWhiteSpace(selectedMod.PageUrl)
            ? "This mod is not linked to Nexus."
            : selectedMod.PageUrl;
        SetSelectedModImages(selectedMod.IconUrl, selectedMod.LargeImageUrl);
        SelectedNexusVersionText.Text = string.IsNullOrWhiteSpace(selectedMod.NexusVersion)
            ? "unknown"
            : selectedMod.NexusVersion;
        SelectedNexusDownloadsText.Text = FormatNullableCount(selectedMod.TotalDownloads);
        SelectedNexusStatsText.Text = BuildNexusStatsText(selectedMod);
        SelectedNexusDescriptionText.Text = CleanNexusDescription(selectedMod.Description);
        ShowSelectedNexusFiles(selectedMod);
        SelectedFileCountText.Text = selectedMod.FileCount.ToString();
        SelectedPluginCountText.Text = selectedMod.PluginCount.ToString();
        SelectedContentCountText.Text = selectedMod.ContentFileCount.ToString();
        SelectedWarningCountText.Text = selectedMod.WarningCount.ToString();
        DependenciesListView.ItemsSource = selectedMod.Dependencies;
        WarningsListBox.ItemsSource = selectedMod.Warnings
            .Where(warning => !string.IsNullOrWhiteSpace(warning))
            .Select(FormatWarningForDisplay)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void ShowSelectedNexusFiles(ModRow selectedMod)
    {
        if (!CanUseNexusFiles(selectedMod))
        {
            ResetSelectedNexusFiles("Link this mod to Nexus before loading file variants.");
            return;
        }

        RefreshNexusFilesButton.IsEnabled = true;
        OpenSelectedModFilesPageButton.IsEnabled = true;
        DownloadSelectedNexusFileButton.IsEnabled = false;

        var fingerprint = BuildNexusFilesFingerprint(selectedMod);
        var cached = _nexusModFilesService.TryLoadCached(
            _managerPaths,
            selectedMod.GameDomain,
            selectedMod.NexusModId!.Value,
            fingerprint);
        if (cached is null)
        {
            NexusFilesListView.ItemsSource = Array.Empty<NexusFileRow>();
            SelectedNexusFilesStatusText.Text = "No cached file list yet. Run Check Updates or Refresh Files.";
            return;
        }

        ShowNexusFilesResult(cached);
    }

    private void ShowNexusFilesResult(NexusModFilesLoadResult result)
    {
        NexusFilesListView.ItemsSource = result.Files
            .Select(NexusFileRow.FromFile)
            .ToArray();
        var source = result.IsFromCache ? "Cached" : "Refreshed";
        SelectedNexusFilesStatusText.Text = $"{source}: {result.Files.Count} files, {result.CachedAt.LocalDateTime:g}.";
        DownloadSelectedNexusFileButton.IsEnabled = false;
    }

    private void ResetSelectedNexusFiles(string statusText)
    {
        NexusFilesListView.ItemsSource = Array.Empty<NexusFileRow>();
        SelectedNexusFilesStatusText.Text = statusText;
        RefreshNexusFilesButton.IsEnabled = false;
        DownloadSelectedNexusFileButton.IsEnabled = false;
        OpenSelectedModFilesPageButton.IsEnabled = false;
    }

    private void NexusFilesListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        DownloadSelectedNexusFileButton.IsEnabled = NexusFilesListView.SelectedItem is NexusFileRow
            && ModsListView.SelectedItem is ModRow selectedMod
            && CanUseNexusFiles(selectedMod);
    }

    private async void RefreshNexusFiles_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetSelectedNexusMod(out var selectedMod))
        {
            return;
        }

        var apiKey = GetConfiguredNexusApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            ShowNexusFilesApiKeyRequired(selectedMod);
            return;
        }

        RefreshNexusFilesButton.IsEnabled = false;
        SelectedNexusFilesStatusText.Text = "Refreshing Nexus file list...";
        try
        {
            var result = await _nexusModFilesService.LoadOrRefreshAsync(
                _managerPaths,
                selectedMod.GameDomain,
                selectedMod.NexusModId!.Value,
                BuildNexusFilesFingerprint(selectedMod),
                apiKey,
                forceRefresh: true);
            ShowNexusFilesResult(result);
        }
        catch (Exception exception) when (exception is HttpRequestException
            or TaskCanceledException
            or JsonException
            or IOException
            or InvalidOperationException
            or UnauthorizedAccessException)
        {
            SelectedNexusFilesStatusText.Text = "Nexus file refresh failed.";
            OfferManualFallbackForSelectedFiles(selectedMod, exception.Message);
        }
        finally
        {
            RefreshNexusFilesButton.IsEnabled = CanUseNexusFiles(selectedMod);
        }
    }

    private async void DownloadSelectedNexusFile_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetSelectedNexusMod(out var selectedMod)
            || NexusFilesListView.SelectedItem is not NexusFileRow selectedFile)
        {
            MessageBox.Show(this, "Select a Nexus file first.", "No file selected", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var apiKey = GetConfiguredNexusApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            ShowNexusFilesApiKeyRequired(selectedMod);
            return;
        }

        var answer = MessageBox.Show(
            this,
            $"Download and install this Nexus file?\n\n{selectedMod.Name}\n{selectedFile.FileName}\nVersion: {selectedFile.Version}",
            "Download Nexus File",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        var downloadRootPath = Path.Combine(_managerPaths.DownloadsPath, "nexus-files");
        DownloadSelectedNexusFileButton.IsEnabled = false;
        SelectedNexusFilesStatusText.Text = "Downloading selected Nexus file...";
        try
        {
            var source = BuildSelectedNexusFileSource(selectedMod, selectedFile);
            var download = await _nexusModDownloadService.DownloadUpdateArchiveAsync(
                source,
                selectedFile.FileId,
                apiKey,
                downloadRootPath,
                BuildNexusFileArchiveName(selectedMod, selectedFile));
            SelectedNexusFilesStatusText.Text = "Download complete. Installing archive...";
            InstallModArchives(new[] { download.ArchivePath });
        }
        catch (Exception exception) when (exception is HttpRequestException
            or TaskCanceledException
            or JsonException
            or IOException
            or InvalidOperationException
            or UnauthorizedAccessException)
        {
            SelectedNexusFilesStatusText.Text = "Nexus download failed.";
            OfferManualFallbackForSelectedFiles(selectedMod, exception.Message);
        }
        finally
        {
            DownloadSelectedNexusFileButton.IsEnabled = NexusFilesListView.SelectedItem is NexusFileRow
                && CanUseNexusFiles(selectedMod);
        }
    }

    private void OpenSelectedModFilesPage_Click(object sender, RoutedEventArgs e)
    {
        if (TryGetSelectedNexusMod(out var selectedMod))
        {
            OpenNexusFilesPage(selectedMod.GameDomain, selectedMod.NexusModId!.Value);
        }
    }

    private bool TryGetSelectedNexusMod(out ModRow selectedMod)
    {
        if (ModsListView.SelectedItem is not ModRow mod)
        {
            selectedMod = null!;
            MessageBox.Show(this, "Select a mod first.", "No mod selected", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        selectedMod = mod;
        if (!CanUseNexusFiles(selectedMod))
        {
            MessageBox.Show(this, "Select a Nexus-linked mod first.", "No Nexus mod selected", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        return true;
    }

    private static bool CanUseNexusFiles(ModRow selectedMod)
    {
        return selectedMod.NexusModId is not null
            && !string.IsNullOrWhiteSpace(selectedMod.GameDomain);
    }

    private void ShowNexusFilesApiKeyRequired(ModRow selectedMod)
    {
        var answer = MessageBox.Show(
            this,
            "Nexus API key is required to load or download file variants.\n\nIf Nexus refuses automatic downloads, the account may need Nexus Premium.\n\nOpen the Nexus files page manually?",
            "Nexus API key required",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information);
        if (answer == MessageBoxResult.Yes)
        {
            OpenNexusFilesPage(selectedMod.GameDomain, selectedMod.NexusModId!.Value);
        }
    }

    private void OfferManualFallbackForSelectedFiles(ModRow selectedMod, string errorMessage)
    {
        var answer = MessageBox.Show(
            this,
            $"Nexus request failed:\n\n{errorMessage}\n\nAutomatic downloads may require Nexus Premium for this account.\n\nOpen the Nexus files page manually?",
            "Manual Nexus Download",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer == MessageBoxResult.Yes)
        {
            OpenNexusFilesPage(selectedMod.GameDomain, selectedMod.NexusModId!.Value);
        }
    }

    private ModSourceInfo BuildSelectedNexusFileSource(ModRow selectedMod, NexusFileRow selectedFile)
    {
        var entry = _libraryEntries.FirstOrDefault(libraryEntry => libraryEntry.Mod.Id.Equals(selectedMod.Id, StringComparison.OrdinalIgnoreCase));
        var source = entry?.Manifest.Source;
        var fallbackSource = new ModSourceInfo(
            "NexusMods",
            selectedMod.GameDomain,
            selectedMod.NexusModId,
            selectedFile.FileId,
            selectedFile.Version,
            selectedFile.UploadedAt,
            selectedFile.FileName);

        return (source ?? fallbackSource) with
        {
            GameDomain = selectedMod.GameDomain,
            ModId = selectedMod.NexusModId,
            FileId = selectedFile.FileId,
            FileVersion = selectedFile.Version,
            FileTimestamp = selectedFile.UploadedAt,
            SourceArchiveFileName = selectedFile.FileName
        };
    }

    private static string BuildNexusFileArchiveName(ModRow selectedMod, NexusFileRow selectedFile)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return $"{selectedMod.Name}-{selectedMod.NexusModId}-{selectedFile.FileId}-{timestamp}.zip";
    }

    private void OpenSelectedModNexus_Click(object sender, RoutedEventArgs e)
    {
        if (ModsListView.SelectedItem is not ModRow selectedMod
            || string.IsNullOrWhiteSpace(selectedMod.PageUrl))
        {
            return;
        }

        OpenUri(selectedMod.PageUrl, "Open Nexus page failed");
    }

    private void SetSelectedModImages(string? iconUrl, string? largeImageUrl)
    {
        var requestId = ++_selectedImageRequestId;
        SelectedModImage.Source = null;
        SelectedModLargeImage.Source = null;
        _ = LoadSelectedModImageAsync(SelectedModImage, iconUrl, requestId);
        _ = LoadSelectedModImageAsync(SelectedModLargeImage, largeImageUrl ?? iconUrl, requestId);
    }

    private async Task LoadSelectedModImageAsync(Image target, string? imageUrl, int requestId)
    {
        if (string.IsNullOrWhiteSpace(imageUrl)
            || !Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri))
        {
            return;
        }

        if (_imageCache.TryGetValue(uri.AbsoluteUri, out var cachedImage))
        {
            if (requestId == _selectedImageRequestId)
            {
                target.Source = cachedImage;
            }

            return;
        }

        try
        {
            var image = await DownloadBitmapImageAsync(uri);
            if (image is null)
            {
                return;
            }

            CacheImage(uri.AbsoluteUri, image);
            if (requestId == _selectedImageRequestId)
            {
                target.Source = image;
            }
        }
        catch (Exception exception) when (exception is NotSupportedException
            or IOException
            or InvalidDataException
            or InvalidOperationException
            or HttpRequestException
            or TaskCanceledException
            or ObjectDisposedException)
        {
            if (requestId == _selectedImageRequestId)
            {
                target.Source = null;
            }
        }
    }

    private async Task<BitmapImage?> DownloadBitmapImageAsync(Uri uri)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("UCU-ModManager", "0.1"));
        using var response = await _imageHttpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var contentLength = response.Content.Headers.ContentLength;
        if (contentLength > MaxImageDownloadBytes)
        {
            return null;
        }

        await using var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var memory = contentLength is > 0 and <= MaxImageDownloadBytes
            ? new MemoryStream((int)contentLength.Value)
            : new MemoryStream();
        await CopyImageStreamToMemoryAsync(source, memory).ConfigureAwait(false);
        memory.Position = 0;

        var image = new BitmapImage();
        image.BeginInit();
        image.StreamSource = memory;
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static async Task CopyImageStreamToMemoryAsync(Stream source, MemoryStream destination)
    {
        var buffer = new byte[ImageDownloadBufferSize];
        while (true)
        {
            var read = await source.ReadAsync(buffer).ConfigureAwait(false);
            if (read == 0)
            {
                return;
            }

            if (destination.Length + read > MaxImageDownloadBytes)
            {
                throw new InvalidDataException("Image download exceeded the allowed size.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
        }
    }

    private void CacheImage(string cacheKey, BitmapImage image)
    {
        var isNewEntry = !_imageCache.ContainsKey(cacheKey);
        _imageCache[cacheKey] = image;
        if (isNewEntry)
        {
            _imageCacheOrder.Enqueue(cacheKey);
        }

        while (_imageCache.Count > MaxImageCacheEntries && _imageCacheOrder.Count > 0)
        {
            var oldestKey = _imageCacheOrder.Dequeue();
            _imageCache.Remove(oldestKey);
        }
    }

    private static string BuildNexusStatsText(ModRow selectedMod)
    {
        var parts = new[]
        {
            $"Endorsements: {FormatNullableCount(selectedMod.Endorsements)}",
            $"Unique downloads: {FormatNullableCount(selectedMod.UniqueDownloads)}",
            $"Views: {FormatNullableCount(selectedMod.TotalViews)}"
        };
        return string.Join("   ", parts);
    }

    private static string FormatNullableCount(int? value)
    {
        return value is null
            ? "unknown"
            : value.Value.ToString("N0");
    }

    private static string CleanNexusDescription(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return "No Nexus description is available.";
        }

        var text = WebUtility.HtmlDecode(description);
        text = Regex.Replace(text, @"(?i)<br\s*/?>", "\n");
        text = Regex.Replace(text, @"(?i)</p\s*>", "\n\n");
        text = Regex.Replace(text, @"(?i)<[^>]+>", " ");
        text = Regex.Replace(text, @"(?is)\[img\].*?\[/img\]", " ");
        text = Regex.Replace(text, @"(?is)\[url=(?<url>[^\]]+)\](?<label>.*?)\[/url\]", "${label} (${url})");
        text = Regex.Replace(text, @"(?is)\[/?(?:b|i|u|size|font|color|list|spoiler|heading|line|code)(?:=[^\]]*)?\]", " ");
        text = Regex.Replace(text, @"(?is)\[\*\]|\[/\*\]", "- ");
        text = Regex.Replace(text, @"[ \t]+\n", "\n");
        text = Regex.Replace(text, @"\n{3,}", "\n\n");
        text = Regex.Replace(text, @"[ \t]{2,}", " ");
        return text.Trim();
    }

    private void RefreshSettingsStatus()
    {
        _isLoadingSettings = true;
        try
        {
            AutoLinkNexusOnStartupCheckBox.IsChecked = _settings.AutoLinkNexusOnStartup;
            ShowAdvancedModColumnsCheckBox.IsChecked = _settings.ShowAdvancedModColumns;
            VirtualizationEnabledCheckBox.IsChecked = _settings.VirtualizationEnabled;
            ExperimentalVirtualizedLaunchCheckBox.IsEnabled = _settings.VirtualizationEnabled && _currentProfile is not null;
            ExperimentalVirtualizedLaunchCheckBox.IsChecked = _currentProfile?.Virtualization.UseExperimentalVirtualizedLaunch == true;
            RedirectVirtualWritesCheckBox.IsEnabled = _settings.VirtualizationEnabled && _currentProfile is not null;
            RedirectVirtualWritesCheckBox.IsChecked = _currentProfile?.Virtualization.RedirectWritesToProfileState ?? true;
            ApplyModTableColumnSettings();
            NexusGameDomainTextBox.Text = _settings.NexusGameDomain;
            var hasKey = _secureSecretStore.HasNexusApiKey(_managerPaths);
            NexusApiKeyStatusText.Text = hasKey
                ? $"Nexus API key is saved. Domain: {_settings.NexusGameDomain}."
                : $"No Nexus API key saved. Domain: {_settings.NexusGameDomain}.";
            NexusApiKeyStatusText.Foreground = (Brush)FindResource(hasKey
                ? "AccentBrush"
                : "MutedTextBrush");
            RefreshNexusMetadataStatusText();
            RefreshVirtualLaunchStatus();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            NexusApiKeyStatusText.Text = exception.Message;
            NexusApiKeyStatusText.Foreground = (Brush)FindResource("DangerBrush");
        }
        finally
        {
            _isLoadingSettings = false;
        }
    }

    private void ApplyModTableColumnSettings()
    {
        var advancedWidth = _settings.ShowAdvancedModColumns;
        ModFilesColumn.Width = advancedWidth ? ModFilesColumnWidth : HiddenModColumnWidth;
        ModPluginsColumn.Width = advancedWidth ? ModPluginsColumnWidth : HiddenModColumnWidth;
        ModContentColumn.Width = advancedWidth ? ModContentColumnWidth : HiddenModColumnWidth;
        ModConfigsColumn.Width = advancedWidth ? ModConfigsColumnWidth : HiddenModColumnWidth;
        UpdateDisplayedModNames();
        ModsListView.Items.Refresh();
        RefreshModpackExportProfilePreview();
        if (_importedUcuModpack is not null)
        {
            ShowImportedUcuModpack(_importedUcuModpack, _importedUcuModpackPath);
        }
    }

    private void UpdateDisplayedModNames()
    {
        foreach (var mod in _mods)
        {
            mod.DisplayName = BuildDisplayModName(mod.Name, mod.SourceDisplayName, _settings.ShowAdvancedModColumns);
        }
    }

    private static string BuildDisplayModName(string name, string? sourceDisplayName, bool showAdvancedName)
    {
        if (showAdvancedName)
        {
            return string.IsNullOrWhiteSpace(name) ? "Unnamed mod" : name.Trim();
        }

        var candidate = FirstNonEmpty(sourceDisplayName, name) ?? "Unnamed mod";
        var cleaned = CleanArchiveStyleModName(candidate);
        return string.IsNullOrWhiteSpace(cleaned) ? candidate.Trim() : cleaned;
    }

    private static string CleanArchiveStyleModName(string name)
    {
        var cleaned = name.Trim();
        cleaned = Regex.Replace(
            cleaned,
            @"(?i)\s+\d+\s+v?\d+(?:[.-]\d+)*(?:-[A-Za-z][A-Za-z0-9.-]*)?\s+[A-Za-z0-9]{4,}$",
            string.Empty);
        cleaned = Regex.Replace(
            cleaned,
            @"(?i)-\d+-v?\d+(?:-\d+)+(?:-for-\d+(?:-\d+)*)?-\d+$",
            string.Empty);
        cleaned = Regex.Replace(
            cleaned,
            @"(?i)(?:[ _.-]+v?\d+(?:\.\d+)+(?:-[A-Za-z][A-Za-z0-9.-]*)?|v\d+(?:\.\d+)+)$",
            string.Empty);
        cleaned = Regex.Replace(cleaned, @"(?i)[ _-]+\d{2,}$", string.Empty);
        return cleaned.Replace('_', ' ').Trim(' ', '.', '-', '_');
    }

    private void RefreshNexusMetadataStatusText(NexusMetadataCatalogStatus? status = null)
    {
        try
        {
            status ??= _nexusMetadataCatalogService.GetStatus(_managerPaths);
            NexusMetadataStatusText.Text =
                $"Downloaded: {FormatLocalDateTime(status.LastDownloadedAt)}. " +
                $"Metadata: {FormatLocalDateTime(status.CatalogLastModifiedAt)}. " +
                $"Checked: {FormatLocalDateTime(status.LastAttemptedAt)}. " +
                $"Mods: {status.EntryCount}.";

            if (!string.IsNullOrWhiteSpace(status.LastError))
            {
                NexusMetadataStatusText.Text += $" Last error: {status.LastError}";
            }

            NexusMetadataStatusText.Foreground = (Brush)FindResource(!string.IsNullOrWhiteSpace(status.LastError)
                ? "DangerBrush"
                : status.LastDownloadedAt is not null
                    ? "AccentBrush"
                    : "MutedTextBrush");
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            NexusMetadataStatusText.Text = $"Metadata status unavailable: {exception.Message}";
            NexusMetadataStatusText.Foreground = (Brush)FindResource("DangerBrush");
        }
    }

    private static string FormatLocalDateTime(DateTimeOffset? value)
    {
        return value is null
            ? "unknown"
            : value.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    }

    private void AutoLinkNexusOnStartupCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings)
        {
            return;
        }

        var autoLinkOnStartup = AutoLinkNexusOnStartupCheckBox.IsChecked == true;
        if (_settings.AutoLinkNexusOnStartup == autoLinkOnStartup)
        {
            return;
        }

        try
        {
            _settings = _settings with { AutoLinkNexusOnStartup = autoLinkOnStartup };
            _settingsService.Save(_managerPaths, _settings);
            RefreshSettingsStatus();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, exception.Message, "Save settings failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            RefreshSettingsStatus();
        }
    }

    private void ShowAdvancedModColumnsCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings)
        {
            return;
        }

        var showAdvancedColumns = ShowAdvancedModColumnsCheckBox.IsChecked == true;
        if (_settings.ShowAdvancedModColumns == showAdvancedColumns)
        {
            return;
        }

        try
        {
            _settings = _settings with { ShowAdvancedModColumns = showAdvancedColumns };
            _settingsService.Save(_managerPaths, _settings);
            ApplyModTableColumnSettings();
            ShowSelectedMod(ModsListView.SelectedItem as ModRow);
            RefreshSettingsStatus();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, exception.Message, "Save settings failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            RefreshSettingsStatus();
        }
    }

    private void VirtualizationEnabledCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings)
        {
            return;
        }

        var enabled = VirtualizationEnabledCheckBox.IsChecked == true;
        if (_settings.VirtualizationEnabled == enabled)
        {
            return;
        }

        try
        {
            _settings = _settings with { VirtualizationEnabled = enabled };
            _settingsService.Save(_managerPaths, _settings);
            RefreshSettingsStatus();
            RefreshProfilePageStatus();
            RefreshVirtualLaunchStatus();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, exception.Message, "Save settings failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            RefreshSettingsStatus();
        }
    }

    private void ExperimentalVirtualizedLaunchCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings)
        {
            return;
        }

        if (_currentProfile is null)
        {
            RefreshSettingsStatus();
            return;
        }

        var enabled = ExperimentalVirtualizedLaunchCheckBox.IsChecked == true;
        if (_currentProfile.Virtualization.UseExperimentalVirtualizedLaunch == enabled)
        {
            return;
        }

        try
        {
            _currentProfile = _currentProfile with
            {
                Virtualization = _currentProfile.Virtualization with
                {
                    UseExperimentalVirtualizedLaunch = enabled
                }
            };
            _profileService.SaveProfile(_managerPaths, _currentProfile);
            RefreshVirtualLaunchStatus();
            RefreshProfilePageStatus();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, exception.Message, "Save profile settings failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            RefreshSettingsStatus();
        }
    }

    private void RedirectVirtualWritesCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings)
        {
            return;
        }

        if (_currentProfile is null)
        {
            RefreshSettingsStatus();
            return;
        }

        var enabled = RedirectVirtualWritesCheckBox.IsChecked == true;
        if (_currentProfile.Virtualization.RedirectWritesToProfileState == enabled)
        {
            return;
        }

        try
        {
            _currentProfile = _currentProfile with
            {
                Virtualization = _currentProfile.Virtualization with
                {
                    RedirectWritesToProfileState = enabled
                }
            };
            _profileService.SaveProfile(_managerPaths, _currentProfile);
            RefreshVirtualLaunchStatus();
            RefreshProfilePageStatus();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, exception.Message, "Save profile settings failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            RefreshSettingsStatus();
        }
    }

    private string? GetConfiguredNexusApiKey()
    {
        try
        {
            return _secureSecretStore.LoadNexusApiKey(_managerPaths)
                ?? Environment.GetEnvironmentVariable("NEXUSMODS_API_KEY");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            MessageBox.Show(this, exception.Message, "Nexus API key unavailable", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
    }

    private static async Task<string?> TestNexusApiKey(string apiKey)
    {
        using var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.nexusmods.com/v1/users/validate.json");
        request.Headers.Add("apikey", apiKey);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("UCU-ModManager", "0.1"));
        using var response = await client.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Nexus returned {(int)response.StatusCode} {response.ReasonPhrase}.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var document = await JsonDocument.ParseAsync(stream);
        if (document.RootElement.TryGetProperty("name", out var nameElement)
            && nameElement.ValueKind == JsonValueKind.String)
        {
            return nameElement.GetString();
        }

        return null;
    }

    private void RefreshSetupStatus()
    {
        var release = BepInExRelease.Current;
        if (string.IsNullOrWhiteSpace(_settings.GameRootPath))
        {
            GameRootText.Text = "No game folder selected";
            SetStatus(GameStatusText, "Game: not configured", "WarningBrush");
            SetStatus(BepInExStatusText, "BepInEx: waiting for a valid game folder", "MutedTextBrush");
            BepInExDetailText.Text = $"Expected BepInEx {release.Version}";
            RefreshDeployStatus();
            RefreshVirtualLaunchStatus();
            return;
        }

        var validation = _gameValidator.Validate(_settings.GameRootPath);
        GameRootText.Text = validation.GameRootPath;
        if (!validation.IsValid)
        {
            SetStatus(GameStatusText, $"Game: invalid folder. Missing: {string.Join(", ", validation.MissingMarkers)}", "DangerBrush");
            SetStatus(BepInExStatusText, "BepInEx: select a valid game folder first", "MutedTextBrush");
            BepInExDetailText.Text = $"Expected BepInEx {release.Version}";
            RefreshDeployStatus();
            RefreshVirtualLaunchStatus();
            return;
        }

        var gameWarnings = validation.Warnings.Count == 0
            ? string.Empty
            : $" Warnings: {string.Join("; ", validation.Warnings)}";
        SetStatus(GameStatusText, $"Game: valid Casualties Unknown Demo folder.{gameWarnings}", "AccentBrush");

        var state = _bepInExProbe.Probe(validation.GameRootPath);
        if (state.IsComplete)
        {
            SetStatus(BepInExStatusText, "BepInEx: installed", "AccentBrush");
            BepInExDetailText.Text = $"Game BepInEx is installed. Expected version: {release.Version}.";
            RefreshDeployStatus();
            RefreshVirtualLaunchStatus();
            return;
        }

        if (state.IsInstalled)
        {
            SetStatus(BepInExStatusText, "BepInEx: incomplete, repair recommended", "WarningBrush");
            BepInExDetailText.Text = $"Missing: {string.Join(", ", state.MissingMarkers)}";
            RefreshDeployStatus();
            RefreshVirtualLaunchStatus();
            return;
        }

        SetStatus(BepInExStatusText, "BepInEx: not installed", "WarningBrush");
        BepInExDetailText.Text = $"Install from ZIP or download {release.ArchiveFileName}.";
        RefreshDeployStatus();
        RefreshVirtualLaunchStatus();
    }

    private void RefreshDeployStatus()
    {
        var (baseGameState, baseGameStateBrush) = GetBaseGameState();
        if (_currentProfile is null)
        {
            SetStatus(DeployStatusText, $"Game state: {baseGameState}", baseGameStateBrush);
            DeployDetailText.Text = "Profile deployment controls need an active profile.";
            return;
        }

        var manifest = _profileDeployService.LoadManifest(_managerPaths, _currentProfile.Id);
        if (manifest is null)
        {
            var otherDeployedProfiles = GetOtherDeployedProfiles(_currentProfile.Id);
            if (otherDeployedProfiles.Count > 0)
            {
                SetStatus(DeployStatusText, $"Game state: {baseGameState} + deployed profile files", "WarningBrush");
                DeployDetailText.Text = $"A different profile is physically deployed. Deploy will clear {string.Join(", ", otherDeployedProfiles.Select(profile => profile.Name))} before copying this profile.";
                return;
            }

            SetStatus(DeployStatusText, $"Game state: {baseGameState}", baseGameStateBrush);
            DeployDetailText.Text = "No copied profile files are currently managed in the game folder.";
            return;
        }

        SetStatus(DeployStatusText, $"Game state: {baseGameState} + {manifest.Files.Count} deployed files", "WarningBrush");
        var gameRootNote = string.IsNullOrWhiteSpace(_settings.GameRootPath)
            || manifest.GameRootPath.Equals(_settings.GameRootPath, StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : " Game path differs from current settings.";
        DeployDetailText.Text = $"Last updated {manifest.UpdatedAt.LocalDateTime:g}.{gameRootNote}";
    }

    private (string Text, string BrushResourceKey) GetBaseGameState()
    {
        if (string.IsNullOrWhiteSpace(_settings.GameRootPath))
        {
            return ("not configured", "WarningBrush");
        }

        var validation = _gameValidator.Validate(_settings.GameRootPath);
        if (!validation.IsValid)
        {
            return ("invalid folder", "DangerBrush");
        }

        var state = _bepInExProbe.Probe(validation.GameRootPath);
        if (state.IsComplete)
        {
            return ("Vanilla + BepInEx", "AccentBrush");
        }

        if (state.IsInstalled)
        {
            return ("Vanilla + incomplete BepInEx", "WarningBrush");
        }

        return ("Vanilla", "AccentBrush");
    }

    private void RefreshVirtualLaunchStatus()
    {
        SetVirtualLaunchButtonCleanupRisk(false);
        if (_currentProfile is null)
        {
            VirtualLaunchButton.IsEnabled = false;
            SetStatus(VirtualLaunchStatusText, "Virtual launch: no active profile", "MutedTextBrush");
            return;
        }

        if (!_settings.VirtualizationEnabled)
        {
            VirtualLaunchButton.IsEnabled = false;
            SetStatus(VirtualLaunchStatusText, "Virtual launch: disabled in Settings", "MutedTextBrush");
            return;
        }

        if (!_currentProfile.Virtualization.UseExperimentalVirtualizedLaunch)
        {
            VirtualLaunchButton.IsEnabled = false;
            SetStatus(VirtualLaunchStatusText, "Virtual launch: disabled for this profile", "MutedTextBrush");
            return;
        }

        if (string.IsNullOrWhiteSpace(_settings.GameRootPath))
        {
            VirtualLaunchButton.IsEnabled = false;
            SetStatus(VirtualLaunchStatusText, "Virtual launch: choose a game folder first", "WarningBrush");
            return;
        }

        var validation = _gameValidator.Validate(_settings.GameRootPath);
        if (!validation.IsValid)
        {
            VirtualLaunchButton.IsEnabled = false;
            SetStatus(VirtualLaunchStatusText, "Virtual launch: invalid game folder", "DangerBrush");
            return;
        }

        var state = _bepInExProbe.Probe(validation.GameRootPath);
        if (!state.IsComplete)
        {
            VirtualLaunchButton.IsEnabled = false;
            SetStatus(VirtualLaunchStatusText, "Virtual launch: install BepInEx first", "WarningBrush");
            return;
        }

        var overlayPreview = BuildCurrentOverlayPreview();
        if (overlayPreview is null)
        {
            VirtualLaunchButton.IsEnabled = false;
            SetStatus(VirtualLaunchStatusText, "Virtual launch: profile overlay unavailable", "WarningBrush");
            return;
        }

        if (overlayPreview.MissingSources.Count > 0)
        {
            VirtualLaunchButton.IsEnabled = false;
            SetStatus(VirtualLaunchStatusText, $"Virtual launch: {overlayPreview.MissingSources.Count} missing source files", "DangerBrush");
            return;
        }

        VirtualLaunchButton.IsEnabled = true;
        var cleanupRisk = GetVirtualLaunchCleanupRisk(_currentProfile.Id, validation.GameRootPath);
        SetVirtualLaunchButtonCleanupRisk(cleanupRisk.HasRisk);
        var conflictText = overlayPreview.Conflicts.Count == 1
            ? "1 conflict"
            : $"{overlayPreview.Conflicts.Count} conflicts";
        SetStatus(
            VirtualLaunchStatusText,
            $"Virtual launch: ready, {overlayPreview.ActiveEntries.Count} mapped files, {conflictText}",
            overlayPreview.Conflicts.Count == 0 && overlayPreview.Warnings.Count == 0
                ? "AccentBrush"
                : "WarningBrush");
    }

    private void SetVirtualLaunchButtonCleanupRisk(bool hasCleanupRisk)
    {
        if (hasCleanupRisk)
        {
            VirtualLaunchButton.Background = (Brush)FindResource("PanelAltBrush");
            VirtualLaunchButton.Foreground = (Brush)FindResource("PrimaryTextBrush");
            VirtualLaunchButton.BorderBrush = (Brush)FindResource("WarningBrush");
            VirtualLaunchButton.BorderThickness = new Thickness(1.5);
            VirtualLaunchButton.ToolTip = "Clean Deploy will run before virtual launch because physical profile files are currently deployed.";
            return;
        }

        VirtualLaunchButton.Background = (Brush)FindResource("AccentBrush");
        VirtualLaunchButton.Foreground = (Brush)FindResource("AccentTextBrush");
        VirtualLaunchButton.BorderBrush = (Brush)FindResource("AccentBrush");
        VirtualLaunchButton.BorderThickness = new Thickness(1);
        VirtualLaunchButton.ToolTip = "Starts the game through the experimental virtual profile overlay. Alpha-tested feature: please report bugs.";
    }

    private GameInstallation? GetValidGameInstallationOrShowMessage()
    {
        if (string.IsNullOrWhiteSpace(_settings.GameRootPath))
        {
            MessageBox.Show(this, "Select the Casualties Unknown Demo game folder first.", "Game folder required", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }

        var validation = _gameValidator.Validate(_settings.GameRootPath);
        if (!validation.IsValid)
        {
            RefreshSetupStatus();
            MessageBox.Show(this, $"Selected folder is not a valid game folder. Missing: {string.Join(", ", validation.MissingMarkers)}", "Invalid game folder", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }

        return GameInstallation.FromRootPath(validation.GameRootPath);
    }

    private GameInstallation? GetDeploymentReadyGameOrShowMessage()
    {
        return GetBepInExReadyGameOrShowMessage("deploying profile files");
    }

    private GameInstallation? GetBepInExReadyGameOrShowMessage(string actionDescription)
    {
        var installation = GetValidGameInstallationOrShowMessage();
        if (installation is null)
        {
            return null;
        }

        var state = _bepInExProbe.Probe(installation.RootPath);
        if (!state.IsComplete)
        {
            RefreshSetupStatus();
            MessageBox.Show(this, $"Install or repair BepInEx before {actionDescription}.", "BepInEx required", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }

        return installation;
    }

    private void TryInstallBepInExArchive(string archivePath, GameInstallation installation)
    {
        try
        {
            var result = _bepInExInstaller.InstallFromArchive(archivePath, installation.RootPath);
            if (_currentProfile is not null)
            {
                _profileService.SaveProfile(_managerPaths, _currentProfile);
            }

            RefreshSetupStatus();

            var skipped = result.SkippedEntries.Count == 0
                ? string.Empty
                : $" Skipped unsafe entries: {result.SkippedEntries.Count}.";
            MessageBox.Show(this, $"Installed {result.InstalledFiles.Count} BepInEx files.{skipped}", "BepInEx installed", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            RefreshSetupStatus();
            MessageBox.Show(this, exception.Message, "BepInEx install failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private string GetOverlayGameRootPath()
    {
        return string.IsNullOrWhiteSpace(_settings.GameRootPath)
            ? _managerPaths.RootPath
            : _settings.GameRootPath;
    }

    private OverlayPreview? BuildCurrentOverlayPreview()
    {
        if (_currentProfile is null)
        {
            return null;
        }

        var profile = CreateProfileFromRows();
        var plan = _virtualizationPlanBuilder.Build(
            GetOverlayGameRootPath(),
            GameInstallation.DefaultExecutableName,
            profile,
            _libraryEntries);

        return _overlayPreviewService.Build(plan);
    }

    private void ShowDeployResult(
        string title,
        ProfileDeployResult result,
        IReadOnlyList<ProfileDeployResult>? preDeployCleanResults = null)
    {
        var message = $"Copied: {result.CopiedFiles}\nDeleted: {result.DeletedFiles}\nPreserved: {result.PreservedFiles}";
        var preDeployDeletedFiles = preDeployCleanResults?.Sum(cleanResult => cleanResult.DeletedFiles) ?? 0;
        if (preDeployDeletedFiles > 0)
        {
            message = $"Cleaned previous profile files: {preDeployDeletedFiles}\n{message}";
        }

        var warnings = result.Warnings
            .Concat(preDeployCleanResults?.SelectMany(cleanResult => cleanResult.Warnings
                .Select(warning => $"{cleanResult.ProfileId}: {warning}")) ?? Array.Empty<string>())
            .ToArray();
        if (warnings.Length > 0)
        {
            message += $"\nWarnings: {warnings.Length}\n\n{string.Join("\n", warnings.Take(8))}";
            if (warnings.Length > 8)
            {
                message += $"\n... {warnings.Length - 8} more";
            }
        }

        MessageBox.Show(this, message, title, MessageBoxButton.OK, warnings.Length == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private static string BuildVirtualLaunchStartedMessage(
        ModProfile profile,
        OverlayPreview overlayPreview,
        string planPath,
        VirtualizedGameImageBuildResult image,
        VirtualizedLaunchPlanValidationResult validation)
    {
        var validationWarnings = validation.Issues
            .Where(issue => issue.Severity == VirtualizedLaunchPlanIssueSeverity.Warning)
            .ToArray();
        var lines = new List<string>
        {
            "Virtualized launch started.",
            "The game is running from a linked profile runtime image.",
            string.Empty,
            $"Profile: {overlayPreview.ProfileId}",
            $"Mapped files: {overlayPreview.ActiveEntries.Count}",
            $"Linked game files: {image.GameFilesLinked}",
            $"Linked mod files: {image.OverlayFilesLinked}",
            $"Runtime folders: {image.DirectoriesCreated}",
            $"Conflicts: {overlayPreview.Conflicts.Count}",
            $"Warnings: {overlayPreview.Warnings.Count + image.Warnings.Count}",
            $"Plan validation: {(validationWarnings.Length == 0 ? "passed" : $"{validationWarnings.Length} warnings")}",
            $"Write redirect: {(profile.Virtualization.RedirectWritesToProfileState ? "profile state" : "game default")}",
            $"Virtual game root: {image.VirtualGameRootPath}",
            $"Plan: {planPath}",
            string.Empty,
            "BepInEx stays installed in the real game folder. Mod files remain in manager storage and are linked for this launch."
        };

        if (overlayPreview.Conflicts.Count > 0)
        {
            lines.Add(string.Empty);
            lines.AddRange(overlayPreview.Conflicts
                .Take(5)
                .Select(conflict => $"Conflict winner: {conflict.TargetRelativePath} <- {conflict.Winner.OwningModId}"));
        }

        if (validationWarnings.Length > 0)
        {
            lines.Add(string.Empty);
            lines.AddRange(validationWarnings
                .Take(6)
                .Select(issue => $"{issue.Code}: {issue.Message}"));
        }

        if (image.Warnings.Count > 0)
        {
            lines.Add(string.Empty);
            lines.AddRange(image.Warnings
                .Take(6)
                .Select(warning => $"Image warning: {warning}"));
        }

        return string.Join("\n", lines);
    }

    private static string BuildVirtualLaunchCleanupWarningMessage(IReadOnlyList<ProfileDeployResult> cleanResults)
    {
        var deletedFiles = cleanResults.Sum(result => result.DeletedFiles);
        var preservedFiles = cleanResults.Sum(result => result.PreservedFiles);
        var warnings = cleanResults
            .SelectMany(result => result.Warnings.Select(warning => $"{result.ProfileId}: {warning}"))
            .Take(8)
            .ToArray();

        var lines = new List<string>
        {
            "UCU cleaned previously deployed manager files before virtualized launch.",
            string.Empty,
            $"Deleted managed files: {deletedFiles}",
            $"Preserved changed files: {preservedFiles}",
            string.Empty,
            "Preserved files may still affect the virtualized game image because they are currently present in the real game folder."
        };

        if (warnings.Length > 0)
        {
            lines.Add(string.Empty);
            lines.AddRange(warnings);
        }

        lines.Add(string.Empty);
        lines.Add("Launch anyway?");
        return string.Join("\n", lines);
    }

    private static string BuildVirtualLaunchPreCleanupMessage(VirtualLaunchCleanupRisk cleanupRisk)
    {
        var lines = new List<string>
        {
            "Virtual launch requires a clean game state.",
            "Clean Deploy will run before launch and remove manager-copied profile files from the real game folder.",
            string.Empty
        };

        if (cleanupRisk.ActiveProfileFiles > 0)
        {
            lines.Add($"Active profile deploy: {cleanupRisk.ActiveProfileFiles} files");
        }

        if (cleanupRisk.OtherProfiles.Count > 0)
        {
            lines.Add("Other deployed profiles:");
            lines.AddRange(cleanupRisk.OtherProfiles.Select(profile => $"- {profile}"));
        }

        lines.Add(string.Empty);
        lines.Add("Continue?");
        return string.Join("\n", lines);
    }

    private static string BuildVirtualLaunchValidationMessage(
        VirtualizedLaunchPlanValidationResult validation,
        string planPath)
    {
        var lines = new List<string>
        {
            "Virtualized launch plan was written, but validation found blocking errors.",
            string.Empty,
            $"Plan: {planPath}",
            string.Empty
        };

        lines.AddRange(validation.Issues
            .Where(issue => issue.Severity == VirtualizedLaunchPlanIssueSeverity.Error)
            .Take(10)
            .Select(issue => $"{issue.Code}: {issue.Message}"));

        var remaining = validation.Issues.Count(issue => issue.Severity == VirtualizedLaunchPlanIssueSeverity.Error) - 10;
        if (remaining > 0)
        {
            lines.Add($"... {remaining} more errors");
        }

        return string.Join("\n", lines);
    }

    private static string BuildBlockedProfileCleanupMessage(IReadOnlyList<ProfileDeployResult> blockedCleanResults)
    {
        var warnings = blockedCleanResults
            .SelectMany(result => result.Warnings.Select(warning => $"{result.ProfileId}: {warning}"))
            .Take(8)
            .ToArray();
        var message = "Previous profile deployment could not be fully cleaned. Changed or unsafe files were preserved, so the new profile was not deployed.";
        if (warnings.Length > 0)
        {
            message += $"\n\n{string.Join("\n", warnings)}";
        }

        return message;
    }

    private static string BuildBlockedUpdateCleanupMessage(IReadOnlyList<ProfileDeployResult> blockedCleanResults)
    {
        var warnings = blockedCleanResults
            .SelectMany(result => result.Warnings.Select(warning => $"{result.ProfileId}: {warning}"))
            .Take(8)
            .ToArray();
        var message = "The selected update was not installed because the current deployed files could not be fully cleaned. Changed or unsafe files were preserved.";
        if (warnings.Length > 0)
        {
            message += $"\n\n{string.Join("\n", warnings)}";
        }

        return message;
    }

    private void SetStatus(TextBlock textBlock, string text, string brushResourceKey)
    {
        textBlock.Text = text;
        textBlock.Foreground = (Brush)FindResource(brushResourceKey);
    }

    private void SetAutoLinkStatus(string text, string brushResourceKey)
    {
        AutoLinkStatusText.Text = text;
        AutoLinkStatusText.Foreground = (Brush)FindResource(brushResourceKey);
    }

    private static ManagerPaths ResolveManagerPaths()
    {
        var applicationDirectory = new DirectoryInfo(AppContext.BaseDirectory);
        var directory = applicationDirectory;
        while (directory is not null)
        {
            var devDataPath = Path.Combine(directory.FullName, "dev-data");
            if (Directory.Exists(Path.Combine(devDataPath, "mods")))
            {
                return new ManagerPaths(devDataPath);
            }

            directory = directory.Parent;
        }

        return ManagerPaths.FromApplicationDataDirectory(applicationDirectory.FullName);
    }

    private static bool PathsEqual(string firstPath, string secondPath)
    {
        return EnsureTrailingSeparator(Path.GetFullPath(firstPath))
            .Equals(EnsureTrailingSeparator(Path.GetFullPath(secondPath)), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsInsideRoot(string candidatePath, string rootPath)
    {
        var root = EnsureTrailingSeparator(Path.GetFullPath(rootPath));
        var candidate = Path.GetFullPath(candidatePath);
        return candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            || candidate.Equals(root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    }

    private static string EnsureTrailingSeparator(string path)
    {
        return path.EndsWith(Path.DirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;
    }

    private sealed class ModRow
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public required string DisplayName { get; set; }
        public required string Version { get; init; }
        public required string SourceStatus { get; init; }
        public required string? SourceDisplayName { get; init; }
        public required string Author { get; init; }
        public required string? PageUrl { get; init; }
        public required string GameDomain { get; init; }
        public required int? NexusModId { get; init; }
        public required int? CurrentNexusFileId { get; init; }
        public required int? LatestNexusFileId { get; set; }
        public required string LatestNexusFileName { get; set; }
        public required string SourceArchiveFileName { get; init; }
        public required string? IconUrl { get; init; }
        public required string? LargeImageUrl { get; init; }
        public required string NexusVersion { get; set; }
        public required string Description { get; init; }
        public required int? Endorsements { get; init; }
        public required int? UniqueDownloads { get; init; }
        public required int? TotalDownloads { get; init; }
        public required int? TotalViews { get; init; }
        public required int FileCount { get; init; }
        public required int PluginCount { get; init; }
        public required int ContentFileCount { get; init; }
        public required int ProfileConfigCount { get; init; }
        public required int AssemblyCount { get; init; }
        public required int WarningCount { get; init; }
        public required int MissingDependencyCount { get; init; }
        public required string DependencyStatus { get; init; }
        public required string Status { get; init; }
        public required string UpdateStatus { get; set; }
        public required string LatestVersion { get; set; }
        public required bool CanCheckUpdates { get; init; }
        public required IReadOnlyList<DependencyRow> Dependencies { get; init; }
        public required IReadOnlyList<string> Warnings { get; init; }
        public bool IsEnabled { get; set; }
        public int Priority { get; set; }

        public static ModRow FromEntry(
            ModLibraryEntry entry,
            bool isEnabled,
            int priority,
            IReadOnlyDictionary<string, IReadOnlyList<string>> activeAssemblyProviders)
        {
            var dependencies = entry.Dependencies
                .Select(dependency => DependencyRow.FromStatus(dependency, activeAssemblyProviders))
                .ToArray();
            var missingDependencies = dependencies.Count(dependency => dependency.Status != "Found");
            var warnings = entry.Warnings
                .Where(IsActionableWarning)
                .Concat(dependencies
                    .Where(dependency => dependency.Status != "Found")
                    .Select(dependency => dependency.Status == "Installed disabled"
                        ? $"Dependency disabled in active profile: {dependency.AssemblyName} ({dependency.Providers})"
                        : $"Missing assembly reference: {dependency.AssemblyName}"))
                .ToArray();

            return new ModRow
            {
                Id = entry.Mod.Id,
                Name = entry.Mod.Name,
                DisplayName = BuildDisplayModName(entry.Mod.Name, entry.Manifest.Source?.DisplayName, showAdvancedName: false),
                Version = string.IsNullOrWhiteSpace(entry.Mod.Version) ? "unknown" : entry.Mod.Version,
                SourceStatus = BuildSourceStatus(entry.Manifest.Source),
                SourceDisplayName = entry.Manifest.Source?.DisplayName,
                Author = string.IsNullOrWhiteSpace(entry.Manifest.Source?.Author) ? string.Empty : entry.Manifest.Source.Author!,
                PageUrl = entry.Manifest.Source?.PageUrl,
                GameDomain = entry.Manifest.Source?.GameDomain ?? string.Empty,
                NexusModId = entry.Manifest.Source?.ModId,
                CurrentNexusFileId = entry.Manifest.Source?.FileId,
                LatestNexusFileId = entry.Manifest.Source?.LastLatestFileId,
                LatestNexusFileName = entry.Manifest.Source?.DisplayName ?? entry.Manifest.Source?.SourceArchiveFileName ?? entry.Manifest.SourceArchiveFileName,
                SourceArchiveFileName = entry.Manifest.Source?.SourceArchiveFileName ?? entry.Manifest.SourceArchiveFileName,
                IconUrl = entry.Manifest.Source?.IconUrl,
                LargeImageUrl = entry.Manifest.Source?.ImageUrls?.FirstOrDefault() ?? entry.Manifest.Source?.IconUrl,
                NexusVersion = entry.Manifest.Source?.LastLatestVersion
                    ?? entry.Manifest.Source?.FileVersion
                    ?? entry.Mod.Version,
                Description = entry.Manifest.Source?.Description ?? string.Empty,
                Endorsements = entry.Manifest.Source?.Endorsements,
                UniqueDownloads = entry.Manifest.Source?.UniqueDownloads,
                TotalDownloads = entry.Manifest.Source?.TotalDownloads,
                TotalViews = entry.Manifest.Source?.TotalViews,
                FileCount = entry.FileCount,
                PluginCount = entry.Mod.Files.Count(IsPluginDll),
                ContentFileCount = entry.Mod.Files.Count(IsContentFile),
                ProfileConfigCount = entry.Mod.Files.Count(IsConfigFile),
                AssemblyCount = entry.Mod.Assemblies.Count,
                WarningCount = warnings.Length,
                MissingDependencyCount = missingDependencies,
                DependencyStatus = dependencies.Length == 0 ? "None" : $"{dependencies.Length - missingDependencies}/{dependencies.Length}",
                Status = warnings.Length == 0 ? "Ready" : "Review",
                UpdateStatus = BuildInitialUpdateStatus(entry.Manifest.Source),
                LatestVersion = string.Empty,
                CanCheckUpdates = entry.Manifest.Source?.CanCheckUpdates == true,
                Dependencies = dependencies,
                Warnings = warnings,
                IsEnabled = isEnabled,
                Priority = priority
            };
        }

        private static string BuildSourceStatus(ModSourceInfo? source)
        {
            if (source is null)
            {
                return "manual archive";
            }

            if (source.Provider.Equals("NexusMods", StringComparison.OrdinalIgnoreCase) && source.ModId is not null)
            {
                return $"Nexus #{source.ModId}";
            }

            if (source.Provider.Equals("NexusMods", StringComparison.OrdinalIgnoreCase))
            {
                return "Nexus file";
            }

            return source.Provider;
        }

        private static string BuildInitialUpdateStatus(ModSourceInfo? source)
        {
            if (source is null)
            {
                return "Not linked";
            }

            if (!string.IsNullOrWhiteSpace(source.LastUpdateStatus))
            {
                return IsStaleStartupUpdateStatus(source.LastUpdateStatus)
                    ? "Check needed"
                    : source.LastUpdateStatus;
            }

            if (source.CanCheckUpdates)
            {
                return "Nexus linked";
            }

            if (source.Provider.Equals("NexusMods", StringComparison.OrdinalIgnoreCase))
            {
                return "Needs link";
            }

            return "Not linked";
        }

        private static bool IsStaleStartupUpdateStatus(string status)
        {
            return status.StartsWith("Update", StringComparison.OrdinalIgnoreCase)
                || status.Equals("Update available", StringComparison.OrdinalIgnoreCase)
                || status.Equals("Needs file check", StringComparison.OrdinalIgnoreCase)
                || status.Equals("API error", StringComparison.OrdinalIgnoreCase)
                || status.Equals("Downloading...", StringComparison.OrdinalIgnoreCase)
                || status.Equals("Downloaded", StringComparison.OrdinalIgnoreCase)
                || status.Equals("Install queued", StringComparison.OrdinalIgnoreCase)
                || status.Equals("Download failed", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsActionableWarning(string warning)
        {
            return !warning.StartsWith("Potential external assembly references detected:", StringComparison.OrdinalIgnoreCase)
                && !warning.StartsWith("Archive root '", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsPluginDll(ModFileMapping file)
        {
            return file.TargetKind == ModTargetKind.BepInExPlugin
                && Path.GetExtension(file.NormalizedTargetRelativePath).Equals(".dll", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsContentFile(ModFileMapping file)
        {
            return file.TargetKind != ModTargetKind.Documentation
                && file.TargetKind != ModTargetKind.BepInExProfileConfig
                && file.TargetKind != ModTargetKind.BepInExConfig
                && !IsPluginDll(file);
        }

        private static bool IsConfigFile(ModFileMapping file)
        {
            return file.TargetKind == ModTargetKind.BepInExProfileConfig
                || file.TargetKind == ModTargetKind.BepInExConfig
                || file.NormalizedTargetRelativePath.StartsWith("BepInEx/config/", StringComparison.OrdinalIgnoreCase)
                || Path.GetExtension(file.NormalizedTargetRelativePath).Equals(".cfg", StringComparison.OrdinalIgnoreCase);
        }
    }

    private sealed record ProfileRow(string Id, string Name)
    {
        public override string ToString()
        {
            return Name;
        }
    }

    private sealed record ModInstallPlan(string ArchivePath, ModImportPreview Preview);

    private sealed record ModInstallOutcome(string ArchiveFileName, ModImportResult? Result, string? Error)
    {
        public static ModInstallOutcome Success(string archiveFileName, ModImportResult result)
        {
            return new ModInstallOutcome(archiveFileName, result, null);
        }

        public static ModInstallOutcome Failure(string archiveFileName, string error)
        {
            return new ModInstallOutcome(archiveFileName, null, error);
        }
    }

    private sealed record NexusFileRow(
        int FileId,
        string Category,
        string Name,
        string FileName,
        string Version,
        string Uploaded,
        DateTimeOffset? UploadedAt,
        string Size,
        bool IsPrimary)
    {
        public static NexusFileRow FromFile(NexusModFileInfo file)
        {
            return new NexusFileRow(
                file.FileId,
                file.IsPrimary ? $"{file.Category} *" : file.Category,
                file.Name,
                file.FileName,
                string.IsNullOrWhiteSpace(file.Version) ? "unknown" : file.Version,
                file.UploadedAt?.LocalDateTime.ToString("g") ?? "unknown",
                file.UploadedAt,
                FormatFileSize(file.SizeInBytes),
                file.IsPrimary);
        }

        private static string FormatFileSize(long? sizeInBytes)
        {
            if (sizeInBytes is null || sizeInBytes.Value < 0)
            {
                return "unknown";
            }

            var size = sizeInBytes.Value;
            if (size >= 1024L * 1024L * 1024L)
            {
                return $"{size / (1024d * 1024d * 1024d):0.##} GB";
            }

            if (size >= 1024L * 1024L)
            {
                return $"{size / (1024d * 1024d):0.##} MB";
            }

            if (size >= 1024L)
            {
                return $"{size / 1024d:0.##} KB";
            }

            return $"{size} B";
        }
    }

    private sealed record NexusCatalogRow(
        string CatalogId,
        string Name,
        string Version,
        string Author,
        string Downloads,
        string Status,
        string NexusModIdText,
        bool CanDownload,
        NexusMetadataCatalogEntry Entry)
    {
        public static NexusCatalogRow FromEntry(NexusMetadataCatalogEntry entry, bool isInstalled)
        {
            var downloadReference = entry.DownloadReference;
            var gameDomain = FirstNonEmpty(entry.NexusGameDomain, downloadReference?.GameDomain) ?? "unknown";
            var modId = entry.NexusModId ?? downloadReference?.ModId;
            return new NexusCatalogRow(
                $"{gameDomain}:{modId?.ToString() ?? entry.Id ?? entry.Name ?? "unknown"}",
                string.IsNullOrWhiteSpace(entry.Name) ? "Unnamed mod" : entry.Name!,
                GetKnownModVersion(entry.BestVersion) ?? entry.BestVersion ?? "unknown",
                string.IsNullOrWhiteSpace(entry.Author) ? "unknown" : entry.Author!,
                FormatNullableCount(entry.Statistics?.TotalDownloads),
                isInstalled ? "Installed" : "Available",
                modId?.ToString() ?? "unknown",
                modId is not null && !string.IsNullOrWhiteSpace(gameDomain),
                entry);
        }
    }

    private sealed record UcuModpackModRow(
        int Priority,
        string Name,
        string DisplayName,
        string Version,
        string Enabled,
        string Source,
        string Status)
    {
        public static UcuModpackModRow FromProfileMod(UcuModpackMod mod, bool showAdvancedName)
        {
            var hasNexusSource = GetUcuModId(mod) is not null && !string.IsNullOrWhiteSpace(GetUcuGameDomain(mod));
            var hasPortableSource = !string.IsNullOrWhiteSpace(mod.SourceArchiveFileName);
            var status = hasNexusSource
                ? hasPortableSource ? "Ready for both" : "Ready for .UCU"
                : hasPortableSource
                    ? ".UCUP only"
                    : "Needs Nexus link";
            return new UcuModpackModRow(
                mod.Priority + 1,
                mod.Name,
                BuildDisplayModName(mod.Name, null, showAdvancedName),
                string.IsNullOrWhiteSpace(mod.Version) ? "unknown" : mod.Version!,
                mod.IsEnabled ? "Yes" : "No",
                hasNexusSource ? $"Nexus #{GetUcuModId(mod)}" : "Not linked",
                status);
        }

        public static UcuModpackModRow FromMod(UcuModpackMod mod, bool isInstalled, bool needsManualDownload, bool showAdvancedName)
        {
            var hasNexusSource = GetUcuModId(mod) is not null && !string.IsNullOrWhiteSpace(GetUcuGameDomain(mod));
            var status = isInstalled
                ? "Installed"
                : needsManualDownload
                    ? "Needs manual download"
                    : !string.IsNullOrWhiteSpace(mod.EmbeddedArchiveFileName)
                        ? "Embedded"
                : hasNexusSource
                    ? GetUcuFileId(mod) is null ? "Needs file lookup" : "Ready"
                    : "Manual only";
            return new UcuModpackModRow(
                mod.Priority + 1,
                mod.Name,
                BuildDisplayModName(mod.Name, null, showAdvancedName),
                string.IsNullOrWhiteSpace(mod.Version) ? "unknown" : mod.Version!,
                mod.IsEnabled ? "Yes" : "No",
                hasNexusSource ? $"Nexus #{GetUcuModId(mod)}" : "Not linked",
                status);
        }
    }

    private sealed record NexusDownloadTarget(
        string GameDomain,
        int ModId,
        int FileId,
        string? Version,
        string? FileName);

    private sealed record UcuModpackInstallPlan(
        UcuModpackMod Mod,
        string ArchivePath,
        ModImportPreview Preview);

    private sealed record NexusModUpdatePlan(ModLibraryEntry Entry, NexusUpdateCheckResult Result);

    private sealed record NexusLinkCompletion(ModManifest Manifest, NexusUpdateCheckResult? Result);

    private sealed record MissingDependencyProvider(string ModId, string ModName);

    private sealed record MissingDependencySeed(
        string ModId,
        string ModName,
        string AssemblyName,
        IReadOnlyList<MissingDependencyProvider> InstalledProviders);

    private sealed record MissingDependencyCandidate(
        string AssemblyName,
        NexusMetadataCatalogEntry Entry);

    private sealed record MissingDependencyIssue(
        string ModName,
        string AssemblyName,
        IReadOnlyList<MissingDependencyProvider> InstalledProviders,
        IReadOnlyList<MissingDependencyCandidate> Candidates);

    private sealed record MissingDependencyReport(
        IReadOnlyList<MissingDependencyIssue> Issues,
        string? CatalogWarning);

    private sealed record NexusAutoLinkSummary(
        int Linked,
        int Completed,
        int Repaired,
        int Cleared,
        int AlreadyLinked,
        int Skipped,
        int ApiErrors,
        int SearchLinked,
        int SearchErrors,
        bool UsedApi,
        IReadOnlyList<string> Details);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect MonitorArea;
        public NativeRect WorkArea;
        public int Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo monitorInfo);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int attribute,
        ref int attributeValue,
        int attributeSize);

    private sealed record VirtualLaunchCleanupRisk(
        int ActiveProfileFiles,
        IReadOnlyList<string> OtherProfiles)
    {
        public bool HasRisk => ActiveProfileFiles > 0 || OtherProfiles.Count > 0;
    }

    private sealed record DependencyRow(string AssemblyName, string Status, string Providers)
    {
        public static DependencyRow FromStatus(
            ModDependencyStatus status,
            IReadOnlyDictionary<string, IReadOnlyList<string>> activeAssemblyProviders)
        {
            var activeProviderIds = activeAssemblyProviders.TryGetValue(status.AssemblyName, out var activeProviders)
                ? status.ProviderModIds
                    .Where(providerId => activeProviders.Contains(providerId, StringComparer.OrdinalIgnoreCase))
                    .ToArray()
                : Array.Empty<string>();
            if (activeProviderIds.Length > 0)
            {
                return new DependencyRow(status.AssemblyName, "Found", string.Join(", ", activeProviderIds));
            }

            if (status.ProviderModIds.Count > 0)
            {
                return new DependencyRow(status.AssemblyName, "Installed disabled", string.Join(", ", status.ProviderModIds));
            }

            return new DependencyRow(
                status.AssemblyName,
                "Missing",
                string.Empty);
        }
    }

    private sealed record OverlayRow(string Status, string DisplayKind, string TargetRelativePath, string OwningModId)
    {
        public static OverlayRow FromEntry(OverlayPreviewEntry entry)
        {
            return new OverlayRow(
                entry.Status,
                GetDisplayKind(entry),
                entry.TargetRelativePath,
                GetDisplayOwner(entry.OwningModId));
        }

        private static string GetDisplayOwner(string ownerId)
        {
            return ownerId.Equals("__profile_state__", StringComparison.OrdinalIgnoreCase)
                ? "Profile State"
                : ownerId;
        }

        private static string GetDisplayKind(OverlayPreviewEntry entry)
        {
            var extension = Path.GetExtension(entry.TargetRelativePath);
            var isDll = extension.Equals(".dll", StringComparison.OrdinalIgnoreCase);

            return entry.TargetKind switch
            {
                ModTargetKind.BepInExPlugin when isDll => "Plugin DLL",
                ModTargetKind.BepInExPlugin => "Plugin Content",
                ModTargetKind.BepInExPatcher when isDll => "Patcher DLL",
                ModTargetKind.BepInExPatcher => "Patcher Content",
                ModTargetKind.BepInExProfileConfig => "Profile Config",
                ModTargetKind.BepInExConfig => "BepInEx Config",
                ModTargetKind.BepInExTranslation => "Translation",
                ModTargetKind.GameDataContent => "Game Content",
                ModTargetKind.GameRootContent => "Game Root File",
                ModTargetKind.Documentation => "Documentation",
                _ => "Content"
            };
        }
    }
}
