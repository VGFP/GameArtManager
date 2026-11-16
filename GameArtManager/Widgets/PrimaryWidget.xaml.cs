using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using GameArtManager.Core;
using Microsoft.Gaming.XboxGameBar;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
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
        // game id whose artwork is currently in the grid; avoids a refetch when the
        // same game's cover picker is re-opened
        private long? _artLoadedForGameId;
        // generation tokens: async UI population checks its token after every await so a
        // slow earlier load can never paint another game's data (selection race)
        private int _artLoadGen;
        private int _listGen;
        // suppresses SelectionChanged reactions while the games list is rebuilt —
        // Clear() fires SelectionChanged(null) which would otherwise yank the user
        // out of the cover picker and clobber the selection made during the rebuild
        private bool _rebuilding;

        public PrimaryWidget()
        {
            InitializeComponent();
            GamesList.ItemsSource = _games;
            var engine = AppEngine.Instance;
            engine.EntriesChanged += Engine_OnEntriesChanged;
            engine.StatusMessage += msg => SetStatus(msg);
            // gamepad B / Escape / system back all leave cover mode
            PreviewKeyDown += Page_PreviewKeyDown;
            Windows.UI.Core.SystemNavigationManager.GetForCurrentView().BackRequested += Page_BackRequested;
            // PC gamepads do not get Xbox-style automatic UI navigation — translate the
            // VirtualKey.Gamepad* events ourselves (full screen experience / handhelds)
            Window.Current.CoreWindow.KeyDown += CoreWindow_KeyDown;
            // after an FSE app switch or Game Bar re-activation nothing may be focused,
            // which leaves the controller and keyboard dead — restore it
            Window.Current.Activated += OnWindowActivated;
            Loaded += (s, e) => RestoreFocus();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            // gamepad/keyboard users need a focused control before D-pad navigation works
            RestoreFocus();
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
            _rebuilding = true;
            _games.Clear();
            foreach (var entry in AppEngine.Instance.Entries.Where(e => !e.IsOrphaned))
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
                // selection — leaving cover mode here would kick the user out
                if (_rebuilding) return;
                // selected game vanished (e.g. list rebuild): leave cover mode
                if (DetailPane.Visibility == Visibility.Visible) BackToList();
                return;
            }

            DetailThumb.Source = vm.Thumb;
            DetailName.Text = vm.Entry.DisplayName;
            DetailTitle.Text = vm.Entry.DisplayName;
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

            // artwork is only fetched while the cover picker is actually open —
            // moving through the list must not fire a network load per game
            if (DetailPane.Visibility != Visibility.Visible) return;

            if (vm.Entry.SgdbId.HasValue)
            {
                _selectedSearchGame = new SgdbGame { Id = vm.Entry.SgdbId.Value, Name = vm.Entry.DisplayName };
                if (_artLoadedForGameId != vm.Entry.SgdbId.Value)
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
            _artLoadedForGameId = null;
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
                _artLoadedForGameId = gameId;
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
            // spurious events while the list is rebuilt (Clear / programmatic
            // reselect) must not clobber the user's selection or detail state
            if (_rebuilding) return;
            _selected = GamesList.SelectedItem as GameVm;
            RefreshDetail();
        }

        private void GamesList_ItemClick(object sender, ItemClickEventArgs e)
        {
            // activating a game (click / gamepad A / Enter) opens its cover picker
            _selected = e.ClickedItem as GameVm;
            if (GamesList.SelectedItem != _selected)
                GamesList.SelectedItem = _selected;
            EnterDetailMode();
        }

        private void EnterDetailMode()
        {
            MainBar.Visibility = Visibility.Collapsed;
            ListPane.Visibility = Visibility.Collapsed;
            DetailPane.Visibility = Visibility.Visible;
            RefreshDetail();
            // land on Back so D-pad starts at the top of the picker and B exits
            RestoreFocus();
        }

        private void BackToList()
        {
            DetailPane.Visibility = Visibility.Collapsed;
            ListPane.Visibility = Visibility.Visible;
            MainBar.Visibility = Visibility.Visible;
            RestoreFocus();
        }

        /// <summary>Focus the list, or Back when the cover picker is open.</summary>
        private void RestoreFocus()
        {
            if (DetailPane.Visibility == Visibility.Visible) BtnBack.Focus(FocusState.Programmatic);
            else GamesList.Focus(FocusState.Programmatic);
        }

        private void OnWindowActivated(object sender, Windows.UI.Core.WindowActivatedEventArgs e)
        {
            if (e.WindowActivationState == Windows.UI.Core.CoreWindowActivationState.Deactivated) return;
            if (FocusManager.GetFocusedElement() != null) return;
            RestoreFocus();
        }

        private void CoreWindow_KeyDown(Windows.UI.Core.CoreWindow sender, Windows.UI.Core.KeyEventArgs args)
        {
            // gamepad B leaves cover mode (handled before the key reaches XAML)
            if (args.VirtualKey == Windows.System.VirtualKey.GamepadB)
            {
                if (DetailPane.Visibility == Visibility.Visible)
                {
                    args.Handled = true;
                    BackToList();
                }
                return;
            }
            // D-pad / stick / A: PC gamepads need explicit focus translation
            if (GamepadFocus.HandleKey(args.VirtualKey, ActivateGamepadItem))
                args.Handled = true;
        }

        /// <summary>Gamepad A on a focused item: GamesList opens the cover picker,
        /// the search results and artwork grid select the focused entry.</summary>
        private bool ActivateGamepadItem(FrameworkElement container)
        {
            var vm = GamepadFocus.ItemOf(GamesList, container) as GameVm;
            if (vm != null)
            {
                if (GamesList.SelectedItem != vm) GamesList.SelectedItem = vm;
                EnterDetailMode();
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

        private void BtnBack_Click(object sender, RoutedEventArgs e)
        {
            BackToList();
        }

        private void Page_PreviewKeyDown(object sender, Windows.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            // Escape leaves cover mode; gamepad B is handled in CoreWindow_KeyDown
            if (e.Key == Windows.System.VirtualKey.Escape
                && DetailPane.Visibility == Visibility.Visible)
            {
                e.Handled = true;
                BackToList();
            }
        }

        private void Page_BackRequested(object sender, Windows.UI.Core.BackRequestedEventArgs e)
        {
            if (DetailPane.Visibility == Visibility.Visible)
            {
                e.Handled = true;
                BackToList();
            }
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
            SearchBox.Focus(FocusState.Programmatic);
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
