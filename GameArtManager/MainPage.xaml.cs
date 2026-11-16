using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using GameArtManager.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
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
        // suppresses SelectionChanged reactions while the games list is rebuilt —
        // Clear() fires SelectionChanged(null) which would otherwise clobber the
        // selection made during the rebuild and drop the detail pane
        private bool _rebuilding;

        public MainPage()
        {
            InitializeComponent();
            GamesList.ItemsSource = _games;
            var engine = AppEngine.Instance;
            engine.EntriesChanged += Engine_OnEntriesChanged;
            engine.StatusMessage += msg => SetStatus(msg);
            // PC gamepads do not get Xbox-style automatic UI navigation — translate the
            // VirtualKey.Gamepad* events ourselves (full screen experience / handhelds)
            Window.Current.CoreWindow.KeyDown += CoreWindow_KeyDown;
            // after an FSE app switch nothing may be focused, which leaves the
            // controller and keyboard dead — restore it
            Window.Current.Activated += OnWindowActivated;
            Loaded += (s, e) => RestoreFocus();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            BtnFixPermission.Visibility = Visibility.Collapsed;
            // gamepad/keyboard users need a focused control before D-pad navigation works
            RestoreFocus();
            await RunBusyAsync(async () => await AppEngine.Instance.ScanAsync());
            if (AppEngine.Instance.Entries.Count == 0)
            {
                BtnFixPermission.Visibility = Visibility.Visible;
            }
        }

        /// <summary>Focus the game list so D-pad / stick navigation has a start point.</summary>
        private void RestoreFocus()
        {
            GamesList.Focus(FocusState.Programmatic);
        }

        private void OnWindowActivated(object sender, Windows.UI.Core.WindowActivatedEventArgs e)
        {
            if (e.WindowActivationState == Windows.UI.Core.CoreWindowActivationState.Deactivated) return;
            if (FocusManager.GetFocusedElement() != null) return;
            RestoreFocus();
        }

        private void CoreWindow_KeyDown(Windows.UI.Core.CoreWindow sender, Windows.UI.Core.KeyEventArgs args)
        {
            // D-pad / stick / A: PC gamepads need explicit focus translation
            if (GamepadFocus.HandleKey(args.VirtualKey, ActivateGamepadItem))
                args.Handled = true;
        }

        /// <summary>Gamepad A on a focused item: the game list selects, the search
        /// results and artwork grid select the focused entry.</summary>
        private bool ActivateGamepadItem(FrameworkElement container)
        {
            var vm = GamepadFocus.ItemOf(GamesList, container) as GameVm;
            if (vm != null)
            {
                if (GamesList.SelectedItem != vm) GamesList.SelectedItem = vm;
                return true;
            }
            var game = GamepadFocus.ItemOf(SearchResults, container) as SgdbGame;
            if (game != null)
            {
                if (SearchResults.SelectedItem != game) SearchResults.SelectedItem = game;
                return true;
            }
            var art = GamepadFocus.ItemOf(ArtResults, container) as ArtVm;
            if (art != null)
            {
                if (ArtResults.SelectedItem != art) ArtResults.SelectedItem = art;
                return true;
            }
            return false;
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
            await RunBusyAsync(async () =>
            {
                var deleted = await engine.CleanupOrphansAsync(orphans);
                SetStatus($"Deleted {deleted} orphaned entries.");
            });
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
            _rebuilding = true;
            _games.Clear();
            foreach (var entry in engine.Entries.Where(e => !(hideOrphans && e.IsOrphaned)))
            {
                var vm = await GameVm.CreateAsync(entry);
                if (gen != _listGen) return; // a newer refresh superseded this one
                _games.Add(vm);
            }
            if (gen != _listGen) return;
            _rebuilding = false;

            // honour the newest user selection — a game clicked during the rebuild
            // must win over the entry captured when the rebuild started
            var desiredEntry = _selected?.Entry ?? selectedEntry;
            GameVm reselect = null;
            if (desiredEntry != null)
                reselect = _games.FirstOrDefault(g => ReferenceEquals(g.Entry, desiredEntry));
            GamesList.SelectedItem = reselect; // null also clears a stale selection
            RefreshDetail();
            // a just-filled list may have nothing focused yet (an empty ListView
            // cannot take focus) — give the controller/keyboard a start point
            if (FocusManager.GetFocusedElement() == null) RestoreFocus();
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
                // a rebuild is in progress: its reselect at the end restores the
                // selection — collapsing the pane here would flash the detail away
                if (_rebuilding) return;
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
                await RunBusyAsync(async () =>
                {
                    var restored = await AppEngine.Instance.RestoreAllAsync();
                    SetStatus($"Restored {restored} originals.");
                });
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
            // spurious events while the list is rebuilt (Clear / programmatic
            // reselect) must not clobber the user's selection or detail state
            if (_rebuilding) return;
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
            // entry captured before the picker: _selected must not be re-read
            // after the awaits (a list rebuild may have changed the selection)
            var entry = _selected.Entry;
            await RunBusyAsync(async () =>
            {
                await AppEngine.Instance.UseCustomImageAsync(entry, file);
                SetStatus($"Custom artwork applied to {entry.DisplayName}.");
            });
        }

        private async void BtnRestore_Click(object sender, RoutedEventArgs e)
        {
            if (_selected == null) return;
            await RunBusyAsync(async () =>
            {
                if (await AppEngine.Instance.RestoreAsync(_selected.Entry))
                    SetStatus($"Restored original art for {_selected.Entry.DisplayName}.");
            });
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
