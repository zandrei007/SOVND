﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data.OleDb;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using Anotar.NLog;
using BugSense;
using BugSense.Core.Model;
using JetBrains.Annotations;
using libspotifydotnet;
using MahApps.Metro.Controls;
using ServiceStack.Text;
using SOVND.Client.Modules;
using SOVND.Client.Util;
using SOVND.Client.ViewModels;
using SOVND.Client.Views;
using SOVND.Lib.Handlers;
using SOVND.Lib.Models;
using SOVND.Lib.Settings;
using SOVND.Lib.Utils;
using SpotifyClient;
using Toastify;
using Key = System.Windows.Input.Key;
using Song = SOVND.Lib.Models.Song;
using Spotify = SpotifyClient.Spotify;

namespace SOVND.Client
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow
    {
        private readonly SovndClient _client;
        private readonly IPlayerFactory _playerFactory;
        private readonly SettingsModel _settings;
        private NowPlayingHandler _player;
        private SynchronizationContext synchronization;
        private Toast toast;

        private CancellationTokenSource searchToken = null;
        private ListCollectionView playlist;
        private Scrobbler _scrobbler;

        public MainWindow(SovndClient client, ChannelDirectory channels, IPlayerFactory playerFactory, ISettingsProvider settings, Scrobbler scrobbler)
        {
            InitializeComponent();

            toast = new Toast();
            toast.Show();

            _scrobbler = scrobbler;
            _client = client;
            _playerFactory = playerFactory;
            _settings = settings.GetSettings();

            AllowDrop = true;
            channelbox.ItemsSource = channels.channels;

            PreviewDragOver += OnPreviewDragEnter;
            PreviewDragEnter += OnPreviewDragEnter;
            DragEnter += OnPreviewDragEnter;
            DragOver += OnPreviewDragEnter;

            Drop += OnDrop;

            Loaded += (_, __) =>
            {
                BindingOperations.EnableCollectionSynchronization(channels.channels, channels.channels);
                App.WindowHandle = new WindowInteropHelper(this).Handle;
                synchronization = SynchronizationContext.Current;

                _client.Run();

                if (!string.IsNullOrWhiteSpace(_settings.LastChannel))
                {
                    _player = _playerFactory.CreatePlayer(_settings.LastChannel);
                    _client.SubscribeToChannel(_settings.LastChannel);
                    SetupChannel();
                    Logging.Event("Switched to previously set channel");
                }
            };

            Closed += (_, __) =>
            {
                _client.Disconnect();
                Spotify.ShutDown();
                Process.GetCurrentProcess().Kill(); // TODO That's really inelegant
            };
        }

        private void OnDrop(object sender, DragEventArgs dragEventArgs)
        {
            var dats = (string)dragEventArgs.Data.GetData("Text");
            var b = OpenLinkToURI(dats);
            if (b == null)
                return;

            EnqueueTrack(b);
        }

        private void OnPreviewDragEnter(object sender, DragEventArgs dragEventArgs)
        {
            var dats = (string)dragEventArgs.Data.GetData("Text");
            if (OpenLinkToURI(dats) == null)
                dragEventArgs.Effects = DragDropEffects.None;
            else
                dragEventArgs.Effects = DragDropEffects.All;
            dragEventArgs.Handled = true;
        }

        private string OpenLinkToURI(string link)
        {
            if (!link.StartsWith("http://open.spotify.com/track/"))
                return null;
            if (link.Contains("?"))
                link = link.Remove(link.IndexOf("?"));
            return "spotify:track:" + link.Remove(0, link.LastIndexOf("/") + 1);
        }

        private void SetupChannel()
        {
            var observablePlaylist = ((IObservablePlaylistProvider)_client.SubscribedChannelHandler.Playlist);

            playlist = (ListCollectionView)(CollectionViewSource.GetDefaultView(observablePlaylist.Songs));
            playlist.CustomSort = new SongComparer();

            _player.PlayingSongChanged_Toast = (songID, playing) =>
            {
                var song = observablePlaylist.Songs.FirstOrDefault(x => x.SongID == songID);
                if (song == null)
                    return;

                song.Playing = playing;
                if (!playing)
                    return;

                if(_settings.SongToasts)
                    toast.NewSong(song.track);
            };

            _player.ScrobbleSong = (songID, playing) =>
            {
                var song = observablePlaylist.Songs.FirstOrDefault(x => x.SongID == songID);
                if (song == null)
                    return;

                if(_settings.Scrobbling)
                    _scrobbler.Scrobble(song, playing);
            };

            // TODO: Is this bad? Seems like it's a recipe for leaking - probably holds a ref to playlist.Songs and handler.Chats
            synchronization.Send((_) =>
            {
                BindingOperations.EnableCollectionSynchronization(observablePlaylist.Songs, observablePlaylist.Songs);
                BindingOperations.EnableCollectionSynchronization(_client.SubscribedChannelHandler.Chats, _client.SubscribedChannelHandler.Chats);
            }, null);

            chatbox.ItemsSource = _client.SubscribedChannelHandler.Chats;

            observablePlaylist.PropertyChanged += OnObservablePlaylistOnPropertyChanged;
            BindToPlaylist();
        }

        private void DropChannel()
        {
            if(_player != null)
                _player.Disconnect();
            lbPlaylist.ItemsSource = null;

            if (_client != null && _client.SubscribedChannelHandler != null && _client.SubscribedChannelHandler.Playlist != null)
            {
                var observablePlaylist = ((IObservablePlaylistProvider)_client.SubscribedChannelHandler.Playlist);
                observablePlaylist.PropertyChanged -= OnObservablePlaylistOnPropertyChanged;
            }

            if(_client != null & _client.SubscribedChannelHandler != null)
                _client.SubscribedChannelHandler.ShutdownHandler();

            playlist = null;
            chatbox.ItemsSource = null;
        }

        private void OnObservablePlaylistOnPropertyChanged(object _, PropertyChangedEventArgs __)
        {
            try
            {
                synchronization.Send(x => playlist.Refresh(), null); // Sometimes throws ugly exceptions from deep within wpf
            }
            catch(Exception e)
            { }
        }

        private void Refresh()
        {
            OnObservablePlaylistOnPropertyChanged(null, null);
        } 

        private void BindToPlaylist()
        {
            if (playlist == null)
                return;

            lbPlaylist.ItemsSource = playlist;
            Refresh();
        }

        private void TextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            string text = tbSearch.Text;

            if (!string.IsNullOrWhiteSpace(text))
            {
                if (searchToken != null)
                    searchToken.Cancel();

                searchToken = new CancellationTokenSource();
                var token = searchToken.Token;

                Task.Run(() =>
                {
                    var search = Spotify.GetSearch(text);

                    if (search == null)
                        return;

                    var tracks = from trackPtr in search.TrackPtrs
                        select new Track(trackPtr);

                    synchronization.Send((_) => lbPlaylist.ItemsSource = tracks.Take(25), null);
                }, token);
            }
            else
            {
                if (searchToken != null)
                    searchToken.Cancel();
                BindToPlaylist();
                Logging.Event("Searched");
            }
        }
        
        private void EnqueueTrack(string songID)
        {
            _client.AddTrack(songID);
        }

        private void EnqueueTrack(Track track)
        {
            _client.AddTrack(track);
        }

        private void SendChat(object sender, RoutedEventArgs e)
        {
            _client.SendChat(chatinput.Text);
            chatinput.Clear();
            Logging.Event("Chatted");
        }

        private void NewChannel(object sender, RoutedEventArgs e)
        {
            var newch = new NewChannel(new NewChannelViewModel(_client));
            if (newch.ShowDialog().Value == true)
            {
                var channel = newch.ChannelName;
                _client.SubscribeToChannel(channel);
                Logging.Event("Created new channel");
            }
            else
                Logging.Event("Cancelled new channel creation");
        }

        private void Channelbox_SelectionChanged(object sender, RoutedEventArgs e)
        {
            var channel = channelbox.SelectedItem as Channel;
            if (channel != null)
            {
                DropChannel();

                _settings.LastChannel = channel.Name;
                _settings.Save();

                _player = _playerFactory.CreatePlayer(channel.Name);
                _client.SubscribeToChannel(channel.Name);
                SetupChannel();
                Logging.Event("Switched channels");
            }
        }

        private void Chatinput_OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Return)
            {
                SendChat(null, null);
            }
        }

        private void SongControl_OnOnVoteUp(object sender, EventArgs e)
        {
            var item = ((Button)sender).DataContext as Song;
            if (item == null)
                return;

            EnqueueTrack(item.track);

            Logging.Event("Voted");
        }

        private void SongControl_OnOnBlock(object sender, EventArgs e)
        {
            var item = ((Button)sender).DataContext as Song;
            if (item == null)
                return;

            _client.BlockSong(item);
            Logging.Event("Blocked song");
        }

        private void SongControl_OnOnDelete(object sender, EventArgs e)
        {
            var item = ((Button)sender).DataContext as Song;
            if (item == null)
                return;

            _client.DeleteSong(item);
            Logging.Event("Deleted song");
        }

        private void SongControl_OnOnReport(object sender, EventArgs e)
        {
            var item = ((Button)sender).DataContext as Song;
            if (item == null)
                return;

            _client.ReportSong(item);
            Logging.Event("Reported song");
        }

        private void TrackControl_OnOnSongAdd(object sender, EventArgs e)
        {
            var item = ((Button)sender).DataContext as Track;
            if (item == null)
                return;

            BindToPlaylist();

            EnqueueTrack(item);
            tbSearch.Clear();

            Logging.Event("Added song");
        }

        private void SettingsButton_OnClick(object sender, RoutedEventArgs e)
        {
            SettingsFlyout.IsOpen = !SettingsFlyout.IsOpen;
        }
    }

    public class MainWindowViewModel : INotifyPropertyChanged
    {
        private readonly SettingsModel _settings;

        public MainWindowViewModel(ISettingsProvider settings)
        {
            _settings = settings.GetSettings();
            //_settings.PropertyChanged += (sender, args) => OnPropertyChanged() // TODO Fire propertychanged when settings fires on something we care about
        }

        public bool ChatToasts
        {
            get { return _settings.ChatToasts; }
            set { _settings.ChatToasts = value; }
        }

        public bool SongToasts
        {
            get { return _settings.SongToasts; }
            set { _settings.SongToasts = value; }
        }

        public bool Normalization
        {
            get { return _settings.Normalization; }
            set
            {
                _settings.Normalization = value;
                Spotify.Normalization = value;
            }
        }

        public bool Scrobbling
        {
            get { return _settings.Scrobbling; }
            set { _settings.Scrobbling = value; }
        }

        public libspotify.sp_bitrate SelectedBitrate
        {
            get { return _settings.Bitrate; }
            set
            {
                _settings.Bitrate = value;
                Spotify.SetBitrate(value);
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            if (PropertyChanged != null)
                PropertyChanged(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
