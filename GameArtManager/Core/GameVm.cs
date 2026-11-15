using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using GameArtManager.Core;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;

namespace GameArtManager.Core
{
    /// <summary>List item wrapper for a game entry (INPC for x:Bind).</summary>
    public class GameVm : INotifyPropertyChanged
    {
        private static readonly SolidColorBrush GoodBrush = new SolidColorBrush(Windows.UI.ColorHelper.FromArgb(255, 79, 176, 109));
        private static readonly SolidColorBrush WarnBrush = new SolidColorBrush(Windows.UI.ColorHelper.FromArgb(255, 217, 161, 59));
        private static readonly SolidColorBrush BadBrush = new SolidColorBrush(Windows.UI.ColorHelper.FromArgb(255, 192, 86, 79));

        public GameEntry Entry { get; }

        private BitmapImage _thumb;
        public BitmapImage Thumb
        {
            get => _thumb;
            set { _thumb = value; OnPropertyChanged(); }
        }

        public GameVm(GameEntry entry)
        {
            Entry = entry;
        }

        public string Title => Entry.DisplayName;
        public string Platform => Entry.Platform.ToUpperInvariant();

        public string StatusText
        {
            get
            {
                if (Entry.Customized) return "custom art";
                switch (Entry.MatchMethod)
                {
                    case "exact":
                    case "cache":
                    case "manual":
                        return "exact match";
                    case "fuzzy":
                        return "fuzzy match";
                    default:
                        return "no match";
                }
            }
        }

        public SolidColorBrush StatusBrush
        {
            get
            {
                if (Entry.Customized) return GoodBrush;
                switch (Entry.MatchMethod)
                {
                    case "exact":
                    case "cache":
                    case "manual":
                        return GoodBrush;
                    case "fuzzy":
                        return WarnBrush;
                    default:
                        return BadBrush;
                }
            }
        }

        public static async Task<GameVm> CreateAsync(GameEntry entry)
        {
            var vm = new GameVm(entry);
            vm.Thumb = await AppEngine.LoadThumbnailAsync(entry.ImagePath);
            return vm;
        }

        public void Refresh()
        {
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(Platform));
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(StatusBrush));
            _ = LoadThumbAsync();
        }

        private async Task LoadThumbAsync()
        {
            Thumb = await AppEngine.LoadThumbnailAsync(Entry.ImagePath);
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>Artwork tile wrapper (thumb bitmap + SgdbArt).</summary>
    public class ArtVm : INotifyPropertyChanged
    {
        private BitmapImage _thumb;
        public BitmapImage Thumb
        {
            get => _thumb;
            set { _thumb = value; OnPropertyChanged(); }
        }

        public SgdbArt Art { get; }

        public ArtVm(SgdbArt art)
        {
            Art = art;
        }

        public static async Task<ArtVm> CreateAsync(SgdbArt art)
        {
            var vm = new ArtVm(art);
            vm.Thumb = await LoadBitmapAsync(art.Thumb ?? art.Url);
            return vm;
        }

        private static async Task<BitmapImage> LoadBitmapAsync(string url)
        {
            try
            {
                var bytes = await Imaging.DownloadAsync(url);
                var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                await stream.WriteAsync(bytes.AsBuffer());
                stream.Seek(0);
                var bitmap = new BitmapImage();
                await bitmap.SetSourceAsync(stream);
                return bitmap;
            }
            catch
            {
                return null;
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
