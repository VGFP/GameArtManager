using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using GameArtManager.Core;
using Microsoft.Gaming.XboxGameBar;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;

namespace GameArtManager.Widgets
{
    /// <summary>Compact Game Bar widget surface.</summary>
    public sealed partial class PrimaryWidget : Page
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

        public PrimaryWidget()
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
            // direct System.IO may be denied even though the broker can see the folder
            if (!AppEngine.Instance.Library.Exists &&
                !await BrokerFs.FolderExistsAsync(AppEngine.Instance.Library.Root))
            {
                SetStatus("Xbox app ThirdPartyLibraries folder not found - is the Xbox app installed? Also enable the file system permission in Windows Settings.");
                return;
            }
            await RunBusyAsync(async () => await AppEngine.Instance.ScanAsync());
        }

        private void Engine_OnEntriesChanged()
        {
            _ = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, RefreshGamesList);
        }

        private void SetStatus(string message)
        {
            _ = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () => StatusText.Text = message);
        }

        private async Task RunBusyAsync(Func<Task> work)
        {
            if (_busy) return;
            _busy = true;
            Progress.IsActive = true;
            try
            {
                await work();
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
            _games.Clear();
            foreach (var entry in AppEngine.Instance.Entries.Where(e => !e.IsOrphaned))
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
            DetailStatus.Text = $"{vm.Entry.Platform.ToUpper()}  -  {vm.StatusText}";
            BtnApplyBest.IsEnabled = vm.Entry.SgdbId.HasValue;
            BtnRestore.IsEnabled = vm.Entry.Customized;

            // the search box/results belong to the previously selected game —
            // drop them whenever a different game comes into view. Comparison by
            // Entry (not the VM) keeps the context across list rebuilds of the
            // same game, e.g. the pending refresh right after "Fix match…"
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
                SetStatus($"{_artItems.Count} tile options available.");
                BtnUseArt.IsEnabled = ArtResults.SelectedItem != null;
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

        private async void BtnScan_Click(object sender, RoutedEventArgs e)
        {
            await RunBusyAsync(async () => await AppEngine.Instance.ScanAsync());
        }

        private async void BtnMatch_Click(object sender, RoutedEventArgs e)
        {
            var engine = AppEngine.Instance;
            if (engine.ApiKeyMissing)
            {
                SetStatus("Set your free SteamGridDB API key in the full app (Settings) first.");
                return;
            }
            var progress = new Progress<(int done, int total)>(t => SetStatus($"Matching… {t.done}/{t.total}"));
            await RunBusyAsync(async () => await engine.AutoMatchAsync(progress));
        }

        private async void BtnApplyAll_Click(object sender, RoutedEventArgs e)
        {
            var engine = AppEngine.Instance;
            if (engine.ApiKeyMissing)
            {
                SetStatus("Set your free SteamGridDB API key in the full app (Settings) first.");
                return;
            }
            var targets = engine.Entries.Where(en => en.SgdbId.HasValue && !en.Customized).ToList();
            if (targets.Count == 0)
            {
                SetStatus("Nothing to apply - every matched game already has custom art.");
                return;
            }
            var progress = new Progress<(int done, int total)>(t => SetStatus($"Applying… {t.done}/{t.total}"));
            await RunBusyAsync(async () =>
            {
                var applied = await engine.ApplyBestAsync(targets, progress);
                SetStatus($"Applied {applied} artworks ({targets.Count - applied} skipped).");
            });
        }

        private async void BtnRestoreAll_Click(object sender, RoutedEventArgs e)
        {
            await RunBusyAsync(async () =>
            {
                var restored = await AppEngine.Instance.RestoreAllAsync();
                SetStatus($"Restored {restored} originals.");
            });
        }

        private async void BtnOpenApp_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await Windows.System.Launcher.LaunchUriAsync(new Uri("gameart:"));
            }
            catch
            {
                // protocol not available
            }
        }

        private void GamesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _selected = GamesList.SelectedItem as GameVm;
            RefreshDetail();
        }

        private async void BtnApplyBest_Click(object sender, RoutedEventArgs e)
        {
            var engine = AppEngine.Instance;
            if (_selected == null || !_selected.Entry.SgdbId.HasValue) return;
            await RunBusyAsync(async () =>
            {
                await engine.ApplyBestAsync(new[] { _selected.Entry }, new Progress<(int, int)>());
                SetStatus($"Artwork applied to {_selected.Entry.DisplayName}.");
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

            SearchBox.Text = name != "Unknown" ? name : "";
            SearchBox.Focus(FocusState.Pointer);
            SetStatus($"Match cleared for {name}. Type the correct name, pick the right game, then its artwork.");
        }

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
            if (AppEngine.Instance.ApiKeyMissing)
            {
                SetStatus("Set your free SteamGridDB API key in the full app (Settings) first.");
                return;
            }
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

        private async void BtnUseArt_Click(object sender, RoutedEventArgs e)
        {
            if (_selected == null || _selectedSearchGame == null) return;
            var art = (ArtResults.SelectedItem as ArtVm)?.Art;
            if (art == null) return;
            await RunBusyAsync(async () =>
            {
                await AppEngine.Instance.UseArtAsync(_selected.Entry, _selectedSearchGame, art);
                SetStatus($"Artwork applied to {_selected.Entry.DisplayName}.");
            });
        }
    }
}
