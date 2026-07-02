using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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
    private static readonly IReadOnlyList<KnownNexusLink> KnownNexusLinks = new[]
    {
        new KnownNexusLink(
            "scavprototype",
            69,
            new[] { "catpatch" },
            new[] { "CatPatch", "Catpatch", "Cat Patch", "Cat-Patch" })
    };

    private readonly ManagerSettingsService _settingsService = new();
    private readonly ModLibraryService _libraryService = new();
    private readonly ModImportService _importService = new();
    private readonly ModRemovalService _modRemovalService = new();
    private readonly ProfileService _profileService = new();
    private readonly GameInstallationValidator _gameValidator = new();
    private readonly BepInExInstallationProbe _bepInExProbe = new();
    private readonly BepInExInstaller _bepInExInstaller = new();
    private readonly ProfileDeployService _profileDeployService = new();
    private readonly VirtualizationPlanBuilder _virtualizationPlanBuilder = new();
    private readonly OverlayPreviewService _overlayPreviewService = new();
    private readonly NexusUpdateCheckService _nexusUpdateCheckService = new();
    private readonly NexusModDownloadService _nexusModDownloadService = new();
    private readonly NexusModSearchService _nexusModSearchService = new();
    private readonly SecureSecretStore _secureSecretStore = new();
    private readonly ObservableCollection<ModRow> _mods = new();
    private readonly ObservableCollection<ProfileRow> _profiles = new();
    private readonly ManagerPaths _managerPaths;

    private ManagerSettings _settings = ManagerSettings.Empty;
    private IReadOnlyList<ModLibraryEntry> _libraryEntries = Array.Empty<ModLibraryEntry>();
    private ModProfile? _currentProfile;
    private bool _isLoading;
    private bool _isLoadingProfiles;

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
        RefreshSetupStatus();
        LoadMods();
        RefreshSettingsStatus();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        RefreshSetupStatus();
        LoadMods();
    }

    private void ChooseGameFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select Casualties Unknown Demo folder"
        };

        if (!string.IsNullOrWhiteSpace(_settings.GameRootPath) && Directory.Exists(_settings.GameRootPath))
        {
            dialog.InitialDirectory = _settings.GameRootPath;
        }

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var validation = _gameValidator.Validate(dialog.FolderName);
        _settings = _settings with { GameRootPath = validation.GameRootPath };
        _settingsService.Save(_managerPaths, _settings);

        RefreshSetupStatus();
        RefreshProfileSummary();
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

    private void DeployProfile_Click(object sender, RoutedEventArgs e)
    {
        var installation = GetDeploymentReadyGameOrShowMessage();
        if (installation is null || _currentProfile is null)
        {
            return;
        }

        var missingDependencyMods = _mods
            .Where(mod => mod.IsEnabled && mod.MissingDependencyCount > 0)
            .Select(mod => mod.Name)
            .ToArray();
        if (missingDependencyMods.Length > 0)
        {
            var answer = MessageBox.Show(
                this,
                $"Some enabled mods have missing DLL dependencies: {string.Join(", ", missingDependencyMods)}. Deploy anyway?",
                "Missing dependencies",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes)
            {
                return;
            }
        }

        var overlayPreview = BuildCurrentOverlayPreview();
        if (overlayPreview is null)
        {
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
                dialog.Result,
                GetConfiguredNexusApiKey());
            var updatedManifest = completion.Manifest;

            _libraryService.SaveManifest(libraryEntry.ManifestPath, updatedManifest);
            LoadMods();
            SelectModById(selectedMod.Id);
            var linkedSource = updatedManifest.Source;
            var versionLine = string.IsNullOrWhiteSpace(linkedSource?.FileVersion)
                ? string.Empty
                : $"\nVersion: {linkedSource.FileVersion}";
            var apiLine = string.IsNullOrWhiteSpace(completion.Result?.ErrorMessage)
                ? string.Empty
                : $"\n\nNexus API completion failed: {completion.Result.ErrorMessage}";
            MessageBox.Show(
                this,
                $"Linked '{libraryEntry.Mod.Name}' to Nexus #{linkedSource?.ModId}.{versionLine}{apiLine}",
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
        if (_libraryEntries.Count == 0)
        {
            MessageBox.Show(this, "No installed mods were found.", "Auto Link Nexus", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var answer = MessageBox.Show(
            this,
            "Automatically link installed mods to Nexus using archive names and API search?\n\nThese links may be wrong if an archive was renamed or packed in a non-standard way. If a mod id is missing from the archive name, the manager may scan Nexus mod ids for this game using your API key. Review the results before updating mods.",
            "Auto Link Nexus",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            AutoLinkNexusButton.IsEnabled = false;
            Mouse.OverrideCursor = Cursors.Wait;
            SetAutoLinkStatus("Auto Link: preparing Nexus scan...", "WarningBrush");
            await Task.Yield();

            var progress = new Progress<string>(message => SetAutoLinkStatus(message, "WarningBrush"));
            var summary = await AutoLinkNexusModsAsync(GetConfiguredNexusApiKey(), progress);
            LoadMods();
            SetAutoLinkStatus(
                $"Auto Link: linked {summary.Linked}, repaired {summary.Repaired}, cleared {summary.Cleared}, skipped {summary.Skipped}",
                summary.Skipped == 0 && summary.ApiErrors == 0 && summary.SearchErrors == 0 ? "AccentBrush" : "WarningBrush");
            ShowAutoLinkNexusResults(summary);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or HttpRequestException
            or TaskCanceledException)
        {
            SetAutoLinkStatus("Auto Link failed", "DangerBrush");
            MessageBox.Show(this, exception.Message, "Auto Link Nexus failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            AutoLinkNexusButton.IsEnabled = true;
            Mouse.OverrideCursor = null;
        }
    }

    private async Task<NexusLinkCompletion> BuildLinkedNexusManifestAsync(
        ModLibraryEntry entry,
        NexusLinkDialogResult result,
        string? apiKey)
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
        return await CompleteNexusSourceFromApiAsync(manifest, apiKey);
    }

    private async Task<NexusAutoLinkSummary> AutoLinkNexusModsAsync(string? apiKey, IProgress<string>? progress = null)
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
        var usedApi = !string.IsNullOrWhiteSpace(apiKey);
        var details = new List<string>();

        var processed = 0;
        foreach (var entry in _libraryEntries)
        {
            processed++;
            progress?.Report($"Auto Link: {processed}/{_libraryEntries.Count} {entry.Mod.Name}");

            if (entry.Manifest.Source?.CanCheckUpdates == true)
            {
                if (usedApi)
                {
                    var currentValidation = await ValidateCurrentNexusSourceAsync(entry, apiKey!);
                    if (!currentValidation.IsReliable)
                    {
                        var repairDetection = await TryCreateNexusSourceForAutoLinkAsync(entry, apiKey);
                        if (repairDetection.Error is not null)
                        {
                            searchErrors++;
                            skipped++;
                            details.Add($"{entry.Mod.Name}: skipped repair, Nexus search failed ({repairDetection.Error})");
                            continue;
                        }

                        if (repairDetection.Source is not null
                            && repairDetection.Source.ModId != entry.Manifest.Source.ModId)
                        {
                            var repairedManifest = ApplyNexusSource(entry.Manifest, repairDetection.Source);
                            var repairCompletion = await CompleteNexusSourceFromApiAsync(repairedManifest, apiKey);
                            if (repairCompletion.Result is not null && !string.IsNullOrWhiteSpace(repairCompletion.Result.ErrorMessage))
                            {
                                apiErrors++;
                                details.Add($"{entry.Mod.Name}: repaired link, API check failed ({repairCompletion.Result.ErrorMessage})");
                            }
                            else
                            {
                                repairedManifest = repairCompletion.Manifest;
                            }

                            _libraryService.SaveManifest(entry.ManifestPath, repairedManifest);
                            repaired++;
                            if (repairDetection.WasSearchMatch)
                            {
                                searchLinked++;
                            }

                            details.Add(BuildAutoLinkDetail(entry.Mod.Name, repairedManifest.Source, $"repaired: {repairDetection.Description}"));
                            continue;
                        }

                        _libraryService.SaveManifest(entry.ManifestPath, ClearUnreliableNexusSource(entry.Manifest));
                        cleared++;
                        details.Add($"{entry.Mod.Name}: cleared unreliable Nexus link ({currentValidation.Reason})");
                        continue;
                    }

                    var refreshCompletion = await CompleteNexusSourceFromApiAsync(entry.Manifest, apiKey);
                    if (refreshCompletion.Result is not null && !string.IsNullOrWhiteSpace(refreshCompletion.Result.ErrorMessage))
                    {
                        apiErrors++;
                        alreadyLinked++;
                        details.Add($"{entry.Mod.Name}: already linked, API refresh failed ({refreshCompletion.Result.ErrorMessage})");
                    }
                    else
                    {
                        _libraryService.SaveManifest(entry.ManifestPath, refreshCompletion.Manifest);
                        completed++;
                        details.Add(BuildAutoLinkDetail(entry.Mod.Name, refreshCompletion.Manifest.Source, "refreshed"));
                    }

                    continue;
                }

                alreadyLinked++;

                continue;
            }

            var detection = await TryCreateNexusSourceForAutoLinkAsync(entry, apiKey);
            if (detection.Error is not null)
            {
                searchErrors++;
                skipped++;
                details.Add($"{entry.Mod.Name}: skipped, Nexus search failed ({detection.Error})");
                continue;
            }

            if (detection.Source is null)
            {
                skipped++;
                details.Add($"{entry.Mod.Name}: skipped, no reliable Nexus match found");
                continue;
            }

            var manifest = ApplyNexusSource(entry.Manifest, detection.Source);
            if (usedApi)
            {
                var completion = await CompleteNexusSourceFromApiAsync(manifest, apiKey);
                if (completion.Result is not null && !string.IsNullOrWhiteSpace(completion.Result.ErrorMessage))
                {
                    apiErrors++;
                    details.Add($"{entry.Mod.Name}: linked from archive name, API check failed ({completion.Result.ErrorMessage})");
                }
                else
                {
                    manifest = completion.Manifest;
                }
            }

            _libraryService.SaveManifest(entry.ManifestPath, manifest);
            linked++;
            if (detection.WasSearchMatch)
            {
                searchLinked++;
            }

            details.Add(BuildAutoLinkDetail(entry.Mod.Name, manifest.Source, detection.Description));
        }

        return new NexusAutoLinkSummary(linked, completed, repaired, cleared, alreadyLinked, skipped, apiErrors, searchLinked, searchErrors, usedApi, details);
    }

    private async Task<NexusAutoLinkDetection> TryCreateNexusSourceForAutoLinkAsync(ModLibraryEntry entry, string? apiKey)
    {
        var knownSource = TryCreateKnownNexusSource(entry);
        if (knownSource is not null)
        {
            return new NexusAutoLinkDetection(knownSource, true, "linked from known Nexus mapping", null);
        }

        var detectedSource = TryCreateDetectedNexusSource(entry);
        if (detectedSource is not null)
        {
            return new NexusAutoLinkDetection(detectedSource, false, "linked from archive name", null);
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return new NexusAutoLinkDetection(null, false, string.Empty, "Nexus API key is required for name search");
        }

        try
        {
            var searchMatch = await TryFindNexusSourceBySearchAsync(entry, apiKey);
            return searchMatch is null
                ? new NexusAutoLinkDetection(null, false, string.Empty, null)
                : new NexusAutoLinkDetection(searchMatch.Source, true, $"linked by search: {searchMatch.Title}", null);
        }
        catch (Exception exception) when (exception is HttpRequestException
            or TaskCanceledException
            or IOException
            or InvalidOperationException)
        {
            return new NexusAutoLinkDetection(null, false, string.Empty, exception.Message);
        }
    }

    private ModSourceInfo? TryCreateKnownNexusSource(ModLibraryEntry entry)
    {
        var knownLink = FindKnownNexusLink(entry);
        if (knownLink is null)
        {
            return null;
        }

        var archiveFileName = GetSourceArchiveFileName(entry);
        return new ModSourceInfo(
            "NexusMods",
            NormalizeNexusGameDomain(knownLink.GameDomain),
            knownLink.ModId,
            null,
            GetKnownModVersion(entry.Mod.Version)
                ?? GetKnownModVersion(ModSourceDetector.DetectVersion(archiveFileName)),
            entry.Manifest.Source?.FileTimestamp,
            archiveFileName);
    }

    private ModSourceInfo? TryCreateDetectedNexusSource(ModLibraryEntry entry)
    {
        var archiveFileName = entry.Manifest.Source?.SourceArchiveFileName;
        if (string.IsNullOrWhiteSpace(archiveFileName))
        {
            archiveFileName = entry.Manifest.SourceArchiveFileName;
        }

        var detectedVersion = ModSourceDetector.DetectVersion(archiveFileName);
        var detectedSource = ModSourceDetector.Detect(archiveFileName, detectedVersion);
        if (detectedSource?.ModId is null)
        {
            return null;
        }

        return new ModSourceInfo(
            "NexusMods",
            NormalizeNexusGameDomain(detectedSource.GameDomain),
            detectedSource.ModId,
            detectedSource.FileId ?? entry.Manifest.Source?.FileId,
            GetKnownModVersion(detectedSource.FileVersion)
                ?? GetKnownModVersion(entry.Mod.Version)
                ?? GetKnownModVersion(detectedVersion),
            detectedSource.FileTimestamp ?? entry.Manifest.Source?.FileTimestamp,
            archiveFileName);
    }

    private async Task<NexusSearchMatch?> TryFindNexusSourceBySearchAsync(ModLibraryEntry entry, string apiKey)
    {
        var archiveFileName = GetSourceArchiveFileName(entry);
        var version = GetKnownModVersion(entry.Mod.Version)
            ?? GetKnownModVersion(ModSourceDetector.DetectVersion(archiveFileName));
        var queries = BuildNexusSearchQueries(entry);

        foreach (var domain in GetNexusGameDomainCandidates(_settings.NexusGameDomain))
        {
            var results = await _nexusModSearchService.SearchAsync(domain, queries.FirstOrDefault() ?? entry.Mod.Name, apiKey);
            var candidates = results
                .Select(result => new
                {
                    Result = result,
                    Score = queries.Max(query => ScoreNexusSearchResult(entry, query, result))
                })
                .Where(match => match.Score >= 60)
                .OrderByDescending(match => match.Score)
                .ThenBy(match => match.Result.ModId)
                .Take(30)
                .ToArray();

            var verified = new List<NexusSearchMatch>();
            foreach (var candidate in candidates)
            {
                var files = await _nexusModSearchService.GetFilesAsync(candidate.Result.GameDomain, candidate.Result.ModId, apiKey);
                var evidence = ScoreNexusFileEvidence(entry, candidate.Result, files);
                if (!evidence.IsReliable)
                {
                    continue;
                }

                var source = new ModSourceInfo(
                    "NexusMods",
                    candidate.Result.GameDomain,
                    candidate.Result.ModId,
                    evidence.FileId,
                    GetKnownModVersion(evidence.Version) ?? version,
                    null,
                    archiveFileName);
                verified.Add(new NexusSearchMatch(source, candidate.Result.Title, candidate.Score + evidence.Score));
            }

            var best = verified
                .OrderByDescending(match => match.Score)
                .ThenBy(match => match.Source.ModId)
                .FirstOrDefault();
            if (best is not null)
            {
                return best;
            }
        }

        return null;
    }

    private static IReadOnlyList<string> BuildNexusSearchQueries(ModLibraryEntry entry)
    {
        var archiveName = BuildSearchNameFromArchive(GetSourceArchiveFileName(entry));
        return new[]
        {
            entry.Mod.Name,
            archiveName,
            entry.Mod.Id.Replace('-', ' ')
        }
        .Concat(entry.Mod.Assemblies.Select(assembly => assembly.Name))
        .Concat(ExtractDocumentationAliases(entry))
        .Concat(BuildKnownNexusAliases(entry))
        .Where(query => !string.IsNullOrWhiteSpace(query))
        .Select(query => Regex.Replace(query!.Trim(), @"\s+", " "))
        .Where(query => query.Length >= 3)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
    }

    private static IReadOnlyList<string> ExtractDocumentationAliases(ModLibraryEntry entry)
    {
        var aliases = new List<string>();
        foreach (var file in entry.Mod.Files.Where(file => file.TargetKind == ModTargetKind.Documentation).Take(5))
        {
            var path = Path.Combine(entry.ModDirectoryPath, "files", file.NormalizedTargetRelativePath);
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                var text = File.ReadAllText(path);
                if (text.Length > 8192)
                {
                    text = text[..8192];
                }

                aliases.AddRange(Regex.Matches(text, @"""(?<title>[^""]{4,80})""")
                    .Select(match => match.Groups["title"].Value));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }

        return aliases
            .Select(alias => alias.Replace(':', ' ').Trim())
            .Where(alias => alias.Length >= 3)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<string> BuildKnownNexusAliases(ModLibraryEntry entry)
    {
        var aliases = new List<string>();
        if (entry.Mod.Id.Equals("krokmp", StringComparison.OrdinalIgnoreCase)
            || entry.Mod.Name.Equals("KrokMP", StringComparison.OrdinalIgnoreCase))
        {
            aliases.Add("Casualties Together");
        }

        var knownLink = FindKnownNexusLink(entry);
        if (knownLink is not null)
        {
            aliases.AddRange(knownLink.Aliases);
        }

        return aliases
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static KnownNexusLink? FindKnownNexusLink(ModLibraryEntry entry)
    {
        return KnownNexusLinks.FirstOrDefault(link =>
            link.ModIdHints.Contains(entry.Mod.Id, StringComparer.OrdinalIgnoreCase)
            || link.ModIdHints.Contains(entry.Mod.Name, StringComparer.OrdinalIgnoreCase)
            || link.Aliases.Contains(entry.Mod.Name, StringComparer.OrdinalIgnoreCase)
            || entry.Mod.Assemblies.Any(assembly => link.Aliases.Contains(assembly.Name, StringComparer.OrdinalIgnoreCase)));
    }

    private static string BuildSearchNameFromArchive(string archiveFileName)
    {
        var name = Path.GetFileNameWithoutExtension(archiveFileName);
        name = Regex.Replace(name, @"(?i)-\d+-(?:v?\d+(?:[.-]\d+)*|\d+)-\d{9,}$", " ");
        name = Regex.Replace(name, @"(?i)_[A-Za-z0-9]{8,}$", " ");
        name = Regex.Replace(name, @"(?i)(?:^|[_-])v?\d+(?:[._-]\d+)+(?:-for-\d+(?:[._-]\d+)*)?(?=[_-]|$)", " ");
        name = Regex.Replace(name, @"(?i)v\d+(?:\.\d+)+", " ");
        name = Regex.Replace(name, @"(?i)(?:^|[_-])\d+(?=[_-]|$)", " ");
        name = Regex.Replace(name, @"[_\-.]+", " ");
        return Regex.Replace(name, @"\s+", " ").Trim();
    }

    private static int ScoreNexusSearchResult(ModLibraryEntry entry, string query, NexusModSearchResult result)
    {
        var candidate = NormalizeSearchComparable(result.Title);
        if (string.IsNullOrWhiteSpace(candidate))
        {
            candidate = NormalizeSearchComparable(result.Url);
        }

        var localNames = BuildNexusSearchQueries(entry)
            .Append(query)
            .Select(NormalizeSearchComparable)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var bestScore = 0;
        foreach (var localName in localNames)
        {
            if (candidate.Equals(localName, StringComparison.OrdinalIgnoreCase))
            {
                bestScore = Math.Max(bestScore, 100);
                continue;
            }

            if (candidate.StartsWith(localName, StringComparison.OrdinalIgnoreCase)
                || localName.StartsWith(candidate, StringComparison.OrdinalIgnoreCase))
            {
                bestScore = Math.Max(bestScore, 88);
            }

            if (candidate.Contains(localName, StringComparison.OrdinalIgnoreCase)
                || localName.Contains(candidate, StringComparison.OrdinalIgnoreCase))
            {
                bestScore = Math.Max(bestScore, 78);
            }

            var localTokens = TokenizeSearchComparable(localName);
            var candidateTokens = TokenizeSearchComparable(candidate);
            if (localTokens.Length == 0 || candidateTokens.Length == 0)
            {
                continue;
            }

            var commonTokens = localTokens.Intersect(candidateTokens, StringComparer.OrdinalIgnoreCase).Count();
            var tokenScore = (int)Math.Round(commonTokens * 72.0 / Math.Max(localTokens.Length, candidateTokens.Length));
            bestScore = Math.Max(bestScore, tokenScore);
        }

        return Math.Max(0, bestScore - GetForeignDescriptorPenalty(entry, result.Title));
    }

    private async Task<NexusSourceValidation> ValidateCurrentNexusSourceAsync(ModLibraryEntry entry, string apiKey)
    {
        var source = entry.Manifest.Source;
        if (source?.ModId is null || string.IsNullOrWhiteSpace(source.GameDomain))
        {
            return new NexusSourceValidation(false, "missing Nexus mod id");
        }

        var files = await _nexusModSearchService.GetFilesAsync(source.GameDomain, source.ModId.Value, apiKey);
        if (files.Count == 0)
        {
            return new NexusSourceValidation(false, "Nexus files not found");
        }

        var result = new NexusModSearchResult(
            source.GameDomain,
            source.ModId.Value,
            entry.Mod.Name,
            $"https://www.nexusmods.com/{source.GameDomain}/mods/{source.ModId.Value}");
        var evidence = ScoreNexusFileEvidence(entry, result, files);
        return evidence.IsReliable
            ? new NexusSourceValidation(true, evidence.Reason)
            : new NexusSourceValidation(false, evidence.Reason);
    }

    private static NexusFileEvidence ScoreNexusFileEvidence(
        ModLibraryEntry entry,
        NexusModSearchResult result,
        IReadOnlyList<NexusModFileSearchResult> files)
    {
        var strongTitleMatch = IsStrongTitleMatch(entry, result.Title);
        if (files.Count == 0)
        {
            return strongTitleMatch
                ? new NexusFileEvidence(true, 75, null, null, "exact Nexus title matched")
                : new NexusFileEvidence(false, 0, null, null, "no Nexus files");
        }

        var archiveFileName = GetSourceArchiveFileName(entry);
        var expectedVersion = GetKnownModVersion(entry.Mod.Version)
            ?? GetKnownModVersion(ModSourceDetector.DetectVersion(archiveFileName));
        var localNames = BuildNexusSearchQueries(entry)
            .Select(NormalizeSearchComparable)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var assemblyNames = entry.Mod.Assemblies
            .Select(assembly => NormalizeSearchComparable(assembly.Name))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var titlePenalty = GetForeignDescriptorPenalty(entry, result.Title);

        var bestScore = 0;
        NexusModFileSearchResult? bestFile = null;
        foreach (var file in files)
        {
            var fileName = NormalizeSearchComparable(file.Name ?? string.Empty);
            var fileScore = 0;
            if (!string.IsNullOrWhiteSpace(expectedVersion)
                && !string.IsNullOrWhiteSpace(file.Version)
                && VersionsEqual(expectedVersion, file.Version))
            {
                fileScore += 80;
            }

            if (localNames.Any(name => !string.IsNullOrWhiteSpace(name)
                && (fileName.Contains(name, StringComparison.OrdinalIgnoreCase)
                    || name.Contains(fileName, StringComparison.OrdinalIgnoreCase))))
            {
                fileScore += 45;
            }

            if (assemblyNames.Any(name => !string.IsNullOrWhiteSpace(name)
                && fileName.Contains(name, StringComparison.OrdinalIgnoreCase)))
            {
                fileScore += 25;
            }

            if (IsLikelyMainNexusFile(file))
            {
                fileScore += 10;
            }

            if (strongTitleMatch)
            {
                fileScore += 25;
            }

            fileScore = Math.Max(0, fileScore - titlePenalty);
            if (fileScore > bestScore)
            {
                bestScore = fileScore;
                bestFile = file;
            }
        }

        if (bestFile is null)
        {
            return new NexusFileEvidence(false, 0, null, null, "no matching Nexus file");
        }

        return new NexusFileEvidence(
            bestScore >= 70 || (strongTitleMatch && bestScore >= 35),
            bestScore,
            bestFile.FileId,
            bestFile.Version,
            bestScore >= 70 || (strongTitleMatch && bestScore >= 35)
                ? strongTitleMatch ? "exact title and file evidence matched" : "file/version evidence matched"
                : "weak file/version evidence");
    }

    private static bool IsStrongTitleMatch(ModLibraryEntry entry, string title)
    {
        var normalizedTitle = NormalizeSearchComparable(title);
        return BuildNexusSearchQueries(entry)
            .Select(NormalizeSearchComparable)
            .Where(query => !string.IsNullOrWhiteSpace(query))
            .Any(query => query.Equals(normalizedTitle, StringComparison.OrdinalIgnoreCase));
    }

    private static int GetForeignDescriptorPenalty(ModLibraryEntry entry, string title)
    {
        var localTokens = BuildNexusSearchQueries(entry)
            .SelectMany(TokenizeSearchComparable)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidateTokens = TokenizeSearchComparable(title);
        var foreignDescriptors = new[]
        {
            "translation",
            "translations",
            "supplement",
            "chinese",
            "simplified",
            "traditional",
            "russian",
            "localization",
            "localisation",
            "language",
            "japanese",
            "korean",
            "german",
            "french",
            "spanish",
            "italian",
            "polish",
            "portuguese",
            "turkish"
        };

        return candidateTokens.Any(token => foreignDescriptors.Contains(token, StringComparer.OrdinalIgnoreCase)
            && !localTokens.Contains(token))
                ? 60
                : 0;
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

    private static bool IsLikelyMainNexusFile(NexusModFileSearchResult file)
    {
        return file.CategoryId == 1
            || file.CategoryName?.Contains("main", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static string NormalizeSearchComparable(string value)
    {
        var spaced = Regex.Replace(value, "([a-z])([A-Z])", "$1 $2");
        spaced = Regex.Replace(spaced, @"(?i)\bv?\d+(?:[.\-_]\d+)+\b", " ");
        spaced = Regex.Replace(spaced, @"[^A-Za-z0-9]+", " ");
        return Regex.Replace(spaced.ToLowerInvariant(), @"\s+", " ").Trim();
    }

    private static string[] TokenizeSearchComparable(string value)
    {
        return NormalizeSearchComparable(value)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => token.Length > 1)
            .Where(token => !int.TryParse(token, out _))
            .Where(token => !token.Equals("mod", StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    private static string GetSourceArchiveFileName(ModLibraryEntry entry)
    {
        return string.IsNullOrWhiteSpace(entry.Manifest.Source?.SourceArchiveFileName)
            ? entry.Manifest.SourceArchiveFileName
            : entry.Manifest.Source!.SourceArchiveFileName;
    }

    private async Task<NexusLinkCompletion> CompleteNexusSourceFromApiAsync(ModManifest manifest, string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey) || manifest.Source?.CanCheckUpdates != true)
        {
            return new NexusLinkCompletion(manifest, null);
        }

        var result = await CheckNexusUpdateWithDomainFallback(manifest, apiKey);
        if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
        {
            return new NexusLinkCompletion(manifest, result);
        }

        var source = manifest.Source!;
        var completedSource = source with
        {
            GameDomain = string.IsNullOrWhiteSpace(result.GameDomain)
                ? source.GameDomain
                : result.GameDomain,
            ModId = result.NexusModId ?? source.ModId,
            FileId = source.FileId ?? result.LatestFileId,
            FileVersion = GetKnownModVersion(source.FileVersion)
                ?? GetKnownModVersion(result.LatestVersion),
            LastUpdateStatus = BuildUpdateStatusText(result),
            LastLatestVersion = result.LatestVersion,
            LastCheckedAt = DateTimeOffset.UtcNow
        };
        return new NexusLinkCompletion(ApplyNexusSource(manifest, completedSource), result);
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
            LastCheckedAt = DateTimeOffset.UtcNow
        };

        if (!result.IsUpdateAvailable && string.IsNullOrWhiteSpace(result.ErrorMessage))
        {
            source = source with
            {
                FileId = source.FileId ?? result.LatestFileId,
                FileVersion = GetKnownModVersion(source.FileVersion)
                    ?? GetKnownModVersion(result.LatestVersion)
            };
        }

        return manifest with { Source = source };
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

    private static string BuildAutoLinkDetail(string modName, ModSourceInfo? source, string action)
    {
        var version = string.IsNullOrWhiteSpace(source?.FileVersion)
            ? string.Empty
            : $", version {source.FileVersion}";
        return $"{modName}: {action} Nexus #{source?.ModId}{version}";
    }

    private void ShowAutoLinkNexusResults(NexusAutoLinkSummary summary)
    {
        var lines = new List<string>
        {
            $"Linked: {summary.Linked}",
            $"Linked by search: {summary.SearchLinked}",
            $"Repaired: {summary.Repaired}",
            $"Cleared unreliable: {summary.Cleared}",
            $"Completed existing links: {summary.Completed}",
            $"Already linked: {summary.AlreadyLinked}",
            $"Skipped: {summary.Skipped}",
            $"API errors: {summary.ApiErrors}",
            $"Search errors: {summary.SearchErrors}"
        };

        if (!summary.UsedApi)
        {
            lines.Add("Nexus API was not used because no API key is configured. Some file ids or versions may remain incomplete.");
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

        MessageBox.Show(
            this,
            string.Join("\n", lines),
            "Auto Link Nexus",
            MessageBoxButton.OK,
            summary.ApiErrors == 0 && summary.SearchErrors == 0 && summary.Skipped == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        var apiKey = GetConfiguredNexusApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            MessageBox.Show(
                this,
                "Nexus API key is not configured. Open Settings and save your Nexus Mods API key first.",
                "Nexus API key required",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            NavigationListBox.SelectedIndex = 2;
            return;
        }

        var checkableEntries = GetCheckableNexusEntries();
        if (checkableEntries.Length == 0)
        {
            MessageBox.Show(this, "No installed mods are linked to a Nexus mod id yet.", "Check Updates", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var results = await CheckNexusUpdatesAsync(checkableEntries, apiKey);
            ShowUpdateCheckResults(results);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException)
        {
            MessageBox.Show(this, exception.Message, "Check Updates failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void UpdateMods_Click(object sender, RoutedEventArgs e)
    {
        var apiKey = GetConfiguredNexusApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            MessageBox.Show(
                this,
                "Nexus API key is not configured. Open Settings and save your Nexus Mods API key first.",
                "Nexus API key required",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            NavigationListBox.SelectedIndex = 2;
            return;
        }

        var checkableEntries = GetCheckableNexusEntries();
        if (checkableEntries.Length == 0)
        {
            MessageBox.Show(this, "No installed mods are linked to a Nexus mod id yet.", "Update Mods", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var results = await CheckNexusUpdatesAsync(checkableEntries, apiKey);
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
            foreach (var updatePlan in updatePlans)
            {
                var row = _mods.FirstOrDefault(mod => mod.Id.Equals(updatePlan.Entry.Mod.Id, StringComparison.OrdinalIgnoreCase));
                if (row is not null)
                {
                    row.UpdateStatus = "Downloading...";
                    ModsListView.Items.Refresh();
                }

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
                ShowModInstallResults(downloadFailures);
                OfferManualFallbackForUpdates(skippedUpdates
                    .Concat(apiErrorFallbacks)
                    .Concat(failedDownloadPlans.Select(plan => plan.Result)));
                return;
            }

            foreach (var row in _mods.Where(mod => downloadedArchives.Count > 0 && mod.UpdateStatus == "Downloaded"))
            {
                row.UpdateStatus = "Install queued";
            }

            ModsListView.Items.Refresh();
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
        var plannedImports = new List<ModInstallPlan>();
        var outcomes = initialOutcomes?.ToList() ?? new List<ModInstallOutcome>();

        foreach (var archivePath in archivePaths)
        {
            try
            {
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
        }

        foreach (var plannedImport in plannedImports)
        {
            try
            {
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

        LoadMods();
        ShowModInstallResults(outcomes, preUpdateCleanResults);
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
            $"API errors: {errors.Length}"
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

        MessageBox.Show(
            this,
            string.Join("\n", lines),
            "Check Updates",
            MessageBoxButton.OK,
            errors.Length == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private async Task<IReadOnlyList<NexusUpdateCheckResult>> CheckNexusUpdatesAsync(
        IReadOnlyList<ModLibraryEntry> checkableEntries,
        string apiKey)
    {
        foreach (var row in _mods.Where(mod => mod.CanCheckUpdates))
        {
            row.UpdateStatus = "Checking...";
        }

        ModsListView.Items.Refresh();

        var results = new List<NexusUpdateCheckResult>();
        foreach (var entry in checkableEntries)
        {
            var result = await CheckNexusUpdateWithDomainFallback(entry.Manifest, apiKey);
            results.Add(result);
            PersistNexusUpdateCheck(entry, result);
            var row = _mods.FirstOrDefault(mod => mod.Id.Equals(result.ModId, StringComparison.OrdinalIgnoreCase));
            if (row is not null)
            {
                row.UpdateStatus = BuildUpdateStatusText(result);
                row.LatestVersion = result.LatestVersion ?? string.Empty;
            }
        }

        ModsListView.Items.Refresh();
        return results;
    }

    private void PersistNexusUpdateCheck(ModLibraryEntry entry, NexusUpdateCheckResult result)
    {
        if (entry.Manifest.Source is null)
        {
            return;
        }

        _libraryService.SaveManifest(entry.ManifestPath, ApplyNexusUpdateCheckResult(entry.Manifest, result));
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
            $"API errors: {errors.Length}",
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
        try
        {
            Process.Start(new ProcessStartInfo(uri)
            {
                UseShellExecute = true
            });
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            MessageBox.Show(
                this,
                $"Could not open the Nexus page automatically.\n\n{uri}\n\n{exception.Message}",
                "Open Nexus page failed",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private async Task<NexusUpdateCheckResult> CheckNexusUpdateWithDomainFallback(ModManifest manifest, string apiKey)
    {
        if (manifest.Source is null)
        {
            return await _nexusUpdateCheckService.CheckAsync(manifest, apiKey);
        }

        NexusUpdateCheckResult? lastResult = null;
        foreach (var domain in GetNexusGameDomainCandidates(manifest.Source.GameDomain))
        {
            var result = await _nexusUpdateCheckService.CheckAsync(
                manifest with { Source = manifest.Source with { GameDomain = domain } },
                apiKey);
            if (!IsNexusNotFound(result))
            {
                return result;
            }

            lastResult = result;
        }

        return lastResult ?? await _nexusUpdateCheckService.CheckAsync(manifest, apiKey);
    }

    private IReadOnlyList<string> GetNexusGameDomainCandidates(string? manifestDomain)
    {
        return new[]
        {
            _settings.NexusGameDomain,
            manifestDomain,
            "scavprototype",
            "casualtiesunknowndemo",
            "casualtiesunknown"
        }
        .Where(domain => !string.IsNullOrWhiteSpace(domain))
        .Select(domain => domain!.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
    }

    private static bool ShouldResetNexusGameDomain(string? domain)
    {
        return string.IsNullOrWhiteSpace(domain)
            || domain.Equals("casualtiesunknowndemo", StringComparison.OrdinalIgnoreCase)
            || domain.Equals("casualtiesunknown", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNexusNotFound(NexusUpdateCheckResult result)
    {
        return result.ErrorMessage?.Contains("404", StringComparison.OrdinalIgnoreCase) == true
            || result.ErrorMessage?.Contains("Not Found", StringComparison.OrdinalIgnoreCase) == true;
    }

    private string GetModName(string modId)
    {
        return _libraryEntries.FirstOrDefault(entry => entry.Mod.Id.Equals(modId, StringComparison.OrdinalIgnoreCase))?.Mod.Name
            ?? modId;
    }

    private static string BuildUpdateStatusText(NexusUpdateCheckResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
        {
            return "API error";
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
            ProfileNameTextBox.Text = _currentProfile.Name;

            var libraryByModId = _libraryEntries.ToDictionary(entry => entry.Mod.Id, StringComparer.OrdinalIgnoreCase);
            _mods.Clear();
            foreach (var profileEntry in _currentProfile.Mods.OrderBy(entry => entry.Priority))
            {
                if (libraryByModId.TryGetValue(profileEntry.ModId, out var libraryEntry))
                {
                    _mods.Add(ModRow.FromEntry(libraryEntry, profileEntry.IsEnabled, profileEntry.Priority));
                }
            }
        }
        finally
        {
            _isLoadingProfiles = false;
            _isLoading = false;
        }

        RefreshProfileSummary();
        RefreshProfilePageStatus();
        RefreshDeployStatus();
        ModsListView.SelectedIndex = _mods.Count > 0 ? 0 : -1;
        if (_mods.Count == 0)
        {
            ShowSelectedMod(null);
        }
    }

    private void SelectModById(string modId)
    {
        var row = _mods.FirstOrDefault(mod => mod.Id.Equals(modId, StringComparison.OrdinalIgnoreCase));
        if (row is not null)
        {
            ModsListView.SelectedItem = row;
        }
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

        UpdateRowPriorities();
        var profile = CreateProfileFromRows();
        _profileService.SaveProfile(_managerPaths, profile);
        _currentProfile = profile;
        ModsListView.Items.Refresh();
        RefreshProfileSummary();
        ShowSelectedMod(ModsListView.SelectedItem as ModRow);
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
            ConflictCountText.Text = "0";
            OverlayListView.ItemsSource = Array.Empty<OverlayRow>();
            return;
        }

        var overlayPreview = BuildCurrentOverlayPreview();
        if (overlayPreview is null)
        {
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
        ConflictCountText.Text = overlayPreview.Conflicts.Count.ToString();
        OverlayListView.ItemsSource = overlayPreview.Entries
            .Select(OverlayRow.FromEntry)
            .ToArray();
        RefreshDeployStatus();
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
            $"Profile BepInEx: {_currentProfile.ProfileBepInExPath}",
            manifest is null
                ? "Deploy state: clean"
                : $"Deploy state: {manifest.Files.Count} managed files, updated {manifest.UpdatedAt.LocalDateTime:g}",
            $"Manager storage: {_managerPaths.RootPath}"
        };
    }

    private void ShowSelectedMod(ModRow? selectedMod)
    {
        if (selectedMod is null)
        {
            SelectedModNameText.Text = "No mod selected";
            SelectedModIdText.Text = string.Empty;
            SelectedFileCountText.Text = "0";
            SelectedPluginCountText.Text = "0";
            SelectedContentCountText.Text = "0";
            SelectedWarningCountText.Text = "0";
            DependenciesListView.ItemsSource = Array.Empty<DependencyRow>();
            WarningsListBox.ItemsSource = Array.Empty<string>();
            return;
        }

        SelectedModNameText.Text = selectedMod.Name;
        SelectedModIdText.Text = $"{selectedMod.Id} - version {selectedMod.Version} - {selectedMod.SourceStatus} - {(selectedMod.IsEnabled ? "enabled" : "disabled")} - order {selectedMod.Priority}";
        SelectedFileCountText.Text = selectedMod.FileCount.ToString();
        SelectedPluginCountText.Text = selectedMod.PluginCount.ToString();
        SelectedContentCountText.Text = selectedMod.ContentFileCount.ToString();
        SelectedWarningCountText.Text = selectedMod.WarningCount.ToString();
        DependenciesListView.ItemsSource = selectedMod.Dependencies;
        WarningsListBox.ItemsSource = selectedMod.Warnings;
    }

    private void RefreshSettingsStatus()
    {
        try
        {
            NexusGameDomainTextBox.Text = _settings.NexusGameDomain;
            var hasKey = _secureSecretStore.HasNexusApiKey(_managerPaths);
            NexusApiKeyStatusText.Text = hasKey
                ? $"Nexus API key is saved. Domain: {_settings.NexusGameDomain}."
                : $"No Nexus API key saved. Domain: {_settings.NexusGameDomain}.";
            NexusApiKeyStatusText.Foreground = (Brush)FindResource(hasKey
                ? "AccentBrush"
                : "MutedTextBrush");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            NexusApiKeyStatusText.Text = exception.Message;
            NexusApiKeyStatusText.Foreground = (Brush)FindResource("DangerBrush");
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
            return;
        }

        var validation = _gameValidator.Validate(_settings.GameRootPath);
        GameRootText.Text = validation.GameRootPath;
        if (!validation.IsValid)
        {
            SetStatus(GameStatusText, $"Game: invalid folder. Missing: {string.Join(", ", validation.MissingMarkers)}", "DangerBrush");
            SetStatus(BepInExStatusText, "BepInEx: select a valid game folder first", "MutedTextBrush");
            BepInExDetailText.Text = $"Expected BepInEx {release.Version}";
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
            BepInExDetailText.Text = $"Profile BepInEx is enabled. Expected version: {release.Version}.";
            return;
        }

        if (state.IsInstalled)
        {
            SetStatus(BepInExStatusText, "BepInEx: incomplete, repair recommended", "WarningBrush");
            BepInExDetailText.Text = $"Missing: {string.Join(", ", state.MissingMarkers)}";
            return;
        }

        SetStatus(BepInExStatusText, "BepInEx: not installed", "WarningBrush");
        BepInExDetailText.Text = $"Install from ZIP or download {release.ArchiveFileName}.";
    }

    private void RefreshDeployStatus()
    {
        if (_currentProfile is null)
        {
            SetStatus(DeployStatusText, "Deploy: no active profile", "MutedTextBrush");
            DeployDetailText.Text = "Load or create a profile before deploying files.";
            return;
        }

        var manifest = _profileDeployService.LoadManifest(_managerPaths, _currentProfile.Id);
        if (manifest is null)
        {
            var otherDeployedProfiles = GetOtherDeployedProfiles(_currentProfile.Id);
            if (otherDeployedProfiles.Count > 0)
            {
                SetStatus(DeployStatusText, "Deploy: another profile is active", "WarningBrush");
                DeployDetailText.Text = $"Deploy will clean {string.Join(", ", otherDeployedProfiles.Select(profile => profile.Name))} before deploying this profile.";
                return;
            }

            SetStatus(DeployStatusText, "Deploy: clean", "AccentBrush");
            DeployDetailText.Text = "No profile files are currently managed in the game folder.";
            return;
        }

        SetStatus(DeployStatusText, $"Deploy: {manifest.Files.Count} managed files", "WarningBrush");
        var gameRootNote = string.IsNullOrWhiteSpace(_settings.GameRootPath)
            || manifest.GameRootPath.Equals(_settings.GameRootPath, StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : " Game path differs from current settings.";
        DeployDetailText.Text = $"Last updated {manifest.UpdatedAt.LocalDateTime:g}.{gameRootNote}";
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
        var installation = GetValidGameInstallationOrShowMessage();
        if (installation is null)
        {
            return null;
        }

        var state = _bepInExProbe.Probe(installation.RootPath);
        if (!state.IsComplete)
        {
            RefreshSetupStatus();
            MessageBox.Show(this, "Install or repair BepInEx before deploying profile files.", "BepInEx required", MessageBoxButton.OK, MessageBoxImage.Warning);
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
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var devDataPath = Path.Combine(directory.FullName, "dev-data");
            if (Directory.Exists(Path.Combine(devDataPath, "mods")))
            {
                return new ManagerPaths(devDataPath);
            }

            directory = directory.Parent;
        }

        return ManagerPaths.FromApplicationDirectory();
    }

    private static bool PathsEqual(string firstPath, string secondPath)
    {
        return EnsureTrailingSeparator(Path.GetFullPath(firstPath))
            .Equals(EnsureTrailingSeparator(Path.GetFullPath(secondPath)), StringComparison.OrdinalIgnoreCase);
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
        public required string Version { get; init; }
        public required string SourceStatus { get; init; }
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

        public static ModRow FromEntry(ModLibraryEntry entry, bool isEnabled, int priority)
        {
            var dependencies = entry.Dependencies
                .Select(DependencyRow.FromStatus)
                .ToArray();
            var missingDependencies = dependencies.Count(dependency => dependency.Status == "Missing");
            var warnings = entry.Warnings
                .Where(IsActionableWarning)
                .Concat(dependencies.Where(dependency => dependency.Status == "Missing")
                    .Select(dependency => $"Missing assembly reference: {dependency.AssemblyName}"))
                .ToArray();

            return new ModRow
            {
                Id = entry.Mod.Id,
                Name = entry.Mod.Name,
                Version = string.IsNullOrWhiteSpace(entry.Mod.Version) ? "unknown" : entry.Mod.Version,
                SourceStatus = BuildSourceStatus(entry.Manifest.Source),
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
                return source.LastUpdateStatus;
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

    private sealed record ProfileRow(string Id, string Name);

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

    private sealed record NexusModUpdatePlan(ModLibraryEntry Entry, NexusUpdateCheckResult Result);

    private sealed record NexusLinkCompletion(ModManifest Manifest, NexusUpdateCheckResult? Result);

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

    private sealed record NexusAutoLinkDetection(
        ModSourceInfo? Source,
        bool WasSearchMatch,
        string Description,
        string? Error);

    private sealed record NexusSearchMatch(ModSourceInfo Source, string Title, int Score);

    private sealed record NexusSourceValidation(bool IsReliable, string Reason);

    private sealed record NexusFileEvidence(
        bool IsReliable,
        int Score,
        int? FileId,
        string? Version,
        string Reason);

    private sealed record KnownNexusLink(
        string GameDomain,
        int ModId,
        IReadOnlyList<string> ModIdHints,
        IReadOnlyList<string> Aliases);

    private sealed record DependencyRow(string AssemblyName, string Status, string Providers)
    {
        public static DependencyRow FromStatus(ModDependencyStatus status)
        {
            return new DependencyRow(
                status.AssemblyName,
                status.IsSatisfied ? "Found" : "Missing",
                status.ProviderModIds.Count == 0 ? string.Empty : string.Join(", ", status.ProviderModIds));
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
                entry.OwningModId);
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
