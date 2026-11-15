using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using GameArtManager.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;

namespace GameArtManager
{
    /// <summary>Full-window UI: grid of games, detail pane, SGDB search, custom images.</summary>
    public sealed partial class MainPage : Page
    {
        private readonly ObservableCollection<GameVm> _games = new ObservableCollection<GameVm>();
        private GameVm _selected;
        private SgdbGame _selectedSearchGame;
        // entry the current search box/results belong to (cleared on game change)
        private GameEntry _searchContextEntry;
        private readonly List<ArtVm> _artItems = new List<ArtVm>();
        private bool _busy;
        // generation tokens: async UI population checks its token after every await so a
        // slow earlier load can never paint another game's data (selection race)
        private int _artLoadGen;
        private int _listGen;

        public MainPage()
        {
            InitializeComponent();
            GamesList.ItemsSource = _games;
            var engine = AppEngine.Instance;
            engine.EntriesChanged += Engine_OnEntriesChanged;
            engine.StatusMessage += msg => SetStatus(msg);
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            BtnFixPermission.Visibility = Visibility.Collapsed;
            await RunBusyAsync(async () => await AppEngine.Instance.ScanAsync());
            if (AppEngine.Instance.Entries.Count == 0)
            {
                BtnFixPermission.Visibility = Visibility.Visible;
            }
        }

        private void TglHideOrphaned_Click(object sender, RoutedEventArgs e)
        {
            RefreshGamesList();
        }

        private async void BtnCleanOrphans_Click(object sender, RoutedEventArgs e)
        {
            var engine = AppEngine.Instance;
            var orphans = engine.Entries.Where(en => en.IsOrphaned).ToList();
            if (orphans.Count == 0)
            {
                SetStatus("No orphaned entries - every cached game is tracked by the Xbox app.");
                return;
            }
            var dialog = new ContentDialog
            {
                Title = "Delete orphaned artwork",
                Content = $"Delete the cached artwork files (.png/.bak/.new) of {orphans.Count} games that are no longer in your Xbox app library?\n\nThis cannot be undone.",
                PrimaryButtonText = "Delete",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            await RunBusyAsync(() => Task.Run(() =>
            {
                var deleted = engine.CleanupOrphans(orphans);
                SetStatus($"Deleted {deleted} orphaned entries.");
            }));
        }

        private async void BtnFixPermission_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:privacy-broadfilesystemaccess"));
            }
            catch
            {
                // settings page unavailable
            }
        }

        private void Engine_OnEntriesChanged()
        {
            _ = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, RefreshGamesList);
        }

        // ------------------------------------------------------------ helpers

        private void SetStatus(string message)
        {
            _ = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
            {
                StatusText.Text = message;
            });
        }

        private async Task RunBusyAsync(Func<Task> work, string status = null)
        {
            if (_busy) return;
            _busy = true;
            Progress.IsActive = true;
            try
            {
                await work();
                if (status != null) StatusText.Text = status;
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error: {ex.Message}";
            }
            finally
            {
                Progress.IsActive = false;
                _busy = false;
            }
        }

        private async Task RefreshGamesAsync()
        {
            var gen = ++_listGen;
            var selectedEntry = _selected?.Entry;
            var engine = AppEngine.Instance;
            var hideOrphans = TglHideOrphaned?.IsChecked == true;
            _games.Clear();
            foreach (var entry in engine.Entries.Where(e => !(hideOrphans && e.IsOrphaned)))
            {
                var vm = await GameVm.CreateAsync(entry);
                if (gen != _listGen) return; // a newer refresh superseded this one
                _games.Add(vm);
            }
            if (gen != _listGen) return;

            GameVm reselect = null;
            if (selectedEntry != null)
                reselect = _games.FirstOrDefault(g => ReferenceEquals(g.Entry, selectedEntry));
            if (reselect != null) GamesList.SelectedItem = reselect;
            RefreshDetail();
        }

        private void RefreshGamesList()
        {
            _ = RefreshGamesAsync();
        }

        private void RefreshDetail()
        {
            var vm = GamesList.SelectedItem as GameVm;
            if (vm == null)
            {
                DetailPane.Visibility = Visibility.Collapsed;
                return;
            }
            DetailPane.Visibility = Visibility.Visible;
            DetailThumb.Source = vm.Thumb;
            DetailName.Text = vm.Entry.DisplayName;
            DetailId.Text = vm.Entry.FullId + (vm.Entry.ImagePath != null ? "\n" + vm.Entry.ImagePath : "");
            DetailStatus.Text = vm.StatusText;
            BtnApplyBest.IsEnabled = vm.Entry.SgdbId.HasValue;
            BtnRestore.IsEnabled = vm.Entry.Customized;

            // the search box/results belong to the previously selected game —
            // drop them whenever a different game comes into view. Comparison by
            // Entry (not the VM) keeps the context across list rebuilds of the
            // same game, e.g. the pending refresh right after "Wrong match? Fix…"
            if (!ReferenceEquals(vm.Entry, _searchContextEntry))
            {
                _searchContextEntry = vm.Entry;
                SearchBox.Text = "";
                SearchResults.ItemsSource = null;
                ResetArtGrid();
            }

            // auto-load available SteamGridDB art for matched games
            if (vm.Entry.SgdbId.HasValue)
            {
                _selectedSearchGame = new SgdbGame { Id = vm.Entry.SgdbId.Value, Name = vm.Entry.DisplayName };
                _ = LoadArtGridAsync(vm.Entry.SgdbId.Value);
            }
            else
            {
                // no match: drop anything still loading/shown for the previous selection
                _selectedSearchGame = null;
                ResetArtGrid();
            }
        }

        /// <summary>Clears the art grid and invalidates any in-flight artwork load.</summary>
        private void ResetArtGrid()
        {
            _artLoadGen++;
            _artItems.Clear();
            ArtResults.ItemsSource = null;
            BtnUseArt.IsEnabled = false;
            ArtLoading.Visibility = Visibility.Collapsed;
            ArtEmpty.Visibility = Visibility.Visible;
        }

        private async Task LoadArtGridAsync(long gameId)
        {
            var gen = ++_artLoadGen;
            var gameName = _selectedSearchGame?.Name;
            _artItems.Clear();
            ArtResults.ItemsSource = null;
            BtnUseArt.IsEnabled = false;
            ArtEmpty.Visibility = Visibility.Collapsed;
            ArtLoading.Visibility = Visibility.Visible;
            try
            {
                var icons = await AppEngine.Instance.LoadTileArtAsync(gameId);
                if (gen != _artLoadGen) return; // selection changed while loading
                foreach (var art in icons.OrderByDescending(a => a.Score))
                {
                    var vm = await ArtVm.CreateAsync(art);
                    if (gen != _artLoadGen) return;
                    _artItems.Add(vm);
                }
                if (gen != _artLoadGen) return;
                ArtResults.ItemsSource = _artItems;
                BtnUseArt.IsEnabled = ArtResults.SelectedItem != null;
                SetStatus($"{_artItems.Count} tile options (icons / square art / covers) available for {gameName}.");
            }
            catch (Exception ex)
            {
                if (gen == _artLoadGen)
                    SetStatus($"Artwork load failed: {ex.Message}");
            }
            finally
            {
                // only the load that is still current may touch the overlay state
                if (gen == _artLoadGen)
                {
                    ArtLoading.Visibility = Visibility.Collapsed;
                    ArtEmpty.Visibility = _artItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                }
            }
        }

        private void UpdateGameVm(GameEntry entry)
        {
            var vm = _games.FirstOrDefault(g => ReferenceEquals(g.Entry, entry));
            if (vm != null) vm.Refresh();
            RefreshDetail();
        }

        // ------------------------------------------------------------ toolbar

        private async void BtnScan_Click(object sender, RoutedEventArgs e)
        {
            await RunBusyAsync(async () => await AppEngine.Instance.ScanAsync());
        }

        private async void BtnMatch_Click(object sender, RoutedEventArgs e)
        {
            var engine = AppEngine.Instance;
            if (engine.ApiKeyMissing)
            {
                await ShowSettingsAsync("A free SteamGridDB API key is required.\nCreate an account at steamgriddb.com, generate a key under Preferences > API, then paste it below.");
                if (engine.ApiKeyMissing) return;
            }
            var progress = new Progress<(int done, int total)>(t => SetStatus($"Matching… {t.done}/{t.total}"));
            await RunBusyAsync(async () => await engine.AutoMatchAsync(progress));
        }

        private async void BtnApplyAll_Click(object sender, RoutedEventArgs e)
        {
            var engine = AppEngine.Instance;
            if (engine.ApiKeyMissing)
            {
                await ShowSettingsAsync();
                return;
            }
            var targets = engine.Entries.Where(en => en.SgdbId.HasValue && !en.Customized).ToList();
            if (targets.Count == 0)
            {
                SetStatus("Nothing to apply - every matched game already has custom art.");
                return;
            }
            var progress = new Progress<(int done, int total)>(t => SetStatus($"Applying art… {t.done}/{t.total}"));
            await RunBusyAsync(async () =>
            {
                var applied = await engine.ApplyBestAsync(targets, progress);
                SetStatus($"Applied {applied} artworks ({targets.Count - applied} skipped).");
            });
        }

        private async void BtnRestoreAll_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new ContentDialog
            {
                Title = "Restore originals",
                Content = "Put the original Xbox-app artwork back for every customized game?",
                PrimaryButtonText = "Restore",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                await RunBusyAsync(() => Task.Run(() =>
                {
                    var restored = AppEngine.Instance.RestoreAll();
                    SetStatus($"Restored {restored} originals.");
                }));
            }
        }

        private async void BtnSettings_Click(object sender, RoutedEventArgs e)
        {
            await ShowSettingsAsync();
        }

        private async Task ShowSettingsAsync(string message = null)
        {
            var settings = AppEngine.Instance.Settings;
            var keyBox = new Windows.UI.Xaml.Controls.PasswordBox
            {
                Password = settings.ApiKey,
                PlaceholderText = "Paste your SteamGridDB API key here (free: steamgriddb.com → account → Preferences → API → generate)"
            };
            var threshold = new Windows.UI.Xaml.Controls.TextBox { Text = settings.FuzzyThreshold.ToString() };
            var nsfw = new Windows.UI.Xaml.Controls.CheckBox { Content = "Include NSFW artwork", IsChecked = settings.Nsfw };
            var humor = new Windows.UI.Xaml.Controls.CheckBox { Content = "Include humor artwork", IsChecked = settings.Humor };
            var panel = new StackPanel { Spacing = 10 };
            if (message != null)
                panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 12 });
            panel.Children.Add(keyBox);
            panel.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children = { new TextBlock { Text = "Fuzzy threshold:", VerticalAlignment = VerticalAlignment.Center }, threshold }
            });
            panel.Children.Add(nsfw);
            panel.Children.Add(humor);

            var dialog = new ContentDialog
            {
                Title = "Settings",
                Content = panel,
                PrimaryButtonText = "Save",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                var updated = new Settings
                {
                    ApiKey = keyBox.Password.Trim(),
                    FuzzyThreshold = int.TryParse(threshold.Text, out var t) ? Math.Max(50, Math.Min(100, t)) : settings.FuzzyThreshold,
                    Nsfw = nsfw.IsChecked == true,
                    Humor = humor.IsChecked == true,
                    GridDimensions = settings.GridDimensions,
                    IconDimensions = settings.IconDimensions,
                    Styles = settings.Styles,
                    MatchWorkers = settings.MatchWorkers,
                };
                AppEngine.Instance.SaveSettings(updated);
                SetStatus("Settings saved.");
            }
        }

        // ------------------------------------------------------------ list/detail

        private void GamesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _selected = GamesList.SelectedItem as GameVm;
            RefreshDetail();
        }

        private async void BtnApplyBest_Click(object sender, RoutedEventArgs e)
        {
            var engine = AppEngine.Instance;
            if (_selected == null || !_selected.Entry.SgdbId.HasValue) return;
            var progress = new Progress<(int done, int total)>(t => SetStatus("Applying…"));
            await RunBusyAsync(async () =>
            {
                await engine.ApplyBestAsync(new[] { _selected.Entry }, progress);
                SetStatus($"Artwork applied to {_selected.Entry.DisplayName}.");
            });
        }

        private async void BtnCustom_Click(object sender, RoutedEventArgs e)
        {
            if (_selected == null) return;
            var picker = new Windows.Storage.Pickers.FileOpenPicker
            {
                ViewMode = Windows.Storage.Pickers.PickerViewMode.Thumbnail,
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.PicturesLibrary,
            };
            foreach (var ext in new[] { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif", ".ico" })
                picker.FileTypeFilter.Add(ext);

            var file = await picker.PickSingleFileAsync();
            if (file == null) return;
            await RunBusyAsync(async () =>
            {
                await AppEngine.Instance.UseCustomImageAsync(_selected.Entry, file.Path);
                SetStatus($"Custom artwork applied to {_selected.Entry.DisplayName}.");
            });
        }

        private async void BtnRestore_Click(object sender, RoutedEventArgs e)
        {
            if (_selected == null) return;
            await RunBusyAsync(() => Task.Run(() =>
            {
                if (AppEngine.Instance.Restore(_selected.Entry))
                    SetStatus($"Restored original art for {_selected.Entry.DisplayName}.");
            }));
        }

        private void BtnFixMatch_Click(object sender, RoutedEventArgs e)
        {
            if (_selected == null) return;
            var entry = _selected.Entry;
            var name = entry.DisplayName;

            AppEngine.Instance.ClearMatch(entry);
            _selectedSearchGame = null;
            SearchResults.ItemsSource = null;
            ResetArtGrid();
            BtnApplyBest.IsEnabled = false;
            DetailStatus.Text = "no match - fix manually";
            _selected.Refresh();

            // prefill with the last known name to make the correction quick
            SearchBox.Text = name != "Unknown" ? name : "";
            SearchBox.Focus(FocusState.Pointer);
            SetStatus($"Match cleared for {name}. Type the correct name and press Search, then pick the right game.");
        }

        // ------------------------------------------------------------ SGDB search

        private async void BtnSearch_Click(object sender, RoutedEventArgs e)
        {
            await DoSearchAsync();
        }

        private async void SearchBox_KeyDown(object sender, Windows.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter) await DoSearchAsync();
        }

        private async Task DoSearchAsync()
        {
            var term = SearchBox.Text?.Trim();
            if (string.IsNullOrEmpty(term)) return;
            SearchResults.ItemsSource = null;
            _selectedSearchGame = null;
            ResetArtGrid();
            if (_busy) { SetStatus("Busy - wait for the current operation to finish."); return; }
            SearchLoading.Visibility = Visibility.Visible;
            try
            {
                await RunBusyAsync(async () =>
                {
                    var games = await AppEngine.Instance.SearchAsync(term);
                    SetStatus($"{games.Count} games found for '{term}'.");
                    SearchResults.ItemsSource = games;
                });
            }
            finally
            {
                SearchLoading.Visibility = Visibility.Collapsed;
            }
        }

        private async void SearchResults_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var game = SearchResults.SelectedItem as SgdbGame;
            _selectedSearchGame = game;
            if (game == null) return;
            var gen = ++_artLoadGen;
            _artItems.Clear();
            ArtResults.ItemsSource = null;
            BtnUseArt.IsEnabled = false;
            if (_busy) { SetStatus("Busy - wait for the current operation to finish."); ResetArtGrid(); return; }
            await RunBusyAsync(async () =>
            {
                ArtEmpty.Visibility = Visibility.Collapsed;
                ArtLoading.Visibility = Visibility.Visible;
                try
                {
                    var artByKind = await AppEngine.Instance.LoadArtAsync(game.Id);
                    foreach (var kind in new[] { "grids", "icons", "heroes", "logos" })
                    {
                        foreach (var art in (artByKind.TryGetValue(kind, out var list) ? list : Enumerable.Empty<SgdbArt>())
                            .OrderByDescending(a => a.Score))
                        {
                            var vm = await ArtVm.CreateAsync(art);
                            if (gen != _artLoadGen) return; // another load superseded this one
                            _artItems.Add(vm);
                        }
                    }
                    if (gen != _artLoadGen) return;
                    ArtResults.ItemsSource = _artItems;
                    SetStatus($"{_artItems.Count} artworks for {game.Name}.");
                }
                finally
                {
                    // only the load that is still current may touch the overlay state
                    if (gen == _artLoadGen)
                    {
                        ArtLoading.Visibility = Visibility.Collapsed;
                        ArtEmpty.Visibility = _artItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                    }
                }
            });
        }

        private void ArtResults_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            BtnUseArt.IsEnabled = ArtResults.SelectedItem != null;
        }

        private void BtnUseArt_Click(object sender, RoutedEventArgs e)
        {
            if (_selected == null || _selectedSearchGame == null) return;
            var art = (ArtResults.SelectedItem as ArtVm)?.Art;
            if (art == null) return;
            _ = RunBusyAsync(async () =>
            {
                await AppEngine.Instance.UseArtAsync(_selected.Entry, _selectedSearchGame, art);
                SetStatus($"Artwork applied to {_selected.Entry.DisplayName}.");
            });
        }
    }
}
