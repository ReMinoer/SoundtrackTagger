using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Windows.Input;
using JikanDotNet;
using SoundtrackTagger.Utils;
using SoundtrackTagger.ViewModels.Base;
using SoundtrackTagger.ViewModels.Utils;
using TagLib;
using File = System.IO.File;

namespace SoundtrackTagger.ViewModels
{
    public class MainViewModel : NotifyPropertyChangedBase
    {
        public string AnimeAlbumArtist = "Anime";
        private CancellationTokenSource _cancellation;

        private string _musicFolderPath;

        public string MusicFolderPath
        {
            get => _musicFolderPath;
            set => Set(ref _musicFolderPath, value);
        }

        private string _coverFolderPath;

        public string CoverFolderPath
        {
            get => _coverFolderPath;
            set => Set(ref _coverFolderPath, value);
        }

        private int _currentStep;
        public int CurrentStep
        {
            get => _currentStep;
            set
            {
                if (Set(ref _currentStep, value))
                    NotifyPropertyChanged(nameof(StepsText));
            }
        }

        private int _successfulSteps;
        public int SuccessfulSteps
        {
            get => _successfulSteps;
            set
            {
                if (Set(ref _successfulSteps, value))
                    NotifyPropertyChanged(nameof(StepsText));
            }
        }

        private int _step;
        public int Steps
        {
            get => _step;
            set
            {
                if (Set(ref _step, value))
                    NotifyPropertyChanged(nameof(StepsText));
            }
        }

        private bool _isEnabled = true;
        public bool IsEnabled
        {
            get => _isEnabled;
            private set => Set(ref _isEnabled, value);
        }

        public string StepsText => $"(Tagged files : {SuccessfulSteps}) {CurrentStep}/{Steps}";

        public ICommand BrowseMusicFolderCommand { get; }
        public ICommand BrowseCoverFolderCommand { get; }
        public ICommand FillCacheFromMyAnimeListCommand { get; }
        public ICommand ApplyCacheCommand { get; }

        public MainViewModel()
        {
            _musicFolderPath = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
            _coverFolderPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Covers");

            BrowseMusicFolderCommand = new SimpleCommand(BrowseMusicFolder);
            BrowseCoverFolderCommand = new SimpleCommand(BrowseCoverFolder);
            FillCacheFromMyAnimeListCommand = new SimpleCommand(async () => await FillCacheFromMyAnimeListAsync(CancellationToken.None));
            ApplyCacheCommand = new SimpleCommand(async () => await ApplyCacheAsync(CancellationToken.None));
        }

        public async Task FillCacheFromMyAnimeListAsync(CancellationToken cancellationToken)
        {
            if (!CheckDirectories())
                return;

            var albumHashSet = new ConcurrentDictionary<string, byte>();

            await ForEachMusic(async audioFile =>
            {
                if (!audioFile.Tag.AlbumArtists.Contains(AnimeAlbumArtist))
                    return false;

                string albumTag = audioFile.Tag.Album;
                if (!albumHashSet.TryAdd(albumTag, 0))
                    return false;

                string validCoverFileName = GetValidFileName(albumTag);
                if (Directory.GetFiles(CoverFolderPath, validCoverFileName + ".*").Length > 0)
                    return false;

                var jikan = new Jikan(useHttps: true);

                string titleSearch = GetValidSearch(albumTag);
                AnimeSearchResult searchResult = await jikan.SearchAnime(titleSearch);
                if (searchResult?.Results == null || searchResult.Results.Count == 0)
                    return false;

                AnimeSearchEntry searchBestEntry = GetBestEntry(searchResult, albumTag);

                byte[] imageBytes;
                using (var webClient = new WebClient())
                    imageBytes = await webClient.DownloadDataTaskAsync(searchBestEntry.ImageURL);

                File.WriteAllBytes(Path.Combine(CoverFolderPath, validCoverFileName + ".jpg"), imageBytes);
                return true;
            }, cancellationToken);
        }

        public async Task ApplyCacheAsync(CancellationToken cancellationToken)
        {
            await Task.Run(() => ApplyCache(cancellationToken), cancellationToken);
        }

        private void ApplyCache(CancellationToken cancellationToken)
        {
            if (!CheckDirectories())
                return;

            ForEachMusic(audioFile =>
            {
                string coverFilePattern = $"{GetValidFileName(audioFile.Tag.Album)}.*";
                string coverFilePath = Directory.EnumerateFiles(CoverFolderPath, coverFilePattern, SearchOption.AllDirectories).FirstOrDefault();
                if (coverFilePath == null)
                    return Task.FromResult(false);

                audioFile.Tag.Pictures = new IPicture[] { new Picture(new ByteVector(File.ReadAllBytes(coverFilePath))) };
                audioFile.Save();
                audioFile.Dispose();
                return Task.FromResult(true);
            }, cancellationToken).Wait(cancellationToken);
        }

        private async Task ForEachMusic(Func<TagLib.File, Task<bool>> action, CancellationToken cancellationToken)
        {
            IsEnabled = false;

            _cancellation = new CancellationTokenSource();
            CancellationTokenSource linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(_cancellation.Token, cancellationToken);
            CancellationToken linkedCancellationToken = linkedCancellation.Token;

            string[] audioFilePaths = Directory.GetFiles(MusicFolderPath, "*", SearchOption.AllDirectories).ToArray();
            Steps = audioFilePaths.Length;
            CurrentStep = 0;
            SuccessfulSteps = 0;

            var failedFilePaths = new ConcurrentBag<string>();

            await Task.WhenAll(audioFilePaths.Select(audioFilePath => Task.Run(async () =>
            {
                try
                {
                    Throw.IfTaskCancelled(linkedCancellationToken);

                    TagLib.File audioFile;
                    try
                    {
                        audioFile = TagLib.File.Create(audioFilePath);
                    }
                    catch (UnsupportedFormatException)
                    {
                        IncrementStep();
                        return;
                    }

                    string albumTag = audioFile.Tag.Album;
                    if (string.IsNullOrWhiteSpace(albumTag))
                    {
                        IncrementStep();
                        return;
                    }

                    if (!await action(audioFile))
                    {
                        IncrementStep();
                        return;
                    }

                    IncrementStep(successful: true);
                }
                catch (Exception)
                {
                    failedFilePaths.Add(audioFilePath);
                    IncrementStep();
                }
            }, cancellationToken)));

            if (!failedFilePaths.IsEmpty)
                MessageBox.Show(string.Join("\n", failedFilePaths), $@"{failedFilePaths.Count} errors");

            Throw.IfTaskCancelled(linkedCancellationToken);
            _cancellation = null;

            IsEnabled = true;
        }

        private bool CheckDirectories()
        {
            if (!Directory.Exists(MusicFolderPath))
            {
                MessageBox.Show(@"Cannot found music folder!");
                return false;
            }

            if (!Directory.Exists(CoverFolderPath))
                Directory.CreateDirectory(CoverFolderPath);

            return true;
        }

        private void IncrementStep(bool successful = false)
        {
            Interlocked.Increment(ref _currentStep);
            NotifyPropertyChanged(nameof(CurrentStep));

            if (successful)
            {
                Interlocked.Increment(ref _successfulSteps);
                NotifyPropertyChanged(nameof(SuccessfulSteps));
            }

            NotifyPropertyChanged(nameof(StepsText));
        }

        public void Cancel()
        {
            _cancellation?.Cancel();
        }

        private void BrowseMusicFolder()
        {
            MusicFolderPath = BrowseFolder(MusicFolderPath);
        }

        private void BrowseCoverFolder()
        {
            CoverFolderPath = BrowseFolder(CoverFolderPath);
        }

        private string BrowseFolder(string currentValue)
        {
            var folderBrowserDialog = new FolderBrowserDialog { SelectedPath = currentValue };
            return folderBrowserDialog.ShowDialog() == DialogResult.OK ? folderBrowserDialog.SelectedPath : currentValue;
        }

        static private string GetValidFileName(string fileName)
        {
            return new string(fileName.Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray());
        }

        static private string GetValidSearch(string fileName)
        {
            return new string(fileName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? ' ' : c).ToArray());
        }

        static private AnimeSearchEntry GetBestEntry(AnimeSearchResult searchResult, string albumTag)
        {
            const StringComparison comparison = StringComparison.InvariantCultureIgnoreCase;
            ICollection<AnimeSearchEntry> entries = searchResult.Results;

            List<AnimeSearchEntry> results = entries.Where(x => x.Title.Equals(albumTag, comparison)).ToList();
            if (results.Count == 1)
                return results[0];
            if (results.Count > 1)
                return results.MaxBy(x => x.Members ?? int.MinValue);

            results = entries.Where(x => x.Title.StartsWith(albumTag, comparison)).ToList();
            if (results.Count == 1)
                return results[0];
            if (results.Count >= 1)
                return results.MaxBy(x => x.Members ?? int.MinValue);

            throw new InvalidOperationException();
        }
    }
}